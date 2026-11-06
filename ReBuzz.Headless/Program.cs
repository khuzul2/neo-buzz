// Fork (docs/specs/mac-engine.md M1): rebuzz-headless. Starts the engine as ReBuzz.exe's
// `--agent` does (ReBuzz/Agent/HeadlessAgent.cs), with the seams for hosts without Windows or a UI
// toolkit (ReBuzz/Platform): the JSON registry, the loop dispatcher on the main thread, messages
// on stderr. Then serves MCP on stdio until stdin closes. stdout carries the protocol only.
//
// With --audio (M5) it also plays through PortAudio, and the agent works in "live" mode: the live
// tools (transport, midi_play, meters_read…) play for whoever listens to that device.
//
// On macOS (M6) MIDI goes through CoreMIDI: --midi-in and --midi-out open devices, --clock-out
// sends MIDI clock, and other apps reach it through its virtual "NeoBuzz Live" ports. --link joins
// Ableton Link through the ReBuzzLink helper beside the app.
//
//   rebuzz-headless --agent [song.bmxml] [--settings <folder>]
//                   [--audio [--device <name>] [--input <name|default>] [--buffer <frames>] [--rate <hz>]]
//                   [--midi-in <name|all|none>]… [--midi-out <name|all|none>]… [--clock-out <name|none>]… [--link|--no-link]
//   rebuzz-headless --list-devices

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BuzzGUI.Common;
using ReBuzz.Agent;
using ReBuzz.Agent.Mcp;
using ReBuzz.Host;
using ReBuzz.Platform;

namespace ReBuzz.Headless
{
    internal static class Program
    {
        static readonly string Usage =
            $"rebuzz-headless: {Product.Name}'s engine with its MCP server on stdio (docs/specs/mcp-server.md).\n" +
            "  rebuzz-headless --agent [song.bmxml] [--settings <folder>]\n" +
            "      [--audio [--device <name>] [--input <name|default>] [--buffer <frames>] [--rate <hz>]]\n" +
            "      [--midi-in <name|all|none>]... [--midi-out <name|all|none>]... [--clock-out <name|none>]... [--link|--no-link]\n" +
            "  rebuzz-headless --list-devices\n" +
            "--audio plays through PortAudio (the default output, or --device), so the live tools work.\n" +
            "--midi-in plays MIDI inputs into the machine last added or armed tracks; --midi-out opens outputs for\n" +
            "MIDI out machines; --clock-out sends MIDI clock; --link joins Ableton Link (the ReBuzzLink helper\n" +
            $"beside this program). They are kept in the settings, as {Product.Name}'s MIDI and sync settings are; none\n" +
            $"and --no-link turn them off. On macOS other apps also reach it through its virtual \"{Product.Name}\" ports.\n" +
            "Point an MCP client at it, e.g. " + ClaudeCode.StdioCommand("rebuzz-headless");

        /// <summary>The options that take a value (the song is the argument that is neither).</summary>
        static readonly string[] Valued = { "--settings", "--device", "--input", "--buffer", "--rate", "--midi-in", "--midi-out", "--clock-out" };

        static void Log(string message) => Console.Error.WriteLine(Product.LogPrefix + message);

        static int Main(string[] args)
        {
            EngineHost.PrepareProcess(Log);

            if (Flag(args, "--list-devices")) return EngineHost.ListDevices(Console.Out, Console.Error);
            if (!Flag(args, "--agent"))
            {
                Console.Error.WriteLine(Usage);
                return args.Any(a => a is "-h" or "--help") ? 0 : 2;
            }
            // Absolute: render files are reported as file URIs, which a relative path can't make.
            string settings = Path.GetFullPath(Option(args, "--settings") ?? JsonRegistry.SettingsFolder);
            // The song: the first argument that is neither an option nor an option's value.
            string song = null;
            for (int i = 0; i < args.Length && song == null; i++)
            {
                if (Valued.Any(v => string.Equals(args[i], v, StringComparison.OrdinalIgnoreCase))) i++;
                else if (!args[i].StartsWith("-")) song = args[i];
            }
            var audio = Flag(args, "--audio") ? new AudioOptions(Option(args, "--device"), Option(args, "--input"), Number(args, "--buffer"), Number(args, "--rate")) : null;
            var sync = new SyncOptions(Options(args, "--midi-in"), Options(args, "--midi-out"), Options(args, "--clock-out"),
                                       Flag(args, "--link") ? true : Flag(args, "--no-link") ? false : null);

            // Before anything touches Global: its settings load from the JSON registry, not the Windows one.
            var registry = EngineHost.UseSettings(settings);
            if (audio != null) EngineHost.UseAudio(registry, audio);

            var stdout = Console.OpenStandardOutput();
            var stdin = Console.OpenStandardInput();
            Console.SetOut(Console.Error);                  // stdout carries the protocol only
            NonModal.CaptureAlways(m => Log(m.Replace('\n', ' ')));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            void Step(string what) => Log($"{clock.Elapsed.TotalSeconds:F1} s: {what}");

            using var dispatcher = new LoopDispatcher();
            int exit = 0;
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    Start(dispatcher, registry, settings, song, audio, sync, stdin, stdout, Step);
                }
                catch (Exception e)
                {
                    Log($"start-up failed: {e}");
                    exit = 1;
                    dispatcher.Stop();
                }
            });
            dispatcher.Run();
            return exit;
        }

        static bool Flag(string[] args, string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        static string Option(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static int? Number(string[] args, string name) => int.TryParse(Option(args, name), out int n) ? n : null;

        /// <summary>Every value of an option that may repeat.</summary>
        static string[] Options(string[] args, string name)
        {
            var values = new List<string>();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) values.Add(args[++i]);
            return values.ToArray();
        }

        static void Start(LoopDispatcher dispatcher, JsonRegistry registry, string settings, string song, AudioOptions audio, SyncOptions sync, Stream stdin, Stream stdout, Action<string> step)
        {
            step($"settings in {settings}");
            var run = EngineHost.Start(new HostServices { Dispatcher = dispatcher }, registry, openAudio: audio != null, step, Log);
            var core = run.Core;
            string mode = "headless";
            if (audio != null)
            {
                if (run.AudioOpen) mode = "live";
                else Log("serving headless: renders work, live tools don't.");
            }
            EngineHost.StartMidiAndSync(core, registry, sync, step, Log);

            if (song != null)
            {
                if (File.Exists(song)) core.OpenSongForAgent(Path.GetFullPath(song));
                else Log($"No song at {song}; starting with an empty song.");
            }

            string logs = Path.Combine(settings, "agent-logs");
            var host = new AgentHost(core, dispatcher, mode, logs);
            Log($"Headless agent server ready (MCP on stdio). Log: {host.Log.FilePath}");

            Task.Run(async () =>
            {
                try
                {
                    await host.RunStdio(stdin, stdout);
                }
                catch (Exception e)
                {
                    Log("stdio ended: " + e.Message);
                }
                dispatcher.BeginInvoke(() =>
                {
                    run.Shutdown();
                    host.Dispose();
                    dispatcher.Stop();
                });
            });
        }
    }
}
