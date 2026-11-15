// The only thing the DSP library and the editor library share: one blittable struct in native memory (allocated by the DSP
// library, passed to the editor library as a pointer). No managed object crosses the boundary, so neither runtime's GC
// ever sees the other's data. Single writer per field; the ring is single producer (editor), single consumer (audio thread).
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace NeoExport
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Shared
    {
        public const int MaxParams = 32, RingSize = 64;
        public const int Magic = 0x4E454F58;

        public int Magic_, Size;
        // DSP -> editor
        public fixed int Param[MaxParams];       // current raw values
        public int ParamSeq;                     // bumped when one changes
        public float InPeak, OutPeak;            // meters: peak of the last block (max over both channels)
        public int MeterSeq;
        public int Closing;                      // the DSP library asks the editor to stop
        public long EditsApplied;                // edits the audio thread has taken from the ring
        // editor -> DSP: parameter edits (index, raw)
        public int RingHead, RingTail;
        public fixed int Ring[RingSize * 2];
        // editor runtime's own GC figures (written by the editor library)
        public int EdGc0, EdGc1, EdGc2, EdReady, EdAlive;
        public long EdPauseNs, EdMaxPauseNs, EdAllocBytes, EdEditsPosted, EdFrames;

        /// <summary>Editor side: post an edit. False when the ring is full (the edit is dropped).</summary>
        public static bool Post(Shared* s, int index, int raw)
        {
            int head = Volatile.Read(ref s->RingHead), tail = Volatile.Read(ref s->RingTail);
            if (head - tail >= RingSize) return false;
            int slot = (head & (RingSize - 1)) * 2;
            s->Ring[slot] = index; s->Ring[slot + 1] = raw;
            Volatile.Write(ref s->RingHead, head + 1);
            return true;
        }

        /// <summary>Audio side: take the next edit, if any. No allocation, no lock.</summary>
        public static bool Take(Shared* s, out int index, out int raw)
        {
            int tail = Volatile.Read(ref s->RingTail);
            if (tail == Volatile.Read(ref s->RingHead)) { index = 0; raw = 0; return false; }
            int slot = (tail & (RingSize - 1)) * 2;
            index = s->Ring[slot]; raw = s->Ring[slot + 1];
            Volatile.Write(ref s->RingTail, tail + 1);
            return true;
        }
    }
}
