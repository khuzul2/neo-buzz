// neoplug (H0 spike): macOS helpers for the GUI questions. A container NSView to put a plugin view
// in (inside Avalonia's NativeControlHost), a floating NSWindow, a description of the view tree and
// a snapshot of the pixels, so CI can tell an embedded plugin view from an empty one.
#import <Cocoa/Cocoa.h>
#include <dlfcn.h>
#include <set>
#include <string>

#include "np_internal.h"

static void ensureApp()
{
    [NSApplication sharedApplication];
    static bool launched = false;
    if (!launched)
    {
        // Only for the probe's own windows: inside Avalonia the app is already running.
        if ([NSApp activationPolicy] == NSApplicationActivationPolicyProhibited)
            [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
        if (![NSApp isRunning]) [NSApp finishLaunching];
        launched = true;
    }
}

@interface NeoPlugContainer : NSView
@end
@implementation NeoPlugContainer
- (BOOL)isFlipped { return YES; }
@end

static NSMutableArray* windows()
{
    static NSMutableArray* list = [NSMutableArray new];
    return list;
}

extern "C" {

NP_API void* np_platform_container(void* parent, int width, int height)
{
    // No autoresizing: the host (Avalonia's NativeControlHost) sets this view's frame itself, and
    // an autoresizing mask on top of that collapses or shifts it.
    NeoPlugContainer* v = [[NeoPlugContainer alloc] initWithFrame:NSMakeRect(0, 0, width, height)];
    v.autoresizingMask = NSViewNotSizable;
    if (parent) [(__bridge NSView*)parent addSubview:v];
    return (__bridge_retained void*)v;
}

NP_API void np_platform_resize(void* view, int width, int height)
{
    NSView* v = (__bridge NSView*)view;
    if (v) [v setFrameSize:NSMakeSize(width, height)];
}

NP_API void* np_platform_window(const char* title, int width, int height)
{
    ensureApp();
    NSWindow* w = [[NSWindow alloc] initWithContentRect:NSMakeRect(120, 120, width, height)
                                              styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskResizable
                                                backing:NSBackingStoreBuffered
                                                  defer:NO];
    w.title = [NSString stringWithUTF8String:title ? title : "neoplug"];
    w.releasedWhenClosed = NO;
    NeoPlugContainer* v = [[NeoPlugContainer alloc] initWithFrame:NSMakeRect(0, 0, width, height)];
    w.contentView = v;
    [w makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
    [windows() addObject:w];
    return (__bridge_retained void*)v;
}

static void describe(NSView* v, int depth, std::string& out)
{
    NSRect f = v.frame;
    char line[512];
    snprintf(line, sizeof line, "%*s%s frame=(%.0f,%.0f %.0fx%.0f)%s%s\n", depth * 2, "", [NSStringFromClass([v class]) UTF8String],
             f.origin.x, f.origin.y, f.size.width, f.size.height, v.hidden ? " hidden" : "", v.wantsLayer ? " layer" : "");
    out += line;
    for (NSView* s in v.subviews) describe(s, depth + 1, out);
}

NP_API int np_platform_describe(void* view, char* buf, int len)
{
    NSView* v = (__bridge NSView*)view;
    if (!v) return -1;
    std::string out;
    NSWindow* w = v.window;
    char head[512];
    snprintf(head, sizeof head, "window: %s #%ld class=%s visible=%d key=%d contentView=%s\n",
             w ? [w.title UTF8String] : "(none)", w ? (long)w.windowNumber : -1L, w ? [NSStringFromClass([w class]) UTF8String] : "-",
             w ? (int)w.visible : 0, w ? (int)w.keyWindow : 0, w ? [NSStringFromClass([w.contentView class]) UTF8String] : "-");
    out += head;
    // Ancestors: shows whether the container sits inside the Avalonia window's view tree.
    std::string chain;
    for (NSView* a = v.superview; a; a = a.superview)
        chain = std::string([NSStringFromClass([a class]) UTF8String]) + (chain.empty() ? "" : " > ") + chain;
    out += "ancestors: " + (chain.empty() ? std::string("(none)") : chain) + "\n";
    describe(v, 0, out);
    np::copyStr(buf, (size_t)len, out);
    return (int)out.size();
}

static void stats(NSBitmapImageRep* rep, int rgb, std::string& json)
{
    std::set<uint32_t> colors;
    long match = 0, total = 0;
    int tr = (rgb >> 16) & 255, tg = (rgb >> 8) & 255, tb = rgb & 255;
    NSInteger w = rep.pixelsWide, h = rep.pixelsHigh;
    for (NSInteger y = 0; y < h; y += 2)
        for (NSInteger x = 0; x < w; x += 2)
        {
            NSColor* c = [[rep colorAtX:x y:y] colorUsingColorSpace:[NSColorSpace sRGBColorSpace]];
            if (!c) continue;
            int r = (int)lround(c.redComponent * 255), g = (int)lround(c.greenComponent * 255), b = (int)lround(c.blueComponent * 255);
            colors.insert((uint32_t)((r << 16) | (g << 8) | b));
            total++;
            if (abs(r - tr) <= 24 && abs(g - tg) <= 24 && abs(b - tb) <= 24) match++;
        }
    char buf[256];
    snprintf(buf, sizeof buf, "{\"width\":%ld,\"height\":%ld,\"colors\":%zu,\"match_permille\":%ld}", (long)w, (long)h, colors.size(),
             total ? match * 1000 / total : 0L);
    json = buf;
}

NP_API int np_platform_snapshot(void* view, const char* path, int rgb, char* statsOut, int len)
{
    NSView* v = (__bridge NSView*)view;
    if (!v) return -1;
    std::string json = "{}";
    // 1. The view's own drawing (works for views that draw with drawRect:, as VSTGUI and AppKit do).
    NSBitmapImageRep* rep = [v bitmapImageRepForCachingDisplayInRect:v.bounds];
    if (!rep) return -1;
    [v cacheDisplayInRect:v.bounds toBitmapImageRep:rep];
    std::string viewStats;
    stats(rep, rgb, viewStats);
    if (path)
    {
        NSData* png = [rep representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
        [png writeToFile:[NSString stringWithUTF8String:path] atomically:YES];
    }
    // 2. The window as the window server composited it (Metal or OpenGL layers included). Looked up
    //    at run time: the macOS 15 SDK marks CGWindowListCreateImage unavailable.
    std::string windowStats = "null";
    NSWindow* w = v.window;
    using CreateImage = CGImageRef (*)(CGRect, uint32_t, uint32_t, uint32_t);
    auto createImage = (CreateImage)dlsym(RTLD_DEFAULT, "CGWindowListCreateImage");
    if (w && createImage)
    {
        CGImageRef img = createImage(CGRectNull, 1 << 3 /* kCGWindowListOptionIncludingWindow */, (uint32_t)w.windowNumber,
                                     1 << 0 /* kCGWindowImageBoundsIgnoreFraming */);
        if (img)
        {
            NSBitmapImageRep* wrep = [[NSBitmapImageRep alloc] initWithCGImage:img];
            stats(wrep, rgb, windowStats);
            if (path)
            {
                NSData* png = [wrep representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
                [png writeToFile:[NSString stringWithFormat:@"%s.window.png", path] atomically:YES];
            }
            CGImageRelease(img);
        }
    }
    json = "{\"view\":" + viewStats + ",\"window\":" + windowStats + "}";
    np::copyStr(statsOut, (size_t)len, json);
    return 0;
}

NP_API void np_platform_run(double seconds)
{
    ensureApp();
    NSDate* until = [NSDate dateWithTimeIntervalSinceNow:seconds];
    while ([until timeIntervalSinceNow] > 0)
    {
        @autoreleasepool
        {
            NSEvent* e = [NSApp nextEventMatchingMask:NSEventMaskAny untilDate:[NSDate dateWithTimeIntervalSinceNow:0.02] inMode:NSDefaultRunLoopMode dequeue:YES];
            if (e) [NSApp sendEvent:e];
            [NSApp updateWindows];
        }
    }
}

NP_API void np_platform_release(void* handle)
{
    if (!handle) return;
    NSView* v = (__bridge_transfer NSView*)handle;
    NSWindow* w = v.window;
    if (w && w.contentView == v && [windows() containsObject:w])
    {
        [w close];
        [windows() removeObject:w];
    }
    else
        [v removeFromSuperview];
}

} // extern "C"
