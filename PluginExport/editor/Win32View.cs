// Windows: a child window (WS_CHILD) of the host's HWND (CLAP_WINDOW_API_WIN32), painted with SetDIBitsToDevice; the host's message loop
// delivers its mouse messages to WndProc. Not testable on the Linux CI: hand-test in Ableton.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NeoExport
{
    internal sealed unsafe class Win32View : INativeView
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WNDCLASSEXW { public uint cbSize, style; public delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, nint, nint> lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }

        [DllImport("user32", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(WNDCLASSEXW* c);
        [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
        [DllImport("user32")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32")] static extern IntPtr SetCapture(IntPtr h);
        [DllImport("user32")] static extern bool ReleaseCapture();
        [DllImport("user32")] static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32")] static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
        [DllImport("user32")] static extern nint DefWindowProcW(IntPtr h, uint msg, nuint w, nint l);
        [DllImport("user32")] static extern bool ValidateRect(IntPtr h, IntPtr r);
        [DllImport("gdi32")] static extern int SetDIBitsToDevice(IntPtr dc, int x, int y, uint w, uint h, int sx, int sy, uint start, uint lines, void* bits, BITMAPINFOHEADER* bi, uint usage);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

        static readonly Dictionary<IntPtr, Win32View> views = new();
        static bool registered;
        const string ClassName = "NeoBuzzEditorView";
        IntPtr hwnd; readonly EditorInstance owner; byte* last; int lw, lh;

        Win32View(EditorInstance o) { owner = o; }

        public static Win32View Create(IntPtr parent, int w, int h, EditorInstance owner)
        {
            IntPtr inst = GetModuleHandleW(null);
            if (!registered)
            {
                var c = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), style = 0x8 /*CS_DBLCLKS*/, lpfnWndProc = &WndProc, hInstance = inst, hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512) };
                fixed (char* name = ClassName) { c.lpszClassName = (IntPtr)name; RegisterClassExW(&c); }
                registered = true;
            }
            var v = new Win32View(owner);
            v.hwnd = CreateWindowExW(0, ClassName, "Neo", 0x40000000 /*WS_CHILD*/ | 0x10000000 /*WS_VISIBLE*/ | 0x04000000 /*CLIPSIBLINGS*/, 0, 0, w, h, parent, IntPtr.Zero, inst, IntPtr.Zero);
            if (v.hwnd == IntPtr.Zero) return null;
            views[v.hwnd] = v;
            return v;
        }

        public unsafe void Present(byte* bgra, int width, int height, int stride)
        {
            if (hwnd == IntPtr.Zero) return;
            last = bgra; lw = width; lh = height;
            Paint();
        }

        void Paint()
        {
            if (last == null) return;
            var bi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = lw, biHeight = -lh, biPlanes = 1, biBitCount = 32 };
            IntPtr dc = GetDC(hwnd);
            SetDIBitsToDevice(dc, 0, 0, (uint)lw, (uint)lh, 0, 0, 0, (uint)lh, last, &bi, 0);
            ReleaseDC(hwnd, dc);
        }

        [DllImport("user32")] static extern bool PostMessageW(IntPtr h, uint msg, nuint w, nint l);
        public double BackingScale => 1;
        /// <summary>Tests: WM_LBUTTONDOWN, WM_MOUSEMOVE (button down) and WM_LBUTTONUP posted to the view's window: its WndProc, as the message loop delivers them.</summary>
        public void TestInject(int kind, double x, double y)
        {
            if (hwnd == IntPtr.Zero) return;
            nint l = (nint)((((int)y & 0xFFFF) << 16) | ((int)x & 0xFFFF));
            PostMessageW(hwnd, kind == 0 ? 0x0201u : kind == 1 ? 0x0200u : 0x0202u, kind == 2 ? (nuint)0 : (nuint)1, l);
        }

        public void Resize(int w, int h) { if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h, 0x16 /*NOMOVE|NOZORDER|NOACTIVATE*/); }
        public void SetVisible(bool v) { if (hwnd != IntPtr.Zero) ShowWindow(hwnd, v ? 5 : 0); }
        public void Pump() { }
        public void Destroy() { if (hwnd == IntPtr.Zero) return; views.Remove(hwnd); DestroyWindow(hwnd); hwnd = IntPtr.Zero; }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static nint WndProc(IntPtr h, uint msg, nuint w, nint l)
        {
            if (views.TryGetValue(h, out var v))
            {
                try
                {
                    int x = (short)(l & 0xFFFF), y = (short)((l >> 16) & 0xFFFF);
                    switch (msg)
                    {
                        case 0x0201: SetFocus(h); SetCapture(h); v.owner.MouseDown(x, y, 1); return 0;
                        case 0x0202: ReleaseCapture(); v.owner.MouseUp(x, y, 1); return 0;
                        case 0x0204: v.owner.MouseDown(x, y, 3); return 0;
                        case 0x0205: v.owner.MouseUp(x, y, 3); return 0;
                        case 0x0200: v.owner.MouseMove(x, y); return 0;
                        case 0x020A:   // WM_MOUSEWHEEL: screen coordinates, delta in the high word of wParam
                            v.owner.Wheel(v.lw / 2, v.lh / 2, (short)((w >> 16) & 0xFFFF) / 120.0); return 0;
                        case 0x000F: v.Paint(); ValidateRect(h, IntPtr.Zero); return 0;
                        case 0x0014: return 1;   // WM_ERASEBKGND
                    }
                }
                catch (Exception) { }
            }
            return DefWindowProcW(h, msg, w, l);
        }
    }
}
