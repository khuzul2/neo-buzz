#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 1.0): audio clips, pure. The audio a
// clip plays, how its beats map onto that audio (warp markers), and how it follows the tempo
// (warp modes). No dependency on the rest of ReBuzz, so ReBuzz.SessionTests links this file.

using System;
using System.Collections.Generic;
using System.Linq;
using ReBuzz.Core.AudioLibrary;

namespace ReBuzz.Core.Clips
{
    /// <summary>
    /// Audio, as a clip plays it: two channels of floats (-1 to 1) at a sample rate. Never modified. The samples are in memory, or (neo-audio.md,
    /// NA1b) read from the decoded cache file through the prefetch thread: the same object switches from one to the other while it plays, so
    /// nothing that holds it changes. <see cref="L"/>, <see cref="R"/>, <see cref="Read"/> and <see cref="CopyTo"/> are the audio thread's reads
    /// (no I/O, no lock; a sample not in memory is silence and is counted); <see cref="CopyExact"/> and <see cref="ForAnalysis"/> are for every
    /// other thread (they may wait for the disk).
    /// </summary>
    internal sealed class AudioSource
    {
        volatile float[] left, right;           // null once the audio is read from the stream only (or in a reversed view)
        volatile StreamedAudio stream;          // the cache file, once there is one
        readonly AudioSource parent;            // a reversed view of another source: sample i is the parent's Length - 1 - i
        readonly int length;
        readonly bool mono;
        readonly AudioSource exactOf;           // an exact view (analysis, peaks): reads blocks through exactOf.CopyExact
        float[] blockL, blockR;
        long blockAt = -1;
        int blockCount;

        public AudioSource(float[] left, float[] right, int sampleRate)
        {
            // Audio that came from a file or a recording can hold NaN or infinity (a damaged float file): one such
            // sample would poison the phase vocoder's state for good, so they are silenced here, once, off the audio thread.
            mono = right == null || ReferenceEquals(left, right);
            this.left = Clean(left ?? Array.Empty<float>());
            this.right = mono ? this.left : Clean(right);
            length = this.left.Length;
            SampleRate = Math.Max(1, sampleRate);
        }

        AudioSource(StreamedAudio s)
        {
            stream = s;
            length = (int)Math.Min(int.MaxValue, s.Frames);
            mono = s.Channels < 2;
            SampleRate = Math.Max(1, s.SampleRate);
        }

        AudioSource(AudioSource mirrorOf, bool mirror)
        {
            parent = mirrorOf;
            length = mirrorOf.length;
            mono = mirrorOf.mono;
            SampleRate = mirrorOf.SampleRate;
        }

        AudioSource(float[] l, float[] r, int rate, bool unchecked_)
        {
            left = l; right = r; mono = ReferenceEquals(l, r); length = l.Length; SampleRate = rate;
        }

        AudioSource(AudioSource exact)
        {
            exactOf = exact;
            length = exact.length;
            mono = exact.mono;
            SampleRate = exact.SampleRate;
        }

        /// <summary>Audio that is only in a cache file (a song opened with its cache): nothing in memory.</summary>
        internal static AudioSource FromStream(StreamedAudio s) => new AudioSource(s);

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

        /// <summary>The samples as arrays (for code that needs them all; a streamed source makes a copy, which is its whole size).</summary>
        public float[] Left => left ?? ToArrays().Left;
        public float[] Right => right ?? ToArrays().Right;

        public int SampleRate { get; }
        public int Length => length;
        public double Seconds => Length / (double)SampleRate;
        public bool IsMono => mono;

        /// <summary>The cache file this reads from (or will), or null while the audio is only in memory.</summary>
        public StreamedAudio Stream => parent != null ? parent.Stream : stream;

        /// <summary>The audio is in memory as arrays (reads do not go through pages).</summary>
        public bool InMemory => parent != null ? parent.InMemory : left != null;

        /// <summary>A cache file is attached: voices tell the prefetch thread where they read.</summary>
        public bool IsStreaming => Stream != null;

        /// <summary>This source plays the audio backwards (a view of another).</summary>
        public bool IsReversedView => parent != null;

        /// <summary>
        /// Attaches the cache file the audio is also in. The samples stay in memory until <see cref="DropMemory"/>, so nothing changes for what
        /// plays; the prefetch thread starts reading what the voices will need.
        /// </summary>
        internal void Attach(StreamedAudio s) => stream = s;

        /// <summary>The samples in memory are let go: reads are pages from here on. Only once the pages that are wanted are resident.</summary>
        internal void DropMemory()
        {
            if (stream == null) return;
            left = null;
            right = null;
        }

        /// <summary>Bytes of floats this holds in memory (0 once streamed; a reversed view holds none).</summary>
        public long MemoryBytes
        {
            get
            {
                if (parent != null || exactOf != null) return 0;
                var a = left;
                if (a == null) return 0;
                return (long)a.Length * 4 * (mono ? 1 : 2);
            }
        }

        /// <summary>
        /// The same audio played backwards. Sample <c>i</c> of the original is sample <c>Length - 1 - i</c> of the result, so a clip's reverse plays
        /// this with a mirrored map (<see cref="WarpMap.Mirrored"/>). A short source is copied; one that would stream is a view of it (a copy would
        /// be as big as the audio), so it follows the audio into the cache.
        /// </summary>
        public AudioSource Reversed()
        {
            if (parent != null) return parent;
            if (stream != null || StreamingPolicy.ShouldStream(length, mono ? 1 : 2))
                return new AudioSource(this, true);
            var a = Left;
            var l = (float[])a.Clone();
            Array.Reverse(l);
            if (mono) return new AudioSource(l, l, SampleRate);
            var r = (float[])Right.Clone();
            Array.Reverse(r);
            return new AudioSource(l, r, SampleRate);
        }

        // ------------------------------------------------------------------ the audio thread's reads

        /// <summary>A sample, 0 outside the audio (and for audio not in memory yet, which is counted).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public float L(long i)
        {
            var a = left;
            if (a != null) return (ulong)i < (ulong)a.Length ? a[i] : 0f;
            return Slow(0, i);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public float R(long i)
        {
            var a = right;
            if (a != null) return (ulong)i < (ulong)a.Length ? a[i] : 0f;
            return Slow(1, i);
        }

        float Slow(int channel, long i)
        {
            if ((ulong)i >= (ulong)length) return 0f;
            if (parent != null) return channel == 0 ? parent.L(length - 1 - i) : parent.R(length - 1 - i);
            if (exactOf != null) return Block(channel, i);
            var s = stream;
            return s == null ? 0f : s.Sample(channel, i);
        }

        /// <summary>Mono, for analysis.</summary>
        public float Mono(long i) => i >= 0 && i < length ? 0.5f * (L(i) + R(i)) : 0f;

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
            right = mono ? left : Hermite(R(i - 1), R(i), R(i + 1), R(i + 2), t);
        }

        static float Hermite(float y0, float y1, float y2, float y3, float t)
        {
            float c1 = 0.5f * (y2 - y0);
            float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
            float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
            return ((c3 * t + c2) * t + c1) * t + y1;
        }

        /// <summary>
        /// Copies samples of a channel (0 left, 1 right) from <paramref name="start"/>: silence outside the audio and, for a streamed source, for
        /// what is not in memory (counted). The audio thread's bulk read.
        /// </summary>
        public void CopyTo(int channel, long start, Span<float> destination)
        {
            var a = channel > 0 ? right : left;
            if (a != null)
            {
                CopyArray(a, start, destination);
                return;
            }
            if (parent != null || exactOf != null)
            {
                for (int k = 0; k < destination.Length; k++) destination[k] = channel > 0 ? R(start + k) : L(start + k);
                return;
            }
            var s = stream;
            if (s == null) destination.Clear();
            else s.CopyTo(channel, start, destination);
        }

        static void CopyArray(float[] from, long start, Span<float> to)
        {
            int n = to.Length;
            if (n <= 0) return;
            long end = start + n;
            if (start >= from.Length || end <= 0)
            {
                to.Clear();
                return;
            }
            int lead = (int)Math.Max(0, -start);
            int tail = (int)Math.Max(0, end - from.Length);
            if (lead > 0) to.Slice(0, lead).Clear();
            from.AsSpan((int)(start + lead), n - lead - tail).CopyTo(to.Slice(lead, n - lead - tail));
            if (tail > 0) to.Slice(n - tail).Clear();
        }

        // ------------------------------------------------------------------ every other thread's reads

        /// <summary>
        /// Copies samples exactly (from the cache file if they are not in memory; this may wait for the disk, so not the audio thread).
        /// Silence outside the audio.
        /// </summary>
        public void CopyExact(int channel, long start, Span<float> destination)
        {
            var a = channel > 0 ? right : left;
            if (a != null)
            {
                CopyArray(a, start, destination);
                return;
            }
            if (parent != null)
            {
                // The parent's range read backwards: [start, start + n) of this is [length - start - n, length - start) of the parent.
                int n = destination.Length;
                long from = length - start - n;
                var tmp = new float[n];
                parent.CopyExact(channel, from, tmp);
                for (int k = 0; k < n; k++) destination[k] = tmp[n - 1 - k];
                return;
            }
            if (exactOf != null)
            {
                exactOf.CopyExact(channel, start, destination);
                return;
            }
            var s = stream;
            if (s == null) destination.Clear();
            else s.ReadExact(channel, start, destination);
        }

        /// <summary>The whole audio as arrays (a copy of its size; a mono source gives one array twice).</summary>
        public (float[] Left, float[] Right) ToArrays()
        {
            var l = new float[length];
            CopyExact(0, 0, l);
            if (mono) return (l, l);
            var r = new float[length];
            CopyExact(1, 0, r);
            return (l, r);
        }

        /// <summary>
        /// A source for code that reads the whole audio off the audio thread (analysis, peaks, saving): the same samples, always exact. For a source in
        /// memory it shares the arrays (so letting the memory go meanwhile does not touch it); for a streamed one it reads blocks from the file.
        /// Not for several threads at once (it holds a block).
        /// </summary>
        public AudioSource ForAnalysis()
        {
            if (exactOf != null) return this;
            if (parent != null) return new AudioSource(parent.ForAnalysis(), true);
            var a = left;
            if (a != null) return new AudioSource(a, right, SampleRate, true);
            return new AudioSource(this);
        }

        const int BlockFrames = 16384;

        float Block(int channel, long i)
        {
            {
                if (blockAt < 0 || i < blockAt || i >= blockAt + blockCount)
                {
                    blockL ??= new float[BlockFrames];
                    if (!mono) blockR ??= new float[BlockFrames];
                    long at = i - i % BlockFrames;
                    blockCount = (int)Math.Min(BlockFrames, length - at);
                    exactOf.CopyExact(0, at, blockL.AsSpan(0, blockCount));
                    if (!mono) exactOf.CopyExact(1, at, blockR.AsSpan(0, blockCount));
                    blockAt = at;
                }
                int k = (int)(i - blockAt);
                return channel > 0 && !mono ? blockR[k] : blockL[k];
            }
        }

        // ------------------------------------------------------------------ for the prefetch thread

        /// <summary>
        /// A voice reading this source says where: it is at <paramref name="at"/> and wants [aFrom, aTo) and [bFrom, bTo) (frames of this source,
        /// either range may be empty). Mapped to the cache file (a reversed view's frames are mirrored). Audio thread, no lock.
        /// </summary>
        public void Hint(StreamHint hint, long at, long aFrom, long aTo, long bFrom, long bTo)
        {
            var s = Stream;
            if (s == null)
            {
                hint.Clear();
                return;
            }
            if (parent != null)
            {
                long m = length - 1;
                hint.Set(s, m - at, m + 1 - aTo, m + 1 - aFrom, m + 1 - bTo, m + 1 - bFrom);
                return;
            }
            hint.Set(s, at, aFrom, aTo, bFrom, bTo);
        }

        /// <summary>
        /// The frames of these ranges (of this source) stay in memory while <paramref name="owner"/> holds them: the starts of a clip's loop and
        /// markers, so a launch does not wait for the disk. Replaces the owner's earlier ranges.
        /// </summary>
        public void Pin(object owner, IReadOnlyList<(long From, long To)> ranges)
        {
            var s = Stream;
            if (s == null) return;
            if (parent != null && ranges != null)
            {
                long m = length;
                ranges = ranges.Select(r => (m - r.To, m - r.From)).ToList();
            }
            s.SetPins(owner, ranges);
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
        Complex,
        /// <summary>
        /// Signalsmith Stretch (neo-audio.md section 11, the native neostretch library): a higher quality stretch and pitch shift of any
        /// material. Plays as Complex when the library is missing. Last, so the values of the others stay what they were.
        /// </summary>
        Pro
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
