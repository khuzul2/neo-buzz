// Linux: a child window of the host's X11 window (CLAP_WINDOW_API_X11). Its own connection to the X server; Pump (called from the
// editor's tick, 60 Hz) handles the pending events. Layout of XEvent fields is the LP64 one (x86-64 and arm64 Linux).
using System;
using System.Runtime.InteropServices;

namespace NeoExport
{
    internal sealed unsafe class X11View : INativeView
    {
        const string X = "libX11.so.6";
        [DllImport(X)] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(X)] static extern int XCloseDisplay(IntPtr d);
        [DllImport(X)] static extern int XDefaultScreen(IntPtr d);
        [DllImport(X)] static extern IntPtr XDefaultVisual(IntPtr d, int s);
        [DllImport(X)] static extern int XDefaultDepth(IntPtr d, int s);
        [DllImport(X)] static extern ulong XCreateSimpleWindow(IntPtr d, ulong parent, int x, int y, uint w, uint h, uint bw, ulong border, ulong bg);
        [DllImport(X)] static extern int XDestroyWindow(IntPtr d, ulong w);
        [DllImport(X)] static extern int XMapWindow(IntPtr d, ulong w);
        [DllImport(X)] static extern int XUnmapWindow(IntPtr d, ulong w);
        [DllImport(X)] static extern int XResizeWindow(IntPtr d, ulong w, uint width, uint height);
        [DllImport(X)] static extern int XSelectInput(IntPtr d, ulong w, long mask);
        [DllImport(X)] static extern IntPtr XCreateGC(IntPtr d, ulong w, ulong mask, IntPtr values);
        [DllImport(X)] static extern int XFreeGC(IntPtr d, IntPtr gc);
        [DllImport(X)] static extern IntPtr XCreateImage(IntPtr d, IntPtr visual, uint depth, int format, int offset, IntPtr data, uint w, uint h, int pad, int stride);
        [DllImport(X)] static extern int XPutImage(IntPtr d, ulong w, IntPtr gc, IntPtr img, int sx, int sy, int dx, int dy, uint w2, uint h2);
        [DllImport(X)] static extern int XDestroyImage(IntPtr img);
        [DllImport(X)] static extern int XPending(IntPtr d);
        [DllImport(X)] static extern int XNextEvent(IntPtr d, byte* ev);
        [DllImport(X)] static extern int XFlush(IntPtr d);

        const long ButtonPressMask = 1 << 2, ButtonReleaseMask = 1 << 3, PointerMotionMask = 1 << 6, ExposureMask = 1 << 15, StructureNotifyMask = 1 << 17;
        IntPtr dpy, gc, img; ulong win; int scr; int w, h;
        byte* imgData; int imgW, imgH;
        readonly EditorInstance owner;

        X11View(EditorInstance o) { owner = o; }

        public static X11View Create(ulong parent, int w, int h, EditorInstance owner)
        {
            var v = new X11View(owner) { w = w, h = h };
            v.dpy = XOpenDisplay(IntPtr.Zero);
            if (v.dpy == IntPtr.Zero) return null;
            v.scr = XDefaultScreen(v.dpy);
            v.win = XCreateSimpleWindow(v.dpy, parent, 0, 0, (uint)w, (uint)h, 0, 0, 0x202020);
            XSelectInput(v.dpy, v.win, ButtonPressMask | ButtonReleaseMask | PointerMotionMask | ExposureMask | StructureNotifyMask);
            v.gc = XCreateGC(v.dpy, v.win, 0, IntPtr.Zero);
            XMapWindow(v.dpy, v.win); XFlush(v.dpy);
            return v;
        }

        public unsafe void Present(byte* bgra, int width, int height, int stride)
        {
            if (dpy == IntPtr.Zero) return;
            if (img != IntPtr.Zero && (imgData != bgra || imgW != width || imgH != height)) { DropImage(); }
            if (img == IntPtr.Zero)
            {
                img = XCreateImage(dpy, XDefaultVisual(dpy, scr), (uint)XDefaultDepth(dpy, scr), 2, 0, (IntPtr)bgra, (uint)width, (uint)height, 32, stride);
                imgData = bgra; imgW = width; imgH = height;
            }
            if (img != IntPtr.Zero) { XPutImage(dpy, win, gc, img, 0, 0, 0, 0, (uint)width, (uint)height); XFlush(dpy); }
        }

        void DropImage()
        {
            if (img == IntPtr.Zero) return;
            *(IntPtr*)((byte*)img + 16) = IntPtr.Zero;   // XImage.data: ours, not Xlib's to free
            XDestroyImage(img); img = IntPtr.Zero;
        }

        public void Resize(int width, int height) { w = width; h = height; if (dpy != IntPtr.Zero) { XResizeWindow(dpy, win, (uint)width, (uint)height); XFlush(dpy); } }
        public void SetVisible(bool v) { if (dpy == IntPtr.Zero) return; if (v) XMapWindow(dpy, win); else XUnmapWindow(dpy, win); XFlush(dpy); }

        public void Pump()
        {
            if (dpy == IntPtr.Zero) return;
            byte* ev = stackalloc byte[192];
            while (XPending(dpy) > 0)
            {
                XNextEvent(dpy, ev);
                int type = *(int*)ev;
                int x = *(int*)(ev + 64), y = *(int*)(ev + 68);
                switch (type)
                {
                    case 4:   // ButtonPress: 1 left, 2 middle, 3 right, 4 wheel up, 5 wheel down
                        {
                            uint b = *(uint*)(ev + 84);
                            if (b == 4 || b == 5) owner.Wheel(x, y, b == 4 ? 1 : -1);
                            else owner.MouseDown(x, y, (int)b);
                            break;
                        }
                    case 5: { uint b = *(uint*)(ev + 84); if (b <= 3) owner.MouseUp(x, y, (int)b); break; }
                    case 6: owner.MouseMove(x, y); break;
                    case 12: break;   // Expose: the next tick paints again
                }
            }
        }

        public void Destroy()
        {
            if (dpy == IntPtr.Zero) return;
            DropImage();
            XFreeGC(dpy, gc); XDestroyWindow(dpy, win); XFlush(dpy); XCloseDisplay(dpy);
            dpy = IntPtr.Zero;
        }
    }
}
