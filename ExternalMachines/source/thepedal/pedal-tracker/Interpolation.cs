// Interpolation kernels for Pedal Tracker
//
// Six modes, ordered by quality / cost:
//
//   None        – zero-order hold (chiptune / glitch).               1 tap
//   Linear      – 2-point linear.                                    2 taps
//   Cubic       – 4-point Catmull-Rom cubic Hermite.                 4 taps
//   BSpline     – 4-point cubic B-spline (smoother, slight LP).      4 taps
//   Sinc16      – 16-tap polyphase windowed sinc, Blackman-Harris.   16 taps
//   Sinc32      – 32-tap polyphase windowed sinc, Blackman-Harris.   32 taps
//
// The sinc filterbanks are precomputed once on first use into a
// [PHASES][TAPS] table.  PHASES = 4096 gives sub-cent fractional
// resolution.  Lookup is then a fixed-cost dot product per output
// sample: no transcendental functions on the audio thread.
//
// All functions are STATELESS — caller passes the source buffer, the
// integer position and the fractional position [0,1).  Bounds checking
// (loop wrap, end-of-sample) is the caller's responsibility; these
// kernels assume enough taps are available either side of `pos`.
//
// Performance notes:
// - Sinc16 ≈ 16 mul-add per channel per output sample.
// - Sinc32 ≈ 32 mul-add per channel per output sample.
// - At 96 kHz × 16 voices stereo with Sinc32 that's ~98M MAC/s, which
//   is well within reach on any modern CPU.

using System;
using System.Threading;

namespace WDE.PedalTracker
{
    public enum Interp : int
    {
        None    = 0,
        Linear  = 1,
        Cubic   = 2,
        BSpline = 3,
        Sinc16  = 4,
        Sinc32  = 5,
    }

    internal static class Interpolation
    {
        // ── Fast paths ────────────────────────────────────────────────────────
        // Caller guarantees:
        //   * src has at least (taps) valid samples around `i0`
        //   * For Sinc16 / Sinc32 src index range [i0 - taps/2 + 1 .. i0 + taps/2]
        //     is valid (caller pads with zeros or wraps via loop)

        public static float None(float[] src, int i0)
            => src[i0];

        public static float Linear(float[] src, int i0, float t)
        {
            float a = src[i0];
            float b = src[i0 + 1];
            return a + (b - a) * t;
        }

        // 4-point Catmull-Rom cubic Hermite.
        // src[i0-1], src[i0], src[i0+1], src[i0+2] required.
        public static float Cubic(float[] src, int i0, float t)
        {
            float p0 = src[i0 - 1];
            float p1 = src[i0];
            float p2 = src[i0 + 1];
            float p3 = src[i0 + 2];

            float t2 = t * t;
            float t3 = t2 * t;

            return 0.5f * (
                  2f * p1
                + (-p0 + p2) * t
                + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                + (-p0 + 3f * p1 - 3f * p2 + p3) * t3
            );
        }

        // 4-point cubic B-spline – very smooth, gently low-pass.
        public static float BSpline(float[] src, int i0, float t)
        {
            float p0 = src[i0 - 1];
            float p1 = src[i0];
            float p2 = src[i0 + 1];
            float p3 = src[i0 + 2];

            float t2 = t * t;
            float t3 = t2 * t;

            float c0 = (-p0 + 3f * p1 - 3f * p2 + p3) * (1f / 6f);
            float c1 = ( p0 - 2f * p1 +     p2)        * 0.5f;
            float c2 = (-p0          +     p2)        * 0.5f;
            float c3 = ( p0 + 4f * p1 +     p2)        * (1f / 6f);

            return ((c0 * t + c1) * t + c2) * t + c3;
        }

        // ── Polyphase sinc filterbanks ───────────────────────────────────────

        const int PHASES = 4096;     // fractional-position resolution
        const int PHASE_MASK = PHASES - 1;

        static float[][] _sinc16;    // [PHASES][16]
        static float[][] _sinc32;    // [PHASES][32]

        static int _initFlag16;
        static int _initFlag32;

        public static void EnsureSinc16() { if (_sinc16 == null) BuildSincTable(16, ref _sinc16, ref _initFlag16); }
        public static void EnsureSinc32() { if (_sinc32 == null) BuildSincTable(32, ref _sinc32, ref _initFlag32); }

        public static float Sinc16(float[] src, int i0, float t)
        {
            // Precondition: EnsureSinc16() has been called from a non-audio
            // thread.  We do NOT lazy-init from inside Work().
            var table = _sinc16;
            int phase = (int)(t * PHASES) & PHASE_MASK;
            float[] k = table[phase];

            // 16 taps centred on i0:  src[i0-7 .. i0+8]
            int b = i0 - 7;
            float s = 0f;
            for (int n = 0; n < 16; n++)
                s += src[b + n] * k[n];
            return s;
        }

        public static float Sinc32(float[] src, int i0, float t)
        {
            var table = _sinc32;
            int phase = (int)(t * PHASES) & PHASE_MASK;
            float[] k = table[phase];

            int b = i0 - 15;
            float s = 0f;
            for (int n = 0; n < 32; n++)
                s += src[b + n] * k[n];
            return s;
        }

        // ── Sinc table construction ──────────────────────────────────────────
        // Polyphase windowed sinc with a Blackman-Harris window.
        // Each phase row corresponds to a fractional sample offset of
        // (phase / PHASES).  At phase 0 the kernel reduces to a delta.
        //
        // Cutoff is set to 0.95 × Nyquist of the *source* rate, which gives
        // a small guard band against aliasing on slight upsampling and is
        // standard for high-quality audio resamplers.

        static void BuildSincTable(int taps, ref float[][] target, ref int initFlag)
        {
            // Single-init guard – multiple voices may call EnsureSincNN()
            // simultaneously during construction.  Interlocked makes the
            // first caller build, the rest spin briefly until done.
            if (Interlocked.CompareExchange(ref initFlag, 1, 0) != 0)
            {
                while (target == null) Thread.SpinWait(64);
                return;
            }

            const double cutoff = 0.95;       // fraction of Nyquist
            int half = taps / 2;
            var t = new float[PHASES][];

            for (int p = 0; p < PHASES; p++)
            {
                var row = new float[taps];
                double frac = (double)p / PHASES;     // 0..1
                double sum  = 0.0;

                for (int n = 0; n < taps; n++)
                {
                    // x = sample index relative to ideal sample position
                    // For phase=0 the centre tap (n = half-1) lies exactly on
                    // the sample, giving a delta.
                    double x = (n - (half - 1)) - frac;

                    // Windowed sinc
                    double s = (x == 0.0) ? 1.0 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * x);

                    // Blackman-Harris 4-term window across the full kernel
                    double w = BlackmanHarris(n, taps);

                    double v = s * w;
                    row[n] = (float)v;
                    sum += v;
                }

                // Normalise so DC gain = 1 (preserves levels across phases)
                if (sum > 1e-12)
                {
                    float inv = (float)(1.0 / sum);
                    for (int n = 0; n < taps; n++) row[n] *= inv;
                }

                t[p] = row;
            }

            target = t;
        }

        static double BlackmanHarris(int n, int N)
        {
            // 4-term Blackman-Harris window – ~92 dB sidelobe rejection
            const double a0 = 0.35875, a1 = 0.48829, a2 = 0.14128, a3 = 0.01168;
            double x = 2.0 * Math.PI * n / (N - 1);
            return a0
                 - a1 * Math.Cos(x)
                 + a2 * Math.Cos(2.0 * x)
                 - a3 * Math.Cos(3.0 * x);
        }

        // ── Friendly names for ValueDescriptions ─────────────────────────────
        public static readonly string[] InterpNames = new[]
        {
            "None",
            "Linear",
            "Cubic",
            "B-Spline",
            "Sinc 16",
            "Sinc 32",
        };

        // ── Padding requirements ─────────────────────────────────────────────
        // How many samples must be valid BEFORE i0 and AFTER i0+1 for a
        // given mode.  Used by the voice loop to decide when to switch from
        // direct interpolation to a "guarded" copy that pads with zeros or
        // wraps the loop.
        public static int PadBefore(Interp m) => m switch
        {
            Interp.None    => 0,
            Interp.Linear  => 0,
            Interp.Cubic   => 1,
            Interp.BSpline => 1,
            Interp.Sinc16  => 7,
            Interp.Sinc32  => 15,
            _              => 0,
        };

        public static int PadAfter(Interp m) => m switch
        {
            Interp.None    => 0,
            Interp.Linear  => 1,
            Interp.Cubic   => 2,
            Interp.BSpline => 2,
            Interp.Sinc16  => 8,
            Interp.Sinc32  => 16,
            _              => 0,
        };
    }
}
