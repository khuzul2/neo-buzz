#nullable disable
// Neo Audio to MIDI (docs/specs/audio-to-midi.md §4): the drum part. A hit (the shared onset detector) is named kick, snare or hat.
//  - Out of the box, by rule: four band filters run on the signal (under 130 Hz, 130-450 Hz, 450 Hz-3 kHz, over 5 kHz) and their energies are kept
//    per millisecond. When a hit's 14 ms are in, each band's energy then is compared with the 14 ms before it: the bands the hit ADDED say which drums
//    are in it (the idea of the offline converter, ConvertToMidi's drum rules, retuned to a window short enough to play live): a kick adds to the low
//    band, a snare adds a broad mid-range body with noise above it, a hat adds noise over 5 kHz and little under 450 Hz. A hit can be several drums
//    at once (a kick with a hat).
//  - Calibrated: five hits of each drum, filed by the trainer Neo Voice to MIDI uses (VoiceFeatures and VoiceClassifier), are named by the
//    nearest trained sound instead; a hit none of them fits falls back to the rules.
//  - Each class has its own sensitivity and minimum interval (so hats can run fast while a kick's tail is not a second kick).
// Allocation-free.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.AudioToMidi
{
    /// <summary>What one hit added in each band, and what became of it.</summary>
    internal struct HitBands
    {
        public const int Bands = 4;
        /// <summary>Each band's share of what the hit added (0 to 1; they sum to 1).</summary>
        public float S0, S1, S2, S3;
        /// <summary>Each band's rise over the 14 ms before, dB.</summary>
        public float R0, R1, R2, R3;
        /// <summary>The hit's whole rise, dB, and the level of the 14 ms, dBFS.</summary>
        public float Rise, LevelDb;
    }

    internal sealed class DrumHits
    {
        public const int Classes = 3;
        public const int Kick = 0, Snare = 1, Hat = 2;
        public static readonly string[] Names = { "Kick", "Snare", "Hat" };
        /// <summary>The slice of each hit the rules read, ms (also the delay of a hit's note on, with the detector's lead).</summary>
        public const double WindowMs = 14;

        readonly int rate;
        readonly int blk, nBlocks, window;
        readonly float[] energy;          // per block: four bands and the whole
        long blocksDone;
        Biquad lp130, hp130, lp450, hp450, lp3k, hp5k;
        double[] acc = new double[5];
        int accCount;
        long samples;

        // Rule thresholds (docs/specs/audio-to-midi.md §4.2): the share each class needs of what the hit added, and the rise.
        public float KickShare = 0.25f, KickRise = 8f;
        public float SnareShare = 0.6f, SnareRise = 10f;
        public float HatShare = 0.02f, HatRise = 3f, HatBodyMax = 1f;

        public DrumHits(int sampleRate)
        {
            rate = sampleRate;
            blk = Math.Max(8, sampleRate / 1000);
            window = (int)Math.Round(WindowMs);
            nBlocks = 256;
            energy = new float[nBlocks * 5];
            lp130.Set(BiquadType.LowPass, 130, sampleRate, 0.7071); hp130.Set(BiquadType.HighPass, 130, sampleRate, 0.7071);
            lp450.Set(BiquadType.LowPass, 450, sampleRate, 0.7071); hp450.Set(BiquadType.HighPass, 450, sampleRate, 0.7071);
            lp3k.Set(BiquadType.LowPass, 3000, sampleRate, 0.7071); hp5k.Set(BiquadType.HighPass, 5000, sampleRate, 0.7071);
        }

        public void Reset()
        {
            lp130.Reset(); hp130.Reset(); lp450.Reset(); hp450.Reset(); lp3k.Reset(); hp5k.Reset();
            Array.Clear(energy, 0, energy.Length); Array.Clear(acc, 0, acc.Length);
            accCount = 0; blocksDone = 0; samples = 0;
        }

        /// <summary>Runs the band filters over a block (call for every block, in order).</summary>
        public void Push(float[] x, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float v = x[i];
                double b0 = lp130.Process(v);
                double b1 = lp450.Process(hp130.Process(v));
                double b2 = lp3k.Process(hp450.Process(v));
                double b3 = hp5k.Process(v);
                acc[0] += b0 * b0; acc[1] += b1 * b1; acc[2] += b2 * b2; acc[3] += b3 * b3; acc[4] += v * v;
                samples++;
                if (++accCount >= blk)
                {
                    if ((blocksDone & 63) == 0) { lp130.Flush(); hp130.Flush(); lp450.Flush(); hp450.Flush(); lp3k.Flush(); hp5k.Flush(); }
                    int at = (int)(blocksDone % nBlocks) * 5;
                    for (int k = 0; k < 5; k++) { energy[at + k] = (float)acc[k]; acc[k] = 0; }
                    accCount = 0; blocksDone++;
                }
            }
        }

        /// <summary>Whether the blocks a hit at <paramref name="sample"/> needs are all in.</summary>
        public bool Ready(long sample) => FirstBlock(sample) + window <= blocksDone;

        long FirstBlock(long sample) => (sample - (long)(0.001 * rate)) / blk;

        /// <summary>The bands a hit added (see <see cref="HitBands"/>), from the blocks around <paramref name="sample"/>.</summary>
        public HitBands Analyse(long sample)
        {
            long b0 = FirstBlock(sample);
            Span<double> after = stackalloc double[5], before = stackalloc double[5];
            for (int w = 0; w < window; w++)
            {
                long ba = b0 + w, bb = b0 - 1 - w;
                // A block that has been overwritten (a hit older than the ring) or lies before the start counts as silence.
                bool okA = ba >= 0 && blocksDone - ba <= nBlocks, okB = bb >= 0 && blocksDone - bb <= nBlocks;
                for (int k = 0; k < 5; k++)
                {
                    if (okA) after[k] += energy[(int)(ba % nBlocks) * 5 + k];
                    if (okB) before[k] += energy[(int)(bb % nBlocks) * 5 + k];
                }
            }
            double eps = 1e-10 * window * blk;
            var h = new HitBands();
            Span<double> add = stackalloc double[4];
            double total = 1e-30;
            Span<float> rise = stackalloc float[4];
            for (int b = 0; b < 4; b++)
            {
                add[b] = Math.Max(after[b] - 0.5 * before[b], 0.02 * after[b]);
                total += add[b];
                rise[b] = (float)(10.0 * PortableMath.Log10((after[b] + eps) / (before[b] + eps)));
            }
            h.S0 = (float)(add[0] / total); h.S1 = (float)(add[1] / total); h.S2 = (float)(add[2] / total); h.S3 = (float)(add[3] / total);
            h.R0 = rise[0]; h.R1 = rise[1]; h.R2 = rise[2]; h.R3 = rise[3];
            h.Rise = (float)(10.0 * PortableMath.Log10((after[4] + eps) / (before[4] + eps)));
            h.LevelDb = (float)(10.0 * PortableMath.Log10(after[4] / (window * blk) + 1e-14));
            return h;
        }

        /// <summary>The drums in a hit by rule: a bit mask (1 kick, 2 snare, 4 hat). <paramref name="sens"/> (0 to 1 per class) lowers the thresholds as it rises.</summary>
        public int Rules(in HitBands h, float sK, float sS, float sH)
        {
            int mask = 0;
            float kk = 1.5f - sK, ks = 1.5f - sS, kh = 1.5f - sH;      // 1 at the middle setting
            float body = h.S1 + h.S2;
            bool kick = h.S0 >= KickShare * kk && h.R0 >= KickRise * kk;
            bool hat = h.S3 >= HatShare * kh && h.R3 >= HatRise * kh && h.S1 <= HatBodyMax / kh;
            bool snare = body >= SnareShare * ks && Math.Max(h.R1, h.R2) >= SnareRise * ks;
            if (kick) mask |= 1;
            if (snare) mask |= 2;
            if (hat) mask |= 4;
            return mask;
        }
    }
}
