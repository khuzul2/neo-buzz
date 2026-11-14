// Fork (docs/specs/daw-modes.md AR2): a controller lane under the piano roll: one control change (or the pitch bend) of the pattern's
// MIDI column, drawn as points that hold their value to the next one. Click on empty space to add a point, drag a point to move it
// (its value, and its time on the grid), double-click a point to delete it. Each gesture is one undo step of the song's history.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Detail
{
    using Control = Avalonia.Controls.Control;

    internal sealed class ControllerLane : Control
    {
        const double Pad = 6;
        const double Grab = 5;
        const double DragDistance = 3;

        readonly PianoRoll roll;
        readonly MidiControllers store;
        double offsetX;
        List<ControllerPoint> points = new List<ControllerPoint>();
        ControllerPoint? dragFrom, dragTo;
        bool dragging, created;
        double downY;
        string refusal;

        public ControllerLane(PianoRoll roll, MidiControllers store, int controller)
        {
            this.roll = roll;
            this.store = store;
            Controller = controller;
            Height = VelocityLane.LaneHeight;
            ClipToBounds = true;
            Focusable = false;
            Avalonia.Automation.AutomationProperties.SetName(this, "Controller lane");
            ToolTip.SetTip(this, "Click to add a point, drag a point to move it, double-click it to delete it. Each point holds its value to the next.");
            Reload();
        }

        /// <summary>The controller drawn: 0-127, or <see cref="ControllerPoint.PitchBend"/>.</summary>
        public int Controller { get; }

        public double OffsetX { get => offsetX; set { offsetX = value; InvalidateVisual(); } }

        /// <summary>The points of this controller, as drawn now.</summary>
        internal IReadOnlyList<ControllerPoint> Points => points;

        /// <summary>Why the last edit was refused, or null.</summary>
        public string Refusal => refusal;

        int Max => Controller == ControllerPoint.PitchBend ? 16383 : 127;

        /// <summary>Reads the column again (after an edit, an undo, a recording).</summary>
        public void Reload()
        {
            points = store.Points().Where(p => p.Controller == Controller).ToList();
            InvalidateVisual();
        }

        // ------------------------------------------------------------------ geometry

        double TopOf(int value) => Height - Pad - value / (double)Max * (Height - 2 * Pad);

        int ValueAt(double y) => Math.Clamp((int)Math.Round((Height - Pad - y) / (Height - 2 * Pad) * Max), 0, Max);

        double XOf(ControllerPoint p) => roll.X(p.Time) - offsetX;

        /// <summary>Where a point is, for tests that drag it.</summary>
        internal Point PointOf(ControllerPoint p) => new Point(XOf(p), TopOf(p.Value));

        /// <summary>A point of the lane at a time and value, for tests that click empty space.</summary>
        internal Point At(int time, int value) => new Point(roll.X(time) - offsetX, TopOf(value));

        internal int ValueOfY(double y) => ValueAt(y);

        // ------------------------------------------------------------------ drawing

        static readonly IPen GuidePen = new Pen(StudioTokens.SurfaceBorderBrush, 1);

        public override void Render(DrawingContext dc)
        {
            double w = Bounds.Width, h = Bounds.Height;
            dc.FillRectangle(StudioTokens.SurfaceViewBrush, new Rect(0, 0, w, h));
            dc.DrawLine(GuidePen, new Point(0, 0.5), new Point(w, 0.5));
            foreach (double f in new[] { 0.25, 0.5, 0.75, 1.0 })
            {
                double y = Math.Round(TopOf((int)(Max * f))) + 0.5;
                dc.DrawLine(GuidePen, new Point(PianoRoll.KeysWidth, y), new Point(w, y));
            }
            if (Controller == ControllerPoint.PitchBend)
            {
                double y = Math.Round(TopOf(ControllerPoint.BendCentre)) + 0.5;
                dc.DrawLine(new Pen(StudioTokens.TextSecondaryBrush, 1, DashStyle.Dash), new Point(PianoRoll.KeysWidth, y), new Point(w, y));
            }
            var shown = points.ToList();
            if (dragFrom is { } from) shown.Remove(from);
            if (dragTo is { } to) shown.Add(to);
            shown.Sort((a, b) => a.Time.CompareTo(b.Time));
            var line = new Pen(PianoRoll.NoteFill, 1.5);
            for (int i = 0; i < shown.Count; i++)
            {
                double x = XOf(shown[i]), y = TopOf(shown[i].Value);
                double next = i + 1 < shown.Count ? XOf(shown[i + 1]) : roll.X(roll.Source.Length) - offsetX;
                if (next >= PianoRoll.KeysWidth && x <= w)
                    dc.DrawLine(line, new Point(Math.Max(x, PianoRoll.KeysWidth), y), new Point(Math.Min(next, w), y));
                if (x >= PianoRoll.KeysWidth && x <= w)
                {
                    bool active = dragTo is { } d && d == shown[i];
                    dc.DrawEllipse(active ? PianoRoll.NoteSelected : PianoRoll.NoteFill, null, new Point(x, y), 3.5, 3.5);
                }
            }
            dc.FillRectangle(StudioTokens.SurfacePanelBrush, new Rect(0, 1, PianoRoll.KeysWidth, h - 1));
            var title = new FormattedText(ControllerPoint.NameOf(Controller), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, StudioTokens.FontCaption, StudioTokens.TextSecondaryBrush);
            dc.DrawText(title, new Point(3, 4));
            if (dragTo is { } cur)
                dc.DrawText(new FormattedText(cur.Value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default,
                    StudioTokens.FontBody, StudioTokens.TextPrimaryBrush), new Point(3, 20));
        }

        // ------------------------------------------------------------------ the gestures

        int HitTest(Point p)
        {
            int best = -1;
            double bestScore = double.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                double dx = Math.Abs(XOf(points[i]) - p.X);
                if (dx > Grab) continue;
                double score = dx + Math.Abs(TopOf(points[i].Value) - p.Y) * 0.25;
                if (score < bestScore) { bestScore = score; best = i; }
            }
            return best;
        }

        int TimeAt(double x)
        {
            int grid = Math.Max(1, roll.Grid);
            int t = roll.TimeAt(x + offsetX);
            return Math.Clamp(ClipNotes.Snap(t, grid), 0, Math.Max(0, roll.Source.Length - 1));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var p = e.GetPosition(this);
            if (p.X < PianoRoll.KeysWidth) return;
            refusal = null;
            int hit = HitTest(p);
            if (e.ClickCount == 2)
            {
                if (hit >= 0) Commit(new[] { points[hit] }, Array.Empty<ControllerPoint>());
                e.Handled = true;
                return;
            }
            downY = p.Y;
            dragging = false;
            if (hit >= 0)
            {
                dragFrom = points[hit];
                dragTo = points[hit];
                created = false;
            }
            else
            {
                // A new point at the pointer, the grid's time; if one is there already, that one moves.
                int time = TimeAt(p.X);
                var existing = points.Cast<ControllerPoint?>().FirstOrDefault(q => q!.Value.Time == time);
                dragFrom = existing;
                dragTo = new ControllerPoint(time, Controller, ValueAt(p.Y), existing?.Channel ?? 0);
                created = existing == null;
                dragging = true;
            }
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (dragTo is not { } cur) return;
            var p = e.GetPosition(this);
            if (!dragging)
            {
                if (Math.Abs(p.Y - downY) < DragDistance && dragFrom is { } f0 && Math.Abs(p.X - XOf(f0)) < DragDistance) return;
                dragging = true;
            }
            dragTo = cur with { Time = TimeAt(p.X), Value = ValueAt(p.Y) };
            InvalidateVisual();
        }

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
            var from = dragFrom;
            var to = dragTo;
            dragFrom = dragTo = null;
            dragging = false;
            if (commit && to is { } t && (created || (from is { } f && f != t)))
            {
                // A point moved onto another one's time replaces it.
                var removed = new List<ControllerPoint>();
                if (from is { } old) removed.Add(old);
                foreach (var other in points)
                    if (other.Time == t.Time && other.Controller == t.Controller && other != from) removed.Add(other);
                Commit(removed, new[] { t });
            }
            else
            {
                InvalidateVisual();
            }
        }

        void Commit(IReadOnlyList<ControllerPoint> removed, IReadOnlyList<ControllerPoint> added)
        {
            refusal = store.Edit(removed, added);
            Reload();
            Edited?.Invoke();
        }

        /// <summary>Raised after an edit (the host marks its undo bookkeeping and the summary).</summary>
        public event Action Edited;
    }
}
