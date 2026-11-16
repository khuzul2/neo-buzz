#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md section 11): the Pro warp mode. A time-stretcher and pitch-shifter from
// Signalsmith Stretch (MIT), behind a small C ABI library (NeoStretch/, "neostretch"). Pure but for the library: ReBuzz.SessionTests
// links this file. The library is loaded once, on the UI thread (Prepare); nothing here allocates, locks or throws on the audio thread.
// When the library is missing the clip plays in Complex, and the log says so once.

using System;
using System.Runtime.InteropServices;
using ReBuzz.Platform;

namespace ReBuzz.Core.Clips
{
    /// <summary>One native stretcher (one or two channels). Made off the audio thread; its calls are for the audio thread alone.</summary>
    internal sealed unsafe class NeoStretch
    {
        const int AbiVersion = 1;

        static readonly object gate = new object();
        static bool tried;
        static string failure;
        static delegate* unmanaged[Cdecl]<int, int, int, int, IntPtr> create;
        static delegate* unmanaged[Cdecl]<IntPtr, void> destroy;
        static delegate* unmanaged[Cdecl]<IntPtr, int> inputLatency, outputLatency;
        static delegate* unmanaged[Cdecl]<IntPtr, double, double, int> setTranspose;
        static delegate* unmanaged[Cdecl]<IntPtr, float*, float*, int, int> outputSeek;
        static delegate* unmanaged[Cdecl]<IntPtr, float*, float*, int, float*, float*, int, int> process;

        /// <summary>Where a message for the user goes (the app sets it to its log). Called on the thread that loads, never on the audio thread.</summary>
        public static Action<string> Log = message => System.Diagnostics.Debug.WriteLine(message);

        /// <summary>The lines logged, newest last (for tests and for the agent's audio status): the missing-library line is there once.</summary>
        public static string LastMessage { get; private set; }

        /// <summary>Whether the native library is loaded (it is looked for the first time here: call it off the audio thread).</summary>
        public static bool Available
        {
            get
            {
                lock (gate)
                {
                    if (!tried)
                    {
                        tried = true;
                        failure = Load();
                        if (failure != null) Say("Pro warp: " + failure + " Clips set to Pro play in Complex.");
                    }
                    return failure == null;
                }
            }
        }

        /// <summary>Why the library is not there (null when it is).</summary>
        public static string Failure
        {
            get
            {
                _ = Available;
                return failure;
            }
        }

        /// <summary>Forgets what was tried (tests only): the next <see cref="Available"/> loads again.</summary>
        internal static void ResetForTests()
        {
            lock (gate) { tried = false; failure = null; }
        }

        static void Say(string message)
        {
            LastMessage = message;
            try { Log?.Invoke(message); } catch { }
        }

        static string Load()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("NEO_STRETCH_OFF") == "1") return "the library is turned off (NEO_STRETCH_OFF).";
                if (!NativeLibraries.TryLoad("neostretch", out var lib, out var detail)) return "the neostretch library is missing (" + detail + ").";
                var abi = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(lib, "ns_abi_version");
                if (abi() != AbiVersion) return $"the neostretch library at {detail} is another version (ABI {abi()}, wanted {AbiVersion}).";
                create = (delegate* unmanaged[Cdecl]<int, int, int, int, IntPtr>)NativeLibrary.GetExport(lib, "ns_create");
                destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(lib, "ns_destroy");
                inputLatency = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(lib, "ns_input_latency");
                outputLatency = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(lib, "ns_output_latency");
                setTranspose = (delegate* unmanaged[Cdecl]<IntPtr, double, double, int>)NativeLibrary.GetExport(lib, "ns_set_transpose");
                outputSeek = (delegate* unmanaged[Cdecl]<IntPtr, float*, float*, int, int>)NativeLibrary.GetExport(lib, "ns_output_seek");
                process = (delegate* unmanaged[Cdecl]<IntPtr, float*, float*, int, float*, float*, int, int>)NativeLibrary.GetExport(lib, "ns_process");
                return null;
            }
            catch (Exception e)
            {
                return "the neostretch library would not load (" + e.Message + ").";
            }
        }

        IntPtr handle;

        NeoStretch(IntPtr handle, int channels)
        {
            this.handle = handle;
            Channels = channels;
            InputLatency = inputLatency(handle);
            OutputLatency = outputLatency(handle);
            pressure = 256L * 1024 * channels + 8L * (InputLatency + OutputLatency) * channels;     // the library's buffers and spectra, roughly
            GC.AddMemoryPressure(pressure);
        }

        readonly long pressure;

        ~NeoStretch()
        {
            var h = handle;
            handle = IntPtr.Zero;
            if (h != IntPtr.Zero)
            {
                destroy(h);
                GC.RemoveMemoryPressure(pressure);
            }
        }

        public int Channels { get; }
        /// <summary>Samples of input the stretcher wants ahead of the output's position (input domain).</summary>
        public int InputLatency { get; }
        /// <summary>Samples the output trails its processing time by (output domain).</summary>
        public int OutputLatency { get; }

        /// <summary>
        /// A stretcher whose STFT block is 120 ms and hop 30 ms at <paramref name="sampleRate"/> (the library's default preset, with its
        /// computation spread evenly over the hop), or null when the library is missing. Allocates: not for the audio thread.
        /// </summary>
        public static NeoStretch Create(int channels, int sampleRate)
        {
            if (!Available) return null;
            int rate = Math.Clamp(sampleRate, 8000, 192000);
            int block = (int)(rate * 0.12), interval = (int)(rate * 0.03);
            var h = create(channels, block, interval, 1);
            if (h == IntPtr.Zero)
            {
                Say($"Pro warp: the neostretch library could not make a stretcher ({channels} channels, block {block}, hop {interval}). Clips set to Pro play in Complex.");
                return null;
            }
            return new NeoStretch(h, channels);
        }

        /// <summary>The frequency multiplier of the next samples, and the tonality limit (a multiple of the sample rate, 0 for none).</summary>
        public void SetTranspose(double factor, double tonalityLimit)
        {
            if (handle == IntPtr.Zero || (factor == lastFactor && tonalityLimit == lastLimit)) return;
            lastFactor = factor;
            lastLimit = tonalityLimit;
            setTranspose(handle, factor, tonalityLimit);
        }

        double lastFactor = double.NaN, lastLimit = double.NaN;

        /// <summary>A start at the first of <paramref name="length"/> samples (see ns_output_seek): the next <see cref="Process"/> goes on from there.</summary>
        public bool Seek(float[] left, float[] right, int length)
        {
            if (handle == IntPtr.Zero || length < InputLatency || length > left.Length || length > right.Length) return false;
            fixed (float* l = left)
            fixed (float* r = right)
                return outputSeek(handle, l, r, length) == 0;
        }

        /// <summary><paramref name="inputSamples"/> in, <paramref name="outputSamples"/> out (both arrays from index 0); their ratio is the stretch.</summary>
        public bool Process(float[] inLeft, float[] inRight, int inputSamples, float[] outLeft, float[] outRight, int outputSamples)
        {
            if (handle == IntPtr.Zero || inputSamples < 0 || inputSamples > inLeft.Length || inputSamples > inRight.Length ||
                outputSamples < 0 || outputSamples > outLeft.Length || outputSamples > outRight.Length) return false;
            fixed (float* il = inLeft)
            fixed (float* ir = inRight)
            fixed (float* ol = outLeft)
            fixed (float* orr = outRight)
                return process(handle, il, ir, inputSamples, ol, orr, outputSamples) == 0;
        }
    }

    /// <summary>
    /// A clip voice's Pro rendering (neo-audio.md section 11): two stretchers, the one playing and the one fading out, so that a jump
    /// (a launch over a playing clip, a loop going round, a stop) crossfades two stretched signals and not a stretched one with a raw
    /// copy of the audio. Pure, and allocation-free after construction.
    /// </summary>
    /// <remarks>
    /// <para>The stretcher's output trails its processing time (<see cref="NeoStretch.OutputLatency"/> samples) and wants input ahead of it
    /// (<see cref="NeoStretch.InputLatency"/> samples), so the input is read ahead of the clip's beat: for a chunk ending at output time
    /// <c>t</c> from the chunk's start beat <c>b</c>, up to source sample <c>map(b + (t + outputLatency) * beatsPerSample) + inputLatency</c>.
    /// A start (<see cref="Restart"/>) is the library's output seek over the input from the clip's beat up to that point, so the first output
    /// sample is the audio at the beat and the output lines up with Repitch's, whatever the latency.</para>
    /// <para>Every chunk is a whole number of input samples against a whole number of output samples; the input position is rounded from
    /// the map each time (not accumulated), so the ratio averages out to the map's and there is no drift.</para>
    /// </remarks>
    internal sealed class ProVoice
    {
        /// <summary>Output samples per call to the stretcher: always whole chunks, on a grid counted from the start, so the output does not depend on the engine's block size.</summary>
        public const int Chunk = 256;
        /// <summary>The most input samples one call takes (a chunk at 128 times speed, or the audio a start reads ahead).</summary>
        public const int MaxIn = 1 << 15;
        /// <summary>Samples a jump crossfades over (ClipVoice.Fade).</summary>
        const int Fade = ClipVoice.Fade;

        /// <summary>A stretcher and where it is in its audio: what it has rendered and not yet been asked for (a chunk's tail) waits in the FIFO.</summary>
        sealed class Stream
        {
            public NeoStretch S;
            public AudioSource Source;
            public long Pos;                    // the source sample the next input sample is
            public double Rate = 1;             // input samples per output sample, at the last chunk
            public double Acc;                  // the fraction of an input sample a fading stream owes
            public float Gain = 1;
            public readonly float[] L = new float[Chunk], R = new float[Chunk];
            public int Have, Used;              // the FIFO: [Used, Have)
        }

        Stream current, fading;
        readonly float[] inL = new float[MaxIn], inR = new float[MaxIn];
        readonly bool mono;
        bool active;
        int fadeIn;                 // samples of the fade in that are left
        int fadeLeft;

        ProVoice(NeoStretch a, NeoStretch b, bool mono)
        {
            current = new Stream { S = a };
            fading = new Stream { S = b };
            this.mono = mono;
        }

        /// <summary>Whether this voice makes mono stretchers (one channel; half the work).</summary>
        public bool Mono => mono;

        /// <summary>A voice, or null when the native library is missing. Allocates (stretchers, scratch): not for the audio thread.</summary>
        public static ProVoice Create(bool mono, int sampleRate)
        {
            var a = NeoStretch.Create(mono ? 1 : 2, sampleRate);
            var b = a == null ? null : NeoStretch.Create(mono ? 1 : 2, sampleRate);
            return b == null ? null : new ProVoice(a, b, mono);
        }

        /// <summary>Playing, or fading out.</summary>
        public bool Sounding => active || fadeLeft > 0;
        public bool Fading => fadeLeft > 0;

        /// <summary>Everything is silent at once (a crossfade tail that has faded by its own envelope).</summary>
        public void Silence()
        {
            active = false;
            fadeLeft = 0;
        }

        /// <summary>What plays goes on as the fading one, over <see cref="Fade"/> samples (the streams swap: the next start reuses the one that has faded).</summary>
        public void BeginFade()
        {
            if (!active) return;
            (current, fading) = (fading, current);
            fading.Acc = 0;
            fadeLeft = Fade;
            active = false;
        }

        /// <summary>The fading one's samples for this block, added in (before the playing one's).</summary>
        public void RenderFading(float[] left, float[] right, int offset, int count)
        {
            if (fadeLeft <= 0) return;
            var f = fading;
            int total = Math.Min(count, fadeLeft);
            for (int done = 0; done < total;)
            {
                if (f.Used >= f.Have)
                {
                    // What it would read on at the speed it had.
                    f.Acc += f.Rate * Chunk;
                    int nIn = (int)Math.Clamp(f.Acc, 0, MaxIn);
                    f.Acc = Math.Min(f.Acc - nIn, 1);
                    if (!Next(f, nIn)) { fadeLeft = 0; return; }
                }
                int n = Math.Min(total - done, f.Have - f.Used);
                for (int i = 0; i < n; i++)
                {
                    fadeLeft--;
                    float g = f.Gain * fadeLeft / Fade;
                    int k = f.Used + i;
                    left[offset + done + i] += Clean(f.L[k]) * g;
                    right[offset + done + i] += Clean(mono ? f.L[k] : f.R[k]) * g;
                }
                f.Used += n;
                done += n;
            }
        }

        /// <summary>
        /// A start from <paramref name="beat"/> (a launch, a jump): what played fades out and the audio from the beat is sought, so that the next
        /// sample rendered is the audio at the beat.
        /// </summary>
        public void Restart(AudioClipParams p, double beat, double beatsPerSample, double rate)
        {
            BeginFade();
            var c = current;
            var s = c.S;
            c.Source = p.Source;
            c.Used = c.Have = 0;
            SetPitch(s, p, rate);
            long from = Index(p.Map.SampleAt(beat));
            long ahead = Index(p.Map.SampleAt(beat + s.OutputLatency * beatsPerSample)) + s.InputLatency;
            int length = (int)Math.Clamp(ahead - from, s.InputLatency, MaxIn);
            Fill(c.Source, from, length);
            if (!s.Seek(inL, inR, length))
            {
                active = false;
                return;
            }
            c.Pos = from + length;
            c.Rate = Math.Clamp(p.Map.SamplesPerBeat(beat) * beatsPerSample, 0, MaxIn / (double)Chunk);
            c.Gain = p.Gain;
            active = true;
            fadeIn = Fade;
        }

        /// <summary>The next <paramref name="count"/> samples from <paramref name="beat"/>, added in (times the clip's gain, faded in after a start).</summary>
        public void Render(AudioClipParams p, double beat, double beatsPerSample, double rate, float[] left, float[] right, int offset, int count)
        {
            if (!active) return;
            var c = current;
            c.Gain = p.Gain;
            for (int done = 0; done < count;)
            {
                if (c.Used >= c.Have)
                {
                    // The next chunk, from where the beat is at its first sample: input up to where the map puts the beat a chunk and the output latency on.
                    SetPitch(c.S, p, rate);
                    double b = beat + done * beatsPerSample;
                    long to = Index(p.Map.SampleAt(b + (Chunk + c.S.OutputLatency) * beatsPerSample)) + c.S.InputLatency;
                    int nIn = (int)Math.Clamp(to - c.Pos, 0, MaxIn);
                    if (!Next(c, nIn)) { active = false; return; }
                    c.Rate = nIn / (double)Chunk;
                }
                int n = Math.Min(count - done, c.Have - c.Used);
                for (int i = 0; i < n; i++)
                {
                    float g = p.Gain;
                    if (fadeIn > 0)
                    {
                        g *= (Fade - fadeIn + 1) / (float)Fade;
                        fadeIn--;
                    }
                    int k = c.Used + i;
                    left[offset + done + i] += Clean(c.L[k]) * g;
                    right[offset + done + i] += Clean(mono ? c.L[k] : c.R[k]) * g;
                }
                c.Used += n;
                done += n;
            }
        }

        /// <summary>A chunk from <paramref name="nIn"/> input samples of the stream's audio, into its FIFO.</summary>
        bool Next(Stream st, int nIn)
        {
            Fill(st.Source, st.Pos, nIn);
            if (!st.S.Process(inL, inR, nIn, st.L, st.R, Chunk)) return false;
            st.Pos += nIn;
            st.Have = Chunk;
            st.Used = 0;
            return true;
        }

        static void SetPitch(NeoStretch s, AudioClipParams p, double rate)
        {
            // The pitch alone moves with the transposition; a source at another rate than the output also plays at its own pitch, which
            // only a whole-spectrum factor gives (no tonality limit then). Same rate: the highs keep their timbre a little (the library's advice).
            double factor = p.PitchRatio * rate;
            double tonality = rate == 1.0 ? 8000.0 / p.Source.SampleRate : 0;
            s.SetTranspose(factor, tonality);
        }

        /// <summary>Source samples [<paramref name="from"/>, +<paramref name="n"/>) into the input scratch, silence outside the audio.</summary>
        void Fill(AudioSource src, long from, int n)
        {
            Copy(src.Left, from, n, inL);
            if (!mono) Copy(src.Right, from, n, inR);
        }

        static void Copy(float[] from, long start, int n, float[] to)
        {
            if (n <= 0) return;
            long end = start + n;
            if (start >= from.Length || end <= 0)
            {
                Array.Clear(to, 0, n);
                return;
            }
            int lead = (int)Math.Max(0, -start);
            int tail = (int)Math.Max(0, end - from.Length);
            if (lead > 0) Array.Clear(to, 0, lead);
            Array.Copy(from, start + lead, to, lead, n - lead - tail);
            if (tail > 0) Array.Clear(to, n - tail, tail);
        }

        /// <summary>A source position as a sample index: nothing a clip could play is beyond 1e12.</summary>
        static long Index(double position) => double.IsFinite(position) ? (long)Math.Round(Math.Clamp(position, -1e12, 1e12)) : 0;

        static float Clean(float x) => !float.IsFinite(x) ? 0f : Math.Abs(x) < 1e-30f ? 0f : x;
    }
}
