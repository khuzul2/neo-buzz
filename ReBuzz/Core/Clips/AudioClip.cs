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
            // Audio that came from a file or a recording can hold NaN or infinity (a damaged float file): one such
            // sample would poison the phase vocoder's state for good, so they are silenced here, once, off the audio thread.
            bool mono = right == null || ReferenceEquals(left, right);
            Left = Clean(left ?? Array.Empty<float>());
            Right = mono ? Left : Clean(right);
            SampleRate = Math.Max(1, sampleRate);
        }

        /// <summary>The array itself when every sample is finite, else a copy with the others silenced.</summary>
        static float[] Clean(float[] x)
        {
            int bad = -1;
            for (int i = 0; i < x.Length; i++)
                if (!float.IsFinite(x[i])) { bad = i; break; }
            if (bad < 0) return x;
            var y = (float[])x.Clone();
            for (int i = bad; i < y.Length; i++)
                if (!float.IsFinite(y[i])) y[i] = 0f;
            return y;
        }

        public float[] Left { get; }
        public float[] Right { get; }
        public int SampleRate { get; }
        public int Length => Left.Length;
        public double Seconds => Length / (double)SampleRate;

        /// <summary>
        /// The same audio played backwards (a copy; made off the audio thread). Sample <c>i</c> of the original is sample <c>Length - 1 - i</c> of
        /// the copy, so a clip's reverse plays this with a mirrored map (<see cref="WarpMap.Mirrored"/>).
        /// </summary>
        public AudioSource Reversed()
        {
            bool mono = ReferenceEquals(Left, Right);
            var l = (float[])Left.Clone();
            Array.Reverse(l);
            if (mono) return new AudioSource(l, l, SampleRate);
            var r = (float[])Right.Clone();
            Array.Reverse(r);
            return new AudioSource(l, r, SampleRate);
        }

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
            // A position that isn't a place in any audio (NaN, infinity, beyond 2^50) is silence: a NaN fraction would
            // make the whole interpolation NaN even from zeros.
            if (!(Math.Abs(position) < 1e15))
            {
                left = right = 0f;
                return;
            }
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
            // Strictly rising both ways, and a slope the engine can play (between 1e-3 and 1e9 samples a beat): a marker
            // that isn't is dropped (a pair 1e-9 beats apart and 1e3 samples apart would be a slope of 1e12 and more).
            var kept = new List<WarpMarker>();
            foreach (var m in list)
            {
                if (kept.Count == 0) { kept.Add(m); continue; }
                double beats = m.Beat - kept[^1].Beat, samples = m.Sample - kept[^1].Sample;
                if (beats > 1e-9 && samples > 1e-6 && double.IsFinite(samples / beats) && samples / beats >= 1e-3 && samples / beats <= 1e9) kept.Add(m);
            }
            if (kept.Count == 0) kept.Add(new WarpMarker(0, 0));
            if (kept.Count == 1) kept.Add(new WarpMarker(kept[0].Sample + 44100, kept[0].Beat + 2));      // 120 BPM at 44.1 kHz
            this.markers = kept.ToArray();
        }

        /// <summary>The two markers of a straight map: the audio's <paramref name="samples"/> are <paramref name="beats"/> beats.</summary>
        public static WarpMap Straight(double samples, double beats) =>
            new WarpMap(new[] { new WarpMarker(0, 0), new WarpMarker(Math.Max(1, samples), Math.Max(1e-6, beats)) });

        public IReadOnlyList<WarpMarker> Markers => markers;

        /// <summary>
        /// This map for the audio played backwards (<see cref="AudioSource.Reversed"/>, <paramref name="length"/> samples): the clip's beat
        /// <c>b</c> plays what beat <c>endBeat - b</c> played, so each marker (sample, beat) becomes (length - 1 - sample, endBeat - beat).
        /// </summary>
        public WarpMap Mirrored(double length, double endBeat) =>
            new WarpMap(markers.Select(m => new WarpMarker(length - 1 - m.Sample, endBeat - m.Beat)));

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
    /// An arrangement clip's fades (docs/specs/neo-audio.md, NA2), in beats: the gain rises over <see cref="FadeIn"/> from the clip's start (a
    /// sine curve when <see cref="CrossIn"/>, the equal-power half of a crossfade; else a line), falls over <see cref="FadeOut"/> to its end, and
    /// <see cref="Tail"/> is how long the audio goes on past the end under what starts there, falling as a cosine (the other half).
    /// </summary>
    internal readonly record struct ClipFades(double FadeIn, double FadeOut, double Tail, bool CrossIn)
    {
        public static readonly ClipFades None = default;

        public bool Active => FadeIn > 0 || FadeOut > 0;

        public ClipFades Sanitised() => new ClipFades(Clean(FadeIn), Clean(FadeOut), Clean(Tail), CrossIn);

        static double Clean(double x) => double.IsFinite(x) ? Math.Clamp(x, 0, 1e6) : 0;

        /// <summary>The gain at <paramref name="pos"/> beats from the clip's start with <paramref name="left"/> beats to go.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public float GainAt(double pos, double left)
        {
            float g = 1f;
            if (FadeIn > 0 && pos < FadeIn)
            {
                double x = pos <= 0 ? 0 : pos / FadeIn;
                g = CrossIn ? (float)Math.Sin(x * (Math.PI / 2)) : (float)x;
            }
            if (FadeOut > 0 && left < FadeOut)
                g *= left <= 0 ? 0f : (float)(left / FadeOut);
            return g;
        }

        /// <summary>The gain of the tail <paramref name="gone"/> beats after the clip's end.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public float TailGainAt(double gone) =>
            Tail <= 0 || gone >= Tail ? 0f : gone <= 0 ? 1f : (float)Math.Cos(gone / Tail * (Math.PI / 2));
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
            Gain = float.IsFinite(gain) ? Math.Clamp(gain, 0f, 64f) : 1f;
            Semitones = double.IsFinite(semitones) ? Math.Clamp(semitones, -48, 48) : 0;
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

        /// <summary>An arrangement clip's fades and crossfade tail (NA2); none for a Session clip. Set once, before the params are handed over.</summary>
        public ClipFades Fades { get => fades; init => fades = value.Sanitised(); }
        ClipFades fades;
    }
}
