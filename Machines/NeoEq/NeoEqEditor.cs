#nullable disable
// Neo EQ's editor (docs/specs/machines.md §4): the response the engine runs (EqDesign, the same
// function) over the output spectrum, with a draggable handle per band; below it the mode and the
// selected band's controls; the output gain and meters at the side. In graphic mode the graph shows
// the 31 sliders on their tracks and the response the solver gives them (GraphicSolver, as the
// engine runs it), and below it the selected slider's knob. WPF lays the editor out; the graph
// draws on the kit's canvas (INeoCanvas) and its input is toolkit-free (Core/ResponseGraph.cs,
// Core/GraphicSliders.cs).
// v2 (docs/specs/mastering-tools.md §5.3): each band's channel and shape beside its type, a second row with its
// dynamics (Off/Down/Up, the detector, threshold, ratio, knee, range, attack, release); in the graph a dynamic band's
// range shaded and the curve drawn where its gain is now, a band off Stereo labelled L, R, M or S at its handle;
// Delta and Match (Output set from the gain-match reading, one undo step) under Output.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Eq
{
    internal sealed class NeoEqEditor : NeoEditor
    {
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0x4F, 0xB3, 0xFF);

        internal static readonly NeoColor[] BandColors =
        {
            NeoColor.FromRgb(0xFF, 0x6B, 0x6B), NeoColor.FromRgb(0xFF, 0xA9, 0x4D), NeoColor.FromRgb(0xF5, 0xD5, 0x47),
            NeoColor.FromRgb(0x8F, 0xD1, 0x4F), NeoColor.FromRgb(0x3F, 0xD1, 0xB0), NeoColor.FromRgb(0x4F, 0xB3, 0xFF),
            NeoColor.FromRgb(0x9B, 0x8C, 0xFF), NeoColor.FromRgb(0xE0, 0x7A, 0xE6),
        };

        protected override string Title => "Neo EQ";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoEqMachine;
            var graph = new ResponseGraph(-24, 24, 6);
            var handles = new BandHandleInput(graph, new Bands(ctx));
            handles.Select(2);
            var sliders = new GraphicSliderInput(graph, new GraphicBands(ctx), 17);
            var curve = new GraphicCurve();
            bool Graphic() => ctx.Raw(P.Mode) == 1;
            var spectrum = new SpectrumView();
            var view = new InteractiveGraph(680, 298, (g, r) =>
            {
                if (Graphic()) DrawGraphic(g, r, ctx, graph, sliders, machine, spectrum, curve);
                else Draw(g, r, ctx, graph, handles, machine, spectrum);
            }, new ModeInput(Graphic, handles, sliders), animate: true);
            ctx.Changed += view.InvalidateVisual;

            // One block of controls per band; only the selected band's is shown: its filter, then (v2) its dynamics.
            var rows = new Grid();
            var rowOf = new UIElement[EqDesign.Bands];
            for (int b = 0; b < EqDesign.Bands; b++)
            {
                var filter = Layout.Row(
                    new ParamChoice(ctx, EqDesign.On(b), "On", 34),
                    Layout.Gap(10),
                    Layout.Column(new ParamDropdown(ctx, EqDesign.TypeOf(b), "Type", 112), Layout.Gap(1, 6), new ParamDropdown(ctx, EqDesign.Slope(b), "Slope (cuts)", 112)),
                    Layout.Gap(6),
                    Layout.Column(new ParamDropdown(ctx, EqDesign.Channel(b), "Channel", 104), Layout.Gap(1, 6), new ParamDropdown(ctx, EqDesign.ShapeOf(b), "Shape", 104)),
                    Layout.Gap(8),
                    new ParamKnob(ctx, EqDesign.Freq(b), "Freq", 50),
                    new ParamKnob(ctx, EqDesign.Gain(b), "Gain", 50),
                    new ParamKnob(ctx, EqDesign.Q(b), "Q", 50));
                var dynamics = Layout.Row(
                    Layout.Column(new ParamChoice(ctx, EqDesign.Dynamic(b), "Dynamic", 40), Layout.Gap(1, 4), new ParamDropdown(ctx, EqDesign.Detect(b), "Detector", 120)),
                    Layout.Gap(8),
                    new ParamKnob(ctx, EqDesign.Threshold(b), "Thresh", 36),
                    new ParamKnob(ctx, EqDesign.Ratio(b), "Ratio", 36),
                    new ParamKnob(ctx, EqDesign.Knee(b), "Knee", 36),
                    new ParamKnob(ctx, EqDesign.Range(b), "Range", 36),
                    new ParamKnob(ctx, EqDesign.Attack(b), "Attack", 36),
                    new ParamKnob(ctx, EqDesign.Release(b), "Release", 36));
                var row = Layout.Column(filter, Layout.Gap(1, 6), dynamics);
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

            var band = Layout.Row(tabs, Layout.Gap(14), rows);

            // Graphic mode: the selected slider's knob (one per slider, the selected one shown) and Flat.
            var knobs = new Grid();
            var knobOf = new UIElement[GraphicDesign.Bands];
            for (int k = 0; k < GraphicDesign.Bands; k++)
            {
                var knob = new ParamKnob(ctx, GraphicDesign.Slider(k), GraphicDesign.Labels[k], 50);
                knob.Visibility = k == sliders.Selected ? Visibility.Visible : Visibility.Collapsed;
                knobOf[k] = knob;
                knobs.Children.Add(knob);
            }
            var flat = new ActionButton("Flat", AccentColor, 70) { ToolTip = "Every slider to 0 dB" };
            flat.Click += () =>
            {
                for (int k = 0; k < GraphicDesign.Bands; k++) ctx.Set(GraphicDesign.Slider(k), Params.Table[GraphicDesign.Slider(k)].Default);
                ctx.RaiseChanged();
            };
            var graphicRow = Layout.Row(knobs, Layout.Gap(14), Layout.Column(Layout.Gap(1, 30), flat));
            sliders.SelectedChanged += sel =>
            {
                for (int k = 0; k < knobOf.Length; k++) knobOf[k].Visibility = k == sel ? Visibility.Visible : Visibility.Collapsed;
                view.InvalidateVisual();
            };

            // The mode, and whichever mode's controls apply. Both stay in the layout (the other one transparent and
            // out of reach of the pointer), so the editor keeps its size when the mode changes.
            var mode = new ParamChoice(ctx, P.Mode, "Mode", 0, vertical: true);
            var swap = new Grid();
            swap.Children.Add(band);
            swap.Children.Add(graphicRow);
            int shown = -1;
            void ShowMode()
            {
                int now = Graphic() ? 1 : 0;
                if (now == shown) return;
                shown = now;
                Show(band, now == 0);
                Show(graphicRow, now == 1);
                view.ToolTip = now == 1
                    ? "Drag a slider's cap (Shift: fine), or press beside it and drag across the sliders to draw. Wheel: 0.5 dB (Shift: 0.1). Double-click: 0 dB."
                    : "Drag a handle: frequency and gain (Shift: fine). Wheel: Q. Double-click: band on/off.";
                view.InvalidateVisual();
            }
            ShowMode();
            ctx.Changed += ShowMode;

            // v2: Match sets Output from the gain-match reading (one undo step); Delta hears what the EQ changes.
            var match = new ActionButton("Match", AccentColor, 96) { ToolTip = "Output takes back the loudness the EQ added or took (the gain-match reading, 3 s): one undo step" };
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
                Layout.Column(
                    new ParamKnob(ctx, P.Output, diameter: 56),
                    Layout.Gap(1, 10),
                    new ParamChoice(ctx, P.Delta, "Delta", 40),
                    Layout.Gap(1, 8),
                    match),
                Layout.Gap(10),
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 240),
                Layout.Gap(6),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 240));

            return Layout.Column(
                Layout.Row(
                    new Section("Response", AccentColor, view),
                    new Section("Output", AccentColor, output)),
                new Section("Band", AccentColor, Layout.Row(mode, Layout.Gap(14), swap)));
        }

        static readonly string[] ChannelLetters = { "", "L", "R", "M", "S" };

        static void Show(UIElement e, bool shown)
        {
            e.Opacity = shown ? 1 : 0;
            e.IsHitTestVisible = shown;
        }

        /// <summary>The pointer to the band handles (parametric) or the sliders (graphic); a drag stays with the one it began on.</summary>
        sealed class ModeInput : IGraphInput
        {
            readonly Func<bool> graphic;
            readonly IGraphInput parametric, sliders;
            IGraphInput captured;

            public ModeInput(Func<bool> graphic, IGraphInput parametric, IGraphInput sliders)
            {
                this.graphic = graphic;
                this.parametric = parametric;
                this.sliders = sliders;
            }

            IGraphInput Now => captured ?? (graphic() ? sliders : parametric);

            public bool Capturing => Now.Capturing;

            public bool Down(double x, double y, int clicks, bool fine)
            {
                captured = null;
                var input = Now;
                bool handled = input.Down(x, y, clicks, fine);
                if (input.Capturing) captured = input;
                return handled;
            }

            public bool Move(double x, double y, bool fine) => Now.Move(x, y, fine);

            public bool Up()
            {
                var input = Now;
                captured = null;
                return input.Up();
            }

            public bool Wheel(double x, double y, int delta, bool fine) => Now.Wheel(x, y, delta, fine);

            public bool Leave() => parametric.Leave() | sliders.Leave();
        }

        /// <summary>The graphic mode's sliders, read and set through the editor context (0.1 dB steps).</summary>
        sealed class GraphicBands : IGraphicSliders
        {
            readonly EditorContext ctx;

            public GraphicBands(EditorContext ctx) { this.ctx = ctx; }

            public int Count => GraphicDesign.Bands;
            public double Hz(int i) => GraphicDesign.Centres[i];
            public string Label(int i) => GraphicDesign.Labels[i];
            public double Db(int i) => ctx.Value(GraphicDesign.Slider(i));
            public double MinDb => GraphicDesign.MinDb;
            public double MaxDb => GraphicDesign.MaxDb;

            public void Set(int i, double db)
            {
                int p = GraphicDesign.Slider(i);
                int raw = Params.Table[p].Raw(Math.Round(db * 10) / 10);
                if (raw == ctx.Raw(p)) return;
                ctx.Set(p, raw);
                ctx.RaiseChanged();
            }
        }

        /// <summary>The solver the editor draws with: solved again when a slider or the sample rate changes.</summary>
        sealed class GraphicCurve
        {
            readonly GraphicSolver solver = new GraphicSolver();
            readonly int[] raw = new int[GraphicDesign.Bands];
            readonly double[] db = new double[GraphicDesign.Bands];
            bool solved;

            public GraphicSolver For(EditorContext ctx, double rate)
            {
                bool changed = !solved;
                if (rate != solver.SampleRate)
                {
                    solver.SetSampleRate(rate);
                    changed = true;
                }
                for (int k = 0; k < GraphicDesign.Bands; k++)
                {
                    int r = ctx.Raw(GraphicDesign.Slider(k));
                    if (r == raw[k] && solved) continue;
                    raw[k] = r;
                    db[k] = Params.Table[GraphicDesign.Slider(k)].Value(r);
                    changed = true;
                }
                if (changed) solver.Solve(db);
                solved = true;
                return solver;
            }
        }

        /// <summary>The bands as handles: their parameters, read and set through the editor context.</summary>
        sealed class Bands : IBandHandles
        {
            readonly EditorContext ctx;

            public Bands(EditorContext ctx) { this.ctx = ctx; }

            public int Count => EqDesign.Bands;

            public (double Hz, double Db, bool On, bool HasGain) Band(int i)
            {
                var type = (BandType)(int)ctx.Value(EqDesign.TypeOf(i));
                bool allPass = (BandShape)ctx.Raw(EqDesign.ShapeOf(i)) == BandShape.AllPass;
                return (ctx.Value(EqDesign.Freq(i)), ctx.Value(EqDesign.Gain(i)), ctx.Value(EqDesign.On(i)) > 0,
                    type is BandType.Bell or BandType.LowShelf or BandType.HighShelf && !allPass);
            }

            public void Move(int i, double hz, double? db)
            {
                ctx.Set(EqDesign.Freq(i), Params.Table[EqDesign.Freq(i)].Raw(hz));
                if (db.HasValue) ctx.Set(EqDesign.Gain(i), Params.Table[EqDesign.Gain(i)].Raw(Math.Round(db.Value * 10) / 10));
                ctx.RaiseChanged();
            }

            public void StepQ(int i, int steps)
            {
                ctx.Set(EqDesign.Q(i), ctx.Raw(EqDesign.Q(i)) + 2 * steps);
                ctx.RaiseChanged();
            }

            public void SetOn(int i, bool on)
            {
                ctx.Set(EqDesign.On(i), on ? 1 : 0);
                ctx.RaiseChanged();
            }
        }

        /// <summary>Band selector: a button per band, with a dot in the band's colour (filled when on).</summary>
        sealed class BandTabs : FrameworkElement
        {
            static readonly string[] Names = { "1", "2", "3", "4", "5", "6", "7", "8" };
            readonly EditorContext ctx;
            readonly BandHandleInput handles;
            readonly WpfCanvas canvas;
            readonly ChoiceStrip strip;
            int hover = -1;

            public BandTabs(EditorContext ctx, BandHandleInput handles)
            {
                this.ctx = ctx;
                this.handles = handles;
                canvas = new WpfCanvas(this);
                strip = new ChoiceStrip("Band", Names, 30, false, canvas);
                Width = strip.Width;
                Height = strip.Height + 12;
                Cursor = Cursors.Hand;
                ToolTip = "The band shown below (also: click its handle in the graph)";
                ctx.Changed += InvalidateVisual;
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                var p = e.GetPosition(this);
                int i = strip.CellAt(p.X, p.Y);
                if (i >= 0) handles.Select(i);
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
                for (int i = 0; i < Names.Length; i++)
                {
                    var cell = strip.Cell(i);
                    var colour = BandColors[i];
                    var centre = new NeoPoint(cell.X + cell.Width / 2, cell.Bottom + 7);
                    bool on = ctx.Value(EqDesign.On(i)) > 0;
                    c.Ellipse(centre, 3.5, 3.5, on ? (NeoFill)colour : NeoFill.None, new NeoStroke(colour, 1.2));
                }
            }
        }

        /// <summary>The output spectrum: 4096-point Hann FFT, smoothed to 1/6 octave, tilted +3 dB/oct (pink noise reads flat), falling slowly.</summary>
        sealed class SpectrumView
        {
            const int N = EqEngine.ScopeLength, Points = 180;
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

            public void Update(EqEngine engine)
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
                    // A full-scale sine reads 0 dB: Hann's coherent gain is 1/2.
                    double db = 10 * Math.Log10(power * 16.0 / ((double)N * N) + 1e-30) + 3 * Math.Log2(Hz[p] / 1000);
                    Db[p] = Math.Max(db, Db[p] - 1.5);
                }
            }
        }

        /// <summary>Graphic mode: the output spectrum, the sliders' tracks, the response through them (with the output gain) and their caps.</summary>
        static void DrawGraphic(INeoCanvas g, NeoRect r, EditorContext ctx, ResponseGraph graph, GraphicSliderInput sliders, NeoEqMachine machine, SpectrumView spectrum, GraphicCurve curve)
        {
            graph.Layout(r);
            if (machine != null)
            {
                spectrum.Update(machine.Engine);
                graph.InPlot(g, () => g.Path(graph.Spectrum(spectrum.Hz, spectrum.Db, 0, 96), Palette.Track.WithAlpha(0x90), NeoStroke.None));
            }
            graph.DrawGrid(g);
            sliders.DrawTracks(g);
            var solver = curve.For(ctx, machine?.Engine.SampleRate ?? 48000);
            double output = ctx.Value(P.Output);
            int i = sliders.Hover >= 0 ? sliders.Hover : sliders.Selected;
            string text = "Graphic · " + sliders.Caption(i);
            if (output != 0) text += " · " + Params.Table[P.Output].Name + " " + Params.Table[P.Output].Describe(ctx.Raw(P.Output));
            graph.InPlot(g, () =>
            {
                g.Path(graph.Curve(hz => solver.ResponseDb(hz) + output), NeoFill.None, new NeoStroke(AccentColor, 2.2));
                g.Text(text, graph.Plot.Left + 6, graph.Plot.Top + 2, 10.5, AccentColor);
            });
            // The caps over the curve, in the room kept around the plot (never over the axis labels).
            sliders.DrawCaps(g, AccentColor);
        }

        static void Draw(INeoCanvas g, NeoRect r, EditorContext ctx, ResponseGraph graph, BandHandleInput handles, NeoEqMachine machine, SpectrumView spectrum)
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

            int sel = handles.Selected;
            var colour = BandColors[sel];
            // v2: each dynamic band where its gain is now (the engine's reading), in the selected band's curve and the total.
            var now = new BandSettings[EqDesign.Bands];
            for (int b = 0; b < EqDesign.Bands; b++)
            {
                now[b] = EqDesign.Settings(values, b);
                now[b].GainDb += machine?.Engine.DynamicDb(b) ?? 0;
            }
            var cur = EqDesign.Settings(values, sel);
            string text = $"Band {sel + 1} · {Params.Table[EqDesign.TypeOf(sel)].Describe(values[EqDesign.TypeOf(sel)])} · {ParamDef.Freq(cur.Hz)}";
            if (cur.HasGain) text += $" · {Params.Table[EqDesign.Gain(sel)].Describe(values[EqDesign.Gain(sel)])}";
            text += $" · Q {cur.Q:0.00}";
            if (cur.Type is BandType.LowCut or BandType.HighCut) text += $" · {Params.Table[EqDesign.Slope(sel)].Describe(values[EqDesign.Slope(sel)])}";
            if (cur.Shape != BandShape.Standard) text += $" · {Params.Table[EqDesign.ShapeOf(sel)].Describe(values[EqDesign.ShapeOf(sel)])}";
            if (values.Choice(EqDesign.Channel(sel)) != 0) text += $" · {Params.Table[EqDesign.Channel(sel)].Describe(values[EqDesign.Channel(sel)])}";
            bool dynamic = values.Choice(EqDesign.Dynamic(sel)) != 0 && cur.HasGain;
            if (dynamic)
                text += $" · dynamic {Params.Table[EqDesign.Dynamic(sel)].Describe(values[EqDesign.Dynamic(sel)]).ToLowerInvariant()} {(machine?.Engine.DynamicDb(sel) ?? 0).ToString("+0.0;−0.0;0.0")} dB";
            if (!values.On(EqDesign.On(sel))) text += " (off)";
            // The curves inside the plot, never over the axis labels; the handles over them, in the room kept around it.
            graph.InPlot(g, () =>
            {
                if (values.On(EqDesign.On(sel)))
                {
                    if (dynamic)
                    {
                        // The dynamic range: between the band at its setting and moved by Range, down or up.
                        double range = values.Value(EqDesign.Range(sel)) * (values.Choice(EqDesign.Dynamic(sel)) == 1 ? -1 : 1);
                        var moved = cur;
                        moved.GainDb = Math.Clamp(cur.GainDb + range, -EqEngine.MaxDynamicDb, EqEngine.MaxDynamicDb);
                        Func<double, double> at = hz => 20 * Math.Log10(Math.Max(1e-9, EqDesign.Magnitude(cur, hz, rate)));
                        Func<double, double> far = hz => 20 * Math.Log10(Math.Max(1e-9, EqDesign.Magnitude(moved, hz, rate)));
                        g.Path(graph.Curve(far, 160), NeoFill.None, new NeoStroke(colour.WithAlpha(0x70), 1));
                        g.Path(graph.Curve(at, 160), NeoFill.None, new NeoStroke(colour.WithAlpha(0x70), 1));
                    }
                    var live = now[sel];
                    Func<double, double> own = hz => 20 * Math.Log10(Math.Max(1e-9, EqDesign.Magnitude(live, hz, rate)));
                    g.Path(graph.Area(own), colour.WithAlpha(0x2C), NeoStroke.None);
                    g.Path(graph.Curve(own, 160), NeoFill.None, new NeoStroke(colour.WithAlpha(0xB0), 1.2));
                }
                g.Path(graph.Curve(hz =>
                {
                    double db = values.Value(P.Output);
                    for (int b = 0; b < EqDesign.Bands; b++)
                        if (values.On(EqDesign.On(b))) db += 20 * Math.Log10(Math.Max(1e-12, EqDesign.Magnitude(now[b], hz, rate)));
                    return db;
                }), NeoFill.None, new NeoStroke(AccentColor, 2.2));
                g.Text(text, graph.Plot.Left + 6, graph.Plot.Top + 2, 10.5, values.On(EqDesign.On(sel)) ? colour : Palette.DimText);
            });
            handles.Draw(g, BandColors);
            // v2: a band on one channel or half carries its letter beside its handle.
            for (int b = 0; b < EqDesign.Bands; b++)
            {
                int chan = values.Choice(EqDesign.Channel(b));
                if (chan == 0 || !values.On(EqDesign.On(b))) continue;
                var pos = handles.Position(b);
                g.Text(ChannelLetters[chan], pos.X + BandHandleInput.Radius + 1, pos.Y - 16, 9.5, BandColors[b]);
            }
        }
    }
}
