// Fork (machine keyboard, docs/specs/studio-mvp.md §3.12): the notes a machine plays now, from any
// source, for the keyboard under its window to light up. Two feeds, both where the machine gets its
// notes: MIDI as MachineManager hands it to the machine (a MIDI keyboard, the session's clips, the
// piano roll, the agent's midi_play, the on-screen keyboard), and the Note track parameters its tick
// sends (patterns, notes_write's columns, the on-screen keyboard playing a machine's tracks). Pitches
// are as the machine gets them (MIDI numbers); the keyboard takes the manifest's pitch offset off.
//
// Made by the UI the first time a keyboard shows a machine (MachineCore.NoteMonitor); until then the
// feeds cost a null check. The writers are the MIDI and audio threads: plain array writes and an
// interlocked count, nothing allocated, no lock. The UI reads a version and, when it moved, the state.

using System;
using System.Threading;

namespace ReBuzz.Core.Keyboards
{
    internal sealed class NoteMonitor
    {
        public const int Keys = 128;
        const int Tracks = 256;     // as ParameterCore's pvals

        // MIDI: per key, a bit per channel held, and the velocity of its last note-on.
        readonly int[] midi = new int[Keys];
        readonly byte[] midiVelocity = new byte[Keys];
        // Tracks: the MIDI pitch each track's Note parameter holds, plus one (0: none).
        readonly int[] tracks = new int[Tracks];
        int version;

        /// <summary>Moves on every change: the UI redraws when it differs from what it drew.</summary>
        public int Version => Volatile.Read(ref version);

        void Changed() => Interlocked.Increment(ref version);

        /// <summary>A MIDI message the machine gets (any thread): notes, and controllers 120 and 123 (all off).</summary>
        public void Midi(int data)
        {
            int status = data & 0xFF, kind = status & 0xF0, channel = status & 0x0F;
            int key = (data >> 8) & 0x7F, velocity = (data >> 16) & 0x7F;
            if (kind == 0x90 && velocity > 0) Note(channel, key, velocity);
            else if (kind == 0x80 || kind == 0x90) Note(channel, key, 0);
            else if (kind == 0xB0 && (key == 120 || key == 123)) ClearMidi(channel);
        }

        /// <summary>A note as a machine sends one to another (velocity 0 releases).</summary>
        public void Note(int channel, int key, int velocity)
        {
            if ((uint)key >= Keys) return;
            int bit = 1 << (channel & 15);
            if (velocity > 0)
            {
                midiVelocity[key] = (byte)Math.Min(127, velocity);
                Interlocked.Or(ref midi[key], bit);
            }
            else if ((Volatile.Read(ref midi[key]) & bit) == 0) return;
            else Interlocked.And(ref midi[key], ~bit);
            Changed();
        }

        void ClearMidi(int channel)
        {
            int bit = 1 << (channel & 15);
            bool any = false;
            for (int k = 0; k < Keys; k++)
            {
                if ((Volatile.Read(ref midi[k]) & bit) == 0) continue;
                Interlocked.And(ref midi[k], ~bit);
                any = true;
            }
            if (any) Changed();
        }

        /// <summary>
        /// Audio thread, a tick: the value a track's Note parameter sends (a Buzz note: octave &lt;&lt; 4 | 1…12;
        /// 255 off; 0 none). A note lasts until the track's next note or off, as trackers play them.
        /// </summary>
        public void TrackNote(int track, int value)
        {
            if ((uint)track >= Tracks || value == 0) return;
            int semitone = (value & 15) - 1;
            bool note = value != 255 && (value >> 4) <= 9 && semitone >= 0 && semitone < 12;
            int held = note ? (value >> 4) * 12 + semitone + 1 : 0;      // as TrackNotes.FromBuzz: C-5 is 60
            if (tracks[track] == held) return;
            Volatile.Write(ref tracks[track], held);
            Changed();
        }

        /// <summary>The transport stopped: what the patterns played has ended (the machines were told to stop).</summary>
        public void Stopped()
        {
            bool any = false;
            for (int t = 0; t < Tracks; t++)
            {
                if (Volatile.Read(ref tracks[t]) == 0) continue;
                Volatile.Write(ref tracks[t], 0);
                any = true;
            }
            for (int k = 0; k < Keys; k++)
            {
                if (Volatile.Read(ref midi[k]) == 0) continue;
                Volatile.Write(ref midi[k], 0);
                any = true;
            }
            if (any) Changed();
        }

        /// <summary>
        /// UI: the keys sounding now, by MIDI pitch as the machine gets it: their velocity (1–127), 0 for
        /// silent. A track note's velocity isn't known here: <paramref name="trackVelocity"/>.
        /// </summary>
        public void Read(byte[] into, byte trackVelocity = 100)
        {
            Array.Clear(into);
            for (int k = 0; k < Keys && k < into.Length; k++)
                if (Volatile.Read(ref midi[k]) != 0) into[k] = Math.Max((byte)1, midiVelocity[k]);
            for (int t = 0; t < Tracks; t++)
            {
                int p = Volatile.Read(ref tracks[t]) - 1;
                if (p >= 0 && p < into.Length && into[p] == 0) into[p] = trackVelocity;
            }
        }
    }
}
