#nullable disable
// Neo Dither's editor (docs/specs/mastering-tools.md §5.9): the word length, the dither and its shaping, the
// noise this makes drawn against the ear's sensitivity, and meters. One source for WPF and Avalonia (the Neo editor
// kit); the picture draws on the kit's canvas.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Dither
{
    internal sealed class NeoDitherEditor : NeoEditor
    {
        // Teal: quiet, clear of the meters' red.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0x4F, 0xC1, 0xB0);

        protected override string Title => "Neo Dither";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoDitherMachine;

            var word = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Bits, diameter: 58), new ParamKnob(ctx, P.BlankAfter, "Blank after", 40)),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.Blank, "Auto blank", 40));

            var noise = Layout.Column(
                new ParamDropdown(ctx, P.Dither, "Dither", 96),
                Layout.Gap(1, 4),
                new ParamDropdown(ctx, P.Shaping, "Shaping", 96));

            var graph = new GraphView(260, 196, (g, r) => DrawNoise(g, r, ctx), animate: false);

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 196),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 196));

            return Layout.Row(
                new Section("Word length", AccentColor, word),
                new Section("Noise", AccentColor, noise),
                new Section("Noise spectrum", AccentColor, graph),
                new Section("Meters", AccentColor, meters));
        }

        /// <summary>The noise the chosen shaping makes against frequency (dB against the unshaped noise, 20 Hz … 20 kHz at 48 kHz).</summary>
        static void DrawNoise(INeoCanvas g, NeoRect r, EditorContext ctx)
        {
            const double lo = -20, hi = 20;
            var plot = new NeoRect(r.X + 30, r.Y + 8, r.Width - 38, r.Height - 30);
            double Y(double db) => plot.Bottom - (Math.Clamp(db, lo, hi) - lo) / (hi - lo) * plot.Height;
            var grid = new NeoStroke(Palette.Grid, 1, false);
            for (double d = lo; d <= hi; d += 10)
            {
                g.Line(plot.Left, Y(d), plot.Right, Y(d), grid);
                g.Text(d.ToString("+0;−0;0"), plot.Left - 4, Y(d) - 7, 9.5, Palette.DimText, align: NeoAlign.Right);
            }
            var b = NeoBuzz.Dsp.Dither.Shapes[(int)ctx.Value(P.Shaping)];
            var path = new NeoPath();
            const int Points = 120;
            for (int k = 0; k <= Points; k++)
            {
                double f = 20 * Math.Pow(1000, k / (double)Points);
                double w = 2 * Math.PI * f / 48000, re = 1, im = 0;
                for (int j = 0; j < b.Length; j++) { re += b[j] * Math.Cos(w * (j + 1)); im -= b[j] * Math.Sin(w * (j + 1)); }
                double db = 10 * Math.Log10(re * re + im * im + 1e-12);
                double x = plot.Left + k / (double)Points * plot.Width;
                if (k == 0) path.MoveTo(x, Y(db));
                else path.LineTo(x, Y(db));
            }
            g.Path(path, NeoFill.None, new NeoStroke(AccentColor, 2));
            g.Text("noise against the unshaped, 20 Hz to 20 kHz", plot.Left + 4, plot.Top + 2, 10, Palette.DimText);
        }
    }
}
