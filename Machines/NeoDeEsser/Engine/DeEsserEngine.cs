#nullable disable
// Neo De-esser (docs/specs/mastering-tools.md §5.12).
//  - The detector is KeyDetector's band (a second-order high-pass and low-pass around Frequency, Width octaves wide) read
//    as a linked peak level. Relative: the band's level minus the whole signal's (a second KeyDetector without filters);
//    nothing is read as sibilance while the whole signal is under −70 dBFS. Absolute: the band's level in dBFS.
//  - The reduction is the excess over Threshold times (1 − 1/Ratio), at most Max reduction, smoothed in dB with Attack
//    and Release (Detector.Smooth). It is one number for both channels (the detector is linked).
//  - The band is scaled to gain 1 at its centre, so a tone there is read, and cut, at its own level.
//  - Action: Split y = x + (g − 1)·band, g = the reduction as a gain: the band is the detector's own band-pass (minimum
//    phase, no latency of its own) or a linear-phase FIR of the same centre and width (511 taps, a symmetric
//    windowed-sinc band-pass, latency 255); Shelf and Bell run the audio through a high shelf or a peak filter whose
//    gain is −reduction (coefficients every 8 samples of a fixed counter, so they never depend on the host's blocks).
//    With no reduction every Action returns the delayed input bit for bit (the FIR is not even evaluated).
//  - Lookahead delays the audio (and the band) so the reduction is in place when the sibilant arrives. The latency
//    reported is the lookahead plus the FIR's 255 in Split linear; Delta delays the dry by it.
//  - A new Frequency or Width redesigns the FIR into a second set and crossfades to it over 512 samples (no click).
//  - Listen outputs the band the detector hears (delayed by the lookahead). Delta: the output minus the input.
// Allocation-free after construction; the portable maths and double arithmetic, fixed order.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Analysis;
using NeoBuzz.Dsp;

namespace NeoBuzz.DeEsser
{
    internal sealed class DeEsserEngine
    {
        public const int MaxBlock = 256;
        public const int FirHalf = 255;
        public const int FirTaps = 2 * FirHalf + 1;
        public const double MaxLookahead = 0.005;
        const int FadeLength = 512;
        const int HistSize = 2048;
        const double SilenceDb = -70;

        readonly ParamValues values;
        float sampleRate = 48000;
        readonly KeyDetector band = new KeyDetector();
        readonly KeyDetector full = new KeyDetector();
        DelayLineF audioL, audioR, bandL, bandR;
        readonly float[] histL = new float[HistSize], histR = new float[HistSize];
        int histPos;
        readonly double[] firCur = new double[FirHalf + 1], firNext = new double[FirHalf + 1];
        int fade;                       // 0: no crossfade; else the samples done
        bool redesign;                  // a Frequency / Width change is waiting for the crossfade to finish
        Biquad wideL, wideR;
        double wideSet = -1;            // the reduction the shelf / bell was last set for
        int count;
        readonly float[] dryL = new float[MaxBlock], dryR = new float[MaxBlock];
        SmoothRamp output;
        public readonly DeltaListen Delta;
        public readonly GainMatch Match;
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        // parameters
        bool relative, listen;
        int action;
        double threshold, slope, range, attackA, releaseA, freq, widthOct;
        int lookahead;
        double red;
        double hold, blockMax, pubMatch = double.NaN, pubRed;
        int pubLatency;
        float bandNorm = 1;             // 1 / the band-pass's gain at the centre, so a tone there reads (and is reduced) at its own level

        public DeEsserEngine(ParamValues values)
        {
            this.values = values;
            Delta = new DeltaListen(FirHalf + 1 + (int)Math.Ceiling(MaxLookahead * 192000), 48000);
            Match = new GainMatch(48000);
        }

        /// <summary>Samples the audio is delayed by: the lookahead, plus the linear-phase band's 255 in Split linear.</summary>
        public int Latency => lookahead + (action == 1 ? FirHalf : 0);

        public double ReductionDb => Volatile.Read(ref pubRed);
        public double GainMatchDb => Volatile.Read(ref pubMatch);

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            int max = (int)Math.Ceiling(MaxLookahead * sr) + 2;
            audioL = new DelayLineF(max + FirHalf + 2);
            audioR = new DelayLineF(max + FirHalf + 2);
            bandL = new DelayLineF(max + 2);
            bandR = new DelayLineF(max + 2);
            output.Setup(0.02, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            Delta.SetSampleRate(sr);
            Match.SetRate((int)Math.Round(sr));
            Update(true);
            Reset();
        }

        public void Reset()
        {
            band.Reset();
            full.Reset();
            audioL.Reset(); audioR.Reset(); bandL.Reset(); bandR.Reset();
            Array.Clear(histL); Array.Clear(histR);
            histPos = 0;
            wideL.Reset(); wideR.Reset();
            wideSet = -1;
            fade = 0;
            red = hold = blockMax = 0;
            Delta.Reset();
            Match.Reset();
            count = 0;
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            relative = values.Choice(P.Mode) == 0;
            double f = Math.Min(values.Value(P.Freq), 0.45 * sampleRate);
            double w = values.Value(P.Width);
            bool bandChanged = force || f != freq || w != widthOct;
            freq = f;
            widthOct = w;
            if (bandChanged)
            {
                double half = PortableMath.Pow(2, 0.5 * w);
                Corners(f, half, sampleRate, out double h1, out double h2);
                band.SetBand(h1, h2, sampleRate);
                var hp = new Biquad(); var lp = new Biquad();
                hp.Set(BiquadType.HighPass, h1, sampleRate, 0.7071067811865476);
                lp.Set(BiquadType.LowPass, h2, sampleRate, 0.7071067811865476);
                bandNorm = (float)(1 / Math.Max(1e-3, hp.Magnitude(f, sampleRate) * lp.Magnitude(f, sampleRate)));
            }
            band.SetDetector(false, 0.0001, 0.02, sampleRate);
            if (force) full.SetBand(0, 0, sampleRate);
            full.SetDetector(false, 0.0001, 0.02, sampleRate);
            threshold = values.Value(P.Threshold);
            slope = 1 - 1 / values.Value(P.Ratio);
            range = values.Value(P.Range);
            attackA = Detector.Coef(values.Value(P.Attack), sampleRate);
            releaseA = Detector.Coef(values.Value(P.Release), sampleRate);
            action = values.Choice(P.Action);
            lookahead = Math.Clamp((int)Math.Round(values.Value(P.Lookahead) * 0.001 * sampleRate), 0, (int)Math.Ceiling(MaxLookahead * sampleRate));
            listen = values.On(P.Listen);
            wideSet = -1;
            if (bandChanged)
            {
                if (force) { Design(firCur); fade = 0; redesign = false; }
                else redesign = true;
            }
            Delta.Latency = Latency;
            pubLatency = Latency;
            Target(ref output, (float)DspMath.DbToGain(values.Value(P.Output)), force);
            if (force) Delta.Set(values.On(P.Delta));
            else Delta.On = values.On(P.Delta);
            values.Clean();
        }

        /// <summary>The high-pass and low-pass corners, symmetric about <paramref name="f0"/> in the bilinear (warped) domain: the digital
        /// band-pass has zero phase at the centre, so a tone there is cut by exactly the reduction.</summary>
        static void Corners(double f0, double half, double sr, out double f1, out double f2)
        {
            double t0 = PortableMath.Tan(Math.PI * f0 / sr);
            f1 = Math.Max(20, PortableMath.Atan(t0 / half) * sr / Math.PI);
            f2 = Math.Min(0.45 * sr, PortableMath.Atan(t0 * half) * sr / Math.PI);
        }

        static void Target(ref SmoothRamp r, float v, bool force)
        {
            if (force) r.Reset(v);
            else r.SetTarget(v);
        }

        /// <summary>The linear-phase band: a Blackman-windowed band-pass, half of it (the other half mirrors), gain 1 at the centre.</summary>
        void Design(double[] h)
        {
            double half = PortableMath.Pow(2, 0.5 * widthOct);
            double f1 = Math.Max(20, freq / half), f2 = Math.Min(0.45 * sampleRate, freq * half);
            double w1 = 2 * Math.PI * f1 / sampleRate, w2 = 2 * Math.PI * f2 / sampleRate;
            double sum = 0;
            for (int k = 0; k <= FirHalf; k++)
            {
                double ideal = k == 0 ? (w2 - w1) / Math.PI : (PortableMath.Sin(w2 * k) - PortableMath.Sin(w1 * k)) / (Math.PI * k);
                double a = 2 * Math.PI * (k + FirHalf) / (FirTaps - 1);
                double win = 0.42 - 0.5 * PortableMath.Cos(a) + 0.08 * PortableMath.Cos(2 * a);
                h[k] = ideal * win;
            }
            // Normalise the gain at the centre to 1 (the sum of h[k]·cos(w0·k) over both halves).
            double w0 = 2 * Math.PI * freq / sampleRate;
            sum = h[0];
            for (int k = 1; k <= FirHalf; k++) sum += 2 * h[k] * PortableMath.Cos(w0 * k);
            if (Math.Abs(sum) > 1e-9)
                for (int k = 0; k <= FirHalf; k++) h[k] /= sum;
        }

        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            Array.Copy(left, dryL, n);
            Array.Copy(right, dryR, n);
            Run(left, right, n);
            Delta.Process(dryL, dryR, left, right, n);
            for (int i = 0; i < n; i++)
            {
                float o = output.Next();
                left[i] *= o;
                right[i] *= o;
            }
            if (!Delta.Active) Match.Process(dryL, dryR, left, right, 0, n);
            OutMeter.Feed(left, right, n);
            hold = Math.Max(blockMax, hold - 12.0 * n / sampleRate);
            blockMax = 0;
            Volatile.Write(ref pubRed, hold);
            Volatile.Write(ref pubMatch, Match.Db);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Run(float[] left, float[] right, int n)
        {
            int la = lookahead;
            int dx = la + (action == 1 ? FirHalf : 0);
            double thr = threshold, sl = slope, rg = range;
            double q = 0;
            if (action == 3)
            {
                double p = PortableMath.Pow(2, widthOct);
                q = Math.Sqrt(p) / (p - 1);
            }
            for (int i = 0; i < n; i++)
            {
                float l = left[i], r = right[i];
                audioL.Write(l); audioR.Write(r);
                histPos = (histPos + 1) & (HistSize - 1);
                histL[histPos] = l; histR[histPos] = r;
                band.Filter(l, r, out float bl, out float br);
                bl *= bandNorm; br *= bandNorm;
                bandL.Write(bl); bandR.Write(br);
                double bandDb = band.Level(bl, br);
                double level;
                if (relative)
                {
                    double fullDb = full.Level(l, r);
                    level = fullDb > SilenceDb ? bandDb - fullDb : -100;
                }
                else level = bandDb;
                double over = level - thr;
                double target = over > 0 ? Math.Min(rg, over * sl) : 0;
                red = Detector.Smooth(red, target, attackA, releaseA);
                if (red < 1e-5) red = 0;
                if (red > blockMax) blockMax = red;

                float yl, yr;
                if (listen)
                {
                    yl = bandL.Read(la); yr = bandR.Read(la);
                }
                else if (red == 0 && action < 2)
                {
                    yl = audioL.Read(dx); yr = audioR.Read(dx);
                }
                else if (action == 0)
                {
                    float g1 = (float)(DspMath.DbToGain(-red) - 1);
                    yl = audioL.Read(dx) + g1 * bandL.Read(la);
                    yr = audioR.Read(dx) + g1 * bandR.Read(la);
                }
                else if (action == 1)
                {
                    float g1 = (float)(DspMath.DbToGain(-red) - 1);
                    FirBand(la, out float fl, out float fr);
                    yl = audioL.Read(dx) + g1 * fl;
                    yr = audioR.Read(dx) + g1 * fr;
                }
                else
                {
                    if ((++count & 7) == 0 || wideSet < 0)
                    {
                        if (Math.Abs(red - wideSet) > 0.01 || (red == 0 && wideSet != 0))
                        {
                            if (red == 0) { wideL.SetIdentity(); wideR.SetIdentity(); }
                            else if (action == 2)
                            {
                                wideL.Set(BiquadType.HighShelf, freq, sampleRate, 0.7071067811865476, -red);
                                wideR.Set(BiquadType.HighShelf, freq, sampleRate, 0.7071067811865476, -red);
                            }
                            else
                            {
                                wideL.Set(BiquadType.Peak, freq, sampleRate, q, -red);
                                wideR.Set(BiquadType.Peak, freq, sampleRate, q, -red);
                            }
                            wideSet = red;
                        }
                    }
                    yl = wideL.Process(audioL.Read(dx));
                    yr = wideR.Process(audioR.Read(dx));
                }
                left[i] = yl;
                right[i] = yr;
                if (fade > 0 && ++fade > FadeLength) { fade = 0; Array.Copy(firNext, firCur, firCur.Length); StartPending(); }
                else if (fade == 0 && redesign) StartPending();
            }
        }

        void StartPending()
        {
            if (!redesign) return;
            redesign = false;
            Design(firNext);
            fade = 1;
        }

        /// <summary>The linear-phase band at the audio's position (centred 255 + <paramref name="la"/> samples back), both channels.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void FirBand(int la, out float outL, out float outR)
        {
            double a = Convolve(firCur, histL, la), b = Convolve(firCur, histR, la);
            if (fade > 0)
            {
                double w = fade / (double)FadeLength;
                double an = Convolve(firNext, histL, la), bn = Convolve(firNext, histR, la);
                a = a + (an - a) * w;
                b = b + (bn - b) * w;
            }
            outL = (float)a;
            outR = (float)b;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        double Convolve(double[] h, float[] x, int la)
        {
            int m = histPos - FirHalf - la;
            double acc = h[0] * x[m & (HistSize - 1)];
            for (int k = 1; k <= FirHalf; k++)
                acc += h[k] * ((double)x[(m - k) & (HistSize - 1)] + x[(m + k) & (HistSize - 1)]);
            return acc;
        }

        public IReadOnlyDictionary<string, double> Readings()
        {
            var d = new Dictionary<string, double>
            {
                ["reduction_db"] = Math.Round(ReductionDb, 2),
                ["latency_samples"] = Volatile.Read(ref pubLatency),
            };
            double m = GainMatchDb;
            if (double.IsFinite(m)) d["gain_match_db"] = Math.Round(m, 2);
            return d;
        }
    }
}
