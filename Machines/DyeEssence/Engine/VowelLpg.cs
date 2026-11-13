#nullable disable
// Two filter types for the per-note filter stage (Neo Learn L2a, docs/specs/neo-learn.md §3 and §9; FilterSettings.Kind chooses them, Dye Essence's own
// controls never do, so nothing of Dye Essence changes):
//
//  * Vowel (FilterType.Vowel): four band-pass filters in parallel over a vowel table (the first four formants of a, e, i, o, u), their centres moved
//    between the vowels by Morph and all scaled by the tract size, their bandwidths by Q. Each is a state-variable filter (the same TPT form as
//    NeoBuzz.Dsp's SVF) taken at its band-pass, at unit gain at its centre, then weighted by the formant's level; the sum is levelled so a vowel's
//    energy does not depend on which one it is. The coefficients ramp across each control tick like Dye Essence's own filters, so a sweep has no zipper.
//  * Lpg (FilterType.Lpg): a low-pass gate. A "vactrol" (a lamp and a photocell, modelled as a level with a quick attack and a decay that slows as it
//    dims) follows the filter envelope plus a strike at note-on; the cutoff follows it, from 30 Hz to 18 kHz; two one-pole low-passes in series.
//    Mode 0 holds the gate open (a plain amplifier: the amp envelope alone shapes the note); Mode 1 lets the cutoff follow, so the treble leaves
//    before the level does. Strike adds a blow of light at note-on and a short thump through the filter. A DC blocker follows (a fold can leave an offset).
//
// Both run note by note (scalar), only for notes that sound, with the stage's layout (a column a note, a row a sample). NaN, infinities and the
// denormal are made zero in every state.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    /// <summary>The vowel tables and what the vowel filter and the pictures make of them.</summary>
    internal static class VowelTable
    {
        public const int Bands = 4, Vowels = 5;

        /// <summary>The first four formants (Hz) of a, e, i, o, u, their bandwidths (Hz) and levels (dB), as published for a sung bass.</summary>
        public static readonly float[,] Freq =
        {
            { 800, 1150, 2900, 3900 }, { 400, 1600, 2700, 3300 }, { 350, 1700, 2700, 3700 }, { 450, 800, 2830, 3500 }, { 325, 700, 2530, 3500 },
        };

        public static readonly float[,] Width =
        {
            { 80, 90, 120, 130 }, { 60, 80, 120, 150 }, { 50, 100, 120, 150 }, { 70, 80, 100, 130 }, { 50, 60, 170, 180 },
        };

        public static readonly float[,] Db =
        {
            { 0, -6, -32, -20 }, { 0, -24, -30, -35 }, { 0, -20, -30, -36 }, { 0, -11, -21, -40 }, { 0, -16, -35, -40 },
        };

        /// <summary>The tract's scale: the formants move by this many octaves at ±1 (a giant at -1, a child at +1).</summary>
        public const double TractOctaves = 0.7;

        /// <summary>Bandwidth scale at Q 0 (wide, a choir) and 1 (narrow, nasal): in between, straight in the logarithm.</summary>
        public const double WidthAtQ0 = 2.8, WidthAtQ1 = 0.22;

        /// <summary>
        /// The four bands for a morph (0 … 1: a, e, i, o, u), tract (−1 … 1) and Q (0 … 1): centre (Hz), bandwidth (Hz) and linear level, levelled so that
        /// white noise through the sum has the same power for every vowel. <paramref name="maxHz"/> keeps a band under the filter's limit.
        /// </summary>
        public static void At(double morph, double tract, double q, double maxHz, double[] hz, double[] bw, double[] amp)
        {
            double s = Math.Clamp(morph, 0, 1) * (Vowels - 1);
            int i = Math.Min((int)s, Vowels - 2);
            double f = s - i;
            double scale = PortableMath.Pow(2, TractOctaves * Math.Clamp(tract, -1, 1));
            double wq = PortableMath.Pow(WidthAtQ0, 1 - Math.Clamp(q, 0, 1)) * PortableMath.Pow(WidthAtQ1, Math.Clamp(q, 0, 1));
            double power = 0;
            for (int b = 0; b < Bands; b++)
            {
                double a = Freq[i, b], c = Freq[i + 1, b];
                hz[b] = Math.Min(a * PortableMath.Pow(c / a, f) * scale, maxHz);
                bw[b] = Math.Max(10.0, (Width[i, b] + f * (Width[i + 1, b] - Width[i, b])) * wq * Math.Sqrt(scale));
                double db = Db[i, b] + f * (Db[i + 1, b] - Db[i, b]);
                amp[b] = PortableMath.Pow(10, db / 20);
                power += amp[b] * amp[b] * bw[b];
            }
            // White noise through the four bands comes out at (pi/2) · sum(level² · width) / (fs/2) of its power: held at 1 at 44.1 kHz for every vowel.
            double norm = power > 1e-12 ? Math.Sqrt(14000.0 / power) : 1.0;
            for (int b = 0; b < Bands; b++) amp[b] *= norm;
        }

        /// <summary>The magnitude (linear) of the four bands' sum at <paramref name="at"/> Hz (the picture's response curve): each a band-pass of unit gain at its centre.</summary>
        public static double Response(double at, double[] hz, double[] bw, double[] amp)
        {
            double sum = 0;
            for (int b = 0; b < Bands; b++)
            {
                double qq = hz[b] / bw[b];
                double r = at / hz[b] - hz[b] / at;
                sum += amp[b] / Math.Sqrt(1 + qq * qq * r * r);
            }
            return sum;
        }
    }

    internal sealed partial class NoteBank
    {
        const int VowelRows = 5 * VowelTable.Bands;     // per band: a1, a2, a3, k, level
        readonly float[] vwCur = new float[VowelRows * Notes], vwTgt = new float[VowelRows * Notes], vwStep = new float[VowelRows * Notes];
        readonly float[] vwState = new float[16 * Notes];       // per note: [channel * 8 + band * 2 + (ic1, ic2)]
        readonly double[] vwHz = new double[VowelTable.Bands], vwBw = new double[VowelTable.Bands], vwAmp = new double[VowelTable.Bands];

        // The gate: per note, the optical level, the age (s), a pending thump, the coefficient (now, step), and the states.
        readonly float[] lgV = new float[Notes], lgAge = new float[Notes], lgClick = new float[Notes], lgP = new float[Notes], lgPStep = new float[Notes];
        readonly float[] lgState = new float[8 * Notes];        // per note: [channel * 4 + (low-pass 1, low-pass 2, dc in, dc out)]

        /// <summary>This tick's vowel coefficients for a note (targets; they ramp across the tick).</summary>
        void VowelControl(int note, FilterSettings fs, float fe, float invCount, double vr)
        {
            if (snap[note]) Array.Clear(vwState, 16 * note, 16);
            double morph = Math.Clamp(fs.Morph + fs.EnvMod * fe, 0f, 1f);
            VowelTable.At(morph, fs.Tract, fs.Q, 0.45 * vr, vwHz, vwBw, vwAmp);
            for (int b = 0; b < VowelTable.Bands; b++)
            {
                float g = Svf.Prewarp((float)vwHz[b], (float)vr);
                float k = (float)Math.Clamp(vwBw[b] / vwHz[b], 0.02, 2.0);
                float a1 = 1f / (1f + g * (g + k));
                int r = 5 * b;
                vwTgt[(r + 0) * Notes + note] = a1;
                vwTgt[(r + 1) * Notes + note] = g * a1;
                vwTgt[(r + 2) * Notes + note] = g * g * a1;
                vwTgt[(r + 3) * Notes + note] = k;
                vwTgt[(r + 4) * Notes + note] = (float)vwAmp[b];
            }
            for (int r = 0; r < VowelRows; r++)
            {
                int i = r * Notes + note;
                if (snap[note]) { vwCur[i] = vwTgt[i]; vwStep[i] = 0f; }
                else vwStep[i] = (vwTgt[i] - vwCur[i]) * invCount;
            }
        }

        /// <summary>The vowel filter on a group (eight notes), both channels, from a + mix · b into <paramref name="outL"/> and <paramref name="outR"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void RunVowel(float[] aL, float[] aR, float[] bL, float[] bR, float mix, float[] outL, float[] outR, int g, int count, float drive)
        {
            bool hasB = bL != null, driven = drive > 1.0001f;
            float invDrive = 1f / drive;
            for (int note = 8 * g; note < 8 * g + 8; note++)
            {
                if (!live[note])
                {
                    for (int t = 0; t < count; t++) { outL[t * Notes + note] = 0f; outR[t * Notes + note] = 0f; }
                    continue;
                }
                var cur = vwCur;
                float a1_0 = cur[0 * Notes + note], a2_0 = cur[1 * Notes + note], a3_0 = cur[2 * Notes + note], k0 = cur[3 * Notes + note], m0 = cur[4 * Notes + note];
                float a1_1 = cur[5 * Notes + note], a2_1 = cur[6 * Notes + note], a3_1 = cur[7 * Notes + note], k1 = cur[8 * Notes + note], m1 = cur[9 * Notes + note];
                float a1_2 = cur[10 * Notes + note], a2_2 = cur[11 * Notes + note], a3_2 = cur[12 * Notes + note], k2 = cur[13 * Notes + note], m2 = cur[14 * Notes + note];
                float a1_3 = cur[15 * Notes + note], a2_3 = cur[16 * Notes + note], a3_3 = cur[17 * Notes + note], k3 = cur[18 * Notes + note], m3 = cur[19 * Notes + note];
                float d1_0 = vwStep[0 * Notes + note], d2_0 = vwStep[1 * Notes + note], d3_0 = vwStep[2 * Notes + note], dk0 = vwStep[3 * Notes + note], dm0 = vwStep[4 * Notes + note];
                float d1_1 = vwStep[5 * Notes + note], d2_1 = vwStep[6 * Notes + note], d3_1 = vwStep[7 * Notes + note], dk1 = vwStep[8 * Notes + note], dm1 = vwStep[9 * Notes + note];
                float d1_2 = vwStep[10 * Notes + note], d2_2 = vwStep[11 * Notes + note], d3_2 = vwStep[12 * Notes + note], dk2 = vwStep[13 * Notes + note], dm2 = vwStep[14 * Notes + note];
                float d1_3 = vwStep[15 * Notes + note], d2_3 = vwStep[16 * Notes + note], d3_3 = vwStep[17 * Notes + note], dk3 = vwStep[18 * Notes + note], dm3 = vwStep[19 * Notes + note];
                int sb = 16 * note;
                float l1a = vwState[sb], l2a = vwState[sb + 1], l1b = vwState[sb + 2], l2b = vwState[sb + 3], l1c = vwState[sb + 4], l2c = vwState[sb + 5], l1d = vwState[sb + 6], l2d = vwState[sb + 7];
                float r1a = vwState[sb + 8], r2a = vwState[sb + 9], r1b = vwState[sb + 10], r2b = vwState[sb + 11], r1c = vwState[sb + 12], r2c = vwState[sb + 13], r1d = vwState[sb + 14], r2d = vwState[sb + 15];
                for (int t = 0; t < count; t++)
                {
                    int o = t * Notes + note;
                    float xl = aL[o], xr = aR[o];
                    if (hasB) { xl += mix * bL[o]; xr += mix * bR[o]; }
                    if (driven)
                    {
                        // the glottis driven hard: a soft clip before the tract
                        float u = Math.Clamp(xl * drive, -3f, 3f);
                        xl = u * (27f + u * u) / (27f + 9f * u * u) * invDrive;
                        u = Math.Clamp(xr * drive, -3f, 3f);
                        xr = u * (27f + u * u) / (27f + 9f * u * u) * invDrive;
                    }
                    float yl = 0f, yr = 0f;
                    // band 0
                    float v3 = xl - l2a, v1 = a1_0 * l1a + a2_0 * v3, v2 = l2a + a2_0 * l1a + a3_0 * v3;
                    l1a = 2f * v1 - l1a; l2a = 2f * v2 - l2a; yl += m0 * k0 * v1;
                    v3 = xr - r2a; v1 = a1_0 * r1a + a2_0 * v3; v2 = r2a + a2_0 * r1a + a3_0 * v3;
                    r1a = 2f * v1 - r1a; r2a = 2f * v2 - r2a; yr += m0 * k0 * v1;
                    // band 1
                    v3 = xl - l2b; v1 = a1_1 * l1b + a2_1 * v3; v2 = l2b + a2_1 * l1b + a3_1 * v3;
                    l1b = 2f * v1 - l1b; l2b = 2f * v2 - l2b; yl += m1 * k1 * v1;
                    v3 = xr - r2b; v1 = a1_1 * r1b + a2_1 * v3; v2 = r2b + a2_1 * r1b + a3_1 * v3;
                    r1b = 2f * v1 - r1b; r2b = 2f * v2 - r2b; yr += m1 * k1 * v1;
                    // band 2
                    v3 = xl - l2c; v1 = a1_2 * l1c + a2_2 * v3; v2 = l2c + a2_2 * l1c + a3_2 * v3;
                    l1c = 2f * v1 - l1c; l2c = 2f * v2 - l2c; yl += m2 * k2 * v1;
                    v3 = xr - r2c; v1 = a1_2 * r1c + a2_2 * v3; v2 = r2c + a2_2 * r1c + a3_2 * v3;
                    r1c = 2f * v1 - r1c; r2c = 2f * v2 - r2c; yr += m2 * k2 * v1;
                    // band 3
                    v3 = xl - l2d; v1 = a1_3 * l1d + a2_3 * v3; v2 = l2d + a2_3 * l1d + a3_3 * v3;
                    l1d = 2f * v1 - l1d; l2d = 2f * v2 - l2d; yl += m3 * k3 * v1;
                    v3 = xr - r2d; v1 = a1_3 * r1d + a2_3 * v3; v2 = r2d + a2_3 * r1d + a3_3 * v3;
                    r1d = 2f * v1 - r1d; r2d = 2f * v2 - r2d; yr += m3 * k3 * v1;
                    outL[o] = yl;
                    outR[o] = yr;
                    a1_0 += d1_0; a2_0 += d2_0; a3_0 += d3_0; k0 += dk0; m0 += dm0;
                    a1_1 += d1_1; a2_1 += d2_1; a3_1 += d3_1; k1 += dk1; m1 += dm1;
                    a1_2 += d1_2; a2_2 += d2_2; a3_2 += d3_2; k2 += dk2; m2 += dm2;
                    a1_3 += d1_3; a2_3 += d2_3; a3_3 += d3_3; k3 += dk3; m3 += dm3;
                }
                for (int r = 0; r < VowelRows; r++)
                {
                    int i = r * Notes + note;
                    vwCur[i] = vwTgt[i];     // the ramp ended at its target (a tick is one ramp)
                }
                vwState[sb] = Tame(l1a); vwState[sb + 1] = Tame(l2a); vwState[sb + 2] = Tame(l1b); vwState[sb + 3] = Tame(l2b);
                vwState[sb + 4] = Tame(l1c); vwState[sb + 5] = Tame(l2c); vwState[sb + 6] = Tame(l1d); vwState[sb + 7] = Tame(l2d);
                vwState[sb + 8] = Tame(r1a); vwState[sb + 9] = Tame(r2a); vwState[sb + 10] = Tame(r1b); vwState[sb + 11] = Tame(r2b);
                vwState[sb + 12] = Tame(r1c); vwState[sb + 13] = Tame(r2c); vwState[sb + 14] = Tame(r1d); vwState[sb + 15] = Tame(r2d);
            }
        }

        /// <summary>A filter state kept finite and out of the denormals (a NaN fails the test and becomes 0).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Tame(float x) => MathF.Abs(x) < 1e6f && MathF.Abs(x) > 1e-18f ? x : 0f;

        // ------------------------------------------------------------------ the low-pass gate

        /// <summary>Highest and lowest cutoff of the gate (Hz).</summary>
        public const double LpgOpenHz = 18000, LpgClosedHz = 30;

        /// <summary>The optical level's attack and decay times (s): a quick rise; a fall that slows as it dims (up to this much longer).</summary>
        public const double LpgAttack = 0.002, LpgDecay = 0.04, LpgDimExtra = 0.16;

        /// <summary>How long the strike's blow of light lasts (s), and the size of the thump it puts through the filter.</summary>
        public const double LpgPing = 0.012, LpgThump = 0.5;

        void LpgControl(int note, FilterSettings fs, float fe, float invCount, double vr)
        {
            if (snap[note])
            {
                Array.Clear(lgState, 8 * note, 8);
                lgV[note] = 0f;
                lgAge[note] = 0f;
                lgClick[note] = Math.Clamp(fs.Strike, 0f, 1f) * (float)LpgThump;
                lgP[note] = 0f;
            }
            double dt = 1.0 / (invCount * vr);
            lgAge[note] += (float)dt;
            float strike = Math.Clamp(fs.Strike, 0f, 1f);
            float ping = strike * (float)PortableMath.Exp(-lgAge[note] / LpgPing);
            float c = Math.Clamp(fe + ping, 0f, 1f);
            float v = lgV[note];
            double tau = c > v ? LpgAttack : LpgDecay + LpgDimExtra * (1.0 - v);
            v += (c - v) * (float)(1.0 - PortableMath.Exp(-dt / tau));
            if (!(v > 1e-6f)) v = 0f;
            lgV[note] = v;
            float mode = Math.Clamp(fs.Mode, 0f, 1f);
            double u = (1.0 - mode) + mode * v;                           // 1: open
            double hz = Math.Min(LpgClosedHz * PortableMath.Pow(LpgOpenHz / LpgClosedHz, u), 0.45 * vr);
            float p = (float)(1.0 - PortableMath.Exp(-2.0 * Math.PI * hz / vr));
            if (snap[note]) lgP[note] = p;
            lgPStep[note] = (p - lgP[note]) * invCount;
        }

        /// <summary>The low-pass gate on a group, both channels, from a + mix · b.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void RunLpg(float[] aL, float[] aR, float[] bL, float[] bR, float mix, float[] outL, float[] outR, int g, int count)
        {
            bool hasB = bL != null;
            for (int note = 8 * g; note < 8 * g + 8; note++)
            {
                if (!live[note])
                {
                    for (int t = 0; t < count; t++) { outL[t * Notes + note] = 0f; outR[t * Notes + note] = 0f; }
                    continue;
                }
                float p = lgP[note], dp = lgPStep[note];
                int sb = 8 * note;
                float la1 = lgState[sb], la2 = lgState[sb + 1], ldi = lgState[sb + 2], ldo = lgState[sb + 3];
                float ra1 = lgState[sb + 4], ra2 = lgState[sb + 5], rdi = lgState[sb + 6], rdo = lgState[sb + 7];
                float thump = lgClick[note];
                for (int t = 0; t < count; t++)
                {
                    int o = t * Notes + note;
                    float xl = aL[o], xr = aR[o];
                    if (hasB) { xl += mix * bL[o]; xr += mix * bR[o]; }
                    if (thump != 0f) { xl += thump; xr += thump; thump = 0f; }
                    la1 += p * (xl - la1);
                    la2 += p * (la1 - la2);
                    ra1 += p * (xr - ra1);
                    ra2 += p * (ra1 - ra2);
                    // a DC blocker (about 3 Hz at 44.1 kHz)
                    float yl = la2 - ldi + 0.9995f * ldo;
                    ldi = la2; ldo = yl;
                    float yr = ra2 - rdi + 0.9995f * rdo;
                    rdi = ra2; rdo = yr;
                    if (!(MathF.Abs(yl) < 1e6f)) { yl = 0f; ldo = 0f; la1 = la2 = 0f; }
                    if (!(MathF.Abs(yr) < 1e6f)) { yr = 0f; rdo = 0f; ra1 = ra2 = 0f; }
                    outL[o] = yl;
                    outR[o] = yr;
                    p += dp;
                }
                lgClick[note] = 0f;
                lgP[note] = p;
                lgState[sb] = Tame(la1); lgState[sb + 1] = Tame(la2); lgState[sb + 2] = Tame(ldi); lgState[sb + 3] = Tame(ldo);
                lgState[sb + 4] = Tame(ra1); lgState[sb + 5] = Tame(ra2); lgState[sb + 6] = Tame(rdi); lgState[sb + 7] = Tame(rdo);
            }
        }
    }
}
