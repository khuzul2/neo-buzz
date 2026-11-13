#nullable disable
// Neo machines shared DSP (docs/specs/dye-essence.md section 4.10, P4b1): a frequency (Bode) shifter. The input goes
// through QuadratureAllpass (two allpass branches 90 degrees apart over 20 Hz ... 20 kHz: the analytic signal a + jb,
// both phase-distorted but not otherwise changed), which is multiplied by a rotating phasor: Re{(a + jb)e^(j theta)} =
// a cos(theta) - b sin(theta) moves every component up by the shift (every partial by the same number of Hz, so the
// harmonic series is bent: metallic, inharmonic, or with a few Hz a slow phasing); a negative shift moves it down.
// Ring mode keeps both sidebands (a cos(theta)).
//  - The sideband on the other side (the image) is rejected by the allpass pair's phase error: 60 dB over most of the band
//    (measured in DyeEssenceFxTests).
//  - A feedback (with a tone filter in it) feeds the shifted signal back: spirals of rising or falling echoes of the shift.
//  - The two channels can differ by a stereo offset, and an LFO moves the shift.
//  - The phasor turns by rotation (complex multiplication) in double precision, renormalised every block, its rate
//    (the shift, smoothed) set every block: IEEE basic operations and PortableMath only.
// Wet only; no latency to report (the allpass pair's group delay is the phase distortion, not a delay of the signal).
// Allocation-free after construction.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal sealed class FrequencyShifter
    {
        readonly float sr;
        readonly QuadratureAllpass qL, qR;
        OnePole fbToneL, fbToneR;
        double cL = 1, sL, cR = 1, sR;
        float fbL, fbR, lfoPhase;
        double smoothShift;
        bool primed;
        double toneApplied = -1;

        /// <summary>The shift (Hz), positive up.</summary>
        public double ShiftHz;
        /// <summary>Ring modulation (both sidebands) instead of a shift.</summary>
        public bool Ring;
        /// <summary>-0.95 ... 0.95.</summary>
        public float Feedback;
        public double FeedbackToneHz = 3000;
        /// <summary>The right channel's shift minus the left's is twice this (Hz).</summary>
        public double StereoHz;
        public float LfoRateHz = 0.5f;
        /// <summary>The LFO's swing of the shift (Hz).</summary>
        public double LfoHz;
        /// <summary>The smoothing of the shift (seconds): a changed shift glides.</summary>
        public double GlideSeconds = 0.05;
        public float Output = 1;

        public FrequencyShifter(float sampleRate)
        {
            sr = sampleRate;
            qL = new QuadratureAllpass(sampleRate);
            qR = new QuadratureAllpass(sampleRate);
            Reset();
        }

        public int Latency => 0;

        public void Reset()
        {
            qL.Reset(); qR.Reset();
            fbToneL.Reset(); fbToneR.Reset();
            cL = cR = 1; sL = sR = 0;
            fbL = fbR = 0;
            lfoPhase = 0;
            primed = false;
            toneApplied = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Sine(float cycles)
        {
            float x = cycles - MathF.Floor(cycles + 0.5f);
            float y = 4 * x * (1 - 2 * MathF.Abs(x));
            return 0.225f * (y * MathF.Abs(y) - y) + y;
        }

        /// <summary>Turns the stereo block into its wet signal, in place.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, int n)
        {
            double tone = Math.Clamp(FeedbackToneHz, 100, sr * 0.45);
            if (tone != toneApplied)
            {
                toneApplied = tone;
                fbToneL.Set(tone, sr);
                fbToneR.Set(tone, sr);
            }
            double target = ShiftHz;
            if (!primed) { smoothShift = target; primed = true; }
            double k = GlideSeconds <= 1e-4 ? 1 : 1 - PortableMath.Exp(-(n / (double)sr) / GlideSeconds);
            smoothShift += (target - smoothShift) * k;
            float lfo = LfoHz == 0 ? 0 : Sine(lfoPhase);
            lfoPhase += Math.Clamp(LfoRateHz, 0.01f, 30f) * n / sr;
            lfoPhase -= MathF.Floor(lfoPhase);
            double centre = smoothShift + LfoHz * lfo;
            double hzL = Math.Clamp(centre - StereoHz, -sr * 0.45, sr * 0.45), hzR = Math.Clamp(centre + StereoHz, -sr * 0.45, sr * 0.45);
            double wL = 2 * Math.PI * hzL / sr, wR = 2 * Math.PI * hzR / sr;
            double rcL = PortableMath.Cos(wL), rsL = PortableMath.Sin(wL), rcR = PortableMath.Cos(wR), rsR = PortableMath.Sin(wR);
            float fb = Math.Clamp(Feedback, -0.95f, 0.95f);
            bool ring = Ring;
            float gain = Output;
            for (int i = 0; i < n; i++)
            {
                qL.Process(l[i] + fb * fbL, out double aL, out double bL);
                qR.Process(r[i] + fb * fbR, out double aR, out double bR);
                double yl = ring ? aL * cL : aL * cL - bL * sL;
                double yr = ring ? aR * cR : aR * cR - bR * sR;
                double ncL = cL * rcL - sL * rsL, nsL = cL * rsL + sL * rcL;
                double ncR = cR * rcR - sR * rsR, nsR = cR * rsR + sR * rcR;
                cL = ncL; sL = nsL; cR = ncR; sR = nsR;
                float ol = (float)yl, or = (float)yr;
                fbL = DspMath.Flush(fbToneL.LowPass(ol));
                fbR = DspMath.Flush(fbToneR.LowPass(or));
                l[i] = ol * gain;
                r[i] = or * gain;
            }
            // The phasors keep their length.
            double mL = 1.0 / Math.Sqrt(cL * cL + sL * sL), mR = 1.0 / Math.Sqrt(cR * cR + sR * sR);
            cL *= mL; sL *= mL; cR *= mR; sR *= mR;
        }
    }
}
