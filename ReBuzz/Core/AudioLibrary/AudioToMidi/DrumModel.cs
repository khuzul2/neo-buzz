#nullable disable
// Fork (docs/specs/audio-to-midi.md, Convert to MIDI drums CD1): the drum classifier's forward pass. A small multi-label network (one hidden layer
// of ReLU units, a sigmoid per class: kick, snare, closed hat, open hat, tom, cymbal) over the features of DrumFeatures. The coefficients are in
// DrumModelData.cs: fitted by tools/convert/train_drums.py on the TRAIN split of the Groove MIDI Dataset (Gillick et al., CC BY 4.0), the
// per-class thresholds on its VALIDATION split; the test split is only ever scored. A few thousand numbers, no audio.

using System;

namespace ReBuzz.Core.AudioLibrary.AudioToMidi
{
    internal static class DrumModel
    {
        public const int Classes = 6;
        public static readonly string[] Names = { "kick", "snare", "closed hat", "open hat", "tom", "cymbal" };

        /// <summary>Probability of each class for one feature vector (the mean of the members of the ensemble).</summary>
        public static void Predict(float[] f, float[] p)
        {
            Array.Clear(p, 0, Classes);
            int inN = DrumModelData.Mean.Length;
            var z = new float[inN];
            for (int i = 0; i < inN; i++) z[i] = (f[i] - DrumModelData.Mean[i]) / DrumModelData.Sd[i];
            int members = DrumModelData.Hidden.Length;
            for (int m = 0; m < members; m++)
            {
                int hid = DrumModelData.Hidden[m];
                var w1 = DrumModelData.W1[m]; var b1 = DrumModelData.B1[m]; var w2 = DrumModelData.W2[m]; var b2 = DrumModelData.B2[m];
                var o = new float[Classes];
                for (int c = 0; c < Classes; c++) o[c] = b2[c];
                for (int h = 0; h < hid; h++)
                {
                    float s = b1[h];
                    int row = h * inN;
                    for (int i = 0; i < inN; i++) s += w1[row + i] * z[i];
                    if (s <= 0) continue;
                    for (int c = 0; c < Classes; c++) o[c] += w2[c * hid + h] * s;
                }
                for (int c = 0; c < Classes; c++) p[c] += (float)(1.0 / (1.0 + Math.Exp(-o[c]))) / members;
            }
        }
    }
}
