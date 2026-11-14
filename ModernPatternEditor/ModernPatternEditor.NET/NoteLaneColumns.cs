#if !WINDOWS
// Fork (docs/specs/daw-modes.md AR3, seam S7): the tracker sees the notes.
//
// The engine's MIDI column is where a pattern's notes live for a machine that takes MIDI (the Session View's takes, the agent
// and both piano rolls write it). The studio's editor draws it as lanes of its own, a voice at a time ("Notes / Voice 1":
// Note, Velocity, Delay, Cut), computed with BuzzGUI.Common.NoteLanes. They are display only: nothing of them is saved with
// the editor's data, MPE doesn't play them (the engine plays the column), and an edit in a lane is turned into the notes it
// changes and written to the column as a step of the song's history, so every other note stays exactly as it was.
// The editor's own MIDI lanes (old songs) are unchanged and stay beside these.

using BuzzGUI.Common;
using BuzzGUI.Common.Actions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using WDE.ModernPatternEditor.ColumnRenderer;
using WDE.ModernPatternEditor.MPEStructures;
using static WDE.ModernPatternEditor.PatternEditorUtils;

namespace WDE.ModernPatternEditor
{
    internal enum NoteLaneKind { Note, Velocity, Delay, Cut }

    /// <summary>
    /// The pattern's engine MIDI column seen as tracker lanes: finds the column, follows its changes (a take, the piano rolls,
    /// the agent, an undo) and lays its notes out when asked. One for each pattern the editor shows.
    /// </summary>
    internal sealed class NoteLaneHost : IDisposable
    {
        readonly PatternVM vm;
        readonly IPattern pattern;
        IPatternColumn column;
        NoteLanes lanes;
        int builtRpb;
        bool pending;

        /// <summary>The user asked for the lanes (the toolbar's Notes) though the column holds no notes yet.</summary>
        public bool Forced { get; set; }

        /// <summary>Raised on the editor's thread after the column changed (the lanes will be made again).</summary>
        public event Action Changed;

        public NoteLaneHost(PatternVM vm, IPattern pattern)
        {
            this.vm = vm;
            this.pattern = pattern;
            pattern.ColumnAdded += OnColumnAdded;
            Attach(FindColumn(pattern));
        }

        public IPattern Pattern => pattern;

        public void Dispose()
        {
            pattern.ColumnAdded -= OnColumnAdded;
            Attach(null);
        }

        /// <summary>The pattern's MIDI column for its own machine, as the Session View finds it (a parameter column of a track isn't).</summary>
        internal static IPatternColumn FindColumn(IPattern pattern) =>
            pattern?.Columns.FirstOrDefault(c => c.Type == PatternColumnType.MIDI && (c.Parameter == null || c.Parameter.IndexInGroup < 0) && ReferenceEquals(c.Machine, pattern.Machine));

        /// <summary>The MIDI column, made if the pattern has none.</summary>
        internal static IPatternColumn EnsureColumn(IPattern pattern)
        {
            var c = FindColumn(pattern);
            if (c != null || pattern == null) return c;
            pattern.InsertColumn(pattern.Columns.Count, (IMachine)null);
            return FindColumn(pattern);
        }

        void Attach(IPatternColumn c)
        {
            if (ReferenceEquals(c, column)) return;
            if (column != null) column.EventsChanged -= OnEventsChanged;
            column = c;
            if (column != null) column.EventsChanged += OnEventsChanged;
        }

        void OnColumnAdded(IPatternColumn added)
        {
            var c = FindColumn(pattern);
            if (c != null && !ReferenceEquals(c, column))
            {
                Attach(c);
                OnEventsChanged(null, true);
            }
        }

        void OnEventsChanged(IEnumerable<PatternEvent> events, bool set)
        {
            // Raised wherever the column was written: the lanes are made again on the editor's thread, once for a burst of edits.
            lanes = null;
            if (vm.Editor.Dispatcher.CheckAccess())
            {
                Changed?.Invoke();
                return;
            }
            if (pending) return;
            pending = true;
            vm.Editor.Dispatcher.BeginInvoke(() =>
            {
                pending = false;
                lanes = null;
                Changed?.Invoke();
            });
        }

        /// <summary>The notes of the column on rows: the pattern's rows per beat, a voice for each overlapping note.</summary>
        public NoteLanes Lanes
        {
            get
            {
                int rpb = Math.Max(1, vm.DefaultRPB);
                if (lanes != null && rpb == builtRpb) return lanes;
                var events = column == null
                    ? Enumerable.Empty<LaneEvent>()
                    : column.GetEvents(int.MinValue, int.MaxValue).Select(e => new LaneEvent(e.Time, e.Value, e.Duration));
                builtRpb = rpb;
                return lanes = new NoteLanes(events, RowGrid.Beats(new[] { rpb }), pattern.Length * PatternEvent.TimeBase);
            }
        }

        /// <summary>Whether the lanes are shown: the column holds notes, or the user asked for them.</summary>
        public bool Visible => Forced || Lanes.NoteCount > 0;

        /// <summary>The voices shown: those the notes need and a free one to type the next note in (none when the lanes are hidden).</summary>
        public int ShownVoices => Visible ? Math.Min(Lanes.Voices + 1, 32) : 0;

        /// <summary>One edit of the lanes as a step of the song's history; null when done (or nothing to do), else why not.</summary>
        public string Apply(NoteLaneEdit edit)
        {
            if (edit.Refused != null) return edit.Refused;
            if (edit.IsEmpty) return null;
            var action = new NotesEditAction(pattern, edit.Removed.ToArray(), edit.Added.ToArray());
            pattern.Machine.Graph.Do(action);
            vm.Editor.SetModifiedFlag();
            return null;
        }
    }

    /// <summary>
    /// Events leave and come to a pattern's engine MIDI column, one step of the song's history. The pattern is found by its
    /// machine's and its own name each time, so undo and redo work after the machine is deleted and made again.
    /// </summary>
    [HistoryName("Edit notes")]
    internal sealed class NotesEditAction : IAction
    {
        readonly string machine;
        readonly string name;
        readonly LaneEvent[] removed;
        readonly LaneEvent[] added;

        public NotesEditAction(IPattern pattern, LaneEvent[] removed, LaneEvent[] added)
        {
            machine = pattern.Machine.Name;
            name = pattern.Name;
            this.removed = removed;
            this.added = added;
        }

        public void Do() => Apply(removed, added);

        public void Undo() => Apply(added, removed);

        void Apply(LaneEvent[] take, LaneEvent[] put)
        {
            var p = Global.Buzz.Song.Machines.FirstOrDefault(m => m.Name == machine)?.Patterns.FirstOrDefault(x => x.Name == name);
            if (p == null) return;
            var column = put.Length > 0 ? NoteLaneHost.EnsureColumn(p) : NoteLaneHost.FindColumn(p);
            if (column == null) return;
            if (take.Length > 0) column.SetEvents(take.Select(e => new PatternEvent(e.Time, e.Value, e.Duration)).ToArray(), false);
            if (put.Length > 0) column.SetEvents(put.Select(e => new PatternEvent(e.Time, e.Value, e.Duration)).ToArray(), true);
        }
    }

    /// <summary>
    /// The edit a key in a lane asks for. It isn't an action of the editor's own history: running it writes the song's
    /// (<see cref="NoteLaneHost.Apply"/>), so Undo and Redo are the song's, as the piano rolls'.
    /// </summary>
    internal sealed class NoteLaneAction : BuzzAction
    {
        readonly NoteLaneHost host;
        readonly Func<NoteLanes, NoteLaneEdit> make;

        public NoteLaneAction(NoteLaneHost host, Func<NoteLanes, NoteLaneEdit> make)
        {
            this.host = host;
            this.make = make;
        }

        /// <summary>Makes the edit from the lanes as they are now and writes it; says why in the status bar when it can't.</summary>
        public void RunOnSong()
        {
            string why;
            try { why = host.Apply(make(host.Lanes)); }
            catch (Exception e) { why = "The notes couldn't be written: " + e.Message; }       // a machine that can't take a MIDI column
            if (why != null) Global.Buzz.DCWriteLine("{statusbar}" + why);
        }

        protected override void DoAction() => RunOnSong();

        protected override void UndoAction() { }
    }

    /// <summary>One voice of the Notes lanes: Note, Velocity, Delay and Cut, with a label of its own.</summary>
    internal sealed class NoteLaneColumnSet : ParameterColumnSet
    {
        public readonly NoteLaneHost Host;
        public readonly int Voice;

        public NoteLaneColumnSet(PatternVM pattern, NoteLaneHost host, int voice) : base(pattern, voice)
        {
            Host = host;
            Voice = voice;
            var machine = pattern.Pattern.Machine;
            columns = new ParameterColumn[]
            {
                Make(machine, PatternEditorUtils.InternalParameter.MidiNote, NoteLaneKind.Note),
                Make(machine, PatternEditorUtils.InternalParameter.MidiVelocity, NoteLaneKind.Velocity),
                Make(machine, PatternEditorUtils.InternalParameter.MidiNoteDelay, NoteLaneKind.Delay),
                Make(machine, PatternEditorUtils.InternalParameter.MidiNoteCut, NoteLaneKind.Cut),
            };
            Label = "Notes\nVoice " + (voice + 1);
        }

        ParameterColumn Make(IMachine machine, PatternEditorUtils.InternalParameter type, NoteLaneKind kind)
        {
            var parameter = MPEInternalParameter.GetInternalParameter(machine, type);
            return new NoteLaneColumn(this, kind, new MPEPatternColumnBuzz(PatternColumnType.MIDI, Pattern.Pattern, machine, parameter, Voice));
        }

        /// <summary>The lanes aren't played by the editor, so there is nothing to send.</summary>
        public override void SendCCsAtTime(int time) { }

        public void InvalidateAll() => FireBeatsInvalidated(Enumerable.Range(0, Math.Max(1, BeatCount)).ToHashSet());
    }

    internal sealed class NoteLaneColumn : ParameterColumn
    {
        static readonly int BeatTime = 4 * PatternEvent.TimeBase;

        public readonly NoteLaneKind Kind;
        readonly NoteLaneColumnSet lanesSet;

        public NoteLaneColumn(NoteLaneColumnSet set, NoteLaneKind kind, IPatternColumn pc) : base(set, pc, set.Voice)
        {
            lanesSet = set;
            Kind = kind;
        }

        int Rpb => Math.Max(1, Set.Pattern.DefaultRPB);

        int Voice => lanesSet.Voice;

        public override ColumnRenderer.ColumnType Type => Kind == NoteLaneKind.Note ? ColumnRenderer.ColumnType.Note : ColumnRenderer.ColumnType.HexValue;

        internal override bool IsDecimal => Kind != NoteLaneKind.Note && !ValueDisplay.IsHex;

        public override int DigitCount => Kind == NoteLaneKind.Note ? 3 : IsDecimal ? ValueDisplay.DecimalDigits(PatternColumn.Parameter.MaxValue) : 2;

        public override bool TiedToNext => false;

        public override string Label => Kind switch { NoteLaneKind.Note => "Note", NoteLaneKind.Velocity => "Vel", NoteLaneKind.Delay => "Dly", _ => "Cut" };

        public override string Description => Kind switch
        {
            NoteLaneKind.Note => "Notes of the MIDI column (the engine plays them)",
            NoteLaneKind.Velocity => "Velocity of the note",
            NoteLaneKind.Delay => "Delay into the row, in 1/95 of a row",
            _ => "Cut: where in the row the note ends, in 1/95 of a row",
        };

        public override void Release() { }

        public override void SendCCAtTime(int time) { }

        public override IBeat FetchBeat(int index) => new LaneBeat(this, index);

        int RowTime(int rowInBeat) => BeatTime * rowInBeat / Rpb;

        public override int GetDigitTime(Digit d) => d.Beat * BeatTime + RowTime(d.RowInBeat);

        public override Digit GetDigitAtTime(int time)
        {
            var digit = new Digit(Set.Pattern, Set.Pattern.ColumnSets.IndexOf(Set), Set.Columns.IndexOf(this), 0, 0, 0);
            int beat = time / BeatTime;
            if (beat < 0 || beat >= Set.BeatCount) return digit;
            return digit.SetBeat(beat).SetRowInBeat(Math.Min(Rpb - 1, (time - beat * BeatTime) * Rpb / BeatTime));
        }

        /// <summary>The value the column shows in a row (the parameter's no-value when it shows none).</summary>
        int ValueIn(int beat, int rowInBeat)
        {
            var lanes = lanesSet.Host.Lanes;
            if (!lanes.TryCell(Voice, beat * Rpb + rowInBeat, out var cell)) return PatternColumn.Parameter.NoValue;
            switch (Kind)
            {
                case NoteLaneKind.Note: return cell.IsOff ? BuzzNote.Off : BuzzNote.FromMIDINote(cell.Midi);
                case NoteLaneKind.Velocity: return cell.IsOff ? 0 : cell.Velocity;
                case NoteLaneKind.Delay: return cell.Delay;
                default: return cell.Cut;
            }
        }

        sealed class LaneBeat : IBeat
        {
            readonly NoteLaneColumn column;
            readonly int index;

            public LaneBeat(NoteLaneColumn column, int index)
            {
                this.column = column;
                this.index = index;
            }

            public IList<BeatRow> Rows
            {
                get
                {
                    int rpb = column.Rpb;
                    var rows = new BeatRow[rpb];
                    for (int i = 0; i < rpb; i++)
                    {
                        int v = column.ValueIn(index, i);
                        int time = column.RowTime(i);
                        rows[i] = new BeatRow
                        {
                            Value = v,
                            Time = time,
                            VisualTime = time,
                            Type = v == column.PatternColumn.Parameter.NoValue ? BeatValueType.NoValue
                                 : column.Kind == NoteLaneKind.Note ? BeatValueType.Note : BeatValueType.Parameter,
                            ValueString = column.Text(v),
                        };
                    }
                    return rows;
                }
            }
        }

        string Text(int v)
        {
            var parameter = PatternColumn.Parameter;
            if (v == parameter.NoValue) return new string('.', DigitCount);
            if (Kind == NoteLaneKind.Note) return v != BuzzNote.Off && NoteName(v) is string name ? name : parameter.GetHexValueString(v);
            return IsDecimal ? v.ToString("D" + DigitCount) : parameter.GetHexValueString(v);
        }

        // ------------------------------------------------------------------ edits

        NoteLaneAction Edit(Func<NoteLanes, NoteLaneEdit> make) => new NoteLaneAction(lanesSet.Host, make);

        int RowOf(Digit d) => d.Beat * Rpb + d.RowInBeat;

        public override IEnumerable<BuzzAction> EditValue(Digit d, int newvalue)
        {
            int row = RowOf(d), voice = Voice;
            if (newvalue < 0) return Cleared(row);
            switch (Kind)
            {
                case NoteLaneKind.Note:
                    if (newvalue == BuzzNote.Off) return Edit(l => l.SetOff(voice, row)).Once();
                    return NoteFrom(newvalue, row);
                case NoteLaneKind.Velocity: return Edit(l => l.SetVelocity(voice, row, newvalue)).Once();
                case NoteLaneKind.Delay: return Edit(l => l.SetDelay(voice, row, newvalue)).Once();
                default: return Edit(l => l.SetCut(voice, row, newvalue)).Once();
            }
        }

        IEnumerable<BuzzAction> Cleared(int row)
        {
            int voice = Voice;
            switch (Kind)
            {
                case NoteLaneKind.Note: return Edit(l => l.Clear(voice, row)).Once();
                case NoteLaneKind.Velocity: return Edit(l => l.SetVelocity(voice, row, 0)).Once();
                case NoteLaneKind.Delay: return Edit(l => l.SetDelay(voice, row, 0)).Once();
                default: return Edit(l => l.SetCut(voice, row, 0)).Once();
            }
        }

        IEnumerable<BuzzAction> NoteFrom(int buzzNote, int row)
        {
            int voice = Voice;
            int midi;
            try { midi = BuzzNote.ToMIDINote(buzzNote); }
            catch { return null; }
            return Edit(l => l.SetNote(voice, row, midi)).Once();
        }

        public override IEnumerable<BuzzAction> EditDigit(Digit d, int newvalue)
        {
            int row = RowOf(d), voice = Voice;
            if (newvalue < 0) return Cleared(row);
            if (Kind == NoteLaneKind.Note)
            {
                if (d.Index == 0) return newvalue == BuzzNote.Off ? Edit(l => l.SetOff(voice, row)).Once() : NoteFrom(newvalue, row);
                // The octave digit, as the notes are named (ux-overhaul.md §3.5), of a note that is there.
                var lanes = lanesSet.Host.Lanes;
                if (!lanes.TryCell(voice, row, out var cell) || cell.IsOff) return null;
                int octave = NoteNames.BuzzOctave(newvalue);
                if (octave < BuzzNote.MinOctave || octave > BuzzNote.MaxOctave) return null;
                return NoteFrom((BuzzNote.FromMIDINote(cell.Midi) & 15) | (octave << 4), row);
            }

            int current = ValueIn(d.Beat, d.RowInBeat);
            if (current == PatternColumn.Parameter.NoValue) current = 0;
            int value;
            if (IsDecimal)
            {
                var digits = current.ToString("D" + DigitCount).ToCharArray();
                if (d.Index < 0 || d.Index >= digits.Length) return null;
                digits[d.Index] = (char)('0' + newvalue);
                value = int.Parse(new string(digits), System.Globalization.CultureInfo.InvariantCulture);
            }
            else
                value = (current & (0x0f << (d.Index * 4))) | (newvalue << ((d.Index ^ 1) * 4));
            value = Math.Min(value, PatternColumn.Parameter.MaxValue);
            return EditValue(d, value);
        }
    }

    internal static class NoteLaneExtensions
    {
        public static IEnumerable<BuzzAction> Once(this BuzzAction action) => new[] { action };
    }
}
#endif
