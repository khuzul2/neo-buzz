// Envelope.cs — ADSR with click-free retrigger (SH101 §6.2 pattern).
//
// Stages: Idle → Attack → Decay → Sustain → Release → Idle.
//
// Attack uses a slight overshoot target (1.05) so exponential approach
// hits unity in finite time. NoteOn() does NOT reset _level — retrigger
// from a partial release decays smoothly from its current value, no
// click. NoteOff transitions to Release from wherever the level sits.
//
// Forced-release flag (Core §27): when set, Release uses a fixed ~5 ms
// coef instead of the user's Release time. Used by the transport-stop
// falling-edge handler so long-release patches don't ring on after Stop.

using System;

namespace WDE.PedalM1
{
    public sealed class Envelope
    {
        public enum Stage { Idle, Attack, Decay, Sustain, Release }

        Stage _stage = Stage.Idle;
        float _level = 0f;

        // Coefficients per stage — set by UpdateCoefs.
        float _aCoef = 0f, _dCoef = 0f, _rCoef = 0f;
        float _sustainLevel = 1f;

        bool  _forcedRelease;
        float _forcedReleaseCoef;

        public Stage CurrentStage => _stage;
        public bool  IsActive     => _stage != Stage.Idle;
        public float Level        => _level;

        // a, d, r in milliseconds; sustain in 0..1.
        public void UpdateCoefs(float aMs, float dMs, float sLevel, float rMs, int sr)
        {
            _aCoef        = TimeCoef(aMs, sr);
            _dCoef        = TimeCoef(dMs, sr);
            _rCoef        = TimeCoef(rMs, sr);
            _sustainLevel = Clamp01(sLevel);

            // Forced release: ~5 ms regardless of user Release.
            _forcedReleaseCoef = TimeCoef(5f, sr);
        }

        public void NoteOn()
        {
            // Don't touch _level — smooth retrigger from current value.
            _stage = Stage.Attack;
            _forcedRelease = false;
        }

        public void NoteOff()
        {
            if (_stage == Stage.Idle) return;
            _stage = Stage.Release;
        }

        public void ForcedRelease()
        {
            if (_stage == Stage.Idle) return;
            _stage = Stage.Release;
            _forcedRelease = true;
        }

        public void HardReset()
        {
            _stage = Stage.Idle;
            _level = 0f;
            _forcedRelease = false;
        }

        // Per-sample tick. Returns the current envelope value [0..1].
        public float Tick()
        {
            switch (_stage)
            {
                case Stage.Idle:
                    _level = 0f;
                    break;

                case Stage.Attack:
                {
                    const float aTarget = 1.05f;
                    _level = aTarget + (_level - aTarget) * _aCoef;
                    if (_level >= 1f) { _level = 1f; _stage = Stage.Decay; }
                    break;
                }

                case Stage.Decay:
                    _level = _sustainLevel + (_level - _sustainLevel) * _dCoef;
                    if (Math.Abs(_level - _sustainLevel) < 1e-4f)
                    {
                        _level = _sustainLevel;
                        _stage = Stage.Sustain;
                    }
                    break;

                case Stage.Sustain:
                    _level = _sustainLevel;
                    break;

                case Stage.Release:
                {
                    float coef = _forcedRelease ? _forcedReleaseCoef : _rCoef;
                    _level = _level * coef;
                    if (_level < 1e-4f)
                    {
                        _level = 0f;
                        _stage = Stage.Idle;
                        _forcedRelease = false;
                    }
                    break;
                }
            }
            return _level;
        }

        // Exponential coefficient for "reach 1/e of remaining distance in
        // <ms> milliseconds". Standard one-pole formula.
        static float TimeCoef(float ms, int sr)
        {
            if (ms < 0.1f) ms = 0.1f;
            return MathF.Exp(-1f / (ms * 0.001f * sr));
        }

        static float Clamp01(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);
    }
}
