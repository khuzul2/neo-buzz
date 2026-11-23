// Fork (docs/specs/daw-modes.md EV1, §8.8): one envelope of a clip, drawn as a lane under the Detail panel's piano roll (or the Audio page's
// waveform): the points of the clip's engine automation column for one state parameter, joined by ramps (a straight line to the next
// point) or held as steps, on the time axis of the view above it. The Tracker's own column for the parameter shows as read-only steps, and
// where it and the points both set the parameter the lane marks an overlap (AR4's rule, AutomationReader.OverlapsOf).
//
// Mouse: click on empty space adds a point (the grid; Shift turns it off); drag a point moves it, and a selection moves together; drag on empty
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
        public static Func<IPattern, ColumnKey, bool> Open { get; set; }
    }

    internal sealed class EnvelopeLane : Control
    {
        public const double LaneHeight = 76;
        const double Pad = 7;
        const double Grab = 8;
        const double DragDistance = 3;

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

        enum Gesture { None, Points, Band, Add }

        readonly ReBuzzCore core;
        readonly IEnvelopeAxis axis;
        readonly ClipEnvelopeEditor editor;
        readonly HashSet<int> selected = new();
        IPattern pattern;
        ClipEnvelope env;
        long signature;
        string refusal;

        Gesture gesture;
        Point down, now;
        bool moved, additive, collapseOnRelease;
        AutoPoint grabbed;
        int previewDt, previewDv;
        int hoverTime = -1;
        int insertTime;

        public EnvelopeLane(ReBuzzCore core, IEnvelopeAxis axis, IPattern pattern, ColumnKey key)
        {
            this.core = core;
            this.axis = axis;
            this.pattern = pattern;
            ParamKey = key;
            editor = new ClipEnvelopeEditor(core);
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
        public IPattern Pattern => pattern;

        /// <summary>The envelope as the last reading found it (null when the parameter is gone).</summary>
        public ClipEnvelope Envelope => env;

        public AutoParam Param => env?.Param;

        /// <summary>The times of the selected points.</summary>
        public IReadOnlyCollection<int> Selection => selected;

        /// <summary>Why the last edit was refused, or null.</summary>
        public string Refusal => refusal;

        /// <summary>A gesture is going on (the stack leaves the lane alone).</summary>
        public bool IsDragging => gesture != Gesture.None;

        /// <summary>A point was added, moved, removed or changed (one step of the history).</summary>
        public event Action Edited;

        /// <summary>The selection changed.</summary>
        public event Action SelectionChanged;

        /// <summary>The clip time a paste goes to: where the pointer last pressed.</summary>
        public int InsertTime => insertTime;

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
        public string TipText => TipFor(gesture == Gesture.Points && moved ? grabbed.Time : hoverTime);

        // ------------------------------------------------------------------ reading

        static long Sign(ClipEnvelope e)
        {
            if (e == null) return -1;
            long sum = e.Length;
            foreach (var p in e.Engine.Points) sum = sum * 31 + p.Time * 7 + p.Value * 131 + (p.Ramp ? 1 : 0);
            if (e.Tracker != null) foreach (var p in e.Tracker.Points) sum = sum * 37 + p.Time * 11 + p.Value * 17;
            return sum * 31 + e.Overlaps.Count;
        }

        /// <summary>Reads the clip again (after an edit, an undo, the agent); true when what the lane shows changed.</summary>
        public bool Reload(bool force = false)
        {
            var next = ClipEnvelopes.Read(pattern, ParamKey);
            long sig = Sign(next);
            if (!force && sig == signature && ReferenceEquals(next?.Pattern, env?.Pattern)) return false;
            signature = sig;
            env = next;
            var times = env?.Points.Select(p => p.Time).ToHashSet() ?? new HashSet<int>();
            if (selected.RemoveWhere(t => !times.Contains(t)) > 0) SelectionChanged?.Invoke();
            InvalidateVisual();
            return true;
        }

        // ------------------------------------------------------------------ geometry

        double Top => Pad;
        double PlotHeight => Math.Max(8, Bounds.Height - 2 * Pad);

        double YOf(int value) => env == null ? 0 : Top + (1 - env.Param.Fraction(value)) * PlotHeight;

        int ValueAt(double y) => env == null ? 0 : env.Param.ValueAt(1 - (y - Top) / PlotHeight);

        double XOf(int time) => axis.X(time);

        int Last => Math.Max(0, (env?.Length ?? 1) - 1);

        int Snap(int time, bool noSnap)
        {
            int grid = axis.GridUnits;
            if (!noSnap && grid > 1) time = (int)Math.Round(time / (double)grid) * grid;
            return Math.Clamp(time, 0, Last);
        }

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

        IReadOnlyList<AutoPoint> Shown()
        {
            if (env == null) return Array.Empty<AutoPoint>();
            if (gesture != Gesture.Points || !moved) return env.Points;
            var moving = env.Points.Where(p => selected.Contains(p.Time)).Select(Displaced).ToList();
            var landed = moving.Select(p => p.Time).ToHashSet();
            return env.Points.Where(p => !selected.Contains(p.Time) && !landed.Contains(p.Time)).Concat(moving).OrderBy(p => p.Time).ToList();
        }

        AutoPoint Displaced(AutoPoint p) => p with
        {
            Time = Math.Clamp(p.Time + previewDt, 0, Last),
            Value = Math.Clamp(p.Value + previewDv, env.Param.Min, env.Param.Max),
        };

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
                var shown = Shown();
                DrawCurve(dc, shown, xEnd);
                DrawPoints(dc, shown);
                if (gesture == Gesture.Band && moved)
                {
                    var band = new Rect(down, now).Normalize();
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
            bool previewing = gesture == Gesture.Points && moved;
            var movedTimes = previewing ? selected.Select(t => Math.Clamp(t + previewDt, 0, Last)).ToHashSet() : null;
            foreach (var p in pts)
            {
                double x = XOf(p.Time), y = YOf(p.Value);
                bool sel = previewing ? movedTimes.Contains(p.Time) : selected.Contains(p.Time);
                double r = sel || p.Time == hoverTime ? 5.5 : 4.5;
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
            hi.MaxTextWidth = lo.MaxTextWidth = room;
            hi.MaxTextHeight = lo.MaxTextHeight = 14;
            hi.Trimming = lo.Trimming = TextTrimming.CharacterEllipsis;
            double x = back ? 3 : 4;
            dc.DrawText(hi, new Point(x, 2));
            dc.DrawText(lo, new Point(x, h - lo.Height - 2));
            dc.DrawLine(PianoRoll.BarLine, new Point(0, 0.5), new Point(w, 0.5));
        }

        string TipFor(int time)
        {
            if (env == null || time < 0) return null;
            AutoPoint? p = gesture == Gesture.Points && moved && time == grabbed.Time ? Displaced(grabbed) : env.Points.Cast<AutoPoint?>().FirstOrDefault(q => q!.Value.Time == time);
            if (p is not { } pt) return null;
            int tpb = Math.Max(1, core.TPB), bpb = Math.Max(1, core.Session?.BeatsPerBar ?? 4);
            return $"{env.Param.Describe(pt.Value)} · {TimeText.Position(pt.Time / PatternEvent.TimeBase, tpb, bpb)} · {(pt.Ramp ? "ramp" : "step")}";
        }

        void DrawTip(DrawingContext dc, double w, double h)
        {
            string tip = TipText;
            if (tip == null) return;
            var p = (gesture == Gesture.Points && moved ? Displaced(grabbed) : env.Points.FirstOrDefault(q => q.Time == hoverTime));
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

        // ------------------------------------------------------------------ the gestures

        int HitTest(Point p)
        {
            if (env == null) return -1;
            int best = -1;
            double bestD = Grab * Grab;
            foreach (var pt in env.Points)
            {
                double dx = XOf(pt.Time) - p.X, dy = YOf(pt.Value) - p.Y;
                double d = dx * dx + dy * dy;
                if (d <= bestD) { bestD = d; best = pt.Time; }
            }
            return best;
        }

        static bool Additive(KeyModifiers mods) => mods.HasFlag(KeyModifiers.Shift) || SessionView.Normalize(mods).HasFlag(KeyModifiers.Control);

        void Select(IEnumerable<int> times, bool extend)
        {
            if (!extend) selected.Clear();
            foreach (int t in times) selected.Add(t);
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }

        public void ClearSelection()
        {
            if (selected.Count == 0) return;
            selected.Clear();
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }

        /// <summary>Selects points by their times (tests, and the stack).</summary>
        internal void SelectTimes(IEnumerable<int> times, bool extend = false) => Select(times.Where(t => env?.Points.Any(p => p.Time == t) == true), extend);

        public bool SelectAll()
        {
            if (env == null) return false;
            Select(env.Points.Select(p => p.Time), false);
            return true;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (env == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var p = e.GetPosition(this);
            if (p.X < axis.Gutter) return;
            Focus();
            refusal = null;
            down = now = p;
            moved = false;
            additive = Additive(e.KeyModifiers);
            int hit = HitTest(p);
            insertTime = Snap(axis.TimeAt(p.X), false);
            if (hit >= 0)
            {
                if (e.ClickCount == 2)
                {
                    selected.Clear();
                    Commit(editor.Delete(env, new[] { hit }));
                    SelectionChanged?.Invoke();
                    e.Handled = true;
                    return;
                }
                if (additive)
                {
                    // Shift/Ctrl/Cmd-click toggles the point; one that is now selected can be dragged at once (Shift is also "no snap" in a drag).
                    if (!selected.Remove(hit)) selected.Add(hit);
                    SelectionChanged?.Invoke();
                    if (!selected.Contains(hit))
                    {
                        InvalidateVisual();
                        e.Handled = true;
                        return;
                    }
                    collapseOnRelease = false;
                }
                else
                {
                    collapseOnRelease = selected.Contains(hit) && selected.Count > 1;
                    if (!selected.Contains(hit)) { selected.Clear(); selected.Add(hit); SelectionChanged?.Invoke(); }
                }
                grabbed = env.Points.First(q => q.Time == hit);
                gesture = Gesture.Points;
                previewDt = previewDv = 0;
            }
            else
            {
                gesture = XOf(0) <= p.X && axis.TimeAt(p.X) <= Last && !additive ? Gesture.Add : Gesture.Band;
                if (gesture == Gesture.Add && selected.Count > 0) { selected.Clear(); SelectionChanged?.Invoke(); }
            }
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (env == null) return;
            var p = e.GetPosition(this);
            if (gesture == Gesture.None)
            {
                int hit = HitTest(p);
                if (hit != hoverTime) { hoverTime = hit; InvalidateVisual(); }
                return;
            }
            now = p;
            if (!moved)
            {
                if (Math.Abs(p.X - down.X) < DragDistance && Math.Abs(p.Y - down.Y) < DragDistance) return;
                moved = true;
                if (gesture == Gesture.Add) gesture = Gesture.Band;
            }
            bool noSnap = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (gesture == Gesture.Points)
            {
                int target = Snap(axis.TimeAt(p.X), noSnap);
                int lo = selected.Min(), hi = selected.Max();
                previewDt = Math.Clamp(target - grabbed.Time, -lo, Last - hi);
                previewDv = ValueAt(p.Y) - grabbed.Value;
            }
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton != MouseButton.Left || gesture == Gesture.None) return;
            var p = e.GetPosition(this);
            now = p;
            var g = gesture;
            bool noSnap = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            gesture = Gesture.None;
            e.Pointer.Capture(null);
            switch (g)
            {
                case Gesture.Add:
                {
                    int time = Snap(axis.TimeAt(p.X), noSnap);
                    insertTime = time;
                    string why = editor.AddPoint(env, time, ValueAt(p.Y));
                    selected.Clear();
                    if (why == null) selected.Add(time);
                    Commit(why);
                    SelectionChanged?.Invoke();
                    break;
                }
                case Gesture.Points when moved:
                {
                    int dt = previewDt, dv = previewDv;
                    var times = selected.ToList();
                    string why = editor.Move(env, times, dt, dv);
                    selected.Clear();
                    foreach (int t in times) selected.Add(Math.Clamp(t + dt, 0, Last));
                    Commit(why);
                    SelectionChanged?.Invoke();
                    break;
                }
                case Gesture.Points:
                    if (collapseOnRelease && !additive) { selected.Clear(); selected.Add(grabbed.Time); SelectionChanged?.Invoke(); }
                    InvalidateVisual();
                    break;
                case Gesture.Band:
                {
                    var band = new Rect(down, p).Normalize();
                    if (!moved) { if (!additive) { selected.Clear(); SelectionChanged?.Invoke(); } }
                    else
                    {
                        var inside = env.Points.Where(q => band.Contains(new Point(XOf(q.Time), YOf(q.Value)))).Select(q => q.Time);
                        Select(inside, additive);
                    }
                    InvalidateVisual();
                    break;
                }
            }
            moved = false;
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (gesture == Gesture.None) return;
            gesture = Gesture.None;
            moved = false;
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (hoverTime >= 0 && gesture == Gesture.None) { hoverTime = -1; InvalidateVisual(); }
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
            var mods = SessionView.Normalize(e.KeyModifiers);
            bool ctrl = mods == KeyModifiers.Control;
            bool handled = true;
            switch (e.Key)
            {
                case Key.Delete:
                case Key.Back when mods == KeyModifiers.None:
                    DeleteSelection();
                    break;
                case Key.R when mods == KeyModifiers.None:
                    ToggleRamp();
                    break;
                case Key.A when ctrl:
                    SelectAll();
                    break;
                case Key.C when ctrl:
                    CopySelection();
                    break;
                case Key.X when ctrl:
                    CopySelection();
                    DeleteSelection();
                    break;
                case Key.V when ctrl:
                    Paste();
                    break;
                case Key.Escape:
                    if (gesture != Gesture.None) { gesture = Gesture.None; moved = false; }
                    else ClearSelection();
                    InvalidateVisual();
                    break;
                default:
                    handled = false;
                    break;
            }
            if (handled) e.Handled = true;
        }

        // ------------------------------------------------------------------ edits (the keys, the buttons and the Edit commands call these)

        void Commit(string why)
        {
            refusal = why;
            Reload(force: true);
            Edited?.Invoke();
        }

        public bool DeleteSelection()
        {
            if (env == null || selected.Count == 0) return false;
            var times = selected.ToList();
            selected.Clear();
            Commit(editor.Delete(env, times));
            SelectionChanged?.Invoke();
            return true;
        }

        /// <summary>The line after the selected points a ramp or a step: ramps unless every one of them is a ramp already.</summary>
        public bool ToggleRamp()
        {
            if (env == null || selected.Count == 0) return false;
            bool allRamps = env.Points.Where(p => selected.Contains(p.Time)).All(p => p.Ramp);
            return SetRamp(!allRamps);
        }

        public bool SetRamp(bool ramp)
        {
            if (env == null || selected.Count == 0) return false;
            Commit(editor.SetRamp(env, selected.ToList(), ramp));
            return true;
        }

        public bool CopySelection()
        {
            if (env == null || selected.Count == 0) return false;
            EnvelopeClipboard.Set(env.Points.Where(p => selected.Contains(p.Time)), SourceId, env.Param.Min, env.Param.Max);
            return true;
        }

        string SourceId => $"{pattern.Machine?.Name}|{ParamKey.Group}|{ParamKey.Index}|{ParamKey.Track}";

        public bool Paste() => PasteAt(insertTime);

        /// <summary>Puts the copied points in at a clip time; they become the selection.</summary>
        public bool PasteAt(int at)
        {
            var copied = EnvelopeClipboard.Points;
            if (env == null || copied.Count == 0) return false;
            bool same = EnvelopeClipboard.Source == SourceId;
            var points = copied.Select(p => same ? p : p with
            {
                Value = EnvelopeClipboard.Max > EnvelopeClipboard.Min
                    ? env.Param.ValueAt((p.Value - EnvelopeClipboard.Min) / (double)(EnvelopeClipboard.Max - EnvelopeClipboard.Min))
                    : env.Param.Min,
            }).ToList();
            int shift = Math.Clamp(at, 0, Last) - points.Min(p => p.Time);
            var landed = points.Select(p => p.Time + shift).Where(t => t >= 0 && t <= Last).ToList();
            string why = editor.Paste(env, points, at);
            selected.Clear();
            if (why == null) foreach (int t in landed) selected.Add(t);
            Commit(why);
            SelectionChanged?.Invoke();
            return why == null;
        }

        /// <summary>The points and their types now selected: all ramps, all steps or mixed.</summary>
        internal (int Ramps, int Steps) SelectedKinds()
        {
            if (env == null) return (0, 0);
            var sel = env.Points.Where(p => selected.Contains(p.Time)).ToList();
            return (sel.Count(p => p.Ramp), sel.Count(p => !p.Ramp));
        }
    }
}
