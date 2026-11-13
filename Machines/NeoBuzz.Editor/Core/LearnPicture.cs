#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/neo-learn.md §5.4): the picture of a Neo Learn editor. A scope of what the
// machine plays, the filter's response curve over it, and an X/Y pad: the handle sits at (cutoff, resonance) and a drag moves
// both controls as one undo step. Pointer input arrives as plain calls (IGraphInput) like every interactive graph; drawing
// goes through INeoCanvas, so both backends draw the same.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
    /// <summary>What the picture's pad moves: two controls (X and Y) and the gesture around them.</summary>
    internal interface IPictureTarget
    {
        ParamDef XDef { get; }
        ParamDef YDef { get; }
        int XRaw { get; }
        int YRaw { get; }
        /// <summary>Writes both controls (one write each, inside a gesture).</summary>
        void Set(int xRaw, int yRaw);
        void BeginGesture();
        void EndGesture();
    }

    /// <summary>The pad's pointer input: a press takes the handle to the pointer, a drag moves it (Shift: fine), a double-click resets both.</summary>
    internal sealed class LearnPictureInput : IGraphInput
    {
        /// <summary>A fine drag moves a tenth as far.</summary>
        public const double FineScale = 0.1;

        readonly IPictureTarget target;
        readonly Func<NeoRect> plot;
        bool dragging;
        double lastX, lastY;
        double tx, ty;

        /// <param name="plot">The pad's area now (the part of the picture the pad covers).</param>
        public LearnPictureInput(IPictureTarget target, Func<NeoRect> plot)
        {
            this.target = target;
            this.plot = plot;
        }

        public bool Capturing => dragging;

        /// <summary>The handle, as 0…1 over the pad (x: left to right, y: bottom to top).</summary>
        public NeoPoint Handle => new NeoPoint(ParamMapping.Normalized(target.XDef, target.XRaw), ParamMapping.Normalized(target.YDef, target.YRaw));

        public bool Down(double x, double y, int clicks, bool fine)
        {
            var r = plot();
            if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) return false;
            target.BeginGesture();
            if (clicks >= 2)
            {
                target.Set(target.XDef.Default, target.YDef.Default);
                target.EndGesture();
                return true;
            }
            dragging = true;
            lastX = x;
            lastY = y;
            var h = Handle;
            tx = h.X;
            ty = h.Y;
            if (!fine)
            {
                tx = Clamp01((x - r.Left) / r.Width);
                ty = Clamp01(1 - (y - r.Top) / r.Height);
            }
            Apply();
            return true;
        }

        public bool Move(double x, double y, bool fine)
        {
            if (!dragging) return false;
            var r = plot();
            double k = fine ? FineScale : 1;
            tx = Clamp01(tx + (x - lastX) / r.Width * k);
            ty = Clamp01(ty - (y - lastY) / r.Height * k);
            lastX = x;
            lastY = y;
            Apply();
            return true;
        }

        public bool Up()
        {
            if (!dragging) return false;
            dragging = false;
            target.EndGesture();
            return true;
        }

        public bool Wheel(double x, double y, int delta, bool fine) => false;

        public bool Leave() => false;

        void Apply() => target.Set(ParamMapping.RawFromNormalized(target.XDef, tx), ParamMapping.RawFromNormalized(target.YDef, ty));

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }

    /// <summary>Draws the picture: scope behind, filter response curve over it, the pad's handle on top.</summary>
    internal static class LearnPictureGraph
    {
        public const double MinHz = 20, MaxHz = 20000;
        /// <summary>The response's vertical range, dB.</summary>
        public const double TopDb = 30, BottomDb = -60;

        /// <summary>The 24 dB/octave low-pass's gain at <paramref name="hz"/> (a ladder: four poles with feedback k = 4 x resonance).</summary>
        public static double LadderDb(double hz, double cutoffHz, double resonance)
        {
            double w = hz / Math.Max(cutoffHz, 1e-3);
            double k = 3.95 * Math.Clamp(resonance, 0, 1);
            // (1 + jw)^-4 / (1 + k (1 + jw)^-4) = 1 / ((1 + jw)^4 + k)
            double re = 1, im = 0;
            for (int i = 0; i < 4; i++)
            {
                double nr = re - im * w, ni = re * w + im;
                re = nr;
                im = ni;
            }
            double mag = 1 / Math.Sqrt((re + k) * (re + k) + im * im);
            return 20 * Math.Log10(Math.Max(mag, 1e-6));
        }

        public static double XOfHz(NeoRect r, double hz) => r.Left + r.Width * Math.Log(Math.Clamp(hz, MinHz, MaxHz) / MinHz) / Math.Log(MaxHz / MinHz);

        public static double YOfDb(NeoRect r, double db) => r.Top + r.Height * (TopDb - Math.Clamp(db, BottomDb, TopDb)) / (TopDb - BottomDb);

        /// <param name="scope">The last samples played (mono), or null.</param>
        /// <param name="cutoffHz">The filter's cutoff, Hz.</param>
        /// <param name="resonance">0 … 1.</param>
        /// <param name="handle">The pad's handle (0…1 each), or null.</param>
        public static void Draw(INeoCanvas g, NeoRect r, float[] scope, int scopeCount, double cutoffHz, double resonance, NeoPoint? handle, bool dragging, NeoColor accent)
        {
            var grid = new NeoStroke(Palette.Grid, 1, false);
            foreach (double hz in new[] { 100.0, 1000, 10000 })
            {
                double x = XOfHz(r, hz);
                g.Line(x, r.Top, x, r.Bottom, grid);
                g.Text(hz >= 1000 ? (hz / 1000).ToString("0") + "k" : hz.ToString("0"), x + 3, r.Bottom - 13, 9, Palette.DimText);
            }
            g.Line(r.Left, YOfDb(r, 0), r.Right, YOfDb(r, 0), grid);

            // The scope: the output, centred in the picture, behind the curve.
            if (scope != null && scopeCount > 1)
            {
                double mid = r.Top + r.Height * 0.5, half = r.Height * 0.3;
                float peak = 1e-4f;
                for (int i = 0; i < scopeCount; i++) peak = Math.Max(peak, Math.Abs(scope[i]));
                double scale = half / Math.Max(peak, 0.05);
                var path = new NeoPath();
                for (int i = 0; i < scopeCount; i++)
                {
                    double x = r.Left + r.Width * i / (scopeCount - 1);
                    double y = mid - Math.Clamp(scope[i] * scale, -half, half);
                    if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                }
                g.Path(path, NeoFill.None, new NeoStroke(Palette.DimText.WithAlpha(0xA0), 1));
            }

            // The filter's response: gain against frequency.
            const int steps = 120;
            var curve = new NeoPath();
            var area = new NeoPath();
            double y0 = r.Bottom;
            area.MoveTo(r.Left, y0);
            for (int i = 0; i <= steps; i++)
            {
                double hz = MinHz * Math.Pow(MaxHz / MinHz, (double)i / steps);
                double x = r.Left + r.Width * i / steps;
                double y = YOfDb(r, LadderDb(hz, cutoffHz, resonance));
                if (i == 0) curve.MoveTo(x, y); else curve.LineTo(x, y);
                area.LineTo(x, y);
            }
            area.LineTo(r.Right, y0);
            area.Close();
            g.Path(area, (NeoFill)accent.WithAlpha(0x22), NeoStroke.None);
            g.Path(curve, NeoFill.None, new NeoStroke(accent, 2));

            // The pad's handle.
            if (handle is { } h)
            {
                double hx = r.Left + r.Width * h.X, hy = r.Top + r.Height * (1 - h.Y);
                var guide = new NeoStroke(accent.WithAlpha(0x60), 1, true);
                g.Line(hx, r.Top, hx, r.Bottom, guide);
                g.Line(r.Left, hy, r.Right, hy, guide);
                g.Ellipse(new NeoPoint(hx, hy), dragging ? 8 : 6, dragging ? 8 : 6, accent, new NeoStroke(Palette.Text, 1.5));
            }
            g.Text("Cutoff →, Resonance ↑: drag the dot; double-click resets", r.X + 6, r.Y + 4, 10, Palette.DimText);
        }
    }
}
