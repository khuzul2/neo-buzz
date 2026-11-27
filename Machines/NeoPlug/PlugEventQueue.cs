// The events a hosted plug-in is given between two blocks (docs/specs/notes.md G9): kept in order, a note-on is the only event that
// may be dropped when the queue is full, and it stops being taken Reserve events before the end so a note-off, a controller or an
// all-notes-off always finds room: a flood of notes can never leave a note stuck in the plug-in.
using System;

namespace NeoBuzz.Plug
{
    internal sealed class PlugEventQueue<T> where T : struct
    {
        readonly T[] ring;
        readonly bool[] droppable;
        readonly int reserve;
        readonly object gate = new();
        int count;

        public PlugEventQueue(int size, int reserve) { ring = new T[size]; droppable = new bool[size]; this.reserve = reserve; }

        public int Count { get { lock (gate) return count; } }
        /// <summary>Note-ons that did not fit (a counter, for tests and diagnostics).</summary>
        public long Dropped;

        /// <summary>Any thread. A note-on (<paramref name="isNoteOn"/>) is refused once only the reserve is left.</summary>
        public void Push(in T e, bool isNoteOn)
        {
            lock (gate)
            {
                if (count >= ring.Length - (isNoteOn ? reserve : 0)) { if (isNoteOn) Dropped++; return; }
                droppable[count] = isNoteOn; ring[count++] = e;
            }
        }

        public void Clear() { lock (gate) count = 0; }

        /// <summary>Audio thread: appends the queued events to <paramref name="dst"/> from <paramref name="n"/> up to <paramref name="limit"/> and empties the queue. Note-ons stop <c>reserve</c> short of the limit.</summary>
        public void DrainTo(T[] dst, ref int n, int limit)
        {
            lock (gate)
            {
                for (int i = 0; i < count; i++)
                {
                    if (n >= limit - (droppable[i] ? reserve : 0)) { if (droppable[i]) Dropped++; continue; }
                    dst[n++] = ring[i];
                }
                count = 0;
            }
        }
    }
}
