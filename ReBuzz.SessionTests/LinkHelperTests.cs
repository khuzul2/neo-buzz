using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// The Link helper (LinkHelper/, docs/specs/mac-engine.md M6) as ReBuzz runs it: it starts,
    /// says hello, reports state lines in Stopwatch's time, takes commands and quits. CI builds it
    /// with CMake on macOS and Linux and sets REBUZZ_LINK_HELPER; elsewhere this is skipped.
    /// </summary>
    [TestFixture]
    public class LinkHelperTests
    {
        static double StopwatchMicros() => Stopwatch.GetTimestamp() * 1e6 / Stopwatch.Frequency;

        sealed record State(double Micros, double Beat, double Bpm, int Peers, bool Playing, bool Enabled, double ReadAt);

        static State Parse(string line, double readAt)
        {
            var f = line.Split(' ');
            var c = CultureInfo.InvariantCulture;
            return new State(double.Parse(f[1], c), double.Parse(f[2], c), double.Parse(f[3], c), int.Parse(f[4], c), f[5] == "1", f[6] == "1", readAt);
        }

        [Test]
        public void TheHelperRunsOnStopwatchTimeTakesCommandsAndQuits()
        {
            string helper = Environment.GetEnvironmentVariable("REBUZZ_LINK_HELPER") ?? "";
            if (helper.Length == 0 || !File.Exists(helper)) Assert.Ignore("ReBuzzLink isn't built here (CI sets REBUZZ_LINK_HELPER)");

            var info = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
            using var p = Process.Start(info)!;
            var lines = new BlockingCollection<(string Line, double At)>();
            var reader = new Thread(() =>
            {
                string? l;
                while ((l = p.StandardOutput.ReadLine()) != null) lines.Add((l, StopwatchMicros()));
                lines.CompleteAdding();
            }) { IsBackground = true };
            reader.Start();
            try
            {
                (string Line, double At) Next() => lines.TryTake(out var l, 3000) ? l : throw new TimeoutException("no line from the helper in 3 s");
                State NextState(Func<State, bool> wanted)
                {
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 3000)
                    {
                        var (line, at) = Next();
                        if (!line.StartsWith("state ")) continue;
                        var s = Parse(line, at);
                        if (wanted(s)) return s;
                    }
                    throw new TimeoutException("the state didn't come in 3 s");
                }

                Assert.That(Next().Line, Is.EqualTo("hello 1"));

                // Its clock is Stopwatch's: a fresh line's time is a moment before we read it.
                double nearest = double.MaxValue;
                for (int i = 0; i < 30; i++)
                {
                    var s = NextState(_ => true);
                    double behind = s.ReadAt - s.Micros;
                    if (Math.Abs(behind) < Math.Abs(nearest)) nearest = behind;
                }
                TestContext.Out.WriteLine($"a state line's time, read: {nearest:F0} µs before Stopwatch's now");
                Assert.That(nearest, Is.InRange(-2_000, 20_000), "the helper's host time is Stopwatch's, in microseconds");

                p.StandardInput.WriteLine("enable 1");
                p.StandardInput.WriteLine("tempo 100");
                p.StandardInput.Flush();
                var a = NextState(s => s.Enabled && Math.Abs(s.Bpm - 100) < 1e-6);
                Assert.That(a.Peers, Is.EqualTo(0), "alone on the runner");
                var b = NextState(s => s.Micros - a.Micros > 300_000);
                double beatsPerSecond = (b.Beat - a.Beat) / ((b.Micros - a.Micros) / 1e6);
                Assert.That(beatsPerSecond, Is.EqualTo(100 / 60.0).Within(1).Percent, "beats advance at the tempo, in its time");

                p.StandardInput.WriteLine($"play 1 {(long)StopwatchMicros()}");
                p.StandardInput.Flush();
                Assert.That(NextState(s => s.Playing).Playing, Is.True);

                p.StandardInput.WriteLine("quit");
                p.StandardInput.Flush();
                Assert.That(p.WaitForExit(3000), Is.True, "it quits when asked");
                Assert.That(p.ExitCode, Is.EqualTo(0));
            }
            finally
            {
                if (!p.HasExited) p.Kill();
            }
        }
    }
}
