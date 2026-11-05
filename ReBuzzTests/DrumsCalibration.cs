using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/machines.md, Neo Drums; mcp-server.md §12.10): the classic drum machines' hits, measured for
    /// Neo Drums' successor pairs. Rout 909 is closed and plays only from its trigger columns, which the tools can't write:
    /// each hit plays from a copy of a repo song (Oomek/Exciter/909.bmx) whose pattern has that one trigger written into the
    /// file (DrumHits.WithTrackRows). FSM KickXP plays a note. Each hit is read from its probe (DrumHits.Describe).
    /// </summary>
    public partial class AgentHeadlessTests
    {
        /// <summary>Rout 909's voices, in the order of its trigger columns (BD Trigger … RC Trigger).</summary>
        static readonly string[] Rout909Voices = { "BD", "SD", "LT", "MT", "HT", "RS", "CP", "CH", "OH", "CC", "RC" };

        /// <summary>Rout 909's globals at their defaults (levels 100 %, the bare 0–63 scales in the middle, no shuffle).</summary>
        static JsonArray Rout909Defaults()
        {
            var list = new List<(string, int)>();
            foreach (var v in Rout909Voices) list.Add(($"{v} Level", 128));
            foreach (var p in new[] { "BD Tune", "BD Attack", "BD Decay", "SD Tune", "SD Tone", "SD Snap", "LT Tune", "LT Decay", "MT Tune", "MT Decay",
                                      "HT Tune", "HT Decay", "CH Decay", "OH Decay", "CC Tune", "RC Tune" })
                list.Add((p, 32));
            list.Add(("Shuffle", 0));
            list.Add(("Shuffle Step", 2));
            return Raw(list.ToArray());
        }

        /// <summary>
        /// A copy of 909.bmx whose machine "909 " plays <paramref name="hits"/> (row, trigger column) in its 16-row pattern, every
        /// other cell empty; the path of the copy.
        /// </summary>
        static string Rout909Song(string label, params (int Row, int Column)[] hits)
        {
            string template = Path.Combine(Repo(), "ExternalMachines", "source", "buzztrax-buzzmachines", "Oomek", "Exciter", "909.bmx");
            if (!File.Exists(template)) Assert.Inconclusive($"{template} not found");
            var song = DrumHits.WithTrackRows(File.ReadAllBytes(template), "909 ", "00",
                (row, track, column) => track == 0 && hits.Any(h => h.Row == row && h.Column == column) ? 1 : 255);
            string path = Path.Combine(Path.GetTempPath(), $"neo-drums-calibration-{label}-{Guid.NewGuid():N}.bmx");
            File.WriteAllBytes(path, song);
            return path;
        }

        /// <summary>Renders the song (60 BPM, so its 16 rows last 4 s) and describes what <paramref name="probe"/> played.</summary>
        static string RenderHit(Client client, string probe, bool tonal, double tail = 1.5)
        {
            var render = Quiet(client, "render", Args(("tail_seconds", tail), ("probes", new JsonArray(probe))));
            string file = render["probes"]!.AsArray().First()!["file"]!.GetValue<string>();
            var (x, rate) = DrumHits.ReadMono(file);
            return $"{rate} Hz: " + DrumHits.Describe(x, rate, tonal);
        }

        /// <summary>
        /// Measurements behind the Rout 909 and FSM KickXP → Neo Drums mappings (not assertions): each Rout 909 voice alone at its
        /// defaults and at the ends of its own scales, its levels, the open hat cut by the closed one; FSM KickXP's kick at its
        /// defaults and each of its parameters at its ends.
        /// </summary>
        [Explicit("measurements for Neo Drums' successor pairs; run by name (test_filter) when a mapping is recalibrated")]
        [TestCase("BD")]
        [TestCase("SD")]
        [TestCase("LT")]
        [TestCase("MT")]
        [TestCase("HT")]
        [TestCase("RS")]
        [TestCase("CP")]
        [TestCase("CH")]
        [TestCase("OH")]
        [TestCase("CC")]
        [TestCase("RC")]
        [TestCase("KickXP")]
        public void DrumsCalibration(string voice)
        {
            using var client = StartAgent(out string bin);
            void Report(string line) => TestContext.Out.WriteLine("[notice] " + line);
            WarmUp(client, "Jeskola Reverb");
            if (voice == "KickXP")
            {
                if (!Has(bin, "Generators", "FSM KickXP")) Assert.Inconclusive("FSM KickXP is not in this build's Gear");
                foreach (var (label, pitch, changes) in new (string, int, (string, int)[])[]
                         {
                             ("defaults, A1", 33, new (string, int)[0]),
                             ("defaults, A0", 21, new (string, int)[0]),
                             ("defaults, A2", 45, new (string, int)[0]),
                             ("defaults, A3", 57, new (string, int)[0]),
                             ("Start 1", 21, new[] { ("Start", 1) }),
                             ("Start 240", 21, new[] { ("Start", 240) }),
                             ("End 1", 21, new[] { ("End", 1) }),
                             ("End 240", 21, new[] { ("End", 240) }),
                             ("T DecRate 1", 21, new[] { ("T DecRate", 1) }),
                             ("T DecRate 240", 21, new[] { ("T DecRate", 240) }),
                             ("T DecShape 1", 21, new[] { ("T DecShape", 1) }),
                             ("T DecShape 240", 21, new[] { ("T DecShape", 240) }),
                             ("Buzz 0, Click 0, Punch 0", 21, new[] { ("Buzz", 0), ("Click", 0), ("Punch", 0) }),
                             ("Buzz 100, Click 0, Punch 0", 21, new[] { ("Buzz", 100), ("Click", 0), ("Punch", 0) }),
                             ("Click 100, Buzz 0, Punch 0", 21, new[] { ("Click", 100), ("Buzz", 0), ("Punch", 0) }),
                             ("Punch 100, Buzz 0, Click 0", 21, new[] { ("Punch", 100), ("Buzz", 0), ("Click", 0) }),
                             ("A DecTime 1", 21, new[] { ("A DecTime", 1) }),
                             ("A DecTime 240", 21, new[] { ("A DecTime", 240) }),
                             ("A DecSlope 120", 21, new[] { ("A DecSlope", 120) }),
                             ("A DecSlope 240", 21, new[] { ("A DecSlope", 240) }),
                             ("A RelSlope 1", 21, new[] { ("A RelSlope", 1) }),
                             ("A RelSlope 240", 21, new[] { ("A RelSlope", 240) }),
                         })
                {
                    Quiet(client, "song_new", Args(("discard_unsaved", true)));
                    Quiet(client, "song_set", Args(("tempo_bpm", 60), ("ticks_per_beat", 4), ("song_end_beats", 4)));
                    Quiet(client, "machine_add", Args(("gear", "FSM KickXP"), ("name", "K")));
                    if (changes.Length > 0) Quiet(client, "param_set", Args(("machine", "K"), ("changes", OnTrack(Raw(changes)))));
                    Quiet(client, "pattern_create", Args(("machine", "K"), ("name", "00"), ("length_beats", 4)));
                    Quiet(client, "notes_write", Args(("machine", "K"), ("pattern", "00"), ("notes", new JsonArray(new JsonObject { ["pitch"] = pitch, ["start"] = 0, ["length"] = 3 }))));
                    Quiet(client, "arrangement_place", Args(("machine", "K"), ("pattern", "00"), ("at", 0)));
                    Report($"KickXP {label}: {RenderHit(client, "K", true)}");
                }
            }
            else
            {
                if (!Has(bin, "Generators", "Rout 909")) Assert.Inconclusive("Rout 909 is not in this build's Gear");
                int column = Array.IndexOf(Rout909Voices, voice);
                bool tonal = voice is "BD" or "SD" or "LT" or "MT" or "HT" or "RS";
                var settings = new List<(string Label, (string, int)[] Changes)> { ("defaults", new (string, int)[0]) };
                void Ends(string param, params int[] raws)
                {
                    foreach (int raw in raws) settings.Add(($"{param} {raw}", new[] { (param, raw) }));
                }
                Ends($"{voice} Level", 0, 64, 192, 254);
                switch (voice)
                {
                    case "BD":
                        Ends("BD Tune", 0, 16, 48, 63);
                        Ends("BD Attack", 0, 16, 48, 63);
                        Ends("BD Decay", 0, 16, 48, 63);
                        break;
                    case "SD":
                        Ends("SD Tune", 0, 16, 48, 63);
                        Ends("SD Tone", 0, 16, 48, 63);
                        Ends("SD Snap", 0, 16, 48, 63);
                        break;
                    case "LT":
                    case "MT":
                    case "HT":
                        Ends($"{voice} Tune", 0, 16, 48, 63);
                        Ends($"{voice} Decay", 0, 16, 48, 63);
                        break;
                    case "CH":
                    case "OH":
                        Ends($"{voice} Decay", 0, 16, 48, 63);
                        break;
                    case "CC":
                    case "RC":
                        Ends($"{voice} Tune", 0, 16, 48, 63);
                        break;
                }
                string single = Rout909Song(voice, (0, column));
                foreach (var (label, changes) in settings)
                {
                    Quiet(client, "song_open", Args(("path", single), ("discard_unsaved", true)));
                    Quiet(client, "song_set", Args(("tempo_bpm", 60), ("ticks_per_beat", 4)));
                    Quiet(client, "param_set", Args(("machine", "909 "), ("changes", Join(Rout909Defaults(), Raw(changes)))));
                    Report($"909 {voice} {label}: {RenderHit(client, "909 ", tonal, voice is "CC" or "RC" ? 3 : 1.5)}");
                }
                if (voice == "OH")
                {
                    // The open hat, then the closed one half a second later (row 2 at 60 BPM): does it cut the open one?
                    string choke = Rout909Song("choke", (0, column), (2, Array.IndexOf(Rout909Voices, "CH")));
                    Quiet(client, "song_open", Args(("path", choke), ("discard_unsaved", true)));
                    Quiet(client, "song_set", Args(("tempo_bpm", 60), ("ticks_per_beat", 4)));
                    Quiet(client, "param_set", Args(("machine", "909 "), ("changes", Join(Rout909Defaults(), Raw(("OH Decay", 63))))));
                    Report($"909 OH (decay 63), CH 0.5 s later: {RenderHit(client, "909 ", false)}");
                    try { File.Delete(choke); } catch (IOException) { }
                }
                // A one-bar beat of the voices the repo's songs use, at 120 BPM, to compare whole patterns by.
                if (voice == "BD")
                {
                    string beat = Rout909Song("beat", (0, 0), (4, 1), (8, 0), (12, 1), (0, 7), (2, 7), (4, 7), (6, 8), (8, 7), (10, 7), (12, 7), (14, 8), (12, 6));
                    Quiet(client, "song_open", Args(("path", beat), ("discard_unsaved", true)));
                    Quiet(client, "song_set", Args(("tempo_bpm", 120), ("ticks_per_beat", 4)));
                    Quiet(client, "param_set", Args(("machine", "909 "), ("changes", Rout909Defaults())));
                    var render = Quiet(client, "render", Args(("tail_seconds", 1), ("probes", new JsonArray("909 "))));
                    string id = render["probes"]!.AsArray().First()!["id"]!.GetValue<string>();
                    var a = Quiet(client, "analyze", Args(("source", id), ("metrics", new JsonArray("loudness", "spectrum", "peaks"))));
                    Report($"909 beat at its defaults: {a["loudness"]?["integrated_lufs"]} LUFS, peak {a["peaks"]?["sample_peak_dbfs"]} dBFS, " +
                           $"thirds {a["spectrum"]?["third_octave"]?["db"]?.ToJsonString()}");
                    try { File.Delete(beat); } catch (IOException) { }
                }
                try { File.Delete(single); } catch (IOException) { }
            }
            Assert.That(client.CloseAndWait(60), Is.True, client.Errors);
        }
    }
}
