// Fork (docs/specs/clips.md §5.5, §6, T2): the envelope lane beside a tracker parameter column. It is EV1's lane turned on its side: time runs
// down the strip at the tracker's own row scale (a row of the grid is a row of the lane, and it scrolls with the grid), the value runs across it
// from the parameter's minimum on the left to its maximum on the right. It draws and edits the column's own lane of the clip store (the unmarked
// lane the column's cells are a view of), so a point placed here is a value in the column, and a value typed in the column is a point here.
//
// Mouse and keys are EV1's (LaneController, through LaneInput: one core for both lanes): click on empty space adds a point (a ramp), drag a point moves it (to a row; Alt keeps it off the
// rows), drag on empty space draws a band, R makes the selection steps or ramps, Delete removes it. Every gesture is one step of the song's
// history. The studio's Undo reaches it while a lane has the keyboard (ClipPage.SongHistory), as it does for the Detail panel's lane.
//
// Drawing: the curve is built once into geometry that is kept while neither the points, the strip's size nor the rows' scale change, so a
// scroll or the play position redraws without making anything new.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Detail
{
    using Control = Avalonia.Controls.Control;

    /// <summary>The studio's lanes for the tracker (BuzzGUI.Common.ClipLaneHost): a state parameter of the clip's machine has one.</summary>
    internal sealed class TrackerLaneProvider : IClipLaneProvider
    {
        readonly ReBuzzCore core;

        public TrackerLaneProvider(ReBuzzCore core) => this.core = core;

        /// <summary>The key of a parameter and track of a clip's machine, or null when it is not one a lane can show.</summary>
        public static ColumnKey? KeyOf(IClip clip, IParameter parameter, int track)
        {
            var machine = clip?.Machine;
            if (machine == null || parameter?.Group == null) return null;
            var groups = machine.ParameterGroups;
            int g = -1;
            for (int i = 0; i < groups.Count && g < 0; i++)
                if (ReferenceEquals(groups[i], parameter.Group)) g = i;
            for (int i = 0; i < groups.Count && g < 0; i++)
                if (groups[i].Type == parameter.Group.Type) g = i;
            if (g < 0) return null;
            var key = new ColumnKey(g, parameter.IndexInGroup, track);
            return AutomationParams.Find(machine, key) != null ? key : null;
        }

        public bool Supports(IClip clip, IParameter parameter, int track) => KeyOf(clip, parameter, track) != null;

        public object Create(IClip clip, IParameter parameter, int track, IClipRowAxis axis) =>
            KeyOf(clip, parameter, track) is { } key ? new TrackerLane(core, clip, key, axis) : null;
    }

    internal sealed class TrackerLane : Control, ILaneGeometry
    {
        const double Pad = 5;

        static readonly IBrush Fill = StudioTokens.Themed(() => NeoBuzz.Editor.Tokens.IsLight ? Color.Parse("#26C2185B") : Color.Parse("#30E0457B"));
        static readonly IBrush OutOfClip = SessionPalette.Pair("#141417", "#D8D8E0");
        static readonly IBrush Surround = StudioTokens.SurfaceViewBrush;
        static readonly IPen CurvePen = new Pen(PianoRoll.NoteFill, 2);
        static readonly IPen RingPen = new Pen(StudioTokens.AccentBrush, 2.5);
        static readonly IPen DotEdge = new Pen(StudioTokens.SurfaceViewBrush, 1);
        static readonly IPen BandPen = new Pen(StudioTokens.AccentBrush, 1, new DashStyle(new double[] { 4, 3 }, 0));
        static readonly IPen MidPen = new Pen(StudioTokens.GridLineBrush, 1, new DashStyle(new double[] { 1, 3 }, 0));
        static readonly IPen RowPen = new Pen(new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x90)), 1);
        static readonly IPen EdgePen = new Pen(StudioTokens.GridLineBrush, 1);
        static readonly IPen PlayPen = new Pen(StudioTokens.PlayBrush, 1.5);

        readonly ReBuzzCore core;
        readonly IClipRowAxis axis;
        readonly IClip clip;
        readonly LaneController lane;
        readonly List<int> rowBuffer = new();
        StreamGeometry fillGeometry, lineGeometry;
        int builtVersion = -1;
        double builtWidth = -1, builtExtent = -1, builtScale = -1;
        readonly List<Point> path = new();

        /// <summary>The lane that has the keyboard, or null: the studio's Undo and Redo go to the song's history while this is set.</summary>
        public static TrackerLane Focused { get; private set; }

        public TrackerLane(ReBuzzCore core, IClip clip, ColumnKey key, IClipRowAxis axis)
        {
            this.core = core;
            this.axis = axis;
            this.clip = clip;
            ParamKey = key;
            lane = new LaneController(core, this, () => ClipEnvelopes.ReadOwn(this.clip, ParamKey), InvalidateVisual,
                () => $"{this.clip.Machine?.Name}|{ParamKey.Group}|{ParamKey.Index}|{ParamKey.Track}");
            ClipToBounds = true;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Cross);
            AutomationProperties.SetName(this, "Tracker envelope lane");
            lane.Reload(force: true);
        }

        public ColumnKey ParamKey { get; }

        /// <summary>The clip the lane shows.</summary>
        public IClip Clip => clip;

        /// <summary>The curve as the last reading found it (the column's own lane); null when the parameter is gone.</summary>
        public ClipEnvelope Envelope => lane.Env;

        public AutoParam Param => lane.Env?.Param;

        public IReadOnlyCollection<int> Selection => lane.Selection;

        public string Refusal => lane.Refusal;

        public bool IsDragging => lane.IsDragging;

        /// <summary>A point was added, moved, removed or changed (one step of the history).</summary>
        public event Action Edited { add => lane.Edited += value; remove => lane.Edited -= value; }

        public event Action SelectionChanged { add => lane.SelectionChanged += value; remove => lane.SelectionChanged -= value; }

        public string TipText => lane.TipText;

        // ------------------------------------------------------------------ keeping up with the grid

        void OnView() => InvalidateVisual();

        void OnData()
        {
            if (lane.IsDragging) return;
            lane.Reload();
            InvalidateVisual();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            axis.Changed -= OnView;
            axis.Changed += OnView;
            axis.DataChanged -= OnData;
            axis.DataChanged += OnData;
            lane.Reload(force: true);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            axis.Changed -= OnView;
            axis.DataChanged -= OnData;
            if (Focused == this) Focused = null;
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            Focused = this;
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            if (Focused == this) Focused = null;
        }

        /// <summary>Reads the clip again; true when what the lane shows changed (tests, and after an undo).</summary>
        public bool Reload(bool force = false) => lane.Reload(force);

        // ------------------------------------------------------------------ geometry

        double PlotWidth => Math.Max(8, Bounds.Width - 2 * Pad);

        double XOf(int value) => lane.Env == null ? 0 : Pad + lane.Env.Param.Fraction(value) * PlotWidth;

        int ValueAt(double x) => lane.Env == null ? 0 : lane.Env.Param.ValueAt((x - Pad) / PlotWidth);

        double YOf(int time) => axis.Y(time);

        int Last => Math.Max(0, (lane.Env?.Length ?? 1) - 1);

        Point ILaneGeometry.PointOf(int time, int value) => new Point(XOf(value), YOf(time));

        int ILaneGeometry.TimeAt(Point p) => axis.TimeAt(p.Y);

        int ILaneGeometry.ValueAt(Point p) => ValueAt(p.X);

        int ILaneGeometry.Snap(int time, bool noSnap) => noSnap ? time : axis.SnapToRow(time);

        bool ILaneGeometry.InClip(Point p) => p.Y >= 0 && p.Y <= YOf(axis.Length) && axis.TimeAt(p.Y) <= Last;

        /// <summary>Where a point is, for tests that click or drag it (in this control).</summary>
        internal Point PointOf(int time)
        {
            var p = lane.Env.Points.First(q => q.Time == time);
            return new Point(XOf(p.Value), YOf(p.Time));
        }

        /// <summary>A place of the lane at a clip time and a value, in this control.</summary>
        internal Point At(int time, int value) => new Point(XOf(value), YOf(time));

        internal int ValueOfX(double x) => ValueAt(x);

        // ------------------------------------------------------------------ drawing

        /// <summary>Makes the geometry again when what it was made from changed; true when it did (a test counts these).</summary>
        internal bool Prepare()
        {
            double w = Bounds.Width, extent = axis.Extent, scale = axis.Y(axis.BeatUnits);
            if (lane.Version == builtVersion && w == builtWidth && extent == builtExtent && scale == builtScale && fillGeometry != null) return false;
            builtVersion = lane.Version;
            builtWidth = w;
            builtExtent = extent;
            builtScale = scale;
            Build();
            return true;
        }

        /// <summary>How many times the geometry was made (tests: none while the view only scrolls).</summary>
        internal int Builds { get; private set; }

        void Build()
        {
            Builds++;
            fillGeometry = lineGeometry = null;
            var env = lane.Env;
            if (env == null) return;
            var pts = lane.Shown();
            if (pts.Count == 0) return;
            double yEnd = YOf(axis.Length), baseline = Pad;
            path.Clear();
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                double x = XOf(p.Value), y = YOf(p.Time);
                bool next = i + 1 < pts.Count;
                path.Add(new Point(x, y));
                if (p.Ramp && next) continue;       // the next point's own entry draws the line to it
                path.Add(new Point(x, next ? YOf(pts[i + 1].Time) : yEnd));
            }
            fillGeometry = new StreamGeometry();
            using (var ctx = fillGeometry.Open())
            {
                ctx.BeginFigure(new Point(baseline, path[0].Y), true);
                foreach (var pt in path) ctx.LineTo(pt);
                ctx.LineTo(new Point(baseline, path[^1].Y));
                ctx.EndFigure(true);
            }
            lineGeometry = new StreamGeometry();
            using (var ctx = lineGeometry.Open())
            {
                ctx.BeginFigure(path[0], false);
                for (int i = 1; i < path.Count; i++) ctx.LineTo(path[i]);
                ctx.EndFigure(false);
            }
        }

        static FormattedText Text(string text, IBrush brush, double size) =>
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

        public override void Render(DrawingContext dc)
        {
            double w = Bounds.Width, h = Bounds.Height;
            var (viewTop, viewBottom) = axis.View;
            double top = Math.Max(0, viewTop - 2), bottom = Math.Min(h, viewBottom + 2);
            if (bottom <= top) return;
            dc.FillRectangle(Surround, new Rect(0, top, w, bottom - top));
            var env = lane.Env;
            if (env == null) return;
            Prepare();
            double yEnd = YOf(axis.Length);
            using (dc.PushClip(new Rect(0, top, w, bottom - top)))
            {
                DrawRules(dc, w, top, bottom);
                dc.DrawLine(MidPen, new Point(Pad + PlotWidth / 2, top), new Point(Pad + PlotWidth / 2, bottom));
                if (fillGeometry != null)
                {
                    dc.DrawGeometry(Fill, null, fillGeometry);
                    dc.DrawGeometry(null, CurvePen, lineGeometry);
                }
                DrawPoints(dc, top, bottom);
                if (lane.Current == LaneController.Gesture.Band && lane.Moved)
                    dc.DrawRectangle(StudioTokens.BlockSelectionFill, BandPen, new Rect(lane.Down, lane.Now).Normalize());
                if (axis.PlayPosition >= 0)
                {
                    double y = Math.Round(YOf(axis.PlayPosition)) + 0.5;
                    dc.DrawLine(PlayPen, new Point(0, y), new Point(w, y));
                }
                if (yEnd < bottom) dc.FillRectangle(OutOfClip, new Rect(0, Math.Max(top, yEnd), w, bottom - Math.Max(top, yEnd)));
            }
            dc.DrawLine(EdgePen, new Point(0.5, top), new Point(0.5, bottom));
            DrawTip(dc, w, h);
        }

        /// <summary>A line at each row, a stronger one at each beat and bar: the tracker's own rows, so a point on a row is on a line.</summary>
        void DrawRules(DrawingContext dc, double w, double top, double bottom)
        {
            int from = Math.Max(0, axis.TimeAt(top) - 1), to = axis.TimeAt(bottom) + 1;
            axis.RowStarts(from, to, rowBuffer);
            int beat = Math.Max(1, axis.BeatUnits), bar = Math.Max(beat, axis.BarUnits);
            for (int i = 0; i < rowBuffer.Count; i++)
            {
                int t = rowBuffer[i];
                double y = Math.Round(YOf(t)) + 0.5;
                if (t % bar == 0) dc.DrawLine(PianoRoll.BarLine, new Point(0, y), new Point(w, y));
                else if (t % beat == 0) dc.DrawLine(PianoRoll.BeatLine, new Point(0, y), new Point(w, y));
                else dc.DrawLine(RowPen, new Point(0, y), new Point(w, y));
            }
        }

        void DrawPoints(DrawingContext dc, double top, double bottom)
        {
            var pts = lane.Shown();
            bool previewing = lane.Previewing;
            var movedTimes = previewing ? lane.Selection.Select(lane.LandedTime).ToHashSet() : null;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                double x = XOf(p.Value), y = YOf(p.Time);
                if (y < top - 8 || y > bottom + 8) continue;
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

        void DrawTip(DrawingContext dc, double w, double h)
        {
            string tip = TipText;
            if (tip == null || lane.TipPoint is not { } p) return;
            var text = Text(tip, StudioTokens.TextPrimaryBrush, StudioTokens.FontBody);
            double bw = text.Width + 14, bh = text.Height + 6;
            double px = XOf(p.Value), py = YOf(p.Time);
            double x = px + 10, y = py - bh - 8;
            if (x + bw > w - 2) x = px - bw - 10;
            if (x < 2) x = 2;
            if (y < 2) y = py + 10;
            var r = new Rect(x, y, bw, bh);
            dc.DrawRectangle(StudioTokens.SurfaceControlBrush, new Pen(StudioTokens.SurfaceBorderBrush, 1), r, 5, 5);
            dc.DrawText(text, new Point(r.X + 7, r.Y + 3));
        }

        // ------------------------------------------------------------------ the gestures (LaneController: shared with the Detail panel's lane)

        internal void SelectTimes(IEnumerable<int> times, bool extend = false) => lane.SelectTimes(times, extend);

        public void ClearSelection() => lane.ClearSelection();

        public bool SelectAll() => lane.SelectAll();

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            LaneInput.Pressed(lane, this, e);
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

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            LaneInput.Key(lane, e);
        }

        // ------------------------------------------------------------------ edits

        public bool DeleteSelection() => lane.DeleteSelection();

        public bool ToggleRamp() => lane.ToggleRamp();

        public bool SetRamp(bool ramp) => lane.SetRamp(ramp);

        public bool CopySelection() => lane.CopySelection();

        public bool Paste() => lane.Paste();

        public bool PasteAt(int at) => lane.PasteAt(at);
    }
}
