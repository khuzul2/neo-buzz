#nullable disable
// Dye Essence's editor (docs/specs/dye-essence.md §8), its first page: the three engine slots, each
// with a 3-D view of its table (the frame playing now drawn bright) and a user-table loader, then
// sub and noise, the two filters and their routing, the voice, the envelopes and the output. One
// source for WPF and Avalonia (the kit's names; the views draw on INeoCanvas). P1c brings the pages.

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

        protected override string Title => "Dye Essence";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as DyeEssenceMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

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
                    FontSize = 10.5,
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
                    Layout.Row(new ParamDropdown(ctx, I(P.AType), width: 96), Layout.Gap(6), new ParamDropdown(ctx, I(P.ATable), width: 112)),
                    Layout.Gap(1, 4),
                    Layout.Row(load, Layout.Gap(6), userName),
                    Layout.Gap(1, 6),
                    TableView(ctx, machine, slot, 6 * 50, 92),
                    Layout.Gap(1, 4),
                    Build.Knobs(ctx, 6, D, I(P.APos), I(P.AOct), I(P.ASemi), I(P.AFine), I(P.ALevel), I(P.APan),
                        I(P.AUnison), I(P.ADetune), I(P.ASpread), I(P.APhase), I(P.ARandom), I(P.AToF2)),
                    Layout.Gap(1, 4),
                    Layout.Row(new ParamDropdown(ctx, I(P.AWarp1), width: 104), Layout.Gap(4), new ParamKnob(ctx, I(P.AWarp1Amt), "amount", 30),
                               Layout.Gap(10), new ParamDropdown(ctx, I(P.AWarp2), width: 104), Layout.Gap(4), new ParamKnob(ctx, I(P.AWarp2Amt), "amount", 30)),
                    Layout.Gap(1, 4),
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
                    Build.Knobs(ctx, 3, D, I(P.F1Drive), I(P.F1Env), I(P.F1Key), I(P.F1Vel))));
            }

            var sub = S("Sub", Layout.Column(
                new ParamChoice(ctx, P.SubShape, cellWidth: 46),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.SubOct, cellWidth: 46),
                Build.Knobs(ctx, 3, D, P.SubLevel, P.SubPan, P.SubToF2)));
            var noise = S("Noise", Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.NoiseColor, "color", D), Layout.Gap(8), new ParamKnob(ctx, P.NoiseLevel, "level", D)),
                Layout.Gap(1, 6),
                Layout.Row(new ParamKnob(ctx, P.NoisePan, "pan", D), Layout.Gap(8), new ParamKnob(ctx, P.NoiseToF2, "→ filter 2", D))));
            var routing = S("Routing", Layout.Column(new ParamKnob(ctx, P.Routing, diameter: 50)));
            var voice = S("Voice", Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.VoiceMode, cellWidth: 50), Layout.Gap(6), new ParamChoice(ctx, P.GlideMode, cellWidth: 50)),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.Quality, cellWidth: 84),
                Build.Knobs(ctx, 3, D, P.Polyphony, P.Glide, P.BendRange, P.VelAmp, P.Volume)));
            var meter = new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 140);

            UIElement MakeEnv(string title, int a, int d, int s, int r) => S(title, Layout.Column(
                Build.Envelope(ctx, a, d, s, r, 4 * 60, 44),
                Build.Knobs(ctx, 4, D, a, d, s, r)));

            return Layout.Column(
                Layout.Row(Slot(0), Slot(1), Slot(2)),
                Layout.Row(sub, noise, Filter(0), Filter(1), routing, voice),
                Layout.Row(MakeEnv("Amp envelope", P.AmpA, P.AmpD, P.AmpS, P.AmpR),
                           MakeEnv("Filter envelope", P.FltA, P.FltD, P.FltS, P.FltR),
                           S("Out", meter)));
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
                g.Text($"{table.Name} · frame {(int)Math.Round(framePos) + 1} of {table.FrameCount}", r.Left + 7, r.Top + 4, 10.5, Palette.DimText);
            });
        }
    }
}
