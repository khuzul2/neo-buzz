// Fork (docs/specs/ux-overhaul.md P6c, §9 row 1): the Windows side of NativeGui.cs. A native machine's window lives in its host process;
// this takes it into a studio window the way H0's spike embeds a plugin's view (docs/specs/plugin-host.md §8.6): a NativeControlHost
// whose native control is a child window of ours, with the machine's window made a child of that one (SetParent works across
// processes; both windows then share an input queue). The machine's window loses its caption and frame while it is inside and gets
// them back, hidden, when the studio window closes, so the machine finds it as it left it. The app manifest (supported OS list) is
// what lets Avalonia's NativeControlHost create its child windows (H0 finding).
//
// Not reachable from a test without a desktop (Avalonia's headless platform has no window handles): NativeGui.cs holds the decisions
// and the fallback and is tested with a fake platform; this file is checked on the Windows runner by hand and by the package's smoke.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Platform
{
    using Control = Avalonia.Controls.Control;

    internal static class Win32
    {
        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
        public const long WS_POPUP = 0x80000000L, WS_CHILD = 0x40000000L, WS_VISIBLE = 0x10000000L, WS_CAPTION = 0x00C00000L,
            WS_THICKFRAME = 0x00040000L, WS_SYSMENU = 0x00080000L, WS_MINIMIZEBOX = 0x00020000L, WS_MAXIMIZEBOX = 0x00010000L,
            WS_BORDER = 0x00800000L, WS_DLGFRAME = 0x00400000L, WS_CLIPCHILDREN = 0x02000000L, WS_CLIPSIBLINGS = 0x04000000L;
        public const long WS_EX_TOPMOST = 0x8, WS_EX_APPWINDOW = 0x40000, WS_EX_TOOLWINDOW = 0x80, WS_EX_DLGMODALFRAME = 0x1,
            WS_EX_WINDOWEDGE = 0x100, WS_EX_CLIENTEDGE = 0x200, WS_EX_STATICEDGE = 0x20000;
        public const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40, SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1;
        public const int SW_HIDE = 0, SW_SHOWNA = 8;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(int exStyle, string className, string name, uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        public static long GetLong(IntPtr hwnd, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index).ToInt64() : GetWindowLong32(hwnd, index);

        public static void SetLong(IntPtr hwnd, int index, long value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, index, new IntPtr(value));
            else SetWindowLong32(hwnd, index, unchecked((int)value));
        }
    }

    /// <summary>The platform for NativeGuiController on Windows: lists host windows, embeds in studio windows.</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class Win32NativeGui : INativeGuiPlatform
    {
        readonly Dictionary<IntPtr, Window> windows = new();
        /// <summary>Called when a studio window around a machine's window closes (the controller's claim ends).</summary>
        public Action<object> Closed;

        public IReadOnlyList<ForeignWindow> TopLevelWindows(int processId)
        {
            var list = new List<ForeignWindow>();
            Win32.EnumWindows((hwnd, _) =>
            {
                Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != (uint)processId) return true;
                var title = new StringBuilder(256);
                var cls = new StringBuilder(128);
                Win32.GetWindowText(hwnd, title, title.Capacity);
                Win32.GetClassName(hwnd, cls, cls.Capacity);
                Win32.GetWindowRect(hwnd, out var r);
                list.Add(new ForeignWindow(hwnd, processId, title.ToString(), cls.ToString(), Win32.IsWindowVisible(hwnd), r.Width, r.Height));
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public bool Exists(IntPtr handle) => Win32.IsWindow(handle);

        public void Raise(IntPtr handle)
        {
            if (windows.TryGetValue(handle, out var w) && w.IsVisible) w.Activate();
        }

        public void Later(TimeSpan delay, Action again)
        {
            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                again();
            };
            timer.Start();
        }

        public bool Embed(string machineName, ForeignWindow window, object machine)
        {
            var host = new ForeignWindowHost(window.Handle);
            var studioWindow = StudioMachineUiHost.MakeNativeWindow((IMachine)machine, machineName, host, host.Resizable);
            studioWindow.Closed += (s, e) =>
            {
                windows.Remove(window.Handle);
                Closed?.Invoke(machine);
            };
            windows[window.Handle] = studioWindow;
            host.Gone += () => studioWindow.Close();
            StudioMachineUiHost.ShowNativeWindow(studioWindow);
            // The native control is made when the window is first laid out; make sure it was.
            if (!host.Embedded) studioWindow.UpdateLayout();
            if (host.Embedded) return true;
            windows.Remove(window.Handle);
            studioWindow.Close();
            return false;
        }
    }

    /// <summary>
    /// The machine's window inside a studio window: a NativeControlHost whose native control is a child window of the studio window, with
    /// the machine's window a child of that.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class ForeignWindowHost : NativeControlHost
    {
        readonly IntPtr foreign;
        IntPtr container;
        long savedStyle, savedExStyle, savedOwner;
        Win32.RECT savedRect;
        int clientWidth, clientHeight;
        DispatcherTimer watch;

        public ForeignWindowHost(IntPtr foreign)
        {
            this.foreign = foreign;
            Win32.GetWindowRect(foreign, out savedRect);
            Win32.GetClientRect(foreign, out var client);
            clientWidth = client.Width;
            clientHeight = client.Height;
            Resizable = (Win32.GetLong(foreign, Win32.GWL_STYLE) & Win32.WS_THICKFRAME) != 0;
            Focusable = true;
            AutomationProperties.SetName(this, "Machine window");
        }

        /// <summary>The machine's window is inside (set once the native control exists and the reparent worked).</summary>
        public bool Embedded { get; private set; }

        /// <summary>The window had a sizing frame: the studio window may be resized and the machine's window follows.</summary>
        public bool Resizable { get; }

        /// <summary>The machine's window went away or hid itself: the studio window around it has nothing to show.</summary>
        public event Action Gone;

        double Scale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

        protected override Size MeasureOverride(Size availableSize) => new Size(clientWidth / Scale, clientHeight / Scale);

        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = base.ArrangeOverride(finalSize);
            if (Embedded && Resizable)
                Win32.SetWindowPos(foreign, IntPtr.Zero, 0, 0, Math.Max(1, (int)(size.Width * Scale)), Math.Max(1, (int)(size.Height * Scale)), Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
            return size;
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            container = Win32.CreateWindowEx(0, "Static", "", (uint)(Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS),
                0, 0, Math.Max(1, clientWidth), Math.Max(1, clientHeight), parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (container == IntPtr.Zero) return base.CreateNativeControlCore(parent);
            Embedded = TryReparent();
            if (Embedded)
            {
                watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                watch.Tick += (s, e) =>
                {
                    // The machine destroyed its window, or hid it (its own Close): nothing left to show.
                    if (!Win32.IsWindow(foreign) || (Win32.GetLong(foreign, Win32.GWL_STYLE) & Win32.WS_VISIBLE) == 0)
                    {
                        watch.Stop();
                        Gone?.Invoke();
                    }
                };
                watch.Start();
            }
            return new PlatformHandle(container, "HWND");
        }

        bool TryReparent()
        {
            try
            {
                savedStyle = Win32.GetLong(foreign, Win32.GWL_STYLE);
                savedExStyle = Win32.GetLong(foreign, Win32.GWL_EXSTYLE);
                savedOwner = Win32.GetLong(foreign, Win32.GWLP_HWNDPARENT);
                // A popup becomes a child: no caption, frame or taskbar presence; the style first, then the parent (SetParent's remark).
                long style = (savedStyle & ~(Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_THICKFRAME | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX | Win32.WS_BORDER | Win32.WS_DLGFRAME))
                             | Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS;
                long ex = savedExStyle & ~(Win32.WS_EX_TOPMOST | Win32.WS_EX_APPWINDOW | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_DLGMODALFRAME | Win32.WS_EX_WINDOWEDGE | Win32.WS_EX_CLIENTEDGE | Win32.WS_EX_STATICEDGE);
                Win32.SetLong(foreign, Win32.GWLP_HWNDPARENT, 0);
                Win32.SetLong(foreign, Win32.GWL_STYLE, style);
                Win32.SetLong(foreign, Win32.GWL_EXSTYLE, ex);
                Win32.SetParent(foreign, container);
                // SetParent answers the previous parent (none for a top-level window), so its result proves nothing: ask.
                if (Win32.GetParent(foreign) != container)
                {
                    Restore();
                    return false;
                }
                Win32.SetWindowPos(foreign, IntPtr.Zero, 0, 0, clientWidth, clientHeight, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
                return true;
            }
            catch (Exception)
            {
                Restore();
                return false;
            }
        }

        /// <summary>The machine's window as it was, hidden: a floating window again, for the machine to show when it likes.</summary>
        void Restore()
        {
            try
            {
                if (!Win32.IsWindow(foreign)) return;
                Win32.ShowWindow(foreign, Win32.SW_HIDE);
                Win32.SetParent(foreign, IntPtr.Zero);
                Win32.SetLong(foreign, Win32.GWL_STYLE, savedStyle & ~Win32.WS_VISIBLE);
                Win32.SetLong(foreign, Win32.GWL_EXSTYLE, savedExStyle);
                Win32.SetLong(foreign, Win32.GWLP_HWNDPARENT, savedOwner);
                Win32.SetWindowPos(foreign, IntPtr.Zero, savedRect.Left, savedRect.Top, savedRect.Width, savedRect.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
            }
            catch (Exception)
            {
            }
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            watch?.Stop();
            watch = null;
            // The machine's window leaves first: destroying the container would destroy it with it.
            if (Embedded) Restore();
            Embedded = false;
            if (container != IntPtr.Zero)
            {
                Win32.DestroyWindow(container);
                container = IntPtr.Zero;
            }
        }
    }
}
