#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.7): the Harmonic engine, additive synthesis. A slot is a
// bank of up to 128 sine partials at multiples of the note (stretched apart or bunched together by
// "stretch"), their levels taken from a set (made by code, or the partials of the slot's loaded
// wavetable) or a morph between two sets, then shaped by tilt, odd/even and the partial count; the
// upper partials may die away faster while the note is held (damp). Band-limiting is exact: a
// partial past the output's band is not rendered (it fades out across the top of the band), so there is no alias floor to measure.
//
// Like the Sample and Granular engines it renders beside the lanes (RenderExt), scalar-shaped and
// deterministic: each partial is a rotating phasor (cos, sin) advanced by a rotation of its own,
// four partials at a time in 128-bit vectors (element-wise multiplies and adds, no fused multiply-add;
// the lanes of a sample's sum are added in a fixed order), so every platform gives the same bits.
// The rotations come from a polynomial sine and cosine (not the platform's), the phasors are
// renormalised each tick, and everything is preallocated.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    /// <summary>The Harmonic engine's partial sets (append only; User is the last).</summary>
    internal enum HarmonicSet { Saw, Square, Triangle, Organ, Reed, Brass, Strings, VowelA, VowelO, Bright, Comb, Random, Octaves, User }

    /// <summary>The Harmonic engine: partials, rendered beside the lanes (nothing for the lanes to read).</summary>
    internal sealed class HarmonicEngine : IEngine
    {
        public TableArena TableOf(OscSettings os) => null;
        public float PositionOf(OscSettings os) => os.Position;
        public float DriftCents(OscSettings os, float r) => 0f;
    }

    /// <summary>The built-in partial sets: signed levels of partials 1 … 128, scaled to a sum of squares of 1. Made by code, with portable maths.</summary>
    internal static class HarmonicSets
    {
        public const int Partials = 128;
        public static readonly int Count = Enum.GetValues(typeof(HarmonicSet)).Length;
        public static readonly string[] Names = { "Saw", "Square", "Triangle", "Organ", "Reed", "Brass", "Strings", "Vowel A", "Vowel O", "Bright", "Comb", "Random", "Octaves", "User" };
        static readonly float[][] sets = Build();

        /// <summary>A built-in set (the User index and anything past it give the first set).</summary>
        public static float[] Get(int index) => sets[index >= 0 && index < sets.Length ? index : 0];

        static float[][] Build()
        {
            var all = new float[(int)HarmonicSet.User][];
            for (int s = 0; s < all.Length; s++)
            {
                var a = new double[Partials];
                var rng = new Rng(0x2545F491u + (uint)s);
                for (int j = 0; j < Partials; j++)
                {
                    double k = j + 1;
                    bool odd = (j & 1) == 0;
                    double v = 0;
                    switch ((HarmonicSet)s)
                    {
                        case HarmonicSet.Saw: v = 1 / k; break;
                        case HarmonicSet.Square: v = odd ? 1 / k : 0; break;
                        case HarmonicSet.Triangle: v = odd ? (((j / 2) & 1) == 0 ? 1 : -1) / (k * k) : 0; break;
                        case HarmonicSet.Organ:
                            // Drawbars at 1, 2, 3, 4, 6 and 8 times the pitch.
                            v = j == 0 ? 1.0 : j == 1 ? 0.8 : j == 2 ? 0.6 : j == 3 ? 0.5 : j == 5 ? 0.3 : j == 7 ? 0.25 : 0;
                            break;
                        case HarmonicSet.Reed: v = odd ? PortableMath.Pow(k, -0.6) : 0.12 * PortableMath.Pow(k, -1.2); break;
                        case HarmonicSet.Brass: v = k * PortableMath.Exp(-k / 5) + 0.02 / k; break;
                        case HarmonicSet.Strings: v = PortableMath.Pow(k, -0.9) * (1 + 0.3 * PortableMath.Sin(k * 0.9)); break;
                        case HarmonicSet.VowelA:
                            v = Formant(k, 6.6, 1.6, 1.0) + Formant(k, 9.9, 2.2, 0.6) + Formant(k, 22, 3.5, 0.25) + 0.2 / k;
                            break;
                        case HarmonicSet.VowelO:
                            v = Formant(k, 4.1, 1.4, 1.0) + Formant(k, 7.3, 2.0, 0.4) + 0.15 / k;
                            break;
                        case HarmonicSet.Bright: v = PortableMath.Pow(k, -0.5); break;
                        case HarmonicSet.Comb: v = (j % 3) == 0 ? 1 / k : 0; break;
                        case HarmonicSet.Random: v = (0.1 + rng.NextUnipolar()) * (0.1 + rng.NextUnipolar()) * PortableMath.Pow(k, -0.6); break;
                        case HarmonicSet.Octaves:
                            // Partials 1, 2, 4, 8 … each a little softer than the one below.
                            v = (j & (j + 1)) == 0 ? PortableMath.Pow(0.8, Math.Round(PortableMath.Log(k) / PortableMath.Log(2))) : 0;
                            break;
                    }
                    a[j] = v;
                }
                all[s] = Normalise(a);
            }
            return all;
        }

        /// <summary>The level partial <paramref name="k"/> (1-based) gets from the partial count, tilt (dB an octave) and odd/even (−1 … 1), before the set's own.</summary>
        public static float Base(int k, int count, float tilt, float oddEven)
        {
            int w = Math.Min(4, count);
            // The cut: the top few partials fade out, the rest are whole.
            float cut = Math.Clamp((count - k + 1) / (float)w, 0f, 1f);
            float parity = (k & 1) == 1 ? 1f - Math.Max(0f, -oddEven) : 1f - Math.Max(0f, oddEven);
            float slope = tilt == 0f ? 1f : (float)PortableMath.Pow(k, tilt / 6.0206);
            return cut * parity * slope;
        }

        /// <summary>Partial <paramref name="k"/>'s pitch as a multiple of the note's, for a stretch of −1 … 1.</summary>
        public static float Ratio(int k, float stretch) => stretch == 0f ? k : (float)PortableMath.Pow(k, 1.0 + 0.5 * stretch);

        static double Formant(double k, double centre, double width, double gain)
        {
            double x = (k - centre) / width;
            return gain * PortableMath.Exp(-0.5 * x * x);
        }

        static float[] Normalise(double[] a)
        {
            double e = 0;
            for (int j = 0; j < a.Length; j++) e += a[j] * a[j];
            var r = new float[a.Length];
            if (e < 1e-18)
            {
                r[0] = 1f;
                return r;
            }
            double g = 1.0 / Math.Sqrt(e);
            for (int j = 0; j < a.Length; j++) r[j] = (float)(a[j] * g);
            return r;
        }

        /// <summary>The partials of one frame of a wavetable (its full-band level, by a direct Fourier sum), as a set. Off the audio thread.</summary>
        public static float[] FromTable(TableArena table, int frame)
        {
            if (table == null) return Get(0);
            int n = NeoBuzz.Dsp.Wavetable.LevelLength(0);
            frame = Math.Clamp(frame, 0, table.FrameCount - 1);
            int at = TableArena.Offset(frame, 0) + 1;
            var sin = new double[n];
            for (int i = 0; i < n; i++) sin[i] = PortableMath.Sin(2 * Math.PI * i / n);
            int mask = n - 1, quarter = n / 4;
            var a = new double[Partials];
            for (int h = 1; h <= Partials; h++)
            {
                double re = 0, im = 0;
                for (int i = 0; i < n; i++)
                {
                    int ph = (int)(((long)h * i) & mask);
                    double x = table.Data[at + i];
                    im += x * sin[ph];
                    re += x * sin[(ph + quarter) & mask];
                }
                a[h - 1] = Math.Sqrt(re * re + im * im);
            }
            return Normalise(a);
        }
    }

    internal sealed partial class NoteBank
    {
        /// <summary>Partials of a voice at most (a multiple of four).</summary>
        public const int MaxPartials = HarmonicSets.Partials;

        /// <summary>
        /// A slot's partials times its unison voices, at most, a note: more voices share that many (each voice at
        /// least <see cref="HarmFloor"/> partials), so a wide unison costs what a thin one does.
        /// </summary>
        public const int HarmBudget = 256, HarmFloor = 8;

        // Per voice (note · MaxLanes + slot · MaxUnison + voice) and partial: the phasor, the level and its step, the rotation.
        readonly float[] hC = new float[Notes * MaxLanes * MaxPartials], hS = new float[Notes * MaxLanes * MaxPartials],
            hA = new float[Notes * MaxLanes * MaxPartials], hDA = new float[Notes * MaxLanes * MaxPartials],
            hRC = new float[Notes * MaxLanes * MaxPartials], hRS = new float[Notes * MaxLanes * MaxPartials];
        readonly int[] hBlocks = new int[Notes * MaxLanes], hActive = new int[Notes * MaxLanes];
        readonly double[] hInc = new double[Notes * MaxLanes];
        readonly int[] hRotBlocks = new int[Notes * MaxLanes];
        // Per slot: the shape its settings give the partials (levels, spacing, decay per tick), and the key it was made for.
        readonly float[] hBase = new float[Oscillators * MaxPartials], hRatio = new float[Oscillators * MaxPartials], hStep = new float[Oscillators * MaxPartials];
        readonly float[] hKey = new float[Oscillators * 6];
        readonly double[] hKeyDt = new double[Oscillators];
        // Per note and slot: how far each partial has decayed, and this tick's levels before the voices' own limits.
        readonly float[] hDecay = new float[Notes * Oscillators * MaxPartials];
        readonly float[] hGain = new float[MaxPartials];
        readonly bool[] hDamp = new bool[Notes * Oscillators];
        readonly int[] hPer = new int[Notes * Oscillators];
        readonly Vector128<float>[] hAcc = new Vector128<float>[MaxTick];

        /// <summary>Sine and cosine of a phase in turns (any value), from polynomials of the angle within its quadrant: the same bits everywhere.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SinCosTurns(float turns, out float sin, out float cos)
        {
            float t = turns - MathF.Floor(turns);
            float x = t * 4f;
            int q = (int)x;
            float th = (x - q) * 1.57079637f, th2 = th * th;
            float s = th * (1f + th2 * (-1f / 6f + th2 * (1f / 120f + th2 * (-1f / 5040f + th2 * (1f / 362880f + th2 * (-1f / 39916800f))))));
            float c = 1f + th2 * (-0.5f + th2 * (1f / 24f + th2 * (-1f / 720f + th2 * (1f / 40320f + th2 * (-1f / 3628800f + th2 * (1f / 479001600f))))));
            switch (q & 3)
            {
                case 0: sin = s; cos = c; break;
                case 1: sin = c; cos = -s; break;
                case 2: sin = -s; cos = -c; break;
                default: sin = -c; cos = s; break;
            }
        }

        /// <summary>A slot's partial levels, spacing and decay for its settings (once, then when they change).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void HarmShape(int o, OscSettings os)
        {
            int n = Math.Clamp(os.Partials, 1, MaxPartials);
            float tilt = Math.Clamp(os.Tilt, -24f, 24f), oddEven = Math.Clamp(os.OddEven, -1f, 1f), stretch = Math.Clamp(os.Stretch, -1f, 1f), damp = Math.Clamp(os.Damp, 0f, 1f);
            int kb = o * 6;
            if (hKey[kb] == n && hKey[kb + 1] == tilt && hKey[kb + 2] == oddEven && hKey[kb + 3] == stretch && hKey[kb + 4] == damp && hKeyDt[o] == tickDt) return;
            hKey[kb] = n; hKey[kb + 1] = tilt; hKey[kb + 2] = oddEven; hKey[kb + 3] = stretch; hKey[kb + 4] = damp;
            hKeyDt[o] = tickDt;
            for (int j = 0; j < MaxPartials; j++)
            {
                int k = j + 1, at = o * MaxPartials + j;
                hBase[at] = HarmonicSets.Base(k, n, tilt, oddEven);
                hRatio[at] = HarmonicSets.Ratio(k, stretch);
                // Upper partials die away: partial k's level falls by e^(−damp · (k − 1) / 2) a second.
                hStep[at] = damp == 0f ? 1f : (float)PortableMath.Exp(-damp * 0.5 * j * tickDt);
            }
        }

        void HarmInit(int note, int o, int no)
        {
            int pb = no * MaxPartials;
            for (int j = 0; j < MaxPartials; j++) hDecay[pb + j] = 1f;
            hPer[no] = MaxPartials;
        }

        /// <summary>This tick's partials of a note's Harmonic slot: levels, rotations and the voices' gains.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void HarmControl(int note, int o, int no, OscSettings os, int u, double inc0, float invCount)
        {
            HarmShape(o, os);
            int sb = o * MaxPartials;
            int n = Math.Clamp(os.Partials, 1, MaxPartials);
            int per = Math.Min(n, Math.Max(HarmFloor, HarmBudget / u));
            per = (per + 3) & ~3;
            hPer[no] = per;
            bool damp = os.Damp > 0f;
            int db = no * MaxPartials;
            if (damp) for (int j = 0; j < per; j++) hDecay[db + j] *= hStep[sb + j];
            hDamp[no] = damp;
            float m = Math.Clamp(OscPosition(o) + Md(note, RouteDest.APosition + o), 0f, 1f);
            var sa = os.SetA ?? HarmonicSets.Get(0);
            var sb2 = os.SetB ?? HarmonicSets.Get(1);
            // The levels before the voices' own limits, scaled so a set's loudness is the same whatever its shape.
            float e = 0;
            for (int j = 0; j < per; j++)
            {
                float g = (sa[j] + (sb2[j] - sa[j]) * m) * hBase[sb + j];
                hGain[j] = g;
                e += g * g;
            }
            for (int j = per; j < MaxPartials; j++) hGain[j] = 0f;
            float norm = e > 1e-12f ? 0.7f / MathF.Sqrt(e) : 0f;
            float vnorm = 1f / MathF.Sqrt(u);
            int vb = note * MaxLanes + o * MaxUnison;
            int count = Math.Max(u, extPrevU[no]);
            for (int l = 0; l < count; l++)
            {
                int k = vb + l;
                bool live = l < u;
                int pb = k * MaxPartials;
                if (live)
                {
                    bool fresh = l >= extPrevU[no];
                    if (fresh) HarmStart(note, o, l, k, os);
                    double inc = inc0 * ratio[o * MaxUnison + l];
                    // Partials below the output's band (0.46 of the sample rate, the top 0.06 fading out): none is made that the
                    // decimator would have to remove, so the engine's output has nothing to fold back.
                    float hi = 0.46f / oversample, fadeWidth = 0.06f / oversample;
                    int active = 0;
                    while (active < per && inc * hRatio[sb + active] < hi) active++;
                    int blocks = (Math.Max(active, hActive[k]) + 3) >> 2;
                    hActive[k] = active;
                    hBlocks[k] = blocks;
                    int lim = blocks * 4;
                    if (inc != hInc[k] || hRotBlocks[k] < blocks)
                    {
                        hInc[k] = inc;
                        hRotBlocks[k] = blocks;
                        for (int j = 0; j < lim; j++)
                        {
                            SinCosTurns((float)(inc * hRatio[sb + j]), out float rs, out float rc);
                            hRS[pb + j] = rs;
                            hRC[pb + j] = rc;
                        }
                    }
                    for (int j = 0; j < lim; j++)
                    {
                        float t = (float)(inc * hRatio[sb + j]);
                        float nyq = Math.Clamp((hi - t) / fadeWidth, 0f, 1f);
                        float target = j < active ? hGain[j] * norm * nyq * (damp ? hDecay[db + j] : 1f) : 0f;
                        if (fresh) { hA[pb + j] = target; hDA[pb + j] = 0f; }
                        else hDA[pb + j] = (target - hA[pb + j]) * invCount;
                        // Keep the phasor on the unit circle (one Newton step each tick).
                        float c = hC[pb + j], s = hS[pb + j];
                        float f = 1.5f - 0.5f * (c * c + s * s);
                        hC[pb + j] = c * f;
                        hS[pb + j] = s * f;
                    }
                }
                float tl = live ? vnorm * panL[o * MaxUnison + l] : 0f, tr = live ? vnorm * panR[o * MaxUnison + l] : 0f;
                svDL[k] = (tl - svGL[k]) * invCount;
                svDR[k] = (tr - svGR[k]) * invCount;
                if (!live && svGL[k] == 0f && svGR[k] == 0f) svDL[k] = svDR[k] = 0f;
                svOn[k] = live || svGL[k] != 0f || svGR[k] != 0f;
            }
            extVoices[no] = count;
            extPrevU[no] = u;
        }

        /// <summary>A voice starts: every partial from its place in the cycle (the slot's start phase plus the voice's random share).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void HarmStart(int note, int o, int l, int k, OscSettings os)
        {
            var rng = new Rng(unchecked(seeds[note] * 2654435761u + (uint)(o * MaxUnison + l + 1) * 0x85EBCA6Bu) | 1u);
            double p0 = os.Phase + os.Random * rng.NextUnipolar();
            int pb = k * MaxPartials;
            for (int j = 0; j < MaxPartials; j++)
            {
                double ph = (j + 1) * p0;
                ph -= Math.Floor(ph);
                SinCosTurns((float)ph, out float s, out float c);
                hS[pb + j] = s;
                hC[pb + j] = c;
                hA[pb + j] = hDA[pb + j] = 0f;
            }
            hInc[k] = double.NaN;
            hRotBlocks[k] = 0;
            hActive[k] = 0;
            hBlocks[k] = 0;
            svGL[k] = svGR[k] = svDL[k] = svDR[k] = 0f;
        }

        /// <summary>A note's Harmonic slot over <paramref name="count"/> samples into the engine buffers (voice by voice, four partials at a time).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void HarmVoices(int note, int o, int no, int count)
        {
            int vb = note * MaxLanes + o * MaxUnison;
            var acc = hAcc;
            for (int v = 0; v < extVoices[no]; v++)
            {
                int k = vb + v;
                if (!svOn[k]) continue;
                int blocks = hBlocks[k], pb = k * MaxPartials;
                Array.Clear(acc, 0, count);
                for (int b = 0; b < blocks; b++)
                {
                    int at = pb + 4 * b;
                    var c = Vector128.LoadUnsafe(ref hC[at]);
                    var s = Vector128.LoadUnsafe(ref hS[at]);
                    var a = Vector128.LoadUnsafe(ref hA[at]);
                    var da = Vector128.LoadUnsafe(ref hDA[at]);
                    var rc = Vector128.LoadUnsafe(ref hRC[at]);
                    var rs = Vector128.LoadUnsafe(ref hRS[at]);
                    for (int i = 0; i < count; i++)
                    {
                        acc[i] += a * s;
                        var nc = c * rc - s * rs;
                        s = s * rc + c * rs;
                        c = nc;
                        a += da;
                    }
                    c.StoreUnsafe(ref hC[at]);
                    s.StoreUnsafe(ref hS[at]);
                    a.StoreUnsafe(ref hA[at]);
                }
                float gl = svGL[k], gr = svGR[k], sl = svDL[k], sr = svDR[k];
                for (int i = 0; i < count; i++)
                {
                    var q = acc[i];
                    float y = ((q.GetElement(0) + q.GetElement(1)) + q.GetElement(2)) + q.GetElement(3);
                    gl += sl;
                    gr += sr;
                    exL[i] += gl * y;
                    exR[i] += gr * y;
                }
                svGL[k] = gl;
                svGR[k] = gr;
            }
        }
    }
}
