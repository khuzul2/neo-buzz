#nullable disable
// Neo Space's editor (docs/specs/neo-space.md): the room (the choice, the wave slot, pre-delay and length), a picture of the response
// with the pre-delay and the trim drawn on it, tone (cuts, width, reverse) and output. One source for WPF and Avalonia (the Neo editor
// kit); the response draws on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Space
{
    internal sealed class NeoSpaceEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the DelayReverb identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.DelayReverb, NeoColor.FromRgb(0x5C, 0xC8, 0xB0));
        static readonly NeoColor WarnColor = NeoColor.FromRgb(0xE0, 0xA0, 0x40);

        protected override string Title => "Neo Space";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoSpaceMachine;

            var room = Layout.Column(
                new ParamDropdown(ctx, P.Room, width: 130),
                Layout.Row(new ParamKnob(ctx, P.Wave), new ParamKnob(ctx, P.PreDelay), new ParamKnob(ctx, P.Length)));

            var picture = new GraphView(330, 170, (g, r) => DrawResponse(g, r, ctx, machine), animate: true);
            ctx.Changed += picture.InvalidateVisual;

            var tone = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.LowCut), new ParamKnob(ctx, P.HighCut)),
                Layout.Row(new ParamKnob(ctx, P.Width), new ParamChoice(ctx, P.Reverse, "Reverse", 40)));

            // Amount (the strength of the effect) and Mix (dry/wet) side by side, Amount first, the same size as on every Neo effect
            // (machines.md, "Mix and Amount"); the level of the reverb under them.
            var output = Layout.Row(
                Layout.Column(
                    Layout.Row(new ParamKnob(ctx, P.Amount, diameter: 40), new ParamKnob(ctx, P.Mix, diameter: 40)),
                    new ParamKnob(ctx, P.Output)),
                Layout.Gap(6),
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 140),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 140));

            return Layout.Row(
                new Section("Room", AccentColor, room),
                new Section("Response", AccentColor, picture),
                new Section("Tone", AccentColor, tone),
                new Section("Output", AccentColor, output));
        }

        /// <summary>
        /// The response in the room as it plays (its level in dB over time, the peak of both channels), with the pre-delay in front of it
        /// (a shaded gap) and the length's trim on it (a line, the part cut away dimmed); reversed, the picture is.
        /// </summary>
        static void DrawResponse(INeoCanvas g, NeoRect r, EditorContext ctx, NeoSpaceMachine machine)
        {
            double pre = ctx.Value(P.PreDelay) / 1000.0;
            double lengthFraction = ctx.Value(P.Length) / 100.0;
            bool reverse = ctx.Value(P.Reverse) > 0;
            var built = machine?.Engine.Current;
            var status = machine?.Engine.Status ?? SpaceStatus.Preparing;
            double seconds = built?.BaseSeconds ?? 1.0;
            double span = Math.Max(0.3, pre + seconds);
            var plot = new NeoRect(r.X + 8, r.Y + 22, r.Width - 16, r.Height - 40);
            double X(double t) => plot.Left + t / span * plot.Width;
            double Y(double db) => plot.Top + (-Math.Clamp(db, -80, 0)) / 80.0 * plot.Height;
            var grid = new NeoStroke(Palette.Grid, 1, false);
            for (int d = 0; d >= -80; d -= 20) g.Line(plot.Left, Y(d), plot.Right, Y(d), grid);
            if (built?.Preview != null)
            {
                var p = built.Preview;
                var area = new NeoPath();
                area.MoveTo(X(pre), plot.Bottom);
                for (int c = 0; c < p.Length; c++)
                {
                    int k = reverse ? p.Length - 1 - c : c;
                    area.LineTo(X(pre + seconds * (c + 0.5) / p.Length), Y(p[k]));
                }
                area.LineTo(X(pre + seconds), plot.Bottom);
                area.Close();
                g.Path(area, new NeoFill(AccentColor.WithAlpha(0x70), default, false), new NeoStroke(AccentColor, 1.5));
            }
            // The pre-delay: the gap in front of the response.
            if (pre > 0.0005)
            {
                g.Rect(new NeoRect(plot.Left, plot.Top, X(pre) - plot.Left, plot.Height), new NeoFill(Palette.DimText.WithAlpha(0x30), default, false), NeoStroke.None);
                g.Text($"{pre * 1000:0} ms", plot.Left + 3, plot.Top + 2, 10, Palette.DimText);
            }
            // The trim: what the Length keeps, the rest dimmed.
            // Reversed, the kept part is the end of the picture (the trimmed response played backwards) and the part cut away is in front of it.
            double cut = pre + seconds * (reverse ? 1.0 - lengthFraction : lengthFraction);
            if (lengthFraction < 0.999)
            {
                if (reverse) g.Rect(new NeoRect(X(pre), plot.Top, X(cut) - X(pre), plot.Height), new NeoFill(Palette.Background.WithAlpha(0xB0), default, false), NeoStroke.None);
                else g.Rect(new NeoRect(X(cut), plot.Top, plot.Right - X(cut), plot.Height), new NeoFill(Palette.Background.WithAlpha(0xB0), default, false), NeoStroke.None);
                g.Line(X(cut), plot.Top, X(cut), plot.Bottom, new NeoStroke(AccentColor, 2));
            }
            string name = built?.Name ?? "…";
            double kept = seconds * lengthFraction;
            g.Text(name, r.X + 8, r.Y + 3, 11, AccentColor);
            g.Text($"{kept:0.0#} s", r.Right - 52, r.Y + 3, 11, Palette.DimText);
            string note = status switch
            {
                SpaceStatus.WaveMissing => $"Wave {ctx.Raw(P.Wave) + 1} is not in the song: the hall plays",
                SpaceStatus.WaveLoading => "Reading the wave…",
                SpaceStatus.Preparing => "Preparing the room…",
                _ => null,
            };
            if (note != null) g.Text(note, r.X + 8, r.Bottom - 15, 10, status == SpaceStatus.WaveMissing ? WarnColor : Palette.DimText);
            else g.Text(reverse ? "reversed" : "0 to " + $"{span:0.0#} s", r.X + 8, r.Bottom - 15, 10, Palette.DimText);
        }
    }
}
