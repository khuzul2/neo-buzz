#nullable disable
// Fork (docs/specs/ux-overhaul.md §3.1, P2a): the design tokens, one table for the studio and every machine editor.
// The studio's StudioTokens reads it, the kit's Palette is made from it on Avalonia (the studio's editors), and machine
// editors read it through the kit: surfaces, text, the one accent, the reserved state colours, meters, an identity
// colour per machine category, graph series, the type scale (Inter) and the 8 pt grid. Toolkit-free, so the kit's
// core, the machines and the tests share it. On WPF (the classic app) the kit and the editors keep their own colours:
// WPF gets bug fixes only (§1), so Palette and Identity(category, classic) answer with the classic ones there.
//
// Adoption, for a machine editor (ux-overhaul.md §6): no literal colours: surfaces, text and lines from here (through
// the kit's panels, knobs, meters and graphs); its accent Identity(its category, its old accent); a second trace
// Partner(accent); graph lines Series; no state colour (play green, record red, override orange) as an accent; text
// 11 pt or more, at TextSecondary or brighter on a panel; sizes and gaps on the 8 pt grid.

using System;

namespace NeoBuzz.Editor
{
    /// <summary>What a machine is, for its identity colour: the machine browser's categories (ux-overhaul.md P3d).</summary>
    internal enum MachineCategory
    {
        Synth,
        Drums,
        Sampler,
        Dynamics,
        EqFilter,
        DelayReverb,
        Modulation,
        Distortion,
        Utility,
        Control,
    }

    internal static class Tokens
    {
        static NeoColor Rgb(uint rgb) => new NeoColor(0xFF000000u | rgb);

        // ---- surfaces, darkest first (dark theme)

        /// <summary>The window and the canvases behind everything.</summary>
        public static readonly NeoColor SurfaceBase = Rgb(0x141416);
        /// <summary>A view, a machine editor's panels.</summary>
        public static readonly NeoColor SurfaceView = Rgb(0x1B1B1E);
        /// <summary>A panel raised on a view: the inspector, a toolbar, a popover.</summary>
        public static readonly NeoColor SurfacePanel = Rgb(0x242428);
        /// <summary>A control's face; a panel's edge.</summary>
        public static readonly NeoColor SurfaceControl = Rgb(0x2E2E34);
        /// <summary>A border, a hovered control, a strong line.</summary>
        public static readonly NeoColor SurfaceBorder = Rgb(0x37373F);

        /// <summary>Grid lines in graphs and editors: quiet, between the view and a control.</summary>
        public static readonly NeoColor GridLine = Rgb(0x2A2A30);
        /// <summary>A control's unfilled part (a knob's track): 3:1 on a view, as WCAG asks of a control's parts.</summary>
        public static readonly NeoColor ControlTrack = Rgb(0x6E6E7A);

        // ---- text (each 4.5:1 or more on the base, view and panel surfaces)

        public static readonly NeoColor TextPrimary = Rgb(0xE8E8EC);
        public static readonly NeoColor TextSecondary = Rgb(0xA0A0AA);
        /// <summary>A text box's placeholder, a hint.</summary>
        public static readonly NeoColor TextPlaceholder = Rgb(0x8C8C96);
        /// <summary>A disabled control's text (not read, so not held to 4.5:1).</summary>
        public static readonly NeoColor TextDisabled = Rgb(0x6E6E78);

        // ---- the one accent: focus, selection, the default button; never a state

        public static readonly NeoColor Accent = Rgb(0x4C8DF6);
        /// <summary>A selected row or item's fill (white text on it 6.4:1).</summary>
        public static readonly NeoColor SelectionFill = Rgb(0x1F5FA8);

        // ---- reserved state colours: never an accent, an identity, or a clip, track or scene colour

        public static readonly NeoColor Play = Rgb(0x34C759);
        public static readonly NeoColor Record = Rgb(0xFF453A);
        /// <summary>Something the arrangement plays is overridden (the Session View plays instead).</summary>
        public static readonly NeoColor Override = Rgb(0xE8823A);
        /// <summary>Queued to play: the play colour, blinking.</summary>
        public static readonly NeoColor Queued = Play;
        /// <summary>A warning (dropouts, a machine guard off).</summary>
        public static readonly NeoColor Warning = Rgb(0xFFB340);
        /// <summary>An error, a problem the user must fix.</summary>
        public static readonly NeoColor Error = Record;

        /// <summary>The state colours, in the order the reserved-distance checks compare them.</summary>
        public static readonly NeoColor[] StateColours = { Play, Record, Override };

        /// <summary>A colour this close (ΔE, CIE76) to a state colour isn't offered for clips, tracks, scenes or identities.</summary>
        public const double ReservedDistance = 20;

        // ---- meters

        /// <summary>A level below the hot zone: mint, ΔE 23 from play green (a meter is never a state).</summary>
        public static readonly NeoColor MeterNormal = Rgb(0x7BE495);
        /// <summary>Within 6 dB of full scale.</summary>
        public static readonly NeoColor MeterHot = Rgb(0xFFC43D);
        /// <summary>Over 0 dB: clipping.</summary>
        public static readonly NeoColor MeterOver = Record;

        // ---- identity: a colour per machine category, the accent of its editor (and, from P3h, of its box and track)

        static readonly NeoColor[] identity =
        {
            Rgb(0xF34C82),      // Synth: pink
            Rgb(0xEDB235),      // Drums: amber
            Rgb(0xCFAAF8),      // Sampler: lavender
            Rgb(0x0BD1B9),      // Dynamics: teal
            Rgb(0x16BCFF),      // EQ and filter: sky
            Rgb(0x8777FF),      // Delay and reverb: violet
            Rgb(0xE361D3),      // Modulation: orchid
            Rgb(0xF8A98D),      // Distortion: salmon
            Rgb(0xA8B4C4),      // Utility: slate
            Rgb(0xD1E181),      // Control: lime
        };

        /// <summary>
        /// A category's identity colour: each 5:1 or more on a view (it can be text), 37 or more (ΔE) from every other,
        /// 24 or more from the accent and the state colours.
        /// </summary>
        public static NeoColor Identity(MachineCategory category) => identity[(int)category];

        /// <summary>
        /// An editor's accent: its category's identity in the studio (Avalonia), its <paramref name="classic"/> colour in the
        /// classic WPF app, which keeps its look (ux-overhaul.md §1).
        /// </summary>
        public static NeoColor Identity(MachineCategory category, NeoColor classic)
        {
#if AVALONIA
            return Identity(category);
#else
            return classic;
#endif
        }

        /// <summary>
        /// A machine's category from its manifest's kind and type ("generator", "monosynth"; "effect", "reverb"): the
        /// manifest editor's accent.
        /// </summary>
        public static MachineCategory CategoryOf(bool generator, string type)
        {
            string t = (type ?? "").ToLowerInvariant();
            if (t.Contains("drum") || t.Contains("triggered")) return MachineCategory.Drums;
            if (t.Contains("sampler")) return MachineCategory.Sampler;
            if (t.Contains("control")) return MachineCategory.Control;
            if (t.Contains("synth")) return MachineCategory.Synth;
            if (t.Contains("dynamic") || t.Contains("compress") || t.Contains("limit")) return MachineCategory.Dynamics;
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"\beq\b") || t.Contains("filter")) return MachineCategory.EqFilter;
            if (t.Contains("delay") || t.Contains("reverb") || t.Contains("echo")) return MachineCategory.DelayReverb;
            if (t.Contains("modulation") || t.Contains("chorus") || t.Contains("flanger") || t.Contains("phaser")) return MachineCategory.Modulation;
            if (t.Contains("distortion") || t.Contains("exciter") || t.Contains("drive") || t.Contains("saturat")) return MachineCategory.Distortion;
            return generator ? MachineCategory.Synth : MachineCategory.Utility;
        }

        // ---- graph series: lines and bands of one graph (an EQ's bands, a multiband's crossovers), in this order

        public static readonly NeoColor[] Series =
        {
            Rgb(0x16BCFF), Rgb(0xEDB235), Rgb(0x0BD1B9), Rgb(0xF34C82), Rgb(0xD1E181), Rgb(0x8777FF), Rgb(0xF8A98D), Rgb(0xE361D3),
        };

        /// <summary>
        /// A graph's band colours: the first <paramref name="classic"/>.Length series in the studio, the
        /// <paramref name="classic"/> colours in the classic WPF app.
        /// </summary>
        public static NeoColor[] Bands(NeoColor[] classic)
        {
#if AVALONIA
            var bands = new NeoColor[classic.Length];
            for (int i = 0; i < bands.Length; i++) bands[i] = Series[i % Series.Length];
            return bands;
#else
            return classic;
#endif
        }

        /// <summary>A second trace beside an accent (a stereo pair's right side): sky, or amber when the accent is blue.</summary>
        public static NeoColor Partner(NeoColor accent)
        {
            var sky = Rgb(0x7EC8FF);
            var amber = Rgb(0xEDB235);
            return DeltaE(accent, sky) >= DeltaE(accent, amber) ? sky : amber;
        }

        /// <summary>As <see cref="Partner(NeoColor)"/> in the studio; <paramref name="classic"/> in the classic WPF app.</summary>
        public static NeoColor Partner(NeoColor accent, NeoColor classic)
        {
#if AVALONIA
            return Partner(accent);
#else
            return classic;
#endif
        }

        // ---- type: Inter, one scale

        /// <summary>The UI's typeface (the studio bundles it: Avalonia.Fonts.Inter).</summary>
        public const string FontFamily = "Inter";
        /// <summary>The smallest text anywhere: captions, units, a knob's value.</summary>
        public const double FontCaption = 11;
        /// <summary>Labels, list rows, menus.</summary>
        public const double FontBody = 13;
        /// <summary>A section's or a panel's title.</summary>
        public const double FontTitle = 15;
        /// <summary>A view's or a dialog's heading.</summary>
        public const double FontHeading = 20;

        // ---- space: the 8 pt grid (4 for the half steps inside a control)

        public const double Grid = 8;
        public const double HalfGrid = 4;
        /// <summary>The smallest control (a chip, a header button).</summary>
        public const double MinControl = 20;
        /// <summary>A pointer target: 24 pt or more.</summary>
        public const double HitTarget = 24;
        public const double RadiusSmall = 4;
        public const double RadiusMedium = 6;
        public const double RadiusLarge = 8;

        /// <summary>A length on the grid: <paramref name="steps"/> × 8 pt (halves allowed: 0.5 is 4 pt).</summary>
        public static double Space(double steps) => steps * Grid;

        // ---- WCAG 2.2 and colour distance

        static double Linear(byte v)
        {
            double c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        /// <summary>WCAG relative luminance of an opaque colour.</summary>
        public static double Luminance(NeoColor c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

        /// <summary>WCAG contrast ratio of two opaque colours (1 to 21).</summary>
        public static double ContrastOf(NeoColor a, NeoColor b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        static readonly NeoColor white = Rgb(0xFFFFFF), black = Rgb(0x000000);

        /// <summary>Text on a colour: pure white or pure black, whichever has more contrast (always 4.58:1 or more).</summary>
        public static NeoColor TextOn(NeoColor background) => ContrastOf(background, white) >= ContrastOf(background, black) ? white : black;

        static (double L, double A, double B) Lab(NeoColor c)
        {
            double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);
            double x = (r * 0.4124 + g * 0.3576 + b * 0.1805) / 0.95047;
            double y = r * 0.2126 + g * 0.7152 + b * 0.0722;
            double z = (r * 0.0193 + g * 0.1192 + b * 0.9505) / 1.08883;
            static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116.0;
            double fx = F(x), fy = F(y), fz = F(z);
            return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
        }

        /// <summary>Colour distance ΔE (CIE76, D65).</summary>
        public static double DeltaE(NeoColor a, NeoColor b)
        {
            var p = Lab(a);
            var q = Lab(b);
            return Math.Sqrt((p.L - q.L) * (p.L - q.L) + (p.A - q.A) * (p.A - q.A) + (p.B - q.B) * (p.B - q.B));
        }

        /// <summary>Whether a colour is too close to a state colour to be an accent, an identity or a clip colour.</summary>
        public static bool IsReservedForState(NeoColor c)
        {
            foreach (var s in StateColours)
                if (DeltaE(c, s) < ReservedDistance) return true;
            return false;
        }
    }
}
