#nullable disable
// Agent server (docs/specs/mastering-tools.md §5.9, M5): render(bits, dither). The master is rounded to a 16 or 24 bit
// grid with NeoBuzz.Dsp's Dither (one generator pair per channel, seeded the same way every time, so renders stay
// bit-identical) and written as PCM; the measurements are taken from what is in the file.

using System;
using NeoBuzz.Dsp;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.Audio
{
    public static class Requantize
    {
        /// <summary>"off" (plain rounding), "tpdf", "light" or "psychoacoustic": TPDF with that noise shaping.</summary>
        internal static (bool Tpdf, DitherShape Shape) Parse(string dither) => (dither ?? "tpdf").Trim().ToLowerInvariant() switch
        {
            "off" or "none" => (false, DitherShape.Off),
            "tpdf" => (true, DitherShape.Off),
            "light" => (true, DitherShape.Light),
            "psychoacoustic" => (true, DitherShape.Psychoacoustic),
            _ => throw new ToolError($"dither: \"{dither}\" isn't off, tpdf, light or psychoacoustic."),
        };

        /// <summary>Throws a ToolError for a dither name that isn't one.</summary>
        public static void Validate(string dither) => Parse(dither);

        /// <summary>The audio rounded to <paramref name="bits"/> (16 or 24), exactly on the grid.</summary>
        public static StereoAudio To(StereoAudio a, int bits, string dither)
        {
            if (bits != 16 && bits != 24) throw new ToolError("bits: 16 or 24.");
            var (tpdf, shape) = Parse(dither);
            var l = new float[a.Frames];
            var r = a.Right != null ? new float[a.Frames] : null;
            var dl = new Dither(bits, tpdf, shape, 1);
            var dr = new Dither(bits, tpdf, shape, 2);
            for (int i = 0; i < l.Length; i++)
            {
                l[i] = dl.Process(a.Left[i]);
                if (r != null) r[i] = dr.Process(a.Right[i]);
            }
            return new StereoAudio { Left = l, Right = r, Rate = a.Rate };
        }
    }
}
