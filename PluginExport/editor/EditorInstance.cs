// One open editor: the machine's own Neo editor class (NeoFilterEditor …) built over a FakeMachine, shown in an Avalonia headless
// window (Skia, no OS window), and its frames blitted into a native child window of the host's (INativeView). Everything runs on the
// host's main thread: the DSP library's timer callback calls Tick, the native window's events arrive on the host's message loop.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoExport
{
    internal sealed class EditorApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Dark;
        }
    }

    internal interface INativeView
    {
        /// <summary>Shows a frame (BGRA, top row first).</summary>
        unsafe void Present(byte* bgra, int width, int height, int stride);
        void Resize(int width, int height);
        void SetVisible(bool visible);
        /// <summary>Handles the window system's pending events (X11 only; Win32 and Cocoa deliver theirs through the host's loop).</summary>
        void Pump();
        void Destroy();
        /// <summary>Device pixels per point of the view's window (macOS Retina: 2; 1 elsewhere, where sizes are already pixels).</summary>
        double BackingScale { get; }
        /// <summary>Tests: a left mouse event (0 down, 1 move with the button down, 2 up) at a point of the view, sent the way the window system sends one.</summary>
        void TestInject(int kind, double x, double y);
    }

    internal sealed unsafe class EditorInstance
    {
        static bool appReady;
        static string libDir = "";

        readonly Shared* sh;
        readonly string pluginId;
        NeoEditor editor;
        object machine;
        FakeMachine fake;
        Window window;
        Viewbox box;
        INativeView native;
        double scale = 1;
        int natW = 640, natH = 400, pxW, pxH;
        int lw, lh;            // the size the host sees (CLAP gui get_size): pixels on Windows and X11, points on macOS
        double backing = 1;    // device pixels per point (macOS Retina 2): the frame is rendered at lw * backing
        byte* frame; int frameCap;
        int seenSeq = -1;
        bool pointerDown, visible = true;
        readonly HashSet<int> open = new HashSet<int>();
        long lastSet;
        long frames;

        EditorInstance(Shared* s, string id, double scale) { sh = s; pluginId = id; this.scale = scale > 0 ? scale : 1; }

        public static void SetDirectory(string dir)
        {
            libDir = dir ?? "";
            // SkiaSharp and HarfBuzzSharp find their native libraries next to this library, not next to the host's executable.
            foreach (var asm in new[] { typeof(SkiaSharp.SKCanvas).Assembly, typeof(HarfBuzzSharp.Blob).Assembly })
            {
                try
                {
                    NativeLibrary.SetDllImportResolver(asm, (name, a, path) =>
                    {
                        string file = name.StartsWith("lib", StringComparison.Ordinal) || OperatingSystem.IsWindows() ? name : "lib" + name;
                        string ext = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
                        string full = System.IO.Path.Combine(libDir, file.EndsWith(ext, StringComparison.Ordinal) ? file : file + ext);
                        return System.IO.File.Exists(full) && NativeLibrary.TryLoad(full, out var h) ? h : IntPtr.Zero;
                    });
                }
                catch (InvalidOperationException) { }
            }
        }

        static void EnsureApp()
        {
            if (appReady) return;
            AppBuilder.Configure<EditorApp>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .With(new FontManagerOptions { DefaultFamilyName = "Inter" })
                .SetupWithoutStarting();
            appReady = true;
        }

        /// <summary>Builds the plug-in's editor. Null when there is none for this id.</summary>
        public static EditorInstance Create(Shared* s, string id, double scale)
        {
            EnsureApp();
            var inst = new EditorInstance(s, id, scale);
            return inst.Build() ? inst : null;
        }

        bool Build()
        {
            if (!EditorRegistry.TryMake(pluginId, out editor, out machine)) return false;
            var table = editor.Table;
            fake = new FakeMachine(pluginId, table, this, machine as Buzz.MachineInterface.IBuzzMachine);
            for (int i = 0; i < fake.Count && i < Shared.MaxParams; i++) fake.P(i).Value = Volatile.Read(ref sh->Param[i]);
            SyncMachine();
            NeoEditor.NoPresets = true;   // the host has presets; no ReBuzz preset bar
            editor.Machine = fake;
            box = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = editor };
            window = new Window { Content = box, SizeToContent = SizeToContent.Manual, CanResize = true, ShowInTaskbar = false };
            // The editor's natural size: measured with room to spare, then the window is that size (times the scale).
            editor.Measure(new Size(4000, 4000));
            natW = Math.Max(64, (int)Math.Ceiling(editor.DesiredSize.Width)); natH = Math.Max(48, (int)Math.Ceiling(editor.DesiredSize.Height));
            SetLogicalSize((int)Math.Round(natW * scale), (int)Math.Round(natH * scale));
            window.Show();
            window.AddHandler(InputElement.PointerReleasedEvent, (s, e) => { }, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
            LoadGen.Publish = sh;
            Volatile.Write(ref sh->EdAlive, 1);
            string load = Environment.GetEnvironmentVariable("NEO_EXPORT_EDITOR_LOAD");
            if (int.TryParse(load, out int mode) && mode > 0) LoadGen.Start(mode);
            Tick();
            return true;
        }

        void SetLogicalSize(int w, int h)
        {
            lw = Math.Max(16, w); lh = Math.Max(16, h);
            SetPixelSize((int)Math.Round(lw * backing), (int)Math.Round(lh * backing));
        }

        void SetPixelSize(int w, int h)
        {
            pxW = Math.Max(16, w); pxH = Math.Max(16, h);
            window.Width = pxW; window.Height = pxH;
            int need = pxW * pxH * 4;
            if (need > frameCap) { if (frame != null) NativeMemory.Free(frame); frame = (byte*)NativeMemory.Alloc((nuint)need); frameCap = need; }
        }

        internal Window TestWindow => window;
        internal ParamDef[] TestTable => editor.Table;
        internal int TestMachineRaw(int i) => ((NeoBuzz.Machine.NeoMachineBase)machine).Values[i];

        public void GetSize(out uint w, out uint h) { w = (uint)lw; h = (uint)lh; }

        public bool SetScale(double s)
        {
            scale = s > 0 ? s : 1;
            return Resize((int)Math.Round(natW * scale), (int)Math.Round(natH * scale));
        }

        public bool Resize(int w, int h)
        {
            SetLogicalSize(w, h);
            native?.Resize(lw, lh);
            try { window?.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Present(); } catch (Exception) { }   // the new size is laid out and drawn now, not at some later frame
            return true;
        }

        public bool SetParent(int api, void* handle)
        {
            native?.Destroy(); native = null;
            try
            {
                if (api == 3 && OperatingSystem.IsLinux()) native = X11View.Create((ulong)handle, lw, lh, this);
                else if (api == 1 && OperatingSystem.IsWindows()) native = Win32View.Create((IntPtr)handle, lw, lh, this);
                else if (api == 2 && OperatingSystem.IsMacOS()) native = CocoaView.Create((IntPtr)handle, lw, lh, this);
            }
            catch (Exception) { native = null; }
            if (native == null) return false;
            UpdateBacking();
            native.SetVisible(visible);
            Present();
            return true;
        }

        public void Show() { visible = true; native?.SetVisible(true); }
        public void Hide() { visible = false; native?.SetVisible(false); }

        // ---------------------------------------------------------------- editor -> host
        /// <summary>The editor wrote a parameter: gesture begin (once), the value, and the gesture's end when the pointer is up.</summary>
        public void UserSet(int index, int raw)
        {
            if (index < 0 || index >= Shared.MaxParams) return;
            if (open.Add(index)) Shared.Post(sh, Shared.EditBegin, index, 0);
            Shared.Post(sh, Shared.EditValue, index, raw);
            Interlocked.Increment(ref sh->EdEditsPosted);
            lastSet = Environment.TickCount64;
            RequestFlush();
        }

        void EndGestures()
        {
            if (open.Count == 0) return;
            foreach (int i in open) Shared.Post(sh, Shared.EditEnd, i, 0);
            open.Clear();
            RequestFlush();
        }

        void RequestFlush()
        {
            if (sh->FlushFn != null) ((delegate* unmanaged[Cdecl]<void*, void>)sh->FlushFn)(sh->FlushCtx);
        }

        // ---------------------------------------------------------------- host -> editor, and the frame
        void SyncMachine()
        {
            if (machine is NeoBuzz.Machine.NeoMachineBase b)
                for (int i = 0; i < fake.Count && i < b.Table.Length; i++) b.Values.Set(i, fake.P(i).Value);
        }

        public void Tick()
        {
            if (sh == null) return;
            native?.Pump();
            UpdateBacking();
            int seq = Volatile.Read(ref sh->ParamSeq);
            if (seq != seenSeq)
            {
                seenSeq = seq;
                for (int i = 0; i < fake.Count && i < Shared.MaxParams; i++) fake.P(i).Arrived(Volatile.Read(ref sh->Param[i]));
                SyncMachine();
            }
            if (open.Count > 0 && !pointerDown && Environment.TickCount64 - lastSet > 300) EndGestures();   // a wheel turn, a key
            Dispatcher.UIThread.RunJobs();
            Present();
            Volatile.Write(ref sh->EdFrames, ++frames);
        }

        /// <summary>The window's backing scale (macOS: 2 on a Retina display, and it changes when the window moves to another display): the frame is rendered at that many pixels per point.</summary>
        void UpdateBacking()
        {
            if (native == null) return;
            double b = native.BackingScale;
            if (b < 1 || b > 4 || Math.Abs(b - backing) < 0.01) return;
            backing = b;
            SetLogicalSize(lw, lh);
        }

        void Present()
        {
            if (window == null) return;
            var bmp = window.CaptureRenderedFrame();
            if (bmp == null) return;
            using var fb = bmp.Lock();
            int w = Math.Min(fb.Size.Width, pxW), h = Math.Min(fb.Size.Height, pxH);
            bool rgba = fb.Format == PixelFormat.Rgba8888;
            for (int y = 0; y < h; y++)
            {
                uint* src = (uint*)((byte*)fb.Address + (long)y * fb.RowBytes);
                uint* dst = (uint*)(frame + (long)y * pxW * 4);
                if (rgba) for (int x = 0; x < w; x++) { uint p = src[x]; dst[x] = (p & 0xFF00FF00u) | ((p & 0xFF) << 16) | ((p >> 16) & 0xFF) | 0xFF000000u; }
                else for (int x = 0; x < w; x++) dst[x] = src[x] | 0xFF000000u;
            }
            Volatile.Write(ref sh->EdPixels, w * (long)h);
            native?.Present(frame, pxW, pxH, pxW * 4);
        }

        // ---------------------------------------------------------------- native events -> the headless window
        // (points from the native view, pixels in the headless window)
        public void MouseDown(double x, double y, int button) { pointerDown = true; window.MouseDown(new Point(x * backing, y * backing), ToButton(button)); }
        public void MouseUp(double x, double y, int button) { window.MouseUp(new Point(x * backing, y * backing), ToButton(button)); pointerDown = false; EndGestures(); }
        public void MouseMove(double x, double y) => window.MouseMove(new Point(x * backing, y * backing));
        public void Wheel(double x, double y, double dy) => window.MouseWheel(new Point(x * backing, y * backing), new Vector(0, dy));
        public void TestInject(int kind, double x, double y) => native?.TestInject(kind, x, y);
        static MouseButton ToButton(int b) => b == 3 ? MouseButton.Right : b == 2 ? MouseButton.Middle : MouseButton.Left;

        /// <summary>For the tests: where a parameter knob is, in window pixels.</summary>
        public bool Hotspot(out int x, out int y)
        {
            x = y = 0;
            int best = int.MaxValue;   // the knob of the lowest parameter index below 64: the host has a slot for it
            foreach (var v in window.GetVisualDescendants())
                if (v is NeoBuzz.Editor.ParamKnob c && c.Bounds.Width > 8 && c.ParamIndex < 64 && c.ParamIndex < best)
                {
                    var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window);
                    if (p == null) continue;
                    best = c.ParamIndex;
                    x = (int)Math.Round(p.Value.X / backing); y = (int)Math.Round(p.Value.Y / backing);   // in the native view's units
                }
            return best != int.MaxValue;
        }

        public void Destroy()
        {
            EndGestures();
            LoadGen.Stop(); LoadGen.Publish = null;
            native?.Destroy(); native = null;
            try { window?.Close(); } catch (Exception) { }
            window = null; editor = null; fake = null;
            if (frame != null) { NativeMemory.Free(frame); frame = null; frameCap = 0; }
            if (sh != null) Volatile.Write(ref sh->EdAlive, 0);
        }
    }
}
