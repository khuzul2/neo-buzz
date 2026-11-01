#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): the graph surface's frame
// and the graphs every editor shares (level meter, ADSR envelope), drawn on an INeoCanvas.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
    /// <summary>The frame of a graph: a dark rounded panel that clips what is drawn in it.</summary>
    internal static class GraphFrame
    {
        public const double Radius = 4;

        /// <summary>
        /// Draws the frame over <paramref name="r"/>, then <paramref name="body"/> clipped to it. A
        /// drawing error in the body must never take the editor down: it ends the graph there.
        /// </summary>
        public static void Draw(INeoCanvas c, NeoRect r, Action<INeoCanvas, NeoRect> body)
        {
            c.Rect(r, Palette.Background, new NeoStroke(Palette.PanelEdge, 1), Radius);
            c.PushClip(r, Radius);
            try
            {
                body?.Invoke(c, r);
            }
            catch (Exception)
            {
            }
            c.PopClip();
        }
    }

    /// <summary>A vertical level meter in dB (−60 … +6), or a gain-reduction bar growing down (0 … 30 dB).</summary>
    internal static class MeterPainter
    {
        public const double Low = -60, High = 6;
        static readonly double[] Ticks = { 0.0, -12, -24, -48 };

        /// <param name="level">The level as a gain (for reduction: the gain applied, ≤ 1).</param>
        public static void Draw(INeoCanvas c, NeoRect r, float level, NeoColor color, bool reduction)
        {
            const double lo = Low, hi = High;
            float a = level;
            double db = DspMath.GainToDb(Math.Max(1e-9f, a));
            var inner = new NeoRect(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6);
            double Y(double d) => inner.Bottom - (Math.Clamp(d, lo, hi) - lo) / (hi - lo) * inner.Height;
            if (reduction)
            {
                double bottom = inner.Top + Math.Clamp(-db, 0, 30) / 30 * inner.Height;
                if (bottom > inner.Top + 0.5)
                    c.Rect(new NeoRect(inner.X, inner.Top, inner.Width, bottom - inner.Top), color, NeoStroke.None);
                return;
            }
            double y = Y(db);
            var fill = db > 0 ? Palette.Over : color;
            if (y < inner.Bottom)
                c.Rect(new NeoRect(inner.X, y, inner.Width, inner.Bottom - y), fill, NeoStroke.None);
            // 0 dB and −12/−24/−48 ticks.
            var tick = new NeoStroke(Palette.Grid, 1, false);
            foreach (double t in Ticks)
                c.Line(new NeoPoint(inner.X, Y(t)), new NeoPoint(inner.Right, Y(t)), tick);
        }
    }

    internal static class EnvelopePainter
    {
        /// <summary>
        /// An ADSR envelope (times in seconds, sustain 0 … 1) as a filled shape. Segment widths
        /// grow with the square root of time, so short and long both read.
        /// </summary>
        public static void Adsr(INeoCanvas c, NeoRect r, double a, double d, double s, double rl, NeoColor accent)
        {
            double wa = Math.Sqrt(a), wd = Math.Sqrt(d), ws = 0.35, wr = Math.Sqrt(rl);
            double total = wa + wd + ws + wr;
            double x0 = r.Left + 6, w = r.Width - 12, top = r.Top + 6, bottom = r.Bottom - 6, h = bottom - top;
            double X(double t) => x0 + t / total * w;
            var path = new NeoPath()
                .MoveTo(X(0), bottom)
                .LineTo(X(wa), top)
                .LineTo(X(wa + wd), bottom - s * h)
                .LineTo(X(wa + wd + ws), bottom - s * h)
                .LineTo(X(total), bottom)
                .Close();
            c.Path(path, accent.WithAlpha(0x30), new NeoStroke(accent, 1.5));
        }
    }
}
