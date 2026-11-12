#nullable disable
// Neo EQ v2 (docs/specs/mastering-tools.md §5.3, M3): a dynamic band's section. The TPT state-variable section of
// SvfSection, operation for operation (the same output for the same coefficients), whose coefficients move between
// the designs made on the 16-sample grid sample by sample: a1, a2, a3 and the output mix m0, m1, m2 each follow the
// uniform quadratic B-spline of the last three designs (D₋₂, D₋₁, D₀ over a segment: from (D₋₂ + D₋₁)/2 to
// (D₋₁ + D₀)/2, its slope continuous from segment to segment), stepped by forward differences and landing on the
// segment's end value exactly. Straight lines (a first-order hold) left kinks every 16 samples whose images, at
// 3 kHz ± the sound, measured 8 dB over designing every sample with the gain swinging 12 dB (attack 10 ms); the
// spline's are under it. It trails the designs by 24 samples (straight lines: 16).
// Allocation-free; double precision.

using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Eq
{
    internal struct RampSection
    {
        double ic1, ic2;
        // The coefficients now, their forward differences and second differences, and where the segment ends.
        double a1, a2, a3, m0, m1, m2;
        double d1, d2, d3, e0, e1, e2;
        double dd1, dd2, dd3, ee0, ee1, ee2;
        double t1, t2, t3, n0, n1, n2;
        // The two designs before the newest (the spline's control points).
        double p1a, p2a, p3a, q0a, q1a, q2a, p1b, p2b, p3b, q0b, q1b, q2b;
        int left;

        static void Coefficients(in SvfSection s, out double a1, out double a2, out double a3)
        {
            // As SvfSection.Set.
            a1 = 1.0 / (1.0 + s.G * (s.G + s.K));
            a2 = s.G * a1;
            a3 = s.G * a2;
        }

        /// <summary>The coefficients of <paramref name="s"/> at once and from now on (the state kept).</summary>
        public void Snap(in SvfSection s)
        {
            Coefficients(s, out a1, out a2, out a3);
            m0 = s.M0;
            m1 = s.M1;
            m2 = s.M2;
            p1a = p1b = a1;
            p2a = p2b = a2;
            p3a = p3b = a3;
            q0a = q0b = m0;
            q1a = q1b = m1;
            q2a = q2b = m2;
            left = 0;
        }

        /// <summary>A new design: the next <paramref name="steps"/> samples follow the spline towards it.</summary>
        public void RampTo(in SvfSection s, int steps)
        {
            Coefficients(s, out double c1, out double c2, out double c3);
            double c4 = s.M0, c5 = s.M1, c6 = s.M2;
            double u = 1.0 / steps;
            bool still = c1 == p1b && c2 == p2b && c3 == p3b && c4 == q0b && c5 == q1b && c6 == q2b
                && p1a == p1b && p2a == p2b && p3a == p3b && q0a == q0b && q1a == q1b && q2a == q2b;
            Segment(p1a, p1b, c1, u, out d1, out dd1, out t1);
            Segment(p2a, p2b, c2, u, out d2, out dd2, out t2);
            Segment(p3a, p3b, c3, u, out d3, out dd3, out t3);
            Segment(q0a, q0b, c4, u, out e0, out ee0, out n0);
            Segment(q1a, q1b, c5, u, out e1, out ee1, out n1);
            Segment(q2a, q2b, c6, u, out e2, out ee2, out n2);
            p1a = p1b; p2a = p2b; p3a = p3b; q0a = q0b; q1a = q1b; q2a = q2b;
            p1b = c1; p2b = c2; p3b = c3; q0b = c4; q1b = c5; q2b = c6;
            left = still ? 0 : steps;
        }

        /// <summary>
        /// One coefficient's segment: B(t) = a + b t + c t², t = (j + 1)/steps, with a = (x + y)/2 (where it is), b = y − x,
        /// c = (x − 2y + z)/2; its first step b u + c u², each step after it 2 c u² more; its end (y + z)/2.
        /// </summary>
        static void Segment(double x, double y, double z, double u, out double first, out double second, out double end)
        {
            double b = y - x, c = 0.5 * (x - 2 * y + z);
            first = b * u + c * u * u;
            second = 2 * c * u * u;
            end = 0.5 * (y + z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Process(double v0)
        {
            if (left > 0)
            {
                if (--left == 0)
                {
                    a1 = t1; a2 = t2; a3 = t3;
                    m0 = n0; m1 = n1; m2 = n2;
                }
                else
                {
                    a1 += d1; a2 += d2; a3 += d3;
                    m0 += e0; m1 += e1; m2 += e2;
                    d1 += dd1; d2 += dd2; d3 += dd3;
                    e0 += ee0; e1 += ee1; e2 += ee2;
                }
            }
            double v3 = v0 - ic2;
            double v1 = a1 * ic1 + a2 * v3;
            double v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = 2 * v1 - ic1;
            ic2 = 2 * v2 - ic2;
            return m0 * v0 + m1 * v1 + m2 * v2;
        }

        public void Reset()
        {
            ic1 = ic2 = 0;
        }

        /// <summary>State under 1e-30 to zero (on the engine's grid, not per host block).</summary>
        public void Flush()
        {
            ic1 = DspMath.Flush(ic1);
            ic2 = DspMath.Flush(ic2);
        }
    }
}
