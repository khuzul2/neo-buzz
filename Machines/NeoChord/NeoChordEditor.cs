#nullable disable
// Neo Chord's editor (docs/specs/neo-chord.md): the chord type and the mode (Fixed or Diatonic, with its key and scale) on top, a three-octave keyboard that
// lights the notes of the last chord sent (brighter while they sound), then Voicing, Velocity and Strum. One source for WPF and Avalonia (the Neo editor kit);
// everything draws through INeoCanvas.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Chord
{
    internal sealed class NeoChordEditor : NeoEditor
    {
        // The Control identity in the studio, its own periwinkle in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Control, NeoColor.FromRgb(0x8C, 0x9C, 0xF0));
        static readonly NeoColor WhiteKey = NeoColor.FromRgb(0xE6, 0xE8, 0xEE), BlackKey = NeoColor.FromRgb(0x23, 0x25, 0x2B), KeyEdge = NeoColor.FromRgb(0x6A, 0x70, 0x7E);
        static readonly bool[] IsBlack = { false, true, false, true, false, false, true, false, true, false, true, false };
        static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        const int Octaves = 3, WhiteKeys = 7 * Octaves;
        const double KeyW = 19, KeyboardHeight = 74, BlackH = 44;

        protected override string Title => "Neo Chord";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoChordMachine;
            var engine = machine?.Engine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            // Chord type and mode on top; the Diatonic key and scale and the Custom intervals beside them.
            var chord = S("Chord", Layout.Row(
                new ParamDropdown(ctx, P.Chord, "Chord", 150),
                Layout.Gap(10),
                new ParamChoice(ctx, P.Mode, "Mode", 64)));
            var diatonic = S("Diatonic", Layout.Row(
                new ParamDropdown(ctx, P.Key, "Key", 66),
                Layout.Gap(6),
                new ParamDropdown(ctx, P.Scale, "Scale", 130),
                Layout.Gap(6),
                new ParamChoice(ctx, P.DiaSize, "Chord", 60)));
            var custom = S("Custom chord", Build.Knobs(ctx, 4, 40, P.Custom1, P.Custom2, P.Custom3, P.Custom4));

            // The keyboard: three octaves from the C below the lowest note of the last chord.
            var keys = new GraphView(WhiteKeys * KeyW + 2, KeyboardHeight + 16, (g, r) => DrawKeys(g, r, engine), animate: true);
            var board = S("Chord sent", keys);

            var voicing = S("Voicing", Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.Spread, "Spread", 54), Layout.Gap(10), new ParamChoice(ctx, P.Bass, "Bass note", 70)),
                Layout.Gap(1, 6),
                Layout.Row(new ParamKnob(ctx, P.Inversion, diameter: 44), new ParamKnob(ctx, P.Octave, diameter: 44), new ParamKnob(ctx, P.Voices, diameter: 44))));
            var velocity = S("Velocity", Layout.Row(
                new ParamKnob(ctx, P.VelTop, diameter: 44), new ParamKnob(ctx, P.VelBass, diameter: 44), new ParamKnob(ctx, P.VelSpread, diameter: 44)));
            var strum = S("Strum", Layout.Column(
                new ParamChoice(ctx, P.Strum, "Strum", 58),
                Layout.Gap(1, 6),
                Layout.Row(new ParamKnob(ctx, P.StrumTime, diameter: 44), new ParamKnob(ctx, P.StrumCurve, diameter: 44),
                    new ParamKnob(ctx, P.HumTime, diameter: 44), new ParamKnob(ctx, P.HumVel, diameter: 44), new ParamKnob(ctx, P.Seed, diameter: 44))));

            return Layout.Column(
                Layout.Row(chord, diatonic, custom),
                board,
                Layout.Row(voicing, velocity, strum));
        }

        /// <summary>The first note shown: the C at or below the lowest note of the last chord (and the window kept on the keyboard), the C below middle C when nothing has been sent.</summary>
        static int FirstKey(ChordEngine e)
        {
            int lowest = -1;
            if (e != null)
            {
                for (int n = 0; n < 128 && lowest < 0; n++) if (Lit(e, n)) lowest = n;
            }
            if (lowest < 0) return 48;
            return Math.Clamp(lowest / 12 * 12, 0, 128 - 12 * Octaves);
        }

        static bool Lit(ChordEngine e, int note) => note < 64 ? (e.LastLow >> note & 1UL) != 0 : (e.LastHigh >> (note - 64) & 1UL) != 0;

        static void DrawKeys(INeoCanvas g, NeoRect r, ChordEngine e)
        {
            int first = FirstKey(e);
            var lit = AccentColor;
            var sounding = NeoColor.FromRgb((byte)Math.Min(255, lit.R + 60), (byte)Math.Min(255, lit.G + 60), (byte)Math.Min(255, lit.B + 40));
            double top = r.Y + 14;
            int index = 0;
            // White keys first.
            for (int note = first; note < first + 12 * Octaves; note++)
            {
                int pc = note % 12;
                if (IsBlack[pc]) continue;
                var cell = new NeoRect(r.X + 1 + index * KeyW, top, KeyW - 1, KeyboardHeight);
                bool on = e != null && Lit(e, note), ring = e != null && e.IsSounding(note);
                g.Rect(cell, on ? (ring ? sounding : lit) : WhiteKey, new NeoStroke(KeyEdge, 1, false), 2);
                if (pc == 0) g.Text("C" + (note / 12 - 1), cell.X + KeyW / 2, cell.Bottom - 14, 8, on ? NeoColor.FromRgb(0x14, 0x16, 0x1C) : NeoColor.FromRgb(0x44, 0x4A, 0x58), false, NeoAlign.Center);
                index++;
            }
            // Then the black ones across them.
            index = 0;
            for (int note = first; note < first + 12 * Octaves; note++)
            {
                int pc = note % 12;
                if (!IsBlack[pc]) { index++; continue; }
                var cell = new NeoRect(r.X + 1 + index * KeyW - KeyW * 0.3, top, KeyW * 0.6, BlackH);
                bool on = e != null && Lit(e, note), ring = e != null && e.IsSounding(note);
                g.Rect(cell, on ? (ring ? sounding : lit) : BlackKey, new NeoStroke(KeyEdge, 1, false), 2);
            }
            // The notes of the chord, as names, above the keys.
            string text = "";
            if (e != null)
                for (int n = 0; n < 128; n++)
                    if (Lit(e, n)) text += (text.Length > 0 ? " " : "") + Names[n % 12] + (n / 12 - 1);
            g.Text(text.Length > 0 ? text : "play a note: the chord it makes lights here", r.X + 2, r.Y, 10, Palette.DimText, false, NeoAlign.Left, r.Width - 4);
        }
    }
}
