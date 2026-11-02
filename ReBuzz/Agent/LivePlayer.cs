// Agent server, live MIDI (docs/specs/mcp-server.md §5.5, A7): phrases the agent plays into a
// machine while the song runs, on a thread of its own, as a keyboard player would. Times follow
// the session's clock as heard (the time a recording take stamps), so a phrase started "on the
// bar" lands on the bar in a take and follows tempo changes. With the transport stopped they are
// timed by the wall clock from the tempo.
//
// An armed track's machine gets the notes through the session's keyboard input, so a take
// recording there records them; any other machine gets them directly.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ReBuzz.Core;

namespace ReBuzz.Agent
{
    internal sealed class LivePlayer : IDisposable
    {
        const int TimeBase = 240;     // session time is 1/240 tick (TakeTime)

        internal sealed class Phrase
        {
            public MachineCore Machine;
            /// <summary>Times in session time (1/240 tick): absolute when <see cref="Clocked"/>, else from the start.</summary>
            public (long At, int Data)[] Events;
            public bool Clocked;
            /// <summary>Unclocked: when it started, and session time per stopwatch tick.</summary>
            public long StartedAt;
            public double TimePerStopwatchTick;
            public int Next;
            public readonly HashSet<(int Channel, int Key)> Held = new();
        }

        readonly ReBuzzCore core;
        readonly object gate = new();
        readonly List<Phrase> phrases = new();
        Thread thread;
        volatile bool stopping;

        public LivePlayer(ReBuzzCore core) => this.core = core;

        /// <summary>Tests: no thread of its own; <see cref="Poll"/> sends what is due.</summary>
        public bool Manual { get; set; }

        public int Playing { get { lock (gate) return phrases.Count; } }

        public void Play(Phrase phrase)
        {
            lock (gate)
            {
                phrases.Add(phrase);
                if (Manual || thread != null) return;
                stopping = false;
                thread = new Thread(Run) { IsBackground = true, Name = "Agent MIDI", Priority = ThreadPriority.AboveNormal };
                thread.Start();
            }
        }

        /// <summary>Releases every note held and drops every phrase.</summary>
        public void StopAll()
        {
            List<Phrase> all;
            lock (gate)
            {
                all = phrases.ToList();
                phrases.Clear();
            }
            foreach (var p in all) Release(p);
        }

        /// <summary>Sends what is due. Returns microseconds until the next event, or −1 when nothing is left.</summary>
        public double Poll()
        {
            List<Phrase> now;
            lock (gate) now = phrases.ToList();
            double soonest = double.MaxValue;
            long heard = -2;
            foreach (var p in now)
            {
                long position;
                if (p.Clocked)
                {
                    if (heard == -2) heard = core.SessionCore.HeardTimeNow();
                    if (heard < 0)
                    {
                        // The transport stopped: let go of what it holds.
                        Release(p);
                        Remove(p);
                        continue;
                    }
                    position = heard;
                }
                else
                    position = (long)((Stopwatch.GetTimestamp() - p.StartedAt) * p.TimePerStopwatchTick);

                while (p.Next < p.Events.Length && p.Events[p.Next].At <= position)
                {
                    Send(p, p.Events[p.Next].Data);
                    p.Next++;
                }
                if (p.Next >= p.Events.Length)
                {
                    Remove(p);
                    continue;
                }
                double ahead = (p.Events[p.Next].At - position) * MicrosPerTime();
                soonest = Math.Min(soonest, ahead);
            }
            lock (gate) return phrases.Count == 0 ? -1 : soonest;
        }

        /// <summary>Microseconds per unit of session time at the current tempo.</summary>
        double MicrosPerTime() => 60e6 / (Math.Max(1, core.BPM) * (double)Math.Max(1, core.TPB) * TimeBase);

        void Remove(Phrase p)
        {
            lock (gate) phrases.Remove(p);
        }

        void Send(Phrase p, int data)
        {
            int kind = data & 0xF0, channel = data & 0x0F, key = (data >> 8) & 0x7F, velocity = (data >> 16) & 0x7F;
            if (kind == 0x90 && velocity > 0) p.Held.Add((channel, key));
            else if (kind == 0x80 || kind == 0x90) p.Held.Remove((channel, key));
            try
            {
                // An armed track plays and records what comes in as from its keyboard.
                if (core.SessionCore.IsArmedMachine(p.Machine) && core.SessionCore.OnMidiInput(data)) return;
                core.MachineManager.SendMidiInput(p.Machine, data, false);
            }
            catch (Exception)
            {
                // A machine removed meanwhile: nothing to play into.
            }
        }

        void Release(Phrase p)
        {
            foreach (var (channel, key) in p.Held.ToList())
                Send(p, 0x80 | channel | (key << 8));
            p.Held.Clear();
        }

        void Run()
        {
            using var wait = new Waiter();
            while (!stopping)
            {
                double ahead;
                try { ahead = Poll(); }
                catch (Exception) { ahead = 1000; }
                if (ahead < 0)
                {
                    lock (gate)
                    {
                        if (phrases.Count == 0) { thread = null; return; }
                    }
                    continue;
                }
                // Sleep most of the way, then look again (the tempo or the clock may have moved).
                if (ahead > 800) wait.Micros(Math.Min(ahead - 600, 5000));
                else Thread.SpinWait(50);
            }
        }

        /// <summary>Waits of a millisecond or less: a high-resolution waitable timer on Windows (as the MIDI clock's), else Sleep.</summary>
        sealed class Waiter : IDisposable
        {
            IntPtr timer;

            public Waiter()
            {
                if (!OperatingSystem.IsWindows()) return;
                try { timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* high resolution */, 0x1F0003); }
                catch (Exception) { timer = IntPtr.Zero; }
            }

            public void Micros(double micros)
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
                if (timer != IntPtr.Zero) CloseHandle(timer);
                timer = IntPtr.Zero;
            }

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, bool resume);

            [System.Runtime.InteropServices.DllImport("kernel32.dll")]
            static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

            [System.Runtime.InteropServices.DllImport("kernel32.dll")]
            static extern bool CloseHandle(IntPtr handle);
        }

        public void Dispose()
        {
            stopping = true;
            StopAll();
        }
    }
}
