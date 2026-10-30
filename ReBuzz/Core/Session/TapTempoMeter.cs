using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Tap tempo (0.6): the tempo of the last taps. From the third tap (two intervals) on, each
    /// tap gives the average of the last four intervals, rounded to Buzz's whole BPM; a pause
    /// of over two seconds starts again.
    /// </summary>
    internal sealed class TapTempoMeter
    {
        const double ResetSeconds = 2.0;
        const int Intervals = 4;

        readonly List<double> taps = new List<double>();    // seconds

        /// <summary>A tap at <paramref name="seconds"/> (any clock). Returns the tempo once there is one.</summary>
        public int? Tap(double seconds)
        {
            if (taps.Count > 0 && (seconds - taps[^1] > ResetSeconds || seconds <= taps[^1]))
                taps.Clear();
            taps.Add(seconds);
            if (taps.Count > Intervals + 1)
                taps.RemoveAt(0);
            if (taps.Count < 3) return null;
            double average = (taps[^1] - taps[0]) / (taps.Count - 1);
            return Math.Clamp((int)Math.Round(60.0 / average), 16, 500);
        }

        public void Reset() => taps.Clear();
    }
}
