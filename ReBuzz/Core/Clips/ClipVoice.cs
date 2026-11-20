#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 1.0): an audio clip playing, on the audio
// thread. Pure (ReBuzz.SessionTests links this file); no allocation on the audio thread.
//
// The engine says, each block, which beat of the clip plays and how fast beats go; the voice
// turns that into audio by its clip's warp mode. A jump (a launch, a loop, a new setting that
// moves the audio) starts it again, with what was sounding faded out under the new start, so
// nothing clicks. A new setting that leaves the audio where it is (gain, transposition) goes on.

using System;
using ReBuzz.Core.AudioLibrary;

namespace ReBuzz.Core.Clips
{
    internal sealed class ClipVoice : ISourceClock
    {
        /// <summary>Samples a jump crossfades over (about 1.5 ms): what sounded fades out as the new start fades in.</summary>
        public const int Fade = 64;
        /// <summary>A Beats slice that runs out loops at most this long a stretch of its end.</summary>
        public const double SliceLoopSeconds = 0.08;

        readonly StreamHint hint = StreamPrefetcher.NewHint();     // what the prefetch thread is told of this voice (neo-audio.md, NA1b)
        readonly StreamHint hintAhead = StreamPrefetcher.NewHint(); // and, from the UI thread, where a queued launch will start
        volatile PhaseVocoder vocoder;      // made on the UI thread when a clip in Complex mode is given (Prepare)
        volatile ProVoice pro;              // likewise for Pro (null when the native library is missing: the clip plays as Complex)
        // Complex material that is a bass line plays in the time domain too (PeriodicStretch.cs), as in Pro: the phase vocoder loses the cycle's shape.
        PeriodicVoice periodic;             // made on the UI thread (Prepare) for a Complex clip
        bool periodicRestart, periodicOn;
        bool proRestart;                    // Pro: the next block starts the stretcher from its beat
        bool proRaw;                        // Pro, waiting for its start (the block's seek was spent): playing as Beats meanwhile
        /// <summary>The inline Pro starts the block may still do; set by the mixer (null: no limit).</summary>
        internal SeekBudget Budget;
        AudioClipParams current;
        double expected = double.NaN;      // the beat the next block should start on
        bool sounding;
        long time;                         // output samples since the start
        int fadeIn;
        double startSource;                // Off: where the audio started

        // What sounded last, to fade it out on a jump or a stop.
        double lastSource = double.NaN, lastStep;

        /// <summary>A fade-out: two at once, so a jump right after another doesn't cut the first short.</summary>
        struct Tail
        {
            public AudioClipParams Params;
            public double Source, Step;
            public int Left;
        }

        Tail tail, older;

        // Beats: the slice playing, and the output sample it started on.
        int slice = -1;
        long sliceTime;

        // Complex: the clock the vocoder reads (the beat at an output time, and how fast beats go).
        double clockBeat, clockRate;
        long clockTime;

        /// <summary>Whether it sounds (or fades out).</summary>
        public bool IsSounding => sounding || tail.Left > 0 || older.Left > 0 || (pro?.Fading ?? false) || (periodic?.Fading ?? false);

        /// <summary>
        /// UI thread, before the params are handed to the audio thread: what they need made
        /// (Complex mode's phase vocoder, once).
        /// </summary>
        public void Prepare(AudioClipParams p)
        {
            if (p?.Mode == WarpMode.Complex && vocoder == null)
                vocoder = new PhaseVocoder();
            if (p?.Mode == WarpMode.Complex && PeriodicAnalysis.Of(p.Source) != null && (periodic == null || periodic.Mono != p.Source.IsMono || periodic.Rate != p.Source.SampleRate))
                periodic = new PeriodicVoice(p.Source.IsMono, p.Source.SampleRate);
            if (p?.Mode == WarpMode.Pro)
            {
                bool mono = p.Source.IsMono;
                var have = pro;
                if (have == null || have.Mono != mono)
                {
                    var made = NeoStretch.Available ? ProVoice.Create(mono, p.Source.SampleRate) : null;
                    if (made != null) pro = made;
                }
                // No native library (or no memory for it): Pro plays as Complex, which needs its vocoder.
                if (pro == null && vocoder == null) vocoder = new PhaseVocoder();
                if (pro != null) PeriodicAnalysis.Of(p.Source);       // the time-domain path for a bass line: decided here, once, off the audio thread
                else if (PeriodicAnalysis.Of(p.Source) != null && (periodic == null || periodic.Mono != p.Source.IsMono || periodic.Rate != p.Source.SampleRate))
                    periodic = new PeriodicVoice(p.Source.IsMono, p.Source.SampleRate);   // Pro without its library plays as Complex, bass path included
                pro?.Anticipate(p);
            }
        }

        /// <summary>Tests: waits for the Pro spare to be ready.</summary>
        internal bool WaitForProSpare(int milliseconds) => pro?.WaitForSpare(milliseconds) ?? false;

        /// <summary>The mode a clip plays in: Pro without its stretcher is Complex.</summary>
        WarpMode Effective(AudioClipParams p) => p.Mode == WarpMode.Pro && pro == null ? WarpMode.Complex : p.Mode;

        /// <summary>
        /// Adds a block of the clip into the buffers: <paramref name="beat"/> is the clip's beat
        /// at its first sample, <paramref name="beatsPerSample"/> how fast beats go,
        /// <paramref name="outputRate"/> the engine's sample rate. A block that doesn't go on
        /// from the last (a launch, a loop, other params that move the audio) starts again there.
        /// </summary>
        public void Render(AudioClipParams p, double beat, double beatsPerSample, int outputRate, float[] left, float[] right, int offset, int count)
        {
            if (p == null || count <= 0) return;
            if (!(beatsPerSample > 0) || !double.IsFinite(beatsPerSample) || !double.IsFinite(beat) || outputRate <= 0)
            {
                Stop(left, right, offset, count);
                return;
            }
            // Within two samples of where the last block ended is going on (rounding, not a jump).
            bool continuous = sounding && Math.Abs(beat - expected) <= 2 * beatsPerSample;
            if (!continuous || !ReferenceEquals(p, current))
            {
                bool sameAudio = sounding && current != null && ReferenceEquals(p.Source, current.Source) && p.Mode == current.Mode;
                if (continuous && sameAudio && Unmoved(current, p, beat))
                    current = p;                               // gain, pitch: the audio goes on where it is
                else if (sameAudio && Effective(p) == WarpMode.Complex && vocoder != null)
                    Glide(p, beat);                            // a loop going round: the vocoder's frames overlap it
                else
                    Start(p, beat);
            }
            double rate = p.Source.SampleRate / (double)Math.Max(1, outputRate);
            ProVoice.NoteTempo(beatsPerSample, outputRate);
            var stream = p.Source.Stream;                  // a streamed source: what is not in memory yet is silence, and is counted
            long missedBefore = stream != null ? stream.MissedSamples : 0;
            var fading = pro;
            if (fading != null && fading.Fading) fading.RenderFading(left, right, offset, count);       // what a jump or a stop left sounding
            if (periodic != null && periodic.Fading) periodic.RenderFading(left, right, offset, count);
            switch (Effective(p))
            {
                case WarpMode.Off:
                    {
                        double speed = rate * p.PitchRatio;
                        for (int i = 0; i < count; i++)
                        {
                            Tails(left, right, offset + i);
                            Put(p, startSource + (time + i) * speed, speed, left, right, offset + i, 1);
                        }
                        break;
                    }
                case WarpMode.Repitch:
                    for (int i = 0; i < count; i++)
                    {
                        Tails(left, right, offset + i);
                        double b = beat + i * beatsPerSample;
                        Put(p, p.Map.SampleAt(b), p.Map.SamplesPerBeat(b) * beatsPerSample, left, right, offset + i, 1);
                    }
                    break;
                case WarpMode.Beats:
                    RenderBeats(p, beat, beatsPerSample, rate * p.PitchRatio, left, right, offset, count);
                    break;
                case WarpMode.Complex:
                    RenderComplex(p, beat, beatsPerSample, rate, left, right, offset, count);
                    break;
                case WarpMode.Pro:
                    RenderPro(p, beat, beatsPerSample, rate, left, right, offset, count);
                    break;
            }
            time += count;
            expected = beat + count * beatsPerSample;
            if (stream != null)
            {
                if (stream.MissedSamples != missedBefore) stream.NoteUnderrun();
                PublishHint(p, beat, beatsPerSample, outputRate, count);
            }
        }

        /// <summary>
        /// Streamed audio: tells the prefetch thread where this voice reads and how far ahead it will (the window is the voice's playing time
        /// <see cref="StreamingPolicy.ReadAheadSeconds"/> at the speed it reads, with the reach of the mode: Pro reads up to its input
        /// latency and chunk ahead of the clip's beat, Complex a frame around it). Two windows: where the voice actually read (Beats plays a slice
        /// from its own start), and where the warp map puts the beat.
        /// </summary>
        void PublishHint(AudioClipParams p, double beat, double beatsPerSample, int outputRate, int count)
        {
            double s0 = p.Map.SampleAt(beat), s1 = p.Map.SampleAt(beat + count * beatsPerSample);
            double mapStep = Math.Abs(s1 - s0) / Math.Max(1, count);
            double step = !double.IsNaN(lastSource) && lastStep > 0 && double.IsFinite(lastStep) ? lastStep : mapStep;
            if (!double.IsFinite(s0) || !double.IsFinite(s1) || !double.IsFinite(step) || Math.Abs(s0) > 1e12 || Math.Abs(s1) > 1e12) return;
            double horizon = StreamingPolicy.ReadAheadSeconds * outputRate;
            long behind = Math.Max(4096, (long)(0.2 * p.Source.SampleRate));
            long guard = Effective(p) == WarpMode.Pro ? ProVoice.MaxIn + 4096 : Effective(p) == WarpMode.Complex ? 4 * PhaseVocoder.Size : 64;
            double cap = 30.0 * p.Source.SampleRate;                    // never ask for more than 30 seconds of source
            long aheadActual = (long)Math.Min(cap, step * horizon) + guard;
            long aheadMap = (long)Math.Min(cap, mapStep * horizon) + guard;
            long at = !double.IsNaN(lastSource) && Math.Abs(lastSource) < 1e12 ? (long)lastSource : (long)s1;
            long lo = (long)Math.Min(s0, s1), hi = (long)Math.Max(s0, s1);
            p.Source.Hint(hint, at, at - behind, at + aheadActual, lo - behind, hi + aheadMap);
        }

        /// <summary>
        /// UI thread: a clip is about to start at <paramref name="beat"/> (a launch queued for the next bar, the arrangement's lookahead): the
        /// prefetch thread reads where it will start, ahead of the window the pins hold.
        /// </summary>
        public void Anticipate(AudioClipParams p, double beat, double beatsPerSample, int outputRate)
        {
            if (p == null || !p.Source.IsStreaming || !(beatsPerSample > 0) || outputRate <= 0) return;
            double s = p.Map.SampleAt(beat);
            double step = Math.Abs(p.Map.SamplesPerBeat(beat) * beatsPerSample);
            if (!double.IsFinite(s) || !double.IsFinite(step) || Math.Abs(s) > 1e12) return;
            long behind = (long)(0.1 * p.Source.SampleRate);
            long ahead = (long)Math.Min(30.0 * p.Source.SampleRate, step * StreamingPolicy.ReadAheadSeconds * outputRate) + ProVoice.MaxIn + 4096;
            p.Source.Hint(hintAhead, (long)s, (long)s - behind, (long)s + ahead, 0, 0);
        }

        /// <summary>Whether new params of the same audio and mode leave it where it plays at this beat.</summary>
        static bool Unmoved(AudioClipParams old, AudioClipParams p, double beat)
        {
            if (p.Mode == WarpMode.Off) return true;                       // it plays on from where it is
            if (Math.Abs(old.Map.SampleAt(beat) - p.Map.SampleAt(beat)) >= 1) return false;
            if (p.Mode != WarpMode.Beats) return true;
            var a = old.Slices;
            var b = p.Slices;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i] || old.SliceBeats[i] != p.SliceBeats[i]) return false;
            return true;
        }

        /// <summary>The voice is silent at once, with no fade (a crossfade tail that has faded to nothing by its own envelope).</summary>
        public void Silence()
        {
            hint.Clear();
            sounding = false;
            expected = double.NaN;
            tail.Left = 0;
            older.Left = 0;
            lastSource = double.NaN;
            pro?.Silence();
            periodic?.Silence();
            periodicOn = false;
            proRaw = false;
        }

        /// <summary>The clip stopped: what sounded fades out (into these buffers, and the next ones until it has).</summary>
        public void Stop(float[] left, float[] right, int offset, int count)
        {
            if (sounding)
            {
                hint.Clear();
                BeginTail();
                pro?.BeginFade();
                if (periodicOn) { periodic.BeginFade(); periodicOn = false; }
                sounding = false;
                expected = double.NaN;
            }
            if (pro != null && pro.Fading) pro.RenderFading(left, right, offset, count);
            if (periodic != null && periodic.Fading) periodic.RenderFading(left, right, offset, count);
            for (int i = 0; i < count && (tail.Left > 0 || older.Left > 0); i++) Tails(left, right, offset + i);
        }

        void Start(AudioClipParams p, double beat)
        {
            if (sounding) BeginTail();
            if (sounding && current != null && Effective(current) == WarpMode.Pro) pro?.BeginFade();      // Pro fades through its own stretcher
            current = p;
            sounding = true;
            time = 0;
            fadeIn = Fade;
            slice = -1;
            startSource = p.Map.SampleAt(beat);
            lastSource = double.NaN;
            if (Effective(p) == WarpMode.Complex) vocoder?.Reset();
            if (periodicOn) periodic.BeginFade();
            periodicOn = false;
            periodicRestart = Effective(p) == WarpMode.Complex && periodic != null;
            proRestart = Effective(p) == WarpMode.Pro;
            proRaw = false;
        }

        /// <summary>
        /// Complex, the same audio from another beat (a loop going round, legato): the vocoder
        /// goes on reading from there, its overlapping frames crossfading what was and what is,
        /// with no fresh start to compute all at once.
        /// </summary>
        void Glide(AudioClipParams p, double beat)
        {
            current = p;
            startSource = p.Map.SampleAt(beat);
        }

        void BeginTail()
        {
            if (current == null || double.IsNaN(lastSource)) return;
            if (tail.Left > 0) older = tail;
            tail = new Tail { Params = current, Source = lastSource + lastStep, Step = lastStep, Left = Fade };
        }

        void Tails(float[] left, float[] right, int index)
        {
            if (tail.Left > 0) TailSample(ref tail, left, right, index);
            if (older.Left > 0) TailSample(ref older, left, right, index);
        }

        static void TailSample(ref Tail t, float[] left, float[] right, int index)
        {
            t.Left--;
            float g = t.Params.Gain * t.Left / Fade;
            t.Params.Source.Read(t.Source, out float l, out float r);
            left[index] += l * g;
            right[index] += r * g;
            t.Source += t.Step;
        }

        float FadeInGain()
        {
            if (fadeIn <= 0) return 1;
            float g = (Fade - fadeIn + 1) / (float)Fade;
            fadeIn--;
            return g;
        }

        /// <summary>One sample read at a position (times <paramref name="scale"/>), faded in after a start; remembered for a fade-out.</summary>
        void Put(AudioClipParams p, double position, double step, float[] left, float[] right, int index, float scale)
        {
            p.Source.Read(position, out float l, out float r);
            float g = p.Gain * scale * FadeInGain();
            left[index] += l * g;
            right[index] += r * g;
            lastSource = position;
            lastStep = step;
        }

        /// <summary>
        /// Beats: the slice the beat falls in plays from its start at the audio's own speed (times
        /// the transposition), so its attack lands on its beat at any tempo. A slice that runs out
        /// before the next one starts loops its end, crossfaded where it goes round (the first time
        /// too); at a new slice the last fades out under it.
        /// </summary>
        void RenderBeats(AudioClipParams p, double beat, double beatsPerSample, double speed, float[] left, float[] right, int offset, int count)
        {
            var beats = p.SliceBeats;
            var starts = p.Slices;
            for (int i = 0; i < count; i++)
            {
                int index = offset + i;
                double b = beat + i * beatsPerSample;
                if (b >= p.EndBeat)
                {
                    // Past the audio's end: silence (and nothing to fade out after).
                    Tails(left, right, index);
                    lastSource = p.Source.Length;
                    lastStep = 0;
                    continue;
                }
                int k = SliceAt(beats, b);
                if (k != slice)
                {
                    if (slice >= 0)
                    {
                        BeginTail();
                        fadeIn = Fade;
                    }
                    slice = k;
                    // Started inside the slice (a launch, a jump): where it would be had it started on its beat.
                    sliceTime = time + i - (long)Math.Round((b - beats[k]) / beatsPerSample);
                }
                Tails(left, right, index);                     // after a new slice's tail begins: from this sample
                double start = starts[slice];
                double end = slice + 1 < starts.Length ? starts[slice + 1] : p.Source.Length;
                double position = start + (time + i - sliceTime) * speed;
                double loop = Math.Max(1, Math.Min(end - start, SliceLoopSeconds * p.Source.SampleRate));
                double fade = Math.Min(loop / 4, 256);
                bool loops = position >= end;
                if (!loops && fade >= 1 && position >= end - fade)
                {
                    // Nearly out: does the next slice (or the audio's end) come first?
                    double next = slice + 1 < beats.Length ? beats[slice + 1] : p.EndBeat;
                    loops = (end - position) / speed < (next - b) / beatsPerSample;
                }
                if (loops)
                {
                    // Runs out before the next slice: loop its end, crossfaded where it goes round.
                    double loopStart = end - loop;
                    double into = (position - loopStart) % loop;
                    position = loopStart + into;
                    if (fade >= 1 && into > loop - fade)
                    {
                        float w = (float)((into - (loop - fade)) / fade);
                        p.Source.Read(position - loop, out float l2, out float r2);
                        Put(p, position, speed, left, right, index, 1 - w);
                        float g = p.Gain * w;
                        left[index] += l2 * g;
                        right[index] += r2 * g;
                        continue;
                    }
                }
                Put(p, position, speed, left, right, index, 1);
            }
        }

        /// <summary>Complex: the phase vocoder, reading the audio where the beat puts it, transposed (and resampled) by the ratio.</summary>
        void RenderComplex(AudioClipParams p, double beat, double beatsPerSample, double rate, float[] left, float[] right, int offset, int count)
        {
            if (periodic != null)
            {
                if (periodicRestart)
                {
                    periodicRestart = false;
                    periodicOn = PeriodicVoice.Qualifies(p, rate, periodic.Rate);
                    if (periodicOn) periodic.Start(p, beat, beatsPerSample);
                }
                else if (periodicOn && !PeriodicVoice.Qualifies(p, rate, periodic.Rate))
                {
                    // Asked for something it does not do (a transposition): the vocoder takes over, from here.
                    periodic.BeginFade();
                    periodicOn = false;
                    vocoder?.Reset();
                    fadeIn = Fade;
                }
                if (periodicOn)
                {
                    for (int n0 = 0; n0 < count && (tail.Left > 0 || older.Left > 0); n0++) Tails(left, right, offset + n0);
                    periodic.Render(p, beat, beatsPerSample, left, right, offset, count);
                    lastSource = double.NaN;                  // what a fade-out would fall back on is the run's own
                    return;
                }
            }
            var v = vocoder;
            if (v == null)
            {
                // Not prepared (params made without Prepare): silent rather than allocating here.
                for (int n = 0; n < count; n++) Tails(left, right, offset + n);
                return;
            }
            clockBeat = beat;
            clockTime = v.Time;
            clockRate = beatsPerSample;
            double ratio = p.PitchRatio * rate;
            int i = 0;
            // After a start: one sample at a time, faded in, under what fades out.
            for (; i < count && fadeIn > 0; i++)
            {
                Tails(left, right, offset + i);
                RenderOne(v, p, ratio, left, right, offset + i, FadeInGain());
            }
            for (; i < count && (tail.Left > 0 || older.Left > 0); i++)
            {
                Tails(left, right, offset + i);
                RenderOne(v, p, ratio, left, right, offset + i, 1);
            }
            if (i < count) v.Render(p.Source, this, ratio, left, right, offset + i, count - i, p.Gain);
            lastSource = SourceAt(v.Time - 1);
            lastStep = p.Map.SamplesPerBeat(beat) * beatsPerSample;
        }

        /// <summary>Pro: Signalsmith Stretch (<see cref="ProVoice"/>), started from the beat on a jump, reading ahead of it by its latencies.</summary>
        void RenderPro(AudioClipParams p, double beat, double beatsPerSample, double rate, float[] left, float[] right, int offset, int count)
        {
            var v = pro;
            if (v == null) return;
            if (proRestart)
            {
                var started = v.Restart(p, beat, beatsPerSample, rate, Budget);
                if (started != ProVoice.Started.Later)
                {
                    proRestart = false;
                    if (proRaw)
                    {
                        BeginTail();                  // the stand-in fades out under the stretcher
                        proRaw = false;
                    }
                }
                else proRaw = true;
            }
            if (proRaw)
            {
                // The block's seek went to another clip: this one plays as Beats (pitch and time right at any tempo, and cheap) until
                // its turn, a block or two; the stretcher's start then fades it out. Repitch would be the wrong pitch off the clip's tempo.
                RenderBeats(p, beat, beatsPerSample, rate * p.PitchRatio, left, right, offset, count);
                return;
            }
            for (int i = 0; i < count && (tail.Left > 0 || older.Left > 0); i++) Tails(left, right, offset + i);
            v.Render(p, beat, beatsPerSample, rate, left, right, offset, count);
            // A raw copy of the audio at the end of this block is what a fade-out falls back on if its own stretcher is not there.
            lastSource = double.NaN;
        }

        readonly float[] oneL = new float[1], oneR = new float[1];

        void RenderOne(PhaseVocoder v, AudioClipParams p, double ratio, float[] left, float[] right, int index, float scale)
        {
            oneL[0] = oneR[0] = 0;
            v.Render(p.Source, this, ratio, oneL, oneR, 0, 1, p.Gain * scale);
            left[index] += oneL[0];
            right[index] += oneR[0];
        }

        static int SliceAt(double[] beats, double beat)
        {
            int lo = 0, hi = beats.Length - 1;
            if (hi <= 0 || beat < beats[0]) return 0;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (beats[mid] <= beat) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>The vocoder's clock: the audio's position for an output time.</summary>
        public double SourceAt(double outputTime) =>
            Math.Clamp(current.Map.SampleAt(clockBeat + (outputTime - clockTime) * clockRate), -1e15, 1e15);
    }
}
