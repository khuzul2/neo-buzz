using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// The CPU time the calling thread has used, in milliseconds (RB1). A budget for what a block of audio costs is a budget for the work
    /// the block does; on a shared 2-core runner the wall clock also holds whatever else was scheduled over the thread (a stall of a
    /// millisecond or two is enough to take a 1.4 ms block over 2.67 ms). Linux and macOS have a per-thread CPU clock; elsewhere
    /// (<see cref="IsThreadTime"/> false) it is the wall clock, as before.
    /// </summary>
    internal static class ThreadCpuClock
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Timespec { public long Seconds; public long Nanoseconds; }

        [DllImport("libc", EntryPoint = "clock_gettime")]
        static extern int ClockGetTime(int clock, out Timespec ts);

        static readonly int ClockId = OperatingSystem.IsMacOS() ? 16 : 3;       // CLOCK_THREAD_CPUTIME_ID
        static readonly long start = Stopwatch.GetTimestamp();
        static bool threadTime = (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && Probe();

        /// <summary>Whether <see cref="NowMs"/> is the thread's CPU time (otherwise the wall clock).</summary>
        public static bool IsThreadTime => threadTime;

        static bool Probe()
        {
            try { return ClockGetTime(ClockId, out _) == 0; }
            catch (Exception) { return false; }
        }

        public static double NowMs()
        {
            if (threadTime)
            {
                try
                {
                    if (ClockGetTime(ClockId, out var ts) == 0) return ts.Seconds * 1000.0 + ts.Nanoseconds / 1e6;
                }
                catch (Exception) { }
                threadTime = false;
            }
            return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
