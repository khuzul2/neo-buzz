using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReBuzz.NativeMachine
{
    /// <summary>
    /// Fork (docs/specs/cross-platform.md R1): one of a native host channel's two auto-reset events, "Ping" and "Pong".
    /// On Windows it is the named EventWaitHandle ("Global\Ping" + id) the host process opens by name, as always.
    /// Elsewhere there are no named events: it is a FIFO in the session folder (NativeSession), one byte per Set and
    /// one per wait, which the portable host (ReBuzzEngine/portable) opens by path. The channels alternate strictly
    /// (one message and one reply at a time), so unlike an event the FIFO never holds more than one byte, and Reset
    /// has nothing to do.
    /// </summary>
    internal sealed class NativeEvent : IDisposable
    {
        readonly EventWaitHandle windows;
        readonly SafeFileHandle fifo;
        readonly int fd;
        volatile bool disposed;

        public NativeEvent(string windowsName, string fifoPath)
        {
            if (OperatingSystem.IsWindows())
            {
                windows = new EventWaitHandle(false, EventResetMode.AutoReset, windowsName);
            }
            else
            {
                // Read and write on one handle: opened O_RDWR, an open of a FIFO never waits for the other end.
                fifo = File.OpenHandle(fifoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                fd = (int)fifo.DangerousGetHandle();
            }
        }

        public void Set()
        {
            if (windows != null) { windows.Set(); return; }
            if (disposed) return;
            byte b = 1;
            unsafe
            {
                while (Posix.write(fd, &b, 1) < 0 && Marshal.GetLastPInvokeError() == Posix.EINTR) { }
            }
        }

        public void Reset()
        {
            windows?.Reset();
        }

        public bool WaitOne()
        {
            if (windows != null) return windows.WaitOne();
            return Read(-1);
        }

        public bool WaitOne(int milliseconds)
        {
            if (windows != null) return windows.WaitOne(milliseconds);
            return Read(milliseconds);
        }

        bool Read(int milliseconds)
        {
            unsafe
            {
                if (disposed) return false;
                if (milliseconds >= 0)
                {
                    var p = new Posix.PollFd { fd = fd, events = Posix.POLLIN };
                    long end = Environment.TickCount64 + milliseconds;
                    for (;;)
                    {
                        int left = (int)Math.Max(0, end - Environment.TickCount64);
                        int r = Posix.poll(&p, 1, left);
                        if (r > 0) break;
                        if (r == 0) return false;
                        if (Marshal.GetLastPInvokeError() != Posix.EINTR) return false;
                    }
                }
                byte b;
                for (;;)
                {
                    long n = Posix.read(fd, &b, 1);
                    if (n == 1) return true;
                    if (n < 0 && Marshal.GetLastPInvokeError() == Posix.EINTR) continue;
                    return false;
                }
            }
        }

        public void Dispose()
        {
            disposed = true;
            windows?.Dispose();
            fifo?.Dispose();
        }
    }

    /// <summary>The few C library calls the FIFOs and the session folder need (macOS and Linux).</summary>
    internal static unsafe class Posix
    {
        public const int EINTR = 4;
        public const short POLLIN = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct PollFd
        {
            public int fd;
            public short events;
            public short revents;
        }

        [DllImport("libc", SetLastError = true)]
        public static extern long read(int fd, byte* buf, nuint count);

        [DllImport("libc", SetLastError = true)]
        public static extern long write(int fd, byte* buf, nuint count);

        [DllImport("libc", SetLastError = true)]
        public static extern int poll(PollFd* fds, ulong nfds, int timeout);

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern int mkfifo(string path, uint mode);
    }
}
