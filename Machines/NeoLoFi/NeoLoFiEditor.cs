#nullable disable
// Neo Lo-Fi's editor (docs/specs/neo-lofi.md): four sections of knob cards in the house style: Crush (bits, rate, jitter,
// dither, anti-alias, with a picture of a sine through the crush), Noise (hiss, follow, crackle), Motion (wow, flutter,
// drift) and Output (tone, mix, output, meters). One source for WPF and Avalonia (the Neo editor kit).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.LoFi
{
    internal sealed class NeoLoFiEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Distortion identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Distortion, NeoColor.FromRgb(0xE0, 0xA0, 0x40));

        protected override string Title => "Neo Lo-Fi";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoLoFiMachine;
            var picture = new GraphView(190, 120, (g, r) => DrawCrush(g, r, ctx), animate: false);

            var crush = Layout.Row(
                Layout.Column(
                    Layout.Row(new ParamKnob(ctx, P.Bits, diameter: 58), new ParamKnob(ctx, P.Rate, diameter: 58), new ParamKnob(ctx, P.Jitter)),
                    Layout.Gap(1, 4),
                    Layout.Row(new ParamChoice(ctx, P.Dither, "Dither", 40), Layout.Gap(6), new ParamChoice(ctx, P.AntiAlias, "Anti-alias", 40))),
                Layout.Gap(8),
                picture);

            var noise = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Hiss), new ParamKnob(ctx, P.Crackle), new ParamKnob(ctx, P.CrackleLevel, "Crackle level", 46)),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.HissFollow, "Hiss follow", 40));

            var motion = Layout.Row(
                Layout.Column(new ParamKnob(ctx, P.Wow), new ParamKnob(ctx, P.WowRate)),
                Layout.Column(new ParamKnob(ctx, P.Flutter), new ParamKnob(ctx, P.Drift)));

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 120),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 120));

            var output = Layout.Row(
                Layout.Column(new ParamKnob(ctx, P.Tone), new ParamKnob(ctx, P.Mix), new ParamKnob(ctx, P.Output)),
                Layout.Gap(6),
                meters);

            return Layout.Row(
                new Section("Crush", AccentColor, crush),
                new Section("Noise", AccentColor, noise),
                new Section("Motion", AccentColor, motion),
                new Section("Output", AccentColor, output));
        }

        /// <summary>Two cycles of a sine (dim) and the same through the hold and the bit reduction (accent), as the settings stand.</summary>
        static void DrawCrush(INeoCanvas g, NeoRect r, EditorContext ctx)
        {
            double bits = ctx.Value(P.Bits);
            double rate = ctx.Value(P.Rate);
            bool rateOff = ctx.Raw(P.Rate) >= Params.Table[P.Rate].Max || rate >= 48000;
            var plot = new NeoRect(r.X + 6, r.Y + 6, r.Width - 12, r.Height - 12);
            double mid = plot.Y + plot.Height / 2, half = plot.Height / 2 - 14;   // the top 12 px hold the label, clear of the curves
            g.Line(plot.Left, mid, plot.Right, mid, new NeoStroke(Palette.Grid, 1, false));
            const int Points = 240;
            const double Cycles = 2;
            // The picture's time axis is 2 cycles of a 1 kHz sine at 48 kHz: 96 samples.
            double perPoint = 96.0 / Points;
            double hold = rateOff ? 1 : Math.Max(1, 48000 / rate);
            var sine = new NeoPath();
            var crushed = new NeoPath();
            double held = 0, next = 0;
            for (int k = 0; k <= Points; k++)
            {
                double t = k * perPoint;
                double x = Math.Sin(2 * Math.PI * Cycles * k / Points);
                if (t >= next) { held = x; next += hold; }
                double y = bits >= 24 ? held : LoFiEngine.Quantize(held, bits);
                double px = plot.Left + plot.Width * k / Points;
                if (k == 0) { sine.MoveTo(px, mid - x * half); crushed.MoveTo(px, mid - y * half); }
                else { sine.LineTo(px, mid - x * half); crushed.LineTo(px, mid - y * half); }
            }
            g.Path(sine, NeoFill.None, new NeoStroke(Palette.DimText, 1));
            g.Path(crushed, NeoFill.None, new NeoStroke(AccentColor, 2));
            g.Text(bits >= 24 ? "24 bit" : $"{bits:0.#} bit", plot.Left + 4, plot.Top, 10, Palette.DimText);
        }
    }
}
