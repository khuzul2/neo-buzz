#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.6): an LFO's drawn shape. Breakpoints across one cycle,
// each a phase (0 … 1) and a value (−1 … 1), and each segment to the next point bent by a curve
// (−1 … 1, as the envelopes' segments: 0 straight, below 0 a fast start, above a slow one). The shape
// repeats: the last point runs on to the first one cycle later, so a point at 0 and one at 1 make a
// jump at the wrap (a saw), and two points at the same phase a step (a square). Kept in the machine
// state as text ("0,0,-0.28 0.25,1,0.28 …": phase,value[,curve] per point), read by the editor,
// presets and agents; the audio thread reads an immutable shape (a new one replaces it whole).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NeoBuzz.DyeEssence
{
    internal sealed class DrawnShape
    {
        /// <summary>The most points a shape holds.</summary>
        public const int MaxPoints = 32;

        /// <summary>Values are kept to this step (four decimals), so a shape and its text are the same thing.</summary>
        const double Step = 1e-4;

        /// <summary>A sine drawn with four points (curves ∓0.28: within 0.044 of a sine).</summary>
        public const string DefaultText = "0,0,-0.28 0.25,1,0.28 0.5,0,-0.28 0.75,-1,0.28";

        public static readonly DrawnShape Default = Parse(DefaultText);

        readonly float[] x, y, curve;

        DrawnShape(float[] x, float[] y, float[] curve)
        {
            this.x = x;
            this.y = y;
            this.curve = curve;
        }

        public int Count => x.Length;
        public float X(int i) => x[i];
        public float Y(int i) => y[i];
        public float Curve(int i) => curve[i];

        /// <summary>A shape from points (phase 0 … 1, value −1 … 1, curve −1 … 1), clamped, rounded and sorted by phase (stable).</summary>
        public static DrawnShape From(IReadOnlyList<(double X, double Y, double Curve)> points)
        {
            if (points == null || points.Count == 0) return Default;
            int n = Math.Min(points.Count, MaxPoints);
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            var px = new double[n];
            for (int i = 0; i < n; i++) px[i] = Round(Math.Clamp(Finite(points[i].X), 0, 1));
            // Insertion sort: stable, so points at one phase keep their order (a step's two sides).
            for (int i = 1; i < n; i++)
                for (int j = i; j > 0 && px[order[j - 1]] > px[order[j]]; j--) (order[j - 1], order[j]) = (order[j], order[j - 1]);
            var x = new float[n];
            var y = new float[n];
            var c = new float[n];
            for (int i = 0; i < n; i++)
            {
                var p = points[order[i]];
                x[i] = (float)px[order[i]];
                y[i] = (float)Round(Math.Clamp(Finite(p.Y), -1, 1));
                c[i] = (float)Round(Math.Clamp(Finite(p.Curve), -1, 1));
            }
            return new DrawnShape(x, y, c);
        }

        static double Finite(double v) => double.IsFinite(v) ? v : 0;

        static double Round(double v) => Math.Round(v / Step) * Step;

        /// <summary>The shape a text describes; the default for an empty or unreadable one.</summary>
        public static DrawnShape Parse(string text) => TryParse(text, out var shape, out _) ? shape : Default;

        /// <summary>Reads a shape's text: points "phase,value" or "phase,value,curve" separated by spaces.</summary>
        public static bool TryParse(string text, out DrawnShape shape, out string error)
        {
            shape = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                shape = Default;
                return true;
            }
            var points = new List<(double, double, double)>();
            foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = token.Split(',');
                if (parts.Length < 2 || parts.Length > 3)
                {
                    error = $"\"{token}\" isn't a point: write phase,value or phase,value,curve (\"0.25,1,-0.3\")";
                    return false;
                }
                var v = new double[3];
                for (int k = 0; k < parts.Length; k++)
                    if (!double.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]) || !double.IsFinite(v[k]))
                    {
                        error = $"\"{parts[k]}\" in \"{token}\" isn't a number";
                        return false;
                    }
                if (v[0] < 0 || v[0] > 1)
                {
                    error = $"{token}: the phase is 0 … 1 (one cycle)";
                    return false;
                }
                if (v[1] < -1 || v[1] > 1)
                {
                    error = $"{token}: the value is −1 … 1";
                    return false;
                }
                if (v[2] < -1 || v[2] > 1)
                {
                    error = $"{token}: the curve is −1 … 1";
                    return false;
                }
                points.Add((v[0], v[1], v[2]));
            }
            if (points.Count == 0)
            {
                shape = Default;
                return true;
            }
            if (points.Count > MaxPoints)
            {
                error = $"{points.Count} points; a shape holds at most {MaxPoints}";
                return false;
            }
            shape = From(points);
            return true;
        }

        /// <summary>The shape as text (Parse reads it back to the same shape).</summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Text(x[i])).Append(',').Append(Text(y[i]));
                if (curve[i] != 0) sb.Append(',').Append(Text(curve[i]));
            }
            return sb.ToString();
        }

        static string Text(float v) => Round(v).ToString("0.####", CultureInfo.InvariantCulture);

        /// <summary>Whether two shapes are the same points.</summary>
        public bool SameAs(DrawnShape other)
        {
            if (other == null || other.Count != Count) return false;
            for (int i = 0; i < Count; i++)
                if (x[i] != other.x[i] || y[i] != other.y[i] || curve[i] != other.curve[i]) return false;
            return true;
        }

        /// <summary>The value (−1 … 1) at <paramref name="phase"/> (cycles, 0 … 1). No allocation; portable maths.</summary>
        public double Value(double phase)
        {
            int n = x.Length;
            if (n == 1) return y[0];
            // The segment the phase is in starts at the last point at or before it (the later of two at one phase).
            int i = n - 1;
            while (i >= 0 && x[i] > phase) i--;
            double x0, y0, x1, y1;
            float c;
            if (i < 0)
            {
                // Before the first point: the segment from the last point, a cycle back.
                x0 = x[n - 1] - 1.0;
                y0 = y[n - 1];
                c = curve[n - 1];
                x1 = x[0];
                y1 = y[0];
            }
            else
            {
                x0 = x[i];
                y0 = y[i];
                c = curve[i];
                if (i + 1 < n)
                {
                    x1 = x[i + 1];
                    y1 = y[i + 1];
                }
                else
                {
                    x1 = x[0] + 1.0;
                    y1 = y[0];
                }
            }
            double w = x1 - x0;
            double t = w > 1e-9 ? (phase - x0) / w : 1.0;
            return y0 + (y1 - y0) * ModEnvelope.Shape(t, c);
        }
    }
}
