#nullable disable
// Fork (docs/specs/ux-overhaul.md §3.1, P2a): the design tokens, one table for the studio and every machine editor.
// The studio's StudioTokens reads it, the kit's Palette is made from it on Avalonia (the studio's editors), and machine
// editors read it through the kit: surfaces, text, the one accent, the reserved state colours, meters, an identity
// colour per machine category, graph series, the type scale (Inter) and the 8 pt grid; each colour in a dark and a light
// set (P4b: TokenSet, Tokens.SetLight). Toolkit-free, so the kit's
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

    /// <summary>
    /// One theme's colours (ux-overhaul.md §3.1, §3.3, P4b): every token has a value in <see cref="Tokens.Dark"/> and in
    /// <see cref="Tokens.Light"/>; <see cref="Tokens.Set"/> is the one in use.
    /// </summary>
    internal sealed class TokenSet
    {
        public string Name;
        public NeoColor SurfaceBase, SurfaceView, SurfacePanel, SurfaceControl, SurfaceBorder, GridLine, ControlTrack;
        public NeoColor TextPrimary, TextSecondary, TextPlaceholder, TextDisabled;
        public NeoColor Accent, SelectionFill;
        public NeoColor Play, Record, Override, Warning;
        public NeoColor MeterNormal, MeterHot;
        /// <summary>Text and edges of the problem bar and the notes' banner.</summary>
        public NeoColor Link, ErrorText, SevereBack, NoticeBack, BannerBack, Diverged;
        /// <summary>A knob's face, top and bottom.</summary>
        public NeoColor KnobTop, KnobBottom;
        /// <summary>Partner traces: sky, amber.</summary>
        public NeoColor PartnerSky, PartnerAmber;
        /// <summary>One per <see cref="MachineCategory"/>, in its order.</summary>
        public NeoColor[] Identity;
        /// <summary>Graph series: sky, amber, teal, pink, lime, violet, salmon, orchid.</summary>
        public NeoColor[] Series;

        /// <summary>Every colour of the set, in a fixed order (the tests check each is defined; Resolve pairs them by position).</summary>
        public NeoColor[] All()
        {
            var list = new System.Collections.Generic.List<NeoColor>
            {
                SurfaceBase, SurfaceView, SurfacePanel, SurfaceControl, SurfaceBorder, GridLine, ControlTrack,
                TextPrimary, TextSecondary, TextPlaceholder, TextDisabled, Accent, SelectionFill,
                Play, Record, Override, Warning, MeterNormal, MeterHot,
                Link, ErrorText, SevereBack, NoticeBack, BannerBack, Diverged, KnobTop, KnobBottom, PartnerSky, PartnerAmber,
            };
            list.AddRange(Identity);
            list.AddRange(Series);
            return list.ToArray();
        }

        /// <summary>The names of <see cref="All"/>, for a failing test's message.</summary>
        public static string[] AllNames()
        {
            var names = new System.Collections.Generic.List<string>
            {
                "SurfaceBase", "SurfaceView", "SurfacePanel", "SurfaceControl", "SurfaceBorder", "GridLine", "ControlTrack",
                "TextPrimary", "TextSecondary", "TextPlaceholder", "TextDisabled", "Accent", "SelectionFill",
                "Play", "Record", "Override", "Warning", "MeterNormal", "MeterHot",
                "Link", "ErrorText", "SevereBack", "NoticeBack", "BannerBack", "Diverged", "KnobTop", "KnobBottom", "PartnerSky", "PartnerAmber",
            };
            foreach (var c in System.Enum.GetNames(typeof(MachineCategory))) names.Add("Identity." + c);
            for (int i = 0; i < 8; i++) names.Add("Series." + i);
            return names.ToArray();
        }
    }

    internal static class Tokens
    {
        static NeoColor Rgb(uint rgb) => new NeoColor(0xFF000000u | rgb);

        // ---- the two sets (the dark one seeded by the Session View, §3.1; the light one measured in §3.1.2)

        public static readonly TokenSet Dark = new TokenSet
        {
            Name = "Dark",
            SurfaceBase = Rgb(0x141416), SurfaceView = Rgb(0x1B1B1E), SurfacePanel = Rgb(0x242428), SurfaceControl = Rgb(0x2E2E34), SurfaceBorder = Rgb(0x37373F),
            GridLine = Rgb(0x2A2A30), ControlTrack = Rgb(0x6E6E7A),
            TextPrimary = Rgb(0xE8E8EC), TextSecondary = Rgb(0xA0A0AA), TextPlaceholder = Rgb(0x8C8C96), TextDisabled = Rgb(0x6E6E78),
            Accent = Rgb(0x4C8DF6), SelectionFill = Rgb(0x1F5FA8),
            Play = Rgb(0x34C759), Record = Rgb(0xFF453A), Override = Rgb(0xE8823A), Warning = Rgb(0xFFB340),
            MeterNormal = Rgb(0x7BE495), MeterHot = Rgb(0xFFC43D),
            Link = Rgb(0x6CB4FF), ErrorText = Rgb(0xFF7A70), SevereBack = Rgb(0x4A1F22), NoticeBack = Rgb(0x3D3320), BannerBack = Rgb(0x233A55), Diverged = Rgb(0xC03030),
            KnobTop = Rgb(0x34343A), KnobBottom = Rgb(0x242429),   // one step from the panel, so a value names one token
            PartnerSky = Rgb(0x7EC8FF), PartnerAmber = Rgb(0xEDB235),
            Identity = new[]
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
            },
            Series = new[] { Rgb(0x16BCFF), Rgb(0xEDB235), Rgb(0x0BD1B9), Rgb(0xF34C82), Rgb(0xD1E181), Rgb(0x8777FF), Rgb(0xF8A98D), Rgb(0xE361D3) },
        };

        public static readonly TokenSet Light = new TokenSet
        {
            Name = "Light",
            // never pure white or black: those stay what they are in both themes (text on a colour, Resolve)
            SurfaceBase = Rgb(0xE8E8ED), SurfaceView = Rgb(0xF4F4F7), SurfacePanel = Rgb(0xFCFCFD), SurfaceControl = Rgb(0xE4E4EA), SurfaceBorder = Rgb(0xC8C8D1),
            GridLine = Rgb(0xDCDCE3), ControlTrack = Rgb(0x8A8A97),
            TextPrimary = Rgb(0x1B1B20), TextSecondary = Rgb(0x505059), TextPlaceholder = Rgb(0x626270), TextDisabled = Rgb(0x8E8E99),
            Accent = Rgb(0x0064C7), SelectionFill = Rgb(0x2B6CC4),
            Play = Rgb(0x008A20), Record = Rgb(0xCE0018), Override = Rgb(0xA84700), Warning = Rgb(0x7F6000),
            MeterNormal = Rgb(0x0A8F77), MeterHot = Rgb(0xAB7C00),
            Link = Rgb(0x0A58B8), ErrorText = Rgb(0xB3261E), SevereBack = Rgb(0xFBE3E1), NoticeBack = Rgb(0xFFF1CC), BannerBack = Rgb(0xDCE9FB), Diverged = Rgb(0xB02828),
            KnobTop = Rgb(0xFBFBFD), KnobBottom = Rgb(0xE0E0E8),
            PartnerSky = Rgb(0x1C86C4), PartnerAmber = Rgb(0x895F00),
            Identity = new[]
            {
                Rgb(0xCD004D),      // Synth
                Rgb(0x895F00),      // Drums
                Rgb(0x78589F),      // Sampler
                Rgb(0x007461),      // Dynamics
                Rgb(0x006CA7),      // EQ and filter
                Rgb(0x5D54D6),      // Delay and reverb
                Rgb(0xAE2BA1),      // Modulation
                Rgb(0x9F4F43),      // Distortion
                Rgb(0x626773),      // Utility
                Rgb(0x596F15),      // Control
            },
            Series = new[] { Rgb(0x006CA7), Rgb(0x895F00), Rgb(0x007461), Rgb(0xCD004D), Rgb(0x596F15), Rgb(0x5D54D6), Rgb(0x9F4F43), Rgb(0xAE2BA1) },
        };

        static TokenSet set = Dark;

        /// <summary>The set in use.</summary>
        public static TokenSet Set => set;

        public static bool IsLight => ReferenceEquals(set, Light);

        /// <summary>Raised after <see cref="SetLight"/> changed the set: caches made from colours are dropped, views draw again.</summary>
        public static event Action Changed;

        /// <summary>Uses the light set (true) or the dark one; the studio calls it when the theme changes. The classic WPF app never does.</summary>
        public static void SetLight(bool light)
        {
            var next = light ? Light : Dark;
            if (ReferenceEquals(next, set)) return;
            set = next;
            adapted.Clear();
            Changed?.Invoke();
        }

        // ---- surfaces, darkest first (dark theme; the ramp reverses in light: the base is the deepest grey, a panel the lightest)

        /// <summary>The window and the canvases behind everything.</summary>
        public static NeoColor SurfaceBase => set.SurfaceBase;
        /// <summary>A view, a machine editor's panels.</summary>
        public static NeoColor SurfaceView => set.SurfaceView;
        /// <summary>A panel raised on a view: the inspector, a toolbar, a popover.</summary>
        public static NeoColor SurfacePanel => set.SurfacePanel;
        /// <summary>A control's face; a panel's edge.</summary>
        public static NeoColor SurfaceControl => set.SurfaceControl;
        /// <summary>A border, a hovered control, a strong line.</summary>
        public static NeoColor SurfaceBorder => set.SurfaceBorder;

        /// <summary>Grid lines in graphs and editors: quiet, between the view and a control.</summary>
        public static NeoColor GridLine => set.GridLine;
        /// <summary>A control's unfilled part (a knob's track): 3:1 on a view, as WCAG asks of a control's parts.</summary>
        public static NeoColor ControlTrack => set.ControlTrack;

        // ---- text (each 4.5:1 or more on the base, view and panel surfaces)

        public static NeoColor TextPrimary => set.TextPrimary;
        public static NeoColor TextSecondary => set.TextSecondary;
        /// <summary>A text box's placeholder, a hint.</summary>
        public static NeoColor TextPlaceholder => set.TextPlaceholder;
        /// <summary>A disabled control's text (not read, so not held to 4.5:1).</summary>
        public static NeoColor TextDisabled => set.TextDisabled;

        // ---- the one accent: focus, selection, the default button; never a state

        public static NeoColor Accent => set.Accent;
        /// <summary>A selected row or item's fill (white text on it: 6.4:1 in dark, 5.2:1 in light).</summary>
        public static NeoColor SelectionFill => set.SelectionFill;

        // ---- reserved state colours: never an accent, an identity, or a clip, track or scene colour

        public static NeoColor Play => set.Play;
        public static NeoColor Record => set.Record;
        /// <summary>Something the arrangement plays is overridden (the Session View plays instead).</summary>
        public static NeoColor Override => set.Override;
        /// <summary>Queued to play: the play colour, blinking.</summary>
        public static NeoColor Queued => set.Play;
        /// <summary>A warning (dropouts, a machine guard off).</summary>
        public static NeoColor Warning => set.Warning;
        /// <summary>An error, a problem the user must fix.</summary>
        public static NeoColor Error => set.Record;

        /// <summary>The state colours, in the order the reserved-distance checks compare them.</summary>
        public static NeoColor[] StateColours => new[] { set.Play, set.Record, set.Override };

        /// <summary>A colour this close (ΔE, CIE76) to a state colour isn't offered for clips, tracks, scenes or identities.</summary>
        public const double ReservedDistance = 20;

        // ---- meters

        /// <summary>A level below the hot zone: mint, ΔE 23 from play green (a meter is never a state).</summary>
        public static NeoColor MeterNormal => set.MeterNormal;
        /// <summary>Within 6 dB of full scale.</summary>
        public static NeoColor MeterHot => set.MeterHot;
        /// <summary>Over 0 dB: clipping.</summary>
        public static NeoColor MeterOver => set.Record;

        // ---- identity: a colour per machine category, the accent of its editor (and, from P3h, of its box and track)

        /// <summary>
        /// A category's identity colour: each 5:1 or more on a view in dark, 4.5:1 or more in light (it can be text), 37 (dark) or
        /// 31 (light) or more (ΔE) from every other, 24 or more from the accent and the state colours.
        /// </summary>
        public static NeoColor Identity(MachineCategory category) => set.Identity[(int)category];

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

        public static NeoColor[] Series => set.Series;

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
            var sky = set.PartnerSky;
            var amber = set.PartnerAmber;
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

        // ---- one colour in either theme (P4b)

        static readonly System.Collections.Generic.Dictionary<uint, NeoColor> adapted = new System.Collections.Generic.Dictionary<uint, NeoColor>();
        static System.Collections.Generic.Dictionary<uint, int> darkSlots, lightSlots;
        static NeoColor[] darkAll, lightAll;

        static void EnsureSlots()
        {
            if (darkSlots != null) return;
            darkAll = Dark.All();
            lightAll = Light.All();
            var d = new System.Collections.Generic.Dictionary<uint, int>();
            var l = new System.Collections.Generic.Dictionary<uint, int>();
            for (int i = 0; i < darkAll.Length; i++) d.TryAdd(darkAll[i].Argb & 0xFFFFFF, i);
            for (int i = 0; i < lightAll.Length; i++) l.TryAdd(lightAll[i].Argb & 0xFFFFFF, i);
            lightSlots = l;
            darkSlots = d;
        }

        static readonly System.Collections.Generic.HashSet<uint> kept = new System.Collections.Generic.HashSet<uint>();

        /// <summary>Colours that are the same in both themes (a keyboard's ivory and ebony): <see cref="Resolve"/> leaves them as they are.</summary>
        public static void KeepAsIs(params NeoColor[] colours)
        {
            lock (kept)
                foreach (var c in colours) kept.Add(c.Argb & 0xFFFFFF);
        }

        /// <summary>
        /// A colour as the theme in use draws it. A token of either set becomes the same token of the set in use (an editor that
        /// kept a colour from before the theme changed draws it right); pure white and black stay (text on a colour); any other
        /// colour an editor chose for itself is left as it is in dark, and in light it is adapted: a neutral or dark tint
        /// turns its lightness over (a dark panel becomes a pale one), a bright colour darkens until it is 3:1 on the light
        /// view. The alpha stays.
        /// </summary>
        public static NeoColor Resolve(NeoColor c)
        {
            if (c.A == 0) return c;
            uint rgb = c.Argb & 0xFFFFFF;
            if (rgb == 0 || rgb == 0xFFFFFF) return c;
            lock (kept)
                if (kept.Contains(rgb)) return c;
            EnsureSlots();
            bool light = IsLight;
            if (darkSlots.TryGetValue(rgb, out int i)) return light ? lightAll[i].WithAlpha(c.A) : c;
            if (lightSlots.TryGetValue(rgb, out i)) return light ? c : darkAll[i].WithAlpha(c.A);
            if (!light) return c;
            lock (adapted)
            {
                if (!adapted.TryGetValue(rgb, out var a))
                {
                    a = AdaptToLight(new NeoColor(0xFF000000u | rgb));
                    adapted[rgb] = a;
                }
                return a.WithAlpha(c.A);
            }
        }

        /// <summary>A dark-theme colour no token names, for the light theme (see <see cref="Resolve"/>).</summary>
        internal static NeoColor AdaptToLight(NeoColor c)
        {
            var (l, a, b) = Lab(c);
            double chroma = Math.Sqrt(a * a + b * b), hue = Math.Atan2(b, a) * 180 / Math.PI;
            if (chroma < 14)
                return FromLab(Math.Clamp(100 - l, 8, 97), a, b);
            if (l < 38)
                return FromLab(Math.Clamp(100 - l, 55, 95), a * 0.6, b * 0.6);
            var view = Light.SurfaceView;
            var best = c;
            for (double lightness = l; lightness > 2 && ContrastOf(best, view) < 3; lightness -= 1)
                best = FromLab(lightness, chroma * Math.Cos(hue * Math.PI / 180), chroma * Math.Sin(hue * Math.PI / 180));
            return best;
        }

        static NeoColor FromLab(double l, double a, double b)
        {
            double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
            static double Inv(double t) => t * t * t > 0.008856 ? t * t * t : (t - 16.0 / 116.0) / 7.787;
            double x = Inv(fx) * 0.95047, y = Inv(fy), z = Inv(fz) * 1.08883;
            double r = 3.2406 * x - 1.5372 * y - 0.4986 * z, g = -0.9689 * x + 1.8758 * y + 0.0415 * z, bl = 0.0557 * x - 0.2040 * y + 1.0570 * z;
            static byte Gamma(double v)
            {
                v = Math.Clamp(v, 0, 1);
                return (byte)Math.Round((v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055) * 255);
            }
            return NeoColor.FromRgb(Gamma(r), Gamma(g), Gamma(bl));
        }

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
