#nullable disable
// Neo Multiband's editor (docs/specs/machines.md §4): the bands over the input spectrum with a
// draggable handle per crossover (MultibandDesign, the engine's own curves: each band's shape moved
// by its makeup and its gain change now, and the machine's response through them), a gain-change
// meter per band (cuts down, upward expansion up), the selected band's controls, the crossovers and
// phase, the detector, and the output with meters. WPF lays the editor out; the graph draws on the
// kit's canvas (INeoCanvas) and its input is toolkit-free (Core/CrossoverHandles.cs), so the studio's
// Avalonia flavour draws and behaves the same.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using BP = NeoBuzz.Multiband.MultibandEngine.BandParam;

namespace NeoBuzz.Multiband
{
    internal sealed class NeoMultibandEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Dynamics identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Dynamics, NeoColor.FromRgb(0xF2, 0xB1, 0x3C));

        internal static readonly NeoColor[] BandColors = Tokens.Bands(new[]
        {
            NeoColor.FromRgb(0xFF, 0x6B, 0x6B), NeoColor.FromRgb(0x8F, 0xD1, 0x4F), NeoColor.FromRgb(0x4F, 0xB3, 0xFF), NeoColor.FromRgb(0xC3, 0x8C, 0xFF),
        });

        const double MeterRangeDb = 24, BoostRangeDb = 12;

        protected override string Title => "Neo Multiband";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoMultibandMachine;
            var graph = new ResponseGraph(-30, 12, 6);
            var handles = new CrossoverHandleInput(graph, new Crossovers(ctx));
            handles.Select(1);
            var spectrum = new SpectrumView();
            var view = new InteractiveGraph(600, 262, (g, r) => Draw(g, r, ctx, graph, handles, machine, spectrum), handles, animate: true)
            {
                ToolTip = "Drag a crossover (Shift: fine). Click a band to show its controls; double-click: solo; wheel: its threshold (Shift: 0.1 dB).",
            };
            ctx.Changed += view.InvalidateVisual;

            // The gain change of every band, cuts down from 0 dB and upward expansion up.
            var meters = Layout.Row(
                BandMeter(ctx, machine, 0), Layout.Gap(3), BandMeter(ctx, machine, 1), Layout.Gap(3),
                BandMeter(ctx, machine, 2), Layout.Gap(3), BandMeter(ctx, machine, 3));

            var output = Layout.Column(
                new ParamKnob(ctx, P.Mix, diameter: 52),
                new ParamKnob(ctx, P.Output, diameter: 52),
                Layout.Gap(1, 6),
                Layout.Row(
                    new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 110),
                    Layout.Gap(4),
                    new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 110)));

            // One row of controls per band; only the selected band's is shown.
            var rows = new Grid();
            var rowOf = new UIElement[MultibandDesign.MaxBands];
            for (int b = 0; b < MultibandDesign.MaxBands; b++)
            {
                var row = Layout.Row(
                    new ParamKnob(ctx, MultibandEngine.Param(b, BP.Threshold), "Threshold", 50),
                    new ParamKnob(ctx, MultibandEngine.Param(b, BP.Ratio), "Ratio", 50),
                    new ParamKnob(ctx, MultibandEngine.Param(b, BP.Attack), "Attack"),
                    new ParamKnob(ctx, MultibandEngine.Param(b, BP.Release), "Release"),
                    new ParamKnob(ctx, MultibandEngine.Param(b, BP.Knee), "Knee"),
                    new ParamKnob(ctx, MultibandEngine.Param(b, BP.Range), "Range"),
                    new ParamKnob(ctx, MultibandEngine.Param(b, BP.Makeup), "Makeup", 50),
                    Layout.Gap(6),
                    Layout.Column(
                        new ParamChoice(ctx, MultibandEngine.Param(b, BP.Solo), "Solo", 34),
                        new ParamChoice(ctx, MultibandEngine.Param(b, BP.Bypass), "Bypass", 34)));
                row.Visibility = b == handles.Selected ? Visibility.Visible : Visibility.Collapsed;
                rowOf[b] = row;
                rows.Children.Add(row);
            }
            var tabs = new BandTabs(ctx, handles);
            handles.SelectedChanged += sel =>
            {
                for (int b = 0; b < rowOf.Length; b++) rowOf[b].Visibility = b == sel ? Visibility.Visible : Visibility.Collapsed;
                tabs.InvalidateVisual();
                view.InvalidateVisual();
            };
            // Three bands: band 4 can't stay selected.
            ctx.Changed += () => { if (handles.Selected >= Bands(ctx)) handles.Select(Bands(ctx) - 1); };

            var crossovers = Layout.Column(
                new ParamChoice(ctx, P.Bands, "Bands", 54),
                Layout.Gap(1, 2),
                new ParamChoice(ctx, P.Phase, "Phase", 64),
                Layout.Row(new ParamKnob(ctx, P.Xo1, "XO 1"), new ParamKnob(ctx, P.Xo2, "XO 2"), new ParamKnob(ctx, P.Xo3, "XO 3")));

            var detector = Layout.Column(
                new ParamChoice(ctx, P.Stereo, "Stereo", 44),
                new ParamKnob(ctx, P.Link, diameter: 46));

            return Layout.Column(
                Layout.Row(
                    new Section("Bands", AccentColor, view),
                    new Section("Gain change", AccentColor, meters),
                    new Section("Output", AccentColor, output)),
                Layout.Row(
                    new Section("Band", AccentColor, Layout.Row(tabs, Layout.Gap(10), rows)),
                    new Section("Crossovers", AccentColor, crossovers),
                    new Section("Detector", AccentColor, detector)));
        }

        static int Bands(EditorContext ctx) => ctx.Raw(P.Bands) == 0 ? 3 : 4;

        /// <summary>A band's gain-change meter: 0 dB a third of the way down, cuts growing down to −24 dB, boosts up to +12 dB; its value above.</summary>
        static GraphView BandMeter(EditorContext ctx, NeoMultibandMachine machine, int b)
        {
            var colour = BandColors[b];
            var view = new GraphView(30, 262, (g, r) =>
            {
                bool on = b < Bands(ctx);
                var inner = new NeoRect(r.X + 8, r.Y + 20, r.Width - 16, r.Height - 40);
                double zero = inner.Top + inner.Height * BoostRangeDb / (BoostRangeDb + MeterRangeDb);
                double perDb = inner.Height / (BoostRangeDb + MeterRangeDb);
                g.Rect(inner, Palette.Panel, NeoStroke.None);
                foreach (double d in new[] { 6.0, -6, -12, -18 })
                    g.Line(inner.Left, zero - d * perDb, inner.Right, zero - d * perDb, new NeoStroke(Palette.Grid, 1, false));
                g.Line(inner.Left - 3, zero, inner.Right + 3, zero, new NeoStroke(Palette.Track, 1));
                float cut = on ? machine?.Engine.ReductionDb(b) ?? 0 : 0, boost = on ? machine?.Engine.BoostDb(b) ?? 0 : 0;
                if (cut > 0.01) g.Rect(new NeoRect(inner.X, zero, inner.Width, Math.Min(MeterRangeDb, cut) * perDb), colour, NeoStroke.None);
                if (boost > 0.01) g.Rect(new NeoRect(inner.X, zero - Math.Min(BoostRangeDb, boost) * perDb, inner.Width, Math.Min(BoostRangeDb, boost) * perDb), colour.Tint(0.35), NeoStroke.None);
                double change = cut >= boost ? -cut : boost;
                string text = !on ? "–" : Math.Abs(change) < 0.05 ? "0.0" : (change > 0 ? "+" : "−") + Math.Abs(change).ToString(Math.Abs(change) >= 10 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture);
                g.Text(text, r.X + r.Width / 2, r.Y + 3, 9.5, on ? Palette.Text : Palette.DimText, align: NeoAlign.Center);
                g.Text((b + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), r.X + r.Width / 2, r.Bottom - 16, 10.5, on ? colour : Palette.DimText, true, NeoAlign.Center);
            }, animate: true)
            {
                ToolTip = $"Band {b + 1}'s gain change now: a cut grows down from 0 dB (to −24), upward expansion grows up (to +12)",
            };
            return view;
        }

        /// <summary>The crossovers as handles: their parameters, read and set through the editor context.</summary>
        sealed class Crossovers : ICrossovers
        {
            readonly EditorContext ctx;

            public Crossovers(EditorContext ctx) { this.ctx = ctx; }

            public int Bands => NeoMultibandEditor.Bands(ctx);

            static int Index(int i) => i == 0 ? P.Xo1 : i == 1 ? P.Xo2 : P.Xo3;

            public double Hz(int i) => ctx.Value(Index(i));

            public void Move(int i, double hz)
            {
                int p = Index(i), raw = Params.Table[p].Raw(hz);
                if (raw == ctx.Raw(p)) return;
                ctx.Set(p, raw);
                ctx.RaiseChanged();
            }

            public void ToggleSolo(int band)
            {
                int p = MultibandEngine.Param(band, BP.Solo);
                ctx.Set(p, ctx.Raw(p) == 0 ? 1 : 0);
                ctx.RaiseChanged();
            }

            public void StepThreshold(int band, int tenths)
            {
                int p = MultibandEngine.Param(band, BP.Threshold);
                ctx.Set(p, Math.Clamp(ctx.Raw(p) + tenths, Params.Table[p].Min, Params.Table[p].Max));
                ctx.RaiseChanged();
            }
        }

        /// <summary>Band selector: a button per band, with a dot in the band's colour (filled while it sounds: not bypassed, not muted by a solo).</summary>
        sealed class BandTabs : FrameworkElement
        {
            static readonly string[] Names = { "1", "2", "3", "4" };
            readonly EditorContext ctx;
            readonly CrossoverHandleInput handles;
            readonly WpfCanvas canvas;
            readonly ChoiceStrip strip;
            int hover = -1;

            public BandTabs(EditorContext ctx, CrossoverHandleInput handles)
            {
                this.ctx = ctx;
                this.handles = handles;
                canvas = new WpfCanvas(this);
                strip = new ChoiceStrip("Band", Names, 30, false, canvas);
                Width = strip.Width;
                Height = strip.Height + 12;
                Cursor = Cursors.Hand;
                ToolTip = "The band shown beside (also: click it in the graph)";
                ctx.Changed += InvalidateVisual;
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                var p = e.GetPosition(this);
                int i = strip.CellAt(p.X, p.Y);
                if (i >= 0 && i < Bands(ctx)) handles.Select(i);
                e.Handled = true;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                var p = e.GetPosition(this);
                int i = strip.CellAt(p.X, p.Y);
                if (i == hover) return;
                hover = i;
                InvalidateVisual();
            }

            protected override void OnMouseLeave(MouseEventArgs e)
            {
                hover = -1;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                var c = canvas.On(dc);
                strip.Draw(c, handles.Selected, hover, AccentColor);
                bool anySolo = false;
                for (int b = 0; b < Bands(ctx); b++) anySolo |= ctx.Raw(MultibandEngine.Param(b, BP.Solo)) > 0;
                for (int i = 0; i < Names.Length; i++)
                {
                    var cell = strip.Cell(i);
                    var colour = i < Bands(ctx) ? BandColors[i] : Palette.DimText;
                    var centre = new NeoPoint(cell.X + cell.Width / 2, cell.Bottom + 7);
                    bool sounds = i < Bands(ctx) && ctx.Raw(MultibandEngine.Param(i, BP.Bypass)) == 0 && (!anySolo || ctx.Raw(MultibandEngine.Param(i, BP.Solo)) > 0);
                    c.Ellipse(centre, 3.5, 3.5, sounds ? (NeoFill)colour : NeoFill.None, new NeoStroke(colour, 1.2));
                }
            }
        }

        /// <summary>The input spectrum: 4096-point Hann FFT, smoothed to 1/6 octave, tilted +3 dB/oct (pink noise reads flat), falling slowly.</summary>
        sealed class SpectrumView
        {
            const int N = MultibandEngine.ScopeLength, Points = 180;
            readonly float[] samples = new float[N];
            readonly double[] re = new double[N], im = new double[N], window = new double[N];
            public readonly double[] Hz = new double[Points], Db = new double[Points];

            public SpectrumView()
            {
                for (int i = 0; i < N; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N);
                for (int p = 0; p < Points; p++)
                {
                    Hz[p] = 20 * Math.Pow(1000, p / (double)(Points - 1));
                    Db[p] = -200;
                }
            }

            public void Update(MultibandEngine engine)
            {
                engine.Scope(samples);
                for (int i = 0; i < N; i++)
                {
                    re[i] = samples[i] * window[i];
                    im[i] = 0;
                }
                Fft.Transform(re, im, false);
                double rate = engine.SampleRate, binHz = rate / N;
                for (int p = 0; p < Points; p++)
                {
                    double lo = Hz[p] * Math.Pow(2, -1.0 / 12), hi = Hz[p] * Math.Pow(2, 1.0 / 12);
                    int k0 = Math.Max(1, (int)Math.Floor(lo / binHz)), k1 = Math.Min(N / 2 - 1, Math.Max(k0, (int)Math.Ceiling(hi / binHz)));
                    double power = 0;
                    for (int k = k0; k <= k1; k++) power += re[k] * re[k] + im[k] * im[k];
                    power /= k1 - k0 + 1;
                    double db = 10 * Math.Log10(power * 16.0 / ((double)N * N) + 1e-30) + 3 * Math.Log2(Hz[p] / 1000);
                    Db[p] = Math.Max(db, Db[p] - 1.5);
                }
            }
        }

        /// <summary>The input spectrum; each band's shape at its makeup and gain change now, its span shaded; the response through them; the crossovers.</summary>
        static void Draw(INeoCanvas g, NeoRect r, EditorContext ctx, ResponseGraph graph, CrossoverHandleInput handles, NeoMultibandMachine machine, SpectrumView spectrum)
        {
            graph.Layout(r);
            if (machine != null)
            {
                spectrum.Update(machine.Engine);
                graph.InPlot(g, () => g.Path(graph.Spectrum(spectrum.Hz, spectrum.Db, 0, 96), Palette.Track.WithAlpha(0x90), NeoStroke.None));
            }
            graph.DrawGrid(g);

            var values = new ParamValues(Params.Table);
            for (int i = 0; i < Params.Table.Length; i++) values.Set(i, ctx.Raw(i));
            double rate = machine?.Engine.SampleRate ?? 48000;
            var xo = MultibandDesign.Effective(values, rate);
            int bands = Bands(ctx);
            bool anySolo = false;
            for (int b = 0; b < bands; b++) anySolo |= values.On(MultibandEngine.Param(b, BP.Solo));

            // Each band's gain now (dB): its makeup less its gain change; a bypassed band 0 dB, a band a solo leaves out silent.
            var gainDb = new double[MultibandDesign.MaxBands];
            var linear = new double[MultibandDesign.MaxBands];
            for (int b = 0; b < MultibandDesign.MaxBands; b++)
            {
                double change = machine == null || b >= bands ? 0 : machine.Engine.GainChangeDb(b);
                gainDb[b] = values.On(MultibandEngine.Param(b, BP.Bypass)) ? 0 : values.Value(MultibandEngine.Param(b, BP.Makeup)) - change;
                linear[b] = anySolo && !values.On(MultibandEngine.Param(b, BP.Solo)) ? 0 : Math.Pow(10, gainDb[b] / 20);
            }

            int sel = handles.Selected;
            // The bands' shapes and the response inside the plot: steep slopes end at its floor, never over the frequency labels.
            graph.InPlot(g, () =>
            {
                foreach (var (b, left, right) in handles.Spans())
                    if (b == sel) g.Rect(new NeoRect(left, graph.Plot.Top, Math.Max(0, right - left), graph.Plot.Height), BandColors[b].WithAlpha(0x14), NeoStroke.None);
                for (int b = 0; b < bands; b++)
                {
                    int band = b;
                    var colour = BandColors[b];
                    Func<double, double> shape = hz => 20 * Math.Log10(Math.Max(1e-9, MultibandDesign.Band(band, bands, hz, xo, rate))) + gainDb[band];
                    bool heard = linear[b] > 0;
                    g.Path(graph.Curve(shape, 160), NeoFill.None, new NeoStroke(colour.WithAlpha(heard ? (byte)(b == sel ? 0xE0 : 0x90) : (byte)0x40), b == sel ? 1.6 : 1.1));
                }
                g.Path(graph.Curve(hz => 20 * Math.Log10(Math.Max(1e-9, MultibandDesign.Response(bands, hz, xo, rate, linear[0], linear[1], linear[2], linear[3])))),
                    NeoFill.None, new NeoStroke(AccentColor, 2.2));
            });
            handles.Draw(g, AccentColor);

            // Each band's number and gain change at the bottom of its span.
            foreach (var (b, left, right) in handles.Spans())
            {
                if (right - left < 18) continue;
                double change = machine?.Engine.GainChangeDb(b) ?? 0;
                string text = (b + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (right - left > 60 && Math.Abs(change) >= 0.05) text += (change > 0 ? "  −" : "  +") + Math.Abs(change).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB";
                g.Text(text, (left + right) / 2, graph.Plot.Bottom - 15, 10, BandColors[b], true, NeoAlign.Center);
            }
            string mode = values.Choice(P.Phase) == 0 ? $"Linear phase · {MultibandDesign.LinearLatency(rate) * 1000.0 / rate:0} ms latency" : "Minimum phase · no latency";
            if (values.Choice(P.Stereo) == 1) mode += " · mid/side";
            if (anySolo) mode += " · solo";
            g.Text(mode, graph.Plot.Right - 4, graph.Plot.Top + 16, 9.5, Palette.DimText, align: NeoAlign.Right);
        }
    }
}
