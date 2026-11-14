#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.12): the Spectral engine, the resynthesis half. A voice reads the
// sample's model (SpectralModel.cs) at a time position: each tick it works out, for each of the 64 partial slots,
// the frequency and amplitude the model has there (between two frames, over up to five taps of time when Blur is
// on), moves them by the controls (pitch with the key, Formant keep, Harmonic snap, Stretch, Tilt, Tonal/Noise,
// Partials), and hands them to the Harmonic engine's phasor bank (HarmonicEngine.cs: four partials to a 128-bit
// vector, a rotation and a level ramp each, the same bits on every lane implementation). The residual is band-
// filtered noise: 24 biquad band-passes in six vectors, their gains following the model's band levels.
// Like the Harmonic and Modal engines it renders beside the lanes (RenderExt).

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading;
using System.Threading.Tasks;
using NeoBuzz.Dsp;
using NeoBuzz.Sampler;

namespace NeoBuzz.DyeEssence
{
    internal sealed class SpectralEngine : IEngine
    {
        public TableArena TableOf(OscSettings os) => null;
        public float PositionOf(OscSettings os) => os.Position;
        public float DriftCents(OscSettings os, float r) => 0f;
    }

    /// <summary>The built-in samples' models, made by the analysis in the background once per process (as the samples are).</summary>
    internal static class SpectralSources
    {
        public const int Count = 5;
        static readonly SpectralModel[] built = new SpectralModel[Count];
        static readonly int[] requested = new int[Count];

        public static SpectralModel Get(int index)
        {
            if (index < 0 || index >= built.Length) return null;
            var t = Volatile.Read(ref built[index]);
            if (t != null) return t;
            if (Interlocked.Exchange(ref requested[index], 1) == 0) Start(index);
            return null;
        }

        // Apart from Get so that its closure is made only when a model is wanted (a closure over a parameter allocates on every call, wanted or not).
        static void Start(int index) => Task.Run(() => Volatile.Write(ref built[index], Make(index)));

        public static bool AllBuilt()
        {
            bool all = true;
            for (int i = 0; i < built.Length; i++)
                if (Get(i) == null) all = false;
            return all;
        }

        public static SpectralModel GetNow(int index)
        {
            var t = Volatile.Read(ref built[index]);
            if (t != null) return t;
            t = Make(index);
            Volatile.Write(ref built[index], t);
            Interlocked.Exchange(ref requested[index], 1);
            return t;
        }

        static SpectralModel Make(int index)
        {
            var layer = SampleSources.GetNow(index);
            return SpectralAnalysis.Analyse(MonoOf(layer), layer.Rate, name: SampleSources.Names[index]);
        }

        /// <summary>The sample's channels mixed to mono, as the analysis reads it.</summary>
        public static float[] MonoOf(SampleLayer layer)
        {
            var x = new float[layer.Length];
            var l = layer.Left[0];
            var r = layer.Stereo ? layer.Right[0] : null;
            for (int i = 0; i < x.Length; i++) x[i] = r == null ? l[SampleLayer.Pad + i] : 0.5f * (l[SampleLayer.Pad + i] + r[SampleLayer.Pad + i]);
            return x;
        }
    }

    internal sealed partial class NoteBank
    {
        /// <summary>A slot's partials times its unison voices, at most, a note (the Harmonic engine's budget, <see cref="HarmBudget"/>).</summary>
        const int SpecBands = SpectralModel.Bands, SpecGroups = SpectralModel.Bands / 4, SpecSlots = SpectralModel.MaxPartials;

        // Per note and slot: where the read position is (seconds travelled), the model that was read, and the noise's state.
        readonly double[] spElapsed = new double[Notes * Oscillators];
        readonly SpectralModel[] spModel = new SpectralModel[Notes * Oscillators];
        readonly Vector128<uint>[] spRngV = new Vector128<uint>[Notes * Oscillators * SpecGroups];
        readonly bool[] spNzOn = new bool[Notes * Oscillators];
        readonly float[] spPanL = new float[Notes * Oscillators], spPanR = new float[Notes * Oscillators];
        readonly Vector128<float>[] spZ1 = new Vector128<float>[Notes * Oscillators * SpecGroups], spZ2 = new Vector128<float>[Notes * Oscillators * SpecGroups],
            spZ3 = new Vector128<float>[Notes * Oscillators * SpecGroups], spZ4 = new Vector128<float>[Notes * Oscillators * SpecGroups],
            spG = new Vector128<float>[Notes * Oscillators * SpecGroups], spDG = new Vector128<float>[Notes * Oscillators * SpecGroups];
        // Per voice and slot: the turns a sample the rotation was made for.
        readonly float[] spTurns = new float[Notes * MaxLanes * SpecSlots];
        // This tick's slots for the slot being worked on: the output, a tap's two frames, the sums over taps, the envelope.
        readonly float[] spAmp = new float[SpecSlots], spFreq = new float[SpecSlots];
        readonly float[] spAmpA = new float[SpecSlots], spFreqA = new float[SpecSlots], spAmpB = new float[SpecSlots], spFreqB = new float[SpecSlots];
        readonly float[] spSumA = new float[SpecSlots], spSumF = new float[SpecSlots];
        readonly float[] spEnv = new float[SpecSlots];
        readonly float[] spNoise = new float[SpecBands], spNoiseTarget = new float[SpecBands];
        // The noise's band-pass filters for the oversampling in use (index 0, 1, 2: 1×, 2×, 4×), six vectors each, and the gain of each band's white noise.
        readonly Vector128<float>[] spB0 = new Vector128<float>[3 * SpecGroups], spB2 = new Vector128<float>[3 * SpecGroups],
            spA1 = new Vector128<float>[3 * SpecGroups], spA2 = new Vector128<float>[3 * SpecGroups];
        readonly float[] spCal = new float[3 * SpecBands];

        /// <summary>Blur's widest reach in seconds either side at 100 %; the taps' weights (1 2 3 2 1) / 9.</summary>
        const float BlurSeconds = 0.3f;

        static int OvIndex(int ov) => ov >= 4 ? 2 : ov >= 2 ? 1 : 0;

        /// <summary>The noise's band-pass filters for each oversampling at the sample rate (when the rate is set; the audio thread only reads them).</summary>
        const double BandWiden = 1.5;

        void SpecPrepare(float sr)
        {
            for (int oi = 0; oi < 3; oi++)
            {
                double vr = sr * (1 << oi);
                var b0 = new float[SpecBands]; var b2 = new float[SpecBands]; var a1 = new float[SpecBands]; var a2 = new float[SpecBands];
                for (int b = 0; b < SpecBands; b++)
                {
                    double lo = SpectralModel.BandEdges[b], hi = b == SpecBands - 1 ? 16000.0 : SpectralModel.BandEdges[b + 1];
                    double f0 = b == 0 ? 50.0 : Math.Sqrt(lo * hi);
                    double q = f0 / (BandWiden * (hi - lo));   // two such sections in a row: each wider, so the pair covers the band
                    double w0 = 2.0 * Math.PI * Math.Min(f0, 0.45 * vr) / vr;
                    double alpha = PortableMath.Sin(w0) / (2.0 * q);
                    double a0 = 1.0 + alpha;
                    double cb0 = alpha / a0, cb2 = -alpha / a0, ca1 = -2.0 * PortableMath.Cos(w0) / a0, ca2 = (1.0 - alpha) / a0;
                    b0[b] = (float)cb0; b2[b] = (float)cb2; a1[b] = (float)ca1; a2[b] = (float)ca2;
                    // The variance gain on white noise: the impulse response's energy (a unit variance in, this out).
                    double z1 = 0, z2 = 0, y1 = 0, y2 = 0, e = 0;
                    for (int i = 0; i < 16384; i++)
                    {
                        double x = i == 0 ? 1.0 : 0.0;
                        double y = cb0 * x + z1;
                        z1 = -ca1 * y + z2;
                        z2 = cb2 * x - ca2 * y;
                        double w = cb0 * y + y1;
                        y1 = -ca1 * w + y2;
                        y2 = cb2 * y - ca2 * w;
                        e += w * w;
                    }
                    spCal[oi * SpecBands + b] = (float)Math.Sqrt(e);
                }
                for (int g = 0; g < SpecGroups; g++)
                {
                    spB0[oi * SpecGroups + g] = Vector128.Create(b0[4 * g], b0[4 * g + 1], b0[4 * g + 2], b0[4 * g + 3]);
                    spB2[oi * SpecGroups + g] = Vector128.Create(b2[4 * g], b2[4 * g + 1], b2[4 * g + 2], b2[4 * g + 3]);
                    spA1[oi * SpecGroups + g] = Vector128.Create(a1[4 * g], a1[4 * g + 1], a1[4 * g + 2], a1[4 * g + 3]);
                    spA2[oi * SpecGroups + g] = Vector128.Create(a2[4 * g], a2[4 * g + 1], a2[4 * g + 2], a2[4 * g + 3]);
                }
            }
        }

        /// <summary>2^x in single precision from a polynomial (no libm: the same bits everywhere); good to about 1e-7 relative.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Exp2F(float x)
        {
            x = x < -60f ? -60f : x > 30f ? 30f : x;
            float fl = MathF.Floor(x);
            float g = x - fl - 0.5f;
            float g2 = g * g;
            // e^(g ln 2) to the sixth degree on -0.5 … 0.5, times 2^0.5.
            float p = 1f + g * (0.69314718f + g * (0.24022651f + g * (0.05550411f + g * (0.00961813f + g * (0.00133336f + g * 0.00015403f)))));
            return p * 1.41421356f * BitConverter.Int32BitsToSingle(((int)fl + 127) << 23);
        }

        void SpecInit(int note, int o, int no)
        {
            var os = S.Osc[o];
            spElapsed[no] = 0;
            spModel[no] = os.Spectrum;
            spNzOn[no] = false;
            int zb = no * SpecGroups;
            for (int g = 0; g < SpecGroups; g++)
            {
                spZ1[zb + g] = spZ2[zb + g] = spZ3[zb + g] = spZ4[zb + g] = spG[zb + g] = spDG[zb + g] = default;
                // Every band has a noise of its own (independent, so the bands' powers add exactly): xorshift lanes seeded apart.
                uint b0 = unchecked(seeds[note] * 2246822519u + (uint)(o + 1) * 3266489917u + (uint)g * 668265263u);
                spRngV[zb + g] = Vector128.Create(b0 | 1u, (b0 * 2654435761u) | 1u, (b0 * 40503u + 12345u) | 1u, (b0 * 2246822519u + 7u) | 1u);
            }
            hPer[no] = SpecSlots;
        }

        /// <summary>
        /// The read position in seconds into the model for a slot of a note this tick (the start, plus the time travelled at Speed; the loop applied), or a
        /// negative number when the sample has ended (no loop) and nothing sounds.
        /// </summary>
        double SpecTime(int no, OscSettings os, double dur, float pos)
        {
            double s = pos * dur + spElapsed[no];   // reversed: the same distance, read from the end (the mirror is applied at the return)
            double ls = os.LoopStart * dur, le = os.LoopEnd * dur;
            if (os.Reverse) { double a = dur - le, b = dur - ls; ls = a; le = b; }
            double len = le - ls;
            if (os.Loop != SampleLoop.Off && len >= 0.02 && s >= le)
            {
                double u = s - ls;
                if (os.Loop == SampleLoop.Forward) s = ls + u - len * Math.Floor(u / len);
                else
                {
                    double period = 2.0 * len;
                    u -= period * Math.Floor(u / period);
                    s = ls + (u < len ? u : period - u);
                }
            }
            else if (s >= dur) return -1.0;
            return os.Reverse ? dur - s : s;
        }

        /// <summary>
        /// One frame's partials into the slot arrays <paramref name="amp"/> and <paramref name="freq"/> (zero where the frame has none): the pitch ratio
        /// (and Harmonic snap and Stretch before it), Formant keep (the amplitude the frame's own envelope has at the new place), Tilt.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void SpecFrame(SpectralModel m, int f, float[] amp, float[] freq, float l2r, float r, OscSettings os)
        {
            Array.Clear(amp, 0, SpecSlots);
            Array.Clear(freq, 0, SpecSlots);
            if ((uint)f >= (uint)m.Frames) return;
            int a0 = m.Start[f], a1 = m.Start[f + 1];
            if (a1 == a0) return;
            float formant = os.SpFormant, snap = os.SpSnap, stretch = os.SpStretch, tilt = os.SpTilt;
            float f0 = m.F0[f];
            float fref = f0 > 0f ? f0 : 200f;
            float fl2 = f0 > 0f ? m.F0L2[f] : 7.643856f;     // log2 200
            bool env = formant > 0f && l2r != 0f;
            if (env)
            {
                // The frame's own envelope (through its partials, log-linear; 12 dB an octave below the last) at each partial's place after the shift.
                int j = a0;
                for (int i = a0; i < a1; i++)
                {
                    float x = m.L2[i] + l2r;
                    while (j + 1 < a1 && m.L2[j + 1] <= x) j++;
                    float e;
                    if (x <= m.L2[a0]) e = m.LnA[a0];
                    else if (j >= a1 - 1) e = m.LnA[a1 - 1] - 1.38629436f * (x - m.L2[a1 - 1]);
                    else
                    {
                        float d = m.L2[j + 1] - m.L2[j];
                        float t = d > 1e-6f ? (x - m.L2[j]) / d : 0f;
                        e = m.LnA[j] + t * (m.LnA[j + 1] - m.LnA[j]);
                    }
                    spEnv[i - a0] = e;
                }
            }
            float tiltK = tilt * (1f / 6.0206f), cen = m.Centroid[f];
            for (int i = a0; i < a1; i++)
            {
                int s = m.Slot[i];
                float fi = m.Freq[i];
                if (snap > 0f && f0 > 0f)
                {
                    float h = MathF.Max(1f, MathF.Round(fi / f0));
                    fi += snap * (h * f0 - fi);
                }
                if (stretch != 0f)
                {
                    float oct = MathF.Max(0f, m.L2[i] - fl2);
                    fi *= Exp2F(0.5f * stretch * oct);
                }
                float a = m.Amp[i];
                float l2a = 0f;
                bool shape = false;
                if (env)
                {
                    l2a = formant * (spEnv[i - a0] - m.LnA[i]) * 1.44269504f;
                    shape = true;
                }
                if (tiltK != 0f)
                {
                    l2a += tiltK * (m.L2[i] - cen);
                    shape = true;
                }
                if (shape) a *= Exp2F(l2a);
                amp[s] = a;
                freq[s] = fi * r;
            }
        }

        /// <summary>This tick's partials and noise of a note's Spectral slot, then each voice's rotations and level steps.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void SpecControl(int note, int o, int no, OscSettings os, int u, double noteHz, double vr, float invCount, float pitchMod)
        {
            var m = os.Spectrum;
            double dur = m.Duration, frameSec = m.FrameSeconds;
            // The pitch ratio: the key (as far as Key tracking follows it) from the root, the slot's tuning, the pitch modulation.
            double rootHz = DspMath.NoteToHz(os.Root);
            double keyRatio = noteHz / rootHz;
            double pitchRatio = os.SpKey >= 0.9999f ? keyRatio : PortableMath.Pow(keyRatio, os.SpKey);
            pitchRatio *= oscRatio[o];
            if (pitchMod != 0) pitchRatio *= PortableMath.Exp2(pitchMod);
            float r = (float)pitchRatio;
            float l2r = (float)PortableMath.Log2(pitchRatio);

            float pos = Math.Clamp(OscPosition(o) + Md(note, RouteDest.APosition + o), 0f, 1f);
            double t = SpecTime(no, os, dur, pos);
            spElapsed[no] += os.SpSpeed * tickDt;
            bool ended = t < 0;
            float blur = Math.Clamp(os.SpBlur, 0f, 1f);
            int taps = blur > 0.001f ? 5 : 1;
            float tonalGain = Math.Min(1f, 2f * os.SpBalance), noiseGain = Math.Min(1f, 2f * (1f - os.SpBalance));

            Array.Clear(spAmp, 0, SpecSlots);
            Array.Clear(spFreq, 0, SpecSlots);
            Array.Clear(spNoise, 0, SpecBands);
            if (!ended)
            {
                if (taps > 1)
                {
                    Array.Clear(spSumA, 0, SpecSlots);
                    Array.Clear(spSumF, 0, SpecSlots);
                }
                float width = BlurSeconds * blur * blur;
                for (int tp = 0; tp < taps; tp++)
                {
                    float wt = taps == 1 ? 1f : (tp == 0 || tp == 4 ? 1f : tp == 2 ? 3f : 2f) * (1f / 9f);
                    double tt = taps == 1 ? t : Math.Clamp(t + (tp - 2) * 0.5 * width, 0.0, dur);
                    double fpos = tt / frameSec;
                    int f0 = (int)Math.Floor(fpos);
                    float phi = (float)(fpos - f0);
                    if (f0 < 0) { f0 = 0; phi = 0f; }
                    SpecFrame(m, f0, spAmpA, spFreqA, l2r, r, os);
                    SpecFrame(m, f0 + 1, spAmpB, spFreqB, l2r, r, os);
                    float wA = 1f - phi, wB = phi;
                    for (int s = 0; s < SpecSlots; s++)
                    {
                        float aa = spAmpA[s], ab = spAmpB[s];
                        float a = wA * aa + wB * ab;
                        if (a == 0f) continue;
                        float fr = aa > 0f && ab > 0f ? wA * spFreqA[s] + wB * spFreqB[s] : aa > 0f ? spFreqA[s] : spFreqB[s];
                        if (taps == 1) { spAmp[s] = a; spFreq[s] = fr; }
                        else { spSumA[s] += wt * a; spSumF[s] += wt * a * fr; }
                    }
                    // The residual's bands between the two frames.
                    int nb0 = f0 * SpecBands, nb1 = (f0 + 1) * SpecBands;
                    for (int b = 0; b < SpecBands; b++)
                    {
                        float na = f0 < m.Frames ? m.Noise[nb0 + b] : 0f, nbv = f0 + 1 < m.Frames ? m.Noise[nb1 + b] : 0f;
                        spNoise[b] += wt * (wA * na + wB * nbv);
                    }
                }
                if (taps > 1)
                    for (int s = 0; s < SpecSlots; s++)
                        if (spSumA[s] > 0f) { spAmp[s] = spSumA[s]; spFreq[s] = spSumF[s] / spSumA[s]; }
            }

            int count = oversample * ControlRate;
            int partials = Math.Clamp(os.SpPartials, 1, SpecSlots);
            int per = Math.Min(partials, Math.Max(HarmFloor, HarmBudget / u));
            per = Math.Min(SpecSlots, (per + 3) & ~3);
            float vnorm = 1f / MathF.Sqrt(u);
            float hi = 0.46f / oversample, fadeWidth = 0.06f / oversample;
            int vb = note * MaxLanes + o * MaxUnison;
            int voices = Math.Max(u, extPrevU[no]);
            for (int l = 0; l < voices; l++)
            {
                int k = vb + l;
                bool live = l < u;
                int pb = k * MaxPartials, tb = k * SpecSlots;
                if (live)
                {
                    bool fresh = l >= extPrevU[no];
                    if (fresh) SpecStart(note, o, l, k);
                    float vr_ = ratio_(o, l);
                    int top = 0;
                    for (int j = 0; j < per; j++)
                    {
                        float a = spAmp[j];
                        float target = 0f;
                        if (a > 0f)
                        {
                            float turns = spFreq[j] * vr_ / (float)vr;
                            float nyq = Math.Clamp((hi - turns) / fadeWidth, 0f, 1f);
                            if (nyq > 0f)
                            {
                                target = a * tonalGain * nyq;
                                if (!(target < 8f)) target = float.IsNaN(target) ? 0f : 8f;
                                if (turns != spTurns[tb + j])
                                {
                                    spTurns[tb + j] = turns;
                                    SinCosTurns(turns, out float rs, out float rc);
                                    hRS[pb + j] = rs;
                                    hRC[pb + j] = rc;
                                }
                            }
                        }
                        if (fresh) { hA[pb + j] = 0f; hDA[pb + j] = target * invCount; }
                        else if (target == 0f && MathF.Abs(hA[pb + j]) < 1e-9f) hA[pb + j] = hDA[pb + j] = 0f;   // run down: exact silence, no denormal tail
                        else hDA[pb + j] = (target - hA[pb + j]) * invCount;
                        if (target != 0f || hA[pb + j] != 0f) top = j + 1;
                        // Keep the phasor on the unit circle (one Newton step each tick).
                        float c = hC[pb + j], sn = hS[pb + j];
                        float fct = 1.5f - 0.5f * (c * c + sn * sn);
                        hC[pb + j] = c * fct;
                        hS[pb + j] = sn * fct;
                    }
                    for (int j = per; j < SpecSlots; j++) { hDA[pb + j] = hA[pb + j] == 0f ? 0f : -hA[pb + j] * invCount; if (hA[pb + j] != 0f) top = j + 1; }
                    hBlocks[k] = (top + 3) >> 2;
                }
                float tl = live ? vnorm * panL[o * MaxUnison + l] : 0f, tr = live ? vnorm * panR[o * MaxUnison + l] : 0f;
                svDL[k] = (tl - svGL[k]) * invCount;
                svDR[k] = (tr - svGR[k]) * invCount;
                if (!live && svGL[k] == 0f && svGR[k] == 0f) svDL[k] = svDR[k] = 0f;
                svOn[k] = (live || svGL[k] != 0f || svGR[k] != 0f);
            }
            extVoices[no] = voices;
            extPrevU[no] = u;

            // The noise: gains for each band from the model's levels, ramped across the tick.
            DspMath.PanGains(os.Pan, out float npl, out float npr);
            spPanL[no] = npl; spPanR[no] = npr;
            int oi = OvIndex(oversample);
            bool any = false;
            for (int b = 0; b < SpecBands; b++)
            {
                float g = spNoise[b] * noiseGain / spCal[oi * SpecBands + b];
                if (!(g < 64f)) g = float.IsNaN(g) ? 0f : 64f;
                spNoiseTarget[b] = g;
                if (g > 0f) any = true;
            }
            int gb = no * SpecGroups;
            bool wasOn = spNzOn[no];
            for (int g = 0; g < SpecGroups; g++)
            {
                var tgt = Vector128.LoadUnsafe(ref spNoiseTarget[4 * g]);
                var cur = spG[gb + g];
                // A gain running down to a zero target ends at exactly zero.
                cur = Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(cur), Vector128.Create(1e-9f)) & Vector128.Equals(tgt, Vector128<float>.Zero), Vector128<float>.Zero, cur);
                spG[gb + g] = cur;
                spDG[gb + g] = (tgt - cur) * Vector128.Create(invCount);
            }
            if (!any && wasOn)
            {
                // Silent targets: once every gain has run to zero the filters can rest (their state is cleared).
                bool zero = true;
                for (int g = 0; g < SpecGroups; g++) if (spG[gb + g] != Vector128<float>.Zero) zero = false;
                if (zero) { spNzOn[no] = false; for (int g = 0; g < SpecGroups; g++) spZ1[gb + g] = spZ2[gb + g] = spZ3[gb + g] = spZ4[gb + g] = default; }
            }
            else if (any) spNzOn[no] = true;
        }

        float ratio_(int o, int l) => ratio[o * MaxUnison + l];

        /// <summary>A voice starts: every partial at a place of its own in the cycle (from the voice's seed), silent.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void SpecStart(int note, int o, int l, int k)
        {
            var rng = new Rng(unchecked(seeds[note] * 2654435761u + (uint)(o * MaxUnison + l + 1) * 0x85EBCA6Bu) | 1u);
            int pb = k * MaxPartials, tb = k * SpecSlots;
            for (int j = 0; j < SpecSlots; j++)
            {
                SinCosTurns((float)rng.NextUnipolar(), out float s, out float c);
                hS[pb + j] = s;
                hC[pb + j] = c;
                hA[pb + j] = hDA[pb + j] = 0f;
                hRS[pb + j] = 0f;
                hRC[pb + j] = 1f;
                spTurns[tb + j] = -1f;
            }
            hBlocks[k] = 0;
            svGL[k] = svGR[k] = svDL[k] = svDR[k] = 0f;
        }

        /// <summary>A note's Spectral slot over <paramref name="count"/> samples into the engine buffers: the partials, then the noise.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void SpecVoices(int note, int o, int no, int count)
        {
            HarmVoices(note, o, no, count);
            int gb = no * SpecGroups;
            if (!spNzOn[no]) return;
            int ob = OvIndex(oversample) * SpecGroups;
            float pl = spPanL[no], pr = spPanR[no];
            const float scale = 1.7320508f / 2147483648f;
            for (int i = 0; i < count; i++)
            {
                var acc = Vector128<float>.Zero;
                for (int g = 0; g < SpecGroups; g++)
                {
                    var rv = spRngV[gb + g];
                    rv ^= Vector128.ShiftLeft(rv, 13);
                    rv ^= Vector128.ShiftRightLogical(rv, 17);
                    rv ^= Vector128.ShiftLeft(rv, 5);
                    spRngV[gb + g] = rv;
                    var vx = Vector128.ConvertToSingle(rv.AsInt32()) * Vector128.Create(scale);
                    var z1 = spZ1[gb + g];
                    var z2 = spZ2[gb + g];
                    var y = spB0[ob + g] * vx + z1;
                    spZ1[gb + g] = z2 - spA1[ob + g] * y;
                    spZ2[gb + g] = spB2[ob + g] * vx - spA2[ob + g] * y;
                    var z3 = spZ3[gb + g];
                    var z4 = spZ4[gb + g];
                    var w = spB0[ob + g] * y + z3;
                    spZ3[gb + g] = z4 - spA1[ob + g] * w;
                    spZ4[gb + g] = spB2[ob + g] * y - spA2[ob + g] * w;
                    y = w;
                    var gain = spG[gb + g] + spDG[gb + g];
                    spG[gb + g] = gain;
                    acc += gain * y;
                }
                float s = ((acc.GetElement(0) + acc.GetElement(1)) + acc.GetElement(2)) + acc.GetElement(3);
                exL[i] += pl * s;
                exR[i] += pr * s;
            }
            // A filter ringing down to nothing leaves denormals: flush the states that are negligible.
            var tiny = Vector128.Create(1e-20f);
            for (int g = 0; g < SpecGroups; g++)
            {
                var z1 = spZ1[gb + g];
                var z2 = spZ2[gb + g];
                spZ1[gb + g] = Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(z1), tiny), Vector128<float>.Zero, z1);
                spZ2[gb + g] = Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(z2), tiny), Vector128<float>.Zero, z2);
                var z3 = spZ3[gb + g];
                var z4 = spZ4[gb + g];
                spZ3[gb + g] = Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(z3), tiny), Vector128<float>.Zero, z3);
                spZ4[gb + g] = Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(z4), tiny), Vector128<float>.Zero, z4);
            }
        }
    }
}
