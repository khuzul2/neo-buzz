#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4): the virtual-analog oscillator. One phase
// drives four band-limited components (sine, triangle, saw, pulse) mixed by a continuous shape;
// hard sync and through-zero FM keep every discontinuity band-limited (BLEP for jumps, BLAMP for
// slope changes). Output runs BlepTable.Z samples late.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal sealed class BlepOscillator
    {
        double phase;
        BlepBuffer buf;
        float wSine, wTri, wSaw, wSq;
        float pw = 0.5f;

        public BlepOscillator()
        {
            buf.Init();
        }

        /// <summary>Current phase, 0 … 1.</summary>
        public double Phase => phase;

        public void Reset(double startPhase = 0)
        {
            phase = startPhase - Math.Floor(startPhase);
            buf.Reset();
        }

        /// <summary>
        /// Shape 0 … 3: sine → triangle → saw → pulse (each step a crossfade between band-limited
        /// waves, so morphing never aliases). <paramref name="pulseWidth"/> 0.5 is square.
        /// </summary>
        public void SetShape(float shape, float pulseWidth)
        {
            shape = DspMath.Clamp(shape, 0f, 3f);
            wSine = wTri = wSaw = wSq = 0;
            if (shape < 1) { wSine = 1 - shape; wTri = shape; }
            else if (shape < 2) { wTri = 2 - shape; wSaw = shape - 1; }
            else { wSaw = 3 - shape; wSq = shape - 2; }
            pw = DspMath.Clamp(pulseWidth, 0.02f, 0.98f);
        }

        /// <summary>Waveform value at a phase (naive).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float Value(double p)
        {
            float v = 0;
            if (wSine != 0) v += wSine * -MathF.Cos((float)(DspMath.TwoPiD * p));
            if (wTri != 0) v += wTri * (p < 0.5 ? (float)(-1 + 4 * p) : (float)(3 - 4 * p));
            if (wSaw != 0) v += wSaw * (float)(2 * p - 1);
            if (wSq != 0) v += wSq * (p < pw ? -1f : 1f);
            return v;
        }

        /// <summary>d value / d phase.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float Slope(double p)
        {
            float s = 0;
            if (wSine != 0) s += wSine * DspMath.TwoPi * MathF.Sin((float)(DspMath.TwoPiD * p));
            if (wTri != 0) s += wTri * (p < 0.5 ? 4f : -4f);
            if (wSaw != 0) s += wSaw * 2f;
            return s;
        }

        /// <summary>
        /// Moves the phase by <paramref name="inc"/> × <paramref name="fraction"/> of a sample,
        /// starting <paramref name="startAgo"/> samples before the current one, adding the
        /// corrections for every breakpoint crossed. Returns whether it wrapped forward, and when.
        /// </summary>
        bool Advance(double inc, double startAgo, double fraction, out double wrapAgo)
        {
            wrapAgo = 0;
            double p = phase;
            // Fast path: no breakpoint crossed in this step (almost every sample).
            double end = p + inc * fraction;
            if (inc > 0)
            {
                double next = 1.0;
                if (wTri != 0 && p < 0.5) next = 0.5;
                if (wSq != 0 && p < pw && pw < next) next = pw;
                if (end < next)
                {
                    phase = end;
                    return false;
                }
            }
            else if (inc < 0)
            {
                double next = 0.0;
                if (wTri != 0 && p > 0.5) next = 0.5;
                if (wSq != 0 && p > pw && pw > next) next = pw;
                if (end > next)
                {
                    phase = end;
                    return false;
                }
            }
            else return false;
            bool wrapped = false;
            double t = 0;   // elapsed, in samples, of this segment
            int guard = 0;
            if (inc > 0)
            {
                while (guard++ < 64)
                {
                    double next = 1.0;
                    if (wTri != 0 && p < 0.5) next = 0.5;
                    if (wSq != 0 && p < pw && pw < next) next = pw;
                    double dt = (next - p) / inc;
                    if (t + dt > fraction) { p += (fraction - t) * inc; break; }
                    t += dt;
                    float ago = (float)(startAgo - t);
                    if (ago < 0) ago = 0; else if (ago > 1) ago = 1;
                    if (next >= 1.0)
                    {
                        if (wSaw != 0 || wSq != 0) buf.AddStep(-2f * (wSaw + wSq), ago);
                        if (wTri != 0) buf.AddRamp((float)(8 * inc) * wTri, ago);
                        p = 0;
                        wrapped = true;
                        wrapAgo = ago;
                    }
                    else if (next == 0.5 && wTri != 0 && (wSq == 0 || pw != 0.5))
                    {
                        buf.AddRamp((float)(-8 * inc) * wTri, ago);
                        p = 0.5;
                    }
                    else
                    {
                        // pw (possibly == 0.5 together with the triangle's corner)
                        buf.AddStep(2f * wSq, ago);
                        if (wTri != 0 && next == 0.5) buf.AddRamp((float)(-8 * inc) * wTri, ago);
                        p = next;
                    }
                }
            }
            else if (inc < 0)
            {
                while (guard++ < 64)
                {
                    double next = 0.0;
                    if (wTri != 0 && p > 0.5) next = 0.5;
                    if (wSq != 0 && p > pw && pw > next) next = pw;
                    double dt = (next - p) / inc;   // positive
                    if (t + dt > fraction) { p += (fraction - t) * inc; break; }
                    t += dt;
                    float ago = (float)(startAgo - t);
                    if (ago < 0) ago = 0; else if (ago > 1) ago = 1;
                    if (next <= 0.0)
                    {
                        if (wSaw != 0 || wSq != 0) buf.AddStep(2f * (wSaw + wSq), ago);
                        if (wTri != 0) buf.AddRamp((float)(-8 * inc) * wTri, ago);
                        p = 1.0;
                    }
                    else
                    {
                        if (wSq != 0 && next == pw) buf.AddStep(-2f * wSq, ago);
                        if (wTri != 0 && next == 0.5) buf.AddRamp((float)(8 * inc) * wTri, ago);
                        p = next;
                    }
                    // Moving down from exactly a breakpoint: step off it so it isn't found again.
                    if (p == next) p = Math.BitDecrement(p);
                }
                if (p >= 1.0) p = Math.BitDecrement(1.0);
            }
            phase = p >= 1.0 ? p - 1.0 : p < 0 ? p + 1.0 : p;
            return wrapped;
        }

        /// <summary>
        /// One sample of a free-running oscillator. <paramref name="inc"/> is cycles per sample
        /// (negative runs backwards: through-zero FM). Reports a forward wrap for syncing others.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Next(double inc, out bool wrapped, out double wrapAgo)
        {
            wrapped = Advance(inc, 1.0, 1.0, out wrapAgo);
            return buf.Next(Value(phase));
        }

        /// <summary>The naive value at the current phase (no latency): a modulation source.</summary>
        public float NaiveValue => Value(phase);

        /// <summary>
        /// One sample of an oscillator hard-synced to a master that wrapped
        /// <paramref name="masterAgo"/> samples ago in this sample (when <paramref name="sync"/>).
        /// </summary>
        public float NextSynced(double inc, bool sync, double masterAgo)
        {
            if (!sync)
                return Next(inc, out _, out _);
            double before = 1.0 - masterAgo;
            Advance(inc, 1.0, before, out _);
            // Reset at the master's wrap.
            float ago = (float)Math.Clamp(masterAgo, 0, 1);
            double pr = phase;
            float dv = Value(0) - Value(pr);
            float ds = (Slope(0) - Slope(pr)) * (float)inc;
            if (dv != 0) buf.AddStep(dv, ago);
            if (ds != 0) buf.AddRamp(ds, ago);
            phase = 0;
            Advance(inc, masterAgo, masterAgo, out _);
            return buf.Next(Value(phase));
        }
    }
}
