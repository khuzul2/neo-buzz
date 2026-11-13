#nullable disable
// Neo Learn L2b's effects (docs/specs/neo-learn.md §4, "Adds"): a chorus, a grit stage and a room, in the shared engine so Dye Essence can adopt them later.
// None of them is a new algorithm. Each takes a kernel the Neo machines already have and gives it one knob:
//   ChorusFx  the "dimension" chorus (NeoBuzz.Dsp.DimensionChorus: short modulated delays, the two channels half a cycle apart) made warmer, the way a
//             bucket-brigade chorus is: a gentle top roll-off and a little noise under the wet signal.
//   GritFx    the bit-depth and sample-rate reducer with its wow (NeoBuzz.Dsp.Bitcrusher), one knob from clean to 12 bits at 55 % of the host's rate
//             with a slow pitch wobble. At 0 the stage is out of the path: the block passes untouched and nothing is delayed (the wobble's fixed 2 ms delay
//             belongs to the wet signal alone; it fades in over 10 ms when the knob leaves 0).
//   RoomFx    Neo Verb's Room early reflections (the same 12-tap pattern, seeded as Neo Verb seeds its Room mode, scaled by Size) and a short damped tail of
//             four combs and two all-passes (NeoBuzz.Dsp.AllpassDiffuser), under the reflections.
//   TiltFx    the sampler's tone control: a gentle low-pass to one side of the middle, a high shelf with a low shelf under it to the other, around 1 kHz.
// Each reads the stereo block in place, allocates nothing after construction, passes the block untouched at an amount of 0, turns a non-finite input into 0,
// flushes its feedback paths, and moves its amounts through a one-pole smoother (no zipper noise). IEEE basic operations and PortableMath only, so the same bits
// on every platform.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    /// <summary>What the Neo Learn machines run after the engine: the effects a machine's map asks for, built (off the audio thread) for a sample rate.</summary>
    internal sealed class LearnFxSet
    {
        public readonly ChorusFx Chorus;
        public readonly GritFx Grit;
        public readonly RoomFx Room;
        public readonly TiltFx Tilt;

        public LearnFxSet(float sampleRate, bool chorus, bool grit, bool room, bool tilt)
        {
            if (chorus) Chorus = new ChorusFx(sampleRate);
            if (grit) Grit = new GritFx(sampleRate);
            if (room) Room = new RoomFx(sampleRate);
            if (tilt) Tilt = new TiltFx(sampleRate);
        }

        /// <summary>Tone, grit, chorus, room: in the order a sampler's signal would meet them.</summary>
        public void Process(float[] l, float[] r, int n)
        {
            Tilt?.Process(l, r, n);
            Grit?.Process(l, r, n);
            Chorus?.Process(l, r, n);
            Room?.Process(l, r, n);
        }

        public void Reset()
        {
            Tilt?.Reset();
            Grit?.Reset();
            Chorus?.Reset();
            Room?.Reset();
        }
    }

    internal static class LearnFxMath
    {
        /// <summary>The one-pole coefficient (per sample) for a time constant.</summary>
        public static float Coefficient(double seconds, float sampleRate) => (float)(1 - PortableMath.Exp(-1.0 / (seconds * sampleRate)));

        /// <summary>Non-finite becomes 0 (an engine that went wrong must not reach a feedback path).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clean(float x) => float.IsFinite(x) ? x : 0f;
    }

    /// <summary>A warm stereo chorus on the output: dry plus a wet signal from the dimension chorus, softened and lightly noisy. One knob (Amount, 0 … 1).</summary>
    internal sealed class ChorusFx
    {
        /// <summary>The noise under the wet signal (linear, about -66 dBFS) and the wet's top roll-off (Hz).</summary>
        public const float NoiseLevel = 0.0005f;
        public const double ToneHz = 6500;

        readonly DimensionChorus chorus;
        readonly float[] wetL = new float[NoteBank.MaxBlock], wetR = new float[NoteBank.MaxBlock];
        readonly float smoothK;
        Rng rng = new Rng(0xC0A05E5u);
        float noiseL, noiseR;
        float amount, mix;

        public ChorusFx(float sampleRate)
        {
            chorus = new DimensionChorus(sampleRate)
            {
                Voices = 2,
                DelayMs = 7f,
                Width = 1f,
                ToneHz = ToneHz,
                LowCutHz = 70,
                Drift = 0.25f,
                Feedback = 0f,
            };
            smoothK = LearnFxMath.Coefficient(0.02, sampleRate);
        }

        /// <summary>0 (off: the block passes untouched) … 1 (the wet at 0.8 of the dry, the rate and depth at their widest).</summary>
        public float Amount { get => amount; set => amount = Math.Clamp(value, 0f, 1f); }

        public void Reset()
        {
            chorus.Reset();
            mix = 0;
            noiseL = noiseR = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, int n)
        {
            if (amount <= 0f && mix < 1e-5f)
            {
                mix = 0;
                return;
            }
            chorus.RateHz = 0.35f + 0.55f * amount;
            chorus.Depth = 0.3f + 0.4f * amount;
            float target = 0.8f * amount;
            for (int i = 0; i < n; i++)
            {
                float a = LearnFxMath.Clean(l[i]), b = LearnFxMath.Clean(r[i]);
                l[i] = a;
                r[i] = b;
                wetL[i] = a;
                wetR[i] = b;
            }
            chorus.Process(wetL, wetR, n);
            // the bucket-brigade's noise: white, low-passed, only under the wet signal
            float nl = noiseL, nr = noiseR;
            float m = mix;
            for (int i = 0; i < n; i++)
            {
                m += (target - m) * smoothK;
                nl += (rng.NextBipolar() - nl) * 0.25f;
                nr += (rng.NextBipolar() - nr) * 0.25f;
                l[i] += m * (wetL[i] + NoiseLevel * nl);
                r[i] += m * (wetR[i] + NoiseLevel * nr);
            }
            noiseL = DspMath.Flush(nl);
            noiseR = DspMath.Flush(nr);
            mix = DspMath.Flush(m);
        }
    }

    /// <summary>
    /// Grit: bit depth, sample-rate reduction and tape wow, from one knob (Amount, 0 … 1): bits 16 → 12, the rate the host's → 55 % of it, the wobble a slow wow
    /// of up to 0.3 % of pitch. Out of the path at 0; fades in over 10 ms.
    /// </summary>
    internal sealed class GritFx
    {
        public const float BitsAtFull = 12f;
        public const double RateShareAtFull = 0.55;
        public const float WobbleAtFull = -0.5f;

        readonly Bitcrusher crusher;
        readonly float rate;
        readonly float[] wetL = new float[NoteBank.MaxBlock], wetR = new float[NoteBank.MaxBlock];
        readonly float fadeStep;
        float amount, fade;

        public GritFx(float sampleRate)
        {
            rate = sampleRate;
            crusher = new Bitcrusher(sampleRate) { AntiAlias = false, Dither = 0f, Quantizer = CrusherQuantizer.MidTread };
            fadeStep = 1f / Math.Max(1f, 0.010f * sampleRate);
        }

        /// <summary>0 (clean, out of the path) … 1 (12 bits, 55 % of the rate, a slow wow).</summary>
        public float Amount { get => amount; set => amount = Math.Clamp(value, 0f, 1f); }

        /// <summary>The bits, rate and wobble an amount stands for (the picture and the tests read the same).</summary>
        public static void Settings(float amount, double sampleRate, out float bits, out double rateHz, out float wobble)
        {
            float a = Math.Clamp(amount, 0f, 1f);
            bits = 16f - (16f - BitsAtFull) * a;
            rateHz = sampleRate * (1.0 - (1.0 - RateShareAtFull) * a);
            wobble = WobbleAtFull * a;
        }

        public void Reset()
        {
            crusher.Reset();
            fade = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, int n)
        {
            if (amount <= 0f && fade <= 0f) return;
            Settings(amount, rate, out float bits, out double rateHz, out float wobble);
            crusher.Bits = bits;
            crusher.RateHz = rateHz;
            crusher.Wobble = wobble;
            for (int i = 0; i < n; i++)
            {
                float a = LearnFxMath.Clean(l[i]), b = LearnFxMath.Clean(r[i]);
                l[i] = a;
                r[i] = b;
                wetL[i] = a;
                wetR[i] = b;
            }
            crusher.Process(wetL, wetR, n);
            float f = fade, step = amount > 0f ? fadeStep : -fadeStep;
            for (int i = 0; i < n; i++)
            {
                f = Math.Clamp(f + step, 0f, 1f);
                l[i] += (DspMath.Flush(wetL[i]) - l[i]) * f;
                r[i] += (DspMath.Flush(wetR[i]) - r[i]) * f;
            }
            fade = f;
        }
    }

    /// <summary>
    /// A small room: Neo Verb's Room early reflections (12 taps between 3 and 40 ms, seeded as its Room mode seeds them, scaled by <see cref="Size"/>) and a short
    /// damped tail (four combs, two all-passes) of 0.45 s at Size 1. One knob (Amount, 0 … 1): the wet signal's level, up to 0.7 of the dry.
    /// </summary>
    internal sealed class RoomFx
    {
        public const int Taps = 12;
        public const double ErMinMs = 3, ErMaxMs = 40;
        /// <summary>Neo Verb's Room is its mode 1 (its generator seeds a pattern with 0xBEEF00 + mode).</summary>
        public const uint Seed = 0xBEEF00u + 1;
        static readonly double[] CombMs = { 23.1, 29.7, 37.1, 41.1 };
        static readonly double[] DiffuserMs = { 5.3, 1.7 };

        readonly float sr;
        readonly FracDelay preL, preR, feed;
        readonly FracDelay[] combL = new FracDelay[4], combR = new FracDelay[4];
        readonly float[] combLp = new float[8];
        readonly AllpassDiffuser[] apL = new AllpassDiffuser[2], apR = new AllpassDiffuser[2];
        readonly float[] timeL = new float[Taps], timeR = new float[Taps], gainL = new float[Taps], gainR = new float[Taps];
        readonly float[] combDelay = new float[8], combG = new float[8];
        readonly float smoothK;
        float amount, mix, size = 1f, sizeApplied = -1f;

        public RoomFx(float sampleRate)
        {
            sr = sampleRate;
            // the longest reflection and comb at Size 2, plus the lines' headroom
            int max = (int)(sampleRate * ErMaxMs * 2 * 0.001) + 8;
            preL = new FracDelay(max);
            preR = new FracDelay(max);
            feed = new FracDelay((int)(sampleRate * 0.03) + 8);
            for (int k = 0; k < 4; k++)
            {
                combL[k] = new FracDelay((int)(sampleRate * CombMs[k] * 2 * 0.001) + 8);
                combR[k] = new FracDelay((int)(sampleRate * CombMs[k] * 1.13 * 2 * 0.001) + 8);
            }
            for (int k = 0; k < 2; k++)
            {
                apL[k] = new AllpassDiffuser((int)(sampleRate * DiffuserMs[k] * 2 * 0.001) + 8);
                apR[k] = new AllpassDiffuser((int)(sampleRate * DiffuserMs[k] * 1.13 * 2 * 0.001) + 8);
            }
            smoothK = LearnFxMath.Coefficient(0.02, sampleRate);
            Setup(1f);
        }

        /// <summary>0 (dry: the block passes untouched) … 1.</summary>
        public float Amount { get => amount; set => amount = Math.Clamp(value, 0f, 1f); }

        /// <summary>The room's scale, 0.5 … 2 (the reflections' times and the tail's lengths).</summary>
        public float Size { get => size; set => size = Math.Clamp(value, 0.5f, 2f); }

        /// <summary>
        /// The reflection pattern (the taps' times in samples and gains, left and right), as Neo Verb's engine draws it for its Room mode (VerbEngine, "Early
        /// reflections: a seeded pattern per mode, scaled by size"), at <paramref name="scale"/>.
        /// </summary>
        public static void Pattern(double scale, double rate, float[] timeL, float[] timeR, float[] gainL, float[] gainR)
        {
            var er = new Rng(Seed);
            for (int k = 0; k < Taps; k++)
            {
                double t = ErMinMs + (ErMaxMs - ErMinMs) * (k + 0.5 * er.NextUnipolar()) / Taps;
                double t2 = ErMinMs + (ErMaxMs - ErMinMs) * (k + 0.5 * er.NextUnipolar()) / Taps;
                double decay = PortableMath.Exp(-2.2 * (t - ErMinMs) / (ErMaxMs - ErMinMs));
                timeL[k] = (float)(t * scale * 0.001 * rate);
                timeR[k] = (float)(t2 * scale * 0.001 * rate);
                gainL[k] = (float)(decay * (0.8 + 0.4 * er.NextUnipolar()) * ((er.NextUInt() & 1) == 0 ? 1 : -1) / Math.Sqrt(Taps / 4.0));
                gainR[k] = (float)(decay * (0.8 + 0.4 * er.NextUnipolar()) * ((er.NextUInt() & 1) == 0 ? 1 : -1) / Math.Sqrt(Taps / 4.0));
            }
        }

        void Setup(float scale)
        {
            sizeApplied = scale;
            Pattern(scale, sr, timeL, timeR, gainL, gainR);
            // each comb's feedback for a T60 of 0.45 s × size (the loop's own damping makes the highs die sooner)
            double t60 = 0.45 * scale;
            for (int k = 0; k < 4; k++)
            {
                combDelay[k] = (float)(CombMs[k] * scale * 0.001 * sr);
                combDelay[4 + k] = (float)(CombMs[k] * 1.13 * scale * 0.001 * sr);
                combG[k] = (float)Math.Min(0.92, PortableMath.Pow(10.0, -3.0 * CombMs[k] * scale * 0.001 / t60));
                combG[4 + k] = (float)Math.Min(0.92, PortableMath.Pow(10.0, -3.0 * CombMs[k] * 1.13 * scale * 0.001 / t60));
            }
            for (int k = 0; k < 2; k++)
            {
                apL[k].Delay = (float)(DiffuserMs[k] * scale * 0.001 * sr);
                apR[k].Delay = (float)(DiffuserMs[k] * 1.13 * scale * 0.001 * sr);
                apL[k].Gain = apR[k].Gain = 0.55f;
            }
        }

        public void Reset()
        {
            preL.Reset(); preR.Reset(); feed.Reset();
            for (int k = 0; k < 4; k++) { combL[k].Reset(); combR[k].Reset(); }
            for (int k = 0; k < 2; k++) { apL[k].Reset(); apR[k].Reset(); }
            Array.Clear(combLp);
            mix = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, int n)
        {
            if (amount <= 0f && mix < 1e-5f)
            {
                mix = 0;
                return;
            }
            if (size != sizeApplied) Setup(size);
            float target = 0.7f * amount;
            float m = mix;
            const float damp = 0.35f;   // the loop's one-pole: each pass takes 35 % of the new
            for (int i = 0; i < n; i++)
            {
                float a = LearnFxMath.Clean(l[i]), b = LearnFxMath.Clean(r[i]);
                preL.Write(a);
                preR.Write(b);
                float erL = 0, erR = 0;
                for (int k = 0; k < Taps; k++)
                {
                    erL += gainL[k] * preL.ReadLinear(timeL[k] + 1);
                    erR += gainR[k] * preR.ReadLinear(timeR[k] + 1);
                }
                // the tail starts after the first reflections: the mono sum, 12 ms late
                feed.Write(0.5f * (a + b));
                float x = feed.ReadLinear(0.012f * sr + 1) * 0.25f;
                float tl = 0, tr = 0;
                for (int k = 0; k < 4; k++)
                {
                    float yl = combL[k].ReadLinear(Math.Max(1f, combDelay[k]));
                    float yr = combR[k].ReadLinear(Math.Max(1f, combDelay[4 + k]));
                    combLp[k] = DspMath.Flush(combLp[k] + damp * (yl - combLp[k]));
                    combLp[4 + k] = DspMath.Flush(combLp[4 + k] + damp * (yr - combLp[4 + k]));
                    combL[k].Write(DspMath.Flush(x + combG[k] * combLp[k]));
                    combR[k].Write(DspMath.Flush(x + combG[4 + k] * combLp[4 + k]));
                    tl += yl;
                    tr += yr;
                }
                for (int k = 0; k < 2; k++)
                {
                    tl = apL[k].Process(tl);
                    tr = apR[k].Process(tr);
                }
                m += (target - m) * smoothK;
                l[i] = a + m * (erL + 0.5f * tl);
                r[i] = b + m * (erR + 0.5f * tr);
            }
            mix = DspMath.Flush(m);
        }
    }

    /// <summary>
    /// A tone control around 1 kHz: below the middle (Tone under 0) a gentle low-pass whose cutoff falls from 18 kHz to 1.5 kHz; above it a high shelf of up to
    /// +10 dB at 1 kHz with a low shelf of up to -4 dB under it. At 0 the block passes untouched.
    /// </summary>
    internal sealed class TiltFx
    {
        public const double PivotHz = 1000, LowCutoffHz = 1500, HighCutoffHz = 18000;
        public const double ShelfDb = 10, LowShelfDb = -4;

        readonly float sr;
        Biquad lpL, lpR, hsL, hsR, lsL, lsR;
        float tone, applied = 2f, smoothed;
        int mode;
        readonly float smoothK;

        public TiltFx(float sampleRate)
        {
            sr = sampleRate;
            smoothK = LearnFxMath.Coefficient(0.02, sampleRate);
        }

        /// <summary>-1 (dark) … 0 (flat: out of the path) … +1 (bright).</summary>
        public float Tone { get => tone; set => tone = Math.Clamp(value, -1f, 1f); }

        /// <summary>The curve's level (dB) at <paramref name="hz"/> for a Tone, for the picture and the tests.</summary>
        public static double ResponseDb(double tone, double hz, double sampleRate)
        {
            var lp = new Biquad(); var hs = new Biquad(); var ls = new Biquad();
            Coefficients(tone, sampleRate, ref lp, ref hs, ref ls);
            double db = 0;
            if (tone < -1e-4) db += 20 * PortableMath.Log10(Math.Max(1e-9, lp.Magnitude(hz, sampleRate)));
            if (tone > 1e-4) db += 20 * PortableMath.Log10(Math.Max(1e-9, hs.Magnitude(hz, sampleRate) * ls.Magnitude(hz, sampleRate)));
            return db;
        }

        static void Coefficients(double tone, double sr, ref Biquad lp, ref Biquad hs, ref Biquad ls)
        {
            if (tone < -1e-4)
            {
                double cutoff = Math.Min(sr * 0.45, HighCutoffHz * PortableMath.Pow(LowCutoffHz / HighCutoffHz, -tone));
                lp.Set(BiquadType.LowPass, cutoff, sr, 0.7071);
            }
            else if (tone > 1e-4)
            {
                hs.Set(BiquadType.HighShelf, PivotHz, sr, 0.7071, ShelfDb * tone);
                ls.Set(BiquadType.LowShelf, PivotHz, sr, 0.7071, LowShelfDb * tone);
            }
        }

        public void Reset()
        {
            lpL.Reset(); lpR.Reset(); hsL.Reset(); hsR.Reset(); lsL.Reset(); lsR.Reset();
            smoothed = 0;
            applied = 2f;
            mode = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, int n)
        {
            // the tone moves by a smoother once a block; the coefficients follow it
            smoothed += (tone - smoothed) * Math.Min(1f, smoothK * n);
            if (MathF.Abs(tone - smoothed) < 1e-4f) smoothed = tone;
            int now = smoothed < -1e-4f ? -1 : smoothed > 1e-4f ? 1 : 0;
            if (now == 0)
            {
                mode = 0;
                return;
            }
            if (now != mode)
            {
                // the other side of the middle: its filters start from silence
                mode = now;
                lpL.Reset(); lpR.Reset(); hsL.Reset(); hsR.Reset(); lsL.Reset(); lsR.Reset();
                applied = 2f;
            }
            if (MathF.Abs(smoothed - applied) > 1e-4f)
            {
                applied = smoothed;
                Coefficients(smoothed, sr, ref lpL, ref hsL, ref lsL);
                Coefficients(smoothed, sr, ref lpR, ref hsR, ref lsR);
            }
            for (int i = 0; i < n; i++)
            {
                float a = LearnFxMath.Clean(l[i]), b = LearnFxMath.Clean(r[i]);
                if (now < 0) { a = lpL.Process(a); b = lpR.Process(b); }
                else { a = lsL.Process(hsL.Process(a)); b = lsR.Process(hsR.Process(b)); }
                l[i] = a;
                r[i] = b;
            }
            if (now < 0) { lpL.Flush(); lpR.Flush(); }
            else { hsL.Flush(); hsR.Flush(); lsL.Flush(); lsR.Flush(); }
        }
    }
}
