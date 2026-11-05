#nullable disable
// Neo Drums (docs/specs/machines.md §5): the hit columns' values, queued where ReBuzz sets them (its tick) and played at
// the next block's start, as notes are (NeoBuzz.Machine NoteInput). A track's row can hit every voice at once, each at
// its own velocity; an Accent on any track's row accents the whole tick. Pure; no allocation after construction.

using System;

namespace NeoBuzz.Drums
{
    internal sealed class PendingHits
    {
        readonly int tracks;
        readonly byte[] velocity;
        readonly object gate = new object();
        bool any, accent;

        public PendingHits(int tracks)
        {
            this.tracks = Math.Max(1, tracks);
            velocity = new byte[this.tracks * DrumsEngine.VoiceCount];
        }

        /// <summary>A hit column's value: <paramref name="voice"/> on <paramref name="track"/> at <paramref name="value"/> (1 … 127).</summary>
        public void Set(int track, int voice, int value)
        {
            if ((uint)track >= (uint)tracks || (uint)voice >= DrumsEngine.VoiceCount || value <= 0) return;
            lock (gate)
            {
                velocity[track * DrumsEngine.VoiceCount + voice] = (byte)Math.Min(127, value);
                any = true;
            }
        }

        /// <summary>The Accent column on a row: the tick's hits are accented.</summary>
        public void Accent()
        {
            lock (gate) accent = true;
        }

        /// <summary>Whether the tick is accented (and clears it).</summary>
        public bool TakeAccent()
        {
            if (!accent) return false;
            lock (gate)
            {
                bool a = accent;
                accent = false;
                return a;
            }
        }

        /// <summary>At a block's start: plays what's queued, track by track, voice by voice.</summary>
        public void Drain(DrumsEngine engine, bool accented)
        {
            if (!any) return;
            lock (gate)
            {
                any = false;
                for (int t = 0; t < tracks; t++)
                    for (int v = 0; v < DrumsEngine.VoiceCount; v++)
                    {
                        int i = t * DrumsEngine.VoiceCount + v;
                        if (velocity[i] == 0) continue;
                        engine.Hit(v, velocity[i] / 127f, accented);
                        velocity[i] = 0;
                    }
            }
        }
    }
}
