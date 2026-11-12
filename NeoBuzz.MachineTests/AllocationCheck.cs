// The machines' zero-allocation tests, measured one block at a time so that a failure says what allocated. Their
// audio paths allocate nothing, and the tests have always measured them in one stretch after a warm-up; on CI one
// failed now and then (Neo Delay on the Mac runner: 5768 bytes, then 5736, in a single stretch; never in isolation),
// and a total can't say where or why. Each block is measured by ReBuzzTests' AllocationWindow (compiled in): the bytes
// this thread allocated in it, the methods the JIT compiled on it, the GCs, and the runtime's own events on the thread
// (JIT with each method and its tier, type loads, exceptions, contention, waits, the GC's allocation ticks) placed in
// their blocks. A failure lists the blocks that allocated, each with what happened in it. The assertion is unchanged:
// 0 bytes, under GuardPolicy (ReBuzzTests/Automation/GuardPolicy.cs): the path is warmed up twice, and a stretch that
// allocated is measured again, up to 3 attempts, and fails only if every attempt allocates.

using System.Reflection;
using System.Threading;
using ReBuzzTests.Automation;

namespace NeoBuzz.MachineTests
{
    internal static class AllocationCheck
    {
        static readonly Action pollGc = (Action)Delegate.CreateDelegate(typeof(Action),
            typeof(Thread).GetMethod("PollGC", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes)
            ?? typeof(AllocationCheck).GetMethod(nameof(Nothing), BindingFlags.Static | BindingFlags.NonPublic)!);

        static void Nothing() { }

        /// <summary>
        /// What the runtime does the first time a collection suspends this thread is not the machine's: Pedal EQ failed with 968
        /// bytes in one block of its stretch (runs 43239764974 and one before), the block a collection from another thread
        /// (the finalizer thread, running finalizers of earlier tests' objects) had suspended, whose only event on this thread
        /// was R2RGetEntryPoint for System.Threading.Thread.PollGC, the runtime's first use of it in the process. So before a
        /// stretch the thread calls that method once (resolved from then on), and the earlier tests' garbage is collected and
        /// finalized, so no other thread's collection or finalizers fall in the stretch.
        /// </summary>
        static void Settle()
        {
            pollGc();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>
        /// Runs blocks 0 … <paramref name="count"/> − 1 of an audio path (<paramref name="block"/>(b): whatever the test does
        /// per block, the parameter changes with it), each measured on this thread. Returns the bytes allocated in all and,
        /// when any were, which blocks and what the thread did in them ("" when none did).
        /// </summary>
        public static (long Bytes, string Why) Run(int count, Action<int> block)
        {
            // Two unmeasured passes over the first blocks (what the first measured blocks would otherwise be the first of),
            // then the measured stretch, repeated while it allocates (GuardPolicy.Allocation).
            int warm = Math.Min(count, 64);
            GuardPolicy.Warm(() => { for (int b = 0; b < warm; b++) block(b); });
            return GuardPolicy.Allocation(() => Once(count, block));
        }

        static (long Bytes, string Why) Once(int count, Action<int> block)
        {
            Settle();
            using var window = new AllocationWindow(count, unit: "block");
            long total = 0;
            var first = new List<string>();
            for (int b = 0; b < count; b++)
            {
                window.Begin();
                block(b);
                long bytes = window.End();
                if (bytes == 0) continue;
                total += bytes;
                if (first.Count < 10) first.Add($"block {b}: {bytes} bytes");
            }
            if (total == 0) return (0, "");
            return (total, $"{total} bytes in {count} blocks ({string.Join(", ", first)})" + window.Describe());
        }
    }
}
