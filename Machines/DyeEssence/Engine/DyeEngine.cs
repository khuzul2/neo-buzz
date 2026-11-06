#nullable disable
// Dye Essence (docs/specs/dye-essence.md §3): the engine behind the machine. Parameters (in real
// units, from the generated table) become the note bank's settings at the start of a block when any
// has changed; notes go through Neo's voice allocator (poly, mono, legato, glide, sustain, steals)
// to the bank's note slots. User wavetables arrive from any thread and are picked up at a block.

using System;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    internal sealed class DyeEngine : INoteSink
    {
        public const int MaxBlock = NoteBank.MaxBlock;
        /// <summary>Headroom: one note of the init sound peaks round −12 dBFS at 0 dB volume.</summary>
        const float Headroom = 0.25f;

        static readonly int[] OscBase = { P.AType, P.BType, P.CType };
        static readonly int[] FilterBase = { P.F1Type, P.F2Type };

        readonly ParamValues values;
        readonly VoiceAllocator alloc;
        readonly TableArena[] requestedUser = new TableArena[NoteBank.Oscillators];
        readonly TableArena[] userTable = new TableArena[NoteBank.Oscillators];
        readonly string[] userName = { "", "", "" };
        int userChanged;
        bool tablesPending;
        float bend, bendRange = 2;

        public DyeEngine(ParamValues values, LaneKind lanes = LaneKind.Auto)
        {
            this.values = values;
            Bank = new NoteBank(lanes);
            var slots = new IVoice[NoteBank.Notes];
            for (int i = 0; i < slots.Length; i++) slots[i] = new Slot(Bank, i);
            alloc = new VoiceAllocator(slots);
            SetSampleRate(48000);
            Update(true);
        }

        public NoteBank Bank { get; }
        public readonly LevelMeter OutMeter = new LevelMeter();
        public float ModWheelValue, AftertouchValue;

        public void SetSampleRate(float sr)
        {
            Bank.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
        }

        /// <summary>Whether a built-in table is still being built (any thread): offline renders wait for it.</summary>
        public bool TablesBuilding => !TableBank.AllBuilt();

        /// <summary>A chosen table is still being built (the slot plays its previous table, or nothing yet).</summary>
        public bool TablesPending => Volatile.Read(ref tablesPending);

        /// <summary>Any thread: slot <paramref name="osc"/>'s "User" table from the next block (null: none).</summary>
        public void SetUserTable(int osc, TableArena table, string name)
        {
            userName[osc] = name ?? "";
            Volatile.Write(ref requestedUser[osc], table);
            Volatile.Write(ref userChanged, 1);
        }

        public TableArena UserTable(int osc) => Volatile.Read(ref requestedUser[osc]);
        public string UserTableName(int osc) => userName[osc];

        /// <summary>The table slot <paramref name="osc"/> plays now (null while a built-in one is still being built).</summary>
        public TableArena TableFor(int osc) => Bank.S.Osc[osc].Table;

        public int ActiveNotes => Bank.ActiveNotes;

        void Update(bool force)
        {
            bool userNew = Interlocked.Exchange(ref userChanged, 0) == 1;
            if (!force && !values.AnyDirty && !userNew && !tablesPending) return;
            if (userNew)
                for (int o = 0; o < userTable.Length; o++) userTable[o] = Volatile.Read(ref requestedUser[o]);
            var s = Bank.S;
            alloc.Mode = (VoiceMode)values.Choice(P.VoiceMode);
            alloc.Polyphony = (int)values.Value(P.Polyphony);
            alloc.Unison = 1;
            int glide = values.Choice(P.GlideMode);
            alloc.GlideAlways = glide == 1;
            alloc.GlideLegatoOnly = glide == 2;
            s.Glide = glide == 0 ? 0f : values.F(P.Glide);
            bendRange = values.F(P.BendRange);
            s.Oversample = values.Choice(P.Quality) == 1 ? 4 : 2;
            s.VelAmp = values.F(P.VelAmp);
            double vol = values.Value(P.Volume);
            s.Volume = double.IsNegativeInfinity(vol) ? 0f : (float)DspMath.DbToGain(vol) * Headroom;

            bool pendingNow = false;
            for (int o = 0; o < NoteBank.Oscillators; o++)
            {
                var os = s.Osc[o];
                int b = OscBase[o];
                os.Type = (SlotType)values.Choice(b);
                int choice = values.Choice(b + (P.ATable - P.AType));
                var table = choice >= TableBank.Count ? userTable[o] ?? TableBank.Get(0) : TableBank.Get(choice);
                // Keep playing the previous table until a newly chosen one is built.
                if (table == null) pendingNow = true;
                else os.Table = table;
                os.Position = values.F(b + (P.APos - P.AType));
                os.Transpose = (float)(12 * values.Value(b + (P.AOct - P.AType)) + values.Value(b + (P.ASemi - P.AType)) + values.Value(b + (P.AFine - P.AType)) / 100.0);
                os.Level = values.F(b + (P.ALevel - P.AType));
                os.Pan = values.F(b + (P.APan - P.AType));
                os.Unison = (int)values.Value(b + (P.AUnison - P.AType));
                os.Detune = values.F(b + (P.ADetune - P.AType));
                os.Spread = values.F(b + (P.ASpread - P.AType));
                os.Phase = values.F(b + (P.APhase - P.AType)) / 360f;
                os.Random = values.F(b + (P.ARandom - P.AType));
                os.Warp1 = (Warp)values.Choice(b + (P.AWarp1 - P.AType));
                os.Amount1 = values.F(b + (P.AWarp1Amt - P.AType));
                os.Warp2 = (Warp)values.Choice(b + (P.AWarp2 - P.AType));
                os.Amount2 = values.F(b + (P.AWarp2Amt - P.AType));
                os.Mod = (ModSource)values.Choice(b + (P.AMod - P.AType));
                os.ToF2 = values.F(b + (P.AToF2 - P.AType));
            }
            tablesPending = pendingNow;

            s.SubShape = (SubShape)values.Choice(P.SubShape);
            s.SubOctave = -values.Choice(P.SubOct);
            s.Sub.Level = values.F(P.SubLevel);
            s.Sub.Pan = values.F(P.SubPan);
            s.Sub.ToF2 = values.F(P.SubToF2);
            s.NoiseColor = values.F(P.NoiseColor);
            s.Noise.Level = values.F(P.NoiseLevel);
            s.Noise.Pan = values.F(P.NoisePan);
            s.Noise.ToF2 = values.F(P.NoiseToF2);
            for (int f = 0; f < 2; f++)
            {
                var fs = s.Filter[f];
                int b = FilterBase[f];
                fs.Type = (FilterType)values.Choice(b);
                fs.Cutoff = values.F(b + (P.F1Cutoff - P.F1Type));
                fs.Reso = values.F(b + (P.F1Reso - P.F1Type));
                fs.Drive = (float)DspMath.DbToGain(values.Value(b + (P.F1Drive - P.F1Type)));
                fs.Env = values.F(b + (P.F1Env - P.F1Type));
                fs.Key = values.F(b + (P.F1Key - P.F1Type));
                fs.Vel = values.F(b + (P.F1Vel - P.F1Type));
            }
            s.Routing = values.F(P.Routing);
            Env(s.Amp, P.AmpA, P.AmpD, P.AmpS, P.AmpR);
            Env(s.Flt, P.FltA, P.FltD, P.FltS, P.FltR);
            values.Clean();
        }

        void Env(EnvSettings e, int a, int d, int s, int r)
        {
            e.A = values.F(a);
            e.D = values.F(d);
            e.S = values.F(s);
            e.R = values.F(r);
        }

        public bool Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            Bank.S.Bend = bend * bendRange;
            bool any = Bank.Process(left, right, n);
            OutMeter.Feed(left, right, n);
            return any || OutMeter.Peak > 1e-6f;
        }

        public void NoteOn(int key, int note, float velocity) => alloc.NoteOn(key, note, velocity);
        public void NoteOff(int key) => alloc.NoteOff(key);
        public void ModWheel(float value) => ModWheelValue = value;
        public void PitchBend(float value) => bend = value;
        public void Sustain(bool down) => alloc.Sustain(down);
        public void Aftertouch(float value) => AftertouchValue = value;
        public void AllNotesOff(bool hard) => alloc.AllNotesOff(hard);

        /// <summary>A note slot of the bank as the allocator's voice.</summary>
        sealed class Slot : IVoice
        {
            readonly NoteBank bank;
            readonly int slot;

            public Slot(NoteBank bank, int slot)
            {
                this.bank = bank;
                this.slot = slot;
            }

            public bool IsActive => bank.Active(slot);
            public float Loudness => bank.Loudness(slot);
            public void NoteOn(float note, float velocity, int unisonIndex, int unisonCount, float glideFrom) => bank.NoteOn(slot, note, velocity, glideFrom);
            public void Legato(float note, bool glide) => bank.Legato(slot, note, glide);
            public void NoteOff() => bank.NoteOff(slot);
            public void Kill() => bank.Kill(slot);
        }
    }
}
