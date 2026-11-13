#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.6): the Sample and Granular engines. They read a
// sample (Neo Sampler's SampleLayer: octave-down copies, a windowed-sinc read whose cutoff follows
// the step) instead of a wavetable, so they don't go through the unison lanes: each note renders
// them sample by sample (scalar, so the same bits whatever the lane implementation), into the
// filters' inputs beside the lanes' sum. A Sample slot's unison voices are voices of the sample at
// the slot's detunes and pans; a Granular slot's unison voices are the pitches and places its grains
// take in turn. Everything is preallocated; the audio thread allocates nothing.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Dsp;
using NeoBuzz.Sampler;

namespace NeoBuzz.DyeEssence
{
    /// <summary>How a Sample slot loops while the note is held (append only).</summary>
    internal enum SampleLoop { Off, Forward, PingPong }

    /// <summary>The window a grain fades in and out with (append only).</summary>
    internal enum GrainShape { Hann, Triangle, Tukey, Gauss, Percussive }

    /// <summary>The Sample engine: plays the slot's sample (start, loop and direction in the settings).</summary>
    internal sealed class SampleEngine : IEngine
    {
        public TableArena TableOf(OscSettings os) => null;
        public float PositionOf(OscSettings os) => os.Position;
        public float DriftCents(OscSettings os, float r) => 0f;
    }

    /// <summary>The Granular engine: clouds of short windowed pieces of the slot's sample.</summary>
    internal sealed class GranularEngine : IEngine
    {
        public TableArena TableOf(OscSettings os) => null;
        public float PositionOf(OscSettings os) => os.Position;
        public float DriftCents(OscSettings os, float r) => 0f;
    }

    /// <summary>Grain windows, 1024 steps and one more for the straight line to the end (built once, with portable maths).</summary>
    internal static class GrainWindows
    {
        public const int Size = 1024;
        public static readonly float[][] Table = Build();

        static float[][] Build()
        {
            var all = new float[5][];
            for (int s = 0; s < all.Length; s++)
            {
                var w = new float[Size + 1];
                for (int i = 0; i <= Size; i++)
                {
                    double x = i / (double)Size, v;
                    switch ((GrainShape)s)
                    {
                        case GrainShape.Hann: v = 0.5 - 0.5 * PortableMath.Cos(2 * Math.PI * x); break;
                        case GrainShape.Triangle: v = 1 - Math.Abs(2 * x - 1); break;
                        case GrainShape.Tukey:
                            v = x < 0.25 ? 0.5 - 0.5 * PortableMath.Cos(4 * Math.PI * x)
                              : x > 0.75 ? 0.5 - 0.5 * PortableMath.Cos(4 * Math.PI * (1 - x)) : 1;
                            break;
                        case GrainShape.Gauss:
                        {
                            double g = PortableMath.Exp(-0.5 * ((x - 0.5) / 0.17) * ((x - 0.5) / 0.17));
                            double g0 = PortableMath.Exp(-0.5 * (0.5 / 0.17) * (0.5 / 0.17));
                            v = (g - g0) / (1 - g0);
                            break;
                        }
                        default:
                        {
                            double attack = x < 0.02 ? 0.5 - 0.5 * PortableMath.Cos(Math.PI * x / 0.02) : 1;
                            double e6 = PortableMath.Exp(-6.0);
                            v = attack * (PortableMath.Exp(-6.0 * Math.Max(0, x - 0.02) / 0.98) - e6) / (1 - e6);
                            break;
                        }
                    }
                    w[i] = (float)v;
                }
                w[0] = w[Size] = 0f;
                all[s] = w;
            }
            return all;
        }

        /// <summary>Neo Learn's trapezoid (the morph's sharp end): a straight ramp over the first and last <see cref="TrapezoidEdge"/> of the grain, flat between.</summary>
        public const double TrapezoidEdge = 0.08;
        public static readonly float[] Trapezoid = BuildTrapezoid();

        static float[] BuildTrapezoid()
        {
            var w = new float[Size + 1];
            for (int i = 0; i <= Size; i++)
            {
                double x = i / (double)Size;
                w[i] = (float)(x < TrapezoidEdge ? x / TrapezoidEdge : x > 1 - TrapezoidEdge ? (1 - x) / TrapezoidEdge : 1.0);
            }
            w[0] = w[Size] = 0f;
            return w;
        }

        /// <summary>The window of the morph at <paramref name="m"/> (0 trapezoid … 1 Gaussian) at window point <paramref name="i"/> (the engine's own blend, for pictures and tests).</summary>
        public static float Morphed(double m, int i) => (float)((1 - m) * Trapezoid[i] + m * Table[(int)GrainShape.Gauss][i]);

        static readonly double[] morphEnergy = MorphEnergies();

        // The integrals of w² for the trapezoid, trapezoid x Gaussian and the Gaussian (a Hann window: 0.375).
        static double[] MorphEnergies()
        {
            var g = Table[(int)GrainShape.Gauss];
            double tt = 0, tg = 0, gg = 0;
            for (int i = 0; i < Size; i++)
            {
                tt += (double)Trapezoid[i] * Trapezoid[i];
                tg += (double)Trapezoid[i] * g[i];
                gg += (double)g[i] * g[i];
            }
            return new[] { tt / Size, tg / Size, gg / Size };
        }

        /// <summary>The loudness of a cloud of grains of the morph at <paramref name="m"/> relative to a Hann window's (power adds: the amplitude to scale by is its inverse).</summary>
        public static double MorphLoudness(double m) =>
            Math.Sqrt(((1 - m) * (1 - m) * morphEnergy[0] + 2 * m * (1 - m) * morphEnergy[1] + m * m * morphEnergy[2]) / 0.375);
    }

    internal sealed partial class NoteBank
    {
        /// <summary>Grains a slot of a note may have sounding at once (further ones wait their turn).</summary>
        public const int MaxGrains = 32;

        /// <summary>
        /// The grain cap (spec §4.6): a slot of a note keeps at most <see cref="GrainCap"/> grains live, and the bank shares
        /// <see cref="GrainBudget"/> live grains between its granular slots (never fewer than <see cref="GrainFloor"/> a slot).
        /// A grain past the limit takes the place of the slot's oldest, which fades out over <see cref="GrainFade"/> samples.
        /// </summary>
        public const int GrainCap = 10, GrainBudget = 160, GrainFloor = 4, GrainFade = 64;

        struct Grain
        {
            public bool On;
            public double Pos, Step;           // in the level's samples
            public int Left_, K;               // samples to go, samples played
            public float WinStep;              // window points per sample
            public float Fade, FadeStep;       // 1 and 0, or running down to 0 when the grain was taken over
            public float GainL, GainR;
            public float[] Left, Right, Kernel, Window;
            public bool Stereo;
            public float[] Window2;            // Neo Learn's window morph: the Gaussian end (null: Window alone)
            public float Mix;                  // the share of Window2
        }

        // Per note × slot (note · Oscillators + slot).
        readonly SampleLayer[] extLayer = new SampleLayer[Notes * Oscillators];
        readonly int[] extKind = new int[Notes * Oscillators];            // 0 none, 1 sample, 2 granular, 3 harmonic, 4 modal, 5 spectral
        readonly bool[] extActive = new bool[Notes * Oscillators], extFresh = new bool[Notes * Oscillators];
        readonly float[] extLvl = new float[Notes * Oscillators], extLvlStep = new float[Notes * Oscillators];
        readonly float[] extTo2 = new float[Notes * Oscillators], extTo2Step = new float[Notes * Oscillators];
        readonly int[] extVoices = new int[Notes * Oscillators], extPrevU = new int[Notes * Oscillators];
        readonly bool[] extAny = new bool[Notes], extToF2 = new bool[Notes];
        // The Sample engine's loop and direction this tick.
        readonly double[] smLs = new double[Notes * Oscillators], smLe = new double[Notes * Oscillators], smXf = new double[Notes * Oscillators];
        readonly int[] smLoop = new int[Notes * Oscillators];
        readonly bool[] smRev = new bool[Notes * Oscillators];
        // Its voices (note · MaxLanes + slot · MaxUnison + voice).
        readonly double[] svPos = new double[Notes * MaxLanes], svStep = new double[Notes * MaxLanes], svScale = new double[Notes * MaxLanes];
        readonly int[] svDir = new int[Notes * MaxLanes], svLevel = new int[Notes * MaxLanes];
        readonly bool[] svOn = new bool[Notes * MaxLanes], svLooped = new bool[Notes * MaxLanes];
        readonly float[][] svKernel = new float[Notes * MaxLanes][];
        readonly float[] svGL = new float[Notes * MaxLanes], svGR = new float[Notes * MaxLanes];
        readonly float[] svDL = new float[Notes * MaxLanes], svDR = new float[Notes * MaxLanes];
        // The Granular engine: its grains, its clock and place, and this tick's settings.
        readonly Grain[] grains = new Grain[Notes * Oscillators * MaxGrains];
        readonly double[] grTimer = new double[Notes * Oscillators], grScan = new double[Notes * Oscillators];
        readonly Rng[] grRng = new Rng[Notes * Oscillators];
        readonly int[] grNext = new int[Notes * Oscillators], grU = new int[Notes * Oscillators], grLen = new int[Notes * Oscillators], grShape = new int[Notes * Oscillators];
        readonly double[] grInterval = new double[Notes * Oscillators], grRatio = new double[Notes * Oscillators], grScanStep = new double[Notes * Oscillators];
        readonly float[] grSpray = new float[Notes * Oscillators], grJit = new float[Notes * Oscillators], grRev = new float[Notes * Oscillators],
            grWidth = new float[Notes * Oscillators], grAmp = new float[Notes * Oscillators], grPos = new float[Notes * Oscillators],
            grPan = new float[Notes * Oscillators], grSpread = new float[Notes * Oscillators];
        readonly int[] grLive = new int[Notes * Oscillators];
        // Neo Learn's additions this tick: the window morph (below 0: off) and the share of octave-up grains (0 … 0.5).
        readonly float[] grMorph = new float[Notes * Oscillators], grOct = new float[Notes * Oscillators], grSize = new float[Notes * Oscillators];

        /// <summary>
        /// The last grains started (a ring for pictures; any note's, any slot's): where in the sample (0 … 1), how long (s), where in the stereo field
        /// (-1 … 1) and whether it sounds an octave up. The audio thread writes; a picture reads <see cref="GrainLogAt"/> first, then the entries.
        /// </summary>
        public const int GrainLogSize = 32;
        public readonly float[] GrainLogPos = new float[GrainLogSize], GrainLogLen = new float[GrainLogSize], GrainLogPan = new float[GrainLogSize];
        public readonly bool[] GrainLogUp = new bool[GrainLogSize];
        public int GrainLogAt;
        int grCap = GrainCap;
        readonly float[] exL = new float[MaxTick], exR = new float[MaxTick];

        /// <summary>A note starts: its Sample and Granular slots begin again, silent.</summary>
        void ExtBegin(int slot)
        {
            extAny[slot] = extToF2[slot] = false;
            for (int o = 0; o < Oscillators; o++)
            {
                int no = slot * Oscillators + o;
                extFresh[no] = true;
                extActive[no] = false;
                extLvl[no] = extLvlStep[no] = extTo2[no] = extTo2Step[no] = 0;
                extPrevU[no] = extVoices[no] = 0;
                extKind[no] = 0;
            }
        }

        /// <summary>This tick's control values for a note's Sample and Granular slots (every tick: cheap, and pitch moves).</summary>
        void ExtControl(int note, float invCount, double noteHz, double vr)
        {
            var s = S;
            bool any = false, to2 = false;
            for (int o = 0; o < Oscillators; o++)
            {
                var os = s.Osc[o];
                int no = note * Oscillators + o;
                bool sample = os.Type == SlotType.Sample, grain = os.Type == SlotType.Granular, harm = os.Type == SlotType.Harmonic, modal = os.Type == SlotType.Modal, spec = os.Type == SlotType.Spectral;
                var layer = harm || modal || spec ? null : os.Source;
                bool play = harm || modal || spec && os.Spectrum != null || (sample || grain) && layer != null && layer.Length >= 8;
                if (!play && extLvl[no] == 0f)
                {
                    extActive[no] = false;
                    extFresh[no] = true;
                    continue;
                }
                float lvlTarget = play ? Level(note, o) : 0f, to2Target = play ? OscToF2(o) : 0f;
                if (play)
                {
                    int kind = sample ? 1 : grain ? 2 : harm ? 3 : spec ? 5 : 4;
                    if (extFresh[no] || extKind[no] != kind || !ReferenceEquals(extLayer[no], layer) || spec && !ReferenceEquals(spModel[no], os.Spectrum)) ExtInit(note, o, no, kind, layer);
                    int u = Math.Clamp(os.Unison, 1, MaxUnison);
                    Shape(o, u, os);
                    float pitchMod = Md(note, RouteDest.APitch + o);
                    if (harm)
                    {
                        // Turns of the fundamental a sample at the voice rate.
                        double inc0 = noteHz * oscRatio[o] / vr;
                        if (pitchMod != 0) inc0 *= PortableMath.Exp2(pitchMod);
                        HarmControl(note, o, no, os, u, inc0, invCount);
                    }
                    else if (spec) SpecControl(note, o, no, os, u, noteHz, vr, invCount, pitchMod);
                    else if (modal)
                    {
                        double inc0 = noteHz * oscRatio[o] / vr;
                        if (pitchMod != 0) inc0 *= PortableMath.Exp2(pitchMod);
                        ModalControl(note, o, no, os, u, inc0, vr, invCount);
                    }
                    else
                    {
                        double rootHz = DspMath.NoteToHz(os.Root);
                        double ratio0 = noteHz * oscRatio[o] / rootHz * layer.Rate / vr;
                        if (pitchMod != 0) ratio0 *= PortableMath.Exp2(pitchMod);
                        if (sample) SampleControl(note, o, no, os, layer, u, ratio0, invCount);
                        else GrainControl(note, o, no, os, layer, u, ratio0, vr);
                    }
                }
                float lv = extLvl[no];
                if (lvlTarget == 0f && Math.Abs(lv) < 1e-7f) lv = extLvl[no] = 0f;
                extLvlStep[no] = (lvlTarget - lv) * invCount;
                extTo2Step[no] = (to2Target - extTo2[no]) * invCount;
                extActive[no] = true;
                any = true;
                to2 |= to2Target > 0f || extTo2[no] > 0f;
            }
            extAny[note] = any;
            extToF2[note] = to2;
        }

        void ExtInit(int note, int o, int no, int kind, SampleLayer layer)
        {
            var os = S.Osc[o];
            extFresh[no] = false;
            extKind[no] = kind;
            extLayer[no] = layer;
            extPrevU[no] = 0;
            int vb = note * MaxLanes + o * MaxUnison;
            if (kind == 1)
            {
                // The start is from where the sample begins to play: its end when reversed.
                double start = Math.Clamp(OscPosition(o) + Md(note, RouteDest.APosition + o), 0f, 1f);
                smRev[no] = os.Reverse;
                for (int l = 0; l < MaxUnison; l++)
                {
                    svPos[vb + l] = (os.Reverse ? 1 - start : start) * layer.Length;
                    svDir[vb + l] = os.Reverse ? -1 : 1;
                    svOn[vb + l] = true;
                    svLooped[vb + l] = false;
                    svGL[vb + l] = svGR[vb + l] = svDL[vb + l] = svDR[vb + l] = 0;
                }
            }
            else if (kind == 3) HarmInit(note, o, no);
            else if (kind == 4) ModalInit(note, o, no);
            else if (kind == 5) SpecInit(note, o, no);
            else
            {
                int gb = no * MaxGrains;
                for (int g = 0; g < MaxGrains; g++) grains[gb + g].On = false;
                grLive[no] = 0;
                grTimer[no] = 0;
                grScan[no] = 0;
                grNext[no] = 0;
                grRng[no] = new Rng(unchecked(seeds[note] * 2654435761u + (uint)(o + 1) * 0x85EBCA6Bu) | 1u);
            }
        }

        void SampleControl(int note, int o, int no, OscSettings os, SampleLayer layer, int u, double ratio0, float invCount)
        {
            int vb = note * MaxLanes + o * MaxUnison;
            double n = layer.Length;
            if (os.Reverse != smRev[no])
            {
                // Turned over while playing: every voice runs the other way.
                smRev[no] = os.Reverse;
                for (int l = 0; l < MaxUnison; l++) svDir[vb + l] = -svDir[vb + l];
            }
            smLoop[no] = (int)os.Loop;
            // The loop in whole samples (a wrap by a whole number of samples keeps the phase).
            double a = Math.Clamp(os.LoopStart, 0f, 1f), b = Math.Clamp(os.LoopEnd, 0f, 1f);
            if (b < a) (a, b) = (b, a);
            double ls = Math.Round(a * n), le = Math.Round(b * n);
            if (le - ls < 64) smLoop[no] = (int)SampleLoop.Off;
            smLs[no] = ls;
            smLe[no] = le;
            double xf = Math.Min(Math.Max(0f, os.LoopFade) * layer.Rate, 0.5 * (le - ls));
            smXf[no] = xf >= 1.0 ? xf : 0.0;
            float norm = 1f / MathF.Sqrt(u);
            int count = Math.Max(u, extPrevU[no]);
            for (int l = 0; l < count; l++)
            {
                int k = vb + l;
                bool live = l < u;
                if (live)
                {
                    double r = Math.Clamp(ratio0 * ratio[o * MaxUnison + l], 1e-4, 60.0);
                    double ar = r;
                    int lev = 0;
                    while (ar > 2.0 && lev < layer.Levels - 1)
                    {
                        ar *= 0.5;
                        lev++;
                    }
                    svStep[k] = r;
                    svLevel[k] = lev;
                    svScale[k] = 1.0 / (1 << lev);
                    svKernel[k] = Interpolation.Kernel(Interpolation.CutoffFor(ar));
                }
                float tl = live ? norm * panL[o * MaxUnison + l] : 0f, tr = live ? norm * panR[o * MaxUnison + l] : 0f;
                svDL[k] = (tl - svGL[k]) * invCount;
                svDR[k] = (tr - svGR[k]) * invCount;
                if (!live && svGL[k] == 0f && svGR[k] == 0f) svDL[k] = svDR[k] = 0f;
            }
            extVoices[no] = count;
            extPrevU[no] = u;
        }

        void GrainControl(int note, int o, int no, OscSettings os, SampleLayer layer, int u, double ratio0, double vr)
        {
            grU[no] = u;
            grRatio[no] = ratio0;
            float size = Math.Clamp(os.GrainSize, 0.002f, 1f), rate = Math.Clamp(os.GrainRate, 0.5f, 200f);
            grLen[no] = Math.Max(8, (int)(size * vr));
            grInterval[no] = vr / rate;
            grSpray[no] = Math.Clamp(os.Spray, 0f, 1f);
            grJit[no] = Math.Clamp(os.GrainPitch, 0f, 2400f);
            grRev[no] = Math.Clamp(os.GrainReverse, 0f, 1f);
            grWidth[no] = Math.Clamp(os.GrainWidth, 0f, 1f);
            grShape[no] = Math.Clamp((int)os.GrainShape, 0, GrainWindows.Table.Length - 1);
            grPan[no] = os.Pan;
            grSpread[no] = os.Spread;
            grPos[no] = Math.Clamp(OscPosition(o) + Md(note, RouteDest.APosition + o), 0f, 1f);
            // Frames of the sample per voice-rate sample at the sample's own speed, as a share of its length.
            grScanStep[no] = Math.Clamp(os.Scan, -4f, 4f) * layer.Rate / vr / layer.Length;
            // Grains overlapping at random add in power: hold the cloud's loudness as the overlap grows.
            double overlap = Math.Min(size * rate, grCap);
            grAmp[no] = (float)(1.0 / Math.Sqrt(Math.Max(1.0, overlap * 0.4)));
            grSize[no] = size;
            grMorph[no] = os.GrainMorph < 0f ? -1f : Math.Min(os.GrainMorph, 1f);
            grOct[no] = 0.5f * Math.Clamp(os.GrainOctave, 0f, 1f);
            if (grMorph[no] >= 0f) grAmp[no] = (float)(grAmp[no] / MorphLoudnessAt(grMorph[no]));
        }

        static double MorphLoudnessAt(float m) => GrainWindows.MorphLoudness(m);

        /// <summary>Each tick: the cap for a slot's grains, the bank's budget shared between the granular slots sounding.</summary>
        void GrainShare()
        {
            int slots = 0;
            for (int no = 0; no < Notes * Oscillators; no++) if (extActive[no] && extKind[no] == 2) slots++;
            grCap = Math.Clamp(GrainBudget / Math.Max(1, slots), GrainFloor, GrainCap);
        }

        /// <summary>The Sample and Granular slots of a note into the filters' inputs (they have summed the lanes' voices already).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void RenderExt(int note, int count)
        {
            for (int o = 0; o < Oscillators; o++)
            {
                int no = note * Oscillators + o;
                if (!extActive[no]) continue;
                Array.Clear(exL, 0, count);
                Array.Clear(exR, 0, count);
                if (extKind[no] == 1) SampleVoices(note, o, no, count);
                else if (extKind[no] == 2) Grains(no, count);
                else if (extKind[no] == 3) HarmVoices(note, o, no, count);
                else if (extKind[no] == 5) SpecVoices(note, o, no, count);
                else ModalVoices(note, o, no, count);
                float lv = extLvl[no], ls = extLvlStep[no], t2 = extTo2[no], ts = extTo2Step[no];
                bool f2 = extToF2[note];
                for (int i = 0; i < count; i++)
                {
                    lv += ls;
                    t2 += ts;
                    float a = lv * exL[i], b = lv * exR[i];
                    int at = i * Notes + note;
                    if (f2)
                    {
                        float to1 = 1f - t2;
                        in1L[at] += a * to1;
                        in1R[at] += b * to1;
                        in2L[at] += a * t2;
                        in2R[at] += b * t2;
                    }
                    else
                    {
                        in1L[at] += a;
                        in1R[at] += b;
                    }
                }
                extLvl[no] = lv;
                extTo2[no] = t2;
            }
        }

        /// <summary>
        /// One channel at <paramref name="y"/> (the level's samples), as if the loop ran on for ever past its ends: a tap
        /// beyond the loop's end comes from its start (a mirror image when <paramref name="mirror"/>), and, once the voice
        /// has looped (or when it plays toward the start), a tap before the start from the end. Only used within a kernel's
        /// reach of the loop's ends; everywhere else the plain read is the same samples.
        /// </summary>
        static float ReadLooped(float[] d, double y, float[] kernel, int lsL, int leL, bool mirror, bool wrapUpper, bool wrapLower)
        {
            int i = (int)Math.Floor(y);
            double ph = (y - i) * Interpolation.Phases;
            int p = (int)ph;
            float t = (float)(ph - p);
            int at = i - (Interpolation.Half - 1);
            int k0 = p * Interpolation.Taps, k1 = k0 + Interpolation.Taps, loopL = leL - lsL;
            float acc = 0;
            for (int j = 0; j < Interpolation.Taps; j++)
            {
                int u = at + j;
                if (wrapUpper && u >= leL) u = mirror ? 2 * leL - u : u - loopL;
                else if (wrapLower && u < lsL) u = mirror ? 2 * lsL - u : u + loopL;
                int idx = u + SampleLayer.Pad;
                float s = (uint)idx < (uint)d.Length ? d[idx] : 0f;
                acc += s * (kernel[k0 + j] + t * (kernel[k1 + j] - kernel[k0 + j]));
            }
            return acc;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void SampleVoices(int note, int o, int no, int count)
        {
            var layer = extLayer[no];
            bool stereo = layer.Stereo;
            int loop = smLoop[no], n = layer.Length;
            double ls = smLs[no], le = smLe[no], xf = smXf[no], loopL = le - ls;
            int vb = note * MaxLanes + o * MaxUnison;
            bool forwardLoop = loop == (int)SampleLoop.Forward, pingPong = loop == (int)SampleLoop.PingPong;
            for (int v = 0; v < extVoices[no]; v++)
            {
                int k = vb + v;
                if (!svOn[k]) continue;
                int lev = svLevel[k];
                var dl = layer.Left[lev];
                var dr = layer.Right[lev];
                var kernel = svKernel[k];
                double scale = svScale[k], step = svStep[k], pos = svPos[k];
                double ymax = dl.Length - SampleLayer.Pad - Interpolation.Half - 1, ymin = -(SampleLayer.Pad - Interpolation.Half);
                int lsL = (int)Math.Round(ls * scale), leL = (int)Math.Round(le * scale);
                int dir = svDir[k];
                bool looped = svLooped[k];
                float gl = svGL[k], gr = svGR[k], sl = svDL[k], sr = svDR[k];
                for (int i = 0; i < count; i++)
                {
                    double y = pos * scale;
                    y = y < ymin ? ymin : y > ymax ? ymax : y;
                    float l, r;
                    bool seam = loop != (int)SampleLoop.Off && (y + Interpolation.Half > leL || y - Interpolation.Half < lsL);
                    if (seam)
                    {
                        bool up = dir > 0 || looped, down = dir < 0 || looped;
                        l = ReadLooped(dl, y, kernel, lsL, leL, pingPong, up, down);
                        r = stereo ? ReadLooped(dr, y, kernel, lsL, leL, pingPong, up, down) : l;
                    }
                    else if (stereo) Interpolation.Read(dl, dr, y, kernel, out l, out r);
                    else { l = Interpolation.Read(dl, y, kernel); r = l; }
                    if (xf > 0 && forwardLoop)
                    {
                        // The loop's last stretch (in the direction of play) fades into its first, for loops that aren't whole periods.
                        double into = dir > 0 ? pos - (le - xf) : (ls + xf) - pos;
                        if (into > 0)
                        {
                            double p2 = dir > 0 ? pos - (loopL - xf) : pos + (loopL - xf);
                            double y2 = p2 * scale;
                            y2 = y2 < ymin ? ymin : y2 > ymax ? ymax : y2;
                            float l2, r2;
                            bool up = dir > 0 || looped, down = dir < 0 || looped;
                            l2 = ReadLooped(dl, y2, kernel, lsL, leL, false, up, down);
                            r2 = stereo ? ReadLooped(dr, y2, kernel, lsL, leL, false, up, down) : l2;
                            float w = (float)(into / xf);
                            l += (l2 - l) * w;
                            r += (r2 - r) * w;
                        }
                    }
                    gl += sl;
                    gr += sr;
                    exL[i] += gl * l;
                    exR[i] += gr * r;
                    pos += dir * step;
                    if (forwardLoop)
                    {
                        double cycle = loopL - xf;
                        if (dir > 0 && pos >= le) { pos = ls + xf + (pos - le) % cycle; looped = true; }
                        else if (dir < 0 && pos < ls) { pos = le - xf - (ls - pos) % cycle; looped = true; }
                    }
                    else if (pingPong)
                    {
                        if (dir > 0 && pos >= le) { pos = Math.Max(ls, 2 * le - pos); dir = -1; looped = true; }
                        else if (dir < 0 && pos < ls) { pos = Math.Min(le, 2 * ls - pos); dir = 1; looped = true; }
                    }
                    else if (pos >= n || pos < 0)
                    {
                        svOn[k] = false;
                        break;
                    }
                }
                svPos[k] = pos;
                svDir[k] = dir;
                svLooped[k] = looped;
                svGL[k] = gl;
                svGR[k] = gr;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Grains(int no, int count)
        {
            double timer = grTimer[no], scan = grScan[no], scanStep = grScanStep[no], interval = grInterval[no];
            int from = 0;
            for (int i = 0; i < count; i++)
            {
                timer -= 1.0;
                if (timer <= 0)
                {
                    // The grains sound up to here, then the new one joins (a clock tick is the only thing that changes the set).
                    if (i > from) GrainSpan(no, from, i);
                    from = i;
                    Spawn(no, scan);
                    timer += interval * (1.0 + 0.3 * grRng[no].NextBipolar());
                }
                scan += scanStep;
            }
            if (count > from) GrainSpan(no, from, count);
            grTimer[no] = timer;
            grScan[no] = scan;
            int live = 0, gb = no * MaxGrains;
            for (int g = 0; g < MaxGrains; g++) if (grains[gb + g].On && grains[gb + g].FadeStep == 0f) live++;
            grLive[no] = live;
        }

        /// <summary>The slot's grains over samples [from, to), one grain at a time (no branch per sample); a grain's samples add in the pool's order.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void GrainSpan(int no, int from, int to)
        {
            int gb = no * MaxGrains;
            var xl = exL;
            var xr = exR;
            for (int g = 0; g < MaxGrains; g++)
            {
                ref var gr = ref grains[gb + g];
                if (!gr.On) continue;
                int n = Math.Min(to - from, gr.Left_);
                var left = gr.Left;
                var right = gr.Right;
                var kernel = gr.Kernel;
                var win = gr.Window;
                var win2 = gr.Window2;
                float mix = gr.Mix;
                double ymax = left.Length - SampleLayer.Pad - Interpolation.Half - 1, ymin = -(SampleLayer.Pad - Interpolation.Half);
                double y = gr.Pos, step = gr.Step;
                float gl = gr.GainL, grr = gr.GainR, fade = gr.Fade, fs = gr.FadeStep, ws = gr.WinStep;
                int k = gr.K;
                if (gr.Stereo)
                {
                    for (int i = 0; i < n; i++)
                    {
                        float fi = k++ * ws;
                        int wi = (int)fi;
                        float w = win[wi] + (fi - wi) * (win[wi + 1] - win[wi]);
                        if (win2 != null) w += mix * (win2[wi] + (fi - wi) * (win2[wi + 1] - win2[wi]) - w);
                        w *= fade;
                        fade += fs;
                        double yc = y < ymin ? ymin : y > ymax ? ymax : y;
                        Interpolation.Read(left, right, yc, kernel, out float l, out float r);
                        xl[from + i] += gl * w * l;
                        xr[from + i] += grr * w * r;
                        y += step;
                    }
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        float fi = k++ * ws;
                        int wi = (int)fi;
                        float w = win[wi] + (fi - wi) * (win[wi + 1] - win[wi]);
                        if (win2 != null) w += mix * (win2[wi] + (fi - wi) * (win2[wi + 1] - win2[wi]) - w);
                        w *= fade;
                        fade += fs;
                        double yc = y < ymin ? ymin : y > ymax ? ymax : y;
                        float l = Interpolation.Read(left, yc, kernel);
                        xl[from + i] += gl * w * l;
                        xr[from + i] += grr * w * l;
                        y += step;
                    }
                }
                gr.Pos = y;
                gr.K = k;
                gr.Fade = fade;
                gr.Left_ -= n;
                if (gr.Left_ <= 0) gr.On = false;
            }
        }

        /// <summary>Starts a grain at the cloud's centre (plus its spray), in the next voice's pitch and place.</summary>
        void Spawn(int no, double scan)
        {
            int gb = no * MaxGrains, free = -1;
            // Over the cap: the oldest live grain fades out and the new one takes its turn (the cap is never above the pool).
            while (grLive[no] >= grCap)
            {
                int old = -1;
                float oldest = -1f;
                for (int g = 0; g < MaxGrains; g++)
                {
                    ref var c = ref grains[gb + g];
                    if (!c.On || c.FadeStep != 0f) continue;
                    float age = c.K / (float)(c.K + c.Left_);
                    if (age > oldest) { oldest = age; old = g; }
                }
                if (old < 0) break;
                ref var d = ref grains[gb + old];
                if (d.Left_ > GrainFade) d.Left_ = GrainFade;
                d.FadeStep = -d.Fade / Math.Max(1, d.Left_);
                grLive[no]--;
            }
            for (int g = 0; g < MaxGrains && free < 0; g++) if (!grains[gb + g].On) free = g;
            int o = no % Oscillators;
            int u = grU[no], j = grNext[no];
            grNext[no] = j + 1 >= u ? 0 : j + 1;
            ref var rng = ref grRng[no];
            // The draws always happen, in one order, so the stream doesn't depend on the settings.
            float dPlace = rng.NextBipolar(), dPitch = rng.NextBipolar(), dRev = rng.NextUnipolar(), dPan = rng.NextBipolar();
            // Neo Learn: an octave-up grain is a draw of its own, taken only when asked for, so Dye Essence's stream is unchanged.
            float dOct = grOct[no] > 0f ? rng.NextUnipolar() : 1f;
            if (free < 0) return;
            var layer = extLayer[no];
            double frac = grPos[no] + scan + 0.5 * grSpray[no] * dPlace;
            frac -= Math.Floor(frac);
            double r = grRatio[no] * ratio[o * MaxUnison + j];
            if (grJit[no] > 0f) r *= PortableMath.Pow(2.0, grJit[no] * dPitch / 1200.0);
            if (dOct < grOct[no]) r *= 2.0;
            r = Math.Clamp(r, 1e-4, 60.0);
            double step = dRev < grRev[no] ? -r : r;
            double ar = r;
            int lev = 0;
            while (ar > 2.0 && lev < layer.Levels - 1)
            {
                ar *= 0.5;
                lev++;
            }
            double scale = 1.0 / (1 << lev);
            int len = grLen[no];
            // Centred on the place: half the grain's reading before it, half after.
            double start = frac * layer.Length - 0.5 * len * step;
            float pj = u > 1 ? 2f * j / (u - 1) - 1f : 0f;
            float panned = Math.Clamp(grPan[no] + pj * grSpread[no] + grWidth[no] * dPan, -1f, 1f);
            DspMath.PanGains(panned, out float pl, out float pr);
            ref var gn = ref grains[gb + free];
            gn.On = true;
            gn.Pos = start * scale;
            gn.Step = step * scale;
            gn.K = 0;
            gn.Left_ = len;
            gn.WinStep = GrainWindows.Size / (float)len;
            gn.Fade = 1f;
            gn.FadeStep = 0f;
            grLive[no]++;
            gn.GainL = pl * grAmp[no];
            gn.GainR = pr * grAmp[no];
            gn.Left = layer.Left[lev];
            gn.Right = layer.Right[lev];
            gn.Stereo = layer.Stereo;
            gn.Kernel = Interpolation.Kernel(Interpolation.CutoffFor(ar));
            int li = GrainLogAt & (GrainLogSize - 1);
            GrainLogPos[li] = (float)frac;
            GrainLogLen[li] = grSize[no];
            GrainLogPan[li] = panned;
            GrainLogUp[li] = dOct < grOct[no];
            Volatile.Write(ref GrainLogAt, GrainLogAt + 1);
            if (grMorph[no] >= 0f)
            {
                gn.Window = GrainWindows.Trapezoid;
                gn.Window2 = GrainWindows.Table[(int)GrainShape.Gauss];
                gn.Mix = grMorph[no];
            }
            else
            {
                gn.Window = GrainWindows.Table[grShape[no]];
                gn.Window2 = null;
                gn.Mix = 0f;
            }
        }
    }
}
