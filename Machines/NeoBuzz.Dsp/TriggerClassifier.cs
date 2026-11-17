#nullable disable
// Neo machines shared DSP (docs/specs/pitch.md §4): the trigger classifier Neo Voice to MIDI's trained sounds and Neo Audio
// to MIDI's drum mode share.
//  - TriggerFeatures: per onset, 19 numbers from the first 18 ms (from 3 ms before the onset): the band shares of 8 octave
//    bands in dB re the total (loudness drops out), the spectral centroid (log2 of Hz re 1 kHz), the flatness of 16 bands, the
//    zero-crossing rate, the steepest attack (dB per ms, 1 ms blocks), the time to the peak block (ms), 5 cepstral
//    coefficients (DCT-II of the 16 bands' log energies, 1 … 5), and the decay (dB from the peak block to the mean of the
//    last three: what tells a closed hat from an open one inside 18 ms).
//    The spectra are those of the 18 ms from the onset less (90 % of) the spectrum of the 18 ms before it, floored at 5 % of the
//    first: the tail of the sound before (an open hat under a kick) and the room's noise drop out of the band shares.
//  - TriggerClassifier: k-NN (k = 3) over the examples, features standardised by the training set's own mean and deviation
//    and, optionally, weighted by how much of each feature's variance lies between classes (a Fisher ratio, square-rooted,
//    capped); a vote weighted by 1/distance; a confidence from the vote and the distance; and a reject class ("none of
//    these") when the nearest example of the winning class is further than RejectScale times that class's own typical
//    nearest-neighbour distance (leave-one-out over its training examples). About 10 examples per class are enough. The
//    examples save and load as plain text (Serialize, Deserialize).
//  - TriggerEngine: the detector, the features and the classifier in one stream: an onset waits for its 18 ms to come in,
//    is classified, and reported with the delay (the onset's position and the position it became ready).
// Allocation-free per block after construction (Serialize, Deserialize and Train excepted: training is off the audio thread).

using System;
using System.Globalization;
using System.Text;

namespace NeoBuzz.Dsp
{
    internal sealed class TriggerFeatures
    {
        public const int Dimension = 19;
        public const double LeadSeconds = 0.003, DefaultLengthSeconds = 0.018;
        public readonly double LengthSeconds;
        public readonly double SampleRate;
        public readonly int Length, Lead;
        readonly ComplexFft fft;
        readonly float[] re, im, window, pre, preIm;
        readonly double[] band8 = new double[8], pre8 = new double[8], pre16 = new double[16];
        readonly int n;
        readonly double[] edges8 = new double[9], edges16 = new double[17];
        readonly double[] band16 = new double[16], logBand = new double[16], eBlock = new double[32];
        static readonly double[] Dct = BuildDct();

        static double[] BuildDct()
        {
            var t = new double[6 * 16];
            for (int c = 0; c < 6; c++)
                for (int b = 0; b < 16; b++)
                    t[c * 16 + b] = PortableMath.Cos(Math.PI * c * (b + 0.5) / 16);
            return t;
        }

        public TriggerFeatures(double sampleRate, double lengthSeconds = DefaultLengthSeconds)
        {
            SampleRate = sampleRate;
            LengthSeconds = lengthSeconds;
            Length = (int)Math.Round(LengthSeconds * sampleRate);
            Lead = (int)Math.Round(LeadSeconds * sampleRate);
            n = 64;
            while (n < Length) n <<= 1;
            fft = new ComplexFft(n);
            re = new float[n]; im = new float[n]; window = new float[Length];
            pre = new float[n]; preIm = new float[n];
            // A Tukey window: flat in the middle, a raised cosine over 20 % at each end.
            int edge = Math.Max(1, Length / 5);
            for (int i = 0; i < Length; i++)
            {
                double w = 1;
                if (i < edge) w = 0.5 - 0.5 * PortableMath.Cos(Math.PI * i / edge);
                else if (i >= Length - edge) w = 0.5 - 0.5 * PortableMath.Cos(Math.PI * (Length - 1 - i) / edge);
                window[i] = (float)w;
            }
            double top = Math.Min(20000.0, 0.45 * sampleRate);
            for (int i = 0; i <= 8; i++) edges8[i] = i == 0 ? 100 : (i == 8 ? top : 100 * PortableMath.Pow(2, i));
            // 100 Hz … 12.8 kHz doubling; the last band runs to the top.
            for (int i = 0; i <= 16; i++) edges16[i] = 100.0 * PortableMath.Pow(top / 100.0, i / 16.0);
        }

        /// <summary>The features of the audio from <paramref name="onsetSample"/> − 3 ms for 18 ms, read from <paramref name="read"/> (a ring: index = position &amp; mask).</summary>
        public void Extract(float[] ring, int mask, long onsetSample, float[] features)
        {
            long start = onsetSample - Lead;
            int len = Length;
            int zc = 0;
            float prev = 0;
            for (int i = 0; i < len; i++)
            {
                float v = ring[(int)((start + i) & mask)];
                re[i] = v * window[i];
                im[i] = 0;
                if (i > 0 && ((v >= 0) != (prev >= 0))) zc++;
                prev = v;
            }
            for (int i = len; i < n; i++) { re[i] = 0; im[i] = 0; }
            // The 18 ms before the lead (the background).
            for (int i = 0; i < len; i++) { pre[i] = ring[(int)((start - len + i) & mask)] * window[i]; preIm[i] = 0; }
            for (int i = len; i < n; i++) { pre[i] = 0; preIm[i] = 0; }

            // The attack: energy of 1 ms blocks.
            int blk = Math.Max(4, (int)(0.001 * SampleRate));
            int nb = Math.Min(eBlock.Length, len / blk);
            double peakE = 0;
            int peakB = 0;
            for (int b = 0; b < nb; b++)
            {
                double e = 0;
                for (int i = 0; i < blk; i++) { float v = ring[(int)((start + b * blk + i) & mask)]; e += (double)v * v; }
                eBlock[b] = e + 1e-12 * blk;
                if (e > peakE) { peakE = e; peakB = b; }
            }
            double slope = 0;
            for (int b = 1; b < nb; b++)
            {
                double up = 10.0 * PortableMath.Log10(eBlock[b] / eBlock[b - 1]);
                if (up > slope) slope = up;
            }

            fft.Transform(re, im, false);
            fft.Transform(pre, preIm, false);
            double binHz = SampleRate / n;
            // Band powers of the onset's 18 ms and of the 18 ms before it, then the second less most of the first, band by band
            // (a band holds many bins, so the noise-like scatter of a single bin's power is averaged out before subtracting).
            for (int b = 0; b < 8; b++) { band8[b] = 0; pre8[b] = 0; }
            for (int b = 0; b < 16; b++) { band16[b] = 0; pre16[b] = 0; }
            for (int k = 1; k <= n / 2; k++)
            {
                double f = k * binHz;
                if (f < 100 || f >= edges8[8]) continue;
                double post = (double)re[k] * re[k] + (double)im[k] * im[k];
                double bg = (double)pre[k] * pre[k] + (double)preIm[k] * preIm[k];
                int b8 = 0;
                while (b8 < 7 && f >= edges8[b8 + 1]) b8++;
                band8[b8] += post; pre8[b8] += bg;
                int b16 = 0;
                while (b16 < 15 && f >= edges16[b16 + 1]) b16++;
                band16[b16] += post; pre16[b16] += bg;
            }
            double total = 1e-18, total16 = 1e-18, cNum = 0;
            for (int b = 0; b < 8; b++) { band8[b] = Math.Max(band8[b] - 0.9 * pre8[b], 0.05 * band8[b]); total += band8[b]; }
            for (int b = 0; b < 16; b++)
            {
                band16[b] = Math.Max(band16[b] - 0.9 * pre16[b], 0.05 * band16[b]);
                total16 += band16[b];
                cNum += band16[b] * Math.Sqrt(edges16[b] * edges16[b + 1]);
            }
            for (int b = 0; b < 8; b++) features[b] = (float)(10.0 * PortableMath.Log10(band8[b] / total + 1e-9));
            features[8] = (float)PortableMath.Log2(Math.Max(100.0, cNum / total16) / 1000.0);
            total = total16;

            double gm = 0, am = 0;
            for (int b = 0; b < 16; b++)
            {
                double dens = band16[b] / (edges16[b + 1] - edges16[b]) + 1e-18;
                gm += PortableMath.Log(dens);
                am += dens;
                logBand[b] = PortableMath.Log10(band16[b] / total + 1e-9);
            }
            features[9] = (float)(10.0 * PortableMath.Log10(PortableMath.Exp(gm / 16) / (am / 16)));
            features[10] = (float)zc / len;
            features[11] = (float)Math.Min(slope, 60.0) / 10f;
            features[12] = (float)peakB;
            for (int c = 1; c <= 5; c++)
            {
                double s = 0;
                for (int b = 0; b < 16; b++) s += logBand[b] * Dct[c * 16 + b];
                features[12 + c] = (float)s;
            }
            // The decay: the mean energy of the last six blocks against the six after the lead (the first ms hold the attack).
            double early = 0, late = 0;
            int six = Math.Min(6, nb / 3);
            int firstAt = Math.Min(nb - 1, Lead / blk + 1);
            for (int b = 0; b < six; b++) { early += eBlock[Math.Min(nb - 1, firstAt + b)]; late += eBlock[nb - 1 - b]; }
            features[18] = (float)Math.Clamp(10.0 * PortableMath.Log10((late + 1e-30) / (early + 1e-30)), -40.0, 6.0) / 10f;
        }
    }

    /// <summary>The classifier's answer: the class (−1 for "none of these"), how sure, and how far the nearest example of the winning class was in its own units.</summary>
    internal struct TriggerResult
    {
        public int Class;
        public float Confidence;
        /// <summary>The nearest example's distance over the class's typical nearest-neighbour distance: 1 = typical, above <see cref="TriggerClassifier.RejectScale"/> = rejected.</summary>
        public float Distance;
    }

    internal sealed class TriggerClassifier
    {
        public readonly int MaxClasses, MaxExamples;
        public const int Dim = TriggerFeatures.Dimension;

        public int K = 3;
        /// <summary>A hit further than this many typical distances from the nearest example of its class is "none of these". 0 turns the reject off.</summary>
        public float RejectScale = 2.5f;
        /// <summary>Weight each feature by how much of its variance is between classes.</summary>
        public bool FisherWeights = true;

        readonly float[] raw, z, mean, scale;
        readonly int[] label;
        readonly float[] radius;
        readonly int[] perClass;
        readonly float[] dist = new float[8];
        readonly int[] topClass = new int[8];
        readonly float[] vote;
        int count;
        bool trained;

        public TriggerClassifier(int maxClasses = 8, int maxExamples = 512)
        {
            MaxClasses = maxClasses; MaxExamples = maxExamples;
            raw = new float[maxExamples * Dim];
            z = new float[maxExamples * Dim];
            mean = new float[Dim]; scale = new float[Dim];
            label = new int[maxExamples];
            radius = new float[maxClasses];
            perClass = new int[maxClasses];
            vote = new float[maxClasses];
        }

        public int Count => count;
        public bool Trained => trained;
        public int ExamplesOf(int cls) => perClass[cls];

        public void Clear()
        {
            count = 0; trained = false;
            Array.Clear(perClass, 0, perClass.Length);
        }

        /// <summary>Forgets one class's examples.</summary>
        public void Clear(int cls)
        {
            int w = 0;
            for (int i = 0; i < count; i++)
            {
                if (label[i] == cls) continue;
                if (w != i) { Array.Copy(raw, i * Dim, raw, w * Dim, Dim); label[w] = label[i]; }
                w++;
            }
            count = w; perClass[cls] = 0; trained = false;
        }

        public bool Add(int cls, float[] features)
        {
            if (cls < 0 || cls >= MaxClasses || count >= MaxExamples) return false;
            Array.Copy(features, 0, raw, count * Dim, Dim);
            label[count++] = cls;
            perClass[cls]++;
            trained = false;
            return true;
        }

        /// <summary>Standardises, weights and measures each class's typical distance. Needs at least two classes with an example each.</summary>
        public bool Train()
        {
            trained = false;
            int classes = 0;
            for (int c = 0; c < MaxClasses; c++) if (perClass[c] > 0) classes++;
            if (classes < 1 || count < 2) return false;
            for (int d = 0; d < Dim; d++)
            {
                double m = 0;
                for (int i = 0; i < count; i++) m += raw[i * Dim + d];
                m /= count;
                double v = 0;
                for (int i = 0; i < count; i++) { double e = raw[i * Dim + d] - m; v += e * e; }
                mean[d] = (float)m;
                scale[d] = (float)(1.0 / Math.Max(Math.Sqrt(v / count), 1e-6));
            }
            for (int d = 0; d < Dim; d++)
            {
                float w = 1f;
                if (FisherWeights && classes > 1)
                {
                    // Between-class variance over within-class variance, of the standardised feature.
                    double within = 0, between = 0;
                    for (int c = 0; c < MaxClasses; c++)
                    {
                        if (perClass[c] == 0) continue;
                        double cm = 0;
                        for (int i = 0; i < count; i++) if (label[i] == c) cm += (raw[i * Dim + d] - mean[d]) * scale[d];
                        cm /= perClass[c];
                        between += perClass[c] * cm * cm;
                        for (int i = 0; i < count; i++)
                            if (label[i] == c) { double e = (raw[i * Dim + d] - mean[d]) * scale[d] - cm; within += e * e; }
                    }
                    double ratio = between / Math.Max(within, 1e-9);
                    w = (float)Math.Min(Math.Sqrt(ratio), 3.0);
                    w = Math.Max(w, 0.1f);
                }
                for (int i = 0; i < count; i++) z[i * Dim + d] = (raw[i * Dim + d] - mean[d]) * scale[d] * w;
                scale[d] *= w;
            }
            // Each class's typical nearest-neighbour distance among its own examples.
            for (int c = 0; c < MaxClasses; c++)
            {
                double sum = 0;
                int num = 0;
                for (int i = 0; i < count; i++)
                {
                    if (label[i] != c) continue;
                    float best = float.MaxValue;
                    for (int j = 0; j < count; j++)
                        if (j != i && label[j] == c) best = Math.Min(best, Dist(i * Dim, j * Dim));
                    if (best < float.MaxValue) { sum += best; num++; }
                }
                radius[c] = num > 0 ? (float)Math.Max(sum / num, 1e-3) : 0f;
            }
            // A class whose few examples happen to lie close together would reject its own next hit: no class's radius is
            // under three quarters of the classes' mean.
            double radiusMean = 0; int have = 0;
            for (int c = 0; c < MaxClasses; c++) if (radius[c] > 0) { radiusMean += radius[c]; have++; }
            if (have > 0)
            {
                float floor = (float)(0.75 * radiusMean / have);
                for (int c = 0; c < MaxClasses; c++) if (radius[c] > 0) radius[c] = Math.Max(radius[c], floor);
            }
            trained = true;
            return true;
        }

        float Dist(int a, int b)
        {
            double s = 0;
            for (int d = 0; d < Dim; d++) { double e = z[a + d] - z[b + d]; s += e * e; }
            return (float)Math.Sqrt(s);
        }

        /// <summary>Classifies one feature vector (TriggerFeatures.Dimension numbers). Class −1: untrained, or "none of these".</summary>
        public TriggerResult Classify(float[] features)
        {
            var result = new TriggerResult { Class = -1 };
            if (!trained) return result;
            int k = Math.Clamp(K, 1, dist.Length);
            int have = 0;
            for (int i = 0; i < count; i++)
            {
                double s = 0;
                for (int d = 0; d < Dim; d++)
                {
                    double e = (features[d] - mean[d]) * scale[d] - z[i * Dim + d];
                    s += e * e;
                }
                float dd = (float)Math.Sqrt(s);
                // Insertion into the k nearest.
                int at;
                if (have < k) at = have++;
                else if (dd < dist[k - 1]) at = k - 1;
                else continue;
                while (at > 0 && dist[at - 1] > dd) { dist[at] = dist[at - 1]; topClass[at] = topClass[at - 1]; at--; }
                dist[at] = dd; topClass[at] = label[i];
            }
            if (have == 0) return result;
            Array.Clear(vote, 0, vote.Length);
            float total = 0;
            for (int j = 0; j < have; j++)
            {
                float w = 1f / (dist[j] + 0.05f);
                vote[topClass[j]] += w;
                total += w;
            }
            int win = 0;
            for (int c = 1; c < MaxClasses; c++) if (vote[c] > vote[win]) win = c;
            // The nearest example of the winning class.
            float near = float.MaxValue;
            for (int j = 0; j < have; j++) if (topClass[j] == win) { near = dist[j]; break; }
            float rel = radius[win] > 0 ? near / radius[win] : 0f;
            result.Distance = rel;
            result.Confidence = Math.Clamp((vote[win] / total) * (1f - 0.5f * Math.Min(1f, rel / Math.Max(RejectScale, 1f))), 0f, 1f);
            result.Class = RejectScale > 0 && rel > RejectScale ? -1 : win;
            return result;
        }

        /// <summary>The examples as plain text (versioned): a header line, then one line per example: class, then the features.</summary>
        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("NBTRIG 1 ").Append(Dim).Append(' ').Append(count).Append('\n');
            for (int i = 0; i < count; i++)
            {
                sb.Append(label[i]);
                for (int d = 0; d < Dim; d++) sb.Append(' ').Append(raw[i * Dim + d].ToString("R", CultureInfo.InvariantCulture));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Replaces the examples with those in <paramref name="text"/> and trains. False for text this version can't read.</summary>
        public bool Deserialize(string text)
        {
            Clear();
            if (string.IsNullOrEmpty(text)) return false;
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var head = lines[0].Trim().Split(' ');
            if (head.Length != 4 || head[0] != "NBTRIG" || head[1] != "1" || head[2] != Dim.ToString(CultureInfo.InvariantCulture)) return false;
            var f = new float[Dim];
            for (int i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Trim().Split(' ');
                if (parts.Length != Dim + 1) return false;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cls)) return false;
                for (int d = 0; d < Dim; d++)
                    if (!float.TryParse(parts[d + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out f[d])) return false;
                if (!Add(cls, f)) return false;
            }
            return Train();
        }
    }

    internal struct TriggerEvent
    {
        public long Sample;
        /// <summary>The class, or −1 for "none of these" (also when nothing is trained).</summary>
        public int Class;
        public float Confidence;
        public float Strength;
        public float LevelDb;
        /// <summary>The stream position by which the hit was classified: 18 ms (less the 3 ms lead) after the onset at the earliest.</summary>
        public long ReadyAt;
    }

    /// <summary>Onsets, features and the classifier in one stream.</summary>
    internal sealed class TriggerEngine
    {
        public readonly OnsetDetector Onsets;
        public readonly TriggerFeatures Features;
        public readonly TriggerClassifier Classifier;
        const int Pending = 32;
        readonly float[] ring;
        readonly int mask;
        readonly OnsetEvent[] onsets;
        readonly OnsetEvent[] queue = new OnsetEvent[Pending];
        readonly float[] feat = new float[TriggerFeatures.Dimension];
        int queued;
        long total;

        /// <summary>While &gt;= 0, each onset's features are added to the classifier as examples of this class (and reported as that class).</summary>
        public int Learning = -1;
        public const int ChunkSize = 4096;

        public TriggerEngine(double sampleRate, TriggerClassifier classifier)
        {
            Onsets = new OnsetDetector(sampleRate);
            Features = new TriggerFeatures(sampleRate);
            Classifier = classifier;
            ring = new float[16384];
            mask = ring.Length - 1;
            onsets = new OnsetEvent[Onsets.MaxEvents(ChunkSize)];
        }

        public int MaxEvents(int count) => count / Onsets.Hop + 2;

        public void Reset()
        {
            Onsets.Reset();
            Array.Clear(ring, 0, ring.Length);
            queued = 0; total = 0;
        }

        public int Process(float[] x, int offset, int count, TriggerEvent[] events, int eventOffset)
        {
            if (events.Length - eventOffset < MaxEvents(count) + Pending) throw new ArgumentException("events is shorter than MaxEvents(count) + 32", nameof(events));
            int produced = 0;
            while (count > 0)
            {
                int c = Math.Min(count, ChunkSize);
                for (int i = 0; i < c; i++) ring[(int)((total + i) & mask)] = x[offset + i];
                int found = Onsets.Process(x, offset, c, onsets, 0);
                total += c;
                for (int i = 0; i < found && queued < Pending; i++) queue[queued++] = onsets[i];
                // Classify what has its 18 ms.
                int keep = 0;
                for (int i = 0; i < queued; i++)
                {
                    long ready = queue[i].Sample - Features.Lead + Features.Length;
                    if (ready > total) { queue[keep++] = queue[i]; continue; }
                    Features.Extract(ring, mask, queue[i].Sample, feat);
                    var ev = new TriggerEvent { Sample = queue[i].Sample, Strength = queue[i].Strength, LevelDb = queue[i].LevelDb, ReadyAt = Math.Max(ready, queue[i].ReportedAt) };
                    if (Learning >= 0)
                    {
                        Classifier.Add(Learning, feat);
                        ev.Class = Learning;
                        ev.Confidence = 1f;
                    }
                    else
                    {
                        var r = Classifier.Classify(feat);
                        ev.Class = r.Class;
                        ev.Confidence = r.Confidence;
                    }
                    events[eventOffset + produced++] = ev;
                }
                queued = keep;
                offset += c;
                count -= c;
            }
            return produced;
        }
    }
}
