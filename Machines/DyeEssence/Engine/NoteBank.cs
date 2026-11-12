#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4): the voice architecture, structure of arrays. Up to 32
// note slots, each with three engine slots (P1a: the wavetable engine) whose unison voices (1–16)
// are lanes of the oscillator, not voices of their own; a sub and a noise per note, rendered across
// notes; two filters per note (serial, parallel and anything between), run across notes; the amp
// and filter envelopes in closed form per tick; steal fades, glide. Voices run at 2× or 4× the
// sample rate (1× is kept for measuring): an IIR halfband per octave brings the mix down. Control
// values change every 16 samples of the base rate on a grid that runs across blocks and ramp across
// the tick, so blocks of any size give the same samples. No allocation after construction. Pure.
//
// Packing (§5.3): a note's oscillators with no warp share lane groups whatever their tables, levels
// and frames; a warped oscillator has groups of its own, after the oscillator that modulates it.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;
using NeoBuzz.Sampler;

namespace NeoBuzz.DyeEssence
{
    /// <summary>An oscillator's modulator for FM, PM, AM and Ring (append only).</summary>
    internal enum ModSource { OscA, OscB, OscC, Sub, Noise }

    internal enum SubShape { Sine, Triangle, Saw, Square }

    internal sealed class OscSettings
    {
        public SlotType Type = SlotType.Wavetable;
        public TableArena Table;
        /// <summary>Frame position 0 … 1.</summary>
        public float Position;
        /// <summary>Semitones from the note.</summary>
        public float Transpose;
        /// <summary>Linear level; pan −1 … 1; how much goes to filter 2 instead of filter 1, 0 … 1.</summary>
        public float Level = 1, Pan, ToF2;
        public int Unison = 1;
        /// <summary>Cents: the outermost unison voices sit this far either side of the note.</summary>
        public float Detune;
        /// <summary>Stereo spread of the unison voices, 0 … 1.</summary>
        public float Spread;
        /// <summary>Start phase in cycles, and how much of a random phase is added to it (0 … 1).</summary>
        public float Phase, Random = 1;
        public Warp Warp1, Warp2;
        public float Amount1, Amount2;
        public ModSource Mod = ModSource.OscB;
        /// <summary>The Analog engine: its wave, pulse width (0.03 … 0.5) and drift (0 … 1).</summary>
        public AnalogShape Shape = AnalogShape.Saw;
        public float PulseWidth = 0.5f, Drift;
        /// <summary>The Sample and Granular engines (§4.6): the sample, the note it sounds at its own pitch, its start and loop (shares of its length), its direction.</summary>
        public SampleLayer Source;
        public int Root = 60;
        public SampleLoop Loop;
        public float LoopStart, LoopEnd = 1;
        /// <summary>Seconds the loop's end fades into its start (0: a hard wrap).</summary>
        public float LoopFade;
        public bool Reverse;
        /// <summary>Granular: grain length (s), grains a second, spray (0 … 1 of the sample), scan speed (× the sample's own), pitch wander (cents), window, share played backwards, stereo scatter.</summary>
        public float GrainSize = 0.05f, GrainRate = 25, Spray = 0.1f, Scan, GrainPitch;
        public GrainShape GrainShape;
        public float GrainReverse, GrainWidth = 0.5f;
        /// <summary>The Harmonic engine (§4.7): partials sounding, tilt (dB an octave), odd/even (−1 … 1), stretch (−1 … 1), damp (0 … 1), and the two sets' levels (null: Saw and Square).</summary>
        public int Partials = 32;
        public float Tilt, OddEven, Stretch, Damp;
        public float[] SetA, SetB;
    }

    internal sealed class SourceSettings
    {
        public float Level, Pan, ToF2;
    }

    internal sealed class FilterSettings
    {
        public FilterType Type;
        /// <summary>Hz; resonance 0 … 1; drive as a gain (1: none); envelope, key and velocity in octaves (key: per octave played).</summary>
        public float Cutoff = 1000, Reso, Drive = 1, Env, Key, Vel;
    }

    internal sealed class BankSettings
    {
        /// <summary>2 or 4 (1: for measuring only): voices at the sample rate times this.</summary>
        public int Oversample = 2;
        public readonly OscSettings[] Osc = { new OscSettings(), new OscSettings(), new OscSettings() };
        public SubShape SubShape;
        /// <summary>0, −1 or −2: octaves from the note.</summary>
        public int SubOctave = -1;
        public readonly SourceSettings Sub = new SourceSettings(), Noise = new SourceSettings();
        /// <summary>0 dark … 1 white.</summary>
        public float NoiseColor = 1;
        public readonly FilterSettings[] Filter = { new FilterSettings { Type = FilterType.Ladder24, Cutoff = 4000 }, new FilterSettings() };
        /// <summary>0: filter 1 into filter 2 (serial); 1: side by side (parallel).</summary>
        public float Routing;
        public readonly EnvSettings Amp = new EnvSettings(), Flt = new EnvSettings { S = 0 };
        public float Volume = 0.25f, VelAmp = 0.7f;
        /// <summary>Glide time in seconds (0: none).</summary>
        public float Glide;
        /// <summary>Pitch bend now, in semitones.</summary>
        public float Bend;
        /// <summary>Modulation (§4.5): two envelopes, four LFOs, four macros (0 … 1), sixteen routes; the song's tempo; the wheel and pressure (0 … 1).</summary>
        public readonly ModEnvSettings[] ModEnv = { new ModEnvSettings(), new ModEnvSettings() };
        public readonly LfoSettings[] Lfo = { new LfoSettings(), new LfoSettings(), new LfoSettings(), new LfoSettings() };
        public readonly float[] Macro = new float[4];
        public readonly RouteSettings[] Routes = NewRoutes();
        public double Tempo = 120;
        public float ModWheel, Aftertouch;

        static RouteSettings[] NewRoutes()
        {
            var r = new RouteSettings[16];
            for (int i = 0; i < r.Length; i++) r[i] = new RouteSettings();
            return r;
        }
    }

    internal sealed partial class NoteBank
    {
        public const int Notes = 32;
        public const int Groups = Notes / 8;
        public const int Oscillators = 3;
        public const int MaxUnison = 16;
        /// <summary>A note's lanes, padding included: every oscillator's voices in whole groups at worst.</summary>
        public const int MaxLanes = Oscillators * MaxUnison;
        public const int MaxGroups = MaxLanes / 8;
        public const int ControlRate = 16;              // base-rate samples per control tick
        public const int MaxOversample = 4;
        public const int MaxTick = ControlRate * MaxOversample;
        public const int MaxBlock = 256;
        const double PhaseScale = 4294967296.0;          // 2^32
        const float FadeSeconds = 0.003f;

        static readonly IirHalfbandDesign Decimator = new IirHalfbandDesign(100, (28.0 - 20.0) / 96);
        /// <summary>What a lane with nothing to play reads (any index a lane can reach).</summary>
        static readonly float[] Silent = new float[8192 + 8];
        static readonly float[] Zero = new float[MaxTick * Notes];
        /// <summary>The sub's waves (one frame each, band-limited like every table).</summary>
        internal static readonly TableArena[] SubTables =
        {
            new TableArena(Wavetable.FromFunction("Sine", 1, (f, p) => PortableMath.Sin(2 * Math.PI * p))),
            new TableArena(Wavetable.FromFunction("Triangle", 1, (f, p) => p < 0.25 ? 4 * p : p < 0.75 ? 2 - 4 * p : 4 * p - 4)),
            new TableArena(Wavetable.FromFunction("Saw", 1, (f, p) => p < 0.5 ? 2 * p : 2 * p - 2)),
            new TableArena(Wavetable.FromFunction("Square", 1, (f, p) => p < 0.5 ? 1 : -1)),
        };

        public readonly BankSettings S = new BankSettings();

        /// <summary>The engines, by <see cref="SlotType"/> (Off has none).</summary>
        public readonly IEngine[] Engines = { null, new WavetableEngine(), new AnalogEngine(), new SampleEngine(), new GranularEngine(), new HarmonicEngine() };
        public AnalogEngine Analog => (AnalogEngine)Engines[(int)SlotType.Analog];

        IEngine EngineOf(OscSettings os) => (int)os.Type < Engines.Length ? Engines[(int)os.Type] : null;

        /// <summary>Has something to play (a silent one still renders when it modulates another).</summary>
        bool Plays(OscSettings os) => EngineOf(os)?.TableOf(os) != null;

        // Lanes: note n's from n · MaxLanes, its groups of eight in render order.
        readonly LaneState lanes = new LaneState(Notes * MaxLanes);
        // Per unison voice (note · MaxLanes + osc · MaxUnison + voice): its phase, its last gain targets
        // (rows of Notes · MaxLanes: F1 left, right, F2 left, right) and where it sits this tick.
        readonly uint[] voicePhase = new uint[Notes * MaxLanes];
        readonly float[] voiceGain = new float[4 * Notes * MaxLanes];
        readonly int[] slotOf = new int[Notes * MaxLanes];
        readonly int[] placedUnison = new int[Notes * Oscillators];
        readonly float[] voiceDrift = new float[Notes * MaxLanes];
        // Per note × oscillator this tick: its table read, for a group it fills alone (the uniform kernel).
        readonly float[][] oscData = new float[Notes * Oscillators][];
        readonly int[] oscOffA = new int[Notes * Oscillators], oscOffB = new int[Notes * Oscillators], oscLog2 = new int[Notes * Oscillators];
        readonly float[] oscMix = new float[Notes * Oscillators];
        readonly int[] groupUniform = new int[Notes * MaxGroups];   // −1: lanes of several oscillators or padding; else the oscillator
        // Per note, its groups this tick (note · MaxGroups + k).
        readonly int[] groupCount = new int[Notes];
        readonly int[] groupOsc = new int[Notes * MaxGroups];      // −1: plain voices of any oscillators; else the warped one
        readonly bool[] groupMorph = new bool[Notes * MaxGroups], groupToF2 = new bool[Notes * MaxGroups], groupStore = new bool[Notes * MaxGroups];
        readonly WarpSet[] warpSet = new WarpSet[Notes * Oscillators];
        readonly bool[] noteToF2 = new bool[Notes], noteSubMod = new bool[Notes], noteNoiseMod = new bool[Notes];
        readonly float[] acc = new float[MaxTick * UnisonKernel.AccStride];
        readonly float[] modBuf = new float[MaxTick * MaxLanes];

        // Per oscillator: the unison shape and transposition, kept against the settings they came from.
        readonly float[] ratio = new float[Oscillators * MaxUnison];
        readonly float[] panL = new float[Oscillators * MaxUnison], panR = new float[Oscillators * MaxUnison];
        readonly float[] topRatio = new float[Oscillators];
        readonly int[] shapeUnison = { -1, -1, -1 };
        readonly float[] shapeDetune = new float[Oscillators], shapeSpread = new float[Oscillators], shapePan = new float[Oscillators];
        readonly double[] oscRatio = new double[Oscillators];
        readonly float[] oscTranspose = { float.NaN, float.NaN, float.NaN };
        // Layout scratch (one note at a time).
        readonly int[] playU = new int[Oscillators], modOf = new int[Oscillators];
        readonly bool[] warped = new bool[Oscillators], placed = new bool[Oscillators], isMod = new bool[Oscillators];

        // Per note.
        readonly float[] noteKey = new float[Notes], noteTarget = new float[Notes], noteVel = new float[Notes];
        readonly int[] ampStage = new int[Notes], fltStage = new int[Notes];
        readonly float[] ampLevel = new float[Notes], fltLevel = new float[Notes];
        readonly bool[] gate = new bool[Notes];
        readonly float[] noteGain = new float[2 * Notes];   // the amp's gain per note, then its per-sample step
        readonly float[] fade = new float[Notes];
        readonly bool[] fading = new bool[Notes], pending = new bool[Notes], pendGate = new bool[Notes];
        readonly float[] pendKey = new float[Notes], pendVel = new float[Notes], pendFrom = new float[Notes];
        readonly bool[] live = new bool[Notes], pendingControl = new bool[Notes], snap = new bool[Notes];
        readonly uint[] seeds = new uint[Notes];

        // Modulation per note: the envelopes, the LFOs (phase, the random values either side, the time
        // since the note began), a random value, and the destinations this tick and the last.
        const int DestCount = (int)RouteDest.Count;
        readonly int[] envStage = new int[2 * Notes];
        readonly double[] envTime = new double[2 * Notes];
        readonly float[] envLevel = new float[2 * Notes], envFrom = new float[2 * Notes];
        readonly double[] lfoPhase = new double[4 * Notes], noteAge = new double[Notes];
        readonly float[] lfoHeld = new float[4 * Notes], lfoNext = new float[4 * Notes];
        readonly uint[] lfoSeed = new uint[Notes];
        readonly float[] noteRandom = new float[Notes];
        readonly float[] dest = new float[Notes * DestCount], destLast = new float[Notes * DestCount];
        readonly float[] srcValue = new float[(int)RouteSource.Count];
        readonly bool[] modMoved = new bool[Notes];
        // The free LFOs (retrig off): one for every note.
        readonly double[] freePhase = new double[4];
        readonly float[] freeHeld = new float[4], freeNext = new float[4], freeValue = new float[4];
        uint freeSeed = 0x6A09E667u;
        bool anyRoute;

        /// <summary>Destination <paramref name="d"/>'s modulation for a note (0 without routes).</summary>
        float Md(int note, RouteDest d) => dest[note * DestCount + (int)d];

        // The sub and the noise, notes as lanes.
        readonly LaneState sub = new LaneState(Notes);
        readonly float[] subBuf = new float[MaxTick * Notes], noiseBuf = new float[MaxTick * Notes];
        readonly float[] subGain = new float[8 * Notes], noiseGain = new float[8 * Notes];
        readonly uint[] noiseState = new uint[Notes];
        readonly float[] noiseLp = new float[Notes];
        float subPanL = 1, subPanR = 1, noisePanL = 1, noisePanR = 1, subPan, noisePan;
        bool subSend, noiseSend, subF2, noiseF2;
        float lastSubLevel, lastNoiseLevel, lastSubF2, lastNoiseF2;
        float noiseA = 1, noiseComp = 1;

        // Across notes: the filters' inputs and outputs, coefficients (now, target, per-sample step), states, comb lines.
        readonly float[] in1L = new float[MaxTick * Notes], in1R = new float[MaxTick * Notes];
        readonly float[] in2L = new float[MaxTick * Notes], in2R = new float[MaxTick * Notes];
        readonly float[] f1L = new float[MaxTick * Notes], f1R = new float[MaxTick * Notes];
        readonly float[] f2L = new float[MaxTick * Notes], f2R = new float[MaxTick * Notes];
        readonly float[][] coefCur = { new float[NoteFilters.Rows * Notes], new float[NoteFilters.Rows * Notes] };
        readonly float[][] coefStep = { new float[NoteFilters.Rows * Notes], new float[NoteFilters.Rows * Notes] };
        readonly float[][] coefTarget = { new float[NoteFilters.Rows * Notes], new float[NoteFilters.Rows * Notes] };
        readonly float[][] filterState = { new float[8 * Notes], new float[8 * Notes] };
        readonly float[][] combL = { new float[Notes * NoteFilters.CombLength], new float[Notes * NoteFilters.CombLength] };
        readonly float[][] combR = { new float[Notes * NoteFilters.CombLength], new float[Notes * NoteFilters.CombLength] };
        readonly int[] combWrite = new int[2];
        readonly FilterType[] type = { FilterType.Off, FilterType.Off };

        readonly float[] hiL = new float[MaxOversample * MaxBlock], hiR = new float[MaxOversample * MaxBlock];
        readonly IirHalfband decL = new IirHalfband(Decimator), decR = new IirHalfband(Decimator);
        // 4×: a first stage from four times the rate to twice it (the same design: what it passes
        // above 20 kHz the second stage takes out).
        readonly IirHalfband dec4L = new IirHalfband(Decimator), dec4R = new IirHalfband(Decimator);
        readonly float[] scratch = new float[96];
        float sampleRate = 48000;
        int controlCounter, oversample = 2, quietSamples;
        bool idle;
        LaneKind kind;
        // This tick's shared factors: the envelopes' and the glide's.
        double tickDt, ampKa, ampKd, ampKr, fltKa, fltKd, fltKr, glideK;
        float fadeStep;

        // Settings that glide across ticks (τ 4 ms) instead of stepping when a knob or automation
        // jumps: per oscillator its level, filter-2 share, position and warp amounts; per filter its
        // cutoff (in octaves), resonance and drive; the sub's and noise's levels and shares; the
        // routing; the volume.
        const float SmoothSeconds = 0.004f;
        const int SmOsc = 0, SmFilter = 5 * Oscillators, SmSub = SmFilter + 6, SmNoise = SmSub + 2, SmRouting = SmNoise + 2, SmVolume = SmRouting + 1, SmCount = SmVolume + 1;
        readonly float[] smTarget = new float[SmCount], smNow = new float[SmCount];
        bool smoothStarted;
        float smoothK = 1;

        // Steady ticks (P1a, §5.4): when nothing a note's voices depend on has moved since the last
        // tick (the settings, the smoothed values, the note's pitch), its lanes keep their layout, phases
        // and increments and only their ramps are stopped; a filter whose cutoff and resonance stand
        // still keeps its coefficients. Most ticks of a held note are steady.
        readonly float[] smLast = new float[SmCount];
        readonly object[] snapRef = new object[Oscillators];
        readonly float[] snapF = new float[Oscillators * 10 + 8];
        bool settingsMoved = true;
        readonly bool[] laid = new bool[Notes], rampsLive = new bool[Notes];
        readonly double[] lastHz = new double[Notes];
        readonly double[] filterKey = new double[2 * Notes];
        readonly bool[] filterRamps = new bool[2 * Notes];

        /// <summary>Whether any setting the voices read changed since the last tick (and keeps the new ones).</summary>
        bool SettingsMoved()
        {
            var s = S;
            bool moved = false;
            void F(int i, float v)
            {
                if (snapF[i] != v || float.IsNaN(snapF[i])) { snapF[i] = v; moved = true; }
            }
            for (int o = 0; o < Oscillators; o++)
            {
                var os = s.Osc[o];
                var table = EngineOf(os)?.TableOf(os);
                if (!ReferenceEquals(snapRef[o], table)) { snapRef[o] = table; moved = true; }
                int b = 10 * o;
                F(b, (int)os.Type); F(b + 1, os.Transpose); F(b + 2, os.Unison); F(b + 3, os.Detune); F(b + 4, os.Spread);
                F(b + 5, os.Pan); F(b + 6, (int)os.Warp1); F(b + 7, (int)os.Warp2); F(b + 8, (int)os.Mod); F(b + 9, os.Drift);
            }
            int g = 10 * Oscillators;
            F(g, s.SubOctave); F(g + 1, (int)s.SubShape); F(g + 2, s.Sub.Pan); F(g + 3, s.Noise.Pan); F(g + 4, oversample);
            for (int i = 0; i < SmCount; i++)
                if (smLast[i] != smNow[i]) { smLast[i] = smNow[i]; moved = true; }
            return moved;
        }

        float Sm(int i) => smNow[i];
        float OscLevel(int o) => smNow[SmOsc + 5 * o];
        float OscToF2(int o) => smNow[SmOsc + 5 * o + 1];
        float OscPosition(int o) => smNow[SmOsc + 5 * o + 2];
        float OscAmount(int o, int w) => smNow[SmOsc + 5 * o + 3 + w];

        /// <summary>Once a tick: every smoothed setting a step toward where the settings are now.</summary>
        void Smooth()
        {
            var s = S;
            for (int o = 0; o < Oscillators; o++)
            {
                var os = s.Osc[o];
                int b = SmOsc + 5 * o;
                smTarget[b] = Math.Max(0f, os.Level);
                smTarget[b + 1] = DspMath.Clamp(os.ToF2, 0f, 1f);
                smTarget[b + 2] = DspMath.Clamp(EngineOf(os)?.PositionOf(os) ?? 0f, 0f, 1f);
                smTarget[b + 3] = DspMath.Clamp(os.Amount1, 0f, 1f);
                smTarget[b + 4] = DspMath.Clamp(os.Amount2, 0f, 1f);
            }
            for (int f = 0; f < 2; f++)
            {
                var fs = s.Filter[f];
                smTarget[SmFilter + 3 * f] = (float)PortableMath.Log2(Math.Max(1f, fs.Cutoff));
                smTarget[SmFilter + 3 * f + 1] = DspMath.Clamp(fs.Reso, 0f, 1f);
                smTarget[SmFilter + 3 * f + 2] = Math.Max(1f, fs.Drive);
            }
            smTarget[SmSub] = Math.Max(0f, s.Sub.Level);
            smTarget[SmSub + 1] = DspMath.Clamp(s.Sub.ToF2, 0f, 1f);
            smTarget[SmNoise] = Math.Max(0f, s.Noise.Level);
            smTarget[SmNoise + 1] = DspMath.Clamp(s.Noise.ToF2, 0f, 1f);
            smTarget[SmRouting] = DspMath.Clamp(s.Routing, 0f, 1f);
            smTarget[SmVolume] = Math.Max(0f, s.Volume);
            for (int i = 0; i < SmCount; i++)
            {
                float t = smTarget[i], now = smNow[i];
                if (!smoothStarted || now == t) { smNow[i] = t; continue; }
                now += (t - now) * smoothK;
                // Arrived (to within a part in 10⁵): exactly there, so a level that goes to 0 is 0.
                smNow[i] = MathF.Abs(t - now) <= 1e-5f * Math.Max(1f, MathF.Abs(t)) ? t : now;
            }
            smoothStarted = true;
        }

        public NoteBank(LaneKind lanes = LaneKind.Auto)
        {
            for (int n = 0; n < Notes; n++)
            {
                seeds[n] = 0x9E3779B9u * (uint)(n + 1);
                noiseState[n] = 0x2545F491u * (uint)(n + 3) | 1u;
            }
            for (int i = 0; i < this.lanes.Total; i++) Quiet(this.lanes, i);
            for (int i = 0; i < Notes; i++) Quiet(sub, i);
            Lanes = lanes;
            SetSampleRate(48000);
        }

        /// <summary>The lane implementation in use (Auto picks AVX2 where the CPU has it, else two Vector128 halves).</summary>
        public LaneKind Lanes
        {
            get => kind;
            set => kind = value == LaneKind.Auto ? (Avx2Lanes.Supported ? LaneKind.Avx2 : LaneKind.Pair)
                : value == LaneKind.Avx2 && !Avx2Lanes.Supported ? LaneKind.Pair : value;
        }

        public float SampleRate => sampleRate;
        float VoiceRate => sampleRate * oversample;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            fadeStep = ControlRate / Math.Max(1f, FadeSeconds * sr);
            smoothK = (float)(1.0 - PortableMath.Exp(-ControlRate / (SmoothSeconds * sr)));
        }

        /// <summary>Making sound, or about to (a stolen slot with its next note waiting).</summary>
        public bool Active(int slot) => ampStage[slot] != NoteEnvelope.Idle || pending[slot];

        /// <summary>The amp envelope's level now (steal priority).</summary>
        public float Loudness(int slot) => ampLevel[slot];

        /// <summary>Grains sounding now (live and fading), across every note and slot: the cap's figure for the tests.</summary>
        public int ActiveGrains
        {
            get
            {
                int n = 0;
                for (int g = 0; g < grains.Length; g++) if (grains[g].On) n++;
                return n;
            }
        }

        public int ActiveNotes
        {
            get
            {
                int c = 0;
                for (int n = 0; n < Notes; n++) if (Active(n)) c++;
                return c;
            }
        }

        /// <summary>The lane groups note <paramref name="slot"/> rendered at its last tick (what packing saves).</summary>
        public int GroupsOf(int slot) => groupCount[slot];

        /// <summary>
        /// Starts note slot <paramref name="slot"/> at MIDI <paramref name="key"/>, velocity 0 … 1,
        /// gliding from <paramref name="glideFrom"/> (NaN: none). A sounding slot fades out over 3 ms first.
        /// </summary>
        public void NoteOn(int slot, float key, float velocity, float glideFrom = float.NaN)
        {
            if (Active(slot))
            {
                pending[slot] = true;
                pendGate[slot] = true;
                pendKey[slot] = key;
                pendVel[slot] = velocity;
                pendFrom[slot] = glideFrom;
                if (!fading[slot])
                {
                    fading[slot] = true;
                    fade[slot] = 1f;
                }
                return;
            }
            Begin(slot, key, velocity, glideFrom, true);
        }

        /// <summary>A new pitch without a new attack (legato), gliding there when <paramref name="glide"/>.</summary>
        public void Legato(int slot, float key, bool glide)
        {
            if (pending[slot])
            {
                pendKey[slot] = key;
                return;
            }
            noteTarget[slot] = key;
            if (!glide) noteKey[slot] = key;
        }

        public void NoteOff(int slot)
        {
            if (pending[slot]) pendGate[slot] = false;
            else gate[slot] = false;
        }

        /// <summary>Silences a slot within 3 ms.</summary>
        public void Kill(int slot)
        {
            pending[slot] = false;
            if (Active(slot) && !fading[slot])
            {
                fading[slot] = true;
                fade[slot] = 1f;
            }
        }

        void Begin(int slot, float key, float velocity, float glideFrom, bool held)
        {
            var s = S;
            noteTarget[slot] = key;
            noteKey[slot] = float.IsNaN(glideFrom) ? key : glideFrom;
            noteVel[slot] = velocity;
            ampStage[slot] = fltStage[slot] = NoteEnvelope.Attack;
            ampLevel[slot] = fltLevel[slot] = 0;
            gate[slot] = held;
            fading[slot] = pending[slot] = false;
            fade[slot] = 1f;
            // Each voice from the oscillator's start phase plus its share of a seeded random one (the same on every run).
            var rng = new Rng(seeds[slot]);
            int nb = slot * MaxLanes;
            for (int o = 0; o < Oscillators; o++)
            {
                var os = s.Osc[o];
                double start = (os.Phase - Math.Floor(os.Phase)) * PhaseScale;
                double random = Math.Clamp(os.Random, 0f, 1f);
                for (int l = 0; l < MaxUnison; l++)
                    voicePhase[nb + o * MaxUnison + l] = unchecked((uint)(long)(start + random * rng.NextUInt()));
                placedUnison[slot * Oscillators + o] = 0;
            }
            laid[slot] = false;
            ExtBegin(slot);
            // Each voice's own fixed detune for engines that drift, from a stream of its own (the phases
            // above stay as they were).
            var drift = new Rng(seeds[slot] ^ 0x5BD1E995u);
            for (int i = 0; i < MaxLanes; i++) voiceDrift[nb + i] = drift.NextBipolar();
            seeds[slot] = rng.NextUInt();
            int v = Notes * MaxLanes;
            for (int r = 0; r < 4; r++) Array.Clear(voiceGain, r * v + nb, MaxLanes);
            noteGain[slot] = noteGain[Notes + slot] = 0;
            sub.Phase[slot] = 0;
            noiseLp[slot] = 0;
            for (int r = 0; r < 8; r++) subGain[r * Notes + slot] = noiseGain[r * Notes + slot] = 0;
            for (int f = 0; f < 2; f++)
            {
                for (int r = 0; r < 8; r++) filterState[f][r * Notes + slot] = 0;
                if (NoteFilters.IsComb(type[f]))
                {
                    Array.Clear(combL[f], slot * NoteFilters.CombLength, NoteFilters.CombLength);
                    Array.Clear(combR[f], slot * NoteFilters.CombLength, NoteFilters.CombLength);
                }
            }
            // Modulation starts with the note: envelopes from their delay, LFOs at their phase.
            var mr = new Rng(seeds[slot] ^ 0x9B05688Cu);
            for (int e = 0; e < 2; e++)
            {
                envStage[2 * slot + e] = ModEnvelope.Delay;
                envTime[2 * slot + e] = 0;
                envLevel[2 * slot + e] = envFrom[2 * slot + e] = 0;
            }
            for (int l = 0; l < 4; l++)
            {
                lfoPhase[4 * slot + l] = s.Lfo[l].Phase - Math.Floor(s.Lfo[l].Phase);
                lfoHeld[4 * slot + l] = mr.NextBipolar();
                lfoNext[4 * slot + l] = mr.NextBipolar();
            }
            lfoSeed[slot] = mr.NextUInt() | 1u;
            noteRandom[slot] = mr.NextBipolar();
            noteAge[slot] = 0;
            Array.Clear(dest, slot * DestCount, DestCount);
            snap[slot] = true;
            pendingControl[slot] = true;
        }

        /// <summary>Renders a block at the base rate (overwrites the buffers). Whether anything sounded.</summary>
        public bool Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            return kind switch
            {
                LaneKind.Avx2 => Process<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(left, right, n),
                LaneKind.Scalar => Process<ScalarLanes, Eight<float>, Eight<uint>>(left, right, n),
                _ => Process<PairLanes, Pair<float>, Pair<uint>>(left, right, n),
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        bool Process<TL, TF, TU>(float[] left, float[] right, int n)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            // The voice rate changes at a block's start, with a new tick there (a quality change is a
            // parameter change: it lands on a block boundary like any other).
            int want = S.Oversample >= 4 ? 4 : S.Oversample <= 1 ? 1 : 2;
            if (want != oversample)
            {
                oversample = want;
                decL.Reset(); decR.Reset(); dec4L.Reset(); dec4R.Reset();
                controlCounter = 0;
            }
            if (idle)
            {
                bool wake = false;
                for (int note = 0; note < Notes && !wake; note++) wake = Active(note);
                if (!wake)
                {
                    // Resting (nothing has sounded for a while): only the grid's ticks run, for the
                    // settings' glides, and the output is silence.
                    for (int at = 0; at < n;)
                    {
                        if (controlCounter <= 0)
                        {
                            controlCounter = ControlRate;
                            Tick();
                        }
                        int step = Math.Min(controlCounter, n - at);
                        controlCounter -= step;
                        at += step;
                    }
                    Array.Clear(left, 0, n);
                    Array.Clear(right, 0, n);
                    return false;
                }
                idle = false;
            }
            int os = oversample;
            Array.Clear(hiL, 0, os * n);
            Array.Clear(hiR, 0, os * n);
            bool any = false;
            int pos = 0;
            while (pos < n)
            {
                if (controlCounter <= 0)
                {
                    controlCounter = ControlRate;
                    Tick();
                }
                for (int note = 0; note < Notes; note++)
                    if (pendingControl[note])
                    {
                        // Started mid-tick: its control values for the rest of the tick.
                        live[note] = true;
                        Control(note, os * controlCounter);
                    }
                int m = Math.Min(controlCounter, n - pos);
                any |= Render<TL, TF, TU>(os * pos, os * m);
                controlCounter -= m;
                pos += m;
            }
            if (os == 4)
            {
                for (int i = 0; i < 2 * n; i++)
                {
                    hiL[i] = dec4L.Down(hiL[2 * i], hiL[2 * i + 1]);
                    hiR[i] = dec4R.Down(hiR[2 * i], hiR[2 * i + 1]);
                }
                dec4L.Flush();
                dec4R.Flush();
            }
            if (os >= 2)
            {
                for (int i = 0; i < n; i++)
                {
                    left[i] = decL.Down(hiL[2 * i], hiL[2 * i + 1]);
                    right[i] = decR.Down(hiR[2 * i], hiR[2 * i + 1]);
                }
                decL.Flush();
                decR.Flush();
            }
            else
            {
                Array.Copy(hiL, left, n);
                Array.Copy(hiR, right, n);
            }
            for (int f = 0; f < 2; f++)
            {
                var st = filterState[f];
                for (int i = 0; i < st.Length; i++) st[i] = DspMath.Flush(st[i]);
            }
            // After 100 ms with nothing sounding the decimators' tails are long gone: rest.
            quietSamples = any ? 0 : quietSamples + n;
            if (quietSamples >= sampleRate / 10)
            {
                idle = true;
                quietSamples = 0;
                decL.Reset(); decR.Reset(); dec4L.Reset(); dec4R.Reset();
            }
            return any;
        }

        /// <summary>At each tick of the grid: what the settings decide for every note, then each sounding note's values.</summary>
        void Tick()
        {
            var s = S;
            Smooth();
            GrainShare();
            settingsMoved = SettingsMoved();
            tickDt = ControlRate / (double)sampleRate;
            NoteEnvelope.Factors(s.Amp, tickDt, out ampKa, out ampKd, out ampKr);
            NoteEnvelope.Factors(s.Flt, tickDt, out fltKa, out fltKd, out fltKr);
            glideK = GlideFactor(tickDt);
            anyRoute = false;
            foreach (var r in s.Routes) anyRoute |= r.Source != RouteSource.Off && r.Dest != RouteDest.Off && r.Amount != 0;
            for (int l = 0; l < 4; l++)
            {
                var lfo = s.Lfo[l];
                if (lfo.Retrig) continue;
                double p = freePhase[l] + LfoRate(lfo, 0f) * tickDt;
                if (p >= 1)
                {
                    p -= Math.Floor(p);
                    freeHeld[l] = freeNext[l];
                    var rng = new Rng(freeSeed);
                    freeNext[l] = rng.NextBipolar();
                    freeSeed = rng.NextUInt() | 1u;
                }
                freePhase[l] = p;
                freeValue[l] = LfoWave.Value(lfo.Shape, p, freeHeld[l], freeNext[l], lfo.Drawn);
            }
            for (int f = 0; f < 2; f++)
            {
                var t = s.Filter[f].Type;
                if (t == type[f]) continue;
                // A new model: its own state from zero, its coefficients set at once (no ramp from another model's).
                type[f] = t;
                Array.Clear(filterState[f]);
                if (NoteFilters.IsComb(t))
                {
                    Array.Clear(combL[f]);
                    Array.Clear(combR[f]);
                }
                for (int n = 0; n < Notes; n++) snap[n] = true;
            }
            // The sub and the noise: sent while they have a level (and the tick after, to ramp out).
            if (s.Sub.Pan != subPan) { subPan = s.Sub.Pan; DspMath.PanGains(subPan, out subPanL, out subPanR); }
            if (s.Noise.Pan != noisePan) { noisePan = s.Noise.Pan; DspMath.PanGains(noisePan, out noisePanL, out noisePanR); }
            subSend = Sm(SmSub) > 0 || lastSubLevel > 0;
            noiseSend = Sm(SmNoise) > 0 || lastNoiseLevel > 0;
            subF2 = subSend && (Sm(SmSub + 1) > 0 || lastSubF2 > 0);
            noiseF2 = noiseSend && (Sm(SmNoise + 1) > 0 || lastNoiseF2 > 0);
            lastSubLevel = Sm(SmSub); lastNoiseLevel = Sm(SmNoise);
            lastSubF2 = Sm(SmSub + 1); lastNoiseF2 = Sm(SmNoise + 1);
            float color = Math.Clamp(s.NoiseColor, 0f, 1f);
            if (color >= 0.999f) { noiseA = 1; noiseComp = 1; }
            else
            {
                // A one-pole lowpass from 100 Hz (dark) to 20 kHz, its output kept at white noise's power.
                double fc = 100.0 * PortableMath.Pow(200.0, color);
                noiseA = (float)(1.0 - PortableMath.Exp(-2.0 * Math.PI * fc / VoiceRate));
                noiseComp = MathF.Sqrt((2f - noiseA) / noiseA);
            }
            for (int note = 0; note < Notes; note++)
            {
                if (fading[note] && fade[note] <= 0)
                {
                    // A steal or a kill has faded out: start the waiting note, or rest.
                    fading[note] = false;
                    if (pending[note]) Begin(note, pendKey[note], pendVel[note], pendFrom[note], pendGate[note]);
                    else
                    {
                        ampStage[note] = fltStage[note] = NoteEnvelope.Idle;
                        ampLevel[note] = fltLevel[note] = 0;
                    }
                }
                bool was = live[note];
                live[note] = Active(note);
                if (live[note]) Control(note, oversample * ControlRate);
                else if (was) Quiet(note);
            }
        }

        double GlideFactor(double dt) => S.Glide <= 0.0005f ? 0.0 : PortableMath.Exp(-3.0 * dt / S.Glide);

        /// <summary>A note that has stopped: nothing of it ramps on (its lanes in a sounding group stay still and silent).</summary>
        void Quiet(int note)
        {
            noteGain[note] = noteGain[Notes + note] = 0;
            for (int f = 0; f < 2; f++)
                for (int r = 0; r < NoteFilters.Rows; r++) coefStep[f][r * Notes + note] = 0;
            for (int r = 4; r < 8; r++) subGain[r * Notes + note] = noiseGain[r * Notes + note] = 0;
            groupCount[note] = 0;
            extAny[note] = extToF2[note] = false;
        }

        static void Quiet(LaneState s, int at)
        {
            s.Data[at] = Silent;
            s.OffsetA[at] = s.OffsetB[at] = 0;
            s.FrameMix[at] = 0;
            s.Inc[at] = 0;
            s.ShiftR[at] = 24;   // a 256-sample read: never a shift of 32
            s.ShiftL[at] = 8;
            s.ModSlot[at] = 0;
            for (int r = 0; r < LaneState.GainRows; r++) s.Gain[r * s.Total + at] = 0;
        }

        /// <summary>The control values of one note for a tick of <paramref name="count"/> voice-rate samples.</summary>
        void Control(int note, int count)
        {
            var s = S;
            pendingControl[note] = false;
            double dt = count / (double)VoiceRate;
            double ka = ampKa, kd = ampKd, kr = ampKr, fa = fltKa, fd = fltKd, fr = fltKr, kg = glideK;
            if (count != oversample * ControlRate)
            {
                NoteEnvelope.Factors(s.Amp, dt, out ka, out kd, out kr);
                NoteEnvelope.Factors(s.Flt, dt, out fa, out fd, out fr);
                kg = GlideFactor(dt);
            }
            float amp = NoteEnvelope.Advance(ref ampStage[note], ref ampLevel[note], gate[note], s.Amp, dt, ka, kd, kr);
            float fe = NoteEnvelope.Advance(ref fltStage[note], ref fltLevel[note], gate[note], s.Flt, dt, fa, fd, fr);
            Modulate(note, dt, amp, fe);
            if (noteKey[note] != noteTarget[note])
            {
                float k = noteTarget[note] + (noteKey[note] - noteTarget[note]) * (float)kg;
                noteKey[note] = MathF.Abs(k - noteTarget[note]) < 1e-4f ? noteTarget[note] : k;
            }
            float f = 1f;
            if (fading[note])
            {
                fade[note] = Math.Max(0f, fade[note] - fadeStep * count / (oversample * ControlRate));
                f = fade[note];
            }
            float invCount = 1f / count;
            float vel = noteVel[note];
            float target = ampStage[note] == NoteEnvelope.Idle ? 0f : amp * Sm(SmVolume) * (1f - s.VelAmp + s.VelAmp * vel) * f * Math.Max(0f, 1f + Md(note, RouteDest.Amp));
            noteGain[Notes + note] = (target - noteGain[note]) * invCount;

            double noteHz = DspMath.NoteToHz(noteKey[note] + s.Bend + 12f * Md(note, RouteDest.Pitch));
            double vr = VoiceRate;
            // Mip levels: the plain ones keep harmonics under 44 kHz (the decimator takes 20–24 kHz
            // out); a warped one allows for how fast its warps read, up to the voice rate's Nyquist.
            double limitPlain = Math.Min(44000.0, vr * 0.5 - 100) / vr;
            double limitWarp = (vr * 0.5 - 100) / vr;
            double subRatio = s.SubOctave <= -2 ? 0.25 : s.SubOctave == -1 ? 0.5 : 1.0;
            if (laid[note] && !settingsMoved && !modMoved[note] && noteHz == lastHz[note])
            {
                // Steady: stop the ramps once (each gain exactly at its target), then nothing to do.
                if (rampsLive[note]) Settle(note);
            }
            else
            {
                Voices(note, invCount, noteHz, vr, limitPlain, limitWarp, subRatio);
                Sources(note, invCount, noteHz * subRatio / vr, limitPlain);
                lastHz[note] = noteHz;
                laid[note] = true;
                rampsLive[note] = true;
            }
            ExtControl(note, invCount, noteHz, vr);
            Filters(note, invCount, fe, vr);
            snap[note] = false;
        }

        /// <summary>Lays a note's voices out in lane groups for this tick and sets every lane.</summary>
        void Voices(int note, float invCount, double noteHz, double vr, double limitPlain, double limitWarp, double subRatio)
        {
            var s = S;
            int nb = note * MaxLanes, gb = note * MaxGroups, v = Notes * MaxLanes, total = lanes.Total;
            // Each voice's phase, from where it sat last tick.
            for (int o = 0; o < Oscillators; o++)
            {
                int was = placedUnison[note * Oscillators + o];
                for (int l = 0; l < was; l++)
                    voicePhase[nb + o * MaxUnison + l] = lanes.Phase[nb + slotOf[nb + o * MaxUnison + l]];
            }
            for (int o = 0; o < Oscillators; o++)
            {
                var os = s.Osc[o];
                int u = Plays(os) ? Math.Clamp(os.Unison, 1, MaxUnison) : 0;
                playU[o] = u;
                warped[o] = u > 0 && (os.Warp1 != Warp.None || os.Warp2 != Warp.None);
                if (u > 0) Shape(o, u, os);
            }
            for (int o = 0; o < Oscillators; o++)
            {
                modOf[o] = -1;
                var os = s.Osc[o];
                if (!warped[o] || !(Warps.UsesModulator(os.Warp1) || Warps.UsesModulator(os.Warp2))) continue;
                int m = (int)os.Mod;
                if (m >= Oscillators) modOf[o] = m;               // the sub or the noise
                else if (m != o && playU[m] > 0) modOf[o] = m;
            }
            // A silent oscillator renders only as another's modulator.
            for (int o = 0; o < Oscillators; o++) isMod[o] = false;
            for (int o = 0; o < Oscillators; o++) if (modOf[o] >= 0 && modOf[o] < Oscillators) isMod[modOf[o]] = true;
            for (int o = 0; o < Oscillators; o++)
            {
                if (playU[o] > 0 && !isMod[o] && Level(note, o) <= 0) playU[o] = 0;
                // Voices that stop: the next time they play they ramp in from silence.
                for (int l = playU[o], was = placedUnison[note * Oscillators + o]; l < was; l++)
                    for (int r = 0; r < 4; r++) voiceGain[r * v + nb + o * MaxUnison + l] = 0;
            }

            // Plain voices packed from lane 0; then each warped oscillator in whole groups, after its modulator.
            // Oscillators that fill whole groups first, so their groups take the uniform kernel.
            int slot = 0;
            for (int o = 0; o < Oscillators; o++) placed[o] = false;
            for (int pass = 0; pass < 2; pass++)
                for (int o = 0; o < Oscillators; o++)
                {
                    if (placed[o] || playU[o] == 0 || warped[o] || (pass == 0) != (playU[o] % 8 == 0)) continue;
                    for (int l = 0; l < playU[o]; l++) slotOf[nb + o * MaxUnison + l] = slot++;
                    placed[o] = true;
                }
            int groups = (slot + 7) >> 3;
            for (int k = 0; k < groups; k++) groupOsc[gb + k] = -1;
            while (true)
            {
                bool progress = true;
                while (progress)
                {
                    progress = false;
                    for (int o = 0; o < Oscillators; o++)
                        if (warped[o] && !placed[o] && (modOf[o] < 0 || modOf[o] >= Oscillators || placed[modOf[o]]))
                        {
                            groups = Place(note, o, groups);
                            progress = true;
                        }
                }
                int left = -1;
                for (int o = 0; o < Oscillators && left < 0; o++) if (warped[o] && !placed[o]) left = o;
                if (left < 0) break;
                // A ring of modulators: the first goes without its modulator.
                modOf[left] = -1;
                groups = Place(note, left, groups);
            }
            groupCount[note] = groups;
            for (int o = 0; o < Oscillators; o++) isMod[o] = false;
            noteSubMod[note] = noteNoiseMod[note] = false;
            for (int o = 0; o < Oscillators; o++)
            {
                if (modOf[o] >= 0 && modOf[o] < Oscillators) isMod[modOf[o]] = true;
                if (modOf[o] == Oscillators) noteSubMod[note] = true;
                if (modOf[o] == Oscillators + 1) noteNoiseMod[note] = true;
            }
            for (int k = 0; k < groups; k++)
            {
                groupMorph[gb + k] = groupToF2[gb + k] = groupStore[gb + k] = false;
                groupUniform[gb + k] = -1;
            }
            // Only the padding lanes need quieting: every other lane is set below.
            int used = 0;
            for (int o = 0; o < Oscillators; o++) if (playU[o] > 0 && !warped[o]) used += playU[o];
            for (int i = used; i < 8 * ((used + 7) >> 3); i++) Quiet(lanes, nb + i);
            for (int o = 0; o < Oscillators; o++)
            {
                if (playU[o] == 0 || !warped[o]) continue;
                int first = slotOf[nb + o * MaxUnison];
                for (int i = first + playU[o]; i < first + 8 * ((playU[o] + 7) >> 3); i++) Quiet(lanes, nb + i);
            }

            bool toF2 = false;
            for (int o = 0; o < Oscillators; o++)
            {
                int u = playU[o];
                placedUnison[note * Oscillators + o] = u;
                if (u == 0) continue;
                var os = s.Osc[o];
                var engine = EngineOf(os);
                var table = engine.TableOf(os);
                double baseInc = noteHz * oscRatio[o] / vr;
                float pitchMod = Md(note, RouteDest.APitch + o);
                if (pitchMod != 0) baseInc *= PortableMath.Exp2(pitchMod);   // ±12 st at full amount
                double top = Math.Min(baseInc * topRatio[o], 0.49);
                int level;
                if (!warped[o]) level = Wavetable.LevelFor(top, limitPlain);
                else
                {
                    int m = modOf[o];
                    double modHz = m < 0 || m == Oscillators + 1 ? 0 : m == Oscillators ? noteHz * subRatio : noteHz * oscRatio[m] * topRatio[m];
                    float modRatio = (float)(modHz / Math.Max(1e-9, noteHz * oscRatio[o]));
                    double speed = Warps.Speed(os.Warp1, Amount(note, o, 0), modRatio) * Warps.Speed(os.Warp2, Amount(note, o, 1), modRatio);
                    level = Wavetable.LevelFor(top * speed, limitWarp);
                    SetWarps(ref warpSet[note * Oscillators + o], os, Amount(note, o, 0), Amount(note, o, 1), m, TableArena.Log2(level));
                }
                // The frame pair for this tick, shared by every voice.
                float fpos = Math.Clamp(OscPosition(o) + Md(note, RouteDest.APosition + o), 0f, 1f) * (table.FrameCount - 1);
                int f0 = (int)fpos;
                float ff = fpos - f0;
                // On a frame (to within 10⁻⁴ of one): that frame alone, no morph to pay for.
                if (ff > 0.9999f)
                {
                    f0++;
                    ff = 0;
                }
                else if (ff < 1e-4f) ff = 0;
                if (f0 >= table.FrameCount - 1)
                {
                    f0 = table.FrameCount - 1;
                    ff = 0;
                }
                int offA = TableArena.Offset(f0, level), offB = ff > 0 ? TableArena.Offset(f0 + 1, level) : offA;
                uint log2 = (uint)TableArena.Log2(level);
                float norm = Level(note, o) / MathF.Sqrt(u);
                float to2 = OscToF2(o), to1 = 1f - to2;
                bool f2 = to2 > 0;
                for (int l = 0; l < u; l++)
                {
                    int canon = nb + o * MaxUnison + l, at = nb + slotOf[canon];
                    lanes.Phase[at] = voicePhase[canon];
                    float cents = engine.DriftCents(os, voiceDrift[canon]);
                    double li = baseInc * ratio[o * MaxUnison + l];
                    if (cents != 0) li *= PortableMath.Exp2(cents / 1200.0);
                    lanes.Inc[at] = (uint)(Math.Min(li, 0.49) * PhaseScale);
                    lanes.ShiftR[at] = 32 - log2;
                    lanes.ShiftL[at] = log2;
                    lanes.Data[at] = table.Data;
                    lanes.OffsetA[at] = offA;
                    lanes.OffsetB[at] = offB;
                    lanes.FrameMix[at] = ff;
                    if (modOf[o] >= 0 && modOf[o] < Oscillators)
                    {
                        int m = modOf[o];
                        lanes.ModSlot[at] = (uint)slotOf[nb + m * MaxUnison + l % playU[m]];
                    }
                    float pl = norm * panL[o * MaxUnison + l], pr = norm * panR[o * MaxUnison + l];
                    Ramp(at, canon, 0, pl * to1, invCount, total, v);
                    Ramp(at, canon, 1, pr * to1, invCount, total, v);
                    // To filter 2 while it sends there or ramps out of it.
                    if (voiceGain[2 * v + canon] != 0 || voiceGain[3 * v + canon] != 0) f2 = true;
                    Ramp(at, canon, 2, pl * to2, invCount, total, v);
                    Ramp(at, canon, 3, pr * to2, invCount, total, v);
                }
                int no = note * Oscillators + o;
                oscData[no] = table.Data;
                oscOffA[no] = offA;
                oscOffB[no] = offB;
                oscMix[no] = ff;
                oscLog2[no] = (int)log2;
                int k0 = slotOf[nb + o * MaxUnison] >> 3, k1 = slotOf[nb + o * MaxUnison + u - 1] >> 3;
                // A plain group this oscillator fills alone, all eight lanes, reads one table: the uniform kernel.
                if (!warped[o])
                    for (int k = k0; k <= k1; k++)
                        if (slotOf[nb + o * MaxUnison] <= 8 * k && slotOf[nb + o * MaxUnison + u - 1] >= 8 * k + 7) groupUniform[gb + k] = o;
                for (int k = k0; k <= k1; k++)
                {
                    groupMorph[gb + k] |= ff > 0;
                    groupToF2[gb + k] |= f2;
                    groupStore[gb + k] |= isMod[o];
                }
                toF2 |= f2;
            }
            noteToF2[note] = toF2;
        }

        float Level(int note, int o) => Math.Max(0f, OscLevel(o) + Md(note, RouteDest.ALevel + o));
        float Amount(int note, int o, int w) => Math.Clamp(OscAmount(o, w) + Md(note, RouteDest.AWarp1 + 2 * o + w), 0f, 1f);

        double LfoRate(LfoSettings l, float mod)
        {
            double hz = l.Beats > 0 ? S.Tempo / 60.0 / l.Beats : l.Rate;
            return mod == 0 ? hz : hz * PortableMath.Exp2(4.0 * mod);
        }

        /// <summary>
        /// A note's modulation for the tick: its envelopes and LFOs advanced by <paramref name="dt"/>,
        /// every source's value, and each destination the sum of its routes (source × amount). The LFO
        /// rates take last tick's routes.
        /// </summary>
        void Modulate(int note, double dt, float amp, float fe)
        {
            var s = S;
            noteAge[note] += dt;
            int db = note * DestCount;
            if (!anyRoute)
            {
                // No routes: nothing moves (once the last ones' values are gone).
                bool live = false;
                for (int i = 0; i < DestCount && !live; i++) live = dest[db + i] != 0;
                if (live) Array.Clear(dest, db, DestCount);
                modMoved[note] = live;
                return;
            }
            double beat = 60.0 / Math.Max(1.0, s.Tempo);
            for (int e = 0; e < 2; e++)
                srcValue[(int)RouteSource.ModEnv1 + e] = ModEnvelope.Advance(ref envStage[2 * note + e], ref envTime[2 * note + e],
                    ref envLevel[2 * note + e], ref envFrom[2 * note + e], gate[note], s.ModEnv[e], dt, beat);
            for (int l = 0; l < 4; l++)
            {
                var lfo = s.Lfo[l];
                float v;
                if (lfo.Retrig)
                {
                    int i = 4 * note + l;
                    double p = lfoPhase[i] + LfoRate(lfo, dest[db + (int)RouteDest.Lfo1Rate + l]) * dt;
                    if (p >= 1)
                    {
                        p -= Math.Floor(p);
                        lfoHeld[i] = lfoNext[i];
                        var rng = new Rng(lfoSeed[note]);
                        lfoNext[i] = rng.NextBipolar();
                        lfoSeed[note] = rng.NextUInt() | 1u;
                    }
                    lfoPhase[i] = p;
                    v = LfoWave.Value(lfo.Shape, p, lfoHeld[i], lfoNext[i], lfo.Drawn);
                }
                else v = freeValue[l];
                if (lfo.Fade > 0) v *= (float)Math.Min(1.0, noteAge[note] / lfo.Fade);
                srcValue[(int)RouteSource.Lfo1 + l] = v;
            }
            srcValue[(int)RouteSource.AmpEnv] = amp;
            srcValue[(int)RouteSource.FilterEnv] = fe;
            srcValue[(int)RouteSource.Velocity] = noteVel[note];
            srcValue[(int)RouteSource.Key] = (noteKey[note] - 60f) / 60f;
            srcValue[(int)RouteSource.ModWheel] = s.ModWheel;
            srcValue[(int)RouteSource.Aftertouch] = s.Aftertouch;
            for (int m = 0; m < 4; m++) srcValue[(int)RouteSource.Macro1 + m] = s.Macro[m];
            srcValue[(int)RouteSource.Random] = noteRandom[note];
            Array.Copy(dest, db, destLast, db, DestCount);
            Array.Clear(dest, db, DestCount);
            foreach (var r in s.Routes)
                if (r.Source != RouteSource.Off && r.Dest != RouteDest.Off && r.Source < RouteSource.Count && r.Dest < RouteDest.Count)
                    dest[db + (int)r.Dest] += srcValue[(int)r.Source] * r.Amount;
            bool moved = false;
            for (int i = 0; i < DestCount && !moved; i++) moved = dest[db + i] != destLast[db + i];
            modMoved[note] = moved;
        }

        /// <summary>A steady note's ramps stopped: every voice's gains exactly at their targets, the sub's and noise's still.</summary>
        void Settle(int note)
        {
            int nb = note * MaxLanes, v = Notes * MaxLanes, total = lanes.Total;
            for (int o = 0; o < Oscillators; o++)
                for (int l = 0, u = placedUnison[note * Oscillators + o]; l < u; l++)
                {
                    int canon = nb + o * MaxUnison + l, at = nb + slotOf[canon];
                    for (int r = 0; r < 4; r++)
                    {
                        lanes.Gain[r * total + at] = voiceGain[r * v + canon];
                        lanes.Gain[(4 + r) * total + at] = 0;
                    }
                }
            for (int r = 4; r < 8; r++) subGain[r * Notes + note] = noiseGain[r * Notes + note] = 0;
            rampsLive[note] = false;
        }

        int Place(int note, int o, int groups)
        {
            int nb = note * MaxLanes, first = 8 * groups, u = playU[o];
            for (int l = 0; l < u; l++) slotOf[nb + o * MaxUnison + l] = first + l;
            int ng = (u + 7) >> 3;
            for (int k = 0; k < ng; k++) groupOsc[note * MaxGroups + groups + k] = o;
            placed[o] = true;
            return groups + ng;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Ramp(int at, int canon, int row, float target, float invCount, int total, int v)
        {
            float start = voiceGain[row * v + canon];
            lanes.Gain[row * total + at] = start;
            lanes.Gain[(4 + row) * total + at] = (target - start) * invCount;
            voiceGain[row * v + canon] = target;
        }

        static void SetWarps(ref WarpSet w, OscSettings os, float amount1, float amount2, int mod, int log2)
        {
            Warp w1 = os.Warp1, w2 = os.Warp2;
            // A warp that needs a modulator and has none reads plainly.
            if (mod < 0)
            {
                if (Warps.UsesModulator(w1)) w1 = Warp.None;
                if (Warps.UsesModulator(w2)) w2 = Warp.None;
            }
            w.W1 = w1;
            w.W2 = w2;
            Warps.Constants(w1, amount1, out w.K10, out w.K11);
            Warps.Constants(w2, amount2, out w.K20, out w.K21);
            w.Fm = (w1 == Warp.Fm ? w.K10 : 0f) + (w2 == Warp.Fm ? w.K20 : 0f);
            w.UsesMod = mod >= 0 && (Warps.UsesModulator(w1) || Warps.UsesModulator(w2));
            w.Wrap1 = w1 == Warp.Pm;
            w.Wrap2 = w2 == Warp.Pm;
            w.MapsPhase = Warps.MapsPhase(w1) || Warps.MapsPhase(w2) || w.Wrap1 || w.Wrap2;
            w.Amplitude = IsAmplitude(w1) || IsAmplitude(w2);
            w.Mirror = w1 == Warp.Mirror || w2 == Warp.Mirror;
            w.MirrorMix = w1 == Warp.Mirror ? w.K10 : w2 == Warp.Mirror ? w.K20 : 0f;
            w.Log2Len = log2;
            w.ModKind = mod == Oscillators ? 1 : mod == Oscillators + 1 ? 2 : 0;
        }

        static bool IsAmplitude(Warp w) => w == Warp.WindowSync || w == Warp.Am || w == Warp.Ring;

        /// <summary>Each voice's detune ratio and pan gains for an oscillator, recomputed only when they change.</summary>
        void Shape(int o, int u, OscSettings os)
        {
            if (os.Transpose != oscTranspose[o])
            {
                oscTranspose[o] = os.Transpose;
                oscRatio[o] = PortableMath.Pow(2.0, os.Transpose / 12.0);
            }
            if (shapeUnison[o] == u && shapeDetune[o] == os.Detune && shapeSpread[o] == os.Spread && shapePan[o] == os.Pan) return;
            shapeUnison[o] = u;
            shapeDetune[o] = os.Detune;
            shapeSpread[o] = os.Spread;
            shapePan[o] = os.Pan;
            float top = 0;
            for (int l = 0; l < MaxUnison; l++)
            {
                // Evenly from −1 to 1 across the voices: the pitch by the detune, the place by the spread.
                float p = u > 1 && l < u ? 2f * l / (u - 1) - 1f : 0f;
                float r = (float)PortableMath.Pow(2.0, p * os.Detune / 1200.0);
                ratio[o * MaxUnison + l] = r;
                if (l < u && r > top) top = r;
                DspMath.PanGains(DspMath.Clamp(os.Pan + p * os.Spread, -1f, 1f), out float pl, out float pr);
                panL[o * MaxUnison + l] = pl;
                panR[o * MaxUnison + l] = pr;
            }
            topRatio[o] = top;
        }

        /// <summary>The sub's increment and level, and the sub's and noise's gains, for a note.</summary>
        void Sources(int note, float invCount, double subInc, double limitPlain)
        {
            var s = S;
            subInc = Math.Min(subInc, 0.49);
            int level = Wavetable.LevelFor(subInc, limitPlain);
            uint log2 = (uint)TableArena.Log2(level);
            sub.Inc[note] = (uint)(subInc * PhaseScale);
            sub.ShiftR[note] = 32 - log2;
            sub.ShiftL[note] = log2;
            sub.Data[note] = SubTables[(int)s.SubShape].Data;
            sub.OffsetA[note] = sub.OffsetB[note] = TableArena.Offset(0, level);
            Gains(subGain, note, Math.Max(0f, Sm(SmSub) + Md(note, RouteDest.SubLevel)), Sm(SmSub + 1), subPanL, subPanR, invCount);
            Gains(noiseGain, note, Math.Max(0f, Sm(SmNoise) + Md(note, RouteDest.NoiseLevel)), Sm(SmNoise + 1), noisePanL, noisePanR, invCount);
        }

        static void Gains(float[] g, int note, float lv, float to2, float pl, float pr, float invCount)
        {
            float to1 = 1f - to2;
            Step(g, 0, note, lv * pl * to1, invCount);
            Step(g, 1, note, lv * pr * to1, invCount);
            Step(g, 2, note, lv * pl * to2, invCount);
            Step(g, 3, note, lv * pr * to2, invCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Step(float[] g, int row, int note, float target, float invCount) =>
            g[(4 + row) * Notes + note] = (target - g[row * Notes + note]) * invCount;

        /// <summary>A note's filter coefficients for the tick: cutoff in octaves from the envelope, the key (round C4) and velocity.</summary>
        void Filters(int note, float invCount, float fe, double vr)
        {
            var s = S;
            float key = noteKey[note], vel = noteVel[note];
            for (int f = 0; f < 2; f++)
            {
                var t = type[f];
                if (t == FilterType.Off) continue;
                var fs = s.Filter[f];
                double oct = fs.Env * fe + fs.Key * (key - 60f) / 12f - fs.Vel * (1f - vel) + Sm(SmFilter + 3 * f) + 6f * Md(note, RouteDest.F1Cutoff + f);
                float reso = Math.Clamp(Sm(SmFilter + 3 * f + 1) + Md(note, RouteDest.F1Reso + f), 0f, 1f);
                var cur = coefCur[f];
                var step = coefStep[f];
                int fk = f * Notes + note;
                if (!snap[note] && !settingsMoved && oct == filterKey[fk])
                {
                    // Standing still: the ramp stopped once, exactly at the target.
                    if (filterRamps[fk])
                    {
                        for (int r = 0; r < NoteFilters.Rows; r++)
                        {
                            int i = r * Notes + note;
                            cur[i] = coefTarget[f][i];
                            step[i] = 0;
                        }
                        filterRamps[fk] = false;
                    }
                    continue;
                }
                filterKey[fk] = oct;
                filterRamps[fk] = true;
                float hz = (float)DspMath.Clamp(PortableMath.Exp2(oct), 16.0, 0.45 * vr);
                var target = coefTarget[f];
                if (NoteFilters.IsLadder(t)) NoteFilters.Ladder(target, Notes, note, Svf.Prewarp(hz, (float)vr), reso);
                else if (NoteFilters.IsComb(t)) NoteFilters.Comb(target, Notes, note, hz, (float)vr, reso, t == FilterType.CombMinus);
                else NoteFilters.Svf(target, Notes, note, NoteFilters.SvfModeOf(t), Svf.Prewarp(hz, (float)vr), 2f - 1.96f * reso);
                for (int r = 0; r < NoteFilters.Rows; r++)
                {
                    int i = r * Notes + note;
                    if (snap[note])
                    {
                        cur[i] = target[i];
                        step[i] = 0;
                    }
                    else step[i] = (target[i] - cur[i]) * invCount;
                }
            }
        }

        /// <summary><paramref name="count"/> voice-rate samples of the current tick, from <paramref name="at"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        bool Render<TL, TF, TU>(int at, int count)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var s = S;
            int active = 0;
            bool anyF2 = subF2 || noiseF2, subOn = subSend, noiseOn = noiseSend;
            for (int note = 0; note < Notes; note++)
            {
                if (!live[note]) continue;
                active |= 1 << (note >> 3);
                anyF2 |= noteToF2[note] || extToF2[note];
                subOn |= noteSubMod[note];
                noiseOn |= noteNoiseMod[note];
            }
            if (active == 0) return false;
            Array.Clear(in1L, 0, count * Notes);
            Array.Clear(in1R, 0, count * Notes);
            if (anyF2)
            {
                Array.Clear(in2L, 0, count * Notes);
                Array.Clear(in2R, 0, count * Notes);
            }
            // The sub and the noise first: they may modulate oscillators.
            for (int g = 0; g < Groups; g++)
            {
                if ((active & (1 << g)) == 0) continue;
                if (subOn) UnisonKernel.Across<TL, TF, TU>(sub, 8 * g, subBuf, Notes, count);
                if (noiseOn) UnisonKernel.Noise<TL, TF, TU>(noiseState, noiseLp, 8 * g, noiseA, noiseComp, noiseBuf, Notes, count);
            }
            // The oscillators, note by note, then summed across their lanes into the filters' inputs.
            for (int note = 0; note < Notes; note++)
            {
                if (!live[note]) continue;
                if (groupCount[note] == 0)
                {
                    if (extAny[note]) RenderExt(note, count);
                    continue;
                }
                Array.Clear(acc, 0, count * UnisonKernel.AccStride);
                int nb = note * MaxLanes, gb = note * MaxGroups;
                for (int k = 0; k < groupCount[note]; k++)
                {
                    int o = groupOsc[gb + k];
                    var store = groupStore[gb + k] ? modBuf : null;
                    int uo = groupUniform[gb + k];
                    if (o < 0 && uo >= 0)
                    {
                        int no = note * Oscillators + uo;
                        UnisonKernel.Uniform<TL, TF, TU>(lanes, nb + 8 * k, oscData[no], oscOffA[no], oscOffB[no], oscMix[no], oscLog2[no],
                            acc, count, groupToF2[gb + k], store, 8 * k, MaxLanes);
                    }
                    else if (o < 0)
                        UnisonKernel.Plain<TL, TF, TU>(lanes, nb + 8 * k, acc, count, groupMorph[gb + k], groupToF2[gb + k], store, 8 * k, MaxLanes);
                    else
                    {
                        ref var w = ref warpSet[note * Oscillators + o];
                        var modNote = w.ModKind == 1 ? subBuf : noiseBuf;
                        UnisonKernel.Warped<TL, TF, TU>(lanes, nb + 8 * k, acc, count, groupMorph[gb + k], groupToF2[gb + k], in w,
                            modBuf, 0, MaxLanes, modNote, note, Notes, store, 8 * k);
                    }
                }
                UnisonKernel.SumLanes<TL, TF, TU>(acc, in1L, in1R, in2L, in2R, noteToF2[note], note, Notes, count, scratch);
                if (extAny[note]) RenderExt(note, count);
            }
            for (int g = 0; g < Groups; g++)
            {
                if ((active & (1 << g)) == 0) continue;
                if (subSend) UnisonKernel.Send<TL, TF, TU>(subBuf, subGain, g, Notes, count, subF2, in1L, in1R, in2L, in2R);
                if (noiseSend) UnisonKernel.Send<TL, TF, TU>(noiseBuf, noiseGain, g, Notes, count, noiseF2, in1L, in1R, in2L, in2R);
            }
            // The filters across notes; filter 2 hears its own sources and, serial, filter 1; the amp into the output.
            float r = Sm(SmRouting);
            for (int g = 0; g < Groups; g++)
            {
                if ((active & (1 << g)) == 0) continue;
                RunFilter<TL, TF, TU>(0, in1L, in1R, null, null, 0f, f1L, f1R, g, count);
                RunFilter<TL, TF, TU>(1, anyF2 ? in2L : Zero, anyF2 ? in2R : Zero, f1L, f1R, 1f - r, f2L, f2R, g, count);
                NoteFilters.MixOut<TL, TF, TU>(f1L, f1R, f2L, f2R, r, noteGain, Notes, g, count, hiL, hiR, at, scratch);
            }
            for (int f = 0; f < 2; f++)
                if (NoteFilters.IsComb(type[f])) combWrite[f] = (combWrite[f] + count) & NoteFilters.CombMask;
            return true;
        }

        void RunFilter<TL, TF, TU>(int f, float[] aL, float[] aR, float[] bL, float[] bR, float mix, float[] outL, float[] outR, int g, int count)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var t = type[f];
            float drive = Sm(SmFilter + 3 * f + 2);
            if (t == FilterType.Off)
                NoteFilters.RunOff<TL, TF, TU>(aL, aR, bL, bR, mix, outL, outR, Notes, g, count);
            else if (NoteFilters.IsLadder(t))
                NoteFilters.RunLadder<TL, TF, TU>(aL, aR, bL, bR, mix, outL, outR, Notes, g, count, coefCur[f], coefStep[f], filterState[f], drive, t == FilterType.Ladder12);
            else if (NoteFilters.IsComb(t))
                NoteFilters.RunComb<TL, TF, TU>(aL, aR, bL, bR, mix, outL, outR, Notes, g, count, coefCur[f], coefStep[f], combL[f], combR[f], combWrite[f], drive, scratch);
            else
                NoteFilters.RunSvf<TL, TF, TU>(aL, aR, bL, bR, mix, outL, outR, Notes, g, count, coefCur[f], coefStep[f], filterState[f], drive, NoteFilters.IsSteep(t));
        }
    }
}
