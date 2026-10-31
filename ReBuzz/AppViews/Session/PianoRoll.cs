using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ReBuzz.AppViews.Session
{
    /// <summary>
    /// The Session View's piano roll (fork addition, docs/specs/session-view.md 0.7.1): a clip's
    /// recorded notes, drawn and edited Live's way. Double-click adds or deletes a note; drag
    /// moves, drag a note's end resizes it, Alt+drag changes velocity, drag on empty space
    /// selects. Arrows move the selection (Shift: an octave), Q quantizes, Del deletes, Ctrl+A
    /// selects all. Every edit is one undo step of the session (Ctrl+Z here too).
    /// </summary>
    internal sealed class PianoRoll : FrameworkElement
    {
        public const double KeysWidth = 46;
        const double EdgeGrab = 6;

        enum Drag { None, Move, Resize, Velocity, Box, Key }

        readonly SessionLayout layout;
        readonly ISession session;
        readonly ReBuzzCore buzz;
        List<ClipNote> notes = new List<ClipNote>();
        readonly HashSet<int> selected = new HashSet<int>();

        Drag drag;
        Point down;
        int downTime, downNote, keyNote = -1;
        List<ClipNote> dragFrom;
        List<ClipNote> dragTo;
        Rect? box;
        int lastVelocity = 100;
        int lastDuration;

        public PianoRoll(ReBuzzCore buzz, SessionLayout layout, ISequence track, IPattern clip)
        {
            this.buzz = buzz;
            this.layout = layout;
            session = buzz.Session;
            Track = track;
            Clip = clip;
            Focusable = true;
            FocusVisualStyle = null;
            ClipToBounds = true;
            Reload();
        }

        public ISequence Track { get; }
        public IPattern Clip { get; }

        /// <summary>Snap grid in pattern time (1/240 tick); 1 for none.</summary>
        public int Grid { get; set; } = ClipNotes.TimeBase;

        /// <summary>Horizontal zoom: pixels per tick.</summary>
        public double TickWidth { get; set; } = 24;

        public double RowHeight { get; set; } = 11;

        internal IReadOnlyList<ClipNote> Notes => notes;
        internal IReadOnlyCollection<int> Selection => selected;

        /// <summary>Raised after the notes or the selection changed (the window shows a summary).</summary>
        public event Action Changed;

        int Length => SessionLayout.ClipLengthOf(Clip);

        int BeatTime => Math.Max(1, buzz.TPB) * ClipNotes.TimeBase;

        int BarTime => BeatTime * Math.Max(1, session.BeatsPerBar);

        // ------------------------------------------------------------------ geometry

        double X(int time) => KeysWidth + time / (double)ClipNotes.TimeBase * TickWidth;

        double Y(int note) => (127 - note) * RowHeight;

        int TimeAt(double x) => (int)Math.Round(Math.Max(0, x - KeysWidth) / TickWidth * ClipNotes.TimeBase);

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
            notes = layout.NotesOf(Clip);
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

        void Commit(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added)
        {
            layout.EditNotes(Clip, removed, added);
            notes = layout.NotesOf(Clip);
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

        internal void AddNote(int time, int note)
        {
            var n = ClipNotes.Draw(time, note, lastVelocity, Grid, Length, lastDuration);
            Commit(Array.Empty<ClipNote>(), new[] { n });
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

        // ------------------------------------------------------------------ drawing

        static readonly Brush Back = SessionPalette.Make("#1E1E22");
        static readonly Brush BlackKeyRow = SessionPalette.Make("#19191C");
        static readonly Brush WhiteKey = SessionPalette.Make("#D9D9DE");
        static readonly Brush BlackKey = SessionPalette.Make("#2A2A30");
        static readonly Brush NoteFill = SessionPalette.Make("#E0457B");
        static readonly Brush NoteSelected = SessionPalette.Make("#F0C24B");
        static readonly Brush OutOfClip = SessionPalette.Make("#141417");
        static readonly Pen RowLine = Frozen(new Pen(SessionPalette.Make("#26262B"), 1));
        static readonly Pen TickLine = Frozen(new Pen(SessionPalette.Make("#24242A"), 1));
        static readonly Pen BeatLine = Frozen(new Pen(SessionPalette.Make("#34343C"), 1));
        static readonly Pen BarLine = Frozen(new Pen(SessionPalette.Make("#55555F"), 1));
        static readonly Pen NoteEdge = Frozen(new Pen(SessionPalette.Make("#101012"), 1));
        static readonly Pen BoxPen = Frozen(new Pen(SessionPalette.Make("#F0C24B"), 1) { DashStyle = DashStyles.Dash });
        static readonly Pen PlayheadPen = Frozen(new Pen(SessionPalette.Make("#6FD98A"), 1.5));
        static readonly Typeface Face = new Typeface("Segoe UI");

        static Pen Frozen(Pen pen)
        {
            pen.Freeze();
            return pen;
        }

        static bool IsBlack(int note) => (note % 12) is 1 or 3 or 6 or 8 or 10;

        protected override void OnRender(DrawingContext dc)
        {
            double width = Math.Max(ActualWidth, X(Length) + 40);
            double height = 128 * RowHeight;
            dc.DrawRectangle(Back, null, new Rect(0, 0, width, height));

            // Rows: black-key rows darker, a line under each C.
            for (int note = 0; note < 128; note++)
            {
                double y = Y(note);
                if (IsBlack(note))
                    dc.DrawRectangle(BlackKeyRow, null, new Rect(KeysWidth, y, width - KeysWidth, RowHeight));
                if (note % 12 == 0)
                    dc.DrawLine(RowLine, new Point(KeysWidth, y + RowHeight), new Point(width, y + RowHeight));
            }

            // Past the clip's end.
            double end = X(Length);
            dc.DrawRectangle(OutOfClip, null, new Rect(end, 0, Math.Max(0, width - end), height));

            // Time lines: ticks when they are wide enough, beats, bars.
            int tickTime = ClipNotes.TimeBase;
            for (int t = 0; t <= Length; t += tickTime)
            {
                Pen pen = t % BarTime == 0 ? BarLine : t % BeatTime == 0 ? BeatLine : TickWidth >= 10 ? TickLine : null;
                if (pen == null) continue;
                double x = Math.Round(X(t)) + 0.5;
                dc.DrawLine(pen, new Point(x, 0), new Point(x, height));
            }

            // Notes: the drag's preview instead of what it moves.
            var shown = notes;
            HashSet<int> hidden = null;
            if (dragTo != null && dragFrom != null)
            {
                hidden = new HashSet<int>(selected);
            }
            for (int i = 0; i < shown.Count; i++)
            {
                if (hidden != null && hidden.Contains(i)) continue;
                DrawNote(dc, shown[i], selected.Contains(i));
            }
            if (dragTo != null)
                foreach (var n in dragTo)
                    DrawNote(dc, n, true);

            if (box is Rect b)
                dc.DrawRectangle(null, BoxPen, b);

            // The play position, while the clip plays on its track.
            if (Track != null && session.GetState(Track, Clip) is ClipState.Playing or ClipState.Stopping)
            {
                int position = session.GetClipPosition(Track);
                if (position >= 0)
                {
                    double x = X(position * ClipNotes.TimeBase);
                    dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, height));
                }
            }

            // Keys.
            for (int note = 0; note < 128; note++)
            {
                double y = Y(note);
                bool black = IsBlack(note);
                dc.DrawRectangle(black ? BlackKey : WhiteKey, NoteEdge, new Rect(0, y, KeysWidth, RowHeight));
                if (note % 12 == 0 || note == keyNote)
                {
                    var text = new FormattedText(ClipNotes.Name(note), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        Face, Math.Min(10, RowHeight - 1), black ? WhiteKey : BlackKey, 1.0);
                    dc.DrawText(text, new Point(3, y + (RowHeight - text.Height) / 2));
                }
            }
        }

        void DrawNote(DrawingContext dc, ClipNote n, bool isSelected)
        {
            var r = RectOf(n);
            var fill = isSelected ? NoteSelected : NoteFill;
            dc.PushOpacity(0.45 + 0.55 * n.Velocity / 127.0);
            dc.DrawRectangle(fill, NoteEdge, r);
            dc.Pop();
        }

        // ------------------------------------------------------------------ mouse

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            var p = e.GetPosition(this);
            down = p;
            downTime = TimeAt(p.X);
            downNote = NoteAt(p.Y);
            bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

            if (p.X < KeysWidth)
            {
                keyNote = downNote;
                layout.PreviewNote(Clip, keyNote, lastVelocity, true);
                drag = Drag.Key;
                CaptureMouse();
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
                    AddNote(downTime, downNote);
                    layout.PreviewNote(Clip, downNote, lastVelocity, true);
                    layout.PreviewNote(Clip, downNote, 0, false);
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
                drag = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? Drag.Velocity
                     : p.X >= r.Right - EdgeGrab ? Drag.Resize
                     : Drag.Move;
                dragFrom = SelectedNotes();
                lastVelocity = notes[hit].Velocity;
                lastDuration = notes[hit].Duration;
                if (drag == Drag.Move)
                {
                    keyNote = notes[hit].Note;
                    layout.PreviewNote(Clip, keyNote, lastVelocity, true);
                }
            }
            else
            {
                if (!additive) selected.Clear();
                drag = Drag.Box;
                box = new Rect(p, p);
            }
            CaptureMouse();
            InvalidateVisual();
            Changed?.Invoke();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            if (drag == Drag.None)
            {
                int hit = p.X >= KeysWidth ? ClipNotes.HitTest(notes, TimeAt(p.X), NoteAt(p.Y)) : -1;
                Cursor = hit >= 0 && p.X >= RectOf(notes[hit]).Right - EdgeGrab ? Cursors.SizeWE : hit >= 0 ? Cursors.SizeAll : null;
                return;
            }
            switch (drag)
            {
                case Drag.Move:
                    {
                        int dt = TimeAt(p.X) - downTime;
                        int dn = NoteAt(p.Y) - downNote;
                        dragTo = ClipNotes.Move(dragFrom, dt, dn, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : Grid, Length);
                        int heard = dragTo.Count > 0 ? dragTo[0].Note : -1;
                        if (heard != keyNote && heard >= 0)
                        {
                            layout.PreviewNote(Clip, keyNote, 0, false);
                            keyNote = heard;
                            layout.PreviewNote(Clip, keyNote, lastVelocity, true);
                        }
                        break;
                    }
                case Drag.Resize:
                    {
                        int dd = (int)Math.Round((p.X - down.X) / TickWidth * ClipNotes.TimeBase);
                        dragTo = ClipNotes.Resize(dragFrom, dd, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : Grid, Length);
                        break;
                    }
                case Drag.Velocity:
                    dragTo = ClipNotes.Velocity(dragFrom, (int)Math.Round((down.Y - p.Y) / 2));
                    break;
                case Drag.Box:
                    {
                        box = new Rect(down, p);
                        var b = box.Value;
                        bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                        if (!additive) selected.Clear();
                        foreach (int i in ClipNotes.InBox(notes, TimeAt(b.Left), TimeAt(b.Right), NoteAt(b.Bottom), NoteAt(b.Top)))
                            selected.Add(i);
                        break;
                    }
            }
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            EndDrag(commit: true);
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            EndDrag(commit: false);
        }

        void EndDrag(bool commit)
        {
            if (drag == Drag.None) return;
            var mode = drag;
            drag = Drag.None;
            if (keyNote >= 0 && (mode == Drag.Key || mode == Drag.Move))
                layout.PreviewNote(Clip, keyNote, 0, false);
            keyNote = -1;
            var from = dragFrom;
            var to = dragTo;
            dragFrom = null;
            dragTo = null;
            box = null;
            if (IsMouseCaptured) ReleaseMouseCapture();
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
            var mods = Keyboard.Modifiers;
            int step = Grid > 1 ? Grid : ClipNotes.TimeBase;
            switch (e.Key)
            {
                case Key.Delete:
                case Key.Back:
                    DeleteSelected();
                    break;
                case Key.A when mods == ModifierKeys.Control:
                    SelectAll();
                    break;
                case Key.Left:
                    MoveSelected(-step, 0);
                    break;
                case Key.Right:
                    MoveSelected(step, 0);
                    break;
                case Key.Up when mods.HasFlag(ModifierKeys.Control):
                    ChangeVelocity(8);
                    break;
                case Key.Down when mods.HasFlag(ModifierKeys.Control):
                    ChangeVelocity(-8);
                    break;
                case Key.Up:
                    MoveSelected(0, mods.HasFlag(ModifierKeys.Shift) ? 12 : 1);
                    break;
                case Key.Down:
                    MoveSelected(0, mods.HasFlag(ModifierKeys.Shift) ? -12 : -1);
                    break;
                case Key.Q:
                    Quantize();
                    break;
                case Key.Z when mods == ModifierKeys.Control:
                    layout.Undo();
                    Reload();
                    break;
                case Key.Y when mods == ModifierKeys.Control:
                    layout.Redo();
                    Reload();
                    break;
                case Key.Escape:
                    selected.Clear();
                    InvalidateVisual();
                    Changed?.Invoke();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }
    }

    /// <summary>A window with a clip's piano roll and its tools (0.7.1). One per clip.</summary>
    internal sealed class PianoRollWindow : Window
    {
        static readonly Dictionary<IPattern, PianoRollWindow> open = new Dictionary<IPattern, PianoRollWindow>();

        readonly PianoRoll roll;
        readonly TextBlock summary;
        readonly DispatcherTimer timer;
        readonly SessionLayout layout;
        (int Count, long Sum) seen;

        /// <summary>Shows the clip's piano roll (bringing an open one to the front).</summary>
        public static PianoRollWindow Show(Window owner, ReBuzzCore buzz, SessionLayout layout, ISequence track, IPattern clip)
        {
            if (clip == null) return null;
            if (open.TryGetValue(clip, out var existing))
            {
                existing.Activate();
                return existing;
            }
            var window = new PianoRollWindow(buzz, layout, track, clip) { Owner = owner };
            open[clip] = window;
            window.Closed += (s, e) => open.Remove(clip);
            window.Show();
            return window;
        }

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
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = roll,
                Background = SessionPalette.Window
            };
            scroll.PreviewMouseWheel += (s, e) =>
            {
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
                Zoom(e.Delta > 0 ? 1.25 : 0.8);
                e.Handled = true;
            };
            Loaded += (s, e) => { scroll.ScrollToVerticalOffset(roll.FocusOffset()); roll.Focus(); };

            int beat = Math.Max(1, buzz.TPB) * ClipNotes.TimeBase;
            var grids = new (string Label, int Value)[]
            {
                ("Grid: beat", beat), ("Grid: 1/8", beat / 2), ("Grid: 1/16", beat / 4), ("Grid: 1/32", beat / 8), ("Grid: tick", ClipNotes.TimeBase), ("Grid: off", 1)
            };
            var grid = new ComboBox { Width = 100, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var g in grids) grid.Items.Add(g.Label);
            grid.SelectedIndex = Math.Max(0, Array.FindIndex(grids, g => g.Value == ClipNotes.TimeBase));
            grid.SelectionChanged += (s, e) => { if (grid.SelectedIndex >= 0) roll.Grid = Math.Max(1, grids[grid.SelectedIndex].Value); roll.Focus(); };
            roll.Grid = Math.Max(1, grids[grid.SelectedIndex].Value);

            Button Tool(string label, string tip, Action action)
            {
                var b = new Button { Content = label, ToolTip = tip, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0) };
                b.Click += (s, e) => { action(); roll.Focus(); };
                return b;
            }

            summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = SessionPalette.DimText, Margin = new Thickness(8, 0, 0, 0) };
            var tools = new WrapPanel { Margin = new Thickness(8, 6, 8, 6) };
            tools.Children.Add(grid);
            tools.Children.Add(Tool("Quantize", "Quantize the selected notes (or all) to the grid (Q)", () => roll.Quantize()));
            tools.Children.Add(Tool("Vel −", "Softer (Ctrl+Down)", () => roll.ChangeVelocity(-8)));
            tools.Children.Add(Tool("Vel +", "Louder (Ctrl+Up)", () => roll.ChangeVelocity(8)));
            tools.Children.Add(Tool("Select all", "Ctrl+A", roll.SelectAll));
            tools.Children.Add(Tool("Delete", "Delete the selected notes (Del)", roll.DeleteSelected));
            tools.Children.Add(Tool("−", "Zoom out (Ctrl+wheel)", () => Zoom(0.8)));
            tools.Children.Add(Tool("+", "Zoom in (Ctrl+wheel)", () => Zoom(1.25)));
            tools.Children.Add(summary);
            var help = new TextBlock
            {
                Margin = new Thickness(8, 0, 8, 6),
                Foreground = SessionPalette.DimText,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Text = "Double-click adds or deletes a note · drag moves (Shift: off the grid) · drag a note's end resizes · Alt+drag: velocity · " +
                       "drag on empty space selects · arrows move (Shift: octave) · click the keys to hear them · every edit is one undo step (Ctrl+Z)"
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
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (s, e) =>
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
            };
            Loaded += (s, e) => timer.Start();
            Closed += (s, e) => timer.Stop();
        }

        internal PianoRoll Roll => roll;

        (int, long) Signature()
        {
            var notes = layout.NotesOf(roll.Clip);
            long sum = 0;
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
            summary.Text = count == 0 ? "No notes yet: double-click to draw one."
                : sel == 0 ? $"{count} note{(count == 1 ? "" : "s")}"
                : $"{sel} of {count} selected" + (sel == 1 ? $" · {ClipNotes.Name(roll.Notes[roll.Selection.First()].Note)}, velocity {roll.Notes[roll.Selection.First()].Velocity}" : "");
            seen = Signature();
        }
    }
}
