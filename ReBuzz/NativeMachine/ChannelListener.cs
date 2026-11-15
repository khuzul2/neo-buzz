using BuzzGUI.Common;
using ReBuzz.Core;
using System;
using System.Threading;

namespace ReBuzz.NativeMachine
{
    internal class ChannelListener
    {
        internal readonly ReBuzzCore buzz;

        public ChannelType Channel { get; }
        private readonly NativeMessage msg;
        Thread threadPing;

        public NativeEvent WaitHandlePing { get; }
        public NativeEvent WaitHandlePong { get; }

        /// <summary>Fork (cross-platform.md R1): the name the portable host knows a channel by (its FIFO files in the session folder).</summary>
        internal static string ChannelFileName(ChannelType channel) => channel switch
        {
            ChannelType.HostChannel => "Host",
            ChannelType.UIChannel => "UI",
            ChannelType.MidiChannel => "MIDI",
            _ => "Audio",
        };

        public ChannelListener(ChannelType channel, ThreadPriority priority, string eventId, NativeMessage msg, ReBuzzCore buzz, string sessionFolder = null)
        {
            this.buzz = buzz;
            Channel = channel;
            this.msg = msg;

            // Message from A is ready when Ping event signaled. Message handled when B send Pong nad channel is available
            string fifo = sessionFolder == null ? null : System.IO.Path.Combine(sessionFolder, ChannelFileName(channel));
            WaitHandlePing = new NativeEvent(@"Global\Ping" + eventId, fifo + ".ping");
            WaitHandlePong = new NativeEvent(@"Global\Pong" + eventId, fifo + ".pong");

            Stopped = true;
            this.priority = priority;
        }

        public void Start()
        {
            Stopped = false;
            threadPing = new Thread(this.ThreadTaskPing);
            threadPing.Priority = priority;
            threadPing.Start();
        }

        private void ThreadTaskPing()
        {
            while (!stop)
            {
                WaitHandlePing.WaitOne();
                WaitHandlePing.Reset();
                if (stop)
                    break;

                msg.ReceaveMessage();
                msg.Notify();
            }
            Stopped = true;
        }

        public void Stop()
        {
            stop = true;

            WaitHandlePing.Set();
            WaitHandlePong.Set();
        }

        public void StopAndJoin()
        {
            try
            {
                Stop();

                if (threadPing != null && threadPing.IsAlive)
                {
                    threadPing.Join();
                }

                WaitHandlePing.Dispose();
                WaitHandlePong.Dispose();
            }
            catch (Exception e)
            {
                buzz.DCWriteLine(e.ToString());
            }
        }

        // No time limit for waiting. Needed for UI dialogs etc.
        internal void WaitHandlePongWaitOne()
        {
            if (!stop)
            {
                WaitHandlePong.WaitOne();
            }
        }

        internal bool WaitHandlePongWaitOne(MachineCore machine, int waitTime)
        {
            if (!WaitHandlePong.WaitOne(waitTime)) // Wait reply. If nothing happens in 2 seconds, consider crashed
            {
                if (machine != null)
                {
                    machine.MachineDLL.IsCrashed = true;
                    machine.Ready = false;
                    // Fork: named, and told to the user (it said "<machine> crashed." in the debug console only).
                    buzz.ReportMachineCrashed(machine, $"its host process didn't answer within {waitTime} ms");
                }
                return false;
            }
            return true;
        }

        private bool stop = false;
        public bool Stopped { get; set; }

        private readonly ThreadPriority priority;
    }
}
