#nullable disable
// Neo Clipper's editor (docs/specs/mastering-tools.md §5.1): drive and ceiling, the curve with the input's peak on
// it, the shape, oversampling and bands, the output with Mix, Delta and Match (Output set from the gain-match
// reading, one undo step), and meters. One source for WPF and Avalonia (the Neo editor kit); the curve draws on the
// kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Clipper
{
    internal sealed class NeoClipperEditor : NeoEditor
    {
        // Amber: a clipper's heat, clear of the meters' red.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xF2, 0xA6, 0x3B);

        protected override string Title => "Neo Clipper";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoClipperMachine;

            var clip = Layout.Column(
                new ParamKnob(ctx, P.Drive, diameter: 58),
                new ParamKnob(ctx, P.Ceiling, diameter: 58));

            var graph = new GraphView(220, 196, (g, r) => DrawCurve(g, r, ctx, machine), animate: true);

            var shape = Layout.Column(
                new ParamDropdown(ctx, P.Shape, "Shape", 96),
                Layout.Gap(1, 4),
                new ParamKnob(ctx, P.Knee),
                Layout.Gap(1, 4),
                new ParamDropdown(ctx, P.Oversample, "Oversampling", 96));

            var bands = Layout.Column(
                new ParamDropdown(ctx, P.Bands, "Bands", 96),
                Layout.Row(new ParamKnob(ctx, P.Xo1, "Xover 1", 40), new ParamKnob(ctx, P.Xo2, "Xover 2", 40)),
                Layout.Row(new ParamKnob(ctx, P.LowDrive, "Low", 40), new ParamKnob(ctx, P.MidDrive, "Mid", 40), new ParamKnob(ctx, P.HighDrive, "High", 40)));

            var match = new ActionButton("Match", AccentColor, 104) { ToolTip = "Output takes back the loudness the clipper added (the gain-match reading, 3 s): one undo step" };
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
                new ParamChoice(ctx, P.Stereo, "Stereo", 40),
                Layout.Row(new ParamKnob(ctx, P.Mix, diameter: 40), new ParamKnob(ctx, P.Output, diameter: 40)),
                new ParamChoice(ctx, P.Delta, "Delta", 40),
                Layout.Gap(1, 4),
                match);

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 196),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 196),
                Layout.Gap(4),
                new Meter("Clipped", () => (float)DspMath.DbToGain(-(machine?.Engine.ClipDb ?? 0)), AccentColor, 196, reduction: true));

            return Layout.Row(
                new Section("Clip", AccentColor, clip),
                new Section("Curve", AccentColor, graph),
                new Section("Shape", AccentColor, shape),
                new Section("Bands", AccentColor, bands),
                new Section("Output", AccentColor, output),
                new Section("Meters", AccentColor, meters));
        }

        /// <summary>
        /// The curve: input level (dBFS, after the drive, −36 … +12) across, output level up, the ceiling as a line, the
        /// straight line faint, and the input's peak now as a dot on the curve.
        /// </summary>
        static void DrawCurve(INeoCanvas g, NeoRect r, EditorContext ctx, NeoClipperMachine machine)
        {
            const double lo = -36, hi = 12;
            var plot = new NeoRect(r.X + 30, r.Y + 8, r.Width - 38, r.Height - 30);
            double X(double db) => plot.Left + (Math.Clamp(db, lo, hi) - lo) / (hi - lo) * plot.Width;
            double Y(double db) => plot.Bottom - (Math.Clamp(db, lo, hi) - lo) / (hi - lo) * plot.Height;
            var grid = new NeoStroke(Palette.Grid, 1, false);
            for (double d = lo; d <= hi; d += 12)
            {
                g.Line(X(d), plot.Top, X(d), plot.Bottom, grid);
                g.Line(plot.Left, Y(d), plot.Right, Y(d), grid);
                g.Text(d.ToString("+0;−0;0"), plot.Left - 4, Y(d) - 7, 9.5, Palette.DimText, align: NeoAlign.Right);
                g.Text(d.ToString("+0;−0;0"), X(d), plot.Bottom + 4, 9.5, Palette.DimText, align: NeoAlign.Center);
            }
            g.Line(X(lo), Y(lo), X(hi), Y(hi), new NeoStroke(Palette.DimText.WithAlpha(0x60), 1, false));

            double ceilingDb = ctx.Value(P.Ceiling);
            double c = DspMath.DbToGain(ceilingDb);
            var curve = ClipCurve.Make((ClipShape)(int)ctx.Value(P.Shape), ctx.Value(P.Knee));
            var path = new NeoPath();
            for (int k = 0; k <= 96; k++)
            {
                double inDb = lo + (hi - lo) * k / 96.0;
                double x = DspMath.DbToGain(inDb);
                double y = c * curve.F(x / c);
                double outDb = 20 * Math.Log10(Math.Max(1e-9, y));
                if (k == 0) path.MoveTo(X(inDb), Y(outDb));
                else path.LineTo(X(inDb), Y(outDb));
            }
            g.Path(path, NeoFill.None, new NeoStroke(AccentColor, 2));
            g.Line(plot.Left, Y(ceilingDb), plot.Right, Y(ceilingDb), new NeoStroke(Palette.Text.WithAlpha(0xB0), 1, false));
            g.Text($"ceiling {Params.Table[P.Ceiling].Describe(ctx.Raw(P.Ceiling))}", plot.Right - 4, Y(ceilingDb) - 15, 10, Palette.Text, align: NeoAlign.Right);

            if (machine != null)
            {
                double peak = machine.Engine.InMeter.Peak * DspMath.DbToGain(ctx.Value(P.Drive));
                if (peak > 1e-4)
                {
                    double inDb = 20 * Math.Log10(peak);
                    double outDb = 20 * Math.Log10(Math.Max(1e-9, c * curve.F(peak / c)));
                    g.Ellipse(new NeoPoint(X(inDb), Y(outDb)), 4, 4, AccentColor, new NeoStroke(Palette.Text, 1));
                }
                double clip = machine.Engine.ClipDb;
                g.Text(clip > 0.05 ? $"{clip:F1} dB over" : "under the ceiling", plot.Left + 4, plot.Top + 2, 10, clip > 0.05 ? AccentColor : Palette.DimText);
                g.Text($"{machine.Engine.Factor}×, latency {machine.Engine.Latency} samples", plot.Left + 4, plot.Top + 16, 9.5, Palette.DimText);
            }
        }
    }
}
