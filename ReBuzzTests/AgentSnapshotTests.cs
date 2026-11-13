using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using ReBuzz.Core.SongHistory;
using Rig = ReBuzzTests.AgentServerTests.Rig;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/history-snapshots.md HS2): snapshot_save, snapshot_list, snapshot_restore, snapshot_diff and
    /// snapshot_delete as full-song snapshots, the old parameters-only behaviour as kind="parameters", and the automatic
    /// snapshots before machine_replace-class operations and big history jumps.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class AgentSnapshotTests
    {
        static JsonObject Ok(Rig rig, string tool, JsonObject args = null)
        {
            var r = rig.Call(tool, args);
            Assert.That(r.Error, Is.False, $"{tool}: {r.Text}");
            TestContext.Out.WriteLine($"{tool}: {r.Text}");
            return r.Data;
        }

        static IDisposable Folder()
        {
            string root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "agent-snapshots-" + Guid.NewGuid().ToString("N"));
            string old = SongSnapshots.UntitledRoot;
            SongSnapshots.UntitledRoot = root;
            return new Restore(() =>
            {
                SongSnapshots.UntitledRoot = old;
                try { Directory.Delete(root, true); } catch (IOException) { }
            });
        }

        sealed class Restore : IDisposable
        {
            readonly Action action;
            public Restore(Action action) { this.action = action; }
            public void Dispose() => action();
        }

        [Test]
        public void FullSnapshotsSaveListDiffRestoreAndDelete()
        {
            using var folder = Folder();
            using var rig = new Rig(NeoMachineTests.Poly);
            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Pad" });
            Ok(rig, "pattern_create", new JsonObject { ["machine"] = "Pad", ["name"] = "P", ["length_beats"] = 4 });
            var saved = Ok(rig, "snapshot_save", new JsonObject { ["name"] = "A" });
            Assert.That(saved["kind"]!.GetValue<string>(), Is.EqualTo("full"));
            Assert.That(saved["id"]!.GetValue<string>(), Is.Not.Empty);

            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Bass" });
            Ok(rig, "param_set", new JsonObject { ["machine"] = "Pad", ["changes"] = new JsonArray(new JsonObject { ["param"] = "Volume", ["value"] = "-6 dB" }) });
            Ok(rig, "snapshot_save", new JsonObject { ["name"] = "B" });

            var list = Ok(rig, "snapshot_list", new JsonObject { ["kind"] = "full" })["snapshots"]!.AsArray();
            Assert.That(list.Select(s => s!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "A", "B" }));
            Assert.That(list.All(s => s!["trigger"]!.GetValue<string>() == "manual"));

            var diff = Ok(rig, "snapshot_diff", new JsonObject { ["a"] = "A", ["b"] = "B" })["changes"]!.AsArray();
            Assert.That(diff.Any(c => c!["area"]!.GetValue<string>() == "machines" && c["subject"]!.GetValue<string>() == "Bass"), "the added machine");
            Assert.That(diff.Any(c => c!["area"]!.GetValue<string>() == "parameters" && c["subject"]!.GetValue<string>() == "Pad"), "the parameter");
            Assert.That(Ok(rig, "snapshot_diff", new JsonObject { ["a"] = "B" })["changes"]!.AsArray(), Is.Empty, "B is the song now");

            // Saving under a name again replaces the manual snapshot of that name.
            Ok(rig, "snapshot_save", new JsonObject { ["name"] = "B" });
            Assert.That(Ok(rig, "snapshot_list", new JsonObject { ["kind"] = "full" })["snapshots"]!.AsArray().Count(s => s!["name"]!.GetValue<string>() == "B"), Is.EqualTo(1));

            var restored = Ok(rig, "snapshot_restore", new JsonObject { ["name"] = "A" });
            Assert.That(restored["changed"]!.GetValue<int>(), Is.GreaterThan(0));
            var machines = Ok(rig, "machine_list")["machines"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()).ToList();
            Assert.That(machines, Does.Contain("Pad").And.Not.Contain("Bass"), "the song is as A had it");
            var after = Ok(rig, "snapshot_list", new JsonObject { ["kind"] = "full" })["snapshots"]!.AsArray();
            Assert.That(after.Any(s => s!["trigger"]!.GetValue<string>() == "auto" && s["name"]!.GetValue<string>().StartsWith("Auto: before restoring")), "the way back");
            Ok(rig, "snapshot_restore", new JsonObject { ["name"] = after.First(s => s!["trigger"]!.GetValue<string>() == "auto")!["id"]!.GetValue<string>() });
            Assert.That(Ok(rig, "machine_list")["machines"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()), Does.Contain("Bass"), "back through the auto snapshot");

            Ok(rig, "snapshot_delete", new JsonObject { ["name"] = "A" });
            var bad = rig.Call("snapshot_restore", new JsonObject { ["name"] = "A" });
            Assert.That(bad.Error, Is.True);
            Assert.That(bad.Text, Does.Contain("snapshot_list"));
        }

        [Test]
        public void ParameterSnapshotsKeepTheirOldBehaviourAndRestoreWaitsForStop()
        {
            using var folder = Folder();
            using var rig = new Rig(NeoMachineTests.Poly);
            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Pad" });
            Ok(rig, "snapshot_save", new JsonObject { ["name"] = "light", ["kind"] = "parameters" });
            Ok(rig, "machine_set", new JsonObject { ["machine"] = "Pad", ["mute"] = true });
            var list = Ok(rig, "snapshot_list")["snapshots"]!.AsArray();
            Assert.That(list.Single(s => s!["name"]!.GetValue<string>() == "light")!["kind"]!.GetValue<string>(), Is.EqualTo("parameters"));
            Ok(rig, "snapshot_restore", new JsonObject { ["name"] = "light", ["kind"] = "parameters" });
            Ok(rig, "undo");                                            // a parameters restore is one undo step, as before

            Ok(rig, "snapshot_save", new JsonObject { ["name"] = "whole" });
            rig.Driver.Core.Playing = true;
            try
            {
                var refused = rig.Call("snapshot_restore", new JsonObject { ["name"] = "whole" });
                Assert.That(refused.Error, Is.True);
                Assert.That(refused.Text, Does.Contain("Stop to restore"));
            }
            finally { rig.Driver.Core.Playing = false; }
            Ok(rig, "snapshot_delete", new JsonObject { ["name"] = "light", ["kind"] = "parameters" });
            Assert.That(Ok(rig, "snapshot_list", new JsonObject { ["kind"] = "parameters" })["snapshots"]!.AsArray(), Is.Empty);
        }

        [Test]
        public void ABigHistoryJumpTakesAnAutoSnapshotAndTheToolsAreMarkedForWhatTheyDo()
        {
            using var folder = Folder();
            using var rig = new Rig(NeoMachineTests.Poly);
            for (int i = 0; i < 25; i++)
                Ok(rig, "song_set", new JsonObject { ["tempo_bpm"] = 100 + i });
            Ok(rig, "history_jump", new JsonObject { ["step"] = 0 });
            var autos = Ok(rig, "snapshot_list", new JsonObject { ["kind"] = "full" })["snapshots"]!.AsArray()
                .Where(s => s!["trigger"]!.GetValue<string>() == "auto").Select(s => s!["name"]!.GetValue<string>()).ToList();
            Assert.That(autos.Any(n => n.StartsWith("Auto: before jumping")), $"auto snapshots: {string.Join(", ", autos)}");

            var tools = rig.Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 9001, ["method"] = "tools/list" })["result"]!["tools"]!.AsArray();
            JsonObject Tool(string n) => tools.Single(t => t!["name"]!.GetValue<string>() == n)!.AsObject();
            Assert.That(Tool("snapshot_restore")["annotations"]!["destructiveHint"]!.GetValue<bool>(), Is.True);
            Assert.That(Tool("snapshot_delete")["annotations"]!["destructiveHint"]!.GetValue<bool>(), Is.True);
            Assert.That(Tool("snapshot_list")["annotations"]!["readOnlyHint"]!.GetValue<bool>(), Is.True);
            Assert.That(Tool("snapshot_save")["inputSchema"]!["properties"]!.AsObject().ContainsKey("kind"));
        }
    }
}
