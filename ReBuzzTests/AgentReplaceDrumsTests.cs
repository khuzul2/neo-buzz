using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/mcp-server.md §12.5, §12.10): Rout 909 and FSM KickXP onto Neo Drums. Both are native and load
    /// only here. A beat on Rout 909's trigger columns (written by automation_write, as played parameters) and KickXP's kicks
    /// from notes and its Trigger column convert, every hit with them; the repo's songs with Rout 909 move over with their
    /// patterns and arrangement. The comparisons go into the log as "[notice]" lines (tools/ci-test-summary.ps1); the beats at
    /// the old machines' defaults must be within their mappings' tolerance.
    /// </summary>
    public partial class AgentHeadlessTests
    {
        /// <summary>A played parameter's hits at <paramref name="beats"/>, each at <paramref name="raw"/>.</summary>
        static JsonArray Hits(int raw, params double[] beats) =>
            new(beats.Select(b => (JsonNode)new JsonObject { ["at"] = b, ["value"] = $"raw:{raw}" }).ToArray());

        [Test]
        public void DrumMachinesMoveOntoNeoDrums()
        {
            using var client = StartAgent(out string bin);
            if (!File.Exists(Path.Combine(bin, "Gear", "Generators", "Neo Drums.NET.dll"))) Assert.Inconclusive("Neo Drums not in this build's Gear");
            var results = new List<(string Label, bool? Within)>();
            var report = new List<string>();
            WarmUp(client, "Jeskola Reverb");

            // Rout 909: two bars of every drum, on its trigger columns (track 1), at its defaults and at the ends of a few scales.
            var beat = new (string Column, double[] Beats)[]
            {
                ("BD Trigger", new[] { 0.0, 1, 2, 3, 4, 5, 6, 7 }),
                ("SD Trigger", new[] { 1.0, 3, 5, 7 }),
                ("CH Trigger", new[] { 0.5, 1.5, 2.5, 3.5, 4.5, 5.5, 6.5 }),
                ("OH Trigger", new[] { 7.5 }),
                ("CP Trigger", new[] { 7.0 }),
                ("RS Trigger", new[] { 2.75, 6.75 }),
                ("LT Trigger", new[] { 6.0 }),
                ("MT Trigger", new[] { 6.25 }),
                ("HT Trigger", new[] { 6.5 }),
                ("CC Trigger", new[] { 0.0 }),
                ("RC Trigger", new[] { 4.0, 4.5, 5.0 }),
            };
            int hits = beat.Sum(b => b.Beats.Length);
            if (Has(bin, "Generators", "Rout 909"))
            {
                foreach (var (label, settings) in new (string, JsonArray)[]
                         {
                             ("Rout 909 beat at its defaults", null),
                             ("Rout 909 beat, BD Tune 0, BD Decay 63, SD Tune 63, SD Snap 63, CH Decay 0, CC Tune 63, BD Level 64",
                              Raw(("BD Tune", 0), ("BD Decay", 63), ("SD Tune", 63), ("SD Snap", 63), ("CH Decay", 0), ("CC Tune", 63), ("BD Level", 64))),
                         })
                {
                    Quiet(client, "song_new", Args(("discard_unsaved", true)));
                    Quiet(client, "machine_add", Args(("gear", "Rout 909"), ("name", "909")));
                    if (settings != null) Quiet(client, "param_set", Args(("machine", "909"), ("changes", settings)));
                    Quiet(client, "pattern_create", Args(("machine", "909"), ("name", "00"), ("length_beats", 8)));
                    foreach (var (column, beats) in beat)
                    {
                        var w = Quiet(client, "automation_write", Args(("machine", "909"), ("pattern", "00"), ("param", column), ("track", 0), ("shape", "step"), ("points", Hits(1, beats))));
                        Assert.That(w["played"]?.GetValue<bool>(), Is.True, $"{column} is a played parameter");
                    }
                    Quiet(client, "arrangement_place", Args(("machine", "909"), ("pattern", "00"), ("at", 0)));
                    var r = Replace(client, label, "909");
                    Assert.That(r["patterns"]![0]!["notes"]!.GetValue<int>(), Is.EqualTo(hits), $"{label}: every hit moves");
                    results.Add((label, Within(r)));
                }
            }

            // FSM KickXP: a kick on every beat from notes (A1: the pitch falls to 55 Hz), a Trigger hit at half volume between
            // (the volume holds for the kicks after it); at its defaults, and tuned up with a slower, longer fall.
            if (Has(bin, "Generators", "FSM KickXP"))
            {
                foreach (var (label, pitch, settings) in new (string, int, JsonArray)[]
                         {
                             ("FSM KickXP at its defaults, A1", 33, null),
                             ("FSM KickXP, A2, Start 180, T DecRate 60, Buzz 0, A DecTime 60", 45, OnTrack(Raw(("Start", 180), ("T DecRate", 60), ("Buzz", 0), ("A DecTime", 60)))),
                         })
                {
                    Quiet(client, "song_new", Args(("discard_unsaved", true)));
                    Quiet(client, "machine_add", Args(("gear", "FSM KickXP"), ("name", "Kick")));
                    if (settings != null) Quiet(client, "param_set", Args(("machine", "Kick"), ("changes", settings)));
                    Quiet(client, "pattern_create", Args(("machine", "Kick"), ("name", "00"), ("length_beats", 8)));
                    var notes = new JsonArray();
                    for (int b = 0; b < 8; b++) notes.Add(new JsonObject { ["pitch"] = pitch, ["start"] = b, ["length"] = 0.5 });
                    Quiet(client, "notes_write", Args(("machine", "Kick"), ("pattern", "00"), ("notes", notes)));
                    Quiet(client, "automation_write", Args(("machine", "Kick"), ("pattern", "00"), ("param", "Trigger"), ("track", 0), ("shape", "step"), ("points", Hits(64, 3.5))));
                    Quiet(client, "arrangement_place", Args(("machine", "Kick"), ("pattern", "00"), ("at", 0)));
                    var r = Replace(client, label, "Kick");
                    Assert.That(r["patterns"]![0]!["notes"]!.GetValue<int>(), Is.EqualTo(9), $"{label}: every kick moves");
                    results.Add((label, Within(r)));
                }
            }

            // The repo's songs with Rout 909: every Rout 909 in them moves over, its patterns and arrangement with it.
            string root = Repo();
            foreach (var song in new[]
                     {
                         "Oomek/Exciter/909.bmx", "Oomek/Exciter/909_excite.bmx", "Oomek/Exciter/test.bmx", "Oomek/Masterizer/Masterizer_test.bmx",
                         "Oomek/Aggressor/Oomek_Aggressor_demo.bmx", "Zwar/Csi/Csi.bmx",
                     })
            {
                string path = Path.Combine(root, "ExternalMachines", "source", "buzztrax-buzzmachines", song.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path) || !Has(bin, "Generators", "Rout 909")) continue;
                Quiet(client, "song_open", Args(("path", path), ("discard_unsaved", true)));
                var drums = Quiet(client, "machine_list")["machines"]!.AsArray()
                    .Where(m => m!["gear"]?.GetValue<string>() == "Rout 909").Select(m => m!["name"]!.GetValue<string>()).ToList();
                foreach (var name in drums)
                {
                    var arrangementBefore = Quiet(client, "arrangement_get");
                    var r = Replace(client, $"{Path.GetFileName(song)}, its Rout 909 \"{name}\"", name);
                    report.Add($"{Path.GetFileName(song)} \"{name}\": within {Within(r)?.ToString() ?? "-"}, patterns {r["patterns"]?.ToJsonString()}, " +
                               $"notes {r["notes"]?.ToJsonString()}");
                    var arrangementAfter = Quiet(client, "arrangement_get");
                    Assert.That(Placements(arrangementAfter, name), Is.EqualTo(Placements(arrangementBefore, name)), $"{song}: the arrangement keeps its patterns");
                }
            }
            foreach (var line in report) TestContext.Out.WriteLine(line);
            Assert.That(client.CloseAndWait(60), Is.True, client.Errors);
            Assert.That(results.Where(x => x.Label.Contains("defaults") && x.Within != true).Select(x => $"{x.Label}: {x.Within?.ToString() ?? "not compared"}"), Is.Empty,
                "the pairs at the old machines' defaults convert within their stated tolerance");
        }
    }
}
