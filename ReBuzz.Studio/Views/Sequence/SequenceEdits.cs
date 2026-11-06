// Fork (docs/specs/studio-mvp.md S4): the sequence editor's edits, as undo steps on the song's
// history. The portable BuzzGUI.Common has SetEventAction, ClearAction, SetMarkerAction,
// CreatePatternAction and AddSequenceAction; the others the WPF editor uses are ported here,
// since their BuzzGUI.Common copies are in its Windows build only (System.Windows.Rect):
// InsertOrDelete, SwapSequences, DeleteSequence, SetLength, the clipboard and its cut, copy and
// paste, and the horizontal editor's time signature actions. SequenceEditing holds the commands
// the view runs (SequenceEditor.xaml.cs, HoldDragHelper, PatternResizeHelper), pure of UI.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Common.Actions;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Common.Actions.SequenceActions;
using BuzzGUI.Common.Actions.SongActions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using BuzzGUI.SequenceEditor.Actions;
using ReBuzz.Core;

namespace ReBuzz.Studio.Views.Sequence
{
    /// <summary>Inserts (or deletes) a span of a track, moving what follows (BuzzGUI.SequenceEditor.Actions.InsertOrDeleteAction).</summary>
    internal sealed class InsertOrDeleteAction : SequenceAction
    {
        Tuple<int, EventRef>[] events;
        readonly int time;
        readonly int span;
        readonly bool insert;

        public InsertOrDeleteAction(ISequence s, int t, int sp, bool ins) : base(s)
        {
            time = t;
            span = sp;
            insert = ins;
        }

        protected override void DoAction()
        {
            var seq = Sequence;
            events = seq.Events.Select(e => Tuple.Create(e.Key, new EventRef(e.Value))).ToArray();
            if (insert) seq.Insert(time, span);
            else seq.Delete(time, span);
        }

        protected override void UndoAction()
        {
            var seq = Sequence;
            seq.Clear(0, int.MaxValue);
            foreach (var e in events) e.Item2.Set(seq, e.Item1);
        }
    }

    /// <summary>Swaps two tracks (BuzzGUI.Common SwapSequencesAction).</summary>
    internal sealed class SwapSequencesAction : SongAction
    {
        readonly int a;
        readonly int b;

        public SwapSequencesAction(ISong song, int a, int b) : base(song)
        {
            this.a = a;
            this.b = b;
        }

        protected override void DoAction()
        {
            if (a >= Song.Sequences.Count || b >= Song.Sequences.Count) return;
            Song.SwapSequences(Song.Sequences[a], Song.Sequences[b]);
        }

        protected override void UndoAction() => DoAction();
    }

    /// <summary>Removes a track; undo brings it back with its events (BuzzGUI.Common DeleteSequenceAction).</summary>
    internal sealed class DeleteSequenceAction : MachineAction
    {
        readonly ISong song;
        readonly int seqIndex;
        Tuple<int, EventRef>[] events;
        ISequence deleted;

        public DeleteSequenceAction(ISequence s) : base(s.Machine)
        {
            song = s.Machine.Graph.Buzz.Song;
            seqIndex = song.Sequences.IndexOf(s);
        }

        protected override void DoAction()
        {
            if (seqIndex >= song.Sequences.Count) return;
            var seq = song.Sequences[seqIndex];
            events = seq.Events.Select(e => Tuple.Create(e.Key, new EventRef(e.Value))).ToArray();
            deleted = seq;
            song.RemoveSequence(seq);
        }

        protected override void UndoAction()
        {
            var mac = Machine;
            song.AddSequence(mac, seqIndex);
            var seq = song.Sequences[seqIndex];
            foreach (var e in events) e.Item2.Set(seq, e.Item1);
            Restorations.Report(deleted, seq);
        }
    }

    /// <summary>Sets a pattern's length (BuzzGUI.Common SetLengthAction).</summary>
    internal sealed class SetLengthAction : MachineAction
    {
        readonly string patternName;
        readonly int newLength;
        readonly int oldLength;

        public SetLengthAction(IPattern pattern, int length) : base(pattern.Machine)
        {
            patternName = pattern.Name;
            newLength = length;
            oldLength = pattern.Length;
        }

        IPattern Pattern => Machine.Patterns.FirstOrDefault(x => x.Name == patternName) ?? throw new ActionException(this);

        protected override void DoAction() => Pattern.Length = newLength;

        protected override void UndoAction() => Pattern.Length = oldLength;
    }

    /// <summary>
    /// Events cut or copied from a block of tracks, kept by pattern name (BuzzGUI.Common
    /// SequenceClipboard, with ints for the rectangle).
    /// </summary>
    internal sealed class SequenceClipboard
    {
        List<SortedDictionary<int, EventRef>> data;

        public bool ContainsData => data != null;
        public int FirstTrack { get; private set; }
        public int RowCount => data?.Count ?? 0;
        public int Span { get; private set; }

        public void Clone(SequenceClipboard c)
        {
            data = c.data?.Select(d => new SortedDictionary<int, EventRef>(d)).ToList();
            FirstTrack = c.FirstTrack;
            Span = c.Span;
        }

        public void Clear() => data = null;

        public void Cut(ISong song, SequenceRect r) => CutOrCopy(song, r, true);

        public void Copy(ISong song, SequenceRect r) => CutOrCopy(song, r, false);

        /// <summary>Pastes at a time, from track <paramref name="firstTrack"/> down (the WPF editor: the tracks it came from).</summary>
        public void Paste(ISong song, int time, int firstTrack)
        {
            if (!ContainsData) return;
            for (int t = 0; t < data.Count; t++)
            {
                if (t + firstTrack >= song.Sequences.Count) return;
                var seq = song.Sequences[t + firstTrack];
                seq.Clear(time, Span);
                foreach (var e in data[t]) e.Value.Set(seq, time + e.Key);
            }
        }

        void CutOrCopy(ISong song, SequenceRect r, bool cut)
        {
            FirstTrack = r.Row;
            Span = r.Span;
            data = new List<SortedDictionary<int, EventRef>>();
            for (int t = r.Row; t <= r.LastRow; t++)
            {
                if (t >= song.Sequences.Count) return;
                var d = new SortedDictionary<int, EventRef>();
                foreach (var e in song.Sequences[t].Events.Where(e => e.Key >= r.Time && e.Key < r.End))
                    d[e.Key - r.Time] = new EventRef(e.Value);
                data.Add(d);
                if (cut) song.Sequences[t].Clear(r.Time, r.Span);
            }
        }
    }

    /// <summary>Cuts a block of events to the clipboard; undo puts them back (BuzzGUI.Common CutOrCopySequenceEventsAction).</summary>
    internal sealed class CutSequenceEventsAction : SongAction
    {
        readonly SequenceRect rect;
        readonly SequenceClipboard clipboard;
        readonly SequenceClipboard oldClipboard = new SequenceClipboard();
        readonly SequenceClipboard oldEvents = new SequenceClipboard();

        public CutSequenceEventsAction(ISong song, SequenceRect r, SequenceClipboard clipboard) : base(song)
        {
            rect = r;
            this.clipboard = clipboard;
        }

        protected override void DoAction()
        {
            oldClipboard.Clone(clipboard);
            oldEvents.Copy(Song, rect);
            clipboard.Cut(Song, rect);
        }

        protected override void UndoAction()
        {
            oldEvents.Paste(Song, rect.Time, rect.Row);
            clipboard.Clone(oldClipboard);
        }
    }

    /// <summary>Pastes the clipboard at a time and track; undo restores what was there (BuzzGUI.Common PasteSequenceEventsAction).</summary>
    internal sealed class PasteSequenceEventsAction : SongAction
    {
        readonly int time;
        readonly int row;
        readonly SequenceClipboard clipboard;
        readonly SequenceClipboard pasted = new SequenceClipboard();
        readonly SequenceClipboard oldEvents = new SequenceClipboard();

        public PasteSequenceEventsAction(ISong song, int time, int row, SequenceClipboard clipboard) : base(song)
        {
            this.time = time;
            this.row = row;
            this.clipboard = clipboard;
        }

        protected override void DoAction()
        {
            if (!clipboard.ContainsData && !pasted.ContainsData) return;
            if (!pasted.ContainsData) pasted.Clone(clipboard);     // a redo pastes what was pasted
            oldEvents.Copy(Song, new SequenceRect(time, row, pasted.Span, pasted.RowCount));
            pasted.Paste(Song, time, row);
        }

        protected override void UndoAction() => oldEvents.Paste(Song, time, row);
    }

    /// <summary>Sets the bar length from a time on (the horizontal editor's SetTimeSignatureAction).</summary>
    internal sealed class SetTimeSignatureAction : IAction
    {
        readonly SongMeter tsl;
        readonly SongMeter old;
        readonly int time;
        readonly int step;

        public SetTimeSignatureAction(SongMeter tsl, int time, int step)
        {
            this.tsl = tsl;
            old = new SongMeter(tsl);
            this.time = time;
            this.step = step;
        }

        public void Do() => tsl.Set(time, step);

        public void Undo() => tsl.Clone(old);
    }

    /// <summary>Inserts or deletes time in the bar list (TSLInsertOrDeleteAction).</summary>
    internal sealed class TimeSignatureInsertOrDeleteAction : IAction
    {
        readonly SongMeter tsl;
        readonly SongMeter old;
        readonly int time;
        readonly int span;
        readonly bool insert;

        public TimeSignatureInsertOrDeleteAction(SongMeter tsl, int time, int span, bool insert)
        {
            this.tsl = tsl;
            old = new SongMeter(tsl);
            this.time = time;
            this.span = span;
            this.insert = insert;
        }

        public void Do()
        {
            if (insert) tsl.Insert(time, span);
            else tsl.Delete(time, span);
        }

        public void Undo() => tsl.Clone(old);
    }

    /// <summary>
    /// The editor's commands on the song, each one undo step on the song's history (the shell's
    /// Edit ▸ Undo). From SequenceEditor.xaml.cs's commands, HoldDragHelper (move) and the resize
    /// handling; times are ticks, rows are track indices.
    /// </summary>
    internal sealed class SequenceEditing
    {
        readonly ISong song;
        readonly IActionStack stack;
        readonly SongMeter tsl;
        readonly SequenceClipboard clipboard;

        public SequenceEditing(ISong song, IActionStack stack, SongMeter tsl, SequenceClipboard clipboard)
        {
            this.song = song;
            this.stack = stack;
            this.tsl = tsl;
            this.clipboard = clipboard;
        }

        public ISong Song => song;
        public SequenceClipboard Clipboard => clipboard;

        ISequence Track(int row) => row >= 0 && row < song.Sequences.Count ? song.Sequences[row] : null;

        void Do(IAction a)
        {
            try
            {
                stack.Do(a);
            }
            catch (Exception e)
            {
                // The WPF editor swallows these too (Do: catch {}); the log says what failed.
                Global.Buzz?.DCWriteLine("Sequence editor: " + e.Message);
            }
        }

        /// <summary>Several actions as one undo step.</summary>
        void Group(Action body)
        {
            stack.BeginActionGroup();
            try
            {
                body();
            }
            finally
            {
                stack.EndActionGroup();
            }
        }

        /// <summary>Puts an event (a pattern, or a break, mute, thru or stop) on a track.</summary>
        public void SetEvent(int row, int time, SequenceEvent e)
        {
            var seq = Track(row);
            if (seq != null) Do(new SetEventAction(seq, time, e));
        }

        public void PlacePattern(int row, int time, IPattern pattern) =>
            SetEvent(row, time, new SequenceEvent(SequenceEventType.PlayPattern, pattern));

        /// <summary>A new pattern of <paramref name="length"/> ticks, placed (a double click on an empty cell).</summary>
        public IPattern NewPatternAt(int row, int time, int length)
        {
            var seq = Track(row);
            if (seq == null) return null;
            IPattern made = null;
            Group(() =>
            {
                string name = seq.Machine.GetNewPatternName();
                Do(new CreatePatternAction(seq.Machine, name, length));
                made = seq.Machine.Patterns.FirstOrDefault(p => p.Name == name);
                if (made != null) Do(new SetEventAction(seq, time, new SequenceEvent(SequenceEventType.PlayPattern, made)));
            });
            return made;
        }

        /// <summary>Quick Create Patterns: <paramref name="count"/> new patterns in a row from the cursor, over what was there (or inserted).</summary>
        public void CreatePatterns(int row, int time, int count, int length, bool insert)
        {
            var seq = Track(row);
            if (seq == null || count <= 0) return;
            Group(() =>
            {
                int span = count * length;
                if (!insert) Do(new InsertOrDeleteAction(seq, time, span, false));
                Do(new InsertOrDeleteAction(seq, time, span, true));
                for (int i = 0; i < count; i++)
                {
                    string name = seq.Machine.GetNewPatternName();
                    Do(new CreatePatternAction(seq.Machine, name, length));
                    var p = seq.Machine.Patterns.FirstOrDefault(x => x.Name == name);
                    if (p != null) Do(new SetEventAction(seq, time + i * length, new SequenceEvent(SequenceEventType.PlayPattern, p)));
                }
            });
        }

        /// <summary>Moves (or with <paramref name="clone"/> copies) the event at <paramref name="from"/> to <paramref name="to"/> on its track.</summary>
        public bool MoveEvent(int row, int from, int to, bool clone)
        {
            var seq = Track(row);
            if (seq == null || from == to || to < 0 || !seq.Events.TryGetValue(from, out var e)) return false;
            var copy = new SequenceEvent(e);
            Group(() =>
            {
                if (!clone) Do(new ClearAction(seq, from, 1));
                Do(new SetEventAction(seq, to, copy));
            });
            return true;
        }

        /// <summary>A right-edge resize: the pattern's new length (all its blocks change).</summary>
        public void SetPatternLength(IPattern pattern, int length)
        {
            if (pattern == null || length <= 0 || length == pattern.Length) return;
            Do(new SetLengthAction(pattern, length));
        }

        /// <summary>A left-edge resize: the event starts at <paramref name="newTime"/> and its pattern ends where the block did.</summary>
        public void ResizeLeft(int row, int time, int newTime, int newLength)
        {
            var seq = Track(row);
            if (seq == null || !seq.Events.TryGetValue(time, out var e) || e.Pattern == null) return;
            if (newTime == time && newLength == e.Pattern.Length) return;
            var copy = new SequenceEvent(e);
            var pattern = e.Pattern;
            Group(() =>
            {
                Do(new ClearAction(seq, time, GeometrySpan(e)));
                Do(new SetEventAction(seq, newTime, copy));
                Do(new SetLengthAction(pattern, newLength));
            });
        }

        static int GeometrySpan(SequenceEvent e) => SequenceGeometry.SpanOf(e);

        /// <summary>Clears a span of a track ('.' and Backspace).</summary>
        public void Clear(int row, int time, int span)
        {
            var seq = Track(row);
            if (seq != null) Do(new ClearAction(seq, time, span));
        }

        /// <summary>Clears every event that starts in a block of tracks (Delete on a selection).</summary>
        public void Clear(SequenceRect r) => Group(() =>
        {
            for (int row = r.Row; row <= r.LastRow; row++)
            {
                var seq = Track(row);
                if (seq != null && seq.Events.Keys.Any(k => k >= r.Time && k < r.End)) Do(new ClearAction(seq, r.Time, r.Span));
            }
        });

        /// <summary>Insert (or Delete) at the cursor: the track's events after it move by a bar.</summary>
        public void InsertOrDelete(int row, int time, int span, bool insert)
        {
            var seq = Track(row);
            if (seq != null) Do(new InsertOrDeleteAction(seq, time, span, insert));
        }

        /// <summary>Insert (All Tracks) / Delete (All Tracks): every track, the bar list and the markers after the cursor's bar move.</summary>
        public void InsertOrDeleteAll(int time, int span, bool insert)
        {
            int bottom = time + span;
            Group(() =>
            {
                Do(new TimeSignatureInsertOrDeleteAction(tsl, time, span, insert));
                foreach (var s in song.Sequences.ToList())
                    Do(new InsertOrDeleteAction(s, time, span, insert));
                if (insert)
                {
                    if (song.SongEnd >= bottom) Do(new SetMarkerAction(song, SongMarkers.SongEnd, song.SongEnd + span));
                    if (song.LoopEnd >= bottom) Do(new SetMarkerAction(song, SongMarkers.LoopEnd, song.LoopEnd + span));
                    if (song.LoopStart >= bottom) Do(new SetMarkerAction(song, SongMarkers.LoopStart, song.LoopStart + span));
                }
                else
                {
                    if (song.LoopEnd > bottom) Do(new SetMarkerAction(song, SongMarkers.LoopEnd, song.LoopEnd - span));
                    if (song.SongEnd > bottom) Do(new SetMarkerAction(song, SongMarkers.SongEnd, song.SongEnd - span));
                    if (song.LoopStart > bottom) Do(new SetMarkerAction(song, SongMarkers.LoopStart, song.LoopStart - span));
                }
            });
        }

        /// <summary>Set Start (Ctrl+B): the loop starts at the cursor.</summary>
        public void SetLoopStart(int time) => Do(new SetMarkerAction(song, SongMarkers.LoopStart, time));

        /// <summary>Set End (Ctrl+E): the loop ends at the cursor's bar end; again there, the song ends there.</summary>
        public void SetEnd(int time)
        {
            if (song.LoopEnd != time) Do(new SetMarkerAction(song, SongMarkers.LoopEnd, time));
            else Do(new SetMarkerAction(song, SongMarkers.SongEnd, time));
        }

        public void SetMarker(SongMarkers marker, int time) => Do(new SetMarkerAction(song, marker, time));

        public void SetTimeSignature(int time, int step)
        {
            if (step > 0) Do(new SetTimeSignatureAction(tsl, time, step));
        }

        public void Cut(SequenceRect r) => Do(new CutSequenceEventsAction(song, r, clipboard));

        /// <summary>Copy isn't an undo step here (WPF's is, though it changes nothing in the song).</summary>
        public void Copy(SequenceRect r) => clipboard.Copy(song, r);

        public bool CanPaste => clipboard.ContainsData;

        public void Paste(int time, int row)
        {
            if (clipboard.ContainsData && row >= 0 && row < song.Sequences.Count) Do(new PasteSequenceEventsAction(song, time, row, clipboard));
        }

        /// <summary>
        /// Fork (ux-overhaul.md §4): Duplicate (⌘D): the block again right after itself, one undo step. The user's clipboard
        /// is left as it was.
        /// </summary>
        public void Duplicate(SequenceRect r)
        {
            if (r.Row < 0 || r.Row >= song.Sequences.Count || r.Span <= 0) return;
            var copy = new SequenceClipboard();
            copy.Copy(song, r);
            Do(new PasteSequenceEventsAction(song, r.End, r.Row, copy));
        }

        public void AddTrack(IMachine machine)
        {
            if (machine != null) Do(new AddSequenceAction(machine));
        }

        public void DeleteTrack(int row)
        {
            var seq = Track(row);
            if (seq != null) Do(new DeleteSequenceAction(seq));
        }

        /// <summary>Move Track Up/Down (Ctrl+Up/Down).</summary>
        public bool MoveTrack(int row, int delta)
        {
            int to = row + delta;
            if (Track(row) == null || Track(to) == null) return false;
            Do(new SwapSequencesAction(song, row, to));
            return true;
        }
    }
}
