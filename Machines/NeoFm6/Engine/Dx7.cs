#nullable disable
// Neo FM6 (docs/specs/machines.md §5): the DX7's own behaviour, after the measurements and
// reverse engineering published with Music Synthesizer for Android (msfa, Apache 2.0) and Dexed:
// the log-domain operator envelope with its curved attack and "static" flat segments, output
// level, velocity, keyboard level and rate scaling, operator frequencies, the pitch envelope and
// the LFO. Levels are in DX7 units of 1/256 of a doubling (0.0235 dB); an operator's amplitude
// is 2^(level/256 − 14), so a modulator at full level deviates the phase by about 4π.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Fm6
{
    internal static class Dx7
    {
        static readonly int[] LevelLut = { 0, 5, 9, 13, 17, 20, 23, 25, 27, 29, 31, 33, 35, 37, 39, 41, 42, 43, 45, 46 };

        /// <summary>Output level 0 … 99 → the DX7's internal 0 … 127 scale.</summary>
        public static int ScaleOutLevel(int level)
        {
            level = Math.Clamp(level, 0, 99);
            return level >= 20 ? 28 + level : LevelLut[level];
        }

        static readonly byte[] VelocityData =
        {
            0, 70, 86, 97, 106, 114, 121, 126, 132, 138, 142, 148, 152, 156, 160, 163,
            166, 170, 173, 174, 178, 181, 184, 186, 189, 190, 194, 196, 198, 200, 202,
            205, 206, 209, 211, 214, 216, 218, 220, 222, 224, 225, 227, 229, 230, 232,
            233, 235, 237, 238, 240, 241, 242, 243, 244, 246, 246, 248, 249, 250, 251,
            252, 253, 254,
        };

        /// <summary>Velocity (0 … 127) and sensitivity (0 … 7) → level offset (1/256 doublings, ×… as msfa).</summary>
        public static int ScaleVelocity(int velocity, int sensitivity)
        {
            int v = VelocityData[Math.Clamp(velocity, 0, 127) >> 1] - 239;
            return ((sensitivity * v + 7) >> 3) << 4;
        }

        /// <summary>Rate scaling (0 … 7): extra envelope speed for higher notes.</summary>
        public static int ScaleRate(int midiNote, int sensitivity)
        {
            int x = Math.Clamp(midiNote / 3 - 7, 0, 31);
            return (sensitivity * x) >> 3;
        }

        static readonly byte[] ExpScale = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 14, 16, 19, 23, 27, 33, 39, 47, 56, 66, 80, 94, 110, 126, 142, 158, 174, 190, 206, 222, 238, 250 };

        static int ScaleCurve(int group, int depth, int curve)
        {
            int scale = curve == 0 || curve == 3
                ? (group * depth * 329) >> 12
                : (ExpScale[Math.Min(group, ExpScale.Length - 1)] * depth * 329) >> 15;
            return curve < 2 ? -scale : scale;
        }

        /// <summary>Keyboard level scaling: an offset to the 0 … 127 output level.</summary>
        public static int ScaleLevel(int midiNote, int breakPoint, int leftDepth, int rightDepth, int leftCurve, int rightCurve)
        {
            int offset = midiNote - breakPoint - 17;
            return offset >= 0
                ? ScaleCurve((offset + 1) / 3, rightDepth, rightCurve)
                : ScaleCurve(-(offset - 1) / 3, leftDepth, leftCurve);
        }

        /// <summary>The envelope's "output level" for a note: 0 … 4064 + velocity (as msfa's outlevel).</summary>
        public static int OutLevel(int level, int midiNote, int velocity, int breakPoint, int leftDepth, int rightDepth, int leftCurve, int rightCurve, int velSens)
        {
            int o = ScaleOutLevel(level) + ScaleLevel(midiNote, breakPoint, leftDepth, rightDepth, leftCurve, rightCurve);
            o = Math.Min(127, o) << 5;
            o += ScaleVelocity(velocity, velSens);
            return Math.Max(0, o);
        }

        static readonly double[] CoarseRatio = BuildCoarse();

        static double[] BuildCoarse()
        {
            var r = new double[32];
            r[0] = 0.5;
            for (int i = 1; i < 32; i++) r[i] = i;
            return r;
        }

        /// <summary>Ratio mode: the frequency ratio of coarse 0 … 31 and fine 0 … 99.</summary>
        public static double Ratio(int coarse, int fine) => CoarseRatio[coarse & 31] * (1 + 0.01 * fine);

        /// <summary>Fixed mode: Hz from coarse (only 0 … 3 matter: 1, 10, 100, 1000 Hz) and fine (×1 … ×9.77).</summary>
        public static double FixedHz(int coarse, int fine) => PortableMath.Pow(10, (coarse & 3) + fine / 100.0);

        /// <summary>Detune (0 … 14, 7 = none) in octaves at a note's frequency (about a cent a step at A4).</summary>
        public static double DetuneOctaves(double noteHz, int detune)
        {
            double l = PortableMath.Log2(Math.Max(1, noteHz));
            return 0.0209 * PortableMath.Exp(-0.396 * l) / 7 * l * (detune - 7);
        }

        /// <summary>Fixed-mode detune: only upwards, 13457/2^24 octave a step.</summary>
        public static double FixedDetuneOctaves(int detune) => detune > 7 ? 13457.0 / (1 << 24) * (detune - 7) : 0;

        static readonly short[] PitchTab =
        {
            -128, -116, -104, -95, -85, -76, -68, -61, -56, -52, -49, -46, -43, -41, -39, -37, -35, -33, -32, -31,
            -30, -29, -28, -27, -26, -25, -24, -23, -22, -21, -20, -19, -18, -17, -16, -15, -14, -13, -12, -11,
            -10, -9, -8, -7, -6, -5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 38, 40, 43, 46, 49, 53,
            58, 65, 73, 82, 92, 103, 115, 127,
        };

        /// <summary>Pitch envelope level 0 … 99 → octaves (50 → 0, ±4 octaves at the ends).</summary>
        public static double PitchLevelOctaves(int level) => PitchTab[Math.Clamp(level, 0, 99)] / 32.0;

        // Pitch envelope speed (octaves a second ×21.3), approximating the DX7's table.
        static readonly (int Rate, double Value)[] PitchRateAnchors =
        {
            (0, 1), (10, 7), (20, 10), (30, 14), (40, 21), (50, 33), (60, 50), (70, 72), (80, 110), (90, 170), (95, 211), (99, 255),
        };

        /// <summary>Pitch envelope rate 0 … 99 → octaves a second.</summary>
        public static double PitchRateOctavesPerSecond(int rate)
        {
            rate = Math.Clamp(rate, 0, 99);
            for (int i = 1; i < PitchRateAnchors.Length; i++)
            {
                var (r1, v1) = PitchRateAnchors[i];
                if (rate > r1) continue;
                var (r0, v0) = PitchRateAnchors[i - 1];
                return (v0 + (v1 - v0) * (rate - r0) / (r1 - r0)) / 21.3;
            }
            return 255 / 21.3;
        }

        /// <summary>LFO speed 0 … 99 → Hz (0.06 … 24).</summary>
        public static double LfoHz(int speed)
        {
            int sr = speed == 0 ? 1 : (165 * Math.Clamp(speed, 0, 99)) >> 6;
            sr *= sr < 160 ? 11 : 11 + ((sr - 160) >> 4);
            return sr * 25190424.0 / 4294967296.0;
        }

        /// <summary>LFO delay 0 … 99 → (seconds before the fade-in, seconds of the fade-in).</summary>
        public static (double Wait, double Fade) LfoDelay(int delay)
        {
            if (delay <= 0) return (0, 0);
            int a = 99 - Math.Clamp(delay, 0, 99);
            a = (16 + (a & 15)) << (1 + (a >> 4));
            int a2 = Math.Max(0x80, a & 0xff80);
            return (85.25 / a, 85.25 / a2);
        }

        static readonly int[] PitchModSens = { 0, 10, 20, 33, 55, 92, 153, 255 };

        /// <summary>
        /// LFO pitch modulation in octaves: depth 0 … 99 (scaled by the fade-in, 0 … 1), pitch
        /// sensitivity 0 … 7, LFO value 0 … 1. At depth 99 and sensitivity 7: ±1 octave.
        /// </summary>
        public static double PitchMod(double depthScaled, int sensitivity, double lfo) =>
            depthScaled * PitchModSens[Math.Clamp(sensitivity, 0, 7)] * (lfo - 0.5) / 32768.0;

        /// <summary>A 0 … 99 depth as the DX7 scales it (0 … 255).</summary>
        public static double Depth(int depth) => (Math.Clamp(depth, 0, 99) * 165) >> 6;

        static readonly double[] AmpModSens = { 0, 0.2588, 0.4274, 1.0 };

        /// <summary>Attenuation (doublings) from amplitude modulation 0 … 1 at an operator's sensitivity 0 … 3.</summary>
        public static double AmpModDoublings(double amount, int sensitivity) => amount * AmpModSens[Math.Clamp(sensitivity, 0, 3)] * 6.0;

        /// <summary>Samples (at 44.1 kHz) a flat envelope segment lasts at a rate (msfa's "statics", fitted).</summary>
        public static double StaticSamples44(int rate) =>
            rate < 77 ? 1764000.0 * PortableMath.Pow(2, -rate / 6.35) : 20.0 * (99 - rate);
    }

    /// <summary>
    /// One operator's envelope: four rates and levels, sustain at level 3 while the key is down,
    /// release to level 4. Advanced in blocks; returns its level in 1/256 doublings.
    /// </summary>
    internal sealed class Dx7Envelope
    {
        readonly int[] rates = new int[4], levels = new int[4];
        int outLevel, rateScaling, ix = 4;
        double level, target, inc, staticLeft, srScale = 1;
        bool rising, down;

        /// <summary>Level in 1/256 doublings (16 … about 4064).</summary>
        public double Level => level;
        /// <summary>Released and at its final level.</summary>
        public bool Done => ix >= 4;
        public bool KeyIsDown => down;

        /// <summary>Amplitude of a level: 2^(level/256 − 14).</summary>
        public static double Amplitude(double level) => PortableMath.Pow(2, level / 256.0 - 14);

        public void SetRate(double voiceRate) => srScale = 44100.0 / voiceRate;

        /// <summary>Starts a note from silence.</summary>
        public void Start(int[] r, int[] l, int outLevel, int rateScaling)
        {
            Array.Copy(r, rates, 4);
            Array.Copy(l, levels, 4);
            this.outLevel = outLevel;
            this.rateScaling = rateScaling;
            level = 0;
            down = true;
            Advance(0);
        }

        /// <summary>New settings while sounding: keeps the level, aims at the new target.</summary>
        public void Update(int[] r, int[] l, int outLevel, int rateScaling)
        {
            Array.Copy(r, rates, 4);
            Array.Copy(l, levels, 4);
            this.outLevel = outLevel;
            this.rateScaling = rateScaling;
            if (ix < 4)
            {
                double left = staticLeft;
                // Sustaining (segment 3 while the key is down doesn't move): head for the new level 3.
                Advance(down && ix == 3 ? 2 : ix);
                if (left > 0 && staticLeft > 0) staticLeft = left;   // a wait already under way goes on
            }
        }

        public void KeyDown(bool d)
        {
            if (down == d) return;
            down = d;
            Advance(d ? 0 : 3);
        }

        /// <summary>Silence now.</summary>
        public void Reset()
        {
            level = 0;
            ix = 4;
            down = false;
        }

        int TargetFor(int segment)
        {
            int actual = ((Dx7.ScaleOutLevel(levels[segment]) >> 1) << 6) + outLevel - 4256;
            return actual < 16 ? 16 : actual;
        }

        void Advance(int newIx)
        {
            ix = newIx;
            if (ix >= 4) return;
            int newLevel = levels[ix];
            target = TargetFor(ix);
            rising = target > level;
            int qrate = Math.Min(63, ((rates[ix] * 41) >> 6) + rateScaling);
            // msfa: (4 + (qrate & 3)) << (2 + 6 + (qrate >> 2)) in 2^-24 doublings per 64 samples at
            // 44.1 kHz; here in 1/256 doublings per sample at the voice's rate.
            inc = (4 + (qrate & 3)) * PortableMath.Pow(2, 8 + (qrate >> 2)) / 65536.0 / 64.0 * srScale;
            staticLeft = 0;
            if (Math.Abs(target - level) < 1e-9 || (ix == 0 && newLevel == 0))
            {
                int sr = Math.Min(99, rates[ix] + rateScaling);
                double n = Dx7.StaticSamples44(sr);
                if (sr < 77 && ix == 0 && newLevel == 0) n /= 20;
                staticLeft = Math.Max(1, n / srScale);
            }
        }

        /// <summary>Moves on by <paramref name="n"/> samples; returns the level.</summary>
        public double Next(int n)
        {
            if (ix < 3 || (ix < 4 && !down))
            {
                if (staticLeft > 0)
                {
                    staticLeft -= n;
                    if (staticLeft <= 0) Advance(ix + 1);
                }
                else if (rising)
                {
                    const double jumpTarget = 1716;
                    if (level < jumpTarget) level = jumpTarget;
                    level += Math.Max(1, Math.Floor((17 * 256 - level) / 256)) * inc * n;
                    if (level >= target)
                    {
                        level = target;
                        Advance(ix + 1);
                    }
                }
                else
                {
                    level -= inc * n;
                    if (level <= target)
                    {
                        level = target;
                        Advance(ix + 1);
                    }
                }
            }
            return level;
        }
    }

    /// <summary>The pitch envelope: four rates and levels (50 = no shift), starting at level 4.</summary>
    internal sealed class Dx7PitchEnvelope
    {
        readonly int[] rates = new int[4], levels = new int[4];
        int ix = 4;
        double value, target, step;   // octaves; step: octaves a second
        bool down, rising;

        public double Value => value;

        public void Start(int[] r, int[] l)
        {
            Array.Copy(r, rates, 4);
            Array.Copy(l, levels, 4);
            value = Dx7.PitchLevelOctaves(levels[3]);
            down = true;
            Advance(0);
        }

        public void Update(int[] r, int[] l)
        {
            Array.Copy(r, rates, 4);
            Array.Copy(l, levels, 4);
            if (ix < 4) Advance(down && ix == 3 ? 2 : ix);
        }

        public void KeyDown(bool d)
        {
            if (down == d) return;
            down = d;
            Advance(d ? 0 : 3);
        }

        void Advance(int newIx)
        {
            ix = newIx;
            if (ix >= 4) return;
            target = Dx7.PitchLevelOctaves(levels[ix]);
            rising = target > value;
            step = Dx7.PitchRateOctavesPerSecond(rates[ix]);
        }

        /// <summary>Moves on by <paramref name="seconds"/>; returns octaves.</summary>
        public double Next(double seconds)
        {
            if (ix < 3 || (ix < 4 && !down))
            {
                double d = step * seconds;
                if (rising)
                {
                    value += d;
                    if (value >= target) { value = target; Advance(ix + 1); }
                }
                else
                {
                    value -= d;
                    if (value <= target) { value = target; Advance(ix + 1); }
                }
            }
            return value;
        }
    }
}
