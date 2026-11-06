#nullable disable
// Neo analysis library (docs/specs/mastering-tools.md §6): what the streaming services would do to a master,
// from its integrated loudness and peaks (BS.1770, as they measure). Spotify's rules are its own published
// ones (Spotify for Artists, "Loudness normalization on Spotify", read 2026-11-06): Normal −14, Loud −11,
// Quiet −19 LUFS; a quiet master is raised only as far as its true peak allows, keeping 1 dB of headroom
// for lossy coding (a −20 LUFS master peaking at −5 dBTP plays at −16 LUFS); Loud raises it with a limiter
// that engages at −1 dB (sample values, 5 ms attack, 100 ms decay); masters louder than −14 LUFS should
// peak under −2 dBTP. The others publish no rules: their targets are the observed ones (Apple Music −16
// with Sound Check, YouTube, TIDAL and Amazon Music −14, Deezer −15), modelled as turning loud masters
// down and leaving quiet ones as they are.

using System;
using System.Collections.Generic;

namespace NeoBuzz.Analysis
{
    internal sealed class PlatformPlay
    {
        public string Service, Mode;
        public double TargetLufs;
        /// <summary>The gain the service applies, dB (negative: turned down).</summary>
        public double GainDb;
        /// <summary>The master's loudness as it plays there, LUFS.</summary>
        public double PlaysAtLufs;
        /// <summary>Its true peak there, dBTP (before any limiter of the service's).</summary>
        public double TruePeakDb;
        /// <summary>How far the service's limiter would reduce the loudest peaks, dB (Spotify Loud; 0 elsewhere).</summary>
        public double LimitedDb;
        /// <summary>"published" (the service's own rules) or "observed" (reported targets; no published rules).</summary>
        public string Source;
    }

    internal static class Platform
    {
        /// <summary>Service, mode, target, whether quiet masters are raised, whether a limiter catches what that raises, source.</summary>
        public static readonly (string Service, string Mode, double Target, bool Raises, bool Limiter, string Source)[] Services =
        {
            ("Spotify", "normal", -14, true, false, "published"),
            ("Spotify", "loud", -11, true, true, "published"),
            ("Spotify", "quiet", -19, true, false, "published"),
            ("Apple Music", "Sound Check", -16, false, false, "observed"),
            ("YouTube", "", -14, false, false, "observed"),
            ("TIDAL", "", -14, false, false, "observed"),
            ("Amazon Music", "", -14, false, false, "observed"),
            ("Deezer", "", -15, false, false, "observed"),
        };

        /// <summary>Spotify's headroom kept when it raises a quiet master (dBTP), and where its Loud limiter starts (dBFS).</summary>
        public const double SpotifyCeiling = -1, SpotifyLimiter = -1;

        public static List<PlatformPlay> Preview(double integratedLufs, double truePeakDb, double samplePeakDb)
        {
            var plays = new List<PlatformPlay>();
            if (!double.IsFinite(integratedLufs)) return plays;
            foreach (var (service, mode, target, raises, limiter, source) in Services)
            {
                double gain = target - integratedLufs;
                double limited = 0;
                if (gain > 0)
                {
                    if (!raises) gain = 0;
                    else if (limiter) limited = Math.Max(0, samplePeakDb + gain - SpotifyLimiter);
                    else gain = Math.Max(0, Math.Min(gain, SpotifyCeiling - truePeakDb));
                }
                plays.Add(new PlatformPlay
                {
                    Service = service, Mode = mode, TargetLufs = target, GainDb = gain, PlaysAtLufs = integratedLufs + gain,
                    TruePeakDb = truePeakDb + gain, LimitedDb = limited, Source = source,
                });
            }
            return plays;
        }
    }
}
