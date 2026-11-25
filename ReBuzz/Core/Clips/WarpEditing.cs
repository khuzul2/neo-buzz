#nullable disable
// Fork (docs/specs/neo-audio.md, WM1): the arithmetic of editing warp markers. Pure: lists of (sample, beat) pairs in, a new list or the reason it can't be, out.
// The Audio page's mouse and keys, its context menu and the agent's audio_warp_markers all call these (through SessionLayout.WarpEdit), so a gesture and
// the tool make the same markers. A marker pins a sample of the audio to a beat of the clip (the audio's own beats: a reversed clip is mirrored on screen, not here).
// The list is always sorted, strictly rising in both sample and beat, and its first and last markers are the pinned ends of the audio, which stay. ReBuzz.SessionTests
// links this file.

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Clips
{
    /// <summary>The outcome of a marker edit: the new markers (never fewer than two, or empty for "straight") and, where it applies, which of them the edit is about.</summary>
    internal sealed class WarpResult
    {
        public List<(double Sample, double Beat)> Markers;
        /// <summary>Why the edit can't be made, or null.</summary>
        public string Error;
        /// <summary>The markers the edit touched or made (indices into <see cref="Markers"/>), for the page to keep selected.</summary>
        public List<int> Touched = new();
        /// <summary>How many markers were made, moved or removed.</summary>
        public int Count;
        /// <summary>The audio's tempo to set with the markers, when the edit has one (warp straight).</summary>
        public double? Bpm;

        public bool Ok => Error == null;
        public static WarpResult Fail(string why) => new WarpResult { Error = why };
    }

    internal static partial class WarpMath
    {
        /// <summary>The least two markers are apart in beats: "one tick" of the editor, a 64th of a beat. A marker stops this far before its neighbour.</summary>
        public const double MinGap = 1.0 / 64;
        /// <summary>The least two markers are apart in audio samples.</summary>
        public const double MinSamples = 16;
        /// <summary>The loop lengths "warp as a loop" chooses from, in bars.</summary>
        public static readonly int[] LoopBars = { 1, 2, 4, 8, 16 };

        // ------------------------------------------------------------------ reading

        /// <summary>The markers to edit: the clip's own, sorted by beat, or its straight map (the audio's two ends) when it has none.</summary>
        public static List<(double Sample, double Beat)> Effective(IEnumerable<(double Sample, double Beat)> markers, double length, double rate, double bpm)
        {
            var list = (markers ?? Enumerable.Empty<(double, double)>()).Where(m => double.IsFinite(m.Item1) && double.IsFinite(m.Item2)).OrderBy(m => m.Item2).ToList();
            if (list.Count >= 2) return list;
            return new() { (0, 0), (Math.Max(1, length), Math.Max(1e-6, length / Math.Max(1, rate) * Math.Max(1, bpm) / 60)) };
        }

        static (double Sample, double Beat) Seg(List<(double Sample, double Beat)> m, int i) => m[Math.Clamp(i, 0, m.Count - 2)];

        /// <summary>The beat of a sample: straight lines between the markers, and on past the ends with their slopes.</summary>
        public static double BeatAt(List<(double Sample, double Beat)> m, double sample)
        {
            int i = 0;
            while (i < m.Count - 2 && m[i + 1].Sample <= sample) i++;
            var a = m[i]; var b = m[i + 1];
            return a.Beat + (sample - a.Sample) * (b.Beat - a.Beat) / (b.Sample - a.Sample);
        }

        /// <summary>The sample heard at a beat.</summary>
        public static double SampleAt(List<(double Sample, double Beat)> m, double beat)
        {
            int i = 0;
            while (i < m.Count - 2 && m[i + 1].Beat <= beat) i++;
            var a = m[i]; var b = m[i + 1];
            return a.Sample + (beat - a.Beat) * (b.Sample - a.Sample) / (b.Beat - a.Beat);
        }

        /// <summary>The tempo of the segment from marker <paramref name="index"/> to the next: the beats of the clip per second of the audio there (a straight clip has its own tempo). NaN when there is no such segment.</summary>
        public static double SegmentTempo(List<(double Sample, double Beat)> m, int index, double rate)
        {
            if (index < 0 || index + 1 >= m.Count) return double.NaN;
            double seconds = (m[index + 1].Sample - m[index].Sample) / Math.Max(1, rate);
            return seconds <= 0 ? double.NaN : (m[index + 1].Beat - m[index].Beat) / seconds * 60;
        }

        public static bool IsEnd(List<(double Sample, double Beat)> m, int index) => index == 0 || index == m.Count - 1;

        public static bool Same(List<(double Sample, double Beat)> a, List<(double Sample, double Beat)> b) =>
            a.Count == b.Count && a.Zip(b, (x, y) => Math.Abs(x.Sample - y.Sample) < 1e-9 && Math.Abs(x.Beat - y.Beat) < 1e-9).All(t => t);

        /// <summary>Why a list of markers can't be played, or null: they must rise in beats and in samples, by a margin, at a speed the engine can play.</summary>
        public static string Validate(List<(double Sample, double Beat)> m)
        {
            if (m == null || m.Count < 2) return "A warp needs at least two markers.";
            for (int i = 0; i < m.Count; i++)
                if (!double.IsFinite(m[i].Sample) || !double.IsFinite(m[i].Beat)) return "A marker has no position.";
            for (int i = 1; i < m.Count; i++)
            {
                double beats = m[i].Beat - m[i - 1].Beat, samples = m[i].Sample - m[i - 1].Sample;
                if (beats < MinGap * 0.999) return "A marker can't pass its neighbour (it stops one tick before it).";
                if (samples < MinSamples * 0.999) return "Two markers are too close in the audio.";
                double slope = samples / beats;
                if (slope < 1e-3 || slope > 1e9) return "That would stretch the audio further than it can play.";
            }
            return null;
        }

        static WarpResult Done(List<(double Sample, double Beat)> m, int count, params int[] touched)
        {
            m = m.OrderBy(x => x.Beat).ToList();
            string why = Validate(m);
            return why != null ? WarpResult.Fail(why) : new WarpResult { Markers = m, Count = count, Touched = touched.ToList() };
        }

        // ------------------------------------------------------------------ single markers

        /// <summary>A marker at a sample, on the beat the audio is there now (nothing moves).</summary>
        public static WarpResult Add(List<(double Sample, double Beat)> m, double sample)
        {
            double first = m[0].Sample, last = m[^1].Sample;
            sample = Math.Clamp(sample, first, last);
            double beat = BeatAt(m, sample);
            if (m.Any(x => Math.Abs(x.Sample - sample) < MinSamples || Math.Abs(x.Beat - beat) < MinGap)) return WarpResult.Fail("There is a marker there already.");
            var n = m.ToList();
            n.Add((sample, beat));
            n = n.OrderBy(x => x.Beat).ToList();
            return Done(n, 1, n.FindIndex(x => x.Sample == sample));
        }

        /// <summary>
        /// Moves the markers by a number of beats, keeping the spacing between them (the audio before and after stretches to fit). The move stops where a
        /// marker would reach the neighbour that isn't moving: one tick before it.
        /// </summary>
        public static WarpResult Move(List<(double Sample, double Beat)> m, IReadOnlyCollection<int> indices, double delta)
        {
            var sel = indices.Where(i => i >= 0 && i < m.Count).Distinct().OrderBy(i => i).ToList();
            if (sel.Count == 0) return WarpResult.Fail("No marker to move.");
            if (!double.IsFinite(delta)) return WarpResult.Fail("No distance to move.");
            double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
            foreach (int i in sel)
            {
                if (i > 0 && !sel.Contains(i - 1)) lo = Math.Max(lo, m[i - 1].Beat + MinGap - m[i].Beat);
                if (i + 1 < m.Count && !sel.Contains(i + 1)) hi = Math.Min(hi, m[i + 1].Beat - MinGap - m[i].Beat);
            }
            if (lo > hi) return WarpResult.Fail("A marker can't pass its neighbour (it stops one tick before it).");
            delta = Math.Clamp(delta, lo, hi);
            var n = m.ToList();
            foreach (int i in sel) n[i] = (m[i].Sample, m[i].Beat + delta);
            return Done(n, sel.Count, sel.ToArray());
        }

        /// <summary>Moves one marker to a beat (the same stop at its neighbours).</summary>
        public static WarpResult MoveTo(List<(double Sample, double Beat)> m, int index, double beat)
        {
            if (index < 0 || index >= m.Count) return WarpResult.Fail($"No marker {index}.");
            return Move(m, new[] { index }, beat - m[index].Beat);
        }

        /// <summary>Moves a marker along the audio, keeping its beat: it takes another sample. The pinned ends stay where they are.</summary>
        public static WarpResult MoveAlong(List<(double Sample, double Beat)> m, int index, double sample)
        {
            if (index < 0 || index >= m.Count) return WarpResult.Fail($"No marker {index}.");
            if (IsEnd(m, index)) return WarpResult.Fail("The audio's two ends stay pinned: they can't move along the audio.");
            if (!double.IsFinite(sample)) return WarpResult.Fail("No sample to move to.");
            sample = Math.Clamp(sample, m[index - 1].Sample + MinSamples, m[index + 1].Sample - MinSamples);
            var n = m.ToList();
            n[index] = (sample, m[index].Beat);
            return Done(n, 1, index);
        }

        /// <summary>Removes markers; the audio's two ends can't be removed. What is left is warped as before (a clip left with only its ends is straight).</summary>
        public static WarpResult Delete(List<(double Sample, double Beat)> m, IReadOnlyCollection<int> indices)
        {
            var del = indices.Where(i => i >= 0 && i < m.Count && !IsEnd(m, i)).Distinct().ToList();
            if (del.Count == 0) return WarpResult.Fail("No marker to delete (the audio's two ends stay).");
            var n = m.Where((_, i) => !del.Contains(i)).ToList();
            return Done(n, del.Count);
        }

        // ------------------------------------------------------------------ segments

        /// <summary>
        /// Sets the tempo of the segment from marker <paramref name="index"/> to the next: the next marker moves, and so do all after it by the same
        /// amount, so that only this segment is stretched (the tempos of the others, and their lengths, are as they were).
        /// </summary>
        public static WarpResult SegmentTempoSet(List<(double Sample, double Beat)> m, int index, double bpm, double rate)
        {
            if (index < 0 || index + 1 >= m.Count) return WarpResult.Fail("That marker has no segment after it.");
            if (!double.IsFinite(bpm) || bpm < 1 || bpm > 999) return WarpResult.Fail("The tempo is 1 to 999 BPM.");
            double seconds = (m[index + 1].Sample - m[index].Sample) / Math.Max(1, rate);
            double beats = bpm / 60 * seconds;
            double delta = beats - (m[index + 1].Beat - m[index].Beat);
            var n = m.ToList();
            for (int j = index + 1; j < n.Count; j++) n[j] = (m[j].Sample, m[j].Beat + delta);
            var r = Done(n, 1, index + 1);
            return r;
        }

        /// <summary>The segment's tempo times a factor (a half or a double).</summary>
        public static WarpResult SegmentTempoScale(List<(double Sample, double Beat)> m, int index, double factor, double rate)
        {
            double was = SegmentTempo(m, index, rate);
            if (double.IsNaN(was)) return WarpResult.Fail("That marker has no segment after it.");
            return SegmentTempoSet(m, index, was * factor, rate);
        }

        // ------------------------------------------------------------------ the whole clip

        /// <summary>This moment becomes the first downbeat: the beats shift so that the sample is beat 0, the clip's start (the audio before it is not played).</summary>
        public static WarpResult SetBarOne(List<(double Sample, double Beat)> m, double sample)
        {
            sample = Math.Clamp(sample, 0, m[^1].Sample);
            double shift = BeatAt(m, sample);
            if (Math.Abs(shift) < 1e-9) return WarpResult.Fail("That moment is bar 1 already.");
            var n = m.Select(x => (x.Sample, x.Beat - shift)).ToList();
            return Done(n, n.Count);
        }

        /// <summary>
        /// One tempo from a sample on: the markers before it stay, a marker is made there (on the beat it has now), the markers after it are removed and the audio's end is
        /// pinned at the tempo. <paramref name="bpm"/> is the tempo of the audio from there.
        /// </summary>
        public static WarpResult StraightFrom(List<(double Sample, double Beat)> m, double sample, double bpm, double length, double rate)
        {
            if (!double.IsFinite(bpm) || bpm < 1 || bpm > 999) return WarpResult.Fail("The tempo is 1 to 999 BPM.");
            sample = Math.Clamp(sample, m[0].Sample, length);
            if (length - sample < 0.05 * rate) return WarpResult.Fail("Too little audio after that moment to warp.");
            double beat = BeatAt(m, sample);
            var n = m.Where(x => x.Sample < sample - MinSamples && x.Beat < beat - MinGap).ToList();
            n.Add((sample, beat));
            n.Add((length, beat + (length - sample) / Math.Max(1, rate) * bpm / 60));
            var r = Done(n, 1, n.Count - 2);
            if (r.Ok) r.Bpm = bpm;
            return r;
        }

        /// <summary>The nearest of 1, 2, 4, 8 and 16 bars to a length in bars.</summary>
        public static int NearestLoopBars(double bars) => LoopBars.OrderBy(b => Math.Abs(b - bars)).ThenBy(b => b).First();

        /// <summary>
        /// The audio between two samples becomes a whole number of bars (the given one, or the nearest of 1, 2, 4, 8 or 16): it starts where it starts and
        /// its end moves; the markers inside are spread in proportion, and those after it move by as much as its end did.
        /// </summary>
        public static WarpResult AsLoop(List<(double Sample, double Beat)> m, double from, double to, int? bars, int beatsPerBar)
        {
            from = Math.Clamp(from, m[0].Sample, m[^1].Sample);
            to = Math.Clamp(to, m[0].Sample, m[^1].Sample);
            if (to - from < MinSamples * 2) return WarpResult.Fail("Select a range of audio (or use the clip's region).");
            beatsPerBar = Math.Max(1, beatsPerBar);
            double b0 = BeatAt(m, from), b1 = BeatAt(m, to);
            int n = bars is int given && given > 0 ? given : NearestLoopBars((b1 - b0) / beatsPerBar);
            double target = b0 + n * (double)beatsPerBar;
            // Markers at both ends of the range, then each beat is carried over.
            var withEnds = m.ToList();
            foreach (double s in new[] { from, to })
                if (!withEnds.Any(x => Math.Abs(x.Sample - s) < MinSamples)) withEnds.Add((s, BeatAt(m, s)));
            withEnds = withEnds.OrderBy(x => x.Beat).ToList();
            double fromS = withEnds.OrderBy(x => Math.Abs(x.Sample - from)).First().Sample, toS = withEnds.OrderBy(x => Math.Abs(x.Sample - to)).First().Sample;
            double fromB = withEnds.First(x => x.Sample == fromS).Beat, toB = withEnds.First(x => x.Sample == toS).Beat;
            double scale = (target - fromB) / (toB - fromB), shift = target - toB;
            var result = withEnds.Select(x => x.Sample < fromS - 0.5 ? x : x.Sample <= toS + 0.5 ? (x.Sample, fromB + (x.Beat - fromB) * scale) : (x.Sample, x.Beat + shift)).ToList();
            var r = Done(result, 1);
            if (r.Ok) { r.Bpm = null; r.Touched = new() { r.Markers.FindIndex(x => Math.Abs(x.Sample - toS) < 1) }; r.Count = n; }
            return r;
        }

        // ------------------------------------------------------------------ many markers

        /// <summary>Markers at the transients between two samples (the whole audio by default), each on the beat the audio is there now. Those that would sit on a marker are left out.</summary>
        public static WarpResult InsertAtTransients(List<(double Sample, double Beat)> m, IReadOnlyList<double> transients, double? from, double? to)
        {
            double lo = Math.Max(from ?? m[0].Sample, m[0].Sample), hi = Math.Min(to ?? m[^1].Sample, m[^1].Sample);
            var n = m.ToList();
            var made = new List<double>();
            foreach (double t in transients.OrderBy(t => t))
            {
                if (t <= lo - 0.5 || t >= hi + 0.5) continue;
                if (t <= m[0].Sample + MinSamples || t >= m[^1].Sample - MinSamples) continue;
                double beat = BeatAt(m, t);
                if (n.Any(x => Math.Abs(x.Sample - t) < MinSamples || Math.Abs(x.Beat - beat) < MinGap)) continue;
                n.Add((t, beat));
                made.Add(t);
            }
            if (made.Count == 0) return WarpResult.Fail("No transient there to put a marker on (or every one has one).");
            var r = Done(n, made.Count);
            if (r.Ok) r.Touched = made.Select(t => r.Markers.FindIndex(x => x.Sample == t)).ToList();
            return r;
        }

        /// <summary>A marker on every bar line between two samples (the whole audio by default), on the sample the audio has there now.</summary>
        public static WarpResult InsertEveryBar(List<(double Sample, double Beat)> m, int beatsPerBar, double? from, double? to)
        {
            beatsPerBar = Math.Max(1, beatsPerBar);
            double lo = BeatAt(m, Math.Max(from ?? m[0].Sample, m[0].Sample)), hi = BeatAt(m, Math.Min(to ?? m[^1].Sample, m[^1].Sample));
            var n = m.ToList();
            var made = new List<double>();
            for (long bar = (long)Math.Ceiling(lo / beatsPerBar); bar * (double)beatsPerBar <= hi + 1e-9 && made.Count < 4096; bar++)
            {
                double beat = bar * (double)beatsPerBar;
                if (beat <= m[0].Beat + MinGap || beat >= m[^1].Beat - MinGap) continue;
                if (n.Any(x => Math.Abs(x.Beat - beat) < MinGap)) continue;
                double sample = SampleAt(m, beat);
                if (n.Any(x => Math.Abs(x.Sample - sample) < MinSamples)) continue;
                n.Add((sample, beat));
                made.Add(beat);
            }
            if (made.Count == 0) return WarpResult.Fail("No bar line there without a marker on it already.");
            var r = Done(n, made.Count);
            if (r.Ok) r.Touched = made.Select(b => r.Markers.FindIndex(x => Math.Abs(x.Beat - b) < 1e-9)).ToList();
            return r;
        }

        /// <summary>
        /// Quantize: each marker among <paramref name="indices"/> moves toward the nearest grid line by <paramref name="strength"/> (0 to 1); and, when a range is given,
        /// so do the markers inside it and markers made at the transients in it that have none (then quantized). The pinned ends are left where they are. A marker stops
        /// one tick before a neighbour that does not move.
        /// </summary>
        public static WarpResult Quantize(List<(double Sample, double Beat)> m, IReadOnlyCollection<int> indices, double? from, double? to, IReadOnlyList<double> transients,
            double grid, double strength)
        {
            if (!double.IsFinite(grid) || grid <= 0) return WarpResult.Fail("The grid is a positive number of beats.");
            strength = Math.Clamp(double.IsFinite(strength) ? strength : 1, 0, 1);
            var items = m.Select((x, i) => (x.Sample, x.Beat, Move: indices != null && indices.Contains(i) && !IsEnd(m, i))).ToList();
            int made = 0;
            if (from.HasValue && to.HasValue)
            {
                double lo = Math.Min(from.Value, to.Value), hi = Math.Max(from.Value, to.Value);
                for (int i = 1; i < items.Count - 1; i++)
                    if (items[i].Sample >= lo - 0.5 && items[i].Sample <= hi + 0.5) items[i] = (items[i].Sample, items[i].Beat, true);
                bool anyMarker = items.Any(x => x.Move);
                if (!anyMarker && transients != null)
                    foreach (double t in transients.OrderBy(t => t))
                    {
                        if (t < lo || t > hi || t <= m[0].Sample + MinSamples || t >= m[^1].Sample - MinSamples) continue;
                        double beat = BeatAt(m, t);
                        if (items.Any(x => Math.Abs(x.Sample - t) < MinSamples || Math.Abs(x.Beat - beat) < MinGap)) continue;
                        items.Add((t, beat, true));
                        made++;
                    }
            }
            items = items.OrderBy(x => x.Beat).ToList();
            if (!items.Any(x => x.Move)) return WarpResult.Fail("Select markers, or a range of audio, to quantize.");
            var result = items.Select(x => (x.Sample, x.Beat)).ToList();
            int moved = 0;
            var touched = new List<double>();
            for (int i = 0; i < items.Count; i++)
            {
                if (!items[i].Move) continue;
                double b = items[i].Beat;
                double want = b + strength * (Math.Round(b / grid) * grid - b);
                double lo = i > 0 ? result[i - 1].Beat + MinGap : double.NegativeInfinity;
                double hi = i + 1 < items.Count ? items[i + 1].Beat - MinGap : double.PositiveInfinity;
                if (lo > hi) continue;
                want = Math.Clamp(want, lo, hi);
                if (Math.Abs(want - b) > 1e-9) moved++;
                result[i] = (items[i].Sample, want);
                touched.Add(items[i].Sample);
            }
            if (moved == 0 && made == 0) return WarpResult.Fail("Already on the grid.");
            var r = Done(result, Math.Max(moved, made));
            if (r.Ok) r.Touched = touched.Select(s => r.Markers.FindIndex(x => x.Sample == s)).Where(i => i >= 0).ToList();
            return r;
        }
    }
}
