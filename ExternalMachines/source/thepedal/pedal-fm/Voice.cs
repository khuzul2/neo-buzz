using System;
using System.Runtime.CompilerServices;   // fork (neo-buzz): MethodImplOptions.AggressiveOptimization

namespace PedalFM
{
    /// <summary>
    /// One polyphonic voice: 4 operators + master anti-click envelope +
    /// deferred-trigger machinery + legato glide.
    ///
    /// Render sub-buffers internally at control rate (32-sample blocks)
    /// when glide or LFO pitch is active so phase increments stay smooth
    /// without per-sample MathF.Pow. When pitch is static (no glide and no
    /// pitch LFO) the sub-block layer collapses to a single pass over the
    /// whole buffer — the same fast path as v1.1.
    /// </summary>
    internal sealed class Voice
    {
        public const int OpCount = Algorithms.OpCount;

        // Anti-click fade rate. ~1.3 ms at 48 kHz, 1.5 ms at 44.1.
        const float FadeStep = 1f / 64f;
        const float NearZero = 1e-4f;

        // FM index scale, pre-multiplied by 2^32 (the uint phase scale).
        public  const float ModIndex = 4.0f;
        const         float ModScale = ModIndex * 4294967296f;

        // Control-rate sub-block size. 32 samples ≈ 0.66 ms at 48 kHz —
        // smooth enough that pitch glides and LFO can't audibly stair-step.
        const int BlockSize = 32;

        public readonly Operator[] Ops = new Operator[OpCount];

        // Voice state
        public  bool  Active;
        public  bool  Held;
        public  int   CurMidi = 60;
        public  uint  AgeSamples;
        public  int   TrackOwner = -1;

        // Velocity exposed so the machine can apply per-op velocity
        // sensitivity at op.Level update time (zero per-sample cost).
        public  float Velocity { get; private set; } = 1f;

        // Anti-click master gain (velocity is folded into _targetGain).
        float _curGain    = 0f;
        float _targetGain = 0f;

        // Deferred-trigger: ramp gain to 0, then re-attack inline once silent.
        bool   _pendingTrigger;
        int    _pendingMidi;
        int    _pendingTrack;
        float  _pendingVelocity;

        // Op0 self-feedback memory (DX7-style 2-sample averaging).
        float _op0Prev;
        float _op0PrevPrev;

        // Glide state. _curMidiF is the continuous (float) MIDI pitch the
        // voice is currently sounding at; _glideStep is the per-sample
        // increment that walks it toward the next note's MIDI value over
        // _glideRemainingSamples samples.
        float _curMidiF;
        float _glideStep;
        int   _glideRemainingSamples;

        public Voice()
        {
            for (int i = 0; i < OpCount; i++) Ops[i] = new Operator();
        }

        /// <summary>
        /// Trigger a note. Three paths:
        /// <list type="number">
        /// <item>Voice sounding + glide enabled: legato — ramp pitch, keep envelopes.</item>
        /// <item>Voice sounding + no glide: deferred fade-cross retrigger.</item>
        /// <item>Voice silent: fresh trigger inline.</item>
        /// </list>
        /// </summary>
        public void Trigger(int midi, int trackOwner, float velocity, int glideSamples)
        {
            // Legato glide — voice already sounding, glide enabled.
            if (Active && _curGain > NearZero && !_pendingTrigger && glideSamples > 0)
            {
                _glideRemainingSamples = glideSamples;
                _glideStep             = (midi - _curMidiF) / glideSamples;
                CurMidi                = midi;
                Held                   = true;
                _targetGain            = velocity;
                Velocity               = velocity;
                return;
            }

            // Voice sounding without glide — fade-cross retrigger.
            if (_curGain > NearZero)
            {
                _pendingTrigger  = true;
                _pendingMidi     = midi;
                _pendingTrack    = trackOwner;
                _pendingVelocity = velocity;
                _targetGain      = 0f;
                return;
            }

            // Voice silent — fresh trigger inline.
            ApplyTriggerNow(midi, trackOwner, velocity);
        }

        void ApplyTriggerNow(int midi, int trackOwner, float velocity)
        {
            CurMidi                = midi;
            _curMidiF              = midi;
            _glideRemainingSamples = 0;
            TrackOwner             = trackOwner;
            Active                 = true;
            Held                   = true;
            AgeSamples             = 0;
            _targetGain            = velocity;
            Velocity               = velocity;

            for (int i = 0; i < OpCount; i++)
            {
                Ops[i].Phase = 0;
                Ops[i].Env.NoteOn();
            }
            _op0Prev     = 0f;
            _op0PrevPrev = 0f;
        }

        public void Release()
        {
            if (!Active) return;
            Held = false;
            for (int i = 0; i < OpCount; i++)
                Ops[i].Env.NoteOff();
        }

        public void Stop()
        {
            _targetGain = 0f;
            Held = false;
        }

        /// <summary>
        /// Fork (neo-buzz, docs/specs/mcp-server.md §12.15): released, every envelope done, its anti-click gain where it
        /// is going and no note pending, so it plays exact silence until its next note: the machine leaves it out. Such a
        /// voice stayed Active (its gain stays at its velocity after a release), so the machine rendered it, and said it
        /// was sounding, for as long as it ran. Its state is as it was: its next note starts as it did before.
        /// </summary>
        public bool Finished
        {
            get
            {
                if (Held || _pendingTrigger || _curGain != _targetGain) return false;
                for (int i = 0; i < OpCount; i++)
                    if (Ops[i].Env.IsActive) return false;
                return true;
            }
        }

        public bool IsIdle()
        {
            if (_pendingTrigger) return false;
            if (_curGain > NearZero) return false;
            for (int i = 0; i < OpCount; i++)
                if (Ops[i].Env.IsActive) return false;
            return !Held;
        }

        /// <summary>
        /// Update per-op static frequency config. Inc itself is now computed
        /// inside Render at control rate (so that LFO + glide can move it
        /// without going through this code path every block).
        /// </summary>
        public void UpdateFrequencies(float[] opRatios, float[] opDetunes, bool[] opFixed)
        {
            for (int i = 0; i < OpCount; i++)
                Ops[i].UpdateConfig(opRatios[i], opDetunes[i], opFixed[i]);
        }

        /// <summary>
        /// Render n samples mixing into dst (mono).
        /// </summary>
        /// <param name="feedback">Op0 self-feedback amount in cycles (0..~1.5).</param>
        /// <param name="compoundAtten">Compound-modulator scale (0..1).</param>
        /// <param name="lfoPitchSemis">Pitch LFO offset, semitones, constant for the buffer.</param>
        /// <param name="voiceLfoLevel">Level LFO multiplier (1.0 = no swing), constant for the buffer.</param>
        /// <param name="sr">Sample rate, used for control-rate Inc recompute.</param>
        /// <param name="levelRamp">Fork (neo-buzz, §12.15): per op, its level for each sample while it glides (else null).</param>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // fork (neo-buzz): never compiled on the audio thread
        public void Render(float[] dst, int n, byte algo,
                           float feedback, float compoundAtten,
                           float lfoPitchSemis, float voiceLfoLevel,
                           int sr, float[][] levelRamp = null)
        {
            if (!Active && !_pendingTrigger && _curGain <= NearZero) return;

            // ─── Routing precompute (once per Render) ───
            byte[,] modMaskTbl = Algorithms.ModMask;
            byte    carrierMsk = Algorithms.CarrierMask[algo];

            byte mm1 = modMaskTbl[algo, 1];
            byte mm2 = modMaskTbl[algo, 2];
            byte mm3 = modMaskTbl[algo, 3];
            bool m1_0 = (mm1 & 0b001) != 0;
            bool m2_0 = (mm2 & 0b001) != 0;
            bool m2_1 = (mm2 & 0b010) != 0;
            bool m3_0 = (mm3 & 0b001) != 0;
            bool m3_1 = (mm3 & 0b010) != 0;
            bool m3_2 = (mm3 & 0b100) != 0;

            float a1 = (mm1 != 0) ? compoundAtten : 1.0f;
            float a2 = (mm2 != 0) ? compoundAtten : 1.0f;

            bool c0 = (carrierMsk & 0b0001) != 0;
            bool c1 = (carrierMsk & 0b0010) != 0;
            bool c2 = (carrierMsk & 0b0100) != 0;
            bool c3 = (carrierMsk & 0b1000) != 0;

            Operator op0 = Ops[0], op1 = Ops[1], op2 = Ops[2], op3 = Ops[3];

            // Fork (neo-buzz, §12.15): an op whose level glides takes it sample by sample; the others as before.
            float[] r0 = levelRamp?[0], r1 = levelRamp?[1], r2 = levelRamp?[2], r3 = levelRamp?[3];
            float lv0 = op0.Level, lv1 = op1.Level, lv2 = op2.Level, lv3 = op3.Level;

            bool op0Live = op0.Level > 0f || r0 != null;
            bool op1Live = op1.Level > 0f || r1 != null;
            bool op2Live = op2.Level > 0f || r2 != null;
            bool op3Live = op3.Level > 0f || r3 != null;

            // ─── Frequency / Inc setup ───
            // hzPerInc converts Hz to a uint phase increment per sample.
            double hzPerInc = 4294967296.0 / sr;

            // Fixed-frequency ops are baseHz-independent; set once per Render.
            if (op0.Fixed) op0.Inc = (uint)((double)op0.Ratio * hzPerInc);
            if (op1.Fixed) op1.Inc = (uint)((double)op1.Ratio * hzPerInc);
            if (op2.Fixed) op2.Inc = (uint)((double)op2.Ratio * hzPerInc);
            if (op3.Fixed) op3.Inc = (uint)((double)op3.Ratio * hzPerInc);

            // Initial variable-pitch Inc.
            RecomputeVarInc(op0, op1, op2, op3, hzPerInc, _curMidiF + lfoPitchSemis);

            // When pitch is static (no glide active, no pitch LFO), the
            // sub-block layer collapses to a single iteration over the whole
            // buffer — essentially the v1.1 fast path. Otherwise we run in
            // 32-sample sub-blocks and recompute Inc at each boundary.
            bool dynPitch = _glideRemainingSamples > 0 || lfoPitchSemis != 0f;

            int processed = 0;
            while (processed < n)
            {
                int thisBlock = dynPitch
                    ? Math.Min(BlockSize, n - processed)
                    : (n - processed);
                int blockEnd  = processed + thisBlock;

                // ─── Per-sample inner loop ───
                for (int s = processed; s < blockEnd; s++)
                {
                    // Anti-click ramp toward target
                    if (_curGain < _targetGain)
                    {
                        _curGain += FadeStep;
                        if (_curGain > _targetGain) _curGain = _targetGain;
                    }
                    else if (_curGain > _targetGain)
                    {
                        _curGain -= FadeStep;
                        if (_curGain < _targetGain) _curGain = _targetGain;
                    }

                    // Inline-fire deferred trigger when fade-out hits silence
                    if (_pendingTrigger && _curGain <= NearZero && _targetGain == 0f)
                    {
                        _pendingTrigger = false;
                        ApplyTriggerNow(_pendingMidi, _pendingTrack, _pendingVelocity);
                        // Pitch reset — recompute Inc for the rest of the block
                        RecomputeVarInc(op0, op1, op2, op3, hzPerInc,
                                         _curMidiF + lfoPitchSemis);
                    }

                    // ── Op0 with self-feedback (2-sample averaged) ──
                    float out0;
                    if (op0Live)
                    {
                        float e0 = op0.Env.Process();
                        float fbCycles = (_op0Prev + _op0PrevPrev) * 0.5f * feedback;
                        uint  ph0 = (fbCycles != 0f)
                            ? unchecked((uint)((long)op0.Phase + (long)(fbCycles * ModScale)))
                            : op0.Phase;
                        if (r0 != null) lv0 = r0[s] * op0.VelScale;
                        out0 = SineTable.Lookup(ph0) * lv0 * e0;
                    }
                    else
                    {
                        op0.Env.Process();
                        out0 = 0f;
                    }
                    _op0PrevPrev = _op0Prev;
                    _op0Prev     = out0;
                    op0.Phase    = unchecked(op0.Phase + op0.Inc);

                    // ── Op1 ──
                    float out1;
                    if (op1Live)
                    {
                        float e1 = op1.Env.Process();
                        float modCycles = m1_0 ? out0 : 0f;
                        uint  ph1 = (modCycles != 0f)
                            ? unchecked((uint)((long)op1.Phase + (long)(modCycles * ModScale)))
                            : op1.Phase;
                        if (r1 != null) lv1 = r1[s] * op1.VelScale;
                        out1 = SineTable.Lookup(ph1) * lv1 * e1;
                    }
                    else
                    {
                        op1.Env.Process();
                        out1 = 0f;
                    }
                    op1.Phase = unchecked(op1.Phase + op1.Inc);

                    // ── Op2 ──
                    float out2;
                    if (op2Live)
                    {
                        float e2 = op2.Env.Process();
                        float modCycles = 0f;
                        if (m2_0) modCycles += out0;
                        if (m2_1) modCycles += out1 * a1;
                        uint  ph2 = (modCycles != 0f)
                            ? unchecked((uint)((long)op2.Phase + (long)(modCycles * ModScale)))
                            : op2.Phase;
                        if (r2 != null) lv2 = r2[s] * op2.VelScale;
                        out2 = SineTable.Lookup(ph2) * lv2 * e2;
                    }
                    else
                    {
                        op2.Env.Process();
                        out2 = 0f;
                    }
                    op2.Phase = unchecked(op2.Phase + op2.Inc);

                    // ── Op3 ──
                    float out3;
                    if (op3Live)
                    {
                        float e3 = op3.Env.Process();
                        float modCycles = 0f;
                        if (m3_0) modCycles += out0;
                        if (m3_1) modCycles += out1 * a1;
                        if (m3_2) modCycles += out2 * a2;
                        uint  ph3 = (modCycles != 0f)
                            ? unchecked((uint)((long)op3.Phase + (long)(modCycles * ModScale)))
                            : op3.Phase;
                        if (r3 != null) lv3 = r3[s] * op3.VelScale;
                        out3 = SineTable.Lookup(ph3) * lv3 * e3;
                    }
                    else
                    {
                        op3.Env.Process();
                        out3 = 0f;
                    }
                    op3.Phase = unchecked(op3.Phase + op3.Inc);

                    // ── Sum carriers, apply gain + LFO level ──
                    float voiceSample = 0f;
                    if (c0) voiceSample += out0;
                    if (c1) voiceSample += out1;
                    if (c2) voiceSample += out2;
                    if (c3) voiceSample += out3;

                    dst[s] += voiceSample * _curGain * voiceLfoLevel;
                }

                // ─── Control-rate updates: glide advance + Inc recompute ───
                if (dynPitch)
                {
                    if (_glideRemainingSamples > 0)
                    {
                        int adv = Math.Min(thisBlock, _glideRemainingSamples);
                        _curMidiF              += _glideStep * adv;
                        _glideRemainingSamples -= adv;
                    }
                    if (blockEnd < n)
                        RecomputeVarInc(op0, op1, op2, op3, hzPerInc,
                                         _curMidiF + lfoPitchSemis);
                }

                processed = blockEnd;
            }

            AgeSamples += (uint)n;

            // Finalise voice once everything has gone quiet.
            if (!_pendingTrigger && _curGain <= NearZero && !Held)
            {
                if (!op0.Env.IsActive && !op1.Env.IsActive
                 && !op2.Env.IsActive && !op3.Env.IsActive)
                {
                    Active     = false;
                    TrackOwner = -1;
                }
            }
        }

        // One MathF.Pow + four mul/cast per call. Skips fixed-frequency ops
        // (their Inc is set up front and doesn't track baseHz).
        static void RecomputeVarInc(Operator op0, Operator op1,
                                    Operator op2, Operator op3,
                                    double hzPerInc, float effectiveMidi)
        {
            float baseHz = 440f * MathF.Pow(2f, (effectiveMidi - 69f) / 12f);
            if (!op0.Fixed) op0.Inc = (uint)((double)baseHz * (double)op0.RatioWithDetune * hzPerInc);
            if (!op1.Fixed) op1.Inc = (uint)((double)baseHz * (double)op1.RatioWithDetune * hzPerInc);
            if (!op2.Fixed) op2.Inc = (uint)((double)baseHz * (double)op2.RatioWithDetune * hzPerInc);
            if (!op3.Fixed) op3.Inc = (uint)((double)baseHz * (double)op3.RatioWithDetune * hzPerInc);
        }

        public static float MidiToHz(int midi, int bendCents = 0)
        {
            float semis = midi - 69 + bendCents / 100f;
            return 440f * MathF.Pow(2f, semis / 12f);
        }
    }
}
