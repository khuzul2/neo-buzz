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
using System.Windows.Controls.Primitives;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.DyeEssence
{
    internal sealed class DyeEssenceEditor : NeoEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xE0, 0x6C, 0xD8));
        const double D = 34;

        /// <summary>
        /// Every page's size: with the header and the tabs, the editor fits a 1280 × 680 work area (a 1280 × 720 screen
        /// less its task bar) with the keyboard shown under it and the window's frame: 1264 × 534 at most. A slot shows
        /// the rows of its engine only (wavetable and analog, sample, granular), so more engines do not make it taller.
        /// </summary>
        internal const double PageWidth = 1244, PageHeight = 460;

        /// <summary>The Modulation page's parts: an envelope's or an LFO's page, the macros beside the envelopes, the route list.</summary>
        const double ModPageWidth = 592, MacrosWidth = 164, RoutesWidth = 400, LfoViewWidth = ModPageWidth + MacrosWidth - 250;

        /// <summary>The pages, in order.</summary>
        internal static readonly string[] PageNames = { "Oscillators", "Mix & filters", "Modulation", "Arp & Seq", "Effects" };

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
                // The Harmonic engine's parameters (appended in P3a, a block of their own per slot).
                int hm = slot == 0 ? P.AHSet : slot == 1 ? P.BHSet : P.CHSet;
                int H(int aParam) => hm + (aParam - P.AHSet);
                // The Modal engine's parameters (appended in P3b, a block of their own per slot).
                int md = slot == 0 ? P.AMExc : slot == 1 ? P.BMExc : P.CMExc;
                int M(int aParam) => md + (aParam - P.AMExc);
                // The Sample and Granular engines' parameters (appended in P2, a block of their own per slot).
                int sm = slot == 0 ? P.ASmpSrc : slot == 1 ? P.BSmpSrc : P.CSmpSrc;
                int K(int aParam) => sm + (aParam - P.ASmpSrc);
                // The Spectral engine's parameters (appended in P5a, a block of their own per slot).
                int sp = slot == 0 ? P.ASpSpeed : slot == 1 ? P.BSpSpeed : P.CSpSpeed;
                int Sp(int aParam) => sp + (aParam - P.ASpSpeed);
                bool Streams() => ctx.Raw(I(P.AType)) == (int)SlotType.Sample || ctx.Raw(I(P.AType)) == (int)SlotType.Granular || ctx.Raw(I(P.AType)) == (int)SlotType.Spectral;
                var userName = new TextBlock
                {
                    Foreground = Theme.DimText,
                    FontFamily = Theme.FontFamily,
                    FontSize = 11,
                    Width = 120,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Text = UserLabel(machine, slot, Streams()),
                };
                Poll.Every(userName, 0.5, () =>
                {
                    string label = UserLabel(machine, slot, Streams());
                    if (userName.Text != label) userName.Text = label;
                });
                ActionButton LoadButton()
                {
                var btn = new ActionButton("Load…", AccentColor, 60);
                btn.Click += () =>
                {
                    if (machine == null) return;
                    EditorFiles.Open(btn, "Load a wavetable", "Wavetables (*.wav)|*.wav|All files (*.*)|*.*", file =>
                    {
                        try
                        {
                            // One history step: the table, and the parameters that choose it.
                            StateEdit.Run(ctx, "Load wavetable", machine, m => m.MachineState, (m, s) => m.MachineState = s, () =>
                            {
                                machine.LoadUserTable(slot, file);
                                userName.Text = UserLabel(machine, slot, Streams());
                                ctx.Set(I(P.ATable), DyeEssenceMachine.UserChoice);
                                // The Harmonic engine reads the table's first and last frames as its two sets.
                                if (ctx.Raw(I(P.AType)) == (int)SlotType.Harmonic)
                                {
                                    ctx.Set(H(P.AHSet), (int)HarmonicSet.User);
                                    ctx.Set(H(P.AHSet2), (int)HarmonicSet.User);
                                }
                            });
                            ctx.RaiseChanged();
                        }
                        catch (Exception e)
                        {
                            MessageBox.Show($"Couldn't load {System.IO.Path.GetFileName(file)}: {e.Message}", "Dye Essence", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    });
                };
                return btn;
                }
                var load = LoadButton();
                var load2 = LoadButton();
                ActionButton SampleButton()
                {
                    var btn = new ActionButton("Sample…", AccentColor, 64);
                    btn.Click += () =>
                    {
                    if (machine == null) return;
                    EditorFiles.Open(btn, "Load a sample", "Samples (*.wav)|*.wav|All files (*.*)|*.*", file =>
                    {
                        try
                        {
                            StateEdit.Run(ctx, "Load sample", machine, m => m.MachineState, (m, s) => m.MachineState = s, () =>
                            {
                                machine.LoadUserSample(slot, file);
                                ctx.Set(K(P.ASmpSrc), DyeEssenceMachine.UserSampleChoice);
                                if (!Streams()) ctx.Set(I(P.AType), (int)SlotType.Sample);
                                userName.Text = UserLabel(machine, slot, Streams());
                            });
                            ctx.RaiseChanged();
                        }
                        catch (Exception e)
                        {
                            MessageBox.Show($"Couldn't load {System.IO.Path.GetFileName(file)}: {e.Message}", "Dye Essence", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    });
                    };
                    return btn;
                }
                var loadSample = SampleButton();
                var loadSample2 = SampleButton();
                var loadSample3 = SampleButton();
                var wavetablePanel = Layout.Column(
                    Layout.Row(new ParamDropdown(ctx, I(P.AWarp1), width: 104), Layout.Gap(4), new ParamKnob(ctx, I(P.AWarp1Amt), "amount", 30),
                               Layout.Gap(10), new ParamDropdown(ctx, I(P.AWarp2), width: 104), Layout.Gap(4), new ParamKnob(ctx, I(P.AWarp2Amt), "amount", 30)),
                    Layout.Row(new ParamDropdown(ctx, I(P.AMod), width: 104), Layout.Gap(10), new ParamDropdown(ctx, J(P.AShape), width: 92),
                               Layout.Gap(4), new ParamKnob(ctx, J(P.APw), "width", 30), Layout.Gap(4), new ParamKnob(ctx, J(P.ADrift), "drift", 30)));
                // Sample: the sample, its loop and direction, then the loop's points. Granular: the sample and the grain's shape, then the cloud.
                var samplePanel = Layout.Column(
                    Layout.Row(new ParamDropdown(ctx, K(P.ASmpSrc), width: 90), Layout.Gap(4), loadSample, Layout.Gap(6),
                               new ParamDropdown(ctx, K(P.ASmpLoop), width: 84), Layout.Gap(6), new ParamChoice(ctx, K(P.ASmpRev), "reverse", 30)),
                    Layout.Gap(1, 4),
                    Knobs(4, (K(P.ASmpRoot), "root"), (K(P.ASmpLoopStart), "loop in"), (K(P.ASmpLoopEnd), "loop out"), (K(P.ASmpFade), "fade")));
                var granularPanel = Layout.Column(
                    Layout.Row(new ParamDropdown(ctx, K(P.ASmpSrc), width: 90), Layout.Gap(4), loadSample2, Layout.Gap(6),
                               new ParamDropdown(ctx, K(P.AGrShape), width: 84), Layout.Gap(6), new ParamKnob(ctx, K(P.AGrWidth), "width", 30)),
                    Layout.Gap(1, 4),
                    Knobs(6, (K(P.AGrSize), "size"), (K(P.AGrRate), "rate"), (K(P.AGrSpray), "spray"), (K(P.AGrScan), "scan"),
                        (K(P.AGrJitter), "pitch"), (K(P.AGrRev), "back")));
                var harmonicPanel = Layout.Column(
                    Layout.Row(new ParamDropdown(ctx, H(P.AHSet), "from", 112), Layout.Gap(8), new ParamDropdown(ctx, H(P.AHSet2), "to", 112),
                               Layout.Gap(8), Layout.Column(Layout.Gap(1, 16), load2)),
                    Layout.Gap(1, 4),
                    Knobs(5, (H(P.AHPartials), "partials"), (H(P.AHTilt), "tilt"), (H(P.AHOddEven), "odd/even"), (H(P.AHStretch), "stretch"), (H(P.AHDamp), "damp")));
                var modalPanel = Layout.Column(
                    Layout.Row(new ParamDropdown(ctx, M(P.AMExc), "exciter", 112), Layout.Gap(8), new ParamDropdown(ctx, M(P.AMGeom), "body", 112)),
                    Layout.Gap(1, 4),
                    Knobs(6, (M(P.AMForce), "force"), (M(P.AMModes), "modes"), (M(P.AMMat), "material"), (M(P.AMRing), "ring"), (M(P.AMHfDamp), "hf damp"), (M(P.AMSize), "size")));
                // Spectral: the sample, the speed, key tracking and partials (reverse and the loop are parameters, not knobs here: the row has no room), then the shaping of what is resynthesised.
                var spectralPanel = Layout.Column(
                    Layout.Row(new ParamDropdown(ctx, K(P.ASmpSrc), width: 90), Layout.Gap(4), loadSample3,
                               Layout.Gap(6), new ParamKnob(ctx, Sp(P.ASpSpeed), "speed", 30), Layout.Gap(4), new ParamKnob(ctx, Sp(P.ASpKey), "key", 30),
                               Layout.Gap(4), new ParamKnob(ctx, Sp(P.ASpPartials), "parts", 30)),
                    Layout.Gap(1, 4),
                    Knobs(6, (Sp(P.ASpFormant), "formant"), (Sp(P.ASpSnap), "snap"), (Sp(P.ASpBalance), "tone"), (Sp(P.ASpBlur), "blur"),
                        (Sp(P.ASpTilt), "tilt"), (Sp(P.ASpStretch), "stretch")));
                void ShowEngine()
                {
                    int type = ctx.Raw(I(P.AType));
                    wavetablePanel.Visibility = type < (int)SlotType.Sample ? Visibility.Visible : Visibility.Collapsed;
                    samplePanel.Visibility = type == (int)SlotType.Sample ? Visibility.Visible : Visibility.Collapsed;
                    granularPanel.Visibility = type == (int)SlotType.Granular ? Visibility.Visible : Visibility.Collapsed;
                    harmonicPanel.Visibility = type == (int)SlotType.Harmonic ? Visibility.Visible : Visibility.Collapsed;
                    modalPanel.Visibility = type == (int)SlotType.Modal ? Visibility.Visible : Visibility.Collapsed;
                    spectralPanel.Visibility = type == (int)SlotType.Spectral ? Visibility.Visible : Visibility.Collapsed;
                }
                ShowEngine();
                ctx.Changed += ShowEngine;
                return S("Osc " + DyeEssenceMachine.SlotNames[slot], Layout.Column(
                    Layout.Row(new ParamDropdown(ctx, I(P.AType), width: 96), Layout.Gap(6), new ParamDropdown(ctx, I(P.ATable), width: 112),
                               Layout.Gap(8), Layout.Column(Layout.Gap(1, 16), load)),
                    Layout.Gap(1, 2),
                    userName,
                    Layout.Gap(1, 4),
                    SlotView(ctx, machine, slot, 6 * 50, 76),
                    Layout.Gap(1, 2),
                    Knobs(6, (I(P.APos), "position"), (I(P.AOct), "octave"), (I(P.ASemi), "semi"), (I(P.AFine), "fine"), (I(P.ALevel), "level"), (I(P.APan), "pan"),
                        (I(P.AUnison), "unison"), (I(P.ADetune), "detune"), (I(P.ASpread), "spread"), (I(P.APhase), "phase"), (I(P.ARandom), "random"), (I(P.AToF2), "→ F2")),
                    Layout.Gap(1, 2),
                    // The engine's own rows: one set shows at a time (the type picks it), so the slot keeps one height.
                    wavetablePanel, samplePanel, granularPanel, harmonicPanel, modalPanel, spectralPanel));
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
            pages.Add(PageNames[3], ArpPage(ctx, machine, Knobs, S));
            pages.Add(PageNames[4], new FxPage(ctx, machine, S).Build());
            return pages;
        }

        /// <summary>
        /// The arpeggiator, live MPE and the step sequencer (§4.9): the arpeggiator's mode, step and notes; MPE's switch and
        /// range; the sequencer's mode, length, step and the steps themselves, one lane at a time.
        /// </summary>
        static UIElement ArpPage(EditorContext ctx, DyeEssenceMachine machine, Func<int, (int Index, string Label)[], UniformGrid> knobs, Func<string, UIElement, UIElement> section)
        {
            var arp = section("Arpeggiator", Layout.Column(
                new ParamDropdown(ctx, P.ArpMode, width: 120),
                Layout.Gap(1, 4),
                Layout.Row(new ParamDropdown(ctx, P.ArpRate, "rate", 56), Layout.Gap(4), new ParamDropdown(ctx, P.ArpFeel, "feel", 72)),
                Layout.Gap(1, 4),
                knobs(3, new[] { (P.ArpOct, "octaves"), (P.ArpGate, "gate"), (P.ArpSwing, "swing") }),
                Layout.Gap(1, 2),
                Layout.Row(new ParamChoice(ctx, P.ArpLatch, "Latch", 30), Layout.Gap(8), new ParamKnob(ctx, P.ArpSeed, "seed", D))));
            var mpe = section("Live MPE", Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.MpeMode, "MPE", 30), Layout.Gap(8), new ParamKnob(ctx, P.MpeBend, "range", D)),
                Layout.Gap(1, 4),
                new TextBlock { Foreground = Theme.DimText, FontFamily = Theme.FontFamily, FontSize = 11, Width = 232, TextWrapping = TextWrapping.Wrap, Text = "Per-note bend, pressure and slide, as the sources MPE bend, MPE pressure and MPE slide" }));
            var edit = new SeqEdit(ctx, machine);
            var lanes = new Pages(AccentColor, StepsWidth, StepsHeight, "Dye Essence/steps");
            string[] laneNames = { "Note", "Velocity", "Gate", "Tie", "Probability" };
            string[] laneHelp =
            {
                "Each step's note, in semitones from the key held (the middle is 0). Drag across the bars; the wheel moves a step by one; double-click puts it back to 0",
                "Each step's velocity; 0 is a rest. Drag across the bars; the wheel moves a step by 5 (Shift: 1)",
                "How much of its step each note sounds. Drag across the bars; the wheel moves a step by 5 (Shift: 1)",
                "A tied step holds into the next, which then plays legato (the same note just goes on). Press a step to tie or untie it; drag across to do the same to more",
                "The chance each step plays (seeded: the same seed plays the same notes). Drag across the bars; the wheel moves a step by 5 (Shift: 1)",
            };
            for (int i = 0; i < laneNames.Length; i++)
            {
                var lane = (SeqLane)i;
                var input = new SeqStepInput(edit, lane);
                var view = new InteractiveGraph(StepsWidth, StepsHeight, (g, r) => SeqStepPainter.Draw(g, r, edit, input, lane, AccentColor), input) { ToolTip = laneHelp[i] };
                ctx.Changed += view.InvalidateVisual;
                int seen = machine?.ShapesVersion ?? 0;
                Poll.Every(view, 0.3, () =>
                {
                    int now = machine?.ShapesVersion ?? 0;
                    if (now == seen) return;
                    seen = now;
                    view.InvalidateVisual();
                });
                lanes.Add(laneNames[i], view);
            }
            var seq = section("Sequencer", Layout.Column(
                Layout.Row(
                    new ParamDropdown(ctx, P.SeqMode, width: 80),
                    Layout.Gap(8), new ParamDropdown(ctx, P.SeqRate, "rate", 56),
                    Layout.Gap(4), new ParamDropdown(ctx, P.SeqFeel, "feel", 72),
                    Layout.Gap(12), new ParamKnob(ctx, P.SeqLength, "length", D),
                    Layout.Gap(8), new ParamKnob(ctx, P.SeqSwing, "swing", D),
                    Layout.Gap(8), new ParamKnob(ctx, P.SeqSlew, "slew", D)),
                Layout.Gap(1, 6),
                lanes));
            return Layout.Row(Layout.Column(arp, mpe), seq);
        }

        /// <summary>The step view's size: 32 steps across the page beside the arpeggiator's column.</summary>
        const double StepsWidth = 900, StepsHeight = 250;

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

        static string UserLabel(DyeEssenceMachine machine, int slot, bool sample = false) =>
            sample ? string.IsNullOrEmpty(machine?.UserSampleName(slot)) ? "No user sample" : machine.UserSampleName(slot)
            : string.IsNullOrEmpty(machine?.UserTableName(slot)) ? "No user table" : machine.UserTableName(slot);

        /// <summary>
        /// A slot's picture: the wavetable in perspective, or, for the Sample and Granular engines, the sample's
        /// outline with where it starts (a marker; Sample), its loop (shaded) or where grains are taken from (a band
        /// as wide as the spray; Granular).
        /// </summary>
        static GraphView SlotView(EditorContext ctx, DyeEssenceMachine machine, int slot, double width, double height)
        {
            var table = TableDraw(ctx, machine, slot);
            int b = slot == 0 ? P.AType : slot == 1 ? P.BType : P.CType;
            int typeParam = b, srcParam = slot == 0 ? P.ASmpSrc : slot == 1 ? P.BSmpSrc : P.CSmpSrc;
            int posParam = b + (P.APos - P.AType);
            int K(int aParam) => srcParam + (aParam - P.ASmpSrc);
            int hb = slot == 0 ? P.AHSet : slot == 1 ? P.BHSet : P.CHSet;
            int H(int aParam) => hb + (aParam - P.AHSet);
            // The Harmonic engine: a line for each partial (up to 64) at its pitch (stretch moves them), as tall as its level.
            void HarmonicDraw(INeoCanvas g, NeoRect r)
            {
                int n = Math.Min(64, (int)ctx.Value(H(P.AHPartials)));
                int fullN = (int)ctx.Value(H(P.AHPartials));
                float tilt = (float)ctx.Value(H(P.AHTilt)), oddEven = (float)ctx.Value(H(P.AHOddEven)) / 100f, stretch = (float)ctx.Value(H(P.AHStretch)) / 100f;
                float m = (float)ctx.Value(posParam);
                float[] Set(int param)
                {
                    int choice = ctx.Raw(param);
                    var user = machine?.Engine.UserPartials(slot);
                    return choice == (int)HarmonicSet.User ? user?[param == H(P.AHSet) ? 0 : 1] ?? HarmonicSets.Get(0) : HarmonicSets.Get(choice);
                }
                var a = Set(H(P.AHSet)); var c = Set(H(P.AHSet2));
                var level = new double[n];
                double top = 1e-9;
                for (int j = 0; j < n; j++)
                {
                    level[j] = Math.Abs((a[j] + (c[j] - a[j]) * m) * HarmonicSets.Base(j + 1, fullN, tilt, oddEven));
                    top = Math.Max(top, level[j]);
                }
                double x0 = r.Left + 6, w = r.Width - 12, bottom = r.Bottom - 5, ht = r.Height - 24;
                double reach = HarmonicSets.Ratio(n, stretch);
                g.Line(x0, bottom, x0 + w, bottom, new NeoStroke(Palette.DimText.WithAlpha(0x60), 1));
                for (int j = 0; j < n; j++)
                {
                    double x = x0 + HarmonicSets.Ratio(j + 1, stretch) / reach * (w - 2);
                    g.Line(x, bottom, x, bottom - level[j] / top * ht, new NeoStroke(ctx.Accent, w / n > 5 ? 3 : 1.6));
                }
                g.Text($"{HarmonicSets.Names[ctx.Raw(H(P.AHSet))]} → {HarmonicSets.Names[ctx.Raw(H(P.AHSet2))]} · {fullN} partials", r.Left + 7, r.Top + 4, 11, Palette.DimText);
            }
            int mb = slot == 0 ? P.AMExc : slot == 1 ? P.BMExc : P.CMExc;
            int Mo(int aParam) => mb + (aParam - P.AMExc);
            // The Modal engine: a line for each mode (up to 64) at its place in the body's spacing (the material's stiffness moves them), as tall as the
            // level it is struck at (where on the body, how the body radiates); the bright part is what is left a quarter second later (the ring and the loss).
            void ModalDraw(INeoCanvas g, NeoRect r)
            {
                int body = Math.Clamp(ctx.Raw(Mo(P.AMGeom)), 0, ModalBodies.Names.Length - 1);
                int n = Math.Min((int)ctx.Value(Mo(P.AMModes)), ModalBodies.Count[body]);
                float mat = (float)ctx.Value(Mo(P.AMMat)) / 100f, hf = (float)ctx.Value(Mo(P.AMHfDamp)) / 100f, size = (float)ctx.Value(Mo(P.AMSize)) / 100f;
                double ring = ctx.Value(Mo(P.AMRing)) * Math.Pow(2, 1.5 * (size - 0.5));
                double pp = 0.02 + 0.48 * ctx.Value(posParam), fc = 2000 * Math.Pow(2, -4 * (size - 0.5)), h = 24 * hf * (1 - 0.8 * mat);
                var ratios = ModalBodies.Ratios[body];
                var at = new double[n];
                var level = new double[n];
                var left = new double[n];
                double top = 1e-9;
                for (int j = 0; j < n; j++)
                {
                    at[j] = ratios[j] * Math.Sqrt(1 + 0.003 * mat * mat * (j + 1) * (j + 1));
                    double hz = 220 * at[j];
                    level[j] = Math.Abs(Math.Sin(Math.PI * (j + 1) * pp)) / Math.Sqrt(1 + hz * hz / (fc * fc)) / ratios[j];
                    top = Math.Max(top, level[j]);
                    // 60 dB in ring / (1 + h f²) seconds: the share left after a quarter second.
                    left[j] = Math.Pow(10, -3 * 0.25 * (1 + h * (hz / 1000) * (hz / 1000)) / ring);
                }
                double x0 = r.Left + 6, w = r.Width - 12, bottom = r.Bottom - 5, ht = r.Height - 24;
                double reach = n > 0 ? at[n - 1] : 1;
                g.Line(x0, bottom, x0 + w, bottom, new NeoStroke(Palette.DimText.WithAlpha(0x60), 1));
                double thick = w / Math.Max(1, n) > 5 ? 3 : 1.6;
                for (int j = 0; j < n; j++)
                {
                    double x = x0 + at[j] / reach * (w - 2), tall = level[j] / top * ht;
                    g.Line(x, bottom, x, bottom - tall, new NeoStroke(ctx.Accent.WithAlpha(0x50), thick));
                    g.Line(x, bottom, x, bottom - tall * left[j], new NeoStroke(ctx.Accent, thick));
                }
                string[] excite = { "struck", "plucked", "continuous" };
                g.Text($"{ModalBodies.Names[body]} · {excite[Math.Clamp(ctx.Raw(Mo(P.AMExc)), 0, 2)]} · {n} modes", r.Left + 7, r.Top + 4, 11, Palette.DimText);
            }
            var view = new GraphView(width, height, (g, r) =>
            {
                int type = ctx.Raw(typeParam);
                if (type == (int)SlotType.Modal)
                {
                    ModalDraw(g, r);
                    return;
                }
                if (type < (int)SlotType.Sample)
                {
                    table(g, r);
                    return;
                }
                if (type == (int)SlotType.Harmonic)
                {
                    HarmonicDraw(g, r);
                    return;
                }
                int src = ctx.Raw(srcParam);
                var layer = src >= SampleSources.Count ? machine?.Engine.UserSample(slot) : SampleSources.Get(src);
                if (layer == null)
                {
                    g.Text(src >= SampleSources.Count ? "No user sample: load a WAV" : "Building…", r.Width / 2, r.Height / 2 - 8, 11.5, Palette.DimText, align: NeoAlign.Center);
                    return;
                }
                double x0 = r.Left + 6, w = r.Width - 12, mid = r.Top + r.Height / 2 + 4, amp = (r.Height - 26) / 2;
                if (type == (int)SlotType.Sample && ctx.Raw(K(P.ASmpLoop)) != 0)
                {
                    double la = ctx.Value(K(P.ASmpLoopStart)), le = ctx.Value(K(P.ASmpLoopEnd));
                    if (le < la) (la, le) = (le, la);
                    g.Rect(new NeoRect(x0 + la * w, r.Top + 14, Math.Max(1, (le - la) * w), r.Height - 18), ctx.Accent.WithAlpha(0x28), NeoStroke.None);
                }
                double pos = ctx.Value(posParam);
                if (type == (int)SlotType.Granular)
                {
                    double spray = ctx.Value(K(P.AGrSpray)) * 0.5;
                    double lo = Math.Max(0, pos - spray), hi = Math.Min(1, pos + spray);
                    g.Rect(new NeoRect(x0 + lo * w, r.Top + 14, Math.Max(1, (hi - lo) * w), r.Height - 18), ctx.Accent.WithAlpha(0x28), NeoStroke.None);
                }
                var path = new NeoPath();
                var ov = layer.Overview;
                int cols = ov.Length / 2;
                for (int c = 0; c < cols; c++) path.LineTo(x0 + (c + 0.5) / cols * w, mid - ov[2 * c + 1] * amp * 1.8);
                for (int c = cols - 1; c >= 0; c--) path.LineTo(x0 + (c + 0.5) / cols * w, mid - ov[2 * c] * amp * 1.8);
                path.Close();
                g.Path(path, ctx.Accent.WithAlpha(0x70), new NeoStroke(ctx.Accent, 1));
                g.Line(x0 + pos * w, r.Top + 12, x0 + pos * w, r.Bottom - 3, new NeoStroke(ctx.Accent, 2));
                string state = type == (int)SlotType.Spectral && src >= SampleSources.Count && machine != null && machine.AnalysisProgress(slot) >= 0f ? $" · analysing {machine.AnalysisProgress(slot) * 100:0} %"
                    : type == (int)SlotType.Spectral && src < SampleSources.Count && !SpectralSources.AllBuilt() ? " · analysing" : "";
                g.Text($"{layer.Name} · {layer.Length / (double)layer.Rate:0.0#} s{(layer.Stereo ? " · stereo" : "")}{state}", r.Left + 7, r.Top + 4, 11, Palette.DimText);
            });
            ctx.Changed += view.InvalidateVisual;
            // The Spectral engine's analysis runs after the sample is loaded: show how far it is.
            Poll.Every(view, 0.4, () => { if (ctx.Raw(typeParam) == (int)SlotType.Spectral) view.InvalidateVisual(); });
            return view;
        }

        /// <summary>
        /// A slot's table in perspective: up to 16 of its frames from front (first) to back (last), and
        /// the frame its position plays now, bright.
        /// </summary>
        static Action<INeoCanvas, NeoRect> TableDraw(EditorContext ctx, DyeEssenceMachine machine, int slot)
        {
            int b = slot == 0 ? P.AType : slot == 1 ? P.BType : P.CType;
            int tableParam = b + (P.ATable - P.AType), posParam = b + (P.APos - P.AType);
            TableArena cached = null;
            var back = new NeoPath();   // kept while the table and size stay: the backend builds it once
            double cachedWidth = 0, cachedHeight = 0;
            const int Shown = 16, Points = 64, Level = 6;   // level 6: 256 samples a frame, 16 harmonics
            int len = Wavetable.LevelLength(Level);

            return (g, r) =>
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
            };
        }
    }
}
