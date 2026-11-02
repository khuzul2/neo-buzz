#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): the analysis graphs
// (Neo Analyzer, docs/specs/machines.md §5): a spectrum with third-octave bars, held peaks and a
// display tilt; loudness bars and readouts coloured against a target; a goniometer; and a
// correlation bar. Painters take plain values, so they are tested on Linux and any machine (a
// future Neo Scope) can draw with them.

using System;
using System.Globalization;

namespace NeoBuzz.Editor
{
    internal static class AnalysisColors
    {
        public static readonly NeoColor Under = NeoColor.FromRgb(0x4D, 0x9D, 0xFF);
        public static readonly NeoColor OnTarget = NeoColor.FromRgb(0x3D, 0xDC, 0x84);
        public static readonly NeoColor Over = NeoColor.FromRgb(0xFF, 0xA3, 0x3D);
        public static readonly NeoColor Hot = Palette.Over;

        /// <summary>
        /// A loudness against its target: within 1 LU on target (green), more than 1 LU under
        /// (blue), up to 3 LU over (amber), beyond that red. No signal: dim.
        /// </summary>
        public static NeoColor Loudness(double lufs, double target)
        {
            if (double.IsNegativeInfinity(lufs) || double.IsNaN(lufs)) return Palette.DimText;
            double d = lufs - target;
            if (d > 3) return Hot;
            if (d > 1) return Over;
            if (d >= -1) return OnTarget;
            return Under;
        }

        /// <summary>A true peak: red above −1 dBTP (lossy encoding may clip), amber within 2 dB of it.</summary>
        public static NeoColor TruePeak(double dbtp) =>
            double.IsNegativeInfinity(dbtp) ? Palette.DimText : dbtp > -1 ? Hot : dbtp > -3 ? Over : OnTarget;
    }

    /// <summary>
    /// A spectrum on log frequency (20 Hz – 20 kHz) and dB axes: third-octave bars with their held
    /// peaks, the FFT curve and its held peaks. The tilt (dB per octave around 1 kHz) is for
    /// viewing only.
    /// </summary>
    internal static class SpectrumGraph
    {
        public const double LowHz = 20, HighHz = 20000;
        public const double Pad = 4, AxisH = 14, LabelW = 26;

        static readonly double[] GridHz = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
        static readonly string[] GridText = { "20", "50", "100", "200", "500", "1k", "2k", "5k", "10k", "20k" };

        /// <summary>The plot area inside a graph's frame (the rest is axis labels).</summary>
        public static NeoRect Plot(NeoRect r) => new NeoRect(r.X + LabelW, r.Y + Pad, r.Width - LabelW - Pad, r.Height - Pad - AxisH);

        public static double X(NeoRect plot, double hz) =>
            plot.Left + Math.Log(Math.Clamp(hz, LowHz, HighHz) / LowHz) / Math.Log(HighHz / LowHz) * plot.Width;

        public static double Y(NeoRect plot, double db, double topDb, double bottomDb) =>
            plot.Top + (topDb - Math.Clamp(db, bottomDb, topDb)) / (topDb - bottomDb) * plot.Height;

        /// <summary>The tilt's offset at <paramref name="hz"/>, dB.</summary>
        public static double Tilt(double hz, double dbPerOctave) => dbPerOctave * Math.Log2(hz / 1000);

        /// <param name="curveHz">The curve's frequencies.</param>
        /// <param name="curve">Its levels, dB (−∞ where silent); <paramref name="curvePeak"/>: held peaks (or null).</param>
        /// <param name="thirdLo">Third-octave bands' edges and levels (or null for no bars).</param>
        public static void Draw(INeoCanvas c, NeoRect r, double[] curveHz, float[] curve, float[] curvePeak,
            double[] thirdLo, double[] thirdHi, float[] thirds, float[] thirdsPeak, double tilt, NeoColor accent,
            double topDb = 0, double bottomDb = -96)
        {
            var plot = Plot(r);
            var grid = new NeoStroke(Palette.Grid, 1, false);
            var ci = CultureInfo.InvariantCulture;
            for (double d = topDb; d >= bottomDb; d -= 12)
            {
                double y = Y(plot, d, topDb, bottomDb);
                c.Line(plot.Left, y, plot.Right, y, grid);
                c.Text(d.ToString("0", ci).Replace("-", "−"), plot.Left - 3, y - 6, 9, Palette.DimText, align: NeoAlign.Right);
            }
            for (int i = 0; i < GridHz.Length; i++)
            {
                double x = X(plot, GridHz[i]);
                c.Line(x, plot.Top, x, plot.Bottom, grid);
                c.Text(GridText[i], Math.Clamp(x, plot.Left + 6, plot.Right - 8), plot.Bottom + 2, 9, Palette.DimText, align: NeoAlign.Center);
            }

            c.PushClip(plot);
            if (thirds != null)
            {
                var fill = accent.WithAlpha(0x2E);
                var top = new NeoStroke(accent.WithAlpha(0x80), 1, false);
                var peak = new NeoStroke(accent.WithAlpha(0xB0), 1.5, false);
                for (int i = 0; i < thirds.Length; i++)
                {
                    double x0 = X(plot, thirdLo[i]) + 0.5, x1 = X(plot, thirdHi[i]) - 0.5;
                    if (x1 <= x0) continue;
                    double centre = Math.Sqrt(thirdLo[i] * thirdHi[i]), t = Tilt(centre, tilt);
                    if (float.IsFinite(thirds[i]))
                    {
                        double y = Y(plot, thirds[i] + t, topDb, bottomDb);
                        if (y < plot.Bottom)
                        {
                            c.Rect(new NeoRect(x0, y, x1 - x0, plot.Bottom - y), fill, NeoStroke.None);
                            c.Line(x0, y, x1, y, top);
                        }
                    }
                    if (thirdsPeak != null && float.IsFinite(thirdsPeak[i]))
                    {
                        double y = Y(plot, thirdsPeak[i] + t, topDb, bottomDb);
                        if (y < plot.Bottom) c.Line(x0, y, x1, y, peak);
                    }
                }
            }
            if (curvePeak != null) DrawCurve(c, plot, curveHz, curvePeak, tilt, topDb, bottomDb, new NeoStroke(accent.Tint(0.35).WithAlpha(0x90), 1), false, default);
            DrawCurve(c, plot, curveHz, curve, tilt, topDb, bottomDb, new NeoStroke(accent, 1.6), true, accent.WithAlpha(0x22));
            c.PopClip();
        }

        /// <summary>The curve's points (x, y), silent points dropped to the bottom.</summary>
        public static NeoPath CurvePath(NeoRect plot, double[] hz, float[] db, double tilt, double topDb, double bottomDb)
        {
            var path = new NeoPath();
            for (int i = 0; i < hz.Length && i < db.Length; i++)
            {
                if (hz[i] < LowHz || hz[i] > HighHz) continue;
                double y = float.IsFinite(db[i]) ? Y(plot, db[i] + Tilt(hz[i], tilt), topDb, bottomDb) : plot.Bottom + 2;
                path.LineTo(X(plot, hz[i]), y, true);
            }
            return path;
        }

        static void DrawCurve(INeoCanvas c, NeoRect plot, double[] hz, float[] db, double tilt, double topDb, double bottomDb,
            NeoStroke stroke, bool filled, NeoColor fill)
        {
            var path = CurvePath(plot, hz, db, tilt, topDb, bottomDb);
            if (path.PointCount < 2) return;
            if (filled)
            {
                var area = new NeoPath();
                area.MoveTo(path.Point(0).X, plot.Bottom + 2);
                for (int i = 0; i < path.PointCount; i++) area.LineTo(path.Point(i));
                area.LineTo(path.Point(path.PointCount - 1).X, plot.Bottom + 2).Close();
                c.Path(area, fill, NeoStroke.None);
            }
            c.Path(path, NeoFill.None, stroke);
        }
    }

    /// <summary>What the loudness panel shows (LUFS, LU, dBTP; −∞ when silent).</summary>
    internal struct LoudnessView
    {
        public double Momentary, ShortTerm, Integrated, Range, MomentaryMax, ShortTermMax, Seconds;
        public double TruePeakNow, TruePeakHold, TruePeakMax;
        public double Target;
    }

    /// <summary>
    /// Loudness bars (momentary, short-term, true peak) on a −54 … 0 scale with the target marked,
    /// and the readouts: integrated (large, coloured against the target), short-term, momentary,
    /// range, true peak, time measured.
    /// </summary>
    internal static class LoudnessGraph
    {
        public const double Low = -54, High = 0, BarW = 16, BarGap = 6;

        public static double Y(NeoRect bars, double db) => bars.Bottom - (Math.Clamp(db, Low, High) - Low) / (High - Low) * bars.Height;

        /// <summary>The bars' area: the left part of the graph, under the letters.</summary>
        public static NeoRect Bars(NeoRect r) => new NeoRect(r.X + 26, r.Y + 20, 3 * BarW + 2 * BarGap, r.Height - 28);

        public static string Lu(double v, string unit = "")
        {
            if (double.IsNegativeInfinity(v) || double.IsNaN(v)) return "—";
            return v.ToString("0.0", CultureInfo.InvariantCulture).Replace("-", "−") + unit;
        }

        public static string Time(double seconds)
        {
            int s = (int)Math.Floor(Math.Max(0, seconds));
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
        }

        public static void Draw(INeoCanvas c, NeoRect r, LoudnessView v, NeoColor accent)
        {
            var bars = Bars(r);
            var grid = new NeoStroke(Palette.Grid, 1, false);
            var ci = CultureInfo.InvariantCulture;
            for (double d = High; d >= Low; d -= 6)
            {
                double gy = Y(bars, d);
                c.Line(bars.Left - 3, gy, bars.Right, gy, grid);
                if ((int)d % 12 == 0) c.Text(d.ToString("0", ci).Replace("-", "−"), bars.Left - 5, gy - 6, 9, Palette.DimText, align: NeoAlign.Right);
            }
            Bar(c, bars, 0, "M", v.Momentary, double.NegativeInfinity, AnalysisColors.Loudness(v.Momentary, v.Target));
            Bar(c, bars, 1, "S", v.ShortTerm, double.NegativeInfinity, AnalysisColors.Loudness(v.ShortTerm, v.Target));
            Bar(c, bars, 2, "TP", v.TruePeakNow, v.TruePeakHold, AnalysisColors.TruePeak(v.TruePeakNow));
            // The target across the loudness bars, and −1 dBTP on the true-peak bar.
            double ty = Y(bars, v.Target);
            c.Line(bars.Left - 2, ty, bars.Left + 2 * BarW + BarGap + 2, ty, new NeoStroke(AnalysisColors.OnTarget, 1.5, false));
            double py = Y(bars, -1);
            double px = bars.Left + 2 * (BarW + BarGap);
            c.Line(px - 2, py, px + BarW + 2, py, new NeoStroke(AnalysisColors.Hot.WithAlpha(0xC0), 1, false));

            // Readouts.
            double x = bars.Right + 16, y0 = r.Y + 6, w = r.Right - x - 6;
            c.Text("INTEGRATED", x, y0, 9.5, Palette.DimText);
            c.Text(Lu(v.Integrated), x, y0 + 12, 24, AnalysisColors.Loudness(v.Integrated, v.Target), bold: true);
            c.Text("LUFS", x + w, y0 + 24, 10, Palette.DimText, align: NeoAlign.Right);
            double y = y0 + 48;
            void Row(string label, string value, NeoColor color)
            {
                c.Text(label, x, y, 10.5, Palette.DimText);
                c.Text(value, x + w, y, 11.5, color, bold: true, align: NeoAlign.Right);
                y += 18;
            }
            Row("Short-term", Lu(v.ShortTerm), AnalysisColors.Loudness(v.ShortTerm, v.Target));
            Row("Momentary", Lu(v.Momentary), AnalysisColors.Loudness(v.Momentary, v.Target));
            Row("Range", Lu(double.IsNegativeInfinity(v.Integrated) ? double.NegativeInfinity : v.Range, " LU"), Palette.Text);
            Row("True peak max", Lu(v.TruePeakMax, " dBTP"), AnalysisColors.TruePeak(v.TruePeakMax));
            Row("Loudest 3 s", Lu(v.ShortTermMax), AnalysisColors.Loudness(v.ShortTermMax, v.Target));
            Row("Target", Lu(v.Target), AnalysisColors.OnTarget);
            c.Text("measured " + Time(v.Seconds), x, y + 2, 9.5, Palette.DimText);
        }

        static void Bar(INeoCanvas c, NeoRect bars, int index, string label, double db, double hold, NeoColor color)
        {
            double x = bars.Left + index * (BarW + BarGap);
            c.Text(label, x + BarW / 2, bars.Top - 16, 10, Palette.DimText, bold: true, align: NeoAlign.Center);
            c.Rect(new NeoRect(x, bars.Top, BarW, bars.Height), Palette.Panel, new NeoStroke(Palette.PanelEdge, 1), 2);
            if (db > Low)
            {
                double y = Y(bars, db);
                c.Rect(new NeoRect(x + 2, y, BarW - 4, bars.Bottom - y - 1), NeoFill.Vertical(color, color.WithAlpha(0x70)), NeoStroke.None, 1);
            }
            if (hold > Low)
            {
                double y = Y(bars, hold);
                c.Line(x + 1, y, x + BarW - 1, y, new NeoStroke(Palette.Text, 2, false));
            }
        }
    }

    /// <summary>
    /// A goniometer (vectorscope): each sample as a point, mid up and side across (left channel
    /// up-left, right up-right), scaled to the loudest point with at most 40 dB of zoom.
    /// </summary>
    internal static class Goniometer
    {
        public static double Scale(float[] l, float[] r, int count)
        {
            double max = 0;
            for (int i = 0; i < count; i++) max = Math.Max(max, Math.Max(Math.Abs(l[i] + r[i]), Math.Abs(l[i] - r[i])));
            max *= Math.Sqrt(0.5);
            return 0.92 / Math.Max(max, 0.01);
        }

        /// <summary>A sample's point: x = (R − L)/√2, y = (L + R)/√2 (up), both times <paramref name="scale"/>, in a square of half-side <paramref name="half"/>.</summary>
        public static NeoPoint Point(NeoPoint centre, double half, double scale, float l, float r) =>
            new NeoPoint(centre.X + (r - l) * Math.Sqrt(0.5) * scale * half, centre.Y - (l + r) * Math.Sqrt(0.5) * scale * half);

        public static void Draw(INeoCanvas c, NeoRect rect, float[] l, float[] r, int count, NeoColor accent)
        {
            double side = Math.Min(rect.Width, rect.Height) - 8;
            var centre = rect.Centre;
            double half = side / 2;
            var grid = new NeoStroke(Palette.Grid, 1, false);
            c.Line(centre.X - half, centre.Y, centre.X + half, centre.Y, grid);
            c.Line(centre.X, centre.Y - half, centre.X, centre.Y + half, grid);
            double d = half * Math.Sqrt(0.5);
            c.Line(centre.X - d, centre.Y - d, centre.X + d, centre.Y + d, grid);
            c.Line(centre.X - d, centre.Y + d, centre.X + d, centre.Y - d, grid);
            c.Text("L", centre.X - d - 2, centre.Y - d - 12, 9, Palette.DimText, align: NeoAlign.Center);
            c.Text("R", centre.X + d + 2, centre.Y - d - 12, 9, Palette.DimText, align: NeoAlign.Center);
            c.Text("M", centre.X + 4, rect.Top + 1, 9, Palette.DimText);
            if (count < 2) return;
            double scale = Scale(l, r, count);
            var path = new NeoPath();
            for (int i = 0; i < count; i++) path.LineTo(Point(centre, half, scale, l[i], r[i]));
            c.Path(path, NeoFill.None, new NeoStroke(accent.WithAlpha(0x70), 1));
        }
    }

    /// <summary>A correlation bar, −1 … +1: filled from 0 towards the value, red below 0.</summary>
    internal static class CorrelationBar
    {
        public static double X(NeoRect r, double correlation) => r.Left + 6 + (Math.Clamp(correlation, -1, 1) + 1) / 2 * (r.Width - 12);

        public static void Draw(INeoCanvas c, NeoRect r, double correlation, double slow, NeoColor accent)
        {
            var track = new NeoRect(r.Left + 6, r.Top + 4, r.Width - 12, r.Height - 18);
            c.Rect(track, Palette.Panel, new NeoStroke(Palette.PanelEdge, 1), 2);
            double x0 = X(r, 0);
            c.Line(x0, track.Top - 2, x0, track.Bottom + 2, new NeoStroke(Palette.DimText, 1, false));
            if (!double.IsNaN(correlation))
            {
                double x = X(r, correlation);
                var color = correlation < 0 ? AnalysisColors.Hot : correlation < 0.3 ? AnalysisColors.Over : AnalysisColors.OnTarget;
                c.Rect(new NeoRect(Math.Min(x, x0), track.Top + 2, Math.Abs(x - x0), track.Height - 4), color, NeoStroke.None, 1);
            }
            if (!double.IsNaN(slow))
            {
                double xs = X(r, slow);
                c.Line(xs, track.Top, xs, track.Bottom, new NeoStroke(Palette.Text, 2, false));
            }
            c.Text("−1", track.Left, track.Bottom + 1, 9, Palette.DimText);
            c.Text("0", x0, track.Bottom + 1, 9, Palette.DimText, align: NeoAlign.Center);
            c.Text("+1", track.Right, track.Bottom + 1, 9, Palette.DimText, align: NeoAlign.Right);
        }
    }
}
