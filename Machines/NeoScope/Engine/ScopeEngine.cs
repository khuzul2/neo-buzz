#nullable disable
// Neo Scope (docs/specs/machines.md §5): the oscilloscope and the vectorscope, on the audio thread.
// The audio passes through untouched (the machine copies it); the engine only looks at it. Each
// sweep of the oscilloscope is kept as the lowest and highest value of each column (at most 1024
// columns), so a sweep of four bars costs no more to hand over than one of a millisecond. A sweep
// is free (a time per division, with an optional edge trigger) or synced to the song: the screen
// is a note value of the tempo, and sweeps start on its multiples counted from the song's start, so
// a loop draws the same frame on every pass. Snapshots go to the editor and the agent server
// through a lock-free triple buffer at most 60 times a second of audio: the editors draw at the
// display's rate, not per block. Everything is allocated when the engine is made or the sample
// rate set; a block only computes.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using NeoBuzz.Analysis;
using NeoBuzz.Dsp;

namespace NeoBuzz.Scope
{
    /// <summary>Where the song is at a block's first sample, as the host reports it in Work.</summary>
    internal struct ScopeTransport
    {
        /// <summary>The song plays (its position moves on).</summary>
        public bool Playing;

        /// <summary>The song's position in ticks, and the samples into that tick.</summary>
        public int Tick, PosInTick;

        /// <summary>The tick's length in samples, and the ticks in a beat.</summary>
        public int SamplesPerTick, TicksPerBeat;

        /// <summary>The tempo: a synced sweep's length while the song stands still.</summary>
        public double Bpm;

        /// <summary>The same transport <paramref name="n"/> samples later (ticks of this length).</summary>
        public void Advance(int n)
        {
            if (!Playing || SamplesPerTick <= 0) return;
            PosInTick += n;
            while (PosInTick >= SamplesPerTick)
            {
                PosInTick -= SamplesPerTick;
                Tick++;
            }
        }
    }

    /// <summary>One sweep of the oscilloscope: the lowest and highest value of each column, of one trace or two.</summary>
    internal sealed class ScopeFrame
    {
        public const int MaxColumns = 1024;

        /// <summary>Trace A (and B, the right channel, with L &amp; R): per column, its lowest and highest value (empty where min &gt; max).</summary>
        public readonly float[] AMin = new float[MaxColumns], AMax = new float[MaxColumns];
        public readonly float[] BMin = new float[MaxColumns], BMax = new float[MaxColumns];

        /// <summary>Columns across the screen: at most <see cref="MaxColumns"/>, a sample each when the sweep is shorter.</summary>
        public int Columns = 1;

        /// <summary>The sweep's length in samples.</summary>
        public int Samples = 1;

        /// <summary>Columns up to the last one written (the sweep in progress fills them).</summary>
        public int Filled;

        /// <summary>What the traces are: the Display parameter's choice when the sweep was taken.</summary>
        public int Display;

        /// <summary>Synced to the song: where the sweep starts, in beats from the song's start, and its length in beats.</summary>
        public bool Synced;
        public double StartBeat, Beats;

        /// <summary>The sweep's length in seconds.</summary>
        public double Seconds;

        /// <summary>Free: the sweep began on the trigger, which crossed the level <see cref="Offset"/> samples before the sweep's first sample (0 … 1).</summary>
        public bool Triggered;
        public double Offset;

        /// <summary>Sweeps completed before this one.</summary>
        public long Number;

        /// <summary>Two traces: left (A) and right (B).</summary>
        public bool Two => Display == ScopeEngine.DisplayLR;

        /// <summary>Whether column <paramref name="c"/> holds a sample.</summary>
        public bool Has(int c) => AMin[c] <= AMax[c];

        /// <summary>An empty sweep of <paramref name="columns"/> columns and <paramref name="samples"/> samples.</summary>
        // Fully optimised at its first compile (the warm-up's), so the audio thread never compiles an on-stack replacement of its loops (AudioWarmUp).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Begin(int columns, int samples, int display)
        {
            Columns = Math.Clamp(columns, 1, MaxColumns);
            Samples = Math.Max(1, samples);
            Display = display;
            Filled = 0;
            Synced = false;
            StartBeat = Beats = Seconds = Offset = 0;
            Triggered = false;
            for (int i = 0; i < Columns; i++)
            {
                AMin[i] = BMin[i] = float.PositiveInfinity;
                AMax[i] = BMax[i] = float.NegativeInfinity;
            }
        }

        public void CopyTo(ScopeFrame o)
        {
            o.Columns = Columns; o.Samples = Samples; o.Filled = Filled; o.Display = Display;
            o.Synced = Synced; o.StartBeat = StartBeat; o.Beats = Beats; o.Seconds = Seconds;
            o.Triggered = Triggered; o.Offset = Offset; o.Number = Number;
            Array.Copy(AMin, o.AMin, Columns);
            Array.Copy(AMax, o.AMax, Columns);
            if (Two)
            {
                Array.Copy(BMin, o.BMin, Columns);
                Array.Copy(BMax, o.BMax, Columns);
            }
        }

        /// <summary>Whether two sweeps hold the same values in the same columns (tests: a loop's passes).</summary>
        public bool SameTraces(ScopeFrame o)
        {
            if (o.Columns != Columns || o.Display != Display) return false;
            for (int i = 0; i < Columns; i++)
                if (AMin[i] != o.AMin[i] || AMax[i] != o.AMax[i] || (Two && (BMin[i] != o.BMin[i] || BMax[i] != o.BMax[i])))
                    if (Has(i) || o.Has(i)) return false;
            return true;
        }
    }

    /// <summary>Everything the scope shows at one moment: written by the audio thread, copied out by readers.</summary>
    internal sealed class ScopeSnapshot
    {
        public const int VectorPoints = 1024;

        /// <summary>Snapshots published before this one, samples looked at, the sample rate and the tempo.</summary>
        public long Sequence, Samples;
        public int Rate;
        public double Bpm;

        /// <summary>The last complete sweep (when <see cref="HasFrame"/>) and the one being taken (when <see cref="HasLive"/>).</summary>
        public readonly ScopeFrame Frame = new ScopeFrame(), Live = new ScopeFrame();
        public bool HasFrame, HasLive;

        /// <summary>Freeze is on: the sweeps and the vectorscope hold.</summary>
        public bool Frozen;

        /// <summary>The trigger is in use (Sync Free, Rising or Falling), and the last sweep began on it.</summary>
        public bool TriggerOn, TriggerLocked;

        /// <summary>The latest samples (oldest first), for the vectorscope: <see cref="VectorCount"/> of them.</summary>
        public readonly float[] VectorL = new float[VectorPoints], VectorR = new float[VectorPoints];
        public int VectorCount;

        /// <summary>Over the last 3 s: the highest sample (dBFS), the RMS of both channels (dB: a full-scale sine −3 dB) and the correlation.</summary>
        public double PeakDb = double.NegativeInfinity, RmsDb = double.NegativeInfinity, Correlation = 1;

        /// <summary>The correlation over the last 400 ms (the meter).</summary>
        public double CorrelationNow = 1;

        public void CopyTo(ScopeSnapshot o)
        {
            o.Sequence = Sequence; o.Samples = Samples; o.Rate = Rate; o.Bpm = Bpm;
            Frame.CopyTo(o.Frame);
            Live.CopyTo(o.Live);
            o.HasFrame = HasFrame; o.HasLive = HasLive; o.Frozen = Frozen;
            o.TriggerOn = TriggerOn; o.TriggerLocked = TriggerLocked;
            Array.Copy(VectorL, o.VectorL, VectorCount);
            Array.Copy(VectorR, o.VectorR, VectorCount);
            o.VectorCount = VectorCount;
            o.PeakDb = PeakDb; o.RmsDb = RmsDb; o.Correlation = Correlation; o.CorrelationNow = CorrelationNow;
        }

        /// <summary>
        /// The readings agents get (docs/specs/mcp-server.md §3.5), keyed as Params.def's reading
        /// lines: a new dictionary each time, which nothing changes afterwards.
        /// </summary>
        public IReadOnlyDictionary<string, double> ToReadings() => new ReadOnlyDictionary<string, double>(new Dictionary<string, double>
        {
            ["peak_dbfs"] = PeakDb,
            ["rms_db"] = RmsDb,
            ["correlation"] = Correlation,
            ["trigger_locked"] = TriggerOn && TriggerLocked ? 1 : 0,
        });
    }

    internal sealed class ScopeEngine
    {
        public const int MaxBlock = 256;

        /// <summary>Divisions across the screen (Time/div).</summary>
        public const int Divisions = 10;

        /// <summary>Display's choices.</summary>
        public const int DisplayL = 0, DisplayR = 1, DisplayMid = 2, DisplaySide = 3, DisplayLR = 4;

        /// <summary>Trigger's choices.</summary>
        public const int TriggerOff = 0, TriggerRising = 1, TriggerFalling = 2;

        /// <summary>Sync's choices: the screen's width in beats (0: Free).</summary>
        public static readonly double[] SyncBeats = { 0, 0.25, 0.5, 1, 2, 4, 8, 16 };

        /// <summary>A bar, in beats: Buzz's (16 ticks at 4 ticks a beat). Machines don't see the song's bar list.</summary>
        public const double BeatsPerBar = 4;

        /// <summary>Trail's choices: how long earlier sweeps stay on the screen, fading (seconds).</summary>
        public static readonly double[] TrailSeconds = { 0, 0.15, 0.4, 1 };

        /// <summary>Snapshots at most this many times a second of audio (the display's rate).</summary>
        public const int PublishRate = 60;

        /// <summary>The trigger re-arms once the trace is this far back on the other side of the level (full scale is 1).</summary>
        public const float Hysteresis = 0.002f;

        /// <summary>With the trigger on and none found, a sweep runs free after twice its length, and at least this long (s).</summary>
        public const double AutoSeconds = 0.1;

        const int Steps = 30, NowSteps = 4;     // the readings' 100 ms steps: 3 s, and 400 ms for the meter

        readonly ParamValues values;
        readonly TripleBuffer<ScopeSnapshot> exchange =
            new TripleBuffer<ScopeSnapshot>(() => new ScopeSnapshot(), (a, b) => a.CopyTo(b));
        int rate;
        long samples, sequence, lastPublish;
        int publishEvery;

        // Parameters.
        int display = DisplayLR, sync, trigger = TriggerRising, holdoff;
        double timePerDiv = 0.002;
        float level;
        bool freeze, restart;

        // The sweeps: the one being taken and the last complete one (swapped, never copied).
        ScopeFrame live = new ScopeFrame(), done = new ScopeFrame();
        bool hasDone, sweeping, locked;
        long frames;
        int cols, len, col, pos;
        long nextAt;

        // Free sweeps: waiting for the next one.
        int holdLeft, waited, autoAfter, freeTrigger;
        bool armed;
        float prevA;

        // Synced sweeps: the window and where the next block should start if the song plays on.
        bool syncedMode;
        double window, start, colsPerTick;
        int nextTick, nextPos;

        // The readings, in 100 ms steps.
        readonly double[] stepLL = new double[Steps], stepRR = new double[Steps], stepLR = new double[Steps];
        readonly float[] stepPeak = new float[Steps];
        readonly int[] stepCount = new int[Steps];
        double ll, rr, lr;
        float peak;
        int inStep, stepLength, stepPos, stepsFilled;
        double peakDb = double.NegativeInfinity, rmsDb = double.NegativeInfinity, correlation = 1, correlationNow = 1;

        // The vectorscope's samples.
        readonly float[] vectorL = new float[ScopeSnapshot.VectorPoints], vectorR = new float[ScopeSnapshot.VectorPoints];
        int vectorPos;
        long vectorTotal;

        double bpm = 120;

        public ScopeEngine(ParamValues values)
        {
            this.values = values;
            SetSampleRate(48000);
        }

        public int Rate => rate;

        /// <summary>Starts again at a sample rate.</summary>
        public void SetSampleRate(int sampleRate)
        {
            rate = Math.Max(8000, sampleRate);
            publishEvery = Math.Max(1, rate / PublishRate);
            stepLength = Math.Max(1, rate / 10);
            Array.Clear(stepLL); Array.Clear(stepRR); Array.Clear(stepLR); Array.Clear(stepPeak); Array.Clear(stepCount);
            ll = rr = lr = 0;
            peak = 0;
            inStep = stepPos = stepsFilled = 0;
            peakDb = rmsDb = double.NegativeInfinity;
            correlation = correlationNow = 1;
            Array.Clear(vectorL); Array.Clear(vectorR);
            vectorPos = 0;
            vectorTotal = 0;
            samples = lastPublish = 0;
            hasDone = sweeping = locked = syncedMode = false;
            frames = 0;
            ApplyParameters();
            Restart();
            Publish();
        }

        void ApplyParameters()
        {
            int d = values.Choice(P.Display), s = values.Choice(P.Sync), t = values.Choice(P.Trigger);
            double tpd = values.Value(P.Time);
            bool f = values.On(P.Freeze);
            if (d != display || s != sync || tpd != timePerDiv || (f != freeze) || t != trigger) restart = true;
            display = d;
            sync = Math.Clamp(s, 0, SyncBeats.Length - 1);
            trigger = t;
            timePerDiv = tpd;
            freeze = f;
            level = (float)(values.Value(P.Level) / 100.0);
            holdoff = (int)Math.Round(values.Value(P.Holdoff) / 1000.0 * rate);
            values.Clean();
        }

        /// <summary>The sweep in progress is dropped; the next starts afresh.</summary>
        void Restart()
        {
            sweeping = false;
            syncedMode = false;
            holdLeft = 0;
            waited = 0;
            armed = false;
            restart = false;
        }

        float Trace(float l, float r) => display switch
        {
            DisplayR => r,
            DisplayMid => (l + r) * 0.5f,
            DisplaySide => (l - r) * 0.5f,
            _ => l,
        };

        /// <summary>
        /// Audio thread: looks at one block (±1 floats; the arrays are only read). <paramref name="t"/>
        /// is where the song is at its first sample.
        /// </summary>
        public void Process(float[] left, float[] right, int n, ScopeTransport t)
        {
            if (values.AnyDirty) ApplyParameters();
            if (t.Bpm > 0) bpm = t.Bpm;
            if (restart) Restart();
            if (!freeze)
            {
                bool synced = sync > 0 && t.Playing && t.SamplesPerTick > 0 && t.TicksPerBeat > 0;
                if (synced != syncedMode && sweeping)
                {
                    // Play or stop: the sweep so far is a sweep.
                    Complete();
                }
                if (synced) Synced(left, right, n, t);
                else Free(left, right, n);
                syncedMode = synced;
                Vector(left, right, n);
            }
            Readings(left, right, n);
            samples += n;
            if (samples - lastPublish >= publishEvery)
            {
                // On a grid of publishEvery samples, whatever the blocks: 60 a second on average.
                lastPublish += (samples - lastPublish) / publishEvery * publishEvery;
                Publish();
            }
        }

        // ------------------------------------------------------------------ the sweeps

        void Put(int c, float a, float b)
        {
            var f = live;
            if (a < f.AMin[c]) f.AMin[c] = a;
            if (a > f.AMax[c]) f.AMax[c] = a;
            if (display == DisplayLR)
            {
                if (b < f.BMin[c]) f.BMin[c] = b;
                if (b > f.BMax[c]) f.BMax[c] = b;
            }
            if (c >= f.Filled) f.Filled = c + 1;
        }

        /// <summary>The sweep in progress becomes the last complete one (if it holds anything).</summary>
        void Complete()
        {
            sweeping = false;
            if (live.Filled == 0) return;
            live.Number = frames++;
            (live, done) = (done, live);
            hasDone = true;
        }

        /// <summary>A sweep's length, free: ten divisions; synced while the song stands still: its note value at the tempo.</summary>
        int FreeLength() => sync == 0
            ? Math.Max(2, (int)Math.Round(Divisions * timePerDiv * rate))
            : Math.Max(2, (int)Math.Round(SyncBeats[sync] * 60.0 / bpm * rate));

        void Free(float[] left, float[] right, int n)
        {
            freeTrigger = sync == 0 ? trigger : TriggerOff;
            for (int i = 0; i < n; i++)
            {
                float l = left[i], r = right[i];
                float a = Trace(l, r);
                if (!sweeping)
                {
                    bool begin = false, triggered = false;
                    double offset = 0;
                    if (holdLeft > 0)
                    {
                        holdLeft--;
                        waited++;
                    }
                    else if (freeTrigger == TriggerOff) begin = true;
                    else
                    {
                        waited++;
                        if (freeTrigger == TriggerRising)
                        {
                            if (a < level - Hysteresis) armed = true;
                            else if (armed && a >= level && prevA < level)
                            {
                                begin = triggered = true;
                                offset = (a - level) / (double)(a - prevA);
                            }
                        }
                        else
                        {
                            if (a > level + Hysteresis) armed = true;
                            else if (armed && a <= level && prevA > level)
                            {
                                begin = triggered = true;
                                offset = (level - a) / (double)(prevA - a);
                            }
                        }
                        if (!begin && waited >= autoAfter) begin = true;     // Auto: no trigger came, run free
                    }
                    prevA = a;
                    if (!begin) continue;
                    BeginFree(triggered, offset);
                }
                if (pos >= nextAt)
                {
                    col++;
                    nextAt = ((long)(col + 1) * len + cols - 1) / cols;
                }
                Put(col, a, r);
                if (++pos >= len)
                {
                    Complete();
                    locked = freeTrigger != TriggerOff && done.Triggered;
                    holdLeft = freeTrigger != TriggerOff ? holdoff : 0;
                    waited = 0;
                    armed = false;
                }
            }
        }

        void BeginFree(bool triggered, double offset)
        {
            len = FreeLength();
            cols = Math.Min(ScopeFrame.MaxColumns, len);
            autoAfter = Math.Max(2 * len, (int)(AutoSeconds * rate));
            live.Begin(cols, len, display);
            live.Seconds = len / (double)rate;
            live.Triggered = triggered;
            live.Offset = triggered ? Math.Clamp(offset, 0, 1) : 0;
            if (sync > 0) live.Beats = SyncBeats[sync];
            pos = col = 0;
            nextAt = ((long)len + cols - 1) / cols;
            sweeping = true;
            if (!triggered && freeTrigger != TriggerOff) locked = false;
        }

        void Synced(float[] left, float[] right, int n, ScopeTransport t)
        {
            int spt = t.SamplesPerTick;
            double w = SyncBeats[sync] * t.TicksPerBeat;
            bool goesOn = syncedMode && sweeping && t.Tick == nextTick && t.PosInTick == nextPos && w == window;
            if (!goesOn)
            {
                // A start, a jump (a loop, a new position) or a new length: the sweep so far ends.
                if (sweeping) Complete();
                window = w;
                BeginSynced(t.Tick, t.PosInTick, spt, t.TicksPerBeat);
            }
            locked = false;
            int tick = t.Tick, at = t.PosInTick;
            double inv = 1.0 / spt;
            for (int i = 0; i < n; i++)
            {
                if (at >= spt)
                {
                    at -= spt;
                    tick++;
                }
                // Columns from the sweep's start: its position in ticks against the window's.
                double x = (tick - start + at * inv) * colsPerTick;
                int c = (int)x;
                if (c >= cols)
                {
                    Complete();
                    BeginSyncedWindow(Math.Floor((tick + at * inv) / window + 1e-9), spt, t.TicksPerBeat);
                    x = (tick - start + at * inv) * colsPerTick;
                    c = Math.Clamp((int)x, 0, cols - 1);
                }
                else if (c < 0) c = 0;
                float l = left[i], r = right[i];
                Put(c, Trace(l, r), r);
                at++;
            }
            if (at >= spt)
            {
                at -= spt;
                tick++;
            }
            nextTick = tick;
            nextPos = at;
        }

        void BeginSynced(int tick, int posInTick, int spt, int tpb)
        {
            double p = tick + posInTick / (double)spt;
            BeginSyncedWindow(Math.Floor(p / window + 1e-9), spt, tpb);
        }

        void BeginSyncedWindow(double k, int spt, int tpb)
        {
            start = k * window;
            len = Math.Max(1, (int)Math.Round(window * spt));
            cols = Math.Min(ScopeFrame.MaxColumns, len);
            colsPerTick = cols / window;
            live.Begin(cols, len, display);
            live.Synced = true;
            live.StartBeat = start / tpb;
            live.Beats = SyncBeats[sync];
            live.Seconds = len / (double)rate;
            sweeping = true;
        }

        // ------------------------------------------------------------------ the vectorscope and the readings

        void Vector(float[] left, float[] right, int n)
        {
            int p = vectorPos;
            const int mask = ScopeSnapshot.VectorPoints - 1;
            for (int i = 0; i < n; i++)
            {
                vectorL[p] = left[i];
                vectorR[p] = right[i];
                p = (p + 1) & mask;
            }
            vectorPos = p;
            vectorTotal += n;
        }

        void Readings(float[] left, float[] right, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float l = left[i], r = right[i];
                if (!float.IsFinite(l)) l = 0;
                if (!float.IsFinite(r)) r = 0;
                ll += l * (double)l;
                rr += r * (double)r;
                lr += l * (double)r;
                float m = Math.Max(Math.Abs(l), Math.Abs(r));
                if (m > peak) peak = m;
                if (++inStep >= stepLength) Step();
            }
        }

        void Step()
        {
            stepLL[stepPos] = ll;
            stepRR[stepPos] = rr;
            stepLR[stepPos] = lr;
            stepPeak[stepPos] = peak;
            stepCount[stepPos] = inStep;
            ll = rr = lr = 0;
            peak = 0;
            inStep = 0;
            stepPos = stepPos + 1 == Steps ? 0 : stepPos + 1;
            if (stepsFilled < Steps) stepsFilled++;

            double a = 0, b = 0, c = 0, count = 0;
            float high = 0;
            for (int k = 1; k <= stepsFilled; k++)
            {
                int j = stepPos - k;
                if (j < 0) j += Steps;
                a += stepLL[j];
                b += stepRR[j];
                c += stepLR[j];
                count += stepCount[j];
                if (stepPeak[j] > high) high = stepPeak[j];
                if (k == Math.Min(NowSteps, stepsFilled)) correlationNow = Stereo.Measures(a, b, c).Correlation;
            }
            correlation = Stereo.Measures(a, b, c).Correlation;
            peakDb = high > 0 ? 20 * PortableMath.Log10(high) : double.NegativeInfinity;
            double meanSquare = count > 0 ? (a + b) / (2 * count) : 0;
            rmsDb = meanSquare > 1e-20 ? 10 * PortableMath.Log10(meanSquare) : double.NegativeInfinity;
        }

        // ------------------------------------------------------------------ snapshots

        void Publish()
        {
            var s = exchange.Back;
            s.Sequence = sequence++;
            s.Samples = samples;
            s.Rate = rate;
            s.Bpm = bpm;
            if (hasDone) done.CopyTo(s.Frame);
            s.HasFrame = hasDone;
            if (sweeping) live.CopyTo(s.Live);
            s.HasLive = sweeping;
            s.Frozen = freeze;
            s.TriggerOn = sync == 0 && trigger != TriggerOff;
            s.TriggerLocked = locked;
            int count = (int)Math.Min(vectorTotal, ScopeSnapshot.VectorPoints);
            int from = vectorPos - count;
            if (from < 0) from += ScopeSnapshot.VectorPoints;
            int first = Math.Min(count, ScopeSnapshot.VectorPoints - from);
            Array.Copy(vectorL, from, s.VectorL, 0, first);
            Array.Copy(vectorR, from, s.VectorR, 0, first);
            Array.Copy(vectorL, 0, s.VectorL, first, count - first);
            Array.Copy(vectorR, 0, s.VectorR, first, count - first);
            s.VectorCount = count;
            s.PeakDb = peakDb;
            s.RmsDb = rmsDb;
            s.Correlation = correlation;
            s.CorrelationNow = correlationNow;
            exchange.Publish();
        }

        /// <summary>Any thread: the newest snapshot into <paramref name="into"/>; true when it is new to readers.</summary>
        public bool Read(ScopeSnapshot into) => exchange.Read(into);

        /// <summary>Snapshots published so far.</summary>
        public long Published => exchange.Published;

        /// <summary>Any thread: the newest readings (docs/specs/mcp-server.md §3.5).</summary>
        public IReadOnlyDictionary<string, double> Readings()
        {
            var s = new ScopeSnapshot();
            exchange.Read(s);
            return s.ToReadings();
        }
    }
}
