#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): the plain values the
// canvas draws with. Nothing in Core/ may use WPF, Avalonia or Skia types: the folder also
// compiles on Linux, in NeoBuzz.MachineTests.

using System;
using System.Globalization;

namespace NeoBuzz.Editor
{
    /// <summary>A colour as one ARGB value (0xAARRGGBB). Fully transparent means "none".</summary>
    internal readonly record struct NeoColor(uint Argb)
    {
        public byte A => (byte)(Argb >> 24);
        public byte R => (byte)(Argb >> 16);
        public byte G => (byte)(Argb >> 8);
        public byte B => (byte)Argb;

        public bool IsVisible => A != 0;

        public static NeoColor FromRgb(byte r, byte g, byte b) => FromArgb(0xFF, r, g, b);

        public static NeoColor FromArgb(byte a, byte r, byte g, byte b) =>
            new NeoColor(((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b);

        /// <summary>The same colour with another alpha.</summary>
        public NeoColor WithAlpha(byte alpha) => FromArgb(alpha, R, G, B);

        /// <summary>A lighter tint (for highlights): 0 = the colour, 1 = white.</summary>
        public NeoColor Tint(double amount)
        {
            byte L(byte x) => (byte)Math.Clamp(x + (255 - x) * amount, 0, 255);
            return FromRgb(L(R), L(G), L(B));
        }

        public override string ToString() => "#" + Argb.ToString("X8", CultureInfo.InvariantCulture);
    }

    internal readonly record struct NeoPoint(double X, double Y);

    /// <summary>A rectangle: position of the top-left corner and size, in device-independent pixels.</summary>
    internal readonly record struct NeoRect(double X, double Y, double Width, double Height)
    {
        public double Left => X;
        public double Top => Y;
        public double Right => X + Width;
        public double Bottom => Y + Height;
        public NeoPoint Centre => new NeoPoint(X + Width / 2, Y + Height / 2);

        public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

        /// <summary>True when the two overlap with a positive area (touching edges don't count).</summary>
        public bool Overlaps(NeoRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
    }

    /// <summary>
    /// How a line or outline is drawn: colour, width and ends. Round ends and joins by default;
    /// square ones (flat caps, mitred joins) for grids and hairlines. Width 0 means no outline.
    /// </summary>
    internal readonly record struct NeoStroke(NeoColor Color, double Width, bool Round = true)
    {
        public static readonly NeoStroke None = default;

        public bool IsNone => Width <= 0 || !Color.IsVisible;
    }

    /// <summary>
    /// How a shape is filled: one colour, or a vertical gradient from <see cref="Color"/> at the
    /// top of the shape to <see cref="To"/> at its bottom. A colour converts to a fill.
    /// </summary>
    internal readonly record struct NeoFill(NeoColor Color, NeoColor To, bool IsGradient)
    {
        public static readonly NeoFill None = default;

        public bool IsNone => !IsGradient && !Color.IsVisible;

        public static NeoFill Vertical(NeoColor top, NeoColor bottom) => new NeoFill(top, bottom, true);

        public static implicit operator NeoFill(NeoColor c) => new NeoFill(c, default, false);
    }

    internal enum NeoAlign
    {
        /// <summary>The text starts at x.</summary>
        Left,
        /// <summary>The text is centred on x.</summary>
        Center,
        /// <summary>The text ends at x.</summary>
        Right,
    }
}
