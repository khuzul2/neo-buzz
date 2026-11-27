// Fork (docs/specs/daw-modes.md EV1, §8.8): one envelope of a clip, drawn as a lane under the Detail panel's piano roll (or the Audio page's
// waveform): the points of the clip's engine automation column for one state parameter, joined by ramps (a straight line to the next
// point) or held as steps, on the time axis of the view above it. The Tracker's own column for the parameter shows as read-only steps, and
// where it and the points both set the parameter the lane marks an overlap (AR4's rule, AutomationReader.OverlapsOf).
//
// Mouse: click on empty space adds a point (the grid; a drag with Alt held turns it off); drag a point moves it, and a selection moves together; drag on empty
// space draws a rubber band; Shift-, Ctrl- or Cmd-click adds to the selection; double-click a point (or Delete) removes it. Keys: R makes the
// line after the selected points a ramp or a step, Ctrl/Cmd+A selects all, Ctrl/Cmd+C and V copy and paste points, Esc cancels a drag.
// Every gesture is one step of the song's history (ClipEnvelopeEditor over AR4's AutomationEditor); the lane reads the clip again after it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Detail
{
    using Control = Avalonia.Controls.Control;

    /// <summary>The time axis a lane shares with the view above it: its zoom, scroll, grid, bars and loop. Times are the clip's (1/240 tick).</summary>
    internal interface IEnvelopeAxis
    {
        /// <summary>The width at the left that holds the view's keys or labels; the clip starts after it.</summary>
        double Gutter { get; }

        /// <summary>Where a clip time is in the lane (the scroll is taken off).</summary>
        double X(int time);

        /// <summary>The clip time at an x of the lane (not snapped), 0 or more.</summary>
        int TimeAt(double x);

        /// <summary>The grid a new or moved point snaps to, in 1/240 ticks (1 or less: none).</summary>
        int GridUnits { get; }

        int BeatUnits { get; }

        int BarUnits { get; }

        /// <summary>The song's loop where it crosses the clip, in clip time, or null.</summary>
        (int From, int To)? Loop { get; }

        /// <summary>The play position in clip time, or -1.</summary>
        int Playhead { get; }

        void Zoom(double factor);

        void ScrollBy(double pixels);

        /// <summary>The zoom, the scroll, the grid or the loop changed: lanes draw again.</summary>
        event Action Changed;
    }

    /// <summary>Points copied from a lane, for pasting into this lane or another (the same parameter keeps its values; another is mapped across its range).</summary>
    internal static class EnvelopeClipboard
    {
        public static IReadOnlyList<AutoPoint> Points { get; private set; } = Array.Empty<AutoPoint>();
        public static string Source { get; private set; }
        public static int Min { get; private set; }
        public static int Max { get; private set; }

        public static void Set(IEnumerable<AutoPoint> points, string source, int min, int max)
        {
            Points = points.OrderBy(p => p.Time).ToList();
            Source = source;
            Min = min;
            Max = max;
        }

        public static void Clear() => Set(Array.Empty<AutoPoint>(), null, 0, 0);
    }

    /// <summary>Where a double click on a clip's curve asks for its lane (the window opens the Detail panel).</summary>
    internal static class EnvelopeOpener
    {
        public static Func<IClip, ColumnKey, bool> Open { get; set; }
    }

    internal sealed class EnvelopeLane : Control, ILaneGeometry
    {
        public const double LaneHeight = 76;
        const double Pad = 7;

        static readonly IBrush Fill = StudioTokens.Themed(() => NeoBuzz.Editor.Tokens.IsLight ? Color.Parse("#26C2185B") : Color.Parse("#30E0457B"));
        static readonly IBrush OutOfClip = SessionPalette.Pair("#141417", "#D8D8E0");
        static readonly IBrush LoopShade = StudioTokens.Themed(() => NeoBuzz.Editor.Tokens.IsLight ? Color.Parse("#1E1F6FC0") : Color.Parse("#223F8FD8"));
        static readonly IBrush OverlapFill = StudioTokens.Themed(() => Color.FromArgb(0x48, StudioTokens.Warning.R, StudioTokens.Warning.G, StudioTokens.Warning.B));
        static readonly IPen CurvePen = new Pen(PianoRoll.NoteFill, 2);
        static readonly IPen TrackerPen = new Pen(StudioTokens.TextSecondaryBrush, 1.25, new DashStyle(new double[] { 3, 2 }, 0));
        static readonly IPen RingPen = new Pen(StudioTokens.AccentBrush, 2.5);
        static readonly IPen DotEdge = new Pen(StudioTokens.SurfaceViewBrush, 1);
        static readonly IPen BandPen = new Pen(StudioTokens.AccentBrush, 1, new DashStyle(new double[] { 4, 3 }, 0));
        static readonly IPen MidPen = new Pen(StudioTokens.GridLineBrush, 1, new DashStyle(new double[] { 1, 3 }, 0));
        static readonly IPen LoopPen = new Pen(StudioTokens.AccentBrush, 1);
        static readonly IPen PlayPen = new Pen(StudioTokens.PlayBrush, 1.5);

        readonly ReBuzzCore core;
        readonly IEnvelopeAxis axis;
        readonly LaneController lane;
        IClip clip;

        public EnvelopeLane(ReBuzzCore core, IEnvelopeAxis axis, IClip clip, ColumnKey key)
        {
            this.core = core;
            this.axis = axis;
            this.clip = clip;
            ParamKey = key;
            lane = new LaneController(core, this, () => ClipEnvelopes.Read(this.clip, ParamKey), InvalidateVisual, () => $"{this.clip.Machine?.Name}|{ParamKey.Group}|{ParamKey.Index}|{ParamKey.Track}");
            Height = LaneHeight;
            ClipToBounds = true;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Cross);
            AutomationProperties.SetName(this, "Envelope lane");
            axis.Changed += OnAxis;
            Reload(force: true);
        }

        public ColumnKey ParamKey { get; }

        /// <summary>The clip the lane shows.</summary>
        public IClip Clip => clip;

        /// <summary>The envelope as the last reading found it (null when the parameter is gone).</summary>
        public ClipEnvelope Envelope => env;

        ClipEnvelope env => lane.Env;

        public AutoParam Param => env?.Param;

        /// <summary>The times of the selected points.</summary>
        public IReadOnlyCollection<int> Selection => lane.Selection;

        /// <summary>Why the last edit was refused, or null.</summary>
        public string Refusal => lane.Refusal;

        /// <summary>A gesture is going on (the stack leaves the lane alone).</summary>
        public bool IsDragging => lane.IsDragging;

        /// <summary>A point was added, moved, removed or changed (one step of the history).</summary>
        public event Action Edited { add => lane.Edited += value; remove => lane.Edited -= value; }

        /// <summary>The selection changed.</summary>
        public event Action SelectionChanged { add => lane.SelectionChanged += value; remove => lane.SelectionChanged -= value; }

        /// <summary>The clip time a paste goes to: where the pointer last pressed.</summary>
        public int InsertTime => lane.InsertTime;

        void OnAxis() => InvalidateVisual();

        /// <summary>Lets go of the axis (the stack took the lane away).</summary>
        public void Release() => axis.Changed -= OnAxis;

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            axis.Changed -= OnAxis;
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            axis.Changed -= OnAxis;
            axis.Changed += OnAxis;
        }

        /// <summary>The tooltip text the lane would show for the point under the pointer (or being dragged), or null.</summary>
        public string TipText => lane.TipText;

        // ------------------------------------------------------------------ reading

        /// <summary>Reads the clip again (after an edit, an undo, the agent); true when what the lane shows changed.</summary>
        public bool Reload(bool force = false) => lane.Reload(force);

        // ------------------------------------------------------------------ geometry

        double Top => Pad;
        double PlotHeight => Math.Max(8, Bounds.Height - 2 * Pad);

        double YOf(int value) => env == null ? 0 : Top + (1 - env.Param.Fraction(value)) * PlotHeight;

        int ValueAt(double y) => env == null ? 0 : env.Param.ValueAt(1 - (y - Top) / PlotHeight);

        double XOf(int time) => axis.X(time);

        int Last => Math.Max(0, (env?.Length ?? 1) - 1);

        // ILaneGeometry: the lane lies along the time axis of the view above it.
        Point ILaneGeometry.PointOf(int time, int value) => new Point(XOf(time), YOf(value));

        int ILaneGeometry.TimeAt(Point p) => axis.TimeAt(p.X);

        int ILaneGeometry.ValueAt(Point p) => ValueAt(p.Y);

        int ILaneGeometry.Snap(int time, bool noSnap)
        {
            int grid = axis.GridUnits;
            return !noSnap && grid > 1 ? (int)Math.Round(time / (double)grid) * grid : time;
        }

        bool ILaneGeometry.InClip(Point p) => XOf(0) <= p.X && axis.TimeAt(p.X) <= Last;

        /// <summary>Where a point is, for tests that click or drag it (in this control).</summary>
        internal Point PointOf(int time)
        {
            var p = env.Points.First(q => q.Time == time);
            return new Point(XOf(p.Time), YOf(p.Value));
        }

        /// <summary>A place of the lane at a clip time and a value, in this control.</summary>
        internal Point At(int time, int value) => new Point(XOf(time), YOf(value));

        internal int ValueOfY(double y) => ValueAt(y);

        // ------------------------------------------------------------------ drawing

        static FormattedText Text(string text, IBrush brush, double size) =>
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

        public override void Render(DrawingContext dc)
        {
            double w = Bounds.Width, h = Bounds.Height, g = axis.Gutter;
            dc.FillRectangle(StudioTokens.SurfaceViewBrush, new Rect(0, 0, w, h));
            if (env == null)
            {
                dc.DrawText(Text("This parameter is not on the clip's machine any more.", StudioTokens.TextSecondaryBrush, StudioTokens.FontBody), new Point(g + 8, 8));
                return;
            }
            double xEnd = XOf(env.Length);
            using (dc.PushClip(new Rect(g, 0, Math.Max(0, w - g), h)))
            {
                DrawGrid(dc, w, h, g, xEnd);
                if (axis.Loop is { } loop && loop.To > loop.From)
                {
                    double x0 = XOf(loop.From), x1 = XOf(loop.To);
                    dc.FillRectangle(LoopShade, new Rect(x0, 0, Math.Max(1, x1 - x0), h));
                    dc.DrawLine(LoopPen, new Point(Math.Round(x0) + 0.5, 0), new Point(Math.Round(x0) + 0.5, h));
                    dc.DrawLine(LoopPen, new Point(Math.Round(x1) + 0.5, 0), new Point(Math.Round(x1) + 0.5, h));
                }
                dc.DrawLine(MidPen, new Point(g, Top + PlotHeight / 2), new Point(w, Top + PlotHeight / 2));
                if (env.Tracker != null) DrawTracker(dc, env.Tracker);
                foreach (var o in env.Overlaps)
                {
                    double x0 = XOf(o.From), x1 = XOf(o.To);
                    var r = new Rect(x0, 0, Math.Max(3, x1 - x0), h);
                    dc.FillRectangle(OverlapFill, r);
                    dc.FillRectangle(StudioTokens.WarningBrush, new Rect(r.X, 0, r.Width, 3));
                    if (r.Width > 56) dc.DrawText(Text("overlap", StudioTokens.TextPrimaryBrush, StudioTokens.FontCaption), new Point(r.X + 3, 4));
                }
                var shown = lane.Shown();
                DrawCurve(dc, shown, xEnd);
                DrawPoints(dc, shown);
                if (lane.Current == LaneController.Gesture.Band && lane.Moved)
                {
                    var band = new Rect(lane.Down, lane.Now).Normalize();
                    dc.DrawRectangle(StudioTokens.BlockSelectionFill, BandPen, band);
                }
                if (axis.Playhead >= 0)
                {
                    double x = Math.Round(XOf(axis.Playhead)) + 0.5;
                    dc.DrawLine(PlayPen, new Point(x, 0), new Point(x, h));
                }
                if (xEnd < w) dc.FillRectangle(OutOfClip, new Rect(Math.Max(g, xEnd), 0, w - Math.Max(g, xEnd), h));
            }
            DrawGutter(dc, w, h, g);
            DrawTip(dc, w, h);
        }

        void DrawGrid(DrawingContext dc, double w, double h, double g, double xEnd)
        {
            int beat = Math.Max(1, axis.BeatUnits), bar = Math.Max(beat, axis.BarUnits);
            int first = Math.Max(0, axis.TimeAt(g) / beat - 1), last = axis.TimeAt(w) / beat + 1;
            double beatPx = Math.Abs(axis.X(beat) - axis.X(0));
            for (int k = first; k <= last && k - first < 4000; k++)
            {
                int t = k * beat;
                bool isBar = t % bar == 0;
                if (!isBar && beatPx < 6) continue;
                double x = Math.Round(XOf(t)) + 0.5;
                dc.DrawLine(isBar ? PianoRoll.BarLine : PianoRoll.BeatLine, new Point(x, 0), new Point(x, h));
            }
        }

        void DrawTracker(DrawingContext dc, AutoSegment seg)
        {
            var pts = seg.Points;
            double endX = XOf(seg.End);
            for (int i = 0; i < pts.Count; i++)
            {
                double x = XOf(pts[i].Time), y = YOf(pts[i].Value);
                double nx = i + 1 < pts.Count ? XOf(pts[i + 1].Time) : endX;
                dc.DrawLine(TrackerPen, new Point(x, y), new Point(nx, y));
                dc.FillRectangle(StudioTokens.TextSecondaryBrush, new Rect(x - 2.5, y - 2.5, 5, 5));
            }
        }

        void DrawCurve(DrawingContext dc, IReadOnlyList<AutoPoint> pts, double endX)
        {
            if (pts.Count == 0) return;
            double baseline = Top + PlotHeight;
            var path = new List<Point>();
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                double x = XOf(p.Time), y = YOf(p.Value);
                bool next = i + 1 < pts.Count;
                path.Add(new Point(x, y));
                if (p.Ramp && next) continue;       // the next point's own entry draws the line to it
                double nx = next ? XOf(pts[i + 1].Time) : endX;
                path.Add(new Point(nx, y));
            }
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(path[0].X, baseline), true);
                foreach (var pt in path) ctx.LineTo(pt);
                ctx.LineTo(new Point(path[^1].X, baseline));
                ctx.EndFigure(true);
            }
            dc.DrawGeometry(Fill, null, geometry);
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(path[0], false);
                foreach (var pt in path.Skip(1)) ctx.LineTo(pt);
                ctx.EndFigure(false);
            }
            dc.DrawGeometry(null, CurvePen, line);
        }

        void DrawPoints(DrawingContext dc, IReadOnlyList<AutoPoint> pts)
        {
            bool previewing = lane.Previewing;
            var movedTimes = previewing ? lane.Selection.Select(lane.LandedTime).ToHashSet() : null;
            foreach (var p in pts)
            {
                double x = XOf(p.Time), y = YOf(p.Value);
                bool sel = previewing ? movedTimes.Contains(p.Time) : lane.IsSelected(p.Time);
                double r = sel || p.Time == lane.HoverTime ? 5.5 : 4.5;
                var centre = new Point(x, y);
                if (sel)
                {
                    if (p.Ramp) dc.DrawEllipse(StudioTokens.White, RingPen, centre, r, r);
                    else dc.DrawRectangle(StudioTokens.White, RingPen, new Rect(x - r, y - r, 2 * r, 2 * r));
                }
                else if (p.Ramp) dc.DrawEllipse(PianoRoll.NoteFill, DotEdge, centre, r, r);
                else dc.DrawRectangle(PianoRoll.NoteFill, DotEdge, new Rect(x - r, y - r, 2 * r, 2 * r));
            }
        }

        void DrawGutter(DrawingContext dc, double w, double h, double g)
        {
            var back = g >= 20;
            if (back) dc.FillRectangle(StudioTokens.SurfacePanelBrush, new Rect(0, 0, g, h));
            double room = back ? g - 6 : 120;
            var hi = Text(env.Param.Describe(env.Param.Max), StudioTokens.TextSecondaryBrush, StudioTokens.FontCaption);
            var lo = Text(env.Param.Describe(env.Param.Min), StudioTokens.TextSecondaryBrush, StudioTokens.FontCaption);
            // A range too wide for the gutter ("20.0 kHz") is drawn whole just inside the lane, on a chip, rather than cut to "20.0 ...".
            bool fits = Math.Max(hi.Width, lo.Width) <= room;
            double x = fits ? (back ? 3 : 4) : g + 4;
            double most = Math.Max(20, fits ? room : w - x - 4);
            hi.MaxTextWidth = lo.MaxTextWidth = most;
            hi.MaxTextHeight = lo.MaxTextHeight = 14;
            hi.Trimming = lo.Trimming = TextTrimming.CharacterEllipsis;
            Point top = new(x, 2), bottom = new(x, h - lo.Height - 2);
            if (!fits)
            {
                dc.FillRectangle(StudioTokens.SurfacePanelBrush, new Rect(top.X - 2, top.Y - 1, hi.Width + 4, hi.Height + 2), 3);
                dc.FillRectangle(StudioTokens.SurfacePanelBrush, new Rect(bottom.X - 2, bottom.Y - 1, lo.Width + 4, lo.Height + 2), 3);
            }
            dc.DrawText(hi, top);
            dc.DrawText(lo, bottom);
            dc.DrawLine(PianoRoll.BarLine, new Point(0, 0.5), new Point(w, 0.5));
        }

        void DrawTip(DrawingContext dc, double w, double h)
        {
            string tip = TipText;
            if (tip == null || lane.TipPoint is not { } p) return;
            var text = Text(tip, StudioTokens.TextPrimaryBrush, StudioTokens.FontBody);
            double bw = text.Width + 14, bh = text.Height + 6;
            double x = XOf(p.Time) + 10, y = YOf(p.Value) - bh - 8;
            if (x + bw > w - 4) x = XOf(p.Time) - bw - 10;
            if (y < 2) y = YOf(p.Value) + 10;
            if (y + bh > h - 2) y = Math.Max(2, h - bh - 2);
            var r = new Rect(Math.Max(axis.Gutter + 2, x), y, bw, bh);
            dc.DrawRectangle(StudioTokens.SurfaceControlBrush, new Pen(StudioTokens.SurfaceBorderBrush, 1), r, 5, 5);
            dc.DrawText(text, new Point(r.X + 7, r.Y + 3));
        }

        // ------------------------------------------------------------------ the gestures (LaneController: shared with the tracker's lane)

        /// <summary>Selects points by their times (tests, and the stack).</summary>
        internal void SelectTimes(IEnumerable<int> times, bool extend = false) => lane.SelectTimes(times, extend);

        public void ClearSelection() => lane.ClearSelection();

        public bool SelectAll() => lane.SelectAll();

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            LaneInput.Pressed(lane, this, e, axis.Gutter);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            LaneInput.Moved(lane, this, e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            LaneInput.Released(lane, this, e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            lane.CaptureLost();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            lane.PointerExited();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            var mods = SessionView.Normalize(e.KeyModifiers);
            if (mods.HasFlag(KeyModifiers.Control)) axis.Zoom(e.Delta.Y > 0 ? 1.25 : 0.8);
            else if (mods.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > 0) axis.ScrollBy(-(Math.Abs(e.Delta.X) > 0 ? e.Delta.X : e.Delta.Y) * 60);
            else { base.OnPointerWheelChanged(e); return; }
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            LaneInput.Key(lane, e);
        }

        // ------------------------------------------------------------------ edits (the keys, the buttons and the Edit commands call these)

        public bool DeleteSelection() => lane.DeleteSelection();

        /// <summary>The line after the selected points a ramp or a step: ramps unless every one of them is a ramp already.</summary>
        public bool ToggleRamp() => lane.ToggleRamp();

        public bool SetRamp(bool ramp) => lane.SetRamp(ramp);

        public bool CopySelection() => lane.CopySelection();

        public bool Paste() => lane.Paste();

        /// <summary>Puts the copied points in at a clip time; they become the selection.</summary>
        public bool PasteAt(int at) => lane.PasteAt(at);

        /// <summary>The points and their types now selected: all ramps, all steps or mixed.</summary>
        internal (int Ramps, int Steps) SelectedKinds() => lane.SelectedKinds();
    }
}
