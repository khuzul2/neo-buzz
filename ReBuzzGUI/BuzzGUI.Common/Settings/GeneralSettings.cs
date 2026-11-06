namespace BuzzGUI.Common.Settings
{
    public enum SequenceEditorType { ModernVertical, ModernHorizontal };
    public enum SongLoadWaitTime { Seconds_10 = 10, Seconds_30 = 30, Minutes_1 = 60, Minutes_5 = 5*60 };
    public enum DpiScalingType { System = -1, Scale_100 = 100, Scale_125 = 125, Scale_150 = 150, Scale_175 = 175,
        Scale_200 = 200, Scale_225 = 225, Scale_250 = 250, Scale_275 = 275, Scale_300 = 300,
        Scale_325 = 325, Scale_350 = 350, Scale_375 = 375, Scale_400 = 400 };
    public enum WorkingModeType { Normal, Play };

    public enum PluginRescanStrategyType { WhenChanged, Always, Never };
    public class GeneralSettings : Settings
    {
        [BuzzSetting(true, Description = "Rename previous version to <songname>.backup when saving a song.")]
        public bool AutomaticBackups { get; set; }

        [BuzzSetting(false, Description = "Keep song backups.")]
        public bool KeepBackups { get; internal set; }

        // Fork (NeoBuzz Live): not a setting any more, and always false. Upstream's update check offered upstream ReBuzz's
        // installer, which would replace NeoBuzz Live (BuzzGUI.BuzzUpdate is off). With no [BuzzSetting] the Settings
        // window doesn't list it and a saved True isn't read back; the property stays for code that reads or sets it.
        [System.Obsolete("NeoBuzz Live doesn't check for updates: always false.")]
        public bool CheckForUpdates { get => false; set { } }

        [BuzzSetting(true, Description = "Use TextFormattingMode.Ideal for UI text rendering. Usually looks better if Windows text scaling is set to >100%. Restart required.")]
        public bool WPFIdealFontMetrics { get; set; }

        [BuzzSetting(false, Description = "Disable hardware accelerated (GPU) graphics rendering. Not recommended unless your GPU/drivers suck. Restart required.")]
        public bool WPFSoftwareRendering { get; set; }

        [BuzzSetting(PluginRescanStrategyType.WhenChanged, Description = "Plugin rescan strategy: Rescan when DLL count changes, Always, Never.")]
        public PluginRescanStrategyType PluginRescanStrategy { get; set; }

        [BuzzSetting(SequenceEditorType.ModernVertical, Description = "Sequence View Type.")]
        public SequenceEditorType SequenceView { get; set; }

        [BuzzSetting(SongLoadWaitTime.Seconds_30, Description = "Maximum time to wait a song to load. If the song is not loaded within this time user can choose further actions.")]
        public SongLoadWaitTime SongLoadWait { get; set; }

        //[BuzzSetting(true, Description = "Use multiple threads to initialize machines when loading a song.")]
        //public bool MultithreadSongLoading { get; set; }

        [BuzzSetting(4, Minimum = 2, Maximum = 6, Description = "Default machine base octave.")]
        public int DefaultMachineBaseOctave { get; set; }

        [BuzzSetting(16, Minimum = 1, Maximum = 512, Description = "Default Pattern Length.")]
        public int PatternLength { get; internal set; }

        [BuzzSetting(false, Description = "Make PianoKeyboard topmost.")]
        public bool PianoKeyboardTopmost { get; internal set; }

        // Fork (machine keyboard): the keyboard under the windows of machines that play notes.
        [BuzzSetting(true, Description = "Show a piano keyboard under the window of every machine that plays notes. A window's Keyboard toggle (Ctrl+K, ⌘K on a Mac) shows or hides it for that machine type.")]
        public bool MachineKeyboard { get; set; }

        // Fork (docs/specs/ux-overhaul.md §3.5): how notes are named on screen. NoteNames holds it, for the displays that
        // can't read settings (and the tests that link them); a new key, display only.
        [BuzzSetting(BuzzGUI.Common.NoteNames.Default, Description = "How notes are named on screen, by what middle C (MIDI note 60) is called: in the pattern editor (always three characters, as C-3), the piano roll, the keyboards under machine windows and MIDI mappings. Songs don't change.")]
        public NoteNaming NoteNames { get => BuzzGUI.Common.NoteNames.Naming; set => BuzzGUI.Common.NoteNames.Naming = value; }

        [BuzzSetting(DpiScalingType.System, Description = "DPI scaling.")]
        public DpiScalingType DpiScaling { get; internal set; }

        [BuzzSetting(WorkingModeType.Normal, Description = "Working mode: Normal, Play only (recording disabled).")]
        public WorkingModeType WorkingMode { get; internal set; }
    }
}
