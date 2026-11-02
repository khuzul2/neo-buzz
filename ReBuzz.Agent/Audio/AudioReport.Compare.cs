#nullable disable
// Agent server (docs/specs/mcp-server.md §5.7 compare): two sources side by side (two renders,
// a render and a reference, before and after a change). Each measure as {a, b, difference}
// (difference = b − a), the bands also level-matched (b's bands minus a's, less the loudness
// difference), and reads that say what changed in the agent's terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using NeoBuzz.Analysis;

namespace ReBuzz.Agent.Audio
{
    public static partial class AudioReport
    {
        /// <summary>
        /// {names, seconds, loudness, dynamics, stereo, spectrum, reads[]}: per measure
        /// {a, b, difference}, where difference is b − a.
        /// </summary>
        public static JsonObject Compare(StereoAudio a, StereoAudio b, string nameA = "a", string nameB = "b")
        {
            var x = new Analysed(a);
            var y = new Analysed(b);
            var lx = x.LoudnessResult;
            var ly = y.LoudnessResult;
            var dx = x.DynamicsResult;
            var dy = y.DynamicsResult;
            var sx = x.SpectrumResult;
            var sy = y.SpectrumResult;
            var tx = x.StereoResult;
            var ty = y.StereoResult;

            // Level matching: by integrated loudness when both have one, else by the 20 Hz – 20 kHz total.
            bool byLoudness = !double.IsInfinity(lx.Integrated) && !double.IsInfinity(ly.Integrated);
            double offset = byLoudness ? ly.Integrated - lx.Integrated : sy.TotalDb - sx.TotalDb;

            var bands = new JsonObject();
            var matched = new JsonObject();
            var bandDiffs = new List<(NamedBand Band, double Diff)>();
            for (int i = 0; i < Bands.Named.Length; i++)
            {
                var band = Bands.Named[i];
                bands[band.Name] = Pair(sx.NamedDb[i], sy.NamedDb[i], 1);
                double m = sy.NamedDb[i] - sx.NamedDb[i] - offset;
                matched[band.Name] = Num(m, 1);
                // Bands far below either source's loudest say little.
                if (!double.IsNaN(m) && !double.IsInfinity(m) && sx.NamedDb[i] > sx.NamedDb.Max() - 40 && sy.NamedDb[i] > sy.NamedDb.Max() - 40)
                    bandDiffs.Add((band, m));
            }
            var stereoBands = new JsonObject();
            for (int i = 0; i < Bands.Named.Length; i++) stereoBands[Bands.Named[i].Name] = Pair(tx.Bands[i].Width, ty.Bands[i].Width, 2);

            var result = new JsonObject
            {
                ["names"] = new JsonObject { ["a"] = nameA, ["b"] = nameB },
                ["seconds"] = new JsonObject { ["a"] = Math.Round(a.Seconds, 3), ["b"] = Math.Round(b.Seconds, 3) },
                ["loudness"] = new JsonObject
                {
                    ["integrated_lufs"] = Pair(lx.Integrated, ly.Integrated, 1),
                    ["short_term_max_lufs"] = Pair(lx.ShortTermMax, ly.ShortTermMax, 1),
                    ["range_lu"] = Pair(lx.Range, ly.Range, 1),
                    ["true_peak_dbtp"] = Pair(lx.TruePeakDb, ly.TruePeakDb, 2),
                    ["sample_peak_dbfs"] = Pair(lx.SamplePeakDb, ly.SamplePeakDb, 2),
                },
                ["dynamics"] = new JsonObject
                {
                    ["crest_db"] = Pair(dx.CrestDb, dy.CrestDb, 1),
                    ["plr_db"] = Pair(dx.PlrDb, dy.PlrDb, 1),
                    ["rms_dbfs"] = Pair(dx.RmsDb, dy.RmsDb, 1),
                    ["transients_per_second"] = Pair(dx.TransientsPerSecond, dy.TransientsPerSecond, 2),
                },
                ["stereo"] = new JsonObject
                {
                    ["correlation"] = Pair(tx.Correlation, ty.Correlation, 2),
                    ["width"] = Pair(tx.Width, ty.Width, 2),
                    ["mono_loss_db"] = Pair(tx.MonoLossDb, ty.MonoLossDb, 1),
                    ["width_by_band"] = stereoBands,
                },
                ["spectrum"] = new JsonObject
                {
                    ["bands_db"] = bands,
                    ["bands_level_matched_db"] = matched,
                    ["level_matched_by"] = byLoudness ? "integrated loudness" : "total level",
                    ["tilt_db_per_octave"] = Pair(sx.TiltDbPerOctave, sy.TiltDbPerOctave, 2),
                    ["centroid_hz"] = Pair(sx.CentroidHz, sy.CentroidHz, 0),
                },
            };

            var reads = new JsonArray();
            if (x.Silent || y.Silent)
                reads.Add($"{(x.Silent ? nameA : nameB)} is silent (nothing above -90 dBFS): there is nothing to compare.");
            else
            {
                foreach (var line in CompareReads(nameA, nameB, lx, ly, dx, dy, sx, sy, tx, ty, offset, byLoudness, bandDiffs)) reads.Add(line);
            }
            result["reads"] = reads;
            return result;
        }

        /// <summary>{a, b, difference}: dB values as numbers or "-inf", difference null when either isn't finite.</summary>
        static JsonObject Pair(double a, double b, int digits)
        {
            double d = b - a;
            return new JsonObject { ["a"] = Db(a, digits), ["b"] = Db(b, digits), ["difference"] = double.IsInfinity(a) || double.IsInfinity(b) ? null : Num(d, digits) };
        }

        static IEnumerable<string> CompareReads(string an, string bn, LoudnessResult lx, LoudnessResult ly, DynamicsResult dx, DynamicsResult dy,
                                                SpectrumResult sx, SpectrumResult sy, StereoResult tx, StereoResult ty,
                                                double offset, bool byLoudness, List<(NamedBand Band, double Diff)> bandDiffs)
        {
            bool any = false;
            if (byLoudness)
            {
                double d = ly.Integrated - lx.Integrated;
                if (Math.Abs(d) >= 0.5)
                {
                    any = true;
                    yield return $"{bn} is {F(Math.Abs(d))} LU {(d > 0 ? "louder" : "quieter")} than {an} ({F(ly.Integrated)} vs {F(lx.Integrated)} LUFS integrated).";
                }
                else yield return $"Same loudness within 0.5 LU ({F(ly.Integrated)} vs {F(lx.Integrated)} LUFS).";
            }
            else if (Math.Abs(offset) >= 0.5)
            {
                any = true;
                yield return $"{bn} is {F(Math.Abs(offset))} dB {(offset > 0 ? "louder" : "quieter")} than {an} (total level; too short for integrated loudness).";
            }

            // The biggest boosts and the biggest cuts, each named (level-matching moves them all one way otherwise).
            var more = bandDiffs.Where(t => t.Diff >= 1.5).OrderByDescending(t => t.Diff).Take(2).ToList();
            var less = bandDiffs.Where(t => t.Diff <= -1.5).OrderBy(t => t.Diff).Take(2).ToList();
            string Band((NamedBand Band, double Diff) t) => $"{t.Band.Name} ({t.Band.Lo:0}–{t.Band.Hi:0} Hz, {Signed(t.Diff)} dB)";
            if (more.Count + less.Count > 0)
            {
                any = true;
                var parts = new List<string>();
                if (more.Count > 0) parts.Add("more " + string.Join(" and ", more.Select(Band)));
                if (less.Count > 0) parts.Add("less " + string.Join(" and ", less.Select(Band)));
                yield return $"Level-matched ({(byLoudness ? "by loudness" : "by level")}), {bn} has " + string.Join("; ", parts) + $" than {an}.";
            }
            double dt = sy.TiltDbPerOctave - sx.TiltDbPerOctave;
            if (Math.Abs(dt) >= 0.3 && FullRange(sx) && FullRange(sy))
            {
                any = true;
                yield return $"{bn} is {(dt > 0 ? "brighter" : "darker")} overall: tilt {Signed(sy.TiltDbPerOctave, 2)} vs {Signed(sx.TiltDbPerOctave, 2)} dB/oct.";
            }

            if (!double.IsNaN(dx.PlrDb) && !double.IsNaN(dy.PlrDb) && Math.Abs(dy.PlrDb - dx.PlrDb) >= 1)
            {
                any = true;
                yield return $"{bn} is {(dy.PlrDb < dx.PlrDb ? "more compressed" : "more dynamic")}: PLR {F(dy.PlrDb)} vs {F(dx.PlrDb)} dB.";
            }
            else if (Math.Abs(dy.CrestDb - dx.CrestDb) >= 1.5)
            {
                any = true;
                yield return $"{bn} {(dy.CrestDb < dx.CrestDb ? "has flatter peaks" : "has sharper peaks")}: crest factor {F(dy.CrestDb)} vs {F(dx.CrestDb)} dB.";
            }
            if (ly.TruePeakDb > -1 && lx.TruePeakDb <= -1)
            {
                any = true;
                yield return $"{bn} peaks at {Signed(ly.TruePeakDb)} dBTP, over the -1 dBTP ceiling {an} kept ({Signed(lx.TruePeakDb)}).";
            }

            if (!tx.Mono || !ty.Mono)
            {
                double wx = tx.Width, wy = ty.Width;
                bool finite = !double.IsInfinity(wx) && !double.IsInfinity(wy);
                if (finite && Math.Abs(wy - wx) >= Math.Max(0.05, 0.25 * Math.Min(wx, wy)))
                {
                    any = true;
                    yield return $"{bn} is {(wy > wx ? "wider" : "narrower")}: side/mid {F(wy, 2)} vs {F(wx, 2)}, correlation {F(ty.Correlation, 2)} vs {F(tx.Correlation, 2)}.";
                }
                else if (Math.Abs(ty.Correlation - tx.Correlation) >= 0.2)
                {
                    any = true;
                    yield return $"Correlation {F(ty.Correlation, 2)} in {bn} vs {F(tx.Correlation, 2)} in {an}.";
                }
                if (ty.MonoLossDb < -3 && ty.MonoLossDb < tx.MonoLossDb - 1)
                {
                    any = true;
                    yield return $"{bn} loses {F(-ty.MonoLossDb)} dB in mono ({an}: {F(-tx.MonoLossDb)} dB).";
                }
            }
            if (!any) yield return $"No measurable difference: loudness within 0.5 LU, bands within 1.5 dB, dynamics and stereo alike.";
        }
    }
}
