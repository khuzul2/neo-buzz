#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md section 11, WP1): the time-domain path of the Pro warp mode, for monophonic, periodic,
// low material (a bass line). A phase vocoder (Signalsmith, Complex) keeps each harmonic's frequency but not the phase between them,
// so a sawtooth-like bass loses the shape of its cycle and a click is heard once per period. This path keeps the cycle: waveform-similarity
// overlap-add (Verhelst and Roelands, WSOLA) repeats or drops whole periods of the audio, and at each note's onset it starts a new frame
// exactly there (so an attack is neither doubled nor smeared, and the notes land on their beats). Chosen automatically, once per audio, off the
// audio thread (PeriodicAnalysis), from the samples alone (so the same audio is chosen the same way from memory, from the cache file or in a
// reversed view): a monophonic low line whose harmonics repeat from one period to the next like an oscillator's. A line that does not repeat
// like that (a played instrument's noise, a drifting pitch or a changing timbre, a beating pair of oscillators) and anything else the analysis
// is not sure of stays with the stretcher.
// Pure (ReBuzz.SessionTests links this file); no allocation after construction.

using System;

namespace ReBuzz.Core.Clips
{
    /// <summary>What the analysis found: whether the audio is a bass line the time-domain path should stretch, and what it needs for it.</summary>
    internal sealed class PeriodicInfo
    {
        /// <summary>Use the time-domain path for this audio.</summary>
        public bool Use;
        /// <summary>The share of the loud frames that are one steady pitch (0 to 1) and of the energy that is below 500 Hz.</summary>
        public double VoicedShare, LowShare;
        /// <summary>
        /// How exactly the harmonics above the fundamental (300 Hz to 4 kHz) repeat from one period to the next: the normalised autocorrelation of
        /// that band at the period, as the energy-weighted median and the energy-weighted 10th percentile of the voiced frames (1 is a signal that
        /// repeats to the sample, as a synthesiser's oscillator does; a played and sampled instrument has noise, a drifting pitch and a changing
        /// timbre there and sits lower). <see cref="FidelityFrames"/> frames were measured.
        /// </summary>
        public double Fidelity, FidelityLow;
        public int FidelityFrames;
        /// <summary>Tests only (<see cref="PeriodicAnalysis.Diagnostics"/>): the frames measured, as (start sample, period, energy, band repeat).</summary>
        public (int Start, int Period, double Energy, double Repeat)[] Measured = Array.Empty<(int, int, double, double)>();
        /// <summary>The longest period of the voiced frames (95th percentile), in samples: the search reaches half of it either way.</summary>
        public int MaxPeriod;
        /// <summary>The audio's mono mix, 4 samples averaged to 1 (padded with <see cref="Pad"/> zeros each end), for the coarse search.</summary>
        public float[] Mid4;
        public int Pad;
        /// <summary>Where each note starts, in samples (a little before its attack), rising: found for this path alone (the clip's slices are for drums).</summary>
        public int[] Onsets = Array.Empty<int>();
    }

    internal static class PeriodicAnalysis
    {
        /// <summary>A frame is voiced from this normalised autocorrelation (McLeod's NSDF) up.</summary>
        public const double VoicedFrom = 0.85;
        /// <summary>
        /// The audio is a bass line when this share of its loud frames is voiced and this share of its energy is below 500 Hz; the path takes it
        /// when, besides, it repeats like an oscillator (<see cref="MinFidelity"/>).
        /// </summary>
        public const double MinVoiced = 0.65, MinLow = 0.85;
        /// <summary>
        /// The path is for lines with harmonics to lose: a phase vocoder breaks the phase between the harmonics of a period, and has little to break
        /// in a line that is nearly all fundamental. The audio takes the path only when at least 1.5 % of its energy is above 500 Hz (a low share of
        /// at most this). Measured (WP1b report): the generated lines have 2 to 9 % there; five sampled basses 0.2 to 0.8 %, where the stretcher
        /// does not click and the path was no better by the distance.
        /// </summary>
        public const double MaxLow = 0.985;
        /// <summary>Audio longer than this plays through the stretcher (the analysis would be long, and so the decimated copy).</summary>
        public const double MaxSeconds = 240;
        /// <summary>The band whose period-to-period repeat is measured, in Hz, and how many of the voiced frames are measured at most.</summary>
        public const double FidelityFrom = 300, FidelityTo = 4000, MinBandShare = 1e-4;
        public const int FidelityMaxFrames = 48;
        /// <summary>Tests: keep the frames the fidelity was measured on in <see cref="PeriodicInfo.Measured"/>.</summary>
        internal static volatile bool Diagnostics;
        /// <summary>Tests (renders to compare the paths by ear): every low voiced line takes the path, whatever the repeat and the harmonics. Set before a source is first analysed.</summary>
        internal static volatile bool Force;
        /// <summary>
        /// The audio repeats like an oscillator when the median of that repeat is at least the first and its 10th percentile at least the second.
        /// Measured (WP1b report): generated lines (saw, square, pulse, filtered saw) 0.987 to 0.994 and 0.97 to 0.99; the same line with a pitch
        /// swing or a second oscillator beating against it 0.90 to 0.96 and 0.81 to 0.84; sampled basses, a clean sustained one 0.99 to 1.00 and
        /// 0.95 to 0.97, a noisy or slapped one 0.97 and 0.83 to 0.85.
        /// </summary>
        public const double MinFidelity = 0.98, MinFidelityLow = 0.90;

        /// <summary>
        /// The analysis of a source, made once (off the audio thread) and kept with it, null for audio the path cannot play (too short, too long, a
        /// rate it does not do). It is a function of the samples alone: the same audio gives the same verdict whether it is in memory, in the cache
        /// file (a streamed source is read exactly, off the audio thread) or in a reversed view, so a clip that goes to the cache while it plays,
        /// or is first prepared after it has, keeps its path.
        /// </summary>
        public static PeriodicInfo Of(AudioSource source)
        {
            if (source == null) return null;
            lock (source.PeriodicGate)
            {
                if (!source.PeriodicTried)
                {
                    try
                    {
                        source.Periodic = Analyse(source);
                        source.PeriodicTried = true;
                    }
                    catch (Exception e) when (e is System.IO.IOException || e is System.IO.InvalidDataException || e is ObjectDisposedException)
                    {
                        // The cache file cannot be read just now (it is being repaired, or the asset is being dropped): not decided, so the next
                        // Prepare asks again, and until then the clip plays through the stretcher. A verdict made from a failed read would be a
                        // different one from the same audio in memory.
                        return null;
                    }
                    catch
                    {
                        source.Periodic = null;
                        source.PeriodicTried = true;
                    }
                }
                return source.PeriodicTried ? source.Periodic : null;
            }
        }

        /// <summary>
        /// Note onsets of a monophonic line: the 23 ms RMS of the mix rises 1.8 times over what it was 23 ms before (and is within 45 dB of the
        /// loudest), at least 50 ms after the last; each is put at the first 1.5 ms block of the rise that reaches a quarter of the note's level,
        /// a block earlier. A bass is not a drum: the spectral-flux slices of Beats find a new onset in every decaying harmonic of it.
        /// </summary>
        public static int[] NoteOnsets(float[] left, float[] right, int rate)
        {
            int W = PeriodicRun.HopFor(rate), hop = W / 8, back = 8, fine = W / 16;
            int length = left.Length;
            int n = (length - W) / hop + 1;
            if (n <= back + 2) return Array.Empty<int>();
            var e1 = new double[n];
            double mx = 0;
            for (int k = 0; k < n; k++)
            {
                double sum = 0;
                int at = k * hop;
                for (int j = 0; j < W; j++)
                {
                    double v = 0.5 * (left[at + j] + right[at + j]);
                    sum += v * v;
                }
                e1[k] = Math.Sqrt(sum / W);
                if (e1[k] > mx) mx = e1[k];
            }
            var found = new System.Collections.Generic.List<int>();
            double floor = mx * Math.Pow(10, -45.0 / 20);
            int last = int.MinValue / 2;
            int refractory = (int)(0.05 * rate);
            for (int k = back; k < n; k++)
            {
                if (e1[k] < floor) continue;
                if (!(e1[k] > 1.8 * Math.Max(e1[k - back], mx * 1e-4)) || e1[k] < e1[k - 1] || k * hop - last <= refractory) continue;
                int a = (k - back) * hop, b = Math.Min(length, k * hop + 2 * W);
                int blocks = (b - a) / fine;
                if (blocks < 2) continue;
                var f = new double[blocks];
                double pk = 0;
                for (int i = 0; i < blocks; i++)
                {
                    double sum = 0;
                    for (int j = 0; j < fine; j++)
                    {
                        double v = 0.5 * (left[a + i * fine + j] + right[a + i * fine + j]);
                        sum += v * v;
                    }
                    f[i] = Math.Sqrt(sum / fine);
                    if (f[i] > pk) pk = f[i];
                }
                int first = 0;
                while (first < blocks - 1 && f[first] <= 0.25 * pk) first++;
                found.Add(Math.Max(0, a + (first - 2) * fine));
                last = k * hop;
            }
            return found.ToArray();
        }

        static PeriodicInfo Analyse(AudioSource source)
        {
            int rate = source.SampleRate, length = source.Length;
            if (rate < 32000 || rate > 96000 || length < rate / 2 || length > MaxSeconds * rate) return null;
            var (left, right) = SamplesOf(source);
            int n4 = length / 4;
            int hs = PeriodicRun.HopFor(rate);
            int pad = (hs + hs * 2) / 4 + 64;
            var mid4 = new float[n4 + 2 * pad];
            for (int i = 0; i < n4; i++)
            {
                int j = 4 * i;
                mid4[pad + i] = 0.125f * (left[j] + right[j] + left[j + 1] + right[j + 1] + left[j + 2] + right[j + 2] + left[j + 3] + right[j + 3]);
            }
            var info = new PeriodicInfo { Mid4 = mid4, Pad = pad };

            // The band share: the energy below 500 Hz of all (4096-point frames, no overlap).
            const int Size = 4096;
            var fft = new Fft(Size);
            var re = new double[Size];
            var im = new double[Size];
            double low = 0, total = 0;
            int binLimit = (int)(500.0 * Size / rate);
            for (int at = 0; at + Size <= length; at += Size)
            {
                for (int k = 0; k < Size; k++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * k / Size);
                    re[k] = 0.5 * (left[at + k] + right[at + k]) * w;
                    im[k] = 0;
                }
                fft.Transform(re, im);
                for (int k = 1; k < Size / 2; k++)
                {
                    double e = re[k] * re[k] + im[k] * im[k];
                    total += e;
                    if (k <= binLimit) low += e;
                }
            }
            info.LowShare = total > 0 ? low / total : 0;
            info.Onsets = NoteOnsets(left, right, rate);

            // Steady pitch: the normalised autocorrelation of 93 ms frames of the signal at about 5.5 kHz.
            int D = rate <= 48000 ? 2 : 4;                          // 4 to 8 in all with the first stage
            int n8 = n4 / D;
            var d = new float[n8];
            for (int i = 0; i < n8; i++)
            {
                float s = 0;
                for (int k = 0; k < D; k++) s += mid4[pad + D * i + k];
                d[i] = s / D;
            }
            int step = 4 * D;                                         // samples per decimated sample
            int w8 = (int)(0.093 * rate / step), hop8 = Math.Max(1, w8 / 2);
            int lagMin = Math.Max(2, (int)(rate / (460.0 * step))), lagMax = (int)(rate / (34.0 * step)) + 1;
            int frames = Math.Max(0, (n8 - w8 - lagMax) / hop8);
            if (frames < 4) return info;
            var energy = new double[frames];
            var periods = new int[frames];
            var voiced = new bool[frames];
            var nsdf = new double[lagMax + 1];
            double maxEnergy = 0;
            for (int f = 0; f < frames; f++)
            {
                int s0 = f * hop8;
                double e0 = 0;
                for (int j = 0; j < w8; j++) e0 += d[s0 + j] * (double)d[s0 + j];
                energy[f] = e0;
                if (e0 > maxEnergy) maxEnergy = e0;
            }
            double quiet = maxEnergy * 1e-4;
            var voicedPeriods = new System.Collections.Generic.List<int>();
            var voicedFrames = new System.Collections.Generic.List<(int Start, double Energy, int Period)>();
            double loud = 0, steady = 0;
            for (int f = 0; f < frames; f++)
            {
                if (energy[f] <= quiet) continue;
                int s0 = f * hop8;
                for (int t = lagMin; t <= lagMax; t++)
                {
                    double ab = 0, bb = 0;
                    for (int j = 0; j < w8; j++)
                    {
                        double b = d[s0 + t + j];
                        ab += d[s0 + j] * b;
                        bb += b * b;
                    }
                    nsdf[t] = 2 * ab / (energy[f] + bb + 1e-30);
                }
                int from = lagMin;
                while (from <= lagMax && nsdf[from] > 0) from++;           // past the first lobe (the zero lag's)
                double best = 0;
                for (int t = from; t <= lagMax; t++) if (nsdf[t] > best) best = nsdf[t];
                int pick = -1;
                for (int t = from + 1; t < lagMax; t++)
                    if (nsdf[t] >= nsdf[t - 1] && nsdf[t] >= nsdf[t + 1] && nsdf[t] >= 0.9 * best) { pick = t; break; }
                loud += energy[f];
                if (pick > 0 && nsdf[pick] >= VoicedFrom)
                {
                    steady += energy[f];
                    voiced[f] = true;
                    periods[f] = pick * step;
                    voicedPeriods.Add(pick * step);
                    voicedFrames.Add((s0 * step, energy[f], pick * step));
                }
            }
            info.VoicedShare = loud > 0 ? steady / loud : 0;
            if (voicedPeriods.Count > 0)
            {
                voicedPeriods.Sort();
                info.MaxPeriod = voicedPeriods[Math.Min(voicedPeriods.Count - 1, (int)(0.95 * voicedPeriods.Count))];
            }
            if (info.VoicedShare >= MinVoiced && info.LowShare >= MinLow && voicedPeriods.Count >= 4)
            {
                MeasureFidelity(info, left, right, rate, voicedFrames, w8 * step, step);
                info.Use = Force || (info.LowShare <= MaxLow && info.Fidelity >= MinFidelity && info.FidelityLow >= MinFidelityLow);
            }
            return info;
        }

        /// <summary>The samples of a source as arrays, exact wherever they are (the arrays themselves for audio in memory; a copy of a cache file's or a reversed view's).</summary>
        static (float[] Left, float[] Right) SamplesOf(AudioSource source)
        {
            if (source.InMemory && !source.IsReversedView) return (source.Left, source.Right);
            return source.ToArrays();
        }

        /// <summary>
        /// Fills <see cref="PeriodicInfo.Fidelity"/> and <see cref="PeriodicInfo.FidelityLow"/>: for up to <see cref="FidelityMaxFrames"/> voiced frames, spread
        /// evenly, the normalised autocorrelation (2 sum a b over the energy of both) of the 300 Hz to 4 kHz band of the frame's mix at the lag nearest
        /// the period where it is highest, the peak settled between samples by a parabola through its neighbours. Weighted by the frame's energy.
        /// </summary>
        static void MeasureFidelity(PeriodicInfo info, float[] left, float[] right, int rate, System.Collections.Generic.List<(int Start, double Energy, int Period)> frames, int window, int reach)
        {
            // Only the body of a note: a frame with a note's attack in it (or in the lag's reach) is the onset's, which the path handles by itself.
            var steady = new System.Collections.Generic.List<(int Start, double Energy, int Period)>();
            var onsets = info.Onsets;
            foreach (var fr in frames)
            {
                int lo = fr.Start - rate / 50, hi = fr.Start + window + fr.Period + 2 * reach;
                int a = 0, b = onsets.Length;
                while (a < b)
                {
                    int mid = (a + b) / 2;
                    if (onsets[mid] < lo) a = mid + 1; else b = mid;
                }
                if (a >= onsets.Length || onsets[a] > hi) steady.Add(fr);
            }
            frames = steady;
            int count = frames.Count, take = Math.Min(count, FidelityMaxFrames);
            var chain = new Biquad[4];
            chain[0] = Biquad.Design(FidelityFrom, rate, 0.5412, true);
            chain[1] = Biquad.Design(FidelityFrom, rate, 1.3065, true);
            chain[2] = Biquad.Design(FidelityTo, rate, 0.5412, false);
            chain[3] = Biquad.Design(FidelityTo, rate, 1.3065, false);
            int warm = rate / 50;
            var values = new System.Collections.Generic.List<(double Value, double Weight)>();
            var kept = Diagnostics ? new System.Collections.Generic.List<(int, int, double, double)>() : null;
            for (int i = 0; i < take; i++)
            {
                var fr = frames[(int)((long)i * count / take)];
                double v = BandRepeat(left, right, fr.Start, window, fr.Period, reach + 2, warm, chain);
                if (double.IsNaN(v)) continue;
                values.Add((v, fr.Energy));
                kept?.Add((fr.Start, fr.Period, fr.Energy, v));
            }
            if (kept != null) info.Measured = kept.ToArray();
            info.FidelityFrames = values.Count;
            if (values.Count == 0) return;
            values.Sort((a, b) => a.Value.CompareTo(b.Value));
            double total = 0;
            foreach (var v in values) total += v.Weight;
            info.Fidelity = WeightedAt(values, 0.5 * total);
            info.FidelityLow = WeightedAt(values, 0.1 * total);
        }

        static double WeightedAt(System.Collections.Generic.List<(double Value, double Weight)> sorted, double mass)
        {
            double run = 0;
            foreach (var v in sorted)
            {
                run += v.Weight;
                if (run >= mass) return v.Value;
            }
            return sorted[^1].Value;
        }

        /// <summary>The best normalised autocorrelation of the band-limited mix of [start, start + window) at a lag within <paramref name="reach"/> of <paramref name="period"/>; NaN when the audio ends first.</summary>
        static double BandRepeat(float[] left, float[] right, int start, int window, int period, int reach, int warm, Biquad[] chain)
        {
            int from = Math.Max(0, start - warm);
            int to = start + window + period + reach + 2;
            if (to > left.Length) return double.NaN;
            int m = to - from, at = start - from;
            var work = new double[m];
            var state = new Biquad[chain.Length];
            Array.Copy(chain, state, chain.Length);
            for (int i = 0; i < m; i++)
            {
                double x = 0.5 * (left[from + i] + right[from + i]);
                for (int k = 0; k < state.Length; k++) x = state[k].Process(x);
                work[i] = x;
            }
            double ea = 0, ef = 0;
            for (int j = 0; j < window; j++)
            {
                ea += work[at + j] * work[at + j];
                double raw = 0.5 * (left[start + j] + right[start + j]);
                ef += raw * raw;
            }
            // Nothing above the fundamental (under 40 dB of the frame is in the band: a sine's leak through the filter repeats, and means nothing).
            if (ea < 1e-18 || ea < MinBandShare * ef) return double.NaN;
            int lo = Math.Max(2, period - reach), hi = period + reach;
            var r = new double[hi - lo + 1];
            int best = 0;
            for (int t = lo; t <= hi; t++)
            {
                double ab = 0, bb = 0;
                for (int j = 0; j < window; j++)
                {
                    double b = work[at + t + j];
                    ab += work[at + j] * b;
                    bb += b * b;
                }
                r[t - lo] = 2 * ab / (ea + bb + 1e-30);
                if (r[t - lo] > r[best]) best = t - lo;
            }
            double peak = r[best];
            if (best > 0 && best < r.Length - 1)
            {
                double a = r[best - 1], b = r[best], c = r[best + 1], den = a - 2 * b + c;
                if (den < 0) peak = b - 0.125 * (c - a) * (c - a) / den;
            }
            return Math.Min(1.0, peak);
        }

        /// <summary>A second-order section (RBJ cookbook), in double, transposed direct form II.</summary>
        struct Biquad
        {
            double b0, b1, b2, a1, a2, z1, z2;

            public static Biquad Design(double hz, int rate, double q, bool highPass)
            {
                double w0 = 2 * Math.PI * hz / rate, alpha = Math.Sin(w0) / (2 * q), c = Math.Cos(w0), a0 = 1 + alpha;
                double p = highPass ? (1 + c) / 2 : (1 - c) / 2;
                return new Biquad { b0 = p / a0, b1 = (highPass ? -(1 + c) : 1 - c) / a0, b2 = p / a0, a1 = -2 * c / a0, a2 = (1 - alpha) / a0 };
            }

            public double Process(double x)
            {
                double y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                return y;
            }
        }
    }

    /// <summary>
    /// One run of the time-domain path: from a start at a beat, output sample after sample. Frames of 2 hops are overlap-added on a grid of
    /// one hop; each starts at the source position the warp map gives for its middle, moved by up to a period to where it best continues the
    /// frame before (so the cycle goes on unbroken); an onset in the coming hop starts a frame exactly there, from the onset, and the frame
    /// before fades out by it. The output does not depend on the block size.
    /// </summary>
    internal sealed class PeriodicRun
    {
        const int Ring = 16384, Mask = Ring - 1;

        /// <summary>The hop (half a frame) for a sample rate: about 23 ms, a multiple of 4.</summary>
        public static int HopFor(int rate) => Math.Max(64, (int)Math.Round(0.0232 * rate / 4) * 4);

        readonly int rate, hs, n, rampLen, preRoll;
        int delta;
        readonly bool mono;
        readonly float[] outL, outR, wsum;
        readonly float[] hann, rise, fall;
        readonly float[] scratchL, scratchR, target, window;
        AudioClipParams p;
        AudioSource source;
        PeriodicInfo info;

        long time;                  // the next output sample (0 at the start)
        long nextFrame;             // the output time the next frame starts at
        long bPrev;                 // where the last frame started in the audio
        bool haveFrame;
        double anchorBeat, anchorBps;
        long anchorTime;

        public PeriodicRun(bool mono, int sampleRate, int maxPeriod = 1100)
        {
            this.mono = mono;
            rate = sampleRate;
            hs = HopFor(sampleRate);
            n = 2 * hs;
            delta = Math.Clamp(maxPeriod, 192, (int)(1.3 * hs)) & ~3;
            rampLen = Math.Max(8, sampleRate / 1000);
            preRoll = Math.Max(2, sampleRate / 2000);
            outL = new float[Ring];
            outR = mono ? outL : new float[Ring];
            wsum = new float[Ring];
            hann = new float[n];
            for (int i = 0; i < n; i++) hann[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            rise = new float[rampLen];
            fall = new float[rampLen];
            for (int i = 0; i < rampLen; i++)
            {
                rise[i] = (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / rampLen));
                fall[i] = (float)(0.5 + 0.5 * Math.Cos(Math.PI * i / rampLen));
            }
            scratchL = new float[n + 16];
            scratchR = new float[n + 16];
            target = new float[hs + 16];
            window = new float[n];
        }

        public int Rate => rate;

        /// <summary>Time since the start, in output samples.</summary>
        public long Time => time;

        /// <summary>Starts at <paramref name="beat"/>.</summary>
        public void Start(AudioClipParams p, PeriodicInfo info, double beat, double bps)
        {
            this.p = p;
            source = p.Source;
            this.info = info;
            // Half a period is enough to find a place where the cycle goes on; more only lets the frames wander from where the map puts them.
            delta = Math.Clamp(info.MaxPeriod / 2 + 32, 192, (int)(1.3 * hs)) & ~3;
            Array.Clear(outL);
            if (!mono) Array.Clear(outR);
            Array.Clear(wsum);
            time = 0;
            nextFrame = -hs;
            haveFrame = false;
            anchorBeat = beat;
            anchorBps = bps;
            anchorTime = 0;
        }

        /// <summary>Same run, new params of the same audio (gain and the like): the map, slices and audio are the run's.</summary>
        public void Update(AudioClipParams p) => this.p = p;

        /// <summary>Whether this run's audio and info are these.</summary>
        public bool Is(AudioSource s) => ReferenceEquals(source, s);

        /// <summary>Renders <paramref name="count"/> samples (added into the buffers times <paramref name="gain"/> of <paramref name="fadeIn"/>, see the voice).</summary>
        public void Next(double beat, double bps, int count, float[] outputL, float[] outputR)
        {
            anchorBeat = beat;
            anchorBps = bps;
            anchorTime = time;
            for (int i = 0; i < count; i++) Pull(out outputL[i], out outputR[i]);
        }

        /// <summary>The next samples with no new position (a fade-out goes on at the speed it had).</summary>
        public void Continue(int count, float[] outputL, float[] outputR)
        {
            anchorBeat += (time - anchorTime) * anchorBps;
            anchorTime = time;
            for (int i = 0; i < count; i++) Pull(out outputL[i], out outputR[i]);
        }

        void Pull(out float l, out float r)
        {
            while (nextFrame <= time) Schedule();
            int slot = (int)(time & Mask);
            float w = wsum[slot];
            if (w > 1e-3f)
            {
                float inv = 1f / w;
                l = Clean(outL[slot] * inv);
                r = mono ? l : Clean(outR[slot] * inv);
            }
            else l = r = 0f;
            // The audio's end: faded out over the last 64 samples, as a loop going round at the end finds it (the stretcher's latency would have
            // carried the end on under the new start; this has none).
            double left = (p.EndBeat - (anchorBeat + (time - anchorTime) * anchorBps)) / anchorBps;
            if (left < ClipVoice.Fade)
            {
                float g = left <= 0 ? 0f : (float)(left / ClipVoice.Fade);
                l *= g;
                r *= g;
            }
            outL[slot] = 0;
            if (!mono) outR[slot] = 0;
            wsum[slot] = 0;
            time++;
        }

        static float Clean(float x) => !float.IsFinite(x) ? 0f : Math.Abs(x) < 1e-30f ? 0f : x;

        double BeatAt(long t) => anchorBeat + (t - anchorTime) * anchorBps;

        /// <summary>The first onset whose output time is in (<paramref name="from"/>, <paramref name="to"/>], or -1; its time in <paramref name="at"/>.</summary>
        int OnsetIn(long from, long to, out long at)
        {
            at = 0;
            var on = info.Onsets;
            if (on.Length == 0) return -1;
            double lo = p.Map.SampleAt(BeatAt(from)) - 2, hi = p.Map.SampleAt(BeatAt(to)) + 2;
            int a = 0, b = on.Length;
            while (a < b)
            {
                int mid = (a + b) / 2;
                if (on[mid] <= lo) a = mid + 1; else b = mid;
            }
            for (; a < on.Length && on[a] <= hi; a++)
            {
                long t = anchorTime + (long)Math.Round((p.Map.BeatAt(on[a]) - anchorBeat) / anchorBps);
                if (t > from && t <= to) { at = t; return a; }
            }
            return -1;
        }

        static long Index(double position) => double.IsFinite(position) ? (long)Math.Round(Math.Clamp(position, -1e12, 1e12)) : 0;

        void Schedule()
        {
            long T = nextFrame;
            bool onset = false;
            long B = 0;
            if (haveFrame)
            {
                int k = OnsetIn(T, T + hs, out long at);
                if (k >= 0)
                {
                    onset = true;
                    T = at;
                    B = info.Onsets[k] - preRoll;
                }
            }
            // An onset in the frame's second half: the frame ends there.
            long cutAt = -1;
            {
                long guard = onset ? rampLen : 2;
                int k2 = OnsetIn(T + guard - 1, T + n - 1, out long at2);
                if (k2 >= 0) cutAt = at2 - T;
            }
            if (!onset)
            {
                long nominal = Index(p.Map.SampleAt(BeatAt(T + hs))) - hs;
                B = nominal;
                if (haveFrame)
                {
                    // What the frame may play: only its own note, from its onset to the next one's (the search must not move it past
                    // either, or the next note's attack is heard before its time, or the last note's tail after it).
                    var on = info.Onsets;
                    int a = 0, b = on.Length;
                    while (a < b)
                    {
                        int mid = (a + b) / 2;
                        if (on[mid] <= nominal) a = mid + 1; else b = mid;
                    }
                    long audible = cutAt >= 0 ? cutAt + rampLen / 2 : n;
                    long lo = a > 0 ? on[a - 1] - preRoll : long.MinValue / 2;
                    long hi = a < on.Length ? on[a] - preRoll - audible : long.MaxValue / 2;
                    B = lo <= hi ? Search(nominal, lo, hi) : nominal;
                }
            }
            for (int i = 0; i < n; i++)
            {
                float w = hann[i];
                if (onset && i < hs) w = i < rampLen ? rise[i] : 1f;
                if (cutAt >= 0)
                {
                    if (i >= cutAt + rampLen) w = 0;
                    else if (i >= cutAt) w *= fall[(int)(i - cutAt)];
                }
                window[i] = w;
            }
            source.CopyTo(0, B, scratchL.AsSpan(0, n));
            if (!mono) source.CopyTo(1, B, scratchR.AsSpan(0, n));
            for (int i = 0; i < n; i++)
            {
                float w = window[i];
                if (w == 0 || T + i < time) continue;               // before the start (or already out): nowhere in the ring to put it
                int slot = (int)((T + i) & Mask);
                outL[slot] += scratchL[i] * w;
                if (!mono) outR[slot] += scratchR[i] * w;
                wsum[slot] += w;
            }
            bPrev = B;
            haveFrame = true;
            nextFrame = T + hs;
        }

        /// <summary>
        /// Where, within the delta around <paramref name="nominal"/>, the next frame best continues the last one: the normalised
        /// cross-correlation of its first half with what the last frame would have played next, searched on the 4-fold decimated
        /// signal and then settled at full rate, with a small pull towards the nominal place (a periodic signal matches at every period).
        /// </summary>
        long Search(long nominal, long minB, long maxB)
        {
            var m4 = info.Mid4;
            int pad = info.Pad, h4 = hs / 4;
            long t4 = (bPrev + hs) >> 2;
            long lo4 = (Math.Max(nominal - delta, minB) + 3) >> 2, hi4 = Math.Min(nominal + delta, maxB) >> 2;
            if (hi4 < lo4) return Math.Clamp(nominal, minB, maxB);
            // The array covers audio sample 0 at index pad (4 audio samples to 1): outside it the search has nothing to compare.
            if (t4 + pad < 0 || t4 + pad + h4 > m4.Length) return Math.Clamp(nominal, minB, maxB);
            double et = 0;
            for (int j = 0; j < h4; j++) et += m4[pad + t4 + j] * (double)m4[pad + t4 + j];
            if (et < 1e-9) return Math.Clamp(nominal, minB, maxB);
            long n4 = hi4 - lo4 + 1;
            int usable = 0;
            double bestScore = double.NegativeInfinity;
            long bestC = long.MinValue;
            double ec = 0;
            long startIndex = pad + lo4;
            if (startIndex < 0 || startIndex + n4 + h4 > m4.Length) return Math.Clamp(nominal, minB, maxB);
            for (int j = 0; j < h4; j++) ec += m4[startIndex + j] * (double)m4[startIndex + j];
            for (long c = 0; c < n4; c++)
            {
                long at = startIndex + c;
                double dot = 0;
                for (int j = 0; j < h4; j++) dot += m4[at + j] * (double)m4[pad + t4 + j];
                double score = dot / Math.Sqrt(ec * et + 1e-12) - 0.02 * Math.Abs((lo4 + c) * 4 - nominal) / delta;
                usable++;
                if (score > bestScore) { bestScore = score; bestC = lo4 + c; }
                ec += m4[at + h4] * (double)m4[at + h4] - m4[at] * (double)m4[at];
                if (ec < 0) ec = 0;
            }
            if (usable == 0 || bestC == long.MinValue) return Math.Clamp(nominal, minB, maxB);
            // Settle at full rate, +- 4 samples around the decimated best.
            long first = bestC * 4 - 4;
            int span = hs + 9;
            source.CopyTo(0, first, scratchL.AsSpan(0, span));
            if (!mono) source.CopyTo(1, first, scratchR.AsSpan(0, span));
            long tgtAt = bPrev + hs;
            for (int j = 0; j < hs; j++) target[j] = 0.5f * (source.L(tgtAt + j) + (mono ? source.L(tgtAt + j) : source.R(tgtAt + j)));
            double tn = 0;
            for (int j = 0; j < hs; j++) tn += target[j] * (double)target[j];
            long best = nominal;
            double bs = double.NegativeInfinity;
            for (int s = 0; s <= 8; s++)
            {
                double dot = 0, ce = 0;
                for (int j = 0; j < hs; j++)
                {
                    float v = mono ? scratchL[s + j] : 0.5f * (scratchL[s + j] + scratchR[s + j]);
                    dot += v * (double)target[j];
                    ce += v * (double)v;
                }
                long cand = first + s;
                if (cand < minB || cand > maxB) continue;
                double score = dot / Math.Sqrt(ce * tn + 1e-12) - 0.02 * Math.Abs(cand - nominal) / delta;
                if (score > bs) { bs = score; best = cand; }
            }
            return Math.Clamp(best, minB, maxB);
        }
    }

    /// <summary>
    /// The voice of the time-domain path: two runs (the playing one and the one fading out after a jump or a stop, as ProVoice's two
    /// stretchers), the clip's gain, and the 64-sample fade-in after a start.
    /// </summary>
    internal sealed class PeriodicVoice
    {
        const int Fade = ClipVoice.Fade;
        const int Block = 256;

        PeriodicRun current, fading;
        readonly float[] bufL = new float[Block], bufR = new float[Block];
        readonly int rate;
        readonly bool mono;
        float fadeGain = 1;
        bool active;
        int fadeIn, fadeLeft;

        public PeriodicVoice(bool mono, int sampleRate)
        {
            this.mono = mono;
            rate = sampleRate;
            current = new PeriodicRun(mono, sampleRate);
            fading = new PeriodicRun(mono, sampleRate);
        }

        public bool Active => active;
        public bool Fading => fadeLeft > 0;
        public int Rate => rate;
        public bool Mono => mono;

        /// <summary>Tests: false sends every clip to the stretcher, as before this path existed.</summary>
        internal static volatile bool Enabled = true;

        /// <summary>Whether a clip is played by this path: Pro or Complex, the audio found a bass line, no transposition, the source rate this voice's.</summary>
        public static bool Qualifies(AudioClipParams p, double rate, int voiceRate)
        {
            if (!Enabled || p == null || (p.Mode != WarpMode.Pro && p.Mode != WarpMode.Complex)) return false;
            var s = p.Source;
            if (s.SampleRate != voiceRate) return false;                // a source that goes to the stream cache while it plays keeps the verdict made in memory (reads go through CopyTo)
            if (Math.Abs(p.PitchRatio * rate - 1.0) > 1e-9) return false;
            var info = s.PeriodicTried ? s.Periodic : null;
            return info != null && info.Use;
        }

        public void Start(AudioClipParams p, double beat, double bps)
        {
            BeginFade();
            var info = p.Source.Periodic;
            current.Start(p, info, beat, bps);
            active = true;
            fadeIn = Fade;
        }

        public void Silence()
        {
            active = false;
            fadeLeft = 0;
        }

        public void BeginFade()
        {
            if (!active) return;
            (current, fading) = (fading, current);
            fadeLeft = Fade;
            active = false;
        }

        public void RenderFading(float[] left, float[] right, int offset, int count)
        {
            if (fadeLeft <= 0) return;
            int total = Math.Min(count, fadeLeft);
            for (int done = 0; done < total;)
            {
                int n = Math.Min(total - done, Block);
                fading.Continue(n, bufL, bufR);
                for (int i = 0; i < n; i++)
                {
                    fadeLeft--;
                    float g = fadeGain * fadeLeft / Fade;
                    left[offset + done + i] += bufL[i] * g;
                    right[offset + done + i] += (mono ? bufL[i] : bufR[i]) * g;
                }
                done += n;
            }
        }

        public void Render(AudioClipParams p, double beat, double bps, float[] left, float[] right, int offset, int count)
        {
            if (!active) return;
            current.Update(p);
            fadeGain = p.Gain;
            for (int done = 0; done < count;)
            {
                int n = Math.Min(count - done, Block);
                current.Next(beat + done * bps, bps, n, bufL, bufR);
                for (int i = 0; i < n; i++)
                {
                    float g = p.Gain;
                    if (fadeIn > 0)
                    {
                        g *= (Fade - fadeIn + 1) / (float)Fade;
                        fadeIn--;
                    }
                    left[offset + done + i] += bufL[i] * g;
                    right[offset + done + i] += (mono ? bufL[i] : bufR[i]) * g;
                }
                done += n;
            }
        }
    }
}
