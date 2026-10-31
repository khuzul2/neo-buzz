#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 1.0): audio clips, pure. The audio a
// clip plays, how its beats map onto that audio (warp markers), and how it follows the tempo
// (warp modes). No dependency on the rest of ReBuzz, so ReBuzz.SessionTests links this file.

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Clips
{
    /// <summary>Audio, as a clip plays it: two channels of floats (-1 to 1) at a sample rate. Never modified.</summary>
    internal sealed class AudioSource
    {
        public AudioSource(float[] left, float[] right, int sampleRate)
        {
            Left = left ?? Array.Empty<float>();
            Right = right ?? Left;
            SampleRate = Math.Max(1, sampleRate);
        }

        public float[] Left { get; }
        public float[] Right { get; }
        public int SampleRate { get; }
        public int Length => Left.Length;
        public double Seconds => Length / (double)SampleRate;

        /// <summary>A sample, 0 outside the audio.</summary>
        public float L(long i) => i >= 0 && i < Left.Length ? Left[i] : 0f;
        public float R(long i) => i >= 0 && i < Right.Length ? Right[i] : 0f;

        /// <summary>Mono, for analysis.</summary>
        public float Mono(long i) => i >= 0 && i < Left.Length ? 0.5f * (Left[i] + Right[i]) : 0f;

        /// <summary>
        /// A sample between samples (cubic Hermite through the four around it), both channels.
        /// </summary>
        public void Read(double position, out float left, out float right)
        {
            long i = (long)Math.Floor(position);
            float t = (float)(position - i);
            left = Hermite(L(i - 1), L(i), L(i + 1), L(i + 2), t);
            right = ReferenceEquals(Right, Left) ? left : Hermite(R(i - 1), R(i), R(i + 1), R(i + 2), t);
        }

        static float Hermite(float y0, float y1, float y2, float y3, float t)
        {
            float c1 = 0.5f * (y2 - y0);
            float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
            float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
            return ((c3 * t + c2) * t + c1) * t + y1;
        }
    }

    /// <summary>A warp marker: this sample of the audio is heard on this beat of the clip.</summary>
    internal readonly record struct WarpMarker(double Sample, double Beat);

    /// <summary>
    /// Where each beat of a clip is in its audio: straight lines between warp markers (at least
    /// two), and on past the first and last with their slopes. Never modified.
    /// </summary>
    internal sealed class WarpMap
    {
        readonly WarpMarker[] markers;

        public WarpMap(IEnumerable<WarpMarker> markers)
        {
            var list = (markers ?? Enumerable.Empty<WarpMarker>())
                .Where(m => double.IsFinite(m.Sample) && double.IsFinite(m.Beat))
                .OrderBy(m => m.Beat).ToList();
            // Strictly rising both ways: a marker that doesn't is dropped.
            var kept = new List<WarpMarker>();
            foreach (var m in list)
                if (kept.Count == 0 || (m.Beat > kept[^1].Beat + 1e-9 && m.Sample > kept[^1].Sample + 1e-6)) kept.Add(m);
            if (kept.Count == 0) kept.Add(new WarpMarker(0, 0));
            if (kept.Count == 1) kept.Add(new WarpMarker(kept[0].Sample + 44100, kept[0].Beat + 2));      // 120 BPM at 44.1 kHz
            this.markers = kept.ToArray();
        }

        /// <summary>The two markers of a straight map: the audio's <paramref name="samples"/> are <paramref name="beats"/> beats.</summary>
        public static WarpMap Straight(double samples, double beats) =>
            new WarpMap(new[] { new WarpMarker(0, 0), new WarpMarker(Math.Max(1, samples), Math.Max(1e-6, beats)) });

        public IReadOnlyList<WarpMarker> Markers => markers;

        /// <summary>The beats from the first marker to the last.</summary>
        public double Beats => markers[^1].Beat - markers[0].Beat;

        int SegmentForBeat(double beat)
        {
            int lo = 0, hi = markers.Length - 2;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (markers[mid].Beat <= beat) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        int SegmentForSample(double sample)
        {
            int lo = 0, hi = markers.Length - 2;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (markers[mid].Sample <= sample) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        public double SampleAt(double beat)
        {
            int i = SegmentForBeat(beat);
            var a = markers[i];
            var b = markers[i + 1];
            return a.Sample + (beat - a.Beat) * (b.Sample - a.Sample) / (b.Beat - a.Beat);
        }

        public double BeatAt(double sample)
        {
            int i = SegmentForSample(sample);
            var a = markers[i];
            var b = markers[i + 1];
            return a.Beat + (sample - a.Sample) * (b.Beat - a.Beat) / (b.Sample - a.Sample);
        }

        /// <summary>Samples a beat lasts around a beat (the slope there).</summary>
        public double SamplesPerBeat(double beat)
        {
            int i = SegmentForBeat(beat);
            return (markers[i + 1].Sample - markers[i].Sample) / (markers[i + 1].Beat - markers[i].Beat);
        }
    }

    /// <summary>How a clip follows the tempo (Live's warp modes).</summary>
    internal enum WarpMode
    {
        /// <summary>Not warped: the audio plays at its own speed and pitch, from the clip's start.</summary>
        Off,
        /// <summary>Faster or slower with the tempo, its pitch with it (a turntable).</summary>
        Repitch,
        /// <summary>Slices at transients (or a grid), each played at its own speed from where it falls: drums and loops.</summary>
        Beats,
        /// <summary>A phase vocoder: any material, pitch kept.</summary>
        Complex
    }

    /// <summary>
    /// How an audio clip plays, as the audio thread uses it: its audio, warp map and mode, the
    /// slices Beats mode plays (sample positions), gain and pitch. Never modified: a change is
    /// a new object.
    /// </summary>
    internal sealed class AudioClipParams
    {
        public AudioClipParams(AudioSource source, WarpMap map, WarpMode mode, double[] slices, float gain, double semitones)
        {
            Source = source;
            Map = map;
            Mode = mode;
            Slices = (slices ?? Array.Empty<double>()).Where(s => s >= 0 && s < source.Length).Distinct().OrderBy(s => s).ToArray();
            if (Slices.Length == 0 || Slices[0] > 0) Slices = new[] { 0.0 }.Concat(Slices).ToArray();
            SliceBeats = Slices.Select(map.BeatAt).ToArray();
            EndBeat = map.BeatAt(source.Length);
            Gain = gain;
            Semitones = Math.Clamp(semitones, -48, 48);
            PitchRatio = Math.Pow(2, Semitones / 12.0);
        }

        public AudioSource Source { get; }
        public WarpMap Map { get; }
        public WarpMode Mode { get; }
        /// <summary>Beats mode's slices: sample positions where each starts, rising, from 0.</summary>
        public double[] Slices { get; }
        /// <summary>The beat each slice starts on.</summary>
        public double[] SliceBeats { get; }
        /// <summary>The beat the audio ends on.</summary>
        public double EndBeat { get; }
        public float Gain { get; }
        public double Semitones { get; }
        public double PitchRatio { get; }
    }
}
