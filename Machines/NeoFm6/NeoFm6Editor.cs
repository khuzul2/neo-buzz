#nullable disable
// Neo FM6's editor (docs/specs/machines.md §5): the algorithm as a live diagram (operators light
// up with their level), pitch envelope, LFO, voice, output and the DX7 bank on top; the six
// operators below, each with its frequency, level, envelope and keyboard scaling.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Fm6
{
    internal sealed class NeoFm6Editor : NeoEditor
    {
        static readonly Color AccentColor = Color.FromRgb(0xFF, 0xC4, 0x3D);
        const double D = 34, Small = 24, SmallWidth = 46;

        protected override string Title => "Neo FM6";
        protected override Color Accent => AccentColor;
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
                Foreground = Theme.Solid(AccentColor),
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

            var activity = new GraphView(10, 40, (dc, r, v) =>
            {
                float g = machine?.Engine.OpLevel(op) ?? 0;
                double db = DspMath.GainToDb(Math.Max(1e-9f, g / 2f));
                double h = Math.Clamp((db + 60) / 60, 0, 1) * (r.Height - 4);
                if (h > 0.5) dc.DrawRectangle(Theme.Solid(AccentColor), null, new Rect(r.X + 2, r.Bottom - 2 - h, r.Width - 4, h));
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
            var view = new GraphView(width, height, (dc, r, v) =>
            {
                double left = r.Left + 5, top = r.Top + 5, h = r.Height - 10, w = r.Width - 10;
                double Y(int level) => top + h - level / 99.0 * h;
                if (pitch) dc.DrawLine(Theme.Pen(Theme.Grid, 1, false), new Point(left, Y(50)), new Point(left + w, Y(50)));
                // Segment widths: slower rates are wider (about the log of the time); the sustain gets a fixed width.
                var widths = new double[5];
                for (int i = 0; i < 4; i++) widths[i < 3 ? i : 4] = 0.15 + Math.Pow((99 - ctx.Raw(rate1 + i)) / 99.0, 1.5) * 1.6;
                widths[3] = 0.5;
                double total = 0;
                foreach (var x in widths) total += x;
                double X(double t) => left + t / total * w;
                int l4 = ctx.Raw(level1 + 3);
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    double t = 0;
                    c.BeginFigure(new Point(X(0), Y(l4)), false, false);
                    for (int i = 0; i < 3; i++)
                    {
                        t += widths[i];
                        c.LineTo(new Point(X(t), Y(ctx.Raw(level1 + i))), true, true);
                    }
                    t += widths[3];
                    c.LineTo(new Point(X(t), Y(ctx.Raw(level1 + 2))), true, true);
                    t += widths[4];
                    c.LineTo(new Point(X(t), Y(l4)), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, Theme.Pen(Theme.Solid(ctx.Accent), 1.5), g);
                // Key-off marker.
                double off = X(widths[0] + widths[1] + widths[2] + widths[3]);
                dc.DrawLine(Theme.Pen(Theme.Solid(ctx.Accent, 0x50), 1, false), new Point(off, top), new Point(off, top + h));
            });
            ctx.Changed += view.InvalidateVisual;
            return view;
        }

        /// <summary>Keyboard level scaling across the keys: the level offset each note gets.</summary>
        static GraphView ScalingView(EditorContext ctx, int op, double width, double height)
        {
            var view = new GraphView(width, height, (dc, r, v) =>
            {
                int bp = ctx.Raw(Op(op, P.O1Break)), ld = ctx.Raw(Op(op, P.O1LDepth)), rd = ctx.Raw(Op(op, P.O1RDepth));
                int lc = ctx.Raw(Op(op, P.O1LCurve)), rc = ctx.Raw(Op(op, P.O1RCurve));
                double left = r.Left + 4, w = r.Width - 8, mid = r.Top + r.Height / 2, h = r.Height / 2 - 4;
                dc.DrawLine(Theme.Pen(Theme.Grid, 1, false), new Point(left, mid), new Point(left + w, mid));
                double bx = left + (bp + 21 - 21) / 99.0 * w;
                dc.DrawLine(Theme.Pen(Theme.Solid(ctx.Accent, 0x50), 1, false), new Point(bx, r.Top + 3), new Point(bx, r.Bottom - 3));
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    for (int note = 21; note <= 120; note++)
                    {
                        int s = Dx7.ScaleLevel(note, bp, ld, rd, lc, rc);
                        var p = new Point(left + (note - 21) / 99.0 * w, mid - Math.Clamp(s / 127.0, -1, 1) * h);
                        if (note == 21) c.BeginFigure(p, false, false);
                        else c.LineTo(p, true, false);
                    }
                }
                g.Freeze();
                dc.DrawGeometry(null, Theme.Pen(Theme.Solid(ctx.Accent), 1.5), g);
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
                name.Text = count == 0 ? "No bank loaded" : $"{machine.BankName} ({count})";
                voices.Text = count == 0 ? "Voices ▾" : "Choose a voice ▾";
                voices.InvalidateVisual();
            }
            Show();

            var load = new ActionButton("Load bank…", AccentColor, 132);
            load.Click += () =>
            {
                if (machine == null) return;
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Load a DX7 bank or voice", Filter = "DX7 SysEx (*.syx)|*.syx|All files (*.*)|*.*" };
                if (dialog.ShowDialog() != true) return;
                try
                {
                    machine.LoadBank(dialog.FileName);
                    Show();
                    if (machine.Bank.Count == 1) Apply(ctx, machine.Bank[0]);
                }
                catch (Exception e)
                {
                    MessageBox.Show($"Couldn't load {System.IO.Path.GetFileName(dialog.FileName)}: {e.Message}", "Neo FM6", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
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
                menu.IsOpen = true;
            };

            var export = new ActionButton("Export voice…", AccentColor, 132);
            export.Click += () =>
            {
                if (machine == null) return;
                var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Export as a DX7 voice", Filter = "DX7 SysEx (*.syx)|*.syx", FileName = "Neo FM6 voice.syx" };
                if (dialog.ShowDialog() != true) return;
                try
                {
                    machine.ExportVoice(dialog.FileName, System.IO.Path.GetFileNameWithoutExtension(dialog.FileName).ToUpperInvariant());
                }
                catch (Exception e)
                {
                    MessageBox.Show($"Couldn't write {dialog.FileName}: {e.Message}", "Neo FM6", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
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
                : base(width, height, (dc, r, v) => Draw(dc, r, v, ctx, machine), animate: true)
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

            static void Draw(DrawingContext dc, Rect r, Visual v, EditorContext ctx, NeoFm6Machine machine)
            {
                var alg = Algorithms.Get(ctx.Raw(P.Algorithm) + 1);
                var pos = Algorithms.Layout(alg);
                int columns = 1, rows = 1;
                foreach (var (c, row) in pos)
                {
                    columns = Math.Max(columns, c + 1);
                    rows = Math.Max(rows, row + 1);
                }
                const double bw = 24, bh = 18, gx = 32, gy = 27;
                double x0 = r.Left + (r.Width - ((columns - 1) * gx + bw)) / 2;
                double yBottom = r.Bottom - 22;
                Rect Box(int op) => new Rect(x0 + pos[op].Column * gx, yBottom - bh - pos[op].Row * gy, bw, bh);
                var accent = Theme.Solid(ctx.Accent);
                var line = Theme.Pen(Theme.Solid(ctx.Accent, 0xA0), 1.2);

                Theme.DrawText(dc, v, $"Algorithm {alg.Number}", r.Left + 6, r.Top + 4, 11, Theme.DimText, true);

                // Output bus under the carriers.
                double busY = r.Bottom - 12;
                double busLeft = double.MaxValue, busRight = double.MinValue;
                for (int op = 0; op < 6; op++)
                {
                    if (!alg.IsCarrier(op)) continue;
                    var b = Box(op);
                    double cx = b.X + bw / 2;
                    dc.DrawLine(line, new Point(cx, b.Bottom), new Point(cx, busY));
                    busLeft = Math.Min(busLeft, cx);
                    busRight = Math.Max(busRight, cx);
                }
                dc.DrawLine(line, new Point(busLeft, busY), new Point(busRight, busY));

                // Modulation links.
                for (int to = 0; to < 6; to++)
                    for (int from = 0; from < 6; from++)
                    {
                        if ((alg.Modulators[to] & (1 << from)) == 0) continue;
                        var a = Box(from);
                        var b = Box(to);
                        dc.DrawLine(line, new Point(a.X + bw / 2, a.Bottom), new Point(b.X + bw / 2, b.Top));
                    }

                // Feedback: a loop on the right of the operator (or from one operator back up to another).
                {
                    var f = Box(alg.FeedbackFrom);
                    var t = Box(alg.FeedbackTo);
                    double xr = Math.Max(f.Right, t.Right) + 5;
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(f.Right, f.Y + bh / 2), false, false);
                        c.LineTo(new Point(xr, f.Y + bh / 2), true, false);
                        c.LineTo(new Point(xr, t.Y - 5), true, false);
                        c.LineTo(new Point(t.X + bw / 2, t.Y - 5), true, false);
                        c.LineTo(new Point(t.X + bw / 2, t.Y), true, false);
                    }
                    g.Freeze();
                    int fb = ctx.Raw(P.Feedback);
                    dc.DrawGeometry(null, Theme.Pen(Theme.Solid(ctx.Accent, (byte)(fb == 0 ? 0x40 : 0xC0)), fb == 0 ? 1 : 1.6), g);
                }

                // Operators, lit by their level.
                for (int op = 0; op < 6; op++)
                {
                    var b = Box(op);
                    bool on = ctx.Raw(Op(op, P.O1On)) != 0;
                    float level = machine?.Engine.OpLevel(op) ?? 0;
                    double glow = Math.Clamp((DspMath.GainToDb(Math.Max(1e-9f, level / 2f)) + 60) / 60, 0, 1);
                    byte alpha = (byte)(alg.IsCarrier(op) ? 0x40 + 0x90 * glow : 0x10 + 0xA0 * glow);
                    dc.DrawRoundedRectangle(Theme.Solid(ctx.Accent, alpha), Theme.Pen(on ? accent : Theme.PanelEdge, 1.2), b, 3, 3);
                    Theme.TextCentered(dc, v, (op + 1).ToString(), b.X + bw / 2, b.Y + 1, 11.5, on ? Theme.Text : Theme.DimText, true);
                }
            }
        }
    }
}
