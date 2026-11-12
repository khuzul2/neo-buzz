#nullable disable
// Neo Gate's editor (docs/specs/mastering-tools.md §5.13): the threshold and its hysteresis, the ratio and Range, the
// timing, the key (detection, sidechain, filter, Listen), the transfer curve (input level against output level, the
// open gate on the diagonal, the closed one at its floor) with the gain now, the output with Delta and Match, and meters.
// One source for WPF and Avalonia (the Neo editor kit); the curve draws on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Gate
{
    internal sealed class NeoGateEditor : NeoEditor
    {
        // Teal: a gate's open window, clear of the meters' red.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0x4A, 0xB8, 0xC9);

        protected override string Title => "Neo Gate";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoGateMachine;

            var gate = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Threshold, diameter: 56), new ParamKnob(ctx, P.Hysteresis, diameter: 56)),
                Layout.Row(new ParamKnob(ctx, P.Ratio, diameter: 56), new ParamKnob(ctx, P.Range, diameter: 56)));

            var curve = new GraphView(190, 196, (g, r) => DrawCurve(g, r, ctx, machine), animate: true);

            var timing = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Attack), new ParamKnob(ctx, P.Hold)),
                Layout.Row(new ParamKnob(ctx, P.Release), new ParamKnob(ctx, P.Lookahead)));

            var key = Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.Detection, "Detection", 40), new ParamChoice(ctx, P.Sidechain, "Sidechain", 62)),
                Layout.Row(new ParamKnob(ctx, P.KeyLow), new ParamKnob(ctx, P.KeyHigh)),
                new ParamChoice(ctx, P.Listen, "Listen", 40));

            var match = new ActionButton("Match", AccentColor, 104) { ToolTip = "Output takes back the loudness the gate took out (the gain-match reading, 3 s): one undo step" };
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
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 196),
                Layout.Gap(4),
                new Meter("Gain", () => (float)DspMath.DbToGain(machine?.Engine.GainDb ?? 0), AccentColor, 196, reduction: true));

            return Layout.Row(
                new Section("Gate", AccentColor, gate),
                new Section("Curve", AccentColor, curve),
                new Section("Timing", AccentColor, timing),
                new Section("Key", AccentColor, key),
                new Section("Output", AccentColor, output),
                new Section("Meters", AccentColor, meters));
        }

        /// <summary>Output level against input level (−80 … 0 dB): the open gate on the diagonal, the closed one at its floor (Range, or the expander's slope).</summary>
        static void DrawCurve(INeoCanvas g, NeoRect r, EditorContext ctx, NeoGateMachine machine)
        {
            var plot = new NeoRect(r.X + 8, r.Y + 8, r.Width - 16, r.Height - 30);
            var grid = new NeoStroke(Palette.Grid, 1, false);
            g.Line(plot.Left, plot.Bottom, plot.Right, plot.Top, grid);
            double thr = ctx.Value(P.Threshold), close = thr - ctx.Value(P.Hysteresis), ratio = ctx.Value(P.Ratio), range = ctx.Value(P.Range);
            var path = new NeoPath();
            for (int k = 0; k <= 80; k++)
            {
                double inDb = -80 + k;
                double gainDb = inDb >= close ? 0 : double.IsInfinity(ratio) ? range : Math.Max(range, -(close - inDb) * (ratio - 1));
                double outDb = inDb + gainDb;
                outDb = Math.Max(outDb, -80);
                double px = plot.Left + (inDb + 80) / 80 * plot.Width, py = plot.Bottom - (outDb + 80) / 80 * plot.Height;
                if (k == 0) path.MoveTo(px, py);
                else path.LineTo(px, py);
            }
            g.Path(path, NeoFill.None, new NeoStroke(AccentColor, 2));
            double gain = machine?.Engine.GainDb ?? 0;
            g.Text($"gain {gain:F1} dB", plot.Left + 4, plot.Bottom + 6, 10, Palette.DimText);
        }
    }
}
