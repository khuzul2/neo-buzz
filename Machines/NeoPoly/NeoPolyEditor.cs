#nullable disable
// Neo Poly's editor (docs/specs/machines.md §4): oscillators and mixer on top, filters and voice
// in the middle, envelopes and LFOs, then the modulation matrix.

#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
using System.Windows.Controls;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Poly
{
    internal sealed class NeoPolyEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Synth identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xFF, 0x5C, 0x8A));
        const double D = 38;

        protected override string Title => "Neo Poly";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoPolyMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            var osc1 = S("Osc 1", Build.Knobs(ctx, 3, D, P.O1Shape, P.O1PW, P.O1Level, P.O1Oct, P.O1Semi, P.O1Fine));
            var osc2 = S("Osc 2", Layout.Column(
                Build.Knobs(ctx, 4, D, P.O2Shape, P.O2PW, P.O2Level, P.O2Fm, P.O2Oct, P.O2Semi, P.O2Fine),
                new ParamChoice(ctx, P.O2Sync, "Sync to osc 1", 34)));
            var osc3 = S("Osc 3 · mixer", Build.Knobs(ctx, 4, D, P.O3Shape, P.O3Level, P.O3Oct, P.O3Semi, P.RingLevel, P.NoiseLevel, P.NoiseColor, P.O3Fine));

            var f1 = S("Filter 1", Layout.Column(
                new ParamDropdown(ctx, P.F1Type, width: 120),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.F1Cutoff, diameter: 54), new ParamKnob(ctx, P.F1Reso, diameter: 54)),
                Build.Knobs(ctx, 4, D, P.F1Drive, P.F1Env, P.F1Key, P.F1Vel)));
            var f2 = S("Filter 2", Layout.Column(
                Layout.Row(new ParamDropdown(ctx, P.F2Type, width: 80), Layout.Gap(6), new ParamChoice(ctx, P.Routing, cellWidth: 56)),
                Layout.Gap(1, 4),
                Build.Knobs(ctx, 3, D, P.F2Cutoff, P.F2Reso, P.F2Env)));
            var voice = S("Voice", Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.VoiceMode, cellWidth: 50), Layout.Gap(6), new ParamChoice(ctx, P.GlideMode, cellWidth: 50)),
                Build.Knobs(ctx, 5, D, P.Polyphony, P.Unison, P.UniDetune, P.UniSpread, P.Glide, P.BendRange, P.Drift, P.VelAmp, P.Volume)));

            UIElement MakeEnv(string title, int a, int d, int s, int r) => S(title, Layout.Column(
                Build.Envelope(ctx, a, d, s, r, 4 * 64, 46),
                Build.Knobs(ctx, 4, D, a, d, s, r)));
            UIElement MakeLfo(string title, int shape, int rate, int sync, int retrig, int fade) => S(title, Layout.Column(
                new ParamDropdown(ctx, shape, width: 100),
                Layout.Row(new ParamKnob(ctx, rate, diameter: D), new ParamKnob(ctx, fade, diameter: D)),
                Layout.Row(new ParamDropdown(ctx, sync, width: 66), Layout.Gap(4), new ParamChoice(ctx, retrig, "Retrig", 30))));

            var matrix = new Grid();
            for (int c = 0; c < 2; c++)
            {
                matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }
            for (int r = 0; r < 4; r++) matrix.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int m = 0; m < 8; m++)
            {
                int b = P.M1Src + 3 * m;
                int row = m % 4, col = (m / 4) * 3;
                var src = new ParamDropdown(ctx, b, $"{m + 1} source", 100) { Margin = new Thickness(0, 0, 4, 2) };
                var dst = new ParamDropdown(ctx, b + 1, "destination", 110) { Margin = new Thickness(0, 0, 4, 2) };
                var amt = new ParamKnob(ctx, b + 2, "amount", 30) { Margin = new Thickness(0, 0, 14, 0) };
                Grid.SetRow(src, row); Grid.SetColumn(src, col);
                Grid.SetRow(dst, row); Grid.SetColumn(dst, col + 1);
                Grid.SetRow(amt, row); Grid.SetColumn(amt, col + 2);
                matrix.Children.Add(src);
                matrix.Children.Add(dst);
                matrix.Children.Add(amt);
            }

            var meter = new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 150);

            var ampEnv = MakeEnv("Amp envelope", P.AmpA, P.AmpD, P.AmpS, P.AmpR);
            var fltEnv = MakeEnv("Filter envelope", P.FltA, P.FltD, P.FltS, P.FltR);
            var modEnv = MakeEnv("Mod envelope", P.ModA, P.ModD, P.ModS, P.ModR);
            var lfo1 = MakeLfo("LFO 1", P.L1Shape, P.L1Rate, P.L1Sync, P.L1Retrig, P.L1Fade);
            var lfo2 = MakeLfo("LFO 2", P.L2Shape, P.L2Rate, P.L2Sync, P.L2Retrig, P.L2Fade);
#if AVALONIA
            // ux-overhaul.md P5a: two pages in the studio, Synth (the sound's path) | Modulation (what moves it). The same controls,
            // parameters and order as the classic layout below; only where they sit differs. The classic app keeps its one screen.
            var synth = Layout.Column(
                Layout.Row(osc1, osc2, osc3),
                Layout.Row(f1, f2, voice, S("Out", meter)),
                Layout.Row(ampEnv, fltEnv));
            var modulation = Layout.Column(
                Layout.Row(modEnv, lfo1, lfo2),
                Layout.Row(S("Modulation matrix", matrix)));
            synth.Measure(Avalonia.Size.Infinity);
            modulation.Measure(Avalonia.Size.Infinity);
            var pages = new Pages(AccentColor, System.Math.Ceiling(System.Math.Max(synth.DesiredSize.Width, modulation.DesiredSize.Width)),
                System.Math.Ceiling(System.Math.Max(synth.DesiredSize.Height, modulation.DesiredSize.Height)), "Neo Poly");
            pages.Add("Synth", synth);
            pages.Add("Modulation", modulation);
            return pages;
#else
            return Layout.Column(
                Layout.Row(osc1, osc2, osc3),
                Layout.Row(f1, f2, voice, S("Out", meter)),
                Layout.Row(ampEnv, fltEnv, modEnv),
                Layout.Row(lfo1, lfo2, S("Modulation matrix", matrix)));
#endif
        }
    }
}
