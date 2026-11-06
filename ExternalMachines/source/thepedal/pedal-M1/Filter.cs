// Filter.cs — Vadim Zavalishin TPT (topology-preserving) state-variable
// filter, taking only the lowpass output. M1's VDF is famously gentle —
// almost a tone control rather than a synth filter — so we restrict the
// resonance range to never self-oscillate. k = 1/Q; Q range 0.5 .. 4.0.
//
// Coefficients are control-rate updated (SH101 §2 pattern). The cache
// short-circuits when nothing changed.

using System;

namespace WDE.PedalM1
{
    public sealed class FilterLp2
    {
        // State integrators.
        float _ic1eq, _ic2eq;

        // Coefficients (cached).
        float _a1, _a2, _a3;

        // Cache keys.
        int   _cSr  = -1;
        float _cFc  = -1f;
        float _cRes = -1f;

        public void UpdateCoefs(float fcHz, float resN, int sr)
        {
            if (sr == _cSr && fcHz == _cFc && resN == _cRes) return;
            _cSr = sr; _cFc = fcHz; _cRes = resN;

            // Pre-warp with safety cap.
            float wd = MathF.PI * fcHz / sr;
            if (wd > 1.55f) wd = 1.55f;     // π/2 ≈ 1.5708
            float g  = MathF.Tan(wd);

            // Q: 0.5 .. 4.0 → k: 2.0 .. 0.25.
            float qN = 0.5f + (resN < 0f ? 0f : (resN > 1f ? 1f : resN)) * 3.5f;
            float k  = 1f / qN;

            float den = 1f + g * (g + k);
            _a1 = 1f / den;
            _a2 = g  * _a1;
            _a3 = g  * _a2;
        }

        public void Reset()
        {
            _ic1eq = 0f;
            _ic2eq = 0f;
        }

        // Per-sample lowpass output.
        public float Process(float input)
        {
            float v3 = input - _ic2eq;
            float v1 = _a1 * _ic1eq + _a2 * v3;
            float v2 = _ic2eq + _a2 * _ic1eq + _a3 * v3;
            _ic1eq   = 2f * v1 - _ic1eq;
            _ic2eq   = 2f * v2 - _ic2eq;
            // Fork (neo-buzz, docs/specs/mcp-server.md §12.15): settle to exact zero below −300 dBFS. When a voice's
            // wave ends while its note is held, the filter rang down into the denormal floats and stayed there.
            if (_ic1eq > -1e-15f && _ic1eq < 1e-15f) _ic1eq = 0f;
            if (_ic2eq > -1e-15f && _ic2eq < 1e-15f) _ic2eq = 0f;
            return v2;     // lowpass tap
        }
    }
}
