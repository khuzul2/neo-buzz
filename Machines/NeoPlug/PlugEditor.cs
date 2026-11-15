#nullable disable
// The plug-in's editor (docs/specs/plugin-host.md H3): the plug-in's own window, shown by the studio in one of its windows
// (an HWND in Avalonia's NativeControlHost, an NSView) or floating in a window of the library's. A session is one open
// editor of one machine. Every call comes from the host's UI thread, which the library counts as the plug-in's main thread
// while the editor is attached (np_gui_attach); the one exception is PluginGone, when the plug-in is closed under an open editor.

using System;
using System.Runtime.InteropServices;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace NeoBuzz.Plug
{
    sealed class PlugEditorSession : INativeEditorSession
    {
        readonly PlugCore core;
        readonly string api;
        IntPtr plugin;
        IntPtr container;
        bool attached, disposed;
        int width, height;
        readonly PlugNative.ResizeCb resizeCb;
        readonly PlugNative.ClosedCb closedCb;

        public IntPtr Handle => container;
        public bool Floating { get; }
        public int Width => width;
        public int Height => height;
        public bool CanResize { get; private set; }
        public event Action<int, int> Resized;
        public event Action CloseRequested;

        PlugEditorSession(PlugCore core, string api, bool floating)
        {
            this.core = core;
            this.api = api;
            Floating = floating;
            // The library keeps these pointers: the session holds the delegates until it is disposed.
            resizeCb = (_, w, h) => OnPluginResize(w, h);
            closedCb = _ => CloseRequested?.Invoke();
        }

        /// <summary>Opens the editor of the core's plug-in; null with the reason in <paramref name="error"/>.</summary>
        public static PlugEditorSession Open(PlugCore core, string api, bool floating, IntPtr parent, double scale, string title, out string error)
        {
            var s = new PlugEditorSession(core, api, floating);
            lock (core.EditorLock)
            {
                s.plugin = core.PluginHandle;
                if (s.plugin == IntPtr.Zero || core.Missing)
                {
                    error = "the plug-in is not loaded";
                    return null;
                }
                int support = PlugNative.np_gui_support(s.plugin, api);
                if ((support & (floating ? 2 : 1)) == 0)
                {
                    error = floating ? "the plug-in cannot open a window of its own here" : "the plug-in's editor cannot be embedded here";
                    return null;
                }
                if (scale > 0 && OperatingSystem.IsWindows()) PlugNative.np_gui_set_scale(s.plugin, scale);
                PlugNative.np_gui_set_callbacks(s.plugin, s.resizeCb, s.closedCb, IntPtr.Zero);
                s.container = floating
                    ? PlugNative.np_platform_window(title ?? "Plug-in", 640, 480)
                    : PlugNative.np_platform_container(OperatingSystem.IsWindows() ? parent : IntPtr.Zero, 640, 480);
                if (s.container == IntPtr.Zero)
                {
                    PlugNative.np_gui_set_callbacks(s.plugin, null, null, IntPtr.Zero);
                    error = "the window could not be made";
                    return null;
                }
                var err = new byte[512];
                if (PlugNative.np_gui_attach(s.plugin, s.container, api, out int w, out int h, err, err.Length) != 0)
                {
                    error = PlugNative.Str(err);
                    PlugNative.np_gui_set_callbacks(s.plugin, null, null, IntPtr.Zero);
                    PlugNative.np_platform_release(s.container);
                    s.container = IntPtr.Zero;
                    return null;
                }
                s.attached = true;
                s.width = Math.Max(1, w);
                s.height = Math.Max(1, h);
                s.CanResize = PlugNative.np_gui_can_resize(s.plugin) == 1;
                s.SizeContainer();
                if (floating) PlugNative.np_platform_on_close(s.container, s.closedCb, IntPtr.Zero);
            }
            core.EditorOpened(s);
            error = null;
            return s;
        }

        void SizeContainer()
        {
            if (container == IntPtr.Zero) return;
            if (Floating) PlugNative.np_platform_window_size(container, width, height);
            else PlugNative.np_platform_resize(container, width, height);
        }

        // The plug-in asked for a size (its own controls, a preset with another layout): the container follows at once and the host's
        // window is told; the library then tells the plug-in the size is its own.
        void OnPluginResize(int w, int h)
        {
            if (disposed || w <= 0 || h <= 0) return;
            width = w;
            height = h;
            SizeContainer();
            Resized?.Invoke(w, h);
        }

        public void SetSize(int w, int h)
        {
            if (disposed || !attached || w <= 0 || h <= 0) return;
            lock (core.EditorLock)
            {
                if (!attached) return;
                int rw = w, rh = h;
                if (PlugNative.np_gui_set_size(plugin, ref rw, ref rh) == 0) { width = rw; height = rh; }
                else { width = w; height = h; }
            }
            if (Floating) SizeContainer();
        }

        public void SetScale(double scale)
        {
            if (disposed || !attached || scale <= 0 || !OperatingSystem.IsWindows()) return;
            lock (core.EditorLock)
                if (attached) PlugNative.np_gui_set_scale(plugin, scale);
        }

        public string Snapshot(string imagePath)
        {
            if (disposed || container == IntPtr.Zero) return null;
            var stats = new byte[1024];
            try
            {
                return PlugNative.np_platform_snapshot(container, imagePath, 0x808080, stats, stats.Length) == 0 ? PlugNative.Str(stats) : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The plug-in is being closed under an open editor (PlugMain): detach now, the host closes its window.</summary>
        internal void PluginGone()
        {
            if (!attached) return;
            attached = false;
            try { PlugNative.np_gui_detach(plugin); } catch (Exception) { }
            plugin = IntPtr.Zero;
            CloseRequested?.Invoke();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lock (core.EditorLock)
            {
                if (attached && plugin != IntPtr.Zero)
                {
                    try
                    {
                        PlugNative.np_gui_detach(plugin);
                        PlugNative.np_gui_set_callbacks(plugin, null, null, IntPtr.Zero);
                    }
                    catch (Exception) { }
                }
                attached = false;
                plugin = IntPtr.Zero;
            }
            core.EditorClosed(this);
            if (container != IntPtr.Zero)
            {
                try { PlugNative.np_platform_release(container); } catch (Exception) { }
                container = IntPtr.Zero;
            }
        }
    }
}
