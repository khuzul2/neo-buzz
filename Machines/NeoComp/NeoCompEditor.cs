#nullable disable
// Neo Comp's editor (docs/specs/machines.md §4): dynamics, the static curve with the live level,
// gain reduction, sidechain and output. WPF lays the editor out; the curve draws on the kit's
// canvas (INeoCanvas).

using System;
using System.Windows;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Comp
{
    internal sealed class NeoCompEditor : NeoEditor
    {
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0x2E, 0xC4, 0xB6);

        protected override string Title => "Neo Comp";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoCompMachine;

            var dynamics = Layout.Row(
                new ParamChoice(ctx, P.Mode, vertical: true, cellWidth: 64),
                Layout.Gap(6),
                Layout.Column(
                    Layout.Row(new ParamKnob(ctx, P.Threshold, diameter: 56), new ParamKnob(ctx, P.Ratio, diameter: 56)),
                    Layout.Row(new ParamKnob(ctx, P.Attack), new ParamKnob(ctx, P.Release), new ParamKnob(ctx, P.Knee)),
                    new ParamChoice(ctx, P.AutoRelease, "Auto release", 36)));

            var curve = new GraphView(170, 170, (g, r) => DrawCurve(g, r, ctx, machine), animate: true);
            var gr = new Meter("Gain reduction", () => (float)DspMath.DbToGain(-(machine?.Engine.GainReductionDb ?? 0)),
                AccentColor, 170, reduction: true);

            var sidechain = Layout.Column(
                new ParamChoice(ctx, P.Sidechain, cellWidth: 62),
                Layout.Row(new ParamKnob(ctx, P.ScLowCut), new ParamKnob(ctx, P.Lookahead)),
                new ParamKnob(ctx, P.Link));

            var output = Layout.Row(
                Layout.Column(
                    Layout.Row(new ParamKnob(ctx, P.Makeup), new ParamKnob(ctx, P.Character)),
                    Layout.Row(new ParamKnob(ctx, P.Mix), new ParamKnob(ctx, P.Output)),
                    new ParamChoice(ctx, P.AutoMakeup, "Auto makeup", 36)),
                Layout.Gap(6),
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 170),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 170));

            return Layout.Row(
                new Section("Dynamics", AccentColor, dynamics),
                new Section("Curve", AccentColor, Layout.Row(curve, Layout.Gap(6), gr)),
                new Section("Sidechain", AccentColor, sidechain),
                new Section("Output", AccentColor, output));
        }

        static void DrawCurve(INeoCanvas g, NeoRect r, EditorContext ctx, NeoCompMachine machine)
        {
            var values = new ParamValues(Params.Table);
            for (int i = 0; i < Params.Table.Length; i++) values.Set(i, ctx.Raw(i));
            const double lo = -60, hi = 0;
            double X(double db) => r.Left + 6 + (db - lo) / (hi - lo) * (r.Width - 12);
            double Y(double db) => r.Bottom - 6 - (Math.Clamp(db, lo, hi) - lo) / (hi - lo) * (r.Height - 12);
            var grid = new NeoStroke(Palette.Grid, 1, false);
            for (double d = lo; d <= hi; d += 12)
            {
                g.Line(X(d), r.Top, X(d), r.Bottom, grid);
                g.Line(r.Left, Y(d), r.Right, Y(d), grid);
            }
            g.Line(X(lo), Y(lo), X(hi), Y(hi), grid);
            var curve = new NeoPath();
            for (int i = 0; i <= 120; i++)
            {
                double x = lo + (hi - lo) * i / 120.0;
                curve.LineTo(X(x), Y(CompEngine.Curve(values, x)));
            }
            g.Path(curve, NeoFill.None, new NeoStroke(AccentColor, 2));
            double th = values.Value(P.Threshold);
            g.Line(X(th), r.Top, X(th), r.Bottom, new NeoStroke(AccentColor.WithAlpha(0x50), 1, false));
            if (machine != null)
            {
                double inDb = DspMath.GainToDb(Math.Max(1e-6f, machine.Engine.InMeter.Peak));
                if (inDb > lo)
                {
                    double outDb = inDb - machine.Engine.GainReductionDb;
                    g.Ellipse(new NeoPoint(X(Math.Min(hi, inDb)), Y(outDb)), 4, 4, AccentColor, NeoStroke.None);
                }
                g.Text($"GR {machine.Engine.GainReductionDb:F1} dB", r.X + 6, r.Y + 4, 10, Palette.DimText);
            }
        }
    }
}
