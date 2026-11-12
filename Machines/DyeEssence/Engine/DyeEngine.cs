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
    internal sealed class DyeEngine : IChannelNoteSink, IDriverOut
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
        static readonly int[] ModalBase = { P.AMExc, P.BMExc, P.CMExc };
        readonly SampleLayer[] requestedSample = new SampleLayer[NoteBank.Oscillators];
        readonly SampleLayer[] userSample = new SampleLayer[NoteBank.Oscillators];
        readonly string[] sampleName = { "", "", "" };
        readonly DrawnShape[] requestedShape = { DrawnShape.Default, DrawnShape.Default, DrawnShape.Default, DrawnShape.Default };
        int userChanged, shapeChanged;
        bool tablesPending;
        float bend, bendRange = 2;
        // The arpeggiator and the step sequencer (§4.9), run on the song's grid; sub-blocks are rendered into scratch.
        readonly NoteDriver driver = new NoteDriver();
        readonly float[] segL = new float[MaxBlock], segR = new float[MaxBlock];
        SeqPattern requestedPattern = SeqPattern.Default;
        bool freeRunning;
        long freeSamples, freeAnchorSamples;
        double freeAnchorBeats, freeTempo = 120;
        bool usedSong;

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
        /// <summary>Where the song is (set before each block): the arpeggiator and the sequencer follow it while it plays, and a free clock otherwise.</summary>
        public DriverTransport Transport;
        /// <summary>The notes the arpeggiator or sequencer have sounding, and the keys it holds (for the editor and tests).</summary>
        internal NoteDriver Driver => driver;

        /// <summary>The sequencer's steps (any thread; the audio thread takes them at its next block).</summary>
        public void SetPattern(SeqPattern pattern) => Volatile.Write(ref requestedPattern, pattern ?? SeqPattern.Default);

        public SeqPattern Pattern => Volatile.Read(ref requestedPattern);

        /// <summary>For tests: sees every note the driver plays or stops, with <see cref="EventAt"/> the sample of the block it is on.</summary>
        internal IDriverOut Tap;
        internal int EventAt;

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
                int md = ModalBase[o];
                os.Exciter = values.Choice(md);
                os.Force = values.F(md + (P.AMForce - P.AMExc)) / 100f;
                os.Geometry = values.Choice(md + (P.AMGeom - P.AMExc));
                os.Modes = (int)values.Value(md + (P.AMModes - P.AMExc));
                os.Material = values.F(md + (P.AMMat - P.AMExc)) / 100f;
                os.Ring = values.F(md + (P.AMRing - P.AMExc));
                os.HfDamp = values.F(md + (P.AMHfDamp - P.AMExc)) / 100f;
                os.BodySize = values.F(md + (P.AMSize - P.AMExc)) / 100f;
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
            Bank.S.Mpe = values.On(P.MpeMode);
            Bank.S.MpeBendRange = (float)values.Value(P.MpeBend);
            UpdateDriver();
            bool any = driver.Running ? ProcessDriven(left, right, n) : Bank.Process(left, right, n);
            OutMeter.Feed(left, right, n);
            return any || OutMeter.Peak > 1e-6f;
        }

        /// <summary>The step length in beats: a rate (1/32 … a bar) and a feel (straight, dotted, triplet).</summary>
        internal static double StepBeats(int rate, int feel)
        {
            double b = rate switch { 0 => 0.125, 1 => 0.25, 2 => 0.5, 3 => 1, 4 => 2, _ => 4 };
            return feel == 1 ? b * 1.5 : feel == 2 ? b * (2.0 / 3.0) : b;
        }

        void UpdateDriver()
        {
            driver.SetModes((ArpMode)values.Choice(P.ArpMode), (SeqMode)values.Choice(P.SeqMode), this);
            driver.Latch = values.On(P.ArpLatch);
            driver.ArpOctaves = (int)values.Value(P.ArpOct);
            driver.ArpGate = values.F(P.ArpGate);
            driver.ArpSwing = values.F(P.ArpSwing);
            driver.ArpStep = StepBeats(values.Choice(P.ArpRate), values.Choice(P.ArpFeel));
            driver.SeqLength = (int)values.Value(P.SeqLength);
            driver.SeqStep = StepBeats(values.Choice(P.SeqRate), values.Choice(P.SeqFeel));
            driver.SeqSwing = values.F(P.SeqSwing);
            driver.Seed = (uint)values.Value(P.ArpSeed);
            driver.Pattern = Volatile.Read(ref requestedPattern);
            var s = Bank.S;
            // The sources glide over a share of the step (0: stepped); a step's length in seconds from the tempo.
            double tau = values.F(P.SeqSlew) * driver.SeqStep * 60.0 / Math.Max(1.0, Tempo);
            s.SeqTau = (float)tau;
            if (driver.Seq == SeqMode.Off) { s.SeqStep = 0; s.SeqVel = 0; s.SeqTau = 0; }
        }

        bool ProcessDriven(float[] left, float[] right, int n)
        {
            var tr = Transport;
            bool song = tr.Playing && tr.SamplesPerTick > 0 && tr.TicksPerBeat > 0;
            double beat0, spb;
            if (song)
            {
                beat0 = tr.Beats(0);
                spb = (double)tr.SamplesPerTick * tr.TicksPerBeat;
            }
            else
            {
                spb = 60.0 * Bank.SampleRate / Math.Max(1.0, Tempo);
                if (Tempo != freeTempo)
                {
                    freeAnchorBeats += (freeSamples - freeAnchorSamples) * (60.0 / Math.Max(1.0, freeTempo)) / Bank.SampleRate;
                    freeAnchorSamples = freeSamples;
                    freeTempo = Tempo;
                }
                beat0 = freeAnchorBeats + (freeSamples - freeAnchorSamples) / spb;
            }
            if (song != usedSong)
            {
                usedSong = song;
                driver.Resync(this);
            }
            driver.BeginBlock(beat0, spb, this);
            bool any = false;
            int cursor = 0;
            for (int guard = 0; guard < 8192; guard++)
            {
                int at = driver.NextEvent(beat0, spb, cursor, n);
                if (at >= n) break;
                if (at > cursor)
                {
                    any |= Segment(left, right, cursor, at, n);
                    cursor = at;
                }
                EventAt = at;
                driver.Fire(at, beat0, spb, this);
            }
            any |= Segment(left, right, cursor, n, n);
            if (!song) freeSamples += n;
            return any;
        }

        bool Segment(float[] left, float[] right, int from, int to, int n)
        {
            if (to <= from) return false;
            if (from == 0 && to == n) return Bank.Process(left, right, n);
            bool any = Bank.Process(segL, segR, to - from);
            Array.Copy(segL, 0, left, from, to - from);
            Array.Copy(segR, 0, right, from, to - from);
            return any;
        }

        // The driver's voices: through the allocator, as keys.
        void IDriverOut.Play(int key, int note, float velocity)
        {
            Tap?.Play(key, note, velocity);
            alloc.NoteOn(key, note, velocity);
        }

        void IDriverOut.Stop(int key)
        {
            Tap?.Stop(key);
            alloc.NoteOff(key);
        }

        void IDriverOut.SeqValues(float step, float velocity)
        {
            Tap?.SeqValues(step, velocity);
            Bank.S.SeqStep = step;
            Bank.S.SeqVel = velocity;
        }

        /// <summary>The first key of a phrase starts the free clock afresh (the steps then start on the first key's beat).</summary>
        void RestartFreeClock()
        {
            if (!driver.Intercepts || Transport.Playing) return;
            ResetClock();
        }

        /// <summary>The free clock (the one the steps follow while the song is stopped) starts again at beat 0.</summary>
        internal void ResetClock()
        {
            freeSamples = 0;
            freeAnchorSamples = 0;
            freeAnchorBeats = 0;
            freeTempo = Tempo;
            driver.Resync(this);
        }

        public void NoteOn(int key, int note, float velocity)
        {
            bool first = driver.HeldCount == 0;
            driver.Press(key, note, velocity);
            if (first) RestartFreeClock();
            if (!driver.Intercepts) alloc.NoteOn(key, note, velocity);
        }

        public void NoteOff(int key)
        {
            driver.Release(key);
            if (!driver.Intercepts) alloc.NoteOff(key);
        }

        public void ModWheel(float value) => ModWheelValue = value;
        public void PitchBend(float value) => bend = value;
        public void Sustain(bool down) => alloc.Sustain(down);
        public void Aftertouch(float value) => AftertouchValue = value;

        public void AllNotesOff(bool hard)
        {
            driver.Clear(this);
            alloc.AllNotesOff(hard);
        }

        // Live MPE (§4.9): MIDI notes keep their channel.
        const int MpeKeyBase = 4000;

        public void NoteOn(int key, int note, float velocity, int channel)
        {
            if (!values.On(P.MpeMode))
            {
                NoteOn(key, note, velocity);
                return;
            }
            int k = MpeKeyBase + 128 * channel + note;
            bool first = driver.HeldCount == 0;
            driver.Press(k, note, velocity);
            if (first) RestartFreeClock();
            if (driver.Intercepts) return;
            Bank.NextChannel = channel;
            alloc.NoteOn(k, note, velocity);
            Bank.NextChannel = -1;
        }

        public void NoteOff(int key, int note, int channel)
        {
            if (!values.On(P.MpeMode))
            {
                NoteOff(key);
                return;
            }
            NoteOff(MpeKeyBase + 128 * channel + note);
        }

        public bool Control(int channel, int ctrl, int value)
        {
            var s = Bank.S;
            bool member = values.On(P.MpeMode) && channel > 0;
            switch (ctrl)
            {
                case 74:
                    if (member) s.ChSlide[channel] = Math.Clamp(value, 0, 127) / 127f;
                    else s.Slide = Math.Clamp(value, 0, 127) / 127f;
                    return true;
                case 0xE0:
                case 0xFF:
                    if (!member) return false;
                    s.ChBend[channel] = Math.Clamp((value - 8192) / 8192f, -1f, 1f);
                    return true;
                case 0xD0:
                case 0xFC:
                    if (!member) return false;
                    s.ChPressure[channel] = Math.Clamp(value, 0, 127) / 127f;
                    return true;
            }
            return false;
        }

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
