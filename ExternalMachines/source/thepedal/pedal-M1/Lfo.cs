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
using System.Runtime.CompilerServices;

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
        /// <summary>
        /// Fork (neo-buzz, docs/specs/mcp-server.md §12.15): <paramref name="n"/> ticks' worth of time for both of a voice's
        /// LFOs, their output unread: the same steps as Tick (the phase, its random values at each wrap, the delay and fade
        /// counters), for a voice whose note has ended, so its next note's random values are what they were. The phase
        /// moves by whole runs of equal steps (Advance), not sample by sample: the ended voices cost next to nothing.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // on the audio thread: never tiered up there
        public static void Skip(Lfo a, Lfo b, int n)
        {
            a.Advance(n - a.SkipCounting(n));
            b.Advance(n - b.SkipCounting(n));
        }

        /// <summary>
        /// Fork (neo-buzz, §12.15, follow-ups): <paramref name="n"/> of Tick's phase steps once its delay and fade are
        /// done (<c>_phase += _phaseInc</c>, a wrap at 1 drawing two random values), to the same float, in a few runs.
        /// Within a binade [2^e, 2^(e+1)) every float is a multiple of its ulp u, so <c>p + inc</c> rounds to
        /// <c>p + d</c> with the same d for every p there whose exact sum stays in it (d depends only on inc's remainder
        /// below u, except when that remainder is exactly u / 2 and the rounding goes to even: taken a step at a time).
        /// A run takes all but the last step or two before the binade's top, or 1, in one exact double sum; those and
        /// every wrap are taken as Tick takes them. It replaced 256 dependent float adds a block per LFO (half a
        /// millisecond a second for M1's four ended voices, half again its cost before any note).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // on the audio thread
        internal void Advance(int n)
        {
            float p = _phase, inc = _phaseInc;
            while (n > 0)
            {
                // A run of equal steps, where p's binade allows one.
                int bits = BitConverter.SingleToInt32Bits(p);
                int exp = (bits >> 23) & 0xFF;
                if (p > 0f && exp != 0)
                {
                    double u = Math.ScaleB(1.0, exp - 150);          // p's ulp
                    double top = Math.ScaleB(1.0, exp - 126);        // the binade's top (1 for [0.5, 1))
                    double units = Math.Floor(inc / u);
                    double r = inc - units * u;                      // exact: inc and u are floats, u a power of 2
                    if (r + r != u)                                  // not a tie
                    {
                        double d = units * u + (r + r > u ? u : 0.0);
                        if (d == 0.0) { _phase = p; return; }       // inc is under half p's ulp: the phase never moves
                        double room = (top - Math.Max(d, inc) - p) / d;
                        if (room > 2.0)
                        {
                            int k = room - 1.0 >= n ? n : (int)(room - 1.0);
                            p = (float)(p + k * d);                  // a multiple of u below top: exactly a float
                            n -= k;
                            continue;
                        }
                    }
                }
                // One step as Tick takes it.
                p += inc;
                n--;
                if (p >= 1f) { _phase = p; Wrap(); p = _phase; }
            }
            _phase = p;
        }

        /// <summary>Tick's steps while its delay or fade counts (rarely, after a note); the samples it took.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        int SkipCounting(int n)
        {
            int i = 0;
            for (; i < n && (_delayCounter > 0 || (_fadeCounter > 0 && _fadeSamples > 0)); i++)
            {
                if (_delayCounter > 0)
                {
                    _delayCounter--;
                    _phase += _phaseInc;
                    if (_phase >= 1f) _phase -= 1f;
                    continue;
                }
                _fadeCounter--;
                _phase += _phaseInc;
                if (_phase >= 1f) Wrap();
            }
            return i;
        }

        void Wrap()
        {
            _phase -= 1f;
            _shCurrent = NextRandom();
            _smoothFrom = _smoothTo;
            _smoothTo   = NextRandom();
        }

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
