// Agent server (docs/specs/agent-knowledge.md §5, K3): the engine side of the app-control tools (ReBuzz.Agent/App).
// The settings are read from the same objects Preferences shows (Global's General, Engine and MIDI settings, the ones a host adds:
// Appearance, Agent) and from the preferences store (audio, safety); MIDI bindings go through the parameters and one undo step each;
// the audio device is the engine's own. The tools themselves are portable and tested with fakes.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.App;
using ReBuzz.Agent.Mcp;
using ReBuzz.Audio;
using ReBuzz.Audio.Engine;
using ReBuzz.Core;
using ReBuzz.Core.Session;

namespace ReBuzz.Agent
{
    internal sealed class AppBackend : IAppServices
    {
        readonly AgentHost host;
        readonly ReBuzzCore core;
        readonly Func<int> toolCount;
        readonly List<(string Section, BuzzGUI.Common.Settings.Settings Settings, string Class)> extra = new();
        readonly MidiBackend midi;
        readonly AudioBackend audio;

        public AppBackend(AgentHost host, Func<int> toolCount)
        {
            this.host = host;
            core = host.Core;
            this.toolCount = toolCount;
            midi = new MidiBackend(core);
            audio = new AudioBackend(core);
            SettingsHistoryFile = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(host.Log.FilePath) ?? System.IO.Path.GetTempPath(), "settings-history.jsonl");
        }

        /// <summary>A host's own settings objects (the studio's Appearance, the agent switch), listed by settings_get as section.Name.</summary>
        public void AddSettings(string section, BuzzGUI.Common.Settings.Settings settings, string settingClass = SettingClass.Normal)
        {
            lock (extra) extra.Add((section, settings, settingClass));
        }

        /// <summary>Studio mode's view control, set by the studio when its window exists.</summary>
        public IViewControl View { get; set; }

        public IAudioControl Audio => host.Live ? audio : null;

        public IMidiBindings Midi => midi;

        public string SettingsHistoryFile { get; }

        public Task<T> Engine<T>(string what, bool changes, Func<T> work, CancellationToken ct) => host.Engine(what, changes, _ => work(), ct);

        public IReadOnlyList<string> RecentSongs() => core.RecentFilesList();

        public AppFacts Facts() => new(AgentHost.Version, host.Mode, RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture, toolCount(),
            string.IsNullOrEmpty(core.SongCore.SongName) ? null : core.SongCore.SongName, core.Modified, core.BPM, core.Playing);

        // ------------------------------------------------------------------ settings

        public IReadOnlyList<SettingEntry> Settings()
        {
            var list = new List<SettingEntry>();
            list.AddRange(Reflect("general", Global.GeneralSettings, SettingClass.Normal));
            list.AddRange(AudioEntries());
            list.AddRange(Reflect("midi", Global.MIDISettings, SettingClass.Normal));
            list.AddRange(SyncEntries());
            list.AddRange(SafetyEntries());
            list.AddRange(Reflect("advanced", Global.EngineSettings, SettingClass.Engine));
            lock (extra)
                foreach (var (section, settings, cls) in extra)
                    list.AddRange(Reflect(section, settings, cls));
            return list;
        }

        static IEnumerable<SettingEntry> Reflect(string section, BuzzGUI.Common.Settings.Settings settings, string settingClass)
        {
            if (settings == null) yield break;
            foreach (var st in settings.List)
            {
                var value = st.ValueObject;
                var kind = st.Presets != null ? SettingKind.Choice : value switch
                {
                    bool => SettingKind.Bool,
                    int => SettingKind.Int,
                    double or float => SettingKind.Number,
                    Enum => SettingKind.Choice,
                    _ => SettingKind.Text,
                };
                double? min = null, max = null;
                if (kind == SettingKind.Int && st.Options is { Length: > 1 } o && double.TryParse(o[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out double lo) &&
                    double.TryParse(o[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out double hi))
                    (min, max) = (lo, hi);
                string name = st.Name;
                bool agentSwitch = section == "agent" && name is "AllowAgents" or "Port";
                if (section == "agent" && name == "Port") (kind, min, max) = (SettingKind.Int, 1, 65535);
                var s = settings;
                yield return new SettingEntry
                {
                    Key = section + "." + name,
                    Section = section,
                    Name = name,
                    Kind = kind,
                    Description = st.Description ?? "",
                    Default = st.Default ?? "",
                    Options = kind == SettingKind.Choice || kind == SettingKind.Int ? st.Options : null,
                    OptionLabels = kind == SettingKind.Choice && value is Enum && st.Options != null
                        ? st.Options.Select(o => value.GetType().GetField(o) is { } f && Attribute.GetCustomAttribute(f, typeof(System.ComponentModel.DescriptionAttribute)) is System.ComponentModel.DescriptionAttribute d ? d.Description : o.Replace('_', ' ')).ToArray()
                        : null,
                    Min = min,
                    Max = max,
                    RestartRequired = (st.Description ?? "").Contains("restart", StringComparison.OrdinalIgnoreCase),
                    Class = settingClass,
                    EndsAgentConnection = agentSwitch,
                    Get = () => st.Value ?? "",
                    Set = v =>
                    {
                        s.Set(name, v);
                        s.Save();
                    },
                };
            }
        }

        /// <summary>
        /// Sync (docs/specs/ux-overhaul.md decision 79): Link and MIDI clock are SessionSync's, kept in the settings, not the song; the
        /// entries put them in the one list, so settings_get, settings_set, settings_reset and settings_history cover them as the window does.
        /// </summary>
        IEnumerable<SettingEntry> SyncEntries()
        {
            SessionSync Sync() => core.SessionSync ?? throw new InvalidOperationException("Sync isn't available here.");
            // SessionSync is the UI thread's (its Changed event, the Link helper's start, the clock's registration): a setting the agent
            // sets runs there, from the agent's engine-queue thread, and its refusal ("Link didn't start") comes back as the tool's error.
            void OnUi(Action change) => OnUiThread(change);
            string Flag(bool b) => b ? "True" : "False";
            yield return new SettingEntry { Key = "sync.Link", Section = "sync", Name = "Link", Kind = SettingKind.Bool, Default = "False",
                Description = "Ableton Link: tempo and bar phase shared with other Link apps on the network (runs in the ReBuzzLink helper program). Turns MIDI clock in off.",
                Get = () => Flag(core.SessionSync?.LinkEnabled == true),
                Set = v => OnUi(() =>
                {
                    var sync = Sync();
                    bool on = v == "True";
                    sync.SetLink(on);
                    if (sync.LinkEnabled != on) throw new InvalidOperationException(sync.Problem ?? "Link didn't start.");
                }) };
            yield return new SettingEntry { Key = "sync.LinkStartStop", Section = "sync", Name = "LinkStartStop", Kind = SettingKind.Bool, Default = "False",
                Description = "Play and stop are shared with Link peers that have start/stop sync on. Needs Link.",
                Get = () => Flag(core.SessionSync?.LinkStartStop == true), Set = v => OnUi(() => Sync().SetLinkStartStop(v == "True")) };
            yield return new SettingEntry { Key = "sync.MidiClockIn", Section = "sync", Name = "MidiClockIn", Kind = SettingKind.Bool, Default = "False",
                Description = "Follow the MIDI clock coming in (any open MIDI input): tempo and beat, and Start, Continue and Stop. Turns Link off.",
                Get = () => Flag(core.SessionSync?.FollowMidiClock == true), Set = v => OnUi(() => Sync().SetFollowMidiClock(v == "True")) };
            yield return new SettingEntry { Key = "sync.MidiClockOut", Section = "sync", Name = "MidiClockOut", Kind = SettingKind.Text, Default = "",
                Description = "MIDI outputs that get the MIDI clock while the transport plays, as names separated by \"|\" (the names midi_devices lists); empty for none.",
                Get = () => string.Join("|", core.SessionSync?.ClockOutNames ?? Array.Empty<string>()),
                Set = v => OnUi(() =>
                {
                    var sync = Sync();
                    var want = v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
                    foreach (var name in sync.ClockOutNames.Where(n => !want.Contains(n)).ToList()) sync.SetClockOut(name, false);
                    foreach (var name in want.Where(n => !sync.ClockOutNames.Contains(n)).ToList()) sync.SetClockOut(name, true);
                }) };
        }

        /// <summary>Runs <paramref name="change"/> on the UI thread and waits for it (at once when this is it); what it throws comes back to the caller.</summary>
        void OnUiThread(Action change)
        {
            var dispatcher = core.dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) { change(); return; }
            Exception failure = null;
            dispatcher.Invoke(() =>
            {
                try { change(); } catch (Exception e) { failure = e; }
            });
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        const string AudioDriverUse = "Use audio_set: it opens the device and puts the old one back if no audio flows.";

        IEnumerable<SettingEntry> AudioEntries()
        {
            var reg = core.SettingsRegistry;
            string[] algorithms = { "Recursive Task Groups", "Recursive Tasks", "Threads" };
            yield return new SettingEntry { Key = "audio.Device", Section = "audio", Name = "Device", Kind = SettingKind.Text, Default = "", UseInstead = AudioDriverUse,
                Description = "The audio output device the app opens.", Get = () => reg.Read("AudioDriver", "", "Settings") ?? "", Set = _ => { } };
            yield return new SettingEntry { Key = "audio.BufferSize", Section = "audio", Name = "BufferSize", Kind = SettingKind.Int, Min = 16, Max = 4096, Default = "256", UseInstead = AudioDriverUse,
                Description = "Frames per audio buffer (PortAudio drivers): smaller is lower latency and riskier.", Get = () => reg.Read("BufferSize", 256, PortAudioDriver.Section).ToString(CultureInfo.InvariantCulture), Set = _ => { } };
            yield return new SettingEntry { Key = "audio.SampleRate", Section = "audio", Name = "SampleRate", Kind = SettingKind.Int, Min = 0, Max = 384000, Default = "0", UseInstead = AudioDriverUse,
                Description = "Sample rate in Hz (PortAudio drivers); 0 is the device's own.", Get = () => reg.Read("SampleRate", 0, PortAudioDriver.Section).ToString(CultureInfo.InvariantCulture), Set = _ => { } };
            yield return new SettingEntry { Key = "audio.Input", Section = "audio", Name = "Input", Kind = SettingKind.Text, Default = "", UseInstead = "Choose the input in Preferences ▸ Audio (audio_set handles the output).",
                Description = "The audio input device (PortAudio drivers): empty for none, \"default\" for the system's.", Get = () => reg.Read("Input", "", PortAudioDriver.Section) ?? "", Set = _ => { } };
            yield return new SettingEntry { Key = "audio.AudioThreads", Section = "audio", Name = "AudioThreads", Kind = SettingKind.Int, Min = 1, Max = 8, Default = "4", RestartRequired = true,
                Description = "Threads that render machines in parallel. Restart required.", Get = () => Math.Clamp(reg.Read("AudioThreads", 4, "Settings"), 1, 8).ToString(CultureInfo.InvariantCulture),
                Set = v => reg.Write("AudioThreads", int.Parse(v, CultureInfo.InvariantCulture), "Settings") };
            yield return new SettingEntry { Key = "audio.WorkAlgorithm", Section = "audio", Name = "WorkAlgorithm", Kind = SettingKind.Choice, Options = algorithms, Default = algorithms[1], RestartRequired = true,
                Description = "How machines are scheduled onto the render threads. Restart required.", Get = () => algorithms[Math.Clamp(reg.Read("WorkAlgorithm", 1, "Settings"), 0, 2)],
                Set = v => reg.Write("WorkAlgorithm", Array.IndexOf(algorithms, v), "Settings") };
        }

        IEnumerable<SettingEntry> SafetyEntries()
        {
            var reg = core.SettingsRegistry;
            var safe = core.SafeMaster;
            string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);
            yield return new SettingEntry { Key = "safety.SafetyLimiter", Section = "safety", Name = "SafetyLimiter", Kind = SettingKind.Bool, Default = "True", Class = SettingClass.Safety,
                Description = "The safety limiter on the audio output: off lets a loud or broken signal reach the speakers at full scale and beyond. Renders are never limited.",
                Get = () => safe.LimiterOn ? "True" : "False", Set = v => { safe.LimiterOn = v == "True"; safe.Save(reg); } };
            yield return new SettingEntry { Key = "safety.LimiterCeilingDb", Section = "safety", Name = "LimiterCeilingDb", Kind = SettingKind.Number, Min = SafeMaster.MinCeilingDb, Max = SafeMaster.MaxCeilingDb, Default = "0", Class = SettingClass.Safety,
                Description = "The safety limiter's ceiling in dBFS (-24 to 0).", Get = () => Num(safe.CeilingDb), Set = v => { safe.CeilingDb = double.Parse(v, CultureInfo.InvariantCulture); safe.Save(reg); } };
            yield return new SettingEntry { Key = "safety.ClipThresholdDb", Section = "safety", Name = "ClipThresholdDb", Kind = SettingKind.Number, Min = SafeMaster.MinThresholdDb, Max = SafeMaster.MaxThresholdDb, Default = "0", Class = SettingClass.Safety,
                Description = "The clip indicator's threshold in dBFS (-24 to +24): peaks over it latch until cleared.", Get = () => Num(safe.ClipThresholdDb), Set = v => { safe.ClipThresholdDb = double.Parse(v, CultureInfo.InvariantCulture); safe.Save(reg); } };
            yield return new SettingEntry { Key = "safety.MachineGuard", Section = "safety", Name = "MachineGuard", Kind = SettingKind.Bool, Default = "True", Class = SettingClass.Safety,
                Description = "The guard on every machine: silences and mutes one that outputs NaN, infinity or over +60 dBFS. Off for this song's session only (a new or opened song turns it on again); never saved.",
                Get = () => MachineGuard.Enabled ? "True" : "False", Set = v => MachineGuard.Enabled = v == "True" };
        }
    }

    /// <summary>Parameter MIDI bindings through the parameters, with one undo step each (the calling Engine group).</summary>
    internal sealed class MidiBackend : IMidiBindings
    {
        readonly ReBuzzCore core;

        public MidiBackend(ReBuzzCore core) => this.core = core;

        static IEnumerable<(IParameter Parameter, int Track)> Slots(MachineCore m)
        {
            for (int g = 1; g <= 2 && g < m.ParameterGroups.Count; g++)
            {
                var group = m.ParameterGroups[g];
                foreach (var p in group.Parameters)
                {
                    if (p.Type == ParameterType.Internal || p.Type == ParameterType.Note) continue;
                    for (int t = 0; t < Math.Max(1, group.TrackCount); t++) yield return (p, t);
                }
            }
        }

        public IReadOnlyList<MidiBindingInfo> List(string machine)
        {
            var list = new List<MidiBindingInfo>();
            var machines = machine != null ? new[] { SongModel.Machine(core, machine) } : SongModel.Machines(core).ToArray();
            foreach (var m in machines)
                foreach (var (p, t) in Slots(m))
                {
                    var (ch, cc) = p.GetMIDIBinding(t);
                    if (ch >= 0 && cc >= 0) list.Add(new MidiBindingInfo(m.Name, p.Name, t, ch + 1, cc));
                }
            return list;
        }

        (MachineCore Machine, IParameter Parameter) Find(string machine, string parameter, int track)
        {
            var m = SongModel.Machine(core, machine);
            var p = SongModel.Param(m, SongModel.Params(m), parameter, track > 0).Parameter;
            int tracks = Math.Max(1, p.Group.TrackCount);
            if (track >= tracks)
                throw new ToolError($"{m.Name}'s {p.Name} has {tracks} track{(tracks == 1 ? "" : "s")}: track 0" + (tracks > 1 ? $" to {tracks - 1}" : "") + ".");
            return (m, p);
        }

        /// <summary>Binds without an undo step of its own (the caller's action does that): off first, so the old controller stops.</summary>
        static void Set(IParameter p, int track, int channel0, int controller)
        {
            p.BindToMIDIController(track, -1, -1);
            if (channel0 >= 0 && controller >= 0) p.BindToMIDIController(track, channel0, controller);
        }

        IParameter Again(string machine, int group, int index)
        {
            var m = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == machine);
            return m == null || group >= m.ParameterGroups.Count || index >= m.ParameterGroups[group].Parameters.Count ? null : m.ParameterGroups[group].Parameters[index];
        }

        MidiBindingInfo Info(MachineCore m, IParameter p, int track, (int, int) b) => b.Item1 < 0 ? null : new MidiBindingInfo(m.Name, p.Name, track, b.Item1 + 1, b.Item2);

        public MidiBindingInfo Bind(string machine, string parameter, int track, int channel, int controller)
        {
            var (m, p) = Find(machine, parameter, track);
            var before = p.GetMIDIBinding(track);
            int group = p.Group.Type == ParameterGroupType.Track ? 2 : 1, index = p.IndexInGroup;
            string name = m.Name;
            AgentActions.Do(core, new DelegateAction(
                () => { if (Again(name, group, index) is { } q) Set(q, track, channel - 1, controller); },
                () => { if (Again(name, group, index) is { } q) Set(q, track, before.Item1, before.Item2); }));
            return Info(m, p, track, before);
        }

        public bool Unbind(string machine, string parameter, int track)
        {
            var (m, p) = Find(machine, parameter, track);
            var before = p.GetMIDIBinding(track);
            if (before.Item1 < 0) return false;
            int group = p.Group.Type == ParameterGroupType.Track ? 2 : 1, index = p.IndexInGroup;
            string name = m.Name;
            AgentActions.Do(core, new DelegateAction(
                () => { if (Again(name, group, index) is { } q) Set(q, track, -1, -1); },
                () => { if (Again(name, group, index) is { } q) Set(q, track, before.Item1, before.Item2); }));
            return true;
        }

        public int UnbindAll(string machine)
        {
            var m = SongModel.Machine(core, machine);
            var had = Slots(m).Select(s => (s.Parameter, s.Track, Binding: s.Parameter.GetMIDIBinding(s.Track)))
                .Where(x => x.Binding.Item1 >= 0)
                .Select(x => (Group: x.Parameter.Group.Type == ParameterGroupType.Track ? 2 : 1, Index: x.Parameter.IndexInGroup, x.Track, x.Binding)).ToList();
            if (had.Count == 0) return 0;
            string name = m.Name;
            AgentActions.Do(core, new DelegateAction(
                () => { foreach (var h in had) if (Again(name, h.Group, h.Index) is { } q) Set(q, h.Track, -1, -1); },
                () => { foreach (var h in had) if (Again(name, h.Group, h.Index) is { } q) Set(q, h.Track, h.Binding.Item1, h.Binding.Item2); }));
            return had.Count;
        }

        public IDisposable Listen(Action<int, int> seen)
        {
            void On(int data)
            {
                int status = data & 0xFF;
                if ((status & 0xF0) == 0xB0) seen((status & 0x0F) + 1, (data >> 8) & 0x7F);
            }
            core.MIDIInput += On;
            return new Stop(() => core.MIDIInput -= On);
        }

        sealed class Stop : IDisposable
        {
            readonly Action stop;
            public Stop(Action stop) => this.stop = stop;
            public void Dispose() => stop();
        }
    }

    /// <summary>The audio output as the engine opens it (the studio's Preferences ▸ Audio does the same).</summary>
    internal sealed class AudioBackend : IAudioControl
    {
        readonly ReBuzzCore core;

        public AudioBackend(ReBuzzCore core) => this.core = core;

        static readonly int[] BufferChoices = { 32, 64, 128, 256, 512, 1024, 2048 };
        static readonly int[] RateChoices = { 0, 44100, 48000, 88200, 96000 };

        public IReadOnlyList<int> Buffers => BufferChoices;

        public IReadOnlyList<int> Rates => RateChoices;

        public AudioState Status()
        {
            var engine = core.AudioEngine;
            var device = engine?.SelectedOutDevice;
            var driver = engine?.CurrentDriver;
            int rate = device != null ? Math.Max(1, ReBuzzCore.masterInfo.SamplesPerSec) : 0;
            return new AudioState(
                Open: device != null && driver != null,
                Device: device?.Name,
                Input: engine?.SelectedInDevice?.Name,
                BufferFrames: driver?.BufferSize ?? 0,
                SampleRate: rate,
                LatencyMs: rate > 0 ? core.OutputLatencySamples * 1000.0 / rate : 0,
                Dropouts: ReBuzzCore.DeadlineMisses,
                WorstOverrunMs: Interlocked.Read(ref ReBuzzCore.DeadlineWorstOverrunUs) / 1000.0,
                PlayedFrames: Interlocked.Read(ref ReBuzzCore.PlayedFrames),
                Driver: device?.Type.ToString() ?? "none",
                BufferAndRateSettable: device?.Type == AudioEngine.AudioOutType.PortAudio);
        }

        public IReadOnlyList<AudioDeviceInfo> Devices()
        {
            var engine = core.AudioEngine;
            if (engine == null) return Array.Empty<AudioDeviceInfo>();
            string current = engine.SelectedOutDevice?.Name;
            try
            {
                return engine.AudioDevices().Select(d => new AudioDeviceInfo(d.Name, d.Type.ToString(), d.Name == current)).ToList();
            }
            catch (Exception)
            {
                return Array.Empty<AudioDeviceInfo>();
            }
        }

        public AudioConfig Current()
        {
            var reg = core.SettingsRegistry;
            var device = core.AudioEngine?.SelectedOutDevice;
            string name = device?.Name ?? reg.Read("AudioDriver", "", "Settings") ?? "";
            if (device == null || device.Type == AudioEngine.AudioOutType.PortAudio)
                return new AudioConfig(name, reg.Read("BufferSize", 256, PortAudioDriver.Section), reg.Read("SampleRate", 0, PortAudioDriver.Section));
            return new AudioConfig(name, core.AudioEngine.CurrentDriver?.BufferSize ?? 0, ReBuzzCore.masterInfo.SamplesPerSec);
        }

        public void Apply(AudioConfig config)
        {
            var engine = core.AudioEngine ?? throw new ToolError("The audio engine isn't running.");
            var target = engine.AudioDevices().FirstOrDefault(d => d.Name == config.Device)
                         ?? throw new ToolError($"No output device \"{config.Device}\". audio_devices lists them.");
            var now = Current();
            if (target.Type == AudioEngine.AudioOutType.PortAudio)
            {
                var reg = core.SettingsRegistry;
                if (config.BufferFrames > 0 && config.BufferFrames != now.BufferFrames) reg.Write("BufferSize", config.BufferFrames, PortAudioDriver.Section);
                if (config.SampleRate != now.SampleRate) reg.Write("SampleRate", config.SampleRate, PortAudioDriver.Section);
            }
            else if ((config.BufferFrames != now.BufferFrames && config.BufferFrames > 0) || config.SampleRate != now.SampleRate)
                throw new ToolError($"{target.Name} takes its buffer and rate from its own control panel, not from audio_set.");

            // As Preferences ▸ Audio reopens it: a new device the way the toolbar chooses one (it opens, plays and is remembered), the same one closed and opened again.
            if (core.SelectedAudioDriver != config.Device)
                core.SelectedAudioDriver = config.Device;
            else
            {
                engine.CreateAudioOut(config.Device);
                engine.Play();
            }
            if (engine.SelectedOutDevice == null) throw new ToolError($"{config.Device} did not open (it may be in use by another program).");
        }
    }
}
