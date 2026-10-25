// Interpolation.cs — sample-position interpolation for PCM playback.
//
// Two methods supported in v1: Linear (cheap, audible alias on big detune)
// and Cubic Catmull-Rom (good general-purpose, matches Pedal Tracker's
// default). MAX_PAD samples of head/tail padding are required on snapshots
// so cubic can read pos-1 / pos+2 without bounds checks.

using System;

namespace WDE.PedalM1
{
    public enum InterpolationMode
    {
        Linear = 0,
        Cubic  = 1,
    }

    public static class Interpolation
    {
        public const int MAX_PAD = 4;   // worst-case head/tail padding

        // pos is the absolute position into data[] (already pad-offset).
        // Caller guarantees pos-1 and pos+2 are valid indices for cubic.
        public static float Linear(float[] data, double pos)
        {
            int   i  = (int)pos;
            float f  = (float)(pos - i);
            float a  = data[i];
            float b  = data[i + 1];
            return a + (b - a) * f;
        }

        // Catmull-Rom cubic. Standard form, four taps a, b, c, d at i-1..i+2.
        public static float Cubic(float[] data, double pos)
        {
            int   i  = (int)pos;
            float f  = (float)(pos - i);
            float a  = data[i - 1];
            float b  = data[i    ];
            float c  = data[i + 1];
            float d  = data[i + 2];

            float c0 = b;
            float c1 = 0.5f * (c - a);
            float c2 = a - 2.5f * b + 2f * c - 0.5f * d;
            float c3 = 0.5f * (d - a) + 1.5f * (b - c);

            return ((c3 * f + c2) * f + c1) * f + c0;
        }
    }
}
