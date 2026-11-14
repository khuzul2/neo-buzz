using System;
using System.Collections.Generic;
using System.Threading;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Fork (docs/specs/daw-modes.md AR5, S6): MIDI recorded into the arrangement. The session's record
    /// windows count in session time against the clip a track plays; an arrangement window counts in song
    /// ticks against a take target (a pattern to be, and the song tick its time zero is). The MIDI thread
    /// stamps each message the way <see cref="SessionCore.OnMidiInput"/> stamps a session take's (heard
    /// time, from the audio thread's rendered position), and the UI thread's <see cref="ArrangementRecorder"/>
    /// drains them.
    /// </summary>
    internal sealed partial class SessionCore
    {
        /// <summary>
        /// A track records into the arrangement while one of these is open (immutable). Song time is the session clock
        /// plus <see cref="Offset"/> until the song loops; each wrap of the loop starts the next pass.
        /// </summary>
        internal sealed class ArrangementWindow
        {
            public ArrangementWindow(SequenceCore track, int origin, int start, int offset, int loopStart, int loopEnd, bool loops)
            {
                Track = track;
                Origin = origin;
                Start = start;
                Offset = offset;
                LoopStart = loopStart;
                LoopEnd = loopEnd;
                Loops = loops && loopEnd > loopStart;
            }

            /// <summary>The track (its main lane's sequence, armed).</summary>
            public SequenceCore Track { get; }
            /// <summary>The song tick the take's time zero is (the loop's start when loop recording, else where recording began).</summary>
            public int Origin { get; }
            /// <summary>The song tick recording began at.</summary>
            public int Start { get; }
            /// <summary>Song position minus the session clock, in ticks, when recording began (0 from a stopped transport: the clock starts at the position).</summary>
            public int Offset { get; }
            public int LoopStart { get; }
            public int LoopEnd { get; }
            /// <summary>Loop recording: every pass of the loop is a take.</summary>
            public bool Loops { get; }

            /// <summary>The pass and the song time (1/240 tick) of a heard session time (1/240 tick); false before recording began.</summary>
            public bool Locate(long clockTime, out int pass, out long songTime)
            {
                long unwrapped = clockTime + (long)Offset * TakeTime.TimeBase;
                pass = 0;
                songTime = unwrapped;
                if (unwrapped < (long)Start * TakeTime.TimeBase) return false;
                if (!Loops || unwrapped < (long)LoopEnd * TakeTime.TimeBase) return true;
                long length = (long)(LoopEnd - LoopStart) * TakeTime.TimeBase;
                long over = unwrapped - (long)LoopEnd * TakeTime.TimeBase;
                pass = 1 + (int)(over / length);
                songTime = (long)LoopStart * TakeTime.TimeBase + over % length;
                return true;
            }
        }

        volatile ArrangementWindow[] arrangementWindows = Array.Empty<ArrangementWindow>();
        int songMinusClock;

        internal ArrangementWindow[] ArrangementWindows => arrangementWindows;

        /// <summary>UI thread: the arrangement windows from now on.</summary>
        internal void SetArrangementWindows(ArrangementWindow[] windows) => arrangementWindows = windows ?? Array.Empty<ArrangementWindow>();

        /// <summary>
        /// The song position minus the session clock, in ticks, as the last tick left it (the audio thread publishes it each tick;
        /// it changes by the loop's length at a wrap). Meaningful while the clock runs.
        /// </summary>
        internal int SongMinusClock => Volatile.Read(ref songMinusClock);

        /// <summary>Audio thread, after a tick's clock moved.</summary>
        void PublishSongOffset(int songPosition)
        {
            long clock = scheduler.Clock;
            if (clock >= 0) Volatile.Write(ref songMinusClock, (int)(songPosition - clock));
        }

        /// <summary>Under the gate (MIDI thread): a stamped message into the ring for every arrangement window it belongs to. True if one was queued.</summary>
        bool StampArrangement(int data, long time, int fraction, MachineCore only)
        {
            var windows = arrangementWindows;
            bool queued = false;
            for (int i = 0; i < windows.Length; i++)
            {
                var w = windows[i];
                if (only != null && !ReferenceEquals(w.Track.MachineCore, only)) continue;
                if (!w.Locate(time, out int pass, out long songTime)) continue;
                long position = songTime - (long)w.Origin * TakeTime.TimeBase;
                if (position < 0) continue;
                long end = w.Loops ? (long)(w.LoopEnd - w.Origin) * TakeTime.TimeBase : int.MaxValue;
                var input = new TakeInput
                {
                    Track = w.Track,
                    Arrangement = w,
                    Pass = pass,
                    Stamp = new TakeStamp(data, time, (int)Math.Min(position, int.MaxValue), (int)Math.Min(end, int.MaxValue)),
                };
                lock (ringLock)
                {
                    if (ringCount == RingSize)
                    {
                        droppedInputs++;
                        continue;
                    }
                    ring[(ringStart + ringCount) % RingSize] = input;
                    ringCount++;
                }
                queued = true;
            }
            return queued;
        }

        /// <summary>
        /// UI thread: moves the queued input into <paramref name="into"/>, oldest first: the arrangement recorder's
        /// (<paramref name="arrangement"/>) or the session's, each leaving the other's in the queue.
        /// </summary>
        internal void TakeInputs(List<TakeInput> into, bool arrangement)
        {
            lock (ringLock)
            {
                int kept = 0;
                for (int i = 0; i < ringCount; i++)
                {
                    int at = (ringStart + i) % RingSize;
                    var input = ring[at];
                    if ((input.Arrangement != null) == arrangement)
                    {
                        into.Add(input);
                        ring[at] = default;
                    }
                    else
                    {
                        if (kept != i)
                        {
                            ring[(ringStart + kept) % RingSize] = input;
                            ring[at] = default;
                        }
                        kept++;
                    }
                }
                ringCount = kept;
                if (kept == 0) ringStart = 0;
            }
        }
    }
}
