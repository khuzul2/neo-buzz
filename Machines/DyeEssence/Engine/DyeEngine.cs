#nullable disable
// Dye Essence (docs/specs/dye-essence.md §3): the engine behind the machine. Parameters (in real
// units, from the generated table) become the note bank's settings at the start of a block when any
// has changed; notes go through Neo's voice allocator (poly, mono, legato, glide, sustain, steals)
// to the bank's note slots. User wavetables and drawn LFO shapes arrive from any thread and are
// picked up at a block.

using System;
using System.Threading;
using NeoBuzz.Dsp;
using NeoBuzz.Sampler;

namespace NeoBuzz.DyeEssence
{
    internal sealed class DyeEngine : INoteSink
    {
        public const int MaxBlock = NoteBank.MaxBlock;
        /// <summary>Headroom: one note of the init sound peaks round −12 dBFS at 0 dB volume.</summary>
        const float Headroom = 0.25f;

        static readonly int[] OscBase = { P.AType, P.BType, P.CType };
        static readonly int[] AnalogBase = { P.AShape, P.BShape, P.CShape };
        static readonly int[] SampleBase = { P.ASmpSrc, P.BSmpSrc, P.CSmpSrc };
        static readonly int[] EnvBase = { P.E1Delay, P.E2Delay };
        static readonly int[] LfoBase = { P.L1Shape, P.L2Shape, P.L3Shape, P.L4Shape };
        static readonly int[] MacroParam = { P.M1, P.M2, P.M3, P.M4 };
        /// <summary>The LFO sync choices' periods in beats (Free: 0).</summary>
        static readonly float[] SyncBeats = { 0, 1f / 8, 1f / 6, 1f / 4, 1f / 3, 1f / 2, 2f / 3, 1, 2, 4, 8, 16 };
        static readonly int[] FilterBase = { P.F1Type, P.F2Type };

        readonly ParamValues values;
        readonly VoiceAllocator alloc;
        readonly TableArena[] requestedUser = new TableArena[NoteBank.Oscillators];
        readonly TableArena[] userTable = new TableArena[NoteBank.Oscillators];
        readonly string[] userName = { "", "", "" };
        // The Harmonic engine's "User" sets: the first and last frames of the slot's user table, analysed when it was loaded.
        readonly float[][][] requestedPartials = new float[NoteBank.Oscillators][][];
        readonly float[][][] userPartials = new float[NoteBank.Oscillators][][];
        static readonly int[] HarmBase = { P.AHSet, P.BHSet, P.CHSet };
        readonly SampleLayer[] requestedSample = new SampleLayer[NoteBank.Oscillators];
        readonly SampleLayer[] userSample = new SampleLayer[NoteBank.Oscillators];
        readonly string[] sampleName = { "", "", "" };
        readonly DrawnShape[] requestedShape = { DrawnShape.Default, DrawnShape.Default, DrawnShape.Default, DrawnShape.Default };
        int userChanged, shapeChanged;
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
        /// <summary>The song's tempo (BPM), for synced LFOs and envelopes.</summary>
        public double Tempo = 120;

        public void SetSampleRate(float sr)
        {
            Bank.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
        }

        /// <summary>Whether a built-in table is still being built (any thread): offline renders wait for it.</summary>
        public bool TablesBuilding => !TableBank.AllBuilt() || !SampleSources.AllBuilt();

        /// <summary>A chosen table is still being built (the slot plays its previous table, or nothing yet).</summary>
        public bool TablesPending => Volatile.Read(ref tablesPending);

        /// <summary>Any thread: slot <paramref name="osc"/>'s "User" table from the next block (null: none).</summary>
        public void SetUserTable(int osc, TableArena table, string name)
        {
            userName[osc] = name ?? "";
            Volatile.Write(ref requestedPartials[osc], table == null ? null : new[] { HarmonicSets.FromTable(table, 0), HarmonicSets.FromTable(table, table.FrameCount - 1) });
            Volatile.Write(ref requestedUser[osc], table);
            Volatile.Write(ref userChanged, 1);
        }

        public TableArena UserTable(int osc) => Volatile.Read(ref requestedUser[osc]);

        /// <summary>The Harmonic engine's two "User" sets of slot <paramref name="osc"/> (its table's first and last frames; null: no table).</summary>
        public float[][] UserPartials(int osc) => Volatile.Read(ref requestedPartials[osc]);

        /// <summary>Any thread: slot <paramref name="osc"/>'s "User" sample from the next block (null: none).</summary>
        public void SetUserSample(int osc, SampleLayer layer, string name)
        {
            sampleName[osc] = name ?? "";
            Volatile.Write(ref requestedSample[osc], layer);
            Volatile.Write(ref userChanged, 1);
        }

        public SampleLayer UserSample(int osc) => Volatile.Read(ref requestedSample[osc]);
        public string UserSampleName(int osc) => sampleName[osc];

        /// <summary>Any thread: LFO <paramref name="lfo"/>'s drawn shape from the next block (null: the default).</summary>
        public void SetLfoShape(int lfo, DrawnShape shape)
        {
            Volatile.Write(ref requestedShape[lfo], shape ?? DrawnShape.Default);
            Volatile.Write(ref shapeChanged, 1);
        }

        /// <summary>LFO <paramref name="lfo"/>'s drawn shape (the one asked for last).</summary>
        public DrawnShape LfoShape(int lfo) => Volatile.Read(ref requestedShape[lfo]);
        public string UserTableName(int osc) => userName[osc];

        /// <summary>The table slot <paramref name="osc"/> plays now (null while a built-in one is still being built).</summary>
        public TableArena TableFor(int osc) => Bank.S.Osc[osc].Table;

        public int ActiveNotes => Bank.ActiveNotes;

        void Update(bool force)
        {
            bool userNew = Interlocked.Exchange(ref userChanged, 0) == 1;
            bool shapeNew = Interlocked.Exchange(ref shapeChanged, 0) == 1;
            if (shapeNew || force)
                for (int l = 0; l < requestedShape.Length; l++) Bank.S.Lfo[l].Drawn = Volatile.Read(ref requestedShape[l]);
            if (!force && !values.AnyDirty && !userNew && !tablesPending) return;
            if (userNew)
                for (int o = 0; o < userTable.Length; o++)
                {
                    userTable[o] = Volatile.Read(ref requestedUser[o]);
                    userPartials[o] = Volatile.Read(ref requestedPartials[o]);
                    userSample[o] = Volatile.Read(ref requestedSample[o]);
                }
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
            // The Analog engine's tables: the built-in Basic and PWM.
            var basic = TableBank.Get(0);
            var pulse = TableBank.Get(1);
            if (basic == null || pulse == null) pendingNow = true;
            Bank.Analog.Basic = basic ?? Bank.Analog.Basic;
            Bank.Analog.Pulse = pulse ?? Bank.Analog.Pulse;
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
                int a = AnalogBase[o];
                os.Shape = (AnalogShape)values.Choice(a);
                os.PulseWidth = values.F(a + (P.APw - P.AShape)) / 100f;
                os.Drift = values.F(a + (P.ADrift - P.AShape));
                int m = SampleBase[o];
                if (os.Type == SlotType.Sample || os.Type == SlotType.Granular)
                {
                    int src = values.Choice(m);
                    var layer = src >= SampleSources.Count ? userSample[o] ?? SampleSources.Get(0) : SampleSources.Get(src);
                    // Keep playing the previous sample until a newly chosen one is built.
                    if (layer == null) pendingNow = true;
                    else os.Source = layer;
                }
                os.Root = (int)values.Value(m + (P.ASmpRoot - P.ASmpSrc));
                os.Loop = (SampleLoop)values.Choice(m + (P.ASmpLoop - P.ASmpSrc));
                os.LoopStart = values.F(m + (P.ASmpLoopStart - P.ASmpSrc));
                os.LoopEnd = values.F(m + (P.ASmpLoopEnd - P.ASmpSrc));
                os.LoopFade = values.F(m + (P.ASmpFade - P.ASmpSrc)) / 1000f;
                os.Reverse = values.On(m + (P.ASmpRev - P.ASmpSrc));
                os.GrainSize = values.F(m + (P.AGrSize - P.ASmpSrc));
                os.GrainRate = values.F(m + (P.AGrRate - P.ASmpSrc));
                os.Spray = values.F(m + (P.AGrSpray - P.ASmpSrc));
                os.Scan = values.F(m + (P.AGrScan - P.ASmpSrc));
                os.GrainPitch = values.F(m + (P.AGrJitter - P.ASmpSrc));
                os.GrainShape = (GrainShape)values.Choice(m + (P.AGrShape - P.ASmpSrc));
                os.GrainReverse = values.F(m + (P.AGrRev - P.ASmpSrc));
                os.GrainWidth = values.F(m + (P.AGrWidth - P.ASmpSrc));
                int h = HarmBase[o];
                int setA = values.Choice(h), setB = values.Choice(h + (P.AHSet2 - P.AHSet));
                var user = userPartials[o];
                os.SetA = setA == (int)HarmonicSet.User ? user?[0] ?? HarmonicSets.Get(0) : HarmonicSets.Get(setA);
                os.SetB = setB == (int)HarmonicSet.User ? user?[1] ?? HarmonicSets.Get(1) : HarmonicSets.Get(setB);
                os.Partials = (int)values.Value(h + (P.AHPartials - P.AHSet));
                os.Tilt = values.F(h + (P.AHTilt - P.AHSet));
                os.OddEven = values.F(h + (P.AHOddEven - P.AHSet)) / 100f;
                os.Stretch = values.F(h + (P.AHStretch - P.AHSet)) / 100f;
                os.Damp = values.F(h + (P.AHDamp - P.AHSet)) / 100f;
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
            for (int e = 0; e < 2; e++)
            {
                var me = s.ModEnv[e];
                int b = EnvBase[e];
                me.Delay = values.F(b);
                me.A = values.F(b + (P.E1A - P.E1Delay));
                me.Hold = values.F(b + (P.E1Hold - P.E1Delay));
                me.D = values.F(b + (P.E1D - P.E1Delay));
                me.S = values.F(b + (P.E1S - P.E1Delay));
                me.R = values.F(b + (P.E1R - P.E1Delay));
                me.ACurve = values.F(b + (P.E1ACurve - P.E1Delay));
                me.DCurve = values.F(b + (P.E1DCurve - P.E1Delay));
                me.RCurve = values.F(b + (P.E1RCurve - P.E1Delay));
                me.Sync = values.On(b + (P.E1Sync - P.E1Delay));
            }
            for (int l = 0; l < 4; l++)
            {
                var lfo = s.Lfo[l];
                int b = LfoBase[l];
                lfo.Shape = (WaveShape)values.Choice(b);
                lfo.Rate = values.F(b + (P.L1Rate - P.L1Shape));
                lfo.Beats = SyncBeats[Math.Clamp(values.Choice(b + (P.L1Sync - P.L1Shape)), 0, SyncBeats.Length - 1)];
                lfo.Retrig = values.On(b + (P.L1Retrig - P.L1Shape));
                lfo.Phase = values.F(b + (P.L1Phase - P.L1Shape)) / 360f;
                lfo.Fade = values.F(b + (P.L1Fade - P.L1Shape));
            }
            for (int m = 0; m < 4; m++) s.Macro[m] = values.F(MacroParam[m]);
            for (int r = 0; r < s.Routes.Length; r++)
            {
                int b = P.R1Src + 3 * r;
                s.Routes[r].Source = (RouteSource)values.Choice(b);
                s.Routes[r].Dest = (RouteDest)values.Choice(b + 1);
                s.Routes[r].Amount = values.F(b + 2);
            }
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
            Bank.S.ModWheel = ModWheelValue;
            Bank.S.Aftertouch = AftertouchValue;
            Bank.S.Tempo = Tempo;
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
