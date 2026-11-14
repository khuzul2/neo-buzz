#nullable disable
// Neo Spectral's editor (docs/specs/mastering-tools.md §5.16): the mode with Depth and Knee, the shape of the detector
// (Selectivity, Smoothing, Tilt, Lift), the mask drawn over 30 Hz – 20 kHz in dB with the working region marked, the times and the
// region, the output with Mix, Delta and Match (Output set from the gain-match reading, one undo step), and meters.
// One source for WPF and Avalonia (the Neo editor kit); the picture draws on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Spectral
{
    internal sealed class NeoSpectralEditor : NeoEditor
    {
        // Teal: the mask, clear of the meters' red.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0x4F, 0xB3, 0xC9);

        protected override string Title => "Neo Spectral";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoSpectralMachine;

            var detector = Layout.Column(
                new ParamDropdown(ctx, P.Mode, "Mode", 96),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.Depth, diameter: 52), new ParamKnob(ctx, P.Knee, diameter: 52)),
                Layout.Row(new ParamKnob(ctx, P.Selectivity, diameter: 52), new ParamKnob(ctx, P.Smoothing, diameter: 52)));

            var shape = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Tilt, diameter: 46), new ParamKnob(ctx, P.Lift, diameter: 46)),
                Layout.Row(new ParamKnob(ctx, P.Attack, diameter: 46), new ParamKnob(ctx, P.Release, diameter: 46)));

            var graph = new GraphView(300, 196, (g, r) => DrawMask(g, r, ctx, machine), animate: true);

            var region = Layout.Column(
                new ParamKnob(ctx, P.Low, diameter: 46),
                new ParamKnob(ctx, P.High, diameter: 46));

            var match = new ActionButton("Match", AccentColor, 104) { ToolTip = "Output takes back the loudness the suppressor took out (the gain-match reading, 3 s): one undo step" };
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
                Layout.Row(new ParamKnob(ctx, P.Output, diameter: 40), new ParamKnob(ctx, P.Mix, diameter: 40)),
                new ParamChoice(ctx, P.Delta, "Delta", 40),
                Layout.Gap(1, 4),
                match);

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 196),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 196),
                Layout.Gap(4),
                new Meter("Cut", () => (float)DspMath.DbToGain(-(machine?.Engine.ReductionDb ?? 0)), AccentColor, 196, reduction: true));

            return Layout.Row(
                new Section("Detector", AccentColor, detector),
                new Section("Time", AccentColor, shape),
                new Section("Mask", AccentColor, graph),
                new Section("Region", AccentColor, region),
                new Section("Output", AccentColor, output),
                new Section("Meters", AccentColor, meters));
        }

        /// <summary>The mask now over 30 Hz – 20 kHz (dB, +12 … −24) with the working region marked.</summary>
        static void DrawMask(INeoCanvas g, NeoRect r, EditorContext ctx, NeoSpectralMachine machine)
        {
            var plot = new NeoRect(r.X + 8, r.Y + 8, r.Width - 16, r.Height - 30);
            var grid = new NeoStroke(Palette.Grid, 1, false);
            const double lo = 30, hi = 20000, top = 12, bottom = -24;
            double Y(double db) => plot.Top + (top - Math.Clamp(db, bottom, top)) / (top - bottom) * plot.Height;
            double X(double hz) => plot.Left + Math.Log(Math.Clamp(hz, lo, hi) / lo) / Math.Log(hi / lo) * plot.Width;
            foreach (double db in new[] { 12.0, 0.0, -12.0, -24.0 }) g.Line(plot.Left, Y(db), plot.Right, Y(db), grid);
            foreach (double hz in new[] { 100.0, 1000.0, 10000.0 }) g.Line(X(hz), plot.Top, X(hz), plot.Bottom, grid);
            var edge = new NeoStroke(AccentColor, 1, true);
            double low = ctx.Value(P.Low), high = ctx.Value(P.High);
            g.Line(X(low), plot.Top, X(low), plot.Bottom, edge);
            g.Line(X(high), plot.Top, X(high), plot.Bottom, edge);
            var curve = machine?.Engine.Curve;
            if (curve != null)
            {
                var path = new NeoPath();
                for (int b = 0; b < curve.Length; b++)
                {
                    double x = plot.Left + (b + 0.5) / curve.Length * plot.Width;
                    double y = Y(curve[b]);
                    if (b == 0) path.MoveTo(x, y);
                    else path.LineTo(x, y);
                }
                g.Path(path, NeoFill.None, new NeoStroke(AccentColor, 2));
            }
            double red = machine?.Engine.ReductionDb ?? 0;
            g.Text(red > 0.05 ? $"deepest cut {red:F1} dB" : "the mask: nothing stands out", plot.Left + 4, plot.Bottom + 6, 10, Palette.DimText);
        }
    }
}
