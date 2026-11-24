// Fork (docs/specs/plugin-host.md H3, §8.6, §8.8): a hosted plug-in's own editor in the studio.
//
// A Neo Plug machine implements IBuzzMachineNativeEditor: the plug-in's editor is a native view (an HWND on Windows, an NSView on
// macOS) made by the plug-in. A double click on the machine opens it, in pieces that need no desktop to test:
//
//  - PluginEditorController decides: one window per machine (a second double click raises it), embedded in a studio window when
//    the plug-in can and nothing has switched it off, floating in a window of the library's when embedding failed twice or the plug-in
//    only floats, nothing at all where the platform has no editors (Linux) or the plug-in has none: the caller then opens the
//    device view as before. A closed window is forgotten, so the next double click opens it again.
//  - AvaloniaPluginEditorPlatform makes the windows; PluginEditorHost is the NativeControlHost around the plug-in's view: the same
//    pattern as a foreign-window host with the library making the container, so the plug-in
//    attaches to a view it was given (§8.6: no autoresizing on macOS, the container takes the plug-in's size before layout).
//
// Sizes: Windows plug-ins work in physical pixels, so the host tells the plug-in the window's scale and converts with it; macOS
// ones in points, which are Avalonia's units. A plug-in asking for another size (session.Resized) makes the studio window refit; a
// window the user resizes (when the plug-in allows it) passes the new size to the plug-in.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Platform
{
    using Control = Avalonia.Controls.Control;

    internal enum PluginEditorMode
    {
        /// <summary>No editor was opened: none here, or the plug-in has none.</summary>
        None,
        /// <summary>The plug-in's view is inside a studio window.</summary>
        Embedded,
        /// <summary>The plug-in's view is in a window of its own (the library's).</summary>
        Floating,
    }

    /// <summary>One open editor window of a machine.</summary>
    internal interface IPluginEditorWindow
    {
        bool IsOpen { get; }
        void Raise();
        void Close();
        INativeEditorSession Session { get; }
        event Action Closed;
    }

    /// <summary>What the controller needs of the platform: the window API and the two kinds of window.</summary>
    internal interface IPluginEditorPlatform
    {
        /// <summary>"hwnd", "nsview", or null where plug-in editors cannot be shown (Linux).</summary>
        string Api { get; }

        /// <summary>The editor in a studio window; null with <paramref name="error"/> when that did not work (nothing is left open).</summary>
        IPluginEditorWindow ShowEmbedded(IMachine machine, IBuzzMachineNativeEditor editor, string title, WindowPlacement? at, out string error);

        /// <summary>The editor in a window of the library's; null with <paramref name="error"/> when that did not work.</summary>
        IPluginEditorWindow ShowFloating(IMachine machine, IBuzzMachineNativeEditor editor, string title, out string error);
    }

    internal sealed class PluginEditorController
    {
        /// <summary>Embedding is tried this often for a machine before its editor floats instead ("two real attempts").</summary>
        public const int MaxAttempts = 2;

        readonly IPluginEditorPlatform platform;
        readonly Dictionary<IMachine, (IPluginEditorWindow Window, PluginEditorMode Mode)> open = new();
        readonly Dictionary<IMachine, int> failures = new();

        public PluginEditorController(IPluginEditorPlatform platform)
        {
            this.platform = platform;
        }

        /// <summary>Whether editors are embedded at all (NEOBUZZ_PLUGIN_GUI=floating switches it off).</summary>
        public bool EmbeddingOn { get; set; } = true;

        /// <summary>Why the last attempt did not work (for the log and tests).</summary>
        public string LastError { get; private set; }

        public string Api => platform.Api;

        public int OpenCount => open.Count;

        public PluginEditorMode ModeOf(IMachine machine) => open.TryGetValue(machine, out var e) && e.Window.IsOpen ? e.Mode : PluginEditorMode.None;

        public IPluginEditorWindow WindowOf(IMachine machine) => open.TryGetValue(machine, out var e) && e.Window.IsOpen ? e.Window : null;

        /// <summary>Whether a machine has an editor to show here (without opening it).</summary>
        public bool Has(IMachine machine)
        {
            string api = platform.Api;
            return api != null && machine?.ManagedMachine is IBuzzMachineNativeEditor ed && ed.EditorSupport(api) != 0;
        }

        /// <summary>Opens the machine's editor, or raises it when it is open already. None: the caller shows something else.</summary>
        public PluginEditorMode Open(IMachine machine, WindowPlacement? at = null)
        {
            string api = platform.Api;
            if (api == null || machine?.ManagedMachine is not IBuzzMachineNativeEditor editor) return PluginEditorMode.None;
            if (open.TryGetValue(machine, out var held))
            {
                if (held.Window.IsOpen)
                {
                    held.Window.Raise();
                    return held.Mode;
                }
                open.Remove(machine);
            }
            int support = editor.EditorSupport(api);
            if (support == 0) return PluginEditorMode.None;

            failures.TryGetValue(machine, out int failed);
            string title = machine.Name;
            if (EmbeddingOn && (support & 1) != 0)
            {
                while (failed < MaxAttempts)
                {
                    IPluginEditorWindow window = null;
                    string error = null;
                    try { window = platform.ShowEmbedded(machine, editor, title, at, out error); }
                    catch (Exception e) { error = e.Message; }
                    if (window != null)
                    {
                        failures.Remove(machine);
                        return Track(machine, window, PluginEditorMode.Embedded);
                    }
                    LastError = error;
                    failures[machine] = ++failed;
                }
            }
            if ((support & 2) != 0)
            {
                IPluginEditorWindow window = null;
                string error = null;
                try { window = platform.ShowFloating(machine, editor, title, out error); }
                catch (Exception e) { error = e.Message; }
                if (window != null) return Track(machine, window, PluginEditorMode.Floating);
                LastError = error;
            }
            return PluginEditorMode.None;
        }

        PluginEditorMode Track(IMachine machine, IPluginEditorWindow window, PluginEditorMode mode)
        {
            open[machine] = (window, mode);
            window.Closed += () =>
            {
                if (open.TryGetValue(machine, out var e) && ReferenceEquals(e.Window, window)) open.Remove(machine);
            };
            return mode;
        }

        /// <summary>Closes the machine's editor (the machine is being deleted).</summary>
        public void Close(IMachine machine)
        {
            if (open.TryGetValue(machine, out var e))
            {
                open.Remove(machine);
                e.Window.Close();
            }
            failures.Remove(machine);
        }
    }

    /// <summary>The plug-in's view inside a studio window: a NativeControlHost around the container the plug-in attached to.</summary>
    internal sealed class PluginEditorHost : NativeControlHost
    {
        readonly IBuzzMachineNativeEditor editor;
        readonly string api;
        readonly string title;
        INativeEditorSession session;
        bool adjusting;

        public PluginEditorHost(IBuzzMachineNativeEditor editor, string api, string title)
        {
            this.editor = editor;
            this.api = api;
            this.title = title;
            Focusable = true;
            AutomationProperties.SetName(this, "Plug-in editor");
        }

        /// <summary>The open editor (null until the native control is made, and after it is destroyed).</summary>
        public INativeEditorSession Session => session;

        /// <summary>Why the editor could not be opened.</summary>
        public string Error { get; private set; }

        /// <summary>The session was made (or lost): the window around the host adapts (its resizing, its size).</summary>
        public event Action SessionChanged;

        /// <summary>The plug-in asked for another size: the window around the host refits.</summary>
        public event Action ContentSizeChanged;

        /// <summary>The plug-in closed its editor (or went away): the window around the host closes.</summary>
        public event Action CloseRequested;

        /// <summary>The size a plug-in has before it is open (and a plug-in with none): what most editors fit in.</summary>
        public static readonly Size DefaultSize = new Size(640, 480);

        /// <summary>Native units per Avalonia unit: Windows is in physical pixels, macOS in points.</summary>
        double Ppd => OperatingSystem.IsWindows() ? (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0) : 1.0;

        protected override Size MeasureOverride(Size availableSize)
        {
            if (session == null) return DefaultSize;
            double ppd = Ppd;
            return new Size(session.Width / ppd, session.Height / ppd);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = base.ArrangeOverride(finalSize);
            if (session != null && session.CanResize && !adjusting)
            {
                double ppd = Ppd;
                int w = Math.Max(1, (int)Math.Round(size.Width * ppd)), h = Math.Max(1, (int)Math.Round(size.Height * ppd));
                if (Math.Abs(w - session.Width) > 1 || Math.Abs(h - session.Height) > 1)
                {
                    adjusting = true;
                    try { session.SetSize(w, h); }
                    finally { adjusting = false; }
                }
            }
            return size;
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            string error = null;
            INativeEditorSession made = null;
            try { made = editor.OpenEditor(api, false, parent.Handle, scale, title, out error); }
            catch (Exception e) { error = e.Message; }
            if (made == null || made.Handle == IntPtr.Zero)
            {
                made?.Dispose();
                Error = error ?? "the editor could not be opened";
                return base.CreateNativeControlCore(parent);
            }
            Error = null;
            session = made;
            session.Resized += OnPluginResized;
            session.CloseRequested += OnCloseRequested;
            InvalidateMeasure();
            SessionChanged?.Invoke();
            return new PlatformHandle(session.Handle, api == "hwnd" ? "HWND" : "NSView");
        }

        void OnPluginResized(int w, int h)
        {
            if (adjusting) return;   // our own SetSize is answering
            InvalidateMeasure();
            ContentSizeChanged?.Invoke();
        }

        // The plug-in closes under the editor (it is replaced or deleted) on the plug-in host's thread: the window closes on the UI thread.
        void OnCloseRequested() => Dispatcher.UIThread.Post(() => CloseRequested?.Invoke());

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (TopLevel.GetTopLevel(this) is { } top) top.ScalingChanged += OnScalingChanged;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (TopLevel.GetTopLevel(this) is { } top) top.ScalingChanged -= OnScalingChanged;
            base.OnDetachedFromVisualTree(e);
        }

        void OnScalingChanged(object sender, EventArgs e)
        {
            session?.SetScale(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);
            InvalidateMeasure();
            ContentSizeChanged?.Invoke();
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            var s = session;
            session = null;
            if (s != null)
            {
                s.Resized -= OnPluginResized;
                s.CloseRequested -= OnCloseRequested;
                s.Dispose();   // detaches the plug-in's view and destroys the container
            }
            SessionChanged?.Invoke();
        }
    }

    /// <summary>The platform for PluginEditorController: Avalonia windows, with the library's own window for the floating fallback.</summary>
    internal sealed class AvaloniaPluginEditorPlatform : IPluginEditorPlatform
    {
        public string Api { get; }

        public AvaloniaPluginEditorPlatform()
        {
            bool off = string.Equals(Environment.GetEnvironmentVariable("NEOBUZZ_PLUGIN_GUI"), "off", StringComparison.OrdinalIgnoreCase);
            Api = off ? null : OperatingSystem.IsWindows() ? "hwnd" : OperatingSystem.IsMacOS() ? "nsview" : null;
        }

        public IPluginEditorWindow ShowEmbedded(IMachine machine, IBuzzMachineNativeEditor editor, string title, WindowPlacement? at, out string error)
        {
            var host = new PluginEditorHost(editor, Api, title);
            // Fixed until the plug-in says it can be resized (its own flag, below).
            var window = StudioMachineUiHost.MakeNativeWindow(machine, title, host, resizable: false);
            var result = new EmbeddedEditorWindow(window, host);
            host.SessionChanged += () =>
            {
                if (host.Session != null) window.CanMaximize = window.CanResize = host.Session.CanResize;
            };
            host.ContentSizeChanged += () => window.SizeToContent = SizeToContent.WidthAndHeight;
            host.CloseRequested += () => window.Close();
            StudioMachineUiHost.ShowNativeWindow(window);
            // The native control is made when the window is first laid out; make sure it was.
            if (host.Session == null) window.UpdateLayout();
            if (host.Session != null)
            {
                error = null;
                return result;
            }
            error = host.Error ?? "the editor could not be embedded";
            result.Abandon();
            window.Close();
            return null;
        }

        public IPluginEditorWindow ShowFloating(IMachine machine, IBuzzMachineNativeEditor editor, string title, out string error)
        {
            double scale = Dialogs.Owner?.RenderScaling ?? 1.0;
            var session = editor.OpenEditor(Api, true, IntPtr.Zero, scale, title, out error);
            if (session == null) return null;
            return new FloatingEditorWindow(session);
        }

        sealed class EmbeddedEditorWindow : IPluginEditorWindow
        {
            readonly Window window;
            readonly PluginEditorHost host;
            bool closed;

            public EmbeddedEditorWindow(Window window, PluginEditorHost host)
            {
                this.window = window;
                this.host = host;
                window.Closed += (s, e) => Finish();
            }

            public bool IsOpen => !closed && window.IsVisible;
            public INativeEditorSession Session => host.Session;
            public event Action Closed;
            public void Raise() => window.Activate();
            public void Close() => window.Close();
            public void Abandon() => closed = true;

            void Finish()
            {
                if (closed) return;
                closed = true;
                Closed?.Invoke();
            }
        }

        sealed class FloatingEditorWindow : IPluginEditorWindow
        {
            readonly INativeEditorSession session;
            bool closed;

            public FloatingEditorWindow(INativeEditorSession session)
            {
                this.session = session;
                session.CloseRequested += () => Dispatcher.UIThread.Post(Close);
            }

            public bool IsOpen => !closed;
            public INativeEditorSession Session => session;
            public event Action Closed;
            public void Raise() { }

            public void Close()
            {
                if (closed) return;
                closed = true;
                session.Dispose();
                Closed?.Invoke();
            }
        }
    }
}
