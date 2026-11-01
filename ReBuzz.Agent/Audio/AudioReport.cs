#nullable disable
// Agent server (docs/specs/mcp-server.md §5.7, §7): measurements of a render or file as the agent
// reads them: numbers in LUFS, dBTP, dBFS, dB, Hz and BPM, each with a plain-language read. The
// measuring is NeoBuzz.Analysis (BS.1770 loudness, EBU R128 range, 4× true peak, Welch spectrum,
// stereo, YIN pitch, Krumhansl–Schmuckler key, spectral-flux tempo). Compare and MixReport are in
// AudioReport.Compare.cs and AudioReport.Mix.cs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using NeoBuzz.Analysis;
using ReBuzz.Agent.Model;

namespace ReBuzz.Agent.Audio
{
    public static partial class AudioReport
    {
        /// <summary>Every metric <see cref="Measure"/> knows.</summary>
        public static readonly string[] Metrics = { "loudness", "peaks", "dynamics", "spectrum", "stereo", "pitch", "key", "tempo" };

        /// <summary>The quick pair a render reports by itself (the rest cost more on a whole song).</summary>
        public static readonly string[] Basic = { "loudness", "peaks" };

        static readonly CultureInfo C = CultureInfo.InvariantCulture;

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

        /// <summary>
        /// Measures and reads: {seconds, silent, loudness?, peaks?, dynamics?, spectrum?, stereo?,
        /// pitch?, key?, tempo?, reads[]}, only the metrics asked for.
        /// </summary>
        public static JsonObject Measure(StereoAudio audio, IReadOnlyCollection<string> metrics) => Measure(new Analysed(audio), metrics);

        internal static JsonObject Measure(Analysed a, IReadOnlyCollection<string> metrics)
        {
            var audio = a.Audio;
            var r = a.LoudnessResult;
            var reads = new JsonArray();
            bool silent = a.Silent;
            var result = new JsonObject { ["seconds"] = Math.Round(audio.Seconds, 3), ["silent"] = silent };
            if (silent) reads.Add("Silent: nothing above -90 dBFS. Check that the patterns are placed in the range (arrangement_get), have notes (notes_read), " +
                                  "and that their machines reach Master (machine_list). Some older native machines don't play MIDI notes.");
            void Add(IEnumerable<string> lines)
            {
                if (silent) return;
                foreach (var line in lines) reads.Add(line);
            }
            if (metrics.Contains("loudness"))
            {
                result["loudness"] = new JsonObject
                {
                    ["integrated_lufs"] = Lufs(r.Integrated),
                    ["short_term_max_lufs"] = Lufs(r.ShortTermMax),
                    ["momentary_max_lufs"] = Lufs(r.MomentaryMax),
                    ["range_lu"] = double.IsNaN(r.Range) || double.IsInfinity(r.Range) ? 0 : Math.Round(r.Range, 1),
                };
                Add(LoudnessReads(r));
            }
            if (metrics.Contains("peaks"))
            {
                result["peaks"] = new JsonObject
                {
                    ["true_peak_dbtp"] = Levels.Json(Math.Round(r.TruePeakDb, 2)),
                    ["sample_peak_dbfs"] = Levels.Json(Math.Round(r.SamplePeakDb, 2)),
                    ["clipped_samples"] = r.Clipped,
                };
                Add(PeakReads(r));
            }
            if (metrics.Contains("dynamics"))
            {
                result["dynamics"] = DynamicsJson(a.DynamicsResult);
                Add(DynamicsReads(a.DynamicsResult));
            }
            if (metrics.Contains("spectrum"))
            {
                result["spectrum"] = SpectrumJson(a.SpectrumResult);
                Add(SpectrumReads(a.SpectrumResult));
            }
            if (metrics.Contains("stereo"))
            {
                result["stereo"] = StereoJson(a.StereoResult);
                Add(StereoReads(a.StereoResult));
            }
            if (metrics.Contains("pitch"))
            {
                result["pitch"] = PitchJson(a.PitchTrack);
                Add(PitchReads(a.PitchTrack));
            }
            if (metrics.Contains("key"))
            {
                result["key"] = KeyJson(a.KeyResult);
                Add(KeyReads(a.KeyResult));
            }
            if (metrics.Contains("tempo"))
            {
                result["tempo"] = TempoJson(a.TempoResult, audio.Seconds);
                Add(TempoReads(a.TempoResult));
            }
            result["reads"] = reads;
            return result;
        }

        /// <summary>One line for summaries: "-14.2 LUFS integrated, range 5.1 LU, true peak -1.3 dBTP, 128.0 BPM, A minor".</summary>
        public static string Line(JsonObject m)
        {
            if (m["silent"]?.GetValue<bool>() == true) return "silent";
            var parts = new List<string>();
            if (m["loudness"] is JsonObject l) parts.Add($"{l["integrated_lufs"]} LUFS integrated, range {l["range_lu"]} LU");
            if (m["peaks"] is JsonObject p) parts.Add($"true peak {p["true_peak_dbtp"]} dBTP" + (p["clipped_samples"]!.GetValue<int>() > 0 ? $", {p["clipped_samples"]} clipped samples" : ""));
            if (m["spectrum"] is JsonObject s && s["tilt_db_per_octave"] != null) parts.Add($"tilt {s["tilt_db_per_octave"]} dB/oct");
            if (m["stereo"] is JsonObject st && st["mono"]?.GetValue<bool>() == false) parts.Add($"correlation {st["correlation"]}");
            if (m["tempo"] is JsonObject t && t["bpm"] != null) parts.Add($"{t["bpm"]} BPM");
            if (m["key"] is JsonObject k && k["name"] != null) parts.Add(k["name"]!.GetValue<string>());
            return string.Join(", ", parts);
        }

        // ---- JSON numbers ------------------------------------------------------------------

        static JsonNode Lufs(double x) => double.IsNegativeInfinity(x) || double.IsNaN(x) ? JsonValue.Create("-inf") : JsonValue.Create(Math.Round(x, 1));

        /// <summary>A level in dB rounded to <paramref name="digits"/>: "-inf" and "inf" as text (JSON has no infinities), NaN as null.</summary>
        internal static JsonNode Db(double x, int digits = 1) =>
            double.IsNaN(x) ? null : double.IsNegativeInfinity(x) ? JsonValue.Create("-inf") : double.IsPositiveInfinity(x) ? JsonValue.Create("inf") : JsonValue.Create(Math.Round(x, digits));

        /// <summary>A plain number, null when not finite.</summary>
        internal static JsonNode Num(double x, int digits) => double.IsNaN(x) || double.IsInfinity(x) ? null : JsonValue.Create(Math.Round(x, digits));

        internal static string F(double x, int digits = 1) =>
            double.IsNegativeInfinity(x) ? "-inf" : double.IsPositiveInfinity(x) ? "inf" : double.IsNaN(x) ? "n/a" : x.ToString("F" + digits, C);

        /// <summary>"+3.1", "-0.4", "0".</summary>
        internal static string Signed(double x, int digits = 1)
        {
            if (double.IsInfinity(x) || double.IsNaN(x)) return F(x, digits);
            string number = digits == 0 ? "0" : "0." + new string('0', digits);
            return x.ToString($"+{number};-{number};0", C);
        }

        /// <summary>Up to <paramref name="points"/> values, each the power mean of its group of dB values (−∞ when all are).</summary>
        static JsonObject DbOverTime(double[] db, double step, int points, string key)
        {
            int group = Math.Max(1, (int)Math.Ceiling(db.Length / (double)points));
            var values = new JsonArray();
            for (int i = 0; i < db.Length; i += group)
            {
                double sum = 0;
                int n = 0;
                for (int j = i; j < Math.Min(db.Length, i + group); j++, n++) sum += double.IsNegativeInfinity(db[j]) ? 0 : Math.Pow(10, db[j] / 10);
                values.Add(Db(Spectrum.Db(sum / Math.Max(1, n))));
            }
            return new JsonObject { ["step_seconds"] = Math.Round(step * group, 3), [key] = values };
        }

        // ---- Loudness and peaks ------------------------------------------------------------

        static IEnumerable<string> LoudnessReads(LoudnessResult r)
        {
            double i = r.Integrated;
            if (double.IsNegativeInfinity(i)) yield return "Too short or too quiet for an integrated loudness (needs 400 ms above -70 LUFS).";
            else if (i > -7) yield return $"Integrated {i.ToString("F1", C)} LUFS: very loud, beyond most club masters; expect audible limiting.";
            else if (i > -11) yield return $"Integrated {i.ToString("F1", C)} LUFS: loud (club and EDM masters sit around -9 to -7).";
            else if (i > -16) yield return $"Integrated {i.ToString("F1", C)} LUFS: streaming level (services normalise to about -14).";
            else if (i > -24) yield return $"Integrated {i.ToString("F1", C)} LUFS: moderate; a typical unmastered mix with headroom.";
            else yield return $"Integrated {i.ToString("F1", C)} LUFS: quiet; raise levels before judging the mix.";
            if (!double.IsNaN(r.Range) && !double.IsInfinity(r.Range) && r.Seconds >= 10)
            {
                if (r.Range < 4) yield return $"Loudness range {r.Range.ToString("F1", C)} LU: very even (dense, compressed).";
                else if (r.Range > 15) yield return $"Loudness range {r.Range.ToString("F1", C)} LU: wide (quiet and loud sections differ a lot).";
            }
        }

        static IEnumerable<string> PeakReads(LoudnessResult r)
        {
            if (r.Clipped > 0 || r.SamplePeakDb > 0)
                yield return $"Clipping: the sample peak is {r.SamplePeakDb.ToString("+0.0;-0.0", C)} dBFS ({r.Clipped} samples at or over full scale). Lower the loudest tracks or the master.";
            else if (r.TruePeakDb > -1)
                yield return $"True peak {r.TruePeakDb.ToString("+0.0;-0.0", C)} dBTP: above -1 dBTP, so lossy encoding may clip. Leave about 1 dB (a true-peak limiter does it).";
            else if (r.TruePeakDb < -6)
                yield return $"True peak {r.TruePeakDb.ToString("F1", C)} dBTP: plenty of headroom.";
        }

        // ---- Dynamics ----------------------------------------------------------------------

        static JsonObject DynamicsJson(DynamicsResult d) => new()
        {
            ["crest_db"] = Num(d.CrestDb, 1),
            ["plr_db"] = Num(d.PlrDb, 1),
            ["rms_dbfs"] = Db(d.RmsDb),
            ["peak_dbfs"] = Db(d.PeakDb),
            ["rms_percentiles_dbfs"] = new JsonObject { ["p10"] = Db(d.EnvelopeP10), ["p50"] = Db(d.EnvelopeP50), ["p95"] = Db(d.EnvelopeP95) },
            ["rms_spread_db"] = Num(d.EnvelopeP95 - d.EnvelopeP10, 1),
            ["rms_over_time"] = DbOverTime(d.Envelope, d.WindowSeconds, 120, "dbfs"),
            ["transients"] = d.Onsets.Count,
            ["transients_per_second"] = Num(d.TransientsPerSecond, 2),
        };

        static IEnumerable<string> DynamicsReads(DynamicsResult d)
        {
            double plr = d.PlrDb;
            if (!double.IsNaN(plr))
            {
                string text = $"PLR {F(plr)} dB (true peak over integrated loudness)";
                if (plr < 6) yield return text + ": very dense; peaks barely rise above the body (a heavily limited master, or a steady sound such as a held synth).";
                else if (plr < 9) yield return text + ": dense, loud-master territory.";
                else if (plr <= 14) yield return text + ": balanced dynamics, typical of streaming masters.";
                else yield return text + ": very dynamic; peaks far above the body (an unmastered mix, sparse hits, or acoustic material).";
            }
            else yield return $"Crest factor {F(d.CrestDb)} dB (sample peak over RMS).";
            double spread = d.EnvelopeP95 - d.EnvelopeP10;
            if (!double.IsNaN(spread) && !double.IsInfinity(spread) && spread > 20)
                yield return $"RMS varies by {F(spread)} dB between quiet and loud moments (10th to 95th percentile of 50 ms windows): big level swings.";
            double tps = d.TransientsPerSecond;
            if (tps >= 0.2)
                yield return $"{F(tps)} transients per second: " + (tps < 1 ? "sparse hits." : tps <= 6 ? "a steady rhythmic part." : "busy (fast hats, rolls or dense percussion).");
        }

        // ---- Spectrum ----------------------------------------------------------------------

        static JsonObject SpectrumJson(SpectrumResult s)
        {
            var bands = new JsonObject();
            for (int i = 0; i < Bands.Named.Length; i++) bands[Bands.Named[i].Name] = Db(s.NamedDb[i]);
            return new JsonObject
            {
                ["bands_db"] = bands,
                ["third_octave"] = new JsonObject
                {
                    ["hz"] = new JsonArray(s.ThirdOctaveHz.Select(h => (JsonNode)h).ToArray()),
                    ["db"] = new JsonArray(s.ThirdOctaveDb.Select(d => Db(d)).ToArray()),
                },
                ["total_db"] = Db(s.TotalDb),
                ["centroid_hz"] = Num(s.CentroidHz, 0),
                ["rolloff_hz"] = Num(s.RolloffHz, 0),
                ["flatness"] = Num(s.Flatness, 3),
                ["tilt_db_per_octave"] = Num(s.TiltDbPerOctave, 2),
                ["resolution_hz"] = Math.Round(s.Psd.BinHz, 2),
                ["units"] = "dB RMS per band (a full-scale sine reads -3 dB); third octaves and bands add up to total_db",
            };
        }

        /// <summary>What each band sounds like when it stands out from the overall slope, and when it dips.</summary>
        static readonly Dictionary<string, (string Bump, string Dip)> BandWords = new()
        {
            ["sub"] = ("rumble or boom", "little sub weight"),
            ["low"] = ("boomy, heavy", "thin"),
            ["low_mid"] = ("muddy, boxy", "hollow"),
            ["mid"] = ("honky, nasal", "scooped"),
            ["high_mid"] = ("hard, harsh", "recessed"),
            ["presence"] = ("forward, sibilant", "distant, less clarity"),
            ["air"] = ("airy or hissy", "dull, no air"),
        };

        /// <summary>A band's level against the overall slope: its level per third octave minus the fitted line at its centre.</summary>
        internal static double BandResidual(SpectrumResult s, int band)
        {
            var b = Bands.Named[band];
            double perThird = s.NamedDb[band] - 10 * Math.Log10(3 * b.Octaves);
            double centre = Math.Sqrt(b.Lo * Math.Min(b.Hi, s.Psd.Nyquist));
            return perThird - (s.TiltAt1k + s.TiltDbPerOctave * Math.Log2(centre / 1000));
        }

        /// <summary>True when the audio fills most of the range (a mix or a full sound), so its balance means something.</summary>
        internal static bool FullRange(SpectrumResult s)
        {
            double max = s.NamedDb.Max();
            return !double.IsNegativeInfinity(max) && s.NamedDb.Count(d => d > max - 40) >= 6;
        }

        /// <summary>Every band within 30 dB of the loudest: a full mix, where a band standing out of the slope is a balance question.</summary>
        static bool Broadband(SpectrumResult s)
        {
            double max = s.NamedDb.Max();
            return !double.IsNegativeInfinity(max) && Bands.Named.Select((b, i) => (b, i)).All(t => t.b.Lo >= s.Psd.Nyquist || s.NamedDb[t.i] > max - 30);
        }

        static IEnumerable<string> SpectrumReads(SpectrumResult s)
        {
            int loudest = Array.IndexOf(s.NamedDb, s.NamedDb.Max());
            var lb = Bands.Named[loudest];
            if (!FullRange(s))
            {
                yield return $"Narrow-band sound: most energy in {lb.Name} ({lb.Lo:0}–{lb.Hi:0} Hz), centroid {F(s.CentroidHz, 0)} Hz.";
                yield break;
            }
            double t = s.TiltDbPerOctave;
            string tilt = $"Tilt {Signed(t)} dB/oct (third-octave levels, pink noise 0)";
            if (t > -1) yield return tilt + ": bright, near pink noise or brighter; finished mixes usually fall 2–4 dB per octave.";
            else if (t > -2) yield return tilt + ": on the bright side of finished mixes.";
            else if (t >= -3.5) yield return tilt + ": balanced, typical of finished mixes.";
            else if (t >= -4) yield return tilt + ": warm, leaning dark.";
            else yield return tilt + ": dark; the highs are low against the lows.";
            var notes = new List<string>();
            for (int i = 0; i < Bands.Named.Length; i++)
            {
                double res = BandResidual(s, i);
                var b = Bands.Named[i];
                if (b.Lo >= s.Psd.Nyquist) continue;
                if (res > 3) notes.Add($"{b.Name} {Signed(res)} dB ({BandWords[b.Name].Bump})");
                else if (res < -4) notes.Add($"{b.Name} {Signed(res)} dB ({BandWords[b.Name].Dip})");
            }
            if (notes.Count > 0 && Broadband(s)) yield return "Against the overall slope: " + string.Join("; ", notes) + ".";
        }

        // ---- Stereo ------------------------------------------------------------------------

        static JsonObject StereoJson(StereoResult s)
        {
            var bands = new JsonObject();
            foreach (var b in s.Bands)
                bands[b.Name] = new JsonObject
                {
                    ["width"] = Db(b.Width, 2),
                    ["correlation"] = Num(b.Correlation, 2),
                    ["mono_loss_db"] = Db(b.MonoLossDb),
                    ["level_db"] = Db(b.LevelDb),
                };
            int group = Math.Max(1, (int)Math.Ceiling(s.CorrelationOverTime.Length / 120.0));
            var overTime = new JsonArray();
            for (int i = 0; i < s.CorrelationOverTime.Length; i += group)
            {
                var part = s.CorrelationOverTime.Skip(i).Take(group).Where(v => !double.IsNaN(v)).ToList();
                overTime.Add(part.Count > 0 ? JsonValue.Create(Math.Round(part.Average(), 2)) : null);
            }
            return new JsonObject
            {
                ["mono"] = s.Mono,
                ["correlation"] = Num(s.Correlation, 2),
                ["correlation_min"] = Num(s.CorrelationMin, 2),
                ["negative_correlation_percent"] = Math.Round(100 * s.NegativeShare, 1),
                ["width"] = Db(s.Width, 2),
                ["side_to_mid_db"] = Db(s.SideToMidDb),
                ["mono_loss_db"] = Db(s.MonoLossDb),
                ["balance_db"] = Num(s.BalanceDb, 1),
                ["bands"] = bands,
                ["correlation_over_time"] = new JsonObject { ["step_seconds"] = Math.Round(s.WindowSeconds * group, 3), ["values"] = overTime },
                ["units"] = "width = side/mid energy (0 mono, 1 unrelated channels, above 1 opposed); mono_loss_db = (L+R)/2 against the channels' mean",
            };
        }

        static IEnumerable<string> StereoReads(StereoResult s)
        {
            if (s.Mono)
            {
                yield return "Mono: both channels are identical.";
                yield break;
            }
            double c = s.Correlation;
            string corr = $"Correlation {F(c, 2)}";
            if (c < 0) yield return corr + $": a phase problem; the channels oppose and the mix collapses in mono (mono loss {F(s.MonoLossDb)} dB). Check a polarity flip or a wide effect.";
            else if (c < 0.3) yield return corr + $": very wide; it loses {F(-s.MonoLossDb)} dB in mono. Check it on one speaker.";
            else if (c <= 0.95) yield return corr + $", width {F(s.Width, 2)} (side/mid): healthy stereo.";
            else yield return corr + ": nearly mono.";
            if (s.CorrelationMin < -0.3 && c >= 0) yield return $"At moments the correlation reaches {F(s.CorrelationMin, 2)} ({F(100 * s.NegativeShare, 0)}% of the time below 0): out-of-phase content now and then.";
            var low = s.Bands.Where(b => (b.Name == "sub" || b.Name == "low") && b.LevelDb > s.Bands.Max(x => x.LevelDb) - 30).ToList();
            var wideLow = low.Where(b => b.Width > 0.25 || b.Correlation < 0.5).ToList();
            if (wideLow.Count > 0)
                yield return "The low end is wide or out of phase (" + string.Join(", ", wideLow.Select(b => $"{b.Name}: width {F(b.Width, 2)}, correlation {F(b.Correlation, 2)}, mono loss {F(b.MonoLossDb)} dB")) +
                             "): keep below about 120 Hz mono (Neo Utility's mono-below, or pan the bass and kick centre).";
            if (Math.Abs(s.BalanceDb) > 1.5) yield return $"Off centre: the {(s.BalanceDb > 0 ? "left" : "right")} channel is {F(Math.Abs(s.BalanceDb))} dB louder.";
        }

        // ---- Pitch -------------------------------------------------------------------------

        static JsonObject PitchJson(PitchTrack p)
        {
            var notes = new JsonArray();
            foreach (var n in p.Notes.Take(200))
                notes.Add(new JsonObject
                {
                    ["start_seconds"] = Math.Round(n.Start, 3),
                    ["end_seconds"] = Math.Round(n.End, 3),
                    ["note"] = Pitch.Name(n.Midi),
                    ["midi"] = n.Midi,
                    ["hz"] = Math.Round(n.Hz, 2),
                    ["cents"] = Math.Round(n.Cents, 1),
                    ["confidence"] = Math.Round(n.Confidence, 2),
                });
            int group = Math.Max(1, (int)Math.Ceiling(p.Frames.Length / 200.0));
            var hz = new JsonArray();
            var cents = new JsonArray();
            for (int i = 0; i < p.Frames.Length; i += group)
            {
                var voiced = p.Frames.Skip(i).Take(group).Where(f => f.Voiced).Select(f => f.Hz).OrderBy(x => x).ToList();
                if (voiced.Count == 0) { hz.Add(null); cents.Add(null); continue; }
                double median = voiced[voiced.Count / 2], midi = 69 + 12 * Math.Log2(median / 440);
                hz.Add(Math.Round(median, 1));
                cents.Add(Math.Round(100 * (midi - Math.Round(midi)), 1));
            }
            var result = new JsonObject
            {
                ["voiced_percent"] = Math.Round(100 * p.VoicedShare, 1),
                ["median_hz"] = Num(p.MedianHz, 2),
                ["median_note"] = double.IsNaN(p.MedianMidi) ? null : Pitch.Name((int)Math.Round(p.MedianMidi)),
                ["median_cents"] = double.IsNaN(p.MedianMidi) ? null : (JsonNode)Math.Round(100 * (p.MedianMidi - Math.Round(p.MedianMidi)), 1),
                ["notes"] = notes,
                ["over_time"] = new JsonObject { ["step_seconds"] = Math.Round(p.HopSeconds * group, 3), ["hz"] = hz, ["cents"] = cents },
            };
            if (p.Notes.Count > 200) result["notes_truncated"] = p.Notes.Count;
            return result;
        }

        static IEnumerable<string> PitchReads(PitchTrack p)
        {
            if (p.VoicedShare < 0.2 || double.IsNaN(p.MedianMidi))
            {
                yield return $"Pitched only {F(100 * p.VoicedShare, 0)}% of the time: unpitched, silent or polyphonic material (pitch follows one voice at a time).";
                yield break;
            }
            double cents = 100 * (p.MedianMidi - Math.Round(p.MedianMidi));
            string tune = Math.Abs(cents) <= 5 ? "in tune" : Math.Abs(cents) <= 15 ? (cents > 0 ? "slightly sharp" : "slightly flat") : (cents > 0 ? "sharp" : "flat");
            yield return $"Median pitch {Pitch.Name((int)Math.Round(p.MedianMidi))} ({F(p.MedianHz, 1)} Hz), {Signed(cents, 0)} cents: {tune}. {p.Notes.Count} notes.";
            var off = p.Notes.Where(n => Math.Abs(n.Cents) > 15).ToList();
            if (off.Count > 0 && p.Notes.Count > 1)
                yield return $"{off.Count} of {p.Notes.Count} notes are more than 15 cents from equal temperament (first: {Pitch.Name(off[0].Midi)} at {F(off[0].Start, 2)} s, {Signed(off[0].Cents, 0)} cents).";
        }

        // ---- Key ---------------------------------------------------------------------------

        static JsonObject KeyJson(KeyResult k)
        {
            var chroma = new JsonObject();
            for (int c = 0; c < 12; c++) chroma[KeyFinder.PitchClasses[c]] = Math.Round(k.Chroma[c], 2);
            string name = k.Name;
            return new JsonObject
            {
                ["name"] = name,
                ["tonic"] = name.Split(' ')[0],
                ["mode"] = k.Minor ? "minor" : "major",
                ["correlation"] = Math.Round(k.Correlation, 3),
                ["runner_up"] = k.RunnerUp,
                ["runner_up_correlation"] = Math.Round(k.RunnerUpCorrelation, 3),
                ["chroma"] = chroma,
            };
        }

        static IEnumerable<string> KeyReads(KeyResult k)
        {
            if (k.Correlation < 0.5)
            {
                yield return $"No clear key (best {k.Name}, correlation {F(k.Correlation, 2)}): drums, noise, or chromatic material.";
                yield break;
            }
            string strength = k.Correlation >= 0.75 ? "clear" : "likely";
            string next = $"next {k.RunnerUp} ({F(k.RunnerUpCorrelation, 2)})";
            bool relative = k.Ranking[1].Minor != k.Minor && ((k.Minor ? k.Tonic + 3 : k.Tonic + 9) % 12) == k.Ranking[1].Tonic;
            if (relative) next += ", its relative key: same notes, the tonic's weight decides";
            yield return $"Key {k.Name} ({strength}, correlation {F(k.Correlation, 2)}); {next}.";
        }

        // ---- Tempo -------------------------------------------------------------------------

        static JsonObject TempoJson(TempoResult t, double seconds)
        {
            var result = new JsonObject
            {
                ["bpm"] = t.Bpm > 0 ? (JsonNode)Math.Round(t.Bpm, 2) : null,
                ["confidence"] = Math.Round(t.Confidence, 2),
                ["grid_fit"] = t.Grid,
                ["first_beat_seconds"] = t.Grid ? (JsonNode)Math.Round(t.FirstBeat, 3) : null,
                ["onsets"] = t.Onsets.Count,
                ["onsets_per_second"] = Math.Round(t.OnsetsPerSecond, 2),
                ["onset_seconds"] = new JsonArray(t.Onsets.Take(64).Select(o => (JsonNode)Math.Round(o, 3)).ToArray()),
            };
            if (t.Bpm > 0) result["also_reads_as_bpm"] = new JsonArray((JsonNode)Math.Round(t.Bpm / 2, 2), (JsonNode)Math.Round(t.Bpm * 2, 2));
            return result;
        }

        static IEnumerable<string> TempoReads(TempoResult t)
        {
            if (t.Bpm <= 0 || t.Confidence < 0.1)
            {
                yield return t.Onsets.Count < 4 ? "No steady pulse: too few transients for a tempo." : $"No clear tempo ({t.Onsets.Count} onsets without a regular beat).";
                yield break;
            }
            string grid = t.Grid ? $"; a beat grid fits {t.OnGrid} onsets, first beat at {F(t.FirstBeat, 3)} s" : "";
            yield return $"Tempo {F(t.Bpm, 1)} BPM (confidence {F(t.Confidence, 2)}{grid}). Half or double time ({F(t.Bpm / 2, 1)}, {F(t.Bpm * 2, 1)}) fit the same pulse.";
        }
    }
}
