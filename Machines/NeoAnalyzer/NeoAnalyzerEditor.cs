#nullable disable
// Neo Analyzer's editor (docs/specs/machines.md §5): the spectrum (bars, curve, held peaks) with
// its controls, the loudness meters and readouts against the target, and the stereo image
// (goniometer, correlation, width). WPF lays it out; everything is drawn by the kit's analysis
// graphs (Core/AnalysisGraphs.cs) on INeoCanvas, from the engine's newest snapshot.

using System;
using System.Globalization;
using System.Windows;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Analyzer
{
    internal sealed class NeoAnalyzerEditor : NeoEditor
    {
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0x5C, 0xD6, 0xF2);
        static readonly double[] Tilts = { 0, 3, 4.5 };

        protected override string Title => "Neo Analyzer";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        /// <summary>The snapshot the graphs draw, copied from the engine at most once per frame.</summary>
        sealed class Live
        {
            readonly AnalyzerEngine engine;
            long last = long.MinValue;
            public readonly AnalyzerSnapshot Snapshot = new AnalyzerSnapshot();

            public Live(AnalyzerEngine engine) { this.engine = engine; }

            public AnalyzerSnapshot Get()
            {
                long now = Environment.TickCount64;
                if (engine != null && now - last >= 10)
                {
                    last = now;
                    engine.Read(Snapshot);
                }
                return Snapshot;
            }
        }

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoAnalyzerMachine;
            var live = new Live(machine?.Engine);
            var ci = CultureInfo.InvariantCulture;

            var spectrum = new GraphView(600, 240, (g, r) =>
            {
                var s = live.Get();
                double tilt = Tilts[Math.Clamp(ctx.Raw(P.Tilt), 0, Tilts.Length - 1)];
                SpectrumGraph.Draw(g, r, AnalyzerEngine.CurveHz, s.Curve, s.CurvePeak, AnalyzerEngine.ThirdLo, AnalyzerEngine.ThirdHi,
                    s.Thirds, s.ThirdsPeak, tilt, AccentColor);
                string label = $"{Params.Table[P.Smoothing].Describe(ctx.Raw(P.Smoothing))} · {Params.Table[P.Speed].Describe(ctx.Raw(P.Speed))}" +
                    (tilt > 0 ? $" · tilt +{tilt.ToString("0.#", ci)} dB/oct" : "");
                g.Text(label, r.Right - 8, r.Y + 6, 10, Palette.DimText, align: NeoAlign.Right);
            }, animate: true);
            var spectrumControls = Layout.Row(
                new ParamDropdown(ctx, P.Smoothing, width: 96),
                Layout.Gap(8),
                new ParamDropdown(ctx, P.Speed, width: 96),
                Layout.Gap(8),
                new ParamChoice(ctx, P.Tilt, cellWidth: 74),
                Layout.Gap(8),
                new ParamKnob(ctx, P.Hold, diameter: 36));

            var loudness = new GraphView(250, 240, (g, r) =>
            {
                var s = live.Get();
                LoudnessGraph.Draw(g, r, new LoudnessView
                {
                    Momentary = s.Momentary, ShortTerm = s.ShortTerm, Integrated = s.Integrated, Range = s.Range,
                    MomentaryMax = s.MomentaryMax, ShortTermMax = s.ShortTermMax, Seconds = s.Seconds,
                    TruePeakNow = s.TruePeakNow, TruePeakHold = s.TruePeakHold, TruePeakMax = s.TruePeakMax,
                    Target = ctx.Value(P.Target),
                }, AccentColor);
            }, animate: true);
            var reset = new ActionButton("Reset", AccentColor, 70) { ToolTip = "Start the integrated loudness, range, maxima and held peaks again" };
            reset.Click += () => machine?.Engine.RequestReset();
            var loudnessControls = Layout.Row(
                new ParamKnob(ctx, P.Target, diameter: 36),
                Layout.Gap(10),
                Layout.Column(Layout.Gap(1, 16), reset));

            var gonio = new GraphView(170, 170, (g, r) =>
            {
                var s = live.Get();
                Goniometer.Draw(g, r, s.GonioL, s.GonioR, s.GonioCount, AccentColor);
            }, animate: true);
            var correlation = new GraphView(170, 34, (g, r) =>
            {
                var s = live.Get();
                CorrelationBar.Draw(g, r, s.CorrelationNow, s.Correlation, AccentColor);
            }, animate: true);
            var stereoText = new GraphView(170, 40, (g, r) =>
            {
                var s = live.Get();
                string corr = s.Correlation.ToString("+0.00;−0.00;0.00", ci);
                string width = double.IsPositiveInfinity(s.Width) ? "∞" : s.Width.ToString("0.00", ci);
                g.Text("Correlation (3 s)", r.X + 6, r.Y + 4, 10.5, Palette.DimText);
                g.Text(corr, r.Right - 6, r.Y + 4, 11.5, Palette.Text, bold: true, align: NeoAlign.Right);
                g.Text("Width (side / mid)", r.X + 6, r.Y + 21, 10.5, Palette.DimText);
                g.Text(width, r.Right - 6, r.Y + 21, 11.5, Palette.Text, bold: true, align: NeoAlign.Right);
            }, animate: true);
            gonio.ToolTip = "Goniometer: mid up, side across (left channel up-left, right up-right), scaled to the loudest point";
            correlation.ToolTip = "Correlation over 400 ms (bar) and 3 s (marker): +1 mono, 0 unrelated, −1 opposed";

            return Layout.Row(
                new Section("Spectrum", AccentColor, Layout.Column(spectrum, Layout.Gap(1, 6), spectrumControls)),
                new Section("Loudness", AccentColor, Layout.Column(loudness, Layout.Gap(1, 6), loudnessControls)),
                new Section("Stereo", AccentColor, Layout.Column(gonio, Layout.Gap(1, 6), correlation, Layout.Gap(1, 4), stereoText)));
        }
    }
}
