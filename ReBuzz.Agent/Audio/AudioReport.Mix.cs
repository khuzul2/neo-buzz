#nullable disable
// Agent server (docs/specs/mcp-server.md §5.7 analyze_mix): one render with a probe on every
// track feeding the mix. Per track its loudness, peaks, bands, crest and stereo; a masking table
// (which pairs fight in which critical bands, how badly); the master's measures and headroom;
// and the few biggest problems, worst first, each with what to do.

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
        /// {master, tracks[], masking[], headroom_db, problems[], reads[]}. Tracks are the probes'
        /// audio (what each machine sends towards the master), aligned with <paramref name="master"/>.
        /// </summary>
        public static JsonObject MixReport(IReadOnlyList<(string Name, StereoAudio Audio)> tracks, StereoAudio master)
        {
            var m = new Analysed(master);
            var ts = tracks.Select(t => (t.Name, A: new Analysed(t.Audio))).ToList();
            var problems = new List<(string Kind, double Severity, string Text)>();
            var ml = m.LoudnessResult;

            // ---- Master
            var masterJson = Measure(m, new[] { "loudness", "peaks", "dynamics", "spectrum", "stereo" });
            double headroom = double.IsNegativeInfinity(ml.TruePeakDb) ? double.PositiveInfinity : -ml.TruePeakDb;

            // ---- Tracks
            var trackJson = new JsonArray();
            foreach (var (name, a) in ts)
            {
                var l = a.LoudnessResult;
                var s = a.SpectrumResult;
                var bands = new JsonObject();
                for (int i = 0; i < Bands.Named.Length; i++) bands[Bands.Named[i].Name] = Db(s.NamedDb[i]);
                int loudest = Array.IndexOf(s.NamedDb, s.NamedDb.Max());
                var st = a.StereoResult;
                double relative = ml.Integrated - l.Integrated;
                trackJson.Add(new JsonObject
                {
                    ["name"] = name,
                    ["silent"] = a.Silent,
                    ["integrated_lufs"] = Lufs(l.Integrated),
                    ["short_term_max_lufs"] = Lufs(l.ShortTermMax),
                    ["below_master_lu"] = double.IsInfinity(relative) || double.IsNaN(relative) ? null : (JsonNode)Math.Round(relative, 1),
                    ["true_peak_dbtp"] = Db(l.TruePeakDb, 2),
                    ["sample_peak_dbfs"] = Db(l.SamplePeakDb, 2),
                    ["crest_db"] = Num(a.Levels.CrestDb, 1),
                    ["bands_db"] = bands,
                    ["main_band"] = a.Silent ? null : Bands.Named[loudest].Name,
                    ["correlation"] = Num(st.Correlation, 2),
                    ["mono_loss_db"] = Db(st.MonoLossDb),
                });
                if (a.Silent)
                {
                    problems.Add(("silent", 0.2, $"{name} is silent in this range (nothing above -90 dBFS): muted, not placed, or not reaching the master."));
                    continue;
                }
                if (l.SamplePeakDb >= 0 || l.Clipped > 0)
                    problems.Add(("track_clipping", 0.8, $"{name} clips on its own (sample peak {Signed(l.SamplePeakDb)} dBFS): turn it down at the source or its connection; track peaks around -10 to -6 dBFS leave room."));
                if (!st.Mono && st.Correlation < -0.3)
                    problems.Add(("phase", 0.85, $"{name}'s channels are out of phase (correlation {F(st.Correlation, 2)}, mono loss {F(st.MonoLossDb)} dB): it cancels in mono. Flip one channel's polarity or narrow it."));
                else if (!st.Mono)
                {
                    var low = st.Bands.Where(b => (b.Name == "sub" || b.Name == "low") && b.LevelDb > st.Bands.Max(x => x.LevelDb) - 20 && b.Correlation < 0).ToList();
                    if (low.Count > 0)
                        problems.Add(("phase", 0.75, $"{name}'s low end is out of phase ({string.Join(", ", low.Select(b => $"{b.Name} correlation {F(b.Correlation, 2)}"))}): its bass cancels in mono. Keep it mono below about 120 Hz."));
                }
                if (ts.Count(t => !t.A.Silent) >= 3 && !double.IsInfinity(relative) && relative < 1.5)
                    problems.Add(("dominant", 0.4, $"{name} is nearly as loud as the whole mix ({F(l.Integrated)} LUFS against the master's {F(ml.Integrated)}): it dominates. Check its balance against the other tracks."));
            }

            // ---- Masking
            var maskingJson = new JsonArray();
            string maskingNote = null;
            var sounding = ts.Where(t => !t.A.Silent).ToList();
            if (sounding.Select(t => t.A.Rate).Distinct().Count() > 1)
                maskingNote = "The tracks have different sample rates; masking needs them on one frame grid, so it was skipped.";
            else if (sounding.Count > 1)
            {
                var frames = sounding.Select(t => BandFrames.Analyze(t.Name, t.A.Audio.Left, t.A.Stereo ? t.A.Audio.Right : null, t.A.Rate)).ToList();
                foreach (var pair in Masking.Table(frames).Take(12))
                {
                    var bandsJson = new JsonArray();
                    foreach (var b in pair.Bands.Take(4))
                        bandsJson.Add(new JsonObject
                        {
                            ["from_hz"] = b.LoHz,
                            ["to_hz"] = b.HiHz,
                            ["severity"] = Math.Round(b.Severity, 2),
                            ["overlap_percent"] = Math.Round(100 * b.Overlap),
                            ["seconds"] = Math.Round(b.Seconds, 2),
                            ["levels_db"] = new JsonArray(Db(b.LevelA), Db(b.LevelB)),
                        });
                    maskingJson.Add(new JsonObject
                    {
                        ["tracks"] = new JsonArray(pair.A, pair.B),
                        ["severity"] = Math.Round(pair.Severity, 2),
                        ["level"] = SeverityWord(pair.Severity),
                        ["bands"] = bandsJson,
                    });
                    if (pair.Severity >= 0.25)
                    {
                        var w = pair.Bands[0];
                        string where = string.Join(", ", pair.Bands.Take(3).Select(b => $"{b.LoHz:0}–{b.HiHz:0} Hz"));
                        problems.Add(("masking", Math.Min(0.9, 0.3 + 0.6 * pair.Severity),
                            $"{pair.A} and {pair.B} fight at {where} (severity {F(pair.Severity, 2)}, {SeverityWord(pair.Severity)}; within {F(Math.Abs(w.LevelA - w.LevelB))} dB of each other " +
                            $"for {F(100 * w.Overlap, 0)}% of the time the less busy one plays there). Cut one where the other lives (EQ), sidechain one to the other, or move one an octave."));
                    }
                }
            }

            // ---- Master problems
            if (!m.Silent)
            {
                if (ml.SamplePeakDb >= 0 || ml.Clipped > 0)
                    problems.Add(("clipping", 1.0, $"The master clips (sample peak {Signed(ml.SamplePeakDb)} dBFS, {ml.Clipped} samples): lower the loudest tracks or the master, or limit it."));
                else if (ml.TruePeakDb > -1)
                    problems.Add(("headroom", 0.7, $"Master true peak {Signed(ml.TruePeakDb)} dBTP: above -1 dBTP. Fine only if a true-peak limiter is meant to catch it; a mix for mastering wants about 6 dB."));
                else if (ml.TruePeakDb > -6)
                    problems.Add(("headroom", 0.25, $"Headroom {F(headroom)} dB: under the 6 dB a mix leaves for mastering (fine if this is already the master)."));
                var ms = m.StereoResult;
                if (!ms.Mono && ms.Correlation < 0)
                    problems.Add(("phase", 0.9, $"The master is out of phase (correlation {F(ms.Correlation, 2)}): it collapses in mono. Look for a track with a flipped channel."));
                else if (!ms.Mono)
                {
                    var low = ms.Bands.Where(b => (b.Name == "sub" || b.Name == "low") && b.LevelDb > ms.Bands.Max(x => x.LevelDb) - 30 && (b.Correlation < 0.5 || b.MonoLossDb < -3)).ToList();
                    if (low.Count > 0 && !problems.Any(p => p.Kind == "phase"))
                        problems.Add(("phase", 0.7, $"The master's low end is wide or out of phase ({string.Join(", ", low.Select(b => $"{b.Name}: correlation {F(b.Correlation, 2)}, mono loss {F(b.MonoLossDb)} dB"))}): keep below about 120 Hz mono."));
                }
                var spec = m.SpectrumResult;
                if (FullRange(spec) && (spec.TiltDbPerOctave < -4.5 || spec.TiltDbPerOctave > -1))
                    problems.Add(("balance", 0.3, $"The master's tilt is {Signed(spec.TiltDbPerOctave, 2)} dB/oct: {(spec.TiltDbPerOctave > -1 ? "bright" : "dark")} against finished mixes (about -2 to -3.5)."));
            }

            var top = problems.OrderByDescending(p => p.Severity).Take(6).ToList();
            var problemsJson = new JsonArray(top.Select(p => (JsonNode)new JsonObject { ["kind"] = p.Kind, ["severity"] = Math.Round(p.Severity, 2), ["text"] = p.Text }).ToArray());
            var reads = new JsonArray();
            reads.Add(m.Silent ? "The master is silent." :
                $"Master {F(ml.Integrated)} LUFS, true peak {Signed(ml.TruePeakDb)} dBTP ({F(headroom)} dB headroom); {ts.Count} tracks, " +
                $"{maskingJson.Count} masking pair{(maskingJson.Count == 1 ? "" : "s")}.");
            reads.Add(top.Count == 0 ? "No problems found: no clipping, phase trouble, strong masking or dominant track." : $"Biggest problem: {top[0].Text}");
            var result = new JsonObject
            {
                ["master"] = masterJson,
                ["headroom_db"] = Db(headroom, 2),
                ["tracks"] = trackJson,
                ["masking"] = maskingJson,
                ["problems"] = problemsJson,
                ["reads"] = reads,
            };
            if (maskingNote != null) result["masking_note"] = maskingNote;
            return result;
        }

        /// <summary>"strong" from 0.4, "moderate" from 0.2, else "mild".</summary>
        static string SeverityWord(double s) => s >= 0.4 ? "strong" : s >= 0.2 ? "moderate" : "mild";
    }
}
