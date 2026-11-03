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
            ReBuzzCore.DefaultUiHost = new StudioMachineUiHost();     // machine windows (studio-mvp.md §3.4)
            Current = new StudioHost(settingsFolder ?? JsonRegistry.SettingsFolder, openAudio);
            return Current;
        }

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
            Console.Error.WriteLine("[ReBuzz] " + message);
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
            Started?.Invoke();
        }

        public void Shutdown()
        {
            Agent?.Dispose();
            Run?.Shutdown();
        }
    }
}
