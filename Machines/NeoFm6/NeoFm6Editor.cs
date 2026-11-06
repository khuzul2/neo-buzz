#nullable disable
// Neo FM6's editor (docs/specs/machines.md §5): the algorithm as a live diagram (operators light
// up with their level), pitch envelope, LFO, voice, output and the DX7 bank on top; the six
// operators below, each with its frequency, level, envelope and keyboard scaling. WPF lays the
// editor out; the graphs draw on the kit's canvas (INeoCanvas), the algorithm with the kit's
// OperatorDiagram.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Fm6
{
    internal sealed class NeoFm6Editor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Synth identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xFF, 0xC4, 0x3D));
        const double D = 34, Small = 24, SmallWidth = 46;

        protected override string Title => "Neo FM6";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoFm6Machine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            // Algorithm.
            var algorithm = S("Algorithm", Layout.Column(
                new AlgorithmView(ctx, machine, 214, 132),
                Layout.Row(new ParamKnob(ctx, P.Algorithm, diameter: D), new ParamKnob(ctx, P.Feedback, diameter: D),
                           Layout.Gap(4), new ParamChoice(ctx, P.OscSync, "Key sync", 34))));

            // Pitch envelope.
            var peg = S("Pitch envelope", Layout.Column(
                EnvelopeView(ctx, P.PegR1, P.PegL1, 4 * 54, 52, true),
                CompactW(ctx, 4, 54, P.PegR1, "R1", P.PegR2, "R2", P.PegR3, "R3", P.PegR4, "R4"),
                CompactW(ctx, 4, 54, P.PegL1, "L1", P.PegL2, "L2", P.PegL3, "L3", P.PegL4, "L4")));

            // LFO and controllers.
            var lfo = S("LFO", Layout.Column(
                Layout.Row(new ParamDropdown(ctx, P.LfoWave, width: 96), Layout.Gap(6), new ParamChoice(ctx, P.LfoSync, "Key sync", 34)),
                CompactW(ctx, 3, 64, P.LfoSpeed, "Speed", P.LfoDelay, "Delay", P.PitchSens, "P sens",
                                     P.LfoPmd, "Pitch", P.LfoAmd, "Amp"),
                Compact(ctx, 4, P.WheelPitch, "Whl P", P.WheelAmp, "Whl A", P.AtPitch, "AT P", P.AtAmp, "AT A")));

            var voice = S("Voice", Layout.Column(
                new ParamChoice(ctx, P.VoiceMode, cellWidth: 52),
                new ParamChoice(ctx, P.GlideMode, cellWidth: 52),
                Compact(ctx, 4, P.Polyphony, "Poly", P.Unison, "Unison", P.UniDetune, "Detune", P.UniSpread, "Spread",
                                P.Glide, "Glide", P.BendRange, "Bend", P.Transpose, "Transp")));

            var meter = new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 118);
            var output = S("Output", Layout.Row(Layout.Column(
                new ParamChoice(ctx, P.EngineMode, cellWidth: 56),
                Layout.Row(new ParamKnob(ctx, P.Tone, diameter: D), new ParamKnob(ctx, P.Volume, diameter: D))), Layout.Gap(6), meter));

            var bank = S("DX7 voices", BankPanel(ctx, machine));

            var ops = new UIElement[6];
            for (int op = 0; op < 6; op++) ops[op] = Operator(ctx, machine, op);

            return Layout.Column(
                Layout.Row(algorithm, peg, lfo, voice, output, bank),
                Layout.Row(ops[0], ops[1], ops[2]),
                Layout.Row(ops[3], ops[4], ops[5]));
        }

        /// <summary>Small knobs with short labels, <paramref name="columns"/> to a row: (parameter, label) pairs.</summary>
        static UniformGrid Compact(EditorContext ctx, int columns, params object[] pairs) => CompactW(ctx, columns, SmallWidth, pairs);

        /// <summary>As <see cref="Compact"/>, with cells <paramref name="width"/> wide (for longer value texts).</summary>
        static UniformGrid CompactW(EditorContext ctx, int columns, double width, params object[] pairs)
        {
            var g = new UniformGrid { Columns = columns, HorizontalAlignment = HorizontalAlignment.Left };
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                g.Children.Add(new ParamKnob(ctx, (int)pairs[i], (string)pairs[i + 1], Small) { Width = width });
            return g;
        }

        static int Op(int op, int op1Param) => FmEngine.OpParam(op, op1Param - P.O1On);

        UIElement Operator(EditorContext ctx, NeoFm6Machine machine, int op)
        {
            var freq = new TextBlock
            {
                Foreground = Theme.Brush(AccentColor),
                FontFamily = Theme.FontFamily,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Width = 76,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 2),
                ToolTip = "Frequency: a ratio to the note, or fixed in Hz",
            };
            void ShowFreq() => freq.Text = FmText.Frequency(ctx.Raw(Op(op, P.O1Mode)) == 1, ctx.Raw(Op(op, P.O1Coarse)), ctx.Raw(Op(op, P.O1Fine)), ctx.Raw(Op(op, P.O1Detune)));
            ShowFreq();
            ctx.Changed += ShowFreq;

            var activity = new GraphView(10, 40, (c, r) =>
            {
                float g = machine?.Engine.OpLevel(op) ?? 0;
                double db = DspMath.GainToDb(Math.Max(1e-9f, g / 2f));
                double h = Math.Clamp((db + 60) / 60, 0, 1) * (r.Height - 4);
                if (h > 0.5) c.Rect(new NeoRect(r.X + 2, r.Bottom - 2 - h, r.Width - 4, h), AccentColor, NeoStroke.None);
            }, animate: true) { ToolTip = "Operator level now" };

            var top = Layout.Row(
                new ParamChoice(ctx, Op(op, P.O1On), "On", 30),
                Layout.Gap(6),
                new ParamChoice(ctx, Op(op, P.O1Mode), "Mode", 44),
                Layout.Gap(6),
                new ParamDropdown(ctx, Op(op, P.O1Wave), "Wave", 84),
                freq,
                Layout.Gap(4),
                activity);

            var tuning = Layout.Row(
                new ParamKnob(ctx, Op(op, P.O1Level), "Level", 40),
                new ParamKnob(ctx, Op(op, P.O1Coarse), "Coarse", 28) { Width = 54 },
                new ParamKnob(ctx, Op(op, P.O1Fine), "Fine", 28) { Width = 54 },
                new ParamKnob(ctx, Op(op, P.O1Detune), "Detune", 28) { Width = 54 },
                new ParamKnob(ctx, Op(op, P.O1VelSens), "Vel", 28) { Width = 46 },
                new ParamKnob(ctx, Op(op, P.O1AmpSens), "AM", 28) { Width = 46 });

            var envelope = Layout.Column(
                EnvelopeView(ctx, Op(op, P.O1R1), Op(op, P.O1L1), 4 * SmallWidth, 46, false),
                Compact(ctx, 4, Op(op, P.O1R1), "R1", Op(op, P.O1R2), "R2", Op(op, P.O1R3), "R3", Op(op, P.O1R4), "R4"),
                Compact(ctx, 4, Op(op, P.O1L1), "L1", Op(op, P.O1L2), "L2", Op(op, P.O1L3), "L3", Op(op, P.O1L4), "L4"));

            var scaling = Layout.Column(
                ScalingView(ctx, op, 3 * SmallWidth, 46),
                Compact(ctx, 3, Op(op, P.O1Break), "Break", Op(op, P.O1LDepth), "L dep", Op(op, P.O1RDepth), "R dep",
                                Op(op, P.O1RateSc), "Rate sc", Op(op, P.O1LCurve), "L crv", Op(op, P.O1RCurve), "R crv"));

            return new Section($"Operator {op + 1}", AccentColor, Layout.Column(
                top,
                Layout.Gap(1, 4),
                tuning,
                Layout.Row(envelope, Layout.Gap(10), scaling)));
        }

        /// <summary>A DX7 envelope: four rates and four levels; pitch envelopes centre on level 50.</summary>
        static GraphView EnvelopeView(EditorContext ctx, int rate1, int level1, double width, double height, bool pitch)
        {
            var view = new GraphView(width, height, (c, r) =>
            {
                double left = r.Left + 5, top = r.Top + 5, h = r.Height - 10, w = r.Width - 10;
                double Y(int level) => top + h - level / 99.0 * h;
                if (pitch) c.Line(left, Y(50), left + w, Y(50), new NeoStroke(Palette.Grid, 1, false));
                // Segment widths: slower rates are wider (about the log of the time); the sustain gets a fixed width.
                var widths = new double[5];
                for (int i = 0; i < 4; i++) widths[i < 3 ? i : 4] = 0.15 + Math.Pow((99 - ctx.Raw(rate1 + i)) / 99.0, 1.5) * 1.6;
                widths[3] = 0.5;
                double total = 0;
                foreach (var x in widths) total += x;
                double X(double t) => left + t / total * w;
                int l4 = ctx.Raw(level1 + 3);
                var env = new NeoPath();
                {
                    double t = 0;
                    env.MoveTo(X(0), Y(l4));
                    for (int i = 0; i < 3; i++)
                    {
                        t += widths[i];
                        env.LineTo(X(t), Y(ctx.Raw(level1 + i)), smoothJoin: true);
                    }
                    t += widths[3];
                    env.LineTo(X(t), Y(ctx.Raw(level1 + 2)), smoothJoin: true);
                    t += widths[4];
                    env.LineTo(X(t), Y(l4), smoothJoin: true);
                }
                c.Path(env, NeoFill.None, new NeoStroke(ctx.Accent, 1.5));
                // Key-off marker.
                double off = X(widths[0] + widths[1] + widths[2] + widths[3]);
                c.Line(off, top, off, top + h, new NeoStroke(ctx.Accent.WithAlpha(0x50), 1, false));
            });
            ctx.Changed += view.InvalidateVisual;
            return view;
        }

        /// <summary>Keyboard level scaling across the keys: the level offset each note gets.</summary>
        static GraphView ScalingView(EditorContext ctx, int op, double width, double height)
        {
            var view = new GraphView(width, height, (c, r) =>
            {
                int bp = ctx.Raw(Op(op, P.O1Break)), ld = ctx.Raw(Op(op, P.O1LDepth)), rd = ctx.Raw(Op(op, P.O1RDepth));
                int lc = ctx.Raw(Op(op, P.O1LCurve)), rc = ctx.Raw(Op(op, P.O1RCurve));
                double left = r.Left + 4, w = r.Width - 8, mid = r.Top + r.Height / 2, h = r.Height / 2 - 4;
                c.Line(left, mid, left + w, mid, new NeoStroke(Palette.Grid, 1, false));
                double bx = left + (bp + 21 - 21) / 99.0 * w;
                c.Line(bx, r.Top + 3, bx, r.Bottom - 3, new NeoStroke(ctx.Accent.WithAlpha(0x50), 1, false));
                var curve = new NeoPath();
                for (int note = 21; note <= 120; note++)
                {
                    int s = Dx7.ScaleLevel(note, bp, ld, rd, lc, rc);
                    curve.LineTo(left + (note - 21) / 99.0 * w, mid - Math.Clamp(s / 127.0, -1, 1) * h);
                }
                c.Path(curve, NeoFill.None, new NeoStroke(ctx.Accent, 1.5));
            }) { ToolTip = "Keyboard level scaling: level offset from the lowest to the highest key" };
            ctx.Changed += view.InvalidateVisual;
            return view;
        }

        /// <summary>Loads a DX7 bank (.syx), lists its voices, and exports the current sound as a DX7 voice.</summary>
        static UIElement BankPanel(EditorContext ctx, NeoFm6Machine machine)
        {
            var name = new TextBlock
            {
                Foreground = Theme.Text,
                FontFamily = Theme.FontFamily,
                FontSize = 11.5,
                Width = 132,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var voices = new ActionButton("Voices ▾", AccentColor, 132);
            void Show()
            {
                int count = machine?.Bank.Count ?? 0;
                string label = count == 0 ? "No bank loaded" : $"{machine.BankName} ({count})";
                if (name.Text == label) return;
                name.Text = label;
                voices.Text = count == 0 ? "Voices ▾" : "Choose a voice ▾";
                voices.InvalidateVisual();
            }
            Show();
            Poll.Every(name, 0.5, Show);   // a bank can also arrive with the song or a preset

            var load = new ActionButton("Load bank…", AccentColor, 132);
            load.Click += () =>
            {
                if (machine == null) return;
                EditorFiles.Open(load, "Load a DX7 bank or voice", "DX7 SysEx (*.syx)|*.syx|All files (*.*)|*.*", file =>
                {
                    try
                    {
                        machine.LoadBank(file);
                        Show();
                        if (machine.Bank.Count == 1) Apply(ctx, machine.Bank[0]);
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show($"Couldn't load {System.IO.Path.GetFileName(file)}: {e.Message}", "Neo FM6", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                });
            };

            voices.Click += () =>
            {
                if (machine == null || machine.Bank.Count == 0) return;
                var menu = new ContextMenu();
                for (int i = 0; i < machine.Bank.Count; i++)
                {
                    var v = machine.Bank[i];
                    var item = new MenuItem { Header = $"{i + 1,2}  {v.Name}" };
                    item.Click += (s, a) => Apply(ctx, v);
                    menu.Items.Add(item);
                }
                menu.PlacementTarget = voices;
                menu.Placement = PlacementMode.Bottom;
#if AVALONIA
                menu.Open(voices);
#else
                menu.IsOpen = true;
#endif
            };

            var export = new ActionButton("Export voice…", AccentColor, 132);
            export.Click += () =>
            {
                if (machine == null) return;
                EditorFiles.Save(export, "Export as a DX7 voice", "DX7 SysEx (*.syx)|*.syx", "Neo FM6 voice.syx", file =>
                {
                    try
                    {
                        machine.ExportVoice(file, System.IO.Path.GetFileNameWithoutExtension(file).ToUpperInvariant());
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show($"Couldn't write {file}: {e.Message}", "Neo FM6", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                });
            };

            var hint = new TextBlock
            {
                Foreground = Theme.DimText,
                FontFamily = Theme.FontFamily,
                FontSize = 10.5,
                Width = 132,
                TextWrapping = TextWrapping.Wrap,
                Text = "32-voice banks or single voices. The bank is saved with the song.",
            };
            return Layout.Column(name, Layout.Gap(1, 6), load, Layout.Gap(1, 4), voices, Layout.Gap(1, 4), export, Layout.Gap(1, 6), hint);
        }

        static void Apply(EditorContext ctx, Sysex.Voice voice)
        {
            foreach (var (p, value) in Sysex.ToParams(voice.Data)) ctx.Set(p, value);
            ctx.RaiseChanged();
        }

        /// <summary>
        /// The algorithm: operators as boxes, modulators above what they modulate, carriers on the
        /// bottom line to the output, the feedback loop; each box lights with its operator's level.
        /// Mouse wheel or click: next / previous algorithm.
        /// </summary>
        sealed class AlgorithmView : GraphView
        {
            readonly EditorContext ctx;

            public AlgorithmView(EditorContext ctx, NeoFm6Machine machine, double width, double height)
                : base(width, height, (c, r) => Draw(c, r, ctx, machine), animate: true)
            {
                this.ctx = ctx;
                Cursor = Cursors.Hand;
                ToolTip = "Algorithm: wheel or click to change (right click: back)";
            }

            void Step(int delta) => ctx.Set(P.Algorithm, (ctx.Raw(P.Algorithm) + delta + 32) % 32);

            protected override void OnMouseWheel(MouseWheelEventArgs e)
            {
                Step(e.Delta < 0 ? 1 : -1);
                e.Handled = true;
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                Step(1);
                e.Handled = true;
            }

            protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
            {
                Step(-1);
                e.Handled = true;
            }

            /// <summary>The diagram of algorithm 1 … 32 (Engine/Algorithms.cs), laid out by the kit.</summary>
            internal static OperatorDiagram Diagram(int number)
            {
                var alg = Algorithms.Get(number);
                return new OperatorDiagram(alg.Number, Algorithms.Layout(alg), alg.Carriers, alg.Modulators, alg.FeedbackFrom, alg.FeedbackTo);
            }

            static void Draw(INeoCanvas c, NeoRect r, EditorContext ctx, NeoFm6Machine machine) =>
                Diagram(ctx.Raw(P.Algorithm) + 1).Draw(c, r, ctx.Accent, ctx.Raw(P.Feedback),
                    op => ctx.Raw(Op(op, P.O1On)) != 0,
                    op => machine?.Engine.OpLevel(op) ?? 0);
        }
    }
}
