#nullable disable
// Neo Pulse engine (docs/specs/neo-pulse.md): 8-lane Euclidean and probability rhythm generator on note events.
// Pure C#, portable, zero heap allocation per block, zero locks. Bjorklund's algorithm (Toussaint, 2005) builds
// each lane's rhythm ring and accent ring; steps, swing, dividers, ratchets, gate and humanize are scheduled in
// the song's beat domain so every NoteOn and NoteOff lands within 1 sample at any tempo or block size.

using System;
using System.Globalization;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Pulse
{
    /// <summary>Matches BuzzGUI.Interfaces.NoteMessageKind (the machine casts).</summary>
    internal enum PulseKind : byte { NoteOn, NoteOff, Controller, PitchBend, Pressure }

    /// <summary>One outgoing note event: Offset is samples from the start of the block.</summary>
    internal struct PulseEvent
    {
        public int Offset;
        public PulseKind Kind;
        public byte Channel;
        public byte Data1;
        public int Data2;

        public static PulseEvent On(int offset, int channel, int note, int velocity) => new PulseEvent
        {
            Offset = offset,
            Kind = PulseKind.NoteOn,
            Channel = (byte)(channel & 15),
            Data1 = (byte)(note & 127),
            Data2 = Math.Clamp(velocity, 1, 127),
        };

        public static PulseEvent Off(int offset, int channel, int note) => new PulseEvent
        {
            Offset = offset,
            Kind = PulseKind.NoteOff,
            Channel = (byte)(channel & 15),
            Data1 = (byte)(note & 127),
            Data2 = 0,
        };
    }

    /// <summary>Host clock snapshot for one block.</summary>
    internal struct PulseBlock
    {
        public int Samples;
        public bool Playing;
        /// <summary>Song position at the block's first sample, in quarter-note beats.</summary>
        public double Beat;
        /// <summary>Samples per quarter-note beat at this block's tempo.</summary>
        public double SamplesPerBeat;
        /// <summary>Quarter-note beats per bar (4 unless the song sets another meter).</summary>
        public double BeatsPerBar;
    }

    /// <summary>
    /// Clean-room iterative Bjorklund algorithm (Toussaint, 2005) producing a bitmask of up to 32 steps
    /// with zero heap allocation (bit 0 = step 0 .. bit steps-1 = final step).
    /// </summary>
    internal static class PulseEuclidean
    {
        public static uint Compute(int pulses, int steps)
        {
            steps = Math.Clamp(steps, 1, 32);
            pulses = Math.Clamp(pulses, 0, steps);
            if (pulses <= 0) return 0u;
            if (pulses >= steps) return steps == 32 ? 0xFFFFFFFFu : ((1u << steps) - 1u);

            Span<uint> seq = stackalloc uint[32];
            Span<byte> len = stackalloc byte[32];
            for (int i = 0; i < pulses; i++) { seq[i] = 1u; len[i] = 1; }
            for (int i = pulses; i < steps; i++) { seq[i] = 0u; len[i] = 1; }

            int count = pulses;
            int rem = steps - pulses;
            while (rem > 1)
            {
                int pairs = Math.Min(count, rem);
                for (int i = 0; i < pairs; i++)
                {
                    seq[i] |= seq[count + i] << len[i];
                    len[i] = (byte)(len[i] + len[count + i]);
                }
                if (rem > count)
                {
                    int left = rem - count;
                    for (int i = 0; i < left; i++)
                    {
                        seq[count + i] = seq[2 * count + i];
                        len[count + i] = len[2 * count + i];
                    }
                    rem = left;
                }
                else
                {
                    int left = count - rem;
                    count = pairs;
                    rem = left;
                }
            }

            uint mask = 0u;
            int bit = 0;
            int total = count + rem;
            for (int i = 0; i < total; i++)
            {
                mask |= seq[i] << bit;
                bit += len[i];
            }
            return mask;
        }

        /// <summary>Right-rotates a <paramref name="steps"/>-bit rhythm mask by <paramref name="rotation"/> steps.</summary>
        public static uint Rotate(uint mask, int steps, int rotation)
        {
            steps = Math.Clamp(steps, 1, 32);
            int r = Math.Clamp(rotation, 0, steps - 1);
            if (r == 0 || mask == 0u) return mask;
            ulong full = steps == 32 ? 0xFFFFFFFFul : ((1ul << steps) - 1ul);
            ulong m = mask & full;
            ulong shifted = ((m << r) | (m >> (steps - r))) & full;
            return (uint)shifted;
        }

        public static uint Rhythm(int pulses, int steps, int rotation) =>
            Rotate(Compute(pulses, steps), steps, rotation);
    }

    internal static class PulseText
    {
        static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        static bool installed;

        public static string FormatNote(int midi)
        {
            midi = Math.Clamp(midi, 0, 127);
            int oct = (midi / 12) - 1;
            return NoteNames[midi % 12] + oct.ToString(CultureInfo.InvariantCulture);
        }

        public static void Install(ParamDef[] table)
        {
            if (installed) return;
            installed = true;
            table[P.Seed].Describer = raw => raw <= 0 ? "Free" : raw.ToString(CultureInfo.InvariantCulture);
            for (int l = 0; l < PulseEngine.LaneCount; l++)
                table[PulseEngine.LaneParam(l, PulseEngine.FNote)].Describer = FormatNote;
        }
    }

    internal sealed class PulseEngine
    {
        public const int LaneCount = 8;
        public const int LaneStride = 15;
        public const int LaneBase = P.L1Steps;

        public const int FSteps = 0, FPulses = 1, FRot = 2, FChance = 3, FAccPulses = 4, FAccRot = 5,
            FNote = 6, FVel = 7, FAccVel = 8, FGate = 9, FRatchet = 10, FRatchetCh = 11, FDiv = 12, FCh = 13, FMute = 14;

        public static int LaneParam(int lane, int field) => LaneBase + lane * LaneStride + field;

        public const int OutputCapacity = 4096;
        const int NoteOffReserve = 256;
        const int PendingCapacity = 512;
        const int KeySpace = 16 * 128; // unique (channel 0..15, note 0..127)

        /// <summary>Base step durations in quarter-note beats for Rate 1/1 .. 1/64.</summary>
        public static readonly double[] RateBeats = TempoSync.BeatsTable(Params.Table[P.Rate].Names);
        public static readonly double[] FeelFactors = TempoSync.FeelFactors;
        public static readonly int[] Dividers = { 1, 2, 3, 4 };

        const ulong PurposeChance = 0x101u;
        const ulong PurposeFill = 0x202u;
        const ulong PurposeRatchet = 0x303u;
        const ulong PurposeHumTime = 0x404u;
        const ulong PurposeHumVel = 0x505u;

        static long globalFreeSalt;

        struct ActiveNote
        {
            public bool Active;
            public byte OwnerLane;
            public byte Channel;
            public byte Note;
            public double OffBeat;
        }

        struct PendingHit
        {
            public byte Lane;
            public byte Channel;
            public byte Note;
            public byte Vel;
            public double OnBeat;
            public double OffBeat;
        }

        readonly ParamValues v;
        int sampleRate = 48000;

        /// <summary>Events emitted during the last <see cref="Process"/> call, in chronological offset order.</summary>
        public readonly PulseEvent[] Out = new PulseEvent[OutputCapacity];
        public int OutCount;

        // Active notes keyed uniquely by (channel << 7) | note, plus compact list of active keys for O(active) scans.
        readonly ActiveNote[] activeByKey = new ActiveNote[KeySpace];
        readonly int[] activeKeys = new int[KeySpace];
        int activeCount;

        // Queued note-ons (sub-hits from ratchets or humanized timing delays).
        readonly PendingHit[] pending = new PendingHit[PendingCapacity];
        int pendingCount;

        // Per-lane scheduling state and cached Euclidean masks.
        readonly long[] nextStep = new long[LaneCount];
        readonly int[] lastLaneSig = new int[LaneCount];
        readonly int[] lastDiv = new int[LaneCount];
        readonly int[] lastMute = new int[LaneCount];
        readonly int[] currentStep = new int[LaneCount];
        readonly uint[] rhythmMask = new uint[LaneCount];
        readonly uint[] accentMask = new uint[LaneCount];

        bool wasPlaying;
        double expectedBeat;
        int lastGlobalClockSig = int.MinValue;
        ulong freeSalt;
        volatile bool panicRequested;

        public PulseEngine(ParamValues values)
        {
            v = values;
            PulseText.Install(Params.Table);
            freeSalt = (ulong)Interlocked.Increment(ref globalFreeSalt);
            for (int l = 0; l < LaneCount; l++)
            {
                lastLaneSig[l] = int.MinValue;
                lastDiv[l] = -1;
                lastMute[l] = -1;
                currentStep[l] = -1;
            }
        }

        public void SetSampleRate(int rate) => sampleRate = Math.Max(8000, rate);

        public void RequestPanic() => panicRequested = true;

        /// <summary>Live playing step index (0..Steps-1) for lane <paramref name="lane"/>, or -1 while stopped.</summary>
        public int CurrentStep(int lane) => (uint)lane < LaneCount ? Volatile.Read(ref currentStep[lane]) : -1;

        public void Process(in PulseBlock block)
        {
            OutCount = 0;
            int n = Math.Max(0, block.Samples);
            if (n == 0) return;

            if (panicRequested)
            {
                panicRequested = false;
                FlushAllActive(0);
                pendingCount = 0;
            }

            if (!block.Playing)
            {
                if (wasPlaying)
                {
                    FlushAllActive(0);
                    pendingCount = 0;
                    wasPlaying = false;
                    for (int l = 0; l < LaneCount; l++)
                        Volatile.Write(ref currentStep[l], -1);
                }
                return;
            }

            double spb = block.SamplesPerBeat > 1.0 ? block.SamplesPerBeat : sampleRate * 0.5;
            double beat0 = block.Beat;
            double endBeat = beat0 + n / spb;
            double halfSampleBeats = 0.5 / spb;

            int rateIdx = Math.Clamp(v.Choice(P.Rate), 0, RateBeats.Length - 1);
            int feelIdx = Math.Clamp(v.Choice(P.Feel), 0, FeelFactors.Length - 1);
            int swingPct = Math.Clamp((int)v.Value(P.Swing), 0, 75);
            // Timing: Song tempo (a division and a feel) or Free time (a step in ms, turned into beats at the tempo of this block).
            bool freeTime = v.Choice(P.Sync) == 1;
            double baseStepBeats = freeTime
                ? Math.Clamp(v.Value(P.FreeMs), 20.0, 2000.0) * 0.001 * sampleRate / spb
                : TempoSync.Beats(RateBeats[rateIdx], feelIdx);
            double swingFrac = swingPct * 0.01;
            int globalClockSig = freeTime
                ? (1 << 30) | (swingPct << 20) | (int)Math.Min(0xFFFFF, Math.Round(baseStepBeats * 4096.0))
                : (rateIdx << 10) | (feelIdx << 8) | swingPct;

            double humTimeMs = Math.Clamp(v.Value(P.HumTime), 0.0, 20.0);
            int humVelPct = Math.Clamp((int)v.Value(P.HumVel), 0, 30);
            int fillPct = Math.Clamp((int)v.Value(P.Fill), 0, 100);
            int seedParam = Math.Clamp((int)v.Value(P.Seed), 0, 9999);

            double jumpTol = Math.Max(1e-4, 2.0 / spb);
            bool started = !wasPlaying;
            bool jumped = wasPlaying && Math.Abs(beat0 - expectedBeat) > jumpTol;
            bool globalClockChanged = wasPlaying && globalClockSig != lastGlobalClockSig;

            if (started)
                freeSalt = (ulong)Interlocked.Increment(ref globalFreeSalt);

            if (started || jumped || globalClockChanged)
            {
                if (wasPlaying)
                {
                    FlushAllActive(0);
                    pendingCount = 0;
                }
                wasPlaying = true;
                lastGlobalClockSig = globalClockSig;
                for (int l = 0; l < LaneCount; l++)
                {
                    int divIdx = Math.Clamp(v.Choice(LaneParam(l, FDiv)), 0, 3);
                    double laneStep = baseStepBeats * Dividers[divIdx];
                    nextStep[l] = FirstStepAtOrAfter(beat0 - halfSampleBeats, laneStep, swingFrac);
                    lastDiv[l] = divIdx;
                }
            }

            expectedBeat = endBeat;
            ulong effSeed = seedParam > 0 ? (ulong)seedParam : (0x9E3779B97F4A7C15ul ^ (freeSalt * 0xBF58476D1CE4E5B9ul));

            // Per-lane parameter change checks and step generation for this block.
            for (int l = 0; l < LaneCount; l++)
            {
                int b = LaneBase + l * LaneStride;
                int steps = Math.Clamp((int)v.Value(b + FSteps), 1, 32);
                int pulses = Math.Clamp((int)v.Value(b + FPulses), 0, steps);
                int rot = Math.Clamp((int)v.Value(b + FRot), 0, steps - 1);
                int chance = Math.Clamp((int)v.Value(b + FChance), 0, 100);
                int accPulses = Math.Clamp((int)v.Value(b + FAccPulses), 0, steps);
                int accRot = Math.Clamp((int)v.Value(b + FAccRot), 0, steps - 1);
                int note = Math.Clamp((int)v.Value(b + FNote), 0, 127);
                int vel = Math.Clamp((int)v.Value(b + FVel), 1, 127);
                int accVel = Math.Clamp((int)v.Value(b + FAccVel), 1, 127);
                int gatePct = Math.Clamp((int)v.Value(b + FGate), 5, 100);
                int ratchet = Math.Clamp((int)v.Value(b + FRatchet), 1, 4);
                int ratchetCh = Math.Clamp((int)v.Value(b + FRatchetCh), 0, 100);
                int divIdx = Math.Clamp(v.Choice(b + FDiv), 0, 3);
                int ch = Math.Clamp((int)v.Value(b + FCh), 1, 16) - 1;
                int muted = v.On(b + FMute) ? 1 : 0;

                int sig = steps;
                sig = sig * 37 + pulses;
                sig = sig * 37 + rot;
                sig = sig * 37 + accPulses;
                sig = sig * 37 + accRot;
                sig = sig * 131 + note;
                sig = sig * 17 + ch;
                sig = sig * 5 + divIdx;

                if (sig != lastLaneSig[l] || muted != lastMute[l])
                {
                    if (lastLaneSig[l] != int.MinValue)
                    {
                        // Flush active notes owned by this lane and drop stale pending hits when muted or edited.
                        FlushLaneActive(l, 0);
                        DropLanePending(l);
                    }
                    rhythmMask[l] = PulseEuclidean.Rhythm(pulses, steps, rot);
                    accentMask[l] = PulseEuclidean.Rhythm(accPulses, steps, accRot);
                    if (divIdx != lastDiv[l])
                    {
                        double newLaneStep = baseStepBeats * Dividers[divIdx];
                        nextStep[l] = FirstStepAtOrAfter(beat0 - halfSampleBeats, newLaneStep, swingFrac);
                        lastDiv[l] = divIdx;
                    }
                    lastLaneSig[l] = sig;
                    lastMute[l] = muted;
                }

                double laneStepBeats = baseStepBeats * Dividers[divIdx];
                if (muted != 0)
                {
                    // Advance step cursor silently while muted so unmuting resumes on the exact song grid.
                    long s = nextStep[l];
                    while (StepBeat(s, laneStepBeats, swingFrac) < endBeat - halfSampleBeats) s++;
                    nextStep[l] = s;
                    Volatile.Write(ref currentStep[l], -1);
                    continue;
                }

                // Generate all steps whose nominal beat falls in this block (rounded offset < n).
                while (true)
                {
                    long s = nextStep[l];
                    double sBeat = StepBeat(s, laneStepBeats, swingFrac);
                    if (sBeat >= endBeat - halfSampleBeats) break;
                    nextStep[l] = s + 1;

                    int stepInRing = (int)(s % steps);
                    if (stepInRing < 0) stepInRing += steps;
                    Volatile.Write(ref currentStep[l], stepInRing);

                    bool isMainHit = ((rhythmMask[l] >> stepInRing) & 1u) != 0;
                    bool fires;
                    if (isMainHit)
                        fires = chance >= 100 || (chance > 0 && HashUnit(effSeed, l, s, 0, PurposeChance) * 100.0 < chance);
                    else
                        fires = fillPct >= 100 || (fillPct > 0 && HashUnit(effSeed, l, s, 0, PurposeFill) * 100.0 < fillPct);

                    if (!fires) continue;

                    bool accented = ((accentMask[l] >> stepInRing) & 1u) != 0;
                    int baseVel = accented ? accVel : vel;

                    double nextSBeat = StepBeat(s + 1, laneStepBeats, swingFrac);
                    double slotBeats = nextSBeat - sBeat;

                    int repeats = 1;
                    if (ratchet > 1)
                    {
                        bool doRatchet = ratchetCh >= 100 || (ratchetCh > 0 && HashUnit(effSeed, l, s, 0, PurposeRatchet) * 100.0 < ratchetCh);
                        if (doRatchet) repeats = ratchet;
                    }

                    double subBeats = slotBeats / repeats;
                    double minDurBeats = 1.0 / spb;
                    double humBeatScale = humTimeMs > 0.0 ? (humTimeMs * 0.001 * sampleRate / spb) : 0.0;

                    for (int k = 0; k < repeats; k++)
                    {
                        double nomOn = sBeat + k * subBeats;
                        double delayBeats = humBeatScale > 0.0 ? HashUnit(effSeed, l, s, k, PurposeHumTime) * humBeatScale : 0.0;
                        double onBeat = nomOn + delayBeats;

                        // Gate scales the actual slot (or ratchet sub-slot), so swung odd steps never overrun step s+1.
                        double gateBeats = subBeats * (gatePct * 0.01);
                        if (repeats > 1)
                        {
                            double maxEnd = nomOn + subBeats;
                            if (onBeat + gateBeats > maxEnd)
                                gateBeats = Math.Max(minDurBeats, maxEnd - onBeat);
                        }
                        double offBeat = onBeat + Math.Max(minDurBeats, gateBeats);

                        int hitVel = baseVel;
                        if (humVelPct > 0)
                        {
                            double r = HashUnit(effSeed, l, s, k, PurposeHumVel);
                            hitVel = Math.Clamp((int)Math.Round(baseVel * (1.0 + (2.0 * r - 1.0) * (humVelPct * 0.01))), 1, 127);
                        }

                        EnqueuePending((byte)l, (byte)ch, (byte)note, (byte)hitVel, onBeat, offBeat);
                    }
                }
            }

            // Dispatch all due NoteOff and NoteOn events in exact sample-offset order,
            // always emitting due NoteOffs before same-sample NoteOns.
            while (true)
            {
                int bestOffIdx = -1;
                int bestOffOffset = n;
                double bestOffBeat = double.MaxValue;
                for (int i = 0; i < activeCount; i++)
                {
                    ref var a = ref activeByKey[activeKeys[i]];
                    int off = SampleOffset(a.OffBeat, beat0, spb);
                    if (off < n && (off < bestOffOffset || (off == bestOffOffset && a.OffBeat < bestOffBeat)))
                    {
                        bestOffOffset = off;
                        bestOffBeat = a.OffBeat;
                        bestOffIdx = i;
                    }
                }

                int bestOnIdx = -1;
                int bestOnOffset = n;
                double bestOnBeat = double.MaxValue;
                for (int i = 0; i < pendingCount; i++)
                {
                    int off = SampleOffset(pending[i].OnBeat, beat0, spb);
                    if (off < n && (off < bestOnOffset || (off == bestOnOffset && pending[i].OnBeat < bestOnBeat)))
                    {
                        bestOnOffset = off;
                        bestOnBeat = pending[i].OnBeat;
                        bestOnIdx = i;
                    }
                }

                if (bestOffIdx < 0 && bestOnIdx < 0) break;

                // NoteOff wins ties so Gate = 100 % and ratchets cleanly release before the next NoteOn.
                if (bestOffIdx >= 0 && (bestOnIdx < 0 || bestOffOffset <= bestOnOffset))
                {
                    int key = activeKeys[bestOffIdx];
                    ref var a = ref activeByKey[key];
                    EmitOff(bestOffOffset, a.Channel, a.Note);
                    RemoveActiveAt(bestOffIdx);
                }
                else
                {
                    var hit = pending[bestOnIdx];
                    RemovePendingAt(bestOnIdx);
                    int key = ((int)hit.Channel << 7) | hit.Note;

                    // Reserve room so NoteOffs are never dropped even if a block hits OutputCapacity.
                    if (OutCount >= OutputCapacity - NoteOffReserve) continue;

                    ref var slot = ref activeByKey[key];
                    if (slot.Active)
                    {
                        // Retriggering an already-sounding (channel, note) emits NoteOff first and overwrites in place.
                        EmitOff(bestOnOffset, hit.Channel, hit.Note);
                        slot.OwnerLane = hit.Lane;
                        slot.OffBeat = hit.OffBeat;
                    }
                    else
                    {
                        slot.Active = true;
                        slot.OwnerLane = hit.Lane;
                        slot.Channel = hit.Channel;
                        slot.Note = hit.Note;
                        slot.OffBeat = hit.OffBeat;
                        activeKeys[activeCount++] = key;
                    }
                    EmitOn(bestOnOffset, hit.Channel, hit.Note, hit.Vel);
                }
            }
        }

        // ------------------------------------------------------------------ beat & hash helpers

        public static double StepBeat(long s, double laneStepBeats, double swingFrac) =>
            (s + ((s & 1L) != 0 ? swingFrac : 0.0)) * laneStepBeats;

        static long FirstStepAtOrAfter(double beat, double laneStepBeats, double swingFrac)
        {
            if (beat <= 0.0) return 0;
            long s = Math.Max(0L, (long)Math.Floor(beat / laneStepBeats) - 1L);
            while (StepBeat(s, laneStepBeats, swingFrac) < beat) s++;
            return s;
        }

        static int SampleOffset(double eventBeat, double blockBeat, double spb)
        {
            int off = (int)Math.Round((eventBeat - blockBeat) * spb);
            return off < 0 ? 0 : off;
        }

        /// <summary>Stateless SplitMix64 hash -> uniform [0, 1), deterministic for any (seed, lane, step, sub, purpose).</summary>
        internal static double HashUnit(ulong seed, int lane, long step, int sub, ulong purpose)
        {
            ulong x = seed * 0x9E3779B97F4A7C15ul;
            x ^= ((ulong)(uint)lane + 1ul) * 0xBF58476D1CE4E5B9ul;
            x ^= (ulong)step * 0x94D049BB133111EBul;
            x ^= ((ulong)(uint)sub + 1ul) * 0xD6E8FEB86659FD93ul;
            x ^= purpose * 0xA0761D6478BD642Ful;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9ul;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBul;
            x ^= x >> 31;
            return (x >> 11) * (1.0 / (1ul << 53));
        }

        // ------------------------------------------------------------------ active & pending tables

        void EnqueuePending(byte lane, byte ch, byte note, byte vel, double onBeat, double offBeat)
        {
            if (pendingCount >= PendingCapacity) return;
            pending[pendingCount++] = new PendingHit
            {
                Lane = lane,
                Channel = ch,
                Note = note,
                Vel = vel,
                OnBeat = onBeat,
                OffBeat = offBeat,
            };
        }

        void RemovePendingAt(int idx)
        {
            pendingCount--;
            if (idx < pendingCount) pending[idx] = pending[pendingCount];
        }

        void DropLanePending(int lane)
        {
            for (int i = pendingCount - 1; i >= 0; i--)
                if (pending[i].Lane == lane) RemovePendingAt(i);
        }

        void RemoveActiveAt(int idx)
        {
            int key = activeKeys[idx];
            activeByKey[key].Active = false;
            activeCount--;
            if (idx < activeCount) activeKeys[idx] = activeKeys[activeCount];
        }

        void FlushAllActive(int offset)
        {
            for (int i = 0; i < activeCount; i++)
            {
                int key = activeKeys[i];
                ref var a = ref activeByKey[key];
                EmitOff(offset, a.Channel, a.Note);
                a.Active = false;
            }
            activeCount = 0;
        }

        void FlushLaneActive(int lane, int offset)
        {
            for (int i = activeCount - 1; i >= 0; i--)
            {
                int key = activeKeys[i];
                ref var a = ref activeByKey[key];
                if (a.OwnerLane != lane) continue;
                EmitOff(offset, a.Channel, a.Note);
                RemoveActiveAt(i);
            }
        }

        void EmitOn(int offset, int ch, int note, int vel)
        {
            if (OutCount < OutputCapacity) Out[OutCount++] = PulseEvent.On(offset, ch, note, vel);
        }

        void EmitOff(int offset, int ch, int note)
        {
            if (OutCount < OutputCapacity) Out[OutCount++] = PulseEvent.Off(offset, ch, note);
        }
    }
}
