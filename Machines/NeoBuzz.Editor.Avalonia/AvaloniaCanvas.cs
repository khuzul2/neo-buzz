// Fork (docs/specs/studio-mvp.md S1): the Avalonia backend of the Neo editor kit's INeoCanvas
// (Core/INeoCanvas.cs), drawing on a DrawingContext inside Render, as WpfCanvas does on WPF's.
// Brushes and pens are immutable and made once per colour and stroke; a path kept between frames
// keeps the geometry built from it.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace NeoBuzz.Editor
{
    internal sealed class AvaloniaCanvas : INeoCanvas
    {
        const int CacheLimit = 4096;
        static readonly ConcurrentDictionary<NeoFill, IImmutableBrush> brushes = new ConcurrentDictionary<NeoFill, IImmutableBrush>();
        static readonly ConcurrentDictionary<NeoStroke, ImmutablePen> pens = new ConcurrentDictionary<NeoStroke, ImmutablePen>();

        readonly Stack<DrawingContext.PushedState> clips = new Stack<DrawingContext.PushedState>();
        DrawingContext dc;

        /// <param name="owner">The control drawn on (kept for the WPF backend's shape).</param>
        public AvaloniaCanvas(Visual owner)
        {
        }

        /// <summary>Draws on <paramref name="context"/> until the next call (use in Render).</summary>
        public AvaloniaCanvas On(DrawingContext context)
        {
            clips.Clear();   // clips belong to the context they were pushed on
            dc = context;
            return this;
        }

        // ------------------------------------------------------------ conversions and caches

        /// <summary>A kit colour as the theme in use draws it (<see cref="Tokens.Resolve"/>).</summary>
        public static Color ToAvalonia(NeoColor c)
        {
            c = Tokens.Resolve(c);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        // Fork (ux-overhaul.md P4b): a theme change drops the brushes and pens made in the old one, and the brushes a view holds
        // (Themed) take the new colour in place, so nothing of the old theme stays on screen.
        static readonly List<(Func<NeoColor> Source, SolidColorBrush Brush)> themed = new List<(Func<NeoColor>, SolidColorBrush)>();
        static readonly Dictionary<NeoColor, SolidColorBrush> themedByColour = new Dictionary<NeoColor, SolidColorBrush>();

        static AvaloniaCanvas()
        {
            Tokens.Changed += () =>
            {
                brushes.Clear();
                pens.Clear();
                lock (themed)
                    foreach (var (source, brush) in themed) brush.Color = ToAvalonia(source());
            };
        }

        /// <summary>
        /// A brush a view may keep (a background, a foreground): it follows the theme. <paramref name="source"/> answers the colour
        /// in the theme in use (a Palette entry, which is already the set's).
        /// </summary>
        public static IBrush Themed(Func<NeoColor> source)
        {
            var brush = new SolidColorBrush(ToAvalonia(source()));
            lock (themed) themed.Add((source, brush));
            return brush;
        }

        /// <summary>A brush for a colour a view keeps (an editor's accent): one per colour, following the theme.</summary>
        public static IBrush Themed(NeoColor colour)
        {
            lock (themed)
            {
                if (themedByColour.TryGetValue(colour, out var known)) return known;
                var brush = new SolidColorBrush(ToAvalonia(colour));
                themedByColour[colour] = brush;
                themed.Add((() => colour, brush));
                return brush;
            }
        }

        /// <summary>An immutable brush for a fill (null for none), made once.</summary>
        public static IImmutableBrush Brush(NeoFill fill)
        {
            if (fill.IsNone) return null;
            if (brushes.TryGetValue(fill, out var b)) return b;
            if (brushes.Count > CacheLimit) brushes.Clear();
            b = fill.IsGradient
                // WPF's LinearGradientBrush(top, bottom, 90): down the shape's bounds.
                ? new ImmutableLinearGradientBrush(
                    new[] { new ImmutableGradientStop(0, ToAvalonia(fill.Color)), new ImmutableGradientStop(1, ToAvalonia(fill.To)) },
                    1, null, null, GradientSpreadMethod.Pad,
                    new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative))
                : new ImmutableSolidColorBrush(ToAvalonia(fill.Color));
            return brushes.GetOrAdd(fill, b);
        }

        public static IImmutableBrush Brush(NeoColor color) => Brush((NeoFill)color);

        /// <summary>An immutable pen for a stroke (null for none), made once.</summary>
        public static ImmutablePen Pen(NeoStroke stroke)
        {
            if (stroke.IsNone) return null;
            if (pens.TryGetValue(stroke, out var p)) return p;
            if (pens.Count > CacheLimit) pens.Clear();
            p = stroke.Round
                ? new ImmutablePen(Brush(stroke.Color), stroke.Width, null, PenLineCap.Round, PenLineJoin.Round)
                : new ImmutablePen(Brush(stroke.Color), stroke.Width, null, PenLineCap.Flat, PenLineJoin.Miter);
            return pens.GetOrAdd(stroke, p);
        }

        static Point P(NeoPoint p) => new Point(p.X, p.Y);

        static Rect R(NeoRect r) => new Rect(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));

        // ------------------------------------------------------------ text

        /// <summary>
        /// The kit's font: the design tokens' Inter (ux-overhaul.md P2a), which the studio bundles (fonts:Inter), as the
        /// rest of the studio; where it isn't there, fonts of Segoe UI's widths, so labels fit: Helvetica Neue on macOS,
        /// Arial or Liberation Sans.
        /// </summary>
        public static readonly FontFamily Font = new FontFamily("fonts:Inter#" + Palette.FontFamily + ", " + Palette.FontFamily + ", Helvetica Neue, Arial, Liberation Sans, $Default");

        static readonly Typeface Regular = new Typeface(Font, FontStyle.Normal, FontWeight.Normal);
        static readonly Typeface SemiBold = new Typeface(Font, FontStyle.Normal, FontWeight.SemiBold);

        static FormattedText Format(string text, double size, NeoColor color, bool bold) =>
            new FormattedText(text ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                bold ? SemiBold : Regular, size, Brush(color));

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
            var brush = Brush(fill);
            var pen = Pen(stroke);
            if (brush == null && pen == null) return;
            dc.DrawRectangle(brush, pen, R(rect), radius, radius);
        }

        public void Ellipse(NeoPoint centre, double radiusX, double radiusY, NeoFill fill, NeoStroke stroke)
        {
            var brush = Brush(fill);
            var pen = Pen(stroke);
            if (brush == null && pen == null) return;
            dc.DrawEllipse(brush, pen, P(centre), radiusX, radiusY);
        }

        public void Arc(NeoPoint centre, double radius, double fromDegrees, double toDegrees, NeoStroke stroke)
        {
            var pen = Pen(stroke);
            if (pen == null) return;
            double a0 = fromDegrees, a1 = toDegrees;
            NeoGeometry.ArcAngles(ref a0, ref a1);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(P(NeoGeometry.Polar(centre, radius, a0)), false);
                ctx.ArcTo(P(NeoGeometry.Polar(centre, radius, a1)), new Size(radius, radius), 0, a1 - a0 > 180, SweepDirection.Clockwise);
                ctx.EndFigure(false);
            }
            dc.DrawGeometry(null, pen, g);
        }

        sealed class BuiltPath
        {
            public StreamGeometry Geometry;
            public bool Filled;
        }

        /// <remarks>Joins follow the stroke (round or mitred) throughout: Avalonia has no per-corner smooth join.</remarks>
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
                        ctx.BeginFigure(P(path.Point(fig.Start)), filled);
                        for (int i = fig.Start + 1; i < fig.Start + fig.Count; i++)
                            ctx.LineTo(P(path.Point(i)));
                        ctx.EndFigure(fig.Closed);
                    }
                }
                built = new BuiltPath { Geometry = g, Filled = filled };
                path.BackendCache = built;
            }
            dc.DrawGeometry(brush, pen, built.Geometry);
        }

        // ------------------------------------------------------------ clipping

        public void PushClip(NeoRect rect, double radius = 0) =>
            clips.Push(radius > 0 ? dc.PushClip(new RoundedRect(R(rect), radius)) : dc.PushClip(R(rect)));

        public void PopClip()
        {
            if (clips.Count > 0) clips.Pop().Dispose();
        }
    }
}
