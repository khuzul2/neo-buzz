// Fork (docs/specs/studio-mvp.md §2): the studio's engine: its settings, its services on the UI
// thread (ReBuzz.Host), and the started engine. One per process, as the engine's globals are.

using System;
using System.Collections.Generic;
using System.IO;
using ReBuzz.Core;
using ReBuzz.Host;
using ReBuzz.Platform;
using ReBuzz.Studio.Platform;

namespace ReBuzz.Studio.Shell
{
    internal sealed class StudioHost
    {
        public static StudioHost Current { get; private set; }

        readonly List<string> log = new List<string>();

        StudioHost(string settingsFolder, bool openAudio)
        {
            SettingsFolder = Path.GetFullPath(settingsFolder);
            OpenAudio = openAudio;
            Registry = EngineHost.UseSettings(SettingsFolder);
            EngineHost.UseAudio(Registry, null);    // the settings' device (Preferences picks it)
            OpenChoice = new StudioFileChoice(save: false);
            SaveChoice = new StudioFileChoice(save: true);
            Services = new HostServices
            {
                Dispatcher = new AvaloniaUiDispatcher(),
                Messages = new StudioMessages(Log),
                OpenFile = OpenChoice,
                SaveFile = SaveChoice,
                Keyboard = new StudioKeyboard(),
            };
        }

        /// <summary>Once, before Avalonia starts: the process's native libraries, MIDI, settings.</summary>
        public static StudioHost Create(string settingsFolder, bool openAudio)
        {
            if (Current != null) return Current;
            EngineHost.PrepareProcess(m => Current?.Log(m));
            Tools.DebugLog.Install();     // the debug console's memory of the log (ux-overhaul.md P6b)
            ReBuzzCore.DefaultUiHost = new StudioMachineUiHost();     // machine windows (studio-mvp.md §3.4)
            Current = new StudioHost(settingsFolder ?? DefaultSettingsFolder, openAudio);
            return Current;
        }

        /// <summary>
        /// Where the settings live unless --settings says otherwise: the host's folder for this OS, but on Windows %APPDATA%\NeoBuzz Live,
        /// not %APPDATA%\ReBuzz, which the WPF app keeps its own machine cache and settings in (the preview doesn't share them).
        /// </summary>
        public static string DefaultSettingsFolder =>
            OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Product.Name)
                : JsonRegistry.SettingsFolder;

        /// <summary>The window title's app name: on Windows the studio is a preview beside the WPF app (ux-overhaul.md P6).</summary>
        public static string AppTitle => OperatingSystem.IsWindows() ? Product.Name + " Studio (preview)" : Product.Name;

        public string SettingsFolder { get; }
        public bool OpenAudio { get; }
        public JsonRegistry Registry { get; }
        public HostServices Services { get; }
        public StudioFileChoice OpenChoice { get; }
        public StudioFileChoice SaveChoice { get; }
        public EngineRun Run { get; private set; }
        public ReBuzzCore Core => Run?.Core;

        /// <summary>The MCP server in studio mode (S8): it listens when Preferences ▸ Agent allows agents.</summary>
        public AgentServer Agent { get; private set; }

        /// <summary>The engine started (on the UI thread).</summary>
        public event Action Started;

        /// <summary>Something to tell the user or the log (on any thread).</summary>
        public event Action<string> Logged;

        public IReadOnlyList<string> LogLines
        {
            get { lock (log) return log.ToArray(); }
        }

        public void Log(string message)
        {
            lock (log)
            {
                log.Add(message);
                if (log.Count > 1000) log.RemoveAt(0);
            }
            Console.Error.WriteLine(Product.LogPrefix + message);
            Logged?.Invoke(message);
        }

        /// <summary>Engine steps 1–6, MIDI and sync as the settings say, and the agent server (UI thread).</summary>
        public void Start(Action<string> step)
        {
            if (Run != null) return;
            Run = EngineHost.Start(Services, Registry, OpenAudio, step, Log);
            EngineHost.StartMidiAndSync(Run.Core, Registry, SyncOptions.None, step, Log);
            try
            {
                Agent = new AgentServer(this);
            }
            catch (Exception e)
            {
                Log("The agent server couldn't start: " + e.Message);
            }
            StartWasapiFollower();
            Started?.Invoke();
        }

        WasapiFollower wasapiFollower;

        /// <summary>
        /// Windows: keeps WASAPI on the right output as endpoints come and go (an unplugged one falls back to the system default, and
        /// returns when plugged in; "System default" follows Windows' default). A change opens the device with the way back AudioSwitch gives,
        /// and the chosen device stays what the settings hold, so the choice survives the unplugging.
        /// </summary>
        void StartWasapiFollower()
        {
            if (!OperatingSystem.IsWindows() || Run == null || ReBuzzCore.DefaultAudioDriverFactory is not WindowsHostAudioDriverFactory factory || factory.Endpoints == null) return;
            var core = Run.Core;
            string Saved() => Registry.Read("AudioDriver", "", "Settings");
            wasapiFollower = new WasapiFollower(factory.Endpoints, factory, Saved, () => core.AudioEngine?.SelectedOutDevice?.Name,
                (device, reason) => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    string saved = Saved();
                    var audio = new ReBuzz.Agent.AudioBackend(core);
                    var previous = audio.Current();
                    var done = await ReBuzz.Agent.App.AudioSwitch.Apply(audio, (work, ct) => System.Threading.Tasks.Task.FromResult(work()), previous,
                        new ReBuzz.Agent.App.AudioConfig(device, previous.BufferFrames, previous.SampleRate), default);
                    Registry.Write("AudioDriver", saved, "Settings");      // the choice stays, whatever plays for now
                    WasapiChoice.Notice = done.Applied ? reason : $"Not applied: {done.Reason} {previous.Device} is back." + done.RevertNote;
                    wasapiFollower?.Opened();
                    Log(WasapiChoice.Notice ?? device);
                }));
        }

        public void Shutdown()
        {
            wasapiFollower?.Dispose();
            Agent?.Dispose();
            Run?.Shutdown();
        }
    }
}
