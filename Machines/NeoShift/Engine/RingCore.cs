#nullable disable
// Neo Shift's ring modulator (docs/specs/neo-shift.md): the sound multiplied by a carrier of 1 Hz ... 5 kHz.
//  - The carrier is one oscillator for both channels: a sine from a double-precision phase (the sidebands sit at f +/- fc to well under 0.1 Hz),
//    a triangle or a square from the shared BlepOscillator (band-limited: BLEP for the square's jumps, BLAMP for the triangle's corners).
//    All three run on one phase increment; a change of shape crossfades the old carrier into the new one over 20 ms.
//  - The carrier's frequency glides (a one-pole over 30 ms, in the log domain), the increment moving linearly across each block.
//  - Depth blends amplitude modulation into ring modulation: the gain is g = (1 - d)/2 + (1 + d)/2 c, with c the carrier from -1 to 1. At d = 0
//    that is a tremolo (1/2 + c/2: the sound stays, between 0 and 1 times itself); at d = 1 it is c itself, and the sound's own frequencies are
//    gone, leaving f + fc and f - fc.
// Allocation-free; IEEE basic operations and PortableMath only.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Shift
{
    internal sealed class RingCore
    {
        public const double MinHz = 1, MaxHz = 5000;
        const double GlideSeconds = 0.03, FadeSeconds = 0.02;

        readonly double sr;
        readonly BlepOscillator tri = new BlepOscillator(), sq = new BlepOscillator();
        double phase;
        double hz;
        bool primed;
        int shape = -1, prevShape = -1;
        float fade = 1f;
        readonly float fadeStep;
        SmoothRamp depth;

        public RingCore(double sampleRate)
        {
            sr = sampleRate;
            tri.SetShape(1f, 0.5f);
            sq.SetShape(3f, 0.5f);
            fadeStep = (float)(1.0 / (FadeSeconds * sampleRate));
            depth.Setup(0.02, sampleRate);
            Reset();
        }

        public void Reset()
        {
            phase = 0;
            tri.Reset(0);
            sq.Reset(0);
            primed = false;
            shape = prevShape = -1;
            fade = 1f;
            depth.Reset(1f);
        }

        /// <summary>The carrier's frequency now (Hz).</summary>
        public double Hz => hz;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float Wave(int which, double p, float triV, float sqV)
        {
            switch (which)
            {
                case 1: return triV;
                case 2: return sqV;
                default: return (float)PortableMath.Sin(2 * Math.PI * p);
            }
        }

        /// <summary>The ring modulation of the stereo block, in place.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, int n, double targetHz, int wantedShape, float wantedDepth)
        {
            targetHz = Math.Clamp(targetHz, MinHz, MaxHz);
            if (!primed)
            {
                hz = targetHz;
                shape = wantedShape;
                depth.Reset(wantedDepth);
                primed = true;
                // The band-limited oscillators answer BlepTable.Z samples late: run them that far ahead so that the first sample is the carrier's.
                double inc00 = hz / sr;
                double start = -BlepTable.Z * inc00;
                start -= Math.Floor(start);
                tri.Reset(start);
                sq.Reset(start);
                for (int i = 0; i < BlepTable.Z; i++)
                {
                    tri.Next(inc00, out _, out _);
                    sq.Next(inc00, out _, out _);
                }
            }
            if (wantedShape != shape)
            {
                prevShape = shape;
                shape = wantedShape;
                fade = 0f;
            }
            depth.SetTarget(wantedDepth);
            double hz0 = hz;
            // glide in the log domain
            double k = 1 - PortableMath.Exp(-(n / sr) / GlideSeconds);
            double lg = PortableMath.Log(hz0) + (PortableMath.Log(targetHz) - PortableMath.Log(hz0)) * k;
            double hz1 = Math.Abs(lg - PortableMath.Log(targetHz)) < 1e-9 ? targetHz : PortableMath.Exp(lg);
            double inc0 = hz0 / sr, inc1 = hz1 / sr, dInc = (inc1 - inc0) / n;
            double inc = inc0;
            double ph = phase;
            bool both = fade < 1f;
            for (int i = 0; i < n; i++)
            {
                inc += dInc;
                ph += inc;
                ph -= Math.Floor(ph);
                float t = tri.Next(inc, out _, out _);
                float s = sq.Next(inc, out _, out _);
                float c = Wave(shape, ph, t, s);
                if (both)
                {
                    fade += fadeStep;
                    if (fade >= 1f) { fade = 1f; both = false; }
                    else c = Wave(prevShape, ph, t, s) * (1f - fade) + c * fade;
                }
                float d = depth.Next();
                float gain = 0.5f * (1f - d) + 0.5f * (1f + d) * c;
                l[i] *= gain;
                r[i] *= gain;
            }
            phase = ph;
            hz = hz1;
        }
    }
}
