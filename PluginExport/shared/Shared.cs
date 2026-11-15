// The only thing the DSP library and the editor library share: one blittable struct in native memory (allocated by the DSP
// library, passed to the editor library as a pointer). No managed object crosses the boundary, so neither runtime's GC
// ever sees the other's data. Single writer per field; the edit ring is single producer (editor), single consumer (the audio
// thread in process, or the main thread in params.flush while the host is not processing).
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace NeoExport
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Shared
    {
        public const int MaxParams = 512, RingSize = 256;
        public const int Magic = 0x4E454F59;
        public const int EditValue = 0, EditBegin = 1, EditEnd = 2;

        public int Magic_, Size, ParamCount;
        // DSP -> editor
        public fixed int Param[MaxParams];       // current raw values
        public int ParamSeq;                     // bumped when one changes
        public float InPeak, OutPeak;            // meters: peak of the last block (max over both channels)
        public int MeterSeq;
        public int Closing;                      // the DSP library asks the editor to stop
        public long EditsApplied;                // edits the DSP side has taken from the ring
        public double Rate, Tempo;               // sample rate, host tempo (0: unknown)
        // editor -> DSP: edits (kind, index, raw) and a way to make the host call params.flush
        public int RingHead, RingTail;
        public fixed int Ring[RingSize * 3];
        public void* FlushFn, FlushCtx;          // [UnmanagedCallersOnly] void (void* ctx), set by the DSP library
        // editor runtime's own figures (written by the editor library; the tests read them)
        public int EdGc0, EdGc1, EdGc2, EdReady, EdAlive;
        public long EdPauseNs, EdMaxPauseNs, EdAllocBytes, EdEditsPosted, EdFrames, EdPixels;

        /// <summary>Editor side: post an edit. False when the ring is full (the edit is dropped).</summary>
        public static bool Post(Shared* s, int kind, int index, int raw)
        {
            int head = Volatile.Read(ref s->RingHead), tail = Volatile.Read(ref s->RingTail);
            if (head - tail >= RingSize) return false;
            int slot = (head & (RingSize - 1)) * 3;
            s->Ring[slot] = kind; s->Ring[slot + 1] = index; s->Ring[slot + 2] = raw;
            Volatile.Write(ref s->RingHead, head + 1);
            return true;
        }

        /// <summary>Take the next edit, if any. No allocation, no lock.</summary>
        public static bool Take(Shared* s, out int kind, out int index, out int raw)
        {
            int tail = Volatile.Read(ref s->RingTail);
            if (tail == Volatile.Read(ref s->RingHead)) { kind = index = raw = 0; return false; }
            int slot = (tail & (RingSize - 1)) * 3;
            kind = s->Ring[slot]; index = s->Ring[slot + 1]; raw = s->Ring[slot + 2];
            Volatile.Write(ref s->RingTail, tail + 1);
            return true;
        }
    }
}
