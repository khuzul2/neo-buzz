// Fork (docs/specs/plugin-host.md H3): CI's check of plug-in editors in a real window. `ReBuzz.Studio --plugin-gui-check "<plug-in name>"`
// starts the engine as usual, adds a Neo Plug (or, with --plugin-gui-fx, a Neo Plug FX) for the plug-in whose Gear name contains the text,
// and does what a double click, a resize and a close would: opens the editor (the first check wants it embedded, or floating with
// --plugin-gui-floating), looks at what the plug-in drew in its view, resizes it from the host's side, closes it, opens it again, and
// checks one window per machine. The result goes to the console ("plugin-gui: ok ..." or "plugin-gui: FAIL ...") and to the exit code.
// Needs a desktop (Windows and macOS runners); the decisions it exercises are tested headless (PluginHostUiTests).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Platform
{
    internal static class PluginGuiCheck
    {
        /// <summary>The plug-in's name to look for, from the command line; null when the check isn't asked for.</summary>
        public static string PlugIn { get; set; }
        public static bool Effect { get; set; }
        /// <summary>The plug-in runs in a sandbox (the environment's choice, else the platform's default).</summary>
        static bool Sandboxed => NeoBuzz.Plug.PlugPlacement.IsSandbox(NeoBuzz.Plug.PlugPlacement.Resolve("", "", "", PlugIn, null, out _));
        public static bool Floating { get; set; }

        static readonly List<string> failures = new();

        static void Say(string line)
        {
            Console.Out.WriteLine("plugin-gui: " + line);
            Console.Out.Flush();
            // A GUI-subsystem exe on Windows may have no console to print to: the log file is the sure way.
            string log = Environment.GetEnvironmentVariable("NEOBUZZ_PLUGIN_GUI_LOG");
            if (!string.IsNullOrEmpty(log))
                try { System.IO.File.AppendAllText(log, "plugin-gui: " + line + "\n"); } catch (Exception) { }
        }

        static void Check(bool ok, string what)
        {
            Say((ok ? "ok " : "FAIL ") + what);
            if (!ok) failures.Add(what);
        }

        static async Task Pause(int ms) => await Task.Delay(ms);

        static int Colours(INativeEditorSession s)
        {
            string stats = s?.Snapshot(null);
            var m = stats == null ? null : Regex.Match(stats, "\"colors\":(\\d+)");
            return m is { Success: true } ? int.Parse(m.Groups[1].Value) : -1;
        }

        public static async Task Run(StudioHost host, IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                await Pause(500);
                string gear = Effect ? "Neo Plug FX" : "Neo Plug";
                var instrument = host.Core.Instruments.FirstOrDefault(i => i.MachineDLL?.Name == gear && i.Name != null && i.Name.Contains(PlugIn, StringComparison.OrdinalIgnoreCase));
                Check(instrument != null, $"{gear} lists a plug-in named \"{PlugIn}\"" + (instrument == null ? "" : $" ({instrument.Name})"));
                if (instrument != null) await Exercise(host, gear, instrument.Name, desktop);
            }
            catch (Exception e)
            {
                Check(false, "the check threw " + e);
            }
            Say(failures.Count == 0 ? "all passed" : $"{failures.Count} failed");
            int code = failures.Count == 0 ? 0 : 1;
            desktop.Shutdown(code);
            // The studio's shutdown can wait on the plug-in host's threads: the verdict is in, so leave.
            await Pause(3000);
            Environment.Exit(code);
        }

        static async Task Exercise(StudioHost host, string gear, string instrument, IClassicDesktopStyleApplicationLifetime desktop)
        {
            var song = host.Core.SongCore;
            song.CreateMachine(gear, instrument, "GuiCheck", null, null, null, Effect ? -1 : 2, 0.3f, 0.3f);
            var machine = song.MachinesList.LastOrDefault(m => m.Name == "GuiCheck");
            Check(machine != null, "the machine was added");
            if (machine == null) return;
            var controller = StudioMachineUiHost.PluginEditors;
            controller.EmbeddingOn = !Floating;
            Say($"api {controller.Api}, display scale {desktop.MainWindow?.RenderScaling ?? 0}");
            Check(controller.Has(machine), "the plug-in has an editor for this window API");
            if (!controller.Has(machine)) return;

            var want = Floating || Sandboxed ? PluginEditorMode.Floating : PluginEditorMode.Embedded;
            for (int round = 1; round <= 2; round++)
            {
                string r = round == 1 ? "first open" : "reopen";
                Say($"{r}: opening the editor");
                var mode = controller.Open(machine);
                Check(mode == want, $"{r}: the editor is {want.ToString().ToLowerInvariant()} (got {mode}{(controller.LastError != null ? ", " + controller.LastError : "")})");
                await Pause(2500);   // the plug-in draws, the window lays out
                var session = controller.WindowOf(machine)?.Session;
                Check(session != null, $"{r}: there is an open editor session");
                if (session == null) return;
                Check(session.Width >= 100 && session.Height >= 100, $"{r}: the view is {session.Width} x {session.Height}");
                int colours = Colours(session);
                // A sandboxed editor is a window of another process: its pixels can't be read from here (H2), so only that it opened is checked.
                if (colours < 0 && Sandboxed) Say($"ok {r}: the editor is a window of the plug-in's sandbox (its pixels are not readable from here)");
                else Check(colours > 300, $"{r}: the plug-in drew its view ({colours} colours)");

                if (round == 1)
                {
                    Check(controller.Open(machine) == want && controller.OpenCount == 1, "a second open raises the same window: one window per instance");
                    if (session.CanResize)
                    {
                        int w0 = session.Width, h0 = session.Height;
                        session.SetSize(w0 + 80, h0 + 60);
                        await Pause(800);
                        Check(session.Width > 0 && session.Height > 0, $"resized from the host: {w0} x {h0} to {session.Width} x {session.Height}");
                        { int again = Colours(session); if (again < 0 && Sandboxed) Say("ok and the sandbox's window is still there"); else Check(again > 300, "and the view still draws"); }
                    }
                    else Say($"the editor has a fixed size ({session.Width} x {session.Height})");
                }
                controller.WindowOf(machine)?.Close();
                await Pause(500);
                Check(controller.ModeOf(machine) == PluginEditorMode.None, $"{r}: closing the window closes the editor");
            }
            await LoadSong(host, machine, controller, want);
            host.Core.SongCore.DeleteMachines(host.Core.SongCore.MachinesList.Where(m => m.Name == "GuiCheck").Cast<IMachine>().ToArray());
            await Pause(300);
        }

        // The studio's UI thread is the plug-in host's main thread: loading a song that holds a Neo Plug must not wait on a thread that
        // needs the UI thread. The song is saved and loaded back on the UI thread, with a watchdog on another one.
        static async Task LoadSong(StudioHost host, IMachine machine, PluginEditorController controller, PluginEditorMode want)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gui-check-" + Environment.ProcessId + ".bmxml");
            var finished = new System.Threading.ManualResetEventSlim();
            var watchdog = new System.Threading.Thread(() =>
            {
                if (finished.Wait(90000)) return;
                Say("FAIL loading the song with a Neo Plug did not finish in 90 s (the UI thread waits for itself)");
                Environment.Exit(1);
            }) { IsBackground = true };
            watchdog.Start();
            try
            {
                host.Core.SaveSongFile(path);
                Say("song saved");
                bool opened = host.Core.TryOpenSongFile(path);
                Check(opened, "the song with a Neo Plug loads back on the UI thread");
                await Pause(1500);
                var again = host.Core.SongCore.MachinesList.LastOrDefault(m => m.Name == "GuiCheck");
                Check(again != null, "and the machine is there");
                if (again != null)
                {
                    var mode = controller.Open(again);
                    Check(mode == want, $"the loaded machine's editor opens too (got {mode})");
                    await Pause(1500);
                    controller.WindowOf(again)?.Close();
                    await Pause(300);
                }
            }
            catch (Exception e)
            {
                Check(false, "loading the song threw " + e.Message);
            }
            finally
            {
                finished.Set();
                try { System.IO.File.Delete(path); } catch (Exception) { }
            }
        }
    }
}
