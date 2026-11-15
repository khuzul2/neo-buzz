// neoplug-test.clap GUI on macOS: a solid colour NSView with a label, added to the host's NSView.
#import <Cocoa/Cocoa.h>

@interface NeoPlugTestPanel : NSView
@property(nonatomic, strong) NSColor* fill;
@end

@implementation NeoPlugTestPanel
- (BOOL)isFlipped { return YES; }
- (void)drawRect:(NSRect)dirty
{
    [self.fill setFill];
    NSRectFill(self.bounds);
    NSDictionary* attrs = @{NSForegroundColorAttributeName : [NSColor whiteColor], NSFontAttributeName : [NSFont systemFontOfSize:14]};
    [@"NeoPlug test plugin" drawAtPoint:NSMakePoint(12, 12) withAttributes:attrs];
}
@end

extern "C" int testgui_supported(void) { return 1; }

extern "C" void* testgui_create(void* parent, int w, int h, int rgb)
{
    NSView* p = (__bridge NSView*)parent;
    if (!p) return nullptr;
    NeoPlugTestPanel* v = [[NeoPlugTestPanel alloc] initWithFrame:NSMakeRect(0, 0, w, h)];
    v.fill = [NSColor colorWithSRGBRed:((rgb >> 16) & 255) / 255.0 green:((rgb >> 8) & 255) / 255.0 blue:(rgb & 255) / 255.0 alpha:1];
    [p addSubview:v];
    return (__bridge_retained void*)v;
}

extern "C" void testgui_destroy(void* view)
{
    if (!view) return;
    NeoPlugTestPanel* v = (__bridge_transfer NeoPlugTestPanel*)view;
    [v removeFromSuperview];
}
