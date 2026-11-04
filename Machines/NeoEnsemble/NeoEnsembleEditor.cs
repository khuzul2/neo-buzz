#nullable disable
// Neo Ensemble's editor (docs/specs/machines.md §4): the mode, the LFO, a picture of the sweep (the
// response now, as the LFO moves it, inside the range it sweeps over, and where each voice's LFO
// is), the sound and the output. WPF lays the editor out; the picture draws on the kit's canvas
// (INeoCanvas), so the studio's Avalonia flavour draws the same.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Ensemble
{
    internal sealed class NeoEnsembleEditor : NeoEditor
    {
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xFF, 0x7E, 0xDB);
        static readonly NeoColor RightColor = NeoColor.FromRgb(0x7E, 0xC8, 0xFF);

        const int Points = 96;
        const double MinHz = 20, MaxHz = 20000, TopDb = 24, BottomDb = -36;

        protected override string Title => "Neo Ensemble";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        // The picture's buffers, kept between frames (the sweep's range only changes with the parameters).
        readonly double[] hz = new double[Points], left = new double[Points], right = new double[Points];
        readonly double[] low = new double[Points], high = new double[Points], scratch = new double[Points], taus = new double[4];
        readonly NeoPath band = new NeoPath(), curveL = new NeoPath(), curveR = new NeoPath();
        int rangeKey = -1;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoEnsembleMachine;
            for (int i = 0; i < Points; i++) hz[i] = MinHz * Math.Pow(MaxHz / MinHz, i / (double)(Points - 1));

            var mode = Layout.Column(
                new ParamChoice(ctx, P.Mode, vertical: true, cellWidth: 74),
                Layout.Gap(1, 6),
                Layout.Row(new ParamKnob(ctx, P.Voices), new ParamKnob(ctx, P.Stages)));

            var lfo = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Rate, diameter: 56), new ParamKnob(ctx, P.Depth, diameter: 56)),
                new ParamDropdown(ctx, P.Sync, width: 112),
                Layout.Gap(1, 4),
                Layout.Row(new ParamChoice(ctx, P.Shape, cellWidth: 52)),
                new ParamKnob(ctx, P.Spread));

            var sweep = new GraphView(300, 196, (g, r) => DrawSweep(g, r, ctx, machine), animate: true)
            {
                ToolTip = "The response now (left channel pink, right blue) inside the range the LFO sweeps it over; below, where each voice's LFO is",
            };
            ctx.Changed += sweep.InvalidateVisual;

            var sound = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Delay), new ParamKnob(ctx, P.Centre)),
                Layout.Row(new ParamKnob(ctx, P.Feedback), new ParamKnob(ctx, P.Tone)));

            var output = Layout.Row(
                Layout.Column(new ParamKnob(ctx, P.Mix, diameter: 56), new ParamKnob(ctx, P.Output)),
                Layout.Gap(6),
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 196),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 196));

            return Layout.Row(
                new Section("Mode", AccentColor, mode),
                new Section("LFO", AccentColor, lfo),
                new Section("Sweep", AccentColor, sweep),
                new Section("Sound", AccentColor, sound),
                new Section("Output", AccentColor, output));
        }

        void DrawSweep(INeoCanvas g, NeoRect r, EditorContext ctx, NeoEnsembleMachine machine)
        {
            var values = new ParamValues(Params.Table);
            int key = 17;
            for (int i = 0; i < Params.Table.Length; i++)
            {
                int raw = ctx.Raw(i);
                values.Set(i, raw);
                key = unchecked(key * 31 + raw);
            }
            double rate = 48000, tempo = machine?.Engine.Tempo ?? 120;
            var s = EnsembleDesign.From(values, tempo, rate);
            var plot = new NeoRect(r.X + 4, r.Y + 18, r.Width - 8, r.Height - 50);
            double X(double f) => plot.X + Math.Log(f / MinHz) / Math.Log(MaxHz / MinHz) * plot.Width;
            double Y(double db) => plot.Y + (TopDb - Math.Clamp(db, BottomDb, TopDb)) / (TopDb - BottomDb) * plot.Height;

            var grid = new NeoStroke(Palette.Grid, 1, false);
            foreach (double f in new[] { 100.0, 1000, 10000 }) g.Line(X(f), plot.Top, X(f), plot.Bottom, grid);
            for (double d = TopDb - 12; d >= BottomDb; d -= 12) g.Line(plot.Left, Y(d), plot.Right, Y(d), grid);
            g.Line(plot.Left, Y(0), plot.Right, Y(0), new NeoStroke(Palette.Track, 1));
            g.Text("100", X(100) + 2, plot.Bottom - 12, 9, Palette.DimText);
            g.Text("1k", X(1000) + 2, plot.Bottom - 12, 9, Palette.DimText);
            g.Text("10k", X(10000) + 2, plot.Bottom - 12, 9, Palette.DimText);

            // The range the sweep covers: the lowest and highest response over the LFO's cycle (both channels).
            if (key != rangeKey)
            {
                rangeKey = key;
                for (int i = 0; i < Points; i++) { low[i] = double.MaxValue; high[i] = double.MinValue; }
                for (int step = 0; step < 16; step++)
                {
                    EnsembleDesign.Curve(s, rate, step / 16.0, 0, hz, scratch, taus);
                    for (int i = 0; i < Points; i++) { low[i] = Math.Min(low[i], scratch[i]); high[i] = Math.Max(high[i], scratch[i]); }
                }
                band.Clear();
                for (int i = 0; i < Points; i++) band.LineTo(X(hz[i]), Y(high[i]));
                for (int i = Points - 1; i >= 0; i--) band.LineTo(X(hz[i]), Y(low[i]));
                band.Close();
            }
            g.Path(band, AccentColor.WithAlpha(0x2A), NeoStroke.None);

            // The response now, each channel at its own LFO phase.
            double phase = machine?.Engine.Phase ?? 0;
            EnsembleDesign.Curve(s, rate, phase, 1, hz, right, taus);
            EnsembleDesign.Curve(s, rate, phase, 0, hz, left, taus);
            Polyline(curveR, right, X, Y);
            Polyline(curveL, left, X, Y);
            g.Path(curveR, NeoFill.None, new NeoStroke(RightColor, 1.5));
            g.Path(curveL, NeoFill.None, new NeoStroke(AccentColor, 2));

            // Where each voice's LFO is: a track from −1 to +1, the left channel's voices above, the right's below.
            int voices = s.Mode == EnsembleMode.Chorus ? s.Voices : 1;
            double ty = r.Bottom - 20, x0 = r.X + 40, x1 = r.Right - 10;
            g.Text("LFO", r.X + 6, ty - 6, 10, Palette.DimText);
            g.Line(x0, ty, x1, ty, new NeoStroke(Palette.Track, 3));
            g.Line((x0 + x1) / 2, ty - 5, (x0 + x1) / 2, ty + 5, grid);
            for (int c = 1; c >= 0; c--)
                for (int v = 0; v < voices; v++)
                {
                    double value = EnsembleDesign.Wave(EnsembleDesign.VoicePhase(phase, c, v, voices, s.SpreadCycles), s.Triangle);
                    double x = x0 + (value + 1) / 2 * (x1 - x0);
                    g.Ellipse(new NeoPoint(x, ty + (c == 0 ? -4 : 4)), 3.5, 3.5, c == 0 ? AccentColor : RightColor, NeoStroke.None);
                }

            g.Text(Summary(s), r.X + 6, r.Y + 2, 10, Palette.DimText, maxWidth: r.Width - 12);
        }

        static void Polyline(NeoPath path, double[] db, Func<double, double> x, Func<double, double> y)
        {
            path.Clear();
            for (int i = 0; i < db.Length; i++)
            {
                double f = MinHz * Math.Pow(MaxHz / MinHz, i / (double)(db.Length - 1));
                if (i == 0) path.MoveTo(x(f), y(db[i]));
                else path.LineTo(x(f), y(db[i]));
            }
        }

        /// <summary>"Flanger · 0.40 – 6.40 ms · 0.25 Hz": what sweeps, over what range, how fast.</summary>
        static string Summary(in EnsembleSetting s)
        {
            string rate = s.Rate >= 10 ? $"{s.Rate:F1} Hz" : $"{s.Rate:F2} Hz";
            if (s.Mode == EnsembleMode.Phaser)
                return $"Phaser · {s.Stages} stages · {ParamDef.Freq(Math.Max(20, Math.Pow(2, s.Log2Centre - s.DepthOctaves)))} – {ParamDef.Freq(Math.Min(21600, Math.Pow(2, s.Log2Centre + s.DepthOctaves)))} · {rate}";
            double lo = Math.Pow(2, s.Log2Delay - s.DepthOctaves), hi = Math.Pow(2, s.Log2Delay + s.DepthOctaves);
            string what = s.Mode == EnsembleMode.Chorus ? $"Chorus · {s.Voices} voice{(s.Voices > 1 ? "s" : "")}" : "Flanger";
            return $"{what} · {ParamDef.Time(lo)} – {ParamDef.Time(hi)} · {rate}";
        }
    }
}
