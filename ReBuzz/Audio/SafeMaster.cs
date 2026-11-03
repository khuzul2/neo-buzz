// Fork (docs/specs/machine-safety.md §3.3): the safe master. On what goes to the audio device only
// (CommonAudioProvider.Read; renders and exports call WorkManager.MainAudioFillBuffer and never
// come here, so they keep the true signal): NaN and infinity become 0 first, then a zero-latency
// peak limiter (instant attack, about 50 ms release, then a hard ceiling) at the ceiling, 0 dBFS by
// default. Below the ceiling it does nothing: the samples pass bit for bit. The clip indicator
// latches the highest peak before the limiter that went over the clip threshold (0 dBFS by
// default) until it is cleared, as Ableton's master meter does. No allocation on the audio thread.

using ReBuzz.Core;
using System;
using System.Threading;

namespace ReBuzz.Audio
{
    internal sealed class SafeMaster
    {
        /// <summary>The registry section its preferences are saved in (tenths of a dB).</summary>
        public const string Section = "Settings";
        const string LimiterKey = "SafeMasterLimiter", CeilingKey = "SafeMasterCeiling", ThresholdKey = "ClipThreshold";

        public const double MinCeilingDb = -24, MaxCeilingDb = 0;
        public const double MinThresholdDb = -24, MaxThresholdDb = 24;
        const float ReleaseSeconds = 0.05f;

        volatile bool limiterOn = true;
        float ceiling = 1f, clipThreshold = 1f;
        double ceilingDb, clipThresholdDb;

        // Audio thread. The limiter keeps its gain reduction (1 − gain): near unity a gain itself
        // couldn't hold the release's tiny steps in a float.
        float reduction;
        float releaseCoef;
        int releaseRate;

        // From the audio thread to the views (whole floats and longs: no torn values on 64-bit).
        float latched;              // highest pre-limiter peak over the threshold since cleared; 0: none
        float peakL, peakR;         // pre-limiter peaks since TakePeaks
        float lowestGain = 1f;      // the limiter's lowest gain since TakeGain
        long limitedFrames;         // frames over the ceiling the limiter caught, since ReBuzz started (not its release)
        long scrubbed;              // NaN and infinite samples replaced, since ReBuzz started
        long limitedAtClear;

        /// <summary>The safety limiter (on by default; off asks for confirmation in the preferences).</summary>
        public bool LimiterOn
        {
            get => limiterOn;
            set => limiterOn = value;
        }

        /// <summary>The limiter's ceiling in dBFS (−24 to 0, default 0).</summary>
        public double CeilingDb
        {
            get => ceilingDb;
            set
            {
                ceilingDb = Math.Clamp(Math.Round(value, 1), MinCeilingDb, MaxCeilingDb);
                ceiling = (float)Math.Pow(10, ceilingDb / 20);
            }
        }

        /// <summary>The clip indicator's threshold in dBFS (−24 to +24, default 0).</summary>
        public double ClipThresholdDb
        {
            get => clipThresholdDb;
            set
            {
                clipThresholdDb = Math.Clamp(Math.Round(value, 1), MinThresholdDb, MaxThresholdDb);
                clipThreshold = (float)Math.Pow(10, clipThresholdDb / 20);
            }
        }

        /// <summary>The settings as saved (missing ones at their defaults).</summary>
        public void Load(IRegistryEx registry)
        {
            if (registry == null) return;
            LimiterOn = registry.Read(LimiterKey, 1, Section) != 0;
            CeilingDb = registry.Read(CeilingKey, 0, Section) / 10.0;
            ClipThresholdDb = registry.Read(ThresholdKey, 0, Section) / 10.0;
        }

        public void Save(IRegistryEx registry)
        {
            if (registry == null) return;
            registry.Write(LimiterKey, LimiterOn ? 1 : 0, Section);
            registry.Write(CeilingKey, (int)Math.Round(CeilingDb * 10), Section);
            registry.Write(ThresholdKey, (int)Math.Round(ClipThresholdDb * 10), Section);
        }

        /// <summary>
        /// Audio thread: the device's interleaved output (1.0 = 0 dBFS), in place. One pass scrubs
        /// and measures; the limiter's pass runs only while something is over the ceiling or the
        /// gain is still coming back.
        /// </summary>
        // Fork (audio hygiene): compiled optimized at once (by the warm-up), not first unoptimized
        // and then again, optimized, by the audio thread in the middle of its loop (OSR).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public void Process(Span<float> buffer, int channels, int sampleRate)
        {
            if (channels < 1) return;
            int frames = buffer.Length / channels;
            if (frames == 0) return;

            float pl = peakL, pr = peakR, block = 0;
            for (int f = 0, i = 0; f < frames; f++)
            {
                for (int c = 0; c < channels; c++, i++)
                {
                    float x = buffer[i];
                    if (!float.IsFinite(x))
                    {
                        buffer[i] = x = 0;
                        scrubbed++;
                    }
                    float a = MathF.Abs(x);
                    if (a > block) block = a;
                    if (c == 0) { if (a > pl) pl = a; }
                    else if (c == 1 && a > pr) pr = a;
                }
            }
            peakL = pl;
            peakR = channels > 1 ? pr : pl;
            if (block > clipThreshold && block > latched) latched = block;

            if (!limiterOn)
            {
                reduction = 0;
                return;
            }
            float ceil = ceiling;
            if (block <= ceil && reduction == 0) return;    // nothing to do: untouched

            if (sampleRate != releaseRate && sampleRate > 0)
            {
                releaseRate = sampleRate;
                releaseCoef = MathF.Exp(-1f / (ReleaseSeconds * sampleRate));
            }
            float rc = releaseCoef, r = reduction, low = lowestGain;
            long limited = 0;
            for (int f = 0, i = 0; f < frames; f++, i += channels)
            {
                float peak = 0;
                for (int c = 0; c < channels; c++) peak = MathF.Max(peak, MathF.Abs(buffer[i + c]));
                r *= rc;                                            // release towards unity
                if (peak * (1f - r) > ceil) r = 1f - ceil / peak;   // instant attack
                if (r < 1e-6f) r = 0;                               // back at unity exactly: untouched again
                if (peak > ceil) limited++;
                if (r > 0)
                {
                    float g = 1f - r;
                    if (g < low) low = g;
                    for (int c = 0; c < channels; c++)
                        buffer[i + c] = Math.Clamp(buffer[i + c] * g, -ceil, ceil);    // the hard ceiling
                }
            }
            reduction = r;
            lowestGain = low;
            if (limited > 0) limitedFrames += limited;
        }

        /// <summary>The latched over in dBFS (the highest peak over the clip threshold), or null.</summary>
        public double? LatchedOverDb
        {
            get
            {
                float l = Volatile.Read(ref latched);
                return l > 0 ? Math.Round(20 * Math.Log10(l), 1) : null;
            }
        }

        /// <summary>Whether the limiter caught a peak over its ceiling since the clip indicator was last cleared.</summary>
        public bool LimitedSinceClear => Interlocked.Read(ref limitedFrames) > Interlocked.Read(ref limitedAtClear);

        /// <summary>Frames over the ceiling the limiter caught since ReBuzz started (a reader compares two reads).</summary>
        public long LimitedFrames => Interlocked.Read(ref limitedFrames);

        /// <summary>NaN and infinite samples it replaced with 0 since ReBuzz started.</summary>
        public long Scrubbed => Interlocked.Read(ref scrubbed);

        /// <summary>Clears the clip indicator (a click on it).</summary>
        public void ClearOver()
        {
            Volatile.Write(ref latched, 0f);
            Interlocked.Exchange(ref limitedAtClear, Interlocked.Read(ref limitedFrames));
        }

        /// <summary>The pre-limiter peaks (1.0 = 0 dBFS) since the last call, for meters; resets them.</summary>
        public (float Left, float Right) TakePeaks() => (Interlocked.Exchange(ref peakL, 0f), Interlocked.Exchange(ref peakR, 0f));

        /// <summary>The limiter's lowest gain since the last call (1: it did nothing), for the limiting lamp.</summary>
        public float TakeGain() => Interlocked.Exchange(ref lowestGain, 1f);

        /// <summary>Back to unity with nothing latched (a new device; tests).</summary>
        public void Reset()
        {
            reduction = 0;
            lowestGain = 1f;
            peakL = peakR = 0;
            ClearOver();
        }
    }
}
