// macOS: an NSView subclass added as a subview of the host's NSView (CLAP_WINDOW_API_COCOA), layer-backed; each frame becomes the
// layer's contents (a CGImage). Written against the Objective-C runtime directly (arm64: CGRect/CGPoint are passed as doubles).
// Not testable on the CI: hand-test in Ableton on a Mac.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NeoExport
{
    internal sealed unsafe class CocoaView : INativeView
    {
        const string ObjC = "/usr/lib/libobjc.A.dylib", CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        [StructLayout(LayoutKind.Sequential)] struct Pt { public double X, Y; }
        [DllImport(ObjC)] static extern IntPtr objc_getClass(string n);
        [DllImport(ObjC)] static extern IntPtr sel_registerName(string n);
        [DllImport(ObjC)] static extern IntPtr objc_allocateClassPair(IntPtr super, string name, nuint extra);
        [DllImport(ObjC)] static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr o, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr o, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr o, IntPtr s, bool a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendRect(IntPtr o, IntPtr s, double x, double y, double w, double h);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern Pt SendPoint(IntPtr o, IntPtr s, double x, double y, IntPtr view);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern Pt SendGetPoint(IntPtr o, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern double SendGetDouble(IntPtr o, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern nint SendGetInt(IntPtr o, IntPtr s);
        [DllImport(CF)] static extern IntPtr CFDataCreate(IntPtr alloc, byte* bytes, nint len);
        [DllImport(CF)] static extern void CFRelease(IntPtr o);
        [DllImport(CG)] static extern IntPtr CGColorSpaceCreateDeviceRGB();
        [DllImport(CG)] static extern IntPtr CGDataProviderCreateWithCFData(IntPtr data);
        [DllImport(CG)] static extern void CGDataProviderRelease(IntPtr p);
        [DllImport(CG)] static extern IntPtr CGImageCreate(nuint w, nuint h, nuint bpc, nuint bpp, nuint bpr, IntPtr cs, uint info, IntPtr prov, IntPtr decode, bool interp, int intent);
        [DllImport(CG)] static extern void CGImageRelease(IntPtr i);

        static IntPtr cls;
        static readonly Dictionary<IntPtr, CocoaView> views = new();
        static IntPtr sLocation, sConvert, sSetNeedsDisplay, sLayer, sSetContents;
        IntPtr view, rgb; readonly EditorInstance owner; int w, h;

        CocoaView(EditorInstance o) { owner = o; }

        static void EnsureClass()
        {
            if (cls != IntPtr.Zero) return;
            sLocation = sel_registerName("locationInWindow"); sConvert = sel_registerName("convertPoint:fromView:");
            sLayer = sel_registerName("layer"); sSetContents = sel_registerName("setContents:");
            cls = objc_allocateClassPair(objc_getClass("NSView"), "NeoBuzzEditorView", 0);
            class_addMethod(cls, sel_registerName("mouseDown:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&MouseDown, "v@:@");
            class_addMethod(cls, sel_registerName("mouseUp:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&MouseUp, "v@:@");
            class_addMethod(cls, sel_registerName("mouseDragged:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&MouseMoved, "v@:@");
            class_addMethod(cls, sel_registerName("mouseMoved:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&MouseMoved, "v@:@");
            class_addMethod(cls, sel_registerName("rightMouseDown:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&RightDown, "v@:@");
            class_addMethod(cls, sel_registerName("rightMouseUp:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&RightUp, "v@:@");
            class_addMethod(cls, sel_registerName("scrollWheel:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&Scroll, "v@:@");
            class_addMethod(cls, sel_registerName("isFlipped"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&Yes, "c@:");
            class_addMethod(cls, sel_registerName("acceptsFirstMouse:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)&YesArg, "c@:@");
            objc_registerClassPair(cls);
        }

        public static CocoaView Create(IntPtr parent, int w, int h, EditorInstance owner)
        {
            EnsureClass();
            var v = new CocoaView(owner) { w = w, h = h };
            v.view = SendRect(Send(cls, sel_registerName("alloc")), sel_registerName("initWithFrame:"), 0, 0, w, h);
            if (v.view == IntPtr.Zero) return null;
            Send(v.view, sel_registerName("setWantsLayer:"), true);
            Send(parent, sel_registerName("addSubview:"), v.view);
            v.rgb = CGColorSpaceCreateDeviceRGB();
            views[v.view] = v;
            return v;
        }

        public unsafe void Present(byte* bgra, int width, int height, int stride)
        {
            if (view == IntPtr.Zero) return;
            IntPtr data = CFDataCreate(IntPtr.Zero, bgra, (nint)stride * height);
            IntPtr prov = CGDataProviderCreateWithCFData(data);
            // BGRA, premultiplied alpha first, 32-bit little endian: kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little
            IntPtr image = CGImageCreate((nuint)width, (nuint)height, 8, 32, (nuint)stride, rgb, 2 | (2u << 12), prov, IntPtr.Zero, false, 0);
            Send(Send(view, sLayer), sSetContents, image);
            CGImageRelease(image); CGDataProviderRelease(prov); CFRelease(data);
        }

        public void Resize(int width, int height) { w = width; h = height; if (view != IntPtr.Zero) SendRect(view, sel_registerName("setFrameSize:"), width, height, 0, 0); }
        public void SetVisible(bool v) { if (view != IntPtr.Zero) Send(view, sel_registerName("setHidden:"), !v); }
        public void Pump() { }
        public void Destroy()
        {
            if (view == IntPtr.Zero) return;
            views.Remove(view);
            Send(view, sel_registerName("removeFromSuperview")); Send(view, sel_registerName("release"));
            view = IntPtr.Zero;
        }

        static bool Where(IntPtr self, IntPtr ev, out CocoaView v, out double x, out double y)
        {
            x = y = 0; v = null;
            if (!views.TryGetValue(self, out v)) return false;
            var loc = SendGetPoint(ev, sLocation);
            var p = SendPoint(self, sConvert, loc.X, loc.Y, IntPtr.Zero);   // isFlipped: y grows downwards
            x = p.X; y = p.Y; return true;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void MouseDown(IntPtr self, IntPtr sel, IntPtr ev) { if (Where(self, ev, out var v, out var x, out var y)) v.owner.MouseDown(x, y, 1); }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void MouseUp(IntPtr self, IntPtr sel, IntPtr ev) { if (Where(self, ev, out var v, out var x, out var y)) v.owner.MouseUp(x, y, 1); }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void MouseMoved(IntPtr self, IntPtr sel, IntPtr ev) { if (Where(self, ev, out var v, out var x, out var y)) v.owner.MouseMove(x, y); }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void RightDown(IntPtr self, IntPtr sel, IntPtr ev) { if (Where(self, ev, out var v, out var x, out var y)) v.owner.MouseDown(x, y, 3); }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void RightUp(IntPtr self, IntPtr sel, IntPtr ev) { if (Where(self, ev, out var v, out var x, out var y)) v.owner.MouseUp(x, y, 3); }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void Scroll(IntPtr self, IntPtr sel, IntPtr ev) { if (Where(self, ev, out var v, out var x, out var y)) v.owner.Wheel(x, y, SendGetDouble(ev, sel_registerName("scrollingDeltaY")) / 8.0); }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte Yes(IntPtr self, IntPtr sel) => 1;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte YesArg(IntPtr self, IntPtr sel, IntPtr a) => 1;
    }
}
