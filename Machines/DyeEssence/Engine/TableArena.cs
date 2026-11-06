#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.1): a wavetable laid out for the unison kernel. The
// frames and mip levels of a NeoBuzz.Dsp.Wavetable (11 band-limited levels per frame, harmonics up
// to 1024 >> level, built by FFT) in one array; every level is a power of two long, so a 32-bit
// phase gives the read index and its fraction by shifts alone. Each level keeps Wavetable's three
// guard samples (index −1, len, len+1), so a cubic read never wraps and a lane's four points are one
// 16-byte load. Built off the audio thread; read only after.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    internal sealed class TableArena
    {
        public const int Levels = Wavetable.Levels;

        /// <summary>Samples per level plus the three guards.</summary>
        static readonly int[] Stride = new int[Levels];
        /// <summary>Offset of each level inside a frame's block.</summary>
        static readonly int[] LevelOffset = new int[Levels];
        /// <summary>log2 of each level's length.</summary>
        static readonly int[] Log2Length = new int[Levels];
        public static readonly int FrameStride;

        static TableArena()
        {
            int at = 0;
            for (int l = 0; l < Levels; l++)
            {
                int len = Wavetable.LevelLength(l);
                int k = 0;
                while ((1 << k) < len) k++;
                if ((1 << k) != len) throw new InvalidOperationException("mip levels must be powers of two");
                Log2Length[l] = k;
                LevelOffset[l] = at;
                Stride[l] = len + 3;
                at += len + 3;
            }
            FrameStride = at;
        }

        public TableArena(Wavetable table)
        {
            Name = table.Name;
            FrameCount = table.FrameCount;
            Data = new float[FrameCount * FrameStride];
            for (int f = 0; f < FrameCount; f++)
                for (int l = 0; l < Levels; l++)
                {
                    var s = table.FrameSamples(f, l);
                    int len = s.Length;
                    int o = f * FrameStride + LevelOffset[l];
                    Array.Copy(s, 0, Data, o + 1, len);
                    Data[o] = s[len - 1];
                    Data[o + len + 1] = s[0];
                    Data[o + len + 2] = s[1];
                }
        }

        public string Name { get; }
        public int FrameCount { get; }

        /// <summary>All frames and levels.</summary>
        public float[] Data { get; }

        public long Bytes => (long)Data.Length * sizeof(float);

        /// <summary>Where a frame's level starts (its guard sample −1).</summary>
        public static int Offset(int frame, int level) => frame * FrameStride + LevelOffset[level];

        /// <summary>log2 of a level's length.</summary>
        public static int Log2(int level) => Log2Length[level];
    }
}
