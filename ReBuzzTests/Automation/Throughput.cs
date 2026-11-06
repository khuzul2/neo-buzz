// Fork (docs/specs/machines.md §4, "Throughput guards"): a whole render's speed, timed against a reference workload in
// the same run, so that a slow or busy shared runner can't fail a guard while a machine that really got slower still
// does. Each guard said "this render runs faster than N× real time" from one stretch of wall clock, and the CI runners'
// spread was wider than the guards' margins: on the Mac runner Neo Poly measured 6.7–11.5× against a floor of 5,
// Neo Multiband 29–54× against 20, Neo Ensemble 91–368× against 50, and the engine's heavy song 3.5–39× against 1. A
// floor low enough for that spread lets a machine twice as slow through.
//
// What a guard measures now (the approach of MultibandTests.ACrossoverSweepStaysBoundedPerBlock, PR #97):
//  - The render is made Reps times from the same start (each rep's own engine, set up and warmed untimed), split into
//    chunks of whole blocks at fixed places, so chunk c of every rep is the same work. A chunk's time is its fastest
//    over the reps: other work on a shared machine (another process, a preemption, an interrupt) only ever adds time,
//    so it drops out unless it hit that chunk in every rep. The render's time is the sum over its chunks.
//  - After each chunk (before it, in every other rep) the reference workload runs for as long as the machine's chunks
//    have proved to take, undisturbed (sized from the reps so far, so either is as likely to be hit by other work: a
//    first guess from a busy warm-up once made the reference's chunks four times the machine's, and every one of them
//    hit), timed per unit and summed the same way: a fixed piece of DSP-like arithmetic of its own (below), sharing no
//    code with any machine. A runner slower than another, or slowed for the whole run (a lower clock), slows both.
//  - The guard's figure is the render's speed at the reference's speed: × real time as timed, times the reference's
//    measured time per unit over its nominal one (NominalUnitSeconds, about its time on the Linux runner). Twice the
//    machine's work halves it; a runner twice as slow leaves it.
// Each guard's floor is set from that figure's spread over runs on the Linux, macOS and Windows runners and locally
// under load, so that the slowest run passes and the fastest run's half fails: a machine twice as slow fails on any.
//
// Calibrating a guard (tools/throughput-soak.sh does both, quiet and loaded): NEOBUZZ_THROUGHPUT_SOAK=20 runs each
// guard's measurement 20 times and reports every run (the figure, the plain × real time the guard used to take, the
// reference's unit time) instead of asserting, and NEOBUZZ_THROUGHPUT_SLOWDOWN=2 makes every machine chunk do its work
// twice (a machine twice as slow), which every run of every guard must then fail. Figures go to the test output and,
// with MEASUREMENTS_DIR, to throughput.txt there.
//
// NeoBuzz.MachineTests compiles this file too (as AllocationWindow.cs), for the machines' guards; ReBuzzTests has the
// engine's.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace ReBuzzTests.Automation
{
    internal static class Throughput
    {
        /// <summary>Renders from the same start: each chunk's time is its fastest of at least this many.</summary>
        public const int Reps = 5;

        /// <summary>More reps for a quick render, so a guard times at least this much of the machine's work (64 reps at most).</summary>
        const double LeastSeconds = 0.25;

        /// <summary>A rep is split into at least this many chunks.</summary>
        const int LeastChunks = 16;

        /// <summary>
        /// The reference's nominal time per unit (64 samples of its chain): about what it takes on the Linux runner (AMD EPYC
        /// 9V74: 368–387 ns), so a figure there reads about as × real time. Elsewhere: Windows' (AMD Zen 5) 278–332 ns, the Mac
        /// runner's (Apple M1, virtual) 273–314, a 2.1 GHz Xeon 433–514. The floors are in its units: changing it scales them all.
        /// </summary>
        public const double NominalUnitSeconds = 0.4e-6;

        /// <summary>A chunk is as many whole blocks as take about this long (at least one block; fewer for a quick render).</summary>
        const double ChunkSeconds = 0.001;

        /// <summary>The figures of one measurement.</summary>
        public sealed record Result(string What, double Speed, double TimedSpeed, double BestSpeed, double UnitSeconds, int Chunks, int ChunkSamples, int Units, int Reps, int Slowdown)
        {
            /// <summary>One line for the test output and the measurements.</summary>
            public override string ToString() => string.Format(CultureInfo.InvariantCulture,
                "{0}: {1:F1}× real time at the reference's speed ({2:F1}× as timed, the reference {3:F0} ns a unit; {4} chunks of {5} samples, {6} reps{7})",
                What, Speed, TimedSpeed, UnitSeconds * 1e9, Chunks, ChunkSamples, Reps, Slowdown > 1 ? $", the machine's work ×{Slowdown}" : "");
        }

        /// <summary>The machine's work done this many times a chunk (NEOBUZZ_THROUGHPUT_SLOWDOWN): a guard's own check.</summary>
        public static int Slowdown => int.TryParse(Environment.GetEnvironmentVariable("NEOBUZZ_THROUGHPUT_SLOWDOWN"), out int k) && k > 1 ? k : 1;

        /// <summary>Runs per guard reported instead of asserted (NEOBUZZ_THROUGHPUT_SOAK), or 0.</summary>
        public static int Soak => int.TryParse(Environment.GetEnvironmentVariable("NEOBUZZ_THROUGHPUT_SOAK"), out int n) && n > 0 ? n : 0;

        /// <summary>A floor for this processor's architecture (the reference and the machines differ in their mix).</summary>
        public static double Floor(double x64, double arm64) => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? arm64 : x64;

        /// <summary>
        /// A chunk that runs blocks of <paramref name="block"/> samples over chunk c's place in the render,
        /// [c · chunkSamples, (c + 1) · chunkSamples): <paramref name="process"/>(position, count) for each.
        /// </summary>
        public static Func<int, Action<int>> InBlocks(int block, Action<int, int> process) => chunkSamples => c =>
        {
            int end = (c + 1) * chunkSamples;
            for (int pos = c * chunkSamples; pos < end; pos += block) process(pos, Math.Min(block, end - pos));
        };

        /// <summary>
        /// The guard: measures (Soak times over, reporting each, when asked), and asserts the figure is above
        /// <paramref name="floor"/>. <paramref name="start"/> makes the machine anew, set up and warmed (untimed), and
        /// returns its chunks for a chunk size (<see cref="InBlocks"/>); <paramref name="seconds"/> of audio a rep at
        /// <paramref name="rate"/>, in whole blocks of <paramref name="block"/>.
        /// </summary>
        public static Result Guard(string what, double floor, double seconds, int rate, int block, Func<Func<int, Action<int>>> start,
                                   Action<string>? report = null)
        {
            report ??= line => TestContext.Out.WriteLine(line);
            int soak = Soak;
            if (soak == 0)
            {
                var r = Measure(what, seconds, rate, block, start);
                report(r.ToString());
                Assert.That(r.Speed, Is.GreaterThan(floor),
                    $"{what}: {r.Speed:F1}× real time at the reference's speed, under its floor of {floor}× ({r})");
                return r;
            }
            var runs = new List<Result>();
            for (int i = 0; i < soak; i++)
            {
                var r = Measure(what, seconds, rate, block, start);
                runs.Add(r);
                Record(string.Format(CultureInfo.InvariantCulture, "soak {0} run {1}/{2}: speed {3:F2} timed {4:F2} best {5:F2} unit_ns {6:F1} chunks {7}x{8} reps {9} units {10} slowdown {11} floor {12}",
                    what, i + 1, soak, r.Speed, r.TimedSpeed, r.BestSpeed, r.UnitSeconds * 1e9, r.Chunks, r.ChunkSamples, r.Reps, r.Units, r.Slowdown, floor), report);
            }
            string Spread(Func<Result, double> f) => string.Format(CultureInfo.InvariantCulture, "{0:F2} … {1:F2} (median {2:F2})",
                runs.Min(f), runs.Max(f), runs.Select(f).OrderBy(x => x).ElementAt(runs.Count / 2));
            Record($"soak {what} summary: speed {Spread(r => r.Speed)}, timed {Spread(r => r.TimedSpeed)}, unit_ns {Spread(r => r.UnitSeconds * 1e9)}; " +
                   $"{runs.Count(r => r.Speed <= floor)}/{runs.Count} under the floor {floor.ToString(CultureInfo.InvariantCulture)} on {Platform}", report);
            return runs[^1];
        }

        /// <summary>Where the figures were taken: the OS, the architecture and the processor.</summary>
        public static string Platform => $"{RuntimeInformation.OSDescription.Split(' ')[0]} {RuntimeInformation.ProcessArchitecture} ({Cpu}, {Environment.ProcessorCount} cores)";

        static string? cpu;
        static string Cpu => cpu ??= ReadCpu();

        static string ReadCpu()
        {
            try
            {
                if (OperatingSystem.IsLinux())
                    return File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2)[1].Trim() ?? "?";
                if (OperatingSystem.IsWindows())
                    return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "?";
                if (OperatingSystem.IsMacOS())
                {
                    using var p = Process.Start(new ProcessStartInfo("sysctl", "-n machdep.cpu.brand_string") { RedirectStandardOutput = true, UseShellExecute = false });
                    string s = p!.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    return s;
                }
            }
            catch (Exception)
            {
            }
            return "?";
        }

        static void Record(string line, Action<string> report)
        {
            report(line);
            var dir = Environment.GetEnvironmentVariable("MEASUREMENTS_DIR");
            if (string.IsNullOrEmpty(dir)) return;
            lock (typeof(Throughput))
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "throughput.txt"), line + Environment.NewLine);
            }
        }

        /// <summary>One measurement (see the file's head).</summary>
        public static Result Measure(string what, double seconds, int rate, int block, Func<Func<int, Action<int>>> start)
        {
            int slowdown = Slowdown;
            int total = Math.Max(block, (int)Math.Round(seconds * rate / block) * block);

            // Warm-up: a whole rep, untimed but for its pace (a typical block's time: a busy spell in it mustn't size
            // the chunks), which sizes the chunks, and the reference's time per unit.
            int blocks = total / block;
            var warm = start()(block);
            var paces = new double[blocks];
            for (int c = 0; c < blocks; c++) paces[c] = TimeChunk(warm, c, 1);
            Array.Sort(paces);
            double perBlock = Math.Max(1e-9, paces[blocks / 2] * slowdown);
            double unit = UnitSeconds();
            int blocksPerChunk = Math.Clamp((int)Math.Round(ChunkSeconds / perBlock), 1, Math.Max(1, blocks / LeastChunks));
            int chunkSamples = blocksPerChunk * block;
            int chunks = total / chunkSamples;
            int units = Math.Max(1, (int)Math.Round(perBlock * blocksPerChunk / unit));
            int reps = Math.Clamp((int)Math.Ceiling(LeastSeconds / (perBlock * blocksPerChunk * chunks)), Reps, 64);

            // Each chunk's fastest time over the reps, the machine's whole and the reference's per unit.
            var machine = new double[chunks];
            var reference = new double[chunks];
            Array.Fill(machine, double.MaxValue);
            Array.Fill(reference, double.MaxValue);
            double all = 0;
            for (int rep = 0; rep < reps; rep++)
            {
                var chunk = start()(chunkSamples);
                GC.Collect();
                for (int c = 0; c < chunks; c++)
                {
                    double m, r;
                    if ((rep & 1) == 0)
                    {
                        m = TimeChunk(chunk, c, slowdown);
                        r = TimeReference(units);
                    }
                    else
                    {
                        r = TimeReference(units);
                        m = TimeChunk(chunk, c, slowdown);
                    }
                    all += m;
                    if (m < machine[c]) machine[c] = m;
                    if (r / units < reference[c]) reference[c] = r / units;
                }
                // The reference's chunks as long as the machine's have proved to be so far, undisturbed: a chunk of
                // either is as likely as the other to be hit by other work (the warm-up's pace is only a first guess).
                units = Math.Max(1, (int)Math.Round(machine.Average() / reference.Min()));
            }
            double audio = (double)chunks * chunkSamples / rate;
            double best = machine.Sum();
            double unitSeconds = reference.Average();
            double bestSpeed = audio / best;
            return new Result(what, bestSpeed * unitSeconds / NominalUnitSeconds, audio * reps / all, bestSpeed, unitSeconds, chunks, chunkSamples, units, reps, slowdown);
        }

        // ---- one render's cost against another's -------------------------------------------------------------------

        /// <summary>The figures of one comparison (<see cref="CompareCost"/>).</summary>
        public sealed record Cost(string What, double Ratio, double BaseRealTime, double TestRealTime, int BaseChunks, int TestChunks, int ChunkSamples, int Reps, int Slowdown)
        {
            /// <summary>One line for the test output and the measurements.</summary>
            public override string ToString() => string.Format(CultureInfo.InvariantCulture,
                "{0}: a second of it costs {1:F3}× a second of the baseline ({2:F1}× real time, the baseline {3:F1}×; {4} chunks, the baseline {5}, of {6} samples; {7} reps{8})",
                What, Ratio, TestRealTime, BaseRealTime, TestChunks, BaseChunks, ChunkSamples, Reps, Slowdown > 1 ? $", the test's work ×{Slowdown}" : "");
        }

        /// <summary>
        /// The cost guard: a second of <paramref name="test"/> (a render of <paramref name="testSeconds"/>) costs less than
        /// <paramref name="limit"/> times a second of <paramref name="baseline"/> (<paramref name="baseSeconds"/>), both the
        /// same machine (silence after noise against noise): <see cref="CompareCost"/>, asserted, or Soak times over and
        /// reported. NEOBUZZ_THROUGHPUT_SLOWDOWN does the test's chunks that many times.
        /// </summary>
        public static Cost CostGuard(string what, double limit, int rate, int block, double baseSeconds, Func<Func<int, Action<int>>> baseline,
                                     double testSeconds, Func<Func<int, Action<int>>> test, Action<string>? report = null)
        {
            report ??= line => TestContext.Out.WriteLine(line);
            int soak = Soak;
            if (soak == 0)
            {
                var r = CompareCost(what, rate, block, baseSeconds, baseline, testSeconds, test);
                report(r.ToString());
                Assert.That(r.Ratio, Is.LessThan(limit), $"{what}: a second of it costs {r.Ratio:F2}× a second of the baseline, over its limit of {limit}× ({r})");
                return r;
            }
            var runs = new List<Cost>();
            for (int i = 0; i < soak; i++)
            {
                var r = CompareCost(what, rate, block, baseSeconds, baseline, testSeconds, test);
                runs.Add(r);
                Record(string.Format(CultureInfo.InvariantCulture, "soak {0} run {1}/{2}: ratio {3:F4} base_xrt {4:F2} test_xrt {5:F2} chunks {6}+{7}x{8} reps {9} slowdown {10} limit {11}",
                    what, i + 1, soak, r.Ratio, r.BaseRealTime, r.TestRealTime, r.BaseChunks, r.TestChunks, r.ChunkSamples, r.Reps, r.Slowdown, limit), report);
            }
            var sorted = runs.Select(r => r.Ratio).OrderBy(x => x).ToList();
            Record(string.Format(CultureInfo.InvariantCulture, "soak {0} summary: ratio {1:F4} … {2:F4} (median {3:F4}); {4}/{5} over the limit {6} on {7}",
                what, sorted[0], sorted[^1], sorted[sorted.Count / 2], runs.Count(r => r.Ratio >= limit), runs.Count, limit, Platform), report);
            return runs[^1];
        }

        /// <summary>
        /// What a second of one render costs against a second of another of the same machine, measured as <see cref="Measure"/>
        /// measures (machines.md §4): each render made Reps times or more from its start (<paramref name="baseline"/> and
        /// <paramref name="test"/> make the machine anew, set up and warmed untimed, as for <see cref="Guard"/>), split into
        /// chunks of whole blocks at fixed places (the same size for both: about 1 ms of the baseline's work), so chunk c of
        /// every rep is the same work; the two renders' chunks interleaved in proportion to their lengths, the first of each
        /// pair alternating rep to rep; a chunk's time its fastest over the reps, each render's time the sum of its chunks'.
        /// Other work on a shared runner only adds time, so it drops out unless it hits a chunk in every rep, and a runner
        /// that is slower, or slowed for the whole run, slows both renders alike: the ratio stays.
        /// </summary>
        public static Cost CompareCost(string what, int rate, int block, double baseSeconds, Func<Func<int, Action<int>>> baseline,
                                       double testSeconds, Func<Func<int, Action<int>>> test)
        {
            int slowdown = Slowdown;
            int baseTotal = Math.Max(block, (int)Math.Round(baseSeconds * rate / block) * block);
            int testTotal = Math.Max(block, (int)Math.Round(testSeconds * rate / block) * block);

            // Warm-up: a whole baseline, untimed but for its pace (a typical block's), which sizes the chunks.
            int baseBlocks = baseTotal / block;
            var warm = baseline()(block);
            var paces = new double[baseBlocks];
            for (int c = 0; c < baseBlocks; c++) paces[c] = TimeChunk(warm, c, 1);
            Array.Sort(paces);
            double perBlock = Math.Max(1e-9, paces[baseBlocks / 2]);
            int blocksPerChunk = Math.Clamp((int)Math.Round(ChunkSeconds / perBlock), 1, Math.Max(1, baseBlocks / LeastChunks));
            int chunkSamples = blocksPerChunk * block;
            int baseChunks = baseTotal / chunkSamples, testChunks = testTotal / chunkSamples;
            int reps = Math.Clamp((int)Math.Ceiling(LeastSeconds / (perBlock * blocksPerChunk * baseChunks)), Reps, 64);

            var baseBest = new double[baseChunks];
            var testBest = new double[testChunks];
            Array.Fill(baseBest, double.MaxValue);
            Array.Fill(testBest, double.MaxValue);
            for (int rep = 0; rep < reps; rep++)
            {
                var b = baseline()(chunkSamples);
                var t = test()(chunkSamples);
                GC.Collect();
                int i = 0, j = 0;
                while (i < baseChunks || j < testChunks)
                {
                    // The render further behind in its own length goes next; on a tie, the baseline in even reps.
                    long pb = (long)i * testChunks, pt = (long)j * baseChunks;
                    bool baseNext = j >= testChunks || (i < baseChunks && (pb < pt || (pb == pt && (rep & 1) == 0)));
                    if (baseNext)
                    {
                        double s = TimeChunk(b, i, 1);
                        if (s < baseBest[i]) baseBest[i] = s;
                        i++;
                    }
                    else
                    {
                        double s = TimeChunk(t, j, slowdown);
                        if (s < testBest[j]) testBest[j] = s;
                        j++;
                    }
                }
            }
            double baseAudio = (double)baseChunks * chunkSamples / rate, testAudio = (double)testChunks * chunkSamples / rate;
            double basePerSecond = baseBest.Sum() / baseAudio, testPerSecond = testBest.Sum() / testAudio;
            return new Cost(what, testPerSecond / basePerSecond, 1 / basePerSecond, 1 / testPerSecond, baseChunks, testChunks, chunkSamples, reps, slowdown);
        }

        static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

        static double TimeChunk(Action<int> chunk, int c, int slowdown)
        {
            long t = Stopwatch.GetTimestamp();
            for (int k = 0; k < slowdown; k++) chunk(c);
            return Seconds(Stopwatch.GetTimestamp() - t);
        }

        static double TimeReference(int units)
        {
            long t = Stopwatch.GetTimestamp();
            sink += Reference(units);
            return Seconds(Stopwatch.GetTimestamp() - t);
        }

        /// <summary>The reference's time per unit, the fastest of a few short runs (to size its chunks).</summary>
        static double UnitSeconds()
        {
            sink += Reference(64);
            double best = double.MaxValue;
            for (int i = 0; i < 40; i++)
                best = Math.Min(best, TimeReference(16) / 16);
            return best;
        }

        // ---- the reference workload --------------------------------------------------------------------------------

        static double sink;
        static uint seed = 22222;
        static double z1, z2;
        static float lp, phase;
        static int at;
        static readonly float[] line = new float[512];

        /// <summary>
        /// <paramref name="units"/> × 64 samples of a fixed chain, as a machine's inner loop is built: a noise source
        /// (integer), a low-pass biquad in doubles (a dependency chain), a one-pole, a soft clip with a division, a
        /// delay line of 2 KB written and read back with linear interpolation at a moving tap, and a sine-ish LFO by
        /// polynomial. Compiled fully optimised at once, whatever the host's tiering.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        static double Reference(int units)
        {
            const double b0 = 0.0039160, b1 = 0.0078320, b2 = 0.0039160, a1 = -1.8153396, a2 = 0.8310036;   // 1 kHz at 48 kHz, Q 0.707
            uint s = seed;
            double y1 = z1, y2 = z2;
            float p = lp, ph = phase;
            int w = at;
            var buf = line;
            double acc = 0;
            for (int u = 0; u < units; u++)
            {
                for (int i = 0; i < 64; i++)
                {
                    s = s * 1664525u + 1013904223u;
                    float x = (int)s * (1f / 2147483648f);
                    double y = b0 * x + y1;
                    y1 = b1 * x - a1 * y + y2;
                    y2 = b2 * x - a2 * y;
                    p += 0.05f * ((float)y * 8f - p);
                    float d = p * 2f;
                    float clip = d / (1f + Math.Abs(d));
                    ph += 1f / 4096f;
                    if (ph >= 1f) ph -= 2f;
                    float lfo = ph * (2f - Math.Abs(ph) * 2f);
                    buf[w & 511] = clip + 0.3f * lfo;
                    float tap = 200.5f + 100f * lfo;
                    int ti = (int)tap;
                    float frac = tap - ti;
                    float a = buf[(w - ti) & 511], b = buf[(w - ti - 1) & 511];
                    acc += a + frac * (b - a);
                    w++;
                }
            }
            seed = s;
            z1 = y1;
            z2 = y2;
            lp = p;
            phase = ph;
            at = w & 511;
            return acc;
        }
    }
}
