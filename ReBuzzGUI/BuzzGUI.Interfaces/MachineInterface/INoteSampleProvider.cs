#nullable enable
// Fork (docs/specs/machines.md, Neo Sampler Tracker mode; TM1): a machine that plays a sample a note names, whatever its pitch. Its notes carry a
// sample number (and an offset and a tune), so the piano roll shows a chip on each note, a picker of the samples and a Sample lane. A machine says
// it by implementing this interface on its machine class (Neo Sampler, while its Mode is Tracker; later machines the same way); a machine without
// it gets none of that. Everything here is for the UI thread; nothing is called on the audio thread.

using System;
using System.Collections.Generic;

namespace Buzz.MachineInterface
{
    /// <summary>One sample a note can name: its number (what a note carries), its name, length and a picture of its wave.</summary>
    public sealed class NoteSample
    {
        public NoteSample(int number, string name, int frames, int rate, float[] peaks)
        {
            Number = number;
            Name = name;
            Frames = frames;
            Rate = rate;
            Peaks = peaks;
        }

        /// <summary>The number a note carries (1 and up; no upper limit but the machine's own).</summary>
        public int Number { get; }

        public string Name { get; }

        /// <summary>The length in frames of the wave, at <see cref="Rate"/>.</summary>
        public int Frames { get; }

        public int Rate { get; }

        /// <summary>Its length in seconds.</summary>
        public double Seconds => Rate > 0 ? Frames / (double)Rate : 0;

        /// <summary>The wave's outline: 0 … 1, the largest absolute value in each of a few dozen equal parts; empty when not known yet.</summary>
        public float[] Peaks { get; }
    }

    public interface INoteSampleProvider
    {
        /// <summary>Whether the machine plays a sample per note now (Neo Sampler: its Mode is Tracker).</summary>
        bool PlaysSamplePerNote { get; }

        /// <summary>A number that changes whenever <see cref="ReadNoteSamples"/> or <see cref="PlaysSamplePerNote"/> would answer differently. Cheap, any thread.</summary>
        int NoteSampleVersion { get; }

        /// <summary>The samples a note can name now, in number order. UI thread.</summary>
        IReadOnlyList<NoteSample> ReadNoteSamples();

        /// <summary>Plays the sample at its own speed (the picker's hover), or lets it go. The machine plays it at its next block.</summary>
        void PreviewNoteSample(int number, bool on);
    }
}
