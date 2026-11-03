// Fork (docs/specs/studio-mvp.md S3): the machine view's geometry, ported from BuzzGUI.MachineView
// (MachineCanvas, Connection, Clipper): where a machine's box is for its song position, a wire's
// ends clipped at the boxes, its direction arrow, and hit tests. Pure, so tests can check it.

using System;
using Avalonia;

namespace ReBuzz.Studio.Views.Machines
{
    /// <summary>
    /// Song positions (-2..2, as songs store them) to canvas pixels and back, as MachineCanvas does:
    /// the canvas is twice the view, so -1..1 fills the view when it is scrolled to the middle.
    /// </summary>
    internal readonly struct CanvasMap
    {
        /// <summary>MachineCanvas.CanvasSize in MachineView.xaml.</summary>
        public const double CanvasSize = 2;

        /// <summary>The theme's machine box (MachineControl.xaml, Border).</summary>
        public const double BoxWidth = 100, BoxHeight = 50;

        public CanvasMap(double width, double height)
        {
            Width = width;
            Height = height;
        }

        public double Width { get; }
        public double Height { get; }

        double Cx => Width / 2;
        double Cy => Height / 2;
        double Sx => Cx / CanvasSize;
        double Sy => Cy / CanvasSize;

        /// <summary>The pixel of a song position (a box's centre).</summary>
        public Point PointAt(double x, double y) => new Point(Cx + x * Sx, Cy + y * Sy);

        /// <summary>A machine's box, as MachineCanvas.GetRect places it (rounded to whole pixels).</summary>
        public Rect BoxAt(double x, double y) =>
            new Rect(Math.Round(Cx + x * Sx - BoxWidth / 2), Math.Round(Cy + y * Sy - BoxHeight / 2), BoxWidth, BoxHeight);

        /// <summary>The song position of a pixel (MachineCanvas.GetPositionAtPoint).</summary>
        public Point PositionAt(Point p) => new Point((p.X - Cx) / Sx, (p.Y - Cy) / Sy);

        /// <summary>A pixel distance as a distance in song positions (for drags).</summary>
        public Vector PositionDelta(Vector pixels) => new Vector(pixels.X / Sx, pixels.Y / Sy);

        public Vector ScaleToPixels(Vector v) => new Vector(v.X * Sx, v.Y * Sy);

        /// <summary>Positions stay on the canvas (MachineCanvas.Drag).</summary>
        public static Point Clamp(Point p) =>
            new Point(Math.Clamp(p.X, -CanvasSize, CanvasSize), Math.Clamp(p.Y, -CanvasSize, CanvasSize));
    }

    /// <summary>A wire as Connection.UpdateVisuals draws it: a straight line between the boxes and an arrow in the middle.</summary>
    internal readonly struct WireShape
    {
        public WireShape(Point start, Point end, Point mid, Point apex, Point left, Point right)
        {
            Start = start;
            End = end;
            Mid = mid;
            Apex = apex;
            Left = left;
            Right = right;
            Visible = true;
        }

        /// <summary>False when the boxes overlap: Connection hides the wire then.</summary>
        public bool Visible { get; }
        public Point Start { get; }
        public Point End { get; }
        public Point Mid { get; }

        /// <summary>The arrow (the theme's triangle 0,-9 -10,9 10,9), pointing at the destination.</summary>
        public Point Apex { get; }
        public Point Left { get; }
        public Point Right { get; }
    }

    internal static class MachineGeometry
    {
        /// <summary>The rounded-corner radius the wire ends are clipped by: the border's 0 + half a plug (14) + 1.</summary>
        const double ClipRadius = 8;

        public static WireShape Wire(Rect src, Rect dst)
        {
            if (src.Intersects(dst)) return default;
            Point sp = src.Center, dp = dst.Center;

            // As Connection: each end is clipped where the line toward the other box leaves its own box.
            Point a1 = sp, a2 = Far(sp, dp);
            Clipper.ClipLineByTwoRoundedRects(ref a1, ref a2, src, dst, ClipRadius);
            Point b1 = dp, b2 = Far(dp, sp);
            Clipper.ClipLineByTwoRoundedRects(ref b1, ref b2, dst, src, ClipRadius);
            sp = a1;
            dp = b1;

            var mid = new Point(0.5 * (sp.X + dp.X), 0.5 * (sp.Y + dp.Y));
            // The triangle is turned by the angle from (-1, 0) to the wire's normal.
            double angle = AngleBetween(new Vector(-1, 0), new Vector(dp.Y - sp.Y, sp.X - dp.X)) * Math.PI / 180;
            Point Turn(double x, double y) => new Point(mid.X + x * Math.Cos(angle) - y * Math.Sin(angle), mid.Y + x * Math.Sin(angle) + y * Math.Cos(angle));
            return new WireShape(sp, dp, mid, Turn(0, -9), Turn(-10, 9), Turn(10, 9));
        }

        /// <summary>A point far along the line from p toward q (Connection.SnapPlug without snapping).</summary>
        static Point Far(Point p, Point q)
        {
            var v = q - p;
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (len == 0) return new Point(p.X, p.Y + 10000);
            return p + v / len * 10000;
        }

        /// <summary>WPF's Vector.AngleBetween, in degrees.</summary>
        public static double AngleBetween(Vector v1, Vector v2)
        {
            double sin = v1.X * v2.Y - v2.X * v1.Y;
            double cos = v1.X * v2.X + v1.Y * v2.Y;
            return Math.Atan2(sin, cos) * (180 / Math.PI);
        }

        /// <summary>The arrow takes clicks (the theme's triangle is the wire's control).</summary>
        public static bool HitsArrow(WireShape w, Point p)
        {
            if (!w.Visible) return false;
            if (InTriangle(p, w.Apex, w.Left, w.Right)) return true;
            var d = p - w.Mid;
            return d.X * d.X + d.Y * d.Y <= 6 * 6;
        }

        static bool InTriangle(Point p, Point a, Point b, Point c)
        {
            double Side(Point p1, Point p2, Point p3) => (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
            double d1 = Side(p, a, b), d2 = Side(p, b, c), d3 = Side(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        /// <summary>The mute LED: 8×8 at 2,2 in the box (MachineControl.xaml, ledButton).</summary>
        public static Rect Led(Rect box) => new Rect(box.X + 2, box.Y + 2, 8, 8);

        /// <summary>The delete button: 9×9, 3 in from the top right (deleteButton).</summary>
        public static Rect DeleteButton(Rect box) => new Rect(box.Right - 3 - 9, box.Y + 3, 9, 9);
    }

    /// <summary>BuzzGUI.MachineView's Clipper: a line from a box's centre clipped at its rounded border.</summary>
    internal static class Clipper
    {
        static double Sgn(double x) => x < 0 ? -1 : 1;

        // in: p1, p2 = line; out: p1, p2 = intersection coordinates
        public static bool LineCircleIntersect(ref Point p1, ref Point p2, Point cp, double r)
        {
            double x1 = p1.X - cp.X, y1 = p1.Y - cp.Y, x2 = p2.X - cp.X, y2 = p2.Y - cp.Y;
            double dx = x2 - x1, dy = y2 - y1;
            double dr = Math.Sqrt(dx * dx + dy * dy);
            double D = x1 * y2 - x2 * y1;
            double disc = r * r * dr * dr - D * D;
            if (disc <= 0) return false;
            p1 = new Point(cp.X + (D * dy - Sgn(dy) * dx * Math.Sqrt(disc)) / (dr * dr), cp.Y + (-D * dx - Math.Abs(dy) * Math.Sqrt(disc)) / (dr * dr));
            p2 = new Point(cp.X + (D * dy + Sgn(dy) * dx * Math.Sqrt(disc)) / (dr * dr), cp.Y + (-D * dx + Math.Abs(dy) * Math.Sqrt(disc)) / (dr * dr));
            return true;
        }

        public static Point ClipLineByRect(Point p1, Point p2, Rect r)
        {
            double x = p2.X, y = p2.Y;
            if (y < r.Top)
            {
                double a = (r.Top - p1.Y) / (y - p1.Y);
                x = p1.X + a * (x - p1.X);
                y = r.Top;
            }
            else if (y > r.Bottom)
            {
                double a = (r.Bottom - p1.Y) / (y - p1.Y);
                x = p1.X + a * (x - p1.X);
                y = r.Bottom;
            }
            if (x < r.Left)
            {
                double a = (r.Left - p1.X) / (x - p1.X);
                y = p1.Y + a * (y - p1.Y);
                x = r.Left;
            }
            else if (x > r.Right)
            {
                double a = (r.Right - p1.X) / (x - p1.X);
                y = p1.Y + a * (y - p1.Y);
                x = r.Right;
            }
            return new Point(x, y);
        }

        public static Point ClipLineByRoundedRect(Point p1, Point p2, Rect r, double rad)
        {
            p2 = ClipLineByRect(p1, p2, r);
            double rcx = r.Left + r.Width / 2, rcy = r.Top + r.Height / 2;
            Point i1 = p1, i2 = p2;
            if (i2.Y < rcy)
            {
                if (i2.X < rcx)
                {
                    if (LineCircleIntersect(ref i1, ref i2, new Point(r.Left + rad, r.Top + rad), rad) && i1.X < r.Left + rad && i1.Y < r.Top + rad)
                        p2 = i1;
                }
                else if (i2.X > rcx)
                {
                    if (LineCircleIntersect(ref i1, ref i2, new Point(r.Right - rad, r.Top + rad), rad) && i1.X > r.Right - rad && i1.Y < r.Top + rad)
                        p2 = i1;
                }
            }
            else
            {
                if (i2.X < rcx)
                {
                    if (LineCircleIntersect(ref i1, ref i2, new Point(r.Left + rad, r.Bottom - rad), rad) && i2.X < r.Left + rad && i2.Y > r.Bottom - rad)
                        p2 = i2;
                }
                else if (i2.X > rcx)
                {
                    if (LineCircleIntersect(ref i1, ref i2, new Point(r.Right - rad, r.Bottom - rad), rad) && i2.X > r.Right - rad && i2.Y > r.Bottom - rad)
                        p2 = i2;
                }
            }
            return p2;
        }

        public static void ClipLineByTwoRoundedRects(ref Point p1, ref Point p2, Rect r1, Rect r2, double rad)
        {
            p1 = ClipLineByRoundedRect(p1, p2, r1, rad);
            p2 = ClipLineByRoundedRect(p2, p1, r2, rad);
        }
    }
}
