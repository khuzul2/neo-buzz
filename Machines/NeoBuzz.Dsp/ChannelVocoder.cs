#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4; first user: Dye Essence's vocoder rack type, docs/specs/dye-essence.md §4.10):
//  - ChannelVocoder: a channel vocoder. The modulator (mono) goes through a bank of 8 … 32 band-pass filters spaced evenly in
//    log frequency from LowHz to HighHz (centres LowHz · (HighHz / LowHz)^(i / (N − 1)); each band is two cascaded biquad
//    band-passes whose width is the spacing, so neighbours cross at about −4 dB); each band's level is followed (an attack and
//    a release) and scales the same band of the carrier (stereo: one synthesis bank a channel). The synthesis bank's centres
//    move by the formant shift (±12 semitones: the voice's formants without its pitch). Sibilance: the modulator's part above
//    6 kHz is added as it is, in proportion to how much of the modulator's level is up there (an "s" or "t" has no pitch to
//    carry, so the carrier's bands alone make it a hiss; this brings the original back), gated by that noisiness.
//    No latency. No allocation after construction.
//  - VoiceModulator: a deterministic stand-in for a voice, to use where no modulator is connected (and in tests): a glottal
//    pulse train with a little breath through three formant filters that glide between the vowels a e i o u, one syllable
//    at a time (3 a second at speed 1), the pitch moving a little from syllable to syllable, and every fourth syllable
//    a fricative (noise above 5 kHz). The same samples after every Reset.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal sealed class ChannelVocoder
    {
        public const int MaxBands = 32, MinBands = 8;

        readonly float rate;
        readonly Biquad[] modA = new Biquad[MaxBands], modB = new Biquad[MaxBands];
        readonly Biquad[] carLA = new Biquad[MaxBands], carLB = new Biquad[MaxBands], carRA = new Biquad[MaxBands], carRB = new Biquad[MaxBands];
        readonly float[] env = new float[MaxBands];
        Biquad sibA, sibB, bodyA;
        float sibEnv, allEnv;
        float attackCoef, releaseCoef;
        // The settings the filters were designed for.
        int builtBands;
        float builtLow, builtHigh, builtShift;

        /// <summary>8 … 32.</summary>
        public int Bands = 16;
        /// <summary>The carrier bank's shift against the modulator's, in semitones (-12 … +12).</summary>
        public float FormantSemitones;
        public float LowHz = 120, HighHz = 10000;
        public float AttackMs = 2, ReleaseMs = 60;
        /// <summary>0 … 1: how much of the modulator's own highs are let through when it is noisy.</summary>
        public float Sibilance = 0.35f;
        /// <summary>A gain on the output (linear).</summary>
        public float Output = 1;


        public ChannelVocoder(float sampleRate)
        {
            rate = sampleRate;
            Reset();
        }

        public int Latency => 0;

        /// <summary>Band <paramref name="i"/>'s centre of the analysis bank at the settings (Hz); the synthesis centre is that times 2^(shift / 12).</summary>
        public static double Centre(int bands, double low, double high, int i) =>
            bands <= 1 ? low : low * PortableMath.Pow(high / low, i / (double)(bands - 1));

        public void Reset()
        {
            for (int i = 0; i < MaxBands; i++)
            {
                modA[i].Reset(); modB[i].Reset();
                carLA[i].Reset(); carLB[i].Reset(); carRA[i].Reset(); carRB[i].Reset();
                env[i] = 0;
            }
            sibA.Reset(); sibB.Reset(); bodyA.Reset();
            sibEnv = allEnv = 0;
            builtBands = 0;
        }

        void Design(int bands, float low, float high, float shift)
        {
            double top = Math.Min(high, rate * 0.45);
            double lo = Math.Min(low, top * 0.5);
            double spacing = bands <= 1 ? 1 : PortableMath.Pow(top / lo, 1.0 / (bands - 1));
            // Bandwidth in octaves: the spacing, a little wider; Q from it (RBJ band-pass: BW = 2 asinh(1 / 2Q) / ln 2); two
            // cascaded biquads, each a bit narrower than that so the pair is about the width.
            double bw = Math.Max(0.15, PortableMath.Log2(spacing) * 1.15);
            double h = 0.5 * 0.6931471805599453 * bw;
            double q = 1 / (PortableMath.Exp(h) - PortableMath.Exp(-h));
            double qEach = Math.Max(0.5, 0.75 * q);
            double shiftRatio = PortableMath.Pow(2, shift / 12.0);
            for (int i = 0; i < MaxBands; i++)
            {
                if (i >= bands) continue;
                double c = Centre(bands, lo, top, i);
                modA[i].Set(BiquadType.BandPass, c, rate, qEach);
                modB[i].Set(BiquadType.BandPass, c, rate, qEach);
                double cs = Math.Min(c * shiftRatio, rate * 0.45);
                carLA[i].Set(BiquadType.BandPass, cs, rate, qEach);
                carLB[i].Set(BiquadType.BandPass, cs, rate, qEach);
                carRA[i].Set(BiquadType.BandPass, cs, rate, qEach);
                carRB[i].Set(BiquadType.BandPass, cs, rate, qEach);
            }
            sibA.Set(BiquadType.HighPass, 6000, rate, 0.7071);
            sibB.Set(BiquadType.HighPass, 6000, rate, 0.7071);
            bodyA.Set(BiquadType.LowPass, 3000, rate, 0.7071);
            builtBands = bands; builtLow = low; builtHigh = high; builtShift = shift;
        }

        /// <summary>
        /// The carrier (<paramref name="l"/>, <paramref name="r"/>) vocoded by the modulator <paramref name="mod"/> (mono), in place.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, float[] mod, int n)
        {
            int bands = Math.Clamp(Bands, MinBands, MaxBands);
            float shift = Math.Clamp(FormantSemitones, -12f, 12f);
            if (bands != builtBands || LowHz != builtLow || HighHz != builtHigh || shift != builtShift) Design(bands, LowHz, HighHz, shift);
            attackCoef = (float)(1 - PortableMath.Exp(-1.0 / (Math.Max(0.1, AttackMs) * 0.001 * rate)));
            releaseCoef = (float)(1 - PortableMath.Exp(-1.0 / (Math.Max(1.0, ReleaseMs) * 0.001 * rate)));
            float sibCoef = (float)(1 - PortableMath.Exp(-1.0 / (0.004 * rate)));
            // The bands add up to about the carrier's level for a flat modulator; this brings a voice's average back.
            float gain = Output * 2.2f * (float)Math.Sqrt(16.0 / bands);
            float sib = Math.Clamp(Sibilance, 0f, 1f);
            for (int i = 0; i < n; i++)
            {
                float m = mod[i];
                float cl = l[i], cr = r[i];
                float ol = 0, orr = 0;
                for (int b = 0; b < bands; b++)
                {
                    float a = modB[b].Process(modA[b].Process(m));
                    float rect = a < 0 ? -a : a;
                    float e = env[b];
                    e += (rect > e ? attackCoef : releaseCoef) * (rect - e);
                    env[b] = e;
                    ol += carLB[b].Process(carLA[b].Process(cl)) * e;
                    orr += carRB[b].Process(carRA[b].Process(cr)) * e;
                }
                float hi = sibB.Process(sibA.Process(m));
                float body = bodyA.Process(m);
                float hiAbs = hi < 0 ? -hi : hi, allAbs = (m < 0 ? -m : m);
                sibEnv += sibCoef * (hiAbs - sibEnv);
                allEnv += sibCoef * (allAbs - allEnv);
                // Noisy when the highs are most of the modulator's level; voiced speech has a body that the highs don't match.
                float noisy = allEnv > 1e-6f ? sibEnv / allEnv : 0;
                float gate = noisy < 0.35f ? 0 : noisy > 0.6f ? 1 : (noisy - 0.35f) / 0.25f;
                float s = hi * sib * gate * 2f;
                l[i] = ol * gain + s;
                r[i] = orr * gain + s;
            }
        }
    }

    internal sealed class VoiceModulator
    {
        // Formants (Hz) of a e i o u.
        static readonly float[][] Vowels =
        {
            new[] { 800f, 1200f, 2500f }, new[] { 400f, 2000f, 2600f }, new[] { 300f, 2300f, 3000f }, new[] { 450f, 800f, 2500f }, new[] { 325f, 700f, 2500f },
        };
        static readonly int[] Pattern = { 0, 1, 3, 2, 4, 0, 3, 1 };
        static readonly float[] Contour = { 0, 2, -2, 0 };

        readonly float rate;
        readonly Biquad[] formant = new Biquad[3];
        Biquad hiNoise, tone;
        uint seed;
        double phase;
        long sample;
        float lp;

        /// <summary>0.25 … 4: the syllable rate against 3 a second.</summary>
        public float Speed = 1;

        public VoiceModulator(float sampleRate)
        {
            rate = sampleRate;
            Reset();
        }

        public void Reset()
        {
            for (int i = 0; i < 3; i++) formant[i].Reset();
            hiNoise.Reset(); tone.Reset();
            seed = 0x2545F491u;
            phase = 0;
            sample = 0;
            lp = 0;
            hiNoise.Set(BiquadType.HighPass, 5000, rate, 0.7071);
            tone.Set(BiquadType.LowPass, 7000, rate, 0.7071);
            SetFormants(0, 0, 0);
        }

        float Noise()
        {
            seed = seed * 1664525u + 1013904223u;
            return ((seed >> 8) / 16777216f) * 2 - 1;
        }

        void SetFormants(int from, int to, float t)
        {
            for (int i = 0; i < 3; i++)
            {
                float f = Vowels[from][i] + (Vowels[to][i] - Vowels[from][i]) * t;
                formant[i].Set(BiquadType.BandPass, f, rate, i == 0 ? 7 : i == 1 ? 9 : 11);
            }
        }

        /// <summary>The next <paramref name="n"/> samples (mono, peaking around -6 dBFS: the band levels the vocoder follows are the modulator's, so a modulator of this level gives a vocoded sound about 3 dB under its carrier).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Render(float[] dst, int n)
        {
            double syllable = rate / (3.0 * Math.Clamp(Speed, 0.25f, 4f));
            for (int i = 0; i < n; i++)
            {
                int syl = (int)(sample / syllable);
                double inSyl = (sample - syl * syllable) / syllable;
                if ((sample & 31) == 0)
                {
                    int cur = Pattern[syl & 7], next = Pattern[(syl + 1) & 7];
                    float glide = inSyl < 0.7 ? 0f : (float)((inSyl - 0.7) / 0.3);
                    SetFormants(cur, next, glide);
                }
                bool fricative = (syl & 3) == 3;
                float f0 = 110f * (float)PortableMath.Pow(2, Contour[syl & 3] / 12.0);
                phase += f0 / rate;
                if (phase >= 1) phase -= 1;
                float saw = (float)(phase * 2 - 1);
                lp += 0.35f * (saw - lp);
                float noise = Noise();
                float source = lp * 0.9f + noise * 0.08f;
                float voiced = 0;
                for (int k = 0; k < 3; k++) voiced += formant[k].Process(source) * (k == 0 ? 1f : k == 1 ? 0.7f : 0.5f);
                // A raised-cosine syllable envelope over 80 % of the syllable.
                float amp = inSyl < 0.8 ? (float)(0.5 - 0.5 * PortableMath.Cos(2 * Math.PI * inSyl / 0.8)) : 0f;
                float y;
                if (fricative)
                {
                    float hiN = tone.Process(hiNoise.Process(noise));
                    y = hiN * 6f * (float)(inSyl < 0.5 ? PortableMath.Sin(Math.PI * inSyl / 0.5) : 0) + voiced * amp * 0.8f;
                }
                else y = voiced * amp * 4.5f;
                dst[i] = y;
                sample++;
            }
        }
    }
}
