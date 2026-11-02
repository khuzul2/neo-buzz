#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 0.8): MIDI clock out, on a thread of its
// own: each pulse goes when the session's beat line (as heard) says, not when the audio thread
// happens to run.

using System;
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
        readonly ReBuzz.Platform.PreciseWaiter waiter = new ReBuzz.Platform.PreciseWaiter();   // fork (mac-engine.md M6): each OS's own timer

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
                waiter.Dispose();
            }
        }

        void Wait(double micros) => waiter.Micros(micros);

        public void Dispose()
        {
            stopping = true;
            thread.Join(500);
        }
    }
}
