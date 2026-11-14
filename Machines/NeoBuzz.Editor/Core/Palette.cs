#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): the dark theme's colours
// and the geometry helpers the controls share. The WPF brushes in Theme.cs are made from these. On Avalonia (the
// studio) they are the design tokens (Tokens.cs, ux-overhaul.md P2a); the classic WPF app keeps its own.

using System;

namespace NeoBuzz.Editor
{
    internal static class Palette
    {
#if AVALONIA
        // Fork (ux-overhaul.md P2a, P4b): the studio's editors on the design tokens (Core/Tokens.cs), read when drawn so a
        // theme change shows at once.
        public static NeoColor Background => Tokens.SurfaceBase;
        public static NeoColor Panel => Tokens.SurfaceView;
        public static NeoColor PanelEdge => Tokens.SurfaceControl;
        public static NeoColor Track => Tokens.SurfaceBorder;
        /// <summary>A knob's unfilled track: 3:1 against the panel, as WCAG asks of a control's parts.</summary>
        public static NeoColor KnobTrack => Tokens.ControlTrack;
        public static NeoColor Text => Tokens.TextPrimary;
        public static NeoColor DimText => Tokens.TextSecondary;
        public static NeoColor KnobTop => Tokens.Set.KnobTop;
        public static NeoColor KnobBottom => Tokens.Set.KnobBottom;
        public static NeoColor Grid => Tokens.GridLine;
        /// <summary>A level meter above 0 dB.</summary>
        public static NeoColor Over => Tokens.MeterOver;
#else
        // The classic (WPF) app keeps its look (ux-overhaul.md §1).
        public static readonly NeoColor Background = NeoColor.FromRgb(0x15, 0x17, 0x1C);
        public static readonly NeoColor Panel = NeoColor.FromRgb(0x1E, 0x21, 0x27);
        public static readonly NeoColor PanelEdge = NeoColor.FromRgb(0x2B, 0x2F, 0x37);
        public static readonly NeoColor Track = NeoColor.FromRgb(0x34, 0x39, 0x44);
        /// <summary>
        /// A knob's unfilled track: 3:1 against the panel, as WCAG asks of a control's parts (Track was 1.39:1, UI
        /// gallery review).
        /// </summary>
        public static readonly NeoColor KnobTrack = NeoColor.FromRgb(0x64, 0x6B, 0x7A);
        public static readonly NeoColor Text = NeoColor.FromRgb(0xE4, 0xE7, 0xEC);
        public static readonly NeoColor DimText = NeoColor.FromRgb(0x8A, 0x92, 0xA0);
        public static readonly NeoColor KnobTop = NeoColor.FromRgb(0x33, 0x37, 0x40);
        public static readonly NeoColor KnobBottom = NeoColor.FromRgb(0x23, 0x26, 0x2D);
        public static readonly NeoColor Grid = NeoColor.FromRgb(0x2A, 0x2E, 0x36);
        /// <summary>A level meter above 0 dB.</summary>
        public static readonly NeoColor Over = NeoColor.FromRgb(0xFF, 0x4D, 0x4D);
#endif

        public static NeoFill KnobBody => NeoFill.Vertical(KnobTop, KnobBottom);

#if AVALONIA
        /// <summary>The kit's font: the tokens' (Inter, which the studio bundles); backends map it to what the platform has.</summary>
        public const string FontFamily = Tokens.FontFamily;
#else
        /// <summary>The kit's font; backends map it to what the platform has.</summary>
        public const string FontFamily = "Segoe UI";
#endif
    }

    /// <summary>WCAG 2's contrast between two colours (1 … 21).</summary>
    internal static class Contrast
    {
        static double Channel(byte v)
        {
            double c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        /// <summary>Relative luminance, 0 (black) … 1 (white).</summary>
        public static double Luminance(NeoColor c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

        public static double Ratio(NeoColor a, NeoColor b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }
    }

    internal static class NeoGeometry
    {
        /// <summary>A point on a circle: <paramref name="degrees"/> from up, clockwise.</summary>
        public static NeoPoint Polar(NeoPoint c, double r, double degrees)
        {
            double a = (degrees - 90) * Math.PI / 180;
            return new NeoPoint(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        /// <summary>An arc's angles as backends draw it: smaller first, at least 0.01° apart.</summary>
        public static void ArcAngles(ref double a0, ref double a1)
        {
            if (a1 < a0) (a0, a1) = (a1, a0);
            if (a1 - a0 < 0.01) a1 = a0 + 0.01;
        }
    }
}
