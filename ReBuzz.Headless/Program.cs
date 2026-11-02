// Fork (docs/specs/mac-engine.md M1): rebuzz-headless. Starts the engine as ReBuzz.exe's
// `--agent` does (ReBuzz/Agent/HeadlessAgent.cs), with the seams for hosts without Windows or a UI
// toolkit (ReBuzz/Platform): the JSON registry, the loop dispatcher on the main thread, messages
// on stderr. Then serves MCP on stdio until stdin closes. stdout carries the protocol only.
//
// With --audio (M5) it also plays through PortAudio, and the agent works in "live" mode: the live
// tools (transport, midi_play, meters_read…) play for whoever listens to that device.
//
// On macOS (M6) MIDI goes through CoreMIDI: --midi-in and --midi-out open devices, --clock-out
// sends MIDI clock, and other apps reach ReBuzz through its virtual "ReBuzz" ports. --link joins
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
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.AppViews;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.FileOps;
using ReBuzz.MachineManagement;
using ReBuzz.Midi;
using ReBuzz.Platform;

namespace ReBuzz.Headless
{
    internal static class Program
    {
        const string Usage =
            "rebuzz-headless: ReBuzz's engine with its MCP server on stdio (docs/specs/mcp-server.md).\n" +
            "  rebuzz-headless --agent [song.bmxml] [--settings <folder>]\n" +
            "      [--audio [--device <name>] [--input <name|default>] [--buffer <frames>] [--rate <hz>]]\n" +
            "      [--midi-in <name|all|none>]... [--midi-out <name|all|none>]... [--clock-out <name|none>]... [--link|--no-link]\n" +
            "  rebuzz-headless --list-devices\n" +
            "--audio plays through PortAudio (the default output, or --device), so the live tools work.\n" +
            "--midi-in plays MIDI inputs into the machine last added or armed tracks; --midi-out opens outputs for\n" +
            "MIDI out machines; --clock-out sends MIDI clock; --link joins Ableton Link (the ReBuzzLink helper\n" +
            "beside this program). They are kept in the settings, as ReBuzz's MIDI and sync settings are; none\n" +
            "and --no-link turn them off. On macOS other apps also reach ReBuzz through its virtual \"ReBuzz\" ports.\n" +
            "Point an MCP client at it, e.g. claude mcp add rebuzz -- rebuzz-headless --agent";

        /// <summary>The options that take a value (the song is the argument that is neither).</summary>
        static readonly string[] Valued = { "--settings", "--device", "--input", "--buffer", "--rate", "--midi-in", "--midi-out", "--clock-out" };

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

        /// <summary>The engine's warnings and errors (DCWriteLine), on stderr.</summary>
        sealed class StderrLog : Serilog.Core.ILogEventSink
        {
            public void Emit(Serilog.Events.LogEvent e) => Console.Error.WriteLine($"[ReBuzz] {e.Level.ToString().ToLowerInvariant()}: {e.RenderMessage()}");
        }

        static int Main(string[] args)
        {
            // Native libraries (libsndfile for wave import, PortAudio) from where each OS keeps them.
            NativeLibraries.Register();
            NativeLibraries.Register(typeof(Global).Assembly);  // BuzzGUI.Common: libsndfile
            // MIDI devices: CoreMIDI on macOS (M6); none elsewhere off Windows.
            if (CoreMidi.Available)
                ReBuzzCore.DefaultMidiBackend = core => new CoreMidiBackend(core.SendMIDIInput, m => Console.Error.WriteLine("[ReBuzz] " + m));

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
            var sync = new SyncOptions(Options(args, "--midi-in"), Options(args, "--midi-out"), Options(args, "--clock-out"),
                                       Flag(args, "--link") ? true : Flag(args, "--no-link") ? false : null);

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
            Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().WriteTo.Sink(new StderrLog()).CreateLogger();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            void Step(string what) => Console.Error.WriteLine($"[ReBuzz] {clock.Elapsed.TotalSeconds:F1} s: {what}");

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
                    Console.Error.WriteLine($"[ReBuzz] start-up failed: {e}");
                    exit = 1;
                    dispatcher.Stop();
                }
            });
            dispatcher.Run();
            return exit;
        }

        sealed record AudioOptions(string Device, string Input, int? Buffer, int? Rate);

        /// <summary>MIDI devices to open, clock outputs, and Link on or off (null: as the settings say).</summary>
        sealed record SyncOptions(string[] MidiIn, string[] MidiOut, string[] ClockOut, bool? Link);

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

        /// <summary>--list-devices: PortAudio's devices, as --device and --input name them, and the MIDI devices (macOS).</summary>
        static int ListDevices()
        {
            int exit = 0;
            if (!PortAudio.Available)
            {
                Console.Error.WriteLine("PortAudio isn't installed (macOS: brew install portaudio; Linux: the libportaudio2 package).");
                exit = 1;
            }
            else
            {
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
            }
            if (CoreMidi.Available)
            {
                var midi = new CoreMidiBackend(_ => { }, m => Console.Error.WriteLine(m));
                Console.WriteLine("MIDI inputs (--midi-in):");
                for (int i = 0; i < midi.InputCount; i++) Console.WriteLine("  " + midi.InputName(i));
                Console.WriteLine("MIDI outputs (--midi-out, --clock-out):");
                for (int i = 0; i < midi.OutputCount; i++) Console.WriteLine("  " + midi.OutputName(i));
                Console.WriteLine($"  {CoreMidiBackend.VirtualName} (virtual: other apps receive from it while ReBuzz runs; they send to its \"{CoreMidiBackend.VirtualName}\" destination)");
            }
            return exit;
        }

        /// <summary>
        /// --midi-in, --midi-out, --clock-out and --link: what ReBuzz.exe's MIDI and sync settings
        /// turn on, kept in the settings as those are. The MIDI devices are listed by name, so they
        /// open again when they are plugged in.
        /// </summary>
        static void StartMidiAndSync(ReBuzzCore core, IRegistryEx registry, SyncOptions sync, Action<string> step)
        {
            var midi = core.MidiInOutEngine;
            var devices = midi.Backend;
            static bool IsNone(string name) => string.Equals(name, "none", StringComparison.OrdinalIgnoreCase);
            static Dictionary<string, int> Turn(IEnumerable<string> names, int on) => names.Distinct().ToDictionary(n => n, n => on);
            IEnumerable<string> Expand(IEnumerable<string> names, int count, Func<int, string> name) =>
                names.Where(n => !IsNone(n)).SelectMany(n => string.Equals(n, "all", StringComparison.OrdinalIgnoreCase) ? Enumerable.Range(0, count).Select(name) : new[] { n });

            // Inputs: "none" turns every input in the settings off; names (or "all") turn them on.
            if (sync.MidiIn.Any(IsNone))
            {
                midi.SetMidiInputDevices(Turn(registry.ReadDictionary("MIDI In List").Keys, 0));
                midi.DisposeMidiIn();
            }
            var inputs = Expand(sync.MidiIn, devices.InputCount, devices.InputName).ToList();
            if (inputs.Count > 0)
            {
                midi.SetMidiInputDevices(Turn(inputs, 1));
                midi.OpenMidiInDevices();
            }

            // Outputs, and the clock's outputs (clock out sends to open outputs).
            if (sync.MidiOut.Any(IsNone))
            {
                midi.SetMidiOutputDevices(Turn(registry.ReadDictionary("MIDI Out List").Keys, 0));
                midi.DisposeMidiOuts();
            }
            var outputs = Expand(sync.MidiOut.Concat(sync.ClockOut), devices.OutputCount, devices.OutputName).ToList();
            if (outputs.Count > 0)
            {
                midi.SetMidiOutputDevices(Turn(outputs, 1));
                midi.OpenMidiOutDevices();
            }

            var session = core.SessionSync;
            if (session != null)
            {
                if (sync.ClockOut.Any(IsNone))
                    foreach (var name in session.ClockOutNames.ToList()) session.SetClockOut(name, false);
                foreach (var name in sync.ClockOut.Where(n => !IsNone(n)))
                    session.SetClockOut(name, true);
                if (sync.Link is bool link)
                {
                    session.SetLink(link);
                    if (session.Problem != null) Console.Error.WriteLine("[ReBuzz] Link: " + session.Problem);
                }
            }

            if (devices is not NoMidiBackend)
            {
                var ins = midi.GetMidiInputDevices().Where(i => i < devices.InputCount).Select(devices.InputName);
                var outs = midi.GetMidiOuts().Select(o => o.Item2);
                static string List(IEnumerable<string> names) => names.Any() ? string.Join(", ", names) : "none";
                step($"MIDI in: {List(ins)}; out: {List(outs)}; clock out: {List(session?.ClockOutNames ?? new List<string>())}");
            }
            if (session?.LinkEnabled == true) step("Link on (the ReBuzzLink helper)");
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

        static void Start(LoopDispatcher dispatcher, JsonRegistry registry, string settings, string song, AudioOptions audio, SyncOptions sync, Stream stdin, Stream stdout, Action<string> step)
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
            StartMidiAndSync(core, registry, sync, step);

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
