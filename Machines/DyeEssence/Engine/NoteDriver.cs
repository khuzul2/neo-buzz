#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.9): the arpeggiator and the step sequencer. Both run on the
// song's grid: steps are at multiples of a step length in beats, so a render, a null test and a live
// run at the same song position play the same notes whatever the block size. Events are placed on the
// sample they fall on (the engine cuts its blocks there); a step's random draws (the random order, the
// probabilities) are a hash of the seed and the step's number, so nothing depends on how many draws came
// before. Nothing here allocates: the held notes, the step table and the sounding keys are fixed arrays.

using System;
using System.Globalization;
using System.Text;

namespace NeoBuzz.DyeEssence
{
    internal enum ArpMode { Off, Up, Down, UpDown, Played, Random, Chord }

    internal enum SeqMode { Off, Notes, Mod }

    /// <summary>Where the song is (valid in Work): its tick, the samples into it, the tick's length, the tempo's ticks a beat.</summary>
    internal struct DriverTransport
    {
        public bool Playing;
        public int Tick, PosInTick, SamplesPerTick, TicksPerBeat;

        public readonly double Beats(int offset)
        {
            if (SamplesPerTick <= 0 || TicksPerBeat <= 0) return 0;
            return (Tick + (PosInTick + (double)offset) / SamplesPerTick) / TicksPerBeat;
        }
    }

    /// <summary>What the driver does to the voices (the engine's allocator) and to the sequencer's modulation sources.</summary>
    internal interface IDriverOut
    {
        void Play(int key, int note, float velocity);
        void Stop(int key);
        /// <summary>The sequencer's current step: note offset / 24 (−1 … 1), velocity 0 … 1.</summary>
        void SeqValues(float step, float velocity);
    }

    /// <summary>
    /// The sequencer's steps (immutable; replaced whole): 1 … 32 used, each a note offset (−48 … 48 semitones), a velocity
    /// (1 … 127; 0 is a rest), a gate (1 … 100 % of the step), a tie (the note holds into the next step, which then plays
    /// legato) and a probability (0 … 100 %). Text: steps separated by spaces, "offset,velocity,gate,tie,probability", every
    /// field after the first optional ("0,100,80,0,100" is the default), tie 0 or 1 (or t).
    /// </summary>
    internal sealed class SeqPattern
    {
        public const int MaxSteps = 32;
        public const int DefaultVelocity = 100, DefaultGate = 80;

        public readonly sbyte[] Offset = new sbyte[MaxSteps];
        public readonly byte[] Velocity = new byte[MaxSteps], Gate = new byte[MaxSteps], Prob = new byte[MaxSteps];
        public readonly bool[] Tie = new bool[MaxSteps];

        public static readonly SeqPattern Default = new SeqPattern();

        public SeqPattern()
        {
            for (int i = 0; i < MaxSteps; i++) Reset(i);
        }

        void Reset(int i)
        {
            Offset[i] = 0;
            Velocity[i] = DefaultVelocity;
            Gate[i] = DefaultGate;
            Prob[i] = 100;
            Tie[i] = false;
        }

        bool IsDefault(int i) => Offset[i] == 0 && Velocity[i] == DefaultVelocity && Gate[i] == DefaultGate && Prob[i] == 100 && !Tie[i];

        public SeqPattern With(int i, int offset, int velocity, int gate, bool tie, int prob)
        {
            var p = Clone();
            p.Offset[i] = (sbyte)Math.Clamp(offset, -48, 48);
            p.Velocity[i] = (byte)Math.Clamp(velocity, 0, 127);
            p.Gate[i] = (byte)Math.Clamp(gate, 1, 100);
            p.Tie[i] = tie;
            p.Prob[i] = (byte)Math.Clamp(prob, 0, 100);
            return p;
        }

        SeqPattern Clone()
        {
            var p = new SeqPattern();
            Array.Copy(Offset, p.Offset, MaxSteps);
            Array.Copy(Velocity, p.Velocity, MaxSteps);
            Array.Copy(Gate, p.Gate, MaxSteps);
            Array.Copy(Prob, p.Prob, MaxSteps);
            Array.Copy(Tie, p.Tie, MaxSteps);
            return p;
        }

        public bool SameAs(SeqPattern o)
        {
            for (int i = 0; i < MaxSteps; i++)
                if (Offset[i] != o.Offset[i] || Velocity[i] != o.Velocity[i] || Gate[i] != o.Gate[i] || Prob[i] != o.Prob[i] || Tie[i] != o.Tie[i]) return false;
            return true;
        }

        public override string ToString()
        {
            int last = MaxSteps - 1;
            while (last >= 0 && IsDefault(last)) last--;
            var sb = new StringBuilder();
            for (int i = 0; i <= last; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Offset[i].ToString(CultureInfo.InvariantCulture)).Append(',').Append(Velocity[i]).Append(',').Append(Gate[i])
                    .Append(',').Append(Tie[i] ? 1 : 0).Append(',').Append(Prob[i]);
            }
            return sb.ToString();
        }

        public static SeqPattern Parse(string text) => TryParse(text, out var p, out _) ? p : Default;

        public static bool TryParse(string text, out SeqPattern pattern, out string error)
        {
            pattern = Default;
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;
            var p = new SeqPattern();
            var steps = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (steps.Length > MaxSteps) { error = $"a sequence has at most {MaxSteps} steps ({steps.Length} given)"; return false; }
            for (int i = 0; i < steps.Length; i++)
            {
                var f = steps[i].Split(',');
                if (!int.TryParse(f[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int off))
                { error = $"step {i + 1}: \"{f[0]}\" is not a note offset"; return false; }
                int vel = DefaultVelocity, gate = DefaultGate, prob = 100;
                bool tie = false;
                if (f.Length > 1 && !int.TryParse(f[1], out vel)) { error = $"step {i + 1}: \"{f[1]}\" is not a velocity"; return false; }
                if (f.Length > 2 && !int.TryParse(f[2], out gate)) { error = $"step {i + 1}: \"{f[2]}\" is not a gate"; return false; }
                if (f.Length > 3)
                {
                    string t = f[3].Trim().ToLowerInvariant();
                    tie = t == "1" || t == "t" || t == "true";
                    if (!tie && t != "0" && t != "f" && t != "false" && t != "") { error = $"step {i + 1}: \"{f[3]}\" is not a tie (0 or 1)"; return false; }
                }
                if (f.Length > 4 && !int.TryParse(f[4], out prob)) { error = $"step {i + 1}: \"{f[4]}\" is not a probability"; return false; }
                p.Offset[i] = (sbyte)Math.Clamp(off, -48, 48);
                p.Velocity[i] = (byte)Math.Clamp(vel, 0, 127);
                p.Gate[i] = (byte)Math.Clamp(gate, 1, 100);
                p.Tie[i] = tie;
                p.Prob[i] = (byte)Math.Clamp(prob, 0, 100);
            }
            pattern = p;
            return true;
        }
    }

    internal sealed class NoteDriver
    {
        public const int MaxHeld = 64;
        const int MaxSounding = 256;
        /// <summary>The allocator key of a driver note (its MIDI note added).</summary>
        public const int KeyBase = 2000;
        const double Eps = 1e-4;

        // Settings (the engine copies them in at a block's start).
        public ArpMode Arp;
        public int ArpOctaves = 1;
        public float ArpGate = 0.6f, ArpSwing;
        public double ArpStep = 0.25;
        public SeqMode Seq;
        public int SeqLength = 16;
        public double SeqStep = 0.25;
        public float SeqSwing;
        public uint Seed;
        public SeqPattern Pattern = SeqPattern.Default;

        bool latch;
        // Held keys (press order) and what is sorted from them.
        readonly int[] hKey = new int[MaxHeld], hNote = new int[MaxHeld];
        readonly float[] hVel = new float[MaxHeld];
        readonly bool[] hDown = new bool[MaxHeld];
        int held, downCount;
        readonly int[] sNote = new int[MaxHeld];
        readonly float[] sVel = new float[MaxHeld];
        readonly int[] soundKey = new int[MaxSounding];
        int sounding;

        sealed class Stepper
        {
            public long Next = long.MinValue;       // the next step to fire
            public double OffBeat = double.NaN;     // when the notes sounding end (NaN: none scheduled)
        }

        readonly Stepper arp = new Stepper(), seq = new Stepper();
        long arpCount;       // arp steps fired since the keys were pressed (the pattern's position)
        bool tied;           // the sequencer's note holds into its next step

        /// <summary>The arpeggiator or the sequencer's notes take the keys over (the keys then don't sound as played).</summary>
        public bool Intercepts => Seq == SeqMode.Notes || Arp != ArpMode.Off;

        /// <summary>Something here has steps to fire.</summary>
        public bool Running => Arp != ArpMode.Off || Seq != SeqMode.Off || sounding > 0;

        public int HeldCount => held;
        public int Sounding => sounding;

        public bool Latch
        {
            get => latch;
            set
            {
                if (latch && !value)
                    for (int i = held - 1; i >= 0; i--) if (!hDown[i]) RemoveAt(i);
                latch = value;
            }
        }

        public static uint Mix(uint seed, long k)
        {
            uint x = (uint)k * 0x9E3779B1u ^ (uint)(k >> 32) * 0x85EBCA77u ^ seed * 0xC2B2AE3Du;
            x ^= x >> 16; x *= 0x85EBCA6Bu; x ^= x >> 13; x *= 0xC2B2AE35u; x ^= x >> 16;
            return x;
        }

        void RemoveAt(int i)
        {
            if (hDown[i]) downCount--;
            for (int j = i + 1; j < held; j++)
            {
                hKey[j - 1] = hKey[j]; hNote[j - 1] = hNote[j]; hVel[j - 1] = hVel[j]; hDown[j - 1] = hDown[j];
            }
            held--;
        }

        /// <summary>A key goes down.</summary>
        public void Press(int key, int note, float velocity)
        {
            if (latch && downCount == 0) held = 0;
            for (int i = 0; i < held; i++)
                if (hKey[i] == key) RemoveAt(i--);
            if (held == 0)
            {
                arpCount = 0;
                downCount = 0;
            }
            if (held >= MaxHeld) return;
            hKey[held] = key; hNote[held] = note; hVel[held] = velocity; hDown[held] = true;
            held++;
            downCount++;
        }

        /// <summary>A key comes up (a latched note stays).</summary>
        public void Release(int key)
        {
            for (int i = 0; i < held; i++)
                if (hKey[i] == key)
                {
                    if (hDown[i]) { hDown[i] = false; downCount--; }
                    if (!latch) RemoveAt(i);
                    return;
                }
        }

        /// <summary>Whether the first key of a phrase has just gone down (nothing was held before it).</summary>
        public bool OnlyOneHeld => held == 1;

        /// <summary>Everything off: the held keys forgotten and the notes sounding released.</summary>
        public void Clear(IDriverOut o)
        {
            ReleaseSounding(o);
            held = 0;
            downCount = 0;
            arpCount = 0;
            tied = false;
        }

        /// <summary>
        /// A mode change: what sounded is released; keys that sounded as played are taken over (released), or handed back
        /// (played) when the driver lets go of them.
        /// </summary>
        public void SetModes(ArpMode arpMode, SeqMode seqMode, IDriverOut o)
        {
            if (arpMode == Arp && seqMode == Seq) return;
            bool was = Intercepts;
            Arp = arpMode;
            Seq = seqMode;
            ReleaseSounding(o);
            bool now = Intercepts;
            if (now && !was)
                for (int i = 0; i < held; i++) o.Stop(hKey[i]);
            else if (was && !now)
                for (int i = 0; i < held; i++) if (hDown[i]) o.Play(hKey[i], hNote[i], hVel[i]);
            tied = false;
            arpCount = 0;
        }

        /// <summary>The song position jumped, or the clock changed: forget where the steps were.</summary>
        public void Resync(IDriverOut o)
        {
            ReleaseSounding(o);
            arp.Next = long.MinValue;
            seq.Next = long.MinValue;
            tied = false;
        }

        void ReleaseSounding(IDriverOut o)
        {
            for (int i = 0; i < sounding; i++) o.Stop(soundKey[i]);
            sounding = 0;
            arp.OffBeat = double.NaN;
            seq.OffBeat = double.NaN;
        }

        static double Start(long k, double step, float swing) => k * step + ((k & 1) != 0 ? swing * 0.5 * step : 0.0);

        static long FirstAtOrAfter(double beat, double spb, double step, float swing)
        {
            long k = (long)Math.Floor(beat / step) - 2;
            while ((Start(k, step, swing) - beat) * spb <= -1 + Eps) k++;
            return k < 0 ? 0 : k;
        }

        /// <summary>At a block's start: the steps not fired yet start at the first one that falls in this block or after it.</summary>
        public void BeginBlock(double beat0, double spb, IDriverOut o)
        {
            Sync(arp, beat0, spb, ArpStep, ArpSwing, o);
            Sync(seq, beat0, spb, SeqStep, SeqSwing, o);
        }

        void Sync(Stepper s, double beat0, double spb, double step, float swing, IDriverOut o)
        {
            long first = FirstAtOrAfter(beat0, spb, step, swing);
            if (s.Next == long.MinValue || s.Next < first) s.Next = first;
            else if (s.Next > first + 1)
            {
                // The song went back (a loop, a jump): what was sounding ends, the steps start again from here.
                ReleaseSounding(o);
                s.Next = first;
                tied = false;
            }
        }

        static int OffsetOf(double beat, double beat0, double spb, int floor)
        {
            double x = Math.Ceiling((beat - beat0) * spb - Eps);
            if (x < floor) return floor;
            return x > int.MaxValue / 2 ? int.MaxValue / 2 : (int)x;
        }

        /// <summary>The first sample at or after <paramref name="from"/> where something happens (n: nothing in this block).</summary>
        public int NextEvent(double beat0, double spb, int from, int n)
        {
            int best = n;
            best = Math.Min(best, OfStepper(arp, Arp != ArpMode.Off && Seq != SeqMode.Notes, ArpStep, ArpSwing, beat0, spb, from));
            best = Math.Min(best, OfStepper(seq, Seq != SeqMode.Off, SeqStep, SeqSwing, beat0, spb, from));
            return best;
        }

        static int OfStepper(Stepper s, bool active, double step, float swing, double beat0, double spb, int from)
        {
            int best = int.MaxValue;
            if (!double.IsNaN(s.OffBeat)) best = OffsetOf(s.OffBeat, beat0, spb, from);
            if (active) best = Math.Min(best, OffsetOf(Start(s.Next, step, swing), beat0, spb, from));
            return best;
        }

        /// <summary>Fires everything due at sample <paramref name="at"/> (or earlier).</summary>
        public void Fire(int at, double beat0, double spb, IDriverOut o)
        {
            FireStepper(arp, ArpStep, ArpSwing, beat0, spb, at, o, true);
            FireStepper(seq, SeqStep, SeqSwing, beat0, spb, at, o, false);
        }

        void FireStepper(Stepper s, double step, float swing, double beat0, double spb, int at, IDriverOut o, bool isArp)
        {
            bool active = isArp ? Arp != ArpMode.Off && Seq != SeqMode.Notes : Seq != SeqMode.Off;
            if (!double.IsNaN(s.OffBeat) && OffsetOf(s.OffBeat, beat0, spb, 0) <= at)
            {
                s.OffBeat = double.NaN;
                ReleaseSounding(o);
            }
            if (!active) return;
            if (OffsetOf(Start(s.Next, step, swing), beat0, spb, 0) > at) return;
            long k = s.Next++;
            if (isArp) FireArp(s, k, step, swing, o);
            else FireSeq(s, k, step, swing, o);
        }

        void Add(IDriverOut o, int note, float vel)
        {
            if (note < 0 || note > 127 || sounding >= MaxSounding) return;
            int key = KeyBase + note;
            for (int i = 0; i < sounding; i++) if (soundKey[i] == key) return;
            soundKey[sounding++] = key;
            o.Play(key, note, vel);
        }

        void FireArp(Stepper s, long k, double step, float swing, IDriverOut o)
        {
            ReleaseSounding(o);
            int n = held;
            if (n == 0)
            {
                arpCount = 0;
                return;
            }
            // The held notes low to high (Played: in press order), then the octaves.
            for (int i = 0; i < n; i++) { sNote[i] = hNote[i]; sVel[i] = hVel[i]; }
            if (Arp != ArpMode.Played)
                for (int i = 1; i < n; i++)
                {
                    int nn = sNote[i], jj = i - 1;
                    float vv = sVel[i];
                    while (jj >= 0 && sNote[jj] > nn) { sNote[jj + 1] = sNote[jj]; sVel[jj + 1] = sVel[jj]; jj--; }
                    sNote[jj + 1] = nn; sVel[jj + 1] = vv;
                }
            int oct = Math.Clamp(ArpOctaves, 1, 4);
            long t = (long)n * oct;
            long p = arpCount++;
            if (Arp == ArpMode.Chord)
            {
                int shift = 12 * (int)(p % oct);
                for (int i = 0; i < n; i++) Add(o, sNote[i] + shift, sVel[i]);
            }
            else
            {
                long idx;
                switch (Arp)
                {
                    case ArpMode.Down: idx = t - 1 - p % t; break;
                    case ArpMode.UpDown:
                    {
                        long cycle = t <= 1 ? 1 : 2 * t - 2, q = p % cycle;
                        idx = q < t ? q : cycle - q;
                        break;
                    }
                    case ArpMode.Random: idx = Mix(Seed * 2u, k) % (uint)t; break;
                    default: idx = p % t; break;
                }
                int note = sNote[(int)(idx % n)] + 12 * (int)(idx / n);
                Add(o, note, sVel[(int)(idx % n)]);
            }
            double start = Start(k, step, swing);
            double end = Math.Min(start + Math.Clamp(ArpGate, 0.05f, 1f) * step, Start(k + 1, step, swing));
            s.OffBeat = end;
        }

        void FireSeq(Stepper s, long k, double step, float swing, IDriverOut o)
        {
            var pat = Pattern;
            int len = Math.Clamp(SeqLength, 1, SeqPattern.MaxSteps);
            int i = (int)(((k % len) + len) % len);
            o.SeqValues(pat.Offset[i] / 24f, pat.Velocity[i] / 127f);
            if (Seq != SeqMode.Notes)
            {
                s.OffBeat = double.NaN;
                return;
            }
            bool play = held > 0 && pat.Velocity[i] > 0 && Mix(Seed * 2u + 1u, k) % 100u < pat.Prob[i];
            int note = play ? hNote[held - 1] + pat.Offset[i] : -1;
            if (note < 0 || note > 127) play = false;
            if (play && tied && sounding == 1 && soundKey[0] == KeyBase + note)
            {
                // Tied to the same note: it just goes on.
            }
            else if (play && tied && sounding > 0)
            {
                // Legato: the new note first, then the old one ends.
                int old = soundKey[0];
                sounding = 0;
                Add(o, note, pat.Velocity[i] / 127f);
                o.Stop(old);
            }
            else
            {
                ReleaseSounding(o);
                if (play) Add(o, note, pat.Velocity[i] / 127f);
            }
            tied = play && pat.Tie[i];
            double start = Start(k, step, swing);
            s.OffBeat = !play || tied ? double.NaN : Math.Min(start + pat.Gate[i] / 100.0 * step, Start(k + 1, step, swing));
            if (play && tied) s.OffBeat = double.NaN;
        }
    }
}
