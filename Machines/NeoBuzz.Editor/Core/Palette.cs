#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): the dark theme's colours
// and the geometry helpers the controls share. The WPF brushes in Theme.cs are made from these.

using System;

namespace NeoBuzz.Editor
{
    internal static class Palette
    {
        public static readonly NeoColor Background = NeoColor.FromRgb(0x15, 0x17, 0x1C);
        public static readonly NeoColor Panel = NeoColor.FromRgb(0x1E, 0x21, 0x27);
        public static readonly NeoColor PanelEdge = NeoColor.FromRgb(0x2B, 0x2F, 0x37);
        public static readonly NeoColor Track = NeoColor.FromRgb(0x34, 0x39, 0x44);
        public static readonly NeoColor Text = NeoColor.FromRgb(0xE4, 0xE7, 0xEC);
        public static readonly NeoColor DimText = NeoColor.FromRgb(0x8A, 0x92, 0xA0);
        public static readonly NeoColor KnobTop = NeoColor.FromRgb(0x33, 0x37, 0x40);
        public static readonly NeoColor KnobBottom = NeoColor.FromRgb(0x23, 0x26, 0x2D);
        public static readonly NeoColor Grid = NeoColor.FromRgb(0x2A, 0x2E, 0x36);
        /// <summary>A level meter above 0 dB.</summary>
        public static readonly NeoColor Over = NeoColor.FromRgb(0xFF, 0x4D, 0x4D);

        public static readonly NeoFill KnobBody = NeoFill.Vertical(KnobTop, KnobBottom);

        /// <summary>The kit's font; backends map it to what the platform has.</summary>
        public const string FontFamily = "Segoe UI";
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
