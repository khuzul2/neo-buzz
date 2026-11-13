#nullable disable
// Neo machines shared DSP (docs/specs/dye-essence.md section 4.10, P4b1): a delay with a pitch shifter in its
// feedback loop (the "shimmer" and "falling" echoes): each repeat is shifted again, so +12 semitones climbs an octave
// a repeat and -12 sinks one. A second mode shifts only what is heard (every echo by the same amount: the repeats
// themselves are plain).
//  - The shifter is the two-tap crossfaded delay-line kind (PitchShifterGrain): two read points sweep through a short
//    buffer at (1 - ratio) samples a sample and cross-fade with complementary Hann windows, a grain (20 ... 200 ms) long.
//    It delays by half a grain on average; the delay line's time is shortened by that so the first echo falls on
//    the time that was set (when the time is at least half a grain).
//  - Time changes glide (a one-pole on the read position, the tape sound).
//  - A low-pass and a high-pass sit in the loop, and the two channels can differ in time (Spread) and feed each other
//    (Cross, a ping-pong at 1).
// Wet only (the echoes: the dry is the caller's); no latency. Allocation-free after construction; IEEE basic operations
// and PortableMath only.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    /// <summary>A two-tap, crossfaded delay-line pitch shifter for one channel (mono). Reusable on its own.</summary>
    internal sealed class PitchShifterGrain
    {
        readonly FracDelay line;
        readonly float maxGrain;
        float phase;

        public PitchShifterGrain(int maxGrainSamples)
        {
            line = new FracDelay(maxGrainSamples + 8);
            maxGrain = maxGrainSamples;
        }

        public void Reset() { line.Reset(); phase = 0; }

        /// <summary>One sample: <paramref name="ratio"/> is the pitch ratio, <paramref name="grain"/> the window length in samples.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process(float x, float ratio, float grain)
        {
            grain = Math.Clamp(grain, 16f, maxGrain);
            line.Write(x);
            phase += (1f - ratio) / grain;
            phase -= MathF.Floor(phase);
            float p2 = phase + 0.5f;
            if (p2 >= 1f) p2 -= 1f;
            float c = Cos(phase);
            float wa = 0.5f - 0.5f * c;
            float a = line.ReadCubic(Math.Max(2f, phase * grain));
            float b = line.ReadCubic(Math.Max(2f, p2 * grain));
            return a * wa + b * (1f - wa);
        }

        // cos(2 pi p), p in 0 ... 1: a polynomial sine of the phase a quarter ahead (IEEE basic operations only).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Cos(float p)
        {
            float x = p + 0.25f;
            x -= MathF.Floor(x + 0.5f);
            float y = 4 * x * (1 - 2 * MathF.Abs(x));
            return 0.225f * (y * MathF.Abs(y) - y) + y;
        }
    }

    internal sealed class PitchShiftDelay
    {
        public const float MaxTimeMs = 2000, MaxGrainMs = 200;

        readonly float sr;
        readonly FracDelay dlL, dlR;
        readonly PitchShifterGrain psL, psR;
        Biquad lpL, lpR, hpL, hpR;
        float fbL, fbR, tL, tR, slew;
        double toneApplied = -1, lowApplied = -1;
        bool primed;

        /// <summary>The first echo's time (ms), 10 ... 2000.</summary>
        public float TimeMs = 300;
        public float Semitones = 12;
        public float Cents;
        /// <summary>0 ... 0.95.</summary>
        public float Feedback = 0.4f;
        public float GrainMs = 60;
        /// <summary>The shifter inside the loop (every repeat shifted again) instead of on the output.</summary>
        public bool PerRepeat = true;
        public double ToneHz = 6000;
        public double LowCutHz = 80;
        /// <summary>The right channel's time is the left's times (1 + 0.5 * Spread), -1 ... 1.</summary>
        public float Spread;
        /// <summary>The share of each channel's feedback that goes to the other, 0 ... 1.</summary>
        public float Cross = 0.3f;

        public PitchShiftDelay(float sampleRate)
        {
            sr = sampleRate;
            int max = (int)(sampleRate * MaxTimeMs * 0.001f * 1.5f) + 16;
            dlL = new FracDelay(max);
            dlR = new FracDelay(max);
            int grain = (int)(sampleRate * MaxGrainMs * 0.001f) + 8;
            psL = new PitchShifterGrain(grain);
            psR = new PitchShifterGrain(grain);
            slew = (float)(1 - PortableMath.Exp(-1.0 / (0.08 * sampleRate)));
            Reset();
        }

        public int Latency => 0;

        public void Reset()
        {
            dlL.Reset(); dlR.Reset(); psL.Reset(); psR.Reset();
            lpL.Reset(); lpR.Reset(); hpL.Reset(); hpR.Reset();
            fbL = fbR = 0;
            primed = false;
            toneApplied = lowApplied = -1;
        }

        void Setup()
        {
            double tone = Math.Clamp(ToneHz, 500, 20000), low = Math.Clamp(LowCutHz, 10, 2000);
            if (tone != toneApplied)
            {
                toneApplied = tone;
                lpL.Set(BiquadType.LowPass, tone, sr, 0.7071);
                lpR.Set(BiquadType.LowPass, tone, sr, 0.7071);
            }
            if (low != lowApplied)
            {
                lowApplied = low;
                hpL.Set(BiquadType.HighPass, low, sr, 0.7071);
                hpR.Set(BiquadType.HighPass, low, sr, 0.7071);
            }
        }

        /// <summary>Turns the stereo block into its wet signal, in place.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, int n)
        {
            Setup();
            float grain = Math.Clamp(GrainMs, 20f, MaxGrainMs) * 0.001f * sr;
            float ratio = (float)PortableMath.Pow(2.0, (Semitones + Cents * 0.01) / 12.0);
            float fb = Math.Clamp(Feedback, 0f, 0.95f);
            float cross = Math.Clamp(Cross, 0f, 1f);
            float spread = Math.Clamp(Spread, -1f, 1f);
            float t = Math.Clamp(TimeMs, 10f, MaxTimeMs) * 0.001f * sr;
            float maxDelay = dlL.MaxDelay - 4;
            float targetL = Math.Clamp(t - 0.5f * grain, 2f, maxDelay);
            float targetR = Math.Clamp(t * (1 + 0.5f * spread) - 0.5f * grain, 2f, maxDelay);
            if (!primed) { tL = targetL; tR = targetR; primed = true; }
            bool perRepeat = PerRepeat;
            for (int i = 0; i < n; i++)
            {
                tL += (targetL - tL) * slew;
                tR += (targetR - tR) * slew;
                dlL.Write(l[i] + fbL);
                dlR.Write(r[i] + fbR);
                float rl = dlL.ReadCubic(tL), rr = dlR.ReadCubic(tR);
                float sl = psL.Process(rl, ratio, grain), sr2 = psR.Process(rr, ratio, grain);
                float wl = lpL.Process(hpL.Process(sl)), wr = lpR.Process(hpR.Process(sr2));
                // The loop: the shifted echo (every repeat shifted again), or the plain one (only the output shifted).
                float el = perRepeat ? wl : rl, er = perRepeat ? wr : rr;
                float ml = el + cross * (er - el), mr = er + cross * (el - er);
                fbL = DspMath.Flush(fb * ml);
                fbR = DspMath.Flush(fb * mr);
                l[i] = wl;
                r[i] = wr;
            }
        }
    }
}
