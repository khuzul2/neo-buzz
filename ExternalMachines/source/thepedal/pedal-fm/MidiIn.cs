// Fork (neo-buzz, docs/specs/mcp-server.md §12.15): MIDI notes and controllers for a Pedal synth, as the Neo synths
// take them (Machines/NeoBuzz.Machine/NoteInput.cs). ReBuzz calls the machine's MidiNote and MidiControlChange on
// the MIDI thread (a keyboard) or the audio thread (a pattern's MIDI column, notes_write); they are queued here and
// the synth applies them at the start of its next block, after its track notes. The audio thread never allocates
// or waits here. MidiVoices keeps which voice plays which MIDI key, for the polyphonic synths. The same file is in
// each Pedal synth's folder (Juno106, SH101, FM, M1, Tracker), in its own namespace.

using System.Threading;

namespace PedalFM
{
    internal sealed class MidiIn
    {
        public const int NoteOn = 1, NoteOff = 2, Control = 3;

        // Controllers as ReBuzz passes them (MachineManager.SendMidiInput): CC numbers; pitch bend as 0xE0 (0xFF with
        // Polac's conversion) with its 14-bit value; channel pressure as 0xD0 (0xFC).
        public const int ModWheel = 1, SustainPedal = 64, AllSoundOff = 120, AllNotesOff = 123;
        public const int PitchBend = 0xE0, PitchBendPolac = 0xFF, Pressure = 0xD0, PressurePolac = 0xFC;

        /// <summary>Pitch bend's range either way, in semitones (the MIDI default; the machines have no parameter for it).</summary>
        public const float BendSemitones = 2f;

        const int Size = 1024;   // a power of two
        readonly int[] kinds = new int[Size], firsts = new int[Size], seconds = new int[Size];
        int write, read;
        readonly object gate = new object();

        /// <summary>A note on (velocity 1–127) or off (velocity 0).</summary>
        public void Note(int note, int velocity)
        {
            if ((uint)note > 127) return;
            if (velocity > 127) velocity = 127;
            Push(velocity > 0 ? NoteOn : NoteOff, note, velocity);
        }

        public void Controller(int ctrl, int value) => Push(Control, ctrl, value);

        void Push(int kind, int a, int b)
        {
            lock (gate)
            {
                int next = (write + 1) & (Size - 1);
                if (next == Volatile.Read(ref read)) return;   // full: dropped
                kinds[write] = kind;
                firsts[write] = a;
                seconds[write] = b;
                Volatile.Write(ref write, next);
            }
        }

        /// <summary>The audio thread, at the start of a block: the next queued event, oldest first.</summary>
        public bool Next(out int kind, out int a, out int b)
        {
            int r = read;
            if (r == Volatile.Read(ref write))
            {
                kind = a = b = 0;
                return false;
            }
            kind = kinds[r];
            a = firsts[r];
            b = seconds[r];
            Volatile.Write(ref read, (r + 1) & (Size - 1));
            return true;
        }

        // The controllers' state, as applied (audio thread).
        /// <summary>Pitch bend, −1 … +1 (0 at rest).</summary>
        public float Bend;
        /// <summary>Mod wheel, 0 … 1.</summary>
        public float Mod;
        /// <summary>Sustain pedal down: MIDI note offs wait for it to come up.</summary>
        public bool Pedal;

        /// <summary>Applies a controller to the state; true when it is the sustain pedal coming up.</summary>
        public bool Apply(int ctrl, int value)
        {
            switch (ctrl)
            {
                case ModWheel:
                    Mod = (value < 0 ? 0 : value > 127 ? 127 : value) / 127f;
                    return false;
                case SustainPedal:
                    bool was = Pedal;
                    Pedal = value >= 64;
                    return was && !Pedal;
                case PitchBend:
                case PitchBendPolac:
                    float bend = (value - 8192) / 8192f;
                    Bend = bend < -1f ? -1f : bend > 1f ? 1f : bend;
                    return false;
            }
            return false;
        }
    }

    /// <summary>Which voice plays which MIDI key (the polyphonic synths), and how long ago each voice started.</summary>
    internal sealed class MidiVoices
    {
        /// <summary>The MIDI key each voice plays, or −1 (free, or playing a track's note).</summary>
        public readonly int[] Key;
        /// <summary>Its key is down.</summary>
        public readonly bool[] Held;
        /// <summary>Its key is up, but the sustain pedal holds it.</summary>
        public readonly bool[] Sustained;
        readonly uint[] started;
        uint clock;

        public MidiVoices(int voices)
        {
            Key = new int[voices];
            Held = new bool[voices];
            Sustained = new bool[voices];
            started = new uint[voices];
            for (int v = 0; v < voices; v++) Key[v] = -1;
        }

        public int Count => Key.Length;

        /// <summary>The voice started a note for <paramref name="key"/>.</summary>
        public void Start(int v, int key)
        {
            Key[v] = key;
            Held[v] = true;
            Sustained[v] = false;
            started[v] = ++clock;
        }

        /// <summary>A track's note took the voice: it is no longer any MIDI key's.</summary>
        public void Track(int v)
        {
            Clear(v);
            started[v] = ++clock;
        }

        public void Clear(int v)
        {
            Key[v] = -1;
            Held[v] = false;
            Sustained[v] = false;
        }

        /// <summary>The voice playing <paramref name="key"/> for MIDI (held or sustained) among the first <paramref name="count"/>, or −1.</summary>
        public int Playing(int key, int count)
        {
            for (int v = 0; v < count; v++)
                if (Key[v] == key) return v;
            return -1;
        }

        /// <summary>
        /// A voice for a new note among the first <paramref name="count"/>: a free one, the highest first (pattern tracks
        /// take the first voices); else the one released longest ago; else the one that started longest ago.
        /// </summary>
        public int Pick(int count, bool[] free, bool[] released)
        {
            for (int v = count - 1; v >= 0; v--)
                if (free[v]) return v;
            int best = -1;
            for (int v = 0; v < count; v++)
                if (released[v] && (best < 0 || Older(v, best))) best = v;
            if (best >= 0) return best;
            best = 0;
            for (int v = 1; v < count; v++)
                if (Older(v, best)) best = v;
            return best;
        }

        bool Older(int a, int b) => unchecked((int)(started[a] - started[b])) < 0;
    }
}
