using ReBuzz.Core.Sync;

namespace ReBuzz.SessionTests
{
    /// <summary>Tempo and phase sync (docs/specs/session-view.md 0.8), pure: the time lines, the audio clock fit and the phase lock.</summary>
    [TestFixture]
    public class SyncTimeTests
    {
        [Test]
        public void ATimelineMapsBeatsAndTimesBothWays()
        {
            var t = new Timeline(1_000_000, 10, 120);                  // beat 10 at 1 s, half a second a beat
            Assert.That(t.BeatAt(1_500_000), Is.EqualTo(11));
            Assert.That(t.MicrosAt(8), Is.EqualTo(0));
            Assert.That(t.PhaseAt(1_250_000, 4), Is.EqualTo(2.5));
            Assert.That(t.NextBarAt(1_250_000, 4), Is.EqualTo(t.MicrosAt(12)), "the next bar line");
            Assert.That(t.NextBarAt(t.MicrosAt(12), 4), Is.EqualTo(t.MicrosAt(12)), "on a bar line: that one");
            Assert.That(new Timeline(0, 0, 0).IsValid, Is.False);
        }

        [Test]
        public void WrappingKeepsTheNearestWay()
        {
            Assert.That(SyncMath.Wrap(3.5, 4), Is.EqualTo(-0.5));
            Assert.That(SyncMath.Wrap(-2.25, 4), Is.EqualTo(1.75));
            Assert.That(SyncMath.Wrap(1.0, 4), Is.EqualTo(1.0));
            Assert.That(SyncMath.Mod(-1, 4), Is.EqualTo(3));
        }

        [Test]
        public void TheFilterLearnsTheAudioClockThroughJitter()
        {
            const double rate = 48000, drift = 50e-6;                   // the device runs 50 ppm fast
            var rnd = new Random(7);
            var filter = new HostTimeFilter(512, 1e6 / rate);
            double TrueMicros(double s) => 5e9 + s * 1e6 / rate * (1 - drift);
            for (int i = 0; i < 2000; i++)
            {
                double s = i * 256.0;
                filter.Add(s, TrueMicros(s) + (rnd.NextDouble() - 0.5) * 3000);     // ±1.5 ms late or early
            }
            double next = 2000 * 256.0;
            Assert.That(Math.Abs(filter.YAt(next) - TrueMicros(next)), Is.LessThan(250), "within a quarter millisecond");
            Assert.That(Math.Abs(filter.Slope / (1e6 / rate * (1 - drift)) - 1), Is.LessThan(2e-4), "the device's own rate, within the jitter's reach");
            Assert.That(Math.Abs(filter.XAt(filter.YAt(next)) - next), Is.LessThan(1e-3));
        }

        [Test]
        public void AFitOffByMoreThanOnePercentFallsBackToTheNominalRate()
        {
            var filter = new HostTimeFilter(16, 10);
            for (int i = 0; i < 16; i++) filter.Add(i, i * 50);           // 5x: a glitch, not a clock
            Assert.That(filter.Slope, Is.EqualTo(10));
            filter.Reset();
            Assert.That(filter.Count, Is.EqualTo(0));
            Assert.That(double.IsNaN(filter.YAt(3)), Is.True);
        }

        /// <summary>
        /// The acceptance criterion's run: 30 minutes against a clock at a fractional tempo,
        /// starting 1.3 beats out of phase, through callback jitter and a device clock 30 ppm
        /// slow, with a tempo change at 10 minutes. Ticks are whole samples.
        /// </summary>
        [Test]
        public void ThePhaseLockHoldsWithinAMillisecondForThirtyMinutes()
        {
            const double rate = 48000, drift = -30e-6, latency = 12_000;      // µs
            const int tpb = 4, buffer = 256;
            const double quantum = 4;
            var rnd = new Random(42);
            double Heard(double s) => 1e9 + s * 1e6 / rate * (1 + drift) + latency;   // when sample s is heard
            var external = new Timeline(Heard(0), -1.3, 120.37);         // we start 1.3 beats ahead of it
            var filter = new HostTimeFilter(512, 1e6 / rate);

            long sample = 0, nextCallback = 0;
            long tick = 0;
            double worstAfterLock = 0, worstAfterChange = 0;
            bool changed = false;
            double changeAt = Heard(0) + 600e6;
            while (Heard(sample) < Heard(0) + 1800e6)
            {
                while (nextCallback <= sample)
                {
                    filter.Add(nextCallback, Heard(nextCallback) - latency + (rnd.NextDouble() - 0.5) * 2000);   // rendered a latency before it's heard
                    nextCallback += buffer;
                }
                double heard = Heard(sample);
                if (!changed && heard >= changeAt)
                {
                    external = new Timeline(changeAt, external.BeatAt(changeAt), 128.0);     // a peer changes the tempo
                    changed = true;
                }
                double error = PhaseLock.Error(external, quantum, tick / (double)tpb, heard) * 60e6 / external.Tempo;
                double since = heard - Heard(0);
                if (since > 60e6 && since < 600e6) worstAfterLock = Math.Max(worstAfterLock, Math.Abs(error));
                if (since > 610e6) worstAfterChange = Math.Max(worstAfterChange, Math.Abs(error));

                int length = PhaseLock.TickLength(external, quantum, (tick + 1) / (double)tpb, filter.YAt(sample) + latency, filter.Slope, tpb);
                sample += length;
                tick++;
            }
            Assert.That(worstAfterLock, Is.LessThan(1000), $"worst error after locking: {worstAfterLock:F0} µs");
            Assert.That(worstAfterChange, Is.LessThan(1000), $"worst error after the tempo change: {worstAfterChange:F0} µs");
        }

        [Test]
        public void ALargeErrorClosesAtTheBoundedRate()
        {
            var external = new Timeline(0, 0, 120);
            double tick = PhaseLock.TickMicros(120, 4);
            // Nearly two beats ahead in a bar of 4: the tick stretches by the most it may; behind, it shrinks.
            Assert.That(PhaseLock.TickLength(external, 4, 2.2, 0, 1, 4), Is.EqualTo((int)Math.Round(tick * (1 + PhaseLock.MaxDeviation))));
            Assert.That(PhaseLock.TickLength(external, 4, -1.7, 0, 1, 4), Is.EqualTo((int)Math.Round(tick * (1 - PhaseLock.MaxDeviation))));
            // In phase: the clock's own tick.
            Assert.That(PhaseLock.TickLength(external, 4, 0.25, 0, 1, 4), Is.EqualTo((int)Math.Round(tick)));
            // A bar later is the same phase.
            Assert.That(PhaseLock.TickLength(external, 4, 4.25, 0, 1, 4), Is.EqualTo((int)Math.Round(tick)));
        }

        [Test]
        public void ATimelineCellIsNeverReadHalfWritten()
        {
            var cell = new TimelineCell();
            Assert.That(cell.TryRead(out _), Is.False, "never written");
            bool stop = false;
            var writer = new Thread(() =>
            {
                for (int i = 1; !Volatile.Read(ref stop); i++)
                    cell.Write(new Timeline(i, 2.0 * i, 100 + (i % 7)));
            });
            writer.Start();
            int reads = 0, torn = 0;
            var until = DateTime.UtcNow.AddMilliseconds(300);
            while (DateTime.UtcNow < until)
            {
                if (!cell.TryRead(out var t)) continue;
                reads++;
                if (t.Beat != 2 * t.Micros || t.Tempo != 100 + (t.Micros % 7)) torn++;
            }
            Volatile.Write(ref stop, true);
            writer.Join();
            Assert.That(torn, Is.EqualTo(0));
            Assert.That(reads, Is.InRange(1, int.MaxValue));
            cell.Clear();
            Assert.That(cell.TryRead(out _), Is.False, "cleared");
        }
    }
}

namespace ReBuzz.SessionTests
{
    using ReBuzz.Core.Sync;

    /// <summary>The audio thread's sync (0.8), driven as WorkManager drives it.</summary>
    [TestFixture]
    public class SyncClockTests
    {
        sealed class FakeSource : ISyncSource
        {
            public Timeline Line;
            public bool Has = true;
            public bool AlignOnBar = true;
            public bool TryGetTimeline(out Timeline t) { t = Line; return Has; }
            public double StartTarget(in Timeline t, double heard, double quantum) => AlignOnBar ? t.NextBarAt(heard, quantum) : double.NaN;
        }

        const int Rate = 48000, Tpb = 4, Bpb = 4, Buffer = 256;
        const double Latency = 10_000;

        /// <summary>An engine: callbacks of 256 frames in real time, ticks as the clock says, the transport as asked.</summary>
        sealed class Engine
        {
            public readonly SyncClock Sync = new SyncClock();
            public double Now = 1e9;                 // host µs at the next callback
            public long Frames;                      // rendered
            public int PosInTick, TickLength = 1;
            public bool Playing, PlayRequested;
            public long Clock = -1;
            public int Nominal = Rate * 60 / (120 * Tpb);
            public readonly System.Collections.Generic.List<(long Clock, double Heard)> Ticks = new();
            public double StartedHeard = double.NaN;

            public void Callback()
            {
                Sync.OnCallback(Now, Rate);
                int left = Buffer;
                while (left > 0)
                {
                    if (PosInTick == 0)
                    {
                        TickLength = Sync.TickLength(Nominal, Playing, PlayRequested && !Playing, Clock, 0, Tpb, Bpb, Latency);
                        if (Playing) Ticks.Add((Clock, Heard(Frames)));
                    }
                    int n = Math.Min(left, TickLength - PosInTick);
                    Frames += n;
                    Sync.OnFrames(n);
                    PosInTick += n;
                    left -= n;
                    if (PosInTick >= TickLength)
                    {
                        PosInTick = 0;
                        if (Playing) Clock++;
                        else if (PlayRequested && Sync.StartDue)
                        {
                            Playing = true;
                            Clock = 0;
                            StartedHeard = Heard(Frames);
                        }
                    }
                }
                Now += Buffer * 1e6 / Rate;
            }

            /// <summary>When a frame is heard: the callback rendering it plus the latency (as the filter models it).</summary>
            public double Heard(long frame) => 1e9 + frame * 1e6 / Rate + Latency;
        }

        [Test]
        public void WithoutASourceTicksAreTheEnginesOwn()
        {
            var e = new Engine { PlayRequested = true };
            for (int i = 0; i < 200; i++) e.Callback();
            Assert.That(e.Playing, Is.True);
            Assert.That(e.TickLength, Is.EqualTo(e.Nominal));
            Assert.That(e.Sync.IsFollowing, Is.False);
            Assert.That(e.Sync.Ours.TryRead(out var ours), Is.True, "our beat line is published");
            Assert.That(ours.Tempo, Is.InRange(119.99, 120.01));
        }

        [Test]
        public void AStartWaitsForTheClocksBarLineAndLandsOnIt()
        {
            var source = new FakeSource { Line = new Timeline(1e9 + 123_456, 0, 120) };   // bars every 2 s from there
            var e = new Engine();
            e.Sync.Source = source;
            for (int i = 0; i < 20; i++) e.Callback();
            e.PlayRequested = true;
            for (int i = 0; i < 1000 && !e.Playing; i++) e.Callback();
            Assert.That(e.Playing, Is.True);
            double bar = source.Line.MicrosAt(Math.Round(source.Line.BeatAt(e.StartedHeard) / Bpb) * Bpb);
            Assert.That(Math.Abs(e.StartedHeard - bar), Is.LessThan(1e6 / Rate), "heard on the bar line, within a sample");
            double phase = source.Line.PhaseAt(e.StartedHeard, Bpb);
            Assert.That(phase < 0.001 || phase > Bpb - 0.001, Is.True, $"phase {phase}");
        }

        [Test]
        public void WhilePlayingItStaysOnTheClocksPhaseAndPublishesItsTempo()
        {
            var source = new FakeSource { Line = new Timeline(1e9, 0.37, 127.3), AlignOnBar = false };
            var e = new Engine { PlayRequested = true };
            e.Sync.Source = source;
            for (int i = 0; i < 20 * Rate / Buffer; i++) e.Callback();              // 20 s
            var late = e.Ticks.Where(t => t.Heard > 1e9 + 15e6).ToList();
            Assert.That(late.Count, Is.InRange(40, int.MaxValue));          // 5 s of ticks
            double worst = late.Max(t => Math.Abs(PhaseLock.Error(source.Line, Bpb, t.Clock / (double)Tpb, t.Heard))) * 60e6 / 127.3;
            Assert.That(worst, Is.LessThan(1000), $"worst {worst:F0} µs");
            Assert.That(e.Sync.Ours.TryRead(out var ours), Is.True);
            Assert.That(Math.Abs(ours.Tempo - 127.3), Is.LessThan(0.5));
            Assert.That(Math.Abs(e.Sync.ErrorMicros), Is.LessThan(1000));
        }
    }
}

namespace ReBuzz.SessionTests
{
    using ReBuzz.Core.Sync;

    /// <summary>MIDI clock in and out (0.8), pure.</summary>
    [TestFixture]
    public class MidiClockTests
    {
        const double Pulse120 = 60e6 / (120 * 24);                   // µs between pulses at 120 BPM

        [Test]
        public void PulsesGiveTheTempoAndBeatThroughJitter()
        {
            var rx = new MidiClockReceiver();
            var rnd = new Random(5);
            double t = 1e9;
            for (int i = 0; i < 96; i++, t += Pulse120)
                rx.OnMessage(0xF8, t + (rnd.NextDouble() - 0.5) * 1000);
            Assert.That(rx.Timeline.TryRead(out var line), Is.True);
            Assert.That(Math.Abs(line.Tempo - 120), Is.LessThan(0.3), $"tempo {line.Tempo}");
            Assert.That(rx.Running, Is.False, "no Start yet: tempo only");
        }

        [Test]
        public void AfterStartTheNextPulseIsBeatZero()
        {
            var rx = new MidiClockReceiver();
            double t = 1e9;
            for (int i = 0; i < 48; i++, t += Pulse120) rx.OnMessage(0xF8, t);
            Assert.That(rx.OnMessage(0xFA, t - Pulse120 / 2), Is.EqualTo(MidiClockEvent.Start));
            Assert.That(rx.Running, Is.True);
            Assert.That(rx.Timeline.TryRead(out var projected), Is.True);
            Assert.That(Math.Abs(projected.MicrosAt(0) - t), Is.LessThan(50), "beat 0 where the next pulse will be");
            rx.OnMessage(0xF8, t);
            rx.Timeline.TryRead(out var line);
            Assert.That(Math.Abs(line.BeatAt(t)), Is.LessThan(0.001));
            for (int i = 1; i <= 24; i++) rx.OnMessage(0xF8, t + i * Pulse120);
            rx.Timeline.TryRead(out line);
            Assert.That(Math.Abs(line.BeatAt(t + 24 * Pulse120) - 1), Is.LessThan(0.001), "a beat a 24 pulses");
            Assert.That(rx.OnMessage(0xFC, t), Is.EqualTo(MidiClockEvent.Stop));
            Assert.That(rx.Running, Is.False);
        }

        [Test]
        public void ASongPositionSetsTheNextPulsesBeat()
        {
            var rx = new MidiClockReceiver();
            double t = 1e9;
            for (int i = 0; i < 48; i++, t += Pulse120) rx.OnMessage(0xF8, t);
            rx.OnMessage(0xF2 | (16 << 8), t);                           // 16 sixteenths: beat 4
            Assert.That(rx.NextBeat, Is.EqualTo(4));
            rx.OnMessage(0xFB, t);
            rx.OnMessage(0xF8, t);
            rx.Timeline.TryRead(out var line);
            Assert.That(Math.Abs(line.BeatAt(t) - 4), Is.LessThan(0.001));
        }

        [Test]
        public void SendingStartsWithStartThenAPulseEveryTwentyFourthOfABeat()
        {
            var tx = new MidiClockSender();
            var ours = new Timeline(2e9, 0, 120);                     // beat 0 heard at 2e9
            double now = 2e9 - 50_000;
            Assert.That(tx.Peek(false, default, now, out _), Is.EqualTo(0), "stopped: nothing");
            int m = tx.Peek(true, ours, now, out double at);
            Assert.That(m, Is.EqualTo(MidiClockSender.Start));
            Assert.That(at, Is.EqualTo(2e9 - MidiClockSender.StartLead));
            tx.Sent(m, ours, at);
            for (int p = 0; p < 30; p++)
            {
                m = tx.Peek(true, ours, at, out at);
                Assert.That(m, Is.EqualTo(MidiClockSender.Clock));
                Assert.That(Math.Abs(at - (2e9 + p * Pulse120)), Is.LessThan(0.01), $"pulse {p}");
                tx.Sent(m, ours, at);
            }
            m = tx.Peek(false, ours, at, out _);
            Assert.That(m, Is.EqualTo(MidiClockSender.Stop));
            tx.Sent(m, ours, at);
            Assert.That(tx.Peek(false, ours, at, out _), Is.EqualTo(0));
        }

        [Test]
        public void JoiningWhilePlayingSendsThePositionThenContinues()
        {
            var tx = new MidiClockSender();
            var ours = new Timeline(2e9, 0, 120);
            double now = ours.MicrosAt(2.3);
            int m = tx.Peek(true, ours, now, out double at);
            Assert.That(m & 0xFF, Is.EqualTo(MidiClockSender.SongPosition));
            int sixteenth = ((m >> 8) & 0x7F) | (((m >> 16) & 0x7F) << 7);
            Assert.That(sixteenth, Is.EqualTo(10), "the next 16th after beat 2.3");
            tx.Sent(m, ours, at);
            m = tx.Peek(true, ours, now, out at);
            Assert.That(m, Is.EqualTo(MidiClockSender.Continue));
            Assert.That(at, Is.EqualTo(ours.MicrosAt(2.5) - MidiClockSender.StartLead));
            tx.Sent(m, ours, at);
            m = tx.Peek(true, ours, at, out at);
            Assert.That(m, Is.EqualTo(MidiClockSender.Clock));
            Assert.That(Math.Abs(at - ours.MicrosAt(2.5)), Is.LessThan(0.01));
        }

        [Test]
        public void LinkStateLinesParse()
        {
            Assert.That(LinkProtocol.ParseHello("hello 1"), Is.EqualTo(1));
            Assert.That(LinkProtocol.ParseHello("hi"), Is.EqualTo(-1));
            Assert.That(LinkProtocol.TryParseState("state 2140458497 6.170473000 123.500000 1 0 1", out var s), Is.True);
            Assert.That(s.Timeline, Is.EqualTo(new Timeline(2140458497, 6.170473, 123.5)));
            Assert.That((s.Peers, s.Playing, s.Enabled), Is.EqualTo((1, false, true)));
            Assert.That(LinkProtocol.TryParseState("state 1 x 120 0 0 1", out _), Is.False);
            Assert.That(LinkProtocol.TryParseState("state 1 2 0 0 0 1", out _), Is.False, "no tempo");
            Assert.That(LinkProtocol.Beat(1.5, 1234.4), Is.EqualTo("beat 1.5 1234"));
            Assert.That(LinkProtocol.Tempo(120.25), Is.EqualTo("tempo 120.25"));
        }
    }
}
