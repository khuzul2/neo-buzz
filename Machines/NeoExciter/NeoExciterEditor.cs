#nullable disable
// Neo Exciter's editor (docs/specs/mastering-tools.md §5.8): the curve and its drive, an Amount per band, the
// crossovers, the shelf on the added harmonics, a picture of what the shaper makes of a sine, the output with
// Delta and Match. One source for WPF and Avalonia (the Neo editor kit); the picture draws on the kit's canvas.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Exciter
{
    internal sealed class NeoExciterEditor : NeoEditor
    {
        // Amber: warmth, clear of the meters' red.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xF0, 0xB0, 0x4A);

        protected override string Title => "Neo Exciter";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoExciterMachine;

            var curve = Layout.Column(
                new ParamDropdown(ctx, P.Mode, "Mode", 96),
                Layout.Gap(1, 4),
                new ParamKnob(ctx, P.Drive, diameter: 52),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.Stereo, "Stereo", 40));

            var graph = new GraphView(210, 196, (g, r) => DrawCurve(g, r, ctx), animate: false);

            string[] names = { "Low", "Low mid", "High mid", "High" };
            var amounts = new UIElement[4];
            for (int b = 0; b < 4; b++) amounts[b] = new ParamKnob(ctx, P.Amount1 + b, names[b], 46);
            var bandBox = Layout.Column(
                Layout.Row(amounts),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.Xo1, "Xover 1", 40), new ParamKnob(ctx, P.Xo2, "Xover 2", 40), new ParamKnob(ctx, P.Xo3, "Xover 3", 40)));

            var shelf = Layout.Column(
                new ParamKnob(ctx, P.ShelfFreq, "Freq", 40),
                new ParamKnob(ctx, P.ShelfGain, "Gain", 40),
                new ParamChoice(ctx, P.Auto, "Auto gain", 40));

            var match = new ActionButton("Match", AccentColor, 104) { ToolTip = "Output takes back the loudness the exciter added (the gain-match reading, 3 s): one undo step" };
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
                new Section("Curve", AccentColor, curve),
                new Section("Shape", AccentColor, graph),
                new Section("Bands", AccentColor, bandBox),
                new Section("Shelf", AccentColor, shelf),
                new Section("Output", AccentColor, output),
                new Section("Meters", AccentColor, meters));
        }

        /// <summary>The shaper's transfer curve at the current drive (input −1 … +1, output as the audio sees it) and the harmonics a sine makes.</summary>
        static void DrawCurve(INeoCanvas g, NeoRect r, EditorContext ctx)
        {
            var plot = new NeoRect(r.X + 8, r.Y + 8, r.Width - 16, r.Height - 30);
            var grid = new NeoStroke(Palette.Grid, 1, false);
            double cx = plot.X + plot.Width / 2, cy = plot.Y + plot.Height / 2;
            g.Line(plot.Left, cy, plot.Right, cy, grid);
            g.Line(cx, plot.Top, cx, plot.Bottom, grid);
            int mode = (int)ctx.Value(P.Mode);
            double d = DspMath.DbToGain(ctx.Value(P.Drive));
            var s = new NeoBuzz.Drive.Shaper
            {
                Mode = mode == 1 ? NeoBuzz.Drive.DriveMode.Tube : mode == 2 ? NeoBuzz.Drive.DriveMode.Transistor : mode == 3 ? NeoBuzz.Drive.DriveMode.Diode : NeoBuzz.Drive.DriveMode.Tape,
                Hardness = mode == 2 ? 0.7 : 0,
                NegLevel = mode == 1 ? 0.8 : mode == 3 ? 0.6 : 1,
            };
            var path = new NeoPath();
            for (int k = 0; k <= 80; k++)
            {
                double x = -1 + k / 40.0;
                double y = mode == 4 ? 0.8 * (x * d) * (x * d) / (1 + (x * d) * (x * d)) - 0.4 : Math.Clamp(s.F(x * d), -1.2, 1.2) / 1.2;
                double px = plot.Left + (x + 1) / 2 * plot.Width, py = cy - y * plot.Height / 2;
                if (k == 0) path.MoveTo(px, py);
                else path.LineTo(px, py);
            }
            g.Path(path, NeoFill.None, new NeoStroke(AccentColor, 2));
            g.Text(mode == 4 ? "even harmonics only" : "the curve at this drive", plot.Left + 4, plot.Bottom + 6, 10, Palette.DimText);
        }
    }
}
