// Fork (docs/specs/neo-audio.md "Takes and comping"): audio recorded into the arrangement. An armed Neo Audio track records what its Audio From
// gives it, from the song tick recording began, as one continuous stream into a take (AudioCapture, to a temp file). The audio thread marks
// each wrap of the song's loop with the frame it was at, so every pass of a loop recording is a take of exact length, whatever the tempo does
// within it. Pure bookkeeping: no allocation on the audio thread, nothing here waits.

using System;
using System.Threading;
using ReBuzz.Core.Clips;

namespace ReBuzz.Core.Session
{
    internal sealed class ArrangementAudioTake
    {
        public const int MaxMarks = 4096;

        readonly long[] marks = new long[MaxMarks];
        int markCount;
        int lastTick = int.MinValue;
        bool started;

        public ArrangementAudioTake(BuzzGUI.Interfaces.IMachine machine, AudioCapture capture, int startTick, int loopStart, int loopEnd, bool loops)
        {
            Machine = machine;
            Capture = capture;
            StartTick = startTick;
            LoopStart = loopStart;
            LoopEnd = loopEnd;
            Loops = loops && loopEnd > loopStart;
        }

        public BuzzGUI.Interfaces.IMachine Machine { get; }
        public AudioCapture Capture { get; }
        /// <summary>The song tick the stream's first frame is at.</summary>
        public int StartTick { get; }
        public int LoopStart { get; }
        public int LoopEnd { get; }
        public bool Loops { get; }
        /// <summary>Whether the stream has begun (its start tick came).</summary>
        public bool Started => started;
        /// <summary>The block (engine frame) the machine last wrote this stream.</summary>
        public long LastBlock = long.MinValue;

        /// <summary>Loop wraps seen so far (each is the end of a pass).</summary>
        public int MarkCount => Volatile.Read(ref markCount);

        /// <summary>The frame the take had when the loop wrapped for the <paramref name="index"/>th time.</summary>
        public long MarkAt(int index) => Volatile.Read(ref marks[index]);

        /// <summary>
        /// Audio thread, at the start of a block of the machine: the song's tick and the frames into it. Returns false while the take waits for its
        /// start tick. The first block after the start tick begins the stream with the frames since that tick as silence.
        /// </summary>
        public bool Begin(int tick, int posInTick, int samplesPerTick)
        {
            if (!started)
            {
                if (tick < StartTick) return false;
                started = true;
                long lead = (long)(tick - StartTick) * Math.Max(1, samplesPerTick) + posInTick;
                if (lead > 0) Capture.WriteSilence((int)Math.Min(lead, int.MaxValue));
                lastTick = tick;
                return true;
            }
            if (Loops && tick < lastTick && markCount < MaxMarks)
            {
                Volatile.Write(ref marks[markCount], Capture.Written);
                Volatile.Write(ref markCount, markCount + 1);
            }
            lastTick = tick;
            return true;
        }
    }
}
