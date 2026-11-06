#nullable disable
// Fork addition (neo-buzz, docs/specs/ux-overhaul.md §3.5): how notes are named on screen. One setting (Preferences ▸
// General ▸ Note names, GeneralSettings.NoteNames) that every note display reads: the pattern editor (three characters
// in every naming: "C-3"), the piano roll, the keyboards under machine windows, MIDI mappings, the messages that name
// notes. Display only: songs don't change; BuzzNote's names ("C-5" is MIDI 60, which Parse reads and chord files hold)
// and the agent server's (scientific, "C4" is 60: docs/specs/mcp-server.md) stay as they are. Pure: no dependency on
// anything else, so the test projects that link the files using it link this one too.

using System;
using System.Globalization;

namespace BuzzGUI.Common
{
    /// <summary>Which octave number MIDI note 60 (middle C) gets on screen.</summary>
    public enum NoteNaming
    {
        /// <summary>Buzz's: C-5 is 60, every name three characters ("C#5").</summary>
        [System.ComponentModel.Description("Buzz: C-5 = MIDI 60")]
        Buzz,

        /// <summary>C3 is 60, as most music apps name notes.</summary>
        [System.ComponentModel.Description("C3 = MIDI 60")]
        C3,

        /// <summary>Scientific pitch: C4 is 60 (the agent server's names).</summary>
        [System.ComponentModel.Description("Scientific: C4 = MIDI 60")]
        Scientific,
    }

    /// <summary>Note names as the setting says: a MIDI note's ("C3"), a Buzz note value's in the tracker ("C-3").</summary>
    public static class NoteNames
    {
        /// <summary>The naming until the settings say otherwise.</summary>
        public const NoteNaming Default = NoteNaming.C3;

        static readonly string[] Sharps = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        static readonly string[] Tracker3 = { "C-", "C#", "D-", "D#", "E-", "F-", "F#", "G-", "G#", "A-", "A#", "B-" };

        // Every name made once, so drawing a grid of notes allocates nothing: [naming][MIDI 0-127] and [naming][Buzz value 0-255].
        static readonly string[][] names = new string[3][];
        static readonly string[][] trackers = new string[3][];

        static NoteNaming naming = Default;

        /// <summary>The naming every display reads; GeneralSettings.NoteNames sets it (from the saved setting, and when it changes).</summary>
        public static NoteNaming Naming
        {
            get => naming;
            set
            {
                if (!Enum.IsDefined(typeof(NoteNaming), value) || value == naming) return;
                naming = value;
                Changed?.Invoke();
            }
        }

        /// <summary>After <see cref="Naming"/> changes: views that drew names draw them again.</summary>
        public static event Action Changed;

        /// <summary>What a Buzz octave (MIDI notes octave × 12 to octave × 12 + 11) is called: Buzz 5 is 3 in C3 naming.</summary>
        public static int Octave(int buzzOctave) => Octave(buzzOctave, naming);

        public static int Octave(int buzzOctave, NoteNaming n) => buzzOctave + Offset(n);

        /// <summary>The Buzz octave of an octave as shown (typed into the pattern editor's octave digit).</summary>
        public static int BuzzOctave(int shownOctave) => shownOctave - Offset(naming);

        static int Offset(NoteNaming n) => n switch { NoteNaming.C3 => -2, NoteNaming.Scientific => -1, _ => 0 };

        /// <summary>A MIDI note's name (0-127, clamped): 60 is "C3", "C4" or (Buzz) "C-5"; 61 "C#3", "C#4" or "C#5"; 0 "C-2" in C3 naming.</summary>
        public static string Name(int midi) => Name(midi, naming);

        public static string Name(int midi, NoteNaming n)
        {
            var table = names[(int)n] ??= MakeNames(n);
            return table[Math.Clamp(midi, 0, 127)];
        }

        static string[] MakeNames(NoteNaming n)
        {
            var t = new string[128];
            for (int midi = 0; midi < 128; midi++)
            {
                string octave = Octave(midi / 12, n).ToString(CultureInfo.InvariantCulture);
                t[midi] = (n == NoteNaming.Buzz ? Tracker3 : Sharps)[midi % 12] + octave;
            }
            return t;
        }

        /// <summary>
        /// A Buzz note value as the tracker shows it (a value of BuzzNote: octave × 16 + note 1-12, or 255 for off): three
        /// characters in every naming, "C-3" or "C#3" (Buzz's C-5 and C#5 in C3 naming), "off"; an octave below 0 adds its
        /// minus sign ("C--1", "C#-1": MIDI notes under 24 in C3 naming). Null for a value that isn't a note.
        /// </summary>
        public static string TryTracker(int value) => TryTracker(value, naming);

        public static string TryTracker(int value, NoteNaming n)
        {
            if (value < 0 || value > 255) return null;
            var table = trackers[(int)n] ??= MakeTrackers(n);
            return table[value];
        }

        static string[] MakeTrackers(NoteNaming n)
        {
            var t = new string[256];
            for (int value = 0; value < 256; value++)
            {
                int note = (value & 15) - 1, octave = value >> 4;
                if (note >= 0 && note <= 11 && octave <= 9)
                    t[value] = Tracker3[note] + Octave(octave, n).ToString(CultureInfo.InvariantCulture);
            }
            t[255] = "off";
            return t;
        }

        /// <summary>How a naming reads in Preferences: "C3 = MIDI 60".</summary>
        public static string Label(NoteNaming n)
        {
            var field = typeof(NoteNaming).GetField(n.ToString());
            return field != null && Attribute.GetCustomAttribute(field, typeof(System.ComponentModel.DescriptionAttribute)) is System.ComponentModel.DescriptionAttribute d ? d.Description : n.ToString();
        }
    }
}
