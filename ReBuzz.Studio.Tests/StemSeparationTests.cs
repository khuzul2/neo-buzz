// Fork (docs/specs/stem-separation.md): stem separation in the studio's host, with a model built in the test (TinyOnnx), so no real weights
// are needed: the agent's audio_separate (tracks named after the stems, clips aligned with the source, one undo step, the cache), the
// dialog's flow, the menus, and the model folder.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using libsndfile;
using NUnit.Framework;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.AudioLibrary.Stems;
using ReBuzz.SessionTests.Stems;
using ReBuzz.Studio.Shell;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    public class StemSeparationTests
    {
        static readonly float[] Gains = { 0.4f, 0.3f, 0.2f, 0.1f };          // vocals, drums, bass, other

        static string Work(string name)
        {
            string dir = TestTemp.Path($"stems-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        static string Tones(string name, int rate = 44100, double seconds = 2)
        {
            string path = Work(name);
            int n = (int)(rate * seconds);
            var inter = new float[n * 2];
            for (int i = 0; i < n; i++)
            {
                inter[2 * i] = (float)(0.5 * Math.Sin(2 * Math.PI * 220 * i / rate));
                inter[2 * i + 1] = (float)(0.25 * Math.Sin(2 * Math.PI * 330 * i / rate));
            }
            using var f = SoundFile.Create(path, rate, 2, Format.SF_FORMAT_WAV | Format.SF_FORMAT_FLOAT);
            f.WriteFloat(inter, 0, n);
            return path;
        }

        /// <summary>Points the stem models at a folder with one little "Best" model (stems are the input times 0.4, 0.3, 0.2, 0.1) and the cache at a fresh one.</summary>
        static IDisposable TinyModel(string folder, bool installed = true)
        {
            string oldData = StemModels.DataFolder, oldGear = StemModels.GearFolder;
            StemModels.DataFolder = Work("stem-data-" + folder);
            StemModels.GearFolder = Work("stem-gear-" + folder);
            StemModels.UserFolder = "";
            if (installed)
            {
                const int nfft = 64, hop = 16, segment = 2048;
                string dir = Path.Combine(StemModels.GearFolder, "tiny");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "m.onnx"), TinyOnnx.SpecMask(nfft / 2 + 1, 1 + segment / hop, Gains));
                var outputs = new JsonObject { ["vocals"] = new JsonArray(0), ["drums"] = new JsonArray(1), ["bass"] = new JsonArray(2), ["other"] = new JsonArray(3) };
                var manifest = new JsonObject
                {
                    ["id"] = "tiny", ["quality"] = "Best", ["title"] = "Tiny test model", ["family"] = "spec-mask", ["sampleRate"] = 44100, ["segment"] = segment,
                    ["overlap"] = 0.25, ["nfft"] = nfft, ["hop"] = hop, ["outputStems"] = 4,
                    ["stems"] = new JsonArray("vocals", "drums", "bass", "other"),
                    ["passes"] = new JsonArray(new JsonObject { ["file"] = "m.onnx", ["outputs"] = outputs }),
                };
                File.WriteAllText(Path.Combine(dir, "model.json"), manifest.ToJsonString());
            }
            return Studio.AfterTest(() => { StemModels.DataFolder = oldData; StemModels.GearFolder = oldGear; });
        }

        static JsonObject Tool(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(90);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            return task.GetAwaiter().GetResult()["result"]!.AsObject();
        }

        static void Pump(Task t)
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            while (!t.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail("the task didn't finish in time");
                Thread.Sleep(2);
            }
            t.GetAwaiter().GetResult();
        }

        /// <summary>The machines a person sees (each audio track has a hidden pattern-editor machine beside it).</summary>
        static int Visible(ReBuzzCore core) => core.SongCore.MachinesList.Count(m => !m.Hidden);

        static double Worst(float[] stem, float[] source, float gain)
        {
            double w = 0;
            for (int i = 0; i < Math.Min(stem.Length, source.Length); i++) w = Math.Max(w, Math.Abs(stem[i] - source[i] * gain));
            return w;
        }

        [AvaloniaTest]
        public void TheAgentSeparatesAClipIntoNamedTracksAlignedWithItInOneUndoStep()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            using var models = TinyModel("agent");
            var imported = Tool("audio_import", new JsonObject { ["path"] = Tones("agent.wav"), ["slot"] = 3, ["at_beats"] = 4 });
            Assert.That(imported["isError"]?.GetValue<bool>() ?? false, Is.False, imported.ToJsonString());
            var s = imported["structuredContent"]!.AsObject();
            string machine = (string)s["machine"], clip = (string)s["pattern"];
            var sourceClip = core.SongCore.MachinesList.First(m => m.Name == machine).ClipsList.First(p => p.Name == clip);
            int machinesBefore = Visible(core), sequencesBefore = core.SongCore.SequencesList.Count;
            var sourceEvent = core.SongCore.SequencesList.First(q => q.Machine.Name == machine).Events.Single(e => e.Value.Clip == sourceClip);

            var r = Tool("audio_separate", new JsonObject { ["machine"] = machine, ["pattern"] = clip, ["quality"] = "Best", ["stems"] = new JsonArray("bass", "drums") });
            Assert.That(r["isError"]?.GetValue<bool>() ?? false, Is.False, r.ToJsonString());
            var o = r["structuredContent"]!.AsObject();
            Assert.That((string)o["model"], Is.EqualTo("tiny"));
            Assert.That((bool)o["from_cache"], Is.False);
            var tracks = o["tracks"]!.AsArray().Select(t => t!.AsObject()).ToList();
            Assert.That(tracks.Select(t => (string)t["stem"]), Is.EqualTo(new[] { "drums", "bass" }), "the app's order, vocals drums bass other");
            Assert.That(tracks.Select(t => (string)t["machine"]), Is.EqualTo(new[] { "Drums - agent", "Bass - agent" }), "named after the stem");
            Assert.That(Visible(core), Is.EqualTo(machinesBefore + 2));
            Assert.That(core.SongCore.SequencesList.Count, Is.EqualTo(sequencesBefore + 2));
            Assert.That(core.AudioAssets.Count, Is.EqualTo(3), "the source and two stems");

            var layout = core.SessionLayout;
            var source = layout.AudioSourceOf(sourceClip)!;
            foreach (var (t, gain) in new[] { (tracks[0], Gains[1]), (tracks[1], Gains[2]) })
            {
                var m = core.SongCore.MachinesList.First(x => x.Name == (string)t["machine"]);
                var stemClip = m.ClipsList.Single(p => layout.IsAudioClip(p));
                Assert.That((int)t["slot"], Is.EqualTo((int)s["slot"]), "the source's Session scene");
                Assert.That(layout.ClipAt(layout.FirstTrackOf(m), (int)s["slot"] - 1), Is.SameAs(stemClip));
                Assert.That(stemClip.Length, Is.EqualTo(sourceClip.Length), "the same length");
                Assert.That(layout.ClipOf(stemClip).Audio.Bpm, Is.EqualTo(layout.ClipOf(sourceClip).Audio.Bpm).Within(1e-9));
                var stem = layout.AudioSourceOf(stemClip)!;
                Assert.That(stem.Length, Is.EqualTo(source.Length));
                Assert.That(Worst(stem.Left, source.Left, gain), Is.LessThan(2e-3), (string)t["machine"]);
                Assert.That(Worst(stem.Right, source.Right, gain), Is.LessThan(2e-3), (string)t["machine"]);
                var seq = core.SongCore.SequencesList.First(q => q.Machine == m);
                var at = seq.Events.Single();
                Assert.That(at.Key, Is.EqualTo(sourceEvent.Key), "the same arrangement position");
                Assert.That(at.Value.Clip, Is.SameAs(stemClip));
            }

            // one undo step takes every track away
            core.SongCore.ActionStack.Undo();
            Assert.That(Visible(core), Is.EqualTo(machinesBefore));
            Assert.That(core.SongCore.SequencesList.Count, Is.EqualTo(sequencesBefore));

            // the same again is from the cache
            var again = Tool("audio_separate", new JsonObject { ["machine"] = machine, ["pattern"] = clip, ["quality"] = "Best", ["stems"] = new JsonArray("bass") });
            Assert.That(again["isError"]?.GetValue<bool>() ?? false, Is.False, again.ToJsonString());
            Assert.That((bool)again["structuredContent"]!["from_cache"], Is.True);
        }

        [AvaloniaTest]
        public void TheAgentRefusesWhatItCannotDoAndSaysWhy()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            using var models = TinyModel("refuse");
            var imported = Tool("audio_import", new JsonObject { ["path"] = Tones("refuse.wav") })["structuredContent"]!.AsObject();
            string machine = (string)imported["machine"], clip = (string)imported["pattern"];

            var noModel = Tool("audio_separate", new JsonObject { ["machine"] = machine, ["pattern"] = clip, ["quality"] = "Balanced" });
            Assert.That(noModel["isError"]?.GetValue<bool>(), Is.True);
            Assert.That(noModel["content"]![0]!["text"]!.GetValue<string>(), Does.Contain("Install models"));
            var both = Tool("audio_separate", new JsonObject { ["machine"] = machine, ["pattern"] = clip, ["path"] = Tones("x.wav"), ["quality"] = "Best" });
            Assert.That(both["isError"]?.GetValue<bool>(), Is.True, "a clip or a path, not both");
            var neither = Tool("audio_separate", new JsonObject { ["quality"] = "Best" });
            Assert.That(neither["isError"]?.GetValue<bool>(), Is.True);
            var badStem = Tool("audio_separate", new JsonObject { ["machine"] = machine, ["pattern"] = clip, ["quality"] = "Best", ["stems"] = new JsonArray("guitar") });
            Assert.That(badStem["isError"]?.GetValue<bool>(), Is.True);
            var noFile = Tool("audio_separate", new JsonObject { ["path"] = Work("nothing.wav"), ["quality"] = "Best" });
            Assert.That(noFile["isError"]?.GetValue<bool>(), Is.True);
            Assert.That(Visible(core), Is.EqualTo(2), "the master and nothing else: a refused call changes nothing");
        }

        [AvaloniaTest]
        public void TheAgentSeparatesAFileIntoTheFirstSceneAndTheArrangement()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            using var models = TinyModel("file");
            string file = Tones("fromfile.wav", rate: 32000);
            var r = Tool("audio_separate", new JsonObject { ["path"] = file, ["quality"] = "Best", ["stems"] = new JsonArray("vocals"), ["slot"] = 2, ["at_beats"] = 8 });
            Assert.That(r["isError"]?.GetValue<bool>() ?? false, Is.False, r.ToJsonString());
            var t = r["structuredContent"]!["tracks"]![0]!.AsObject();
            Assert.That((string)t["machine"], Is.EqualTo("Vocals - fromfile"));
            Assert.That((int)t["slot"], Is.EqualTo(2));
            Assert.That((double)t["at_beats"]![0]!, Is.EqualTo(8).Within(1e-9));
            Assert.That(core.AudioAssets.Count, Is.EqualTo(1), "the file itself isn't added to the song, only its stem");
        }

        [AvaloniaTest]
        public void TheDialogsFlowPutsTheStemsOnNewTracksAndTheMenusOfferIt()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; StemFlow.Responder = null; StemFlow.ZipPicker = null; StemFlow.FilePicker = null; core.Modified = false; });
            using var models = TinyModel("flow");
            var imported = Tool("audio_import", new JsonObject { ["path"] = Tones("flow.wav") })["structuredContent"]!.AsObject();
            string machine = (string)imported["machine"], clipName = (string)imported["pattern"];
            var clip = core.SongCore.MachinesList.First(m => m.Name == machine).ClipsList.First(p => p.Name == clipName);
            var view = rig.Show();
            int track = core.SessionLayout.FirstTrackOf(core.SongCore.MachinesList.First(m => m.Name == machine));
            int scene = (int)imported["slot"] - 1;

            view.ShowSlotMenu(view.SlotAt(track, scene));
            Assert.That(view.LastMenu.Items.OfType<MenuItem>().Any(i => i.Header as string == "Separate stems…" || i.Tag as string == "Separate stems"), Is.True, "an audio clip's menu offers it");

            string asked = null;
            StemFlow.Responder = name => { asked = name; return new StemChoice(StemQuality.Best, new[] { "drums", "other" }); };
            Pump(StemFlow.SeparateClip(clip));
            Assert.That(asked, Is.EqualTo("flow"));
            Assert.That(StemFlow.LastOutcomes.Select(x => x.Stem), Is.EqualTo(new[] { "drums", "other" }));
            Assert.That(StemFlow.LastSummary, Does.Contain("drums, other"));
            Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Drums - flow"), Is.True);

            StemFlow.Responder = _ => null;
            int machines = Visible(core);
            Pump(StemFlow.SeparateClip(clip));
            Assert.That(Visible(core), Is.EqualTo(machines), "cancel changes nothing");

            // a file, as the browser's menu and File > Separate Stems from Audio File ask for it
            StemFlow.Responder = _ => new StemChoice(StemQuality.Best, new[] { "bass" });
            Pump(StemFlow.SeparateFile(Tones("other.wav")));
            Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Bass - other"), Is.True);
            StemFlow.FilePicker = () => Tones("picked.wav");
            Pump(StemFlow.SeparateFile());
            Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Bass - picked"), Is.True);
        }

        [AvaloniaTest]
        public void InstallModelsTakesTheZipAndMissingOnesAreGreyedWithAHint()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            using var restore = Studio.AfterTest(() => { StemFlow.ZipPicker = null; });
            using var models = TinyModel("install", installed: false);
            Assert.That(StemModels.For(StemQuality.Best), Is.Null);
            Assert.That(StemModels.Missing(StemQuality.Best), Does.Contain("Install models"));

            // a zip the workflow's way: the models' folders at the top
            string src = Work("zip-src");
            if (Directory.Exists(src)) Directory.Delete(src, true);
            Directory.CreateDirectory(Path.Combine(src, "tiny"));
            File.WriteAllBytes(Path.Combine(src, "tiny", "m.onnx"), TinyOnnx.SpecMask(33, 129, Gains));
            File.WriteAllText(Path.Combine(src, "tiny", "model.json"),
                "{\"id\":\"tiny\",\"quality\":\"Best\",\"family\":\"spec-mask\",\"segment\":2048,\"nfft\":64,\"hop\":16,\"outputStems\":4," +
                "\"passes\":[{\"file\":\"m.onnx\",\"outputs\":{\"vocals\":[0],\"drums\":[1],\"bass\":[2],\"other\":[3]}}]}");
            string zip = Work("models.zip");
            if (File.Exists(zip)) File.Delete(zip);
            System.IO.Compression.ZipFile.CreateFromDirectory(src, zip, System.IO.Compression.CompressionLevel.Fastest, false);
            StemFlow.ZipPicker = () => zip;
            string said = null;
            var task = StemFlow.InstallModels();
            Pump(task);
            said = task.Result;
            Assert.That(said, Does.StartWith("Installed tiny"));
            Assert.That(StemModels.For(StemQuality.Best)?.Id, Is.EqualTo("tiny"));
            Assert.That(StemModels.Missing(StemQuality.Fast), Is.Not.Null, "the others are still missing");

            var page = new ReBuzz.Studio.Shell.Preferences.StemsPage(null);
            page.Refresh();
            Assert.That(page.FoundText.Text, Does.Contain("Best: Tiny test model").Or.Contain("Best: tiny").Or.Contain("Best: "));
            Assert.That(page.FoundText.Text, Does.Contain("Fast: not installed"));
        }
    }
}
