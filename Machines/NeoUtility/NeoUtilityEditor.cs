#nullable disable
// Neo Utility's editor (docs/specs/machines.md §4): level and pan, the stereo image (width, pan,
// bass mono) with the live correlation, the channel switches and output meters. WPF lays the
// editor out; the image and the correlation bar draw on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Utility
{
    internal sealed class NeoUtilityEditor : NeoEditor
    {
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0x8F, 0xD1, 0x4F);

        protected override string Title => "Neo Utility";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoUtilityMachine;

            var level = Layout.Column(
                new ParamKnob(ctx, P.Gain, diameter: 58),
                new ParamKnob(ctx, P.Pan),
                new ParamChoice(ctx, P.PanLaw, cellWidth: 62));

            var image = new GraphView(176, 132, (g, r) => DrawImage(g, r, ctx, machine), animate: true);
            ctx.Changed += image.InvalidateVisual;
            var correlation = new GraphView(176, 30, (g, r) => DrawCorrelation(g, r, machine), animate: true)
            {
                ToolTip = "Output correlation: +1 mono, 0 unrelated, −1 out of phase",
            };
            var stereo = Layout.Row(
                Layout.Column(image, Layout.Gap(1, 6), correlation),
                Layout.Gap(6),
                Layout.Column(
                    new ParamKnob(ctx, P.Width),
                    new ParamChoice(ctx, P.BassMono, "Bass mono", 34),
                    Layout.Gap(1, 4),
                    new ParamKnob(ctx, P.BassFreq, "Mono below")));

            var channels = Layout.Column(
                new ParamChoice(ctx, P.InvertL, "Invert L", 34),
                new ParamChoice(ctx, P.InvertR, "Invert R", 34),
                new ParamChoice(ctx, P.Swap, "Swap L/R", 34),
                new ParamChoice(ctx, P.DcRemove, "DC removal", 34));

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 196),
                Layout.Gap(6),
                new Meter("Output L", () => machine?.Engine.OutLeft.Peak ?? 0, AccentColor, 196),
                Layout.Gap(3),
                new Meter("Output R", () => machine?.Engine.OutRight.Peak ?? 0, AccentColor, 196));

            return Layout.Row(
                new Section("Level", AccentColor, level),
                new Section("Stereo", AccentColor, stereo),
                new Section("Channels", AccentColor, channels),
                new Section("Meters", AccentColor, meters));
        }

        /// <summary>
        /// The stereo field from left to right: the image as a wedge (its centre the pan, its
        /// spread the width), the mono lows as a line, polarity and swap as tags.
        /// </summary>
        static void DrawImage(INeoCanvas g, NeoRect r, EditorContext ctx, NeoUtilityMachine machine)
        {
            var c = new NeoPoint(r.X + r.Width / 2, r.Bottom - 16);
            double radius = Math.Min(r.Width / 2 - 12, r.Height - 30);
            var grid = new NeoStroke(Palette.Grid, 1, false);
            g.Arc(c, radius, -90, 90, new NeoStroke(Palette.Track, 1.5));
            g.Arc(c, radius * 0.5, -90, 90, grid);
            g.Line(c, NeoGeometry.Polar(c, radius, 0), grid);
            g.Line(c, NeoGeometry.Polar(c, radius, -90), grid);
            g.Line(c, NeoGeometry.Polar(c, radius, 90), grid);
            g.Text("L", c.X - radius - 9, c.Y - 7, 10, Palette.DimText);
            g.Text("R", c.X + radius + 3, c.Y - 7, 10, Palette.DimText);

            double pan = ctx.Value(P.Pan);
            bool monoPan = ctx.Value(P.PanLaw) > 0;
            double width = ctx.Value(P.Width) / 100.0;
            double centre = pan * 90;
            // Width 100 % spans ±45° (the input's own image); 200 % reaches the speakers' outside.
            double spread = monoPan ? 0 : Math.Min(90, 45 * width);
            if (spread > 0.5)
            {
                double a0 = Math.Max(-90, centre - spread), a1 = Math.Min(90, centre + spread);
                var wedge = new NeoPath().MoveTo(c);
                for (int i = 0; i <= 24; i++) wedge.LineTo(NeoGeometry.Polar(c, radius - 2, a0 + (a1 - a0) * i / 24));
                wedge.Close();
                g.Path(wedge, AccentColor.WithAlpha(0x40), new NeoStroke(AccentColor, 1.5));
            }
            g.Line(c, NeoGeometry.Polar(c, radius - 2, centre), new NeoStroke(AccentColor, 2.5));
            if (ctx.Value(P.BassMono) > 0)
            {
                // The lows: mono, so a line at the pan position, short.
                g.Line(c, NeoGeometry.Polar(c, radius * 0.5, centre), new NeoStroke(AccentColor.Tint(0.5), 4));
                g.Text("mono < " + ParamDef.Freq(ctx.Value(P.BassFreq)), r.X + 6, r.Y + 4, 10, Palette.DimText);
            }
            string tags = (ctx.Value(P.InvertL) > 0 ? "Ø L  " : "") + (ctx.Value(P.InvertR) > 0 ? "Ø R  " : "") +
                (ctx.Value(P.Swap) > 0 ? "L⇄R  " : "") + (ctx.Value(P.DcRemove) > 0 ? "DC" : "");
            if (tags.Length > 0) g.Text(tags.Trim(), r.Right - 6, r.Y + 4, 10, AccentColor, align: NeoAlign.Right);
            string gain = Params.Table[P.Gain].Describe(ctx.Raw(P.Gain));
            g.Text(gain, c.X, c.Y + 2, 10.5, Palette.Text, align: NeoAlign.Center);
        }

        /// <summary>A bar from −1 to +1 with the live output correlation.</summary>
        static void DrawCorrelation(INeoCanvas g, NeoRect r, NeoUtilityMachine machine)
        {
            double x0 = r.X + 22, x1 = r.Right - 22, y = r.Y + r.Height / 2;
            g.Line(x0, y, x1, y, new NeoStroke(Palette.Track, 4));
            double mid = (x0 + x1) / 2;
            g.Line(mid, y - 6, mid, y + 6, new NeoStroke(Palette.Grid, 1, false));
            g.Text("−1", r.X + 4, y - 8, 10, Palette.DimText);
            g.Text("+1", r.Right - 4, y - 8, 10, Palette.DimText, align: NeoAlign.Right);
            if (machine == null) return;
            double c = Math.Clamp(machine.Engine.Correlation, -1, 1);
            double x = mid + c * (x1 - mid);
            var colour = c < 0 ? Palette.Over : AccentColor;
            g.Line(mid, y, x, y, new NeoStroke(colour, 4));
            g.Ellipse(new NeoPoint(x, y), 4, 4, colour, NeoStroke.None);
        }
    }
}
