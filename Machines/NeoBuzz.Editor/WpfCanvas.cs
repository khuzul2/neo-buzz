#nullable disable
// Neo editor kit: the WPF backend of INeoCanvas (docs/specs/cross-platform.md §8.3), drawing on a
// DrawingContext inside OnRender. Brushes and pens are made once per colour and stroke and kept
// frozen; a path kept between frames keeps the geometry built from it.

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NeoBuzz.Editor
{
    internal sealed class WpfCanvas : INeoCanvas
    {
        const int CacheLimit = 4096;
        static readonly ConcurrentDictionary<NeoFill, Brush> brushes = new ConcurrentDictionary<NeoFill, Brush>();
        static readonly ConcurrentDictionary<NeoStroke, Pen> pens = new ConcurrentDictionary<NeoStroke, Pen>();

        readonly Visual owner;
        DrawingContext dc;

        /// <param name="owner">The element drawn on (its DPI sets how text is laid out).</param>
        public WpfCanvas(Visual owner)
        {
            this.owner = owner;
        }

        /// <summary>Draws on <paramref name="context"/> until the next call (use in OnRender).</summary>
        public WpfCanvas On(DrawingContext context)
        {
            dc = context;
            return this;
        }

        // ------------------------------------------------------------ conversions and caches

        public static Color ToWpf(NeoColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        static T Frozen<T>(T f) where T : Freezable
        {
            if (f.CanFreeze) f.Freeze();
            return f;
        }

        /// <summary>A frozen brush for a fill (null for none), made once.</summary>
        public static Brush Brush(NeoFill fill)
        {
            if (fill.IsNone) return null;
            if (brushes.TryGetValue(fill, out var b)) return b;
            if (brushes.Count > CacheLimit) brushes.Clear();
            b = fill.IsGradient
                ? (Brush)new LinearGradientBrush(ToWpf(fill.Color), ToWpf(fill.To), 90)
                : new SolidColorBrush(ToWpf(fill.Color));
            return brushes.GetOrAdd(fill, Frozen(b));
        }

        public static Brush Brush(NeoColor color) => Brush((NeoFill)color);

        /// <summary>A frozen pen for a stroke (null for none), made once.</summary>
        public static Pen Pen(NeoStroke stroke)
        {
            if (stroke.IsNone) return null;
            if (pens.TryGetValue(stroke, out var p)) return p;
            if (pens.Count > CacheLimit) pens.Clear();
            p = new Pen(Brush(stroke.Color), stroke.Width);
            if (stroke.Round)
            {
                p.StartLineCap = PenLineCap.Round;
                p.EndLineCap = PenLineCap.Round;
                p.LineJoin = PenLineJoin.Round;
            }
            return pens.GetOrAdd(stroke, Frozen(p));
        }

        static Point P(NeoPoint p) => new Point(p.X, p.Y);

        static Rect R(NeoRect r) => new Rect(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));

        // ------------------------------------------------------------ text

        static readonly Typeface Font = new Typeface(new FontFamily(Palette.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static readonly Typeface FontBold = new Typeface(new FontFamily(Palette.FontFamily), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        static double PixelsPerDip(Visual v)
        {
            try
            {
                return VisualTreeHelper.GetDpi(v).PixelsPerDip;
            }
            catch (Exception)
            {
                return 1.0;
            }
        }

        FormattedText Format(string text, double size, NeoColor color, bool bold) =>
            new FormattedText(text ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                bold ? FontBold : Font, size, Brush(color), PixelsPerDip(owner));

        public double MeasureText(string text, double size, bool bold = false) => Format(text, size, Palette.Text, bold).Width;

        public void Text(string text, double x, double y, double size, NeoColor color, bool bold = false,
            NeoAlign align = NeoAlign.Left, double maxWidth = 0)
        {
            var ft = Format(text, size, color, bold);
            if (maxWidth > 0)
            {
                ft.MaxTextWidth = maxWidth;
                ft.MaxLineCount = 1;
                ft.Trimming = TextTrimming.CharacterEllipsis;
            }
            if (align == NeoAlign.Center) x -= ft.Width / 2;
            else if (align == NeoAlign.Right) x -= ft.Width;
            dc.DrawText(ft, new Point(x, y));
        }

        // ------------------------------------------------------------ shapes

        public void Line(NeoPoint from, NeoPoint to, NeoStroke stroke)
        {
            var pen = Pen(stroke);
            if (pen != null) dc.DrawLine(pen, P(from), P(to));
        }

        public void Rect(NeoRect rect, NeoFill fill, NeoStroke stroke, double radius = 0)
        {
            if (radius > 0) dc.DrawRoundedRectangle(Brush(fill), Pen(stroke), R(rect), radius, radius);
            else dc.DrawRectangle(Brush(fill), Pen(stroke), R(rect));
        }

        public void Ellipse(NeoPoint centre, double radiusX, double radiusY, NeoFill fill, NeoStroke stroke) =>
            dc.DrawEllipse(Brush(fill), Pen(stroke), P(centre), radiusX, radiusY);

        public void Arc(NeoPoint centre, double radius, double fromDegrees, double toDegrees, NeoStroke stroke)
        {
            var pen = Pen(stroke);
            if (pen == null) return;
            double a0 = fromDegrees, a1 = toDegrees;
            NeoGeometry.ArcAngles(ref a0, ref a1);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(P(NeoGeometry.Polar(centre, radius, a0)), false, false);
                ctx.ArcTo(P(NeoGeometry.Polar(centre, radius, a1)), new Size(radius, radius), 0, a1 - a0 > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }

        sealed class BuiltPath
        {
            public StreamGeometry Geometry;
            public bool Filled;
        }

        public void Path(NeoPath path, NeoFill fill, NeoStroke stroke)
        {
            if (path == null || path.FigureCount == 0) return;
            var brush = Brush(fill);
            var pen = Pen(stroke);
            if (brush == null && pen == null) return;
            bool filled = brush != null;
            if (!(path.BackendCache is BuiltPath built) || built.Filled != filled)
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    for (int f = 0; f < path.FigureCount; f++)
                    {
                        var fig = path.GetFigure(f);
                        ctx.BeginFigure(P(path.Point(fig.Start)), filled, fig.Closed);
                        for (int i = fig.Start + 1; i < fig.Start + fig.Count; i++)
                            ctx.LineTo(P(path.Point(i)), true, path.SmoothJoin(i));
                    }
                }
                g.Freeze();
                built = new BuiltPath { Geometry = g, Filled = filled };
                path.BackendCache = built;
            }
            dc.DrawGeometry(brush, pen, built.Geometry);
        }

        // ------------------------------------------------------------ clipping

        public void PushClip(NeoRect rect, double radius = 0) =>
            dc.PushClip(radius > 0 ? new RectangleGeometry(R(rect), radius, radius) : new RectangleGeometry(R(rect)));

        public void PopClip() => dc.Pop();
    }
}
