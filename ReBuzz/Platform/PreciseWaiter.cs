#nullable disable
// Fork (docs/specs/mac-engine.md M6): short, precise waits for the threads that time MIDI (the
// clock output, the agent's midi_play), on each OS's own timer:
//  - Windows: a high-resolution waitable timer (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION);
//  - macOS: mach_wait_until on mach_absolute_time, as Apple recommends for timing threads, from a
//    thread with a real-time policy (RealtimeThread), without which macOS coalesces its timers;
//  - Linux: clock_nanosleep on CLOCK_MONOTONIC.
// Each falls back to Thread.Sleep (whole milliseconds) when its timer isn't there. One waiter per
// thread: a Windows timer handle isn't shared.

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReBuzz.Platform
{
    public sealed class PreciseWaiter : IDisposable
    {
        IntPtr timer;                  // Windows
        double machTicksPerMicro;      // macOS
        bool linux;
        int realtimeThread;            // macOS: the managed thread given a real-time policy

        public PreciseWaiter()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* high resolution */, 0x1F0003 /* all access */);
                else if (OperatingSystem.IsMacOS() && mach_timebase_info(out var tb) == 0 && tb.Numer > 0)
                    machTicksPerMicro = 1000.0 * tb.Denom / tb.Numer;     // ticks = ns × denom / numer
                else if (OperatingSystem.IsLinux())
                    linux = true;
            }
            catch (Exception)
            {
                timer = IntPtr.Zero;
                machTicksPerMicro = 0;
                linux = false;
            }
        }

        /// <summary>Which timer it uses: "waitable timer", "mach_wait_until", "clock_nanosleep" or "sleep".</summary>
        public string Kind => timer != IntPtr.Zero ? "waitable timer" : machTicksPerMicro > 0 ? "mach_wait_until" : linux ? "clock_nanosleep" : "sleep";

        /// <summary>Waits about <paramref name="micros"/> microseconds (at least one, at most a second).</summary>
        public void Micros(double micros)
        {
            micros = Math.Clamp(micros, 1, 1_000_000);
            try
            {
                if (timer != IntPtr.Zero)
                {
                    long due = -(long)(micros * 10);            // relative, in 100 ns
                    if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        WaitForSingleObject(timer, 1100);
                        return;
                    }
                }
                else if (machTicksPerMicro > 0)
                {
                    // An ordinary thread's timers are coalesced (a 0.5 ms wait took 2.5–3.7 ms on
                    // CI): the waiting thread gets a real-time policy, once. It wakes when it
                    // likes and needs a little CPU each time, soon.
                    int id = Environment.CurrentManagedThreadId;
                    if (realtimeThread != id)
                    {
                        realtimeThread = id;
                        RealtimeThread.SetTimeConstraint(0, 0.0002, 0.001);
                    }
                    mach_wait_until(mach_absolute_time() + (ulong)(micros * machTicksPerMicro));
                    return;
                }
                else if (linux)
                {
                    long ns = (long)(micros * 1000);
                    var t = new Timespec { Seconds = ns / 1_000_000_000, Nanoseconds = ns % 1_000_000_000 };
                    clock_nanosleep(1 /* CLOCK_MONOTONIC */, 0, ref t, IntPtr.Zero);
                    return;
                }
            }
            catch (Exception)
            {
                // A timer that failed once: sleep from now on.
                timer = IntPtr.Zero;
                machTicksPerMicro = 0;
                linux = false;
            }
            Thread.Sleep(Math.Max(1, (int)(micros / 1000)));
        }

        public void Dispose()
        {
            if (timer != IntPtr.Zero && OperatingSystem.IsWindows()) CloseHandle(timer);
            timer = IntPtr.Zero;
        }

        // ---- Windows

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, bool resume);

        [DllImport("kernel32.dll")]
        static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        // ---- macOS

        [StructLayout(LayoutKind.Sequential)]
        struct MachTimebase
        {
            public uint Numer;
            public uint Denom;
        }

        [DllImport("/usr/lib/libSystem.dylib")]
        static extern int mach_timebase_info(out MachTimebase info);

        [DllImport("/usr/lib/libSystem.dylib")]
        static extern ulong mach_absolute_time();

        [DllImport("/usr/lib/libSystem.dylib")]
        static extern int mach_wait_until(ulong deadline);

        // ---- Linux

        [StructLayout(LayoutKind.Sequential)]
        struct Timespec
        {
            public long Seconds;
            public long Nanoseconds;
        }

        [DllImport("libc", SetLastError = true)]
        static extern int clock_nanosleep(int clock, int flags, ref Timespec request, IntPtr remain);
    }
}
