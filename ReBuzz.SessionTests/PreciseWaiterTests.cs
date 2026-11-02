using System.Diagnostics;
using ReBuzz.Platform;

namespace ReBuzz.SessionTests
{
    /// <summary>The MIDI timing threads' waits (docs/specs/mac-engine.md M6), on each OS's own timer.</summary>
    [TestFixture]
    public class PreciseWaiterTests
    {
        [Test]
        public void ItUsesThePlatformsTimer()
        {
            using var w = new PreciseWaiter();
            string expected = OperatingSystem.IsWindows() ? "waitable timer" : OperatingSystem.IsMacOS() ? "mach_wait_until" : OperatingSystem.IsLinux() ? "clock_nanosleep" : "sleep";
            Assert.That(w.Kind, Is.EqualTo(expected));
        }

        [Test]
        public void ShortWaitsLastAboutWhatTheyAsk()
        {
            using var w = new PreciseWaiter();
            var times = new List<double>();
            for (int i = 0; i < 50; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                w.Micros(500);
                times.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
            }
            times.Sort();
            double median = times[times.Count / 2];
            TestContext.Out.WriteLine($"{w.Kind}: median {median:F0} µs, min {times[0]:F0}, max {times[^1]:F0}");
            Assert.That(times[0], Is.GreaterThanOrEqualTo(450), "never much shorter than asked");
            Assert.That(median, Is.LessThan(2500), "half a millisecond, not a scheduler tick (15.6 ms on Windows' default)");
        }
    }
}
