#nullable disable
// Neo Tape's editor (docs/specs/mastering-tools.md §5.11): the speed, saturation, bias and emphasis, the head bump and
// high-frequency loss with the small-signal response they make drawn over the audible range, the electronics and hiss,
// wow and flutter, the output (quality, mix, Delta, Match: Output set from the gain-match reading, one undo step) and meters.
// One source for WPF and Avalonia (the Neo editor kit); the response draws on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Tape
{
    internal sealed class NeoTapeEditor : NeoEditor
    {
        // Warm brown: oxide, clear of the meters' red.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xC8, 0x8A, 0x5A);

        protected override string Title => "Neo Tape";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoTapeMachine;

            var tape = Layout.Column(
                new ParamDropdown(ctx, P.Speed, "Speed", 96),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.Drive, diameter: 52), new ParamKnob(ctx, P.Bias, diameter: 52)),
                new ParamKnob(ctx, P.Emphasis, diameter: 40));

            var graph = new GraphView(210, 196, (g, r) => DrawResponse(g, r, ctx), animate: false);
            var head = Layout.Column(
                graph,
                Layout.Row(new ParamKnob(ctx, P.Bump, diameter: 40), new ParamKnob(ctx, P.Loss, diameter: 40)));

            var character = Layout.Column(
                new ParamKnob(ctx, P.Electronics, diameter: 46),
                new ParamKnob(ctx, P.Noise, diameter: 46));

            var motion = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Wow, diameter: 46), new ParamKnob(ctx, P.Flutter, diameter: 46)),
                new ParamKnob(ctx, P.WowRate, diameter: 40));

            var match = new ActionButton("Match", AccentColor, 104) { ToolTip = "Output takes back the loudness the tape added (the gain-match reading, 3 s): one undo step" };
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
                new ParamChoice(ctx, P.Quality, "Quality", 40),
                new ParamKnob(ctx, P.Mix, diameter: 40),
                new ParamKnob(ctx, P.Output, diameter: 40),
                new ParamChoice(ctx, P.Delta, "Delta", 40),
                Layout.Gap(1, 4),
                match);

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 196),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 196));

            return Layout.Row(
                new Section("Tape", AccentColor, tape),
                new Section("Head", AccentColor, head),
                new Section("Character", AccentColor, character),
                new Section("Motion", AccentColor, motion),
                new Section("Output", AccentColor, output),
                new Section("Meters", AccentColor, meters));
        }

        /// <summary>The small-signal response the head bump and the high-frequency loss make, 20 Hz – 20 kHz, ±6 dB.</summary>
        static void DrawResponse(INeoCanvas g, NeoRect r, EditorContext ctx)
        {
            var plot = new NeoRect(r.X + 8, r.Y + 8, r.Width - 16, r.Height - 30);
            var grid = new NeoStroke(Palette.Grid, 1, false);
            double cy = plot.Y + plot.Height / 2;
            g.Line(plot.Left, cy, plot.Right, cy, grid);
            int speed = (int)ctx.Value(P.Speed);
            double bump = ctx.Value(P.Bump), loss = ctx.Value(P.Loss);
            var path = new NeoPath();
            for (int k = 0; k <= 120; k++)
            {
                double f = 20 * Math.Pow(1000, k / 120.0);
                double db = TapeEngine.DesignDb(speed, bump, loss, f, 48000);
                double y = cy - Math.Clamp(db, -12, 12) / 12 * plot.Height / 2;
                double x = plot.Left + k / 120.0 * plot.Width;
                if (k == 0) path.MoveTo(x, y);
                else path.LineTo(x, y);
            }
            g.Path(path, NeoFill.None, new NeoStroke(AccentColor, 2));
            g.Text("head bump and HF loss at this speed", plot.Left + 4, plot.Bottom + 6, 10, Palette.DimText);
        }
    }
}
