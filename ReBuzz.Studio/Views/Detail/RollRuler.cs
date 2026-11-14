// Fork (docs/specs/ux-overhaul.md P3b2, decisions 45-48): what the docked piano roll has around its grid: the bar and beat ruler
// above it (the song's bar length at the pattern, the song's ticks a beat), the loop brace on that ruler (the song's own loop,
// dragged), the playhead's line, and the velocity lane below. All three follow the roll's horizontal scroll and zoom: they ask the
// roll where a time is (PianoRoll.X) and are given the scroll offset.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Detail
{
    using Control = Avalonia.Controls.Control;

    internal enum RulerMark { Bar, Beat }

    /// <summary>One line of the ruler: a tick of the pattern, a bar's start or a beat, and the text on it ("" when there is no room).</summary>
    internal readonly record struct RulerLine(int Tick, RulerMark Kind, string Label);

    /// <summary>The bar and beat ruler over the roll, with the loop brace on its lower half.</summary>
    internal sealed class RollRuler : Control
    {
        public const double BarHeight = 26;
        const double BraceTop = 15;
        const double EdgeGrab = 6;

        readonly PianoRoll roll;
        enum Gesture { None, New, Start, End }
        Gesture gesture;
        int anchorTick, previewStart = -1, previewEnd = -1;
        bool moved;

        public RollRuler(PianoRoll roll)
        {
            this.roll = roll;
            Height = BarHeight;
            ClipToBounds = true;
            Focusable = false;
            Cursor = new Cursor(StandardCursorType.Hand);
            Avalonia.Automation.AutomationProperties.SetName(this, "Ruler");
        }

        /// <summary>The horizontal scroll of the roll's viewer.</summary>
        public double OffsetX { get; set; }

        /// <summary>The bar's length in ticks (the song's list at the pattern).</summary>
        public int BarTicks { get; set; } = 16;

        public int Tpb { get; set; } = 4;

        /// <summary>The pattern's length in ticks.</summary>
        public int LengthTicks { get; set; }

        /// <summary>The song's loop in this pattern's ticks, or -1 when the pattern lies outside it / isn't in the song.</summary>
        public int LoopStartTick { get; set; } = -1;

        public int LoopEndTick { get; set; } = -1;

        /// <summary>Whether the transport loops (the brace is lit then).</summary>
        public bool LoopOn { get; set; }

        /// <summary>Whether the pattern is in the song: only then can the ruler set a loop.</summary>
        public bool CanSetLoop { get; set; }

        /// <summary>The playhead in pattern time (1/240 tick), or -1.</summary>
        public int Playhead { get; set; } = -1;

        /// <summary>A drag on the ruler ended: the loop is these ticks of the pattern.</summary>
        public event Action<int, int> LoopSet;

        // ------------------------------------------------------------------ marks (pure: tests call it)

        /// <summary>
        /// The ruler's lines between two ticks: a bar every <paramref name="barTicks"/>, beats every <paramref name="tpb"/> inside it
        /// (from the bar's start, so a bar that isn't a whole number of beats still starts on its line). Bar numbers go on the bar
        /// lines, thinned to leave 44 px a label; a beat's "bar.beat" only when its beats are 40 px or more apart.
        /// </summary>
        internal static List<RulerLine> Lines(int firstTick, int lastTick, int tpb, int barTicks, double tickWidth)
        {
            var list = new List<RulerLine>();
            tpb = Math.Max(1, tpb);
            barTicks = Math.Max(1, barTicks);
            double barPx = barTicks * tickWidth, beatPx = tpb * tickWidth;
            int every = Math.Max(1, (int)Math.Ceiling(44 / Math.Max(0.1, barPx)));
            int firstBar = Math.Max(0, firstTick / barTicks);
            for (int bar = firstBar; (long)bar * barTicks <= lastTick; bar++)
            {
                int start = bar * barTicks;
                list.Add(new RulerLine(start, RulerMark.Bar, bar % every == 0 ? (bar + 1).ToString(CultureInfo.InvariantCulture) : ""));
                if (beatPx < 6) continue;
                for (int beat = 1; start + beat * tpb < start + barTicks && start + beat * tpb <= lastTick; beat++)
                    list.Add(new RulerLine(start + beat * tpb, RulerMark.Beat,
                        beatPx >= 40 ? $"{bar + 1}.{beat + 1}" : ""));
            }
            return list;
        }

        // ------------------------------------------------------------------ drawing

        static readonly IPen BarPen = new Pen(StudioTokens.TextSecondaryBrush, 1);
        static readonly IPen BeatPen = new Pen(StudioTokens.SurfaceBorderBrush, 1);
        static readonly IPen EdgePen = new Pen(StudioTokens.SurfaceBorderBrush, 1);

        public override void Render(DrawingContext dc)
        {
            double w = Bounds.Width, h = Bounds.Height;
            dc.FillRectangle(StudioTokens.SurfacePanelBrush, new Rect(0, 0, w, h));
            dc.DrawLine(EdgePen, new Point(0, h - 0.5), new Point(w, h - 0.5));
            double tw = roll.TickWidth;
            int first = Math.Max(0, (int)Math.Floor((OffsetX) / tw) - 1);
            int last = Math.Min(LengthTicks, (int)Math.Ceiling((OffsetX + w) / tw) + 1);
            foreach (var line in Lines(first, last, Tpb, BarTicks, tw))
            {
                double x = Math.Round(roll.X(line.Tick * ClipNotes.TimeBase) - OffsetX) + 0.5;
                if (x < PianoRoll.KeysWidth) continue;
                bool bar = line.Kind == RulerMark.Bar;
                dc.DrawLine(bar ? BarPen : BeatPen, new Point(x, bar ? 2 : 9), new Point(x, BraceTop - 1));
                if (line.Label.Length > 0)
                {
                    var text = new FormattedText(line.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default,
                        bar ? StudioTokens.FontBody : StudioTokens.FontCaption, bar ? StudioTokens.TextPrimaryBrush : StudioTokens.TextSecondaryBrush);
                    dc.DrawText(text, new Point(x + 3, bar ? 0 : 3));
                }
            }

            // The loop brace: the song's loop where it crosses this pattern (or the one being dragged).
            int s = gesture != Gesture.None ? previewStart : LoopStartTick, e = gesture != Gesture.None ? previewEnd : LoopEndTick;
            if (s >= 0 && e > s)
            {
                double x0 = Math.Max(PianoRoll.KeysWidth, roll.X(s * ClipNotes.TimeBase) - OffsetX), x1 = roll.X(e * ClipNotes.TimeBase) - OffsetX;
                if (x1 > x0)
                {
                    var fill = LoopOn || gesture != Gesture.None ? StudioTokens.AccentBrush : StudioTokens.SurfaceBorderBrush;
                    using (dc.PushOpacity(LoopOn || gesture != Gesture.None ? 0.55 : 0.8))
                        dc.FillRectangle(fill, new Rect(x0, BraceTop, x1 - x0, h - BraceTop - 1));
                    dc.FillRectangle(StudioTokens.TextPrimaryBrush, new Rect(x0, BraceTop, 2, h - BraceTop - 1));
                    dc.FillRectangle(StudioTokens.TextPrimaryBrush, new Rect(x1 - 2, BraceTop, 2, h - BraceTop - 1));
                }
            }

            // The play position: a marker on the ruler (the line itself is the overlay's).
            if (Playhead >= 0)
            {
                double x = roll.X(Playhead) - OffsetX;
                if (x >= PianoRoll.KeysWidth)
                    {
                        var tri = new StreamGeometry();
                        using (var g = tri.Open())
                        {
                            g.BeginFigure(new Point(x - 5, 0), true);
                            g.LineTo(new Point(x + 5, 0));
                            g.LineTo(new Point(x, 7));
                            g.EndFigure(true);
                        }
                        dc.DrawGeometry(StudioTokens.PlayBrush, null, tri);
                    }
            }

            // The keys' column: a label.
            dc.FillRectangle(StudioTokens.SurfacePanelBrush, new Rect(0, 0, PianoRoll.KeysWidth, h - 1));
            var title = new FormattedText("Bar", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, StudioTokens.FontBody, StudioTokens.TextSecondaryBrush);
            dc.DrawText(title, new Point(6, 5));
        }

        // ------------------------------------------------------------------ the loop brace by mouse

        int TickAt(double x, KeyModifiers mods)
        {
            double tick = (x + OffsetX - PianoRoll.KeysWidth) / roll.TickWidth;
            bool free = mods.HasFlag(KeyModifiers.Shift) || mods.HasFlag(KeyModifiers.Alt);
            int step = free ? 1 : Math.Max(1, Tpb);
            return Math.Clamp((int)Math.Round(tick / step) * step, 0, LengthTicks);
        }

        double XOfTick(int tick) => roll.X(tick * ClipNotes.TimeBase) - OffsetX;

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var p = e.GetPosition(this);
            if (p.X < PianoRoll.KeysWidth) return;
            moved = false;
            int tick = TickAt(p.X, e.KeyModifiers);
            if (CanSetLoop && LoopStartTick >= 0 && LoopEndTick > LoopStartTick && Math.Abs(p.X - XOfTick(LoopStartTick)) <= EdgeGrab)
            {
                gesture = Gesture.Start;
                anchorTick = LoopEndTick;
            }
            else if (CanSetLoop && LoopStartTick >= 0 && LoopEndTick > LoopStartTick && Math.Abs(p.X - XOfTick(LoopEndTick)) <= EdgeGrab)
            {
                gesture = Gesture.End;
                anchorTick = LoopStartTick;
            }
            else
            {
                gesture = Gesture.New;
                anchorTick = tick;
            }
            previewStart = LoopStartTick;
            previewEnd = LoopEndTick;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (gesture == Gesture.None) return;
            var p = e.GetPosition(this);
            int tick = TickAt(p.X, e.KeyModifiers);
            if (tick == anchorTick && !moved) return;
            moved = true;
            previewStart = Math.Min(anchorTick, tick);
            previewEnd = Math.Max(anchorTick, tick);
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            Finish(commit: true);
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            Finish(commit: false);
        }

        void Finish(bool commit)
        {
            if (gesture == Gesture.None) return;
            var was = gesture;
            gesture = Gesture.None;
            int s = previewStart, end = previewEnd;
            if (commit && moved && end > s && s >= 0)
            {
                if (CanSetLoop) LoopSet?.Invoke(s, end);
            }
            else if (commit && !moved && was == Gesture.New)
            {
                roll.InsertTime = Math.Min(anchorTick * ClipNotes.TimeBase, Math.Max(0, roll.Source.Length - 1));
            }
            InvalidateVisual();
        }
    }

    /// <summary>The playhead's line over the roll: 2 pt in the play colour with a dark edge (ux-overhaul.md: playheads are 2 pt, ≥3:1).</summary>
    internal sealed class PlayheadLine : Control
    {
        static readonly IPen Edge = new ImmutablePen(Colors.Black.ToUInt32(), 4);
        static readonly IPen Line = new Pen(StudioTokens.PlayBrush, 2);

        public PlayheadLine()
        {
            IsHitTestVisible = false;
        }

        /// <summary>The line's x in this control, or a negative number for none.</summary>
        public double LineX { get; set; } = -1;

        public override void Render(DrawingContext dc)
        {
            if (LineX < PianoRoll.KeysWidth || LineX > Bounds.Width) return;
            dc.DrawLine(Edge, new Point(LineX, 0), new Point(LineX, Bounds.Height));
            dc.DrawLine(Line, new Point(LineX, 0), new Point(LineX, Bounds.Height));
        }
    }

    /// <summary>The velocity lane under the roll: a bar a note, dragged to set the velocity (one undo step a drag).</summary>
    internal sealed class VelocityLane : Control
    {
        public const double LaneHeight = 72;
        const double Pad = 6;
        const double Grab = 4;
        const double DragDistance = 3;

        readonly PianoRoll roll;
        double offsetX;
        List<ClipNote> from, to;
        int hitIndex = -1;
        double downY;
        bool dragging;

        public VelocityLane(PianoRoll roll)
        {
            this.roll = roll;
            Height = LaneHeight;
            ClipToBounds = true;
            Focusable = false;
            Avalonia.Automation.AutomationProperties.SetName(this, "Velocity lane");
            ToolTip.SetTip(this, "Velocity: drag a bar. With several notes selected, the drag changes them all by the same amount.");
        }

        public double OffsetX { get => offsetX; set { offsetX = value; InvalidateVisual(); } }

        // ------------------------------------------------------------------ geometry

        static double BarTop(int velocity) => LaneHeight - Pad - (velocity / 127.0) * (LaneHeight - 2 * Pad);

        static int VelocityAt(double y) => Math.Clamp((int)Math.Round((LaneHeight - Pad - y) / (LaneHeight - 2 * Pad) * 127), 1, 127);

        double BarWidth => Math.Clamp(roll.TickWidth * 0.5, 3, 6);

        double BarX(ClipNote n) => roll.X(n.Time) - offsetX;

        /// <summary>Where a note's bar is, for tests that drag it: its top centre in this control.</summary>
        internal Point BarPoint(ClipNote n) => new Point(BarX(n) + BarWidth / 2, BarTop(n.Velocity));

        internal int VelocityOfY(double y) => VelocityAt(y);

        internal double YOfVelocity(int velocity) => BarTop(velocity);

        // ------------------------------------------------------------------ drawing

        static readonly IPen GuidePen = new Pen(StudioTokens.SurfaceBorderBrush, 1);

        public override void Render(DrawingContext dc)
        {
            double w = Bounds.Width, h = Bounds.Height;
            dc.FillRectangle(StudioTokens.SurfaceViewBrush, new Rect(0, 0, w, h));
            dc.DrawLine(GuidePen, new Point(0, 0.5), new Point(w, 0.5));
            foreach (int v in new[] { 32, 64, 96, 127 })
            {
                double y = Math.Round(BarTop(v)) + 0.5;
                dc.DrawLine(GuidePen, new Point(PianoRoll.KeysWidth, y), new Point(w, y));
            }
            var selected = roll.Selection;
            var notes = roll.Notes;
            double bw = BarWidth;
            for (int i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                bool sel = selected.Contains(i);
                if (sel && to != null) continue;                         // the drag's preview instead
                DrawBar(dc, n, sel, bw, w);
            }
            if (to != null)
                foreach (var n in to) DrawBar(dc, n, true, bw, w);

            dc.FillRectangle(StudioTokens.SurfacePanelBrush, new Rect(0, 1, PianoRoll.KeysWidth, h - 1));
            var title = new FormattedText("Velocity", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, StudioTokens.FontCaption, StudioTokens.TextSecondaryBrush);
            dc.DrawText(title, new Point(3, 4));
            if (from != null || hitIndex >= 0)
            {
                var v = to != null && to.Count > 0 ? to[0].Velocity : hitIndex >= 0 && hitIndex < notes.Count ? notes[hitIndex].Velocity : 0;
                if (v > 0)
                    dc.DrawText(new FormattedText(v.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default,
                        StudioTokens.FontBody, StudioTokens.TextPrimaryBrush), new Point(3, 20));
            }
        }

        void DrawBar(DrawingContext dc, ClipNote n, bool sel, double bw, double w)
        {
            double x = BarX(n);
            if (x + bw < PianoRoll.KeysWidth || x > w) return;
            double top = BarTop(n.Velocity);
            dc.FillRectangle(sel ? PianoRoll.NoteSelected : PianoRoll.NoteFill, new Rect(x, top, bw, LaneHeight - Pad - top));
        }

        // ------------------------------------------------------------------ the drag

        int HitTest(Point p)
        {
            var notes = roll.Notes;
            int best = -1;
            double bestScore = double.MaxValue, bw = BarWidth;
            for (int i = 0; i < notes.Count; i++)
            {
                double x = BarX(notes[i]);
                double dx = p.X < x ? x - p.X : p.X > x + bw ? p.X - (x + bw) : 0;
                if (dx > Grab) continue;
                double score = dx * 1000 + Math.Abs(p.Y - BarTop(notes[i].Velocity));
                if (score < bestScore) { bestScore = score; best = i; }
            }
            return best;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var p = e.GetPosition(this);
            if (p.X < PianoRoll.KeysWidth) return;
            int hit = HitTest(p);
            if (hit < 0) return;
            var mods = e.KeyModifiers;
            bool additive = mods.HasFlag(KeyModifiers.Shift) || SessionView.Normalize(mods).HasFlag(KeyModifiers.Control);
            if (additive) roll.ToggleSelect(hit);
            else if (!roll.Selection.Contains(hit)) roll.SelectOnly(new[] { hit });
            if (!roll.Selection.Contains(hit)) { InvalidateVisual(); e.Handled = true; return; }
            hitIndex = hit;
            from = roll.Selection.OrderBy(i => i).Select(i => roll.Notes[i]).ToList();
            downY = p.Y;
            dragging = false;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (from == null) return;
            double y = e.GetPosition(this).Y;
            if (!dragging)
            {
                if (Math.Abs(y - downY) < DragDistance) return;
                dragging = true;
            }
            to = Edited(from, roll.Notes[hitIndex < roll.Notes.Count ? hitIndex : 0].Velocity, y);
            InvalidateVisual();
        }

        /// <summary>One note: the velocity of the pointer, exactly. Several: all by the amount the pointer moved from where it went down.</summary>
        List<ClipNote> Edited(List<ClipNote> notes, int hitVelocity, double y) =>
            notes.Count == 1
                ? new List<ClipNote> { notes[0] with { Velocity = VelocityAt(y) } }
                : ClipNotes.Velocity(notes, VelocityAt(y) - VelocityAt(downY));

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            End(commit: true);
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            End(commit: false);
        }

        void End(bool commit)
        {
            var f = from;
            var t = to;
            from = null;
            to = null;
            hitIndex = -1;
            dragging = false;
            if (commit && f != null && t != null && !f.SequenceEqual(t))
                roll.ApplyEdit(f, t);                                    // one edit: one undo step
            InvalidateVisual();
        }
    }
}
