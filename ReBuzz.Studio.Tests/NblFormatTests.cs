// Fork (docs/specs/native-format.md, NF1): the native song file, .nbl. The container is deterministic, checked and
// written atomically; migrations chain; what a build doesn't know is kept; a song saves, opens and saves again to the same
// bytes; names are UTF-8 and positions wide; old songs become .nbl and come back the same. Every fork feature's own tests
// also run with .nbl (DawModesSeamTests, ArrangementViewTests, ArrangementRecordingTests, AutomationLaneTests,
// DyeStressTests with more than 256 MIDI bindings, SongNotesTests); the embedded history is in SnapshotTests.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReBuzz.Agent.Mcp;
using Avalonia.Headless.NUnit;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.SongHistory;
using ReBuzz.FileOps;
using ReBuzz.FileOps.Nbl;
using ReBuzz.Studio.Shell;
using ReBuzzTests.Automation;

namespace ReBuzz.Studio.Tests
{
    public class NblFormatTests
    {
        static string Work(string name)
        {
            string dir = TestTemp.Path($"nbl-format-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        static byte[] Noise(int n, int seed)
        {
            var b = new byte[n];
            new Random(seed).NextBytes(b);
            return b;
        }

        static JsonObject ManifestOf(string path)
        {
            using var r = new NblContainer.Reader(path);
            return (JsonObject)JsonNode.Parse(r.Manifest);
        }

        // ------------------------------------------------------------------ the container

        [Test]
        public void TheBytesDependOnlyOnTheContent()
        {
            var a = new NblBlobSet();
            var b = new NblBlobSet();
            var x = Noise(300_000, 1);
            var y = Encoding.UTF8.GetBytes(new string('a', 5000));
            a.AddChunked(x); a.Add(y); a.Add(y);
            b.Add(y); b.AddChunked(x);
            var manifest = Encoding.UTF8.GetBytes("{\"format\":\"t\"}");
            Assert.That(a.Count, Is.EqualTo(b.Count), "the same bytes are one blob");
            Assert.That(NblContainer.Build(manifest, a), Is.EqualTo(NblContainer.Build(manifest, b)), "whatever order they came in");
            Assert.That(a.Count, Is.GreaterThan(2), "the noise was cut in chunks");
        }

        [Test]
        public void BlobsComeBackCheckedAndDamageOrACutIsRefused()
        {
            var set = new NblBlobSet();
            var data = Noise(1000, 2);
            var text = Encoding.UTF8.GetBytes(new string('z', 4000));
            var d = set.Add(data);
            var t = set.Add(text);
            string path = Work("container.nbl");
            NblContainer.WriteAtomic(path, Encoding.UTF8.GetBytes("{}"), set);
            using (var r = new NblContainer.Reader(path))
            {
                Assert.That(r.Get(d.Hash), Is.EqualTo(data), "stored raw: noise doesn't deflate");
                Assert.That(r.Get(t.Hash), Is.EqualTo(text), "deflated");
                Assert.That(r.GetStored(t.Hash).Stored.Length, Is.LessThan(text.Length));
            }
            Assert.That(NblContainer.LooksLikeOne(path), Is.True);
            var bytes = File.ReadAllBytes(path);
            string damaged = Work("damaged.nbl"), cut = Work("cut.nbl");
            var bad = (byte[])bytes.Clone();
            bad[bad.Length / 2] ^= 0x40;
            File.WriteAllBytes(damaged, bad);
            File.WriteAllBytes(cut, bytes.Take(bytes.Length - 5).ToArray());
            Assert.Throws<InvalidDataException>(() => new NblContainer.Reader(damaged));
            Assert.Throws<InvalidDataException>(() => new NblContainer.Reader(cut));
        }

        [Test]
        public void ASaveThatFailsLeavesTheOldFileWholeAndNoTemporaryFile()
        {
            string dir = Work("atomic");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "song.nbl");
            var one = new NblBlobSet();
            one.Add(Noise(100, 3));
            NblContainer.WriteAtomic(path, Encoding.UTF8.GetBytes("{\"v\":1}"), one);
            byte[] before = File.ReadAllBytes(path);
            Assert.That(Directory.GetFiles(dir), Is.EqualTo(new[] { path }), "a finished save leaves no temporary file");

            string target = Path.Combine(dir, "folder.nbl");
            Directory.CreateDirectory(target);                      // the rename onto a folder fails, after the temporary file is written
            Assert.That(() => NblContainer.WriteAtomic(target, Encoding.UTF8.GetBytes("{\"v\":2}"), one),
                Throws.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>(), "(Windows says access denied)");
            Assert.That(Directory.GetFiles(dir), Is.EqualTo(new[] { path }), "the failed save removed its temporary file");
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "and the old file is as it was");
        }

        // ------------------------------------------------------------------ documents and migrations

        [Test]
        public void XmlSubsectionsAreHeldAsJsonOnlyWhenTheyComeBackExactly()
        {
            byte[] markers = Encoding.UTF8.GetBytes("<SongMarkers version=\"1\"><Marker name=\"Intro &amp; ü\" tick=\"0\" /><Marker name=\"Drop\" tick=\"64\" /></SongMarkers>");
            var json = NblXml.TryToJson(markers);
            Assert.That(json, Is.Not.Null);
            Assert.That((string)json["element"], Is.EqualTo("SongMarkers"));
            Assert.That((string)json["children"][0]["attrs"]["name"], Is.EqualTo("Intro & ü"));
            Assert.That(NblXml.ToBytes(json), Is.EqualTo(markers), "the same bytes");
            var notes = NblXml.TryToJson(Encoding.UTF8.GetBytes("<SongNotes><Text>line 1 | line 2 音</Text></SongNotes>"));   // (a newline would be written as CR LF on Windows)
            Assert.That((string)notes["children"][0]["text"], Is.EqualTo("line 1 | line 2 音"));

            Assert.That(NblXml.TryToJson(Encoding.UTF8.GetBytes("<a><!-- c --><b/></a>")), Is.Null, "a comment: kept as a blob");
            Assert.That(NblXml.TryToJson(Encoding.UTF8.GetBytes("<a>\n  <b/>\n</a>")), Is.Null, "formatting whitespace: kept as a blob");
            Assert.That(NblXml.TryToJson(new byte[] { 1, 2, 3, 4 }), Is.Null, "binary");
            Assert.That(NblXml.TryToJson(Array.Empty<byte>()), Is.Null);
        }

        [Test]
        public void MigrationsRunInOrderCanUseBlobsAndANewerFileIsRefused()
        {
            string path = Work("migrate.nbl");
            var set = new NblBlobSet();
            var old = set.Add(Encoding.UTF8.GetBytes("old payload"));
            NblContainer.WriteAtomic(path, Encoding.UTF8.GetBytes("{}"), set);
            using var reader = new NblContainer.Reader(path);
            var ctx = new NblReadContext(reader);

            var steps = new NblMigrations();
            var seen = new List<int>();
            steps.Register(1, (m, c) =>
            {
                seen.Add(1);
                m["payload"] = Encoding.UTF8.GetString(c.Get((string)m["payloadBlob"]));
                m.Remove("payloadBlob");
            });
            steps.Register(2, (m, c) =>
            {
                seen.Add(2);
                m["payloadBlob"] = c.Add(Encoding.UTF8.GetBytes((string)m["payload"] + " v3"));
            });
            var manifest = new JsonObject { ["formatVersion"] = 1, ["payloadBlob"] = old.Hash };
            steps.Migrate(manifest, ctx, 3);
            Assert.That(seen, Is.EqualTo(new[] { 1, 2 }));
            Assert.That((int)manifest["formatVersion"], Is.EqualTo(3));
            Assert.That(Encoding.UTF8.GetString(ctx.Get((string)manifest["payloadBlob"])), Is.EqualTo("old payload v3"));

            Assert.Throws<InvalidDataException>(() => steps.Migrate(new JsonObject { ["formatVersion"] = 3 }, ctx, 5), "no step from 3");
            var newer = new JsonObject { ["formatVersion"] = 7, ["minReaderVersion"] = 7 };
            Assert.Throws<InvalidDataException>(() => NblMigrations.Default.Migrate(newer, ctx), "needs a newer reader");
            var tolerated = new JsonObject { ["formatVersion"] = 7, ["minReaderVersion"] = 1 };
            NblMigrations.Default.Migrate(tolerated, ctx);
            Assert.That((int)tolerated["formatVersion"], Is.EqualTo(7), "a newer file that says older readers can read it is read as far as it is known");
        }

        // ------------------------------------------------------------------ a song

        static void Reopen(ReBuzzCore core, string path)
        {
            core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Assert.That(core.TryOpenSongFile(path), Is.True, "opens " + Path.GetFileName(path));
            Dialogs.Responder = null;
        }

        [AvaloniaTest]
        public void ASongSavesAndOpensAndSavesToTheSameBytesWithItsForkData()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = SnapshotTests.Song("Lead", "Bass");
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => Dialogs.Responder = null);
            core.MarkerStore.Set("Intro ü", 0);
            core.MarkerStore.Set("Drop", 64);
            core.SongNotes.Write("# Plan\n\nnotes 音 €");
            core.SongMeter.Set(0, 12);
            string a = Work("a.nbl"), b = Work("b.nbl"), c = Work("c.nbl");
            core.SaveSongCopy(a);
            core.SaveSongCopy(c);
            Assert.That(File.ReadAllBytes(c), Is.EqualTo(File.ReadAllBytes(a)), "the same song saved twice: the same file");

            var before = core.CaptureSongSections();
            Reopen(core, a);
            Assert.That(core.MarkerStore.All.Select(m => (m.Name, m.Tick)), Is.EqualTo(new[] { ("Intro ü", 0), ("Drop", 64) }));
            Assert.That(core.SongNotes.Text, Is.EqualTo("# Plan\n\nnotes 音 €"));
            Assert.That(core.SongMeter.Entries[0], Is.EqualTo(12));
            Assert.That(core.SongCore.MachinesList.Where(m => !m.Hidden).Select(m => m.Name).ToArray(), Is.EqualTo(new[] { "Master", "Lead", "Bass" }));
            var after = core.CaptureSongSections();
            Assert.That(after.Select(s => s.Magic), Is.EqualTo(before.Select(s => s.Magic)));
            for (int i = 0; i < before.Count; i++)
                Assert.That(after[i].Data, Is.EqualTo(before[i].Data), $"section {i} ({before[i].Magic:x8}) is the same after the round trip");
            core.SaveSongCopy(b);
            Assert.That(File.ReadAllBytes(b), Is.EqualTo(File.ReadAllBytes(a)), "saved again from the opened song: the same file");

            var manifest = ManifestOf(a);
            Assert.That((string)manifest["format"], Is.EqualTo("neobuzz-live-song"));
            Assert.That((int)manifest["formatVersion"], Is.EqualTo(1));
            Assert.That(manifest["stores"]["SongMarkers"]["kind"].ToString(), Is.EqualTo("xml"), "markers are manifest fields");
            Assert.That(manifest["stores"]["SongNotes"]["kind"].ToString(), Is.EqualTo("xml"));
            Assert.That(manifest["index"]["machines"].AsArray().Select(m => (string)m["name"]).ToArray(), Does.Contain("Lead").And.Contain("Bass"));
            Assert.That(manifest["core"]["sections"].AsArray().Select(s => (string)s["id"]), Does.Contain("MACH").And.Contain("SEQU").And.Contain("PATT"));
        }

        [AvaloniaTest]
        public void NamesAreUtf8AndLongPositionsAndManyBindingsKeepTheirValues()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = SnapshotTests.Song("Lead");
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => Dialogs.Responder = null);
            var lead = rig.Machine("Lead");
            lead.Name = "Léad 音樂 €";
            var pattern = lead.Patterns.First();
            pattern.Name = "Pattern ☃";
            var seq = core.SongCore.SequencesList.First(s => s.Machine == lead);
            seq.SetEvent(100_000, new SequenceEvent(SequenceEventType.PlayPattern, (PatternCore)pattern));
            string path = Work("utf8.nbl");
            core.SaveSongCopy(path);
            Reopen(core, path);
            var back = core.SongCore.MachinesList.First(m => m.Name.StartsWith("L"));
            Assert.That(back.Name, Is.EqualTo("Léad 音樂 €"), "a name with characters .bmx can't hold");
            Assert.That(back.Patterns.Select(p => p.Name), Does.Contain("Pattern ☃"));
            var events = core.SongCore.SequencesList.First(s => s.Machine == back).Events;
            Assert.That(events.ContainsKey(100_000), Is.True, "an event past 65,535 ticks");
        }

        [AvaloniaTest]
        public void WhatABuildDoesNotKnowIsKeptWhenTheSongIsSavedAgain()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = SnapshotTests.Song("Lead");
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => Dialogs.Responder = null);
            string a = Work("known.nbl"), future = Work("future.nbl"), again = Work("again.nbl");
            core.SaveSongCopy(a);

            // A file from a later version: a member and a store this build has never heard of, with blobs.
            var payload = Noise(200_000, 9);
            var set = new NblBlobSet();
            JsonObject manifest;
            using (var r = new NblContainer.Reader(a))
            {
                manifest = (JsonObject)JsonNode.Parse(r.Manifest);
                foreach (var h in r.Hashes) { var (codec, stored) = r.GetStored(h); set.AddStored(h, codec, stored); }
            }
            var refs = set.AddChunked(payload);
            manifest["future"] = new JsonObject { ["hello"] = "world", ["data"] = new JsonArray(refs.Select(x => (JsonNode)new JsonObject { ["blob"] = x.Hash, ["size"] = x.Size }).ToArray()) };
            manifest["stores"]["FutureStore"] = new JsonObject { ["kind"] = "binary", ["size"] = payload.Length, ["chunks"] = new JsonArray(refs.Select(x => (JsonNode)new JsonObject { ["blob"] = x.Hash, ["size"] = x.Size }).ToArray()) };
            manifest["formatVersion"] = 2;
            manifest["minReaderVersion"] = 1;
            NblContainer.WriteAtomic(future, Encoding.UTF8.GetBytes(manifest.ToJsonString()), set);

            Reopen(core, future);
            Assert.That(core.NativeCarry, Is.Not.Null);
            core.SaveSongCopy(again);
            var back = ManifestOf(again);
            Assert.That((string)back["future"]["hello"], Is.EqualTo("world"), "an unknown top-level member");
            Assert.That(back["stores"]["FutureStore"], Is.Not.Null, "an unknown store");
            Assert.That((int)back["formatVersion"], Is.EqualTo(1), "written as this build's version");
            using (var r = new NblContainer.Reader(again))
            {
                var joined = back["future"]["data"].AsArray().SelectMany(x => r.Get((string)x["blob"])).ToArray();
                Assert.That(joined, Is.EqualTo(payload), "and the blobs they refer to");
            }

            core.NewSong();
            Assert.That(core.NativeCarry, Is.Null, "a new song carries nothing");
        }

        [AvaloniaTest]
        public void AnOldSongIsSavedAsANewNblAndTheOriginalIsNotTouched()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = SnapshotTests.Song("Lead");
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { SongFiles.Picker = null; Dialogs.Responder = null; });
            string old = Work("old.bmx");
            core.SaveSongCopy(old);
            byte[] original = File.ReadAllBytes(old);
            Reopen(core, old);
            Assert.That(core.LegacyOrigin, Is.EqualTo(old));
            core.MarkerStore.Set("Edited", 8);                                    // a change to save

            string target = Work("old-as-new.nbl");
            int asked = 0;
            SongFiles.Picker = saving => { asked++; return target; };
            Assert.That(SongFiles.Save().Result, Is.True);
            Assert.That(asked, Is.EqualTo(1), "Save asked for a name: it is Save As");
            Assert.That(File.ReadAllBytes(old), Is.EqualTo(original), "the .bmx is as it was");
            Assert.That(NblContainer.LooksLikeOne(target), Is.True);
            Assert.That(core.SongCore.SongName, Is.EqualTo(target));
            Assert.That(core.LegacyOrigin, Is.Null);
            core.MarkerStore.Set("More", 16);
            Assert.That(SongFiles.Save().Result, Is.True);
            Assert.That(asked, Is.EqualTo(1), "and the next Save goes to the .nbl");
        }

        // ------------------------------------------------------------------ the agent

        static JsonObject Tool(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            return task.GetAwaiter().GetResult()["result"]!.AsObject();
        }

        [AvaloniaTest]
        public void TheSongToolsSaveAndOpenNblAndNeverOverwriteAnOpenedOldSong()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = SnapshotTests.Song("Lead");
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            string a = Work("agent-a.nbl");
            var saved = Tool("song_save", new JsonObject { ["path"] = a, ["overwrite"] = true });
            Assert.That(saved["isError"]?.GetValue<bool>() == true, Is.False, saved.ToJsonString());
            Assert.That(NblContainer.LooksLikeOne(a), Is.True);

            string bare = Work("agent-bare");
            var named = Tool("song_save", new JsonObject { ["path"] = bare, ["overwrite"] = true });
            Assert.That(named["isError"]?.GetValue<bool>() == true, Is.False, named.ToJsonString());
            Assert.That(NblContainer.LooksLikeOne(bare + ".nbl"), Is.True, "a path with no extension gets the format's (nbl by default)");
            var xml = Tool("song_save", new JsonObject { ["path"] = Work("agent-x"), ["format"] = "bmxml", ["overwrite"] = true });
            Assert.That(xml["isError"]?.GetValue<bool>() == true, Is.False);
            Assert.That(File.Exists(Work("agent-x.bmxml")), Is.True);

            string old = Work("agent-old.bmx");
            core.SaveSongCopy(old);
            byte[] original = File.ReadAllBytes(old);
            var opened = Tool("song_open", new JsonObject { ["path"] = old, ["discard_unsaved"] = true });
            Assert.That(opened["isError"]?.GetValue<bool>() == true, Is.False, opened.ToJsonString());
            core.MarkerStore.Set("agent", 4);
            var beside = Tool("song_save");
            Assert.That(beside["isError"]?.GetValue<bool>() == true, Is.False, beside.ToJsonString());
            Assert.That(File.ReadAllBytes(old), Is.EqualTo(original), "the opened .bmx is untouched");
            Assert.That(NblContainer.LooksLikeOne(Work("agent-old.nbl")), Is.True, "the new .nbl is beside it");
            var again = Tool("song_open", new JsonObject { ["path"] = Work("agent-old.nbl"), ["discard_unsaved"] = true });
            Assert.That(again["isError"]?.GetValue<bool>() == true, Is.False, again.ToJsonString());
            Assert.That(core.MarkerStore.All.Select(m => m.Name), Does.Contain("agent"));
        }

        // ------------------------------------------------------------------ the demo songs

        static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "ExternalMachines", "source"))) return dir.FullName;
            return null;
        }

        /// <summary>The songs tools/song-roundtrip.py uses: the first 12 .bmx under ExternalMachines/source, in path order.</summary>
        internal static List<string> DemoSongs()
        {
            string root = RepoRoot();
            if (root == null) return new List<string>();
            return Directory.GetFiles(Path.Combine(root, "ExternalMachines", "source"), "*.bmx", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Take(12).ToList();
        }

        [AvaloniaTest]
        public void TheDemoSongsBecomeNblAndComeBackTheSame()
        {
            var songs = DemoSongs();
            if (songs.Count == 0) Assert.Inconclusive("the demo songs aren't beside the tests");
            var core = Studio.Host.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var table = new StringBuilder();
            table.AppendLine("song | .bmx in | .bmx saved | .nbl | save .bmx ms | save .nbl ms | open .bmx ms | open .nbl ms");
            int opened = 0;
            foreach (var song in songs)
            {
                core.Modified = false;
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                var sw = Stopwatch.StartNew();
                if (!core.TryOpenSongFile(song)) continue;
                double openBmx = sw.Elapsed.TotalMilliseconds;
                opened++;
                string name = Path.GetFileNameWithoutExtension(song);
                string x = Work(name + ".x.bmx"), nbl = Work(name + ".nbl"), y = Work(name + ".y.bmx"), nbl2 = Work(name + ".2.nbl");
                sw.Restart(); core.SaveSongCopy(x); double saveBmx = sw.Elapsed.TotalMilliseconds;
                sw.Restart(); core.SaveSongCopy(nbl); double saveNbl = sw.Elapsed.TotalMilliseconds;

                core.Modified = false;
                sw.Restart();
                Assert.That(core.TryOpenSongFile(nbl), Is.True, name);
                double openNbl = sw.Elapsed.TotalMilliseconds;
                core.SaveSongCopy(y);
                Assert.That(File.ReadAllBytes(y), Is.EqualTo(File.ReadAllBytes(x)), $"{name}: the .nbl gives back the song the .bmx gave: the same .bmx when saved again");
                core.SaveSongCopy(nbl2);
                Assert.That(File.ReadAllBytes(nbl2), Is.EqualTo(File.ReadAllBytes(nbl)), $"{name}: and the same .nbl");
                table.AppendLine($"{name} | {new FileInfo(song).Length:N0} | {new FileInfo(x).Length:N0} | {new FileInfo(nbl).Length:N0} | {saveBmx:F0} | {saveNbl:F0} | {openBmx:F0} | {openNbl:F0}");
            }
            TestContext.Progress.WriteLine(table.ToString());
            Assert.That(opened, Is.GreaterThanOrEqualTo(Math.Min(6, songs.Count)), "most of the demo songs open here (the rest need machines this platform lacks)");
        }
    }
}
