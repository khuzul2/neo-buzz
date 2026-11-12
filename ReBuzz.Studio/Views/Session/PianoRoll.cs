// Fork (docs/specs/studio-mvp.md S5): the Session View's piano roll, ported from the WPF view's
// PianoRoll.cs (session-view.md 0.7.1): a clip's notes, the note-ons of its engine MIDI column,
// drawn and edited Live's way. Until the pattern editor comes (S6), this is where the studio
// writes notes. Double-click adds or deletes a note; drag moves, drag a note's end resizes it,
// Alt+drag changes velocity, drag on empty space selects. Arrows move the selection (Shift: an
// octave), Q quantizes, Del deletes, Ctrl+A selects all (Command on macOS). Every edit is one undo
// step of the session (SessionLayout.EditNotes). The notes come through a note source
// (daw-modes.md S4, ReBuzz/Core/Notes): the session's here; other hosts pass theirs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;

namespace ReBuzz.Studio.Views.Session
{
    using Control = Avalonia.Controls.Control;

    internal sealed class PianoRoll : Control
    {
        public const double KeysWidth = 46;
        const double EdgeGrab = 6;
        const double DragDistance = 4;

        enum Drag { None, Move, Resize, Velocity, Box, Key }

        readonly ISession session;
        readonly ReBuzzCore buzz;
        List<ClipNote> notes = new List<ClipNote>();
        readonly HashSet<int> selected = new HashSet<int>();

        Drag drag;
        Point down;
        int downTime, downNote, keyNote = -1;
        List<ClipNote> dragFrom;
        List<ClipNote> dragTo;
        bool dragging;
        Rect? box;
        int lastVelocity = 100;
        int lastDuration;

        static readonly Cursor SizeWE = new Cursor(StandardCursorType.SizeWestEast);
        static readonly Cursor SizeAll = new Cursor(StandardCursorType.SizeAll);

        /// <summary>A session clip's piano roll: its notes on the session's undo history.</summary>
        public PianoRoll(ReBuzzCore buzz, SessionLayout layout, ISequence track, IPattern clip)
            : this(buzz, new SessionClipNotes(layout, clip), track)
        {
        }

        /// <summary>A piano roll on any note source; the play position shows while <paramref name="track"/> plays the pattern in the session.</summary>
        public PianoRoll(ReBuzzCore buzz, INoteSource source, ISequence track)
        {
            this.buzz = buzz;
            Source = source;
            session = buzz.Session;
            Track = track;
            Focusable = true;
            FocusAdorner = null;
            ClipToBounds = true;
            Reload();
        }

        public ISequence Track { get; }

        /// <summary>Where the notes come from and go to (daw-modes.md S4).</summary>
        internal INoteSource Source { get; }

        /// <summary>The clip, as it stands now: the session's undo may have made it again (0.7.1 hardening).</summary>
        public IPattern Pattern => Source.Pattern;

        /// <summary>Whether the clip is in the song (not deleted, or brought back by an undo).</summary>
        public bool IsLive => Source.IsLive;

        /// <summary>Whether the clip can't be edited now though it is in the song: it records (edits wait until it stops).</summary>
        public bool IsRecording => Source.IsLive && Source.WhyNotEditable != null;

        /// <summary>Snap grid in pattern time (1/240 tick); 1 for none.</summary>
        public int Grid { get; set; } = ClipNotes.TimeBase;

        /// <summary>Horizontal zoom: pixels per tick.</summary>
        public double TickWidth { get; set; } = 24;

        public double RowHeight { get; set; } = 11;

        /// <summary>
        /// Fork (ux-overhaul.md P3b): the part of the roll the viewer shows, in this control's coordinates (the host sets it as
        /// the scroll viewer scrolls). Only that part is drawn, and the keys stay at its left edge; empty: all of it.
        /// </summary>
        public Rect Viewport { get; set; }

        /// <summary>
        /// Fork (P3b): Alt held bypasses the grid (the docked roll; Shift does too). Off in the Session View's roll, where Alt+drag
        /// changes a note's velocity.
        /// </summary>
        public bool AltBypassesGrid { get; set; }

        Rect Visible => Viewport.Width > 0 && Viewport.Height > 0 ? Viewport : new Rect(0, 0, Math.Max(Bounds.Width, X(Length) + 40), 128 * RowHeight);

        double KeysLeft => Viewport.Width > 0 ? Viewport.X : 0;

        int GridFor(KeyModifiers mods) => mods.HasFlag(KeyModifiers.Shift) || (AltBypassesGrid && mods.HasFlag(KeyModifiers.Alt)) ? 1 : Grid;

        internal IReadOnlyList<ClipNote> Notes => notes;
        internal IReadOnlyCollection<int> Selection => selected;

        /// <summary>Raised after the notes or the selection changed (the window shows a summary).</summary>
        public event Action Changed;

        int Length => Source.Length;

        int BeatTime => Math.Max(1, buzz.TPB) * ClipNotes.TimeBase;

        /// <summary>
        /// Fork (ux-overhaul.md P3b2): the bar's length in ticks when the host knows the song's (the docked roll takes the
        /// bar of the song's list where its pattern lies); 0: the Session View's beats a bar.
        /// </summary>
        public int BarTicks { get; set; }

        internal int BarTime => BarTicks > 0 ? BarTicks * ClipNotes.TimeBase : BeatTime * Math.Max(1, session.BeatsPerBar);

        internal int BeatLength => BeatTime;

        // ------------------------------------------------------------------ geometry

        internal double X(int time) => KeysWidth + time / (double)ClipNotes.TimeBase * TickWidth;

        internal double Y(int note) => (127 - note) * RowHeight;

        internal int TimeAt(double x) => (int)Math.Round(Math.Max(0, x - KeysWidth) / TickWidth * ClipNotes.TimeBase);

        int NoteAt(double y) => Math.Clamp(127 - (int)Math.Floor(y / RowHeight), 0, 127);

        Rect RectOf(ClipNote n) =>
            new Rect(X(n.Time), Y(n.Note) + 1, Math.Max(3, n.Duration / (double)ClipNotes.TimeBase * TickWidth), RowHeight - 2);

        protected override Size MeasureOverride(Size availableSize) =>
            new Size(X(Length) + 40, 128 * RowHeight);

        /// <summary>Scrolls a viewer to the notes (or middle C).</summary>
        public double FocusOffset()
        {
            int note = notes.Count > 0 ? (int)notes.Average(n => n.Note) : 60;
            return Math.Max(0, Y(note) - 160);
        }

        // ------------------------------------------------------------------ state

        /// <summary>Reads the clip's notes again (after an edit, an undo, a recording); keeps what is selected where it can.</summary>
        public void Reload()
        {
            var keep = selected.Where(i => i < notes.Count).Select(i => notes[i]).ToList();
            notes = Source.Notes();
            selected.Clear();
            foreach (var n in keep)
            {
                int i = notes.IndexOf(n);
                if (i >= 0) selected.Add(i);
            }
            InvalidateMeasure();
            InvalidateVisual();
            Changed?.Invoke();
        }

        /// <summary>Why the last edit was refused (the tracker has no track left for a chord…), or null.</summary>
        public string Refusal { get; private set; }

        void Commit(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added)
        {
            Refusal = Source.Edit(removed, added);
            if (Refusal != null)
            {
                Reload();                                    // nothing changes: show the notes as they are
                return;
            }
            notes = Source.Notes();
            selected.Clear();
            foreach (var n in added)
            {
                int i = notes.IndexOf(n);
                if (i >= 0) selected.Add(i);
            }
            InvalidateMeasure();
            InvalidateVisual();
            Changed?.Invoke();
        }

        List<ClipNote> SelectedNotes() => selected.OrderBy(i => i).Where(i => i < notes.Count).Select(i => notes[i]).ToList();

        // ------------------------------------------------------------------ commands (keys, buttons, tests)

        internal void AddNote(int time, int note, int? grid = null)
        {
            var n = ClipNotes.Draw(time, note, lastVelocity, grid ?? Grid, Length, lastDuration);
            Commit(Array.Empty<ClipNote>(), new[] { n });
        }

        /// <summary>Copies the selected notes right after themselves (the selection's span later); the copies are selected.</summary>
        internal void DuplicateSelected()
        {
            var from = SelectedNotes();
            if (from.Count == 0) return;
            int start = from.Min(n => n.Time), end = from.Max(n => n.End);
            int step = Grid > 1 ? (int)Math.Ceiling((end - start) / (double)Grid) * Grid : end - start;
            var copies = from.Select(n => n with { Time = n.Time + step }).Where(n => n.Time < Length).ToList();
            if (copies.Count > 0) Commit(Array.Empty<ClipNote>(), copies);
        }

        // ---- Fork (P3b2): the clipboard of notes. One per app, so a copy can be pasted in another pattern; the notes keep their pitch
        // and their distances in time (the first one at the paste time).

        static List<ClipNote> clipboard = new List<ClipNote>();

        /// <summary>The notes the last Copy or Cut took, the first at time 0 (tests).</summary>
        internal static IReadOnlyList<ClipNote> Clipboard => clipboard;

        internal static void ClearClipboard() => clipboard = new List<ClipNote>();

        /// <summary>Where the keyboard's paste goes when the pointer is elsewhere: the last click on the roll or its ruler.</summary>
        public int InsertTime { get; set; }

        /// <summary>Where the pointer is over the grid (pattern time), or -1.</summary>
        int hoverTime = -1;

        /// <summary>The pattern time the roll plays now (the host sets it), or -1: pasting goes there while it plays.</summary>
        public int PlayheadTime { get; set; } = -1;

        /// <summary>Copies the selected notes to the clipboard; false when none is selected.</summary>
        internal bool CopySelected()
        {
            var from = SelectedNotes();
            if (from.Count == 0) return false;
            int start = from.Min(n => n.Time);
            clipboard = from.Select(n => n with { Time = n.Time - start }).ToList();
            return true;
        }

        /// <summary>Copies the selected notes and deletes them (one undo step).</summary>
        internal bool CutSelected()
        {
            if (!CopySelected()) return false;
            DeleteSelected();
            return true;
        }

        /// <summary>
        /// Where a paste lands: under the pointer when it is over the grid, else at the playhead while the pattern plays, else at the
        /// last click; on the grid.
        /// </summary>
        internal int PasteTime()
        {
            int t = hoverTime >= 0 ? hoverTime : PlayheadTime >= 0 ? PlayheadTime : InsertTime;
            int grid = Math.Max(1, Grid);
            return Math.Max(0, (int)Math.Round(t / (double)grid) * grid);
        }

        /// <summary>Pastes the clipboard at <paramref name="at"/> (else <see cref="PasteTime"/>); the new notes are selected. One undo step.</summary>
        internal bool Paste(int? at = null)
        {
            if (clipboard.Count == 0) return false;
            int time = at ?? PasteTime();
            var added = clipboard.Select(n => n with { Time = n.Time + time }).Where(n => n.Time < Length).ToList();
            if (added.Count == 0) return false;
            added = added.Select(n => n.End > Length ? n with { Duration = Math.Max(1, Length - n.Time) } : n).ToList();
            Commit(Array.Empty<ClipNote>(), added);
            return Refusal == null;
        }

        /// <summary>Writes notes for others (the velocity lane): the one edit of a gesture, on the source's undo history.</summary>
        internal void ApplyEdit(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added) => Commit(removed, added);

        /// <summary>Selects exactly these notes by index (the velocity lane's click).</summary>
        internal void SelectOnly(IEnumerable<int> indexes)
        {
            selected.Clear();
            foreach (int i in indexes) if (i >= 0 && i < notes.Count) selected.Add(i);
            InvalidateVisual();
            Changed?.Invoke();
        }

        internal void ToggleSelect(int index)
        {
            if (index < 0 || index >= notes.Count) return;
            if (!selected.Remove(index)) selected.Add(index);
            InvalidateVisual();
            Changed?.Invoke();
        }

        /// <summary>Selects nothing.</summary>
        internal void DeselectAll()
        {
            selected.Clear();
            InvalidateVisual();
            Changed?.Invoke();
        }

        internal void SelectAll()
        {
            selected.Clear();
            for (int i = 0; i < notes.Count; i++) selected.Add(i);
            InvalidateVisual();
            Changed?.Invoke();
        }

        internal void DeleteSelected()
        {
            var gone = SelectedNotes();
            if (gone.Count > 0) Commit(gone, Array.Empty<ClipNote>());
        }

        /// <summary>Lengthens (or shortens) the selected notes by <paramref name="deltaTime"/>.</summary>
        internal void ResizeSelected(int deltaTime)
        {
            var from = SelectedNotes();
            if (from.Count > 0) Commit(from, ClipNotes.Resize(from, deltaTime, 1, Length));
        }

        internal void MoveSelected(int deltaTime, int deltaNote)
        {
            var from = SelectedNotes();
            if (from.Count > 0) Commit(from, ClipNotes.Move(from, deltaTime, deltaNote, 1, Length));
        }

        internal void ChangeVelocity(int delta)
        {
            var from = SelectedNotes();
            if (from.Count > 0) Commit(from, ClipNotes.Velocity(from, delta));
        }

        /// <summary>Quantizes the selection, or every note if none is selected.</summary>
        internal void Quantize(double strength = 1.0, bool ends = false)
        {
            var from = selected.Count > 0 ? SelectedNotes() : notes.ToList();
            if (from.Count == 0) return;
            int grid = Grid > 1 ? Grid : BeatTime / 4;
            Commit(from, ClipNotes.Quantize(from, grid, strength, ends, Length));
        }

        /// <summary>Where a note's cell is, for tests that click it: (time, note) to a point in this control.</summary>
        internal Point PointOf(int time, int note) => new Point(X(time) + 2, Y(note) + RowHeight / 2);

        // ------------------------------------------------------------------ drawing

        static readonly IBrush Back = SessionPalette.Make("#1E1E22");
        static readonly IBrush BlackKeyRow = SessionPalette.Make("#19191C");
        static readonly IBrush WhiteKey = SessionPalette.Make("#D9D9DE");
        static readonly IBrush BlackKey = SessionPalette.Make("#2A2A30");
        internal static readonly IBrush NoteFill = SessionPalette.Make("#E0457B");
        internal static readonly IBrush NoteSelected = SessionPalette.Make("#F0C24B");
        static readonly IBrush OutOfClip = SessionPalette.Make("#141417");
        static readonly IPen RowLine = new ImmutablePen(Color.Parse("#26262B").ToUInt32(), 1);
        static readonly IPen TickLine = new ImmutablePen(Color.Parse("#24242A").ToUInt32(), 1);
        static readonly IPen BeatLine = new ImmutablePen(Color.Parse("#34343C").ToUInt32(), 1);
        static readonly IPen BarLine = new ImmutablePen(Color.Parse("#55555F").ToUInt32(), 1);
        static readonly IPen NoteEdge = new ImmutablePen(Color.Parse("#101012").ToUInt32(), 1);
        static readonly IPen BoxPen = new ImmutablePen(Color.Parse("#F0C24B").ToUInt32(), 1, (ImmutableDashStyle)DashStyle.Dash.ToImmutable());
        static readonly IPen PlayheadPen = new ImmutablePen(Color.Parse("#6FD98A").ToUInt32(), 1.5);

        static bool IsBlack(int note) => (note % 12) is 1 or 3 or 6 or 8 or 10;

        public override void Render(DrawingContext dc)
        {
            // Only what the viewer shows (P3b): rows, time lines and notes outside it cost nothing.
            var vis = Visible;
            double width = Math.Max(Bounds.Width, X(Length) + 40);
            double height = 128 * RowHeight;
            double left = Math.Max(0, vis.X), right = Math.Min(width, vis.Right), top = Math.Max(0, vis.Y), bottom = Math.Min(height, vis.Bottom);
            dc.FillRectangle(Back, new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)));
            int noteHigh = Math.Clamp(127 - (int)Math.Floor(top / RowHeight), 0, 127);
            int noteLow = Math.Clamp(127 - (int)Math.Floor((bottom - 0.01) / RowHeight), 0, 127);
            double gridLeft = Math.Max(left, KeysWidth);

            // Rows: black-key rows darker, a line under each C.
            for (int note = noteLow; note <= noteHigh; note++)
            {
                double y = Y(note);
                if (IsBlack(note))
                    dc.FillRectangle(BlackKeyRow, new Rect(gridLeft, y, Math.Max(0, right - gridLeft), RowHeight));
                if (note % 12 == 0)
                    dc.DrawLine(RowLine, new Point(gridLeft, y + RowHeight), new Point(right, y + RowHeight));
            }

            // Past the clip's end.
            double end = X(Length);
            if (end < right)
                dc.FillRectangle(OutOfClip, new Rect(Math.Max(end, left), top, right - Math.Max(end, left), bottom - top));

            // Time lines: ticks when they are wide enough, beats, bars; those in view.
            int tickTime = ClipNotes.TimeBase;
            int firstTick = Math.Max(0, (int)Math.Floor((gridLeft - KeysWidth) / TickWidth));
            int lastTick = Math.Min(Length / tickTime, (int)Math.Ceiling((right - KeysWidth) / TickWidth));
            for (int k = firstTick; k <= lastTick; k++)
            {
                int t = k * tickTime;
                IPen pen = t % BarTime == 0 ? BarLine : t % BeatTime == 0 ? BeatLine : TickWidth >= 10 ? TickLine : null;
                if (pen == null) continue;
                double x = Math.Round(X(t)) + 0.5;
                dc.DrawLine(pen, new Point(x, top), new Point(x, bottom));
            }

            // Notes: the drag's preview instead of what it moves.
            HashSet<int> hidden = dragTo != null && dragFrom != null ? new HashSet<int>(selected) : null;
            for (int i = 0; i < notes.Count; i++)
            {
                if (hidden != null && hidden.Contains(i)) continue;
                if (!InView(notes[i], vis)) continue;
                DrawNote(dc, notes[i], selected.Contains(i));
            }
            if (dragTo != null)
                foreach (var n in dragTo)
                    DrawNote(dc, n, true);

            if (box is Rect b)
                dc.DrawRectangle(null, BoxPen, b.Normalize());

            // The play position, while the clip plays on its track.
            if (Track != null && Pattern != null && session.GetState(Track, Pattern) is ClipState.Playing or ClipState.Stopping)
            {
                int position = session.GetClipPosition(Track);
                if (position >= 0)
                {
                    double x = X(position * ClipNotes.TimeBase);
                    dc.DrawLine(PlayheadPen, new Point(x, top), new Point(x, bottom));
                }
            }

            // Keys, at the viewer's left edge.
            double keysX = KeysLeft;
            for (int note = noteLow; note <= noteHigh; note++)
            {
                double y = Y(note);
                bool black = IsBlack(note);
                dc.DrawRectangle(black ? BlackKey : WhiteKey, NoteEdge, new Rect(keysX, y, KeysWidth, RowHeight));
                if (note % 12 == 0 || note == keyNote)
                {
                    var text = new FormattedText(ClipNotes.Name(note), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        Typeface.Default, Math.Min(10, RowHeight - 1), black ? WhiteKey : BlackKey);
                    dc.DrawText(text, new Point(keysX + 3, y + (RowHeight - text.Height) / 2));
                }
            }
        }

        bool InView(ClipNote n, Rect vis)
        {
            var r = RectOf(n);
            return r.Right >= vis.X && r.X <= vis.Right && r.Bottom >= vis.Y && r.Y <= vis.Bottom;
        }

        /// <summary>How many notes the last redraw would draw in the current view (tests: only the visible region is drawn).</summary>
        internal int NotesInView() => notes.Count(n => InView(n, Visible));

        void DrawNote(DrawingContext dc, ClipNote n, bool isSelected)
        {
            var r = RectOf(n);
            var fill = isSelected ? NoteSelected : NoteFill;
            using (dc.PushOpacity(0.45 + 0.55 * n.Velocity / 127.0))
                dc.DrawRectangle(fill, NoteEdge, r);
        }

        // ------------------------------------------------------------------ pointer

        static bool Additive(KeyModifiers mods) => mods.HasFlag(KeyModifiers.Shift) || SessionView.Normalize(mods).HasFlag(KeyModifiers.Control);

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Focus();
            var p = e.GetPosition(this);
            down = p;
            downTime = TimeAt(p.X);
            if (p.X >= KeysLeft + KeysWidth) InsertTime = Math.Min(downTime, Math.Max(0, Length - 1));
            downNote = NoteAt(p.Y);
            var mods = e.KeyModifiers;
            bool additive = Additive(mods);

            if (p.X < KeysLeft + KeysWidth && p.X >= KeysLeft)
            {
                keyNote = downNote;
                Source.Preview(keyNote, lastVelocity, true);
                drag = Drag.Key;
                e.Pointer.Capture(this);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            int hit = ClipNotes.HitTest(notes, downTime, downNote);
            if (e.ClickCount == 2)
            {
                if (hit >= 0)
                {
                    selected.Clear();
                    Commit(new[] { notes[hit] }, Array.Empty<ClipNote>());
                }
                else if (downTime < Length)
                {
                    AddNote(downTime, downNote, GridFor(mods));
                    Source.Preview(downNote, lastVelocity, true);
                    Source.Preview(downNote, 0, false);
                }
                e.Handled = true;
                return;
            }

            if (hit >= 0)
            {
                if (additive)
                {
                    if (!selected.Remove(hit)) selected.Add(hit);
                }
                else if (!selected.Contains(hit))
                {
                    selected.Clear();
                    selected.Add(hit);
                }
                var r = RectOf(notes[hit]);
                drag = mods.HasFlag(KeyModifiers.Alt) && !AltBypassesGrid ? Drag.Velocity
                     : p.X >= r.Right - EdgeGrab ? Drag.Resize
                     : Drag.Move;
                dragFrom = SelectedNotes();
                lastVelocity = notes[hit].Velocity;
                lastDuration = notes[hit].Duration;
                if (drag == Drag.Move)
                {
                    keyNote = notes[hit].Note;
                    Source.Preview(keyNote, lastVelocity, true);
                }
            }
            else
            {
                if (!additive) selected.Clear();
                drag = Drag.Box;
                box = new Rect(p, p);
            }
            e.Pointer.Capture(this);
            InvalidateVisual();
            Changed?.Invoke();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            var mods = e.KeyModifiers;
            hoverTime = p.X >= KeysLeft + KeysWidth ? TimeAt(p.X) : -1;
            if (drag == Drag.None)
            {
                int hit = p.X >= KeysLeft + KeysWidth ? ClipNotes.HitTest(notes, TimeAt(p.X), NoteAt(p.Y)) : -1;
                Cursor = hit >= 0 && p.X >= RectOf(notes[hit]).Right - EdgeGrab ? SizeWE : hit >= 0 ? SizeAll : null;
                return;
            }
            // A click that wobbles a pixel is no drag (it would snap the note to the grid).
            if (!dragging && (drag == Drag.Move || drag == Drag.Resize || drag == Drag.Velocity))
            {
                if (Math.Abs(p.X - down.X) < DragDistance && Math.Abs(p.Y - down.Y) < DragDistance) return;
                dragging = true;
            }
            switch (drag)
            {
                case Drag.Move:
                    {
                        int dt = TimeAt(p.X) - downTime;
                        int dn = NoteAt(p.Y) - downNote;
                        dragTo = ClipNotes.Move(dragFrom, dt, dn, GridFor(mods), Length);
                        int heard = dragTo.Count > 0 ? dragTo[0].Note : -1;
                        if (heard != keyNote && heard >= 0)
                        {
                            Source.Preview(keyNote, 0, false);
                            keyNote = heard;
                            Source.Preview(keyNote, lastVelocity, true);
                        }
                        break;
                    }
                case Drag.Resize:
                    {
                        int dd = (int)Math.Round((p.X - down.X) / TickWidth * ClipNotes.TimeBase);
                        dragTo = ClipNotes.Resize(dragFrom, dd, GridFor(mods), Length);
                        break;
                    }
                case Drag.Velocity:
                    dragTo = ClipNotes.Velocity(dragFrom, (int)Math.Round((down.Y - p.Y) / 2));
                    break;
                case Drag.Box:
                    {
                        box = new Rect(down, p);
                        var b = box.Value.Normalize();
                        if (!Additive(mods)) selected.Clear();
                        foreach (int i in ClipNotes.InBox(notes, TimeAt(b.Left), TimeAt(b.Right), NoteAt(b.Bottom), NoteAt(b.Top)))
                            selected.Add(i);
                        break;
                    }
            }
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            EndDrag(commit: true);
            e.Handled = true;
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            hoverTime = -1;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            EndDrag(commit: false);
        }

        void EndDrag(bool commit)
        {
            if (drag == Drag.None) return;
            var mode = drag;
            drag = Drag.None;
            dragging = false;
            if (keyNote >= 0 && (mode == Drag.Key || mode == Drag.Move))
                Source.Preview(keyNote, 0, false);
            keyNote = -1;
            var from = dragFrom;
            var to = dragTo;
            dragFrom = null;
            dragTo = null;
            box = null;
            if (commit && from != null && to != null && !from.SequenceEqual(to))
            {
                if (mode == Drag.Velocity && to.Count > 0) lastVelocity = to[0].Velocity;
                if (mode == Drag.Resize && to.Count > 0) lastDuration = to[0].Duration;
                Commit(from, to);
            }
            else
            {
                InvalidateVisual();
                Changed?.Invoke();
            }
        }

        // ------------------------------------------------------------------ keys

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (HandleKey(e.Key, SessionView.Normalize(e.KeyModifiers)))
                e.Handled = true;
        }

        /// <summary>A key's command (Control is Command on macOS); returns whether it was used.</summary>
        internal bool HandleKey(Key key, KeyModifiers mods)
        {
            int step = Grid > 1 ? Grid : ClipNotes.TimeBase;
            switch (key)
            {
                case Key.Delete:
                case Key.Back:
                    DeleteSelected();
                    break;
                case Key.A when mods == KeyModifiers.Control:
                    SelectAll();
                    break;
                case Key.Left when mods.HasFlag(KeyModifiers.Shift):
                    ResizeSelected(-step);
                    break;
                case Key.Right when mods.HasFlag(KeyModifiers.Shift):
                    ResizeSelected(step);
                    break;
                case Key.Left:
                    MoveSelected(-step, 0);
                    break;
                case Key.Right:
                    MoveSelected(step, 0);
                    break;
                case Key.Up when mods.HasFlag(KeyModifiers.Control):
                    ChangeVelocity(8);
                    break;
                case Key.Down when mods.HasFlag(KeyModifiers.Control):
                    ChangeVelocity(-8);
                    break;
                case Key.Up:
                    MoveSelected(0, mods.HasFlag(KeyModifiers.Shift) ? 12 : 1);
                    break;
                case Key.Down:
                    MoveSelected(0, mods.HasFlag(KeyModifiers.Shift) ? -12 : -1);
                    break;
                case Key.Q:
                    Quantize();
                    break;
                case Key.X when mods == KeyModifiers.Control:
                    CutSelected();
                    break;
                case Key.C when mods == KeyModifiers.Control:
                    CopySelected();
                    break;
                case Key.V when mods == KeyModifiers.Control:
                    Paste();
                    break;
                case Key.Z when mods == KeyModifiers.Control:
                    Source.Undo();
                    Reload();
                    break;
                case Key.Y when mods == KeyModifiers.Control:
                case Key.Z when mods == (KeyModifiers.Control | KeyModifiers.Shift):
                    Source.Redo();
                    Reload();
                    break;
                case Key.Escape:
                    selected.Clear();
                    InvalidateVisual();
                    Changed?.Invoke();
                    break;
                default:
                    return false;
            }
            return true;
        }
    }

    /// <summary>A window with a clip's piano roll and its tools (0.7.1). One per clip.</summary>
    internal sealed class PianoRollWindow : Window
    {
        static readonly Dictionary<IPattern, PianoRollWindow> open = new Dictionary<IPattern, PianoRollWindow>();

        readonly PianoRoll roll;
        readonly TextBlock summary;
        readonly ScrollViewer scroll;
        readonly ComboBox gridBox;
        readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        readonly SessionLayout layout;
        (int Count, long Sum) seen;

        /// <summary>Shows the clip's piano roll (bringing an open one to the front).</summary>
        public static PianoRollWindow Show(Window owner, ReBuzzCore buzz, SessionLayout layout, ISequence track, IPattern clip)
        {
            if (clip == null) return null;
            var existing = open.Values.FirstOrDefault(w => ReferenceEquals(w.roll.Pattern, layout.Current(clip)));
            if (existing != null)
            {
                existing.Activate();
                return existing;
            }
            var window = new PianoRollWindow(buzz, layout, track, clip);
            open[clip] = window;
            window.Closed += (s, e) => open.Remove(clip);
            if (owner != null) window.Show(owner);
            else window.Show();
            return window;
        }

        /// <summary>Open piano roll windows (tests).</summary>
        internal static int OpenCount => open.Count;

        internal static IEnumerable<PianoRollWindow> OpenWindows => open.Values.ToList();

        internal PianoRollWindow(ReBuzzCore buzz, SessionLayout layout, ISequence track, IPattern clip)
        {
            this.layout = layout;
            Title = $"Notes — {clip.Machine?.Name} · {clip.Name}";
            Width = 960;
            Height = 560;
            Background = SessionPalette.Window;
            Foreground = SessionPalette.Text;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            roll = new PianoRoll(buzz, layout, track, clip);
            scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = roll,
                Background = SessionPalette.Window
            };
            scroll.AddHandler(PointerWheelChangedEvent, (s, e) =>
            {
                if (!SessionView.Normalize(e.KeyModifiers).HasFlag(KeyModifiers.Control)) return;
                Zoom(e.Delta.Y > 0 ? 1.25 : 0.8);
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
            Opened += (s, e) =>
            {
                scroll.Offset = new Vector(0, roll.FocusOffset());
                roll.Focus();
                timer.Start();
            };

            int beat = Math.Max(1, buzz.TPB) * ClipNotes.TimeBase;
            var grids = new (string Label, int Value)[]
            {
                ("Grid: beat", beat), ("Grid: 1/8", beat / 2), ("Grid: 1/16", beat / 4), ("Grid: 1/32", beat / 8), ("Grid: tick", ClipNotes.TimeBase), ("Grid: off", 1)
            };
            gridBox = new ComboBox { Width = 120, Margin = new Thickness(0, 0, 8, 0), FontSize = 12 };
            foreach (var g in grids) gridBox.Items.Add(g.Label);
            gridBox.SelectedIndex = Math.Max(0, Array.FindIndex(grids, g => g.Value == ClipNotes.TimeBase));
            gridBox.SelectionChanged += (s, e) => { if (gridBox.SelectedIndex >= 0) roll.Grid = Math.Max(1, grids[gridBox.SelectedIndex].Value); roll.Focus(); };
            roll.Grid = Math.Max(1, grids[gridBox.SelectedIndex].Value);

            Button Tool(string label, string tip, Action action)
            {
                var b = new Button { Content = label, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), FontSize = 12 };
                ToolTip.SetTip(b, tip);
                b.Click += (s, e) => { action(); roll.Focus(); };
                return b;
            }

            string mod = SessionView.ModName;
            summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = SessionPalette.DimText, Margin = new Thickness(8, 0, 0, 0) };
            var tools = new WrapPanel { Margin = new Thickness(8, 6, 8, 6) };
            tools.Children.Add(gridBox);
            tools.Children.Add(Tool("Quantize", "Quantize the selected notes (or all) to the grid (Q)", () => roll.Quantize()));
            tools.Children.Add(Tool("Vel −", $"Softer ({mod}+Down)", () => roll.ChangeVelocity(-8)));
            tools.Children.Add(Tool("Vel +", $"Louder ({mod}+Up)", () => roll.ChangeVelocity(8)));
            tools.Children.Add(Tool("Select all", $"{mod}+A", roll.SelectAll));
            tools.Children.Add(Tool("Delete", "Delete the selected notes (Del)", roll.DeleteSelected));
            tools.Children.Add(Tool("−", $"Zoom out ({mod}+wheel)", () => Zoom(0.8)));
            tools.Children.Add(Tool("+", $"Zoom in ({mod}+wheel)", () => Zoom(1.25)));
            tools.Children.Add(summary);
            var help = new TextBlock
            {
                Margin = new Thickness(8, 0, 8, 6),
                Foreground = SessionPalette.DimText,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Text = "Double-click adds or deletes a note · drag moves (Shift: off the grid) · drag a note's end resizes · Alt+drag: velocity · " +
                       $"drag on empty space selects · arrows move (Shift: octave) · click the keys to hear them · every edit is one undo step ({mod}+Z)"
            };

            var dock = new DockPanel();
            DockPanel.SetDock(tools, Dock.Top);
            DockPanel.SetDock(help, Dock.Bottom);
            dock.Children.Add(tools);
            dock.Children.Add(help);
            dock.Children.Add(scroll);
            Content = dock;

            roll.Changed += UpdateSummary;
            UpdateSummary();
            seen = Signature();

            // Recording, undo in the Session View and other edits change the notes: follow them; and the play position.
            timer.Tick += (s, e) => Poll();
            // A new or opened song ends the old one's clips, and its undo: close (0.7.1 hardening).
            Action reset = () => Dispatcher.UIThread.Post(Close);
            layout.Resetting += reset;
            Closed += (s, e) => { timer.Stop(); layout.Resetting -= reset; };
        }

        internal PianoRoll Roll => roll;
        internal ScrollViewer Scroll => scroll;
        internal ComboBox GridBox => gridBox;
        internal string SummaryText => summary.Text;

        /// <summary>Follows changes made elsewhere (the timer calls it; tests too).</summary>
        internal void Poll()
        {
            var now = Signature();
            if (now != seen)
            {
                seen = now;
                roll.Reload();
            }
            else
            {
                roll.InvalidateVisual();
            }
        }

        (int, long) Signature()
        {
            if (!roll.IsLive) return (-1, roll.IsRecording ? 1 : 0);
            var notes = roll.Source.Notes();
            long sum = roll.IsRecording ? 1 : 0;
            foreach (var n in notes) sum = sum * 31 + n.Time * 7 + n.Duration * 3 + n.Note * 131 + n.Velocity;
            return (notes.Count, sum);
        }

        void Zoom(double factor)
        {
            roll.TickWidth = Math.Clamp(roll.TickWidth * factor, 3, 160);
            roll.InvalidateMeasure();
            roll.InvalidateVisual();
        }

        void UpdateSummary()
        {
            int count = roll.Notes.Count;
            int sel = roll.Selection.Count;
            summary.Text = roll.Source.WhyNotEditable is string why ? why
                : count == 0 ? "No notes yet: double-click to draw one."
                : sel == 0 ? $"{count} note{(count == 1 ? "" : "s")}"
                : $"{sel} of {count} selected" + (sel == 1 ? $" · {ClipNotes.Name(roll.Notes[roll.Selection.First()].Note)}, velocity {roll.Notes[roll.Selection.First()].Velocity}" : "");
            seen = Signature();
        }
    }
}
