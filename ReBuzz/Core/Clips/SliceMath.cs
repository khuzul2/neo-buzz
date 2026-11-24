#nullable disable
// Fork (docs/specs/audio-to-midi.md, "Slice to new MIDI track (SL1 as built)"): where an audio clip is cut. Pure arithmetic on the audio as
// the clip plays it (reversed when the clip is, the warp map mirrored with it), so ReBuzz.SessionTests links this file. The plan is a list
// of slices (a start and a length in samples, and where each is in the clip's beats as the clip is warped), at most <see cref="SliceMath.MaxSlices"/>:
// Neo Sampler has 16 zones.

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Clips
{
    /// <summary>What decides where a clip is cut.</summary>
    internal enum SliceBy { Transients, WarpMarkers, Bar, Quarter, Eighth, Sixteenth }

    /// <summary>One slice: samples of the audio as the clip plays it, and its place in the clip.</summary>
    internal sealed class SliceCut
    {
        public long Start;
        public long Length;
        /// <summary>The clip beat the slice starts on, as the clip is warped.</summary>
        public double Beat;
        /// <summary>How many clip beats it lasts, as warped.</summary>
        public double Beats;
        /// <summary>How hard the attack is (transients only; the amplitude it rises by).</summary>
        public double Strength;
    }

    /// <summary>The result of planning a cut: the slices, or why there are none.</summary>
    internal sealed class SlicePlan
    {
        public SliceBy By;
        public readonly List<SliceCut> Slices = new();
        /// <summary>How many slices the choice would make before the limit (transients are capped, the rest refused).</summary>
        public int Found;
        /// <summary>Transients: the weakest were merged into the slice before them to stay at the limit.</summary>
        public bool Capped;
        /// <summary>Why nothing can be made (too many slices, nothing to cut), or null.</summary>
        public string Why;
        public double StartBeat, EndBeat;
        public long StartSample, EndSample;
        public bool Ok => Why == null && Slices.Count > 0;
    }

    internal static class SliceMath
    {
        /// <summary>Neo Sampler has 16 zones, so a clip is cut into at most this many slices.</summary>
        public const int MaxSlices = 16;

        public static readonly SliceBy[] All = { SliceBy.Transients, SliceBy.WarpMarkers, SliceBy.Bar, SliceBy.Quarter, SliceBy.Eighth, SliceBy.Sixteenth };

        public static string Label(SliceBy by) => by switch
        {
            SliceBy.Transients => "Transients", SliceBy.WarpMarkers => "Warp markers", SliceBy.Bar => "1 bar",
            SliceBy.Quarter => "1/4", SliceBy.Eighth => "1/8", _ => "1/16",
        };

        /// <summary>The name the agent uses: transients, warp_markers, bar, 1/4, 1/8, 1/16.</summary>
        public static string Key(SliceBy by) => by switch
        {
            SliceBy.Transients => "transients", SliceBy.WarpMarkers => "warp_markers", SliceBy.Bar => "bar",
            SliceBy.Quarter => "1/4", SliceBy.Eighth => "1/8", _ => "1/16",
        };

        public static bool TryParse(string text, out SliceBy by)
        {
            by = SliceBy.Transients;
            switch ((text ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace("-", "_"))
            {
                case "transients": case "transient": by = SliceBy.Transients; return true;
                case "warp_markers": case "markers": case "warp": by = SliceBy.WarpMarkers; return true;
                case "bar": case "1_bar": case "1bar": by = SliceBy.Bar; return true;
                case "1/4": by = SliceBy.Quarter; return true;
                case "1/8": by = SliceBy.Eighth; return true;
                case "1/16": by = SliceBy.Sixteenth; return true;
            }
            return false;
        }

        /// <summary>The grid in beats of a grid choice (a bar is <paramref name="beatsPerBar"/> beats; 1/4 is a beat, 1/8 half, 1/16 a quarter); 0 for the others.</summary>
        public static double GridBeats(SliceBy by, double beatsPerBar) => by switch
        {
            SliceBy.Bar => Math.Max(1, beatsPerBar), SliceBy.Quarter => 1, SliceBy.Eighth => 0.5, SliceBy.Sixteenth => 0.25, _ => 0,
        };

        /// <summary>How many slices a grid makes of a region (the last may be shorter).</summary>
        public static int GridCount(double startBeat, double endBeat, double grid)
        {
            if (grid <= 0 || endBeat <= startBeat) return 0;
            return Math.Max(1, (int)Math.Ceiling((endBeat - startBeat) / grid - 1e-6));
        }

        // ------------------------------------------------------------------ the plan

        const double MinGapMs = 15;               // two attacks closer than this are one
        const double EdgeMs = 12;                 // an attack this close to an end of the region is part of the slice next to it
        const double SilenceLevel = 0.001;        // -60 dBFS: audio under this before the first attack is not a slice

        /// <summary>
        /// Plans the cut of a region. <paramref name="map"/> maps the clip's beats to the audio as it plays (mirrored for a reversed clip);
        /// <paramref name="onsets"/> are the audio's transients, in samples of that audio (any order); <paramref name="level"/> reads the
        /// audio's level (the larger of the channels' magnitudes) for <c>count</c> samples from a sample, silence outside the audio; <paramref name="mono"/>
        /// reads the audio's samples (the channels' mean), for the onset detector that looks for the hits the transients of the warp editor miss;
        /// <paramref name="unitsPerBeat"/> (the clip notes' time base times the ticks per beat; 0 for none) puts each cut on the note grid: the slice starts at the
        /// sample of the beat its note will have, so a note and its slice are never apart by the grid's rounding (about 0.5 ms at 120 BPM).
        /// </summary>
        public static SlicePlan Plan(SliceBy by, WarpMap map, long length, int rate, double startBeat, double endBeat, double beatsPerBar,
                                     IReadOnlyList<double> onsets, Func<long, int, float[]> level, Func<long, int, float[]> mono, double unitsPerBeat = 0)
        {
            var plan = new SlicePlan { By = by, StartBeat = startBeat, EndBeat = endBeat };
            long s0 = Math.Clamp((long)Math.Round(map.SampleAt(startBeat)), 0, Math.Max(0, length - 1));
            long s1 = Math.Clamp((long)Math.Round(map.SampleAt(endBeat)), 0, length);
            plan.StartSample = s0;
            plan.EndSample = s1;
            if (s1 - s0 < 2 || endBeat <= startBeat)
            {
                plan.Why = "The region is empty.";
                return plan;
            }
            var cuts = new List<(long At, double Strength)>();
            double grid = GridBeats(by, beatsPerBar);
            switch (by)
            {
                case SliceBy.Transients:
                    cuts = Transients(plan, s0, s1, rate, onsets, level, mono);
                    break;
                case SliceBy.WarpMarkers:
                    cuts.Add((s0, 0));
                    foreach (var m in map.Markers)
                        if (m.Sample > s0 + 1 && m.Sample < s1 - 1 && m.Beat > startBeat + 1e-9 && m.Beat < endBeat - 1e-9) cuts.Add(((long)Math.Round(m.Sample), 0));
                    plan.Found = cuts.Count;
                    break;
                default:
                    int n = GridCount(startBeat, endBeat, grid);
                    plan.Found = n;
                    for (int k = 0; k < n; k++)
                    {
                        long at = k == 0 ? s0 : (long)Math.Round(map.SampleAt(startBeat + k * grid));
                        if (k > 0 && at <= cuts[^1].At) continue;
                        if (at >= s1) break;
                        cuts.Add((at, 0));
                    }
                    break;
            }
            if (by != SliceBy.Transients && plan.Found > MaxSlices)
            {
                plan.Why = $"{Label(by)} would make {plan.Found} slices; Neo Sampler has {MaxSlices} zones.";
                return plan;
            }
            if (cuts.Count < 2)
            {
                plan.Why = by switch
                {
                    SliceBy.Transients => "No transients found in the region: there is nothing to slice.",
                    SliceBy.WarpMarkers => "The region has no warp markers inside it.",
                    _ => $"The region is shorter than {Label(by)}: there is nothing to slice.",
                };
                return plan;
            }
            if (unitsPerBeat > 0)
            {
                for (int i = 1; i < cuts.Count; i++)
                {
                    double q = Math.Round(map.BeatAt(cuts[i].At) * unitsPerBeat) / unitsPerBeat;
                    long on = (long)Math.Round(map.SampleAt(q));
                    if (on > cuts[i - 1].At && on < s1 && (i + 1 >= cuts.Count || on < cuts[i + 1].At)) cuts[i] = (on, cuts[i].Strength);
                }
            }
            for (int i = 0; i < cuts.Count; i++)
            {
                long start = cuts[i].At, end = i + 1 < cuts.Count ? cuts[i + 1].At : s1;
                double b0 = map.BeatAt(start), b1 = i + 1 < cuts.Count ? map.BeatAt(end) : endBeat;
                plan.Slices.Add(new SliceCut { Start = start, Length = end - start, Beat = b0, Beats = Math.Max(0, b1 - b0), Strength = cuts[i].Strength });
            }
            return plan;
        }

        /// <summary>
        /// The attacks of a region: each of the detector's onsets placed on the first rise of the level (its own estimate is a little early),
        /// the first as the region's start when the audio before it is silent or it is a hair from the start, the weakest merged into the slice
        /// before them to leave at most <see cref="MaxSlices"/> slices.
        /// </summary>
        static List<(long At, double Strength)> Transients(SlicePlan plan, long s0, long s1, int rate, IReadOnlyList<double> onsets, Func<long, int, float[]> level, Func<long, int, float[]> mono)
        {
            int ms(double v) => Math.Max(1, (int)Math.Round(v * rate / 1000.0));
            // The warp editor's transients and the drum detector's candidates (it hears a kick under a ringing cymbal that the first misses); each is placed on its attack.
            var placed = new List<(long At, double Strength)>();
            foreach (double o in onsets.Where(o => o >= s0 - ms(3) && o < s1))
            {
                var (at, strength) = Attack(Math.Max(0, (long)o), rate, level);
                if (strength > 0 && at >= s0 && at < s1) placed.Add((at, strength));
            }
            double top = placed.Count == 0 ? 0 : placed.Max(p => p.Strength);
            if (mono != null)
            {
                // what the detector hears and the transients missed counts only when it is a clear attack, far from the others: a kick under a ringing cymbal, not a tail's wobble
                var extra = new List<(long At, double Strength)>();
                foreach (double o in Detect(s0, s1, rate, mono))
                {
                    var (at, strength) = Attack(Math.Max(0, (long)o), rate, level);
                    if (strength <= 0 || at < s0 || at >= s1) continue;
                    if (placed.Any(p => Math.Abs(p.At - at) < ms(40))) continue;
                    extra.Add((at, strength));
                }
                double bar = 0.35 * Math.Max(top, extra.Count == 0 ? 0 : extra.Max(p => p.Strength));
                placed.AddRange(extra.Where(p => p.Strength >= bar));
            }
            placed.Sort((p, q) => p.At.CompareTo(q.At));
            double floor = placed.Count == 0 ? 0 : 0.05 * placed.Max(p => p.Strength);
            var found = new List<(long At, double Strength)>();
            foreach (var (at, strength) in placed)
            {
                if (strength < floor) continue;
                if (found.Count > 0 && at - found[^1].At < ms(MinGapMs))
                {
                    if (strength > found[^1].Strength) found[^1] = (at, strength);
                    continue;
                }
                found.Add((at, strength));
            }
            // The end: an attack a hair from it is no slice.
            while (found.Count > 0 && s1 - found[^1].At < ms(EdgeMs)) found.RemoveAt(found.Count - 1);
            long start = s0;
            if (found.Count > 0)
            {
                long first = found[0].At;
                bool silent = Peak(level, s0, (int)Math.Min(int.MaxValue, first - s0)) < SilenceLevel;
                if (first - s0 < ms(EdgeMs) && !silent) found.RemoveAt(0);          // a hit on the first beat: the slice starts where the region does
                else if (silent) { start = first; found.RemoveAt(0); }               // nothing sounds before it: the first slice starts on it
            }
            else if (Peak(level, s0, (int)Math.Min(int.MaxValue, s1 - s0)) < SilenceLevel) return new List<(long, double)>();
            plan.Found = found.Count + 1;
            if (found.Count > MaxSlices - 1)
            {
                plan.Capped = true;
                var keep = found.Select((c, i) => (c, i)).OrderByDescending(x => x.c.Strength).ThenBy(x => x.i).Take(MaxSlices - 1).Select(x => x.i).ToHashSet();
                found = found.Where((_, i) => keep.Contains(i)).ToList();
            }
            var cuts = new List<(long At, double Strength)> { (start, 0) };
            cuts.AddRange(found);
            return cuts;
        }

        /// <summary>The onsets (samples) the shared onset detector (PT1) hears in a region, at a threshold between its default and the drum converter's sensitive one.</summary>
        static List<double> Detect(long from, long to, int rate, Func<long, int, float[]> mono)
        {
            var det = new NeoBuzz.Dsp.OnsetDetector(rate) { Threshold = 3f, MinIntervalSeconds = 0.02 };
            var list = new List<double>();
            const int Block = 1 << 16;
            var buf = new NeoBuzz.Dsp.OnsetEvent[det.MaxEvents(Block) + 4];
            for (long pos = from; pos < to; pos += Block)
            {
                int n = (int)Math.Min(Block, to - pos);
                int got = det.Process(mono(pos, n), 0, n, buf, 0);
                for (int i = 0; i < got; i++) list.Add(from + buf[i].Sample);
            }
            return list;
        }

        static float Peak(Func<long, int, float[]> level, long from, int count)
        {
            if (count <= 0) return 0;
            float peak = 0;
            const int Chunk = 1 << 16;
            for (long at = from; count > 0; at += Chunk)
            {
                int n = Math.Min(Chunk, count);
                foreach (float v in level(at, n)) if (v > peak) peak = v;
                count -= n;
            }
            return peak;
        }

        /// <summary>
        /// Where an attack begins, from the detector's early estimate (it is where the energy rose fastest, less 2 ms, and can be some milliseconds off either way
        /// for a soft or noisy start): the sample within 10 ms of it where the energy of the next half millisecond most exceeds that of the half millisecond before
        /// (so an attack over a ringing tail is found where it starts, not where the tail was). The strength is how far the level rises from what it was then to the
        /// attack's peak in the next 15 ms.
        /// </summary>
        public static (long At, double Strength) Attack(long rough, int rate, Func<long, int, float[]> level)
        {
            int ms(double v) => Math.Max(1, (int)Math.Round(v * rate / 1000.0));
            int w = Math.Max(2, ms(0.5));
            long from = Math.Max(0, rough - ms(10));
            int a = (int)(rough - from);
            var x = level(from, a + ms(30));
            var sums = new double[x.Length + 1];
            for (int i = 0; i < x.Length; i++) sums[i + 1] = sums[i] + (double)x[i] * x[i];
            float peak = 0;
            for (int i = Math.Max(0, a - ms(2)); i < Math.Min(x.Length, a + ms(15)); i++) if (x[i] > peak) peak = x[i];
            if (peak <= 1e-6f) return (rough, 0);
            double eps = w * Math.Pow(0.002 * peak, 2);
            int best = -1;
            double bestScore = 0;
            for (int i = w; i <= Math.Min(x.Length - w, a + ms(10)); i++)
            {
                double after = sums[i + w] - sums[i], before = sums[i] - sums[i - w];
                double score = (after + eps) / (before + eps);
                if (score > bestScore * 1.0000001) { bestScore = score; best = i; }
            }
            if (best < 0 || bestScore < 3) return (rough, 0);                    // no jump of 5 dB in the energy: not an attack
            float before2 = 0;
            for (int i = Math.Max(0, best - ms(1.5)); i < best; i++) before2 = Math.Max(before2, x[i]);
            float peak2 = 0;
            for (int i = best; i < Math.Min(x.Length, best + ms(15)); i++) peak2 = Math.Max(peak2, x[i]);
            return (from + best, Math.Max(0, peak2 - before2));
        }
    }
}
