#nullable disable
// Neo Drive's editor (docs/specs/machines.md §4): mode, drive and curve on the left, the transfer
// curve with the live input level, tone and output on the right. WPF lays the editor out; the
// curve draws on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Drive
{
    internal sealed class NeoDriveEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Distortion identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Distortion, NeoColor.FromRgb(0xFF, 0x8A, 0x3D));

        protected override string Title => "Neo Drive";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoDriveMachine;
            var curve = new GraphView(180, 180, (g, r) => DrawCurve(g, r, ctx, machine), animate: true);

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
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 150),
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

        static void DrawCurve(INeoCanvas g, NeoRect r, EditorContext ctx, NeoDriveMachine machine)
        {
            var values = new ParamValues(Params.Table);
            for (int i = 0; i < Params.Table.Length; i++) values.Set(i, ctx.Raw(i));
            var c = new NeoPoint(r.X + r.Width / 2, r.Y + r.Height / 2);
            double half = r.Width / 2 - 8;
            var grid = new NeoStroke(Palette.Grid, 1, false);
            g.Line(r.Left, c.Y, r.Right, c.Y, grid);
            g.Line(c.X, r.Top, c.X, r.Bottom, grid);
            g.Line(c.X - half, c.Y + half, c.X + half, c.Y - half, grid);

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
            var curve = new NeoPath();
            for (int i = 0; i <= steps; i++)
            {
                double x = -1 + 2.0 * i / steps;
                curve.LineTo(c.X + x * half, c.Y - ys[i] * scale * half);
            }
            g.Path(curve, NeoFill.None, new NeoStroke(AccentColor, 2));

            // The input's current peak, on the curve.
            float level = machine?.Engine.InMeter.Peak ?? 0;
            if (level > 1e-4)
            {
                double x = Math.Min(1, level);
                double y = DriveEngine.Transfer(values, x) * scale;
                g.Ellipse(new NeoPoint(c.X + x * half, c.Y - y * half), 4, 4, AccentColor, NeoStroke.None);
            }
            g.Text(((DriveMode)values.Choice(P.Mode)).ToString(), r.X + 6, r.Y + 4, 10, Palette.DimText);
        }
    }
}
