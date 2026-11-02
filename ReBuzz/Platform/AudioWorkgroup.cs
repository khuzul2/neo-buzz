#nullable disable
// Fork (docs/specs/mac-engine.md M7): multi-core rendering in the audio device's workgroup on
// macOS. CoreAudio's I/O thread is real time and belongs to the device's os_workgroup; ReBuzz's
// render workers (the audio thread pool, WorkThreadEngine) are ordinary threads unless they join
// it. A joined thread tells the scheduler it works for the same deadline as the I/O thread, so on
// Apple silicon it gets performance cores and enough clock in time.
//
// The driver attaches the device's workgroup when its stream starts and detaches it when it
// stops. Each worker calls Enter before its work and Leave when it ends: Enter checks one
// number, and only when the workgroup changed does it leave the old one and join the new one
// (with a time-constraint policy for the device's buffer period, which joining needs). Every
// joined thread holds its own reference to the workgroup, so detaching never frees one that a
// thread is still in.
//
// Elsewhere (Windows, Linux) it does nothing: Supported is false.

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReBuzz.Platform
{
    public static unsafe class AudioWorkgroup
    {
        sealed class Group
        {
            public IntPtr Handle;
            public uint Period, Computation, Constraint;    // mach ticks
        }

        static readonly object gate = new object();
        static Group current;               // the device's workgroup, or null
        static long version;                // changes on every attach and detach
        static int joined;
        static int failures;
        static string lastError;

        [ThreadStatic] static long seen;
        [ThreadStatic] static Group mine;
        [ThreadStatic] static IntPtr token;  // os_workgroup_join_token_s (40 bytes), one per thread

        /// <summary>Whether workgroups exist here (macOS 11 and later).</summary>
        public static bool Supported => OperatingSystem.IsMacOSVersionAtLeast(11);

        /// <summary>Whether a device's workgroup is attached.</summary>
        public static bool Attached => Volatile.Read(ref current) != null;

        /// <summary>Threads in the workgroup now.</summary>
        public static int JoinedThreads => Volatile.Read(ref joined);

        /// <summary>Joins that failed (the thread then works outside the workgroup).</summary>
        public static int Failures => Volatile.Read(ref failures);

        public static string LastError => lastError;

        /// <summary>How many threads the workgroup suggests working in parallel (0: none attached).</summary>
        public static int MaxParallelThreads
        {
            get
            {
                lock (gate)
                    return current == null ? 0 : Math.Max(0, os_workgroup_max_parallel_threads(current.Handle, IntPtr.Zero));
            }
        }

        /// <summary>
        /// Attaches a CoreAudio device's I/O workgroup; workers join it at their next
        /// <see cref="Enter"/>. False (and <see cref="LastError"/>) when it has none.
        /// </summary>
        /// <param name="bufferSeconds">The device's buffer period: the workers' time constraint.</param>
        public static bool Attach(uint deviceId, double bufferSeconds)
        {
            if (!Supported || deviceId == 0) return false;
            try
            {
                var address = new PropertyAddress { Selector = FourCC("oswg"), Scope = FourCC("glob"), Element = 0 };
                IntPtr handle = IntPtr.Zero;
                uint size = (uint)IntPtr.Size;
                int status = AudioObjectGetPropertyData(deviceId, &address, 0, null, &size, &handle);
                if (status != 0 || handle == IntPtr.Zero)
                {
                    lastError = $"the device has no I/O workgroup (OSStatus {status})";
                    return false;
                }
                mach_timebase_info(out var timebase);
                double ticksPerSecond = 1e9 * timebase.Denom / Math.Max(1u, timebase.Numer);
                uint period = (uint)Math.Clamp(bufferSeconds * ticksPerSecond, 1000, uint.MaxValue / 2);
                var group = new Group { Handle = handle, Period = period, Computation = period / 2, Constraint = period };
                lock (gate)
                {
                    if (current != null) os_release(current.Handle);    // our reference; joined threads hold theirs
                    current = group;
                    version++;
                }
                lastError = null;
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                lastError = e.Message;
                return false;
            }
        }

        /// <summary>Detaches the workgroup: workers leave it at their next <see cref="Enter"/> or <see cref="Leave"/>.</summary>
        public static void Detach()
        {
            lock (gate)
            {
                if (current == null) return;
                os_release(current.Handle);
                current = null;
                version++;
            }
        }

        /// <summary>
        /// On a render worker, before its work: follows the attached workgroup. Costs one read
        /// when nothing changed; allocates nothing after the thread's first join.
        /// </summary>
        public static void Enter()
        {
            if (Volatile.Read(ref version) == seen) return;
            Group group;
            lock (gate)
            {
                seen = version;
                group = current;
                if (group != null) os_retain(group.Handle);     // this thread's reference
            }
            LeaveGroup();
            if (group == null) return;
            try
            {
                SetTimeConstraint(group);
                if (token == IntPtr.Zero)
                {
                    token = Marshal.AllocHGlobal(64);
                    new Span<byte>((void*)token, 64).Clear();
                }
                int result = os_workgroup_join(group.Handle, token);
                if (result == 0)
                {
                    mine = group;
                    Interlocked.Increment(ref joined);
                    return;
                }
                lastError = $"os_workgroup_join: error {result}";
            }
            catch (Exception e)
            {
                lastError = e.Message;
            }
            Interlocked.Increment(ref failures);
            os_release(group.Handle);
        }

        /// <summary>When a render worker ends: leaves its workgroup.</summary>
        public static void Leave()
        {
            LeaveGroup();
            if (token != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(token);
                token = IntPtr.Zero;
            }
            seen = 0;
        }

        static void LeaveGroup()
        {
            var group = mine;
            if (group == null) return;
            mine = null;
            try
            {
                os_workgroup_leave(group.Handle, token);
            }
            finally
            {
                os_release(group.Handle);
                Interlocked.Decrement(ref joined);
            }
        }

        /// <summary>A real-time policy for the device's period: CoreAudio's own I/O thread has one too.</summary>
        static void SetTimeConstraint(Group group)
        {
            var policy = new TimeConstraintPolicy
            {
                Period = group.Period,
                Computation = group.Computation,
                Constraint = group.Constraint,
                Preemptible = 1,
            };
            uint thread = pthread_mach_thread_np(pthread_self());
            int kr = thread_policy_set(thread, 2 /* THREAD_TIME_CONSTRAINT_POLICY */, &policy, 4 /* its size in integers */);
            if (kr != 0) lastError = $"thread_policy_set: {kr}";
        }

        static uint FourCC(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyAddress
        {
            public uint Selector;
            public uint Scope;
            public uint Element;
        }

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

        const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
        const string LibSystem = "/usr/lib/libSystem.dylib";

        [DllImport(CoreAudio)]
        static extern int AudioObjectGetPropertyData(uint objectId, PropertyAddress* address, uint qualifierSize, void* qualifier, uint* dataSize, void* data);

        [DllImport(LibSystem)]
        static extern int os_workgroup_join(IntPtr workgroup, IntPtr token);

        [DllImport(LibSystem)]
        static extern void os_workgroup_leave(IntPtr workgroup, IntPtr token);

        [DllImport(LibSystem)]
        static extern int os_workgroup_max_parallel_threads(IntPtr workgroup, IntPtr attributes);

        [DllImport(LibSystem)]
        static extern IntPtr os_retain(IntPtr obj);

        [DllImport(LibSystem)]
        static extern void os_release(IntPtr obj);

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
