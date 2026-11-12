#nullable disable
// Neo Transient's editor (docs/specs/mastering-tools.md §5.4): Attack and Sustain, the detector's speed and lookahead,
// the gain the shaper applied over the last three seconds drawn as a trace, Impact's mode, crossovers and four
// knobs, the output with Mix, Delta and Match (Output set from the gain-match reading, one undo step), and meters.
// One source for WPF and Avalonia (the Neo editor kit); the trace draws on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Transient
{
    internal sealed class NeoTransientEditor : NeoEditor
    {
        // Coral: an onset's flash, clear of the meters' red.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xE8, 0x6F, 0x5E);

        protected override string Title => "Neo Transient";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoTransientMachine;

            var shape = Layout.Column(
                new ParamKnob(ctx, P.Attack, diameter: 58),
                new ParamKnob(ctx, P.Sustain, diameter: 58));

            var graph = new GraphView(220, 196, (g, r) => DrawTrace(g, r, ctx, machine), animate: true);

            var detector = Layout.Column(
                new ParamDropdown(ctx, P.Speed, "Speed", 96),
                Layout.Gap(1, 4),
                new ParamKnob(ctx, P.Lookahead),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.Stereo, "Stereo", 40));

            var impact = Layout.Column(
                new ParamDropdown(ctx, P.Mode, "Mode", 96),
                Layout.Row(new ParamKnob(ctx, P.Impact1, "Low", 40), new ParamKnob(ctx, P.Impact2, "Low mid", 40), new ParamKnob(ctx, P.Impact3, "High mid", 40), new ParamKnob(ctx, P.Impact4, "High", 40)),
                Layout.Row(new ParamKnob(ctx, P.Xo1, "Xover 1", 40), new ParamKnob(ctx, P.Xo2, "Xover 2", 40), new ParamKnob(ctx, P.Xo3, "Xover 3", 40)),
                new ParamChoice(ctx, P.Auto, "Auto gain", 40));

            var match = new ActionButton("Match", AccentColor, 104) { ToolTip = "Output takes back the loudness the shaper added (the gain-match reading, 3 s): one undo step" };
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
                Layout.Row(new ParamKnob(ctx, P.Mix, diameter: 40), new ParamKnob(ctx, P.Output, diameter: 40)),
                new ParamChoice(ctx, P.Delta, "Delta", 40),
                Layout.Gap(1, 4),
                match);

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 196),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 196),
                Layout.Gap(4),
                new Meter("Gain", () => (float)DspMath.DbToGain(-Math.Abs(machine?.Engine.GainDb ?? 0)), AccentColor, 196, reduction: true));

            return Layout.Row(
                new Section("Shape", AccentColor, shape),
                new Section("Gain", AccentColor, graph),
                new Section("Detector", AccentColor, detector),
                new Section("Impact", AccentColor, impact),
                new Section("Output", AccentColor, output),
                new Section("Meters", AccentColor, meters));
        }

        /// <summary>The gain the shaper applied, dB (−12 … +12) up, the last three seconds across (the largest in each 20 ms).</summary>
        static void DrawTrace(INeoCanvas g, NeoRect r, EditorContext ctx, NeoTransientMachine machine)
        {
            const double lo = -12, hi = 12;
            var plot = new NeoRect(r.X + 30, r.Y + 8, r.Width - 38, r.Height - 30);
            double Y(double db) => plot.Bottom - (Math.Clamp(db, lo, hi) - lo) / (hi - lo) * plot.Height;
            var grid = new NeoStroke(Palette.Grid, 1, false);
            for (double d = lo; d <= hi; d += 6)
            {
                g.Line(plot.Left, Y(d), plot.Right, Y(d), grid);
                g.Text(d.ToString("+0;−0;0"), plot.Left - 4, Y(d) - 7, 9.5, Palette.DimText, align: NeoAlign.Right);
            }
            g.Line(plot.Left, Y(0), plot.Right, Y(0), new NeoStroke(Palette.Text.WithAlpha(0x80), 1, false));
            if (machine == null) return;
            var h = machine.Engine.History;
            int pos = machine.Engine.HistoryPos;
            var path = new NeoPath();
            for (int k = 0; k < h.Length; k++)
            {
                double x = plot.Left + k / (double)(h.Length - 1) * plot.Width;
                double y = Y(h[(pos + k) % h.Length]);
                if (k == 0) path.MoveTo(x, y);
                else path.LineTo(x, y);
            }
            g.Path(path, NeoFill.None, new NeoStroke(AccentColor, 2));
            bool impact = (int)ctx.Value(P.Mode) == 1;
            g.Text(impact ? "Impact: gain per band" : "gain on the audio", plot.Left + 4, plot.Top + 2, 10, Palette.DimText);
            g.Text($"latency {machine.Engine.Latency} samples", plot.Left + 4, plot.Top + 16, 9.5, Palette.DimText);
        }
    }
}
