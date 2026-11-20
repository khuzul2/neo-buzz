#nullable disable
// COMPARISON BASELINE (tools/ConvertEval only, CD1): the AM2 rule-based drum converter as it was on main before CD1, unchanged but renamed. Fork (docs/specs/pitch.md section 9.2): drum hits from audio, offline. The shared onset detector (PT1) finds the hits; a hit that does not
// stand out of the sound just before it (a ring-out, a flam's echo) is dropped; each hit's spectrum is compared with what came before it,
// band by band, and the bands that the hit ADDED say which drums are in it: low thump (kick), a broad mid-range body with noise (snare),
// bright noise that dies fast (closed hat) or goes on (open hat, cymbal), tonal mid-low (tom). A hit can be several drums at once (a
// kick with a hat). No training: the rules come from the physics of the drums, and their thresholds from one set of drum recordings.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NeoBuzz.Dsp;

namespace ReBuzz.Core.AudioLibrary.AudioToMidi
{
    internal static class Am2RuleDrums
    {
        public const string Kick = "Kick", Snare = "Snare", ClosedHat = "Closed hat", OpenHat = "Open hat", LowTom = "Low tom", MidTom = "Mid tom", HighTom = "High tom", Crash = "Crash";

        /// <summary>The General MIDI note of each class (also the keys of Neo Drums).</summary>
        public static int NoteOf(string label) => label switch
        {
            Kick => 36, Snare => 38, ClosedHat => 42, OpenHat => 46, LowTom => 43, MidTom => 47, HighTom => 50, Crash => 49, _ => 37,
        };

        /// <summary>The hit's added energy per band, and what follows.</summary>
        internal sealed class Hit
        {
            public long Sample;
            public double Strength, LevelDb, RiseDb;
            /// <summary>Added energy in the bands (see <see cref="Edges"/>), dB re the loudest band.</summary>
            public double[] Added = new double[Bands];
            public double[] Rise = new double[Bands];
            public double Centroid, Flatness, Tail, LowPeakHz;
        }

        public const int Bands = 6;
        /// <summary>Band edges, Hz: sub, low, mid, upper mid, high, air.</summary>
        public static readonly double[] Edges = { 35, 110, 300, 1200, 5000, 10000, 18000 };

        public static List<ConvertedNote> Convert(float[] x, int rate, ConvertOptions o, Action<double> progress, CancellationToken cancel)
        {
            if (rate > 48000)
            {
                x = Resampler.Resample(x, rate, 48000);
                rate = 48000;
            }
            var hits = Detect(x, rate, o, progress, cancel);
            var notes = Classify(hits, rate);
            progress?.Invoke(1);
            return notes;
        }

        /// <summary>The onsets (PT1's detector) that stand out of what came before, each with its band analysis.</summary>
        public static List<Hit> Detect(float[] x, int rate, ConvertOptions o, Action<double> progress, CancellationToken cancel)
        {
            var det = new OnsetDetector(rate) { Threshold = (float)o.DrumThreshold, MinIntervalSeconds = Math.Max(0.02, o.DrumMinIntervalMs / 1000.0) };
            var events = new OnsetEvent[det.MaxEvents(x.Length) + 8];
            var list = new List<OnsetEvent>();
            int block = 1 << 16;
            var buf = new OnsetEvent[det.MaxEvents(block) + 4];
            for (int pos = 0; pos < x.Length; pos += block)
            {
                cancel.ThrowIfCancellationRequested();
                int n = Math.Min(block, x.Length - pos);
                int got = det.Process(x, pos, n, buf, 0);
                for (int i = 0; i < got; i++) list.Add(buf[i]);
                progress?.Invoke(0.5 * Math.Min(1.0, (pos + n) / (double)Math.Max(1, x.Length)));
            }
            var hits = new List<Hit>();
            var an = new Analyser(rate);
            int done = 0;
            for (int ei = 0; ei < list.Count; ei++)
            {
                var e = list[ei];
                cancel.ThrowIfCancellationRequested();
                var h = an.Analyse(x, e.Sample, ei + 1 < list.Count ? list[ei + 1].Sample : long.MaxValue);
                h.Strength = e.Strength; h.LevelDb = e.LevelDb;
                // A hit has to rise out of the sound before it (a ring-out, an echo, a flam's tail are not new hits).
                if (h.RiseDb >= 2.0) hits.Add(h);
                if (++done % 64 == 0) progress?.Invoke(0.5 + 0.4 * done / Math.Max(1, list.Count));
            }
            return hits;
        }

        /// <summary>Analyses the audio around a sample the way <see cref="Detect"/> does each onset (the evaluation tool and the tests use it on given positions).</summary>
        internal sealed class Analyser
        {
            readonly int rate, n, pre, post;
            readonly ComplexFft fft;
            readonly float[] re, im, window;
            readonly double[] edgeBin = new double[Bands + 1];
            readonly double[] eAfter = new double[Bands], eBefore = new double[Bands];

            public Analyser(int rate)
            {
                this.rate = rate;
                post = (int)Math.Round(0.040 * rate);
                pre = post;
                n = 64;
                while (n < post) n <<= 1;
                n <<= 1;                                                  // zero padded: finer bins for the low bands
                fft = new ComplexFft(n);
                re = new float[n]; im = new float[n]; window = new float[post];
                for (int i = 0; i < post; i++) window[i] = (float)(0.5 - 0.5 * PortableMath.Cos(2 * Math.PI * (i + 0.5) / post));
                double top = Math.Min(18000, 0.45 * rate);
                for (int i = 0; i <= Bands; i++) edgeBin[i] = Math.Min(Edges[i], top) * n / rate;
            }

            public double PeakHz(int from, int to)
            {
                int k0 = Math.Max(1, (int)Math.Ceiling(from * (double)n / rate)), k1 = Math.Min(n / 2 - 1, (int)Math.Floor(to * (double)n / rate));
                int best = k0; double bp = -1;
                for (int k = k0; k <= k1; k++) { double pw = (double)re[k] * re[k] + (double)im[k] * im[k]; if (pw > bp) { bp = pw; best = k; } }
                return best * (double)rate / n;
            }

            void Spectrum(float[] x, long start, double[] into, out double rms)
            {
                double e = 0;
                for (int i = 0; i < post; i++)
                {
                    long p = start + i;
                    float v = p >= 0 && p < x.Length ? x[p] : 0f;
                    e += (double)v * v;
                    re[i] = v * window[i]; im[i] = 0;
                }
                for (int i = post; i < n; i++) { re[i] = 0; im[i] = 0; }
                rms = Math.Sqrt(e / post);
                fft.Transform(re, im, false);
                for (int b = 0; b < Bands; b++) into[b] = 0;
                int lo = (int)Math.Ceiling(edgeBin[0]);
                int hi = (int)Math.Min(n / 2, Math.Floor(edgeBin[Bands]));
                int band = 0;
                for (int k = lo; k < hi; k++)
                {
                    while (band < Bands - 1 && k >= edgeBin[band + 1]) band++;
                    into[band] += (double)re[k] * re[k] + (double)im[k] * im[k];
                }
            }

            public Hit Analyse(float[] x, long sample, long next = long.MaxValue)
            {
                var h = new Hit { Sample = sample };
                long start = sample - (long)Math.Round(0.002 * rate);
                Spectrum(x, start - pre, eBefore, out double before);
                Spectrum(x, start, eAfter, out double after);
                h.LowPeakHz = PeakHz(60, 400);
                h.RiseDb = 20 * Math.Log10((after + 1e-7) / (before + 1e-7));
                double total = 1e-20, peakBand = 0;
                var add = new double[Bands];
                for (int b = 0; b < Bands; b++)
                {
                    // What the hit added: its band energy less what was already ringing (the ring decays: half of it is assumed to still be there).
                    add[b] = Math.Max(eAfter[b] - 0.5 * eBefore[b], 0.02 * eAfter[b]) / (n * (double)post);
                    h.Rise[b] = 10 * Math.Log10((eAfter[b] + 1e-14 * n) / (eBefore[b] + 1e-14 * n));
                    total += add[b];
                    peakBand = Math.Max(peakBand, add[b]);
                }
                for (int b = 0; b < Bands; b++) h.Added[b] = 10 * Math.Log10(add[b] / peakBand + 1e-9);
                // The tail: the level 60 to 140 ms on, against the hit's own, in dB (a hat closes, a cymbal goes on).
                // Only up to the next hit: a hat roll's next hit is not the first one's ring.
                long t0 = sample + (long)(0.04 * rate), t1 = Math.Min(sample + (long)(0.12 * rate), next - (long)(0.008 * rate));
                double head = Rms(x, sample, sample + (long)(0.015 * rate));
                h.Tail = t1 - t0 >= 0.025 * rate ? 20 * Math.Log10((Rms(x, t0, t1) + 1e-7) / (head + 1e-7)) : -99;
                h.LevelDb = 20 * Math.Log10(Math.Max(1e-7, Math.Max(head, after)));
                return h;
            }

            static double Rms(float[] x, long a, long b)
            {
                double e = 0; int c = 0;
                for (long i = Math.Max(0, a); i < Math.Min(x.Length, b); i++) { e += (double)x[i] * x[i]; c++; }
                return c == 0 ? 0 : Math.Sqrt(e / c);
            }
        }

        // ------------------------------------------------------------------ classes

        public static List<ConvertedNote> Classify(List<Hit> hits, int rate)
        {
            var notes = new List<ConvertedNote>();
            if (hits.Count == 0) return notes;
            var levels = hits.Select(h => h.LevelDb).OrderBy(v => v).ToList();
            double reference = levels[Math.Min(levels.Count - 1, (int)(levels.Count * 0.95))];
            foreach (var h in hits)
            {
                double t = h.Sample / (double)rate;
                int velocity = MelodyConverter.VelocityOf(h.LevelDb, reference);
                foreach (string label in Labels(h))
                    notes.Add(new ConvertedNote { Start = t, End = t + 0.1, Pitch = NoteOf(label), Velocity = velocity, Label = label });
            }
            return notes;
        }

        /// <summary>The drums in one hit. Rules on the bands the hit added (dB under the strongest band) and on how far each band rose.</summary>
        public static List<string> Labels(Hit h)
        {
            var r = new List<string>();
            double[] ad = h.Added, ri = h.Rise;
            double sub = ad[0], low = ad[1], mid = ad[2], umid = ad[3], air = ad[5];
            // Kick: a thump in the sub band, which nothing else in a kit has (a low tom lies higher).
            bool kick = ri[0] >= 12 && sub >= -14 && low <= sub - 3;
            // Bright noise: the spectrum rises from the upper mid-range to the top (a snare's, a kick's and a tom's fall), and the top rose.
            double tilt = air - umid;
            // A hat over a ring-out rises little (judge the shape); over a kick or a snare the shape is the loud drum's (judge the rise).
            bool bright = air >= -34 && ((tilt >= 1 && ri[5] >= 4) || (tilt >= -3 && ri[5] >= 8) || (tilt >= -5 && ri[5] >= 14));
            bool cymbal = bright && umid >= -7 && tilt < 8;
            bool hat = bright && !cymbal;
            // Snare: body and noise in the mid-range (under a kick, the rise of the mid-range and the upper mid-range); tom: a tone in the low bands and little above.
            bool snare = ri[2] >= 8 && mid >= -13 && !(bright && !hat && ri[2] < 14);
            bool tom = !kick && !snare && ri[1] >= 6 && low >= -8 && mid <= -13 && !bright;
            if (kick) r.Add(Kick);
            if (snare) r.Add(Snare);
            if (tom) r.Add(h.LowPeakHz < 110 ? LowTom : h.LowPeakHz < 170 ? MidTom : HighTom);
            if (cymbal) r.Add(Crash);
            if (hat) r.Add(h.Tail > -2 ? OpenHat : ClosedHat);
            return r;
        }
    }
}
