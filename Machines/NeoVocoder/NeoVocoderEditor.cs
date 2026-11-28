#nullable disable
// Neo Vocoder's editor (docs/specs/neo-vocoder.md): the carrier (the choice, the synth's tone and envelope), the bands with a live picture of
// the modulator's and the output's level in every band, the followers, the voice controls and the output. One source for WPF and Avalonia (the
// Neo editor kit); the picture draws on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Vocoder
{
    internal sealed class NeoVocoderEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Modulation identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Modulation, NeoColor.FromRgb(0xC8, 0x6A, 0xE0));
        static readonly NeoColor WarnColor = NeoColor.FromRgb(0xE0, 0xA0, 0x40);

        protected override string Title => "Neo Vocoder";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoVocoderMachine;

            var carrier = Layout.Column(
                new ParamDropdown(ctx, P.Carrier, width: 130),
                Layout.Row(new ParamKnob(ctx, P.Wave), new ParamKnob(ctx, P.Noise), new ParamKnob(ctx, P.Detune), new ParamKnob(ctx, P.Glide)),
                Layout.Row(new ParamKnob(ctx, P.SynAttack), new ParamKnob(ctx, P.SynDecay), new ParamKnob(ctx, P.SynSustain), new ParamKnob(ctx, P.SynRelease)));

            var picture = new GraphView(420, 170, (g, r) => DrawBands(g, r, ctx, machine), animate: true);
            ctx.Changed += picture.InvalidateVisual;
            var bands = Layout.Column(
                picture,
                Layout.Row(new ParamKnob(ctx, P.Bands), new ParamKnob(ctx, P.Low), new ParamKnob(ctx, P.High), new ParamKnob(ctx, P.Width)));

            var voice = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Attack), new ParamKnob(ctx, P.Release)),
                Layout.Row(new ParamKnob(ctx, P.Formant), new ParamKnob(ctx, P.Sibilance), new ParamKnob(ctx, P.Spread)),
                new ParamChoice(ctx, P.Freeze, "Freeze", 40));

            // Amount (the strength of the effect) and Mix (dry/wet) side by side, Amount first, the same size as on every Neo effect
            // (machines.md, "Mix and Amount"); the level of the vocoded sound under them.
            var output = Layout.Row(
                Layout.Column(
                    Layout.Row(new ParamKnob(ctx, P.Amount, diameter: 40), new ParamKnob(ctx, P.Mix, diameter: 40)),
                    new ParamKnob(ctx, P.Output)),
                Layout.Gap(6),
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 140),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 140));

            return Layout.Row(
                new Section("Carrier", AccentColor, carrier),
                new Section("Bands", AccentColor, bands),
                new Section("Voice", AccentColor, voice),
                new Section("Output", AccentColor, output));
        }

        /// <summary>
        /// The bands as they sound now: for every band a bar for the modulator's level (dimmed) and, narrower in front of it, the output's level (in the
        /// accent colour), in dB from -70 to 0 on a log-frequency axis (the bands' own spacing). With Carrier on Sidechain and nothing wired, it says so.
        /// </summary>
        static void DrawBands(INeoCanvas g, NeoRect r, EditorContext ctx, NeoVocoderMachine machine)
        {
            var plot = new NeoRect(r.X + 8, r.Y + 22, r.Width - 16, r.Height - 40);
            double Y(double db) => plot.Top + (-Math.Clamp(db, -70, 0)) / 70.0 * plot.Height;
            var grid = new NeoStroke(Palette.Grid, 1, false);
            for (int d = 0; d >= -70; d -= 20) g.Line(plot.Left, Y(d), plot.Right, Y(d), grid);
            var bank = machine?.Engine.Bank;
            int n = bank?.BuiltBands ?? (int)Math.Round(ctx.Value(P.Bands));
            double slot = plot.Width / Math.Max(1, n);
            for (int i = 0; i < n; i++)
            {
                double x = plot.Left + i * slot;
                double mod = bank != null ? bank.BandLevels[i] * 1.2533 : 0;
                double outl = bank != null ? bank.OutputLevels[i] : 0;
                double mDb = 20 * Math.Log10(Math.Max(mod, 1e-6)), oDb = 20 * Math.Log10(Math.Max(outl, 1e-6));
                g.Rect(new NeoRect(x + slot * 0.1, Y(mDb), slot * 0.8, plot.Bottom - Y(mDb)), new NeoFill(Palette.DimText.WithAlpha(0x70), default, false), NeoStroke.None);
                g.Rect(new NeoRect(x + slot * 0.28, Y(oDb), slot * 0.44, plot.Bottom - Y(oDb)), new NeoFill(AccentColor, default, false), NeoStroke.None);
            }
            g.Text("Modulator", r.X + 8, r.Y + 3, 11, Palette.DimText);
            g.Text("Output", r.X + 84, r.Y + 3, 11, AccentColor);
            double low = ctx.Value(P.Low), high = ctx.Value(P.High);
            g.Text($"{low:0} Hz", plot.Left, r.Bottom - 15, 10, Palette.DimText);
            g.Text($"{high / 1000.0:0.#} kHz", plot.Right - 44, r.Bottom - 15, 10, Palette.DimText);
            if (machine != null && machine.Engine.NeedsWire) g.Text(VocoderEngine.WireMessage, r.X + 150, r.Y + 3, 11, WarnColor);
        }
    }
}
