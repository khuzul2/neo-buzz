#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 0.8): MIDI clock out, on a thread of its
// own: each pulse goes when the session's beat line (as heard) says, not when the audio thread
// happens to run.

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReBuzz.Core.Sync
{
    internal sealed class MidiClockOutput : IDisposable
    {
        readonly Func<bool> playing;
        readonly TimelineCell ours;
        readonly Action<int> send;
        readonly MidiClockSender sender = new MidiClockSender();
        readonly Thread thread;
        volatile bool stopping;
        IntPtr timer;

        /// <param name="playing">Whether the transport plays (any thread).</param>
        /// <param name="ours">The session's beat line as heard (<see cref="SyncClock.Ours"/>).</param>
        /// <param name="send">Sends a short MIDI message to the clock's outputs.</param>
        public MidiClockOutput(Func<bool> playing, TimelineCell ours, Action<int> send)
        {
            this.playing = playing;
            this.ours = ours;
            this.send = send;
            thread = new Thread(Run) { IsBackground = true, Name = "MIDI clock out", Priority = ThreadPriority.Highest };
            thread.Start();
        }

        void Run()
        {
            try
            {
                timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            }
            catch (Exception)
            {
                timer = IntPtr.Zero;
            }
            try
            {
                while (!stopping)
                {
                    Timeline line = default;
                    bool on = playing() && ours.TryRead(out line);
                    if (!on) line = default;
                    double now = HostClock.Now();
                    int message = sender.Peek(on, line, now, out double at);
                    if (message == 0)
                    {
                        Wait(MidiClockSender.IdleMicros);
                        continue;
                    }
                    double ahead = at - now;
                    if (ahead > 800)
                    {
                        // Sleep most of the way, then look again (the line may have moved).
                        Wait(Math.Min(ahead - 600, 5000));
                        continue;
                    }
                    while (HostClock.Now() < at && !stopping) Thread.SpinWait(20);
                    try { send(message); } catch (Exception) { }
                    sender.Sent(message, line, HostClock.Now());
                }
                // Stopping while the clock runs: tell the receivers.
                if (sender.IsRunning)
                    try { send(MidiClockSender.Stop); } catch (Exception) { }
            }
            finally
            {
                if (timer != IntPtr.Zero) CloseHandle(timer);
            }
        }

        void Wait(double micros)
        {
            if (timer != IntPtr.Zero)
            {
                long due = -(long)(micros * 10);            // relative, in 100 ns
                if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    WaitForSingleObject(timer, 100);
                    return;
                }
            }
            Thread.Sleep(Math.Max(1, (int)(micros / 1000)));
        }

        public void Dispose()
        {
            stopping = true;
            thread.Join(500);
        }

        const uint CreateWaitableTimerHighResolution = 0x2;
        const uint TimerAllAccess = 0x1F0003;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, bool resume);

        [DllImport("kernel32.dll")]
        static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
    }
}
