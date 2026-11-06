// ============================================================================
// PedalFMMachine.cs — Pedal FM v1.0
//
// 4-operator FM generator, 16 voices (one per pattern track), 8 algorithms.
// Designed to be driven by Pedal Chord but works fine standalone.
//
// Every parameter follows the Core/PedalComp rules:
//   - MinValue >= 0; the DSP applies any required offset (PedalComp §2)
//   - No IsStateless=true on user-facing params (Core §25)
// Per-track note delivery uses the pvalues polling workaround (Core §14)
// to avoid silent drops when chord tracks fire on the same row.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;   // fork (neo-buzz): MethodImplOptions.AggressiveOptimization
using BuzzGUI.Interfaces;
using Buzz.MachineInterface;

namespace PedalFM
{
    [MachineDecl(
        Name        = "Pedal FM",
        ShortName   = "PedalFM",
        Author      = "Pedal",
        MaxTracks   = MAX_TRACKS,
        InputCount  = 0,
        OutputCount = 1)]
    public sealed class PedalFMMachine : IBuzzMachine
    {
        public const int MAX_TRACKS = 16;
        public const int NUM_OPS    = Algorithms.OpCount;

        // Sample scale: Buzz uses ±32768 for both effects and generators
        // (PedalComp §1 — scale up at output).
        const float OUT_SCALE = 32768f;

        // ── Host plumbing ──────────────────────────────────────────────────
        readonly IBuzzMachineHost _host;
        IBuzz Buzz => _host?.Machine?.Graph?.Buzz;

        // ── Voices ─────────────────────────────────────────────────────────
        readonly Voice[] _voices = new Voice[MAX_TRACKS];

        // ── Per-track pending note delivery ────────────────────────────────
        readonly byte[] _pendingNote    = new byte[MAX_TRACKS];
        readonly bool[] _hasPendingNote = new bool[MAX_TRACKS];

        // Per-track velocity is sticky: the most-recently-set velocity column
        // value persists for every subsequent note on that track. Pattern
        // editors with no velocity column simply leave it at 127.
        readonly int[] _voiceVelocity = new int[MAX_TRACKS];

        // ── Multi-track polling caches (Core §14) ──────────────────────────
        // Both Note and Velocity need the workaround: parametersChanged is
        // keyed by parameter, not (parameter, track), so simultaneous chord
        // entries on multiple tracks all but one get dropped.
        //
        // The pvalues field shape is NOT stable across ReBuzz builds — it was
        // ConcurrentDictionary<int,int> in ≤1818 and int[256] in 1827+. We
        // therefore reflect a *reader* (track -> value) that detects the
        // backing representation at runtime, rather than casting to a fixed
        // type (which silently nulls on a mismatch and disables polling —
        // the "only the last track plays" bug). See PedalTracker §16.
        IParameter         _ownNoteParam;
        Func<int,int>      _ownNoteRead;
        IParameter         _ownVelParam;
        Func<int,int>      _ownVelRead;

        // ── Per-op configuration mirrors (refreshed once per Work) ─────────
        readonly float[] _opRatios   = new float[NUM_OPS];
        readonly float[] _opDetunes  = new float[NUM_OPS];
        readonly bool[]  _opFixed    = new bool[NUM_OPS]; // reserved for future use
        readonly float[] _opLevels   = new float[NUM_OPS];
        readonly float[] _opVelSens  = new float[NUM_OPS]; // 0..1 per op

        // ── LFO state — single free-running global LFO (v1.2) ──────────────
        // Phase wraps 0..1 so shape evaluation is branchless arithmetic.
        // Sample-and-hold needs a held value plus an RNG; held value updates
        // when phase wraps.
        float       _lfoPhase;
        float       _lfoSampleHeld;
        readonly Random _rng = new Random(0xFEDCBA);

        // ── Scratch render buffer (instance, not static — Core §22). ───────
        float[] _scratch = new float[256];

        // ── Fork (neo-buzz, docs/specs/mcp-server.md §12.15): MIDI notes and controllers (MidiIn.cs) ───────
        // A MIDI note takes a free voice among the first Voices (the highest first: tracks take the first ones),
        // else the one released longest ago, else the oldest; its velocity is a track Velocity's (velocity / 127).
        // Glide applies when it takes a voice still held (legato, as a track's note). Pitch bend ±2 semitones, the
        // mod wheel brings in the LFO's vibrato up to ±1 semitone (LFO Pitch's full depth), the sustain pedal holds
        // MIDI notes; the transport stopping releases them. A track's note plays exactly as before.
        readonly MidiIn     _midi          = new MidiIn();
        readonly MidiVoices _midiVoices    = new MidiVoices(MAX_TRACKS);
        readonly bool[]     _voiceFree     = new bool[MAX_TRACKS];
        readonly bool[]     _voiceReleased = new bool[MAX_TRACKS];
        bool _wasPlaying;

        // ── Fork (neo-buzz, §12.15): Output and the four op levels glide to a new setting over 20 ms (ParamGlide,
        // below; the op levels sample by sample through each voice's Render); a setting that doesn't move is the
        // same number as before. The first block, and a new sample rate, start at the setting.
        ParamGlide _gOut;
        readonly ParamGlide[] _gOpLevel = new ParamGlide[NUM_OPS];
        float[][]  _opRamp  = { new float[256], new float[256], new float[256], new float[256] };
        readonly float[][] _rampArg = new float[NUM_OPS][];
        bool _glidesSet;
        int  _glideSr;

        // ────────────────────────────────────────────────────────────────────
        // Constructor — keep light; ParameterGroups not ready yet (Core §15).
        // ────────────────────────────────────────────────────────────────────
        public PedalFMMachine(IBuzzMachineHost host)
        {
            _host = host;
            for (int i = 0; i < MAX_TRACKS; i++)
            {
                _voices[i]         = new Voice();
                _voiceVelocity[i]  = 127;   // default to full velocity (unity gain)
            }
        }

        // ====================================================================
        // PER-TRACK PARAMETER — Note
        // ====================================================================
        [ParameterDecl(
            Name        = "Note",
            Description = "Note to play on this voice (one voice per track).")]
        public void SetNote(Note value, int track)
        {
            if ((uint)track >= MAX_TRACKS) return;

            _pendingNote[track]    = value.Value;
            _hasPendingNote[track] = true;

            // Poll sibling tracks' pvalues to recover notes that
            // parametersChanged dropped (Core §14).
            EnsurePollFields();
            if (_ownNoteRead != null)
            {
                int noVal = _ownNoteParam.NoValue; // 0 for Note type
                for (int t = 0; t < MAX_TRACKS; t++)
                {
                    if (t == track) continue;
                    if (_hasPendingNote[t]) continue; // a real setter call wins
                    int pv = _ownNoteRead(t);
                    if (pv != noVal && pv != 0)
                    {
                        _pendingNote[t]    = (byte)pv;
                        _hasPendingNote[t] = true;
                    }
                }
            }
        }

        void EnsurePollFields()
        {
            if (_ownNoteRead != null) return;
            try
            {
                if (_ownNoteParam == null)
                {
                    var pg = _host?.Machine?.ParameterGroups;
                    if (pg == null || pg.Count < 3) return;
                    _ownNoteParam = pg[2].Parameters
                        .FirstOrDefault(p => p?.Type == ParameterType.Note);
                }
                if (_ownNoteParam == null) return;
                _ownNoteRead = MakePValuesReader(_ownNoteParam);
            }
            catch { /* never break audio for diagnostics */ }
        }

        // ─── Velocity (sticky per track) ───────────────────────────────────
        [ParameterDecl(
            Name        = "Velocity",
            MinValue    = 0,
            MaxValue    = 127,
            DefValue    = 127,
            Description = "Per-track velocity 0–127. Sticky: persists for subsequent notes.")]
        public void SetVelocity(int value, int track)
        {
            if ((uint)track >= MAX_TRACKS) return;
            _voiceVelocity[track] = value;

            // Poll sibling tracks the same way SetNote does — multi-track
            // velocity column entries on the same row would otherwise get
            // dropped (Core §14).
            EnsureVelPollFields();
            if (_ownVelRead != null)
            {
                int noVal = _ownVelParam.NoValue;   // 255 for byte parameters
                for (int t = 0; t < MAX_TRACKS; t++)
                {
                    if (t == track) continue;
                    int pv = _ownVelRead(t);
                    if (pv != noVal)
                        _voiceVelocity[t] = pv;
                }
            }
        }

        void EnsureVelPollFields()
        {
            if (_ownVelRead != null) return;
            try
            {
                if (_ownVelParam == null)
                {
                    var pg = _host?.Machine?.ParameterGroups;
                    if (pg == null || pg.Count < 3) return;
                    _ownVelParam = pg[2].Parameters
                        .FirstOrDefault(p => p?.Name == "Velocity");
                }
                if (_ownVelParam == null) return;
                _ownVelRead = MakePValuesReader(_ownVelParam);
            }
            catch { }
        }

        /// <summary>
        /// Build a track -> raw-value reader over a parameter's private
        /// <c>pvalues</c> field, tolerating the field-shape change across
        /// ReBuzz builds (ConcurrentDictionary&lt;int,int&gt; in ≤1818,
        /// int[256] in 1827+). Returns null if the field can't be resolved,
        /// in which case multi-track polling is simply disabled rather than
        /// silently broken. See PedalTracker §16.
        /// </summary>
        static Func<int,int> MakePValuesReader(IParameter p)
        {
            var fi = p.GetType().GetField("pvalues",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null) return null;
            object raw = fi.GetValue(p);
            if (raw == null) return null;
            int noValue = p.NoValue;

            if (raw is int[] arr)
                return track => ((uint)track < (uint)arr.Length) ? arr[track] : noValue;

            if (raw is ConcurrentDictionary<int,int> dict)
                return track => dict.TryGetValue(track, out int v) ? v : noValue;

            if (raw is System.Collections.IDictionary idict)   // last resort
                return track => idict.Contains(track) ? (int)idict[track] : noValue;

            return null;
        }

        // ====================================================================
        // GLOBAL PARAMETERS
        // ====================================================================
        [ParameterDecl(
            Name              = "Algorithm",
            MinValue          = 0,
            MaxValue          = 7,
            DefValue          = 0,
            ValueDescriptions = new[]
            {
                "1: Linear",
                "2: Dual chain",
                "3: Branched",
                "4: 3-on-1",
                "5: Mixed",
                "6: Twin pair",
                "7: Additive",
                "8: Pad+carrier",
            })]
        public int Algorithm { get; set; } = 0;

        [ParameterDecl(Name = "Output", MinValue = 0, MaxValue = 127, DefValue = 100,
                       Description = "Master output level.")]
        public int OutLevel { get; set; } = 100;

        [ParameterDecl(Name = "Voices", MinValue = 1, MaxValue = 16, DefValue = 8,
                       Description = "Maximum simultaneous voices.")]
        public int VoiceCap { get; set; } = 8;

        // Per-op parameters — declared as four blocks of seven properties.

        // ── Op 1 ──
        [ParameterDecl(Name = "Op1 Ratio",   MinValue = 0, MaxValue = 127, DefValue = 7)]   public int Op1Ratio   { get; set; } = 7;
        [ParameterDecl(Name = "Op1 Detune",  MinValue = 0, MaxValue = 100, DefValue = 50)]  public int Op1Detune  { get; set; } = 50;
        [ParameterDecl(Name = "Op1 Level",   MinValue = 0, MaxValue = 127, DefValue = 100)] public int Op1Level   { get; set; } = 100;
        [ParameterDecl(Name = "Op1 Attack",  MinValue = 0, MaxValue = 127, DefValue = 4)]   public int Op1Attack  { get; set; } = 4;
        [ParameterDecl(Name = "Op1 Decay",   MinValue = 0, MaxValue = 127, DefValue = 50)]  public int Op1Decay   { get; set; } = 50;
        [ParameterDecl(Name = "Op1 Sustain", MinValue = 0, MaxValue = 127, DefValue = 100)] public int Op1Sustain { get; set; } = 100;
        [ParameterDecl(Name = "Op1 Release", MinValue = 0, MaxValue = 127, DefValue = 40)]  public int Op1Release { get; set; } = 40;

        // ── Op 2 ──
        [ParameterDecl(Name = "Op2 Ratio",   MinValue = 0, MaxValue = 127, DefValue = 7)]   public int Op2Ratio   { get; set; } = 7;
        [ParameterDecl(Name = "Op2 Detune",  MinValue = 0, MaxValue = 100, DefValue = 50)]  public int Op2Detune  { get; set; } = 50;
        [ParameterDecl(Name = "Op2 Level",   MinValue = 0, MaxValue = 127, DefValue = 80)]  public int Op2Level   { get; set; } = 80;
        [ParameterDecl(Name = "Op2 Attack",  MinValue = 0, MaxValue = 127, DefValue = 4)]   public int Op2Attack  { get; set; } = 4;
        [ParameterDecl(Name = "Op2 Decay",   MinValue = 0, MaxValue = 127, DefValue = 50)]  public int Op2Decay   { get; set; } = 50;
        [ParameterDecl(Name = "Op2 Sustain", MinValue = 0, MaxValue = 127, DefValue = 100)] public int Op2Sustain { get; set; } = 100;
        [ParameterDecl(Name = "Op2 Release", MinValue = 0, MaxValue = 127, DefValue = 40)]  public int Op2Release { get; set; } = 40;

        // ── Op 3 ──
        [ParameterDecl(Name = "Op3 Ratio",   MinValue = 0, MaxValue = 127, DefValue = 15)]  public int Op3Ratio   { get; set; } = 15;
        [ParameterDecl(Name = "Op3 Detune",  MinValue = 0, MaxValue = 100, DefValue = 50)]  public int Op3Detune  { get; set; } = 50;
        [ParameterDecl(Name = "Op3 Level",   MinValue = 0, MaxValue = 127, DefValue = 70)]  public int Op3Level   { get; set; } = 70;
        [ParameterDecl(Name = "Op3 Attack",  MinValue = 0, MaxValue = 127, DefValue = 4)]   public int Op3Attack  { get; set; } = 4;
        [ParameterDecl(Name = "Op3 Decay",   MinValue = 0, MaxValue = 127, DefValue = 50)]  public int Op3Decay   { get; set; } = 50;
        [ParameterDecl(Name = "Op3 Sustain", MinValue = 0, MaxValue = 127, DefValue = 100)] public int Op3Sustain { get; set; } = 100;
        [ParameterDecl(Name = "Op3 Release", MinValue = 0, MaxValue = 127, DefValue = 40)]  public int Op3Release { get; set; } = 40;

        // ── Op 4 (default carrier in most algorithms) ──
        [ParameterDecl(Name = "Op4 Ratio",   MinValue = 0, MaxValue = 127, DefValue = 7)]   public int Op4Ratio   { get; set; } = 7;
        [ParameterDecl(Name = "Op4 Detune",  MinValue = 0, MaxValue = 100, DefValue = 50)]  public int Op4Detune  { get; set; } = 50;
        [ParameterDecl(Name = "Op4 Level",   MinValue = 0, MaxValue = 127, DefValue = 127)] public int Op4Level   { get; set; } = 127;
        [ParameterDecl(Name = "Op4 Attack",  MinValue = 0, MaxValue = 127, DefValue = 4)]   public int Op4Attack  { get; set; } = 4;
        [ParameterDecl(Name = "Op4 Decay",   MinValue = 0, MaxValue = 127, DefValue = 60)]  public int Op4Decay   { get; set; } = 60;
        [ParameterDecl(Name = "Op4 Sustain", MinValue = 0, MaxValue = 127, DefValue = 110)] public int Op4Sustain { get; set; } = 110;
        [ParameterDecl(Name = "Op4 Release", MinValue = 0, MaxValue = 127, DefValue = 50)]  public int Op4Release { get; set; } = 50;

        // ── New in v1.1 — appended at the end so existing preset indices stay valid ──

        // Op1 self-feedback. Feedback on op0 (the first operator in the chain
        // for every algorithm) lets you turn its sine into a sawtooth-like wave
        // when cranked, adding raspy/bright character that pure forward-FM
        // can't produce. DX7-style 2-sample averaging in Voice keeps it stable.
        [ParameterDecl(Name = "Op1 Feedback", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Self-feedback on Op1. 0 = pure sine, high values approach a saw.")]
        public int Op1Feedback { get; set; } = 0;

        // Compound-modulator scale (formerly the hardcoded 0.25). Low values
        // tame chained algorithms (Linear, Branched, Twin Pair); high values
        // bring back the classic noisy-DX character.
        [ParameterDecl(Name = "Compound Mod", MinValue = 0, MaxValue = 127, DefValue = 32,
            Description = "Compound modulator strength. 32 = tuneful (default), 127 = classic noisy.")]
        public int CompoundMod { get; set; } = 32;

        // ── New in v1.2 — appended at the end so v1.0/v1.1 preset indices stay valid ──

        // Glide / portamento. 0 = off, max ≈ 1 second to traverse 12 semitones.
        // Quadratic curve so low values are subtle. Legato semantics: only
        // engages when the voice is already sounding when the next note hits.
        [ParameterDecl(Name = "Glide", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Portamento time. Voice must already be sounding for glide to engage (legato).")]
        public int Glide { get; set; } = 0;

        // LFO. One free-running global LFO; output routed to pitch and op
        // levels with separate depth controls. Rate is logarithmic so the
        // bottom half covers slow movement (chorus/vibrato) and the top half
        // gets into audio-rate territory.
        [ParameterDecl(Name = "LFO Rate", MinValue = 0, MaxValue = 127, DefValue = 60,
            Description = "LFO rate, log scale. ~0.05 Hz to ~30 Hz.")]
        public int LfoRate { get; set; } = 60;

        [ParameterDecl(Name = "LFO Shape", MinValue = 0, MaxValue = 4, DefValue = 0,
            Description = "0=Sine, 1=Triangle, 2=Square, 3=Saw, 4=Sample&Hold.")]
        public int LfoShape { get; set; } = 0;

        // LFO -> pitch (vibrato). Quadratic, max ±100 cents (±1 semitone).
        [ParameterDecl(Name = "LFO Pitch", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Pitch LFO depth. Max ±1 semitone at 127.")]
        public int LfoPitch { get; set; } = 0;

        // LFO -> op levels (tremolo + timbre wobble). Multiplicative scale on
        // the final voice sample; max ±50% swing to avoid drop-outs.
        [ParameterDecl(Name = "LFO Mod", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Level LFO depth. Modulates carrier amplitude AND modulator depth (timbre + tremolo).")]
        public int LfoMod { get; set; } = 0;

        // Per-op velocity sensitivity. 0 = velocity has no effect on this op
        // (only on master gain); 127 = op level scales linearly with velocity.
        // Common DX trick: high sens on modulators → harder hits sound brighter.
        [ParameterDecl(Name = "Op1 VelSens", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Velocity sensitivity for Op1 level. High values brighten harder hits when Op1 is a modulator.")]
        public int Op1VelSens { get; set; } = 0;

        [ParameterDecl(Name = "Op2 VelSens", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Velocity sensitivity for Op2 level.")]
        public int Op2VelSens { get; set; } = 0;

        [ParameterDecl(Name = "Op3 VelSens", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Velocity sensitivity for Op3 level.")]
        public int Op3VelSens { get; set; } = 0;

        [ParameterDecl(Name = "Op4 VelSens", MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Velocity sensitivity for Op4 level.")]
        public int Op4VelSens { get; set; } = 0;

        // ====================================================================
        // PARAMETER MAPPING
        // ====================================================================
        // Ratio: linear 0.125 step. value=7 -> 1.0, 15 -> 2.0, 31 -> 4.0, 127 -> 16.0.
        static float MapRatio(int v)   => (v + 1) * 0.125f;
        // Detune: ±50 cents (centred at 50).
        static float MapDetune(int v)  => v - 50;
        // Level: 0..1 linear.
        static float MapLevel(int v)   => v / 127f;
        // Sustain: 0..1 linear.
        static float MapSustain(int v) => v / 127f;
        // Time params: x^2 curve, attack max 4 s, decay/release max 8 s.
        static float MapTime(int v, float maxSec)
        {
            float n = v / 127f;
            return n * n * maxSec;
        }
        // Output level: 0..1 with a slight curve so 100 ≈ 0 dB-ish.
        static float MapOutLevel(int v) => (v / 100f) * (v / 100f) * 0.5f;

        // ====================================================================
        // WORK (generator signature)
        // ====================================================================
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // fork (neo-buzz): never compiled on the audio thread
        public bool Work(Sample[] output, int numsamples, WorkModes mode)
        {
            if (output == null || numsamples <= 0) return false;

            // Compute sample rate and glide samples up front so pending-note
            // application can pass glide info into voice.Trigger.
            int sr = (int)(_host?.MasterInfo?.SamplesPerSec ?? 44100);
            if (sr <= 0) sr = 44100;

            float glideNorm    = Glide / 127f;
            float glideSec     = glideNorm * glideNorm;        // quadratic, max 1 sec
            int   glideSamples = (int)(glideSec * sr);

            // Fork (neo-buzz, §12.15): with no voice sounding, a changed setting needs no glide: the next note starts at it.
            bool wasIdle = true;
            for (int i = 0; i < MAX_TRACKS; i++)
                if (!_voices[i].IsIdle() && !_voices[i].Finished) { wasIdle = false; break; }

            // 1. Apply pending note events. Once at the start of each buffer —
            //    sub-buffer timing isn't worth the complexity for v1.
            ApplyPendingNotes(glideSamples);
            CheckTransport();                 // fork (neo-buzz, §12.15): releases MIDI notes on Stop
            ProcessMidi(glideSamples);        // fork (neo-buzz, §12.15): after the tracks' notes

            // 2. Refresh per-op configs and voice frequencies.
            RefreshOpConfig();

            // Fork (neo-buzz, §12.15): glide to Output and the op levels; the first time, at a new rate, and from
            // silence, start there.
            float outLevel = MapOutLevel(OutLevel);
            if (!_glidesSet || sr != _glideSr || wasIdle)
            {
                _gOut.Snap(outLevel);
                for (int op = 0; op < NUM_OPS; op++) _gOpLevel[op].Snap(_opLevels[op]);
                _glidesSet = true;
                _glideSr   = sr;
            }
            else
            {
                int len = (int)(ParamGlide.Ms * 0.001f * sr);
                _gOut.Set(outLevel, len);
                for (int op = 0; op < NUM_OPS; op++) _gOpLevel[op].Set(_opLevels[op], len);
            }

            byte algo = (byte)Math.Clamp(Algorithm, 0, Algorithms.Count - 1);
            int  cap  = Math.Clamp(VoiceCap, 1, MAX_TRACKS);

            // Map feedback param to phase modulation cycles. Quadratic curve
            // makes the bottom half subtle and the top half aggressive.
            // Max ≈ 1.5 cycles ≈ 3π radians of self-phase-mod, plenty to
            // reach saw-like timbres without falling off the stability cliff.
            float fbNorm    = Op1Feedback / 127f;
            float feedback  = fbNorm * fbNorm * 1.5f;

            // Compound-modulator scale: 0..1 linear. Default 32/127 ≈ 0.25
            // matches the v1.0 hardcoded value, so existing presets are
            // unaffected unless the user opts in to higher values.
            float compoundAtten = CompoundMod / 127f;

            // ── LFO ────────────────────────────────────────────────────────
            // Free-running global LFO. Advance phase by numsamples / period.
            // Rate map: log scale, ~0.05 Hz at 0 → ~30 Hz at 127.
            //   rate = 0.05 * (30/0.05)^(v/127) = 0.05 * 600^(v/127)
            float lfoRateHz = 0.05f * MathF.Pow(600f, LfoRate / 127f);
            float lfoStep   = lfoRateHz * numsamples / sr;
            bool  wrapped   = false;
            _lfoPhase += lfoStep;
            while (_lfoPhase >= 1f) { _lfoPhase -= 1f; wrapped = true; }

            // Sample-and-hold pulls a new value on each phase wrap.
            if (LfoShape == 4 && wrapped)
                _lfoSampleHeld = (float)(_rng.NextDouble() * 2.0 - 1.0);

            float lfoOut = ComputeLfoOutput(LfoShape, _lfoPhase, _lfoSampleHeld);

            // LFO Pitch: quadratic to ±100 cents (1 semitone).
            float pitchNorm  = LfoPitch / 127f;
            float pitchDepth = pitchNorm * pitchNorm;             // 0..1
            if (_midi.Mod > pitchDepth) pitchDepth = _midi.Mod;   // fork (neo-buzz, §12.15): the mod wheel's vibrato
            float lfoPitchSemis = lfoOut * pitchDepth             // ±1 semi at depth=1
                                + _midi.Bend * MidiIn.BendSemitones;   // fork: pitch bend (0 without MIDI)

            // LFO Mod: ±50% multiplicative swing on the voice output.
            float modNorm    = LfoMod / 127f;
            float voiceLfoLevel = 1f + lfoOut * modNorm * 0.5f;   // [0.5..1.5] at depth=1

            // ── Per-voice param sync (op level + envelopes + freq config) ──
            for (int v = 0; v < cap; v++)
            {
                var voice = _voices[v];
                if (voice.IsIdle() || voice.Finished) continue;   // fork (neo-buzz, §12.15): Finished

                float vel = voice.Velocity;
                for (int op = 0; op < NUM_OPS; op++)
                {
                    var o = voice.Ops[op];
                    // Apply per-op velocity sensitivity. effLevel = baseLevel × (1 - sens × (1 - vel)).
                    // sens=0 → no velocity effect; sens=1 → full linear scale by velocity.
                    float velScale = 1f - _opVelSens[op] * (1f - vel);
                    o.Level = _opLevels[op] * velScale;
                    o.VelScale = velScale;    // fork (neo-buzz, §12.15): for the level's glide
                    ConfigureEnvelope(op, o.Env, sr);
                }
                voice.UpdateFrequencies(_opRatios, _opDetunes, _opFixed);
            }

            // 3. Render. Allocate scratch lazily (instance, not static).
            if (_scratch.Length < numsamples)
            {
                _scratch = new float[Math.Max(numsamples, _scratch.Length * 2)];
                for (int op = 0; op < NUM_OPS; op++) _opRamp[op] = new float[_scratch.Length];   // fork
            }
            Array.Clear(_scratch, 0, numsamples);

            // Fork (neo-buzz, §12.15): each op level that glides, sample by sample for this block.
            bool anyRamp = false;
            for (int op = 0; op < NUM_OPS; op++)
            {
                if (_gOpLevel[op].Left == 0) { _rampArg[op] = null; continue; }
                var r = _opRamp[op];
                for (int i = 0; i < numsamples; i++) r[i] = _gOpLevel[op].Next();
                _rampArg[op] = r;
                anyRamp = true;
            }
            float[][] ramp = anyRamp ? _rampArg : null;

            bool anyActive = false;
            for (int v = 0; v < cap; v++)
            {
                var voice = _voices[v];
                if (voice.IsIdle() || voice.Finished) continue;   // covers held, releasing, fading-cross; fork: Finished
                anyActive = true;
                voice.Render(_scratch, numsamples, algo,
                             feedback, compoundAtten,
                             lfoPitchSemis, voiceLfoLevel,
                             sr, ramp);
            }

            // 4. Master gain + scale to ±32768. Mono -> stereo (centred).
            float gain = MapOutLevel(OutLevel) * OUT_SCALE;
            bool  outGliding = _gOut.Left > 0;   // fork (neo-buzz, §12.15)
            for (int i = 0; i < numsamples; i++)
            {
                float g = outGliding ? _gOut.Next() * OUT_SCALE : gain;
                float s = _scratch[i] * g;
                output[i].L = s;
                output[i].R = s;
            }

            return anyActive;
        }

        void ApplyPendingNotes(int glideSamples)
        {
            int cap = Math.Clamp(VoiceCap, 1, MAX_TRACKS);
            for (int t = 0; t < MAX_TRACKS; t++)
            {
                if (!_hasPendingNote[t]) continue;
                _hasPendingNote[t] = false;
                byte b = _pendingNote[t];
                if (b == 0) continue; // NoValue — shouldn't happen but guard

                // Track index drives voice slot directly (one voice per track).
                int v = (t < cap) ? t : t % cap;
                var voice = _voices[v];

                if (b == 255)
                {
                    voice.Release();
                    _midiVoices.Clear(v);     // fork (neo-buzz, §12.15): no longer a MIDI key's
                }
                else
                {
                    int   midi = BuzzNoteToMidi(b);
                    float vel  = _voiceVelocity[t] / 127f;   // 0..1 sticky velocity
                    voice.Trigger(midi, t, vel, glideSamples);
                    _midiVoices.Track(v);     // fork (neo-buzz, §12.15): the track's note took the voice
                }
            }
        }

        // Fork (neo-buzz, docs/specs/mcp-server.md §12.15): MIDI from ReBuzz (a keyboard, a pattern's MIDI column),
        // queued and applied at the start of the next block.

        /// <summary>ReBuzz: a MIDI note for this machine (velocity 0 releases).</summary>
        public void MidiNote(int channel, int value, int velocity) => _midi.Note(value, velocity);

        /// <summary>ReBuzz: a MIDI controller, pitch bend (0xE0 / 0xFF) or channel pressure (0xD0 / 0xFC).</summary>
        public void MidiControlChange(int ctrl, int channel, int value) => _midi.Controller(ctrl, value);

        void ProcessMidi(int glideSamples)
        {
            while (_midi.Next(out int kind, out int a, out int b))
            {
                if (kind == MidiIn.NoteOn) MidiNoteOn(a, b, glideSamples);
                else if (kind == MidiIn.NoteOff) MidiNoteOff(a);
                else switch (a)
                {
                    case MidiIn.AllNotesOff:
                        ReleaseMidiVoices();
                        break;
                    case MidiIn.AllSoundOff:
                        ReleaseMidiVoices();
                        for (int v = 0; v < MAX_TRACKS; v++) _voices[v].Stop();
                        break;
                    default:
                        if (_midi.Apply(a, b))   // the sustain pedal came up
                            for (int v = 0; v < MAX_TRACKS; v++)
                                if (_midiVoices.Sustained[v])
                                {
                                    _voices[v].Release();
                                    _midiVoices.Clear(v);
                                }
                        break;
                }
            }
        }

        void MidiNoteOn(int key, int velocity, int glideSamples)
        {
            int v = _midiVoices.Playing(key, MAX_TRACKS);   // the same key again: its voice
            if (v < 0)
            {
                int cap = Math.Clamp(VoiceCap, 1, MAX_TRACKS);
                for (int i = 0; i < cap; i++)
                {
                    _voiceFree[i]     = _voices[i].IsIdle() || _voices[i].Finished;
                    _voiceReleased[i] = !_voices[i].Held;
                }
                v = _midiVoices.Pick(cap, _voiceFree, _voiceReleased);
            }
            var voice = _voices[v];
            voice.Trigger(key, -1, velocity / 127f, voice.Held ? glideSamples : 0);
            _midiVoices.Start(v, key);
        }

        void MidiNoteOff(int key)
        {
            int v = _midiVoices.Playing(key, MAX_TRACKS);
            if (v < 0 || !_midiVoices.Held[v]) return;
            if (_midi.Pedal)
            {
                _midiVoices.Held[v]      = false;
                _midiVoices.Sustained[v] = true;
                return;
            }
            _voices[v].Release();
            _midiVoices.Clear(v);
        }

        void ReleaseMidiVoices()
        {
            for (int v = 0; v < MAX_TRACKS; v++)
            {
                if (_midiVoices.Key[v] < 0) continue;
                _voices[v].Release();
                _midiVoices.Clear(v);
            }
        }

        // The transport stopping releases MIDI notes (a track's note rings on, as before).
        void CheckTransport()
        {
            bool nowPlaying;
            try { nowPlaying = Buzz?.Playing ?? false; }
            catch { nowPlaying = _wasPlaying; }
            if (_wasPlaying && !nowPlaying) ReleaseMidiVoices();
            _wasPlaying = nowPlaying;
        }

        void RefreshOpConfig()
        {
            _opRatios[0]  = MapRatio(Op1Ratio);
            _opRatios[1]  = MapRatio(Op2Ratio);
            _opRatios[2]  = MapRatio(Op3Ratio);
            _opRatios[3]  = MapRatio(Op4Ratio);

            _opDetunes[0] = MapDetune(Op1Detune);
            _opDetunes[1] = MapDetune(Op2Detune);
            _opDetunes[2] = MapDetune(Op3Detune);
            _opDetunes[3] = MapDetune(Op4Detune);

            _opLevels[0]  = MapLevel(Op1Level);
            _opLevels[1]  = MapLevel(Op2Level);
            _opLevels[2]  = MapLevel(Op3Level);
            _opLevels[3]  = MapLevel(Op4Level);

            _opVelSens[0] = Op1VelSens / 127f;
            _opVelSens[1] = Op2VelSens / 127f;
            _opVelSens[2] = Op3VelSens / 127f;
            _opVelSens[3] = Op4VelSens / 127f;

            // _opFixed[*] all false in v1
        }

        void ConfigureEnvelope(int opIndex, Envelope env, int sr)
        {
            int a, d, s, r;
            switch (opIndex)
            {
                case 0: a = Op1Attack; d = Op1Decay; s = Op1Sustain; r = Op1Release; break;
                case 1: a = Op2Attack; d = Op2Decay; s = Op2Sustain; r = Op2Release; break;
                case 2: a = Op3Attack; d = Op3Decay; s = Op3Sustain; r = Op3Release; break;
                case 3: a = Op4Attack; d = Op4Decay; s = Op4Sustain; r = Op4Release; break;
                default: return;
            }
            env.Configure(
                MapTime(a, 4f),
                MapTime(d, 8f),
                MapSustain(s),
                MapTime(r, 8f),
                sr);
        }

        /// <summary>
        /// Evaluate LFO shape at phase 0..1, returning -1..+1.
        /// Sample-and-hold uses the held value; that's updated externally on phase wrap.
        /// </summary>
        static float ComputeLfoOutput(int shape, float phase, float held)
        {
            switch (shape)
            {
                case 0: // Sine
                    return MathF.Sin(2f * MathF.PI * phase);
                case 1: // Triangle: -1 at phase 0, +1 at 0.5, -1 at 1
                    return phase < 0.5f
                        ? 4f * phase - 1f
                        : 3f - 4f * phase;
                case 2: // Square
                    return phase < 0.5f ? 1f : -1f;
                case 3: // Saw (rising)
                    return 2f * phase - 1f;
                case 4: // Sample & hold
                    return held;
                default:
                    return 0f;
            }
        }

        // ── Note encoding helper (Core §7) ─────────────────────────────────
        static int BuzzNoteToMidi(byte b)
        {
            int oct  = (b >> 4) & 0x0F;
            int semi = (b & 0x0F) - 1;
            return oct * 12 + semi;
        }
    }

    // Fork (neo-buzz, docs/specs/mcp-server.md §12.13, §12.15): a setting that glides to a new value in a straight line
    // over Ms milliseconds, sample by sample, and lands on it exactly, so a setting that doesn't move is the same
    // number as before. Automation and pattern events change parameters once a tick or subtick; applied once a block
    // they stepped, which clicks. As in the Pedal effects (U6).
    struct ParamGlide
    {
        public const float Ms = 20f;
        public float Value, Target, Step;
        public int Left;

        public void Snap(float v) { Value = Target = v; Left = 0; }

        public void Set(float v, int samples)
        {
            if (v == Target) return;
            Target = v;
            if (samples <= 1) { Snap(v); return; }
            Left = samples;
            Step = (v - Value) / samples;
        }

        public float Next()
        {
            if (Left > 0) Value = --Left == 0 ? Target : Value + Step;
            return Value;
        }
    }
}
