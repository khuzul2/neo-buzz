#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md, AW1 as built): auto-warp. The beats of the audio (BeatTracker) become warp markers: one
// tempo and the first downbeat when the tempo is steady, a marker on every bar (and on the beats where the drift within a bar is large)
// when it is not. Pure (ReBuzz.SessionTests links this file).

using System;
using System.Collections.Generic;

namespace ReBuzz.Core.Clips
{
    /// <summary>What auto-warp found: the warp markers and the tempo (see <see cref="AudioAnalysis.AutoWarp"/>).</summary>
    internal sealed class AutoWarpResult
    {
        /// <summary>No beats were found: nothing to do (the markers are empty, the tempo 0).</summary>
        public bool Found;
        /// <summary>One tempo over the audio (within 1 %): the markers are the first downbeat only, or none for a loop of whole bars.</summary>
        public bool Steady;
        /// <summary>The tempo, BPM: the mean over the beats, or for a loop the one its whole bars make.</summary>
        public double Bpm;
        /// <summary>Audio sample, clip beat: beat 0 is the first downbeat (at or after the start asked for).</summary>
        public List<(double Sample, double Beat)> Markers = new List<(double, double)>();
        /// <summary>The audio sample of the first downbeat; the beats the tracker found from it on, and how many beats a bar has.</summary>
        public double FirstDownbeat;
        public double[] Beats = Array.Empty<double>();
        public int BeatsPerBar = 4;
        /// <summary>The audio is a whole number of bars at its tempo, and was kept as it was (a loop).</summary>
        public bool Loop;
        /// <summary>The beats came from the beat model (not AW1's DSP track): the telemetry of the model-or-DSP choice.</summary>
        public bool FromModel;

        public void Deconstruct(out List<(double Sample, double Beat)> markers, out double bpm) { markers = Markers; bpm = Bpm; }
    }

    internal static partial class AudioAnalysis
    {
        /// <summary>The most a beat may be off the straight line of its bar before it gets a marker of its own, seconds.</summary>
        const double BeatMarkerTolerance = 0.010;
        /// <summary>A tempo within this of one throughout (over 16 beats) is steady.</summary>
        const double SteadyTolerance = 0.01;

        /// <summary>
        /// Finds the beats of the audio, follows the tempo where it changes, and returns the warp markers and the tempo. Beat 0 of the markers
        /// is the first downbeat (of <paramref name="beatsPerBar"/> beats a bar), the first one at or after <paramref name="fromSeconds"/>.
        /// A steady tempo (within 1 %) gives the tempo and the first downbeat only (two markers on the straight line), and a loop of whole
        /// bars none at all (it stays as <see cref="Tempo(AudioSource,int)"/> has it); a tempo that moves gives a marker on every bar and
        /// on every beat that is more than 10 ms off the line of its bar. Off the audio thread; the beats are found once per source.
        /// </summary>
        public static AutoWarpResult AutoWarp(AudioSource source, int beatsPerBar = 4, double fromSeconds = 0)
        {
            var result = new AutoWarpResult { BeatsPerBar = Math.Clamp(beatsPerBar, 1, 16) };
            int bpb = result.BeatsPerBar;
            if (source == null || source.Length < 1) return result;
            return FromTrack(source, BeatTracker.Of(source), beatsPerBar, fromSeconds);
        }

        /// <summary>The markers for a beat track already found (AW3: the evaluation tool tries settings on one track).</summary>
        internal static AutoWarpResult FromTrack(AudioSource source, BeatTrack track, int beatsPerBar = 4, double fromSeconds = 0)
        {
            var result = new AutoWarpResult { BeatsPerBar = Math.Clamp(beatsPerBar, 1, 16) };
            int bpb = result.BeatsPerBar;
            if (source == null || source.Length < 1 || track == null) return result;
            if (!track.Found) return result;
            int rate = source.SampleRate;

            // The tempo octave as Tempo has it: 80 to 160 BPM.
            var (beats, low, full, change) = track.FromModel ? (track.Beats, track.Low, track.Full, track.Change) : Octave(track);
            int n = beats.Length;
            if (n < 4) return result;

            // The downbeat: the phase of the beats per bar that is accented most over the piece.
            int phase = track.FromModel ? ModelPhase(track.Down, bpb) : DownbeatPhase(low, full, change, bpb);
            double from = Math.Max(0, fromSeconds) * rate;
            int i0 = -1;
            for (int i = 0; i < n; i++)
                if (i % bpb == phase && beats[i] >= from - 0.02 * rate) { i0 = i; break; }
            if (i0 < 0 || n - i0 < 2) return result;
            int m = n - i0;
            var b = new double[m];
            Array.Copy(beats, i0, b, 0, m);
            result.Beats = b;
            result.FirstDownbeat = b[0];
            result.Found = true;
            result.FromModel = track.FromModel;

            // A line through the beats: the steady tempo, and whether it is one.
            LineFit(b, out double a0, out double per);
            double bpm = 60.0 * rate / per;
            int win = Math.Min(16, m - 1);
            bool steady = true;
            for (int w = 0; w + win < m && steady; w += Math.Max(1, win / 4))
            {
                double local = (b[w + win] - b[w]) / win;
                if (Math.Abs(local / per - 1) > SteadyTolerance) steady = false;
            }
            double seconds = source.Seconds;

            if (steady)
            {
                // A loop of whole bars stays as it is (its start is its bar 1, its tempo the bars'): nothing changes for it.
                double bars = BarsTempo(seconds, bpb);
                if (bars > 0 && (Math.Abs(bars - bpm) / bpm < 0.015 || (track.FromModel && (Math.Abs(bars - 2 * bpm) / bars < 0.015 || Math.Abs(bars - 0.5 * bpm) / bars < 0.015))) && from <= 0)
                {
                    result.Steady = true;
                    result.Bpm = bars;
                    result.Loop = true;
                    return result;
                }
                // AW3: a tempo within 1 % over 16 beats can still wander: the phase adds up (a human performance), and one straight line then lands tens of ms off the hits.
                // Steady also needs the beats to stay near the line (the residuals' 95th percentile and largest, ms).
                if (!ResidualsWithin(b, a0, per, rate)) steady = false;
            }
            result.Steady = steady;

            if (steady)
            {
                result.Bpm = Math.Round(bpm, 3);
                double s0 = a0;
                double spb = rate * 60.0 / result.Bpm;
                int last = Math.Max(1, (int)Math.Floor((source.Length - s0) / spb));
                if (Math.Abs(s0) < 0.005 * rate && from <= 0 && Math.Abs(spb - per) < 0.0005 * spb) return result;       // already as the straight map has it
                result.Markers.Add((s0, 0));
                result.Markers.Add((s0 + spb * last, last));
                result.FirstDownbeat = s0;
                return result;
            }

            result.Bpm = Math.Round(60.0 * rate * (m - 1) / (b[m - 1] - b[0]), 3);
            var marks = new SortedSet<int>();
            for (int j = 0; j < m; j += bpb) marks.Add(j);
            marks.Add(m - 1);
            // The beats within a bar that are off the straight line of their bar: split where most off, until none is.
            var bars2 = new List<int>(marks);
            double tol = BeatMarkerTolerance * rate;
            for (int k = 0; k + 1 < bars2.Count; k++) Split(b, bars2[k], bars2[k + 1], tol, marks);
            foreach (int j in marks) result.Markers.Add((b[j], j));
            return result;
        }

        /// <summary>AW3: the largest and the 95th percentile of the beats' distance from the line through them (ms) that still counts as steady. Infinite: not checked. Set on the validation split.</summary>
        internal static double SteadyResidualP95Ms = double.PositiveInfinity, SteadyResidualMaxMs = double.PositiveInfinity;

        static bool ResidualsWithin(double[] b, double a0, double per, int rate)
        {
            if (double.IsInfinity(SteadyResidualP95Ms) && double.IsInfinity(SteadyResidualMaxMs)) return true;
            int m = b.Length;
            var r = new double[m];
            for (int j = 0; j < m; j++) r[j] = Math.Abs(b[j] - (a0 + per * j)) * 1000.0 / rate;
            Array.Sort(r);
            double p95 = r[Math.Min(m - 1, (int)Math.Ceiling(0.95 * m) - 1)];
            return p95 <= SteadyResidualP95Ms && r[m - 1] <= SteadyResidualMaxMs;
        }

        static void Split(double[] b, int lo, int hi, double tol, SortedSet<int> marks)
        {
            if (hi - lo < 2) return;
            double worst = 0;
            int at = -1;
            for (int j = lo + 1; j < hi; j++)
            {
                double line = b[lo] + (b[hi] - b[lo]) * (j - lo) / (hi - lo);
                double dev = Math.Abs(b[j] - line);
                if (dev > worst) { worst = dev; at = j; }
            }
            if (at < 0 || worst <= tol) return;
            marks.Add(at);
            Split(b, lo, at, tol, marks);
            Split(b, at, hi, tol, marks);
        }

        /// <summary>The tempo the audio's length makes it as a whole number of bars (80 to 160 BPM), or 0 when none does: <see cref="Tempo(AudioSource,int,out bool)"/>'s rule.</summary>
        static double BarsTempo(double seconds, int beatsPerBar)
        {
            if (seconds <= 0) return 0;
            for (int bars = 1; bars <= 1024; bars *= 2)
            {
                double bpm = bars * beatsPerBar * 60 / seconds;
                if (bpm >= 80 && bpm < 160) return bpm;
                if (bpm >= 160) break;
            }
            return 0;
        }

        static void LineFit(double[] b, out double intercept, out double slope)
        {
            int m = b.Length;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int j = 0; j < m; j++) { sx += j; sy += b[j]; sxx += (double)j * j; sxy += j * b[j]; }
            double den = m * sxx - sx * sx;
            slope = den > 0 ? (m * sxy - sx * sy) / den : (b[m - 1] - b[0]) / Math.Max(1, m - 1);
            intercept = (sy - slope * sx) / m;
        }

        /// <summary>The tracker's beats at a tempo of 80 to 160 BPM: every second one when it found over 160, a beat between each two when it found under 80.</summary>
        static (double[] Beats, double[] Low, double[] Full, double[] Change) Octave(BeatTrack t)
        {
            int n = t.Beats.Length;
            if (t.MedianBpm >= 160)
            {
                // The offset whose beats are accented most.
                double[] sum = new double[2];
                for (int i = 0; i < n; i++) sum[i & 1] += t.Low[i] + t.Full[i];
                int off = sum[1] > sum[0] ? 1 : 0;
                int k = (n - off + 1) / 2;
                var bs = new double[k]; var lo = new double[k]; var fu = new double[k]; var ch = new double[k];
                for (int j = 0; j < k; j++)
                {
                    bs[j] = t.Beats[off + 2 * j]; lo[j] = t.Low[off + 2 * j]; fu[j] = t.Full[off + 2 * j]; ch[j] = t.Change[off + 2 * j];
                }
                return (bs, lo, fu, ch);
            }
            if (t.MedianBpm > 0 && t.MedianBpm < 80)
            {
                int k = 2 * n - 1;
                var bs = new double[k]; var lo = new double[k]; var fu = new double[k]; var ch = new double[k];
                for (int i = 0; i < n; i++)
                {
                    bs[2 * i] = t.Beats[i]; lo[2 * i] = t.Low[i]; fu[2 * i] = t.Full[i]; ch[2 * i] = t.Change[i];
                    if (i + 1 < n) bs[2 * i + 1] = 0.5 * (t.Beats[i] + t.Beats[i + 1]);
                }
                return (bs, lo, fu, ch);
            }
            return (t.Beats, t.Low, t.Full, t.Change);
        }

        const double WeightLow = 1.0, WeightFull = 0.6, WeightChange = 0.9;

        /// <summary>Which beat (0 to bpb-1) of the beats is the first of its bar: the phase whose beats are accented most.</summary>
        static int DownbeatPhase(double[] low, double[] full, double[] change, int bpb)
        {
            if (bpb <= 1) return 0;
            int n = low.Length;
            var zl = Z(low); var zf = Z(full); var zc = Z(change);
            var sum = new double[bpb];
            for (int i = 0; i < n; i++) sum[i % bpb] += WeightLow * zl[i] + WeightFull * zf[i] + WeightChange * zc[i];
            int best = 0;
            for (int p = 1; p < bpb; p++) if (sum[p] > sum[best] + 1e-9) best = p;
            return best;
        }

        /// <summary>The model's bar phase: the phase of the beats whose downbeat activation is highest over the piece.</summary>
        static int ModelPhase(double[] down, int bpb)
        {
            if (bpb <= 1) return 0;
            var sum = new double[bpb];
            for (int i = 0; i < down.Length; i++) sum[i % bpb] += down[i];
            int best = 0;
            for (int p = 1; p < bpb; p++) if (sum[p] > sum[best] + 1e-9) best = p;
            return best;
        }

        static double[] Z(double[] x)
        {
            int n = x.Length;
            double mean = 0;
            foreach (var v in x) mean += v;
            mean /= n;
            double var = 0;
            foreach (var v in x) var += (v - mean) * (v - mean);
            double sd = Math.Sqrt(var / n);
            var z = new double[n];
            if (sd < 1e-12) return z;
            for (int i = 0; i < n; i++) z[i] = Math.Clamp((x[i] - mean) / sd, -2, 4);
            return z;
        }
    }
}
