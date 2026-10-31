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
