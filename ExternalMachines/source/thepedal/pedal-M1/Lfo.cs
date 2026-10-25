// Lfo.cs — per-voice LFO with delay + fade-in.
//
// Per-voice (not shared) so each voice has its own LFO phase, restarted
// at note-on. Phase advances during silence too (SH101 §10 issue) — the
// caller must Tick() the LFO every Work() sample regardless of voice
// activity to keep behaviour authentic to a free-running hardware LFO.
//
// Waveforms: Triangle, Sawtooth (down), Square, Random S/H, Random smooth.
//
// Delay: hold output at 0 for D ms after note-on.
// Fade-in: linear ramp from 0 to 1 over F ms after delay expires.

using System;

namespace WDE.PedalM1
{
    public enum LfoWave
    {
        Triangle = 0,
        Sawtooth = 1,
        Square   = 2,
        SampleHold = 3,
        Smooth = 4,
    }

    public sealed class Lfo
    {
        float _phase = 0f;             // 0..1
        float _phaseInc = 0f;          // per-sample increment
        int   _delaySamples;
        int   _delayCounter;
        int   _fadeSamples;
        int   _fadeCounter;
        LfoWave _wave;

        // Random-source state.
        uint  _rngState = 0xA1B2C3D4u;
        float _shCurrent = 0f;
        float _smoothFrom = 0f;
        float _smoothTo   = 0f;

        public void UpdateCoefs(float speedHz, float delayMs, float fadeMs, LfoWave wave, int sr)
        {
            if (speedHz < 0.001f) speedHz = 0.001f;
            _phaseInc = speedHz / sr;
            _delaySamples = Math.Max(0, (int)(delayMs * 0.001f * sr));
            _fadeSamples  = Math.Max(0, (int)(fadeMs  * 0.001f * sr));
            _wave = wave;
        }

        public void NoteOn()
        {
            _phase        = 0f;
            _delayCounter = _delaySamples;
            _fadeCounter  = _fadeSamples;
            _shCurrent    = NextRandom();
            _smoothFrom   = _shCurrent;
            _smoothTo     = NextRandom();
        }

        // Per-sample tick. Returns LFO value in [-1, +1] scaled by fade-in.
        // During delay returns 0.
        public float Tick()
        {
            if (_delayCounter > 0)
            {
                _delayCounter--;
                _phase += _phaseInc;
                if (_phase >= 1f) _phase -= 1f;
                return 0f;
            }

            float v;
            switch (_wave)
            {
                case LfoWave.Triangle:
                {
                    // 0..0.5 → -1..+1 ramp; 0.5..1 → +1..-1 ramp.
                    v = _phase < 0.5f
                        ? (_phase * 4f - 1f)
                        : (3f - _phase * 4f);
                    break;
                }
                case LfoWave.Sawtooth:
                {
                    // Falling sawtooth: 0 → +1, advances to -1 over period.
                    v = 1f - _phase * 2f;
                    break;
                }
                case LfoWave.Square:
                {
                    v = _phase < 0.5f ? 1f : -1f;
                    break;
                }
                case LfoWave.SampleHold:
                {
                    v = _shCurrent;
                    break;
                }
                case LfoWave.Smooth:
                {
                    // Linearly interpolate between random targets across the period.
                    v = _smoothFrom + (_smoothTo - _smoothFrom) * _phase;
                    break;
                }
                default:
                    v = 0f;
                    break;
            }

            // Apply fade-in.
            float fadeAmt = 1f;
            if (_fadeCounter > 0 && _fadeSamples > 0)
            {
                fadeAmt = 1f - (_fadeCounter / (float)_fadeSamples);
                _fadeCounter--;
            }

            // Advance phase, handling wrap.
            _phase += _phaseInc;
            if (_phase >= 1f)
            {
                _phase -= 1f;
                // New random target on every full cycle.
                _shCurrent = NextRandom();
                _smoothFrom = _smoothTo;
                _smoothTo   = NextRandom();
            }

            return v * fadeAmt;
        }

        // xorshift32 — fast PRNG, audio-thread safe.
        float NextRandom()
        {
            uint s = _rngState;
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            _rngState = s;
            // Map to [-1, +1].
            return (s / (float)uint.MaxValue) * 2f - 1f;
        }
    }
}
