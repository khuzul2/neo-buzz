#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): the canvas every Neo
// editor draws on. Backends: WPF today (WpfCanvas.cs, over a DrawingContext), a recorder for tests
// (RecordingCanvas.cs); Avalonia and Skia later. Coordinates are device-independent pixels with
// the origin at the top left of the control being drawn.

using System;
using System.Collections.Generic;

namespace NeoBuzz.Editor
{
    /// <summary>Measures text as a canvas would draw it (for layout before drawing).</summary>
    internal interface INeoTextMeasure
    {
        /// <summary>The width of one line of <paramref name="text"/> in the kit's font.</summary>
        double MeasureText(string text, double size, bool bold = false);
    }

    /// <summary>What the editors draw with. Kept small on purpose: each call maps to one call of a toolkit.</summary>
    internal interface INeoCanvas : INeoTextMeasure
    {
        void Line(NeoPoint from, NeoPoint to, NeoStroke stroke);

        /// <summary>A rectangle; <paramref name="radius"/> rounds its corners.</summary>
        void Rect(NeoRect rect, NeoFill fill, NeoStroke stroke, double radius = 0);

        void Ellipse(NeoPoint centre, double radiusX, double radiusY, NeoFill fill, NeoStroke stroke);

        /// <summary>
        /// An arc of a circle, from <paramref name="fromDegrees"/> to <paramref name="toDegrees"/>
        /// (0 = up, clockwise; drawn from the smaller angle to the larger).
        /// </summary>
        void Arc(NeoPoint centre, double radius, double fromDegrees, double toDegrees, NeoStroke stroke);

        /// <summary>Polylines and polygons: every figure of <paramref name="path"/>, filled (closed or not) and stroked.</summary>
        void Path(NeoPath path, NeoFill fill, NeoStroke stroke);

        /// <summary>
        /// One line of text with its top at <paramref name="y"/>. With <paramref name="maxWidth"/> &gt; 0
        /// the text is cut to that width with an ellipsis.
        /// </summary>
        void Text(string text, double x, double y, double size, NeoColor color, bool bold = false,
            NeoAlign align = NeoAlign.Left, double maxWidth = 0);

        /// <summary>Clips what follows to a (rounded) rectangle until the matching <see cref="PopClip"/>.</summary>
        void PushClip(NeoRect rect, double radius = 0);

        void PopClip();
    }

    /// <summary>
    /// Figures made of straight segments, built from points. A backend may keep what it built from
    /// a path (<see cref="BackendCache"/>); any change clears it, so a path kept between frames is
    /// built once.
    /// </summary>
    internal sealed class NeoPath
    {
        readonly List<NeoPoint> points = new List<NeoPoint>();
        readonly List<bool> smooth = new List<bool>();
        readonly List<Figure> figures = new List<Figure>();

        /// <summary>A figure: its points are <c>Start … Start + Count − 1</c>.</summary>
        public readonly record struct Figure(int Start, int Count, bool Closed);

        public int FigureCount => figures.Count;
        public int PointCount => points.Count;
        public IReadOnlyList<NeoPoint> Points => points;

        public Figure GetFigure(int i) => figures[i];

        public NeoPoint Point(int i) => points[i];

        /// <summary>
        /// True when the corner where the segment to point <paramref name="i"/> meets the one before
        /// it is drawn as a smooth join rather than with the stroke's corner style.
        /// </summary>
        public bool SmoothJoin(int i) => smooth[i];

        /// <summary>Whatever a backend built from this path (e.g. a frozen WPF geometry).</summary>
        public object BackendCache { get; set; }

        /// <summary>Starts a new figure.</summary>
        public NeoPath MoveTo(double x, double y) => MoveTo(new NeoPoint(x, y));

        public NeoPath MoveTo(NeoPoint p)
        {
            figures.Add(new Figure(points.Count, 1, false));
            points.Add(p);
            smooth.Add(false);
            BackendCache = null;
            return this;
        }

        public NeoPath LineTo(double x, double y, bool smoothJoin = false) => LineTo(new NeoPoint(x, y), smoothJoin);

        /// <summary>A straight segment to <paramref name="p"/> (the first point of a path starts its first figure).</summary>
        public NeoPath LineTo(NeoPoint p, bool smoothJoin = false)
        {
            if (figures.Count == 0) return MoveTo(p);
            var f = figures[figures.Count - 1];
            figures[figures.Count - 1] = f with { Count = f.Count + 1 };
            points.Add(p);
            smooth.Add(smoothJoin);
            BackendCache = null;
            return this;
        }

        /// <summary>Closes the current figure (a segment back to its first point).</summary>
        public NeoPath Close()
        {
            if (figures.Count == 0) return this;
            figures[figures.Count - 1] = figures[figures.Count - 1] with { Closed = true };
            BackendCache = null;
            return this;
        }

        public void Clear()
        {
            points.Clear();
            smooth.Clear();
            figures.Clear();
            BackendCache = null;
        }

        /// <summary>One open figure through <paramref name="pts"/>.</summary>
        public static NeoPath Polyline(IEnumerable<NeoPoint> pts)
        {
            var p = new NeoPath();
            foreach (var pt in pts) p.LineTo(pt);
            return p;
        }
    }

    /// <summary>Conveniences over the canvas's primitives.</summary>
    internal static class NeoCanvasExtensions
    {
        public static void Line(this INeoCanvas c, double x0, double y0, double x1, double y1, NeoStroke stroke) =>
            c.Line(new NeoPoint(x0, y0), new NeoPoint(x1, y1), stroke);

        public static void Polyline(this INeoCanvas c, IEnumerable<NeoPoint> points, NeoStroke stroke) =>
            c.Path(NeoPath.Polyline(points), NeoFill.None, stroke);
    }
}
