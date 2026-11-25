// Fork (docs/specs/neo-audio.md, NA1): Neo Audio in the studio's host: the name a song used to know it by, audio kept in the song
// (embedded in the .nbl, bit exact, once however often it is used), and the agent's tools.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using libsndfile;
using NUnit.Framework;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.FileOps;
using ReBuzz.FileOps.Nbl;
using ReBuzz.MachineManagement;
using ReBuzz.Studio.Shell;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    public class NeoAudioTests
    {
        static string Work(string name)
        {
            string dir = TestTemp.Path($"neo-audio-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        /// <summary>A stereo float WAV of two tones, at a rate that isn't the song's (so an import resamples).</summary>
        static string Tones(string name, double left, double right, int rate = 32000, double seconds = 2)
        {
            string path = Work(name);
            int n = (int)(rate * seconds);
            var inter = new float[n * 2];
            for (int i = 0; i < n; i++)
            {
                inter[2 * i] = (float)(0.5 * Math.Sin(2 * Math.PI * left * i / rate));
                inter[2 * i + 1] = (float)(0.25 * Math.Sin(2 * Math.PI * right * i / rate));
            }
            using var f = SoundFile.Create(path, rate, 2, Format.SF_FORMAT_WAV | Format.SF_FORMAT_FLOAT);
            f.WriteFloat(inter, 0, n);
            return path;
        }

        static void Reopen(ReBuzzCore core, string path)
        {
            core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Assert.That(core.TryOpenSongFile(path), Is.True, "opens " + Path.GetFileName(path));
            Dialogs.Responder = null;
        }

        static JsonObject ManifestOf(string path)
        {
            using var r = new NblContainer.Reader(path);
            return (JsonObject)JsonNode.Parse(r.Manifest);
        }

        // ------------------------------------------------------------------ the name

        static Dictionary<string, IMachineDLL> Installed(params string[] names) => names.ToDictionary(n => n, n => (IMachineDLL)null, StringComparer.OrdinalIgnoreCase);

        [Test]
        public void ASongNamingSessionClipPlayerLoadsAsNeoAudioOnlyWhenTheOldOneIsNotInstalled()
        {
            Assert.That(MachineAliases.Resolve(Installed("Neo Audio"), "Session Clip Player", out bool aliased), Is.EqualTo("Neo Audio"));
            Assert.That(aliased, Is.True);
            Assert.That(MachineAliases.Resolve(Installed("session clip player", "Neo Audio"), "Session Clip Player", out aliased), Is.EqualTo("Session Clip Player"), "Classic keeps its own");
            Assert.That(aliased, Is.False);
            Assert.That(MachineAliases.Resolve(Installed(), "Session Clip Player", out aliased), Is.EqualTo("Session Clip Player"), "neither: it stays missing, as before");
            Assert.That(MachineAliases.Resolve(Installed("Neo Audio"), "Neo Audio", out aliased), Is.EqualTo("Neo Audio"));
            Assert.That(aliased, Is.False);
            Assert.That(MachineAliases.Resolve(Installed("Neo Audio"), null, out _), Is.Null);
        }

        // ------------------------------------------------------------------ the store, in the song

        [AvaloniaTest]
        public void AudioIsEmbeddedInTheNblOnceBitExactAndComesBackWithItsClips()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var layout = rig.Layout;
            layout.Ensure();
            var machine = layout.NewAudioTrack("Loops")!;
            int track = layout.FirstTrackOf(machine);
            string file = Tones("tones.wav", 440, 660);
            var c0 = layout.ImportAudio(track, 0, file, out string error);
            Assert.That(c0, Is.Not.Null, error);
            layout.InsertScene(1);
            var c1 = layout.ImportAudio(track, 1, file, out error);
            Assert.That(c1, Is.Not.Null, error);
            Assert.That(core.AudioAssets.Count, Is.EqualTo(1), "the same file twice is one asset");
            var asset = core.AudioAssets.All.Single();
            Assert.That(asset.SampleRate, Is.EqualTo(ReBuzzCore.masterInfo.SamplesPerSec), "resampled once, to the song's rate");
            Assert.That(asset.SourceSha256, Is.EqualTo(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant()));
            var left = asset.Source.Left.ToArray();
            var right = asset.Source.Right.ToArray();

            string a = Work("audio-a.nbl"), b = Work("audio-b.nbl");
            core.SaveSongCopy(a);
            var manifest = ManifestOf(a);
            Assert.That((int)manifest["minReaderVersion"], Is.EqualTo(2), "a song with audio needs a reader that knows it");
            var assets = manifest["audio"]["assets"].AsArray();
            Assert.That(assets.Count, Is.EqualTo(1), "one blob set for both clips");
            Assert.That((string)assets[0]["id"], Is.EqualTo(asset.Id));
            Assert.That((long)assets[0]["frames"], Is.EqualTo(left.Length));

            core.NewSong();
            Assert.That(core.AudioAssets.Count, Is.EqualTo(0), "a new song starts with no audio");
            Reopen(core, a);
            Assert.That(core.AudioAssets.Count, Is.EqualTo(1));
            var back = core.AudioAssets.All.Single();
            Assert.That(back.Id, Is.EqualTo(asset.Id));
            Assert.That(back.SourcePath, Is.EqualTo(file));
            Assert.That(back.Source.Left, Is.EqualTo(left), "bit exact");
            Assert.That(back.Source.Right, Is.EqualTo(right), "bit exact");
            var m = core.SongCore.MachinesList.First(x => x.Name == "Loops");
            var clip = m.ClipsList.First(p => core.SessionLayout.IsAudioClip(p));
            Assert.That(core.SessionLayout.AudioSourceOf(clip)!.Left, Is.EqualTo(left), "the clip plays the song's audio");

            core.SaveSongCopy(b);
            Assert.That(File.ReadAllBytes(b), Is.EqualTo(File.ReadAllBytes(a)), "saved again: the same file");
        }

        [AvaloniaTest]
        public void AWaveBasedClipOfAnOldSongMovesIntoTheStoreAndTheWaveStaysUntouched()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var layout = rig.Layout;
            layout.Ensure();
            var machine = layout.NewAudioTrack("Old")!;
            int track = layout.FirstTrackOf(machine);
            int rate = ReBuzzCore.masterInfo.SamplesPerSec;
            var audio = Enumerable.Range(0, rate).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * 300 * i / rate))).ToArray();
            var wavetable = core.SongCore.Wavetable;
            int index = Enumerable.Range(0, wavetable.Waves.Count).First(i => wavetable.Waves[i] == null);
            wavetable.AllocateWave(index, "", "old wave", audio.Length, WaveFormat.Float32, false, 65, false, true);
            var layer = wavetable.Waves[index].Layers[0];
            layer.SampleRate = rate;
            layer.SetDataAsFloat(audio, 0, 1, 0, 0, audio.Length);

            var clip = layout.AddAudioClip(track, 0, machine, "old clip", new SessionDocument.AudioSettings { Wave = index, Bpm = 120, Warp = "Beats" });
            Assert.That(clip, Is.Not.Null);
            var source = layout.AudioSourceOf(clip)!;
            Assert.That(source.Left, Is.EqualTo(audio), "it plays the wave's audio");
            Assert.That(core.AudioAssets.Count, Is.EqualTo(1), "now the song holds a copy");
            Assert.That(core.AudioAssets.All.Single().OriginWave, Is.EqualTo(index));
            Assert.That(layout.AudioOf(clip)!.Asset, Is.EqualTo(core.AudioAssets.All.Single().Id));
            var after = new float[audio.Length];
            wavetable.Waves[index].Layers[0].GetDataAsFloat(after, 0, 1, 0, 0, audio.Length);
            Assert.That(after, Is.EqualTo(audio), "the wave is untouched");
        }

        // ------------------------------------------------------------------ saving for upstream

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
        public void TheAgentImportsAudioAndReadsAndChangesTheClip()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            string file = Tones("agent.wav", 440, 660);

            var imported = Tool("audio_import", new JsonObject { ["path"] = file, ["slot"] = 2 });
            Assert.That(imported["isError"]?.GetValue<bool>() ?? false, Is.False, imported.ToJsonString());
            var s = imported["structuredContent"]!.AsObject();
            string machine = (string)s["machine"], clip = (string)s["clip"];
            Assert.That((int)s["slot"], Is.EqualTo(2));
            Assert.That((string)s["clip"]["warp"], Is.EqualTo("Pro"), "a new clip starts in Pro");
            Assert.That((double)s["clip"]["audio"]["seconds"], Is.EqualTo(2).Within(0.05));
            Assert.That(core.AudioAssets.Count, Is.EqualTo(1));

            var set = Tool("audio_clip_set", new JsonObject { ["machine"] = machine, ["clip"] = clip, ["warp"] = "Complex", ["transpose"] = -3, ["detune"] = 20, ["gain_db"] = -6, ["loop"] = false, ["tempo_bpm"] = 90 });
            Assert.That(set["isError"]?.GetValue<bool>() ?? false, Is.False, set.ToJsonString());
            var get = Tool("audio_clip_get", new JsonObject { ["machine"] = machine, ["clip"] = clip })["structuredContent"]!.AsObject();
            Assert.That((string)get["warp"], Is.EqualTo("Complex"));
            Assert.That((int)get["transpose"], Is.EqualTo(-3));
            Assert.That((int)get["detune"], Is.EqualTo(20));
            Assert.That((double)get["gain_db"], Is.EqualTo(-6).Within(0.01));
            Assert.That((double)get["tempo_bpm"], Is.EqualTo(90).Within(0.01));
            Assert.That((bool)get["loop"], Is.False);

            var bad = Tool("audio_clip_set", new JsonObject { ["machine"] = machine, ["clip"] = clip, ["warp"] = "Sideways" });
            Assert.That(bad["isError"]?.GetValue<bool>(), Is.True, "an unknown warp mode is refused");
            var missing = Tool("audio_import", new JsonObject { ["path"] = Work("nothing.wav") });
            Assert.That(missing["isError"]?.GetValue<bool>(), Is.True, "no such file");
        }

        [AvaloniaTest]
        public void TheProWarpIsSetByTheAgentAndSurvivesASaveAndAReopen()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            string file = Tones("pro.wav", 440, 660);
            var imported = Tool("audio_import", new JsonObject { ["path"] = file, ["slot"] = 1 })["structuredContent"]!.AsObject();
            string machine = (string)imported["machine"], clip = (string)imported["clip"];

            var set = Tool("audio_clip_set", new JsonObject { ["machine"] = machine, ["clip"] = clip, ["warp"] = "pro", ["transpose"] = 4 });
            Assert.That(set["isError"]?.GetValue<bool>() ?? false, Is.False, set.ToJsonString());
            var get = Tool("audio_clip_get", new JsonObject { ["machine"] = machine, ["clip"] = clip })["structuredContent"]!.AsObject();
            Assert.That((string)get["warp"], Is.EqualTo("Pro"));
            // Where the native library is not beside the app the clip plays as Complex and says so; where it is, there is nothing to say.
            Assert.That((string)get["warp_playing"] ?? "Complex", Is.EqualTo("Complex"));
            Assert.That(core.SessionLayout.ClipOf(core.SongCore.MachinesList.First(m => m.Name == machine).ClipsList.First(p => p.Name == clip)).Audio.Warp, Is.EqualTo("Pro"));

            string path = Work("pro.nbl");
            core.SaveSongCopy(path);
            core.NewSong();
            Reopen(core, path);
            var back = Tool("audio_clip_get", new JsonObject { ["machine"] = machine, ["clip"] = clip })["structuredContent"]!.AsObject();
            Assert.That((string)back["warp"], Is.EqualTo("Pro"), "saved as Complex with a marker, read as Pro");
            Assert.That((int)back["transpose"], Is.EqualTo(4));
        }

        [AvaloniaTest]
        public void ANewClipStartsInProAnOldClipKeepsItsModeAndASaveAndAReopenChangesNeitherOfThem()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            string file = Tones("default.wav", 440, 660);
            (string Machine, string Clip) Import(int slot)
            {
                var r = Tool("audio_import", new JsonObject { ["path"] = file, ["slot"] = slot })["structuredContent"]!.AsObject();
                return ((string)r["machine"], (string)r["clip"]);
            }
            string Warp((string Machine, string Clip) c) => (string)Tool("audio_clip_get", new JsonObject { ["machine"] = c.Machine, ["clip"] = c.Clip })["structuredContent"]!["warp"];

            var oldClip = Import(1);                       // a clip of an old song: Beats, which that song saved as no attribute at all
            Tool("audio_clip_set", new JsonObject { ["machine"] = oldClip.Machine, ["clip"] = oldClip.Clip, ["warp"] = "Beats" });
            var newClip = Import(2);                       // a clip made now
            var unwarped = Import(3);                      // a file with no tempo is left as it is until a tempo is set: warp Off
            Tool("audio_clip_set", new JsonObject { ["machine"] = unwarped.Machine, ["clip"] = unwarped.Clip, ["warp"] = "Off" });
            Assert.That((Warp(oldClip), Warp(newClip), Warp(unwarped)), Is.EqualTo(("Beats", "Pro", "Off")));

            string path = Work("modes.nbl");
            core.SaveSongCopy(path);
            core.NewSong();
            Reopen(core, path);
            Assert.That((Warp(oldClip), Warp(newClip), Warp(unwarped)), Is.EqualTo(("Beats", "Pro", "Off")), "after a save and a reopen");

            // A tempo set on the unwarped clip is what it was waiting for: it follows the song now, in the mode a new clip starts in.
            var set = Tool("audio_clip_set", new JsonObject { ["machine"] = unwarped.Machine, ["clip"] = unwarped.Clip, ["tempo_bpm"] = 100 });
            Assert.That(set["isError"]?.GetValue<bool>() ?? false, Is.False, set.ToJsonString());
            Assert.That(Warp(unwarped), Is.EqualTo("Pro"));
        }
    }
}
