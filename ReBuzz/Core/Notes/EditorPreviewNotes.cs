// Fork (docs/specs/daw-modes.md S4): the notes a pattern editor exports (PatternXP, Modern
// Pattern Editor, any editor with CMachineInterfaceEx::ExportMidiEvents), read only: what the
// sequence editors draw in a pattern's blocks. Editors without an import call can't take edits
// (§3.3), so a piano roll shows these and says why it can't change them.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;

namespace ReBuzz.Core.Notes
{
    internal sealed class EditorPreviewNotes : NoteSourceBase
    {
        /// <summary>PatternEditorMachineMIDIEvents' ticks per Buzz tick.</summary>
        public const int EditorTimeBase = 960;

        readonly ReBuzzCore buzz;
        readonly IPattern pattern;

        public EditorPreviewNotes(ReBuzzCore buzz, IPattern pattern)
        {
            this.buzz = buzz;
            this.pattern = pattern;
        }

        public override IPattern Pattern => pattern;

        public override List<ClipNote> Notes()
        {
            if (!IsLive || pattern.Machine is not MachineCore m || m.EditorMachine == null) return new List<ClipNote>();
            int[] pairs;
            try
            {
                pairs = pattern.PatternEditorMachineMIDIEvents;
            }
            catch (Exception e) when (e is InvalidOperationException || e is NullReferenceException || e is ArgumentException || e is KeyNotFoundException)
            {
                return new List<ClipNote>();
            }
            return FromEditorEvents(pairs, Length);
        }

        /// <summary>
        /// Notes from an editor's (time, MIDI message) pairs, times in 1/960 tick: each note-on
        /// lasts to the first note-off of its pitch and channel after it (or the pattern's end).
        /// </summary>
        internal static List<ClipNote> FromEditorEvents(int[] pairs, int patternTime)
        {
            var notes = new List<ClipNote>();
            if (pairs == null) return notes;
            var open = new List<(int Time, int Note, int Velocity, int Channel)>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                int time = (int)((long)pairs[i] * PatternEvent.TimeBase / EditorTimeBase);
                int message = pairs[i + 1];
                int status = message & 0xF0, channel = message & 0x0F;
                int note = (message >> 8) & 0x7F, velocity = (message >> 16) & 0x7F;
                if (status == 0x90 && velocity > 0)
                {
                    open.Add((time, note, velocity, channel));
                }
                else if (status == 0x80 || status == 0x90)
                {
                    int k = open.FindIndex(o => o.Note == note && o.Channel == channel);
                    if (k < 0) continue;
                    var o = open[k];
                    open.RemoveAt(k);
                    notes.Add(new ClipNote(o.Time, Math.Max(1, time - o.Time), o.Note, o.Velocity, o.Channel));
                }
            }
            foreach (var o in open)
                notes.Add(new ClipNote(o.Time, Math.Max(1, patternTime - o.Time), o.Note, o.Velocity, o.Channel));
            notes.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Note.CompareTo(b.Note));
            return notes;
        }

        public override string WhyNotEditable =>
            !IsLive ? "The pattern was deleted."
            : "These are the pattern editor's notes: edit them in the Pattern View.";

        protected override string Apply(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added) => WhyNotEditable;

        public override void Preview(int note, int velocity, bool on) => SendMidiNote(buzz, pattern, note, velocity, on);

        protected override void DoUndo()
        {
        }

        protected override void DoRedo()
        {
        }
    }
}
