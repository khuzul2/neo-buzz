#nullable disable
// Neo Learn Granular's picture (docs/specs/neo-learn.md §5.4): the grains the engine started a moment ago. The audio thread writes each grain's
// place, length and pan into a small ring (NoteBank.GrainLog*); this reads what is new since the last frame (lock-free: the index is read first,
// the entries after) and keeps each as a particle that fades over a short time. No UI toolkit here: the editor draws from the arrays.

using System;
using System.Threading;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.Learn
{
    internal sealed class GrainCloud
    {
        public const int Max = 24;

        /// <summary>The particles alive: place in the recording (0 … 1), length (a share of the recording), pan (-1 … 1), age (0 new … 1 gone), octave up.</summary>
        public readonly double[] Pos = new double[Max], Length = new double[Max], Pan = new double[Max], Age = new double[Max];
        public readonly bool[] Up = new bool[Max];
        public int Count { get; private set; }

        readonly long[] born = new long[Max];
        readonly double[] life = new double[Max];
        int seen = -1;

        /// <summary>Takes the grains started since the last call (at most the ring's size) and ages the particles to <paramref name="nowMs"/>.</summary>
        /// <param name="sourceSeconds">The recording's length, to turn a grain's seconds into a share of it.</param>
        public void Update(NoteBank bank, double sourceSeconds, long nowMs)
        {
            if (bank != null)
            {
                int at = Volatile.Read(ref bank.GrainLogAt);
                if (seen < 0 || at < seen) seen = at;
                if (at - seen > NoteBank.GrainLogSize) seen = at - NoteBank.GrainLogSize;
                for (; seen < at; seen++)
                {
                    int li = seen & (NoteBank.GrainLogSize - 1);
                    Add(bank.GrainLogPos[li], bank.GrainLogLen[li] / Math.Max(0.05, sourceSeconds), bank.GrainLogPan[li], bank.GrainLogUp[li], bank.GrainLogLen[li], nowMs);
                }
            }
            int n = 0;
            for (int i = 0; i < Count; i++)
            {
                double age = (nowMs - born[i]) / life[i];
                if (age >= 1) continue;
                if (n != i)
                {
                    Pos[n] = Pos[i]; Length[n] = Length[i]; Pan[n] = Pan[i]; Up[n] = Up[i]; born[n] = born[i]; life[n] = life[i];
                }
                Age[n] = age;
                n++;
            }
            Count = n;
        }

        void Add(double pos, double length, double pan, bool up, double seconds, long nowMs)
        {
            int slot = Count < Max ? Count++ : 0;
            if (slot == 0 && Count == Max)
            {
                // Full: the oldest (the first) makes way.
                for (int i = 1; i < Max; i++)
                {
                    Pos[i - 1] = Pos[i]; Length[i - 1] = Length[i]; Pan[i - 1] = Pan[i]; Up[i - 1] = Up[i]; born[i - 1] = born[i]; life[i - 1] = life[i]; Age[i - 1] = Age[i];
                }
                slot = Max - 1;
            }
            Pos[slot] = pos;
            Length[slot] = length;
            Pan[slot] = pan;
            Up[slot] = up;
            born[slot] = nowMs;
            life[slot] = Math.Clamp(seconds * 1500, 300, 1500);
            Age[slot] = 0;
        }
    }
}
