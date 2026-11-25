// Fork (docs/specs/neo-audio.md, NA1b): disk streaming in a song. Long audio is decoded once into the cache, plays from it, is saved and
// reopened bit exact (from the cache when it is there, from the song's blobs when it is not), and a cache file lost or damaged is made again
// from the song file's blobs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using libsndfile;
using NUnit.Framework;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Clips;
using ReBuzz.FileOps;
using ReBuzz.FileOps.Nbl;
using ReBuzz.MachineManagement;
using ReBuzz.Studio.Shell;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    public class NeoAudioStreamingSongTests
    {
        static string Work(string name)
        {
            string dir = TestTemp.Path($"neo-stream-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        static string Tones(string name, double seconds)
        {
            string path = Work(name);
            int rate = 32000, n = (int)(rate * seconds);
            var inter = new float[n * 2];
            for (int i = 0; i < n; i++)
            {
                inter[2 * i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / rate) * (0.7 + 0.3 * Math.Sin(i * 0.0003)));
                inter[2 * i + 1] = (float)(0.25 * Math.Sin(2 * Math.PI * 660 * i / rate));
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

        static bool WaitFor(Func<bool> done, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                if (done()) return true;
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            return done();
        }

        sealed class Settings : IDisposable
        {
            readonly string folder;
            readonly long threshold, resident;
            readonly bool enabled;
            public Settings(string cache)
            {
                folder = AudioCache.DataFolder;
                threshold = StreamingPolicy.ThresholdBytes;
                resident = StreamingPolicy.ResidentBytes;
                enabled = StreamingPolicy.Enabled;
                Directory.CreateDirectory(cache);
                AudioCache.DataFolder = cache;
                StreamingPolicy.ThresholdBytes = 1024 * 1024;
                StreamingPolicy.Enabled = true;
            }
            public void Dispose()
            {
                AudioCache.DataFolder = folder;
                StreamingPolicy.ThresholdBytes = threshold;
                StreamingPolicy.ResidentBytes = resident;
                StreamingPolicy.Enabled = enabled;
            }
        }

        [AvaloniaTest]
        public void LongAudioStreamsFromTheCacheIsSavedAndReopenedBitExactAndTheCacheIsMadeAgainFromTheSongsBlobs()
        {
            string cache = Work("cache-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            using var settings = new Settings(cache);
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var layout = rig.Layout;
            layout.Ensure();
            var machine = layout.NewAudioTrack("Long")!;
            int track = layout.FirstTrackOf(machine);
            var clip = layout.ImportAudio(track, 0, Tones("long.wav", 14), out string error);
            Assert.That(clip, Is.Not.Null, error);
            var asset = core.AudioAssets.All.Single();
            var (left, right) = asset.Source.ToArrays();
            Assert.That(left.Length * 8L, Is.GreaterThan(StreamingPolicy.ThresholdBytes), "longer than the threshold");

            // 1. It streams: the cache is written in the background, then the floats go.
            Assert.That(WaitFor(() => asset.Storage == AssetStorage.Streaming, 60_000), Is.True, "the cache file is made and the audio streams");
            Assert.That(asset.Source.InMemory, Is.False);
            Assert.That(File.Exists(AudioCache.PathFor(asset.Id)), Is.True);
            Assert.That(asset.Recipe, Is.Not.Null, "the import file is the first way to make the cache again");

            var get = Tool("audio_clip_get", new JsonObject { ["machine"] = machine.Name, ["pattern"] = clip!.Name })["structuredContent"]!.AsObject();
            var audio = get["audio"]!.AsObject();
            Assert.That((string)audio["storage"], Is.EqualTo("streaming"));
            Assert.That((bool)audio["streams"], Is.True);
            Assert.That((long)audio["underruns"], Is.EqualTo(0));

            // 2. It is saved from the cache (exact reads) and the file holds the audio once, as for audio in memory.
            string a = Work("long-a.nbl"), b = Work("long-b.nbl");
            core.SaveSongCopy(a);
            var manifest = JsonNode.Parse(new NblContainer.Reader(a).Manifest)!.AsObject();
            Assert.That((string)manifest["audio"]!["assets"]![0]!["id"], Is.EqualTo(asset.Id));
            Assert.That((long)manifest["audio"]!["assets"]![0]!["frames"], Is.EqualTo(left.Length));

            // 3. Reopened with the cache there: no decoding, it streams at once, and it is the same bits.
            core.NewSong();
            Assert.That(core.AudioAssets.Count, Is.EqualTo(0));
            Reopen(core, a);
            var again = core.AudioAssets.All.Single();
            Assert.That(again.Storage, Is.EqualTo(AssetStorage.Streaming), "opened from the cache file");
            Assert.That(again.Source.InMemory, Is.False, "never in memory");
            var (l2, r2) = again.Source.ToArrays();
            Assert.That(l2, Is.EqualTo(left), "bit exact");
            Assert.That(r2, Is.EqualTo(right));
            core.SaveSongCopy(b);
            Assert.That(File.ReadAllBytes(b), Is.EqualTo(File.ReadAllBytes(a)), "saved again from the streamed audio: the same file");

            // 4. Reopened with no cache: decoded from the blobs, plays from memory while the cache is written, then streams.
            core.NewSong();
            foreach (var f in Directory.GetFiles(cache)) File.Delete(f);
            Reopen(core, a);
            var third = core.AudioAssets.All.Single();
            Assert.That(WaitFor(() => third.Storage == AssetStorage.Streaming, 60_000), Is.True, "written again from the song");
            Assert.That(File.Exists(AudioCache.PathFor(third.Id)), Is.True);
            Assert.That(third.Source.ToArrays().Left, Is.EqualTo(left));
            Assert.That(third.Recipe, Is.InstanceOf<NblRecipe>(), "the song file is where it is made again from");

            // 5. The cache file is lost and its bytes are not to be trusted: the song's blobs make it again, without the audio in memory.
            string path = AudioCache.PathFor(third.Id);
            File.Delete(path);
            third.Cache!.MarkDamaged("A piece fails its checksum.");
            Assert.That(WaitFor(() => File.Exists(path) && third.Storage == AssetStorage.Streaming && !third.Cache.Damaged, 60_000), Is.True, "rebuilt from the blobs");
            Assert.That(third.Source.ToArrays().Right, Is.EqualTo(right), "bit exact");
            Assert.That(third.Source.InMemory, Is.False);

            // 6. The recipe on its own: a fresh cache file from the song's file, checked against the asset's id.
            File.Delete(path);
            using (var w = new AudioCache.Writer(third.Id, third.Frames, third.Channels, third.SampleRate))
            {
                Assert.That(new NblRecipe(a, third.Id).Rebuild(w, third, default), Is.True);
                Assert.That(w.Complete(), Is.True);
            }
            Assert.That(StreamedAudio.IsWhole(third.Id, third.Frames, third.Channels, third.SampleRate), Is.True);
            using var w2 = new AudioCache.Writer(third.Id, third.Frames, third.Channels, third.SampleRate);
            Assert.That(new NblRecipe(a, "a0000000000000000").Rebuild(w2, third, default), Is.False, "an id the song does not hold");
        }

        [AvaloniaTest]
        public void ShortAudioStaysInMemoryAndTheAgentSaysSo()
        {
            string cache = Work("cache-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            using var settings = new Settings(cache);
            StreamingPolicy.ThresholdBytes = 64L * 1024 * 1024;
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var imported = Tool("audio_import", new JsonObject { ["path"] = Tones("short.wav", 3), ["slot"] = 1 })["structuredContent"]!.AsObject();
            var get = Tool("audio_clip_get", new JsonObject { ["machine"] = (string)imported["machine"], ["pattern"] = (string)imported["pattern"] })["structuredContent"]!.AsObject();
            Assert.That((string)get["audio"]!["storage"], Is.EqualTo("memory"));
            Assert.That((bool)get["audio"]!["streams"], Is.False);
            Assert.That(Directory.GetFiles(cache), Is.Empty, "no cache file for audio that stays in memory");
            var status = Tool("studio_status")["structuredContent"]!.AsObject();
            if (status["audio"] is JsonObject audio) Assert.That((long)audio["stream_underruns"], Is.GreaterThanOrEqualTo(0));
        }

        /// <summary>
        /// Consolidate and every offline render read a clip's audio through ClipEditor.Exact: for a streamed clip that is the exact audio in memory,
        /// not the audio thread's lossy page reads, so the render equals the render of the same audio held in memory (no holes where pages were not loaded).
        /// </summary>
        [AvaloniaTest]
        public void AnOfflineRenderOfAStreamedClipEqualsTheRenderOfTheSameAudioInMemory()
        {
            string cache = Work("cache-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            using var settings = new Settings(cache);
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var layout = rig.Layout;
            layout.Ensure();
            var machine = layout.NewAudioTrack("Long")!;
            var clip = layout.ImportAudio(layout.FirstTrackOf(machine), 0, Tones("render.wav", 14), out string error);
            Assert.That(clip, Is.Not.Null, error);
            var asset = core.AudioAssets.All.Single();
            Assert.That(WaitFor(() => asset.Storage == AssetStorage.Streaming, 60_000), Is.True);
            var streamed = layout.ParamsOfClip(clip!);
            Assert.That(streamed.Source.IsStreaming, Is.True, "the clip plays from the cache");

            var exact = ClipEditor.Exact(streamed);
            Assert.That(exact.Source.IsStreaming, Is.False, "an offline render holds the audio");
            var (l, r) = streamed.Source.ToArrays();
            var memory = new AudioClipParams(new AudioSource(l, r, streamed.Source.SampleRate), streamed.Map, streamed.Mode, streamed.Slices, streamed.Gain, streamed.Semitones) { Fades = streamed.Fades };

            // Straight through the streamed source with nothing prefetched (what the audio thread would read) leaves holes; the exact one does not.
            var a = new ClipMixer(); var b = new ClipMixer();
            var pa = new ClipPlayback { Params = exact }; var pb = new ClipPlayback { Params = memory };
            int rate = 44100, blocks = 600;
            double bps = 2.0 / rate;                                  // 120 bpm
            double peak = 0;
            for (int i = 0; i < blocks; i++)
            {
                a.Begin(256); b.Begin(256);
                a.Play(pa, i * 256 * bps, bps, rate, 256); b.Play(pb, i * 256 * bps, bps, rate, 256);
                a.End(256, bps, rate); b.End(256, bps, rate);
                for (int k = 0; k < 256; k++)
                {
                    Assert.That(a.Left[k], Is.EqualTo(b.Left[k]), $"block {i} sample {k}");
                    Assert.That(a.Right[k], Is.EqualTo(b.Right[k]));
                    peak = Math.Max(peak, Math.Abs(a.Left[k]));
                }
            }
            Assert.That(peak, Is.GreaterThan(0.05), "the render is not silent");
        }
    }
}
