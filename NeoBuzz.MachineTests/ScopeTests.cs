using System.Diagnostics;
using NeoBuzz.Analysis;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Scope;
using P = NeoBuzz.Scope.P;
using Params = NeoBuzz.Scope.Params;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Scope (docs/specs/machines.md §5): the acceptance criteria, measured. The engine only looks
    /// at the audio (the machine passes it through; the studio and ReBuzz tests check it bit for bit),
    /// so these check what it shows: synced sweeps on the song's bars, the trigger, the readings
    /// against the analysis library, the display's rate, and its cost (the engine's and the painters').
    /// </summary>
    [TestFixture]
    public class ScopeTests
    {
        const int Rate = Measure.Rate;

        static (ScopeEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new ScopeEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static int Raw(int index, double value) => Params.Table[index].Raw(value);

        /// <summary>
        /// A song's transport as ReBuzz's audio thread runs it: ticks of whole samples carrying the
        /// fraction left over (WorkManager), blocks that end at ticks, the position looping from
        /// LoopEnd back to LoopStart.
        /// </summary>
        sealed class Song
        {
            readonly int bpm;
            double remainder;
            public readonly int Rate;
            public int Tpb = 4, Tick, PosInTick, Spt, LoopStart, LoopEnd = int.MaxValue;
            public bool Playing = true;

            public Song(int rate, int bpm, int tick = 0)
            {
                Rate = rate;
                this.bpm = bpm;
                Tick = tick;
                Spt = Next();
            }

            int Next()
            {
                double exact = Rate * 60.0 / (bpm * Tpb) + remainder;
                int s = (int)Math.Floor(exact);
                remainder = exact - s;
                return s;
            }

            public ScopeTransport Transport => new ScopeTransport
            {
                Playing = Playing, Tick = Tick, PosInTick = PosInTick, SamplesPerTick = Spt, TicksPerBeat = Tpb, Bpm = bpm,
            };

            /// <summary>The next block's length: at most <paramref name="max"/>, ending at the tick's end.</summary>
            public int Block(int max) => Math.Min(max, Spt - PosInTick);

            public void Advance(int n)
            {
                PosInTick += n;
                if (PosInTick < Spt) return;
                PosInTick = 0;
                Tick++;
                if (Tick >= LoopEnd) Tick = LoopStart;
                Spt = Next();
            }
        }

        /// <summary>Every complete sweep the editor would see, read after each block (copies).</summary>
        sealed class Frames
        {
            readonly ScopeSnapshot snapshot = new ScopeSnapshot();
            long last = -1;
            public readonly List<ScopeFrame> All = new List<ScopeFrame>();
            public ScopeSnapshot Snapshot => snapshot;

            public void Read(ScopeEngine e)
            {
                e.Read(snapshot);
                if (!snapshot.HasFrame || snapshot.Frame.Number == last) return;
                last = snapshot.Frame.Number;
                var copy = new ScopeFrame();
                snapshot.Frame.CopyTo(copy);
                All.Add(copy);
            }
        }

        // ------------------------------------------------------------------ synced to the song

        /// <summary>
        /// The song's signal, a function of where the song is (so a loop's passes are the same): in
        /// each bar a pair of sines whose pitch the bar sets, and a marker (0.95) on the bar's first sample.
        /// </summary>
        static (float L, float R) BarSignal(int tick, int sampleInBar, int rate)
        {
            if (sampleInBar == 0) return (0.95f, 0.95f);
            int bar = tick / 16;
            double f = 110 * (1 + (bar % 4) * 0.25), t = sampleInBar / (double)rate;
            return ((float)(0.3 * Math.Sin(2 * Math.PI * f * t)), (float)(0.25 * Math.Sin(2 * Math.PI * 1.5 * f * t)));
        }

        /// <summary>Plays <paramref name="song"/> through the engine for <paramref name="ticks"/> ticks, reading the sweeps.</summary>
        static void Play(ScopeEngine e, Song song, int ticks, Frames frames, int maxBlock = 256)
        {
            var l = new float[256];
            var r = new float[256];
            int inBar = 0;
            int played = 0;
            while (played < ticks)
            {
                int n = song.Block(maxBlock);
                var t = song.Transport;
                for (int i = 0; i < n; i++)
                {
                    if (song.Tick % 16 == 0 && song.PosInTick + i == 0) inBar = 0;
                    (l[i], r[i]) = BarSignal(song.Tick, inBar, song.Rate);
                    inBar++;
                }
                e.Process(l, r, n, t);
                frames?.Read(e);
                int tick = song.Tick;
                song.Advance(n);
                if (song.Tick != tick) played++;
            }
        }

        [TestCase(48000, 120)]
        [TestCase(44100, 120)]
        [TestCase(48000, 97)]
        public void SyncedSweepsStartOnTheBarSoEachPassOfALoopDrawsTheSameFrame(int rate, int bpm)
        {
            // A loop of bars 2 and 3 (ticks 16–48), played from the song's start for three passes,
            // the scope one bar wide. At 44.1 kHz the ticks are 5512 and 5513 samples (the carried
            // half sample); at 97 BPM 7422 and 7423 in a pattern that a bar repeats only within a sample.
            var (e, v) = Make(v => { v.Set(P.Sync, 5); v.Set(P.Display, ScopeEngine.DisplayLR); }, rate);
            var song = new Song(rate, bpm) { LoopStart = 16, LoopEnd = 48 };
            var frames = new Frames();
            Play(e, song, 16 + 32 * 3, frames);

            var bars = frames.All.Where(f => f.Synced).ToList();
            Assert.That(bars.Count, Is.GreaterThanOrEqualTo(6), "a sweep per bar");
            foreach (var f in bars)
            {
                Assert.That(f.StartBeat % 4, Is.EqualTo(0).Within(1e-9), "each sweep starts on a bar");
                Assert.That(f.Beats, Is.EqualTo(4));
                Assert.That(f.Filled, Is.EqualTo(f.Columns), "and fills the screen");
                Assert.That(f.Columns, Is.EqualTo(ScopeFrame.MaxColumns));
                Assert.That(f.AMax[0], Is.EqualTo(0.95f), "the bar's first sample is in the first column");
                Assert.That(f.AMax.Skip(1).Take(f.Columns - 1).Max(), Is.LessThan(0.31f), "and only there");
            }
            var bar2 = bars.Where(f => f.StartBeat == 4).ToList();
            var bar3 = bars.Where(f => f.StartBeat == 8).ToList();
            Assert.That(bar2.Count, Is.EqualTo(3), "bar 2 in each pass");
            Assert.That(bar3.Count, Is.GreaterThanOrEqualTo(2));
            // Whole-sample ticks repeat each bar when a bar is a whole number of samples (48 and 44.1 kHz
            // at 120 BPM); otherwise a pass's ticks can fall a sample apart from the last's.
            bool exact = rate * 60 % (bpm * 4) == 0 || rate * 60 * 2 % (bpm * 4) == 0;
            double worst = 0, step = 2 * Math.PI * 165 * 1.5 * 0.25 / rate;
            for (int i = 1; i < bar2.Count; i++)
            {
                if (exact) Assert.That(bar2[i].SameTraces(bar2[0]), Is.True, $"pass {i + 1} of bar 2 draws the frame of pass 1");
                for (int c = 0; c < bar2[0].Columns; c++)
                    worst = Math.Max(worst, Math.Max(Math.Abs(bar2[i].AMin[c] - bar2[0].AMin[c]), Math.Abs(bar2[i].AMax[c] - bar2[0].AMax[c])));
            }
            Assert.That(bar2[0].SameTraces(bar3[0]), Is.False, "the two bars differ (the comparison means something)");
            if (!exact) Assert.That(worst, Is.LessThanOrEqualTo(2 * step), "within a sample of the same frame");
            Measure.Report($"Neo Scope: synced to 1 bar at {rate} Hz, {bpm} BPM: {bars.Count} sweeps, each from its bar's first sample; bar 2 in three passes " +
                           (exact ? "identical" : $"within {worst:F4} (ticks a sample apart; a sample's step {step:F4})"));
        }

        [Test]
        public void ASongStartedMidBarFillsTheRestOfTheBarThenWholeBars()
        {
            var (e, _) = Make(v => v.Set(P.Sync, 5));
            var song = new Song(Rate, 120, tick: 6);
            var frames = new Frames();
            Play(e, song, 10 + 32, frames);
            var first = frames.All[0];
            Assert.That(first.StartBeat, Is.EqualTo(0), "the bar it started in");
            int from = 6 * ScopeFrame.MaxColumns / 16;
            Assert.That(first.Has(from - 2), Is.False, "nothing before the start");
            Assert.That(first.Has(from + 1), Is.True, "from where it started");
            Assert.That(frames.All.Skip(1).All(f => f.Filled == f.Columns && f.Has(0)), Is.True, "then whole bars");
        }

        [TestCase(1, 0.25)]
        [TestCase(3, 1.0)]
        [TestCase(7, 16.0)]
        public void EachSyncLengthStartsOnItsOwnMultiple(int sync, double beats)
        {
            var (e, _) = Make(v => v.Set(P.Sync, sync));
            var song = new Song(Rate, 140);
            var frames = new Frames();
            Play(e, song, (int)Math.Max(24, beats * 4 * 3), frames);
            var synced = frames.All.Where(f => f.Synced).ToList();
            Assert.That(synced.Count, Is.GreaterThanOrEqualTo(2));
            foreach (var f in synced)
            {
                Assert.That(f.Beats, Is.EqualTo(beats));
                Assert.That(f.StartBeat / beats, Is.EqualTo(Math.Round(f.StartBeat / beats)).Within(1e-9), "on a multiple of its length");
                Assert.That(f.Seconds, Is.EqualTo(beats * 60 / 140).Within(0.05).Percent, "its length at the tempo (ticks of whole samples)");
            }
        }

        [Test]
        public void WithTheSongStoppedASyncedScopeRunsFreeAtTheTempo()
        {
            var (e, _) = Make(v => v.Set(P.Sync, 3));   // a beat
            var song = new Song(Rate, 120) { Playing = false };
            var frames = new Frames();
            var l = Measure.Sine(220, 2 * Rate, 0.4);
            for (int pos = 0; pos < l.Length; pos += 256)
            {
                e.Process(l.AsSpan(pos, 256).ToArray(), l.AsSpan(pos, 256).ToArray(), 256, song.Transport);
                frames.Read(e);
            }
            Assert.That(frames.All, Is.Not.Empty);
            Assert.That(frames.All.All(f => !f.Synced && f.Samples == Rate / 2), Is.True, "half a second at 120 BPM, not aligned");
        }

        // ------------------------------------------------------------------ the trigger

        /// <summary>The sweeps of a 220 Hz sine (amplitude 0.5) through a free scope at 1 ms a division.</summary>
        static (List<ScopeFrame> Frames, ScopeSnapshot Last, ScopeEngine Engine) Trigger(int edge, double levelPercent, double holdoffMs = 0, double seconds = 1, double hz = 220)
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Sync, 0);
                v.Set(P.Display, ScopeEngine.DisplayMid);
                v.Set(P.Time, Raw(P.Time, 0.001));
                v.Set(P.Trigger, edge);
                v.Set(P.Level, Raw(P.Level, levelPercent));
                v.Set(P.Holdoff, Raw(P.Holdoff, holdoffMs));
            });
            var x = Measure.Sine(hz, (int)(seconds * Rate), 0.5);
            var frames = new Frames();
            var stopped = new ScopeTransport { Bpm = 120 };
            var b = new float[256];
            for (int pos = 0; pos + 256 <= x.Length; pos += 256)
            {
                Array.Copy(x, pos, b, 0, 256);
                e.Process(b, b, 256, stopped);
                frames.Read(e);
            }
            return (frames.All, frames.Snapshot, e);
        }

        [TestCase(ScopeEngine.TriggerRising, 25.0)]
        [TestCase(ScopeEngine.TriggerFalling, -25.0)]
        [TestCase(ScopeEngine.TriggerRising, -40.0)]
        [TestCase(ScopeEngine.TriggerFalling, 10.0)]
        public void TheTriggerLocksOnASineAtTheLevelAndEdge(int edge, double levelPercent)
        {
            var (frames, last, e) = Trigger(edge, levelPercent);
            double level = levelPercent / 100, step = 2 * Math.PI * 220 * 0.5 / Rate;   // the sine's largest step
            var late = frames.Skip(2).ToList();
            Assert.That(late.Count, Is.GreaterThan(20), "sweeps keep coming");
            double worst = 0;
            foreach (var f in late)
            {
                Assert.That(f.Triggered, Is.True, "each began on the trigger");
                Assert.That(f.Columns, Is.EqualTo(480), "10 ms: a sample a column");
                float a0 = f.AMin[0], a1 = f.AMin[1];
                if (edge == ScopeEngine.TriggerRising)
                {
                    Assert.That(a0, Is.InRange(level, level + step), "the first sample is at or just past the level");
                    Assert.That(a1, Is.GreaterThan(a0), "going up");
                }
                else
                {
                    Assert.That(a0, Is.InRange(level - step, level), "the first sample is at or just past the level");
                    Assert.That(a1, Is.LessThan(a0), "going down");
                }
                // The crossing, from the sweep's offset (back along the next step's slope; the sine
                // bends a little over a sample): within 3 % of a step of the level.
                double crossing = a0 - f.Offset * (a1 - a0);
                worst = Math.Max(worst, Math.Abs(crossing - level));
            }
            Assert.That(worst, Is.LessThan(0.03 * step), "the crossing the offset gives is the level");
            Assert.That(e.Readings()["trigger_locked"], Is.EqualTo(1));
            Assert.That(last.TriggerOn && last.TriggerLocked, Is.True);
            Measure.Report($"Neo Scope: trigger {(edge == 1 ? "rising" : "falling")} at {levelPercent:+0;−0} % on a 220 Hz sine (0.5): {late.Count} sweeps, each from the crossing; " +
                           $"the crossing at the level within {worst:E1} (a sample's step {step:F4})");
        }

        [Test]
        public void WithoutACrossingTheScopeRunsFreeAndSaysSo()
        {
            var (frames, last, e) = Trigger(ScopeEngine.TriggerRising, 60);   // above the sine's peak
            Assert.That(frames.Count, Is.GreaterThan(5), "sweeps still come (Auto)");
            Assert.That(frames.All(f => !f.Triggered), Is.True);
            Assert.That(last.TriggerLocked, Is.False);
            Assert.That(e.Readings()["trigger_locked"], Is.EqualTo(0));
            Assert.That(frames.Skip(1).Select(f => f.Number).Zip(frames.Select(f => f.Number), (b, a) => b - a).Max(), Is.LessThan(20));
        }

        [Test]
        public void HoldoffSpacesTheSweeps()
        {
            // 10 ms sweeps of a 220 Hz sine: without holdoff about one a crossing after each sweep;
            // with 25 ms, a sweep at most every 35 ms.
            static double PerSecond(List<ScopeFrame> f) => (f[^1].Number - f[0].Number) / 0.95;
            var (free, _, _) = Trigger(ScopeEngine.TriggerRising, 0, holdoffMs: 0);
            var (held, _, _) = Trigger(ScopeEngine.TriggerRising, 0, holdoffMs: 25);
            double a = PerSecond(free), b = PerSecond(held);
            Assert.That(a, Is.GreaterThan(60), "without holdoff, a sweep at the first crossing after each");
            Assert.That(b, Is.LessThanOrEqualTo(1 / 0.035 + 1), "with 25 ms, at most one every 35 ms");
            Assert.That(b, Is.GreaterThan(20));
            Measure.Report($"Neo Scope: 10 ms sweeps on a 220 Hz sine: {a:F0} a second without holdoff, {b:F0} with 25 ms");
        }

        // ------------------------------------------------------------------ what it shows

        [TestCase(ScopeEngine.DisplayL, 0.6)]
        [TestCase(ScopeEngine.DisplayR, 0.2)]
        [TestCase(ScopeEngine.DisplayMid, 0.4)]
        [TestCase(ScopeEngine.DisplaySide, 0.2)]
        public void EachDisplayDrawsItsSignal(int display, double peak)
        {
            // Left 0.6 sin, right 0.2 sin (in phase): mid (L + R)/2 = 0.4, side (L − R)/2 = 0.2.
            var (e, _) = Make(v => { v.Set(P.Sync, 0); v.Set(P.Display, display); v.Set(P.Time, Raw(P.Time, 0.01)); v.Set(P.Trigger, 0); });
            var l = Measure.Sine(100, Rate, 0.6);
            var r = Measure.Sine(100, Rate, 0.2);
            var frames = new Frames();
            for (int pos = 0; pos + 256 <= l.Length; pos += 256)
            {
                e.Process(l[pos..(pos + 256)], r[pos..(pos + 256)], 256, default);
                frames.Read(e);
            }
            var f = frames.All[^1];
            Assert.That(f.AMax.Take(f.Columns).Max(), Is.EqualTo(peak).Within(0.002));
            Assert.That(f.AMin.Take(f.Columns).Min(), Is.EqualTo(-peak).Within(0.002));
            Assert.That(f.Two, Is.False);
        }

        [Test]
        public void LeftAndRightOverlaidAreTwoTraces()
        {
            var (e, _) = Make(v => { v.Set(P.Sync, 0); v.Set(P.Display, ScopeEngine.DisplayLR); v.Set(P.Trigger, 0); });
            var l = Measure.Sine(500, Rate / 2, 0.7);
            var r = Measure.Sine(500, Rate / 2, 0.1);
            var frames = new Frames();
            for (int pos = 0; pos + 256 <= l.Length; pos += 256)
            {
                e.Process(l[pos..(pos + 256)], r[pos..(pos + 256)], 256, default);
                frames.Read(e);
            }
            var f = frames.All[^1];
            Assert.That(f.Two, Is.True);
            Assert.That(f.AMax.Take(f.Columns).Max(), Is.EqualTo(0.7).Within(0.002));
            Assert.That(f.BMax.Take(f.Columns).Max(), Is.EqualTo(0.1).Within(0.002));
        }

        [Test]
        public void TheReadingsAreTheLibrarysMeasures()
        {
            // 3 s of a 1 kHz sine at 0.5 on the left and 0.25 on the right, in phase, then of noise.
            // Blocks of 240 samples: the readings' 100 ms steps end where the 3 s fed ends.
            const int B = 240;
            var (e, _) = Make();
            var l = Measure.Sine(1000, 3 * Rate, 0.5);
            var r = Measure.Sine(1000, 3 * Rate, 0.25);
            for (int pos = 0; pos < l.Length; pos += B) e.Process(l[pos..(pos + B)], r[pos..(pos + B)], B, default);
            var x = e.Readings();
            var batch = Stereo.Measure(l, r, Rate);
            Assert.That(x.Keys, Is.EquivalentTo(new[] { "peak_dbfs", "rms_db", "correlation", "trigger_locked" }));
            Assert.That(x["peak_dbfs"], Is.EqualTo(Measure.Db(0.5)).Within(0.01));
            Assert.That(x["rms_db"], Is.EqualTo(10 * Math.Log10((0.5 * 0.5 / 2 + 0.25 * 0.25 / 2) / 2)).Within(0.01), "both channels' mean square");
            Assert.That(x["correlation"], Is.EqualTo(batch.Correlation).Within(1e-6), "the library's correlation");
            Assert.That(x["correlation"], Is.EqualTo(1).Within(1e-6));
            Assert.That(x["trigger_locked"], Is.EqualTo(0), "synced (the default): no trigger");

            var nl = Measure.PinkNoise(3 * Rate, 0.1, 71);
            var nr = Measure.PinkNoise(3 * Rate, 0.1, 72);
            for (int pos = 0; pos < nl.Length; pos += B) e.Process(nl[pos..(pos + B)], nr[pos..(pos + B)], B, default);
            x = e.Readings();
            var noise = Stereo.Measure(nl, nr, Rate);
            Assert.That(x["correlation"], Is.EqualTo(noise.Correlation).Within(1e-6), "unrelated noise: the library's correlation");
            Assert.That(Math.Abs(x["correlation"]), Is.LessThan(0.05));

            var inv = nl.Select(s => -s).ToArray();
            for (int pos = 0; pos < nl.Length; pos += B) e.Process(nl[pos..(pos + B)], inv[pos..(pos + B)], B, default);
            Assert.That(e.Readings()["correlation"], Is.EqualTo(-1).Within(1e-6), "opposed");

            var quiet = new float[3 * Rate];
            for (int pos = 0; pos < quiet.Length; pos += B) e.Process(quiet[pos..(pos + B)], quiet[pos..(pos + B)], B, default);
            Assert.That(e.Readings()["peak_dbfs"], Is.EqualTo(double.NegativeInfinity), "silence: −∞");
            Assert.That(e.Readings()["rms_db"], Is.EqualTo(double.NegativeInfinity));
            Measure.Report($"Neo Scope: readings of a 1 kHz sine (0.5 / 0.25): peak {Measure.Db(0.5):F2} dBFS, RMS {10 * Math.Log10((0.125 + 0.03125) / 2):F2} dB; " +
                           $"correlation equal to Stereo.Measure's within 1e-6 (sine, unrelated noise {noise.Correlation:F4}, opposed)");
        }

        [Test]
        public void FreezeHoldsTheDisplaysAndTheReadingsGoOn()
        {
            var (e, v) = Make(v => { v.Set(P.Sync, 0); v.Set(P.Trigger, 0); });
            var a = Measure.Sine(300, Rate, 0.5);
            var frames = new Frames();
            for (int pos = 0; pos < Rate / 2; pos += 256) { e.Process(a[pos..(pos + 256)], a[pos..(pos + 256)], 256, default); frames.Read(e); }
            v.Set(P.Freeze, 1);
            for (int pos = Rate / 2; pos + 256 <= Rate; pos += 256) { e.Process(a[pos..(pos + 256)], a[pos..(pos + 256)], 256, default); frames.Read(e); }
            long held = frames.Snapshot.Frame.Number;
            var vector = frames.Snapshot.VectorL.ToArray();
            var quiet = new float[256];
            for (int k = 0; k < 700; k++) { e.Process(quiet, quiet, 256, default); frames.Read(e); }
            Assert.That(frames.Snapshot.Frozen, Is.True);
            Assert.That(frames.Snapshot.Frame.Number, Is.EqualTo(held), "the sweep holds");
            Assert.That(frames.Snapshot.VectorL, Is.EqualTo(vector), "and the vectorscope");
            Assert.That(e.Readings()["peak_dbfs"], Is.EqualTo(double.NegativeInfinity), "the readings went on (3.7 s of silence)");
            v.Set(P.Freeze, 0);
            for (int k = 0; k < 40; k++) { e.Process(quiet, quiet, 256, default); frames.Read(e); }
            Assert.That(frames.Snapshot.Frame.Number, Is.GreaterThan(held), "unfrozen, it sweeps again");
        }

        // ------------------------------------------------------------------ threads and cost

        [Test]
        public void SnapshotsComeAtTheDisplaysRateNotPerBlock()
        {
            var (e, _) = Make();
            var l = Measure.Sine(220, 4 * Rate, 0.4);
            long before = e.Published;
            var song = new Song(Rate, 120);
            int blocks = 0;
            for (int pos = 0; pos + 64 <= l.Length; pos += 64, blocks++)
            {
                e.Process(l[pos..(pos + 64)], l[pos..(pos + 64)], 64, song.Transport);
                song.Advance(64);
            }
            long published = e.Published - before;
            Assert.That(published, Is.InRange(4 * ScopeEngine.PublishRate - 2, 4 * ScopeEngine.PublishRate + 2), $"{ScopeEngine.PublishRate} a second of audio ({blocks} blocks)");
        }

        [Test]
        public void ProcessingAllocatesNothing()
        {
            var (e, v) = Make();
            var l = Measure.PinkNoise(6 * Rate, 0.2, 81);
            var r = Measure.PinkNoise(6 * Rate, 0.2, 82);
            var bl = new float[256];
            var br = new float[256];
            int blocks = l.Length / 256;
            void Step(Song song, int k, Action<int> at)
            {
                int pos = k * 256;
                at?.Invoke(k);
                Array.Copy(l, pos, bl, 0, 256);
                Array.Copy(r, pos, br, 0, 256);
                int n = song.Block(256);
                e.Process(bl, br, n, song.Transport);
                song.Advance(n);
            }
            void Run(Song song, Action<int> at)
            {
                for (int k = 0; k < blocks; k++) Step(song, k, at);
            }
            // Every mode once (warm-up), then measured: synced with a loop, free with the trigger,
            // holdoff, every display, freeze on and off, the transport stopping.
            void Moves(int k)
            {
                if (k == 100) { v.Set(P.Sync, 0); v.Set(P.Trigger, 1); v.Set(P.Holdoff, 300); }
                if (k == 300) { v.Set(P.Display, ScopeEngine.DisplaySide); v.Set(P.Trigger, 2); v.Set(P.Time, 900); }
                if (k == 500) v.Set(P.Freeze, 1);
                if (k == 550) { v.Set(P.Freeze, 0); v.Set(P.Sync, 7); v.Set(P.Display, ScopeEngine.DisplayLR); }
                if (k == 800) { v.Set(P.Sync, 1); v.Set(P.Level, 1500); }
            }
            Action<int> moves = Moves;
            Song a = new Song(Rate, 128) { LoopStart = 8, LoopEnd = 40 }, b = new Song(Rate, 128) { LoopStart = 8, LoopEnd = 40 }, c = new Song(Rate, 90) { Playing = false };
            Run(a, moves);
            var (allocated, why) = AllocationCheck.Run(2 * blocks, k => Step(k < blocks ? b : c, k % blocks, moves));
            Measure.Report($"Neo Scope: allocation while processing 12 s (synced and looping, free and triggered, every display, freeze, stopped): {allocated} bytes");
            Assert.That(allocated, Is.EqualTo(0), why);
        }

        [TestCase(5, 0, "synced to a bar, left and right", 980, 740)]
        [TestCase(0, 1, "free at 2 ms/div, triggered", 1100, 880)]
        public void LookingCostsLittle(int sync, int trigger, string what, double x64, double arm64)
        {
            // Under 1 % of real time: over 100×, timed against the reference workload in the same run (Throughput.cs): 1 s
            // played untimed, then 2 s, in blocks of 256 split at the song's ticks as ReBuzz splits them.
            var l = Measure.PinkNoise(3 * Rate, 0.2, 91);
            var r = Measure.PinkNoise(3 * Rate, 0.2, 92);
            Throughput.Guard($"Neo Scope: stereo at 48 kHz, {what}", Throughput.Floor(x64, arm64), 2, Rate, 256, () =>
            {
                var (e, _) = Make(v => { v.Set(P.Sync, sync); v.Set(P.Trigger, trigger); });
                var song = new Song(Rate, 120);
                var bl = new float[256];
                var br = new float[256];
                void Block(int pos, int count)
                {
                    for (int done = 0; done < count;)
                    {
                        int n = song.Block(count - done);
                        Array.Copy(l, pos + done, bl, 0, n);
                        Array.Copy(r, pos + done, br, 0, n);
                        e.Process(bl, br, n, song.Transport);
                        song.Advance(n);
                        done += n;
                    }
                }
                for (int pos = 0; pos < Rate; pos += 256) Block(pos, 256);
                return Throughput.InBlocks(256, (pos, n) => Block(Rate + pos, n));
            }, Measure.Report);
        }

        // ------------------------------------------------------------------ the painters

        /// <summary>A canvas that draws nothing: the painters' own cost.</summary>
        sealed class NullCanvas : INeoCanvas
        {
            public long Points;
            public double MeasureText(string text, double size, bool bold = false) => text.Length * size * 0.55;
            public void Line(NeoPoint from, NeoPoint to, NeoStroke stroke) { }
            public void Rect(NeoRect rect, NeoFill fill, NeoStroke stroke, double radius = 0) { }
            public void Ellipse(NeoPoint centre, double radiusX, double radiusY, NeoFill fill, NeoStroke stroke) { }
            public void Arc(NeoPoint centre, double radius, double fromDegrees, double toDegrees, NeoStroke stroke) { }
            public void Path(NeoPath path, NeoFill fill, NeoStroke stroke) => Points += path.PointCount;
            public void Text(string text, double x, double y, double size, NeoColor color, bool bold = false, NeoAlign align = NeoAlign.Left, double maxWidth = 0) { }
            public void PushClip(NeoRect rect, double radius = 0) { }
            public void PopClip() { }
        }

        static ScopeSweep Of(ScopeFrame f) => new ScopeSweep
        {
            AMin = f.AMin, AMax = f.AMax, BMin = f.BMin, BMax = f.BMax,
            Columns = f.Columns, Samples = f.Samples, Filled = f.Filled, Two = f.Two, Offset = f.Offset,
        };

        [Test]
        public void ThePaintersCostFollowTheScreenNotTheSweep()
        {
            // A bar of noise in 1024 columns, two traces, six trails, on a 680-pixel screen; and the
            // vectorscope's 1024 points, both ways.
            var (e, _) = Make(v => { v.Set(P.Sync, 5); v.Set(P.Display, ScopeEngine.DisplayLR); });
            var song = new Song(Rate, 120);
            var frames = new Frames();
            var l = Measure.PinkNoise(5 * Rate, 0.2, 101);
            var r = Measure.PinkNoise(5 * Rate, 0.2, 102);
            for (int pos = 0; pos + 256 <= l.Length; pos += 256)
            {
                int n = song.Block(256);
                e.Process(l[pos..(pos + n)], r[pos..(pos + n)], n, song.Transport);
                song.Advance(n);
                frames.Read(e);
            }
            var view = new ScopeView { ColorA = NeoColor.FromRgb(0x7E, 0xE0, 0x6A), ColorB = NeoColor.FromRgb(0xFF, 0x9F, 0x5A) };
            view.Sweep = Of(frames.All[^1]);
            for (int k = 0; k < 6; k++) view.Trails.Add((Of(frames.All[^2]), 0.5));
            view.Scale = ScopeGraph.FitScale(ScopeGraph.Peak(view.Sweep));
            var rect = new NeoRect(0, 0, 680, 300);
            var canvas = new NullCanvas();
            ScopeGraph.Draw(canvas, rect, view);
            long points = canvas.Points;
            Assert.That(points, Is.LessThan(7 * 2 * 2 * 700 + 200), "at most two points a pixel a trace");
            var s = frames.Snapshot;
            const int Frames = 300;
            var sw = Stopwatch.StartNew();
            // GuardPolicy: the fastest of up to 3 measurements (stopping at the first under the limit).
            double scopeUs = double.MaxValue;
            for (int attempt = 0; attempt < GuardPolicy.Attempts && scopeUs >= 2000; attempt++)
            {
                sw.Restart();
                for (int k = 0; k < Frames; k++) ScopeGraph.Draw(canvas, rect, view);
                scopeUs = Math.Min(scopeUs, sw.Elapsed.TotalMilliseconds * 1000 / Frames);
            }
            sw.Restart();
            for (int k = 0; k < Frames; k++) Goniometer.Draw(canvas, new NeoRect(0, 0, 240, 220), s.VectorL, s.VectorR, s.VectorCount, view.ColorA);
            double gonioUs = sw.Elapsed.TotalMilliseconds * 1000 / Frames;
            sw.Restart();
            for (int k = 0; k < Frames; k++) PolarScope.Draw(canvas, new NeoRect(0, 0, 240, 220), s.VectorL, s.VectorR, s.VectorCount, view.ColorA);
            double polarUs = sw.Elapsed.TotalMilliseconds * 1000 / Frames;
            Measure.Report($"Neo Scope: painters, a frame: the scope (2 traces, 6 trails, 680 px, {points} points) {scopeUs:F0} µs, " +
                           $"the goniometer {gonioUs:F0} µs, the polar vectorscope {polarUs:F0} µs (1024 points; at 30 frames a second {(scopeUs + polarUs) * 30 / 1e4:F2} % of a core)");
            Assert.That(scopeUs, Is.LessThan(2000), "under 2 ms a frame");
        }

        [Test]
        public void ThePolarVectorscopeFoldsIntoTheUpperHalf()
        {
            var centre = new NeoPoint(100, 100);
            var mono = PolarScope.Point(centre, 50, 1, 0.5f, 0.5f, out bool f1);
            var monoNeg = PolarScope.Point(centre, 50, 1, -0.5f, -0.5f, out bool f2);
            var opposed = PolarScope.Point(centre, 50, 1, 0.5f, -0.5f, out _);
            var left = PolarScope.Point(centre, 50, 1, 0.5f, 0, out _);
            Assert.That(f1, Is.False);
            Assert.That(f2, Is.True, "a negative mid folds");
            Assert.That(mono.X, Is.EqualTo(100).Within(1e-9), "mono straight up");
            Assert.That(monoNeg.X, Is.EqualTo(mono.X).Within(1e-9));
            Assert.That(monoNeg.Y, Is.EqualTo(mono.Y).Within(1e-9), "folded onto the same point");
            Assert.That(opposed.Y, Is.EqualTo(100).Within(1e-9), "opposed channels along the base");
            Assert.That(left.X, Is.LessThan(100), "the left channel up-left");
            Assert.That(left.Y, Is.LessThan(100));
            Assert.That(left.X - 100, Is.EqualTo(left.Y - 100).Within(1e-9), "at 45°");
        }

        [Test]
        public void InSilenceTheCorrelationWaitsForSound()
        {
            // UI gallery review (mac-413): in silence the editor read correlation +1.00. Until the windows hold sound
            // the snapshot says there is no reading; agents keep the library's correlation.
            var (e, _) = Make();
            var s = new ScopeSnapshot();
            e.Read(s);
            Assert.That(s.Stereo || s.StereoNow, Is.False, "nothing yet");
            var stopped = new ScopeTransport { Bpm = 120 };
            var z = new float[ScopeEngine.MaxBlock];
            for (int i = 0; i < 2 * Rate / ScopeEngine.MaxBlock; i++) e.Process(z, z, z.Length, stopped);
            e.Read(s);
            Assert.That(s.Stereo || s.StereoNow, Is.False, "silence: no reading");
            Assert.That(StereoReadout.Correlation(s.Correlation, s.Stereo), Is.EqualTo("—"));
            Assert.That(s.ToReadings()["correlation"], Is.EqualTo(1), "agents keep the library's reading");
            var x = Measure.Sine(440, Rate, 0.5);
            var bl = new float[ScopeEngine.MaxBlock];
            for (int pos = 0; pos + bl.Length <= x.Length; pos += bl.Length)
            {
                Array.Copy(x, pos, bl, 0, bl.Length);
                e.Process(bl, bl, bl.Length, stopped);
            }
            e.Read(s);
            Assert.That(s.Stereo && s.StereoNow, Is.True, "sound: readings");
            Assert.That(StereoReadout.Correlation(s.Correlation, s.Stereo), Is.EqualTo("+1.00"));
        }
    }
}
