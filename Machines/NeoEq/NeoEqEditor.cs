#nullable disable
// Neo EQ's editor (docs/specs/machines.md §4): the response the engine runs (EqDesign, the same
// function) over the output spectrum, with a draggable handle per band; below it the selected
// band's controls; the output gain and meters at the side. WPF lays the editor out; the graph
// draws on the kit's canvas (INeoCanvas) and its input is toolkit-free (Core/ResponseGraph.cs).

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
            var spectrum = new SpectrumView();
            var view = new InteractiveGraph(680, 260, (g, r) => Draw(g, r, ctx, graph, handles, machine, spectrum), handles, animate: true)
            {
                ToolTip = "Drag a handle: frequency and gain (Shift: fine). Wheel: Q. Double-click: band on/off.",
            };
            ctx.Changed += view.InvalidateVisual;

            // One row of controls per band; only the selected band's is shown.
            var rows = new Grid();
            var rowOf = new UIElement[EqDesign.Bands];
            for (int b = 0; b < EqDesign.Bands; b++)
            {
                var row = Layout.Row(
                    new ParamChoice(ctx, EqDesign.On(b), "On", 34),
                    Layout.Gap(10),
                    Layout.Column(new ParamDropdown(ctx, EqDesign.TypeOf(b), "Type", 112), Layout.Gap(1, 6), new ParamDropdown(ctx, EqDesign.Slope(b), "Slope (cuts)", 112)),
                    Layout.Gap(8),
                    new ParamKnob(ctx, EqDesign.Freq(b), "Freq", 50),
                    new ParamKnob(ctx, EqDesign.Gain(b), "Gain", 50),
                    new ParamKnob(ctx, EqDesign.Q(b), "Q", 50));
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

            var output = Layout.Column(
                new ParamKnob(ctx, P.Output, diameter: 56),
                Layout.Gap(1, 8),
                Layout.Row(
                    new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 200),
                    Layout.Gap(6),
                    new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 200)));

            return Layout.Column(
                Layout.Row(
                    new Section("Response", AccentColor, view),
                    new Section("Output", AccentColor, output)),
                new Section("Band", AccentColor, band));
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
                return (ctx.Value(EqDesign.Freq(i)), ctx.Value(EqDesign.Gain(i)), ctx.Value(EqDesign.On(i)) > 0,
                    type is BandType.Bell or BandType.LowShelf or BandType.HighShelf);
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

        static void Draw(INeoCanvas g, NeoRect r, EditorContext ctx, ResponseGraph graph, BandHandleInput handles, NeoEqMachine machine, SpectrumView spectrum)
        {
            graph.Layout(r);
            if (machine != null)
            {
                spectrum.Update(machine.Engine);
                g.Path(graph.Spectrum(spectrum.Hz, spectrum.Db, 0, 96), Palette.Track.WithAlpha(0x90), NeoStroke.None);
            }
            graph.DrawGrid(g);

            var values = new ParamValues(Params.Table);
            for (int i = 0; i < Params.Table.Length; i++) values.Set(i, ctx.Raw(i));
            double rate = machine?.Engine.SampleRate ?? 48000;

            int sel = handles.Selected;
            var colour = BandColors[sel];
            if (values.On(EqDesign.On(sel)))
            {
                var s = EqDesign.Settings(values, sel);
                g.Path(graph.Area(hz => 20 * Math.Log10(Math.Max(1e-9, EqDesign.Magnitude(s, hz, rate)))),
                    colour.WithAlpha(0x2C), new NeoStroke(colour.WithAlpha(0xA0), 1));
            }
            g.Path(graph.Curve(hz => EqDesign.ResponseDb(values, hz, rate)), NeoFill.None, new NeoStroke(AccentColor, 2.2));
            handles.Draw(g, BandColors);

            var b = EqDesign.Settings(values, sel);
            string text = $"Band {sel + 1} · {Params.Table[EqDesign.TypeOf(sel)].Describe(values[EqDesign.TypeOf(sel)])} · {ParamDef.Freq(b.Hz)}";
            if (b.HasGain) text += $" · {Params.Table[EqDesign.Gain(sel)].Describe(values[EqDesign.Gain(sel)])}";
            text += $" · Q {b.Q:0.00}";
            if (b.Type is BandType.LowCut or BandType.HighCut) text += $" · {Params.Table[EqDesign.Slope(sel)].Describe(values[EqDesign.Slope(sel)])}";
            if (!values.On(EqDesign.On(sel))) text += " (off)";
            g.Text(text, graph.Plot.Left + 6, graph.Plot.Top + 2, 10.5, values.On(EqDesign.On(sel)) ? colour : Palette.DimText);
        }
    }
}
