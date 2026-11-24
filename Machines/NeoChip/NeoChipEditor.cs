#nullable disable
// Neo Chip's editor (docs/specs/neo-chip.md): four house-style sections on one screen:
// Channels (compact strips for Pulse 1-3 and Noise), Envelope (with a live 16-step shape picture),
// Arpeggio & Motion (arpeggio offsets/rate/sync, pitch sweep, vibrato), and Output (voice mode,
// detune, velocity depth, spread, Raw and Lo-res DAC switches, volume and level meter).
// One source for WPF and Avalonia (the Neo editor kit), light and dark theme safe.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
using System.Windows.Controls;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Chip
{
    internal sealed class NeoChipEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Synth identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0x4C, 0xD9, 0x8F));
        const double D = 38;

        protected override string Title => "Neo Chip";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoChipMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            // Compact channel strip inside Channels (portable across WPF and Avalonia).
            UIElement Strip(string title, UIElement content) => Layout.Column(
                new TextBlock
                {
                    Text = title.ToUpperInvariant(),
                    Foreground = Theme.DimText,
                    FontFamily = Theme.FontFamily,
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 4),
                },
                content);

            UIElement Divider() => new GraphView(1, 96, (g, r) => g.Line(0.5, 0, 0.5, r.Height, new NeoStroke(Palette.PanelEdge, 1, false)), animate: false)
            {
                Margin = new Thickness(8, 2, 8, 2),
            };

            UIElement PulseStrip(string title, int duty, int pwm, int rate, int pan) => Strip(title, Layout.Column(
                new ParamChoice(ctx, duty, "Duty", cellWidth: 44),
                Layout.Gap(1, 4),
                Layout.Row(
                    new ParamKnob(ctx, pwm, "PWM", D),
                    new ParamKnob(ctx, rate, "Rate", D),
                    new ParamKnob(ctx, pan, "Pan", D))));

            var noiseStrip = Strip("Noise", Layout.Column(
                Layout.Row(
                    new ParamChoice(ctx, P.NoiseMode, "Mode", cellWidth: 46),
                    Layout.Gap(6),
                    new ParamDropdown(ctx, P.NoiseMidiCh, "MIDI ch", width: 64)),
                Layout.Gap(1, 4),
                Layout.Row(
                    new ParamKnob(ctx, P.NoisePitch, "Pitch", D),
                    new ParamKnob(ctx, P.NoisePan, "Pan", D))));

            var channels = S("Channels", Layout.Row(
                PulseStrip("Pulse 1", P.P1Duty, P.P1Pwm, P.P1PwmRate, P.P1Pan),
                Divider(),
                PulseStrip("Pulse 2", P.P2Duty, P.P2Pwm, P.P2PwmRate, P.P2Pan),
                Divider(),
                PulseStrip("Pulse 3", P.P3Duty, P.P3Pwm, P.P3PwmRate, P.P3Pan),
                Divider(),
                noiseStrip));

            // Envelope section with a live 16-step staircase shape picture.
            var envGraph = new GraphView(266, 68, (g, r) => DrawEnvelope(g, r, ctx), animate: false);
            ctx.Changed += envGraph.InvalidateVisual;

            var envelope = S("Envelope", Layout.Column(
                Layout.Row(
                    new ParamDropdown(ctx, P.EnvMode, "Mode", width: 122),
                    Layout.Gap(8),
                    new ParamDropdown(ctx, P.EnvSync, "Sync", width: 82)),
                Layout.Gap(1, 4),
                envGraph,
                Layout.Gap(1, 4),
                Layout.Row(
                    new ParamKnob(ctx, P.EnvAttack, "Attack", D),
                    new ParamKnob(ctx, P.EnvDecay, "Decay", D),
                    new ParamKnob(ctx, P.EnvSustain, "Sustain", D),
                    new ParamKnob(ctx, P.EnvRelease, "Release", D),
                    new ParamKnob(ctx, P.EnvPeriod, "Period", D))));

            // Arpeggio & Motion section.
            var arpMotion = S("Arpeggio & Motion", Layout.Column(
                Layout.Row(
                    new ParamDropdown(ctx, P.ArpMode, "Arp", width: 100),
                    Layout.Gap(6),
                    new ParamChoice(ctx, P.ArpSteps, "Steps", cellWidth: 28),
                    Layout.Gap(6),
                    new ParamDropdown(ctx, P.ArpSync, "Sync", width: 72)),
                Layout.Gap(1, 4),
                Layout.Row(
                    new ParamKnob(ctx, P.ArpStep1, "Step 1", D),
                    new ParamKnob(ctx, P.ArpStep2, "Step 2", D),
                    new ParamKnob(ctx, P.ArpStep3, "Step 3", D),
                    new ParamKnob(ctx, P.ArpRate, "Rate", D)),
                Layout.Gap(1, 6),
                Layout.Row(
                    new ParamKnob(ctx, P.Sweep, "Sweep", D),
                    new ParamKnob(ctx, P.VibDepth, "Vib depth", D),
                    new ParamKnob(ctx, P.VibRate, "Vib rate", D),
                    new ParamKnob(ctx, P.VibDelay, "Vib delay", D))));

            // Output & Voice section with live vertical output meter.
            var meter = new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 146);
            var output = S("Output", Layout.Row(
                Layout.Column(
                    new ParamChoice(ctx, P.VoiceMode, "Voice", cellWidth: 50),
                    Layout.Gap(1, 4),
                    Layout.Row(
                        new ParamChoice(ctx, P.Raw, "Raw", cellWidth: 52),
                        Layout.Gap(6),
                        new ParamChoice(ctx, P.LoResDac, "Lo-res DAC", cellWidth: 74)),
                    Layout.Gap(1, 4),
                    Layout.Row(
                        new ParamKnob(ctx, P.Detune, "Detune", D),
                        new ParamKnob(ctx, P.VelDepth, "Velocity", D)),
                    Layout.Row(
                        new ParamKnob(ctx, P.Spread, "Spread", D),
                        new ParamKnob(ctx, P.Volume, "Volume", D))),
                Layout.Gap(8),
                meter));

            return Layout.Column(
                channels,
                Layout.Row(envelope, arpMotion, output));
        }

        /// <summary>Draws the 16-step ADSR or hardware-style envelope staircase for the current parameters.</summary>
        static void DrawEnvelope(INeoCanvas g, NeoRect r, EditorContext ctx)
        {
            var plot = new NeoRect(r.X + 6, r.Y + 6, r.Width - 12, r.Height - 12);
            double top = plot.Top + 12;   // keep room for mode label
            double bottom = plot.Bottom;
            double h = Math.Max(10, bottom - top);

            // Subtle 4-step horizontal grid lines (steps 5, 10, 15).
            var gridStroke = new NeoStroke(Palette.Grid, 1, false);
            for (int st = 5; st <= 15; st += 5)
            {
                double gy = bottom - (st / 15.0) * h;
                g.Line(plot.Left, gy, plot.Right, gy, gridStroke);
            }

            int mode = ctx.Raw(P.EnvMode);
            var path = new NeoPath();

            if (mode == (int)ChipEnvMode.Adsr)
            {
                double a = ctx.Value(P.EnvAttack);
                double d = ctx.Value(P.EnvDecay);
                int sus = Math.Clamp((int)ctx.Value(P.EnvSustain), 0, 15);
                double rl = ctx.Value(P.EnvRelease);
                double wa = Math.Sqrt(a), wd = Math.Sqrt(d), ws = 0.35, wr = Math.Sqrt(rl);
                double total = wa + wd + ws + wr;
                double X(double t) => plot.Left + (t / total) * plot.Width;
                double Y(int step) => bottom - (step / 15.0) * h;

                path.MoveTo(X(0), bottom);
                // 16 stepped attack levels 0..15.
                for (int s = 0; s <= 15; s++)
                {
                    double x0 = X(wa * s / 16.0);
                    double x1 = X(wa * (s + 1) / 16.0);
                    path.LineTo(x0, Y(s)).LineTo(x1, Y(s));
                }
                // Stepped decay 15 -> sus.
                int dSteps = Math.Max(1, 15 - sus);
                for (int i = 0; i < dSteps; i++)
                {
                    int s = 15 - i;
                    double x0 = X(wa + wd * i / dSteps);
                    double x1 = X(wa + wd * (i + 1) / dSteps);
                    path.LineTo(x0, Y(s)).LineTo(x1, Y(s));
                }
                // Sustain plateau.
                path.LineTo(X(wa + wd), Y(sus)).LineTo(X(wa + wd + ws), Y(sus));
                // Stepped release sus -> 0.
                int rSteps = Math.Max(1, sus);
                for (int i = 0; i < rSteps; i++)
                {
                    int s = sus - i;
                    double x0 = X(wa + wd + ws + wr * i / rSteps);
                    double x1 = X(wa + wd + ws + wr * (i + 1) / rSteps);
                    path.LineTo(x0, Y(s)).LineTo(x1, Y(s));
                }
                path.LineTo(X(total), bottom).Close();
                g.Path(path, AccentColor.WithAlpha(0x30), new NeoStroke(AccentColor, 1.5));
                g.Text($"ADSR · 16-step · sus {sus}", plot.Left + 2, plot.Top - 1, 9.5, Palette.DimText);
            }
            else
            {
                bool repeating = mode is (int)ChipEnvMode.SawDown or (int)ChipEnvMode.SawUp or (int)ChipEnvMode.Triangle;
                int cycles = repeating ? 2 : 1;
                int segs = mode == (int)ChipEnvMode.Triangle ? 32 : 16;
                int totalSteps = cycles * segs + (repeating ? 0 : 8);
                double Y(int step) => bottom - (step / 15.0) * h;

                path.MoveTo(plot.Left, bottom);
                for (int k = 0; k < totalSteps; k++)
                {
                    int step;
                    if (!repeating && k >= segs)
                    {
                        step = mode == (int)ChipEnvMode.AttackHold ? 15 : 0;
                    }
                    else
                    {
                        int local = k % segs;
                        step = mode switch
                        {
                            (int)ChipEnvMode.SawDown or (int)ChipEnvMode.DecayShot => 15 - local,
                            (int)ChipEnvMode.SawUp or (int)ChipEnvMode.AttackHold => local,
                            _ => local < 16 ? local : 31 - local,
                        };
                    }
                    double x0 = plot.Left + plot.Width * k / totalSteps;
                    double x1 = plot.Left + plot.Width * (k + 1) / totalSteps;
                    path.LineTo(x0, Y(step)).LineTo(x1, Y(step));
                }
                path.LineTo(plot.Right, bottom).Close();
                g.Path(path, AccentColor.WithAlpha(0x30), new NeoStroke(AccentColor, 1.5));
                string modeName = Params.Table[P.EnvMode].Describe(mode);
                int sync = ctx.Raw(P.EnvSync);
                string periodText = sync > 0 ? Params.Table[P.EnvSync].Describe(sync) : Params.Table[P.EnvPeriod].Describe(ctx.Raw(P.EnvPeriod));
                g.Text($"{modeName} · {periodText}", plot.Left + 2, plot.Top - 1, 9.5, Palette.DimText);
            }
        }
    }
}
