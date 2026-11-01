#nullable disable
// Agent server (docs/specs/mcp-server.md §5.7, §7): measurements of a render or file as the agent
// reads them: numbers in LUFS, dBTP and dBFS, each with a plain-language read. The measuring is
// NeoBuzz.Analysis (BS.1770 loudness, EBU R128 range, 4× true peak).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using NeoBuzz.Analysis;
using ReBuzz.Agent.Model;

namespace ReBuzz.Agent.Audio
{
    public static class AudioReport
    {
        public static readonly string[] Metrics = { "loudness", "peaks" };

        /// <summary>The part of <paramref name="audio"/> from <paramref name="from"/> to <paramref name="to"/> seconds (null: the ends).</summary>
        public static StereoAudio Slice(StereoAudio audio, double? from, double? to)
        {
            int a = (int)Math.Clamp(Math.Round((from ?? 0) * audio.Rate), 0, audio.Frames);
            int b = (int)Math.Clamp(Math.Round((to ?? audio.Seconds) * audio.Rate), a, audio.Frames);
            if (a == 0 && b == audio.Frames) return audio;
            return new StereoAudio
            {
                Left = audio.Left[a..b],
                Right = audio.Right?[a..b],
                Rate = audio.Rate,
            };
        }

        /// <summary>Measures and reads: {seconds, loudness?, peaks?, silent, reads[]}.</summary>
        public static JsonObject Measure(StereoAudio audio, IReadOnlyCollection<string> metrics)
        {
            var r = Loudness.Measure(audio.Left, audio.Right, audio.Rate);
            var reads = new JsonArray();
            bool silent = double.IsNegativeInfinity(r.SamplePeakDb) || r.SamplePeakDb < -90;
            var result = new JsonObject { ["seconds"] = Math.Round(audio.Seconds, 3), ["silent"] = silent };
            if (silent) reads.Add("Silent: nothing above -90 dBFS. Check that notes are placed in the arrangement and the machine is connected to Master.");
            if (metrics.Contains("loudness"))
            {
                result["loudness"] = new JsonObject
                {
                    ["integrated_lufs"] = Lufs(r.Integrated),
                    ["short_term_max_lufs"] = Lufs(r.ShortTermMax),
                    ["momentary_max_lufs"] = Lufs(r.MomentaryMax),
                    ["range_lu"] = double.IsNaN(r.Range) || double.IsInfinity(r.Range) ? 0 : Math.Round(r.Range, 1),
                };
                if (!silent) foreach (var line in LoudnessReads(r)) reads.Add(line);
            }
            if (metrics.Contains("peaks"))
            {
                result["peaks"] = new JsonObject
                {
                    ["true_peak_dbtp"] = Levels.Json(Math.Round(r.TruePeakDb, 2)),
                    ["sample_peak_dbfs"] = Levels.Json(Math.Round(r.SamplePeakDb, 2)),
                    ["clipped_samples"] = r.Clipped,
                };
                if (!silent) foreach (var line in PeakReads(r)) reads.Add(line);
            }
            result["reads"] = reads;
            return result;
        }

        /// <summary>One line for summaries: "-14.2 LUFS, -1.3 dBTP, range 5.1 LU".</summary>
        public static string Line(JsonObject m)
        {
            if (m["silent"]?.GetValue<bool>() == true) return "silent";
            var parts = new List<string>();
            if (m["loudness"] is JsonObject l) parts.Add($"{l["integrated_lufs"]} LUFS integrated, range {l["range_lu"]} LU");
            if (m["peaks"] is JsonObject p) parts.Add($"true peak {p["true_peak_dbtp"]} dBTP" + (p["clipped_samples"]!.GetValue<int>() > 0 ? $", {p["clipped_samples"]} clipped samples" : ""));
            return string.Join(", ", parts);
        }

        static JsonNode Lufs(double x) => double.IsNegativeInfinity(x) || double.IsNaN(x) ? JsonValue.Create("-inf") : JsonValue.Create(Math.Round(x, 1));

        static IEnumerable<string> LoudnessReads(LoudnessResult r)
        {
            var c = CultureInfo.InvariantCulture;
            double i = r.Integrated;
            if (double.IsNegativeInfinity(i)) yield return "Too short or too quiet for an integrated loudness (needs 400 ms above -70 LUFS).";
            else if (i > -7) yield return $"Integrated {i.ToString("F1", c)} LUFS: very loud, beyond most club masters; expect audible limiting.";
            else if (i > -11) yield return $"Integrated {i.ToString("F1", c)} LUFS: loud (club and EDM masters sit around -9 to -7).";
            else if (i > -16) yield return $"Integrated {i.ToString("F1", c)} LUFS: streaming level (services normalise to about -14).";
            else if (i > -24) yield return $"Integrated {i.ToString("F1", c)} LUFS: moderate; a typical unmastered mix with headroom.";
            else yield return $"Integrated {i.ToString("F1", c)} LUFS: quiet; raise levels before judging the mix.";
            if (!double.IsNaN(r.Range) && !double.IsInfinity(r.Range) && r.Seconds >= 10)
            {
                if (r.Range < 4) yield return $"Loudness range {r.Range.ToString("F1", c)} LU: very even (dense, compressed).";
                else if (r.Range > 15) yield return $"Loudness range {r.Range.ToString("F1", c)} LU: wide (quiet and loud sections differ a lot).";
            }
        }

        static IEnumerable<string> PeakReads(LoudnessResult r)
        {
            var c = CultureInfo.InvariantCulture;
            if (r.Clipped > 0 || r.SamplePeakDb > 0)
                yield return $"Clipping: the sample peak is {r.SamplePeakDb.ToString("+0.0;-0.0", c)} dBFS ({r.Clipped} samples at or over full scale). Lower the loudest tracks or the master.";
            else if (r.TruePeakDb > -1)
                yield return $"True peak {r.TruePeakDb.ToString("+0.0;-0.0", c)} dBTP: above -1 dBTP, so lossy encoding may clip. Leave about 1 dB (a true-peak limiter does it).";
            else if (r.TruePeakDb < -6)
                yield return $"True peak {r.TruePeakDb.ToString("F1", c)} dBTP: plenty of headroom.";
        }
    }
}
