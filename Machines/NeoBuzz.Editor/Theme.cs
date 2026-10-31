#nullable disable
// Neo machines' editor kit (docs/specs/machines.md §4): colours, fonts and drawing helpers shared
// by every Neo editor. Code-only WPF (no XAML), linked as source into each machine.

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NeoBuzz.Editor
{
    internal static class Theme
    {
        public static readonly Color BackgroundColor = Color.FromRgb(0x15, 0x17, 0x1C);
        public static readonly Color PanelColor = Color.FromRgb(0x1E, 0x21, 0x27);
        public static readonly Color PanelEdgeColor = Color.FromRgb(0x2B, 0x2F, 0x37);
        public static readonly Color TrackColor = Color.FromRgb(0x34, 0x39, 0x44);
        public static readonly Color TextColor = Color.FromRgb(0xE4, 0xE7, 0xEC);
        public static readonly Color DimTextColor = Color.FromRgb(0x8A, 0x92, 0xA0);
        public static readonly Color KnobTopColor = Color.FromRgb(0x33, 0x37, 0x40);
        public static readonly Color KnobBottomColor = Color.FromRgb(0x23, 0x26, 0x2D);
        public static readonly Color GridColor = Color.FromRgb(0x2A, 0x2E, 0x36);

        public static readonly Brush Background = Frozen(new SolidColorBrush(BackgroundColor));
        public static readonly Brush Panel = Frozen(new SolidColorBrush(PanelColor));
        public static readonly Brush PanelEdge = Frozen(new SolidColorBrush(PanelEdgeColor));
        public static readonly Brush Track = Frozen(new SolidColorBrush(TrackColor));
        public static readonly Brush Text = Frozen(new SolidColorBrush(TextColor));
        public static readonly Brush DimText = Frozen(new SolidColorBrush(DimTextColor));
        public static readonly Brush Grid = Frozen(new SolidColorBrush(GridColor));
        public static readonly Brush KnobBody = Frozen(new LinearGradientBrush(KnobTopColor, KnobBottomColor, 90));

        public static readonly Typeface Font = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        public static readonly Typeface FontBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        public static readonly FontFamily FontFamily = new FontFamily("Segoe UI");

        public static T Frozen<T>(T f) where T : Freezable
        {
            if (f.CanFreeze) f.Freeze();
            return f;
        }

        public static Brush Solid(Color c) => Frozen(new SolidColorBrush(c));

        public static Brush Solid(Color c, byte alpha) => Frozen(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)));

        public static Pen Pen(Brush b, double thickness, bool round = true)
        {
            var p = new Pen(b, thickness);
            if (round)
            {
                p.StartLineCap = PenLineCap.Round;
                p.EndLineCap = PenLineCap.Round;
                p.LineJoin = PenLineJoin.Round;
            }
            return Frozen(p);
        }

        public static FormattedText Format(Visual v, string text, double size, Brush brush, bool bold = false)
        {
            double dpi = 1.0;
            try
            {
                dpi = VisualTreeHelper.GetDpi(v).PixelsPerDip;
            }
            catch (Exception)
            {
            }
            return new FormattedText(text ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                bold ? FontBold : Font, size, brush, dpi);
        }

        /// <summary>Draws text centred horizontally on <paramref name="x"/>.</summary>
        public static void TextCentered(DrawingContext dc, Visual v, string text, double x, double y, double size, Brush brush, bool bold = false)
        {
            var ft = Format(v, text, size, brush, bold);
            dc.DrawText(ft, new Point(x - ft.Width / 2, y));
        }

        public static void DrawText(DrawingContext dc, Visual v, string text, double x, double y, double size, Brush brush, bool bold = false)
        {
            dc.DrawText(Format(v, text, size, brush, bold), new Point(x, y));
        }

        /// <summary>An arc from angle a0 to a1 (degrees, 0 = up, clockwise) on a circle.</summary>
        public static Geometry Arc(Point c, double r, double a0, double a1)
        {
            if (a1 < a0) (a0, a1) = (a1, a0);
            if (a1 - a0 < 0.01) a1 = a0 + 0.01;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(Polar(c, r, a0), false, false);
                ctx.ArcTo(Polar(c, r, a1), new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        public static Point Polar(Point c, double r, double degrees)
        {
            double a = (degrees - 90) * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        /// <summary>A lighter tint of a colour (for highlights).</summary>
        public static Color Tint(Color c, double amount)
        {
            byte L(byte x) => (byte)Math.Clamp(x + (255 - x) * amount, 0, 255);
            return Color.FromRgb(L(c.R), L(c.G), L(c.B));
        }
    }
}
