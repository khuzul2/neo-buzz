#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): a canvas that records the
// calls made on it, for tests of editor drawing that run without any UI toolkit (Linux CI).

using System;
using System.Collections.Generic;
using System.Linq;

namespace NeoBuzz.Editor
{
    internal enum NeoDrawKind { Line, Rect, Ellipse, Arc, Path, Text, PushClip, PopClip }

    /// <summary>One recorded call; fields a kind doesn't use stay at their defaults.</summary>
    internal sealed class NeoDrawCall
    {
        public NeoDrawKind Kind { get; init; }
        /// <summary>Line: its two ends. Ellipse, Arc: the centre. Path: every point. Text: the point given.</summary>
        public NeoPoint[] Points { get; init; } = Array.Empty<NeoPoint>();
        /// <summary>Rect, PushClip: the rectangle. Other shapes: their bounds (text: as measured).</summary>
        public NeoRect Bounds { get; init; }
        public NeoFill Fill { get; init; }
        public NeoStroke Stroke { get; init; }
        /// <summary>Rect, PushClip: the corner radius. Ellipse: the horizontal radius. Arc: the radius.</summary>
        public double Radius { get; init; }
        public double RadiusY { get; init; }
        public double FromDegrees { get; init; }
        public double ToDegrees { get; init; }
        public NeoPath Path { get; init; }
        public string Text { get; init; }
        public double Size { get; init; }
        public bool Bold { get; init; }
        public NeoAlign Align { get; init; }
        public double MaxWidth { get; init; }
        public NeoColor Color { get; init; }

        public override string ToString() => Kind switch
        {
            NeoDrawKind.Text => $"Text \"{Text}\" at ({Points[0].X:0.##}, {Points[0].Y:0.##}) {Size} {Color}",
            NeoDrawKind.Rect => $"Rect {Bounds} fill {Fill.Color} stroke {Stroke.Color}/{Stroke.Width}",
            _ => $"{Kind} {Bounds}",
        };
    }

    internal sealed class RecordingCanvas : INeoCanvas
    {
        readonly List<NeoDrawCall> calls = new List<NeoDrawCall>();
        int depth;

        /// <summary>Text width per character, as a fraction of the size (an estimate: no fonts here).</summary>
        public double CharWidth { get; set; } = 0.55;

        public IReadOnlyList<NeoDrawCall> Calls => calls;
        public int ClipDepth => depth;
        public int MaxClipDepth { get; private set; }
        /// <summary>True if a PopClip ever came without its PushClip.</summary>
        public bool Unbalanced { get; private set; }

        public IEnumerable<NeoDrawCall> Of(NeoDrawKind kind) => calls.Where(c => c.Kind == kind);

        public void Clear()
        {
            calls.Clear();
            depth = 0;
            MaxClipDepth = 0;
            Unbalanced = false;
        }

        public double MeasureText(string text, double size, bool bold = false) =>
            (text ?? "").Length * size * (bold ? CharWidth * 1.08 : CharWidth);

        static NeoRect BoundsOf(IReadOnlyList<NeoPoint> pts)
        {
            if (pts.Count == 0) return default;
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
            foreach (var p in pts)
            {
                l = Math.Min(l, p.X);
                t = Math.Min(t, p.Y);
                r = Math.Max(r, p.X);
                b = Math.Max(b, p.Y);
            }
            return new NeoRect(l, t, r - l, b - t);
        }

        public void Line(NeoPoint from, NeoPoint to, NeoStroke stroke) =>
            calls.Add(new NeoDrawCall { Kind = NeoDrawKind.Line, Points = new[] { from, to }, Bounds = BoundsOf(new[] { from, to }), Stroke = stroke });

        public void Rect(NeoRect rect, NeoFill fill, NeoStroke stroke, double radius = 0) =>
            calls.Add(new NeoDrawCall { Kind = NeoDrawKind.Rect, Bounds = rect, Fill = fill, Stroke = stroke, Radius = radius });

        public void Ellipse(NeoPoint centre, double radiusX, double radiusY, NeoFill fill, NeoStroke stroke) =>
            calls.Add(new NeoDrawCall
            {
                Kind = NeoDrawKind.Ellipse, Points = new[] { centre }, Fill = fill, Stroke = stroke, Radius = radiusX, RadiusY = radiusY,
                Bounds = new NeoRect(centre.X - radiusX, centre.Y - radiusY, 2 * radiusX, 2 * radiusY),
            });

        public void Arc(NeoPoint centre, double radius, double fromDegrees, double toDegrees, NeoStroke stroke)
        {
            NeoGeometry.ArcAngles(ref fromDegrees, ref toDegrees);
            var pts = new List<NeoPoint>();
            for (int i = 0; i <= 32; i++) pts.Add(NeoGeometry.Polar(centre, radius, fromDegrees + (toDegrees - fromDegrees) * i / 32));
            calls.Add(new NeoDrawCall
            {
                Kind = NeoDrawKind.Arc, Points = new[] { centre }, Stroke = stroke, Radius = radius,
                FromDegrees = fromDegrees, ToDegrees = toDegrees, Bounds = BoundsOf(pts),
            });
        }

        public void Path(NeoPath path, NeoFill fill, NeoStroke stroke) =>
            calls.Add(new NeoDrawCall
            {
                Kind = NeoDrawKind.Path, Path = path, Points = path.Points.ToArray(), Fill = fill, Stroke = stroke,
                Bounds = BoundsOf(path.Points),
            });

        public void Text(string text, double x, double y, double size, NeoColor color, bool bold = false,
            NeoAlign align = NeoAlign.Left, double maxWidth = 0)
        {
            double w = MeasureText(text, size, bold);
            if (maxWidth > 0) w = Math.Min(w, maxWidth);
            double left = align == NeoAlign.Center ? x - w / 2 : align == NeoAlign.Right ? x - w : x;
            calls.Add(new NeoDrawCall
            {
                Kind = NeoDrawKind.Text, Text = text ?? "", Points = new[] { new NeoPoint(x, y) }, Size = size, Color = color,
                Bold = bold, Align = align, MaxWidth = maxWidth, Bounds = new NeoRect(left, y, w, size * 1.33),
            });
        }

        public void PushClip(NeoRect rect, double radius = 0)
        {
            depth++;
            MaxClipDepth = Math.Max(MaxClipDepth, depth);
            calls.Add(new NeoDrawCall { Kind = NeoDrawKind.PushClip, Bounds = rect, Radius = radius });
        }

        public void PopClip()
        {
            if (depth == 0) Unbalanced = true;
            else depth--;
            calls.Add(new NeoDrawCall { Kind = NeoDrawKind.PopClip });
        }
    }
}
