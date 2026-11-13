#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/neo-learn.md §5.4): the parts the Neo Learn machines' pictures are composed from, beside
// LearnPicture.cs's filter curve: the scope, bars (a spectrum), a waveform terrain (a table's frames, the playing slice marked) and the
// pad's handle. Each draws through INeoCanvas from plain arrays the machine's editor fills, in a frame's budget (a few hundred segments).

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
    internal static class LearnPictureParts
    {
        /// <summary>The scope: the last samples played, scaled to the loudest (at least 0.05), centred at <paramref name="centre"/> of the height.</summary>
        public static void Scope(INeoCanvas g, NeoRect r, float[] scope, int count, NeoColor colour, double centre = 0.5, double half = 0.3)
        {
            if (scope == null || count < 2) return;
            double mid = r.Top + r.Height * centre, h = r.Height * half;
            float peak = 1e-4f;
            for (int i = 0; i < count; i++) peak = Math.Max(peak, Math.Abs(scope[i]));
            double scale = h / Math.Max(peak, 0.05);
            var path = new NeoPath();
            for (int i = 0; i < count; i++)
            {
                double x = r.Left + r.Width * i / (count - 1);
                double y = mid - Math.Clamp(scope[i] * scale, -h, h);
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            g.Path(path, NeoFill.None, new NeoStroke(colour, 1));
        }

        /// <summary>Bars: <paramref name="count"/> values 0 … 1 from the bottom of <paramref name="r"/>, one slot each, with a gap.</summary>
        public static void Bars(INeoCanvas g, NeoRect r, double[] bars, int count, NeoColor accent)
        {
            if (bars == null || count < 1) return;
            double w = r.Width / count;
            for (int i = 0; i < count; i++)
            {
                double v = Math.Clamp(bars[i], 0, 1);
                if (v <= 0.002) continue;
                double h = r.Height * v;
                g.Rect(new NeoRect(r.Left + i * w + w * 0.12, r.Bottom - h, Math.Max(1, w * 0.76), h), accent.WithAlpha(0xC0), NeoStroke.None);
            }
        }

        /// <summary>
        /// A terrain of a table's frames: <paramref name="slices"/>[0] the first frame, drawn at the back, then forward and down; each slice's
        /// samples are −1 … 1. The slice at <paramref name="position"/> (0 … 1 of the way through the frames) is drawn in the accent, on top.
        /// </summary>
        public static void Terrain(INeoCanvas g, NeoRect r, float[][] slices, double position, NeoColor accent)
        {
            if (slices == null || slices.Length == 0) return;
            int n = slices.Length;
            double depth = r.Height * 0.42, lean = r.Width * 0.10;
            double bandH = r.Height * 0.38, left = r.Left + r.Width * 0.06, width = r.Width * 0.78;
            for (int s = 0; s < n; s++)
            {
                double t = n == 1 ? 0 : s / (double)(n - 1);
                // The back is high and to the right; the front low and to the left.
                double oy = r.Top + r.Height * 0.12 + depth * t + bandH * 0.5, ox = left + lean * (1 - t);
                DrawSlice(g, slices[s], ox, oy, width, bandH * 0.45, Palette.DimText.WithAlpha((byte)(0x50 + 0x50 * t)), 1);
            }
            // The playing slice: where the position says, interpolated between the two frames either side.
            if (n >= 2)
            {
                double f = Math.Clamp(position, 0, 1) * (n - 1);
                int a = Math.Min((int)f, n - 2);
                double k = f - a;
                int len = slices[a].Length;
                var mix = new float[len];
                for (int i = 0; i < len; i++) mix[i] = (float)(slices[a][i] * (1 - k) + slices[a + 1][i] * k);
                double t = f / (n - 1);
                double oy = r.Top + r.Height * 0.12 + depth * t + bandH * 0.5, ox = left + lean * (1 - t);
                DrawSlice(g, mix, ox, oy, width, bandH * 0.45, accent, 2.5);
            }
        }

        static void DrawSlice(INeoCanvas g, float[] y, double ox, double oy, double width, double amp, NeoColor colour, double stroke)
        {
            if (y == null || y.Length < 2) return;
            var p = new NeoPath();
            for (int i = 0; i < y.Length; i++)
            {
                double x = ox + width * i / (y.Length - 1);
                double v = oy - Math.Clamp(y[i], -1.2f, 1.2f) * amp;
                if (i == 0) p.MoveTo(x, v); else p.LineTo(x, v);
            }
            g.Path(p, NeoFill.None, new NeoStroke(colour, stroke));
        }

        /// <summary>A recording's overview (a minimum and a maximum per column, interleaved, as Neo Sampler's layer keeps it) as a filled band, scaled to its peak, centred in <paramref name="r"/>.</summary>
        public static void Overview(INeoCanvas g, NeoRect r, float[] overview, NeoColor colour, int columns = 128)
        {
            if (overview == null || overview.Length < 4) return;
            int src = overview.Length / 2;
            columns = Math.Min(columns, src);
            float peak = 1e-4f;
            for (int i = 0; i < overview.Length; i++) peak = Math.Max(peak, Math.Abs(overview[i]));
            double mid = r.Top + r.Height * 0.5, h = r.Height * 0.40;
            var path = new NeoPath();
            var lows = new double[columns];
            for (int c = 0; c < columns; c++)
            {
                int a = c * src / columns, b = Math.Max(a + 1, (c + 1) * src / columns);
                float lo = 0, hi = 0;
                for (int k = a; k < b && k < src; k++)
                {
                    lo = Math.Min(lo, overview[2 * k]);
                    hi = Math.Max(hi, overview[2 * k + 1]);
                }
                double x = r.Left + r.Width * (c + 0.5) / columns;
                lows[c] = mid - lo / peak * h;
                double yHigh = mid - hi / peak * h;
                if (c == 0) path.MoveTo(x, yHigh); else path.LineTo(x, yHigh);
            }
            for (int c = columns - 1; c >= 0; c--) path.LineTo(r.Left + r.Width * (c + 0.5) / columns, lows[c]);
            path.Close();
            g.Path(path, colour.WithAlpha(0x50), new NeoStroke(colour.WithAlpha(0x90), 1));
        }

        /// <summary>
        /// Grains over a waveform: the scan position (a line) with the spray's reach (a band either side), and each live grain as its own window
        /// drawn where it starts in the recording (x), as long as it is (width, of the recording), at its place in the stereo field (y: left high …
        /// right low), fading as it ages (0 new … 1 gone); grains an octave up are drawn bright. <paramref name="window"/> is the window's shape (0 … 1) sampled across a grain.
        /// </summary>
        public static void Grains(INeoCanvas g, NeoRect r, double scan, double spray, double[] window, int count, double[] pos, double[] length, double[] pan, double[] age, bool[] up, NeoColor accent)
        {
            double sx = r.Left + r.Width * Math.Clamp(scan, 0, 1);
            double reach = r.Width * Math.Clamp(spray, 0, 1) * 0.5;
            double x0 = Math.Max(r.Left, sx - reach), x1 = Math.Min(r.Right, sx + reach);
            if (x1 > x0) g.Rect(new NeoRect(x0, r.Top + 2, x1 - x0, r.Height - 4), accent.WithAlpha(0x22), NeoStroke.None);
            g.Line(sx, r.Top, sx, r.Bottom, new NeoStroke(accent.WithAlpha(0xA0), 1.5));
            if (window == null || window.Length < 2) return;
            double bump = Math.Min(30, r.Height * 0.16);
            for (int i = 0; i < count; i++)
            {
                double a = Math.Clamp(1 - age[i], 0, 1);
                if (a <= 0.02) continue;
                double w = Math.Max(3, length[i] * r.Width);
                double cx = r.Left + r.Width * Math.Clamp(pos[i], 0, 1);
                double baseY = r.Top + r.Height * (0.18 + 0.64 * (0.5 - 0.5 * Math.Clamp(pan[i], -1, 1)));
                var path = new NeoPath();
                for (int k = 0; k < window.Length; k++)
                {
                    double x = cx - w / 2 + w * k / (window.Length - 1);
                    double y = baseY - bump * window[k];
                    if (k == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                }
                path.Close();
                var colour = up[i] ? Palette.Text : accent;
                g.Path(path, colour.WithAlpha((byte)(0x80 * a)), new NeoStroke(colour.WithAlpha((byte)(0xE0 * a)), 1));
            }
        }

        /// <summary>
        /// Modal mode bars: one bar for each mode at its frequency (x, logarithmic from the fundamental to <paramref name="maxRatio"/> times it) as high as its
        /// level (0 … 1), and the exciter's force over its first milliseconds (<paramref name="impulse"/>, 0 … 1) in a box at the top right.
        /// </summary>
        public static void ModesAndImpulse(INeoCanvas g, NeoRect r, double[] ratios, double[] levels, int count, double maxRatio, double[] impulse, NeoColor accent)
        {
            double lg = Math.Log(Math.Max(2, maxRatio));
            var plot = new NeoRect(r.Left + 8, r.Top + 26, r.Width - 16, r.Height - 44);
            for (double f = 1; f <= maxRatio; f *= 2)
            {
                double x = plot.Left + plot.Width * Math.Log(f) / lg;
                g.Line(x, plot.Top, x, plot.Bottom, new NeoStroke(Palette.Grid, 1, false));
                g.Text(f == 1 ? "f0" : f + "x", x + 3, plot.Bottom - 13, 9, Palette.DimText);
            }
            for (int j = 0; j < count; j++)
            {
                double v = Math.Clamp(levels[j], 0, 1);
                if (v <= 0.01 || ratios[j] < 1 || ratios[j] > maxRatio) continue;
                double x = plot.Left + plot.Width * Math.Log(ratios[j]) / lg;
                double h = plot.Height * v;
                g.Rect(new NeoRect(x - 1.5, plot.Bottom - h, 3, h), accent.WithAlpha(0xD0), NeoStroke.None);
            }
            if (impulse == null || impulse.Length < 2) return;
            var box = new NeoRect(r.Right - r.Width * 0.30 - 8, r.Top + 6, r.Width * 0.30, r.Height * 0.36);
            g.Rect(box, Palette.Panel.WithAlpha(0xB0), new NeoStroke(Palette.Grid, 1), 3);
            var path = new NeoPath();
            double peak = 1e-3;
            for (int k = 0; k < impulse.Length; k++) peak = Math.Max(peak, Math.Abs(impulse[k]));
            for (int k = 0; k < impulse.Length; k++)
            {
                double x = box.Left + 4 + (box.Width - 8) * k / (impulse.Length - 1);
                double y = box.Bottom - 4 - (box.Height - 16) * Math.Clamp(impulse[k] / Math.Max(peak, 1.0), 0, 1);
                if (k == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            g.Path(path, NeoFill.None, new NeoStroke(accent, 1.5));
            g.Text("exciter", box.Left + 4, box.Top + 1, 9, Palette.DimText);
        }

        /// <summary>The pad's handle at (<paramref name="handle"/>.X, .Y) over <paramref name="r"/> (y up), with guide lines, and the caption naming what the pad moves.</summary>
        public static void Handle(INeoCanvas g, NeoRect r, NeoPoint? handle, bool dragging, NeoColor accent, string caption)
        {
            if (handle is { } h)
            {
                double hx = r.Left + r.Width * h.X, hy = r.Top + r.Height * (1 - h.Y);
                var guide = new NeoStroke(accent.WithAlpha(0x60), 1, true);
                g.Line(hx, r.Top, hx, r.Bottom, guide);
                g.Line(r.Left, hy, r.Right, hy, guide);
                g.Ellipse(new NeoPoint(hx, hy), dragging ? 8 : 6, dragging ? 8 : 6, accent, new NeoStroke(Palette.Text, 1.5));
            }
            g.Text(caption, r.X + 6, r.Y + 4, 10, Palette.DimText);
        }

        /// <summary>A horizontal grid line at a fraction of the height (0: top), and a label at the left.</summary>
        public static void GridLine(INeoCanvas g, NeoRect r, double fraction, string label = null)
        {
            double y = r.Top + r.Height * fraction;
            g.Line(r.Left, y, r.Right, y, new NeoStroke(Palette.Grid, 1, false));
            if (!string.IsNullOrEmpty(label)) g.Text(label, r.Left + 3, y - 12, 9, Palette.DimText);
        }

        /// <summary>A vertical grid line at a fraction of the width, with a label under it.</summary>
        public static void VGridLine(INeoCanvas g, NeoRect r, double fraction, string label = null)
        {
            double x = r.Left + r.Width * fraction;
            g.Line(x, r.Top, x, r.Bottom, new NeoStroke(Palette.Grid, 1, false));
            if (!string.IsNullOrEmpty(label)) g.Text(label, x + 3, r.Bottom - 13, 9, Palette.DimText);
        }
    }
}
