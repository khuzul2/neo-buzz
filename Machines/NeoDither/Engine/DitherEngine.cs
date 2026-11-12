#nullable disable
// Neo Dither (docs/specs/mastering-tools.md §5.9): both channels through NeoBuzz.Dsp's Dither (one generator pair per
// channel, seeded at Reset), the output exactly on the Bits grid. After BlankAfter (0.7 s by default) of exact digital silence in both channels the output
// is silence and the feedback is cleared; the first non-zero sample ends it. No latency. Allocation-free.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Dither
{
    internal sealed class DitherEngine
    {
        public const int MaxBlock = 256;
        readonly ParamValues values;
        readonly Dsp.Dither[] ch = new Dsp.Dither[2];
        float sampleRate = 48000;
        bool blank;
        long zeros, blankAfter;
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        public DitherEngine(ParamValues values)
        {
            this.values = values;
            ch[0] = new Dsp.Dither(16, true, DitherShape.Off, 1);
            ch[1] = new Dsp.Dither(16, true, DitherShape.Off, 2);
        }

        public int Latency => 0;

        /// <summary>True while the blanking has the output silent (for the machine to rest).</summary>
        public bool Blanked => blank && zeros >= blankAfter;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            Update(true);
            Reset();
        }

        public void Reset()
        {
            ch[0].Reset();
            ch[1].Reset();
            zeros = 0;
        }

        void Update(bool force = false)
        {
            if (!force && !values.AnyDirty) return;
            int bits = (int)Math.Round(values.Value(P.Bits));
            bool tpdf = values.Choice(P.Dither) == 1;
            var shape = (DitherShape)values.Choice(P.Shaping);
            ch[0].Configure(bits, tpdf, shape);
            ch[1].Configure(bits, tpdf, shape);
            blank = values.On(P.Blank);
            blankAfter = (long)Math.Round(values.Value(P.BlankAfter) * sampleRate);
            values.Clean();
        }

        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update();
            InMeter.Feed(left, right, n);
            for (int i = 0; i < n; i++)
            {
                float l = left[i], r = right[i];
                if (l == 0 && r == 0)
                {
                    if (zeros < long.MaxValue) zeros++;
                }
                else zeros = 0;
                if (blank && zeros >= blankAfter)
                {
                    left[i] = right[i] = 0;
                    ch[0].ClearError();
                    ch[1].ClearError();
                    continue;
                }
                left[i] = ch[0].Process(l);
                right[i] = ch[1].Process(r);
            }
            OutMeter.Feed(left, right, n);
        }
    }
}
