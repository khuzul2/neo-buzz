using System;

namespace PedalJuno106
{
    /// <summary>
    /// One-pole DC-blocking high-pass, one instance per voice.
    ///
    /// The DCO's pulse carries a DC offset whenever its width is off 50%: a
    /// pulse of width <c>pw</c> has an intrinsic mean of <c>2·pw − 1</c>, so a
    /// PWM patch sits at a non-zero (and, under LFO→PWM, wandering) offset. The
    /// saw and the 50% sub are ~zero-mean; the pulse is the source. The ladder
    /// filter is a low-pass (unity gain at DC), so the offset passes straight
    /// through. Real Juno hardware AC-couples the output — this is the software
    /// equivalent. (The panel HPF would also block it, but only at positions
    /// 1–3; position 0 = bypass passes DC, so we can't rely on it.)
    ///
    /// Placement (see PedalJuno106.Work): applied to each voice's filter output
    /// BEFORE that voice's VCA/anti-click gain. The offset is a property of the
    /// tone, not the amplitude, so blocking pre-VCA removes a constant DC from a
    /// constant-DC signal — no envelope-scaled DC ramp to thump at note-on, and
    /// (being per-voice) no cross-voice DC pumping as voices come and go. State
    /// is not reset per note (like the cap it models, it holds between notes);
    /// only sample-rate change updates the coefficient.
    ///
    /// y[n] = x[n] − x[n−1] + R·y[n−1];  zero at DC (z=1), pole at z=R,
    /// corner ≈ (1−R)·sr / 2π.
    /// </summary>
    internal sealed class DcBlocker
    {
        float _xPrev;
        float _yPrev;
        float _r = 0.9999f;   // sane default until SetCorner runs

        /// <summary>R = exp(−2π·fc/sr). Call on sample-rate change (Core §29).</summary>
        public void SetCorner(float fcHz, float sr)
        {
            _r = MathF.Exp(-2f * MathF.PI * fcHz / sr);
        }

        /// <summary>Wipe history. Full reset only (panic / voice init); not per note.</summary>
        public void Reset()
        {
            _xPrev = 0f;
            _yPrev = 0f;
        }

        public float Process(float x)
        {
            float y = x - _xPrev + _r * _yPrev;
            _xPrev = x;

            // Denormal flush (Core §30): the pole at R < 1 lets y decay toward
            // zero as the voice quietens; left alone the tail can reach denormal
            // range and trap. Explicit compare-flush is more robust than the
            // ±1e-25 trick under the JIT.
            if (y > -1e-20f && y < 1e-20f) y = 0f;
            _yPrev = y;

            return y;
        }
    }
}
