#nullable disable
// The String slot type (Neo Learn L2a, docs/specs/neo-learn.md §3 and §9): Karplus-Strong. A delay line that is the string, a loop that
// loses a little each time round, and a pluck written into it at note-on. A voice is one line:
//   read the delay line -> fractional-delay all-pass (tunes it) -> one-pole low-pass (damping: the treble dies first)
//   -> dispersion all-pass(es) (stiffness: high partials run ahead of the low ones) -> loop gain -> back into the line.
// The loop's own delay (low-pass and all-pass phase delay at the pitch, worked out exactly) comes out of the line's length, so the note is in tune.
// Decay is a time, not a coefficient: the loop gain is 0.001^(period / T60) divided by the low-pass's gain at the pitch, so the fundamental falls
// 60 dB in T60 seconds at any note. The pluck is a burst of noise (its length and its brightness are the pick's hardness) with a comb notch
// from the pick position, written over the whole line. Two sympathetic strings a note (a octave and a fifth up) are fed the sum of the note's
// strings: they ring only what matches them. A slot is a String slot when OscSettings.TypeExt says so (SlotType.String); Dye Essence's own
// controls never reach it, so nothing of Dye Essence changes.
//
// Audio thread: nothing here allocates (the lines are made by PrepareStrings, off it). Every loop is guarded: NaN, infinities and anything denormal
// become zero in the line, and the filters' states are flushed at the end of each block.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    internal sealed class StringEngine : IEngine
    {
        public TableArena TableOf(OscSettings os) => null;
        public float PositionOf(OscSettings os) => os.Position;
        public float DriftCents(OscSettings os, float r) => 0f;
    }

    internal sealed partial class NoteBank
    {
        /// <summary>Strings a note plays at most (the unison voices), and the sympathetic strings a note has.</summary>
        public const int StringVoices = 5, SympatheticLines = 2;

        /// <summary>The lowest pitch a line is long enough for (Hz).</summary>
        public const double StringLowestHz = 30.0;

        const int StringMainLines = Notes * StringVoices, StringLineCount = Notes * (StringVoices + SympatheticLines);

        float[][] sBuf;
        int sMask;
        float[] sTmp;
        // Per line: the write position, delay (whole samples), fractional all-pass, damping, loop gain, dispersion coefficient, and the states.
        readonly int[] sW = new int[StringLineCount], sD = new int[StringLineCount];
        readonly float[] sA = new float[StringLineCount], sLa = new float[StringLineCount], sG = new float[StringLineCount], sC = new float[StringLineCount];
        readonly float[] sFx = new float[StringLineCount], sFy = new float[StringLineCount], sLp = new float[StringLineCount];
        readonly float[] sX1 = new float[2 * StringLineCount], sY1 = new float[2 * StringLineCount];
        // What each line was made for (hz, T60, damping, stiffness), so a steady note costs no trigonometry.
        readonly double[] sKeyHz = new double[StringLineCount];
        readonly float[] sKeyT = new float[StringLineCount], sKeyD = new float[StringLineCount], sKeyS = new float[StringLineCount];
        readonly double[] sKeyVr = new double[StringLineCount];
        readonly bool[] sLive = new bool[StringLineCount];
        // Samples in a row a line has been silent: a burst that is not on the stretch of the loop being written is not a dead string; a whole trip of silence is.
        readonly int[] sQuiet = new int[StringLineCount];
        // Per note: the sympathetic strings' level (now and the step), whether they are on, and the sum of the note's strings (the strings' input).
        readonly float[] sSympLvl = new float[Notes], sSympStep = new float[Notes];
        readonly bool[] sSympOn = new bool[Notes];
        readonly float[] sMix = new float[MaxTick * 4], sOut = new float[MaxTick * 4], sSymOut = new float[MaxTick * 4];

        /// <summary>The String engine's delay lines, for a voice rate (off the audio thread). Dye Essence never needs them; a map that plays String slots asks.</summary>
        public void PrepareStrings(double voiceRate)
        {
            int need = 1;
            int len = (int)(voiceRate / StringLowestHz) + 16;
            while (need < len) need <<= 1;
            if (sBuf != null && sBuf[0].Length >= need) return;
            sBuf = new float[StringLineCount][];
            for (int i = 0; i < sBuf.Length; i++) sBuf[i] = new float[need];
            sMask = need - 1;
            sTmp = new float[need];
        }

        /// <summary>A String slot's note starts: its strings start silent, to be plucked by the first control pass.</summary>
        void StringInit(int note, int o, int no)
        {
            extFresh[no] = false;
            int lb = note * StringVoices;
            for (int l = 0; l < StringVoices; l++) { sLive[lb + l] = false; sKeyHz[lb + l] = 0; }
            int sb = StringMainLines + note * SympatheticLines;
            for (int j = 0; j < SympatheticLines; j++) { sLive[sb + j] = false; sKeyHz[sb + j] = 0; }
            sSympLvl[note] = 0f;
            sSympOn[note] = false;
            int vb = note * MaxLanes + o * MaxUnison;
            for (int l = 0; l < MaxUnison; l++) mQuiet[vb + l] = true;
        }

        /// <summary>Phase delay (samples) of the all-pass (c + z^-1) / (1 + c z^-1) at the angle w (radians per sample, 0 &lt; w &lt; π).</summary>
        static double AllpassDelay(double c, double w)
        {
            double s = PortableMath.Sin(w), co = PortableMath.Cos(w);
            double arg = PortableMath.Atan2(-s, c + co) - PortableMath.Atan2(-c * s, 1 + c * co);
            return -arg / w;
        }

        /// <summary>Phase delay (samples) of the one-pole low-pass (1 - a) / (1 - a z^-1) at w.</summary>
        static double LowpassDelay(double a, double w) => PortableMath.Atan2(a * PortableMath.Sin(w), 1 - a * PortableMath.Cos(w)) / w;

        /// <summary>The low-pass's gain at w.</summary>
        static double LowpassGain(double a, double w) => (1 - a) / Math.Sqrt(1 - 2 * a * PortableMath.Cos(w) + a * a);

        /// <summary>Makes a line's coefficients for a pitch: the damping, the dispersion, the loop gain from T60, and the delay with the loop's own delay taken out.</summary>
        void StringCoefs(int line, double hz, double vr, float t60, float damp, float stiff)
        {
            if (sKeyHz[line] == hz && sKeyT[line] == t60 && sKeyD[line] == damp && sKeyS[line] == stiff && sKeyVr[line] == vr) return;
            sKeyHz[line] = hz; sKeyT[line] = t60; sKeyD[line] = damp; sKeyS[line] = stiff; sKeyVr[line] = vr;
            hz = Math.Clamp(hz, StringLowestHz, vr * 0.45);
            double w0 = 2.0 * Math.PI * hz / vr;
            double period = vr / hz;
            double la = 0.92 * PortableMath.Pow(Math.Clamp(damp, 0f, 1f), 1.5);
            {
                // The low-pass never takes more than a fifth off the fundamental, so the decay time can still be kept (a high note with heavy damping is dark, not dead).
                double cw = PortableMath.Cos(w0), bq = 2 - 1.28 * cw;
                double amax = (bq - Math.Sqrt(Math.Max(0, bq * bq - 4 * 0.36 * 0.36))) / 0.72;
                if (la > amax) la = amax;
            }
            double lpDelay = la > 0 ? LowpassDelay(la, w0) : 0;
            double gLp = la > 0 ? LowpassGain(la, w0) : 1.0;
            // Stiffness: two all-passes with a negative coefficient (the delay falls with frequency). Scaled down while they would eat the line.
            double c = -0.95 * Math.Clamp(stiff, 0f, 1f);
            double apDelay = 0;
            for (int tries = 0; tries < 6; tries++)
            {
                apDelay = c != 0 ? 2 * AllpassDelay(c, w0) : 0;
                if (period - lpDelay - apDelay >= 4.0) break;
                c *= 0.5;
            }
            double total = Math.Max(period - lpDelay - apDelay, 2.5);
            // The fractional all-pass: a delay in [0.5, 1.5); its phase delay at the pitch differs a little from the one it was made for, so correct it.
            int di = (int)Math.Floor(total - 0.5);
            double target = total - di;
            double d = target, a = (1 - d) / (1 + d);
            for (int it = 0; it < 3; it++)
            {
                a = (1 - d) / (1 + d);
                d = Math.Clamp(d + (target - AllpassDelay(a, w0)), 0.4, 1.6);
            }
            a = (1 - d) / (1 + d);
            // Loop gain: 60 dB in T60 at the fundamental, whatever the pitch (the low-pass's loss at the pitch is made up).
            double gTarget = PortableMath.Pow(0.001, (period / vr) / Math.Max(0.02, t60));
            double g = Math.Min(gTarget / Math.Max(gLp, 1e-3), 0.99999);
            di = Math.Clamp(di, 2, sMask - 2);
            sD[line] = di;
            sA[line] = (float)a;
            sLa[line] = (float)la;
            sG[line] = (float)g;
            sC[line] = (float)c;
        }

        /// <summary>Writes the pluck over a line: a burst of noise (hardness: its length and brightness), a comb notch from the pick position, a fixed level.</summary>
        void StringPluck(int line, uint seed, float pick, float hard, float vel)
        {
            int n = sD[line];
            if (n < 4) return;
            var buf = sBuf[line];
            var e = sTmp;
            var rng = new Rng(seed | 1u);
            hard = Math.Clamp(hard, 0f, 1f);
            // The burst's length (a soft thumb spreads over the whole line; a hammer is a short blow) and its brightness (one-pole low-passes).
            int len = Math.Max(4, (int)(n * (1.0 - 0.85 * hard)));
            float lp = 0.93f * (1f - hard) * (1f - hard);
            float y1 = 0f, y2 = 0f;
            for (int i = 0; i < n; i++)
            {
                float x = i < len ? rng.NextBipolar() : 0f;
                if (i < len && hard < 0.5f)
                {
                    float hann = 0.5f - 0.5f * (float)PortableMath.Cos(2.0 * Math.PI * (i + 0.5) / len);
                    x *= 0.3f + 0.7f * hann;
                }
                y1 = x + lp * (y1 - x);
                y2 = y1 + lp * (y2 - y1);
                e[i] = y2;
            }
            // The pick position: where on the string it is plucked (a share of its length from the bridge, 0.03 to 0.5): a comb through the line.
            int shift = Math.Clamp((int)Math.Round((0.03 + 0.47 * Math.Clamp(pick, 0f, 1f)) * n), 1, n - 1);
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                float v = e[i] - e[i - shift >= 0 ? i - shift : i - shift + n];
                buf[i] = v;
                sum += v;
            }
            double mean = sum / n, ss = 0;
            for (int i = 0; i < n; i++) { float v = buf[i] - (float)mean; buf[i] = v; ss += (double)v * v; }
            float scale = ss > 1e-18 ? (float)(StringPluckRms * (0.35 + 0.65 * vel) / Math.Sqrt(ss / n)) : 0f;
            for (int i = 0; i < n; i++) buf[i] *= scale;
            sW[line] = n;
            sFx[line] = sFy[line] = sLp[line] = 0f;
            sX1[2 * line] = sX1[2 * line + 1] = sY1[2 * line] = sY1[2 * line + 1] = 0f;
        }

        /// <summary>The plucked line's RMS (the note's loudness at full velocity).</summary>
        public const double StringPluckRms = 0.5;

        /// <summary>This tick's control of a note's String slot: each voice's string (pitch, decay, damping, stiffness), its pluck when new, the sympathetic strings and the voices' gains.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void StringControl(int note, int o, int no, OscSettings os, int u, double inc0, double vr, float invCount)
        {
            if (sBuf == null) return;
            u = Math.Min(u, StringVoices);
            int vb = note * MaxLanes + o * MaxUnison;
            int lb = note * StringVoices;
            int count = Math.Max(u, extPrevU[no]);
            float t60 = Math.Clamp(os.StrDecay, 0.05f, 20f);
            for (int l = 0; l < count; l++)
            {
                int k = vb + l;
                bool live = l < u;
                if (live)
                {
                    bool fresh = l >= extPrevU[no];
                    double hz = inc0 * ratio[o * MaxUnison + l] * vr;
                    int line = lb + l;
                    StringCoefs(line, hz, vr, t60, os.StrDamp, os.StrStiff);
                    if (fresh || !sLive[line])
                    {
                        uint seed = unchecked(seeds[note] * 2654435761u + (uint)(o + 1) * 0xC2B2AE35u + (uint)(l + 1) * 0x27D4EB2Fu);
                        StringPluck(line, seed, os.StrPick, os.StrHard, noteVel[note]);
                        sLive[line] = true; sQuiet[line] = 0;
                        mQuiet[k] = false;
                    }
                }
                float tl = live ? 1f / MathF.Sqrt(u) * panL[o * MaxUnison + l] : 0f, tr = live ? 1f / MathF.Sqrt(u) * panR[o * MaxUnison + l] : 0f;
                svDL[k] = (tl - svGL[k]) * invCount;
                svDR[k] = (tr - svGR[k]) * invCount;
                if (!live && svGL[k] == 0f && svGR[k] == 0f) svDL[k] = svDR[k] = 0f;
                svOn[k] = live || svGL[k] != 0f || svGR[k] != 0f;
            }
            extVoices[no] = count;
            extPrevU[no] = u;
            // The sympathetic strings: an octave and a fifth above the note, each at its own pitch, lightly damped like the note's.
            float amt = Math.Clamp(os.StrSymp, 0f, 1f);
            int sb = StringMainLines + note * SympatheticLines;
            double noteHz = inc0 * ratio[o * MaxUnison] * vr;
            if (amt > 0f)
            {
                for (int j = 0; j < SympatheticLines; j++)
                {
                    int line = sb + j;
                    StringCoefs(line, noteHz * (j == 0 ? 2.0 : 1.5), vr, t60, os.StrDamp, os.StrStiff);
                    if (!sLive[line])
                    {
                        Array.Clear(sBuf[line], 0, sBuf[line].Length);
                        sW[line] = 0; sFx[line] = sFy[line] = sLp[line] = 0f;
                        sX1[2 * line] = sX1[2 * line + 1] = sY1[2 * line] = sY1[2 * line + 1] = 0f;
                        sLive[line] = true; sQuiet[line] = 0;
                    }
                }
                sSympOn[note] = true;
            }
            else if (sSympOn[note] && sSympLvl[note] == 0f)
            {
                for (int j = 0; j < SympatheticLines; j++) sLive[sb + j] = false;
                sSympOn[note] = false;
            }
            float target = amt * StringSympGain;
            sSympStep[note] = (target - sSympLvl[note]) * invCount;
        }

        /// <summary>The sympathetic strings' output level at 100 % (the strings are fed the note's sum at <see cref="StringSympFeed"/>).</summary>
        public const float StringSympGain = 0.7f, StringSympFeed = 0.04f;

        /// <summary>One line over <paramref name="count"/> samples: the loop, optionally fed (<paramref name="inp"/> × <paramref name="feed"/>), its tap into <paramref name="outp"/>. Whether it still rings.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        bool StringBlock(int line, float[] inp, float feed, float[] outp, int count)
        {
            var buf = sBuf[line];
            int mask = sMask, w = sW[line], d = sD[line];
            float a = sA[line], la = sLa[line], g = sG[line], c = sC[line];
            float fx = sFx[line], fy = sFy[line], lp = sLp[line];
            int s0 = 2 * line;
            float x1a = sX1[s0], y1a = sY1[s0], x1b = sX1[s0 + 1], y1b = sY1[s0 + 1];
            bool disp = c != 0f, damp = la > 0f;
            float oneMinusLa = 1f - la;
            float peak = 0f;
            for (int i = 0; i < count; i++)
            {
                float x = buf[(w - d) & mask];
                outp[i] = x;
                // the fractional-delay all-pass
                float y = a * x + fx - a * fy;
                fx = x;
                fy = y;
                // damping: the treble dies first
                if (damp)
                {
                    lp = oneMinusLa * y + la * lp;
                    y = lp;
                }
                // stiffness: two dispersion all-passes
                if (disp)
                {
                    float ya = c * y + x1a - c * y1a;
                    x1a = y; y1a = ya;
                    float yb = c * ya + x1b - c * y1b;
                    x1b = ya; y1b = yb;
                    y = yb;
                }
                float v = g * y;
                if (inp != null) v += feed * inp[i];
                // NaN, infinities and the vanishingly small never enter the loop (a NaN fails both tests)
                float av = MathF.Abs(v);
                if (!(av < 1e6f) || av < 1e-20f) v = 0f;
                buf[w] = v;
                w = (w + 1) & mask;
                if (av > peak) peak = av;
            }
            sW[line] = w;
            sFx[line] = MathF.Abs(fx) < 1e-20f ? 0f : fx;
            sFy[line] = MathF.Abs(fy) < 1e-20f ? 0f : fy;
            sLp[line] = MathF.Abs(lp) < 1e-20f ? 0f : lp;
            sX1[s0] = MathF.Abs(x1a) < 1e-20f ? 0f : x1a;
            sY1[s0] = MathF.Abs(y1a) < 1e-20f ? 0f : y1a;
            sX1[s0 + 1] = MathF.Abs(x1b) < 1e-20f ? 0f : x1b;
            sY1[s0 + 1] = MathF.Abs(y1b) < 1e-20f ? 0f : y1b;
            if (peak > 1e-9f) sQuiet[line] = 0;
            else sQuiet[line] += count;
            return sQuiet[line] <= d + 8;
        }

        /// <summary>A note's String slot over <paramref name="count"/> samples into the engine buffers.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void StringVoicesRender(int note, int o, int no, int count)
        {
            if (sBuf == null) return;
            int vb = note * MaxLanes + o * MaxUnison;
            int lb = note * StringVoices;
            int sb = StringMainLines + note * SympatheticLines;
            bool symp = sSympOn[note];
            if (symp) Array.Clear(sMix, 0, count);
            for (int v = 0; v < extVoices[no]; v++)
            {
                int k = vb + v;
                if (!svOn[k] || mQuiet[k]) continue;
                int line = lb + v;
                if (!sLive[line]) continue;
                bool alive = StringBlock(line, null, 0f, sOut, count);
                mQuiet[k] = !alive;
                float gl = svGL[k], gr = svGR[k], sl = svDL[k], sr = svDR[k];
                for (int i = 0; i < count; i++)
                {
                    float s = sOut[i];
                    gl += sl;
                    gr += sr;
                    exL[i] += gl * s;
                    exR[i] += gr * s;
                }
                if (symp) for (int i = 0; i < count; i++) sMix[i] += sOut[i];
            }
            if (!symp) return;
            Array.Clear(sSymOut, 0, count);
            for (int j = 0; j < SympatheticLines; j++)
            {
                int line = sb + j;
                if (!sLive[line]) continue;
                StringBlock(line, sMix, StringSympFeed, sOut, count);
                for (int i = 0; i < count; i++) sSymOut[i] += sOut[i];
            }
            float lvl = sSympLvl[note], step = sSympStep[note];
            for (int i = 0; i < count; i++)
            {
                lvl += step;
                float s = lvl * sSymOut[i];
                exL[i] += s;
                exR[i] += s;
            }
            sSympLvl[note] = Math.Abs(lvl) < 1e-7f && step <= 0f ? 0f : lvl;
        }
    }
}
