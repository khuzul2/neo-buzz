#nullable disable
// Neo Organ (docs/specs/neo-organ.md): the tonewheel generator's tables. Clean room: published literature on how a
// tonewheel organ is built (a fixed set of sine generators, one per note of its range, switched onto nine
// harmonic busses by the keys) and our own DSP; no existing machine's source or tables were read.
//
// 91 wheels, numbered 1 … 91, equal tempered from A = 440 Hz: wheel 1 is C1 (32.703 Hz), wheel 91 is F#8 (5919.9 Hz).
// A manual has 61 keys, MIDI notes 36 … 96 (C2 … C7). A key's 8' pitch is its own note; each drawbar takes the wheel a
// fixed number of semitones above that (16': -12, 5 1/3': +7, 8': 0, 4': +12, 2 2/3': +19, 2': +24, 1 3/5': +28,
// 1 1/3': +31, 1': +36). Above wheel 91 the footage folds back an octave (repeatedly) on to the wheels of the top octave and a half, as on the instrument.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Organ
{
    internal static class Tonewheels
    {
        public const int Count = 91;
        public const int Slots = 92;                 // wheel numbers 1 … 91 index arrays directly
        public const int FirstKey = 36;              // MIDI note of the lowest key
        public const int Keys = 61;
        public const int Bars = 9;
        public const int SineBits = 12;              // table of 4096 + 1 samples, read with linear interpolation
        public const int SineSize = 1 << SineBits;

        /// <summary>Semitones above the key's own (8') pitch for each drawbar: 16', 5 1/3', 8', 4', 2 2/3', 2', 1 3/5', 1 1/3', 1'.</summary>
        public static readonly int[] BarOffset = { -12, 7, 0, 12, 19, 24, 28, 31, 36 };

        public static readonly string[] BarFootage = { "16'", "5 1/3'", "8'", "4'", "2 2/3'", "2'", "1 3/5'", "1 1/3'", "1'" };

        /// <summary>The percussion's wheels: the 2nd harmonic is the 4' wheel, the 3rd the 2 2/3' wheel.</summary>
        public const int Perc2Offset = 12, Perc3Offset = 19;

        /// <summary>Frequency of a wheel (1 … 91) in Hz: the wheel table.</summary>
        public static double WheelHz(int wheel) => 440.0 * PortableMath.Pow(2.0, (24 + wheel - 1 - 69) / 12.0);

        /// <summary>The wheel a key (0 … 60) feeds into a footage <paramref name="offset"/> semitones above its 8' pitch, folded into 1 … 91.</summary>
        public static int WheelFor(int key, int offset)
        {
            int w = key + (FirstKey - 24 + 1) + offset;
            while (w > Count) w -= 12;
            while (w < 1) w += 12;
            return w;
        }

        /// <summary>A MIDI note as a key of the 61-key manual: notes outside it sound in the nearest octave inside it.</summary>
        public static int KeyFor(int midi)
        {
            int n = midi;
            while (n < FirstKey) n += 12;
            while (n > FirstKey + Keys - 1) n -= 12;
            return n - FirstKey;
        }

        /// <summary>The drawbar step law: 0 is off, each step is 3 dB, 8 is 0 dB.</summary>
        public static float BarGain(int step) => step <= 0 ? 0f : (float)PortableMath.Pow(10.0, -3.0 * (8 - Math.Min(step, 8)) / 20.0);

        /// <summary>One cycle of a sine plus its first point again, so an interpolated read at the last index needs no wrap.</summary>
        public static readonly float[] Sine = MakeSine();

        static float[] MakeSine()
        {
            var t = new float[SineSize + 1];
            for (int i = 0; i <= SineSize; i++) t[i] = (float)PortableMath.Sin(2.0 * Math.PI * i / SineSize);
            return t;
        }

        /// <summary>Each wheel's gain: a little level variation (±0.5 dB, fixed, the same in every instance), as the wheels of a real organ never match exactly.</summary>
        public static readonly float[] WheelGain = MakeGains();

        static float[] MakeGains()
        {
            var g = new float[Slots];
            var rng = new Rng(0x0ABC1234u);
            for (int w = 1; w <= Count; w++) g[w] = (float)PortableMath.Pow(10.0, rng.NextBipolar() * 0.5 / 20.0);
            return g;
        }
    }
}
