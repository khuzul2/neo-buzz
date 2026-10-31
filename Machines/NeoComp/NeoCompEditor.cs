#nullable disable
// Neo Comp's editor (docs/specs/machines.md §4): dynamics, the static curve with the live level,
// gain reduction, sidechain and output.

using System;
using System.Windows;
using System.Windows.Media;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Comp
{
    internal sealed class NeoCompEditor : NeoEditor
    {
        static readonly Color AccentColor = Color.FromRgb(0x2E, 0xC4, 0xB6);

        protected override string Title => "Neo Comp";
        protected override Color Accent => AccentColor;
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

            var curve = new GraphView(170, 170, (dc, r, v) => DrawCurve(dc, r, v, ctx, machine), animate: true);
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
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Theme.DimTextColor, 170),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 170));

            return Layout.Row(
                new Section("Dynamics", AccentColor, dynamics),
                new Section("Curve", AccentColor, Layout.Row(curve, Layout.Gap(6), gr)),
                new Section("Sidechain", AccentColor, sidechain),
                new Section("Output", AccentColor, output));
        }

        static void DrawCurve(DrawingContext dc, Rect r, Visual v, EditorContext ctx, NeoCompMachine machine)
        {
            var values = new ParamValues(Params.Table);
            for (int i = 0; i < Params.Table.Length; i++) values.Set(i, ctx.Raw(i));
            const double lo = -60, hi = 0;
            double X(double db) => r.Left + 6 + (db - lo) / (hi - lo) * (r.Width - 12);
            double Y(double db) => r.Bottom - 6 - (Math.Clamp(db, lo, hi) - lo) / (hi - lo) * (r.Height - 12);
            var grid = Theme.Pen(Theme.Grid, 1, false);
            for (double d = lo; d <= hi; d += 12)
            {
                dc.DrawLine(grid, new Point(X(d), r.Top), new Point(X(d), r.Bottom));
                dc.DrawLine(grid, new Point(r.Left, Y(d)), new Point(r.Right, Y(d)));
            }
            dc.DrawLine(grid, new Point(X(lo), Y(lo)), new Point(X(hi), Y(hi)));
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                for (int i = 0; i <= 120; i++)
                {
                    double x = lo + (hi - lo) * i / 120.0;
                    var p = new Point(X(x), Y(CompEngine.Curve(values, x)));
                    if (i == 0) s.BeginFigure(p, false, false);
                    else s.LineTo(p, true, false);
                }
            }
            g.Freeze();
            var accent = Theme.Solid(AccentColor);
            dc.DrawGeometry(null, Theme.Pen(accent, 2), g);
            double th = values.Value(P.Threshold);
            dc.DrawLine(Theme.Pen(Theme.Solid(AccentColor, 0x50), 1, false), new Point(X(th), r.Top), new Point(X(th), r.Bottom));
            if (machine != null)
            {
                double inDb = DspMath.GainToDb(Math.Max(1e-6f, machine.Engine.InMeter.Peak));
                if (inDb > lo)
                {
                    double outDb = inDb - machine.Engine.GainReductionDb;
                    dc.DrawEllipse(accent, null, new Point(X(Math.Min(hi, inDb)), Y(outDb)), 4, 4);
                }
                Theme.Text(dc, v, $"GR {machine.Engine.GainReductionDb:F1} dB", r.X + 6, r.Y + 4, 10, Theme.DimText);
            }
        }
    }
}
