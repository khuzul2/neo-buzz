#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): the oscilloscope and the
// polar vectorscope (Neo Scope, docs/specs/machines.md §5). A sweep comes as the lowest and highest
// value of each column; the painter merges columns into pixels and fills each trace as one band
// (its highest values left to right, its lowest back, at least a line's width thick), so its cost
// follows the screen's width, not the sweep's length, and a dense trace costs a fill, not a stroke
// of every pixel's span. A sweep shorter than the screen is a line through its samples. Painters
// take plain values, so they are tested on Linux.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NeoBuzz.Editor
{
    /// <summary>One sweep for the painter: per column, the lowest and highest value of trace A (and B), empty where min &gt; max.</summary>
    internal struct ScopeSweep
    {
        public float[] AMin, AMax, BMin, BMax;

        /// <summary>Columns across the screen, the sweep's samples, and the columns written so far (a sweep in progress).</summary>
        public int Columns, Samples, Filled;

        /// <summary>Two traces (B drawn too).</summary>
        public bool Two;

        /// <summary>A triggered sweep's first sample is this far (in samples, 0 … 1) after the trigger's crossing, at the left edge.</summary>
        public double Offset;

        public bool IsEmpty => AMin == null || Columns <= 0;
    }

    /// <summary>What the oscilloscope shows, and the paths it reuses from frame to frame.</summary>
    internal sealed class ScopeView
    {
        /// <summary>The newest complete sweep (may be empty).</summary>
        public ScopeSweep Sweep;

        /// <summary>The sweep in progress, drawn over <see cref="Sweep"/> where it has samples (long sweeps); empty for none.</summary>
        public ScopeSweep Live;

        /// <summary>Earlier sweeps, oldest first, each with its opacity (0 … 1): the trail.</summary>
        public readonly List<(ScopeSweep Sweep, double Alpha)> Trails = new List<(ScopeSweep, double)>();

        /// <summary>The vertical gain: a value of 1/<see cref="Scale"/> reaches the top.</summary>
        public double Scale = 1;

        /// <summary>Vertical grid lines across, and every how many of them a stronger one (0: none).</summary>
        public int Divisions = 10, MajorEvery;

        /// <summary>Labels: under the top left (the time base) and the top right (where, what).</summary>
        public string Left = "", Right = "";

        /// <summary>The trigger's level (full scale 1), marked at the right edge; NaN for none.</summary>
        public double TriggerLevel = double.NaN;

        /// <summary>A word at the bottom right ("Trig'd", "Auto", "Frozen"); null for none.</summary>
        public string Status;
        public bool StatusAlert;

        public NeoColor ColorA, ColorB;

        internal readonly NeoPath PathA = new NeoPath(), PathB = new NeoPath(), Grid = new NeoPath(), Minor = new NeoPath();

        // Per pixel: the merged lowest and highest values (reused).
        internal float[] Low = Array.Empty<float>(), High = Array.Empty<float>();
    }

    internal static class ScopeGraph
    {
        /// <summary>The largest magnitude in a sweep (both traces), for auto scaling; 0 when empty.</summary>
        public static double Peak(ScopeSweep s)
        {
            if (s.IsEmpty) return 0;
            float m = 0;
            int n = Math.Min(s.Columns, s.AMin.Length);
            for (int i = 0; i < n; i++)
            {
                if (s.AMin[i] > s.AMax[i]) continue;
                m = Math.Max(m, Math.Max(-s.AMin[i], s.AMax[i]));
                if (s.Two) m = Math.Max(m, Math.Max(-s.BMin[i], s.BMax[i]));
            }
            return float.IsFinite(m) ? m : 0;
        }

        /// <summary>The gain that puts <paramref name="peak"/> at 90 % of the half-height, within −12 … +48 dB.</summary>
        public static double FitScale(double peak) => Math.Clamp(0.9 / Math.Max(peak, 1e-9), 0.25, 251.19);

        /// <summary>A band's points are this many pixels apart: its cost is its edges', and two pixels look the same.</summary>
        public const double BandStep = 2;

        public static void Draw(INeoCanvas c, NeoRect r, ScopeView v)
        {
            var plot = new NeoRect(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
            DrawGrid(c, plot, v);
            DrawTrail(c, plot, v);
            bool overlay = !v.Live.IsEmpty;
            if (!v.Sweep.IsEmpty || overlay)
                DrawSweep(c, plot, v, v.Sweep, v.Live, overlay, v.ColorA, v.ColorB, 1.3);
            if (overlay && !v.Sweep.IsEmpty && v.Live.Columns == v.Sweep.Columns && v.Live.Filled < v.Live.Columns)
            {
                double x = plot.X + plot.Width * v.Live.Filled / v.Live.Columns;
                c.Line(x, plot.Y, x, plot.Bottom, new NeoStroke(Palette.DimText.WithAlpha(0x60), 1, false));
            }
            if (!double.IsNaN(v.TriggerLevel))
            {
                double y = Y(plot, v.TriggerLevel, v.Scale);
                if (y >= plot.Y && y <= plot.Bottom)
                {
                    var marker = new NeoPath().MoveTo(plot.Right, y - 5).LineTo(plot.Right - 8, y).LineTo(plot.Right, y + 5).Close();
                    c.Path(marker, Palette.DimText, NeoStroke.None);
                    c.Line(plot.X, y, plot.Right - 8, y, new NeoStroke(Palette.DimText.WithAlpha(0x38), 1, false));
                }
            }
            c.Text(v.Left ?? "", plot.X + 6, plot.Y + 4, 10, Palette.DimText);
            c.Text(v.Right ?? "", plot.Right - 6, plot.Y + 4, 10, Palette.DimText, align: NeoAlign.Right);
            c.Text(TopLabel(v.Scale), plot.X + 6, plot.Bottom - 16, 9.5, Palette.DimText);
            if (!string.IsNullOrEmpty(v.Status))
                c.Text(v.Status, plot.Right - 6, plot.Bottom - 16, 10, v.StatusAlert ? Palette.Over : v.ColorA, bold: true, align: NeoAlign.Right);
        }

        /// <summary>What the top edge is: "top 0.50 (−6.0 dBFS)".</summary>
        public static string TopLabel(double scale)
        {
            var ci = CultureInfo.InvariantCulture;
            double top = 1 / Math.Max(scale, 1e-9);
            string db = (20 * Math.Log10(top)).ToString("+0.0;−0.0;0.0", ci);
            return $"top {top.ToString(top >= 0.1 ? "0.00" : top >= 0.01 ? "0.000" : "0.0000", ci)} ({db} dBFS)";
        }

        /// <summary>A value's height: 0 in the middle, ±1/scale at the edges (clipped just beyond them).</summary>
        public static double Y(NeoRect plot, double value, double scale)
        {
            double half = plot.Height / 2;
            return plot.Y + half - Math.Clamp(value * scale, -1.02, 1.02) * half;
        }

        static void DrawGrid(INeoCanvas c, NeoRect plot, ScopeView v)
        {
            var minor = v.Minor;
            var major = v.Grid;
            minor.Clear();
            major.Clear();
            int n = Math.Max(1, v.Divisions);
            for (int i = 1; i < n; i++)
            {
                double x = Math.Round(plot.X + plot.Width * i / n) + 0.5;
                var path = v.MajorEvery > 0 && i % v.MajorEvery == 0 ? major : minor;
                path.MoveTo(x, plot.Y).LineTo(x, plot.Bottom);
            }
            for (int j = 1; j < 8; j++)
            {
                double y = Math.Round(plot.Y + plot.Height * j / 8) + 0.5;
                (j == 4 ? major : minor).MoveTo(plot.X, y).LineTo(plot.Right, y);
            }
            c.Path(minor, NeoFill.None, new NeoStroke(Palette.Grid, 1, false));
            c.Path(major, NeoFill.None, new NeoStroke(Palette.Track, 1, false));
        }

        /// <summary>
        /// A sweep's traces (with <paramref name="overlay"/>, the sweep in progress where it has
        /// samples): its columns merged into pixels, each trace one path.
        /// </summary>
        static void DrawSweep(INeoCanvas c, NeoRect plot, ScopeView v, ScopeSweep s, ScopeSweep live, bool overlay,
            NeoColor a, NeoColor b, double width)
        {
            var basis = s.IsEmpty ? live : s;
            if (overlay && !s.IsEmpty && live.Columns != s.Columns) { basis = live; s = live; overlay = false; }
            // The second trace first: the first (the left channel, the trigger's) stays on top.
            if (basis.Two)
            {
                bool lineB = Trace(plot, v, s, live, overlay, true, v.PathB, width);
                c.Path(v.PathB, lineB ? NeoFill.None : b, lineB ? new NeoStroke(b, width) : NeoStroke.None);
            }
            bool line = Trace(plot, v, s, live, overlay, false, v.PathA, width);
            c.Path(v.PathA, line ? NeoFill.None : a, line ? new NeoStroke(a, width) : NeoStroke.None);
        }

        /// <summary>
        /// The trail as one faint band per trace: every earlier sweep's span merged per pixel (a phosphor's
        /// persistence), as opaque as the youngest of them, so it costs one fill however many sweeps it holds.
        /// </summary>
        static void DrawTrail(INeoCanvas c, NeoRect plot, ScopeView v)
        {
            if (v.Trails.Count == 0) return;
            double alpha = 0;
            foreach (var (_, a) in v.Trails) alpha = Math.Max(alpha, a);
            byte opacity = (byte)Math.Clamp((int)(alpha * 0x60), 0, 255);
            if (opacity < 8) return;
            var first = v.Trails[0].Sweep;
            int pixels = Math.Max(1, Math.Min(first.Columns, (int)Math.Ceiling(plot.Width / BandStep)));
            for (int trace = first.Two ? 1 : 0; trace >= 0; trace--)
            {
                Ensure(v, pixels);
                Array.Fill(v.Low, float.PositiveInfinity, 0, pixels);
                Array.Fill(v.High, float.NegativeInfinity, 0, pixels);
                foreach (var (s, _) in v.Trails)
                {
                    if (s.Columns != first.Columns || s.Two != first.Two) continue;
                    Merge(v, s, trace == 1, pixels, s.Columns);
                }
                var path = trace == 0 ? v.PathA : v.PathB;
                Band(plot, v, pixels, path, 1);
                c.Path(path, (trace == 0 ? v.ColorA : v.ColorB).WithAlpha(opacity), NeoStroke.None);
            }
        }

        static void Ensure(ScopeView v, int pixels)
        {
            if (v.Low.Length >= pixels) return;
            v.Low = new float[pixels];
            v.High = new float[pixels];
        }

        /// <summary>Widens each pixel's span in v.Low and v.High by a sweep's columns there.</summary>
        static void Merge(ScopeView v, ScopeSweep s, bool traceB, int pixels, int columns)
        {
            var mn = traceB ? s.BMin : s.AMin;
            var mx = traceB ? s.BMax : s.AMax;
            for (int p = 0; p < pixels; p++)
            {
                int c0 = (int)((long)p * columns / pixels), c1 = Math.Max(c0 + 1, (int)((long)(p + 1) * columns / pixels));
                float lo = v.Low[p], hi = v.High[p];
                for (int k = c0; k < c1; k++)
                {
                    if (mn[k] > mx[k]) continue;
                    if (mn[k] < lo) lo = mn[k];
                    if (mx[k] > hi) hi = mx[k];
                }
                v.Low[p] = lo;
                v.High[p] = hi;
            }
        }

        /// <summary>A band per run of pixels with samples: the highest values left to right, then the lowest back, at least <paramref name="thick"/> tall.</summary>
        static void Band(NeoRect plot, ScopeView v, int pixels, NeoPath path, double thick)
        {
            path.Clear();
            double half = thick / 2;
            int start = 0;
            while (start < pixels)
            {
                while (start < pixels && v.Low[start] > v.High[start]) start++;
                if (start >= pixels) break;
                int end = start;
                while (end + 1 < pixels && v.Low[end + 1] <= v.High[end + 1]) end++;
                for (int p = start; p <= end; p++)
                {
                    double x = plot.X + plot.Width * (p + 0.5) / pixels;
                    double yHi = Y(plot, v.High[p], v.Scale), yLo = Y(plot, v.Low[p], v.Scale);
                    yHi = Math.Min(yHi, 0.5 * (yHi + yLo) - half);
                    if (p == start) path.MoveTo(x, yHi);
                    else path.LineTo(x, yHi);
                }
                for (int p = end; p >= start; p--)
                {
                    double x = plot.X + plot.Width * (p + 0.5) / pixels;
                    double yHi = Y(plot, v.High[p], v.Scale), yLo = Y(plot, v.Low[p], v.Scale);
                    path.LineTo(x, Math.Max(yLo, 0.5 * (yHi + yLo) + half));
                }
                path.Close();
                start = end + 1;
            }
        }

        /// <summary>
        /// One trace into <paramref name="path"/>: true for a line through the samples (a sweep with a sample a
        /// column and no more columns than pixels), false for a band to fill (each pixel's span, at least
        /// <paramref name="thick"/> tall). With <paramref name="overlay"/>, the sweep in progress where it has samples.
        /// </summary>
        static bool Trace(NeoRect plot, ScopeView v, ScopeSweep s, ScopeSweep live, bool overlay, bool traceB, NeoPath path, double thick)
        {
            path.Clear();
            var basis = s.IsEmpty ? live : s;
            int columns = basis.Columns;
            if (columns <= 0) return true;
            bool samplesOnly = columns == basis.Samples && columns <= plot.Width;
            int pixels = samplesOnly ? columns : Math.Max(1, Math.Min(columns, (int)Math.Ceiling(plot.Width / BandStep)));
            Ensure(v, pixels);
            for (int p = 0; p < pixels; p++)
            {
                int c0 = (int)((long)p * columns / pixels), c1 = Math.Max(c0 + 1, (int)((long)(p + 1) * columns / pixels));
                float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                bool fromLive = false;
                if (overlay)
                {
                    int end = Math.Min(c1, live.Filled);
                    var mn = traceB ? live.BMin : live.AMin;
                    var mx = traceB ? live.BMax : live.AMax;
                    for (int k = c0; k < end; k++)
                    {
                        if (mn[k] > mx[k]) continue;
                        if (mn[k] < lo) lo = mn[k];
                        if (mx[k] > hi) hi = mx[k];
                        fromLive = true;
                    }
                }
                if (!fromLive && !s.IsEmpty)
                {
                    var mn = traceB ? s.BMin : s.AMin;
                    var mx = traceB ? s.BMax : s.AMax;
                    for (int k = c0; k < c1; k++)
                    {
                        if (mn[k] > mx[k]) continue;
                        if (mn[k] < lo) lo = mn[k];
                        if (mx[k] > hi) hi = mx[k];
                    }
                }
                v.Low[p] = lo;
                v.High[p] = hi;
            }
            if (!samplesOnly)
            {
                Band(plot, v, pixels, path, thick);
                return false;
            }
            // A line through the samples, shifted by the trigger's offset (a sweep's first sample is that far after it).
            double offset = !s.IsEmpty ? s.Offset : 0;
            bool open = false;
            for (int p = 0; p < pixels; p++)
            {
                float lo = v.Low[p], hi = v.High[p];
                if (lo > hi)
                {
                    open = false;
                    continue;
                }
                double x = plot.X + plot.Width * (p + offset) / basis.Samples, y = Y(plot, 0.5 * (lo + hi), v.Scale);
                if (!open) path.MoveTo(x, y);
                else path.LineTo(x, y);
                open = true;
            }
            return true;
        }
    }

    /// <summary>The polar vectorscope: each sample at its angle and level, folded into the upper half.</summary>
    internal static class PolarScope
    {
        /// <summary>Fits the farthest point at 92 % of the radius.</summary>
        public static double Scale(float[] l, float[] r, int count)
        {
            double max = 0;
            for (int i = 0; i < count; i++) max = Math.Max(max, l[i] * (double)l[i] + r[i] * (double)r[i]);
            return 0.92 / Math.Max(Math.Sqrt(max), 0.01);
        }

        /// <summary>
        /// A sample's point: as the goniometer's (side across, mid up, both over √2), turned through
        /// the centre into the upper half when its mid is negative.
        /// </summary>
        public static NeoPoint Point(NeoPoint centre, double radius, double scale, float l, float r, out bool folded)
        {
            double x = (r - l) * Math.Sqrt(0.5), y = (l + r) * Math.Sqrt(0.5);
            folded = y < 0;
            if (folded)
            {
                x = -x;
                y = -y;
            }
            return new NeoPoint(centre.X + x * scale * radius, centre.Y - y * scale * radius);
        }

        public static void Draw(INeoCanvas c, NeoRect rect, float[] l, float[] r, int count, NeoColor accent, double scale = double.NaN)
        {
            double radius = Math.Min(rect.Width / 2, rect.Height) - 14;
            var centre = new NeoPoint(rect.X + rect.Width / 2, rect.Bottom - 8);
            var grid = new NeoStroke(Palette.Grid, 1, false);
            c.Arc(centre, radius, -90, 90, new NeoStroke(Palette.Track, 1));
            c.Arc(centre, radius * 0.5, -90, 90, grid);
            c.Line(centre.X - radius, centre.Y, centre.X + radius, centre.Y, grid);
            foreach (double deg in new[] { -45.0, 0, 45 })
                c.Line(centre, NeoGeometry.Polar(centre, radius, deg), grid);
            var lp = NeoGeometry.Polar(centre, radius + 8, -45);
            var rp = NeoGeometry.Polar(centre, radius + 8, 45);
            c.Text("L", lp.X, lp.Y - 6, 9, Palette.DimText, align: NeoAlign.Center);
            c.Text("R", rp.X, rp.Y - 6, 9, Palette.DimText, align: NeoAlign.Center);
            c.Text("M", centre.X + 4, centre.Y - radius - 2, 9, Palette.DimText);
            c.Text("±S", centre.X - radius - 2, centre.Y - 14, 9, Palette.DimText);
            if (count < 2) return;
            if (double.IsNaN(scale)) scale = Scale(l, r, count);
            var path = new NeoPath();
            bool wasFolded = false;
            for (int i = 0; i < count; i++)
            {
                var p = Point(centre, radius, scale, l[i], r[i], out bool folded);
                if (i == 0 || folded != wasFolded) path.MoveTo(p);
                else path.LineTo(p);
                wasFolded = folded;
            }
            c.Path(path, NeoFill.None, new NeoStroke(accent.WithAlpha(0x70), 1));
        }
    }
}
