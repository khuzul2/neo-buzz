#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 1.0): an audio clip playing, on the audio
// thread. Pure (ReBuzz.SessionTests links this file); no allocation after construction.
//
// The engine says, each block, which beat of the clip plays and how fast beats go; the voice
// turns that into audio by its clip's warp mode. A jump (a launch, a loop, a new setting)
// starts it again, with what was sounding faded out under the new start, so nothing clicks.

using System;

namespace ReBuzz.Core.Clips
{
    internal sealed class ClipVoice : ISourceClock
    {
        /// <summary>Samples a jump crossfades over (about 1.5 ms): what sounded fades out as the new start fades in.</summary>
        public const int Fade = 64;
        /// <summary>A Beats slice that runs out loops at most this long a stretch of its end.</summary>
        public const double SliceLoopSeconds = 0.08;

        readonly PhaseVocoder vocoder = new PhaseVocoder();
        AudioClipParams current;
        double expected = double.NaN;      // the beat the next block should start on
        bool sounding;
        long time;                         // output samples since the start
        int fadeIn;
        double startSource;                // Off: where the audio started

        // What sounded last, to fade it out on a jump or a stop.
        double lastSource = double.NaN, lastStep;
        AudioClipParams tailParams;
        double tailSource, tailStep;
        int tailLeft;

        // Beats: the slice playing, and the output sample it started on.
        int slice = -1;
        long sliceTime;

        // Complex: the clock the vocoder reads (the beat at an output time, and how fast beats go).
        double clockBeat, clockRate;
        long clockTime;

        /// <summary>Whether it sounds (or fades out).</summary>
        public bool IsSounding => sounding || tailLeft > 0;

        /// <summary>
        /// Adds a block of the clip into the buffers: <paramref name="beat"/> is the clip's beat
        /// at its first sample, <paramref name="beatsPerSample"/> how fast beats go,
        /// <paramref name="outputRate"/> the engine's sample rate. A block that doesn't go on
        /// from the last (a launch, a loop, other params) starts again there.
        /// </summary>
        public void Render(AudioClipParams p, double beat, double beatsPerSample, int outputRate, float[] left, float[] right, int offset, int count)
        {
            if (p == null || count <= 0) return;
            if (!(beatsPerSample > 0) || !double.IsFinite(beat))
            {
                Stop(left, right, offset, count);
                return;
            }
            // Within two samples of where the last block ended is going on (rounding, not a jump).
            if (!sounding || !ReferenceEquals(p, current) || !(Math.Abs(beat - expected) <= 2 * beatsPerSample))
                Start(p, beat);
            double rate = p.Source.SampleRate / (double)Math.Max(1, outputRate);
            switch (p.Mode)
            {
                case WarpMode.Off:
                    {
                        double speed = rate * p.PitchRatio;
                        for (int i = 0; i < count; i++)
                        {
                            TailSample(left, right, offset + i);
                            Put(p, startSource + (time + i) * speed, speed, left, right, offset + i, 1);
                        }
                        break;
                    }
                case WarpMode.Repitch:
                    for (int i = 0; i < count; i++)
                    {
                        TailSample(left, right, offset + i);
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
            }
            time += count;
            expected = beat + count * beatsPerSample;
        }

        /// <summary>The clip stopped: what sounded fades out (into these buffers, and the next ones until it has).</summary>
        public void Stop(float[] left, float[] right, int offset, int count)
        {
            if (sounding)
            {
                BeginTail();
                sounding = false;
                expected = double.NaN;
            }
            for (int i = 0; i < count && tailLeft > 0; i++) TailSample(left, right, offset + i);
        }

        void Start(AudioClipParams p, double beat)
        {
            if (sounding) BeginTail();
            current = p;
            sounding = true;
            time = 0;
            fadeIn = Fade;
            slice = -1;
            startSource = p.Map.SampleAt(beat);
            lastSource = double.NaN;
            if (p.Mode == WarpMode.Complex) vocoder.Reset();
        }

        void BeginTail()
        {
            if (current == null || double.IsNaN(lastSource)) return;
            tailParams = current;
            tailSource = lastSource + lastStep;
            tailStep = lastStep;
            tailLeft = Fade;
        }

        void TailSample(float[] left, float[] right, int index)
        {
            if (tailLeft <= 0) return;
            tailLeft--;
            float g = tailParams.Gain * tailLeft / Fade;
            tailParams.Source.Read(tailSource, out float l, out float r);
            left[index] += l * g;
            right[index] += r * g;
            tailSource += tailStep;
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
        /// before the next one starts loops its end; at a new slice the last fades out under it.
        /// </summary>
        void RenderBeats(AudioClipParams p, double beat, double beatsPerSample, double speed, float[] left, float[] right, int offset, int count)
        {
            var beats = p.SliceBeats;
            var starts = p.Slices;
            for (int i = 0; i < count; i++)
            {
                int index = offset + i;
                TailSample(left, right, index);
                double b = beat + i * beatsPerSample;
                if (b >= p.EndBeat)
                {
                    // Past the audio's end: silence (and nothing to fade out after).
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
                double start = starts[slice];
                double end = slice + 1 < starts.Length ? starts[slice + 1] : p.Source.Length;
                double position = start + (time + i - sliceTime) * speed;
                if (position >= end)
                {
                    // Ran out before the next slice: loop its end, crossfaded where it goes round.
                    double loop = Math.Max(1, Math.Min(end - start, SliceLoopSeconds * p.Source.SampleRate));
                    double loopStart = end - loop;
                    double into = (position - loopStart) % loop;
                    position = loopStart + into;
                    double fade = Math.Min(loop / 4, 256);
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
            clockBeat = beat;
            clockTime = vocoder.Time;
            clockRate = beatsPerSample;
            double ratio = p.PitchRatio * rate;
            int i = 0;
            // After a start: one sample at a time, faded in, under what fades out.
            for (; i < count && fadeIn > 0; i++)
            {
                TailSample(left, right, offset + i);
                RenderOne(p, ratio, left, right, offset + i, FadeInGain());
            }
            for (; i < count && tailLeft > 0; i++)
            {
                TailSample(left, right, offset + i);
                RenderOne(p, ratio, left, right, offset + i, 1);
            }
            if (i < count) vocoder.Render(p.Source, this, ratio, left, right, offset + i, count - i, p.Gain);
            lastSource = SourceAt(vocoder.Time - 1);
            lastStep = p.Map.SamplesPerBeat(beat) * beatsPerSample;
        }

        readonly float[] oneL = new float[1], oneR = new float[1];

        void RenderOne(AudioClipParams p, double ratio, float[] left, float[] right, int index, float scale)
        {
            oneL[0] = oneR[0] = 0;
            vocoder.Render(p.Source, this, ratio, oneL, oneR, 0, 1, p.Gain * scale);
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
            current.Map.SampleAt(clockBeat + (outputTime - clockTime) * clockRate);
    }
}
