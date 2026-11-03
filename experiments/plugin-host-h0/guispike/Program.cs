// H0 GUI spike: plugin views in an Avalonia 12 NativeControlHost (embedded), and in a floating
// window of the library's own, on macOS (NSView) and Windows (HWND). For each --plugin it opens a
// window whose content is a NativeControlHost; the host's native control is a container view
// (np_platform_container) into which the plugin attaches its view (VST3 IPlugView::attached, CLAP
// gui.set_parent). It then records the view tree (is the plugin's view inside the Avalonia window?)
// and snapshots the pixels (did it draw?), opens the same plugin in a floating window, and does
// the same there. Results: <out>/gui-results.json and PNGs (BMPs on Windows).
//
//   NeoPlugGuiSpike --out <dir> --plugin "<path>|<id>" [--plugin ...] [--hold <seconds>]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace NeoPlugGuiSpike
{
    static class Program
    {
        public static string Out = ".";
        public static List<string> Plugins = new();
        public static double Hold = 2.5;

        [STAThread]
        public static int Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) Out = args[++i];
                else if (args[i] == "--plugin" && i + 1 < args.Length) Plugins.Add(args[++i]);
                else if (args[i] == "--hold" && i + 1 < args.Length) Hold = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            Directory.CreateDirectory(Out);
            return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }

        public static string Api => OperatingSystem.IsMacOS() ? "nsview" : OperatingSystem.IsWindows() ? "hwnd" : "x11";
        public static string Descriptor => OperatingSystem.IsMacOS() ? "NSView" : OperatingSystem.IsWindows() ? "HWND" : "XID";
        public static string ImageExt => OperatingSystem.IsWindows() ? ".bmp" : ".png";
    }

    class App : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                Dispatcher.UIThread.Post(async () =>
                {
                    int code = 1;
                    try
                    {
                        code = await Runner.Run();
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine("gui spike failed: " + e);
                    }
                    desktop.Shutdown(code);
                });
            base.OnFrameworkInitializationCompleted();
        }
    }

    /// <summary>The plugin's view inside Avalonia: a NativeControlHost whose control is our container.</summary>
    class PluginView : NativeControlHost
    {
        readonly string path, id;
        public IntPtr Plugin, Container;
        public bool Attached;
        public string Error = "";
        public int PluginWidth, PluginHeight;

        public PluginView(string path, string id)
        {
            this.path = path;
            this.id = id;
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            var err = new byte[512];
            Plugin = Native.np_open(path, string.IsNullOrEmpty(id) ? null : id, err, err.Length);
            if (Plugin == IntPtr.Zero)
            {
                Error = "open: " + Native.Str(err);
                return base.CreateNativeControlCore(parent);
            }
            Native.np_activate(Plugin, 48000, 256, 0, err, err.Length);
            // macOS: an unparented NSView that Avalonia adds to its own tree; Windows: a child of
            // the HWND Avalonia gives us (it reparents it as needed).
            Container = Native.np_platform_container(OperatingSystem.IsWindows() ? parent.Handle : IntPtr.Zero, 1000, 760);
            int support = Native.np_gui_support(Plugin, Program.Api);
            if ((support & 1) == 0)
            {
                Error = $"the plugin can't embed in {Program.Api} (support {support})";
                return new PlatformHandle(Container, Program.Descriptor);
            }
            if (Native.np_gui_attach(Plugin, Container, Program.Api, out PluginWidth, out PluginHeight, err, err.Length) == 0) Attached = true;
            else Error = "attach: " + Native.Str(err);
            return new PlatformHandle(Container, Program.Descriptor);
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (Plugin != IntPtr.Zero)
            {
                if (Attached) Native.np_gui_detach(Plugin);
                Native.np_deactivate(Plugin);
                Native.np_close(Plugin);
                Plugin = IntPtr.Zero;
            }
            if (Container != IntPtr.Zero) Native.np_platform_release(Container);
            Container = IntPtr.Zero;
        }
    }

    static class Runner
    {
        static async Task Pump(double seconds, IntPtr plugin)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                if (plugin != IntPtr.Zero) Native.np_idle(plugin);
                await Task.Delay(50);
            }
        }

        static JsonNode Inspect(IntPtr view, string image, int rgb)
        {
            var tree = new byte[16384];
            Native.np_platform_describe(view, tree, tree.Length);
            var stats = new byte[1024];
            Native.np_platform_snapshot(view, image, rgb, stats, stats.Length);
            var o = new JsonObject { ["tree"] = Native.Str(tree), ["image"] = image };
            try { o["snapshot"] = JsonNode.Parse(Native.Str(stats)); } catch { o["snapshot"] = Native.Str(stats); }
            return o;
        }

        static void ScreenCapture(string file)
        {
            if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/sbin/screencapture")) return;
            try
            {
                using var p = Process.Start(new ProcessStartInfo("/usr/sbin/screencapture", $"-x \"{file}\"") { UseShellExecute = false });
                p?.WaitForExit(10000);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("screencapture: " + e.Message);
            }
        }

        public static async Task<int> Run()
        {
            var results = new JsonArray();
            bool allEmbedded = true;
            int index = 0;
            foreach (var spec in Program.Plugins)
            {
                var parts = spec.Split('|');
                string path = parts[0], id = parts.Length > 1 ? parts[1] : "";
                string tag = $"p{index++}";
                var entry = new JsonObject { ["plugin"] = spec, ["api"] = Program.Api };

                // 1. Embedded in an Avalonia window through NativeControlHost.
                var view = new PluginView(path, id) { Width = 1000, Height = 760 };
                var window = new Window { Title = "neoplug embed " + tag, Width = 1020, Height = 800, Content = view };
                window.Show();
                await Pump(0.5, IntPtr.Zero);
                if (view.Attached && view.PluginWidth > 0 && view.PluginHeight > 0)
                {
                    view.Width = view.PluginWidth;
                    view.Height = view.PluginHeight;
                    window.Width = view.PluginWidth + 20;
                    window.Height = view.PluginHeight + 20;
                }
                await Pump(Program.Hold, view.Plugin);
                var embed = new JsonObject
                {
                    ["attached"] = view.Attached,
                    ["error"] = view.Error,
                    ["size"] = new JsonArray(view.PluginWidth, view.PluginHeight),
                    ["avalonia_window"] = window.Title,
                    ["avalonia_handle"] = window.TryGetPlatformHandle()?.HandleDescriptor ?? "",
                };
                if (view.Container != IntPtr.Zero)
                    embed["inspect"] = Inspect(view.Container, Path.Combine(Program.Out, $"{tag}-embedded{Program.ImageExt}"), 0x1E90FF);
                ScreenCapture(Path.Combine(Program.Out, $"{tag}-screen.png"));
                string tree = embed["inspect"]?["tree"]?.GetValue<string>() ?? "";
                bool inWindow = tree.StartsWith("window: neoplug embed " + tag) || tree.Contains("neoplug embed " + tag);
                embed["inside_avalonia_window"] = inWindow;
                entry["embedded"] = embed;
                window.Close();
                await Pump(0.5, IntPtr.Zero);
                allEmbedded &= view.Attached && inWindow;

                // 2. A floating window of the library's own (what a sandboxed plugin would get on macOS).
                var err = new byte[512];
                IntPtr p = Native.np_open(path, string.IsNullOrEmpty(id) ? null : id, err, err.Length);
                var floating = new JsonObject();
                if (p != IntPtr.Zero)
                {
                    Native.np_activate(p, 48000, 256, 0, err, err.Length);
                    IntPtr content = Native.np_platform_window("neoplug float " + tag, 1000, 760);
                    bool ok = Native.np_gui_attach(p, content, Program.Api, out int w, out int h, err, err.Length) == 0;
                    floating["attached"] = ok;
                    floating["error"] = ok ? "" : Native.Str(err);
                    floating["size"] = new JsonArray(w, h);
                    await Pump(Program.Hold, p);
                    floating["inspect"] = Inspect(content, Path.Combine(Program.Out, $"{tag}-floating{Program.ImageExt}"), 0x1E90FF);
                    if (ok) Native.np_gui_detach(p);
                    Native.np_deactivate(p);
                    Native.np_close(p);
                    Native.np_platform_release(content);
                }
                else
                    floating["error"] = "open: " + Native.Str(err);
                entry["floating"] = floating;
                results.Add(entry);
                Console.WriteLine(entry.ToJsonString());
            }
            File.WriteAllText(Path.Combine(Program.Out, "gui-results.json"), results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return allEmbedded ? 0 : 1;
        }
    }

    static class Native
    {
        const string Lib = "neoplug";

        static Native()
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, assembly, path) =>
            {
                if (name != Lib) return IntPtr.Zero;
                string env = Environment.GetEnvironmentVariable("NEOPLUG_LIB");
                if (!string.IsNullOrEmpty(env) && NativeLibrary.TryLoad(env, out var h)) return h;
                string file = OperatingSystem.IsWindows() ? "neoplug.dll" : OperatingSystem.IsMacOS() ? "libneoplug.dylib" : "libneoplug.so";
                return NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, file), out h) ? h : IntPtr.Zero;
            });
        }

        public static string Str(byte[] b)
        {
            int n = Array.IndexOf(b, (byte)0);
            return System.Text.Encoding.UTF8.GetString(b, 0, n < 0 ? b.Length : n);
        }

        [DllImport(Lib)] public static extern IntPtr np_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, byte[] err, int errlen);
        [DllImport(Lib)] public static extern void np_close(IntPtr p);
        [DllImport(Lib)] public static extern int np_activate(IntPtr p, double rate, int maxFrames, int offline, byte[] err, int errlen);
        [DllImport(Lib)] public static extern void np_deactivate(IntPtr p);
        [DllImport(Lib)] public static extern void np_idle(IntPtr p);
        [DllImport(Lib)] public static extern int np_gui_support(IntPtr p, [MarshalAs(UnmanagedType.LPUTF8Str)] string api);
        [DllImport(Lib)] public static extern int np_gui_attach(IntPtr p, IntPtr parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string api, out int w, out int h, byte[] err, int errlen);
        [DllImport(Lib)] public static extern void np_gui_detach(IntPtr p);
        [DllImport(Lib)] public static extern IntPtr np_platform_container(IntPtr parent, int w, int h);
        [DllImport(Lib)] public static extern IntPtr np_platform_window([MarshalAs(UnmanagedType.LPUTF8Str)] string title, int w, int h);
        [DllImport(Lib)] public static extern int np_platform_describe(IntPtr view, byte[] buf, int len);
        [DllImport(Lib)] public static extern int np_platform_snapshot(IntPtr view, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int rgb, byte[] stats, int len);
        [DllImport(Lib)] public static extern void np_platform_release(IntPtr handle);
    }
}
