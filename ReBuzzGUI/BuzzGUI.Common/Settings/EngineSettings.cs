namespace BuzzGUI.Common.Settings
{
    public enum PriorityProfileType { NormalAppPriority, AllFocusOnAudio, AllDefaults };
    public enum SubTickResolution { Normal = 1, Lower, Low };
    public enum BurstProtectionType { Off, LimitTo0dB, LimitTo3dB, LimitTo6dB, LimitTo9dB, LimitTo12dB };
    public class EngineSettings : Settings
    {
        [BuzzSetting(true, Description = "Make BPM accurate by alternating between two SamplesPerTick values.")]
        public bool AccurateBPM { get; set; }

        [BuzzSetting(true, Description = "Use an equal power panning law. Old songs may sound slightly different.")]
        public bool EqualPowerPanning { get; set; }

        [BuzzSetting(true, Description = "Enable low latency garbage collector mode while the engine is running. Only affects .NET machines.")]
        public bool LowLatencyGC { get; set; }

        // Fork (docs/specs/mastering-tools.md §4.1): on by default. Settings keep only what a user changed, so a stored choice stays.
        [BuzzSetting(true, Description = "Compensate latency of machines by delaying other machines.")]
        public bool MachineDelayCompensation { get; set; }

        [BuzzSetting(true, Description = "Use multiple threads to render audio. Incompatible with many old machines. Use it.")]
        public bool Multithreading { get; set; }

        [BuzzSetting(true, Description = "Keep processing machines even when they are muted. Uses more CPU but makes transitions smoother.")]
        public bool ProcessMutedMachines { get; set; }

        [BuzzSetting(true, Description = "Enables new internal timing stuff. Required by many new machines.")]
        public bool SubTickTiming { get; set; }

        [BuzzSetting(SubTickResolution.Normal, Description = "Sub‑tick resolution. Lower values decrease CPU load.")]
        public SubTickResolution SubTickResolution { get; set; }

        [BuzzSetting(false, Description = "Use background thread to fill audio buffer (restart).")]
        public bool AudioBufferFillThread { get; set; }

        [BuzzSetting(false, Description = "Prototype (#107): use a cached topological work order instead of the per-chunk graph re-scan. Affects the single-thread, Groups (algorithm 0) and Threads (algorithm 2) paths.")]
        public bool UseCachedWorkOrder { get; set; }

        // Fork (docs/specs/history-snapshots.md HS1): what a song's undo history may hold; the oldest steps drop first.
        [BuzzSetting(256, Description = "Memory the song's undo history may use, in MB (per song). The oldest steps drop first.")]
        public int HistoryMemoryMB { get; set; }

        // Fork (docs/specs/history-snapshots.md HS2): full-song snapshots in a history folder beside the song.
        [BuzzSetting(5, Description = "Minutes of editing between automatic song snapshots (0: off). Snapshots are also taken on save and before big operations.")]
        public int SnapshotIntervalMinutes { get; set; }

        [BuzzSetting(false, Description = "On save, also write the song's snapshots into the song file (a section other Buzz versions ignore).")]
        public bool EmbedHistoryInSong { get; set; }

        [BuzzSetting(PriorityProfileType.NormalAppPriority, Description = "Priority setting for threads and processes (restart).")]
        public PriorityProfileType PriorityProfile { get; set; }

        [BuzzSetting(BurstProtectionType.Off, Description = "Off, DSP Chain: DC-block, slew-limit, envelope, soft-clip")]
        public BurstProtectionType BurstProtectionAudioOutput { get; set; }

        [BuzzSetting(BurstProtectionType.Off, Description = "Off, DSP Chain: DC-block, slew-limit, envelope, soft-clip")]
        public BurstProtectionType BurstProtectionConnections { get; set; }
    }
}
