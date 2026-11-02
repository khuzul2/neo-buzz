#nullable disable
// Neo Utility (docs/specs/machines.md §5): DC removal → polarity → channel swap → width (mid/side)
// → bass mono → pan → gain. Every move is a linear ramp that lands exactly on its value, so a
// settled stage is exact and a stage at its identity is skipped: the defaults pass the input bit
// for bit. Pure: tested by NeoBuzz.MachineTests.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Utility
{
    internal enum PanLaw { Balance, MonoPan }

    internal sealed class UtilityEngine
    {
        public const int MaxBlock = 256;
        const double GlideSeconds = 0.02;
        const double SwitchSeconds = 0.005;
        const double DcHz = 5;
        const double CorrelationSeconds = 0.3;

        readonly ParamValues values;
        float sampleRate = 48000;

        // Ramps: gain, pan gains, mono amount (pan law), width, bass mono, swap, polarity, DC.
        LinearRamp gain, panL, panR, mono, width, bass, swap, polL, polR, dc;

        // Filters: DC (one section per channel), bass mono (two sections on the side signal).
        SvfSection dcL, dcR, side1, side2;
        double bassHz, bassTargetHz, bassCoeff;
        readonly double[] bassQs = Butterworth.SectionQs(4);

        // Correlation of the output (one-pole averages).
        double sLR, sLL, sRR, corrCoeff;

        public UtilityEngine(ParamValues values)
        {
            this.values = values;
            SetSampleRate(48000);
        }

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutLeft = new LevelMeter();
        public readonly LevelMeter OutRight = new LevelMeter();

        /// <summary>Output correlation, −1 … +1 (+1 mono, 0 unrelated, −1 out of phase), for the editor.</summary>
        public float Correlation { get; private set; } = 1;

        /// <summary>No latency: every stage works sample by sample.</summary>
        public int Latency => 0;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            gain.Setup(GlideSeconds, sr);
            panL.Setup(GlideSeconds, sr);
            panR.Setup(GlideSeconds, sr);
            mono.Setup(GlideSeconds, sr);
            width.Setup(GlideSeconds, sr);
            bass.Setup(GlideSeconds, sr);
            swap.Setup(SwitchSeconds, sr);
            polL.Setup(SwitchSeconds, sr);
            polR.Setup(SwitchSeconds, sr);
            dc.Setup(SwitchSeconds, sr);
            double g = SvfSection.Prewarp(DcHz, sr);
            dcL.SetHighPass(g, Math.Sqrt(0.5));
            dcR.SetHighPass(g, Math.Sqrt(0.5));
            bassCoeff = 1 - Math.Exp(-1 / (GlideSeconds * sr));
            corrCoeff = 1 - Math.Exp(-1 / (CorrelationSeconds * sr));
            InMeter.SetSampleRate(sr);
            OutLeft.SetSampleRate(sr);
            OutRight.SetSampleRate(sr);
            Update(true);
        }

        public void Reset()
        {
            dcL.Reset(); dcR.Reset(); side1.Reset(); side2.Reset();
            sLR = sLL = sRR = 0;
        }

        /// <summary>The pan stage's gains for a position (−1 … +1) and law.</summary>
        public static void PanGains(PanLaw law, double pan, out float left, out float right)
        {
            pan = Math.Clamp(pan, -1, 1);
            if (law == PanLaw.Balance)
            {
                left = (float)(pan > 0 ? 1 - pan : 1);
                right = (float)(pan < 0 ? 1 + pan : 1);
            }
            else if (pan <= -1) { left = 1; right = 0; }
            else if (pan >= 1) { left = 0; right = 1; }
            else
            {
                double theta = (pan + 1) * Math.PI / 4;
                left = (float)Math.Cos(theta);
                right = (float)Math.Sin(theta);
            }
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            double gDb = values.Value(P.Gain);
            float g = double.IsNegativeInfinity(gDb) ? 0f : (float)DspMath.DbToGain(gDb);
            var law = (PanLaw)values.Choice(P.PanLaw);
            PanGains(law, values.Value(P.Pan), out float pl, out float pr);
            float w = (float)(values.Value(P.Width) / 100.0);
            Target(ref gain, g, force);
            Target(ref panL, pl, force);
            Target(ref panR, pr, force);
            Target(ref mono, law == PanLaw.MonoPan ? 1f : 0f, force);
            Target(ref width, w, force);
            Target(ref bass, values.On(P.BassMono) ? 1f : 0f, force);
            Target(ref swap, values.On(P.Swap) ? 1f : 0f, force);
            Target(ref polL, values.On(P.InvertL) ? -1f : 1f, force);
            Target(ref polR, values.On(P.InvertR) ? -1f : 1f, force);
            Target(ref dc, values.On(P.DcRemove) ? 1f : 0f, force);
            bassTargetHz = values.Value(P.BassFreq);
            if (force || bassHz <= 0)
            {
                bassHz = bassTargetHz;
                SetBass(bassHz);
            }
            values.Clean();
        }

        static void Target(ref LinearRamp r, float v, bool force)
        {
            if (force) r.Reset(v);
            else r.SetTarget(v);
        }

        void SetBass(double hz)
        {
            double g = SvfSection.Prewarp(hz, sampleRate);
            side1.SetHighPass(g, bassQs[0]);
            side2.SetHighPass(g, bassQs[1]);
        }

        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);

            // Stages at their identity (and settled) are skipped: exact passthrough.
            bool dcOn = !dc.At(0);
            bool polOn = !(polL.At(1) && polR.At(1));
            bool swapOn = !swap.At(0);
            bool bassOn = !bass.At(0);
            bool msOn = bassOn || !width.At(1);
            bool panOn = !(mono.At(0) && panL.At(1) && panR.At(1));
            bool gainOn = !gain.At(1);
            bool bassMoving = bassOn && Math.Abs(bassHz - bassTargetHz) > 1e-9 * bassTargetHz;
            if (!dcOn) { dcL.Reset(); dcR.Reset(); }
            if (!bassOn) { side1.Reset(); side2.Reset(); bassHz = bassTargetHz; SetBass(bassHz); }

            for (int i = 0; i < n; i++)
            {
                float l = left[i], r = right[i];
                // Crossfades (switches) take the exact path once settled: l + 1·(x − l) is not x in floats.
                if (dcOn)
                {
                    float d = dc.Next();
                    float hl = (float)dcL.Process(l), hr = (float)dcR.Process(r);
                    if (d == 1) { l = hl; r = hr; }
                    else
                    {
                        l += d * (hl - l);
                        r += d * (hr - r);
                    }
                }
                if (polOn)
                {
                    l *= polL.Next();
                    r *= polR.Next();
                }
                if (swapOn)
                {
                    float s = swap.Next();
                    if (s == 1) (l, r) = (r, l);
                    else
                    {
                        float nl = l + s * (r - l);
                        r += s * (l - r);
                        l = nl;
                    }
                }
                if (msOn)
                {
                    float m = 0.5f * (l + r);
                    float side = 0.5f * (l - r) * width.Next();
                    if (bassOn)
                    {
                        if (bassMoving)
                        {
                            bassHz += (bassTargetHz - bassHz) * bassCoeff;
                            SetBass(bassHz);
                        }
                        float b = bass.Next();
                        float hp = (float)side2.Process(side1.Process(side));
                        side = b == 1 ? hp : side + b * (hp - side);
                    }
                    l = m + side;
                    r = m - side;
                }
                if (panOn)
                {
                    float mu = mono.Next();
                    if (mu == 1) l = r = 0.5f * (l + r);
                    else if (mu != 0)
                    {
                        float m = 0.5f * (l + r);
                        l += mu * (m - l);
                        r += mu * (m - r);
                    }
                    l *= panL.Next();
                    r *= panR.Next();
                }
                if (gainOn)
                {
                    float g = gain.Next();
                    l *= g;
                    r *= g;
                }
                left[i] = l;
                right[i] = r;
                sLR += (l * (double)r - sLR) * corrCoeff;
                sLL += (l * (double)l - sLL) * corrCoeff;
                sRR += (r * (double)r - sRR) * corrCoeff;
            }
            if (bassMoving && Math.Abs(bassHz - bassTargetHz) < 1e-6 * bassTargetHz)
            {
                bassHz = bassTargetHz;
                SetBass(bassHz);
            }
            dcL.Flush(); dcR.Flush(); side1.Flush(); side2.Flush();
            sLR = DspMath.Flush(sLR); sLL = DspMath.Flush(sLL); sRR = DspMath.Flush(sRR);
            double den = Math.Sqrt(sLL * sRR);
            Correlation = den > 1e-12 ? (float)Math.Clamp(sLR / den, -1, 1) : 1f;
            OutLeft.Feed(left, 0, n);
            OutRight.Feed(right, 0, n);
        }

        /// <summary>The side signal's gain from the bass mono high-pass at <paramref name="hz"/> (for the editor and tests).</summary>
        public static double BassMonoResponse(double cutoffHz, double hz, double sampleRate)
        {
            double g = SvfSection.Prewarp(cutoffHz, sampleRate);
            var qs = Butterworth.SectionQs(4);
            double m = 1;
            foreach (var q in qs) m *= SvfSection.Magnitude(g, 1 / q, 1, -1 / q, -1, hz, sampleRate);
            return m;
        }
    }
}
