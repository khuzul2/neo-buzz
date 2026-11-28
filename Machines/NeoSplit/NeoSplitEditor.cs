#nullable disable
// Neo Split's editor (docs/specs/neo-split.md section 6): the mode as tabs; for Bands a frequency strip with the bands' own
// curves and a draggable handle per crossover, the band count, slope and crossovers as controls; for Transient and Body its
// three controls; for Mid and Side a note of what the two outputs hold. Always a live meter per output, named for what the
// output carries in the mode (the outputs themselves are "Out 1" to "Out 4": the studio reads port names once). One source for WPF
// and Avalonia (the Neo editor kit); the strip draws on the kit's canvas (INeoCanvas) and its input is toolkit-free (Engine/SplitHandles.cs).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Split
{
    internal sealed class NeoSplitEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Utility identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Utility, NeoColor.FromRgb(0x6C, 0xB4, 0xE8));

        internal static readonly NeoColor[] OutputColors = Tokens.Bands(new[]
        {
            NeoColor.FromRgb(0xFF, 0x6B, 0x6B), NeoColor.FromRgb(0x8F, 0xD1, 0x4F), NeoColor.FromRgb(0x4F, 0xB3, 0xFF), NeoColor.FromRgb(0xC3, 0x8C, 0xFF),
        });

        protected override string Title => "Neo Split";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        static SplitEngine.Mode ModeOf(EditorContext ctx) => (SplitEngine.Mode)Math.Clamp(ctx.Raw(P.Mode), 0, 2);

        static int BandsOf(EditorContext ctx) => Math.Clamp(ctx.Raw(P.Bands) + 2, 2, 4);

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoSplitMachine;

            var modes = new ParamChoice(ctx, P.Mode, "Mode", 124);

            // Bands: the strip, the count, the slope and the crossovers.
            var graph = new ResponseGraph(-48, 6, 12);
            graph.NeedRoom(SplitHandleInput.GripRadius + 2);
            var handles = new SplitHandleInput(graph, new Crossovers(ctx, machine));
            var strip = new InteractiveGraph(520, 176, (g, r) => DrawStrip(g, r, ctx, graph, handles, machine), handles, animate: true)
            {
                ToolTip = "Drag a crossover (Shift: fine, the wheel steps a twelfth of an octave). Each band is 6 dB down at its crossover and the bands add up to the input.",
            };
            ctx.Changed += strip.InvalidateVisual;
            var bandControls = Layout.Row(
                new ParamChoice(ctx, P.Bands, "Bands", 34, false, new[] { "2", "3", "4" }, new[] { 0, 1, 2 }),
                Layout.Gap(8),
                new ParamChoice(ctx, P.Slope, "Slope", 78),
                Layout.Gap(10),
                new ParamKnob(ctx, P.Xo1, "XO 1", 44), new ParamKnob(ctx, P.Xo2, "XO 2", 44), new ParamKnob(ctx, P.Xo3, "XO 3", 44));
            var bandsPage = Layout.Column(strip, Layout.Gap(1, 4), bandControls);

            // Transient and Body.
            var transientPage = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Sensitivity, diameter: 52), new ParamKnob(ctx, P.Length, diameter: 52), new ParamKnob(ctx, P.Smoothing, diameter: 52)),
                Layout.Gap(1, 6),
                Note(520, 74, "Out 1: the onsets. Out 2: what follows. Together: the input, 2 ms later.",
                    "Sensitivity: how easily a rise counts as a hit. Length: how long a hit lasts. Smoothing: how slowly the body returns."));

            // Mid and Side.
            var sidePage = Layout.Column(
                Note(520, 74, "Out 1 carries the mid, (L + R) / 2, on both sides; Out 2 the side, (L - R) / 2, with the right side inverted.",
                    "Added together they are left and right, bit for bit at the end of a clean chain. No controls: a mono input gives a silent Out 2."));

            var pages = new Grid();
            pages.Children.Add(bandsPage);
            pages.Children.Add(transientPage);
            pages.Children.Add(sidePage);
            void ShowPage()
            {
                var mode = ModeOf(ctx);
                bandsPage.Visibility = mode == SplitEngine.Mode.Bands ? Visibility.Visible : Visibility.Collapsed;
                transientPage.Visibility = mode == SplitEngine.Mode.TransientBody ? Visibility.Visible : Visibility.Collapsed;
                sidePage.Visibility = mode == SplitEngine.Mode.MidSide ? Visibility.Visible : Visibility.Collapsed;
            }
            ShowPage();
            ctx.Changed += ShowPage;

            // A meter per output, named for what it carries.
            var meters = Layout.Row(OutputMeter(ctx, machine, 0), Layout.Gap(4), OutputMeter(ctx, machine, 1), Layout.Gap(4), OutputMeter(ctx, machine, 2), Layout.Gap(4), OutputMeter(ctx, machine, 3));

            return Layout.Row(
                Layout.Column(
                    new Section("Mode", AccentColor, modes),
                    new Section("Split", AccentColor, pages)),
                new Section("Outputs", AccentColor, meters));
        }

        /// <summary>A short note in the graph frame: a line or two of what the outputs hold.</summary>
        static GraphView Note(double width, double height, string first, string second) =>
            new GraphView(width, height, (g, r) =>
            {
                Wrap(g, first, r.X + 8, r.Y + 7, r.Width - 16, 10.5, Palette.Text);
                Wrap(g, second, r.X + 8, r.Y + 30, r.Width - 16, 10, Palette.DimText);
            });

        // The kit's canvas draws a line of text: split a sentence at a space to the width it has.
        static void Wrap(INeoCanvas g, string text, double x, double y, double width, double size, NeoColor colour)
        {
            if (g.MeasureText(text, size) <= width)
            {
                g.Text(text, x, y, size, colour);
                return;
            }
            int cut = text.LastIndexOf(' ', Math.Min(text.Length - 1, (int)(text.Length * width / g.MeasureText(text, size))));
            if (cut <= 0) cut = text.Length / 2;
            g.Text(text.Substring(0, cut), x, y, size, colour);
            g.Text(text.Substring(cut + 1), x, y + size + 3, size, colour);
        }

        /// <summary>One output: its number, a level meter and what it carries now.</summary>
        static GraphView OutputMeter(EditorContext ctx, NeoSplitMachine machine, int o)
        {
            var view = new GraphView(80, 262, (g, r) =>
            {
                string name = SplitDesign.OutputNames(ModeOf(ctx), BandsOf(ctx))[o];
                bool used = name.Length > 0;
                var colour = used ? OutputColors[o] : Palette.DimText;
                var bar = new NeoRect(r.X + r.Width / 2 - 14, r.Y + 22, 28, r.Height - 66);
                g.Rect(bar, Palette.Panel, NeoStroke.None);
                MeterPainter.Draw(g, bar, used ? machine?.Engine.OutMeter[o].Peak ?? 0 : 0, colour, false);
                g.Text("Out " + (o + 1), r.X + r.Width / 2, r.Y + 4, 11, used ? Palette.Text : Palette.DimText, true, NeoAlign.Center);
                g.Text(used ? name : "unused", r.X + r.Width / 2, r.Bottom - 38, 10.5, colour, used, NeoAlign.Center);
                double db = used && machine != null ? DspMath.GainToDb((double)machine.Engine.OutMeter[o].Peak) : -120;
                g.Text(db <= -99 ? "-inf" : db.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB", r.X + r.Width / 2, r.Bottom - 20, 9.5, Palette.DimText, align: NeoAlign.Center);
            }, animate: true)
            {
                ToolTip = $"Output {o + 1}: what it carries depends on the mode (Bands: low to high; Transient and Body: the hit, then the rest; Mid and Side: the centre, then the sides)",
            };
            return view;
        }

        /// <summary>The bands' own curves (the engine's cascade), their spans shaded and labelled, the crossovers as handles.</summary>
        static void DrawStrip(INeoCanvas g, NeoRect r, EditorContext ctx, ResponseGraph graph, SplitHandleInput handles, NeoSplitMachine machine)
        {
            graph.Layout(r);
            graph.DrawGrid(g);
            int bands = BandsOf(ctx), slope = ctx.Raw(P.Slope) == 0 ? 0 : 1;
            var xo = new double[3];
            SplitEngine.EffectiveCrossovers(ctx.Value(P.Xo1), ctx.Value(P.Xo2), ctx.Value(P.Xo3), bands, machine?.Engine.SampleRate ?? 48000, xo);
            var names = SplitDesign.OutputNames(SplitEngine.Mode.Bands, bands);
            var spans = handles.Spans();
            graph.InPlot(g, () =>
            {
                for (int b = 0; b < bands; b++)
                {
                    int band = b;
                    var colour = OutputColors[b];
                    Func<double, double> shape = hz => 20 * Math.Log10(Math.Max(1e-9, SplitDesign.Band(band, bands, hz, xo, slope)));
                    g.Path(graph.Area(shape), colour.WithAlpha(0x26), NeoStroke.None);
                    g.Path(graph.Curve(shape, 200), NeoFill.None, new NeoStroke(colour, 1.6));
                }
            });
            handles.Draw(g, AccentColor);
            for (int b = 0; b < spans.Length && b < bands; b++)
            {
                var (left, right) = spans[b];
                if (right - left < 24) continue;
                g.Text(names[b], (left + right) / 2, graph.Plot.Bottom - 15, 10, OutputColors[b], true, NeoAlign.Center);
            }
            g.Text(slope == 0 ? "24 dB/oct" : "48 dB/oct", graph.Plot.Right - 4, graph.Plot.Top + 16, 9.5, Palette.DimText, align: NeoAlign.Right);
        }

        /// <summary>The crossovers as handles: the frequencies the engine uses, moves written to the parameters.</summary>
        sealed class Crossovers : ISplitCrossovers
        {
            readonly EditorContext ctx;
            readonly NeoSplitMachine machine;

            public Crossovers(EditorContext ctx, NeoSplitMachine machine)
            {
                this.ctx = ctx;
                this.machine = machine;
            }

            public int Bands => BandsOf(ctx);

            static int Index(int i) => i == 0 ? P.Xo1 : i == 1 ? P.Xo2 : P.Xo3;

            public double Hz(int i)
            {
                var xo = new double[3];
                SplitEngine.EffectiveCrossovers(ctx.Value(P.Xo1), ctx.Value(P.Xo2), ctx.Value(P.Xo3), Bands, machine?.Engine.SampleRate ?? 48000, xo);
                return xo[i];
            }

            public void Move(int i, double hz)
            {
                int p = Index(i), raw = Params.Table[p].Raw(hz);
                if (raw == ctx.Raw(p)) return;
                ctx.Set(p, raw);
                ctx.RaiseChanged();
            }
        }
    }
}
