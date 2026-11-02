#nullable disable
// Neo Analyzer (docs/specs/machines.md §5): the analysis, on the audio thread. The audio itself
// passes through untouched (the machine copies it); the engine only listens. Loudness and true
// peak come from the analysis library's streaming meters (NeoBuzz.Analysis LoudnessMeter.cs),
// the spectrum from its FFT and band maths (Fft.cs, Spectrum.cs), correlation and width from its
// stereo measures (Stereo.cs), so the numbers are the ones the agent server's analyze reports.
// Everything is allocated when the sample rate is set; each block only computes, and the results
// go to the editor and the agent server as whole snapshots through a lock-free triple buffer.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using NeoBuzz.Analysis;
using NeoBuzz.Dsp;
using AFft = NeoBuzz.Analysis.Fft;

namespace NeoBuzz.Analyzer
{
    /// <summary>Everything the analyzer shows, at one moment: written by the audio thread, copied out by readers.</summary>
    internal sealed class AnalyzerSnapshot
    {
        public const int GonioPoints = 1024;

        /// <summary>Snapshots published before this one, and samples analysed.</summary>
        public long Sequence, Samples;
        public int Rate;

        // Loudness (LUFS, LU) and true peak (dBTP).
        public double Momentary, ShortTerm, Integrated, Range, MomentaryMax, ShortTermMax, Seconds;
        /// <summary>The highest true peak since the reset; the recent true peak, falling 20 dB/s; the held peak.</summary>
        public double TruePeakMax, TruePeakNow, TruePeakHold;

        /// <summary>Correlation and width (side/mid energy) over 3 s, and over 400 ms (the meter).</summary>
        public double Correlation, Width, CorrelationNow, WidthNow;

        /// <summary>Levels of Bands.Named over 3 s, dB RMS.</summary>
        public readonly double[] NamedDb = new double[Bands.Named.Length];

        /// <summary>Levels of the 31 third-octave bands over 3 s, dB RMS (the readings; the bars follow Speed).</summary>
        public readonly double[] ThirdsDb = new double[AnalyzerEngine.ThirdCount];

        /// <summary>The spectrum curve at <see cref="AnalyzerEngine.CurveHz"/> and its held peaks, dB (untilted).</summary>
        public readonly float[] Curve = new float[AnalyzerEngine.CurvePoints], CurvePeak = new float[AnalyzerEngine.CurvePoints];

        /// <summary>The 31 third-octave bands (Bands.ThirdOctaveNominal) and their held peaks, dB.</summary>
        public readonly float[] Thirds = new float[AnalyzerEngine.ThirdCount], ThirdsPeak = new float[AnalyzerEngine.ThirdCount];

        /// <summary>The latest samples (oldest first), for the goniometer: <see cref="GonioCount"/> of them.</summary>
        public readonly float[] GonioL = new float[GonioPoints], GonioR = new float[GonioPoints];
        public int GonioCount;

        public AnalyzerSnapshot()
        {
            double ninf = double.NegativeInfinity;
            Momentary = ShortTerm = Integrated = MomentaryMax = ShortTermMax = ninf;
            TruePeakMax = TruePeakNow = TruePeakHold = ninf;
            Correlation = CorrelationNow = 1;
            Array.Fill(NamedDb, ninf);
            Array.Fill(ThirdsDb, ninf);
            Array.Fill(Curve, float.NegativeInfinity);
            Array.Fill(CurvePeak, float.NegativeInfinity);
            Array.Fill(Thirds, float.NegativeInfinity);
            Array.Fill(ThirdsPeak, float.NegativeInfinity);
        }

        public void CopyTo(AnalyzerSnapshot o)
        {
            o.Sequence = Sequence; o.Samples = Samples; o.Rate = Rate;
            o.Momentary = Momentary; o.ShortTerm = ShortTerm; o.Integrated = Integrated; o.Range = Range;
            o.MomentaryMax = MomentaryMax; o.ShortTermMax = ShortTermMax; o.Seconds = Seconds;
            o.TruePeakMax = TruePeakMax; o.TruePeakNow = TruePeakNow; o.TruePeakHold = TruePeakHold;
            o.Correlation = Correlation; o.Width = Width; o.CorrelationNow = CorrelationNow; o.WidthNow = WidthNow;
            Array.Copy(NamedDb, o.NamedDb, NamedDb.Length);
            Array.Copy(ThirdsDb, o.ThirdsDb, ThirdsDb.Length);
            Array.Copy(Curve, o.Curve, Curve.Length);
            Array.Copy(CurvePeak, o.CurvePeak, CurvePeak.Length);
            Array.Copy(Thirds, o.Thirds, Thirds.Length);
            Array.Copy(ThirdsPeak, o.ThirdsPeak, ThirdsPeak.Length);
            Array.Copy(GonioL, o.GonioL, GonioCount);
            Array.Copy(GonioR, o.GonioR, GonioCount);
            o.GonioCount = GonioCount;
        }

        /// <summary>The third-octave readings' keys: band_20hz_db … band_31_5hz_db … band_20000hz_db (nominal centres).</summary>
        public static readonly string[] ThirdKeys = Bands.ThirdOctaveNominal
            .Select(f => "band_" + f.ToString(CultureInfo.InvariantCulture).Replace('.', '_') + "hz_db").ToArray();

        /// <summary>
        /// The readings agents get (docs/specs/mcp-server.md §3.5), keyed as Params.def's reading
        /// lines: a new dictionary each time, which nothing changes afterwards.
        /// </summary>
        public IReadOnlyDictionary<string, double> ToReadings()
        {
            var r = new Dictionary<string, double>
            {
                ["momentary_lufs"] = Momentary,
                ["short_term_lufs"] = ShortTerm,
                ["integrated_lufs"] = Integrated,
                ["range_lu"] = Range,
                ["true_peak_dbtp"] = TruePeakMax,
                ["momentary_max_lufs"] = MomentaryMax,
                ["short_term_max_lufs"] = ShortTermMax,
                ["seconds"] = Seconds,
                ["correlation"] = Correlation,
                ["width"] = Width,
            };
            for (int i = 0; i < Bands.Named.Length; i++) r[Bands.Named[i].Name + "_db"] = NamedDb[i];
            for (int i = 0; i < ThirdKeys.Length; i++) r[ThirdKeys[i]] = ThirdsDb[i];
            return new ReadOnlyDictionary<string, double>(r);
        }
    }

    internal sealed class AnalyzerEngine
    {
        public const int MaxBlock = 256;
        public const int CurvePerOctave = 48;
        public static readonly int CurvePoints = (int)Math.Floor(Math.Log2(20000 / 20.0) * CurvePerOctave) + 1;
        public static readonly int ThirdCount = Bands.ThirdOctaveNominal.Length;

        /// <summary>The spectrum curve's frequencies: 48 per octave from 20 Hz to 20 kHz.</summary>
        public static readonly double[] CurveHz = MakeCurveHz();

        /// <summary>The third-octave bands' edges (Bands.ThirdOctaveEdges), Hz.</summary>
        public static readonly double[] ThirdLo = new double[ThirdCount], ThirdHi = new double[ThirdCount];

        static AnalyzerEngine()
        {
            for (int i = 0; i < ThirdCount; i++) (ThirdLo[i], ThirdHi[i]) = Bands.ThirdOctaveEdges(i);
        }

        /// <summary>Speed: averaging time constants, seconds.</summary>
        public static readonly double[] SpeedSeconds = { 0.1, 0.3, 1, 3 };

        /// <summary>Smoothing: half-widths of the curve's bands, as base-ten fractions of an octave (1/24 … 1/3; 1/3 is the third octave's).</summary>
        public static readonly double[] SmoothingEdge = { Math.Pow(10, 1 / 160.0), Math.Pow(10, 1 / 80.0), Math.Pow(10, 1 / 40.0), Math.Pow(10, 1 / 20.0) };

        /// <summary>Held peaks fall this fast after their hold time; so does the recent true peak.</summary>
        public const double FallDbPerSecond = 20;

        const int StereoSteps = 30, StereoFastSteps = 4;
        const double BandSeconds = 3;

        readonly ParamValues values;
        readonly TripleBuffer<AnalyzerSnapshot> exchange =
            new TripleBuffer<AnalyzerSnapshot>(() => new AnalyzerSnapshot(), (a, b) => a.CopyTo(b));
        int rate;
        long samples, sequence, lastPublish;

        // Loudness and true peak.
        LoudnessMeter loudness;
        readonly TruePeakMeter peakL = new TruePeakMeter(), peakR = new TruePeakMeter();
        double peakNowDb = double.NegativeInfinity, peakHoldDb = double.NegativeInfinity, peakHoldLeft;
        double integrated = double.NegativeInfinity, range;

        // Stereo: energies per 100 ms step (the loudness meter's steps).
        readonly double[] stepLL = new double[StereoSteps], stepRR = new double[StereoSteps], stepLR = new double[StereoSteps];
        double ll, rr, lr;
        int stereoPos, stereoFilled;
        double correlation = 1, width, correlationNow = 1, widthNow;

        // Spectrum.
        int size, mask, half, hop, toHop, frames;
        double binHz, nyquist, alpha, holdSeconds, windowSquares;
        SpectrumFft fft;
        double[] window, re, im, frame, average, cumulative;
        float[] ringL, ringR;
        int ringPos;
        double[] thirdLo, thirdHi, curveLo, curveHi;
        readonly float[] curve = new float[CurvePoints], curvePeak = new float[CurvePoints], thirds = new float[ThirdCount], thirdsPeak = new float[ThirdCount];
        readonly double[] curveHold = new double[CurvePoints], thirdsHold = new double[ThirdCount];
        double[] bandFrames;   // per frame, the named bands' and the third octaves' powers
        int bandLength, bandPos, bandFilled;
        readonly double[] namedDb = new double[Bands.Named.Length], thirdsDb = new double[ThirdCount];
        int smoothing = -1;
        volatile bool resetRequested;

        // Goniometer.
        readonly float[] gonioL = new float[AnalyzerSnapshot.GonioPoints], gonioR = new float[AnalyzerSnapshot.GonioPoints];
        int gonioPos;
        long gonioTotal;

        public AnalyzerEngine(ParamValues values)
        {
            this.values = values;
            SetSampleRate(48000);
        }

        public int Rate => rate;

        /// <summary>The FFT's size and hop at this rate (8192 and 2048 at 44.1 and 48 kHz).</summary>
        public int FftSize => size;
        public int Hop => hop;

        static double[] MakeCurveHz()
        {
            var hz = new double[CurvePoints];
            for (int i = 0; i < hz.Length; i++) hz[i] = 20 * Math.Pow(2, i / (double)CurvePerOctave);
            return hz;
        }

        /// <summary>Allocates for a sample rate and starts every measure again.</summary>
        public void SetSampleRate(int sampleRate)
        {
            rate = Math.Max(8000, sampleRate);
            loudness = new LoudnessMeter(rate);
            size = AFft.Nearest(0.17 * rate);
            mask = size - 1;
            half = size / 2;
            hop = size / 4;
            toHop = hop;
            fft = new SpectrumFft(size);
            window = AFft.Hann(size);
            windowSquares = 0;
            foreach (double w in window) windowSquares += w * w;
            re = new double[size];
            im = new double[size];
            frame = new double[half + 1];
            average = new double[half + 1];
            cumulative = new double[half + 2];
            ringL = new float[size];
            ringR = new float[size];
            ringPos = 0;
            frames = 0;
            binHz = rate / (double)size;
            nyquist = rate / 2.0;
            thirdLo = ThirdLo;
            thirdHi = ThirdHi;
            curveLo = new double[CurvePoints];
            curveHi = new double[CurvePoints];
            bandLength = Math.Max(1, (int)Math.Round(BandSeconds * rate / hop));
            bandFrames = new double[bandLength * (Bands.Named.Length + ThirdCount)];
            Array.Fill(namedDb, double.NegativeInfinity);
            Array.Fill(thirdsDb, double.NegativeInfinity);
            bandPos = bandFilled = 0;
            Array.Clear(stepLL); Array.Clear(stepRR); Array.Clear(stepLR);
            ll = rr = lr = 0;
            stereoPos = stereoFilled = 0;
            Array.Clear(gonioL); Array.Clear(gonioR);
            gonioPos = 0;
            gonioTotal = 0;
            samples = 0;
            lastPublish = 0;
            smoothing = -1;
            ApplyParameters(true);
            ClearHeld();
            Publish();
        }

        void ApplyParameters(bool all)
        {
            int s = values.Choice(P.Smoothing);
            if (s != smoothing)
            {
                smoothing = s;
                double e = SmoothingEdge[Math.Clamp(s, 0, SmoothingEdge.Length - 1)];
                for (int i = 0; i < CurvePoints; i++)
                {
                    curveLo[i] = CurveHz[i] / e;
                    curveHi[i] = CurveHz[i] * e;
                }
                if (!all) ClearCurvePeaks();
            }
            double tau = SpeedSeconds[Math.Clamp(values.Choice(P.Speed), 0, SpeedSeconds.Length - 1)];
            alpha = 1 - Math.Exp(-hop / (rate * tau));
            holdSeconds = values.Value(P.Hold);
            if (!all && values.IsDirty(P.Reset) && values.On(P.Reset)) Reset();
            values.Clean();
        }

        /// <summary>
        /// The Reset switch turned On: the integrated loudness, loudness range, maxima and held
        /// peaks start again. Momentary, short-term, the spectrum's average and the stereo meters go on.
        /// </summary>
        public void Reset()
        {
            loudness.ResetIntegrated();
            peakL.ResetPeak();
            peakR.ResetPeak();
            integrated = double.NegativeInfinity;
            range = 0;
            ClearHeld();
        }

        /// <summary>Any thread (the editor's Reset button, the machine's menu): a reset at the next block.</summary>
        public void RequestReset() => resetRequested = true;

        void ClearHeld()
        {
            peakHoldDb = double.NegativeInfinity;
            peakHoldLeft = 0;
            ClearCurvePeaks();
            Array.Copy(thirds, thirdsPeak, ThirdCount);
            Array.Clear(thirdsHold);
        }

        void ClearCurvePeaks()
        {
            Array.Copy(curve, curvePeak, CurvePoints);
            Array.Clear(curveHold);
        }

        /// <summary>
        /// Audio thread: analyses one block (±1 floats). The arrays are only read: the analyzer
        /// passes its input through unchanged.
        /// </summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (values.AnyDirty) ApplyParameters(false);
            if (resetRequested)
            {
                resetRequested = false;
                Reset();
            }
            bool changed = false;
            int at = 0;
            while (at < n)
            {
                int m = Math.Min(n - at, Math.Min(loudness.SamplesToStepEnd, toHop));
                int steps = loudness.Process(left, right, at, m);
                peakL.Process(left, at, m);
                peakR.Process(right, at, m);
                double sll = ll, srr = rr, slr = lr;
                int rp = ringPos, gp = gonioPos;
                for (int i = at, end = at + m; i < end; i++)
                {
                    float l = left[i], r = right[i];
                    sll += l * (double)l;
                    srr += r * (double)r;
                    slr += l * (double)r;
                    ringL[rp] = l;
                    ringR[rp] = r;
                    rp = (rp + 1) & mask;
                    gonioL[gp] = l;
                    gonioR[gp] = r;
                    gp = gp + 1 == AnalyzerSnapshot.GonioPoints ? 0 : gp + 1;
                }
                ll = sll; rr = srr; lr = slr;
                ringPos = rp;
                gonioPos = gp;
                gonioTotal += m;
                samples += m;
                at += m;
                toHop -= m;
                if (steps > 0)
                {
                    StereoStep();
                    integrated = loudness.Integrated;
                    range = loudness.Range;
                    changed = true;
                }
                if (toHop == 0)
                {
                    // Frames start once the ring holds a whole window.
                    if (samples >= size) SpectrumFrame();
                    toHop = hop;
                    changed = true;
                }
            }
            if (changed) Publish();
        }

        void StereoStep()
        {
            stepLL[stereoPos] = ll;
            stepRR[stereoPos] = rr;
            stepLR[stereoPos] = lr;
            ll = rr = lr = 0;
            stereoPos = stereoPos + 1 == StereoSteps ? 0 : stereoPos + 1;
            if (stereoFilled < StereoSteps) stereoFilled++;
            (correlation, width) = StereoOver(StereoSteps);
            (correlationNow, widthNow) = StereoOver(StereoFastSteps);
        }

        (double Correlation, double Width) StereoOver(int steps)
        {
            steps = Math.Min(steps, stereoFilled);
            double a = 0, b = 0, c = 0;
            for (int k = 1; k <= steps; k++)
            {
                int j = stereoPos - k;
                if (j < 0) j += StereoSteps;
                a += stepLL[j];
                b += stepRR[j];
                c += stepLR[j];
            }
            var (corr, w, _) = Stereo.Measures(a, b, c);
            return (corr, w);
        }

        void SpectrumFrame()
        {
            // The last `size` samples, oldest first, Hann-windowed: left in re, right in im.
            for (int i = 0, j = ringPos; i < size; i++, j = (j + 1) & mask)
            {
                double w = window[i];
                re[i] = ringL[j] * w;
                im[i] = ringR[j] * w;
            }
            fft.Forward(re, im);
            // As Welch and Stft: the channels' mean power per bin, scaled so the bins sum to the mean square.
            for (int k = 0; k <= half; k++)
            {
                double c = (k == 0 || k == half ? 1.0 : 2.0) / (size * windowSquares);
                AFft.Split(re, im, k, out double lr0, out double li, out double rr0, out double ri);
                frame[k] = 0.5 * c * (lr0 * lr0 + li * li + rr0 * rr0 + ri * ri);
            }
            if (frames == 0) Array.Copy(frame, average, half + 1);
            else for (int k = 0; k <= half; k++) average[k] += alpha * (frame[k] - average[k]);
            frames++;

            double running = 0;
            for (int k = 0; k <= half; k++)
            {
                cumulative[k] = running;
                running += average[k];
            }
            cumulative[half + 1] = running;

            double dt = hop / (double)rate;
            for (int i = 0; i < ThirdCount; i++)
            {
                float v = thirdLo[i] >= nyquist ? float.NegativeInfinity : (float)Spectrum.Db(Spectrum.Integrate(average, binHz, thirdLo[i], thirdHi[i]));
                thirds[i] = v;
                Hold(ref thirdsPeak[i], ref thirdsHold[i], v, dt);
            }
            for (int i = 0; i < CurvePoints; i++)
            {
                float v = curveLo[i] >= nyquist ? float.NegativeInfinity : (float)Spectrum.Db(Integrate(curveLo[i], curveHi[i]));
                curve[i] = v;
                Hold(ref curvePeak[i], ref curveHold[i], v, dt);
            }

            // The readings' bands over the last 3 s of frames (each frame on its own, not the average).
            int nn = Bands.Named.Length, nb = nn + ThirdCount, at0 = bandPos * nb;
            for (int b = 0; b < nn; b++) bandFrames[at0 + b] = Spectrum.Integrate(frame, binHz, Bands.Named[b].Lo, Bands.Named[b].Hi);
            for (int i = 0; i < ThirdCount; i++) bandFrames[at0 + nn + i] = thirdLo[i] >= nyquist ? 0 : Spectrum.Integrate(frame, binHz, thirdLo[i], thirdHi[i]);
            bandPos = bandPos + 1 == bandLength ? 0 : bandPos + 1;
            if (bandFilled < bandLength) bandFilled++;
            for (int b = 0; b < nb; b++)
            {
                double sum = 0;
                for (int f = 0; f < bandFilled; f++) sum += bandFrames[f * nb + b];
                double db = Spectrum.Db(sum / bandFilled);
                if (b < nn) namedDb[b] = db;
                else thirdsDb[b - nn] = db;
            }
        }

        /// <summary>
        /// Spectrum.Integrate over the average, from its running sums (the curve has 479 points;
        /// the bars and bands use Spectrum.Integrate itself).
        /// </summary>
        double Integrate(double lo, double hi)
        {
            int last = half;
            double a = lo / binHz, b = Math.Min(hi / binHz, last + 0.5);
            if (b <= a) return 0;
            int k0 = Math.Max(0, (int)Math.Floor(a + 0.5)), k1 = Math.Min(last, (int)Math.Floor(b + 0.5));
            if (k0 >= k1) return k0 == k1 ? average[k0] * Math.Clamp(Math.Min(b, k0 + 0.5) - Math.Max(a, k0 - 0.5), 0, 1) : 0;
            double first = average[k0] * Math.Clamp(k0 + 0.5 - Math.Max(a, k0 - 0.5), 0, 1);
            double end = average[k1] * Math.Clamp(Math.Min(b, k1 + 0.5) - (k1 - 0.5), 0, 1);
            return first + end + Math.Max(0, cumulative[k1] - cumulative[k0 + 1]);
        }

        /// <summary>A held peak: follows rises at once, holds for the hold time, then falls at 20 dB/s (never below the value).</summary>
        void Hold(ref float peak, ref double holdLeft, double value, double dt)
        {
            if (!(value < peak))
            {
                peak = (float)value;
                holdLeft = holdSeconds;
            }
            else if (holdLeft > 0) holdLeft -= dt;
            else peak = (float)Math.Max(value, peak - FallDbPerSecond * dt);
        }

        static double Db(double linear) => linear > 0 ? 20 * Math.Log10(linear) : double.NegativeInfinity;

        void Publish()
        {
            double dt = (samples - lastPublish) / (double)rate;
            lastPublish = samples;
            double windowDb = Db(Math.Max(peakL.TakeWindowPeak(), peakR.TakeWindowPeak()));
            peakNowDb = Math.Max(windowDb, peakNowDb - FallDbPerSecond * dt);
            if (!(windowDb < peakHoldDb))
            {
                peakHoldDb = windowDb;
                peakHoldLeft = holdSeconds;
            }
            else if (peakHoldLeft > 0) peakHoldLeft -= dt;
            else peakHoldDb = Math.Max(peakNowDb, peakHoldDb - FallDbPerSecond * dt);

            var s = exchange.Back;
            s.Sequence = sequence++;
            s.Samples = samples;
            s.Rate = rate;
            s.Momentary = loudness.Momentary;
            s.ShortTerm = loudness.ShortTerm;
            s.Integrated = integrated;
            s.Range = range;
            s.MomentaryMax = loudness.MomentaryMax;
            s.ShortTermMax = loudness.ShortTermMax;
            s.Seconds = loudness.Seconds;
            s.TruePeakMax = Db(Math.Max(peakL.Peak, peakR.Peak));
            s.TruePeakNow = peakNowDb;
            s.TruePeakHold = peakHoldDb;
            s.Correlation = correlation;
            s.Width = width;
            s.CorrelationNow = correlationNow;
            s.WidthNow = widthNow;
            Array.Copy(namedDb, s.NamedDb, namedDb.Length);
            Array.Copy(thirdsDb, s.ThirdsDb, thirdsDb.Length);
            Array.Copy(curve, s.Curve, CurvePoints);
            Array.Copy(curvePeak, s.CurvePeak, CurvePoints);
            Array.Copy(thirds, s.Thirds, ThirdCount);
            Array.Copy(thirdsPeak, s.ThirdsPeak, ThirdCount);
            int count = (int)Math.Min(gonioTotal, AnalyzerSnapshot.GonioPoints);
            int from = gonioPos - count;
            if (from < 0) from += AnalyzerSnapshot.GonioPoints;
            int first = Math.Min(count, AnalyzerSnapshot.GonioPoints - from);
            Array.Copy(gonioL, from, s.GonioL, 0, first);
            Array.Copy(gonioR, from, s.GonioR, 0, first);
            Array.Copy(gonioL, 0, s.GonioL, first, count - first);
            Array.Copy(gonioR, 0, s.GonioR, first, count - first);
            s.GonioCount = count;
            exchange.Publish();
        }

        /// <summary>Any thread: the newest snapshot into <paramref name="into"/>; true when it is new to readers.</summary>
        public bool Read(AnalyzerSnapshot into) => exchange.Read(into);

        /// <summary>Snapshots published so far.</summary>
        public long Published => exchange.Published;

        /// <summary>Any thread: the newest readings (docs/specs/mcp-server.md §3.5).</summary>
        public IReadOnlyDictionary<string, double> Readings()
        {
            var s = new AnalyzerSnapshot();
            exchange.Read(s);
            return s.ToReadings();
        }
    }
}
