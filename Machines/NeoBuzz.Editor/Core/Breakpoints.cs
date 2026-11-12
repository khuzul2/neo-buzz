#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): drawing a shape from
// breakpoints (Dye Essence's LFOs, docs/specs/dye-essence.md §8): points across one cycle, each
// segment to the next bent by a curve. The pointer arrives as plain calls (IGraphInput), routed by
// InteractiveGraph on WPF and Avalonia alike; the shape itself is the machine's (IBreakpoints), so
// what is drawn is what plays.

using System;
using System.Collections.Generic;

namespace NeoBuzz.Editor
{
    /// <summary>A breakpoint shape: where its points come from and where edits go.</summary>
    internal interface IBreakpoints
    {
        int Count { get; }
        /// <summary>A point's phase, 0 … 1.</summary>
        double X(int i);
        /// <summary>A point's value, −1 … 1.</summary>
        double Y(int i);
        /// <summary>The curve of the segment from point <paramref name="i"/> to the next, −1 … 1 (0 straight).</summary>
        double Curve(int i);
        /// <summary>The shape's value at a phase, as it plays.</summary>
        double Value(double x);
        int MaxPoints { get; }
        /// <summary>Whether it can be edited now (a shape shown but not playing is drawn dimmed until pressed).</summary>
        bool Live { get; }
        /// <summary>Called on a press before anything changes: makes the shape live (it starts from what was shown).</summary>
        void MakeLive();
        /// <summary>Replaces the points (phase, value, curve), in order of phase.</summary>
        void Set(IReadOnlyList<(double X, double Y, double Curve)> points);
        /// <summary>A gesture starts (a press): its changes are one undo step, ended by <see cref="EndEdit"/>.</summary>
        void BeginEdit();
        void EndEdit();
    }

    /// <summary>
    /// The pointer on a breakpoint shape. Dragging a point moves it (between its neighbours: two points at one phase
    /// make a step); pressing elsewhere adds a point there and drags it; a double-click on a point removes it. The
    /// small handle in the middle of each segment bends it: drag it up or down, or turn the wheel over the segment; a
    /// double-click straightens it. Shift moves a tenth as far.
    /// </summary>
    internal sealed class BreakpointInput : IGraphInput
    {
        public const double PointRadius = 4, Reach = 7;
        const double FineScale = 0.1, CurvePerPixel = 1.0 / 60, CurvePerNotch = 0.05;

        readonly IBreakpoints shape;
        List<(double X, double Y, double Curve)> points;
        int dragPoint = -1, dragCurve = -1;
        bool editing;
        double lastX, lastY;

        public BreakpointInput(IBreakpoints shape)
        {
            this.shape = shape;
        }

        /// <summary>Where the shape is drawn (inside the graph's frame); set by <see cref="BreakpointPainter"/>.</summary>
        public NeoRect Plot { get; set; } = new NeoRect(0, 0, 1, 1);

        public int HoverPoint { get; private set; } = -1;
        public int HoverCurve { get; private set; } = -1;
        /// <summary>The pointer is over the graph (the curve handles show).</summary>
        public bool Inside { get; private set; }
        public bool Capturing => dragPoint >= 0 || dragCurve >= 0;
        public int Dragging => dragPoint;

        public double PX(double x) => Plot.X + x * Plot.Width;
        public double PY(double y) => Plot.Y + (1 - y) / 2 * Plot.Height;
        double ToX(double px) => Math.Clamp((px - Plot.X) / Math.Max(1, Plot.Width), 0, 1);
        double ToY(double py) => Math.Clamp(1 - 2 * (py - Plot.Y) / Math.Max(1, Plot.Height), -1, 1);

        /// <summary>The middle of segment <paramref name="i"/> (to the next point, the last one to the first a cycle on), where its handle is.</summary>
        public NeoPoint CurveHandle(int i)
        {
            int n = shape.Count;
            double x0 = shape.X(i), x1 = i + 1 < n ? shape.X(i + 1) : shape.X(0) + 1;
            double xm = (x0 + x1) / 2;
            if (xm >= 1) xm -= 1;
            return new NeoPoint(PX(xm), PY(shape.Value(xm)));
        }

        /// <summary>Whether segment <paramref name="i"/> is wide enough to show and take its handle.</summary>
        public bool HasCurveHandle(int i)
        {
            int n = shape.Count;
            if (n < 2) return false;
            double x0 = shape.X(i), x1 = i + 1 < n ? shape.X(i + 1) : shape.X(0) + 1;
            return (x1 - x0) * Plot.Width >= 4 * Reach;
        }

        int PointAt(double px, double py)
        {
            int best = -1;
            double bestD = Reach * Reach;
            for (int i = 0; i < shape.Count; i++)
            {
                double dx = PX(shape.X(i)) - px, dy = PY(shape.Y(i)) - py, d = dx * dx + dy * dy;
                if (d <= bestD)
                {
                    best = i;
                    bestD = d;
                }
            }
            return best;
        }

        int CurveAt(double px, double py)
        {
            for (int i = 0; i < shape.Count; i++)
            {
                if (!HasCurveHandle(i)) continue;
                var h = CurveHandle(i);
                if (Math.Abs(h.X - px) <= Reach && Math.Abs(h.Y - py) <= Reach) return i;
            }
            return -1;
        }

        /// <summary>The segment a phase is in (its first point; the last point's before the first).</summary>
        int SegmentAt(double x)
        {
            int i = shape.Count - 1;
            while (i >= 0 && shape.X(i) > x) i--;
            return i < 0 ? shape.Count - 1 : i;
        }

        List<(double X, double Y, double Curve)> Points()
        {
            var list = new List<(double, double, double)>(shape.Count + 1);
            for (int i = 0; i < shape.Count; i++) list.Add((shape.X(i), shape.Y(i), shape.Curve(i)));
            return list;
        }

        void Begin()
        {
            if (editing) return;
            editing = true;
            shape.BeginEdit();
        }

        void End()
        {
            if (!editing) return;
            editing = false;
            shape.EndEdit();
        }

        public bool Down(double x, double y, int clicks, bool fine)
        {
            if (!Plot.Contains(x, y) && PointAt(x, y) < 0) return false;
            Begin();
            if (!shape.Live)
            {
                // A shape shown but not playing: the press makes it the one that plays, from what was shown.
                shape.MakeLive();
                End();
                return true;
            }
            lastX = x;
            lastY = y;
            int p = PointAt(x, y);
            if (p >= 0)
            {
                if (clicks >= 2 && shape.Count > 1)
                {
                    points = Points();
                    points.RemoveAt(p);
                    shape.Set(points);
                    HoverPoint = -1;
                    End();
                    return true;
                }
                dragPoint = p;
                points = Points();
                return true;
            }
            int c = CurveAt(x, y);
            if (c >= 0)
            {
                if (clicks >= 2)
                {
                    points = Points();
                    points[c] = (points[c].X, points[c].Y, 0);
                    shape.Set(points);
                    End();
                    return true;
                }
                dragCurve = c;
                points = Points();
                return true;
            }
            if (shape.Count >= shape.MaxPoints)
            {
                End();
                return true;
            }
            // A new point where the pointer is, dragged from there.
            points = Points();
            double nx = ToX(x), ny = ToY(y);
            int at = 0;
            while (at < points.Count && points[at].X <= nx) at++;
            // It takes the curve of the segment it splits; the segment before it runs straight to it.
            double curve = at > 0 ? points[at - 1].Curve : points.Count > 0 ? points[points.Count - 1].Curve : 0;
            points.Insert(at, (nx, ny, curve));
            shape.Set(points);
            dragPoint = at;
            HoverPoint = at;
            return true;
        }

        public bool Move(double x, double y, bool fine)
        {
            if (dragPoint >= 0)
            {
                double s = fine ? FineScale : 1;
                var p = points[dragPoint];
                double nx = fine ? p.X + (x - lastX) * s / Math.Max(1, Plot.Width) : ToX(x);
                double ny = fine ? p.Y - 2 * (y - lastY) * s / Math.Max(1, Plot.Height) : ToY(y);
                double lo = dragPoint > 0 ? points[dragPoint - 1].X : 0;
                double hi = dragPoint + 1 < points.Count ? points[dragPoint + 1].X : 1;
                points[dragPoint] = (Math.Clamp(nx, lo, hi), Math.Clamp(ny, -1, 1), p.Curve);
                lastX = x;
                lastY = y;
                shape.Set(points);
                return true;
            }
            if (dragCurve >= 0)
            {
                // Up bends the segment's middle up: a fast start on a rising segment (curve below 0), a slow one on a falling one.
                var p = points[dragCurve];
                double up = (lastY - y) * CurvePerPixel * (fine ? FineScale : 1);
                points[dragCurve] = (p.X, p.Y, Math.Clamp(p.Curve - up * Rising(points, dragCurve), -1, 1));
                lastX = x;
                lastY = y;
                shape.Set(points);
                return true;
            }
            bool wasInside = Inside;
            Inside = Plot.Contains(x, y);
            int hp = PointAt(x, y), hc = hp < 0 ? CurveAt(x, y) : -1;
            bool changed = hp != HoverPoint || hc != HoverCurve || wasInside != Inside;
            HoverPoint = hp;
            HoverCurve = hc;
            return changed;
        }

        /// <summary>1 when segment <paramref name="i"/> rises (or stays level), −1 when it falls.</summary>
        static double Rising(List<(double X, double Y, double Curve)> pts, int i) =>
            (i + 1 < pts.Count ? pts[i + 1].Y : pts[0].Y) >= pts[i].Y ? 1 : -1;

        public bool Up()
        {
            bool was = Capturing || editing;
            dragPoint = -1;
            dragCurve = -1;
            End();
            return was;
        }

        public bool Wheel(double x, double y, int delta, bool fine)
        {
            if (!Plot.Contains(x, y) || shape.Count < 2 || !shape.Live) return false;
            int i = SegmentAt(ToX(x));
            var pts = Points();
            // As a drag: the wheel up bends the middle up.
            double up = CurvePerNotch * (fine ? FineScale : 1) * Math.Sign(delta);
            Begin();
            pts[i] = (pts[i].X, pts[i].Y, Math.Clamp(pts[i].Curve - up * Rising(pts, i), -1, 1));
            shape.Set(pts);
            End();
            return true;
        }

        public bool Leave()
        {
            bool changed = Inside || HoverPoint >= 0 || HoverCurve >= 0;
            Inside = false;
            HoverPoint = HoverCurve = -1;
            return changed;
        }
    }

    /// <summary>Draws a breakpoint shape: the curve as it plays, its points, and (while the pointer is over it) the curve handles.</summary>
    internal static class BreakpointPainter
    {
        public const double Margin = 7;

        public static void Draw(INeoCanvas c, NeoRect r, IBreakpoints shape, BreakpointInput input, NeoColor accent)
        {
            var plot = new NeoRect(r.X + Margin, r.Y + Margin, Math.Max(1, r.Width - 2 * Margin), Math.Max(1, r.Height - 2 * Margin));
            input.Plot = plot;
            var grid = new NeoStroke(Palette.Grid, 1);
            for (int q = 1; q < 4; q++)
            {
                double gx = Math.Round(input.PX(q / 4.0)) + 0.5;
                c.Line(new NeoPoint(gx, plot.Top), new NeoPoint(gx, plot.Bottom), grid);
            }
            double zero = Math.Round(input.PY(0)) + 0.5;
            c.Line(new NeoPoint(plot.Left, zero), new NeoPoint(plot.Right, zero), grid);

            bool live = shape.Live;
            var colour = live ? accent : Palette.DimText;
            int steps = Math.Max(2, (int)plot.Width);
            var line = new NeoPath();
            var fill = new NeoPath().MoveTo(plot.Left, zero);
            for (int i = 0; i <= steps; i++)
            {
                double x = i / (double)steps;
                var p = new NeoPoint(input.PX(x), input.PY(shape.Value(Math.Min(x, 0.999999))));
                line.LineTo(p);
                fill.LineTo(p);
                // A step (two points at one phase): straight down or up at it.
                if (i < steps)
                {
                    double x1 = (i + 1) / (double)steps;
                    for (int k = 0; k + 1 < shape.Count; k++)
                        if (shape.X(k) == shape.X(k + 1) && shape.X(k) > x && shape.X(k) <= x1)
                        {
                            var a = new NeoPoint(input.PX(shape.X(k)), input.PY(shape.Y(k)));
                            var b = new NeoPoint(input.PX(shape.X(k)), input.PY(shape.Y(k + 1)));
                            line.LineTo(a); line.LineTo(b);
                            fill.LineTo(a); fill.LineTo(b);
                        }
                }
            }
            fill.LineTo(plot.Right, zero).Close();
            c.Path(fill, colour.WithAlpha(live ? (byte)0x2C : (byte)0x18), NeoStroke.None);
            c.Path(line, NeoFill.None, new NeoStroke(colour, live ? 1.6 : 1.2));
            if (!live) return;

            if (input.Inside || input.Capturing)
                for (int i = 0; i < shape.Count; i++)
                {
                    if (!input.HasCurveHandle(i)) continue;
                    var h = input.CurveHandle(i);
                    bool hot = i == input.HoverCurve;
                    c.Ellipse(h, hot ? 3.5 : 2.5, hot ? 3.5 : 2.5, Palette.Background, new NeoStroke(accent.WithAlpha(hot ? (byte)0xFF : (byte)0xA0), 1.2));
                }
            for (int i = 0; i < shape.Count; i++)
            {
                var p = new NeoPoint(input.PX(shape.X(i)), input.PY(shape.Y(i)));
                bool hot = i == input.HoverPoint || i == input.Dragging;
                double rad = hot ? BreakpointInput.PointRadius + 1.5 : BreakpointInput.PointRadius;
                c.Ellipse(p, rad, rad, hot ? (NeoFill)accent : Palette.Panel, new NeoStroke(accent, 1.5));
            }
        }
    }
}
