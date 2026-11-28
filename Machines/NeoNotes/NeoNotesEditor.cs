#nullable disable
// Neo Notes' editor (docs/specs/neo-notes.md §8): a strip of the whole keyboard on top (the notes held on the input above, the notes sent below), then the seven
// sections in the order they work: Transpose, Scale and Spread; Velocity and Length; Echo and Humanize, each with its On switch first. One source for WPF and
// Avalonia (the Neo editor kit); everything draws through INeoCanvas.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Notes
{
    internal sealed class NeoNotesEditor : NeoEditor
    {
        // The Control identity in the studio, its own green in the classic app (Neo Chord is periwinkle, Neo Arp orange).
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Control, NeoColor.FromRgb(0x5F, 0xC9, 0x9A));
        static readonly bool[] IsBlack = { false, true, false, true, false, false, true, false, true, false, true, false };

        const double D = 44;
        const double StripWidth = 956, StripHeight = 56;

        protected override string Title => "Neo Notes";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoNotesMachine;
            var engine = machine?.Engine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);
            UIElement Sw(int index) => new ParamChoice(ctx, index, "On");

            var strip = new GraphView(StripWidth, StripHeight, (g, r) => DrawStrip(g, r, engine), animate: true);
            var keys = S("Notes: in above, sent below", strip);

            var transpose = S("1  Transpose", Layout.Row(Sw(P.TransOn), new ParamKnob(ctx, P.Trans, diameter: D)));
            var scale = S("2  Scale", Layout.Row(
                Sw(P.ScaleOn), Layout.Gap(8),
                new ParamDropdown(ctx, P.Key, "Key", 60), Layout.Gap(6),
                new ParamDropdown(ctx, P.Scale, "Scale", 124), Layout.Gap(6),
                new ParamDropdown(ctx, P.ScaleMode, "Out of scale", 88)));
            var spread = S("3  Spread", Layout.Row(
                Sw(P.SpreadOn), new ParamKnob(ctx, P.SpreadRange, diameter: D), Layout.Gap(6),
                new ParamDropdown(ctx, P.SpreadPat, "Pattern", 92)));

            var velocity = S("4  Velocity", Layout.Row(
                Sw(P.VelOn),
                new ParamKnob(ctx, P.VelCurve, diameter: D), new ParamKnob(ctx, P.OutMin, diameter: D), new ParamKnob(ctx, P.OutMax, diameter: D),
                new ParamKnob(ctx, P.VelRandom, diameter: D), Layout.Gap(8),
                new ParamChoice(ctx, P.VelFixedOn, "Fixed"), new ParamKnob(ctx, P.VelFixed, diameter: D)));
            var length = S("5  Length", Layout.Column(
                Layout.Row(Sw(P.LenOn), Layout.Gap(8), new ParamChoice(ctx, P.LenMode, "Mode", 64)),
                Layout.Row(
                    ParamTempoTime.Create(ctx, new TempoTimeSpec { Mode = P.LenSync, Division = P.LenRate, Feel = P.LenFeel, Rate = P.LenMs, KnobDiameter = D, Wide = true, MaxSeconds = 4 }), Layout.Gap(8),
                    new ParamKnob(ctx, P.ClampMin, diameter: D), new ParamKnob(ctx, P.ClampMax, diameter: D))));

            var echo = S("6  Echo", Layout.Column(
                Layout.Row(
                    Sw(P.EchoOn), Layout.Gap(8),
                    new ParamKnob(ctx, P.Repeats, diameter: D), Layout.Gap(6),
                    ParamTempoTime.Create(ctx, new TempoTimeSpec { Mode = P.EchoSync, Division = P.EchoRate, Feel = P.EchoFeel, Rate = P.EchoMs, KnobDiameter = D, Wide = true, MaxSeconds = 2 })),
                Layout.Row(
                    new ParamKnob(ctx, P.EchoDrop, diameter: D), new ParamKnob(ctx, P.EchoStep, diameter: D), new ParamKnob(ctx, P.EchoLen, diameter: D))));
            var humanize = S("7  Humanize", Layout.Row(
                Sw(P.HumOn),
                new ParamKnob(ctx, P.HumTime, diameter: D), new ParamKnob(ctx, P.HumVel, diameter: D), new ParamKnob(ctx, P.Seed, diameter: D), Layout.Gap(8),
                new ParamChoice(ctx, P.HumChord, "Keep chords")));

            return Layout.Column(
                keys,
                Layout.Row(transpose, scale, spread),
                Layout.Row(velocity, length),
                Layout.Row(echo, humanize));
        }

        /// <summary>The whole keyboard as two rows of keys: the notes held on the input above, the notes the machine is sounding below.</summary>
        static void DrawStrip(INeoCanvas g, NeoRect r, NotesEngine e)
        {
            double w = (r.Width - 8) / 128.0, x0 = r.X + 4, h = 15, yIn = r.Y + 14, yOut = yIn + h + 3;
            g.Text("held on the input", x0, r.Y, 9, Palette.DimText, false, NeoAlign.Left);
            g.Text("sounding on the output", x0 + 160, r.Y, 9, Palette.DimText, false, NeoAlign.Left);
            var held = AccentColor.Tint(0.35);
            for (int p = 0; p < 128; p++)
            {
                double x = x0 + p * w;
                var idle = IsBlack[p % 12] ? Palette.Background : Palette.PanelEdge;
                g.Rect(new NeoRect(x, yIn, Math.Max(1, w - 1), h), e != null && e.IsHeld(p) ? held : idle, default, 1);
                g.Rect(new NeoRect(x, yOut, Math.Max(1, w - 1), h), e != null && e.IsSounding(p) ? AccentColor : idle, default, 1);
                if (p % 12 == 0 && p % 24 == 0) g.Text("C" + (p / 12 - 1), x, yOut + h + 1, 8, Palette.DimText, false, NeoAlign.Left);
            }
        }
    }
}
