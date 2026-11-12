#nullable disable
// Dye Essence's editor (docs/specs/dye-essence.md §8), in pages, so that it fits a laptop's screen with
// the machine keyboard under it (a 1280 × 720 screen less its task bar): Oscillators (the three engine
// slots, each with a 3-D view of its table, the frame playing now drawn bright, and a user-table
// loader), Mix & filters (the sub and the noise, the two filters and their routing, the amp and filter
// envelopes, the voice, the output) and Modulation (the two modulation envelopes and the four LFOs,
// each on a tab, the LFOs' shapes drawn in place; the macros; the route list: DyeEssenceModEditor.cs).
// One source for WPF and Avalonia (the kit's names; the views draw on INeoCanvas; the kit's Pages).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
using System.Windows.Controls;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.DyeEssence
{
    internal sealed class DyeEssenceEditor : NeoEditor
    {
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xE0, 0x6C, 0xD8);
        const double D = 34;

        /// <summary>
        /// Every page's size: with the header and the tabs, the editor fits a 1280 × 680 work area (a 1280 × 720 screen
        /// less its task bar) with the keyboard shown under it and the window's frame: 1264 × 534 at most.
        /// </summary>
        internal const double PageWidth = 1244, PageHeight = 460;

        /// <summary>The Modulation page's parts: an envelope's or an LFO's page, the macros beside the envelopes, the route list.</summary>
        const double ModPageWidth = 592, MacrosWidth = 164, RoutesWidth = 400, LfoViewWidth = ModPageWidth + MacrosWidth - 250;

        /// <summary>The pages, in order.</summary>
        internal static readonly string[] PageNames = { "Oscillators", "Mix & filters", "Modulation" };

        protected override string Title => "Dye Essence";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as DyeEssenceMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);
            // Knobs named for what they are within their section ("position" in Osc A, not "A position"): narrower.
            UniformGrid Knobs(int columns, params (int Index, string Label)[] knobs)
            {
                var g = new UniformGrid { Columns = columns };
                foreach (var (index, label) in knobs) g.Children.Add(new ParamKnob(ctx, index, label, D));
                return g;
            }

            UIElement Slot(int slot)
            {
                int b = slot == 0 ? P.AType : slot == 1 ? P.BType : P.CType;
                int I(int aParam) => b + (aParam - P.AType);
                // The Analog engine's parameters (appended in P1b, a block of their own per slot).
                int a = slot == 0 ? P.AShape : slot == 1 ? P.BShape : P.CShape;
                int J(int aParam) => a + (aParam - P.AShape);
                var userName = new TextBlock
                {
                    Foreground = Theme.DimText,
                    FontFamily = Theme.FontFamily,
                    FontSize = 11,
                    Width = 120,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Text = UserLabel(machine, slot),
                };
                Poll.Every(userName, 0.5, () =>
                {
                    string label = UserLabel(machine, slot);
                    if (userName.Text != label) userName.Text = label;
                });
                var load = new ActionButton("Load…", AccentColor, 60);
                load.Click += () =>
                {
                    if (machine == null) return;
                    EditorFiles.Open(load, "Load a wavetable", "Wavetables (*.wav)|*.wav|All files (*.*)|*.*", file =>
                    {
                        try
                        {
                            machine.LoadUserTable(slot, file);
                            userName.Text = UserLabel(machine, slot);
                            ctx.Set(I(P.ATable), DyeEssenceMachine.UserChoice);
                            ctx.RaiseChanged();
                        }
                        catch (Exception e)
                        {
                            MessageBox.Show($"Couldn't load {System.IO.Path.GetFileName(file)}: {e.Message}", "Dye Essence", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    });
                };
                return S("Osc " + DyeEssenceMachine.SlotNames[slot], Layout.Column(
                    Layout.Row(new ParamDropdown(ctx, I(P.AType), width: 96), Layout.Gap(6), new ParamDropdown(ctx, I(P.ATable), width: 112),
                               Layout.Gap(8), Layout.Column(Layout.Gap(1, 16), load)),
                    Layout.Gap(1, 2),
                    userName,
                    Layout.Gap(1, 4),
                    TableView(ctx, machine, slot, 6 * 50, 76),
                    Layout.Gap(1, 2),
                    Knobs(6, (I(P.APos), "position"), (I(P.AOct), "octave"), (I(P.ASemi), "semi"), (I(P.AFine), "fine"), (I(P.ALevel), "level"), (I(P.APan), "pan"),
                        (I(P.AUnison), "unison"), (I(P.ADetune), "detune"), (I(P.ASpread), "spread"), (I(P.APhase), "phase"), (I(P.ARandom), "random"), (I(P.AToF2), "→ F2")),
                    Layout.Gap(1, 2),
                    Layout.Row(new ParamDropdown(ctx, I(P.AWarp1), width: 104), Layout.Gap(4), new ParamKnob(ctx, I(P.AWarp1Amt), "amount", 30),
                               Layout.Gap(10), new ParamDropdown(ctx, I(P.AWarp2), width: 104), Layout.Gap(4), new ParamKnob(ctx, I(P.AWarp2Amt), "amount", 30)),
                    Layout.Row(new ParamDropdown(ctx, I(P.AMod), width: 104), Layout.Gap(10), new ParamDropdown(ctx, J(P.AShape), width: 92),
                               Layout.Gap(4), new ParamKnob(ctx, J(P.APw), "width", 30), Layout.Gap(4), new ParamKnob(ctx, J(P.ADrift), "drift", 30))));
            }

            UIElement Filter(int f)
            {
                int b = f == 0 ? P.F1Type : P.F2Type;
                int I(int f1Param) => b + (f1Param - P.F1Type);
                return S("Filter " + (f + 1), Layout.Column(
                    new ParamDropdown(ctx, I(P.F1Type), width: 112),
                    Layout.Gap(1, 4),
                    Layout.Row(new ParamKnob(ctx, I(P.F1Cutoff), diameter: 50), new ParamKnob(ctx, I(P.F1Reso), diameter: 50)),
                    Knobs(4, (I(P.F1Drive), "drive"), (I(P.F1Env), "env"), (I(P.F1Key), "key"), (I(P.F1Vel), "vel"))));
            }

            var sub = S("Sub", Layout.Column(
                new ParamChoice(ctx, P.SubShape, cellWidth: 46),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.SubOct, cellWidth: 46),
                Knobs(3, (P.SubLevel, "level"), (P.SubPan, "pan"), (P.SubToF2, "→ F2"))));
            var noise = S("Noise", Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.NoiseColor, "color", D), Layout.Gap(8), new ParamKnob(ctx, P.NoiseLevel, "level", D)),
                Layout.Gap(1, 6),
                Layout.Row(new ParamKnob(ctx, P.NoisePan, "pan", D), Layout.Gap(8), new ParamKnob(ctx, P.NoiseToF2, "→ F2", D))));
            var routing = S("Routing", Layout.Column(new ParamKnob(ctx, P.Routing, diameter: 50)));
            var voice = S("Voice", Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.VoiceMode, cellWidth: 50), Layout.Gap(6), new ParamChoice(ctx, P.GlideMode, cellWidth: 50)),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.Quality, cellWidth: 84),
                Build.Knobs(ctx, 5, D, P.Polyphony, P.Glide, P.BendRange, P.VelAmp, P.Volume)));
            var meter = new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 120);

            UIElement MakeEnv(string title, int a, int d, int s, int r) => S(title, Layout.Column(
                Build.Envelope(ctx, a, d, s, r, 4 * 60, 44),
                Build.Knobs(ctx, 4, D, a, d, s, r)));

            // An envelope's page: its picture beside its knobs (times over the curves and the sync).
            UIElement ModEnv(int e)
            {
                int b = e == 0 ? P.E1Delay : P.E2Delay;
                int I(int p) => b + (p - P.E1Delay);
                return Layout.Row(
                    ModEnvView(ctx, b, 196, 124),
                    Layout.Gap(8),
                    Layout.Column(
                        Knobs(6, (I(P.E1Delay), "delay"), (I(P.E1A), "attack"), (I(P.E1Hold), "hold"), (I(P.E1D), "decay"), (I(P.E1S), "sustain"), (I(P.E1R), "release")),
                        Layout.Row(Knobs(3, (I(P.E1ACurve), "A curve"), (I(P.E1DCurve), "D curve"), (I(P.E1RCurve), "R curve")), Layout.Gap(12),
                                   Layout.Column(Layout.Gap(1, 10), new ParamChoice(ctx, I(P.E1Sync), cellWidth: 56)))));
            }
            // An LFO's page: its shape, sync and retrig, and its knobs, beside its shape (drawn ones edited in place).
            UIElement Lfo(int l)
            {
                int b = P.L1Shape + l * (P.L2Shape - P.L1Shape);
                int I(int p) => b + (p - P.L1Shape);
                var edit = new LfoShapeEdit(ctx, machine, l);
                var input = new BreakpointInput(edit);
                var view = new InteractiveGraph(LfoViewWidth, 112, (g, r) => BreakpointPainter.Draw(g, r, edit, input, AccentColor), input)
                {
                    ToolTip = "The LFO's shape. Drawn: drag a point; press elsewhere to add one; double-click one to remove it; drag a segment's middle " +
                              "(or turn the wheel over it) to bend it; Shift for finer moves. A built-in shape: press it to draw from it",
                };
                ctx.Changed += view.InvalidateVisual;
                int seen = machine?.ShapesVersion ?? 0;
                Poll.Every(view, 0.3, () =>
                {
                    int now = machine?.ShapesVersion ?? 0;
                    if (now == seen) return;
                    seen = now;
                    view.InvalidateVisual();
                });
                return Layout.Row(
                    Layout.Column(
                        Layout.Row(new ParamDropdown(ctx, I(P.L1Shape), "shape", 92), Layout.Gap(4), new ParamDropdown(ctx, I(P.L1Sync), "sync", 66),
                                   Layout.Gap(6), new ParamChoice(ctx, I(P.L1Retrig), "Retrig", 30)),
                        Layout.Gap(1, 4),
                        Knobs(3, (I(P.L1Rate), "rate"), (I(P.L1Phase), "phase"), (I(P.L1Fade), "fade"))),
                    Layout.Gap(8),
                    view);
            }

            var envelopes = new Pages(AccentColor, ModPageWidth, 128, "Dye Essence/envelopes");
            for (int e = 0; e < 2; e++) envelopes.Add("Mod env " + (e + 1), ModEnv(e));
            var lfos = new Pages(AccentColor, ModPageWidth + MacrosWidth, 116, "Dye Essence/LFOs");
            for (int l = 0; l < 4; l++) lfos.Add("LFO " + (l + 1), Lfo(l));

            var pages = new Pages(AccentColor, PageWidth, PageHeight, "Dye Essence");
            pages.Add(PageNames[0], Layout.Row(Slot(0), Slot(1), Slot(2)));
            pages.Add(PageNames[1], Layout.Column(
                Layout.Row(sub, noise, Filter(0), Filter(1), routing),
                Layout.Row(MakeEnv("Amp envelope", P.AmpA, P.AmpD, P.AmpS, P.AmpR),
                           MakeEnv("Filter envelope", P.FltA, P.FltD, P.FltS, P.FltR),
                           voice,
                           S("Out", meter))));
            pages.Add(PageNames[2], Layout.Row(
                Layout.Column(
                    Layout.Row(S("Envelopes", envelopes), S("Macros", Knobs(2, (P.M1, "Macro 1"), (P.M2, "Macro 2"), (P.M3, "Macro 3"), (P.M4, "Macro 4")))),
                    S("LFOs", lfos)),
                S("Routes", new RouteList(ctx, RoutesWidth))));
            return pages;
        }

        /// <summary>A modulation envelope's shape: delay, attack, hold, decay to the sustain, a stretch of sustain, release; each with its curve.</summary>
        static GraphView ModEnvView(EditorContext ctx, int delay, double width, double height)
        {
            int I(int p) => delay + (p - P.E1Delay);
            var view = new GraphView(width, height, (c, r) =>
            {
                double dl = ctx.Value(I(P.E1Delay)), a = ctx.Value(I(P.E1A)), h = ctx.Value(I(P.E1Hold)), d = ctx.Value(I(P.E1D)),
                       s = ctx.Value(I(P.E1S)), rl = ctx.Value(I(P.E1R));
                float ac = (float)ctx.Value(I(P.E1ACurve)), dc = (float)ctx.Value(I(P.E1DCurve)), rc = (float)ctx.Value(I(P.E1RCurve));
                // Widths grow with the square root of time (as the amp envelope's picture), so short and long both read.
                double wd = Math.Sqrt(dl), wa = Math.Sqrt(a), wh = Math.Sqrt(h), wdc = Math.Sqrt(d), ws = 0.3, wr = Math.Sqrt(rl);
                double total = Math.Max(1e-6, wd + wa + wh + wdc + ws + wr);
                double x0 = r.Left + 6, w = r.Width - 12, top = r.Top + 5, bottom = r.Bottom - 5, ht = bottom - top;
                double X(double t) => x0 + t / total * w;
                double Y(double level) => bottom - level * ht;
                var path = new NeoPath().MoveTo(X(0), Y(0)).LineTo(X(wd), Y(0));
                const int N = 24;
                for (int i = 1; i <= N; i++) path.LineTo(X(wd + wa * i / N), Y(ModEnvelope.Shape(i / (double)N, ac)));
                path.LineTo(X(wd + wa + wh), Y(1));
                for (int i = 1; i <= N; i++) path.LineTo(X(wd + wa + wh + wdc * i / N), Y(1 + (s - 1) * ModEnvelope.Shape(i / (double)N, dc)));
                double rs = wd + wa + wh + wdc + ws;
                path.LineTo(X(rs), Y(s));
                for (int i = 1; i <= N; i++) path.LineTo(X(rs + wr * i / N), Y(s * (1 - ModEnvelope.Shape(i / (double)N, rc))));
                var fill = new NeoPath();
                for (int i = 0; i < path.PointCount; i++) fill.LineTo(path.Point(i));
                fill.LineTo(X(total), Y(0)).Close();
                c.Path(fill, ctx.Accent.WithAlpha(0x30), NeoStroke.None);
                c.Path(path, NeoFill.None, new NeoStroke(ctx.Accent, 1.5));
            });
            ctx.Changed += view.InvalidateVisual;
            return view;
        }

        static string UserLabel(DyeEssenceMachine machine, int slot) =>
            string.IsNullOrEmpty(machine?.UserTableName(slot)) ? "No user table" : machine.UserTableName(slot);

        /// <summary>
        /// A slot's table in perspective: up to 16 of its frames from front (first) to back (last), and
        /// the frame its position plays now, bright.
        /// </summary>
        static GraphView TableView(EditorContext ctx, DyeEssenceMachine machine, int slot, double width, double height)
        {
            int b = slot == 0 ? P.AType : slot == 1 ? P.BType : P.CType;
            int tableParam = b + (P.ATable - P.AType), posParam = b + (P.APos - P.AType);
            TableArena cached = null;
            var back = new NeoPath();   // kept while the table and size stay: the backend builds it once
            double cachedWidth = 0, cachedHeight = 0;
            const int Shown = 16, Points = 64, Level = 6;   // level 6: 256 samples a frame, 16 harmonics
            int len = Wavetable.LevelLength(Level);

            return new GraphView(width, height, (g, r) =>
            {
                int choice = ctx.Raw(tableParam);
                bool isUser = choice >= TableBank.Count;
                var table = isUser ? machine?.Engine.UserTable(slot) : TableBank.Get(choice);
                if (table == null)
                {
                    g.Text(isUser ? "No user table: load a WAV" : "Building…", r.Width / 2, r.Height / 2 - 8, 11.5, Palette.DimText, align: NeoAlign.Center);
                    return;
                }
                double depthX = r.Width * 0.24, depthY = r.Height * 0.36;
                double frameWidth = r.Width - depthX - 12;
                double amp = (r.Height - depthY - 14) * 0.45;
                NeoPoint At(double depth, double t, double y) =>
                    new NeoPoint(r.Left + 6 + depth * depthX + t * frameWidth, r.Bottom - 7 - amp - depth * depthY - y * amp);
                float Sample(int frame, double t) => table.Data[TableArena.Offset(frame, Level) + 1 + Math.Min(len - 1, (int)(t * len))];

                if (!ReferenceEquals(table, cached) || cachedWidth != r.Width || cachedHeight != r.Height)
                {
                    back.Clear();
                    int shown = Math.Min(Shown, table.FrameCount);
                    for (int k = 0; k < shown; k++)
                    {
                        int frame = shown == 1 ? 0 : (int)Math.Round(k * (table.FrameCount - 1) / (double)(shown - 1));
                        double depth = table.FrameCount == 1 ? 0 : frame / (double)(table.FrameCount - 1);
                        for (int i = 0; i <= Points; i++)
                        {
                            double t = i / (double)Points;
                            var p = At(depth, t, Sample(frame, Math.Min(t, 0.999999)));
                            if (i == 0) back.MoveTo(p);
                            else back.LineTo(p);
                        }
                    }
                    cached = table;
                    cachedWidth = r.Width;
                    cachedHeight = r.Height;
                }
                g.Path(back, NeoFill.None, new NeoStroke(ctx.Accent.WithAlpha(0x48), 1));

                double pos = ctx.Value(posParam);
                double framePos = pos * (table.FrameCount - 1);
                int f0 = Math.Min(table.FrameCount - 1, (int)framePos), f1 = Math.Min(table.FrameCount - 1, f0 + 1);
                double ff = framePos - f0;
                var now = new NeoPath();
                for (int i = 0; i <= len; i += 2)
                {
                    double t = i / (double)len;
                    double tt = Math.Min(t, 0.999999);
                    double y = Sample(f0, tt) * (1 - ff) + Sample(f1, tt) * ff;
                    if (i == 0) now.MoveTo(At(pos, t, y));
                    else now.LineTo(At(pos, t, y));
                }
                g.Path(now, NeoFill.None, new NeoStroke(ctx.Accent, 2));
                g.Text($"{table.Name} · frame {(int)Math.Round(framePos) + 1} of {table.FrameCount}", r.Left + 7, r.Top + 4, 11, Palette.DimText);
            });
        }
    }
}
