using BuzzGUI.Common;
using BuzzGUI.Common.Actions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using WDE.ModernPatternEditor.Actions;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor
{
    public class ParameterColumn : ColumnRenderer.IColumn
    {
        static int BeatDuration = 4 * ClipEvent.TimeBase;

        internal readonly ParameterColumnSet Set;
        internal readonly IClipColumn ClipColumn;
        readonly int track;
        private readonly MPEClipColumn mpeColumn;

        class Beat : ColumnRenderer.IBeat
        {
            readonly int index;
            readonly ParameterColumn column;
            public IList<string> ValueStrings { get { return Values.Select(v => ValueToString(v)).ToArray(); } }
            readonly int[] rowTimes;        // actual times, not necessarily the same as visual times
            public IList<int> RowVisualTimes { get { return rowTimes; } }
            readonly int RowsPerBeat;

            public IList<ColumnRenderer.BeatRow> Rows
            {
                get
                {
                    var rows = new ColumnRenderer.BeatRow[rowTimes.Length];
                    var values = Values;
                    var vstrings = ValueStrings;
                    var types = ValueTypes.ToArray();
                    var vtimes = RowVisualTimes;

                    for (int i = 0; i < rows.Length; i++)
                    {
                        rows[i].Type = types[i];
                        rows[i].Value = values[i];
                        rows[i].ValueString = vstrings[i];
                        rows[i].VisualTime = vtimes[i];
                        rows[i].Time = rowTimes[i];
                    }

                    return rows;
                }
            }

            IEnumerable<ColumnRenderer.BeatValueType> ValueTypes
            {
                get
                {
                    var v = Values;

                    return Enumerable.Range(0, RowsPerBeat).Select(i =>
                    {
                        if (v[i] == column.ClipColumn.Parameter.NoValue) return ColumnRenderer.BeatValueType.NoValue;
                        if (column.ClipColumn.Parameter.Type == ParameterType.Note) return ColumnRenderer.BeatValueType.Note;
                        else return ColumnRenderer.BeatValueType.Parameter;
                    });
                }
            }

            string ValueToString(int v)
            {
                if (v == column.ClipColumn.Parameter.NoValue)
                    return new string('.', column.DigitCount);

                // Fork (ux-overhaul.md P3c): decimal, and a drum machine's names for its notes.
                if (column.IsDecimal) return v.ToString(column.decimalFormat);
                if (column.ClipColumn.Parameter.Type == ParameterType.Note && v != BuzzNote.Off && column.NoteName(v) is string name) return name;
                return column.ClipColumn.Parameter.GetHexValueString(v);
            }

            IList<int> Values
            {
                get
                {
                    int[] values = new int[RowsPerBeat];
                    for (int i = 0; i < values.Length; i++) values[i] = column.ClipColumn.Parameter.NoValue;

                    int t = index * BeatDuration;
                    //foreach (var e in column.PatternColumn.GetEvents(t, t + BeatDuration).Where(e => t % PatternEvent.TimeBase == 0))
                    //						values[(e.Time - t) / PatternEvent.TimeBase] = e.Value;

                    // Get event data from our own structure
                    MPEClip mpeClip = column.Set.Clip.Editor.MPEClipsDB.GetMPEClip(column.Set.Clip.Clip);
                    MPEClipColumn mpeColumn = mpeClip.GetColumn(column.ClipColumn);
                    if (mpeColumn != null)
                    {
                        foreach (var e in mpeColumn.GetEvents(t, t + BeatDuration).Where(e => rowTimes.Contains(e.Time - t)))
                            values[Array.IndexOf(rowTimes, e.Time - t)] = e.Value;
                    }

                    return values;
                }
            }

            public int GetRowTime(int row)
            {
                return index * BeatDuration + rowTimes[row];
            }

            public bool HasValue(int row)
            {
                int t = GetRowTime(row);
                MPEClip mpeClip = column.Set.Clip.Editor.MPEClipsDB.GetMPEClip(column.Set.Clip.Clip);
                MPEClipColumn mpeColumn = mpeClip.GetColumn(column.ClipColumn);
                return mpeColumn != null && mpeColumn.GetEvents(t, t + 1).Any();
            }

            public BuzzAction SetValue(int row, int value)
            {
                if (column.ClipColumn.Parameter.Type != ParameterType.Note || value != BuzzNote.Off)
                {
                    value = Math.Min(Math.Max(value, column.ClipColumn.Parameter.MinValue), column.ClipColumn.Parameter.MaxValue);
                }

                if (column.ClipColumn.Type == ClipColumnType.MIDI)
                {
                    MPEInternalParameter iParameter = (MPEInternalParameter)column.ClipColumn.Parameter;
                    iParameter.SetValue(column.track, value);
                }
                /*
				if (column.PatternColumn.Parameter.Type == ParameterType.Note && value != BuzzNote.Off)
                {
					int o = value >> 4;
					if ((value & 15) < 1 || (value & 15) > 12 || o < BuzzNote.MinOctave || o > BuzzNote.MaxOctave)
                    {
						// convert from midinote
						value = Math.Max(0, Math.Min(value, 119));
						value = BuzzNote.FromMIDINote(value);
                    }
                }
				*/
                int t = GetRowTime(row);
                MPEClip mpeClip = column.Set.Clip.Editor.MPEClipsDB.GetMPEClip(column.Set.Clip.Clip);
                MPEClipColumn mpeColumn = mpeClip.GetColumn(column.ClipColumn);
                return new MPESetOrClearEventsAction(mpeClip.Clip, mpeColumn, new[] { new ClipEvent(t, value) }, true);
            }

            public BuzzAction ClearValue(int row)
            {
                int t = GetRowTime(row);
                MPEClip mpeClip = column.Set.Clip.Editor.MPEClipsDB.GetMPEClip(column.Set.Clip.Clip);
                MPEClipColumn mpeColumn = mpeClip.GetColumn(column.ClipColumn);
                return new MPESetOrClearEventsAction(mpeClip.Clip, mpeColumn, mpeColumn.GetEvents(t, t + 1), false);
            }

            public void SendCCAtTime(int time)
            {
                int row = Array.IndexOf(rowTimes, time);
                if (row < 0 || !HasValue(row)) return;

                if (column.ClipColumn.Type == ClipColumnType.MIDI)
                {
                    MPEInternalParameter iParameter = (MPEInternalParameter)column.ClipColumn.Parameter;
                    if (iParameter.InternalType == ClipEditorUtils.InternalParameter.MidiNote)
                    {
                        int newValue = Values[row];
                        // ToDo: fetch volume from vol column
                        this.column.Set.Clip.Editor.playRecordManager.UpdatePlayingNoteClip(iParameter, column.ClipColumn.Track, newValue, MPEInternalParameter.DefaultMidiVolume);
                        iParameter.SetValue(column.track, newValue);
                    }
                }
                else
                {
                    var machine = column.ClipColumn.Machine;
                    int group = column.ClipColumn.Machine.ParameterGroups.IndexOf(this.column.ClipColumn.Parameter.Group) | 16; // Don't record
                    column.Set.Clip.Editor.cb.ControlChange(machine, group, this.column.track, this.column.ClipColumn.Parameter.IndexInGroup, Values[row]);
                    //column.PatternColumn.Parameter.SetValue(column.track, Values[row]);
                }
            }

            internal Beat(ParameterColumn column, int index)
            {
                this.column = column;
                this.index = index;

                // Use our own structure
                RowsPerBeat = column.mpeColumn.BeatRowsList.Count > index ? column.mpeColumn.BeatRowsList[index] : 4;

                // if (!column.PatternColumn.BeatSubdivision.TryGetValue(index, out RowsPerBeat))
                //	RowsPerBeat = rpb;

                rowTimes = Enumerable.Range(0, RowsPerBeat).Select(t => ClipEvent.TimeBase * 4 * t / RowsPerBeat).ToArray();
            }
        }

        public virtual ColumnRenderer.ColumnType Type
        {
            get
            {
                if (ClipColumn.Parameter.Type == ParameterType.Note)
                    return ColumnRenderer.ColumnType.Note;
                else
                    return ClipColumn.Parameter.Flags.HasFlag(ParameterFlags.Ascii)
                        ? ColumnRenderer.ColumnType.Ascii : ColumnRenderer.ColumnType.HexValue;
            }
        }

        // Fork (ux-overhaul.md P3c): byte, word and switch values in decimal when ValueDisplay says so: the digits of the
        // parameter's largest value (0 for a column that stays hex: notes, ASCII, negative minimums).
        int decimalDigits = -1;
        string decimalFormat = "D1";

        void SetUpDecimal()
        {
            var p = ClipColumn.Parameter;
            int digits = 0;
            if (p.Type != ParameterType.Note && !p.Flags.HasFlag(ParameterFlags.Ascii) && p.MinValue >= 0 && p.MaxValue > 0)
                digits = ValueDisplay.DecimalDigits(p.MaxValue);
            decimalDigits = digits;
            decimalFormat = "D" + Math.Max(1, digits);
        }

        /// <summary>Whether the column shows its values in decimal now (and takes decimal digits).</summary>
        internal virtual bool IsDecimal
        {
            get
            {
                if (ValueDisplay.IsHex) return false;
                if (decimalDigits < 0) SetUpDecimal();
                return decimalDigits > 0;
            }
        }

        /// <summary>A drum machine's short name of a Buzz note value, or null (the pitch).</summary>
        internal string NoteName(int buzzNote)
        {
            if (!(buzzNote >= BuzzNote.Min && buzzNote <= BuzzNote.Max)) return null;
            var names = MachineNoteNames.For(ClipColumn.Machine);
            return names?.ShortName(BuzzNote.ToMIDINote(buzzNote));
        }

        /// <summary>The machine's full name for a note value ("Kick"), or null.</summary>
        internal string NoteFullName(int buzzNote)
        {
            if (!(buzzNote >= BuzzNote.Min && buzzNote <= BuzzNote.Max)) return null;
            return MachineNoteNames.For(ClipColumn.Machine)?.Name(BuzzNote.ToMIDINote(buzzNote));
        }

        public virtual int DigitCount
        {
            get
            {
                if (IsDecimal) return decimalDigits;
                switch (ClipColumn.Parameter.Type)
                {
                    case ParameterType.Note: return 3;
                    case ParameterType.Word: return 4;
                    case ParameterType.Switch: return 1;
                    default: return ClipColumn.Parameter.Flags.HasFlag(ParameterFlags.Ascii) ? 1 : 2;
                }
            }
        }

        public virtual bool TiedToNext
        {
            get
            {
                return ClipColumn.Parameter.Flags.HasFlag(ParameterFlags.TiedToNext);
            }
        }

        public virtual string Label { get { return ClipColumn.Parameter.Name; } }
        public virtual string Description { get { return ClipColumn.Parameter.Description; } }
        public virtual ColumnRenderer.IBeat FetchBeat(int index) { return new Beat(this, index); }

        /// <summary>Fork (daw-modes.md AR3): a column that isn't one of the editor's own (the Notes lanes, NoteLaneColumn): no editor column behind it.</summary>
        protected ParameterColumn(ParameterColumnSet set, IClipColumn pc, int track)
        {
            BeatDuration = 4 * ClipEvent.TimeBase;
            this.Set = set;
            this.ClipColumn = pc;
            this.track = track;
        }

        public ParameterColumn(ParameterColumnSet set, IClipColumn pc, int track, int rpb)
        {
            BeatDuration = 4 * ClipEvent.TimeBase;
            this.Set = set;
            this.ClipColumn = pc;
            this.track = track;
            MPEClip mPEClip = Set.Clip.Editor.MPEClipsDB.GetMPEClip(pc.Clip);
            this.mpeColumn = mPEClip.GetColumnOrCreate(ClipColumn);
            mpeColumn.EventsChanged += pc_EventsChanged;
            pc.BeatSubdivisionChanged += pc_BeatSubdivisionChanged;
        }

        public virtual void Release()
        {
            // PatternColumn.EventsChanged -= pc_EventsChanged;
            mpeColumn.EventsChanged -= pc_EventsChanged;
            ClipColumn.BeatSubdivisionChanged -= pc_BeatSubdivisionChanged;
        }

        void pc_EventsChanged(IEnumerable<ClipEvent> events, bool setorclear)
        {
            HashSet<int> hs = new HashSet<int>();
            var invalidbeats = events.Select(e => e.Time / (ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase));
            foreach (var e in invalidbeats)
                hs.Add(e);
            Set.FireBeatsInvalidated(hs);
        }

        void pc_BeatSubdivisionChanged(int index)
        {
            var invalidbeats = new HashSet<int>();
            invalidbeats.Add(index);
            Set.FireBeatsInvalidated(invalidbeats);
            Set.Clip.CursorPosition = Set.Clip.CursorPosition.Constrained;
        }

        public virtual int GetDigitTime(Digit d)
        {
            var beat = FetchBeat(d.Beat) as Beat;
            return beat.GetRowTime(d.RowInBeat);
        }

        public virtual IEnumerable<BuzzAction> EditDigit(Digit d, int newvalue)
        {
            var beat = FetchBeat(d.Beat) as Beat;

            if (newvalue < 0)
            {
                return LinqExtensions.Return(beat.ClearValue(d.RowInBeat));
            }
            else
            {
                int value;

                if (DigitCount == 3 && Type == ColumnRenderer.ColumnType.Note)
                {
                    if (d.Index == 0)
                        value = newvalue;
                    else if (beat.HasValue(d.RowInBeat) && beat.Rows[d.RowInBeat].Value != BuzzNote.Off)
                    {
                        // Fork: the octave digit as the notes are named (ux-overhaul.md §3.5): 3 is Buzz's 5 when C3 is 60.
                        int octave = NoteNames.BuzzOctave(newvalue);
                        if (octave < BuzzNote.MinOctave || octave > BuzzNote.MaxOctave) return null;
                        value = (beat.Rows[d.RowInBeat].Value & 15) | (octave << 4);
                    }
                    else
                        return null;
                }
                else if (IsDecimal)
                {
                    // Fork (ux-overhaul.md P3c, decision 51): the digit at the cursor is replaced by the one typed, the others
                    // kept (a column with no value counts as zeros); a value past the parameter's largest is the largest.
                    var digits = (beat.HasValue(d.RowInBeat) ? beat.Rows[d.RowInBeat].Value : 0).ToString(decimalFormat).ToCharArray();
                    if (d.Index < 0 || d.Index >= digits.Length) return null;
                    digits[d.Index] = (char)('0' + newvalue);
                    value = int.Parse(new string(digits), System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (DigitCount == 2)
                {
                    if (beat.HasValue(d.RowInBeat))
                        value = (beat.Rows[d.RowInBeat].Value & (0x0f << (d.Index * 4))) | (newvalue << ((d.Index ^ 1) * 4));
                    else
                        value = (newvalue << ((d.Index ^ 1) * 4));
                }
                else if (DigitCount == 4)
                {
                    if (beat.HasValue(d.RowInBeat))
                        value = (beat.Rows[d.RowInBeat].Value & (~(0x0f << ((3 - d.Index) * 4)))) | (newvalue << ((3 - d.Index) * 4));
                    else
                        value = (newvalue << ((3 - d.Index) * 4));
                }
                else if (DigitCount == 1)
                {
                    value = newvalue;
                }
                else
                {
                    return null;
                }

                return LinqExtensions.Return(beat.SetValue(d.RowInBeat, value));
            }
        }

        public virtual IEnumerable<BuzzAction> EditValue(Digit d, int newvalue)
        {
            var beat = FetchBeat(d.Beat) as Beat;

            if (newvalue < 0)
                return LinqExtensions.Return(beat.ClearValue(d.RowInBeat));
            else
                return LinqExtensions.Return(beat.SetValue(d.RowInBeat, newvalue));
        }

        public virtual void SendCCAtTime(int time)
        {
            int ibeat = time / BeatDuration;
            if (ibeat < 0 || ibeat >= Set.BeatCount) return;
            var beat = FetchBeat(ibeat) as Beat;
            beat.SendCCAtTime(time % BeatDuration);
        }

        public virtual Digit GetDigitAtTime(int time)
        {
            Digit digit = new Digit(Set.Clip, Set.Clip.ColumnSets.IndexOf(Set), this.Set.Columns.IndexOf(this), 0, 0, 0);
            int ibeat = time / BeatDuration;
            if (ibeat < 0 || ibeat >= Set.BeatCount) return digit;
            var beat = FetchBeat(ibeat) as Beat;
            digit = digit.SetBeat(ibeat);
            int rowTime = BeatDuration / beat.Rows.Count;
            digit = digit.SetRowInBeat((time - ibeat * BeatDuration) / rowTime);
            return digit;
        }

    }
}
