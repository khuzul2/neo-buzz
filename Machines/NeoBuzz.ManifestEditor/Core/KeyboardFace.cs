#nullable disable
// Fork (machine keyboard, docs/specs/studio-mvp.md §3.12): the keyboard under a machine's window, its
// toolkit-free part on the Neo editor kit: which keys fit a width (two octaves at least, more as the
// window widens), where each key is, which key is under the pointer (black keys over white ones),
// the velocity a strike's height gives (top soft, bottom loud), the labels (each C in scientific names,
// C4 = MIDI 60, as the Neo machines and the agent server name pitches; a drum machine's keys by their
// drums, shortened to fit) and how each key is drawn. The controls (Keyboard/MachineKeyboard.cs, one
// source for WPF and Avalonia) hold the state and route the pointer here. Engine-free: it also
// compiles on Linux, in NeoBuzz.MachineTests.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NeoBuzz.Editor
{
    /// <summary>Where a keyboard's keys are, for a range of octaves across a width.</summary>
    internal sealed class KeyboardLayout
    {
        /// <summary>The strip over the keys (the toggle, the octave, what is played) and the keys under it.</summary>
        public const double BarHeight = 24, KeysHeight = 76, Height = BarHeight + KeysHeight;
        /// <summary>The white key width aimed at, and the least; a drum machine's keys are wider, room for their drums' names.</summary>
        public const double TargetWhite = 24, MinWhite = 13, KitWhite = 40;
        public const int MinOctaves = 2, MaxOctaves = 8;
        /// <summary>A black key's height and width, as shares of a white key's.</summary>
        public const double BlackHeight = 0.62, BlackWidth = 0.6;
        /// <summary>The softest strike, at a key's top; its bottom strikes at 127.</summary>
        public const int SoftestVelocity = 12;

        // Each black key's centre against the edge between the white keys it sits on, in white keys (a piano's look).
        static readonly double[] BlackShift = { 0, -0.08, 0, 0.08, 0, 0, -0.1, 0, 0, 0, 0.1, 0 };
        // The white key a note is, or, for a black key, the white key after it, whose left edge it sits on (0 = C … 6 = B).
        static readonly int[] WhiteIndex = { 0, 1, 1, 2, 2, 3, 4, 4, 5, 5, 6, 6 };

        /// <param name="first">The lowest key, a C.</param>
        public KeyboardLayout(int first, int octaves, double width, double keysHeight = KeysHeight)
        {
            Octaves = Math.Clamp(octaves, 1, 10);
            First = Math.Clamp(first - first % 12, 0, 120);
            // C to C: the octaves and the top C, when MIDI goes that high (G9 is MIDI's last).
            Last = Math.Min(127, First + 12 * Octaves);
            Width = Math.Max(1, width);
            KeysHeightValue = Math.Max(1, keysHeight);
            Whites = 0;
            for (int n = First; n <= Last; n++) if (!IsBlack(n)) Whites++;
            WhiteWidth = Width / Whites;
        }

        public int First { get; }
        public int Last { get; }
        public int Octaves { get; }
        public double Width { get; }
        public double KeysHeightValue { get; }
        public int Whites { get; }
        public double WhiteWidth { get; }

        /// <summary>The octaves that fit a width: as many as keep the white keys near <see cref="TargetWhite"/>, two at least.</summary>
        public static int OctavesFor(double width, double white = TargetWhite) =>
            Math.Clamp((int)Math.Floor((width - white) / (7 * white)), MinOctaves, MaxOctaves);

        /// <summary>
        /// A drum machine's octaves: enough to show all its drums (by name) where they fit, its keys near <see cref="KitWhite"/>
        /// when the width allows more, never more than <see cref="OctavesFor"/> gives.
        /// </summary>
        public static int OctavesForKit(double width, IReadOnlyDictionary<int, string> names)
        {
            int most = OctavesFor(width);
            int needed = MinOctaves;
            if (names != null && names.Count > 0)
            {
                int distinct = new HashSet<string>(names.Values).Count;
                for (needed = MinOctaves; needed < most; needed++)
                    if (BestFirstForKit(names, needed).Shown >= distinct) break;
            }
            return Math.Clamp(Math.Max(needed, OctavesFor(width, KitWhite)), MinOctaves, most);
        }

        /// <summary>The lowest C that shows the most of a drum machine's drums (by name) in so many octaves, the higher on a tie.</summary>
        public static (int First, int Shown) BestFirstForKit(IReadOnlyDictionary<int, string> names, int octaves)
        {
            int best = -1, most = -1, low = int.MaxValue, high = 0;
            foreach (var k in names.Keys) { low = Math.Min(low, k); high = Math.Max(high, k); }
            if (names.Count == 0) return (ClampFirst(60, octaves), 0);
            for (int c = ClampFirst(low, octaves); c <= high; c += 12)
            {
                int f = ClampFirst(c, octaves);
                var shown = new HashSet<string>();
                foreach (var (k, n) in names) if (k >= f && k <= f + 12 * octaves) shown.Add(n);
                if (shown.Count >= most) { most = shown.Count; best = f; }
                if (f != c) break;          // clamped: the top of MIDI's range
            }
            return (best, most);
        }

        /// <summary>The narrowest keyboard: two octaves and the top C of the narrowest keys.</summary>
        public static double MinWidth => (MinOctaves * 7 + 1) * MinWhite;

        /// <summary>
        /// The lowest C to show for a wish (a C or not), so that the range stays within 0–127 and shows
        /// <paramref name="octaves"/> octaves.
        /// </summary>
        public static int ClampFirst(int first, int octaves)
        {
            // Whole octaves only: the highest C whose octaves end at 127 or below.
            int top = Math.Max(0, Math.Min(120, 128 - 12 * Math.Max(1, octaves)));
            top -= top % 12;
            int c = first - ((first % 12) + 12) % 12;
            return Math.Clamp(c, 0, top);
        }

        public static bool IsBlack(int note) => ((0b0101_0100_1010 >> (((note % 12) + 12) % 12)) & 1) != 0;

        public bool Contains(int note) => note >= First && note <= Last;

        /// <summary>The white keys before a white note's, from the first key.</summary>
        int WhitesBefore(int note) => (note / 12 - First / 12) * 7 + WhiteIndex[note % 12];

        /// <summary>A key's rectangle (black keys over the top of the white ones).</summary>
        public NeoRect Rect(int note)
        {
            if (!IsBlack(note))
                return new NeoRect(WhitesBefore(note) * WhiteWidth, 0, WhiteWidth, KeysHeightValue);
            double edge = (WhitesBefore(note) + BlackShift[note % 12]) * WhiteWidth;
            double w = WhiteWidth * BlackWidth;
            return new NeoRect(edge - w / 2, 0, w, KeysHeightValue * BlackHeight);
        }

        /// <summary>The key under a point, or −1: a black key where one is, else the white key.</summary>
        public int KeyAt(double x, double y)
        {
            if (x < 0 || y < 0 || x >= Width || y > KeysHeightValue) return -1;
            if (y <= KeysHeightValue * BlackHeight)
            {
                int white = (int)(x / WhiteWidth);
                int octave = white / 7, index = white % 7;
                // The black keys beside this white key.
                for (int d = -1; d <= 1; d++)
                {
                    int note = First + octave * 12 + WhiteNote(index) + d;
                    if (note >= First && note <= Last && IsBlack(note) && Rect(note).Contains(x, y)) return note;
                }
            }
            int w = Math.Clamp((int)(x / WhiteWidth), 0, Whites - 1);
            int n = First + (w / 7) * 12 + WhiteNote(w % 7);
            return n <= Last ? n : -1;
        }

        static int WhiteNote(int index) => index switch { 0 => 0, 1 => 2, 2 => 4, 3 => 5, 4 => 7, 5 => 9, _ => 11 };

        /// <summary>The velocity of a strike at <paramref name="y"/> on a key: soft at its top, 127 at its bottom.</summary>
        public int VelocityAt(int note, double y)
        {
            var r = Rect(note);
            double t = Math.Clamp((y - r.Y) / Math.Max(1, r.Height), 0, 1);
            return Math.Clamp((int)Math.Round(SoftestVelocity + (127 - SoftestVelocity) * t), 1, 127);
        }

        /// <summary>The keys, white ones first (black ones draw over them).</summary>
        public IEnumerable<int> Keys()
        {
            for (int n = First; n <= Last; n++) if (!IsBlack(n)) yield return n;
            for (int n = First; n <= Last; n++) if (IsBlack(n)) yield return n;
        }
    }

    /// <summary>How a key looks now.</summary>
    /// <param name="Playable">The machine takes this key (its range, with its pitch offset).</param>
    /// <param name="Velocity">Sounding now (from any source) at this velocity; 0 when silent.</param>
    /// <param name="Pressed">Held by this keyboard.</param>
    /// <param name="Kit">The keyboard names a drum machine's keys: a key without a name plays nothing.</param>
    internal readonly record struct KeyLook(bool Playable, bool Hover, int Velocity, bool Pressed, bool Kit);

    /// <summary>Names and pictures of the keyboard's keys.</summary>
    internal static class KeyboardFace
    {
        /// <summary>
        /// A pitch's name as Preferences name notes (BuzzGUI.Common.NoteNames, docs/specs/ux-overhaul.md §3.5), as the pattern
        /// editor and the piano roll name it: MIDI 60 is "C3" by default, "C4" in scientific naming, "C-5" in Buzz's.
        /// </summary>
        public static string NoteName(int midi) => BuzzGUI.Common.NoteNames.Name(midi);

        /// <summary>A key as a screen reader says it: "C#3", "D1, Snare".</summary>
        public static string AccessibleName(int midi, string drum) => string.IsNullOrEmpty(drum) ? NoteName(midi) : NoteName(midi) + ", " + drum;

        /// <summary>A name that fits a width: whole, its first word, its initials ("Closed hat" → "CH"), or its first letters.</summary>
        public static string Fit(string name, double width, double size, INeoTextMeasure measure)
        {
            if (string.IsNullOrWhiteSpace(name) || width <= 2) return "";
            name = name.Trim();
            if (measure.MeasureText(name, size) <= width) return name;
            var words = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            string fit;
            if (words.Length > 1)
            {
                // Several words: their initials, as drum machines name their voices ("Closed hat" → "CH", "Low tom" → "LT").
                var initials = new StringBuilder();
                foreach (var w in words) initials.Append(char.ToUpperInvariant(w[0]));
                fit = initials.ToString();
            }
            else
            {
                // One word: as much of it as fits ("Cowbell" → "Cowb"), two letters at least.
                fit = name;
                while (fit.Length > 2 && measure.MeasureText(fit, size) > width) fit = fit.Substring(0, fit.Length - 1);
            }
            while (fit.Length > 1 && measure.MeasureText(fit, size) > width) fit = fit.Substring(0, fit.Length - 1);
            return fit;
        }

        // The keys' colours on the dark theme: ivory and ebony that sit with the kit's panels.
        static readonly NeoColor WhiteTop = NeoColor.FromRgb(0xE8, 0xEB, 0xF0), WhiteBottom = NeoColor.FromRgb(0xCC, 0xD1, 0xD9);
        static readonly NeoColor WhiteHover = NeoColor.FromRgb(0xF6, 0xF7, 0xF9);
        static readonly NeoColor WhiteOff = NeoColor.FromRgb(0x7E, 0x84, 0x8E), WhiteUnnamed = NeoColor.FromRgb(0xA4, 0xAA, 0xB4);
        static readonly NeoColor BlackTop = NeoColor.FromRgb(0x30, 0x34, 0x3D), BlackBottom = NeoColor.FromRgb(0x17, 0x19, 0x1E);
        static readonly NeoColor BlackHover = NeoColor.FromRgb(0x44, 0x49, 0x54), BlackOff = NeoColor.FromRgb(0x3A, 0x3D, 0x44);
        static readonly NeoColor Seam = NeoColor.FromArgb(0xB0, 0x15, 0x17, 0x1C);
        static readonly NeoColor LabelOnWhite = NeoColor.FromRgb(0x58, 0x5F, 0x6B), LabelOnBlack = NeoColor.FromRgb(0xC4, 0xC9, 0xD1);

        public const double LabelSize = 9, DrumLabelSize = 8.5;

        /// <summary>
        /// Draws one key in its own box (0, 0, width, height): its face, lit with the accent while it
        /// sounds (from any source), the part down to where it was struck filled (the deeper, the
        /// louder), and its label: the C's name, or its drum.
        /// </summary>
        public static void DrawKey(INeoCanvas c, double width, double height, int note, KeyLook look, string drum, NeoColor accent)
        {
            bool black = KeyboardLayout.IsBlack(note);
            var r = new NeoRect(0, 0, width, height);
            bool silentKey = look.Kit && string.IsNullOrEmpty(drum);      // a drum machine's key that plays no drum
            NeoFill face;
            if (!look.Playable) face = black ? BlackOff : WhiteOff;
            else if (look.Hover && look.Velocity == 0) face = black ? BlackHover : WhiteHover;
            else if (black) face = silentKey ? BlackOff : NeoFill.Vertical(BlackTop, BlackBottom);
            else face = silentKey ? WhiteUnnamed : NeoFill.Vertical(WhiteTop, WhiteBottom);
            double radius = black ? 2 : 3;
            if (black)
            {
                c.Rect(r, face, new NeoStroke(Palette.Background, 1, false), radius);
            }
            else
            {
                c.Rect(new NeoRect(0.5, -radius, width - 1, height + radius - 0.5), face, NeoStroke.None, radius);
                c.Line(new NeoPoint(width - 0.5, 0), new NeoPoint(width - 0.5, height), new NeoStroke(Seam, 1, false));
            }
            if (look.Velocity > 0)
            {
                // Lit: the key tinted, and filled from its top as deep as it was struck (velocity / 127).
                double inset = black ? 1.5 : 2.5;
                var lit = new NeoRect(inset, 0, width - 2 * inset - (black ? 0 : 1), height - inset);
                c.Rect(lit, accent.WithAlpha(look.Pressed ? (byte)0x50 : (byte)0x3C), NeoStroke.None, radius);
                double depth = lit.Height * Math.Clamp(look.Velocity / 127.0, 0.05, 1);
                c.Rect(new NeoRect(lit.X, lit.Y, lit.Width, depth), accent.WithAlpha(look.Pressed ? (byte)0xE0 : (byte)0xB4), NeoStroke.None, radius);
            }
            // The label at the foot of the key.
            if (!string.IsNullOrEmpty(drum))
            {
                string text = Fit(drum, width - 3, DrumLabelSize, c);
                var colour = black ? LabelOnBlack : (look.Velocity > 0 ? Palette.Background : LabelOnWhite);
                c.Text(text, width / 2, height - DrumLabelSize - 6, DrumLabelSize, colour, false, NeoAlign.Center);
            }
            else if (!black && note % 12 == 0)
            {
                var colour = look.Playable ? (look.Velocity > 0 ? Palette.Background : LabelOnWhite) : Palette.DimText;
                c.Text(NoteName(note), width / 2, height - LabelSize - 6, LabelSize, colour, note == 60, NeoAlign.Center);
            }
        }

        /// <summary>A small button of the strip over the keys: outlined, filled while hovered or on.</summary>
        public static void DrawButton(INeoCanvas c, double width, double height, string text, bool hover, bool on, bool enabled, NeoColor accent)
        {
            var r = new NeoRect(0.5, 0.5, width - 1, height - 1);
            var edge = enabled ? (on ? accent : Palette.PanelEdge) : Palette.Grid;
            c.Rect(r, hover && enabled ? accent.WithAlpha(0x38) : on ? accent.WithAlpha(0x22) : Palette.Background, new NeoStroke(edge, 1), 4);
            c.Text(text, width / 2, (height - 14) / 2, 11, enabled ? (on || hover ? accent : Palette.Text) : Palette.DimText, false, NeoAlign.Center);
        }

        /// <summary>The strip's readout: the key under the pointer or played, its drum and its velocity ("D1 · Snare · velocity 96").</summary>
        public static string Readout(int note, string drum, int velocity)
        {
            if (note < 0) return "";
            var s = NoteName(note);
            if (!string.IsNullOrEmpty(drum)) s += " · " + drum;
            if (velocity > 0) s += " · velocity " + velocity.ToString(CultureInfo.InvariantCulture);
            return s;
        }
    }
}
