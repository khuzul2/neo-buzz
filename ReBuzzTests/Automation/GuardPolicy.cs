// Fork (docs/specs/machines.md §4): the one policy of every timing guard and every allocation guard in the test projects.
//
// Why: the guards ran on shared GitHub runners, where another job, a preemption or the runtime's own one-off work
// (a lazy method resolution on the first GC suspension, a tiering step) lands in a measured stretch now and then. Each
// such hit failed a guard once and passed on rerun (CPU floors of Neo EQ, Delay and Transient; Delay's allocation check;
// CpuIsSmall), costing a rerun on most branches. Both kinds of noise only ever ADD (time, or a few hundred bytes of the
// runtime's own), and a real regression does not come and go:
//  - Timing: a guard's measurement is repeated, up to Attempts times, and the guard passes when any attempt does (the
//    best of N). A machine that really got 2× slower is 2× slower in every attempt, so it fails all three. The floors'
//    meaning is unchanged.
//  - Allocation: a measurement warms the path up (Warm blocks, twice) before it measures, and a non-zero result is
//    measured again, up to Attempts times in all; the guard fails only when every attempt allocates. An allocation per
//    block in the machine reproduces in every attempt, so it still fails; a one-off of the runtime does not.
// The failure text of a guard that failed lists every attempt, so a real regression shows its figures and a flake its spread.
//
// Everything that decides a timing or allocation verdict goes through here. GuardPolicyTests proves the policy still fails
// a block that allocates and a workload that is 2× slower.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzzTests.Automation
{
    internal static class GuardPolicy
    {
        /// <summary>A guard measures up to this many times and passes if one attempt does.</summary>
        public const int Attempts = 3;

        /// <summary>An allocation check runs its path this many unmeasured passes before it measures.</summary>
        public const int WarmPasses = 2;

        /// <summary>
        /// Timing: measures until an attempt satisfies <paramref name="passes"/> (at most <see cref="Attempts"/> times) and
        /// returns that one; when none does, the attempts' results come back in <paramref name="all"/> and the result is the
        /// last. Use it for a guard that can be asserted from one measurement: <c>var r = Retry(measure, r => r.Speed &gt; floor, out var all)</c>.
        /// </summary>
        public static T Retry<T>(Func<T> measure, Func<T, bool> passes, out IReadOnlyList<T> all)
        {
            var list = new List<T>();
            all = list;
            T result = default!;
            for (int i = 0; i < Attempts; i++)
            {
                result = measure();
                list.Add(result);
                if (passes(result)) return result;
            }
            return result;
        }

        /// <summary>
        /// Timing, for a measurement that is a time (lower is better): the smallest of <see cref="Attempts"/> measurements
        /// (a shared runner's noise only adds time).
        /// </summary>
        public static double LeastOf(Func<double> measure)
        {
            double best = double.MaxValue;
            for (int i = 0; i < Attempts; i++) best = Math.Min(best, measure());
            return best;
        }

        /// <summary>
        /// Timing, for a measurement that is a speed (higher is better): the greatest of <see cref="Attempts"/> measurements,
        /// stopping at the first that reaches <paramref name="enough"/>.
        /// </summary>
        public static double GreatestOf(Func<double> measure, double enough = double.PositiveInfinity)
        {
            double best = double.MinValue;
            for (int i = 0; i < Attempts; i++)
            {
                best = Math.Max(best, measure());
                if (best >= enough) break;
            }
            return best;
        }

        /// <summary>
        /// Allocation: <paramref name="measure"/> returns the bytes a stretch allocated and what happened in it (it does its own
        /// warm-up). The first attempt that allocates nothing wins; when every attempt allocates, the verdict is the last
        /// one's bytes and the text lists every attempt.
        /// </summary>
        public static (long Bytes, string Why) Allocation(Func<(long Bytes, string Why)> measure)
        {
            var seen = new List<(long Bytes, string Why)>();
            for (int i = 0; i < Attempts; i++)
            {
                var r = measure();
                if (r.Bytes == 0) return (0, "");
                seen.Add(r);
            }
            long least = seen.Min(s => s.Bytes);
            string why = $"allocated in all {Attempts} attempts ({string.Join(", ", seen.Select(s => s.Bytes + " bytes"))}; a real allocation reproduces)\n" +
                         string.Join("\n", seen.Select((s, i) => $"attempt {i + 1}: {s.Why}"));
            return (least, why);
        }

        /// <summary>
        /// Allocation, for a guard that is a stretch of work on this thread: the bytes it allocates, 0 when any of up to
        /// <see cref="Attempts"/> measurements allocated nothing, after <paramref name="work"/> has run <see cref="WarmPasses"/> times
        /// unmeasured (<paramref name="work"/> must be safe to repeat: a steady state). Otherwise the least of the attempts.
        /// </summary>
        public static long Allocated(Action work, bool warm = true)
        {
            if (warm) Warm(work);
            long least = long.MaxValue;
            for (int i = 0; i < Attempts; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                work();
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                if (bytes == 0) return 0;
                least = Math.Min(least, bytes);
            }
            return least;
        }

        /// <summary>Runs <paramref name="pass"/> <see cref="WarmPasses"/> times, unmeasured.</summary>
        public static void Warm(Action pass)
        {
            for (int i = 0; i < WarmPasses; i++) pass();
        }
    }
}
