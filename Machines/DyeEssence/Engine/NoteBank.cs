#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4): the G0 voice architecture, structure of arrays. Up to
// 16 notes, each with three wavetable oscillators whose unison voices (1–16) are lanes of the
// oscillator, not voices of their own; per note two filters (a ladder, then a state-variable filter,
// serial or parallel and anything between), an amp envelope and a filter envelope. Notes are lanes of
// the filters. Voices run at 1× or 2× the sample rate (2×: an IIR halfband brings the mix down, so
// every mip level keeps the audible band); the control values change every 16 samples of the base
// rate on a grid that runs across blocks. No allocation after construction. Pure.
//
// This is the renderer the G0 gate measures (CPU at 16 notes × 3 oscillators × 8 and 16 unison, the
// same bits on every platform). P1 builds the playable machine on it.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    internal sealed class OscSettings
    {
        public TableArena Table;
        /// <summary>Frame position 0 … 1.</summary>
        public float Position;
        public int Unison = 1;
        /// <summary>Spread of the unison voices' pitch, in semitones from the lowest to the highest.</summary>
        public float Detune;
        /// <summary>Stereo spread of the unison voices, 0 … 1.</summary>
        public float Spread;
        public float Level = 1;
        public float Transpose;
        public bool Enabled = true;
    }

    internal sealed class BankSettings
    {
        /// <summary>1 or 2: voices at the sample rate or twice it.</summary>
        public int Oversample = 2;
        public readonly OscSettings[] Osc = { new OscSettings(), new OscSettings(), new OscSettings() };
        // Filter 1: the ladder.
        public float Cutoff1 = 2000, Reso1 = 0.3f, Drive1 = 1, EnvAmount1 = 2, KeyTrack1 = 0.5f;
        public bool Lp12;
        // Filter 2: the state-variable filter.
        public SvfMode Mode2 = SvfMode.LowPass;
        public float Cutoff2 = 8000, Reso2 = 0.2f, EnvAmount2 = 1;
        /// <summary>0: filter 1 into filter 2 (serial); 1: both from the oscillators, summed (parallel).</summary>
        public float Routing;
        public float AmpA = 0.005f, AmpD = 0.3f, AmpS = 0.7f, AmpR = 0.4f;
        public float FltA = 0.01f, FltD = 0.5f, FltS = 0.3f, FltR = 0.5f;
        public float Volume = 0.25f;
        /// <summary>The oscillators straight to the amp (for measuring them alone).</summary>
        public bool FilterBypass;
    }

    internal sealed class NoteBank
    {
        public const int Notes = 16;
        public const int Groups = Notes / 8;
        public const int Oscillators = 3;
        public const int MaxUnison = 16;
        public const int ControlRate = 16;              // base-rate samples per control tick
        public const int MaxOversample = 2;
        public const int MaxTick = ControlRate * MaxOversample;
        public const int MaxBlock = 256;
        const int Stacks = Notes * Oscillators;
        const double PhaseScale = 4294967296.0;          // 2^32

        static readonly IirHalfbandDesign Decimator = new IirHalfbandDesign(100, (28.0 - 20.0) / 96);

        public readonly BankSettings S = new BankSettings();

        // Per oscillator stack (note·3 + osc), 16 lanes each.
        readonly uint[] phase = new uint[Stacks * MaxUnison];
        readonly uint[] inc = new uint[Stacks * MaxUnison];
        readonly float[] gain = new float[Stacks * 64];  // per stack: left[16], right[16], their steps
        // Per oscillator: unison shape cached against its settings.
        readonly float[] ratio = new float[Oscillators * MaxUnison];
        readonly float[] panL = new float[Oscillators * MaxUnison], panR = new float[Oscillators * MaxUnison];
        readonly int[] shapeUnison = new int[Oscillators];
        readonly float[] shapeDetune = new float[Oscillators], shapeSpread = new float[Oscillators];

        // Per note.
        readonly float[] noteKey = new float[Notes], noteVel = new float[Notes];
        readonly Adsr[] ampEnv = new Adsr[Notes], fltEnv = new Adsr[Notes];
        readonly float[] noteGain = new float[2 * Notes];   // the amp's gain per note and its per-sample step
        readonly float[] acc = new float[MaxTick * UnisonKernel.AccStride];

        // Across notes: the filters' inputs, outputs, coefficients and states.
        readonly float[] inL = new float[MaxTick * Notes], inR = new float[MaxTick * Notes];
        readonly float[] f1L = new float[MaxTick * Notes], f1R = new float[MaxTick * Notes];
        readonly float[] f2L = new float[MaxTick * Notes], f2R = new float[MaxTick * Notes];
        readonly float[] ladderCoef = new float[NoteFilters.LadderCoefs * Notes];
        readonly float[] ladderState = new float[8 * Notes];
        readonly float[] svfCoef = new float[NoteFilters.SvfCoefs * Notes];
        readonly float[] svfState = new float[4 * Notes];

        readonly float[] hiL = new float[MaxOversample * MaxBlock], hiR = new float[MaxOversample * MaxBlock];
        readonly IirHalfband decL = new IirHalfband(Decimator), decR = new IirHalfband(Decimator);
        readonly uint[] seeds = new uint[Notes];
        float sampleRate = 48000;
        int controlCounter;
        LaneKind kind;

        public NoteBank(LaneKind lanes = LaneKind.Auto)
        {
            for (int n = 0; n < Notes; n++)
            {
                ampEnv[n] = new Adsr();
                fltEnv[n] = new Adsr();
                seeds[n] = 0x9E3779B9u * (uint)(n + 1);
            }
            Lanes = lanes;
            for (int o = 0; o < Oscillators; o++) shapeUnison[o] = -1;
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
        float VoiceRate => sampleRate * S.Oversample;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            for (int n = 0; n < Notes; n++)
            {
                ampEnv[n].SetSampleRate(VoiceRate);
                fltEnv[n].SetSampleRate(VoiceRate);
            }
        }

        public int ActiveNotes
        {
            get
            {
                int c = 0;
                for (int n = 0; n < Notes; n++) if (ampEnv[n].IsActive) c++;
                return c;
            }
        }

        /// <summary>Starts note slot <paramref name="slot"/> at MIDI <paramref name="key"/>, velocity 0 … 1.</summary>
        public void NoteOn(int slot, float key, float velocity)
        {
            noteKey[slot] = key;
            noteVel[slot] = velocity;
            bool wasIdle = !ampEnv[slot].IsActive;
            var s = S;
            ampEnv[slot].SetSampleRate(VoiceRate);
            fltEnv[slot].SetSampleRate(VoiceRate);
            ampEnv[slot].Set(s.AmpA, s.AmpD, s.AmpS, s.AmpR);
            fltEnv[slot].Set(s.FltA, s.FltD, s.FltS, s.FltR);
            ampEnv[slot].Gate(true);
            fltEnv[slot].Gate(true);
            // Unison voices start at seeded random phases (the same on every run), a single voice at 0.
            var rng = new Rng(seeds[slot]);
            for (int o = 0; o < Oscillators; o++)
            {
                int b = (slot * Oscillators + o) * MaxUnison;
                bool spread = s.Osc[o].Unison > 1;
                for (int l = 0; l < MaxUnison; l++) phase[b + l] = spread ? rng.NextUInt() : 0u;
                int g = (slot * Oscillators + o) * 64;
                Array.Clear(gain, g, 64);
            }
            seeds[slot] = rng.NextUInt();
            pendingControl[slot] = true;
            if (wasIdle)
            {
                for (int r = 0; r < 8; r++) ladderState[r * Notes + slot] = 0;
                for (int r = 0; r < 4; r++) svfState[r * Notes + slot] = 0;
                noteGain[slot] = 0;
            }
        }

        public void NoteOff(int slot)
        {
            ampEnv[slot].Gate(false);
            fltEnv[slot].Gate(false);
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
            int os = S.Oversample;
            Array.Clear(hiL, 0, os * n);
            Array.Clear(hiR, 0, os * n);
            bool any = false;
            int pos = 0;
            while (pos < n)
            {
                // Control values change on the tick grid only (it runs across blocks), and ramp across
                // the whole tick, so blocks of any size give the same samples.
                if (controlCounter <= 0)
                {
                    controlCounter = ControlRate;
                    for (int note = 0; note < Notes; note++)
                    {
                        live[note] = ampEnv[note].IsActive;
                        if (live[note]) Control(note, os * ControlRate);
                    }
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
            if (os == 2)
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
            for (int i = 0; i < ladderState.Length; i++) ladderState[i] = DspMath.Flush(ladderState[i]);
            for (int i = 0; i < svfState.Length; i++) svfState[i] = DspMath.Flush(svfState[i]);
            return any;
        }

        /// <summary><paramref name="count"/> voice-rate samples of the current tick, from <paramref name="at"/>.</summary>
        bool Render<TL, TF, TU>(int at, int count)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var s = S;
            Array.Clear(inL, 0, count * Notes);
            Array.Clear(inR, 0, count * Notes);
            int groupsActive = 0;
            for (int note = 0; note < Notes; note++)
            {
                if (!live[note])
                {
                    // A silent note beside sounding ones in its group: its lane stays at zero gain.
                    noteGain[note] = 0;
                    noteGain[Notes + note] = 0;
                    continue;
                }
                groupsActive |= 1 << (note >> 3);
                Array.Clear(acc, 0, count * UnisonKernel.AccStride);
                for (int o = 0; o < Oscillators; o++)
                {
                    var os = s.Osc[o];
                    if (!os.Enabled || os.Table == null) continue;
                    RenderStack<TL, TF, TU>(note * Oscillators + o, os.Table, count);
                }
                UnisonKernel.SumLanes<TL, TF, TU>(acc, inL, inR, note, Notes, count);
            }
            if (groupsActive == 0) return false;
            float r = DspMath.Clamp(s.Routing, 0f, 1f);
            for (int g = 0; g < Groups; g++)
            {
                if ((groupsActive & (1 << g)) == 0) continue;
                if (s.FilterBypass)
                {
                    NoteFilters.MixOut<TL, TF, TU>(inL, inR, inL, inR, 0f, noteGain, Notes, g, count, hiL, hiR, at);
                    continue;
                }
                NoteFilters.Ladder<TL, TF, TU>(inL, inR, f1L, f1R, Notes, g, count, ladderCoef, ladderState, s.Drive1, s.Lp12);
                // Serial (0): filter 2 hears filter 1; parallel (1): it hears the oscillators.
                NoteFilters.Svf<TL, TF, TU>(f1L, f1R, inL, inR, r, f2L, f2R, Notes, g, count, svfCoef, svfState);
                NoteFilters.MixOut<TL, TF, TU>(f1L, f1R, f2L, f2R, r, noteGain, Notes, g, count, hiL, hiR, at);
            }
            return true;
        }

        void RenderStack<TL, TF, TU>(int stack, TableArena table, int count)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            int groups = laneGroups[stack];
            for (int grp = 0; grp < groups; grp++)
            {
                int lane = stack * MaxUnison + 8 * grp, gainAt = stack * 64 + 8 * grp;
                if (frameMix[stack] > 0)
                    UnisonKernel.Morph<TL, TF, TU>(table.Data, offsetA[stack], offsetB[stack], frameMix[stack], log2Len[stack], phase, inc, lane, gain, gainAt, acc, count);
                else
                    UnisonKernel.Single<TL, TF, TU>(table.Data, offsetA[stack], log2Len[stack], phase, inc, lane, gain, gainAt, acc, count);
            }
        }

        // Per stack, set by Control for the tick: the frame pair, its mix, the level's length, lane groups.
        readonly int[] offsetA = new int[Stacks], offsetB = new int[Stacks], log2Len = new int[Stacks], laneGroups = new int[Stacks];
        readonly float[] frameMix = new float[Stacks];
        readonly bool[] live = new bool[Notes], pendingControl = new bool[Notes];

        readonly int[] levelOf = new int[Stacks];

        /// <summary>The control values of one note for a tick of <paramref name="count"/> voice-rate samples.</summary>
        void Control(int note, int count)
        {
            var s = S;
            float vr = VoiceRate;
            pendingControl[note] = false;
            var amp = ampEnv[note];
            var flt = fltEnv[note];
            float a = amp.Advance(count);
            float fe = flt.Advance(count);
            float key = noteKey[note];
            float vel = noteVel[note];

            // The amp: ramp to this tick's value across it.
            float invCount = 1f / count;
            float target = a * s.Volume * (0.3f + 0.7f * vel);
            noteGain[Notes + note] = (target - noteGain[note]) * invCount;

            // Oscillators: lane increments from the note's pitch and each lane's detune ratio; one mip
            // level for every lane, chosen for the highest (harmonics below 44 kHz at 2×, below
            // Nyquist at 1×, counting nothing above: the decimator takes 20–24 kHz out at 2×).
            double limit = (s.Oversample >= 2 ? Math.Min(44000.0, vr * 0.5 - 100) : vr * 0.5 - 100) / vr;
            for (int o = 0; o < Oscillators; o++)
            {
                var os = s.Osc[o];
                int u = Math.Clamp(os.Unison, 1, MaxUnison);
                Shape(o, u, os.Detune, os.Spread);
                int stack = note * Oscillators + o;
                int lanes = stack * MaxUnison;
                int g = stack * 64;
                double hz = DspMath.NoteToHz(key + os.Transpose);
                double baseInc = Math.Min(hz / vr, 0.49);
                double top = 0;
                float norm = os.Level / MathF.Sqrt(u);
                int padded = ((u + 7) >> 3) << 3;   // the lanes rendered: whole groups of eight
                for (int l = 0; l < padded; l++)
                {
                    double li = l < u ? Math.Min(baseInc * ratio[o * MaxUnison + l], 0.49) : 0;
                    if (li > top) top = li;
                    inc[lanes + l] = (uint)(li * PhaseScale);
                }
                int level = Wavetable.LevelFor(top, limit);
                levelOf[stack] = level;
                laneGroups[stack] = (u + 7) >> 3;
                if (os.Table != null)
                {
                    // The frame pair for this tick, shared by every lane.
                    var table = os.Table;
                    float fpos = DspMath.Clamp(os.Position, 0f, 1f) * (table.FrameCount - 1);
                    int f0 = (int)fpos;
                    float ff = fpos - f0;
                    if (f0 >= table.FrameCount - 1)
                    {
                        f0 = table.FrameCount - 1;
                        ff = 0;
                    }
                    offsetA[stack] = TableArena.Offset(f0, level);
                    offsetB[stack] = ff > 0 ? TableArena.Offset(f0 + 1, level) : offsetA[stack];
                    frameMix[stack] = ff;
                    log2Len[stack] = TableArena.Log2(level);
                }
                for (int l = 0; l < padded; l++)
                {
                    float tl = l < u ? norm * panL[o * MaxUnison + l] : 0f;
                    float tr = l < u ? norm * panR[o * MaxUnison + l] : 0f;
                    // Left gains at +0, right at +16, their steps at +32 and +48 (lanes 8–15 follow 0–7).
                    gain[g + 32 + l] = (tl - gain[g + l]) * invCount;
                    gain[g + 48 + l] = (tr - gain[g + 16 + l]) * invCount;
                }
            }

            // Filters: cutoff in octaves from the envelope and the key (round C4).
            float keyOct = (key - 60f) / 12f;
            float c1 = DspMath.Clamp(s.Cutoff1 * PortableMathF.Pow(2f, s.EnvAmount1 * fe + s.KeyTrack1 * keyOct), 16f, 0.45f * vr);
            NoteFilters.SetLadder(ladderCoef, Notes, note, Svf.Prewarp(c1, vr), s.Reso1);
            float c2 = DspMath.Clamp(s.Cutoff2 * PortableMathF.Pow(2f, s.EnvAmount2 * fe + s.KeyTrack1 * keyOct), 16f, 0.45f * vr);
            NoteFilters.SetSvf(svfCoef, Notes, note, s.Mode2, Svf.Prewarp(c2, vr), 2f - 1.96f * DspMath.Clamp(s.Reso2, 0f, 1f));
        }

        /// <summary>Each lane's detune ratio and pan for an oscillator, recomputed only when they change.</summary>
        void Shape(int o, int u, float detune, float spread)
        {
            if (shapeUnison[o] == u && shapeDetune[o] == detune && shapeSpread[o] == spread) return;
            shapeUnison[o] = u;
            shapeDetune[o] = detune;
            shapeSpread[o] = spread;
            for (int l = 0; l < MaxUnison; l++)
            {
                float p = u > 1 && l < u ? 2f * l / (u - 1) - 1f : 0f;
                ratio[o * MaxUnison + l] = (float)PortableMath.Pow(2.0, p * detune * 0.5 / 12.0);
                DspMath.PanGains(p * spread, out float pl, out float pr);
                panL[o * MaxUnison + l] = pl * 0.70710678f;
                panR[o * MaxUnison + l] = pr * 0.70710678f;
            }
        }
    }
}
