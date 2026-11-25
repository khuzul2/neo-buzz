#nullable disable
// Fork (docs/specs/neo-audio.md, AW5): auto-warp that sticks to the grid. After auto-warp has put the markers on the beats, a warp marker goes on every detected transient
// and is pulled to the nearest grid line by the strength, so notes played off the beat land on the grid. A transient further from every line than the reach (a share of a
// grid step) is left alone, and in quiet or sustained passages (no real attack) there are no markers. Pure (ReBuzz.SessionTests and tools/AutoWarpEval link this file).

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Clips
{
    /// <summary>What "to grid" did, for the page's message, the agent's answer and the evaluation.</summary>
    internal sealed class GridWarpReport
    {
        /// <summary>The transients that had no marker because they are beyond the reach of every grid line.</summary>
        public int OutOfReach;
        /// <summary>Transients left out because they are in a quiet or sustained passage (see <see cref="AudioAnalysis.GridOnsets"/>).</summary>
        public int Gated;
        /// <summary>Transients that would have come to the same grid line as a closer one, or whose marker would have stretched the audio too far.</summary>
        public int Skipped;
        /// <summary>Auto-warp found beats first (otherwise the clip's own line was used).</summary>
        public bool BeatsFound;
        /// <summary>Markers made at transients.</summary>
        public int Made;
        /// <summary>Beat markers of auto-warp that gave way to a transient on the same grid line.</summary>
        public int Replaced;
    }

    internal static partial class AudioAnalysis
    {
        /// <summary>A block is quiet when it is this many dB under the loud passages of the audio.</summary>
        const double QuietDb = 35;
        /// <summary>A transient is an attack when the 20 ms after it are at least this many dB louder than the 20 ms before; less is a change within a sustained sound.</summary>
        const double AttackRiseDb = 2;

        /// <summary>
        /// The transients "to grid" puts markers on: <see cref="Onsets(AudioSource,int)"/> at the sensitivity, without those in a quiet passage (the 20 ms after it more than
        /// 35 dB under the audio's loud passages) and without those that are no attack (under 2 dB louder than the 20 ms before it, a sustained note changing). Audio samples;
        /// <paramref name="gated"/> counts what was left out.
        /// </summary>
        public static List<double> GridOnsets(AudioSource source, int sensitivity, out int gated)
        {
            var all = Onsets(source, sensitivity);
            gated = 0;
            if (all.Count == 0) return all;
            source = source.ForAnalysis();
            int rate = source.SampleRate, w = Math.Max(16, rate / 50), step = Math.Max(1, rate / 40);
            // The loud passages: the 90th percentile of 20 ms block levels over the audio.
            var levels = new List<double>();
            for (long at = 0; at + w <= source.Length; at += step) levels.Add(Rms(source, at, w));
            if (levels.Count == 0) return all;
            levels.Sort();
            double loud = levels[Math.Min(levels.Count - 1, (int)(0.9 * levels.Count))];
            var kept = new List<double>(all.Count);
            foreach (double t in all)
            {
                long at = (long)t;
                double after = Rms(source, at + w / 5, w), before = Rms(source, Math.Max(0, at - w), (int)Math.Min(w, Math.Max(16, at)));
                double afterDb = Db(after), beforeDb = Db(before);
                bool quiet = afterDb < Db(loud) - QuietDb;
                bool attack = afterDb - beforeDb >= AttackRiseDb;
                if (quiet || !attack) { gated++; continue; }
                kept.Add(t);
            }
            return kept;
        }

        static double Db(double rms) => 20 * Math.Log10(Math.Max(rms, 1e-9));

        static double Rms(AudioSource source, long at, int count)
        {
            double sum = 0;
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                long k = at + i;
                if (k < 0 || k >= source.Length) continue;
                double v = source.Mono(k);
                sum += v * v;
                n++;
            }
            return n == 0 ? 0 : Math.Sqrt(sum / n);
        }
    }

    internal static partial class WarpMath
    {
        /// <summary>A segment may not be stretched or squeezed by "to grid" beyond this factor (either way) of what it was.</summary>
        public const double GridMaxStretch = 2;

        /// <summary>
        /// "To grid": a marker on each of <paramref name="transients"/> (audio samples, from <paramref name="fromSample"/> on), on the beat the audio is at now, moved toward the
        /// nearest line of <paramref name="grid"/> (beats) by <paramref name="strength"/> (0 to 1). A transient more than <paramref name="reach"/> (a share of a grid step,
        /// 0 to 0.5) from every line is left alone. Of two transients that come to one line the closer stays; a beat marker on the line of a transient gives way to it. Nothing
        /// is made that would stretch a segment more than twice (or squeeze it to under half). The audio's ends are the markers' ends, extended to the audio's first and last sample
        /// on the line they have.
        /// </summary>
        public static WarpResult ToGrid(List<(double Sample, double Beat)> m, IReadOnlyList<double> transients, double grid, double strength, double reach, double fromSample,
            double length, GridWarpReport report = null)
        {
            report ??= new GridWarpReport();
            if (!double.IsFinite(grid) || grid <= 0) return WarpResult.Fail("The grid is a positive number of beats.");
            strength = Math.Clamp(double.IsFinite(strength) ? strength : 1, 0, 1);
            reach = Math.Clamp(double.IsFinite(reach) ? reach : 0.4, 0, 0.5);
            var old = m.OrderBy(x => x.Beat).ToList();
            // The markers cover the whole audio, on the line they have (the sound does not change).
            var span = old.ToList();
            if (span[0].Sample > MinSamples) span.Insert(0, (0, BeatAt(old, 0)));
            if (length - span[^1].Sample > MinSamples) span.Add((length, BeatAt(old, length)));
            double lo = Math.Max(fromSample, span[0].Sample) + MinSamples, hi = span[^1].Sample - MinSamples;

            var items = new List<(double Sample, double Beat, double Target, double Dist, double Line)>();
            foreach (double t in transients.OrderBy(t => t))
            {
                if (t < lo || t > hi) continue;
                double beat = BeatAt(old, t);
                double line = Math.Round(beat / grid) * grid, d = Math.Abs(beat - line);
                if (d > reach * grid + 1e-9) { report.OutOfReach++; continue; }
                items.Add((t, beat, beat + strength * (line - beat), d, line));
            }
            // Two transients on one line: the closer stays.
            for (int i = items.Count - 1; i > 0; i--)
                if (Math.Abs(items[i].Line - items[i - 1].Line) < 1e-9)
                {
                    report.Skipped++;
                    items.RemoveAt(items[i].Dist < items[i - 1].Dist ? i - 1 : i);
                }
            // The markers that stay: the beat markers, except those that sit on a transient's line (the transient is the line's anchor now) or on the transient itself.
            var keep = new List<(double Sample, double Beat)>();
            for (int i = 0; i < span.Count; i++)
            {
                bool end = i == 0 || i == span.Count - 1;
                bool pinned = end || span[i].Sample <= fromSample + MinSamples;
                bool given = !pinned && items.Any(x => Math.Abs(x.Line - span[i].Beat) < MinGap || Math.Abs(x.Sample - span[i].Sample) < MinSamples || Math.Abs(x.Target - span[i].Beat) < MinGap);
                if (given) report.Replaced++; else keep.Add(span[i]);
            }
            // Stretch and order: drop the transient whose marker would stretch most, until every segment is within the limit and the markers rise.
            while (true)
            {
                var all = keep.Concat(items.Select(x => (Sample: x.Sample, Beat: x.Target))).OrderBy(x => x.Sample).ToList();
                int bad = -1;
                for (int i = 1; i < all.Count && bad < 0; i++)
                {
                    double nb = all[i].Beat - all[i - 1].Beat, ob = BeatAt(old, all[i].Sample) - BeatAt(old, all[i - 1].Sample);
                    if (nb < MinGap || (ob > 1e-9 && (nb / ob > GridMaxStretch || nb / ob < 1 / GridMaxStretch))) bad = i;
                }
                if (bad < 0) { report.Made = items.Count; return Finish(all, old, items.Count); }
                // The transient at either end of the bad segment that moved further (a beat marker can't be dropped).
                int pick = -1;
                double worst = -1;
                for (int j = 0; j < items.Count; j++)
                    if ((items[j].Sample == all[bad].Sample || items[j].Sample == all[bad - 1].Sample) && Math.Abs(items[j].Target - items[j].Beat) >= worst) { worst = Math.Abs(items[j].Target - items[j].Beat); pick = j; }
                if (pick < 0) return Finish(all, old, items.Count);            // beat markers alone: as auto-warp had them
                items.RemoveAt(pick);
                report.Skipped++;
            }
        }

        static WarpResult Finish(List<(double Sample, double Beat)> all, List<(double Sample, double Beat)> old, int made)
        {
            var r = Done(all, made);
            if (r.Ok) r.Touched = new();
            return r;
        }
    }
}
