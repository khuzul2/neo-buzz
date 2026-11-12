#nullable disable
// Neo De-esser's editor (docs/specs/mastering-tools.md §5.12): the detector (mode, band, threshold, ratio), the band the
// detector hears drawn over the audible range with the reduction now, the reduction (action, range, times, lookahead),
// Listen, the output with Delta and Match (Output set from the gain-match reading, one undo step), and meters.
// One source for WPF and Avalonia (the Neo editor kit); the picture draws on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.DeEsser
{
    internal sealed class NeoDeEsserEditor : NeoEditor
    {
        // Green: the sibilance band, clear of the meters' red.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0x5E, 0xC9, 0x8F);

        protected override string Title => "Neo De-esser";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoDeEsserMachine;

            var detector = Layout.Column(
                new ParamDropdown(ctx, P.Mode, "Detector", 96),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.Freq, diameter: 52), new ParamKnob(ctx, P.Width, diameter: 52)),
                Layout.Row(new ParamKnob(ctx, P.Threshold, diameter: 52), new ParamKnob(ctx, P.Ratio, diameter: 52)));

            var graph = new GraphView(230, 196, (g, r) => DrawBand(g, r, ctx, machine), animate: true);

            var reduction = Layout.Column(
                new ParamDropdown(ctx, P.Action, "Action", 96),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.Range, diameter: 46), new ParamKnob(ctx, P.Lookahead, diameter: 46)),
                Layout.Row(new ParamKnob(ctx, P.Attack, diameter: 46), new ParamKnob(ctx, P.Release, diameter: 46)));

            var match = new ActionButton("Match", AccentColor, 104) { ToolTip = "Output takes back the loudness the de-esser took out (the gain-match reading, 3 s): one undo step" };
            match.Click += () =>
            {
                double db = machine?.Engine.GainMatchDb ?? double.NaN;
                if (!double.IsFinite(db)) return;
                double now = ctx.Value(P.Output);
                double to = NeoBuzz.Analysis.GainMatch.Match(now, db, -24, 24);
                ctx.Set(P.Output, Params.Table[P.Output].Raw(to));
                ctx.RaiseChanged();
            };
            Poll.Every(match, 0.25, () =>
            {
                double db = machine?.Engine.GainMatchDb ?? double.NaN;
                string text = double.IsFinite(db) ? $"Match {(-db).ToString("+0.0;−0.0;0.0")} dB" : "Match";
                if (text != match.Text)
                {
                    match.Text = text;
                    match.InvalidateVisual();
                }
            });

            var output = Layout.Column(
                new ParamKnob(ctx, P.Output, diameter: 40),
                new ParamChoice(ctx, P.Listen, "Listen", 40),
                new ParamChoice(ctx, P.Delta, "Delta", 40),
                Layout.Gap(1, 4),
                match);

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 196),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 196),
                Layout.Gap(4),
                new Meter("Reduction", () => (float)DspMath.DbToGain(-(machine?.Engine.ReductionDb ?? 0)), AccentColor, 196, reduction: true));

            return Layout.Row(
                new Section("Detector", AccentColor, detector),
                new Section("Band", AccentColor, graph),
                new Section("Reduction", AccentColor, reduction),
                new Section("Output", AccentColor, output),
                new Section("Meters", AccentColor, meters));
        }

        /// <summary>The detector's band over 1 – 20 kHz (the high-pass and low-pass around the frequency, in dB) and the reduction now.</summary>
        static void DrawBand(INeoCanvas g, NeoRect r, EditorContext ctx, NeoDeEsserMachine machine)
        {
            var plot = new NeoRect(r.X + 8, r.Y + 8, r.Width - 16, r.Height - 30);
            var grid = new NeoStroke(Palette.Grid, 1, false);
            const double rate = 48000, lo = 1000, hi = 20000, floor = -36;
            g.Line(plot.Left, plot.Top, plot.Right, plot.Top, grid);
            g.Line(plot.Left, plot.Bottom, plot.Right, plot.Bottom, grid);
            double f0 = ctx.Value(P.Freq), width = ctx.Value(P.Width);
            double half = Math.Pow(2, 0.5 * width);
            var hp = new Biquad();
            var lp = new Biquad();
            hp.Set(BiquadType.HighPass, Math.Max(20, f0 / half), rate, 0.7071067811865476);
            lp.Set(BiquadType.LowPass, Math.Min(0.45 * rate, f0 * half), rate, 0.7071067811865476);
            var path = new NeoPath();
            for (int k = 0; k <= 120; k++)
            {
                double f = lo * Math.Pow(hi / lo, k / 120.0);
                double db = 20 * Math.Log10(Math.Max(1e-6, hp.Magnitude(f, rate) * lp.Magnitude(f, rate)));
                double y = plot.Bottom - Math.Clamp((db - floor) / -floor, 0, 1) * plot.Height;
                double x = plot.Left + k / 120.0 * plot.Width;
                if (k == 0) path.MoveTo(x, y);
                else path.LineTo(x, y);
            }
            g.Path(path, NeoFill.None, new NeoStroke(AccentColor, 2));
            double red = machine?.Engine.ReductionDb ?? 0;
            g.Text(red > 0.05 ? $"reduction {red:F1} dB" : "the band the detector hears", plot.Left + 4, plot.Bottom + 6, 10, Palette.DimText);
        }
    }
}
