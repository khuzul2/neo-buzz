#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.8): the Modal engine. A slot is a resonating body: a bank of up to 64
// damped resonators (modes) at the frequencies a string, a tube, a bar, a drum membrane, a plate or a bell has, excited by
// a mallet, a pluck or a continuous noise (bow, breath). The material stretches the spacing (stiffness) and sets how fast
// the upper modes die; the ring time sets the lowest mode's decay; the body size scales the ring and which modes radiate.
//
// Like the Harmonic engine it renders beside the lanes (RenderExt): each mode is a two-pole resonator in coupled form,
// (x, y) turned by r·(cos θ, sin θ) every sample, four modes at a time in 128-bit vectors (element-wise multiplies and
// adds, no fused multiply-add; the four lanes of a sample added in a fixed order), so every platform gives the same bits.
// The rotations come from a polynomial sine and cosine and the decay from the portable exponential. A mode above 0.46 of
// the sample rate is never rendered (so the engine has no alias to measure); a state that is NaN, infinite or below 1e-18
// is flushed to zero each tick, so nothing can blow up or turn denormal. Everything is preallocated.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    /// <summary>How a Modal slot is excited (append only).</summary>
    internal enum ModalExciter { Struck, Plucked, Continuous, Morph }

    /// <summary>The bodies a Modal slot can be (append only).</summary>
    internal enum ModalGeometry { String, Tube, Bar, Membrane, Plate, Bell }

    /// <summary>The Modal engine: modes, rendered beside the lanes (nothing for the lanes to read).</summary>
    internal sealed class ModalEngine : IEngine
    {
        public TableArena TableOf(OscSettings os) => null;
        public float PositionOf(OscSettings os) => os.Position;
        public float DriftCents(OscSettings os, float r) => 0f;
    }

    /// <summary>The modes of each body: frequencies as multiples of the note, lowest first. Made by code from published values and portable maths.</summary>
    internal static class ModalBodies
    {
        public const int MaxModes = 64;
        public static readonly string[] Names = { "String", "Tube", "Bar", "Membrane", "Plate", "Bell" };

        /// <summary>How many modes each body has (the others are silent).</summary>
        public static readonly int[] Count;
        public static readonly float[][] Ratios;

        // Zeros of the Bessel functions J0 … J10 (a circular membrane's modes, fixed edge): j(m, n) / j(0, 1) are the ratios.
        static readonly double[][] Bessel =
        {
            new[] { 2.4048, 5.5201, 8.6537, 11.7915, 14.9309 },
            new[] { 3.8317, 7.0156, 10.1735, 13.3237, 16.4706 },
            new[] { 5.1356, 8.4172, 11.6198, 14.7960 },
            new[] { 6.3802, 9.7610, 13.0152, 16.2235 },
            new[] { 7.5883, 11.0647, 14.3725 },
            new[] { 8.7715, 12.3386, 15.7002 },
            new[] { 9.9361, 13.5893 },
            new[] { 11.0864, 14.8213 },
            new[] { 12.2251 },
            new[] { 13.3543 },
            new[] { 14.4755 },
        };

        // The wavenumbers of a free-free bar's first modes (β·L); the later ones are (n + ½)π.
        static readonly double[] BarBeta = { 4.7300407, 7.8532046, 10.9956078, 14.1371655, 17.2787597 };

        // A bell's partials (hum, prime, tierce, quint, nominal …) relative to its strike note, after the published measurements of tuned bells.
        static readonly float[] Bell = { 0.5f, 1f, 1.183f, 1.506f, 2f, 2.514f, 2.662f, 3.011f, 4.166f, 5.433f, 6.796f, 8.215f };

        static ModalBodies()
        {
            Ratios = new float[Names.Length][];
            Count = new int[Names.Length];
            for (int g = 0; g < Names.Length; g++)
            {
                var list = new double[MaxModes];
                int n = MaxModes;
                switch ((ModalGeometry)g)
                {
                    case ModalGeometry.String:
                        for (int j = 0; j < MaxModes; j++) list[j] = j + 1;
                        break;
                    case ModalGeometry.Tube:
                        for (int j = 0; j < MaxModes; j++) list[j] = 2 * j + 1;
                        break;
                    case ModalGeometry.Bar:
                        for (int j = 0; j < MaxModes; j++)
                        {
                            double b = j < BarBeta.Length ? BarBeta[j] : (j + 1.5) * Math.PI;
                            list[j] = b * b / (BarBeta[0] * BarBeta[0]);
                        }
                        break;
                    case ModalGeometry.Membrane:
                    {
                        var all = new double[40];
                        n = 0;
                        foreach (var row in Bessel) foreach (double z in row) all[n++] = z / Bessel[0][0];
                        Array.Sort(all, 0, n);
                        Array.Copy(all, list, n);
                        break;
                    }
                    case ModalGeometry.Plate:
                    {
                        // A simply supported rectangle, sides 1 : 1.37 (modes ∝ m² + (n / 1.37)²), sorted.
                        var all = new double[12 * 12];
                        int c = 0;
                        for (int m = 1; m <= 12; m++)
                            for (int k = 1; k <= 12; k++) all[c++] = m * m + (k / 1.37) * (k / 1.37);
                        Array.Sort(all);
                        for (int j = 0; j < MaxModes; j++) list[j] = all[j] / all[0];
                        break;
                    }
                    default:
                        n = Bell.Length;
                        for (int j = 0; j < n; j++) list[j] = Bell[j];
                        break;
                }
                Count[g] = n;
                Ratios[g] = new float[MaxModes];
                for (int j = 0; j < MaxModes; j++) Ratios[g][j] = j < n ? (float)list[j] : 1e6f;
            }
        }
    }

    internal sealed partial class NoteBank
    {
        /// <summary>Modes of a voice at most (a multiple of four).</summary>
        public const int MaxModes = ModalBodies.MaxModes;

        /// <summary>A slot's modes times its unison voices, at most, a note: more voices share that many (each voice at least <see cref="ModalFloor"/>).</summary>
        public const int ModalBudget = 256, ModalFloor = 8;

        // Per voice (note · MaxLanes + slot · MaxUnison + voice) and mode: the resonator state, its rotation and its input gain.
        readonly float[] mX = new float[Notes * MaxLanes * MaxModes], mY = new float[Notes * MaxLanes * MaxModes],
            mRC = new float[Notes * MaxLanes * MaxModes], mRS = new float[Notes * MaxLanes * MaxModes], mR = new float[Notes * MaxLanes * MaxModes], mCu = new float[Notes * MaxLanes * MaxModes], mSu = new float[Notes * MaxLanes * MaxModes], mTh = new float[Notes * MaxLanes * MaxModes], mG = new float[Notes * MaxLanes * MaxModes], mGB = new float[Notes * MaxLanes * MaxModes];
        readonly int[] mBlocks = new int[Notes * MaxLanes], mActG = new int[Notes * MaxLanes], mVer = new int[Notes * MaxLanes];
        readonly double[] mInc = new double[Notes * MaxLanes], mGInc = new double[Notes * MaxLanes];
        readonly bool[] mQuiet = new bool[Notes * MaxLanes];
        // Per slot: the spacing its settings give the modes (with the body's stiffness), the settings it was made for, a version of them.
        readonly float[] mRatio = new float[Oscillators * MaxModes];
        readonly float[] mKey = new float[Oscillators * KeySize];
        const int KeySize = 12;
        // P5b: a slot's modes from a sample's analysis (an impulse response): the table, the model, start and length it was made from, a version, whether it is in use,
        // and per mode (in the spacing's order, the strongest `modes` of them) its decay rate and gain (the strongest 1).
        readonly ImportedModes[] mImp = MakeModeImports();
        readonly SpectralModel[] mImpModel = new SpectralModel[Oscillators];
        readonly float[] mImpStart = new float[Oscillators], mImpLen = new float[Oscillators];
        readonly int[] mImpVer = new int[Oscillators];
        readonly bool[] mImpOn = new bool[Oscillators];
        readonly float[] mSigTab = new float[Oscillators * MaxModes], mGainTab = new float[Oscillators * MaxModes];

        static ImportedModes[] MakeModeImports()
        {
            var a = new ImportedModes[Oscillators];
            for (int i = 0; i < a.Length; i++) a[i] = new ImportedModes();
            return a;
        }

        /// <summary>Slot <paramref name="o"/>'s imported modes for this tick's settings (made again only when its model, start or length moved); false when the slot uses its geometry.</summary>
        bool ModeImpPrepare(int o, OscSettings os)
        {
            bool on = os.ModeOn && os.Spectrum != null;
            if (on)
            {
                float start = Math.Clamp(os.ModeStart, 0f, 1f), len = Math.Clamp(os.ModeLen, 0.05f, 2f);
                if (!ReferenceEquals(mImpModel[o], os.Spectrum) || mImpStart[o] != start || mImpLen[o] != len)
                {
                    mImp[o].Make(os.Spectrum, start, len);
                    mImpModel[o] = os.Spectrum;
                    mImpStart[o] = start;
                    mImpLen[o] = len;
                    mImpVer[o]++;
                }
                on = mImp[o].Count > 0;
            }
            mImpOn[o] = on;
            return on;
        }
        readonly int[] mCount = new int[Oscillators], mVersion = new int[Oscillators];
        readonly double[] mKeyVr = new double[Oscillators];
        // Per note and slot: the exciter's clock, pulse length, bow level and noise, and the settings of the last tick.
        readonly int[] mAge = new int[Notes * Oscillators], mLen = new int[Notes * Oscillators], mPer = new int[Notes * Oscillators], mExcKind = new int[Notes * Oscillators];
        readonly float[] mEnv = new float[Notes * Oscillators], mEnvUp = new float[Notes * Oscillators], mEnvDown = new float[Notes * Oscillators],
            mPos = new float[Notes * Oscillators], mForce = new float[Notes * Oscillators], mPrev = new float[Notes * Oscillators];
        readonly bool[] mHeld = new bool[Notes * Oscillators];
        readonly Rng[] mRng = new Rng[Notes * Oscillators];
        // A voice's weights while its gains are made.
        readonly float[] mU = new float[MaxModes], mT = new float[MaxModes], mF = new float[MaxModes];
        readonly float[] mExc = new float[MaxTick], mExcB = new float[MaxTick];
        // Neo Learn's exciter morph (docs/specs/neo-learn.md §3): per note and slot the shares of mallet, pluck and bow this tick, and per mode the pluck's and bow's weights.
        readonly float[] mWM = new float[Notes * Oscillators], mWP = new float[Notes * Oscillators], mWB = new float[Notes * Oscillators];
        readonly float[] mP = new float[MaxModes], mB = new float[MaxModes], mFB = new float[MaxModes];
        readonly Vector128<float>[] mAcc = new Vector128<float>[MaxTick];

        /// <summary>A slot's mode spacing for its settings (once, then when they change).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void ModalShape(int o, OscSettings os, double vr)
        {
            int g = Math.Clamp(os.Geometry, 0, ModalBodies.Names.Length - 1);
            int n = Math.Clamp(os.Modes, 1, MaxModes);
            float mat = Math.Clamp(os.Material, 0f, 1f), ring = Math.Clamp(os.Ring, 0.05f, 20f), hf = Math.Clamp(os.HfDamp, 0f, 1f), size = Math.Clamp(os.BodySize, 0f, 1f);
            int kb = o * KeySize;
            bool imp = mImpOn[o];
            float impVer = imp ? mImpVer[o] : -1f, impDecay = Math.Clamp(os.ModeDecay, 0.25f, 4f);
            // Neo Learn: the body may blend toward another (GeomBlend > 0), the exciters may morph (ExciterMorph >= 0).
            float blend = Math.Clamp(os.GeomBlend, 0f, 1f), morph = os.ExciterMorph < 0f ? -1f : Math.Min(os.ExciterMorph, 1f);
            int to = Math.Clamp(os.GeomTo, 0, ModalBodies.Names.Length - 1);
            if (mKey[kb] == g && mKey[kb + 1] == n && mKey[kb + 2] == mat && mKey[kb + 3] == ring && mKey[kb + 4] == hf && mKey[kb + 5] == size
                && mKey[kb + 6] == (float)os.Exciter && mKey[kb + 7] == morph && mKey[kb + 8] == blend && mKey[kb + 9] == to && mKey[kb + 10] == impVer && mKey[kb + 11] == impDecay && mKeyVr[o] == vr) return;
            mKey[kb] = g; mKey[kb + 1] = n; mKey[kb + 2] = mat; mKey[kb + 3] = ring; mKey[kb + 4] = hf; mKey[kb + 5] = size; mKey[kb + 6] = (float)os.Exciter;
            mKey[kb + 7] = morph; mKey[kb + 8] = blend; mKey[kb + 9] = to; mKey[kb + 10] = impVer; mKey[kb + 11] = impDecay;
            mKeyVr[o] = vr;
            mVersion[o]++;
            if (imp)
            {
                // P5b: the strongest n of the imported modes, in the order of their frequencies; each one's own decay (scaled) and gain (the strongest 1).
                var tab = mImp[o];
                int cnt = 0;
                float top = 1e-30f;
                for (int j = 0; j < tab.Count; j++) top = Math.Max(top, tab.Gain[j]);
                for (int j = 0; j < tab.Count; j++)
                {
                    if (tab.Rank[j] >= n) continue;
                    mRatio[o * MaxModes + cnt] = tab.Ratio[j];
                    mSigTab[o * MaxModes + cnt] = tab.Sigma[j] / impDecay;
                    mGainTab[o * MaxModes + cnt] = tab.Gain[j] / top;
                    cnt++;
                }
                mCount[o] = cnt;
                for (int j = cnt; j < MaxModes; j++) mRatio[o * MaxModes + j] = 1e6f;
                return;
            }
            mCount[o] = Math.Min(n, ModalBodies.Count[g]);
            if (blend > 0f) mCount[o] = Math.Min(mCount[o], ModalBodies.Count[to]);
            // Stiffness stretches the spacing: mode j sounds at ratio · √(1 + B · (j + 1)²), B growing with the hardness squared.
            float b = 0.003f * mat * mat;
            var ratios = ModalBodies.Ratios[g];
            var ratiosTo = ModalBodies.Ratios[to];
            for (int j = 0; j < MaxModes; j++)
            {
                float jj = j + 1;
                float rj = blend > 0f ? ratios[j] + blend * (ratiosTo[j] - ratios[j]) : ratios[j];
                mRatio[o * MaxModes + j] = j < mCount[o] ? rj * MathF.Sqrt(1f + b * jj * jj) : 1e6f;
            }
        }

        void ModalInit(int note, int o, int no)
        {
            mAge[no] = 0;
            mLen[no] = 0;
            mEnv[no] = 0f;
            mPrev[no] = 0f;
            mPos[no] = mForce[no] = -1f;
            mPer[no] = 0;
            mRng[no] = new Rng(unchecked(seeds[note] * 2654435761u + (uint)(o + 1) * 0xC2B2AE35u) | 1u);
            int vb = note * MaxLanes + o * MaxUnison;
            for (int l = 0; l < MaxUnison; l++) ModalStart(vb + l);
        }

        /// <summary>A voice starts silent: no state, no rotation, nothing made for it yet.</summary>
        void ModalStart(int k)
        {
            int pb = k * MaxModes;
            Array.Clear(mX, pb, MaxModes);
            Array.Clear(mY, pb, MaxModes);
            Array.Clear(mRC, pb, MaxModes);
            Array.Clear(mRS, pb, MaxModes);
            Array.Clear(mG, pb, MaxModes);
            Array.Clear(mGB, pb, MaxModes);
            Array.Clear(mR, pb, MaxModes);
            Array.Clear(mCu, pb, MaxModes);
            Array.Clear(mSu, pb, MaxModes);
            Array.Clear(mTh, pb, MaxModes);
            mInc[k] = mGInc[k] = double.NaN;
            mActG[k] = 0;
            mVer[k] = -1;
            mBlocks[k] = 0;
            mQuiet[k] = false;
            svGL[k] = svGR[k] = svDL[k] = svDR[k] = 0f;
        }

        /// <summary>This tick's modes of a note's Modal slot: their rotations, decays and gains (when something changed), and the voices' own gains.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void ModalControl(int note, int o, int no, OscSettings os, int u, double inc0, double vr, float invCount)
        {
            bool imp = ModeImpPrepare(o, os);
            ModalShape(o, os, vr);
            int sb = o * MaxModes;
            bool morph = os.ExciterMorph >= 0f;
            int exc = morph ? (int)ModalExciter.Morph : Math.Clamp((int)os.Exciter, 0, 2);
            // The morph's shares: an equal-power crossfade, mallet to pluck over the first half, pluck to bow over the second.
            float wM = 0f, wP = 0f, wB = 0f;
            if (morph)
            {
                float mm = Math.Min(os.ExciterMorph, 1f);
                float tt = mm < 0.5f ? 2f * mm : 2f * mm - 1f;
                SinCosTurns(0.25f * tt, out float ws, out float wc);
                if (mm < 0.5f) { wM = wc; wP = ws; } else { wP = wc; wB = ws; }
                if (wM < 1e-3f) wM = 0f;
                if (wP < 1e-3f) wP = 0f;
                if (wB < 1e-3f) wB = 0f;
            }
            mWM[no] = wM; mWP[no] = wP; mWB[no] = wB;
            int per = Math.Min(mCount[o], Math.Max(ModalFloor, ModalBudget / u));
            per = Math.Min((per + 3) & ~3, MaxModes);
            float pos = Math.Clamp(OscPosition(o) + Md(note, RouteDest.APosition + o), 0f, 1f);
            // A harder strike or pressure for a harder key.
            float force = Math.Clamp(os.Force * (0.6f + 0.8f * noteVel[note]), 0f, 1f);
            float mat = Math.Clamp(os.Material, 0f, 1f), ring = Math.Clamp(os.Ring, 0.05f, 20f), hf = Math.Clamp(os.HfDamp, 0f, 1f), size = Math.Clamp(os.BodySize, 0f, 1f);
            bool slotChanged = pos != mPos[no] || force != mForce[no] || per != mPer[no] || exc != mExcKind[no];
            mPos[no] = pos;
            mForce[no] = force;
            mPer[no] = per;
            mExcKind[no] = exc;
            mHeld[no] = gate[note];
            // The mallet's contact time (8 ms soft … 0.2 ms hard); the bow's attack and release.
            // The mallet's force on the body is a half-sine pulse of unit area; the modes are driven by its first difference (a velocity), so the
            // body does not swell with the load while the pulse lasts. mLen is the pulse's length plus the one sample the difference adds.
            if (mAge[no] == 0) mLen[no] = Math.Max(2, (int)(0.008 * PortableMath.Exp(-3.6889 * force) * vr)) + 1;
            mEnvUp[no] = (float)(1.0 / (vr * (0.02 + 0.25 * (1f - force) * (1f - force))));
            mEnvDown[no] = (float)(1.0 / (vr * 0.03));
            // The excitation's reach along the body: 0 at an end (every mode, bright) … 100 % at the middle (even modes of a string vanish).
            float pp = 0.02f + 0.48f * pos;
            // How the body radiates (a corner frequency from 8 kHz for a small body to 500 Hz for a large one), its loss and its ring.
            double fc = 2000.0 * PortableMath.Exp2(-4.0 * (size - 0.5));
            double ringSeconds = ring * PortableMath.Exp2(1.5 * (size - 0.5));
            double sigma0 = 6.9078 / ringSeconds;
            double h = 24.0 * hf * (1.0 - 0.8 * mat);
            float hi = 0.46f / oversample, fadeWidth = 0.06f / oversample;
            double bowCorner = 300.0 * PortableMath.Exp(4.0943 * force);
            int vb = note * MaxLanes + o * MaxUnison;
            int count = Math.Max(u, extPrevU[no]);
            for (int l = 0; l < count; l++)
            {
                int k = vb + l;
                bool live = l < u;
                int pb = k * MaxModes;
                if (live)
                {
                    bool fresh = l >= extPrevU[no];
                    if (fresh) ModalStart(k);
                    double inc = inc0 * ratio[o * MaxUnison + l];
                    // A pitch that moved by under 3 % (half a semitone: vibrato, glide) turns the modes but keeps their decays and gains.
                    bool major = fresh || slotChanged || mVer[k] != mVersion[o] || !(Math.Abs(inc - mGInc[k]) <= 0.03 * mGInc[k]);
                    if (major || inc != mInc[k])
                    {
                        int active = 0;
                        while (active < per && inc * mRatio[sb + active] < hi) active++;
                        if (active > mActG[k]) major = true;
                        int blocks = (active + 3) >> 2, lim = blocks * 4;
                        // Modes that left the band are dropped; those past the new edge forget what they held.
                        if (blocks < mBlocks[k])
                        {
                            Array.Clear(mX, pb + lim, MaxModes - lim);
                            Array.Clear(mY, pb + lim, MaxModes - lim);
                        }
                        mBlocks[k] = blocks;
                        mInc[k] = inc;
                        if (!major)
                        {
                            Retune(pb, blocks, (float)(inc / mGInc[k] - 1.0));
                            goto voiceDone;
                        }
                        mGInc[k] = inc;
                        mActG[k] = active;
                        mVer[k] = mVersion[o];
                        bool needG = fresh || exc == (int)ModalExciter.Continuous || exc == (int)ModalExciter.Struck && mAge[no] < mLen[no]
                            || exc == (int)ModalExciter.Morph && (wB > 0f || wM > 0f && mAge[no] < mLen[no]);
                        double e2 = 0, e2m = 0, e2me = 0, e2p = 0, e2b = 0;
                        for (int j = 0; j < lim; j++)
                        {
                            float turns = (float)(inc * mRatio[sb + j]);
                            bool on = j < active;
                            double hz = turns * vr;
                            double sigma = imp ? mSigTab[sb + j] : sigma0 * (1.0 + h * (hz * 0.001) * (hz * 0.001));
                            float r = on ? (float)PortableMath.Exp(-sigma / vr) : 0f;
                            SinCosTurns(turns, out float rs, out float rc);
                            mR[pb + j] = r;
                            mCu[pb + j] = rc;
                            mSu[pb + j] = rs;
                            mTh[pb + j] = turns * 6.2831855f;
                            mRC[pb + j] = r * rc;
                            mRS[pb + j] = r * rs;
                            if (!needG) continue;
                            // The weight of mode j: where the body is excited (a comb), how well it radiates.
                            SinCosTurns((j + 1) * pp * 0.5f, out float pc, out _);
                            float wgt = on ? (imp ? mGainTab[sb + j] : MathF.Abs(pc) / MathF.Sqrt(1f + (float)(hz / fc) * (float)(hz / fc))) : 0f;
                            float fade = on ? Math.Clamp((hi - turns) / fadeWidth, 0f, 1f) : 0f;
                            mF[j] = fade;
                            mU[j] = wgt;
                            if (exc == (int)ModalExciter.Morph)
                            {
                                // Each exciter's weights as its own mode would have them (below), crossfaded by the shares after the loop.
                                if (wM > 0f && needG)
                                {
                                    double x = turns * (mLen[no] - 1);
                                    double den = 1.0 - 4.0 * x * x;
                                    SinCosTurns((float)(x * 0.5), out _, out float cx);
                                    float mag = Math.Abs(den) < 1e-3 ? 0.7854f : MathF.Abs(cx) / (float)Math.Abs(den);
                                    SinCosTurns(turns * 0.5f, out float sh, out _);
                                    float dsin = Math.Max(2f * MathF.Abs(sh), 1e-4f);
                                    float inv = imp ? 1f : 1f / mRatio[sb + j];
                                    mT[j] = wgt * mag * inv;
                                    mU[j] = wgt * inv / dsin;
                                    e2m += (double)mT[j] * mT[j];
                                    // The pulse's envelope (no nulls): the decay of a ringing mode fills a null in, so a sum that fell into nulls would be levelled up too far.
                                    double env = (Math.Abs(den) < 1e-3 ? 0.7854 : 1.0 / Math.Abs(den)) * wgt * inv;
                                    e2me += env * env;
                                }
                                if (wP > 0f && fresh)
                                {
                                    double pexp = 2.0 - 1.4 * force;
                                    mP[j] = imp ? wgt : wgt / (float)PortableMath.Exp(pexp * PortableMath.Log(j + 1.0));
                                    e2p += (double)mP[j] * mP[j];
                                }
                                if (wB > 0f)
                                {
                                    float shape = 1f / MathF.Sqrt(1f + (float)(hz / bowCorner) * (float)(hz / bowCorner));
                                    mB[j] = wgt * shape;
                                    mFB[j] = fade * (float)Math.Sqrt(4.0 * sigma / vr) * 1.7320508f;
                                    e2b += (double)mB[j] * mB[j];
                                }
                                continue;
                            }
                            if (exc == (int)ModalExciter.Struck)
                            {
                                // The half-sine mallet's spectrum at this mode: |cos(πx) / (1 − 4x²)| with x = frequency · contact time.
                                double x = turns * (mLen[no] - 1);
                                double den = 1.0 - 4.0 * x * x;
                                SinCosTurns((float)(x * 0.5), out _, out float cx);
                                float mag = Math.Abs(den) < 1e-3 ? 0.7854f : MathF.Abs(cx) / (float)Math.Abs(den);
                                // The first difference scales mode j by 2 sin(π · turns); the displacement of a struck mode falls with its frequency.
                                SinCosTurns(turns * 0.5f, out float sh, out _);
                                float dsin = Math.Max(2f * MathF.Abs(sh), 1e-4f);
                                float inv = imp ? 1f : 1f / mRatio[sb + j];
                                mT[j] = wgt * mag * inv;
                                mU[j] = wgt * inv / dsin;
                            }
                            else if (exc == (int)ModalExciter.Plucked)
                            {
                                // A released displacement: the harder the pluck, the flatter the fall with the mode number.
                                double pexp = 2.0 - 1.4 * force;
                                mT[j] = imp ? wgt : wgt / (float)PortableMath.Exp(pexp * PortableMath.Log(j + 1.0));
                            }
                            else
                            {
                                // Noise through the bow's pressure: a corner on the force.
                                float shape = 1f / MathF.Sqrt(1f + (float)(hz / bowCorner) * (float)(hz / bowCorner));
                                mT[j] = wgt * shape;
                                mF[j] = fade * (float)Math.Sqrt(4.0 * sigma / vr) * 1.7320508f;
                            }
                            e2 += (double)mT[j] * mT[j];
                        }
                        if (needG && exc == (int)ModalExciter.Morph)
                        {
                            // Each exciter is levelled as it is alone (the mallet and the pluck 0.99; the bow lower than Dye's own bow, so the crossfade keeps one loudness), then shared out.
                            // (A mallet whose pulse has a null at the lowest modes would be levelled up by 20 dB: the floor is a share of the no-null energy.)
                            float normM = e2m > 1e-18 ? 0.99f / MathF.Sqrt((float)Math.Max(e2m, MalletNullFloor * e2me)) : 0f;
                            float normP = e2p > 1e-18 ? 0.99f / MathF.Sqrt((float)e2p) : 0f;
                            float normB = e2b > 1e-18 ? BowLevel / MathF.Sqrt((float)e2b) : 0f;
                            for (int j = 0; j < lim; j++)
                            {
                                mG[pb + j] = wM > 0f ? wM * mU[j] * mF[j] * normM : 0f;
                                mGB[pb + j] = wB > 0f ? wB * mB[j] * normB * mFB[j] : 0f;
                            }
                            if (fresh && wP > 0f)
                                for (int j = 0; j < lim; j++) { mY[pb + j] = wP * mP[j] * normP * mF[j]; mX[pb + j] = 0f; }
                        }
                        else if (needG)
                        {
                            // Loudness: the sum of the modes' squared amplitudes is the same whatever the body, count or force (0.7 rms for a voice).
                            if (exc == (int)ModalExciter.Continuous)
                            {
                                float norm = e2 > 1e-18 ? 0.7f / MathF.Sqrt((float)e2) : 0f;
                                for (int j = 0; j < lim; j++) mG[pb + j] = mT[j] * norm * mF[j];
                            }
                            else
                            {
                                // The mallet's gain is per unit of pulse area (the pulse spectrum is inside mT); a pluck's is the starting displacement.
                                float norm = e2 > 1e-18 ? 0.99f / MathF.Sqrt((float)e2) : 0f;
                                if (exc == (int)ModalExciter.Struck)
                                    for (int j = 0; j < lim; j++) mG[pb + j] = mU[j] * mF[j] * norm;
                                else if (fresh)
                                    for (int j = 0; j < lim; j++) { mY[pb + j] = mT[j] * norm * mF[j]; mX[pb + j] = 0f; }
                            }
                        }
                    }
                }
            voiceDone:
                float tl = live ? 1f / MathF.Sqrt(u) * panL[o * MaxUnison + l] : 0f, tr = live ? 1f / MathF.Sqrt(u) * panR[o * MaxUnison + l] : 0f;
                svDL[k] = (tl - svGL[k]) * invCount;
                svDR[k] = (tr - svGR[k]) * invCount;
                if (!live && svGL[k] == 0f && svGR[k] == 0f) svDL[k] = svDR[k] = 0f;
                svOn[k] = live || svGL[k] != 0f || svGR[k] != 0f;
            }
            extVoices[no] = count;
            extPrevU[no] = u;
        }

        /// <summary>
        /// A voice's pitch moved by <paramref name="d"/> (a fraction, under 3 %) from where its modes were last made: each mode's rotation is
        /// that made then, turned by the small angle the move adds (a short series for its sine and cosine), four modes at a time. Always from
        /// the made one, so nothing accumulates however long a vibrato runs.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Retune(int pb, int blocks, float d)
        {
            var dv = Vector128.Create(d);
            var one = Vector128.Create(1f);
            for (int b = 0; b < blocks; b++)
            {
                int at = pb + 4 * b;
                var delta = Vector128.LoadUnsafe(ref mTh[at]) * dv;
                var d2 = delta * delta;
                var sd = delta * (one + d2 * (Vector128.Create(-1f / 6f) + d2 * Vector128.Create(1f / 120f)));
                var cd = one + d2 * (Vector128.Create(-0.5f) + d2 * (Vector128.Create(1f / 24f) + d2 * Vector128.Create(-1f / 720f)));
                var c0 = Vector128.LoadUnsafe(ref mCu[at]);
                var s0 = Vector128.LoadUnsafe(ref mSu[at]);
                var r = Vector128.LoadUnsafe(ref mR[at]);
                (r * (c0 * cd - s0 * sd)).StoreUnsafe(ref mRC[at]);
                (r * (s0 * cd + c0 * sd)).StoreUnsafe(ref mRS[at]);
            }
        }

        /// <summary>The exciter's signal for the next <paramref name="count"/> samples into <see cref="mExc"/> (the same for every voice of the slot). Whether there is any.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        bool ModalExcite(int no, int count)
        {
            int exc = mExcKind[no];
            if (exc == (int)ModalExciter.Struck)
            {
                int age = mAge[no], len = mLen[no];
                if (age >= len) return false;
                int pulse = len - 1;
                SinCosTurns(0.25f / pulse, out float scale, out _);   // sin(π / 2L): the pulse's samples sum to 1 / scale
                float prev = mPrev[no];
                for (int i = 0; i < count; i++)
                {
                    int n = age + i;
                    float p = 0f;
                    if (n < pulse)
                    {
                        SinCosTurns((n + 0.5f) / (2f * pulse), out float s, out _);
                        p = s * scale;
                    }
                    mExc[i] = p - prev;
                    prev = p;
                }
                mPrev[no] = prev;
                mAge[no] = age + count;
                return true;
            }
            if (exc == (int)ModalExciter.Morph)
            {
                // Both drives, each zero when it has nothing to say: the mallet's pulse (while it lasts) and the bow's noise (while the key is held or it dies away).
                bool any = false;
                if (mWM[no] > 0f && mAge[no] < mLen[no])
                {
                    int age = mAge[no], len = mLen[no], pulse = len - 1;
                    SinCosTurns(0.25f / pulse, out float scale, out _);
                    float prev = mPrev[no];
                    for (int i = 0; i < count; i++)
                    {
                        int n = age + i;
                        float p = 0f;
                        if (n < pulse)
                        {
                            SinCosTurns((n + 0.5f) / (2f * pulse), out float s, out _);
                            p = s * scale;
                        }
                        mExc[i] = p - prev;
                        prev = p;
                    }
                    mPrev[no] = prev;
                    mAge[no] = age + count;
                    any = true;
                }
                else
                {
                    Array.Clear(mExc, 0, count);
                    if (mAge[no] < mLen[no]) mAge[no] += count;
                }
                bool bow = false;
                if (mWB[no] > 0f)
                {
                    float lvl = mEnv[no], up = mEnvUp[no], down = mEnvDown[no];
                    bool held = mHeld[no];
                    if (held || lvl != 0f)
                    {
                        var rng = mRng[no];
                        for (int i = 0; i < count; i++)
                        {
                            lvl = held ? Math.Min(1f, lvl + up) : Math.Max(0f, lvl - down);
                            mExcB[i] = lvl * rng.NextBipolar();
                        }
                        mRng[no] = rng;
                        mEnv[no] = lvl;
                        bow = true;
                    }
                }
                if (!bow) Array.Clear(mExcB, 0, count);
                return any || bow;
            }
            if (exc == (int)ModalExciter.Continuous)
            {
                float lvl = mEnv[no], up = mEnvUp[no], down = mEnvDown[no];
                bool held = mHeld[no];
                if (!held && lvl == 0f) return false;
                var rng = mRng[no];
                for (int i = 0; i < count; i++)
                {
                    lvl = held ? Math.Min(1f, lvl + up) : Math.Max(0f, lvl - down);
                    mExc[i] = lvl * rng.NextBipolar();
                }
                mRng[no] = rng;
                mEnv[no] = lvl;
                return true;
            }
            return false;
        }

        /// <summary>Nothing blows up and nothing turns denormal: NaN, infinities and the vanishingly small become zero. Stores the block; whether anything is left.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool Flush(ref Vector128<float> x, ref Vector128<float> y, float[] sx, float[] sy, int at, Vector128<float> floor, Vector128<float> ceil, Vector128<float> zero)
        {
            var ax = Vector128.Abs(x);
            x = Vector128.ConditionalSelect(Vector128.GreaterThan(ax, floor) & Vector128.LessThan(ax, ceil), x, zero);
            var ay = Vector128.Abs(y);
            y = Vector128.ConditionalSelect(Vector128.GreaterThan(ay, floor) & Vector128.LessThan(ay, ceil), y, zero);
            x.StoreUnsafe(ref sx[at]);
            y.StoreUnsafe(ref sy[at]);
            return !Vector128.EqualsAll(x, zero) || !Vector128.EqualsAll(y, zero);
        }

        /// <summary>The morph's bow level (Dye's own bow is 0.7 rms; the morph's stands lower so its loudest 50 ms matches the mallet's and the pluck's).</summary>
        const float BowLevel = 0.45f;
        /// <summary>The share of the no-null pulse energy the morph's mallet levelling never goes below (a quarter in amplitude is 1/16, an eighth in energy is what a few nulls cost).</summary>
        const double MalletNullFloor = 0.12;

        /// <summary>The morph's voice: the modes' sum into the accumulators with two drives (the mallet's pulse through <see cref="mG"/>, the bow's noise through <see cref="mGB"/>). Whether anything still rings.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        bool MorphBlocks(int pb, int blocks, int count, bool excited)
        {
            var acc = mAcc;
            var zero = Vector128<float>.Zero;
            var floor = Vector128.Create(1e-18f);
            var ceil = Vector128.Create(1e6f);
            bool alive = false;
            for (int bb = 0; bb < blocks; bb++)
            {
                int at = pb + 4 * bb;
                var x = Vector128.LoadUnsafe(ref mX[at]);
                var y = Vector128.LoadUnsafe(ref mY[at]);
                var rc = Vector128.LoadUnsafe(ref mRC[at]);
                var rs = Vector128.LoadUnsafe(ref mRS[at]);
                if (excited)
                {
                    var g = Vector128.LoadUnsafe(ref mG[at]);
                    var gb = Vector128.LoadUnsafe(ref mGB[at]);
                    for (int i = 0; i < count; i++)
                    {
                        acc[i] += y;
                        var nx = rc * x - rs * y + g * Vector128.Create(mExc[i]) + gb * Vector128.Create(mExcB[i]);
                        y = rs * x + rc * y;
                        x = nx;
                    }
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        acc[i] += y;
                        var nx = rc * x - rs * y;
                        y = rs * x + rc * y;
                        x = nx;
                    }
                }
                alive |= Flush(ref x, ref y, mX, mY, at, floor, ceil, zero);
            }
            return alive;
        }

        /// <summary>A note's Modal slot over <paramref name="count"/> samples into the engine buffers (voice by voice, four modes at a time).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void ModalVoices(int note, int o, int no, int count)
        {
            int vb = note * MaxLanes + o * MaxUnison;
            var acc = mAcc;
            bool excited = ModalExcite(no, count);
            bool morph = mExcKind[no] == (int)ModalExciter.Morph;
            var zero = Vector128<float>.Zero;
            var floor = Vector128.Create(1e-18f);
            var ceil = Vector128.Create(1e6f);
            for (int v = 0; v < extVoices[no]; v++)
            {
                int k = vb + v;
                if (!svOn[k] || mQuiet[k] && !excited) continue;
                // Neo Learn's morph runs its own loops (two drives); the others' loops below then see no blocks.
                int blocks = morph ? 0 : mBlocks[k], pb = k * MaxModes;
                Array.Clear(acc, 0, count);
                bool alive = false;
                int bb = 0;
                if (morph) alive = MorphBlocks(pb, mBlocks[k], count, excited);
                // Two blocks (eight modes) at a time keep the sample's sum in registers: (first four + second four), then added to the sum.
                for (; bb + 1 < blocks; bb += 2)
                {
                    int at = pb + 4 * bb, at2 = at + 4;
                    var x = Vector128.LoadUnsafe(ref mX[at]);
                    var y = Vector128.LoadUnsafe(ref mY[at]);
                    var rc = Vector128.LoadUnsafe(ref mRC[at]);
                    var rs = Vector128.LoadUnsafe(ref mRS[at]);
                    var x2 = Vector128.LoadUnsafe(ref mX[at2]);
                    var y2 = Vector128.LoadUnsafe(ref mY[at2]);
                    var rc2 = Vector128.LoadUnsafe(ref mRC[at2]);
                    var rs2 = Vector128.LoadUnsafe(ref mRS[at2]);
                    if (excited)
                    {
                        var g = Vector128.LoadUnsafe(ref mG[at]);
                        var g2 = Vector128.LoadUnsafe(ref mG[at2]);
                        for (int i = 0; i < count; i++)
                        {
                            var e = Vector128.Create(mExc[i]);
                            acc[i] += y + y2;
                            var nx = rc * x - rs * y + g * e;
                            y = rs * x + rc * y;
                            x = nx;
                            var nx2 = rc2 * x2 - rs2 * y2 + g2 * e;
                            y2 = rs2 * x2 + rc2 * y2;
                            x2 = nx2;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < count; i++)
                        {
                            acc[i] += y + y2;
                            var nx = rc * x - rs * y;
                            y = rs * x + rc * y;
                            x = nx;
                            var nx2 = rc2 * x2 - rs2 * y2;
                            y2 = rs2 * x2 + rc2 * y2;
                            x2 = nx2;
                        }
                    }
                    alive |= Flush(ref x, ref y, mX, mY, at, floor, ceil, zero);
                    alive |= Flush(ref x2, ref y2, mX, mY, at2, floor, ceil, zero);
                }
                for (; bb < blocks; bb++)
                {
                    int at = pb + 4 * bb;
                    var x = Vector128.LoadUnsafe(ref mX[at]);
                    var y = Vector128.LoadUnsafe(ref mY[at]);
                    var rc = Vector128.LoadUnsafe(ref mRC[at]);
                    var rs = Vector128.LoadUnsafe(ref mRS[at]);
                    if (excited)
                    {
                        var g = Vector128.LoadUnsafe(ref mG[at]);
                        for (int i = 0; i < count; i++)
                        {
                            acc[i] += y;
                            var nx = rc * x - rs * y + g * Vector128.Create(mExc[i]);
                            y = rs * x + rc * y;
                            x = nx;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < count; i++)
                        {
                            acc[i] += y;
                            var nx = rc * x - rs * y;
                            y = rs * x + rc * y;
                            x = nx;
                        }
                    }
                    alive |= Flush(ref x, ref y, mX, mY, at, floor, ceil, zero);
                }
                mQuiet[k] = !alive;
                float gl = svGL[k], gr = svGR[k], sl = svDL[k], sr = svDR[k];
                for (int i = 0; i < count; i++)
                {
                    var q = acc[i];
                    float s = ((q.GetElement(0) + q.GetElement(1)) + q.GetElement(2)) + q.GetElement(3);
                    gl += sl;
                    gr += sr;
                    exL[i] += gl * s;
                    exR[i] += gr * s;
                }
                svGL[k] = gl;
                svGR[k] = gr;
            }
        }
    }
}
