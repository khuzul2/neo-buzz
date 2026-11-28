#nullable disable
// Neo Looper (docs/specs/neo-looper.md): record, overdub, multiply and undo, in time with the song. Clean room: textbook DSP (a ring-free
// loop buffer, raised-cosine and equal-power fades), no code or table from any other product.
//
// The loop is a whole-buffer layer. Every pass that changes it (an overdub, a multiply, a clear) writes into a different pool buffer while the
// old one stays untouched: the old one IS the undo level, restored bit for bit. The frames a pass has not visited are copied from the old
// buffer in chunks (CopyChunk frames a block), and a read picks the right buffer meanwhile. Nothing is allocated while the machine runs: the
// pool is made when the machine is created, when the limits change (on a worker thread, only while the loop is empty) and at load.

using System;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Looper
{
    internal enum LooperState { Empty = 0, Recording = 1, Playing = 2, Overdubbing = 3, Stopped = 4 }

    internal enum LooperCmd { None, Record, Stop, Play, Undo, Redo, Clear, Multiply }

    /// <summary>The pool of loop buffers: <see cref="Levels"/> + 2 stereo buffers of <see cref="Frames"/> frames, and the copy map.</summary>
    internal sealed class LooperPool
    {
        public readonly int Frames, Levels;
        public readonly float[][] L, R;
        public readonly byte[] Have;

        public LooperPool(int frames, int levels)
        {
            Frames = frames;
            Levels = levels;
            L = new float[levels + 2][];
            R = new float[levels + 2][];
            for (int i = 0; i < L.Length; i++)
            {
                L[i] = new float[frames];
                R[i] = new float[frames];
            }
            Have = new byte[frames];
        }

        public long Bytes => (long)(Levels + 2) * Frames * 8 + Frames;
    }

    internal sealed class LooperEngine
    {
        public const int MaxBlock = 256;
        public const int Latency = 0;
        /// <summary>The most memory the buffers may use (bytes).</summary>
        public const long MemoryCap = 320L * 1024 * 1024;
        /// <summary>Frames copied per block from an old buffer into a new one.</summary>
        public const int CopyChunk = 32768;
        public static readonly double[] MaxLoopSeconds = { 15, 30, 60, 120 };

        readonly ParamValues v;
        readonly FxStage fx;
        double sr = 48000;

        // ---- the pool and the stacks ----
        LooperPool pool;
        volatile LooperPool pendingPool;     // made by the worker, taken at the next block while the loop is empty
        int live = -1;                       // index of the live buffer in the pool
        int frames;                          // the loop's cycle length in frames (0: none)
        double loopBeats;                    // the cycle in beats (Sync), 0 when it was taken in Free
        readonly int[] undoBuf = new int[8], undoFrames = new int[8];
        readonly double[] undoBeats = new double[8];
        readonly int[] redoBuf = new int[8], redoFrames = new int[8];
        readonly double[] redoBeats = new double[8];
        int undoN, redoN;
        readonly bool[] used = new bool[10];

        // ---- the pending copy (a pass wrote into a new buffer; the rest still lives in the old one) ----
        bool copyPending;
        int copySrc, copySrcFrames, copyFront;

        // ---- the loop's playhead ----
        struct Head { public int Buf, Pos, Sub, Frames; }
        Head head, fromHead;
        int xfadeLeft, xfadeTotal;
        LooperState state = LooperState.Empty;
        int rampCount;                       // overdub punch ramp: counts up to F at punch in, down to 0 at punch out
        bool punchingOut;
        int fadeIn, fadeOut;                 // play start and stop fades (frames left)
        int recCount;                        // frames recorded so far
        float[] postL, postR;
        int postCount, postWant;

        // ---- the song ----
        bool tPlaying, lastPlaying, haveLast;
        double tBeat, tSpb = 24000, lastEndBeat;
        int tBeatsPerBar = 4;
        double origin;                       // the song beat at which the loop's frame 0 played (Sync)

        // ---- parameters, read once a block ----
        int F = 288;
        float fb = 1f, gainTarget = 1f;
        bool monitor = true, reverse, half, sync = true, beatQuant, pedalBoth;
        int mulBy = 2;
        SmoothRamp gain;
        float lastGain = 1f;

        // ---- triggers ----
        readonly int[] lastRaw = new int[7];
        bool rawKnown = true;     // the parameters start at 0, so a trigger that is set before the first block fires
        struct Pending { public LooperCmd Cmd; public double Beat; public bool Immediate; }
        readonly Pending[] queue = new Pending[16];
        int queueN;

        // ---- worker for the pool (off the audio thread) ----
        readonly SemaphoreSlim wake = new SemaphoreSlim(0);
        Thread worker;
        volatile int wantFrames, wantLevels;
        volatile bool disposed;

        // ---- readings (the editor and the agent) ----
        public LooperState State => state;
        public int LoopFrames => frames;
        public double LoopBeats => loopBeats;
        public int UndoDepth => undoN;
        public int RedoDepth => redoN;
        public int Position => head.Pos;
        public int PoolFrames => pool.Frames;
        public int PoolLevels => pool.Levels;
        public long PoolBytes => pool.Bytes;
        public bool Syncing { get; private set; }
        public int CrossfadeFrames => F;
        /// <summary>Counts every change of the loop's audio (a new take, a pass, an undo): the editor redraws the waveform when it moves.</summary>
        public int Revision;

        public LooperEngine(ParamValues raw)
        {
            v = raw;
            fx = new FxStage(raw, P.Amount, P.Mix, null, maxLatency: Latency, maxBlock: MaxBlock);
            gain.Setup(0.02, sr);
            gain.Reset(1f);
            SetSampleRate(48000);
        }

        // ---------------------------------------------------------------------------------------------------------- the pool

        /// <summary>Frames and levels for a Max loop choice and an Undo levels count at a sample rate, with the memory cap applied.</summary>
        public static void Plan(double rate, int maxLoopChoice, int levels, out int frames, out int levelsInForce)
        {
            frames = (int)(MaxLoopSeconds[Math.Clamp(maxLoopChoice, 0, 3)] * rate);
            levels = Math.Clamp(levels, 1, 8);
            long per = (long)frames * 8;
            while (levels > 1 && (levels + 2) * per + frames > MemoryCap) levels--;
            levelsInForce = levels;
        }

        public void SetSampleRate(float rate)
        {
            bool changed = rate != sr;
            sr = rate;
            fx.SetSampleRate(rate);
            gain.Setup(0.02, rate);
            gain.Reset(lastGain);
            int post = (int)(rate * 0.05) + 16;
            if (postL == null || postL.Length != post)
            {
                postL = new float[post];
                postR = new float[post];
            }
            if (pool == null || changed)
            {
                Plan(rate, (int)v.Value(P.MaxLoop), (int)v.Value(P.UndoLevels), out int f, out int l);
                if (pool == null || pool.Frames != f || pool.Levels != l) Install(new LooperPool(f, l));
            }
            Update(true);
        }

        void Install(LooperPool p)
        {
            pool = p;
            ClearAll();
        }

        void ClearAll()
        {
            Array.Clear(used, 0, used.Length);
            undoN = redoN = 0;
            copyPending = false;
            frames = 0;
            loopBeats = 0;
            live = -1;
            state = LooperState.Empty;
            xfadeLeft = fadeIn = fadeOut = rampCount = postCount = postWant = 0;
            queueN = 0;
            Revision++;
        }

        /// <summary>Back to an empty machine (a new song, a reset).</summary>
        public void Reset()
        {
            fx.Reset();
            ClearAll();
            haveLast = false;
            ArmTriggers();
        }

        /// <summary>Starts the worker that makes a new pool when Max loop or Undo levels change (the machine calls it once).</summary>
        public void StartWorker()
        {
            if (worker != null) return;
            worker = new Thread(() =>
            {
                while (!disposed)
                {
                    wake.Wait();
                    if (disposed) break;
                    try { pendingPool = new LooperPool(wantFrames, wantLevels); }
                    catch (OutOfMemoryException) { pendingPool = null; }
                }
            }) { IsBackground = true, Name = "Neo Looper pool" };
            worker.Start();
        }

        public void Dispose()
        {
            disposed = true;
            wake.Release();
        }

        /// <summary>Makes the pool for the current Max loop and Undo levels now, on the calling thread (tests, load).</summary>
        public void ApplyLimitsNow()
        {
            Plan(sr, (int)v.Value(P.MaxLoop), (int)v.Value(P.UndoLevels), out int f, out int l);
            if (pool.Frames != f || pool.Levels != l) Install(new LooperPool(f, l));
        }

        void CheckLimits()
        {
            if (state != LooperState.Empty || frames != 0)
            {
                var np0 = pendingPool;
                if (np0 != null) pendingPool = null;
                return;
            }
            Plan(sr, (int)v.Value(P.MaxLoop), (int)v.Value(P.UndoLevels), out int f, out int l);
            var np = pendingPool;
            if (np != null)
            {
                pendingPool = null;
                if (np.Frames == f && np.Levels == l) { Install(np); return; }
            }
            if (pool.Frames != f || pool.Levels != l)
            {
                if (worker == null) return;              // tests and load call ApplyLimitsNow
                if (wantFrames != f || wantLevels != l)
                {
                    wantFrames = f;
                    wantLevels = l;
                    wake.Release();
                }
            }
        }

        int AllocBuffer()
        {
            for (int i = 0; i < pool.L.Length; i++) if (!used[i]) { used[i] = true; return i; }
            return -1;
        }

        void Free(int b)
        {
            if (b >= 0) used[b] = false;
            if (fromHead.Buf == b && xfadeLeft > 0) xfadeLeft = 0;
        }

        // ---------------------------------------------------------------------------------------------------------- load and save

        /// <summary>The loop's audio for the song file (the editor and the saver call it on their own thread): a copy of what plays now.</summary>
        public bool Snapshot(out float[] l, out float[] r, out int count, out double beats)
        {
            l = r = null;
            count = 0;
            beats = 0;
            var p = pool;
            int n = frames, b = live;
            if (b < 0 || n <= 0 || p == null) return false;
            if (state == LooperState.Overdubbing && undoN > 0)
            {
                // Mid pass the buffer is changing: the loop as it was before the pass is what is saved.
                b = undoBuf[undoN - 1];
                n = undoFrames[undoN - 1];
                beats = undoBeats[undoN - 1];
            }
            else beats = loopBeats;
            l = new float[n];
            r = new float[n];
            bool pend = copyPending && b == live;
            int src = copySrc, srcN = copySrcFrames;
            for (int i = 0; i < n; i++)
            {
                if (pend && p.Have[i] == 0) { int s = i % srcN; l[i] = p.L[src][s]; r[i] = p.R[src][s]; }
                else { l[i] = p.L[b][i]; r[i] = p.R[b][i]; }
            }
            count = n;
            return true;
        }

        // ---------------------------------------------------------------------------------------------------------- what the editor shows

        public int BeatsPerBar => tBeatsPerBar;
        public double Rate => sr;
        /// <summary>The trigger waiting for its bar or beat (None when nothing waits).</summary>
        public LooperCmd Queued => queueN > 0 ? queue[0].Cmd : LooperCmd.None;
        /// <summary>The undo levels held (the editor draws them behind the loop).</summary>
        public int UndoLevelsHeld => undoN;

        /// <summary>The loop's peaks in <paramref name="columns"/> columns (0 = the playing loop, 1 = the newest undo level, 2 = the one before): false when there is none.</summary>
        public bool Wave(int which, int columns, float[] min, float[] max)
        {
            var p = pool;
            int b, n;
            if (which == 0) { b = live; n = frames; }
            else if (which <= undoN) { b = undoBuf[undoN - which]; n = undoFrames[undoN - which]; }
            else return false;
            if (b < 0 || n <= 0 || p == null) return false;
            bool pend = which == 0 && copyPending;
            int src = copySrc, srcN = copySrcFrames;
            for (int c = 0; c < columns; c++)
            {
                int from = (int)((long)c * n / columns), to = (int)((long)(c + 1) * n / columns);
                if (to <= from) to = from + 1;
                float lo = 0f, hi = 0f;
                for (int i = from; i < to && i < n; i++)
                {
                    float x = pend && p.Have[i] == 0 ? p.L[src][i % srcN] : p.L[b][i];
                    if (x < lo) lo = x;
                    if (x > hi) hi = x;
                }
                min[c] = lo;
                max[c] = hi;
            }
            return true;
        }

        /// <summary>A loop from a song, for the next block (the loader thread). The machine starts Stopped with the loop ready.</summary>
        public void Load(float[] l, float[] r, int count, double beats)
        {
            if (count <= 0) return;
            if (count > pool.Frames)
            {
                // A loop longer than the limit in force: a pool that holds it (the loader's own thread).
                int levels = pool.Levels;
                while (levels > 1 && (long)(levels + 2) * count * 8 + count > MemoryCap) levels--;
                Install(new LooperPool(count, levels));
            }
            else ClearAll();
            int b = AllocBuffer();
            Array.Copy(l, pool.L[b], count);
            Array.Copy(r ?? l, pool.R[b], count);
            live = b;
            frames = count;
            loopBeats = beats;
            head = new Head { Buf = b, Pos = 0, Sub = 0, Frames = count };
            state = LooperState.Stopped;
            origin = 0;
            Revision++;
        }

        // ---------------------------------------------------------------------------------------------------------- song and triggers

        /// <summary>Where the song is for the next block: whether it plays, the beat of its first sample, the samples a beat takes and the beats in a bar.</summary>
        public void SetTransport(bool playing, double beat, double samplesPerBeat, int beatsPerBar)
        {
            tPlaying = playing;
            tBeat = beat;
            tSpb = samplesPerBeat > 1 ? samplesPerBeat : 24000;
            tBeatsPerBar = beatsPerBar >= 1 ? beatsPerBar : 4;
        }

        void Update(bool force)
        {
            if (!force && !v.AnyDirty) return;
            F = (int)Math.Max(1, Math.Round(v.Value(P.Crossfade) * sr / 1000.0));
            fb = (float)v.Value(P.Feedback);
            monitor = v.On(P.Monitor);
            reverse = v.On(P.Reverse);
            half = v.On(P.HalfSpeed);
            sync = v.Choice(P.Sync) == 1;
            beatQuant = v.Choice(P.Quantise) == 1;
            mulBy = 2 + v.Choice(P.MultiplyBy);
            pedalBoth = v.Choice(P.Pedal) == 1;
            double db = v.Value(P.LoopLevel);
            double g = double.IsNegativeInfinity(db) || db <= -60 ? 0 : db == 0 ? 1.0 : PortableMath.Pow(10.0, db / 20.0);
            double amt = fx.Amount;
            g = Math.Min(g * amt, 1.9952623149688795);   // +6 dB at most
            gainTarget = (float)g;
            if (force) { gain.Reset(gainTarget); lastGain = gainTarget; }
            else gain.SetTarget(gainTarget);
            v.Clean();
        }

        static readonly int[] TriggerIndex = { P.Record, P.Stop, P.Play, P.Undo, P.Redo, P.Clear, P.Multiply };
        static readonly LooperCmd[] TriggerCmd = { LooperCmd.Record, LooperCmd.Stop, LooperCmd.Play, LooperCmd.Undo, LooperCmd.Redo, LooperCmd.Clear, LooperCmd.Multiply };

        void ReadTriggers()
        {
            for (int i = 0; i < TriggerIndex.Length; i++)
            {
                int now = v[TriggerIndex[i]];
                int was = lastRaw[i];
                lastRaw[i] = now;
                if (now == was) continue;
                if (now > was || pedalBoth) Enqueue(TriggerCmd[i]);
            }
            rawKnown = true;
        }

        /// <summary>Takes the triggers' present values as the resting state (the machine calls it before its first block, so a value a song saved does not fire).</summary>
        public void ArmTriggers()
        {
            for (int i = 0; i < TriggerIndex.Length; i++) lastRaw[i] = v[TriggerIndex[i]];
            rawKnown = true;
        }

        /// <summary>A trigger from the editor or a test (the same path as a parameter change).</summary>
        public void Enqueue(LooperCmd cmd)
        {
            if (queueN >= queue.Length) return;
            bool quantised = cmd == LooperCmd.Record || cmd == LooperCmd.Stop || cmd == LooperCmd.Play || cmd == LooperCmd.Multiply;
            bool grid = quantised && sync && tPlaying;
            double land = 0;
            if (grid)
            {
                double unit = beatQuant ? 1.0 : tBeatsPerBar;
                double k = Math.Ceiling(tBeat / unit - 1e-9);
                land = k * unit;
            }
            // Commands land in the order they were pressed.
            if (queueN > 0)
            {
                var prev = queue[queueN - 1];
                if (!prev.Immediate && (!grid || prev.Beat > land)) { grid = true; land = prev.Beat; }
            }
            queue[queueN++] = new Pending { Cmd = cmd, Beat = land, Immediate = !grid };
        }

        // ---------------------------------------------------------------------------------------------------------- process

        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            fx.Begin(left, right, n);
            Update(false);
            CheckLimits();
            ReadTriggers();
            double spb = tSpb;
            bool songSync = sync && tPlaying;
            Syncing = songSync;
            double blockBeat = tBeat;

            // The song moved other than by playing on (a seek, a loop, a jump): the loop's phase moves with it.
            bool jumped = false;
            if (songSync)
            {
                if (!haveLast || !lastPlaying || Math.Abs(blockBeat - lastEndBeat) > 0.02) jumped = true;
                lastEndBeat = blockBeat + n / spb;
            }
            lastPlaying = tPlaying;
            haveLast = true;
            if (jumped && loopBeats > 0 && frames > 0 && (state == LooperState.Playing || state == LooperState.Overdubbing)) Realign(blockBeat, 0);

            // The events of this block, in order of the sample they land on.
            int evN = 0;
            Span<int> evOff = stackalloc int[20];
            Span<int> evKind = stackalloc int[20];     // 0..7: a command, 100: the song's loop wrap
            Span<double> evBeat = stackalloc double[20];
            for (int i = 0; i < queueN; i++)
            {
                int off = 0;
                if (!queue[i].Immediate)
                {
                    if (!songSync) off = 0;
                    else
                    {
                        double o = (queue[i].Beat - blockBeat) * spb;
                        if (o >= n) break;                 // a later block
                        off = o <= 0 ? 0 : (int)Math.Ceiling(o - 1e-6);
                        if (off >= n) break;
                    }
                }
                evOff[evN] = off; evKind[evN] = (int)queue[i].Cmd;
                evBeat[evN] = !queue[i].Immediate && songSync ? queue[i].Beat : blockBeat + off / spb;
                evN++;
                // Stays in the queue's order: remove it below.
            }
            int fired = evN;
            if (songSync && loopBeats > 0 && frames > 0 && (state == LooperState.Playing || state == LooperState.Overdubbing))
            {
                double ph = Mod(blockBeat - origin, loopBeats);
                double to = (loopBeats - ph) * spb;
                int w = ph < 1e-9 ? 0 : to <= 0 ? 0 : (int)Math.Ceiling(to - 1e-6);
                if (w < n) { evOff[evN] = w; evKind[evN] = 100; evBeat[evN] = blockBeat + w / spb; evN++; }
            }
            // The commands that landed leave the queue.
            if (fired > 0)
            {
                int keep = 0;
                for (int i = fired; i < queueN; i++) queue[keep++] = queue[i];
                queueN = keep;
            }
            // Insertion sort by offset (stable: commands keep their order).
            for (int i = 1; i < evN; i++)
            {
                int o = evOff[i], k = evKind[i], j = i - 1;
                double eb = evBeat[i];
                while (j >= 0 && evOff[j] > o) { evOff[j + 1] = evOff[j]; evKind[j + 1] = evKind[j]; evBeat[j + 1] = evBeat[j]; j--; }
                evOff[j + 1] = o; evKind[j + 1] = k; evBeat[j + 1] = eb;
            }

            int at = 0;
            for (int e = 0; e <= evN; e++)
            {
                int end = e < evN ? evOff[e] : n;
                for (; at < end; at++) Step(left, right, at);
                if (e < evN) Event(evKind[e], evBeat[e], spb);
            }

            ChunkCopy();
            lastGain = gain.Value;
            fx.End(left, right, n, Latency);
        }

        static double Mod(double x, double m)
        {
            double r = x - Math.Floor(x / m) * m;
            return r < 0 ? r + m : r >= m ? 0 : r;
        }

        // ---------------------------------------------------------------------------------------------------------- events

        void Event(int kind, double beat, double spb)
        {
            if (kind == 100)
            {
                // The song's loop wrapped: a loop that kept its own time (a tempo change) restarts here, in time.
                if (frames > 0 && (state == LooperState.Playing || state == LooperState.Overdubbing))
                {
                    int d = Math.Min(head.Pos, frames - head.Pos);
                    if (d > 2) Realign(beat, 0);
                }
                return;
            }
            var cmd = (LooperCmd)kind;
            switch (cmd)
            {
                case LooperCmd.Record: DoRecord(beat, spb); break;
                case LooperCmd.Stop: DoStop(); break;
                case LooperCmd.Play: DoPlay(beat); break;
                case LooperCmd.Undo: DoUndo(); break;
                case LooperCmd.Redo: DoRedo(); break;
                case LooperCmd.Clear: DoClear(); break;
                case LooperCmd.Multiply: DoMultiply(); break;
            }
        }

        void StartHeadFade(int newPos)
        {
            fromHead = head;
            xfadeTotal = xfadeLeft = F;
            head.Pos = newPos;
            head.Sub = 0;
        }

        /// <summary>The loop's frame for the song position (Sync), with a crossfade from where it was playing.</summary>
        void Realign(double beat, int _)
        {
            double ph = Mod(beat - origin, loopBeats);
            int pos = (int)Math.Round(ph * (frames / loopBeats));
            if (pos >= frames) pos = 0;
            if (reverse) pos = frames - 1 - pos;
            StartHeadFade(pos);
        }

        void DoRecord(double beat, double spb)
        {
            switch (state)
            {
                case LooperState.Empty:
                    {
                        FinishCopy();
                        int b = AllocBuffer();
                        if (b < 0) return;
                        live = b;
                        recCount = 0;
                        frames = 0;
                        state = LooperState.Recording;
                        origin = beat;
                        break;
                    }
                case LooperState.Recording: CloseTake(beat, spb); break;
                case LooperState.Playing: BeginPass(); break;
                case LooperState.Overdubbing: EndPass(); break;
                case LooperState.Stopped: DoPlay(beat); break;
            }
        }

        void CloseTake(double beat, double spb)
        {
            int n = recCount;
            double beats = 0;
            if (sync && tPlaying)
            {
                double bar = tBeatsPerBar * spb;
                int bars = Math.Max(1, (int)Math.Round(n / bar));
                while (bars > 1 && bars * bar > pool.Frames) bars--;
                int len = (int)Math.Round(bars * bar);
                if (len <= pool.Frames && len >= 1024)
                {
                    for (int i = n; i < len; i++) { pool.L[live][i] = 0f; pool.R[live][i] = 0f; }
                    n = len;
                    beats = bars * tBeatsPerBar;
                }
            }
            if (n < 1) { Free(live); live = -1; state = LooperState.Empty; return; }
            frames = n;
            loopBeats = beats;
            origin = beat;
            head = new Head { Buf = live, Pos = reverse ? n - 1 : 0, Sub = 0, Frames = n };
            state = LooperState.Playing;
            fadeIn = F;       // the loop comes in over the crossfade, from the monitored input
            fadeOut = 0;
            Revision++;
            // The tail of the take joins its head: F frames more are heard (after the take) and blended into the head when they have come.
            postWant = n > 4 * F ? F : 0;
            postCount = 0;
        }

        void BeginPass()
        {
            FinishCopy();
            int b = AllocBufferForPass();
            if (b < 0) return;
            StartCopy(live, frames, b, frames);
            live = b;
            head.Buf = b;
            state = LooperState.Overdubbing;
            punchingOut = false;
            rampCount = 0;
            postWant = 0;
            Revision++;
        }

        /// <summary>A free buffer for a pass: the redo levels go, the oldest undo level goes when the history is full, and the live one becomes the newest undo level.</summary>
        int AllocBufferForPass()
        {
            for (int i = 0; i < redoN; i++) Free(redoBuf[i]);
            redoN = 0;
            int old = live;
            if (undoN >= pool.Levels)
            {
                Free(undoBuf[0]);
                for (int i = 1; i < undoN; i++) { undoBuf[i - 1] = undoBuf[i]; undoFrames[i - 1] = undoFrames[i]; undoBeats[i - 1] = undoBeats[i]; }
                undoN--;
            }
            int b = AllocBuffer();
            if (b < 0) return -1;
            undoBuf[undoN] = old; undoFrames[undoN] = frames; undoBeats[undoN] = loopBeats; undoN++;
            return b;
        }

        void StartCopy(int src, int srcFrames, int dst, int dstFrames)
        {
            copyPending = true;
            copySrc = src;
            copySrcFrames = srcFrames;
            copyFront = 0;
            copyDstFrames = dstFrames;
            Array.Clear(pool.Have, 0, dstFrames);
        }

        int copyDstFrames;

        void ChunkCopy()
        {
            if (!copyPending) return;
            var p = pool;
            int dst = live;
            int end = Math.Min(copyDstFrames, copyFront + CopyChunk);
            for (int i = copyFront; i < end; i++)
            {
                if (p.Have[i] != 0) continue;
                int s = copySrcFrames == copyDstFrames ? i : i % copySrcFrames;
                p.L[dst][i] = p.L[copySrc][s];
                p.R[dst][i] = p.R[copySrc][s];
                p.Have[i] = 1;
            }
            copyFront = end;
            if (copyFront >= copyDstFrames) copyPending = false;
        }

        void FinishCopy()
        {
            while (copyPending) ChunkCopy();
        }

        void EndPass()
        {
            punchingOut = true;
            if (rampCount <= 0) { punchingOut = false; state = LooperState.Playing; }
        }

        void DoStop()
        {
            if (state == LooperState.Playing || state == LooperState.Overdubbing)
            {
                if (state == LooperState.Overdubbing) punchingOut = true;
                fadeOut = F;
                fadeIn = 0;
                stopping = true;
            }
            else if (state == LooperState.Recording)
            {
                CloseTake(origin, tSpb);
                if (state == LooperState.Playing) { fadeOut = F; fadeIn = 0; stopping = true; }
            }
        }

        void DoPlay(double beat)
        {
            if (state != LooperState.Stopped || frames <= 0) return;
            head.Pos = reverse ? frames - 1 : 0;
            head.Sub = 0;
            head.Buf = live;
            head.Frames = frames;
            origin = beat;
            fadeIn = F;
            fadeOut = 0;
            state = LooperState.Playing;
        }

        void PushOutputFade()
        {
            fromHead = head;
            xfadeTotal = xfadeLeft = F;
        }

        void DoUndo()
        {
            if (undoN == 0) return;
            if (state == LooperState.Recording) return;
            FinishCopy();
            PushOutputFade();
            if (redoN < 8) { redoBuf[redoN] = live; redoFrames[redoN] = frames; redoBeats[redoN] = loopBeats; redoN++; }
            else Free(live);
            undoN--;
            SetLive(undoBuf[undoN], undoFrames[undoN], undoBeats[undoN]);
        }

        void DoRedo()
        {
            if (redoN == 0) return;
            if (state == LooperState.Recording) return;
            FinishCopy();
            PushOutputFade();
            if (undoN < 8) { undoBuf[undoN] = live; undoFrames[undoN] = frames; undoBeats[undoN] = loopBeats; undoN++; }
            redoN--;
            SetLive(redoBuf[redoN], redoFrames[redoN], redoBeats[redoN]);
        }

        void SetLive(int buf, int count, double beats)
        {
            live = buf;
            frames = count;
            loopBeats = beats;
            head.Buf = buf;
            head.Frames = count;
            if (count <= 0) { state = LooperState.Empty; head.Pos = 0; }
            else
            {
                if (state == LooperState.Empty) { state = LooperState.Playing; head.Pos = 0; head.Sub = 0; }
                if (state == LooperState.Overdubbing) { state = LooperState.Playing; punchingOut = false; rampCount = 0; }
                head.Pos %= count;
            }
            Revision++;
        }

        void DoClear()
        {
            if (state == LooperState.Empty) return;
            FinishCopy();
            if (state == LooperState.Recording) { Free(live); live = -1; frames = 0; state = LooperState.Empty; return; }
            // The old loop is the undo level; the empty loop needs no buffer.
            for (int i = 0; i < redoN; i++) Free(redoBuf[i]);
            redoN = 0;
            if (undoN >= pool.Levels)
            {
                Free(undoBuf[0]);
                for (int i = 1; i < undoN; i++) { undoBuf[i - 1] = undoBuf[i]; undoFrames[i - 1] = undoFrames[i]; undoBeats[i - 1] = undoBeats[i]; }
                undoN--;
            }
            PushOutputFade();
            undoBuf[undoN] = live; undoFrames[undoN] = frames; undoBeats[undoN] = loopBeats; undoN++;
            live = -1;
            frames = 0;
            loopBeats = 0;
            head.Buf = -1;
            head.Frames = 0;
            state = LooperState.Empty;
            rampCount = 0;
            punchingOut = false;
            Revision++;
        }

        void DoMultiply()
        {
            // Not while a pass is recording: close it first (its fade is not cut short).
            if (state != LooperState.Playing && state != LooperState.Stopped) return;
            long len = (long)frames * mulBy;
            if (len > pool.Frames) return;       // does not fit the limit
            FinishCopy();
            int b = AllocBufferForPass();
            if (b < 0) return;
            StartCopy(live, frames, b, (int)len);
            live = b;
            frames = (int)len;
            if (loopBeats > 0) loopBeats *= mulBy;
            head.Buf = b;
            head.Frames = frames;
            Revision++;
        }

        // ---------------------------------------------------------------------------------------------------------- the sample loop

        /// <summary>One frame of the loop at a head: the live buffer's pending frames come from the old buffer.</summary>
        void Read(ref Head h, out float l, out float r)
        {
            if (h.Buf < 0 || h.Frames <= 0) { l = r = 0f; return; }
            var p = pool;
            int pos = h.Pos;
            ReadAt(h.Buf, h.Frames, pos, out l, out r);
            if (h.Sub != 0)
            {
                int next = reverse ? pos - 1 : pos + 1;
                if (next >= h.Frames) next = 0;
                if (next < 0) next = h.Frames - 1;
                ReadAt(h.Buf, h.Frames, next, out float l2, out float r2);
                l = 0.5f * (l + l2);
                r = 0.5f * (r + r2);
            }
        }

        void ReadAt(int buf, int count, int pos, out float l, out float r)
        {
            var p = pool;
            if (copyPending && buf == live && p.Have[pos] == 0)
            {
                int s = copySrcFrames == copyDstFrames ? pos : pos % copySrcFrames;
                l = p.L[copySrc][s];
                r = p.R[copySrc][s];
            }
            else
            {
                l = p.L[buf][pos];
                r = p.R[buf][pos];
            }
        }

        void Advance(ref Head h)
        {
            if (half)
            {
                if (h.Sub == 0) { h.Sub = 1; return; }
                h.Sub = 0;
            }
            else h.Sub = 0;
            if (reverse) { h.Pos--; if (h.Pos < 0) h.Pos = h.Frames - 1; }
            else { h.Pos++; if (h.Pos >= h.Frames) h.Pos = 0; }
        }

        float inHoldL, inHoldR;

        void Step(float[] left, float[] right, int i)
        {
            float inL = left[i], inR = right[i];
            float lpL = 0f, lpR = 0f;
            float g = gain.Next();

            // The post-roll after a take: the input that follows the take, blended into its head when it is whole.
            if (postWant > 0)
            {
                postL[postCount] = inL;
                postR[postCount] = inR;
                if (++postCount >= postWant) { BlendPost(); postWant = 0; }
            }

            switch (state)
            {
                case LooperState.Recording:
                    {
                        if (recCount < pool.Frames)
                        {
                            pool.L[live][recCount] = inL;
                            pool.R[live][recCount] = inR;
                            recCount++;
                        }
                        if (recCount >= pool.Frames) CloseTake(origin, tSpb);
                        break;
                    }
                case LooperState.Playing:
                case LooperState.Overdubbing:
                    {
                        Read(ref head, out lpL, out lpR);
                        if (xfadeLeft > 0)
                        {
                            Read(ref fromHead, out float fl, out float fr);
                            float t = (float)(xfadeLeft) / xfadeTotal;       // the old signal's share, going from 1 to 0
                            float so = Equal(1f - t), si = Equal(t);
                            lpL = lpL * so + fl * si;
                            lpR = lpR * so + fr * si;
                            Advance(ref fromHead);
                            xfadeLeft--;
                        }
                        float fade = 1f;
                        if (fadeIn > 0) { fade = Raised(1f - (float)fadeIn / F); fadeIn--; }
                        if (fadeOut > 0)
                        {
                            fade *= Raised((float)(fadeOut - 1) / F);
                            fadeOut--;
                        }
                        lpL *= fade;
                        lpR *= fade;

                        if (state == LooperState.Overdubbing) Overdub(inL, inR);

                        Advance(ref head);
                        if (stopping && fadeOut == 0)
                        {
                            stopping = false;
                            punchingOut = false;
                            rampCount = 0;
                            state = LooperState.Stopped;
                        }
                        break;
                    }
            }

            float oL = (monitor ? inL : 0f) + lpL * g;
            float oR = (monitor ? inR : 0f) + lpR * g;
            left[i] = oL;
            right[i] = oR;
        }

        bool stopping;

        static float Raised(float x)
        {
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            return (float)(0.5 - 0.5 * PortableMath.Cos(Math.PI * x));
        }

        static float Equal(float x)
        {
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            return (float)PortableMath.Sin(0.5 * Math.PI * x);
        }

        void BlendPost()
        {
            if (state == LooperState.Empty || live < 0 || frames < 4 * postWant || copyPending) return;
            var p = pool;
            int n = postWant;
            for (int j = 0; j < n; j++)
            {
                float t = (float)j / n;
                float c = Equal(1f - t), s = Equal(t);
                p.L[live][j] = postL[j] * c + p.L[live][j] * s;
                p.R[live][j] = postR[j] * c + p.R[live][j] * s;
            }
            Revision++;
        }

        void Overdub(float inL, float inR)
        {
            // Half speed: two input frames make one loop frame (their mean), written on the second.
            float wl = inL, wr = inR;
            if (half)
            {
                if (head.Sub == 0) { inHoldL = inL; inHoldR = inR; RampStep(); return; }
                wl = 0.5f * (inHoldL + inL);
                wr = 0.5f * (inHoldR + inR);
            }
            float r = RampValue();
            var p = pool;
            int pos = head.Pos;
            ReadAt(live, frames, pos, out float ol, out float orr);
            float og = r >= 1f ? fb : 1f - (1f - fb) * r;
            p.L[live][pos] = ol * og + wl * r;
            p.R[live][pos] = orr * og + wr * r;
            p.Have[pos] = 1;
            RampStep();
        }

        float RampValue()
        {
            if (rampCount >= F) return 1f;
            return Raised((float)rampCount / F);
        }

        void RampStep()
        {
            if (punchingOut)
            {
                if (rampCount > 0) rampCount--;
                if (rampCount <= 0)
                {
                    punchingOut = false;
                    state = LooperState.Playing;
                    Revision++;
                }
            }
            else if (rampCount < F) rampCount++;
        }
    }
}
