// Agent server (docs/specs/agent-knowledge.md §5, K3): the app's own controls, as tools. This file is the
// seam: what the app (ReBuzz.exe, the studio, the headless host) gives the tools, as small interfaces, so
// the tools (SettingsTools, MidiTools, ExportTools, AppInfoTools) are portable and tested with fakes.
// The engine-side implementation is ReBuzz/Agent/AppBackend.cs; the studio adds its view control.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReBuzz.Agent.App
{
    public enum SettingKind { Bool, Int, Number, Choice, Text }

    /// <summary>How careful a client should be: "engine" flags and "safety" settings are what the destructive annotation is for.</summary>
    public static class SettingClass
    {
        public const string Normal = "normal", Engine = "engine", Safety = "safety";
    }

    /// <summary>One preference: where it lives, what it holds, and how to read and write it.</summary>
    public sealed class SettingEntry
    {
        /// <summary>"section.Name" in lower-case section, as settings_get lists it ("general.PatternLength").</summary>
        public required string Key { get; init; }
        public required string Section { get; init; }
        public required string Name { get; init; }
        public SettingKind Kind { get; init; } = SettingKind.Text;
        public string Description { get; init; } = "";
        /// <summary>The default, as text ("True", "16").</summary>
        public string Default { get; init; } = "";
        public string[]? Options { get; init; }
        /// <summary>What a window shows for each of <see cref="Options"/> (an enum value's description); null: the options themselves.</summary>
        public string[]? OptionLabels { get; init; }
        public double? Min { get; init; }
        public double? Max { get; init; }
        /// <summary>Takes effect only after the app restarts.</summary>
        public bool RestartRequired { get; init; }
        public string Class { get; init; } = SettingClass.Normal;
        /// <summary>Changing it ends this agent connection (the agent server's own switch and port).</summary>
        public bool EndsAgentConnection { get; init; }
        /// <summary>Not for settings_set: say what to use instead (the audio device has audio_set).</summary>
        public string? UseInstead { get; init; }
        public required Func<string> Get { get; init; }
        public required Action<string> Set { get; init; }
    }

    /// <summary>A parameter's MIDI controller binding (channel 1 to 16).</summary>
    public sealed record MidiBindingInfo(string Machine, string Parameter, int Track, int Channel, int Controller);

    /// <summary>Parameter MIDI bindings: a controller that drives a machine parameter (saved with the song).</summary>
    public interface IMidiBindings
    {
        /// <summary>The song's bindings, optionally of one machine.</summary>
        IReadOnlyList<MidiBindingInfo> List(string? machine);

        /// <summary>Binds (replacing a binding the parameter had); returns what it had, or null. Names are checked: ToolError says what to do.</summary>
        MidiBindingInfo? Bind(string machine, string parameter, int track, int channel, int controller);

        /// <summary>Removes one parameter's binding; false when it had none.</summary>
        bool Unbind(string machine, string parameter, int track);

        /// <summary>Removes every binding of a machine; how many.</summary>
        int UnbindAll(string machine);

        /// <summary>Starts listening for a controller move (control change): <paramref name="seen"/> gets channel 1-16 and the controller number. Dispose to stop. Null: no MIDI input here.</summary>
        IDisposable? Listen(Action<int, int> seen);
    }

    public sealed record AppFacts(string Version, string Mode, string Platform, int Tools, string? Song, bool Unsaved, double TempoBpm, bool Playing);

    /// <summary>The audio output's settings as the engine opens it.</summary>
    public sealed record AudioConfig(string Device, int BufferFrames, int SampleRate);

    /// <summary>The audio output now.</summary>
    public sealed record AudioState(bool Open, string? Device, string? Input, int BufferFrames, int SampleRate, double LatencyMs,
        long Dropouts, double WorstOverrunMs, long PlayedFrames, string Driver, bool BufferAndRateSettable);

    public sealed record AudioDeviceInfo(string Name, string Kind, bool Current);

    /// <summary>The audio device (studio and live mode).</summary>
    public interface IAudioControl
    {
        AudioState Status();

        IReadOnlyList<AudioDeviceInfo> Devices();

        /// <summary>The settings the output is open with (or would open with).</summary>
        AudioConfig Current();

        /// <summary>Opens the output with these settings (ToolError for a device it can't find, or a buffer or rate the driver doesn't take).</summary>
        void Apply(AudioConfig config);

        IReadOnlyList<int> Buffers { get; }

        IReadOnlyList<int> Rates { get; }
    }

    /// <summary>Where the studio's views and panels are.</summary>
    public sealed record ViewState(string View, bool BrowserOpen, double BrowserWidth, bool InspectorOpen, double InspectorWidth,
        string InspectorTab, bool DetailOpen, double DetailHeight, bool DetailMaximised, string DetailPage,
        string? Machine, string? Pattern, int? Scene, double WindowWidth, double WindowHeight, bool Maximised, bool Recording);

    /// <summary>The studio window (Avalonia studio only): the person watching sees every call.</summary>
    public interface IViewControl
    {
        ViewState State();

        /// <summary>Recording: nothing may take the keyboard or open a window.</summary>
        bool Recording { get; }

        /// <summary>Arrangement, Session or Machines.</summary>
        void ShowView(string view);

        /// <summary>A pattern in the Tracker or piano roll of the detail panel.</summary>
        void ShowPattern(string machine, string pattern, bool pianoRoll);

        /// <summary>A machine's window: gui (its own), parameters or editor (its manifest editor); false when it has none.</summary>
        bool OpenMachineWindow(string machine, string window);

        void Select(string? machine, string? pattern, int? scene);

        /// <summary>browser, inspector or detail: open or close, a size (width; the detail's height), the inspector's tab, the detail's maximised state.</summary>
        void Panel(string panel, bool? open, double? size, string? tab, bool? maximised);

        void Window(double? width, double? height, bool? maximised);
    }

    /// <summary>What the tools get from the app.</summary>
    public interface IAppServices
    {
        /// <summary>Runs on the engine thread; <paramref name="changes"/> makes the call one undo step.</summary>
        Task<T> Engine<T>(string what, bool changes, Func<T> work, CancellationToken ct);

        IReadOnlyList<SettingEntry> Settings();

        /// <summary>Where settings_history keeps its log (a JSON-lines file).</summary>
        string SettingsHistoryFile { get; }

        IMidiBindings Midi { get; }

        /// <summary>Recently opened songs, newest first.</summary>
        IReadOnlyList<string> RecentSongs();

        AppFacts Facts();

        /// <summary>Studio mode's windows and views; null in headless and live mode.</summary>
        IViewControl? View { get; }

        /// <summary>The audio device; null where there is none to control.</summary>
        IAudioControl? Audio { get; }
    }
}
