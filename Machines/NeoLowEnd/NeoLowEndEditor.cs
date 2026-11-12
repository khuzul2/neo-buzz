#nullable disable
// Neo Low End's editor (docs/specs/mastering-tools.md §5.5): the crossover and phase, Punch, Sustain, Speed and
// Contrast, the low band's response (the crossover's low and high shares on a log axis, with the band's level now),
// Balance with its Target, Peak control, mono below a frequency, the output with Delta and Match (Output set from
// the gain-match reading, one undo step) and meters. One source for WPF and Avalonia (the Neo editor kit); the
// response draws on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.LowEnd
{
    internal sealed class NeoLowEndEditor : NeoEditor
    {
        // Indigo: the low end's depth, clear of the meters' red.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0x6C, 0x8C, 0xF5);

        protected override string Title => "Neo Low End";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoLowEndMachine;

            var band = Layout.Column(
                new ParamKnob(ctx, P.Xo, "Crossover", 58),
                new ParamChoice(ctx, P.Phase, "Phase", 40));

            var graph = new GraphView(220, 196, (g, r) => DrawBand(g, r, ctx, machine), animate: true);

            var dynamics = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Punch, diameter: 50), new ParamKnob(ctx, P.Sustain, diameter: 50)),
                Layout.Row(new ParamKnob(ctx, P.Contrast, diameter: 50), new ParamDropdown(ctx, P.Speed, "Speed", 80)));

            var balance = Layout.Column(
                new ParamKnob(ctx, P.Target, diameter: 50),
                new ParamKnob(ctx, P.Balance, diameter: 50));

            var shaping = Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.Peak, "Peak control", 40), new ParamKnob(ctx, P.PeakLevel, "Level", 40)),
                Layout.Row(new ParamChoice(ctx, P.Mono, "Mono", 40), new ParamKnob(ctx, P.MonoFreq, "Below", 40)));

            var match = new ActionButton("Match", AccentColor, 104) { ToolTip = "Output takes back the loudness Low End added (the gain-match reading, 3 s): one undo step" };
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
                new ParamChoice(ctx, P.Delta, "Delta", 40),
                Layout.Gap(1, 4),
                match);

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 196),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 196));

            return Layout.Row(
                new Section("Band", AccentColor, band),
                new Section("Response", AccentColor, graph),
                new Section("Dynamics", AccentColor, dynamics),
                new Section("Balance", AccentColor, balance),
                new Section("Shaping", AccentColor, shaping),
                new Section("Output", AccentColor, output),
                new Section("Meters", AccentColor, meters));
        }

        /// <summary>The split: the low share (filled) and the high share, 20 Hz … 20 kHz across, −36 … +6 dB up, and the crest of the low band now.</summary>
        static void DrawBand(INeoCanvas g, NeoRect r, EditorContext ctx, NeoLowEndMachine machine)
        {
            const double lo = -36, hi = 6;
            var plot = new NeoRect(r.X + 30, r.Y + 8, r.Width - 38, r.Height - 30);
            double X(double hz) => plot.Left + Math.Log(Math.Clamp(hz, 20, 20000) / 20) / Math.Log(1000) * plot.Width;
            double Y(double db) => plot.Bottom - (Math.Clamp(db, lo, hi) - lo) / (hi - lo) * plot.Height;
            var grid = new NeoStroke(Palette.Grid, 1, false);
            for (double d = lo; d <= hi; d += 12)
            {
                g.Line(plot.Left, Y(d), plot.Right, Y(d), grid);
                g.Text(d.ToString("+0;−0;0"), plot.Left - 4, Y(d) - 7, 9.5, Palette.DimText, align: NeoAlign.Right);
            }
            foreach (double f in new[] { 100.0, 1000, 10000 })
            {
                g.Line(X(f), plot.Top, X(f), plot.Bottom, grid);
                g.Text(f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}", X(f), plot.Bottom + 4, 9.5, Palette.DimText, align: NeoAlign.Center);
            }
            double xo = ctx.Value(P.Xo);
            double rate = 48000;
            var low = new NeoPath();
            var high = new NeoPath();
            for (int k = 0; k <= 96; k++)
            {
                double hz = 20 * Math.Pow(1000, k / 96.0);
                double share = LowEndDesign.Band(hz, xo, rate);
                double lowDb = 20 * Math.Log10(Math.Max(1e-6, share));
                double highDb = 20 * Math.Log10(Math.Max(1e-6, 1 - share));
                if (k == 0) { low.MoveTo(X(hz), Y(lowDb)); high.MoveTo(X(hz), Y(highDb)); }
                else { low.LineTo(X(hz), Y(lowDb)); high.LineTo(X(hz), Y(highDb)); }
            }
            g.Path(low, NeoFill.None, new NeoStroke(AccentColor, 2));
            g.Path(high, NeoFill.None, new NeoStroke(Palette.DimText, 1.5));
            g.Line(X(xo), plot.Top, X(xo), plot.Bottom, new NeoStroke(Palette.Text.WithAlpha(0xB0), 1, false));
            g.Text($"{xo:0} Hz", X(xo) + 4, plot.Top + 2, 10, Palette.Text);
            if (machine != null)
            {
                double ci = machine.Engine.CrestInDb, co = machine.Engine.CrestOutDb;
                if (double.IsFinite(ci) && double.IsFinite(co))
                    g.Text($"low band crest {ci:F1} → {co:F1} dB", plot.Left + 4, plot.Bottom - 30, 10, Palette.DimText);
                g.Text(machine.Engine.Phase == 1 ? $"linear phase, latency {machine.Engine.Latency} samples" : "minimum phase, no latency", plot.Left + 4, plot.Bottom - 16, 9.5, Palette.DimText);
            }
        }
    }
}
