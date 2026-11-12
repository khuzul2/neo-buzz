#nullable disable
// Neo Imager's editor (docs/specs/mastering-tools.md §5.7): per band a Width and a Stereoize knob, the crossovers,
// Recover sides, a display of each band's correlation and side-against-mid, the output with Delta and Match.
// One source for WPF and Avalonia (the Neo editor kit); the display draws on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Imager
{
    internal sealed class NeoImagerEditor : NeoEditor
    {
        // Violet: the side, clear of the meters' red.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xB0, 0x7C, 0xF0);

        protected override string Title => "Neo Imager";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoImagerMachine;

            string[] names = { "Low", "Low mid", "High mid", "High" };
            var columns = new UIElement[4];
            for (int b = 0; b < 4; b++)
                columns[b] = Layout.Column(
                    new ParamKnob(ctx, P.Width1 + b, names[b] + " width", 52),
                    Layout.Gap(1, 4),
                    new ParamKnob(ctx, P.Stereo1 + b, "Stereoize", 40));
            var width = Layout.Row(columns);

            var graph = new GraphView(230, 196, (g, r) => DrawBands(g, r, machine), animate: true);

            var split = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Xo1, "Xover 1", 40), new ParamKnob(ctx, P.Xo2, "Xover 2", 40), new ParamKnob(ctx, P.Xo3, "Xover 3", 40)),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.Recover, "Recover sides", 40));

            var match = new ActionButton("Match", AccentColor, 104) { ToolTip = "Output takes back the loudness the imager added (the gain-match reading, 3 s): one undo step" };
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
                new Section("Width", AccentColor, width),
                new Section("Bands", AccentColor, graph),
                new Section("Split", AccentColor, split),
                new Section("Output", AccentColor, output),
                new Section("Meters", AccentColor, meters));
        }

        /// <summary>Per band a bar: the correlation of left and right (−1 … +1, up is mono) and the side against the mid in dB as text.</summary>
        static void DrawBands(INeoCanvas g, NeoRect r, NeoImagerMachine machine)
        {
            var plot = new NeoRect(r.X + 30, r.Y + 8, r.Width - 38, r.Height - 40);
            double Y(double c) => plot.Bottom - (Math.Clamp(c, -1, 1) + 1) / 2 * plot.Height;
            var grid = new NeoStroke(Palette.Grid, 1, false);
            for (double c = -1; c <= 1; c += 0.5)
            {
                g.Line(plot.Left, Y(c), plot.Right, Y(c), grid);
                g.Text(c.ToString("+0.0;−0.0;0"), plot.Left - 4, Y(c) - 7, 9.5, Palette.DimText, align: NeoAlign.Right);
            }
            if (machine == null) return;
            double bw = plot.Width / 4;
            for (int b = 0; b < 4; b++)
            {
                double c = machine.Engine.Correlation(b);
                double x = plot.Left + b * bw + bw * 0.2;
                g.Rect(new NeoRect(x, Math.Min(Y(c), Y(0)), bw * 0.6, Math.Max(1, Math.Abs(Y(c) - Y(0)))), AccentColor.WithAlpha(0xC0), NeoStroke.None);
                double wd = machine.Engine.WidthDb(b);
                g.Text(wd <= -99 ? "mono" : wd.ToString("+0.0;−0.0;0.0") + " dB", x, plot.Bottom + 6, 9.5, Palette.DimText);
            }
            g.Text("correlation, side against mid", plot.Left + 4, plot.Top + 2, 10, Palette.DimText);
        }
    }
}
