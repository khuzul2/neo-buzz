// Fork (docs/specs/mac-engine.md M1): rebuzz-headless. Starts the engine as ReBuzz.exe's
// `--agent` does (ReBuzz/Agent/HeadlessAgent.cs), with the seams for hosts without Windows or a UI
// toolkit (ReBuzz/Platform): the JSON registry, the loop dispatcher on the main thread, messages
// on stderr. Then serves MCP on stdio until stdin closes. stdout carries the protocol only.
//
// With --audio (M5) it also plays through PortAudio, and the agent works in "live" mode: the live
// tools (transport, midi_play, meters_read…) play for whoever listens to that device.
//
//   rebuzz-headless --agent [song.bmxml] [--settings <folder>]
//                   [--audio [--device <name>] [--input <name|default>] [--buffer <frames>] [--rate <hz>]]
//   rebuzz-headless --list-devices

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.AppViews;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.FileOps;
using ReBuzz.MachineManagement;
using ReBuzz.Platform;

namespace ReBuzz.Headless
{
    internal static class Program
    {
        const string Usage =
            "rebuzz-headless: ReBuzz's engine with its MCP server on stdio (docs/specs/mcp-server.md).\n" +
            "  rebuzz-headless --agent [song.bmxml] [--settings <folder>]\n" +
            "      [--audio [--device <name>] [--input <name|default>] [--buffer <frames>] [--rate <hz>]]\n" +
            "  rebuzz-headless --list-devices\n" +
            "--audio plays through PortAudio (the default output, or --device), so the live tools work.\n" +
            "Point an MCP client at it, e.g. claude mcp add rebuzz -- rebuzz-headless --agent";

        /// <summary>The options that take a value (the song is the argument that is neither).</summary>
        static readonly string[] Valued = { "--settings", "--device", "--input", "--buffer", "--rate" };

        sealed class NoFileChoice : IFileNameChoice
        {
            public ChosenValue<string> SelectFileName() => ChosenValue<string>.Nothing;
        }

        sealed class NoKeyboard : IKeyboard
        {
            public bool HasModifierKeyPressed(Enum modifierKey) => false;
        }

        sealed class Observer : IInitializationObserver
        {
            public void NotifyMachineManagerCreated(MachineManager machineManager) { }
        }

        static int Main(string[] args)
        {
            // Native libraries (libsndfile for wave import, PortAudio) from where each OS keeps them.
            NativeLibraries.Register();
            NativeLibraries.Register(typeof(Global).Assembly);  // BuzzGUI.Common: libsndfile

            if (Flag(args, "--list-devices")) return ListDevices();
            if (!Flag(args, "--agent"))
            {
                Console.Error.WriteLine(Usage);
                return args.Any(a => a is "-h" or "--help") ? 0 : 2;
            }
            string settings = Option(args, "--settings") ?? JsonRegistry.SettingsFolder;
            // The song: the first argument that is neither an option nor an option's value.
            string song = null;
            for (int i = 0; i < args.Length && song == null; i++)
            {
                if (Valued.Any(v => string.Equals(args[i], v, StringComparison.OrdinalIgnoreCase))) i++;
                else if (!args[i].StartsWith("-")) song = args[i];
            }
            var audio = Flag(args, "--audio") ? new AudioOptions(Option(args, "--device"), Option(args, "--input"), Number(args, "--buffer"), Number(args, "--rate")) : null;

            // Before anything touches Global: its settings load from the JSON registry, not the Windows one.
            Directory.CreateDirectory(settings);
            var registry = new JsonRegistry(Path.Combine(settings, "registry.json"), @"Software\ReBuzz");
            RegistryEx.Store = registry;
            // Everything per-user in the settings folder: the machine DLL cache and autosaves too.
            MachineDLLScanner.CacheFolder = settings;
            SongAutosave.Folder = Path.Combine(settings, "Autosave");
            if (audio != null)
            {
                // The engine reads these when it opens the device (PortAudioDriver).
                ReBuzzCore.DefaultAudioDriverFactory = new PortAudioDriverFactory();
                registry.Write("AudioDriver", audio.Device ?? PortAudioDriverFactory.DefaultName, "Settings");
                registry.Write("Input", audio.Input ?? "", PortAudioDriver.Section);
                if (audio.Buffer is int buffer) registry.Write("BufferSize", buffer, PortAudioDriver.Section);
                registry.Write("SampleRate", audio.Rate ?? 0, PortAudioDriver.Section);
            }

            var stdout = Console.OpenStandardOutput();
            var stdin = Console.OpenStandardInput();
            Console.SetOut(Console.Error);                  // stdout carries the protocol only
            NonModal.CaptureAlways(m => Console.Error.WriteLine("[ReBuzz] " + m.Replace('\n', ' ')));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            void Step(string what) => Console.Error.WriteLine($"[ReBuzz] {clock.Elapsed.TotalSeconds:F1} s: {what}");

            using var dispatcher = new LoopDispatcher();
            int exit = 0;
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    Start(dispatcher, registry, settings, song, audio, stdin, stdout, Step);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[ReBuzz] start-up failed: {e}");
                    exit = 1;
                    dispatcher.Stop();
                }
            });
            dispatcher.Run();
            return exit;
        }

        sealed record AudioOptions(string Device, string Input, int? Buffer, int? Rate);

        static bool Flag(string[] args, string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        static string Option(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static int? Number(string[] args, string name) => int.TryParse(Option(args, name), out int n) ? n : null;

        /// <summary>--list-devices: PortAudio's devices, as --device and --input name them.</summary>
        static int ListDevices()
        {
            if (!PortAudio.Available)
            {
                Console.Error.WriteLine("PortAudio isn't installed (macOS: brew install portaudio; Linux: the libportaudio2 package).");
                return 1;
            }
            PortAudio.Acquire();
            try
            {
                Console.WriteLine($"{PortAudio.VersionText}");
                int defaultOut = PortAudio.DefaultOutput, defaultIn = PortAudio.DefaultInput;
                foreach (var d in PortAudio.Devices())
                {
                    string marks = (d.Index == defaultOut ? " [default output]" : "") + (d.Index == defaultIn ? " [default input]" : "");
                    Console.WriteLine($"{PortAudioDriverFactory.Label(d)}: {d.Outputs} out, {d.Inputs} in, {d.DefaultSampleRate:0} Hz{marks}");
                }
            }
            finally
            {
                PortAudio.Release();
            }
            return 0;
        }

        /// <summary>
        /// What BuzzGUIStartup.PreInit does for ReBuzz.exe, minus its settings window: unobserved
        /// task exceptions are logged, and machines' dependencies resolve from the gear folders
        /// (with this OS's path separators).
        /// </summary>
        static void PreInit(string buzzPath)
        {
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Console.Error.WriteLine("[ReBuzz] unobserved task exception: " + e.Exception.Message);
                e.SetObserved();
            };
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                if (e.Name.Contains(".XmlSerializers")) return null;
                string name = new System.Reflection.AssemblyName(e.Name).Name;
                foreach (var folder in new[] { Path.Combine("Gear", "Generators"), Path.Combine("Gear", "Effects") })
                {
                    string path = Path.Combine(buzzPath, folder, name + ".dll");
                    if (File.Exists(path)) return System.Reflection.Assembly.LoadFile(path);
                }
                return null;
            };
        }

        static void Start(LoopDispatcher dispatcher, JsonRegistry registry, string settings, string song, AudioOptions audio, Stream stdin, Stream stdout, Action<string> step)
        {
            step($"starting headless on {Environment.OSVersion.Platform} ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}); settings in {settings}");
            var engineSettings = Global.EngineSettings;
            var keyboard = new NoKeyboard();
            string buzzPath = Global.BuzzPath;

            PreInit(buzzPath);
            var core = new ReBuzzCore(
                Global.GeneralSettings,
                engineSettings,
                buzzPath,
                Global.RegistryRoot,
                new MachineDLLScanner(dispatcher, engineSettings),
                dispatcher,
                registry,
                new NoFileChoice(),
                new NoFileChoice(),
                new ConsoleUserMessages(),
                keyboard);
            var init = new ReBuzzCoreInitialization(core, buzzPath, dispatcher, registry, keyboard, engineSettings);
            step("engine step 1");
            init.StartReBuzzEngineStep1((s, e) => { });
            step("engine step 2");
            init.StartReBuzzEngineStep2(IntPtr.Zero);       // no window: native machines have no parent for their GUIs
            step("engine step 3 (gear scan)");
            init.StartReBuzzEngineStep3(engineSettings, new Observer());
            var machineDb = new MachineDatabase(core, buzzPath, dispatcher, engineSettings);
            step("engine step 4 (machine database)");
            init.StartReBuzzEngineStep4(machineDb, s => { }, () => { }, () => { }, s => { }, c => { }, b => { }, t => { });
            step("engine step 5");
            init.StartReBuzzEngineStep5(f => { });
            step(audio != null ? "engine step 6 (audio device)" : "engine step 6");
            init.StartReBuzzEngineStep6(openAudioDevice: audio != null);
            core.SongCore.SongEnd = 16;
            core.SongCore.LoopEnd = 16;
            core.Modified = false;
            string mode = "headless";
            if (audio != null)
            {
                if (core.AudioEngine.SelectedOutDevice != null)
                {
                    mode = "live";
                    // Shorter GC pauses while the device's thread renders (mac-engine.md §5).
                    System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
                    step($"playing through {core.AudioEngine.SelectedOutDevice.Name} at {core.SelectedAudioDriverSampleRate} Hz");
                }
                else
                {
                    Console.Error.WriteLine("[ReBuzz] The audio output didn't open (see above); serving headless: renders work, live tools don't.");
                }
            }

            if (song != null)
            {
                if (File.Exists(song)) core.OpenSongForAgent(Path.GetFullPath(song));
                else Console.Error.WriteLine($"[ReBuzz] No song at {song}; starting with an empty song.");
            }

            string logs = Path.Combine(settings, "agent-logs");
            var host = new AgentHost(core, dispatcher, mode, logs);
            Console.Error.WriteLine($"[ReBuzz] Headless agent server ready (MCP on stdio). Log: {host.Log.FilePath}");

            Task.Run(async () =>
            {
                try
                {
                    await host.RunStdio(stdin, stdout);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("[ReBuzz] stdio ended: " + e.Message);
                }
                dispatcher.BeginInvoke(() =>
                {
                    try { init.ShutDownReBuzzEngine(); } catch (Exception) { }
                    host.Dispose();
                    dispatcher.Stop();
                });
            });
        }
    }
}
