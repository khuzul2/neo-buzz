// The machines' zero-allocation tests, measured one block at a time so that a failure says what allocated. Their
// audio paths allocate nothing, and the tests have always measured them in one stretch after a warm-up; on CI one
// failed now and then (Neo Delay on the Mac runner: 5768 bytes, then 5736, in a single stretch; never in isolation),
// and a total can't say where or why. Each block is measured by ReBuzzTests' AllocationWindow (compiled in): the bytes
// this thread allocated in it, the methods the JIT compiled on it, the GCs, and the runtime's own events on the thread
// (JIT with each method and its tier, type loads, exceptions, contention, waits, the GC's allocation ticks) placed in
// their blocks. A failure lists the blocks that allocated, each with what happened in it. The assertion is unchanged:
// 0 bytes.

using ReBuzzTests.Automation;

namespace NeoBuzz.MachineTests
{
    internal static class AllocationCheck
    {
        /// <summary>
        /// Runs blocks 0 … <paramref name="count"/> − 1 of an audio path (<paramref name="block"/>(b): whatever the test does
        /// per block, the parameter changes with it), each measured on this thread. Returns the bytes allocated in all and,
        /// when any were, which blocks and what the thread did in them ("" when none did).
        /// </summary>
        public static (long Bytes, string Why) Run(int count, Action<int> block)
        {
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
