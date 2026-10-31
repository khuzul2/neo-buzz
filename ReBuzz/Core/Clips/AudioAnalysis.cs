#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 1.0): what an audio clip's audio is
// like: where its transients are (Beats mode slices there) and its tempo. Pure
// (ReBuzz.SessionTests links this file); run when a clip is made, not on the audio thread.

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Clips
{
    internal static class AudioAnalysis
    {
        const int FrameSize = 1024;
        const int FrameHop = 256;

        /// <summary>
        /// Transients: peaks of spectral flux (how much louder each frequency got) above a
        /// running median, at least 50 ms apart, each placed on the steepest rise of the
        /// waveform's energy near it, a little early so its attack is kept whole.
        /// </summary>
        public static List<double> Onsets(AudioSource source)
        {
            var flux = SpectralFlux(source);
            var onsets = new List<double>();
            if (flux.Length < 3) return onsets;
            int minGap = Math.Max(1, (int)(0.05 * source.SampleRate / FrameHop));
            double mean = flux.Average();
            int lastPeak = -minGap;
            for (int i = 1; i < flux.Length - 1; i++)
            {
                if (flux[i] < flux[i - 1] || flux[i] < flux[i + 1]) continue;
                int from = Math.Max(0, i - 8), to = Math.Min(flux.Length - 1, i + 8);
                var around = new double[to - from + 1];
                Array.Copy(flux, from, around, 0, around.Length);
                Array.Sort(around);
                double threshold = around[around.Length / 2] * 1.5 + mean * 0.5 + 1e-6;
                if (flux[i] <= threshold || i - lastPeak < minGap) continue;
                lastPeak = i;
                onsets.Add(Refine(source, (long)i * FrameHop + FrameSize / 2));
            }
            return onsets;
        }

        static double[] SpectralFlux(AudioSource source)
        {
            int frames = Math.Max(0, (source.Length - FrameSize) / FrameHop + 1);
            var fft = new Fft(FrameSize);
            var re = new double[FrameSize];
            var im = new double[FrameSize];
            var window = new double[FrameSize];
            for (int n = 0; n < FrameSize; n++) window[n] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / FrameSize);
            var previous = new double[FrameSize / 2];
            var flux = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                long at = (long)f * FrameHop;
                for (int n = 0; n < FrameSize; n++)
                {
                    re[n] = source.Mono(at + n) * window[n];
                    im[n] = 0;
                }
                fft.Transform(re, im);
                double sum = 0;
                for (int k = 1; k < FrameSize / 2; k++)
                {
                    double mag = Math.Log(1 + 100 * Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
                    double rise = mag - previous[k];
                    if (rise > 0) sum += rise;
                    previous[k] = mag;
                }
                flux[f] = f == 0 ? 0 : sum;
            }
            return flux;
        }

        /// <summary>The steepest rise of 64-sample energy within a frame of a rough position, 2 ms before it.</summary>
        static double Refine(AudioSource source, long rough)
        {
            const int Block = 64;
            long from = Math.Max(0, rough - FrameSize), to = Math.Min(source.Length - Block, rough + FrameSize / 2);
            double best = double.MinValue;
            long at = rough;
            for (long i = from; i <= to; i += 16)
            {
                double rise = Energy(source, i, Block) - Energy(source, i - Block, Block);
                if (rise > best) { best = rise; at = i; }
            }
            return Math.Max(0, at - 0.002 * source.SampleRate);
        }

        static double Energy(AudioSource source, long at, int count)
        {
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                double v = source.Mono(at + i);
                sum += v * v;
            }
            return sum;
        }

        /// <summary>
        /// The tempo of the audio, and its length in beats. A loop is a whole number of bars
        /// (1, 2, 4, 8, 16, …), which puts its tempo in 80-160 BPM exactly; that is taken when
        /// the transients' own tempo (the autocorrelation of their flux) agrees within 4%,
        /// or when there is no clear one. Otherwise the transients' tempo, in 80-160 BPM.
        /// </summary>
        public static (double Bpm, double Beats) Tempo(AudioSource source, int beatsPerBar = 4)
        {
            double seconds = source.Seconds;
            if (seconds <= 0) return (120, 1);
            beatsPerBar = Math.Max(1, beatsPerBar);
            double barsBpm = 0;
            for (int bars = 1; bars <= 1024; bars *= 2)
            {
                double bpm = bars * beatsPerBar * 60 / seconds;
                if (bpm >= 80 && bpm < 160) { barsBpm = bpm; break; }
                if (bpm >= 160) break;
            }
            double ac = AutocorrelationTempo(source, out double confidence);
            if (barsBpm > 0 && (ac <= 0 || confidence < 0.3 || Math.Abs(ac - barsBpm) / barsBpm < 0.04))
                return (barsBpm, Math.Round(barsBpm * seconds / 60));
            double tempo = ac > 0 ? ac : 120;
            return (tempo, tempo * seconds / 60);
        }

        static double AutocorrelationTempo(AudioSource source, out double confidence)
        {
            confidence = 0;
            var flux = SpectralFlux(source);
            if (flux.Length < 16) return 0;
            double mean = flux.Average();
            for (int i = 0; i < flux.Length; i++) flux[i] = Math.Max(0, flux[i] - mean);
            double frameRate = source.SampleRate / (double)FrameHop;
            double zero = 0;
            for (int i = 0; i < flux.Length; i++) zero += flux[i] * flux[i];
            if (zero <= 0) return 0;
            double bestBpm = 0, best = 0;
            for (double bpm = 80; bpm < 160; bpm += 0.25)
            {
                double lag = 60 / bpm * frameRate;
                double sum = 0;
                // The beat and its multiples (a bar's worth), interpolated between frames.
                for (int m = 1; m <= 4; m++)
                {
                    double l = lag * m;
                    int li = (int)l;
                    double frac = l - li;
                    for (int i = 0; i + li + 1 < flux.Length; i++)
                        sum += flux[i] * (flux[i + li] * (1 - frac) + flux[i + li + 1] * frac) / m;
                }
                if (sum > best) { best = sum; bestBpm = bpm; }
            }
            confidence = best / (zero * (1 + 0.5 + 1.0 / 3 + 0.25));
            return bestBpm;
        }

        /// <summary>
        /// Beats mode's slices: the transients, and the beat grid where they are further apart
        /// than a beat (so a long note still keeps time), as sample positions.
        /// </summary>
        public static double[] Slices(AudioSource source, WarpMap map, IReadOnlyList<double> transients, double grid = 1)
        {
            var slices = new SortedSet<double>(transients.Where(t => t >= 0 && t < source.Length));
            slices.Add(0);
            if (grid > 0)
            {
                double first = map.BeatAt(0), last = map.BeatAt(source.Length);
                for (double b = Math.Ceiling(first / grid) * grid; b < last; b += grid)
                {
                    double s = map.SampleAt(b);
                    // Only where no transient is within a quarter of the grid.
                    double near = 0.25 * grid * map.SamplesPerBeat(b);
                    if (!slices.Any(x => Math.Abs(x - s) < near)) slices.Add(s);
                }
            }
            return slices.ToArray();
        }
    }
}
