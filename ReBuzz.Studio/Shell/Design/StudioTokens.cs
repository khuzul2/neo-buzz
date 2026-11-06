// Fork (docs/specs/ux-overhaul.md §3): the studio's design tokens, one table for every view: surfaces,
// text, the one accent, the reserved state colours, and the contrast and size floor. P1 uses the state,
// text and contrast parts; P2 moves the values into the editor kit's toolkit-free core so the machine
// editors read the same table.

using System;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ReBuzz.Studio.Shell.Design
{
    internal static class StudioTokens
    {
        // ---- surfaces (dark ramp; ux-overhaul.md §3.1)
        public static readonly Color SurfaceBase = Color.Parse("#141416");
        public static readonly Color SurfaceView = Color.Parse("#1B1B1E");
        public static readonly Color SurfacePanel = Color.Parse("#242428");
        public static readonly Color SurfaceControl = Color.Parse("#2E2E34");
        public static readonly Color SurfaceBorder = Color.Parse("#37373F");

        // ---- text
        public static readonly Color TextPrimary = Color.Parse("#E8E8EC");
        public static readonly Color TextSecondary = Color.Parse("#A0A0AA");
        public static readonly Color TextPlaceholder = Color.Parse("#8C8C96");

        // ---- the one accent (focus and selection, never a state)
        public static readonly Color Accent = Color.Parse("#4C8DF6");
        public static readonly Color SelectionFill = Color.Parse("#1F5FA8");

        // ---- reserved state colours: never offered as clip, track or scene colours
        public static readonly Color Play = Color.Parse("#34C759");
        public static readonly Color Record = Color.Parse("#FF453A");
        public static readonly Color Override = Color.Parse("#E8823A");

        /// <summary>The state colours, in the order the palette check compares them.</summary>
        public static readonly Color[] StateColours = { Play, Record, Override };

        /// <summary>A palette colour this close (ΔE, CIE76) to a state colour isn't offered as a clip colour.</summary>
        public const double ReservedDistance = 20;

        // ---- size floor (points)
        public const double MinFontSize = 11;
        public const double MinControl = 20;
        public const double HitTarget = 24;

        // ---- brushes
        public static readonly IBrush SurfaceViewBrush = Brush(SurfaceView);
        public static readonly IBrush SurfacePanelBrush = Brush(SurfacePanel);
        public static readonly IBrush SurfaceControlBrush = Brush(SurfaceControl);
        public static readonly IBrush SurfaceBorderBrush = Brush(SurfaceBorder);
        public static readonly IBrush TextPrimaryBrush = Brush(TextPrimary);
        public static readonly IBrush TextSecondaryBrush = Brush(TextSecondary);
        public static readonly IBrush TextPlaceholderBrush = Brush(TextPlaceholder);
        public static readonly IBrush AccentBrush = Brush(Accent);
        public static readonly IBrush SelectionFillBrush = Brush(SelectionFill);
        public static readonly IBrush PlayBrush = Brush(Play);
        public static readonly IBrush RecordBrush = Brush(Record);
        public static readonly IBrush OverrideBrush = Brush(Override);
        public static readonly IBrush White = Brush(Colors.White);
        public static readonly IBrush Black = Brush(Colors.Black);

        /// <summary>The block selection of the grids: the accent at 35 %.</summary>
        public static readonly IBrush BlockSelectionFill = Brush(Color.FromArgb(0x59, Accent.R, Accent.G, Accent.B));

        public static IBrush Brush(Color c) => new ImmutableSolidColorBrush(c);

        // ---- WCAG 2.2

        static double Linear(byte v)
        {
            double c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        /// <summary>WCAG relative luminance of an opaque colour.</summary>
        public static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

        /// <summary>WCAG contrast ratio of two opaque colours (1 to 21).</summary>
        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        /// <summary>
        /// Text on a colour: pure white or pure black, whichever has more contrast. One of them always reaches 4.58:1
        /// (the near-white and near-black pair couldn't reach 4.5:1 on mid-tones: "Hook" on green was 2.88:1, gallery mac-040).
        /// </summary>
        public static Color TextOn(Color background) =>
            Contrast(background, Colors.White) >= Contrast(background, Colors.Black) ? Colors.White : Colors.Black;

        public static IBrush TextOnBrush(Color background) => TextOn(background) == Colors.White ? White : Black;

        // ---- colour distance (CIE76 ΔE in CIELAB, D65)

        static (double L, double A, double B) Lab(Color c)
        {
            double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);
            double x = (r * 0.4124 + g * 0.3576 + b * 0.1805) / 0.95047;
            double y = r * 0.2126 + g * 0.7152 + b * 0.0722;
            double z = (r * 0.0193 + g * 0.1192 + b * 0.9505) / 1.08883;
            static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116.0;
            double fx = F(x), fy = F(y), fz = F(z);
            return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
        }

        /// <summary>Colour distance ΔE (CIE76).</summary>
        public static double DeltaE(Color a, Color b)
        {
            var p = Lab(a);
            var q = Lab(b);
            return Math.Sqrt((p.L - q.L) * (p.L - q.L) + (p.A - q.A) * (p.A - q.A) + (p.B - q.B) * (p.B - q.B));
        }

        /// <summary>Whether a palette colour is too close to a state colour to be offered for clips, tracks and scenes.</summary>
        public static bool IsReservedForState(Color c)
        {
            foreach (var s in StateColours)
                if (DeltaE(c, s) < ReservedDistance) return true;
            return false;
        }
    }
}
