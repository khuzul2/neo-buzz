#nullable disable
// Agent server (docs/specs/mastering-tools.md §6): the mastering measures analyze adds, and the comparison
// with a target. microdynamics (LDR in Ozone's four bands), tonal (the long-term 1/6-octave curve and,
// with a target, what an EQ would have to do to match it), platform (what each streaming service would
// do to the master), and the side/mid ratio in the four bands (stereo's ms_bands). With a target (a
// reference measured on the fly, or a style target from target_build), each says where the master
// stands against it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using NeoBuzz.Analysis;

namespace ReBuzz.Agent.Audio
{
    public static partial class AudioReport
    {
        // ---- Microdynamics -----------------------------------------------------------------

        static JsonObject MicrodynamicsJson(MicrodynamicsResult m, StyleTarget target)
        {
            var bands = new JsonObject();
            for (int b = 0; b < m.Bands.Length; b++)
            {
                var band = m.Bands[b];
                var o = new JsonObject
                {
                    ["lo_hz"] = band.Lo,
                    ["hi_hz"] = double.IsInfinity(band.Hi) ? null : JsonValue.Create(band.Hi),
                    ["ldr_db"] = Num(band.LdrDb, 1),
                    ["median_db"] = Num(band.MedianDb, 1),
                    ["level_db"] = Db(band.LevelDb),
                };
                if (target != null && double.IsFinite(target.LdrDb[b].Mean) && double.IsFinite(band.LdrDb))
                {
                    o["target_ldr_db"] = Num(target.LdrDb[b].Mean, 1);
                    o["diff_db"] = Num(band.LdrDb - target.LdrDb[b].Mean, 1);
                }
                bands[band.Name] = o;
            }
            return new JsonObject
            {
                ["ldr_db"] = Num(m.LdrDb, 1),
                ["median_db"] = Num(m.MedianDb, 1),
                ["bands"] = bands,
                ["units"] = $"LDR = the {Microdynamics.Percentile * 100:0}th percentile of the {Microdynamics.ShortSeconds * 1000:0} ms level over the level of the {Microdynamics.LongSeconds:0} s around it, dB, where the band sounds",
            };
        }

        static IEnumerable<string> MicrodynamicsReads(MicrodynamicsResult m, StyleTarget target)
        {
            string Band(MicrodynamicsBand b) => b.Name.Replace("_", "–").Replace("below–", "below ").Replace("above–", "above ");
            var sounding = m.Bands.Where(b => double.IsFinite(b.LdrDb)).ToList();
            if (sounding.Count == 0) yield break;
            yield return "Microdynamics (LDR, how far 20 ms peaks rise over the second around them): " +
                         string.Join(", ", sounding.Select(b => $"{Band(b)} Hz {F(b.LdrDb)} dB")) + $"; whole band {F(m.LdrDb)} dB.";
            if (target == null) yield break;
            var off = sounding.Select((b, i) => (b, d: b.LdrDb - target.LdrDb[Array.IndexOf(m.Bands, b)].Mean))
                              .Where(x => double.IsFinite(x.d) && Math.Abs(x.d) >= 1.5).ToList();
            if (off.Count == 0) yield return $"Microdynamics match {target.Name} within 1.5 dB in every band.";
            else
                yield return $"Against {target.Name}: " + string.Join(", ", off.Select(x => $"{Band(x.b)} Hz {Signed(x.d)} dB")) +
                             " (negative: flatter than the target, more limited or compressed there; positive: punchier). Less limiting, or a slower attack on a compressor (Neo Comp, a Neo Multiband band), raises it; more of either lowers it.";
        }

        // ---- The side/mid ratio in Ozone's four bands --------------------------------------

        static JsonObject MsBandsJson(StereoResult s, StyleTarget target)
        {
            var o = new JsonObject();
            for (int b = 0; b < s.MsBands.Length; b++)
            {
                var band = s.MsBands[b];
                double db = Spectrum.Db(band.Width);
                var x = new JsonObject
                {
                    ["side_to_mid_db"] = Db(db),
                    ["width"] = Db(band.Width, 2),
                    ["correlation"] = Num(band.Correlation, 2),
                    ["level_db"] = Db(band.LevelDb),
                };
                if (target != null && double.IsFinite(target.SideToMidDb[b].Mean) && double.IsFinite(db))
                {
                    x["target_side_to_mid_db"] = Num(target.SideToMidDb[b].Mean, 1);
                    x["diff_db"] = Num(db - target.SideToMidDb[b].Mean, 1);
                }
                o[band.Name] = x;
            }
            return o;
        }

        static IEnumerable<string> MsBandsReads(StereoResult s, StyleTarget target)
        {
            if (s.Mono || target == null) yield break;
            var off = new List<string>();
            for (int b = 0; b < s.MsBands.Length; b++)
            {
                double db = Spectrum.Db(s.MsBands[b].Width), t = target.SideToMidDb[b].Mean;
                if (double.IsFinite(db) && double.IsFinite(t) && Math.Abs(db - t) >= 2)
                    off.Add($"{s.MsBands[b].Name.Replace("_", "–")} Hz {Signed(db - t)} dB");
            }
            if (off.Count == 0) yield return $"Width per band matches {target.Name} within 2 dB of side/mid.";
            else yield return $"Side/mid against {target.Name}: " + string.Join(", ", off) +
                              " (positive: wider than the target). Neo Utility's Width moves the side as a whole and its Bass mono takes it out below a frequency; Neo Multiband in M/S (Stereo M/S, Link 0 %) moves it band by band. Never widen the lows.";
        }

        // ---- Tonal balance -----------------------------------------------------------------

        static JsonObject TonalJson(TonalCurve c, StyleTarget target, TonalDifference d)
        {
            var o = new JsonObject
            {
                ["resolution"] = "1/6 octave",
                ["hz"] = new JsonArray(c.Hz.Select(h => (JsonNode)Math.Round(h, 1)).ToArray()),
                ["db"] = new JsonArray(c.Db.Select(x => Db(x)).ToArray()),
            };
            if (target != null && d != null)
            {
                var worst = Worst(d, 5);
                o["target"] = new JsonObject
                {
                    ["name"] = target.Name,
                    ["about"] = "diff = target − this, after moving the target to this one's level (mean from 100 Hz to 10 kHz): positive wants a boost",
                    ["offset_db"] = Num(d.OffsetDb, 1),
                    ["rms_db"] = Num(d.RmsDb, 2),
                    ["diff_db"] = new JsonArray(d.Db.Select(x => Num(x, 1)).ToArray()),
                    ["diff_third_octave_db"] = new JsonArray(d.SmoothedDb.Select(x => Num(x, 1)).ToArray()),
                    ["most_off"] = new JsonArray(worst.Select(w => (JsonNode)new JsonObject { ["hz"] = Math.Round(w.Hz), ["diff_db"] = Num(w.Db, 1) }).ToArray()),
                };
            }
            return o;
        }

        /// <summary>The largest deviations of the smoothed difference, each a local extreme at least two thirds of an octave from the others.</summary>
        internal static List<(double Hz, double Db)> Worst(TonalDifference d, int count)
        {
            var picks = new List<(double Hz, double Db)>();
            var order = Enumerable.Range(0, d.Hz.Length)
                .Where(i => double.IsFinite(d.SmoothedDb[i]) && d.Hz[i] >= Tonal.RmsLo && d.Hz[i] <= Tonal.RmsHi)
                .OrderByDescending(i => Math.Abs(d.SmoothedDb[i]));
            foreach (int i in order)
            {
                if (picks.Count >= count) break;
                if (picks.Any(p => Math.Abs(Math.Log2(p.Hz / d.Hz[i])) < 2.0 / 3)) continue;
                if (Math.Abs(d.SmoothedDb[i]) < 0.5) break;
                picks.Add((d.Hz[i], d.SmoothedDb[i]));
            }
            return picks;
        }

        static IEnumerable<string> TonalReads(TonalCurve c, StyleTarget target, TonalDifference d)
        {
            if (target == null || d == null) yield break;
            string fit = d.RmsDb < 1 ? "a close match" : d.RmsDb < 2 ? "near" : d.RmsDb < 3.5 ? "audibly different" : "far apart";
            var worst = Worst(d, 3);
            yield return $"Tonal balance against {target.Name}: {F(d.RmsDb, 1)} dB RMS from 50 Hz to 16 kHz after level matching ({fit})" +
                         (worst.Count > 0 ? "; most off: " + string.Join(", ", worst.Select(w => $"{Signed(w.Db)} dB wanted at {HzText(w.Hz)}")) : "") +
                         ". eq_fit turns this into a few Neo EQ bands.";
        }

        internal static string HzText(double hz) => hz >= 1000 ? $"{F(hz / 1000, hz >= 10000 ? 0 : 1)} kHz" : $"{F(hz, 0)} Hz";

        // ---- Streaming platforms -----------------------------------------------------------

        static JsonObject PlatformJson(LoudnessResult r)
        {
            var plays = Platform.Preview(r.Integrated, r.TruePeakDb, r.SamplePeakDb);
            return new JsonObject
            {
                ["about"] = "What each service does to this master by its integrated loudness: Spotify's published rules; the others' observed targets, turning loud masters down only",
                ["services"] = new JsonArray(plays.Select(p => (JsonNode)new JsonObject
                {
                    ["service"] = p.Service,
                    ["mode"] = p.Mode.Length > 0 ? p.Mode : null,
                    ["target_lufs"] = p.TargetLufs,
                    ["gain_db"] = Num(p.GainDb, 1),
                    ["plays_at_lufs"] = Num(p.PlaysAtLufs, 1),
                    ["true_peak_dbtp"] = Num(p.TruePeakDb, 1),
                    ["limited_db"] = p.LimitedDb > 0 ? Num(p.LimitedDb, 1) : null,
                    ["source"] = p.Source,
                }).ToArray()),
            };
        }

        static IEnumerable<string> PlatformReads(LoudnessResult r)
        {
            var plays = Platform.Preview(r.Integrated, r.TruePeakDb, r.SamplePeakDb);
            if (plays.Count == 0) yield break;
            var normal = plays.First(p => p.Service == "Spotify" && p.Mode == "normal");
            var loud = plays.First(p => p.Service == "Spotify" && p.Mode == "loud");
            if (normal.GainDb < -0.5)
                yield return $"On Spotify (normal) it plays {F(-normal.GainDb)} dB quieter, at -14 LUFS; YouTube, TIDAL and Amazon turn it down about as much, Apple Music to -16. " +
                             "Loudness above -14 LUFS buys nothing there and costs dynamics" + (r.TruePeakDb > -2 ? "; keep the true peak under -2 dBTP at this loudness (Spotify's advice)." : ".");
            else if (normal.GainDb > 0.5)
                yield return $"On Spotify (normal) it is raised {F(normal.GainDb)} dB, to {F(normal.PlaysAtLufs)} LUFS" +
                             (normal.PlaysAtLufs < -14.4 ? $": its true peak ({F(r.TruePeakDb)} dBTP) stops it short of -14 (Spotify keeps 1 dB of headroom)." : ".") +
                             " The services that only turn down play it as it is, quieter than louder masters.";
            else yield return "On Spotify (normal) it plays at about its own level (-14 LUFS).";
            if (loud.LimitedDb > 0.5)
                yield return $"Spotify's Loud setting (-11 LUFS) raises it {F(loud.GainDb)} dB and its limiter (from -1 dBFS) takes about {F(loud.LimitedDb)} dB off the peaks.";
        }

        // ---- Against a target: loudness and dynamics ---------------------------------------

        static IEnumerable<string> TargetLevelReads(LoudnessResult r, DynamicsResult d, StyleTarget t)
        {
            if (t == null) yield break;
            if (double.IsFinite(t.Lufs.Mean) && double.IsFinite(r.Integrated))
                yield return $"Loudness {F(r.Integrated)} LUFS against {t.Name}'s {F(t.Lufs.Mean)}" +
                             (t.Sources.Count > 1 ? $" ({F(t.Lufs.Min)} to {F(t.Lufs.Max)})" : "") + $": {Signed(r.Integrated - t.Lufs.Mean)} LU.";
            if (d != null && double.IsFinite(t.PlrDb.Mean) && double.IsFinite(d.PlrDb))
            {
                double lo = Math.Min(t.PlrDb.Min, t.PlrDb.Mean - 1), hi = Math.Max(t.PlrDb.Max, t.PlrDb.Mean + 1);
                string where = d.PlrDb < lo ? "denser than the target (more limited)" : d.PlrDb > hi ? "more dynamic than the target" : "within the target's range";
                yield return $"PLR {F(d.PlrDb)} dB against {F(t.PlrDb.Mean)} ({F(lo)} to {F(hi)}): {where}.";
            }
        }

        static JsonObject TargetJson(StyleTarget t) => new()
        {
            ["name"] = t.Name,
            ["sources"] = new JsonArray(t.Sources.Select(s => (JsonNode)s.Name).ToArray()),
            ["integrated_lufs"] = Num(t.Lufs.Mean, 1),
            ["plr_db"] = Num(t.PlrDb.Mean, 1),
            ["range_lu"] = Num(t.RangeLu.Mean, 1),
        };
    }
}
