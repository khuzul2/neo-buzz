#nullable disable
// Neo Shift's frequency shifter (docs/specs/neo-shift.md): single-sideband modulation (Bode, 1961; Moog, 1967) on a 90-degree allpass pair.
//  - The input goes through QuadratureAllpass (two allpass branches 90 degrees apart over 20 Hz ... 20 kHz: the analytic signal a + jb), which is
//    multiplied by a rotating phasor: Re{(a + jb) e^(j theta)} = a cos(theta) - b sin(theta) moves every component by the shift in hertz (every
//    partial by the same amount, so a harmonic series is bent); a negative shift moves it down.
//  - The pair is designed for 80 dB of halfband attenuation, so the sideband on the other side (the image) is rejected by more than 60 dB from
//    40 Hz to 16 kHz.
//  - A feedback (with a low-pass in it) feeds the shifted sound back in: each repeat is shifted again, a rising (positive shift and feedback) or
//    falling barber-pole.
//  - The left and right channels each have their own shift (Spread moves the right one the other way). The shifts glide (time-constant
//    <glide>, per block) and the phasors turn by rotation in double precision, renormalised every block, so a changing shift never clicks.
// No latency of its own (the allpass pair's group delay is phase, not a delay of the signal). Allocation-free; IEEE basic operations and
// PortableMath only.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Shift
{
    internal sealed class FreqShifterCore
    {
        public const double AttenuationDb = 80;
        const double GlideSeconds = 0.02;

        readonly double sr;
        readonly QuadratureAllpass qL, qR;
        OnePole fbToneL, fbToneR;
        double cL = 1, sL, cR = 1, sR;
        float fbL, fbR;
        double hzL, hzR;
        bool primed;
        int sinceFlush;

        public FreqShifterCore(double sampleRate)
        {
            sr = sampleRate;
            var coefs = new IirHalfbandDesign(AttenuationDb, Math.Min(0.24, QuadratureAllpass.LowEdgeHz / sampleRate)).Coefs;
            qL = new QuadratureAllpass(coefs);
            qR = new QuadratureAllpass(coefs);
            fbToneL.Set(4000, sampleRate);
            fbToneR.Set(4000, sampleRate);
            Reset();
        }

        public void Reset()
        {
            qL.Reset(); qR.Reset();
            fbToneL.Reset(); fbToneR.Reset();
            cL = cR = 1; sL = sR = 0;
            fbL = fbR = 0;
            primed = false;
            sinceFlush = 0;
        }

        /// <summary>The shifts now (Hz), for the editor and the tests.</summary>
        public double LeftHz => hzL;
        public double RightHz => hzR;

        /// <summary>Shifts the stereo block in place: the channels' shifts (Hz, positive up) and the feedback (-0.95 ... 0.95).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, int n, double targetL, double targetR, float feedback)
        {
            double lim = sr * 0.45;
            targetL = Math.Clamp(targetL, -lim, lim);
            targetR = Math.Clamp(targetR, -lim, lim);
            if (!primed) { hzL = targetL; hzR = targetR; primed = true; }
            double k = 1 - PortableMath.Exp(-(n / sr) / GlideSeconds);
            hzL = Snap(hzL + (targetL - hzL) * k, targetL);
            hzR = Snap(hzR + (targetR - hzR) * k, targetR);
            double wL = 2 * Math.PI * hzL / sr, wR = 2 * Math.PI * hzR / sr;
            double rcL = PortableMath.Cos(wL), rsL = PortableMath.Sin(wL), rcR = PortableMath.Cos(wR), rsR = PortableMath.Sin(wR);
            float fb = Math.Clamp(feedback, -0.95f, 0.95f);
            for (int i = 0; i < n; i++)
            {
                qL.Process(l[i] + fb * fbL, out double aL, out double bL);
                qR.Process(r[i] + fb * fbR, out double aR, out double bR);
                double yl = aL * cL - bL * sL;
                double yr = aR * cR - bR * sR;
                double ncL = cL * rcL - sL * rsL, nsL = cL * rsL + sL * rcL;
                double ncR = cR * rcR - sR * rsR, nsR = cR * rsR + sR * rcR;
                cL = ncL; sL = nsL; cR = ncR; sR = nsR;
                float ol = (float)yl, or = (float)yr;
                fbL = DspMath.Flush(fbToneL.LowPass(ol));
                fbR = DspMath.Flush(fbToneR.LowPass(or));
                l[i] = ol;
                r[i] = or;
                if (++sinceFlush >= 64)
                {
                    sinceFlush = 0;
                    qL.Flush();
                    qR.Flush();
                }
            }
            // The phasors keep their length.
            double mL = 1.0 / Math.Sqrt(cL * cL + sL * sL), mR = 1.0 / Math.Sqrt(cR * cR + sR * sR);
            cL *= mL; sL *= mL; cR *= mR; sR *= mR;
        }

        static double Snap(double v, double target) => Math.Abs(v - target) < 1e-9 ? target : v;
    }
}
