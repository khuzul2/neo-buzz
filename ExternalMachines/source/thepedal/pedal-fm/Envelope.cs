using System;
using System.Runtime.CompilerServices;

namespace PedalFM
{
    /// <summary>
    /// Per-operator ADSR envelope.
    ///
    /// Attack is linear (the perceptually expected shape for a pluck/strike).
    /// Decay and release are exponential (one-pole ramp toward target).
    /// Sustain is a level (0..1), not a rate.
    ///
    /// Coefficients are recomputed only when the input values actually change
    /// (PedalComp §6 dirty-check pattern).
    /// </summary>
    internal sealed class Envelope
    {
        public enum Stage : byte { Idle, Attack, Decay, Sustain, Release }

        public  Stage CurStage = Stage.Idle;
        public  float Level    = 0f;       // 0..1 — current envelope output

        // Cached config for dirty-check
        float _aSec  = -1f, _dSec = -1f, _rSec = -1f, _sLvl = -1f;
        int   _sr    = -1;

        // Derived rates (per-sample increments / pole coefficients)
        float _attackInc      = 0f;       // per-sample +linear toward 1.0
        float _decayPole      = 0f;       // 1-step pole toward sustain
        float _releasePole    = 0f;       // 1-step pole toward 0
        float _sustainTarget  = 0f;

        // Floor below which release auto-completes (avoids infinitely small tails)
        const float ReleaseFloor = 1e-4f;

        public bool IsActive => CurStage != Stage.Idle;

        /// <summary>
        /// Recomputes coefficients only on parameter change. Cheap when nothing
        /// changed (4 int compares).
        /// </summary>
        public void Configure(float attackSec, float decaySec, float sustain01,
                              float releaseSec, int sampleRate)
        {
            if (sampleRate == _sr  &&
                attackSec  == _aSec && decaySec   == _dSec &&
                sustain01  == _sLvl && releaseSec == _rSec)
                return;

            _sr   = sampleRate;
            _aSec = attackSec;
            _dSec = decaySec;
            _sLvl = sustain01;
            _rSec = releaseSec;

            _sustainTarget = sustain01;

            // Linear attack: traverse 0->1 in attackSec seconds.
            _attackInc = (attackSec <= 0f)
                ? 1f
                : 1f / (attackSec * sampleRate);

            // Exponential decay: 1-pole filter toward sustainTarget.
            // pole = exp(-1 / (timeSec * sr)) is the canonical coefficient.
            _decayPole   = (decaySec   <= 0f) ? 0f : MathF.Exp(-1f / (decaySec   * sampleRate));
            _releasePole = (releaseSec <= 0f) ? 0f : MathF.Exp(-1f / (releaseSec * sampleRate));
        }

        public void NoteOn()
        {
            CurStage = Stage.Attack;
            // Don't reset Level to 0 if we're stealing a sounding voice — let
            // it ride from where it was. The voice-level anti-click envelope
            // handles the perceptual gap.
        }

        public void NoteOff()
        {
            if (CurStage == Stage.Idle) return;
            CurStage = Stage.Release;
        }

        /// <summary>Hard reset (for fresh voice allocation).</summary>
        public void Reset()
        {
            CurStage = Stage.Idle;
            Level    = 0f;
        }

        /// <summary>Advance one sample; return current level.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process()
        {
            switch (CurStage)
            {
                case Stage.Attack:
                    Level += _attackInc;
                    if (Level >= 1f)
                    {
                        Level = 1f;
                        CurStage = Stage.Decay;
                    }
                    break;

                case Stage.Decay:
                    // 1-pole toward sustainTarget
                    Level = _sustainTarget + (Level - _sustainTarget) * _decayPole;
                    if (MathF.Abs(Level - _sustainTarget) < 1e-5f)
                    {
                        Level = _sustainTarget;
                        CurStage = Stage.Sustain;
                    }
                    break;

                case Stage.Sustain:
                    Level = _sustainTarget;
                    break;

                case Stage.Release:
                    Level *= _releasePole;
                    if (Level < ReleaseFloor)
                    {
                        Level = 0f;
                        CurStage = Stage.Idle;
                    }
                    break;

                case Stage.Idle:
                    Level = 0f;
                    break;
            }
            return Level;
        }
    }
}
