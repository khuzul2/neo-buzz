#nullable disable
// Neo Space (docs/specs/neo-space.md): a convolution reverb. Zero latency (reported as 0): the convolver is the shared StereoConvolver
// (NeoBuzz.Dsp), a non-uniformly partitioned FFT convolution whose first 64 taps run in the time domain, sample by sample, and whose four
// FFT stages (partitions of 64, 256, 1024 and 4096 samples, their block grids offset from one another) each need only blocks that are
// complete, so the output of a block is ready when its input has ended, whatever the host's block size. No latency to compensate, so
// the dry sound is never delayed and Mix never combs.
//
//   input ──► [ the playing convolver ]──┐
//                                        ├─► crossfade (50 ms, equal power) ──► × wet gain ─┐
//   (a new one, while it is being swapped in) ┘                                              ├─► out
//   input ───────────────────────────────────────────────────────────────────── × dry gain ──┘
//
// A response is prepared off the audio thread: the room is generated (or the song's wave resampled), shaped (SpaceIr) and the convolver
// built (its partition spectra transformed) by the builder thread. The audio thread asks for it by writing a request (a key made of the
// parameters that shape the response and the wave's version), flips a flag, and, when the builder says it is done, takes the new
// convolver and crossfades to it over 50 ms. At most one request is outstanding; a parameter that moves while one is being built is
// asked for again as soon as the builder is free, so the last setting always arrives. Nothing is allocated on the audio thread.
//
// Mix and Amount (docs/specs/machines.md, "Mix and Amount"): the shared FxStage owns Amount's bypass (0 %: the input, bit for bit);
// above it the wet gain is Mix × Amount (200 % is twice the wet signal, +6 dB) and the dry gain is 1 − Mix × min(1, Amount), so Amount
// 0 is dry exactly and 100 % is Mix as set. Output (dB) scales the wet signal. Both glide over 20 ms.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Space
{
    /// <summary>A wave of the song read in: mono or stereo samples at a rate, with the version of what was read.</summary>
    internal sealed class SpaceClip
    {
        public readonly string Name;
        public readonly float[] Left, Right;
        public readonly int Rate, Version;

        public SpaceClip(string name, float[] left, float[] right, int rate, int version)
        {
            Name = name ?? "";
            Left = left;
            Right = right;
            Rate = rate;
            Version = version;
        }
    }

    internal enum SpaceWaveState { Missing, Loading, Ready }

    /// <summary>Where the engine gets the song's waves from (the machine's reader; a test's stand-in).</summary>
    internal interface ISpaceWaves
    {
        /// <summary>Slot 1 … 200: the wave is in the song and read in, being read, or not there.</summary>
        SpaceWaveState State(int slot);

        /// <summary>The wave of the slot, read in (null unless <see cref="State"/> says Ready).</summary>
        SpaceClip Get(int slot);

        /// <summary>The engine wants this slot (0: none): the reader reads that one.</summary>
        void Want(int slot);
    }

    internal enum SpaceStatus
    {
        /// <summary>The response playing is the one the parameters ask for.</summary>
        Ready,
        /// <summary>A new response is being prepared.</summary>
        Preparing,
        /// <summary>The wave is being read from the song.</summary>
        WaveLoading,
        /// <summary>The wave is not in the song: the hall plays instead.</summary>
        WaveMissing,
    }

    /// <summary>What the builder made: a convolver and what describes it.</summary>
    internal sealed class SpaceBuilt
    {
        public StereoConvolver Convolver;
        /// <summary>The shaped response the convolver was built from (for tests and the picture's measures).</summary>
        public float[] Left, Right;
        /// <summary>The unshaped response's picture (dB per column) and its length in seconds: the trim and pre-delay are drawn on it.</summary>
        public float[] Preview;
        public double BaseSeconds;
        public string Name;
        public bool FellBack;
        public int Rate;
        public SpaceKey Key;
    }

    /// <summary>Everything that decides the response: the raw values of the parameters that shape it and the wave's version.</summary>
    internal readonly struct SpaceKey : IEquatable<SpaceKey>
    {
        public readonly int Rate, Room, Slot, WaveVersion, PreDelay, Length, LowCut, HighCut, Width, Reverse;

        public SpaceKey(int rate, int room, int slot, int waveVersion, int[] v)
        {
            Rate = rate; Room = room; Slot = slot; WaveVersion = waveVersion;
            PreDelay = v[P.PreDelay]; Length = v[P.Length]; LowCut = v[P.LowCut]; HighCut = v[P.HighCut]; Width = v[P.Width]; Reverse = v[P.Reverse];
        }

        public bool Equals(SpaceKey o) => Rate == o.Rate && Room == o.Room && Slot == o.Slot && WaveVersion == o.WaveVersion && PreDelay == o.PreDelay
            && Length == o.Length && LowCut == o.LowCut && HighCut == o.HighCut && Width == o.Width && Reverse == o.Reverse;

        public override bool Equals(object o) => o is SpaceKey k && Equals(k);

        public override int GetHashCode() => HashCode.Combine(Rate, Room, Slot, WaveVersion, PreDelay, Length, HashCode.Combine(LowCut, HighCut, Width, Reverse));
    }

    internal sealed class SpaceEngine
    {
        public const int MaxBlock = 256;

        /// <summary>Samples of delay the host compensates: none (see the file's header).</summary>
        public const int Latency = 0;

        /// <summary>Length of the crossfade from one response to the next.</summary>
        public const double FadeSeconds = 0.05;

        /// <summary>The Room choice that means "the song's wave".</summary>
        public const int WaveRoom = SpaceRooms.Count;

        readonly ParamValues values;
        readonly FxStage fx;
        readonly ISpaceWaves waves;

        int rate = 48000;
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        // ---- the builder's hand-over: 0 idle, 1 requested, 2 building, 3 built (waiting for the audio thread), 4 a request being written
        int state;
        SpaceKey wanted, requested;
        bool haveWanted;
        SpaceBuilt incoming;
        volatile SpaceBuilt currentPublished;
        bool requestedFell;
        // The builder's cache of the base response (room or wave at the host's rate).
        int cacheRoom = -1, cacheSlot, cacheVersion, cacheRate;
        float[] baseL, baseR;
        float[] basePreview;
        double baseSeconds;
        string baseName = "";

        // ---- the audio thread's
        SpaceBuilt current, old;
        int fade, fadeLength;
        bool stale;
        long sinceInput;
        readonly float[] wetL = new float[MaxBlock], wetR = new float[MaxBlock], oldL = new float[MaxBlock], oldR = new float[MaxBlock];
        SmoothRamp dryGain, wetGain;
        bool gainsStarted;
        volatile SpaceStatus status;

        public SpaceEngine(ParamValues values, ISpaceWaves waves = null)
        {
            this.values = values;
            this.waves = waves;
            SpaceBuilder.Register(this);
            fx = new FxStage(values, P.Amount, -1, Array.Empty<AmountRule>(), maxLatency: Latency, maxBlock: MaxBlock);
            SetSampleRate(48000);
        }

        public void SetSampleRate(float sr)
        {
            int r = (int)Math.Round(sr);
            if (r != rate || !gainsStarted)
            {
                rate = r;
                // A response made for another rate is no good: the new one is asked for at once.
                if (current != null && current.Rate != rate) { current = null; old = null; fade = 0; currentPublished = null; }
                haveWanted = false;
            }
            fx.SetSampleRate(sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            fadeLength = Math.Max(1, (int)Math.Round(FadeSeconds * rate));
            dryGain.Setup(0.02, sr); wetGain.Setup(0.02, sr);
            Reset();
        }

        /// <summary>Back to the start: the convolvers' histories are cleared (the playing response stays).</summary>
        public void Reset()
        {
            fx.Reset();
            stale = true;
            gainsStarted = false;
            sinceInput = 0;
        }

        /// <summary>The effective parameter values (Amount applied: none of ours has a rule, so the raw ones).</summary>
        public ParamValues Effective => fx.Effective;

        /// <summary>The response playing now (null before the first one is ready).</summary>
        public SpaceBuilt Current => currentPublished;

        /// <summary>What the engine is doing about the response the parameters ask for (the editor says it).</summary>
        public SpaceStatus Status => status;

        /// <summary>The builder has not finished the response asked for, or the wave is being read: a render waits for this to be false (call <see cref="Poll"/> first).</summary>
        public bool Pending
        {
            get
            {
                int s = Volatile.Read(ref state);
                if (s == 1 || s == 2 || s == 4) return true;
                return waves != null && values[P.Room] == WaveRoom && waves.State(values[P.Wave] + 1) == SpaceWaveState.Loading;
            }
        }

        /// <summary>Samples the tail of the playing response lasts: the machine rests after this much silence.</summary>
        public int TailSamples
        {
            get
            {
                var c = currentPublished;
                int n = c?.Left?.Length ?? 0;
                return n + (old != null ? old.Left.Length : 0) + 2 * MaxBlock;
            }
        }

        readonly int[] snap = new int[P.Count];

        /// <summary>Asks for the response the parameters name now (the audio thread, or any other: it only writes a request).</summary>
        public void Poll()
        {
            for (int i = 0; i < snap.Length; i++) snap[i] = values[i];
            Ask(snap);
        }

        /// <summary>The same for the values the host holds now (the song's thread: a value just set reaches the engine only at the next tick).</summary>
        public void Poll(Func<int, int> hostValue)
        {
            var a = new int[P.Count];
            for (int i = 0; i < a.Length; i++) a[i] = hostValue(i);
            Ask(a);
        }

        void Ask(int[] v)
        {
            int room = v[P.Room];
            int slot = 0, version = 0;
            bool fell = false;
            if (room == WaveRoom)
            {
                slot = v[P.Wave] + 1;
                if (waves != null) waves.Want(slot);
                var st = waves == null ? SpaceWaveState.Missing : waves.State(slot);
                if (st == SpaceWaveState.Ready)
                {
                    var clip = waves.Get(slot);
                    if (clip != null) version = clip.Version;
                    else st = SpaceWaveState.Loading;
                }
                if (st == SpaceWaveState.Loading)
                {
                    status = SpaceStatus.WaveLoading;
                    return;   // keep what plays until the wave is in
                }
                if (st == SpaceWaveState.Missing)
                {
                    room = SpaceRooms.Fallback;
                    slot = 0;
                    fell = true;
                }
            }
            else waves?.Want(0);
            var key = new SpaceKey(rate, room, slot, version, v);
            if (haveWanted && key.Equals(wanted))
            {
                status = fell ? SpaceStatus.WaveMissing : (Volatile.Read(ref state) == 0 && current != null && current.Key.Equals(key) ? SpaceStatus.Ready : SpaceStatus.Preparing);
                return;
            }
            if (Volatile.Read(ref state) != 0) { status = SpaceStatus.Preparing; return; }
            if (Interlocked.CompareExchange(ref state, 4, 0) != 0) return;
            requested = key;
            requestedFell = fell;
            wanted = key;
            haveWanted = true;
            status = fell ? SpaceStatus.WaveMissing : SpaceStatus.Preparing;
            Volatile.Write(ref state, 1);
            SpaceBuilder.Kick(this);
        }

        /// <summary>Whether the builder has a request to work on.</summary>
        internal bool HasWork => Volatile.Read(ref state) == 1;

        /// <summary>The builder's job (never the audio thread's): makes the response of the request.</summary>
        internal void Work()
        {
            if (Interlocked.CompareExchange(ref state, 2, 1) != 1) return;
            try
            {
                var key = requested;
                var d = Params.Table;
                bool isWave = key.Room == WaveRoom;
                if (key.Room != cacheRoom || key.Slot != cacheSlot || key.WaveVersion != cacheVersion || key.Rate != cacheRate || baseL == null)
                {
                    MakeBase(key, isWave);
                    cacheRoom = key.Room; cacheSlot = key.Slot; cacheVersion = key.WaveVersion; cacheRate = key.Rate;
                }
                var s = new SpaceSettings
                {
                    PreDelayMs = d[P.PreDelay].Value(key.PreDelay),
                    LengthPercent = d[P.Length].Value(key.Length),
                    LowCutHz = d[P.LowCut].Value(key.LowCut),
                    HighCutHz = d[P.HighCut].Value(key.HighCut),
                    WidthPercent = d[P.Width].Value(key.Width),
                    Reverse = key.Reverse != 0,
                };
                SpaceIr.Shape(baseL, baseR, key.Rate, s, isWave, out var l, out var r, out int start, out int count);
                var built = new SpaceBuilt
                {
                    Convolver = new StereoConvolver(l, r),
                    Left = l, Right = r,
                    Preview = SpaceIr.Preview(baseL, baseR, start, count),
                    BaseSeconds = count / (double)key.Rate,
                    Name = baseName,
                    FellBack = requestedFell,
                    Rate = key.Rate,
                    Key = key,
                };
                Volatile.Write(ref incoming, built);
                Volatile.Write(ref state, 3);
            }
            catch (Exception)
            {
                // A response that cannot be made leaves the last one playing (and is asked for again only when a parameter moves).
                Volatile.Write(ref state, 0);
            }
        }

        void MakeBase(SpaceKey key, bool isWave)
        {
            if (isWave)
            {
                var clip = waves?.Get(key.Slot);
                if (clip == null || clip.Left == null || clip.Left.Length < 16) throw new InvalidOperationException("no wave");
                double step = clip.Rate / (double)key.Rate;
                baseL = ImpulseResponses.Resample(clip.Left, step);
                baseR = clip.Right == null ? baseL : ImpulseResponses.Resample(clip.Right, step);
                baseName = clip.Name;
            }
            else
            {
                SpaceRooms.Generate(key.Room, key.Rate, out baseL, out baseR);
                baseName = SpaceRooms.Names[key.Room];
            }
        }

        /// <summary>For tests (one thread): asks for the response the parameters name and has it made and taken, here and now.</summary>
        public void BuildNow()
        {
            for (int guard = 0; guard < 8; guard++)
            {
                WaitForBuilder();
                if (Volatile.Read(ref state) == 3) TakeIncoming();
                Poll();
                int s = Volatile.Read(ref state);
                if (s == 1) Work();   // (the builder thread may have it already: then this returns at once)
                else if (s == 0) return;
                // 2, 3 or 4: the builder is on it or has finished: the next round waits for it and takes it.
            }
        }

        /// <summary>Test hook: waits until the builder has finished (the response waits for the audio thread, or none was asked for).</summary>
        public void WaitForBuilder()
        {
            var until = Environment.TickCount64 + 60000;
            while (Environment.TickCount64 < until)
            {
                int s = Volatile.Read(ref state);
                if (s == 0 || s == 3) return;
                Thread.Sleep(1);
            }
        }

        /// <summary>Takes the response the builder made, if there is one: the playing one fades out under it.</summary>
        void TakeIncoming()
        {
            if (Volatile.Read(ref state) != 3) return;
            var next = Volatile.Read(ref incoming);
            Volatile.Write(ref incoming, null);
            if (current != null && !stale)
            {
                old = current;
                fade = fadeLength;
            }
            else
            {
                old = null;
                fade = 0;
            }
            current = next;
            currentPublished = next;
            if (status == SpaceStatus.Preparing && next.Key.Equals(wanted)) status = SpaceStatus.Ready;
            Volatile.Write(ref state, 0);
        }

        /// <summary>The machine did not run for a while (the input was silent for longer than the tail): the convolvers' histories are cleared at the next block.</summary>
        public void Skipped() { stale = true; }

        /// <summary>One block, in place (at most <see cref="MaxBlock"/> samples).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] left, float[] right, int n)
        {
            InMeter.Feed(left, right, n);
            fx.Begin(left, right, n);
            Poll();
            TakeIncoming();
            if (stale)
            {
                current?.Convolver.Reset();
                old = null;
                fade = 0;
                stale = false;
            }
            double a = fx.Amount, mix = Params.Table[P.Mix].Value(values[P.Mix]);
            float outGain = (float)DspMath.DbToGain(Params.Table[P.Output].Value(values[P.Output]));
            float wetTarget = (float)(mix * a) * outGain;
            float dryTarget = (float)(1.0 - mix * Math.Min(1.0, a));
            if (!gainsStarted)
            {
                dryGain.Reset(dryTarget);
                wetGain.Reset(wetTarget);
                gainsStarted = true;
            }
            else
            {
                dryGain.SetTarget(dryTarget);
                wetGain.SetTarget(wetTarget);
            }
            var c = current;
            if (c != null)
            {
                Array.Copy(left, wetL, n);
                Array.Copy(right, wetR, n);
                c.Convolver.Process(wetL, wetR, n);
                if (old != null)
                {
                    Array.Copy(left, oldL, n);
                    Array.Copy(right, oldR, n);
                    old.Convolver.Process(oldL, oldR, n);
                    // Equal power: the old response falls as cos, the new rises as sin, over the fade.
                    for (int i = 0; i < n; i++)
                    {
                        float x = fade > 0 ? 1f - fade / (float)fadeLength : 1f;
                        float up = (float)PortableMath.Sin(0.5 * Math.PI * x), down = (float)PortableMath.Cos(0.5 * Math.PI * x);
                        wetL[i] = oldL[i] * down + wetL[i] * up;
                        wetR[i] = oldR[i] * down + wetR[i] * up;
                        if (fade > 0) fade--;
                    }
                    if (fade <= 0) old = null;
                }
            }
            else
            {
                Array.Clear(wetL, 0, n);
                Array.Clear(wetR, 0, n);
            }
            for (int i = 0; i < n; i++)
            {
                float dg = dryGain.Next(), wg = wetGain.Next();
                if (dg == 1f && wg == 0f) continue;
                left[i] = left[i] * dg + wetL[i] * wg;
                right[i] = right[i] * dg + wetR[i] * wg;
            }
            fx.End(left, right, n, Latency);
            OutMeter.Feed(left, right, n);
        }
    }

    /// <summary>The one background thread that makes the engines' responses.</summary>
    internal static class SpaceBuilder
    {
        static readonly object gate = new object();
        static readonly List<WeakReference<SpaceEngine>> engines = new List<WeakReference<SpaceEngine>>();
        static readonly ManualResetEventSlim signal = new ManualResetEventSlim(false);
        static Thread worker;

        /// <summary>Makes the engine known to the builder (when it is made, off the audio thread: a request later allocates nothing).</summary>
        public static void Register(SpaceEngine engine)
        {
            lock (gate)
            {
                engines.RemoveAll(w => !w.TryGetTarget(out _));
                engines.Add(new WeakReference<SpaceEngine>(engine));
                if (worker == null)
                {
                    var t = new Thread(Run) { IsBackground = true, Name = "Neo Space builder", Priority = ThreadPriority.BelowNormal };
                    worker = t;
                    t.Start();
                }
            }
        }

        /// <summary>Wakes the builder (any thread, allocation-free).</summary>
        public static void Kick(SpaceEngine engine) => signal.Set();

        static void Run()
        {
            var live = new List<SpaceEngine>();
            while (true)
            {
                signal.Wait();
                signal.Reset();
                live.Clear();
                lock (gate)
                    foreach (var w in engines)
                        if (w.TryGetTarget(out var e)) live.Add(e);
                foreach (var e in live)
                    if (e.HasWork) e.Work();
            }
        }
    }
}
