#nullable disable
// Neo machines shared DSP (docs/specs/dye-essence.md section 4.10, P4b1): a bit-depth and sample-rate reducer.
//  - Bits: 1 ... 16, fractional (the step is 2^(1 - bits) of full scale); the quantiser is mid-tread (rounds, silence
//    stays silence), mid-rise (no zero level: a sign-only signal at 1 bit) or companded (a mu-law curve before and
//    its inverse after, so small signals keep resolution and the noise follows the signal); an optional bias (up to
//    half a step) before and after it moves the grid without leaving a DC offset; the input is clipped at full scale; an optional TPDF dither (the step's size)
//    turns the staircase's distortion into noise.
//  - Rate: the signal is held for sampleRate / rate samples (any real ratio from 1 up: a phase accumulator takes a new
//    sample every time it wraps, so a fractional ratio gives the uneven holds of a real converter's clock).
//  - Anti-aliasing (optional): an eighth-order Butterworth low-pass at 0.4 of the held rate before the hold, so the
//    content above half the new rate does not fold down, and a fourth-order one at 0.45 after it, so the hold's
//    images are removed: the result is a band-limited reduction. Off, the hold aliases and images freely (the lo-fi sound).
//  - Wobble (optional): a tape-like pitch drift after the crusher, from a delay modulated by a slow wow (about 0.8 Hz,
//    up to about 0.6 % of pitch) or a faster flutter (about 7 Hz, up to about 0.3 %), both with a little random
//    wander. Its latency is the delay's fixed 2 ms (reported by LatencySamples; the wet is late by that whatever the
//    wobble, which keeps it independent of the parameters).
// Wet only: the blend with the dry is the caller's. Allocation-free after construction; IEEE basic operations only
// (PortableMath for the rest), so the same bits on every platform. The planned Neo Learn synths use it as their
// lo-fi stage.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal enum CrusherQuantizer { MidTread, MidRise, Companded }

    internal sealed class Bitcrusher
    {
        const double PreRatio = 0.4, PostRatio = 0.45;
        const float WowHz = 0.8f, FlutterHz = 7f, WowMs = 1.2f, FlutterMs = 0.07f, CentreMs = 2f;

        readonly float sr;
        readonly Biquad[] preL = new Biquad[4], preR = new Biquad[4], postL = new Biquad[2], postR = new Biquad[2];
        Biquad toneL, toneR;
        readonly FracDelay wobL, wobR;
        Rng rng = new Rng(0xB17C0DE5u);
        float heldL, heldR, acc, driftWow, driftFlutter, wowPhase, flutterPhase;
        double rateApplied = -1, toneApplied = -1;
        bool aaApplied;

        /// <summary>1 ... 16, fractional.</summary>
        public float Bits = 8;
        /// <summary>The target sample rate (Hz); a rate at or above the host's does not hold.</summary>
        public double RateHz = 12000;
        public bool AntiAlias = true;
        /// <summary>The input gain into the quantiser (linear).</summary>
        public float Drive = 1;
        public CrusherQuantizer Quantizer;
        /// <summary>A bias inside the quantiser, -1 ... 1 of half a step.</summary>
        public float Bias;
        /// <summary>The TPDF dither, 0 ... 1 of the step.</summary>
        public float Dither;
        /// <summary>The output low-pass (Hz).</summary>
        public double ToneHz = 20000;
        /// <summary>-1 (full wow) ... 0 (off) ... 1 (full flutter).</summary>
        public float Wobble;
        /// <summary>The output gain (linear).</summary>
        public float Output = 1;

        public Bitcrusher(float sampleRate)
        {
            sr = sampleRate;
            int max = (int)(sampleRate * (CentreMs + WowMs + FlutterMs + 1.5f) * 0.001f) + 8;
            wobL = new FracDelay(max);
            wobR = new FracDelay(max);
            Reset();
        }

        /// <summary>The wobble's delay (fixed, so the dry can be aligned to the wet whatever the parameters).</summary>
        public static int LatencySamples(double sampleRate) => (int)Math.Round(CentreMs * 0.001 * sampleRate);

        public int Latency => LatencySamples(sr);

        public void Reset()
        {
            for (int i = 0; i < 4; i++) { preL[i].Reset(); preR[i].Reset(); }
            for (int i = 0; i < 2; i++) { postL[i].Reset(); postR[i].Reset(); }
            toneL.Reset(); toneR.Reset();
            wobL.Reset(); wobR.Reset();
            heldL = heldR = 0;
            acc = 1;
            driftWow = driftFlutter = 0;
            wowPhase = flutterPhase = 0;
            rateApplied = toneApplied = -1;
            aaApplied = false;
        }

        // Butterworth sections' Q for an even order.
        static double SectionQ(int order, int k) => 1.0 / (2 * PortableMath.Cos(Math.PI * (2 * k + 1) / (2.0 * order)));

        void Setup(double rate)
        {
            double tone = Math.Clamp(ToneHz, 500, sr * 0.45);
            if (tone != toneApplied)
            {
                toneApplied = tone;
                toneL.Set(BiquadType.LowPass, tone, sr, 0.7071);
                toneR.Set(BiquadType.LowPass, tone, sr, 0.7071);
            }
            if (rate != rateApplied || AntiAlias != aaApplied)
            {
                rateApplied = rate;
                aaApplied = AntiAlias;
                double pre = Math.Min(sr * 0.45, rate * PreRatio), post = Math.Min(sr * 0.45, rate * PostRatio);
                for (int k = 0; k < 4; k++)
                {
                    preL[k].Set(BiquadType.LowPass, pre, sr, SectionQ(8, k));
                    preR[k].Set(BiquadType.LowPass, pre, sr, SectionQ(8, k));
                }
                for (int k = 0; k < 2; k++)
                {
                    postL[k].Set(BiquadType.LowPass, post, sr, SectionQ(4, k));
                    postR[k].Set(BiquadType.LowPass, post, sr, SectionQ(4, k));
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float Quantise(float x, float step, float bias, float dither)
        {
            x = Math.Clamp(x, -1f, 1f);
            if (Quantizer == CrusherQuantizer.Companded)
            {
                // mu-law (mu = 255): the curve's output is quantised, then expanded.
                const float Mu = 255f;
                float ax = Math.Min(1f, Math.Abs(x));
                float c = MathF.Sign(x) * (float)(PortableMath.Log(1 + Mu * ax) / PortableMath.Log(1 + Mu));
                float q = Round(c, step, 0, dither);
                float aq = Math.Min(1f, Math.Abs(q));
                return MathF.Sign(q) * (float)((PortableMath.Exp(aq * PortableMath.Log(1 + Mu)) - 1) / Mu);
            }
            if (Quantizer == CrusherQuantizer.MidRise)
            {
                float d = dither > 0 ? dither * step * (rng.NextBipolar() + rng.NextBipolar()) * 0.5f : 0;
                float v = x + bias + d;
                float q = (MathF.Floor(v / step) + 0.5f) * step;
                return q - bias;
            }
            return Round(x, step, bias, dither);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float Round(float x, float step, float bias, float dither)
        {
            float d = dither > 0 ? dither * step * (rng.NextBipolar() + rng.NextBipolar()) * 0.5f : 0;
            float v = x + bias + d;
            return MathF.Floor(v / step + 0.5f) * step - bias;
        }

        /// <summary>Turns the stereo block into its wet signal, in place.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, int n)
        {
            double rate = Math.Clamp(RateHz, 20.0, sr);
            Setup(rate);
            float step = (float)PortableMath.Pow(2.0, 1.0 - Math.Clamp(Bits, 1f, 16f));   // full scale -1 ... 1: 2 / 2^bits
            float ratio = (float)(sr / rate);
            bool aa = AntiAlias && ratio > 1.0001f;
            float drive = Drive, bias = Math.Clamp(Bias, -1f, 1f) * 0.5f * step;
            float dither = Math.Clamp(Dither, 0f, 1f);
            float wob = Math.Clamp(Wobble, -1f, 1f);
            float wowDepth = wob < 0 ? -wob * WowMs * 0.001f * sr : 0;
            float flutterDepth = wob > 0 ? wob * FlutterMs * 0.001f * sr : 0;
            float centre = CentreMs * 0.001f * sr;
            float wowInc = WowHz / sr, flutterInc = FlutterHz / sr;
            float driftC = (float)(1 - PortableMath.Exp(-2 * Math.PI * 1.5 / sr));
            float driftG = (float)(Math.Sqrt((2 - driftC) / driftC) * Math.Sqrt(3.0) * 0.4);
            float maxDelay = wobL.MaxDelay - 4;
            float gain = Output;
            for (int i = 0; i < n; i++)
            {
                float xl = l[i] * drive, xr = r[i] * drive;
                if (aa)
                {
                    for (int k = 0; k < 4; k++) { xl = preL[k].Process(xl); xr = preR[k].Process(xr); }
                }
                acc += 1f;
                if (acc >= ratio)
                {
                    acc -= ratio;
                    if (acc >= 1f) acc = 0;
                    heldL = Quantise(xl, step, bias, dither);
                    heldR = Quantise(xr, step, bias, dither);
                }
                float yl = heldL, yr = heldR;
                if (aa)
                {
                    for (int k = 0; k < 2; k++) { yl = postL[k].Process(yl); yr = postR[k].Process(yr); }
                }
                yl = toneL.Process(yl) * gain;
                yr = toneR.Process(yr) * gain;
                // The wobble: a delay modulated around its fixed centre.
                driftWow += (rng.NextBipolar() - driftWow) * driftC * 0.25f;
                driftFlutter += (rng.NextBipolar() - driftFlutter) * driftC * 4f;
                wowPhase += wowInc; if (wowPhase >= 1f) wowPhase -= 1f;
                flutterPhase += flutterInc; if (flutterPhase >= 1f) flutterPhase -= 1f;
                float mod = 0;
                if (wowDepth > 0) mod = wowDepth * (0.8f * Sine(wowPhase) + 0.2f * Math.Clamp(driftG * driftWow, -1f, 1f));
                else if (flutterDepth > 0) mod = flutterDepth * (0.8f * Sine(flutterPhase) + 0.2f * Math.Clamp(driftG * driftFlutter, -1f, 1f));
                float t = Math.Clamp(centre + mod, 2f, maxDelay);
                wobL.Write(yl);
                wobR.Write(yr);
                l[i] = wobL.ReadCubic(t);
                r[i] = wobR.ReadCubic(t);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Sine(float cycles)
        {
            float x = cycles - MathF.Floor(cycles + 0.5f);
            float y = 4 * x * (1 - 2 * MathF.Abs(x));
            return 0.225f * (y * MathF.Abs(y) - y) + y;
        }
    }
}
