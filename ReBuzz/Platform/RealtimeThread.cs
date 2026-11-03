#nullable disable
// Fork (docs/specs/mac-engine.md M6/M7): a real-time (time-constraint) scheduling policy for the
// calling thread on macOS. CoreAudio's I/O thread has one; threads that work for it (render
// workers, M7) or that must wake on time (MIDI clock out, the agent's midi_play, M6) need one
// too. Without it macOS coalesces an ordinary thread's timers: on CI a 0.5 ms mach_wait_until took
// 2.5 to 3.7 ms. Elsewhere it does nothing and returns false.

using System;
using System.Runtime.InteropServices;

namespace ReBuzz.Platform
{
    public static unsafe class RealtimeThread
    {
        static double ticksPerSecond;

        /// <summary>
        /// Gives the calling thread a time-constraint policy (macOS). Times in seconds: the period
        /// (0: it wakes when it likes), the CPU time it needs each time, and how soon after waking
        /// it must have had it. False off macOS, or when the kernel refuses (<see cref="LastError"/>).
        /// </summary>
        public static bool SetTimeConstraint(double period, double computation, double constraint)
        {
            if (!OperatingSystem.IsMacOS()) return false;
            try
            {
                if (ticksPerSecond == 0)
                {
                    mach_timebase_info(out var timebase);
                    ticksPerSecond = 1e9 * timebase.Denom / Math.Max(1u, timebase.Numer);
                }
                var policy = new TimeConstraintPolicy
                {
                    Period = Ticks(period),
                    Computation = Math.Max(1u, Ticks(computation)),
                    Constraint = Math.Max(1u, Ticks(constraint)),
                    Preemptible = 1,
                };
                uint thread = pthread_mach_thread_np(pthread_self());
                int kr = thread_policy_set(thread, 2 /* THREAD_TIME_CONSTRAINT_POLICY */, &policy, 4 /* its size in integers */);
                if (kr == 0) return true;
                LastError = $"thread_policy_set: {kr}";
                return false;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                LastError = e.Message;
                return false;
            }
        }

        public static string LastError { get; private set; }

        static uint Ticks(double seconds) => (uint)Math.Clamp(seconds * ticksPerSecond, 0, uint.MaxValue / 2);

        [StructLayout(LayoutKind.Sequential)]
        struct Timebase
        {
            public uint Numer;
            public uint Denom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TimeConstraintPolicy
        {
            public uint Period;
            public uint Computation;
            public uint Constraint;
            public int Preemptible;
        }

        const string LibSystem = "/usr/lib/libSystem.dylib";

        [DllImport(LibSystem)]
        static extern int mach_timebase_info(out Timebase info);

        [DllImport(LibSystem)]
        static extern IntPtr pthread_self();

        [DllImport(LibSystem)]
        static extern uint pthread_mach_thread_np(IntPtr thread);

        [DllImport(LibSystem)]
        static extern int thread_policy_set(uint thread, uint flavor, TimeConstraintPolicy* policy, uint count);
    }
}
