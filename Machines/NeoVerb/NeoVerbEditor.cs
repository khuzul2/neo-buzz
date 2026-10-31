#nullable disable
// Neo Verb's editor (docs/specs/machines.md §4): the space, a picture of the response (early
// reflections and the decay in three bands), motion and output.

using System;
using System.Windows;
using System.Windows.Media;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Verb
{
    internal sealed class NeoVerbEditor : NeoEditor
    {
        static readonly Color AccentColor = Color.FromRgb(0x7C, 0x8C, 0xFF);
        static readonly Color LowColor = Color.FromRgb(0xFF, 0xB0, 0x5C);
        static readonly Color HighColor = Color.FromRgb(0x5C, 0xE1, 0xFF);

        protected override string Title => "Neo Verb";
        protected override Color Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoVerbMachine;

            var space = Layout.Row(
                new ParamChoice(ctx, P.Mode, vertical: true, cellWidth: 70),
                Layout.Gap(6),
                Layout.Column(
                    Layout.Row(new ParamKnob(ctx, P.Decay, diameter: 56), new ParamKnob(ctx, P.Size, diameter: 56)),
                    Layout.Row(new ParamKnob(ctx, P.PreDelay), new ParamKnob(ctx, P.Diffusion)),
                    Layout.Row(new ParamKnob(ctx, P.LowDecay), new ParamKnob(ctx, P.Damping))));

            var response = new GraphView(230, 170, (dc, r, v) => DrawResponse(dc, r, v, ctx));
            ctx.Changed += response.InvalidateVisual;

            var motion = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.ModDepth), new ParamKnob(ctx, P.ModRate)),
                new ParamKnob(ctx, P.Shimmer),
                new ParamChoice(ctx, P.Freeze, "Freeze", 36));

            var output = Layout.Row(
                Layout.Column(
                    Layout.Row(new ParamKnob(ctx, P.Early), new ParamKnob(ctx, P.Late)),
                    Layout.Row(new ParamKnob(ctx, P.LowCut), new ParamKnob(ctx, P.HighCut)),
                    Layout.Row(new ParamKnob(ctx, P.Width), new ParamKnob(ctx, P.Mix))),
                Layout.Gap(6),
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Theme.DimTextColor, 200),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 200));

            return Layout.Row(
                new Section("Space", AccentColor, space),
                new Section("Response", AccentColor, response),
                new Section("Motion", AccentColor, motion),
                new Section("Output", AccentColor, output));
        }

        static void DrawResponse(DrawingContext dc, Rect r, Visual v, EditorContext ctx)
        {
            var values = new ParamValues(Params.Table);
            for (int i = 0; i < Params.Table.Length; i++) values.Set(i, ctx.Raw(i));
            double t60 = values.Value(P.Decay);
            double pre = values.Value(P.PreDelay) / 1000;
            bool freeze = values.On(P.Freeze);
            double span = Math.Max(0.5, Math.Min(40, (freeze ? 4 : t60 * 1.15) + pre));
            double X(double t) => r.Left + 8 + t / span * (r.Width - 16);
            double Y(double db) => r.Top + 18 + (-Math.Clamp(db, -60, 0)) / 60 * (r.Height - 26);
            var grid = Theme.Pen(Theme.Grid, 1, false);
            for (int d = 0; d >= -60; d -= 20) dc.DrawLine(grid, new Point(r.Left, Y(d)), new Point(r.Right, Y(d)));

            // Early reflections: spikes after the pre-delay.
            double early = values.Value(P.Early);
            if (!double.IsNegativeInfinity(early))
            {
                var pen = Theme.Pen(Theme.Solid(AccentColor, 0x90), 1.5);
                var rng = new Rng(0xBEEF00u + (uint)values.Choice(P.Mode));
                double size = 0.4 + 1.2 * values.Value(P.Size);
                for (int k = 0; k < 10; k++)
                {
                    double t = pre + (0.004 + 0.008 * k + 0.006 * rng.NextUnipolar()) * size;
                    double db = early - 3 * k - 4 * rng.NextUnipolar();
                    dc.DrawLine(pen, new Point(X(t), Y(-60)), new Point(X(t), Y(db)));
                }
            }

            // Decay lines (dB is linear in time) for the low, mid and high bands.
            double late = values.Value(P.Late);
            if (double.IsNegativeInfinity(late)) late = -60;
            void Band(double decay, Color c, string label)
            {
                double t0 = pre + 0.02;
                double t1 = freeze ? span : t0 + decay * (60 + late) / 60;
                double db1 = freeze ? late : -60;
                dc.DrawLine(Theme.Pen(Theme.Solid(c), 2), new Point(X(t0), Y(late)), new Point(X(Math.Min(span, t1)), Y(t1 > span ? late - (late + 60) * (span - t0) / (t1 - t0) : db1)));
            }
            Band(VerbEngine.DecayAt(values, 100), LowColor, "Low");
            Band(VerbEngine.DecayAt(values, 1000), AccentColor, "Mid");
            Band(VerbEngine.DecayAt(values, 20000), HighColor, "High");

            Theme.Text(dc, v, freeze ? "Frozen" : $"T60 {ParamDef.Time(t60)}", r.X + 6, r.Y + 3, 10, Theme.DimText);
            Theme.Text(dc, v, "low", r.Right - 70, r.Y + 3, 10, Theme.Solid(LowColor));
            Theme.Text(dc, v, "mid", r.Right - 48, r.Y + 3, 10, Theme.Solid(AccentColor));
            Theme.Text(dc, v, "high", r.Right - 26, r.Y + 3, 10, Theme.Solid(HighColor));
        }
    }
}
