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
