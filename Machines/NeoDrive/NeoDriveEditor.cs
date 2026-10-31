#nullable disable
// Neo Drive's editor (docs/specs/machines.md §4): mode, drive and curve on the left, the transfer
// curve with the live input level, tone and output on the right.

using System;
using System.Windows;
using System.Windows.Media;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Drive
{
    internal sealed class NeoDriveEditor : NeoEditor
    {
        static readonly Color AccentColor = Color.FromRgb(0xFF, 0x8A, 0x3D);

        protected override string Title => "Neo Drive";
        protected override Color Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoDriveMachine;
            var curve = new GraphView(180, 180, (dc, r, v) => DrawCurve(dc, r, v, ctx, machine), animate: true);

            var drive = Layout.Row(
                new ParamChoice(ctx, P.Mode, vertical: true, cellWidth: 78),
                Layout.Gap(8),
                Layout.Column(
                    new ParamKnob(ctx, P.Drive, diameter: 58),
                    Layout.Row(new ParamKnob(ctx, P.Bias), new ParamKnob(ctx, P.Character))));

            var tone = Layout.Column(
                new ParamKnob(ctx, P.LowCut),
                new ParamKnob(ctx, P.Tone),
                new ParamKnob(ctx, P.HighCut));

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Theme.DimTextColor, 150),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 150));

            var output = Layout.Row(
                Layout.Column(
                    new ParamKnob(ctx, P.Output),
                    new ParamKnob(ctx, P.Mix),
                    new ParamChoice(ctx, P.AutoGain, "Auto gain", 32)),
                Layout.Gap(6),
                Layout.Column(
                    new ParamChoice(ctx, P.Quality, "Oversampling", 30),
                    Layout.Gap(1, 8),
                    meters));

            return Layout.Row(
                new Section("Drive", AccentColor, drive),
                new Section("Curve", AccentColor, curve),
                new Section("Tone", AccentColor, tone),
                new Section("Output", AccentColor, output));
        }

        static void DrawCurve(DrawingContext dc, Rect r, Visual v, EditorContext ctx, NeoDriveMachine machine)
        {
            var values = new ParamValues(Params.Table);
            for (int i = 0; i < Params.Table.Length; i++) values.Set(i, ctx.Raw(i));
            var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            double half = r.Width / 2 - 8;
            var grid = Theme.Pen(Theme.Grid, 1, false);
            dc.DrawLine(grid, new Point(r.Left, c.Y), new Point(r.Right, c.Y));
            dc.DrawLine(grid, new Point(c.X, r.Top), new Point(c.X, r.Bottom));
            dc.DrawLine(Theme.Pen(Theme.Grid, 1, false), new Point(c.X - half, c.Y + half), new Point(c.X + half, c.Y - half));

            // Curve over input −1 … 1, scaled so its largest output fills the frame.
            const int steps = 160;
            var ys = new double[steps + 1];
            double peak = 1e-6;
            for (int i = 0; i <= steps; i++)
            {
                double x = -1 + 2.0 * i / steps;
                ys[i] = DriveEngine.Transfer(values, x);
                peak = Math.Max(peak, Math.Abs(ys[i]));
            }
            double scale = 1 / Math.Max(1, peak);
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                for (int i = 0; i <= steps; i++)
                {
                    double x = -1 + 2.0 * i / steps;
                    var p = new Point(c.X + x * half, c.Y - ys[i] * scale * half);
                    if (i == 0) s.BeginFigure(p, false, false);
                    else s.LineTo(p, true, false);
                }
            }
            g.Freeze();
            var accent = Theme.Solid(AccentColor);
            dc.DrawGeometry(null, Theme.Pen(accent, 2), g);

            // The input's current peak, on the curve.
            float level = machine?.Engine.InMeter.Peak ?? 0;
            if (level > 1e-4)
            {
                double x = Math.Min(1, level);
                double y = DriveEngine.Transfer(values, x) * scale;
                dc.DrawEllipse(accent, null, new Point(c.X + x * half, c.Y - y * half), 4, 4);
            }
            Theme.DrawText(dc, v, ((DriveMode)values.Choice(P.Mode)).ToString(), r.X + 6, r.Y + 4, 10, Theme.DimText);
        }
    }
}
