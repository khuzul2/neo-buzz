#nullable disable
// Neo Grain (docs/specs/neo-grain.md): a granular synth. Each note is a cloud: a grain is a short slice of the source read at a pitch ratio through a
// band-limited interpolator (a Blackman-Harris windowed sinc, 12 taps, its cutoff lowered by the ratio when a grain is read faster than the source
// so a pitch up does not alias) and faded in and out by a window; grains start at the Density, each at the Position plus a random Spray, with a random
// detune, direction and pan (Roads, Microsound; Truax, Real-time granular synthesis; our own DSP). The first grain of a note starts at once; a cloud
// is made of at most 128 grains in all, 16 voices and a drone voice. A grain that starts near sounding ones can be aligned to them (the start moves
// within +-20 ms to where the waveform continues best, as WSOLA does), so a tonal source keeps its pitch instead of taking the comb of the grain rate.
// Randomness comes from one seeded stream per note: a render repeats exactly. Pure; no allocation after construction.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Grain
{
    internal sealed class GrainEngine : INoteSink
    {
        public const int MaxBlock = 256;
        public const int Voices = 16;
        /// <summary>The 16 note voices and the drone voice.</summary>
        public const int Slots = Voices + 1;
        public const int DroneSlot = Voices;
        public const int MaxGrains = 128;
        public const int Tick = 16;
        /// <summary>The note that plays the source at its own pitch (C3 in the project's naming; MIDI 60).</summary>
        public const int RootNote = 60;
        /// <summary>The length of a steal's or a kill's fade, seconds (at least 3 ms).</summary>
        public const double FadeSeconds = 0.005;
        /// <summary>The source's longest alignment shift, seconds.</summary>
        public const double AlignSeconds = 0.020;

        const int Free = 0, Held = 1, Released = 2, Fading = 3;
        const int WinSize = 1024;
        const int Half = 6, TableRes = 2048;
        const int MaxTaps = 2 * (Half * 8) + 2;
        const int AlignWidth = 32;
        const float LimitKnee = 0.7f, LimitCeiling = 0.89f;

        readonly ParamValues values;
        double fs = 48000;

        // The source: a built-in one or the song's wave; a grain keeps the one it started with.
        readonly GrainSource[] builtIns = new GrainSource[GrainSource.BuiltInCount];
        volatile GrainSource songSource;
        GrainSource sourceNow;
        int sourceKind;

        // Tables.
        static readonly float[] kernel = MakeKernel();
        static readonly float[][] windows = MakeWindows();

        // Per voice.
        readonly int[] state = new int[Slots];
        readonly int[] key = new int[Slots];
        readonly float[] note = new float[Slots], velocity = new float[Slots];
        readonly long[] age = new long[Slots];
        readonly float[] level = new float[Slots];
        readonly int[] stage = new int[Slots];
        readonly float[] fadeFrom = new float[Slots], fadeAt = new float[Slots];
        readonly bool[] sustained = new bool[Slots];
        readonly double[] untilSpawn = new double[Slots], scanOffset = new double[Slots];
        readonly Rng[] rng = new Rng[Slots];
        readonly bool[] hasPending = new bool[Slots];
        readonly int[] pendKey = new int[Slots];
        readonly float[] pendNote = new float[Slots], pendVel = new float[Slots];
        readonly int[] lastGrain = new int[Slots];
        readonly long[] lastSerial = new long[Slots];
        long clock;
        long serial;
        bool sustainDown;

        // Per grain.
        readonly bool[] gActive = new bool[MaxGrains];
        readonly int[] gVoice = new int[MaxGrains], gN = new int[MaxGrains], gLen = new int[MaxGrains], gDelay = new int[MaxGrains], gTaps = new int[MaxGrains], gWin = new int[MaxGrains];
        readonly GrainSource[] gSrc = new GrainSource[MaxGrains];
        readonly double[] gPos = new double[MaxGrains], gInc = new double[MaxGrains];
        readonly float[] gL = new float[MaxGrains], gR = new float[MaxGrains], gScale = new float[MaxGrains];
        readonly long[] gSerial = new long[MaxGrains];
        int activeGrains;

        // Block buffers.
        readonly float[] outL = new float[MaxBlock], outR = new float[MaxBlock];
        readonly float[][] busL = new float[Slots][], busR = new float[Slots][];
        readonly float[] envBuf = new float[Tick];
        readonly float[] align = new float[AlignWidth];

        // Smoothed settings.
        SmoothRamp volume, size, density, position, spray, scan, pitch, pitchSpray, reverse, panSpread, alignAmt;
        int tickLeft;
        float sizeNow, densityNow, positionNow, sprayNow, scanNow, pitchNow, pitchSprayNow, reverseNow, panNow, alignNow, normNow;
        int windowShape, seed;
        float attackStep, sustain, decayK, releaseK;
        bool wantDrone;

        /// <summary>The transport runs: the Drone voice plays only then (the machine sets it from the host's play state).</summary>
        public bool Playing;

        public readonly LevelMeter OutMeter = new LevelMeter();

        /// <summary>For the tests: when set, every grain's start (in samples since the engine's reset) and length are written here, up to the arrays' length.</summary>
        public long[] LogStart;
        public int[] LogLength;
        /// <summary>With <see cref="LogStart"/>: where in the source each grain starts (frames, after any alignment; the end of the segment for a reversed grain) and whether it plays backwards.</summary>
        public double[] LogPosition;
        public bool[] LogBackwards;
        public int LogCount;
        /// <summary>Grains that could not start because 128 were sounding.</summary>
        public long Dropped;

        // The editor's picture: the grains playing at the end of the last block (their place in the source, 0 to 1, and how far through their window), published whole.
        readonly float[][] dotPos = { new float[MaxGrains], new float[MaxGrains] }, dotLevel = { new float[MaxGrains], new float[MaxGrains] };
        readonly int[] dotCount = new int[2];
        volatile int dotPublished;

        public GrainEngine(ParamValues values)
        {
            this.values = values;
            for (int k = 0; k < GrainSource.BuiltInCount; k++) builtIns[k] = GrainSource.BuiltIn(k);
            for (int v = 0; v < Slots; v++) { busL[v] = new float[Tick]; busR[v] = new float[Tick]; lastGrain[v] = -1; }
            SetSampleRate(48000);
        }

        public float SampleRate => (float)fs;

        public bool IsActive
        {
            get
            {
                for (int v = 0; v < Slots; v++) if (state[v] != Free) return true;
                return false;
            }
        }

        public int ActiveVoices { get { int n = 0; for (int v = 0; v < Slots; v++) if (state[v] != Free) n++; return n; } }
        public int ActiveGrains => activeGrains;

        /// <summary>The song's wave to read when Source is Song wave (null: none, silence). Any thread.</summary>
        public void SetWave(GrainSource wave) => songSource = wave;

        /// <summary>The source the grains are cut from now (null: a Song wave source with no wave).</summary>
        public GrainSource CurrentSource => sourceKind == 3 ? songSource : builtIns[Math.Clamp(sourceKind, 0, 2)];

        public void SetSampleRate(float rate)
        {
            fs = rate;
            volume.Setup(0.02, rate);
            double tickRate = rate / Tick;
            size.Setup(0.03, tickRate); density.Setup(0.03, tickRate); position.Setup(0.03, tickRate); spray.Setup(0.03, tickRate);
            scan.Setup(0.03, tickRate); pitch.Setup(0.03, tickRate); pitchSpray.Setup(0.03, tickRate); reverse.Setup(0.03, tickRate);
            panSpread.Setup(0.03, tickRate); alignAmt.Setup(0.03, tickRate);
            OutMeter.SetSampleRate(rate);
            KillAll();
            Update(true);
            volume.Reset(volume.Target); size.Reset(size.Target); density.Reset(density.Target); position.Reset(position.Target); spray.Reset(spray.Target);
            scan.Reset(scan.Target); pitch.Reset(pitch.Target); pitchSpray.Reset(pitchSpray.Target); reverse.Reset(reverse.Target);
            panSpread.Reset(panSpread.Target); alignAmt.Reset(alignAmt.Target);
            Latch();
        }

        void KillAll()
        {
            for (int v = 0; v < Slots; v++) Clear(v);
            for (int g = 0; g < MaxGrains; g++) { gActive[g] = false; gSrc[g] = null; }
            activeGrains = 0;
        }

        void Clear(int v)
        {
            state[v] = Free;
            hasPending[v] = false;
            sustained[v] = false;
            level[v] = 0f;
            lastGrain[v] = -1;
            for (int g = 0; g < MaxGrains; g++)
                if (gActive[g] && gVoice[g] == v) { gActive[g] = false; gSrc[g] = null; activeGrains--; }
        }

        // ── settings ────────────────────────────────────────────────────────

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            sourceKind = values.Choice(P.Source);
            windowShape = values.Choice(P.Window);
            seed = (int)values.Value(P.Seed);
            size.SetTarget(values.F(P.Size));
            density.SetTarget(values.F(P.Density));
            position.SetTarget(values.F(P.Position));
            spray.SetTarget(values.F(P.Spray));
            scan.SetTarget(values.F(P.Scan) * 0.01f);
            pitch.SetTarget(values.F(P.Pitch));
            pitchSpray.SetTarget(values.F(P.PitchSpray));
            reverse.SetTarget(values.F(P.Reverse));
            panSpread.SetTarget(values.F(P.PanSpread));
            alignAmt.SetTarget(values.F(P.Align));
            double vol = values.Value(P.Volume);
            volume.SetTarget(double.IsNegativeInfinity(vol) ? 0f : (float)DspMath.DbToGain(vol));
            attackStep = (float)(1.0 / (values.Value(P.Attack) * 0.001 * fs));
            sustain = values.F(P.Sustain);
            decayK = (float)PortableMath.Exp(-6.9 / (values.Value(P.Decay) * 0.001 * fs));
            releaseK = (float)PortableMath.Exp(-3.0 * 2.302585092994046 / (values.Value(P.Release) * 0.001 * fs));
            wantDrone = values.On(P.Drone);
            values.Clean();
        }

        /// <summary>The ramps' values at the start of a tick.</summary>
        void Latch()
        {
            sizeNow = size.Next(); densityNow = density.Next(); positionNow = position.Next(); sprayNow = spray.Next(); scanNow = scan.Next();
            pitchNow = pitch.Next(); pitchSprayNow = pitchSpray.Next(); reverseNow = reverse.Next(); panNow = panSpread.Next(); alignNow = alignAmt.Next();
            // The cloud's level stays about the same as it thickens: the grains that overlap add up like uncorrelated sounds.
            double overlap = Math.Max(1.0, densityNow * sizeNow * 0.001 * 0.5);
            normNow = (float)(1.0 / Math.Sqrt(overlap));
            sourceNow = CurrentSource;
        }

        // ── notes ───────────────────────────────────────────────────────────

        public void NoteOn(int k, int n, float vel)
        {
            Update(false);
            int slot = -1;
            for (int v = 0; v < Voices; v++) if (state[v] == Free) { slot = v; break; }
            if (slot >= 0)
            {
                Begin(slot, k, n, vel);
                return;
            }
            // Steal: the quietest released voice, else the oldest held one; one already fading is taken last (its pending note is replaced).
            int best = -1; double bestScore = double.MaxValue;
            for (int v = 0; v < Voices; v++)
            {
                double score = state[v] == Fading ? 1e12 + age[v] : state[v] == Released ? level[v] : 1e6 + age[v];
                if (score < bestScore) { bestScore = score; best = v; }
            }
            if (state[best] != Fading) StartFade(best);
            hasPending[best] = true;
            pendKey[best] = k; pendNote[best] = n; pendVel[best] = vel;
        }

        public void NoteOff(int k)
        {
            for (int v = 0; v < Voices; v++)
            {
                if (state[v] != Held || key[v] != k) continue;
                if (sustainDown) sustained[v] = true;
                else Release(v);
            }
        }

        void Release(int v)
        {
            sustained[v] = false;
            state[v] = Released;
        }

        void StartFade(int v)
        {
            state[v] = Fading;
            sustained[v] = false;
            fadeFrom[v] = level[v];
            fadeAt[v] = 0f;
        }

        public void ModWheel(float value) { }
        public void PitchBend(float value) { }
        public void Aftertouch(float value) { }

        public void Sustain(bool down)
        {
            sustainDown = down;
            if (down) return;
            for (int v = 0; v < Voices; v++) if (sustained[v] && state[v] == Held) Release(v);
        }

        public void AllNotesOff(bool hard)
        {
            for (int v = 0; v < Voices; v++)
            {
                hasPending[v] = false;
                if (state[v] == Free) continue;
                if (hard) { if (state[v] != Fading) StartFade(v); }
                else if (state[v] == Held) Release(v);
            }
            if (hard && state[DroneSlot] != Free && state[DroneSlot] != Fading) StartFade(DroneSlot);
            if (hard) sustainDown = false;
        }

        void Begin(int v, int k, float n, float vel)
        {
            state[v] = Held;
            key[v] = k; note[v] = n; velocity[v] = vel;
            age[v] = ++clock;
            sustained[v] = false;
            hasPending[v] = false;
            level[v] = 0f;
            stage[v] = 0;
            untilSpawn[v] = 0;
            scanOffset[v] = 0;
            lastGrain[v] = -1;
            rng[v] = new Rng(unchecked(0x9E3779B1u * (uint)(seed + 1) + (uint)(age[v] * 2654435761u) + (uint)v * 40503u));
        }

        // ── grains ──────────────────────────────────────────────────────────

        void Spawn(int v, int offset)
        {
            var src = sourceNow;
            if (src == null) return;
            int g = -1;
            for (int i = 0; i < MaxGrains; i++) if (!gActive[i]) { g = i; break; }
            if (g < 0) { Dropped++; return; }

            int len = Math.Max(16, (int)Math.Round(sizeNow * 0.001 * fs));
            double semis = note[v] - RootNote + pitchNow + (pitchSprayNow > 0 ? rng[v].NextBipolar() * pitchSprayNow * 0.01 : 0.0);
            double ratio = src.Rate / fs * PortableMath.Pow(2.0, semis / 12.0);
            ratio = Math.Clamp(ratio, 1e-3, 64.0);
            bool back = reverseNow > 0f && rng[v].NextUnipolar() < reverseNow;
            double inc = back ? -ratio : ratio;
            double s = positionNow * src.Length + scanOffset[v] + (sprayNow > 0 ? rng[v].NextBipolar() * sprayNow * 0.001 * src.Rate : 0.0);
            s -= Math.Floor(s / src.Length) * src.Length;
            if (back) s += len * ratio;
            if (alignNow > 0f) s += AlignShift(v, offset, s, inc, len, src);

            float pan = panNow > 0f ? rng[v].NextBipolar() * panNow : 0f;
            DspMath.PanGains(pan, out float gl, out float gr);
            float amp = velocity[v] * normNow * 0.70710678f;

            gActive[g] = true; activeGrains++;
            gVoice[g] = v; gN[g] = 0; gLen[g] = len; gDelay[g] = offset; gSrc[g] = src; gWin[g] = windowShape;
            gPos[g] = s; gInc[g] = inc;
            gL[g] = gl * amp; gR[g] = gr * amp;
            double a = ratio > 1.0 ? 0.95 / Math.Min(ratio, 8.0) : 1.0;
            gScale[g] = (float)a;
            gTaps[g] = (int)Math.Ceiling(Half / a);
            gSerial[g] = ++serial;
            lastGrain[v] = g; lastSerial[v] = gSerial[g];
            if (LogStart != null && LogCount < LogStart.Length) { LogStart[LogCount] = chunkStart + offset; LogLength[LogCount] = len; if (LogPosition != null) { LogPosition[LogCount] = back ? s - len * ratio : s; LogBackwards[LogCount] = back; } LogCount++; }
        }

        long clock0, chunkStart;   // samples rendered before this block, and before this chunk: for the log

        /// <summary>
        /// The shift (in source frames, plus or minus <see cref="AlignSeconds"/> times Align) of a new grain's start that makes it continue the
        /// grain this voice started last: the sum of squared differences over 32 samples between the two grains' waveforms, searched coarsely and then
        /// frame by frame, with a parabola through the best three for the fraction.
        /// </summary>
        double AlignShift(int v, int offset, double s, double inc, int len, GrainSource src)
        {
            int pg = lastGrain[v];
            if (pg < 0 || !gActive[pg] || gSerial[pg] != lastSerial[v] || gSrc[pg] != src) return 0.0;
            int ahead = offset - Math.Max(0, gDelay[pg]);
            int w = Math.Min(AlignWidth, Math.Min(len / 2, gLen[pg] - gN[pg] - Math.Max(ahead, 0)));
            if (w < 8 || ahead < 0) return 0.0;
            double radius = Math.Min(AlignSeconds, sizeNow * 0.001 * 0.25) * alignNow * src.Rate;
            int r = (int)radius;
            if (r < 1) return 0.0;
            double p0 = gPos[pg] + gInc[pg] * ahead;
            double pinc = gInc[pg];
            for (int i = 0; i < w; i++) align[i] = Lin(src, p0 + pinc * i);
            int stride = Math.Max(1, r / 48);
            double best = double.MaxValue; int bestD = 0;
            for (int d = 0; d <= r; d += stride)
            {
                for (int sign = d == 0 ? 1 : -1; sign <= 1; sign += 2)
                {
                    int dd = sign * d;
                    double c = Cost(src, s + dd, inc, w);
                    if (c < best) { best = c; bestD = dd; }
                }
            }
            if (stride > 1)
            {
                int lo = Math.Max(-r, bestD - stride), hi = Math.Min(r, bestD + stride);
                for (int dd = lo; dd <= hi; dd++)
                {
                    double c = Cost(src, s + dd, inc, w);
                    if (c < best) { best = c; bestD = dd; }
                }
            }
            double c0 = Cost(src, s + bestD - 1, inc, w), c1 = best, c2 = Cost(src, s + bestD + 1, inc, w);
            double den = c0 - 2 * c1 + c2, frac = 0;
            if (den > 1e-20) frac = Math.Clamp(0.5 * (c0 - c2) / den, -1.0, 1.0);
            return bestD + frac;
        }

        double Cost(GrainSource src, double start, double inc, int w)
        {
            double sum = 0;
            for (int i = 0; i < w; i++)
            {
                float d = Lin(src, start + inc * i) - align[i];
                sum += d * d;
            }
            return sum;
        }

        /// <summary>Linear reading, for the alignment search.</summary>
        static float Lin(GrainSource src, double p)
        {
            if (src.Loop) { p -= Math.Floor(p / src.Length) * src.Length; }
            int i = (int)Math.Floor(p);
            float f = (float)(p - i);
            if (i < -GrainSource.Pad + 1 || i >= src.Length + GrainSource.Pad - 2) return 0f;
            float a = src.Data[GrainSource.Pad + i], b = src.Data[GrainSource.Pad + i + 1];
            return a + (b - a) * f;
        }

        // ── a block ─────────────────────────────────────────────────────────

        /// <summary>One stereo block: the sound replaces whatever is in the buffers.</summary>
        public bool Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            bool any = IsActive || (Playing && wantDrone);
            int pos = 0;
            while (pos < n)
            {
                if (tickLeft <= 0) StartTick();
                int m = Math.Min(tickLeft, n - pos);
                Chunk(pos, m);
                tickLeft -= m;
                pos += m;
            }
            for (int i = 0; i < n; i++) { left[i] = outL[i]; right[i] = outR[i]; }
            clock0 += n;
            PublishDots();
            OutMeter.Feed(left, right, n);
            return any || IsActive;
        }

        void StartTick()
        {
            tickLeft = Tick;
            Latch();
            bool drone = wantDrone && Playing;
            if (drone && state[DroneSlot] == Free) { Begin(DroneSlot, -1, RootNote, 1f); }
            else if (!drone && state[DroneSlot] == Held) Release(DroneSlot);
        }

        void Chunk(int pos, int m)
        {
            for (int i = 0; i < m; i++) { outL[pos + i] = 0f; outR[pos + i] = 0f; }
            chunkStart = clock0 + pos;
            double period = fs / Math.Max(0.5, densityNow);
            // Grains start.
            for (int v = 0; v < Slots; v++)
            {
                if (state[v] == Free) continue;
                Array.Clear(busL[v], 0, m); Array.Clear(busR[v], 0, m);
                if (state[v] == Fading) continue;
                for (int i = 0; i < m; i++)
                {
                    untilSpawn[v] -= 1.0;
                    if (untilSpawn[v] <= 0.0)
                    {
                        Spawn(v, i);
                        untilSpawn[v] += period;
                    }
                }
                scanOffset[v] += m * scanNow * (sourceNow != null ? sourceNow.Rate / fs : 0.0);
            }
            // Grains play.
            for (int g = 0; g < MaxGrains; g++)
            {
                if (!gActive[g]) continue;
                PlayGrain(g, m);
            }
            // Voices: the envelope, then into the mix.
            for (int v = 0; v < Slots; v++)
            {
                int st = state[v];
                if (st == Free) continue;
                float lv = level[v], fadeT = fadeAt[v];
                float sus = v == DroneSlot ? 1f : sustain;
                float fadeD = (float)(1.0 / (FadeSeconds * fs));
                var bl = busL[v]; var br = busR[v];
                for (int i = 0; i < m; i++)
                {
                    if (st == Held)
                    {
                        if (stage[v] == 0) { lv += attackStep; if (lv >= 1f) { lv = 1f; stage[v] = 1; } }
                        else lv = sus + (lv - sus) * decayK;
                    }
                    else if (st == Released) lv *= releaseK;
                    else { fadeT += fadeD; lv = fadeT >= 1f ? 0f : fadeFrom[v] * (0.5f + 0.5f * PortableMathF.Cos((float)(Math.PI * fadeT))); }
                    outL[pos + i] += bl[i] * lv;
                    outR[pos + i] += br[i] * lv;
                }
                level[v] = lv; fadeAt[v] = fadeT;
                if (st == Fading && lv <= 0f)
                {
                    if (hasPending[v])
                    {
                        int pk = pendKey[v]; float pn = pendNote[v], pv = pendVel[v];
                        Clear(v);
                        Begin(v, pk, pn, pv);
                    }
                    else Clear(v);
                }
                else if (st == Released && lv < 1e-5f) Clear(v);
            }
            for (int i = 0; i < m; i++)
            {
                float vol = volume.Next();
                outL[pos + i] = DspMath.Flush(SoftLimit(outL[pos + i] * vol));
                outR[pos + i] = DspMath.Flush(SoftLimit(outR[pos + i] * vol));
            }
        }

        void PlayGrain(int g, int m)
        {
            var src = gSrc[g];
            int v = gVoice[g];
            var bl = busL[v]; var br = busR[v];
            if (state[v] == Free) { gActive[g] = false; gSrc[g] = null; activeGrains--; return; }
            int n = gN[g], len = gLen[g], i0 = gDelay[g], taps = gTaps[g];
            gDelay[g] = 0;
            double p = gPos[g], inc = gInc[g];
            float a = gScale[g], gl = gL[g], gr = gR[g];
            float[] win = windows[gWin[g]];
            float invLen = WinSize / (float)len;
            var data = src.Data;
            int length = src.Length, pad = GrainSource.Pad;
            bool loop = src.Loop;
            float aRes = a * TableRes;
            var kern = kernel;
            for (int i = i0; i < m; i++)
            {
                if (n >= len) { gActive[g] = false; gSrc[g] = null; activeGrains--; return; }
                float wt = (n + 0.5f) * invLen;
                int wi = (int)wt;
                float w = win[wi] + (win[wi + 1] - win[wi]) * (wt - wi);
                int ib = (int)p;
                if (p < ib) ib--;
                if (loop)
                {
                    if (ib >= length) { ib -= length; p -= length; }
                    else if (ib < 0) { ib += length; p += length; }
                }
                float fr = (float)(p - ib);
                float s = 0f;
                if (ib - taps >= -pad && ib + taps < length + pad)
                {
                    // The taps left of the point (distance fr, fr + 1, ...) and right of it (1 - fr, 2 - fr, ...): no sign to take, no range to test.
                    int dl = pad + ib, dr = dl + 1;
                    float ul = fr * aRes + 0.5f, ur = (1f - fr) * aRes + 0.5f;
                    for (int k = 0; k < taps; k++)
                    {
                        s += data[dl - k] * kern[(int)ul] + data[dr + k] * kern[(int)ur];
                        ul += aRes; ur += aRes;
                    }
                    s *= a;
                }
                float o = s * w;
                bl[i] += o * gl;
                br[i] += o * gr;
                p += inc;
                n++;
            }
            gN[g] = n; gPos[g] = p;
            if (n >= len) { gActive[g] = false; gSrc[g] = null; activeGrains--; }
        }

        // ── tables ──────────────────────────────────────────────────────────

        static float[] MakeKernel()
        {
            var k = new float[(Half + 1) * TableRes + 2];
            for (int i = 0; i < k.Length; i++)
            {
                double v = (double)i / TableRes;
                if (v >= Half) { k[i] = 0f; continue; }
                double sinc = v < 1e-9 ? 1.0 : PortableMath.Sin(Math.PI * v) / (Math.PI * v);
                double t = Math.PI * v / Half;
                double win = 0.35875 + 0.48829 * PortableMath.Cos(t) + 0.14128 * PortableMath.Cos(2 * t) + 0.01168 * PortableMath.Cos(3 * t);
                k[i] = (float)(sinc * win);
            }
            return k;
        }

        /// <summary>Hann, Tukey (taper 50 %) and Gauss (sigma 0.14 of the length, lifted to zero at the ends), 1024 points and one more.</summary>
        static float[][] MakeWindows()
        {
            var w = new float[3][];
            for (int s = 0; s < 3; s++) w[s] = new float[WinSize + 2];
            double edge = PortableMath.Exp(-0.5 * (0.5 / 0.14) * (0.5 / 0.14));
            for (int i = 0; i <= WinSize + 1; i++)
            {
                double x = Math.Min(i, WinSize) / (double)WinSize;
                w[0][i] = (float)(0.5 - 0.5 * PortableMath.Cos(2 * Math.PI * x));
                double taper = 0.25;     // each side of a Tukey window with a taper of 0.5 of the length
                double t = x < taper ? 0.5 - 0.5 * PortableMath.Cos(Math.PI * x / taper) : x > 1 - taper ? 0.5 - 0.5 * PortableMath.Cos(Math.PI * (1 - x) / taper) : 1.0;
                w[1][i] = (float)t;
                double d = (x - 0.5) / 0.14;
                w[2][i] = (float)((PortableMath.Exp(-0.5 * d * d) - edge) / (1.0 - edge));
            }
            return w;
        }

        /// <summary>Transparent below 0.7 (-3.1 dBFS); above it a smooth knee to a ceiling of 0.89 (-1 dBFS), which it never reaches.</summary>
        static float SoftLimit(float x)
        {
            float a = Math.Abs(x);
            if (a <= LimitKnee) return x;
            float y = LimitKnee + (LimitCeiling - LimitKnee) * DspMath.FastTanh((a - LimitKnee) * (1f / (LimitCeiling - LimitKnee)));
            return x < 0 ? -y : y;
        }

        // ── for the editor and the tests ────────────────────────────────────

        void PublishDots()
        {
            int w = 1 - dotPublished;
            var pp = dotPos[w]; var pl = dotLevel[w];
            int c = 0;
            for (int g = 0; g < MaxGrains; g++)
            {
                if (!gActive[g]) continue;
                var src = gSrc[g];
                if (src == null) continue;
                double p = gPos[g] / src.Length;
                p -= Math.Floor(p);
                pp[c] = (float)p;
                double x = (double)gN[g] / Math.Max(1, gLen[g]);
                pl[c] = (float)PortableMath.Sin(Math.PI * Math.Clamp(x, 0.0, 1.0));
                c++;
            }
            dotCount[w] = c;
            dotPublished = w;
        }

        /// <summary>The grains playing at the end of the last block: their place in the source (0 to 1) and how far through their window (0 to 1 to 0). Returns the count (UI thread; a torn read only moves a dot).</summary>
        public int Dots(float[] position, float[] weight)
        {
            int w = dotPublished;
            int c = Math.Min(dotCount[w], Math.Min(position.Length, weight.Length));
            Array.Copy(dotPos[w], position, c);
            Array.Copy(dotLevel[w], weight, c);
            return c;
        }
    }
}
