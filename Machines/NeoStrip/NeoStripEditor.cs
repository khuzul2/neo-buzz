#nullable disable
// Neo Strip's editor (docs/specs/mastering-tools.md §5.10): one window for the five stages, in the order the audio takes them: gate, EQ (with its
// response drawn), compressor, drive, then the clipper and limiter at one ceiling, the output with Delta and Match, and meters.
// One source for WPF and Avalonia (the Neo editor kit); the response draws on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Eq;

namespace NeoBuzz.Strip
{
    internal sealed class NeoStripEditor : NeoEditor
    {
        // Warm grey-green: neutral across five stages, clear of the meters' red.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0x9C, 0xC9, 0x6B);

        protected override string Title => "Neo Strip";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoStripMachine;
            const double k = 38;

            var gate = Layout.Row(
                Layout.Column(new ParamChoice(ctx, P.GateOn, "Gate", 40)),
                new ParamKnob(ctx, P.GateThresh, "Threshold", k),
                new ParamKnob(ctx, P.GateRange, "Range", k),
                new ParamKnob(ctx, P.GateAttack, "Attack", k),
                new ParamKnob(ctx, P.GateHold, "Hold", k),
                new ParamKnob(ctx, P.GateRelease, "Release", k));

            var graph = new GraphView(250, 112, (g, r) => DrawEq(g, r, ctx), animate: false);
            var eq = Layout.Column(
                Layout.Row(
                    Layout.Column(new ParamChoice(ctx, P.EqOn, "EQ", 40), Layout.Gap(1, 6), new ParamChoice(ctx, P.LoCutOn, "Low cut", 40)),
                    new ParamKnob(ctx, P.LoCutFreq, "Cut freq", k),
                    new ParamKnob(ctx, P.LoShelfFreq, "Shelf freq", k),
                    new ParamKnob(ctx, P.LoShelfGain, "Shelf gain", k),
                    new ParamKnob(ctx, P.LmFreq, "LM freq", k),
                    new ParamKnob(ctx, P.LmGain, "LM gain", k),
                    new ParamKnob(ctx, P.LmQ, "LM Q", k),
                    new ParamKnob(ctx, P.HmFreq, "HM freq", k),
                    new ParamKnob(ctx, P.HmGain, "HM gain", k),
                    new ParamKnob(ctx, P.HmQ, "HM Q", k),
                    new ParamKnob(ctx, P.HiShelfFreq, "Shelf freq", k),
                    new ParamKnob(ctx, P.HiShelfGain, "Shelf gain", k),
                    new ParamKnob(ctx, P.HiCutFreq, "Cut freq", k),
                    Layout.Column(new ParamChoice(ctx, P.HiCutOn, "High cut", 40))),
                Layout.Gap(1, 4),
                graph);

            var comp = Layout.Row(
                Layout.Column(new ParamChoice(ctx, P.CompOn, "Comp", 40), Layout.Gap(1, 4), new ParamDropdown(ctx, P.CompMode, "Mode", 72)),
                new ParamKnob(ctx, P.CompThresh, "Threshold", k),
                new ParamKnob(ctx, P.CompRatio, "Ratio", k),
                new ParamKnob(ctx, P.CompAttack, "Attack", k),
                new ParamKnob(ctx, P.CompRelease, "Release", k),
                new ParamKnob(ctx, P.CompMakeup, "Makeup", k));

            var drive = Layout.Row(
                Layout.Column(new ParamChoice(ctx, P.DriveOn, "Drive", 40), Layout.Gap(1, 4), new ParamDropdown(ctx, P.DriveMode, "Mode", 90)),
                new ParamKnob(ctx, P.DriveAmount, "Amount", k),
                new ParamKnob(ctx, P.DriveMix, "Mix", k));

            var ceiling = Layout.Row(
                Layout.Column(new ParamChoice(ctx, P.ClipOn, "Clip", 40), Layout.Gap(1, 4), new ParamChoice(ctx, P.LimitOn, "Limit", 40)),
                new ParamKnob(ctx, P.Ceiling, "Ceiling", k),
                new ParamKnob(ctx, P.LimRelease, "Release", k));

            var match = new ActionButton("Match", AccentColor, 96) { ToolTip = "Output takes back the loudness the strip added (the gain-match reading, 3 s): one undo step" };
            match.Click += () =>
            {
                double db = machine?.Engine.GainMatchDb ?? double.NaN;
                if (!double.IsFinite(db)) return;
                double to = NeoBuzz.Analysis.GainMatch.Match(ctx.Value(P.Output), db, -24, 24);
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

            var output = Layout.Row(
                Layout.Column(new ParamKnob(ctx, P.Output, diameter: k), Layout.Gap(1, 4), new ParamChoice(ctx, P.Delta, "Delta", 40), Layout.Gap(1, 4), match),
                Layout.Gap(8),
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 150),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 150));

            return Layout.Column(
                Layout.Row(new Section("Gate", AccentColor, gate), new Section("Compressor", AccentColor, comp)),
                new Section("EQ", AccentColor, eq),
                Layout.Row(new Section("Drive", AccentColor, drive), new Section("Clipper and limiter", AccentColor, ceiling), new Section("Output", AccentColor, output)));
        }

        /// <summary>The EQ stage's response, 20 Hz … 20 kHz, ±18 dB, from the six bands' settings.</summary>
        static void DrawEq(INeoCanvas g, NeoRect r, EditorContext ctx)
        {
            const double lo = -18, hi = 18;
            var plot = new NeoRect(r.X + 30, r.Y + 6, r.Width - 38, r.Height - 12);
            double Y(double db) => plot.Bottom - (Math.Clamp(db, lo, hi) - lo) / (hi - lo) * plot.Height;
            var grid = new NeoStroke(Palette.Grid, 1, false);
            for (double d = -12; d <= 12; d += 12)
            {
                g.Line(plot.Left, Y(d), plot.Right, Y(d), grid);
                g.Text(d.ToString("+0;−0;0"), plot.Left - 4, Y(d) - 7, 9.5, Palette.DimText, align: NeoAlign.Right);
            }
            var bands = new BandSettings[6];
            bool[] on = new bool[6];
            bands[0] = new BandSettings { Type = BandType.LowCut, Hz = ctx.Value(P.LoCutFreq), Q = 0.7071, Slope = 0 };
            bands[1] = new BandSettings { Type = BandType.LowShelf, Hz = ctx.Value(P.LoShelfFreq), GainDb = ctx.Value(P.LoShelfGain), Q = 0.7071 };
            bands[2] = new BandSettings { Type = BandType.Bell, Hz = ctx.Value(P.LmFreq), GainDb = ctx.Value(P.LmGain), Q = ctx.Value(P.LmQ) };
            bands[3] = new BandSettings { Type = BandType.Bell, Hz = ctx.Value(P.HmFreq), GainDb = ctx.Value(P.HmGain), Q = ctx.Value(P.HmQ) };
            bands[4] = new BandSettings { Type = BandType.HighShelf, Hz = ctx.Value(P.HiShelfFreq), GainDb = ctx.Value(P.HiShelfGain), Q = 0.7071 };
            bands[5] = new BandSettings { Type = BandType.HighCut, Hz = ctx.Value(P.HiCutFreq), Q = 0.7071, Slope = 0 };
            on[0] = ctx.Value(P.LoCutOn) > 0;
            on[5] = ctx.Value(P.HiCutOn) > 0;
            for (int b = 1; b < 5; b++) on[b] = bands[b].GainDb != 0;
            bool active = ctx.Value(P.EqOn) > 0;
            var path = new NeoPath();
            const int Points = 140;
            for (int i = 0; i <= Points; i++)
            {
                double f = 20 * Math.Pow(1000, i / (double)Points), db = 0;
                for (int b = 0; b < 6; b++)
                    if (on[b]) db += 20 * Math.Log10(Math.Max(1e-9, EqDesign.Magnitude(bands[b], f, 48000)));
                double x = plot.Left + i / (double)Points * plot.Width;
                if (i == 0) path.MoveTo(x, Y(db));
                else path.LineTo(x, Y(db));
            }
            g.Path(path, NeoFill.None, new NeoStroke(active ? AccentColor : Palette.DimText, 2));
        }
    }
}
