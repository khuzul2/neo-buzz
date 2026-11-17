#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md section 11): the Pro warp mode. A time-stretcher and pitch-shifter from
// Signalsmith Stretch (MIT), behind a small C ABI library (NeoStretch/, "neostretch"). Pure but for the library: ReBuzz.SessionTests
// links this file. The library is loaded once, on the UI thread (Prepare); nothing here allocates, locks or throws on the audio thread.
// When the library is missing the clip plays in Complex, and the log says so once.

using System;
using System.Runtime.InteropServices;
using System.Threading;
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
            // The spare's job and what came of it (spare stretcher, below).
            public AudioClipParams ReqParams;
            public double ReqBeat, ReqBps, ReqRate;
            public long From;
            public int Length;
            public double Factor;
        }

        Stream current, fading, spare;
        readonly float[] inL = new float[MaxIn], inR = new float[MaxIn];
        readonly bool mono;
        bool active;
        int fadeIn;                 // samples of the fade in that are left
        int fadeLeft;

        ProVoice(NeoStretch a, NeoStretch b, NeoStretch c, bool mono)
        {
            current = new Stream { S = a };
            fading = new Stream { S = b };
            spare = new Stream { S = c };              // null stretcher: no spare (no memory for a third), starts are all inline
            this.mono = mono;
        }

        // ---- The spare: a start's seek made ahead, off the audio thread -------------------------------
        //
        // A start (a launch, a loop going round) costs the library's output seek, about a millisecond: several in one block would not fit it.
        // So a third stretcher is sought ahead by a worker thread, at the beat the last start was at (a launch and a loop start in the same
        // place, nearly always) and the tempo the engine last ran at; a start that finds it ready and matching (same audio, same beat and
        // input length, same pitch) takes it as its own stretcher, for no work at all, and asks for the next. Hand-over by one state word,
        // no locks: 0 idle (the voice's), 5 being written, 1 requested, 2 the worker's, 3 ready, 6 being taken.
        int spareState;
        bool queued;
        /// <summary>Tests: false turns the worker's preparing off, so every start is unanticipated.</summary>
        internal static volatile bool Prefetch = true;
        static double tempoBps, tempoRate;
        // Tests: how starts went (the spare's, done in the block, put off to a later block).
        internal static long SpareStarts, InlineStarts, LaterStarts;

        /// <summary>The tempo and output rate the engine last ran at: the guess a clip's first spare is made for.</summary>
        public static void NoteTempo(double beatsPerSample, int outputRate)
        {
            Volatile.Write(ref tempoBps, beatsPerSample);
            Volatile.Write(ref tempoRate, outputRate);
        }

        /// <summary>Asks for the spare to be sought at <paramref name="beat"/> of <paramref name="p"/> (<paramref name="rate"/>: source rate over output rate), unless it is busy or ready already.</summary>
        void RequestSpare(AudioClipParams p, double beat, double bps, double rate)
        {
            if (!Prefetch || spare.S == null || p == null || !(bps > 0) || !(rate > 0) || !double.IsFinite(beat)) return;
            if (Interlocked.CompareExchange(ref spareState, 5, 0) != 0) return;
            var sp = spare;                              // read once the state is ours: nobody swaps it now
            sp.ReqParams = p;
            sp.ReqBeat = beat;
            sp.ReqBps = bps;
            sp.ReqRate = rate;
            Volatile.Write(ref spareState, 1);
            if (!queued)
            {
                queued = true;
                ProWorker.Add(this);
            }
        }

        /// <summary>UI thread, with new params: the first spare, for a start at the clip's beginning at the tempo the engine last ran at.</summary>
        public void Anticipate(AudioClipParams p)
        {
            double bps = Volatile.Read(ref tempoBps), rate = Volatile.Read(ref tempoRate);
            if (bps > 0 && rate > 0 && p != null) RequestSpare(p, 0, bps, p.Source.SampleRate / rate);
        }

        /// <summary>Worker thread: does the requested seek, if there is one. Whether it did.</summary>
        internal bool Work(float[] scratchL, float[] scratchR)
        {
            if (Interlocked.CompareExchange(ref spareState, 2, 1) != 1) return false;
            var st = spare;
            bool ok = false;
            try
            {
                var p = st.ReqParams;
                var s = st.S;
                Plan(s, p, st.ReqBeat, st.ReqBps, out long from, out int length);
                SetPitch(s, p, st.ReqRate);
                Copy(p.Source.Left, from, length, scratchL);
                if (!mono) Copy(p.Source.Right, from, length, scratchR);
                if (s.Seek(scratchL, mono ? scratchL : scratchR, length))
                {
                    st.Source = p.Source;
                    st.From = from;
                    st.Length = length;
                    st.Factor = p.PitchRatio * st.ReqRate;
                    st.Used = st.Have = 0;
                    ok = true;
                }
            }
            catch
            {
            }
            Volatile.Write(ref spareState, ok ? 3 : 0);
            return true;
        }

        /// <summary>Where a start's input begins and how much of it the seek reads (the audio from the beat up to the latencies ahead).</summary>
        static void Plan(NeoStretch s, AudioClipParams p, double beat, double bps, out long from, out int length)
        {
            from = Index(p.Map.SampleAt(beat));
            long ahead = Index(p.Map.SampleAt(beat + s.OutputLatency * bps)) + s.InputLatency;
            length = (int)Math.Clamp(ahead - from, s.InputLatency, MaxIn);
        }

        /// <summary>Tests: waits until the spare is ready (or the time is up). Whether it is.</summary>
        internal bool WaitForSpare(int milliseconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref spareState) != 3)
            {
                if (clock.ElapsedMilliseconds > milliseconds) return false;
                Thread.Sleep(1);
            }
            return true;
        }

        /// <summary>Whether this voice makes mono stretchers (one channel; half the work).</summary>
        public bool Mono => mono;

        /// <summary>A voice, or null when the native library is missing. Allocates (stretchers, scratch): not for the audio thread.</summary>
        public static ProVoice Create(bool mono, int sampleRate)
        {
            var a = NeoStretch.Create(mono ? 1 : 2, sampleRate);
            var b = a == null ? null : NeoStretch.Create(mono ? 1 : 2, sampleRate);
            if (b == null) return null;
            var voice = new ProVoice(a, b, NeoStretch.Create(mono ? 1 : 2, sampleRate), mono);
            // Handed to the worker here, off the audio thread: the first request for a spare can come from the audio thread (nothing has asked
            // from the UI before the engine's first block), and adding a voice allocates and may start the worker's thread.
            voice.queued = true;
            ProWorker.Add(voice);
            return voice;
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

        /// <summary>How a start went.</summary>
        public enum Started { Yes, Later, Failed }

        /// <summary>
        /// A start from <paramref name="beat"/> (a launch, a jump): what played fades out and the audio from the beat is sought, so that the next
        /// sample rendered is the audio at the beat. The seek is the spare's when it is ready for exactly this start (no work); otherwise it is
        /// done here, if the block's <paramref name="budget"/> has a seek left (null: no limit), and if not the answer is <see cref="Started.Later"/>:
        /// ask again next block, and play something else meanwhile.
        /// </summary>
        public Started Restart(AudioClipParams p, double beat, double beatsPerSample, double rate, SeekBudget budget = null)
        {
            var sp = spare;
            long from;
            int length;
            Plan((sp.S ?? current.S), p, beat, beatsPerSample, out from, out length);
            double factor = p.PitchRatio * rate;
            if (sp.S != null)
            {
                int state = Volatile.Read(ref spareState);
                if (state == 3 && Interlocked.CompareExchange(ref spareState, 6, 3) == 3)
                {
                    bool fits = ReferenceEquals(sp.Source, p.Source) && sp.From == from && sp.Length == length && sp.Factor == factor;
                    if (fits)
                    {
                        // The spare becomes the playing stretcher; what played fades out in its own; the one that was fading is the new spare.
                        Stream free;
                        if (active)
                        {
                            free = fading;
                            fading = current;
                            fading.Acc = 0;
                            fadeLeft = Fade;
                        }
                        else free = current;
                        current = sp;
                        spare = free;
                        current.Pos = from + length;
                        current.Rate = Math.Clamp(p.Map.SamplesPerBeat(beat) * beatsPerSample, 0, MaxIn / (double)Chunk);
                        current.Gain = p.Gain;
                        active = true;
                        fadeIn = Fade;
                        Volatile.Write(ref spareState, 0);
                        RequestSpare(p, beat, beatsPerSample, rate);
                        Interlocked.Increment(ref SpareStarts);
                        return Started.Yes;
                    }
                    Volatile.Write(ref spareState, 0);                // not for this start: the next request will be
                }
                else if (state == 1)
                    Interlocked.CompareExchange(ref spareState, 0, 1);       // asked for, not begun: never mind
            }
            if (budget != null && budget.Left <= 0)
            {
                Interlocked.Increment(ref LaterStarts);
                return Started.Later;
            }
            if (budget != null) budget.Left--;
            BeginFade();
            var c = current;
            var s = c.S;
            c.Source = p.Source;
            c.Used = c.Have = 0;
            SetPitch(s, p, rate);
            Fill(c.Source, from, length);
            if (!s.Seek(inL, inR, length))
            {
                active = false;
                return Started.Failed;
            }
            c.Pos = from + length;
            c.Rate = Math.Clamp(p.Map.SamplesPerBeat(beat) * beatsPerSample, 0, MaxIn / (double)Chunk);
            c.Gain = p.Gain;
            active = true;
            fadeIn = Fade;
            RequestSpare(p, beat, beatsPerSample, rate);
            Interlocked.Increment(ref InlineStarts);
            return Started.Yes;
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

    /// <summary>The starts a block may do inline (see <see cref="ProVoice.Restart"/>): the mixer's, refilled each block.</summary>
    internal sealed class SeekBudget
    {
        /// <summary>Inline seeks a mixer allows in one block: about a millisecond each, so one fits any block.</summary>
        public const int PerBlock = 1;
        public int Left = PerBlock;
    }

    /// <summary>The thread that makes the spares' seeks (ProVoice): one for all voices, asleep but for a tick every couple of milliseconds.</summary>
    internal static class ProWorker
    {
        static readonly object gate = new object();
        static WeakReference<ProVoice>[] voices = Array.Empty<WeakReference<ProVoice>>();
        static Thread thread;

        public static void Add(ProVoice v)
        {
            lock (gate)
            {
                var live = new System.Collections.Generic.List<WeakReference<ProVoice>>();
                foreach (var w in voices)
                    if (w.TryGetTarget(out _)) live.Add(w);
                live.Add(new WeakReference<ProVoice>(v));
                voices = live.ToArray();
                if (thread == null)
                {
                    thread = new Thread(Run) { IsBackground = true, Name = "Neo Audio Pro worker", Priority = ThreadPriority.BelowNormal };
                    thread.Start();
                }
            }
        }

        static void Run()
        {
            var l = new float[ProVoice.MaxIn];
            var r = new float[ProVoice.MaxIn];
            while (true)
            {
                bool did = false;
                var list = voices;
                foreach (var w in list)
                    if (w.TryGetTarget(out var v)) did |= v.Work(l, r);
                if (!did) Thread.Sleep(2);
            }
        }
    }
}
