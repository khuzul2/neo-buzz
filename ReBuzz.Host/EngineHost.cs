// Fork (docs/specs/studio-mvp.md §2): the engine's start-up off WPF, shared by rebuzz-headless
// (mac-engine.md M1) and the Avalonia studio. It does what ReBuzz.exe's start-up does, with the
// seams for hosts without Windows (ReBuzz/Platform): settings in a JSON file, native libraries
// from where each OS keeps them, audio through PortAudio, MIDI through CoreMIDI on macOS, Link
// through its helper. The host gives the UI services (dispatcher, messages, file choice,
// keyboard); a headless host gives console ones.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.AppViews;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.FileOps;
using ReBuzz.MachineManagement;
using ReBuzz.Midi;
using ReBuzz.Platform;

namespace ReBuzz.Host
{
    /// <summary>Audio through PortAudio: the device, the input, the buffer and the rate (null: the settings').</summary>
    internal sealed record AudioOptions(string Device, string Input, int? Buffer, int? Rate);

    /// <summary>MIDI devices to open, clock outputs, and Link on or off (null: as the settings say).</summary>
    internal sealed record SyncOptions(string[] MidiIn, string[] MidiOut, string[] ClockOut, bool? Link)
    {
        public static SyncOptions None => new SyncOptions(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null);
    }

    /// <summary>What the engine calls back into: the host's UI, or the console's.</summary>
    internal sealed class HostServices
    {
        public IUiDispatcher Dispatcher { get; init; }
        public IUserMessages Messages { get; init; } = new ConsoleUserMessages();
        public IFileNameChoice OpenFile { get; init; } = new NoFileChoice();
        public IFileNameChoice SaveFile { get; init; } = new NoFileChoice();
        public IKeyboard Keyboard { get; init; } = new NoKeyboard();

        /// <summary>
        /// Windows' native machine hosts (bin32, bin64) take a window of the app as their parent (step 2); the studio gives its
        /// main window's handle once the window exists. Null or zero: none, as in a headless host.
        /// </summary>
        public Func<IntPtr> ParentWindow { get; set; }
    }

    internal sealed class NoFileChoice : IFileNameChoice
    {
        public ChosenValue<string> SelectFileName() => ChosenValue<string>.Nothing;
    }

    internal sealed class NoKeyboard : IKeyboard
    {
        public bool HasModifierKeyPressed(Enum modifierKey) => false;
    }

    /// <summary>A started engine.</summary>
    internal sealed class EngineRun
    {
        public ReBuzzCore Core { get; init; }
        public ReBuzzCoreInitialization Init { get; init; }

        /// <summary>Whether an audio device plays.</summary>
        public bool AudioOpen => Core.AudioEngine?.SelectedOutDevice != null;

        public void Shutdown()
        {
            try { Init.ShutDownReBuzzEngine(); } catch (Exception) { }
        }
    }

    internal static class EngineHost
    {
        sealed class Observer : IInitializationObserver
        {
            public void NotifyMachineManagerCreated(MachineManager machineManager) { }
        }

        /// <summary>The engine's warnings and errors (DCWriteLine), to a writer.</summary>
        sealed class LogSink : Serilog.Core.ILogEventSink
        {
            readonly Action<string> write;
            public LogSink(Action<string> write) => this.write = write;
            public void Emit(Serilog.Events.LogEvent e) => write($"{e.Level.ToString().ToLowerInvariant()}: {e.RenderMessage()}");
        }

        /// <summary>
        /// Once per process, first: native libraries from where each OS keeps them (libsndfile for
        /// wave import, PortAudio), the platform's MIDI devices (CoreMIDI on macOS; none elsewhere
        /// off Windows), and the engine's log to <paramref name="log"/>.
        /// </summary>
        public static void PrepareProcess(Action<string> log)
        {
            NativeLibraries.Register();
            NativeLibraries.Register(typeof(Global).Assembly);  // BuzzGUI.Common: libsndfile
            if (CoreMidi.Available)
                ReBuzzCore.DefaultMidiBackend = core => new CoreMidiBackend(core.SendMIDIInput, log);
            else if (WinMmMidiBackend.Available)
                ReBuzzCore.DefaultMidiBackend = core => new WinMmMidiBackend(core.SendMIDIInput, log);     // Windows: WinMM (ux-overhaul.md P6)
            Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().WriteTo.Sink(new LogSink(log)).CreateLogger();
        }

        /// <summary>
        /// The settings folder: the JSON registry, the machine DLL cache and autosaves. Before
        /// anything touches Global, whose settings then load from it.
        /// </summary>
        public static JsonRegistry UseSettings(string folder)
        {
            folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);
            var registry = new JsonRegistry(Path.Combine(folder, "registry.json"), @"Software\ReBuzz");
            RegistryEx.Store = registry;
            MachineDLLScanner.CacheFolder = folder;
            SongAutosave.Folder = Path.Combine(folder, "Autosave");
            return registry;
        }

        /// <summary>
        /// Audio through PortAudio. With <paramref name="audio"/>, its choices go to the settings,
        /// which the driver reads when it opens; without, the settings' own stand.
        /// </summary>
        public static void UseAudio(JsonRegistry registry, AudioOptions audio)
        {
            // Windows: ASIO drivers (the WPF app's driver) beside PortAudio's WASAPI, WDM-KS, DirectSound and MME devices.
            ReBuzzCore.DefaultAudioDriverFactory = OperatingSystem.IsWindows() ? WindowsHostAudioDriverFactory.ForSystem(registry) : new PortAudioDriverFactory();
            if (audio == null) return;
            registry.Write("AudioDriver", audio.Device ?? PortAudioDriverFactory.DefaultName, "Settings");
            registry.Write("Input", audio.Input ?? "", PortAudioDriver.Section);
            if (audio.Buffer is int buffer) registry.Write("BufferSize", buffer, PortAudioDriver.Section);
            registry.Write("SampleRate", audio.Rate ?? 0, PortAudioDriver.Section);
        }

        /// <summary>
        /// What BuzzGUIStartup.PreInit does for ReBuzz.exe, minus its settings window: unobserved
        /// task exceptions are logged, and machines' dependencies resolve from the gear folders
        /// (with this OS's path separators).
        /// </summary>
        public static void PreInit(string buzzPath, Action<string> log)
        {
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                log("unobserved task exception: " + e.Exception.Message);
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

        /// <summary>
        /// Engine steps 1–6 on the dispatcher's thread, with <paramref name="services"/>. With
        /// <paramref name="openAudio"/> the audio device the settings name opens.
        /// </summary>
        public static EngineRun Start(HostServices services, JsonRegistry registry, bool openAudio, Action<string> step, Action<string> log)
        {
            step($"starting on {Environment.OSVersion.Platform} ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})");
            var engineSettings = Global.EngineSettings;
            string buzzPath = Global.BuzzPath;

            PreInit(buzzPath, log);
            var core = new ReBuzzCore(
                Global.GeneralSettings,
                engineSettings,
                buzzPath,
                Global.RegistryRoot,
                new MachineDLLScanner(services.Dispatcher, engineSettings),
                services.Dispatcher,
                registry,
                services.OpenFile,
                services.SaveFile,
                services.Messages,
                services.Keyboard);
            var init = new ReBuzzCoreInitialization(core, buzzPath, services.Dispatcher, registry, services.Keyboard, engineSettings);
            step("engine step 1");
            init.StartReBuzzEngineStep1((s, e) => { });
            step("engine step 2");
            init.StartReBuzzEngineStep2(services.ParentWindow?.Invoke() ?? IntPtr.Zero);       // no HWND off Windows: native machines have no parent for their GUIs
            step("engine step 3 (gear scan)");
            init.StartReBuzzEngineStep3(engineSettings, new Observer());
            var machineDb = new MachineDatabase(core, buzzPath, services.Dispatcher, engineSettings);
            step("engine step 4 (machine database)");
            init.StartReBuzzEngineStep4(machineDb, s => { }, () => { }, () => { }, s => { }, c => { }, b => { }, t => { });
            step("engine step 5");
            init.StartReBuzzEngineStep5(f => { });
            step(openAudio ? "engine step 6 (audio device)" : "engine step 6");
            init.StartReBuzzEngineStep6(openAudioDevice: openAudio);
            core.SongCore.SongEnd = 16;
            core.SongCore.LoopEnd = 16;
            core.Modified = false;
            var run = new EngineRun { Core = core, Init = init };
            if (openAudio)
            {
                if (run.AudioOpen)
                {
                    // Shorter GC pauses while the device's thread renders (mac-engine.md §5).
                    System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
                    step($"playing through {core.AudioEngine.SelectedOutDevice.Name} at {core.SelectedAudioDriverSampleRate} Hz");
                }
                else
                {
                    log("the audio output didn't open (see above)");
                }
            }
            return run;
        }

        /// <summary>
        /// --midi-in, --midi-out, --clock-out and --link: what ReBuzz.exe's MIDI and sync settings
        /// turn on, kept in the settings as those are. The MIDI devices are listed by name, so they
        /// open again when they are plugged in.
        /// </summary>
        public static void StartMidiAndSync(ReBuzzCore core, IRegistryEx registry, SyncOptions sync, Action<string> step, Action<string> log)
        {
            var midi = core.MidiInOutEngine;
            var devices = midi.Backend;
            static bool IsNone(string name) => string.Equals(name, "none", StringComparison.OrdinalIgnoreCase);
            static Dictionary<string, int> Turn(IEnumerable<string> names, int on) => names.Distinct().ToDictionary(n => n, n => on);
            IEnumerable<string> Expand(IEnumerable<string> names, int count, Func<int, string> name) =>
                names.Where(n => !IsNone(n)).SelectMany(n => string.Equals(n, "all", StringComparison.OrdinalIgnoreCase) ? Enumerable.Range(0, count).Select(name) : new[] { CoreMidiBackend.Current(n) });

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
                    session.SetClockOut(CoreMidiBackend.Current(name), true);
                if (sync.Link is bool link)
                {
                    session.SetLink(link);
                    if (session.Problem != null) log("Link: " + session.Problem);
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

        /// <summary>PortAudio's devices, as --device and --input name them, and the MIDI devices (macOS). 0 when PortAudio loads.</summary>
        public static int ListDevices(TextWriter output, TextWriter errors)
        {
            int exit = 0;
            if (!PortAudio.Available)
            {
                errors.WriteLine("PortAudio isn't installed (macOS: brew install portaudio; Linux: the libportaudio2 package; Windows: portaudio.dll beside the app).");
                exit = 1;
            }
            else
            {
                PortAudio.Acquire();
                try
                {
                    output.WriteLine($"{PortAudio.VersionText}");
                    int defaultOut = PortAudio.DefaultOutput, defaultIn = PortAudio.DefaultInput;
                    foreach (var d in PortAudio.Devices())
                    {
                        string marks = (d.Index == defaultOut ? " [default output]" : "") + (d.Index == defaultIn ? " [default input]" : "");
                        output.WriteLine($"{PortAudioDriverFactory.Label(d)}: {d.Outputs} out, {d.Inputs} in, {d.DefaultSampleRate:0} Hz{marks}");
                    }
                }
                finally
                {
                    PortAudio.Release();
                }
            }
            if (OperatingSystem.IsWindows())
            {
                var asio = new WindowsHostAudioDriverFactory(null);
                output.WriteLine("ASIO drivers (installed): " + (asio.AsioSupported ? string.Join(", ", asio.AsioDriverNames().DefaultIfEmpty("none")) : "not supported here"));
            }
            if (WinMmMidiBackend.Available)
            {
                var midi = new WinMmMidiBackend(_ => { }, m => errors.WriteLine(m));
                output.WriteLine("MIDI inputs (--midi-in):");
                for (int i = 0; i < midi.InputCount; i++) output.WriteLine("  " + midi.InputName(i));
                output.WriteLine("MIDI outputs (--midi-out, --clock-out):");
                for (int i = 0; i < midi.OutputCount; i++) output.WriteLine("  " + midi.OutputName(i));
            }
            if (CoreMidi.Available)
            {
                var midi = new CoreMidiBackend(_ => { }, m => errors.WriteLine(m));
                output.WriteLine("MIDI inputs (--midi-in):");
                for (int i = 0; i < midi.InputCount; i++) output.WriteLine("  " + midi.InputName(i));
                output.WriteLine("MIDI outputs (--midi-out, --clock-out):");
                for (int i = 0; i < midi.OutputCount; i++) output.WriteLine("  " + midi.OutputName(i));
                output.WriteLine($"  {CoreMidiBackend.VirtualName} (virtual: other apps receive from it while {Product.Name} runs; they send to its \"{CoreMidiBackend.VirtualName}\" destination)");
            }
            return exit;
        }
    }
}
