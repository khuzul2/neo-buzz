// Fork (docs/specs/browser.md, BR1): the browser's portable parts: the drag format, the audio file headers, the sample index (search,
// the file, 100,000 entries in under 50 ms), the library (places, an incremental scan, a watcher), the collections, and the agent's tools.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ReBuzz.Agent.App;
using ReBuzz.Agent.Browser;
using ReBuzz.Agent.Mcp;
using NeoBuzz.TestSupport;

namespace ReBuzz.AgentTests
{
    [TestFixture]
    public class BrowserTests
    {
        // ---- helpers ---------------------------------------------------------------------------

        static string Temp() => TestTemp.Path("br1-" + Guid.NewGuid().ToString("N"));

        /// <summary>A 16-bit PCM WAV of silence: <paramref name="seconds"/> long.</summary>
        static void WriteWav(string path, double seconds, int rate = 44100, int channels = 1, float? acidBpm = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            int frames = (int)(seconds * rate), align = channels * 2;
            using var w = new BinaryWriter(File.Create(path));
            int acid = acidBpm != null ? 8 + 24 : 0;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(4 + 8 + 16 + acid + 8 + frames * align); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * align); w.Write((short)align); w.Write((short)16);
            if (acidBpm != null)
            {
                w.Write(Encoding.ASCII.GetBytes("acid")); w.Write(24);
                w.Write(0); w.Write((short)60); w.Write((short)0); w.Write(0f); w.Write(4); w.Write((short)4); w.Write((short)4); w.Write(acidBpm.Value);
            }
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(frames * align);
            w.Write(new byte[frames * align]);
        }

        static SampleEntry Entry(string path, string root = "/r") => new(path, root, IndexKind.Audio, 1.5, 44100, 2, 0, "", 100, 1);

        // ---- the drag format ---------------------------------------------------------------------

        [Test]
        public void TheDragFormatRoundTripsEveryKindAndRefusesWhatIsNotOne()
        {
            foreach (var kind in Enum.GetValues<BrowserKind>())
            {
                var d = new BrowserDragData(kind, kind == BrowserKind.Audio ? "/Users/x/Kick 808.wav" : "Neo Poly", kind == BrowserKind.Preset ? "Warm Bass" : null, "label");
                var back = BrowserDragData.Parse(d.Format());
                Assert.That(back, Is.EqualTo(d), kind.ToString());
            }
            Assert.That(new BrowserDragData(BrowserKind.Plugin, "Vendor/Name (VST3)").IsGear, Is.True);
            Assert.That(new BrowserDragData(BrowserKind.Audio, "x.wav").IsGear, Is.False);
            Assert.That(BrowserDragData.Parse("C:\\kick.wav"), Is.Null, "a path from elsewhere");
            Assert.That(BrowserDragData.Parse("{\"v\":2,\"kind\":\"audio\",\"id\":\"a\"}"), Is.Null, "another version");
            Assert.That(BrowserDragData.Parse("{\"v\":1,\"kind\":\"nonsense\",\"id\":\"a\"}"), Is.Null);
            Assert.That(BrowserDragData.Parse("{\"v\":1,\"kind\":\"audio\"}"), Is.Null, "no id");
            Assert.That(BrowserDragData.Parse("{broken"), Is.Null);
            Assert.That(BrowserDragData.Parse(null), Is.Null);
            Assert.That(BrowserDragData.FormatName, Is.EqualTo("neobuzz-browser-item"), "DD1's drop targets read this name");
        }

        // ---- headers -------------------------------------------------------------------------------

        [Test]
        public void AudioHeadersGiveLengthRateAndChannelsWithoutDecoding()
        {
            string dir = Temp();
            string wav = Path.Combine(dir, "Loop 128 bpm Am.wav");
            WriteWav(wav, 2.5, 48000, 2);
            Assert.That(AudioFileInfo.TryRead(wav, out var w), Is.True);
            Assert.That((w.SampleRate, w.Channels), Is.EqualTo((48000, 2)));
            Assert.That(w.Seconds, Is.EqualTo(2.5).Within(0.001));
            Assert.That(w.Bpm, Is.EqualTo(128), "from the name");
            Assert.That(w.Key, Is.EqualTo("A minor"), "from the name");

            string acid = Path.Combine(dir, "pad.wav");
            WriteWav(acid, 1, 44100, 1, 96f);
            Assert.That(AudioFileInfo.TryRead(acid, out var a), Is.True);
            Assert.That(a.Bpm, Is.EqualTo(96), "the acid chunk's tempo");

            // AIFF: 22.05 kHz, 1 channel, 11025 frames = 0.5 s (the rate is an 80-bit extended float).
            string aif = Path.Combine(dir, "x.aif");
            using (var bw = new BinaryWriter(File.Create(aif)))
            {
                void BE32(uint v) { bw.Write((byte)(v >> 24)); bw.Write((byte)(v >> 16)); bw.Write((byte)(v >> 8)); bw.Write((byte)v); }
                bw.Write(Encoding.ASCII.GetBytes("FORM")); BE32(4 + 8 + 18); bw.Write(Encoding.ASCII.GetBytes("AIFF"));
                bw.Write(Encoding.ASCII.GetBytes("COMM")); BE32(18);
                bw.Write((byte)0); bw.Write((byte)1); BE32(11025); bw.Write((byte)0); bw.Write((byte)16);
                bw.Write((byte)0x40); bw.Write((byte)0x0D); BE32(0xAC440000); BE32(0);          // 22050 = 0xAC44 * 2^(-1): exponent 0x400D, mantissa 0xAC44 << 48
            }
            Assert.That(AudioFileInfo.TryRead(aif, out var f), Is.True);
            Assert.That((f.SampleRate, f.Channels), Is.EqualTo((22050, 1)));
            Assert.That(f.Seconds, Is.EqualTo(0.5).Within(0.001));

            string junk = Path.Combine(dir, "notaudio.wav");
            File.WriteAllText(junk, "hello");
            Assert.That(AudioFileInfo.TryRead(junk, out _), Is.False, "a damaged file is not an entry");
            Assert.That(AudioFileInfo.Hints("Kick.wav"), Is.EqualTo((0.0, "")));
            Assert.That(AudioFileInfo.Hints("Dusty_Loop_92_Dm.wav").Bpm, Is.EqualTo(92));
        }

        // ---- the index -------------------------------------------------------------------------------

        [Test]
        public void SearchNeedsEveryWordAndRanksNamesThatStartWithIt()
        {
            var index = new SampleIndex();
            index.Apply(new[]
            {
                Entry("/r/Drums/Kick 808 Long.wav"), Entry("/r/Drums/808 Kick.wav"), Entry("/r/Bass/Sub 808.wav"), Entry("/r/Drums/Snare Tight.wav"), Entry("/r/Keys/Pad Warm.wav"),
            });
            Assert.That(index.Search("808").Count, Is.EqualTo(3));
            var kick = index.Search("kick 808").Select(e => e.Name).ToList();
            Assert.That(kick, Is.EquivalentTo(new[] { "Kick 808 Long", "808 Kick" }), "every word");
            Assert.That(index.Search("kick").First().Name, Does.StartWith("Kick"), "a name that starts with the word first");
            Assert.That(index.Search("drums snare").Single().Name, Is.EqualTo("Snare Tight"), "the folder's name counts");
            Assert.That(index.Search("zzz"), Is.Empty);
            Assert.That(index.Search("", 2).Count, Is.EqualTo(2), "no words: everything, limited");
            Assert.That(index.Search("808", kind: IndexKind.Midi), Is.Empty);
            Assert.That(index.Search("808", root: "/elsewhere"), Is.Empty);
        }

        [Test]
        public void TheIndexFileKeepsEverythingAndRefusesADamagedOne()
        {
            string dir = Temp();
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "browser-index.bin");
            var index = new SampleIndex();
            index.Apply(new[] { new SampleEntry("/r/a/Loop.wav", "/r", IndexKind.Audio, 2.25, 48000, 2, 120, "A minor", 1234, 99), new SampleEntry("/r/b/Song.mid", "/r", IndexKind.Midi, 0, 0, 0, 0, "", 55, 7) });
            index.Save(file);
            var back = new SampleIndex();
            Assert.That(back.Load(file), Is.True);
            Assert.That(back.Count, Is.EqualTo(2));
            var e = back.Find("/r/a/Loop.wav")!;
            Assert.That((e.Seconds, e.SampleRate, e.Channels, e.Bpm, e.Key, e.Size, e.Modified), Is.EqualTo((2.25, 48000, 2, 120.0, "A minor", 1234L, 99L)));
            Assert.That(back.Find("/r/b/Song.mid")!.Kind, Is.EqualTo(IndexKind.Midi));
            // Truncated: refused, the index left empty.
            File.WriteAllBytes(file, File.ReadAllBytes(file).Take(30).ToArray());
            var bad = new SampleIndex();
            Assert.That(bad.Load(file), Is.False);
            Assert.That(bad.Count, Is.EqualTo(0));
            Assert.That(new SampleIndex().Load(Path.Combine(dir, "missing.bin")), Is.False);
        }

        [Test]
        public void SearchOverAHundredThousandEntriesTakesUnderFiftyMilliseconds()
        {
            var rng = new Random(7);
            string[] words = { "kick", "snare", "hat", "loop", "bass", "pad", "lead", "vocal", "fx", "perc", "808", "909", "warm", "dusty", "tight", "long", "short", "chord", "arp", "break" };
            string[] folders = { "Drums", "Bass", "Keys", "FX", "Vocals", "Loops", "OneShots", "Packs" };
            var list = new List<SampleEntry>(100_000);
            for (int i = 0; i < 100_000; i++)
            {
                string name = $"{words[rng.Next(words.Length)]} {words[rng.Next(words.Length)]} {rng.Next(1000):000}";
                list.Add(new SampleEntry($"/lib/{folders[rng.Next(folders.Length)]}/pack{i % 97}/{name} {i}.wav", "/lib", IndexKind.Audio, rng.NextDouble() * 5, 44100, 2, 0, "", i, i));
            }
            var index = new SampleIndex();
            index.Apply(list);
            Assert.That(index.Count, Is.EqualTo(100_000));
            index.Search("kick warm");                               // warms the JIT
            foreach (var query in new[] { "kick", "kick warm", "drums snare 12", "qzx", "loop 808 pack5" })
            {
                var times = new List<double>();
                for (int run = 0; run < 7; run++)
                {
                    var sw = Stopwatch.StartNew();
                    var hits = index.Search(query, 200);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    Assert.That(hits.Count, Is.LessThanOrEqualTo(200));
                }
                times.Sort();
                TestContext.Out.WriteLine($"search \"{query}\" over 100,000 entries: median {times[3]:0.0} ms, worst {times[^1]:0.0} ms");
                Assert.That(times[3], Is.LessThan(50), $"\"{query}\": the median of seven");
            }
            // And the file: written and read back.
            string file = Path.Combine(Temp(), "big.bin");
            var sw2 = Stopwatch.StartNew();
            index.Save(file);
            double save = sw2.Elapsed.TotalMilliseconds;
            sw2.Restart();
            var again = new SampleIndex();
            Assert.That(again.Load(file), Is.True);
            TestContext.Out.WriteLine($"100,000 entries: save {save:0} ms, load {sw2.Elapsed.TotalMilliseconds:0} ms, {new FileInfo(file).Length / 1e6:0.0} MB");
            Assert.That(again.Count, Is.EqualTo(100_000));
        }

        // ---- the library: places, the scan, the watcher ---------------------------------------------

        sealed class Store
        {
            public Dictionary<string, string> Values = new();
            public string Read(string k) => Values.TryGetValue(k, out var v) ? v : "";
            public void Write(string k, string v) => Values[k] = v;
        }

        [Test]
        public void APlaceIsIndexedInTheBackgroundAndARescanReadsOnlyWhatChanged()
        {
            string settings = Temp(), samples = Temp();
            WriteWav(Path.Combine(samples, "Kick.wav"), 0.3);
            WriteWav(Path.Combine(samples, "Loops", "Dusty 90 bpm.wav"), 2, 48000, 2);
            File.WriteAllText(Path.Combine(samples, "readme.txt"), "x");
            var store = new Store();
            using var lib = new BrowserLibrary(settings, store.Read, store.Write);
            Assert.That(lib.AddPlace(Path.Combine(samples, "nope")), Is.False, "not a folder");
            Assert.That(lib.AddPlace(samples), Is.True);
            Assert.That(lib.AddPlace(samples), Is.False, "once");
            Assert.That(lib.WaitForScan(), Is.True);
            Assert.That(lib.Index.Count, Is.EqualTo(2), "audio only");
            var loop = lib.Index.Search("dusty").Single();
            Assert.That((loop.Bpm, loop.SampleRate, loop.Channels), Is.EqualTo((90.0, 48000, 2)));
            Assert.That(store.Values[BrowserLibrary.PlacesKey], Is.EqualTo(samples.TrimEnd(Path.DirectorySeparatorChar)), "kept in the settings");
            Assert.That(File.Exists(lib.IndexPath), Is.True, "and the index in a file of its own");

            // Rescan: what is unchanged is the same object (not read again); a new file is found; a removed one goes.
            var kick = lib.Index.Find(Path.Combine(samples, "Kick.wav"))!;
            WriteWav(Path.Combine(samples, "Snare.wav"), 0.2);
            File.Delete(Path.Combine(samples, "Loops", "Dusty 90 bpm.wav"));
            lib.Rescan();
            lib.WaitForScan();
            Assert.That(lib.Index.Find(Path.Combine(samples, "Kick.wav")), Is.SameAs(kick), "unchanged: not read again");
            Assert.That(lib.Index.Search("snare").Count, Is.EqualTo(1));
            Assert.That(lib.Index.Search("dusty"), Is.Empty);

            // A new window reads the same places and the same index file.
            using var again = new BrowserLibrary(settings, store.Read, store.Write);
            Assert.That(again.Places, Is.EqualTo(lib.Places));
            Assert.That(again.Index.Count, Is.EqualTo(2), "the index is loaded from its file at once");

            Assert.That(lib.RemovePlace(samples), Is.True);
            Assert.That(lib.Index.Count, Is.EqualTo(0), "its entries go with it");
            Assert.That(lib.Places, Is.Empty);
        }

        [Test]
        public void AFileWatcherKeepsTheIndexCurrentWhileTheAppRuns()
        {
            string settings = Temp(), samples = Temp();
            WriteWav(Path.Combine(samples, "First.wav"), 0.2);
            var store = new Store();
            using var lib = new BrowserLibrary(settings, store.Read, store.Write);
            lib.AddPlace(samples);
            lib.Start();
            lib.WaitForScan();
            Assert.That(lib.Index.Count, Is.EqualTo(1));
            WriteWav(Path.Combine(samples, "Sub", "Second.wav"), 0.2);
            Assert.That(Until(() => lib.Index.Search("second").Count == 1), Is.True, "a file added while the app runs");
            File.Delete(Path.Combine(samples, "First.wav"));
            Assert.That(Until(() => lib.Index.Search("first").Count == 0), Is.True, "a file deleted");
            Directory.Delete(Path.Combine(samples, "Sub"), true);
            Assert.That(Until(() => lib.Index.Count == 0), Is.True, "a folder deleted takes what was in it");
        }

        static bool Until(Func<bool> done)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (done()) return true;
                Thread.Sleep(50);
            }
            return done();
        }

        [Test]
        public void ProgressIsReportedWhileScanning()
        {
            string settings = Temp(), samples = Temp();
            for (int i = 0; i < 30; i++) WriteWav(Path.Combine(samples, $"s{i}.wav"), 0.05);
            var store = new Store();
            using var lib = new BrowserLibrary(settings, store.Read, store.Write);
            var seen = new List<IndexProgress>();
            lib.ProgressChanged += () => { lock (seen) seen.Add(lib.Progress); };
            lib.AddPlace(samples);
            lib.WaitForScan();
            lock (seen)
            {
                Assert.That(seen.Any(p => p.Scanning && p.Total == 30), Is.True, "it says how many there are");
                Assert.That(seen.Last().Scanning, Is.False, "and that it is done");
                Assert.That(seen.First(p => p.Scanning && p.Total == 30).Text, Does.StartWith("Indexing"));
            }
        }

        // ---- collections -----------------------------------------------------------------------------

        [Test]
        public void CollectionsKeepFavouritesAndSevenColourTagsInTheSettings()
        {
            var store = new Store();
            var c = new BrowserCollections(() => store.Read("c"), v => store.Write("c", v));
            string kick = BrowserCollections.Key(BrowserKind.Audio, "/Users/x/Kick.wav");
            string preset = BrowserCollections.Key(BrowserKind.Preset, "Neo Poly", "Warm Bass");
            c.ToggleFavourite(kick);
            c.SetColour(preset, 3);
            Assert.That(c.IsFavourite(kick), Is.True);
            Assert.That(c.Colour(kick), Is.EqualTo(0));
            Assert.That(c.Colour(preset), Is.EqualTo(3));
            Assert.That(c.WithColour(3), Is.EqualTo(new[] { preset }));
            Assert.That(BrowserCollections.Colours, Is.EqualTo(7));
            Assert.Throws<ArgumentOutOfRangeException>(() => c.SetColour(kick, 8));
            var again = new BrowserCollections(() => store.Read("c"), v => store.Write("c", v));
            Assert.That(again.All(), Is.EquivalentTo(new[] { kick, preset }), "read back from the settings");
            again.ToggleFavourite(kick);
            Assert.That(again.IsFavourite(kick), Is.False);
        }

        // ---- the agent's tools ----------------------------------------------------------------------

        sealed class LibraryBrowser : IBrowserControl
        {
            public readonly BrowserLibrary Lib;
            public LibraryBrowser(BrowserLibrary lib) => Lib = lib;
            public IReadOnlyList<BrowserHit> Search(string query, string? kind, int limit) =>
                Lib.Index.Search(query, limit).Select(e => new BrowserHit("audio", e.Name, e.Path, Path.GetFileName(e.Root), e.Describe(), e.Path, null)).ToList();
            public IReadOnlyList<BrowserPlace> Places() => Lib.Places.Select(p => new BrowserPlace(p, Lib.Index.Entries.Count(e => e.Root == p), Directory.Exists(p))).ToList();
            public bool AddPlace(string path) => Lib.AddPlace(path);
            public bool RemovePlace(string path) => Lib.RemovePlace(path);
            public (string Status, int Indexed) Index() => (Lib.IsScanning ? Lib.Progress.Text : "idle", Lib.Index.Count);
            public bool WaitForIndex(int milliseconds) => Lib.WaitForScan(milliseconds);
        }

        sealed class MiniApp : IAppServices
        {
            public IBrowserControl? BrowserFake;
            public IBrowserControl? Browser => BrowserFake;
            public Task<T> Engine<T>(string what, bool changes, Func<T> work, CancellationToken ct) => Task.FromResult(work());
            public IReadOnlyList<SettingEntry> Settings() => Array.Empty<SettingEntry>();
            public string SettingsHistoryFile => Path.Combine(Temp(), "h.jsonl");
            public IMidiBindings Midi => throw new NotSupportedException();
            public IReadOnlyList<string> RecentSongs() => Array.Empty<string>();
            public AppFacts Facts() => new("t", "headless", "Test", 1, null, false, 120, false);
            public IViewControl? View => null;
            public IAudioControl? Audio => null;
        }

        static (McpServer Server, MiniApp App) Server()
        {
            var app = new MiniApp();
            var tools = new ToolRegistry();
            BrowserTools.Register(tools, app);
            return (new McpServer(new ServerInfo { Name = "t", Title = "t", Version = "1" }, tools), app);
        }

        [Test]
        public void BrowserToolsSearchAndManagePlacesAndSayStudioOnlyElsewhere()
        {
            var (server, app) = Server();
            var refused = TestTools.CallTool(server, "browser_search", new JsonObject { ["query"] = "kick" });
            Assert.That(refused.Error, Is.True);
            Assert.That(refused.Text, Does.Contain("Studio only"));

            string settings = Temp(), samples = Temp();
            WriteWav(Path.Combine(samples, "Kick 808.wav"), 0.4);
            WriteWav(Path.Combine(samples, "Snare.wav"), 0.2);
            var store = new Store();
            using var lib = new BrowserLibrary(settings, store.Read, store.Write);
            app.BrowserFake = new LibraryBrowser(lib);

            var add = TestTools.CallTool(server, "browser_places", new JsonObject { ["op"] = "add", ["path"] = samples, ["wait_seconds"] = 20 });
            Assert.That(add.Error, Is.False, add.Text);
            Assert.That(add.Data["indexed"]!.GetValue<int>(), Is.EqualTo(2), "it waited for the scan");
            Assert.That(add.Data["places"]!.AsArray().Single()!["files"]!.GetValue<int>(), Is.EqualTo(2));
            var found = TestTools.CallTool(server, "browser_search", new JsonObject { ["query"] = "kick 808", ["kind"] = "audio" });
            Assert.That(found.Error, Is.False, found.Text);
            Assert.That(found.Data["count"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(found.Data["results"]![0]!["path"]!.GetValue<string>(), Does.EndWith("Kick 808.wav"));
            Assert.That(TestTools.CallTool(server, "browser_search", new JsonObject { ["query"] = "kick", ["kind"] = "nonsense" }).Error, Is.True);
            Assert.That(TestTools.CallTool(server, "browser_places", new JsonObject { ["op"] = "add" }).Error, Is.True, "add needs a path");
            Assert.That(TestTools.CallTool(server, "browser_places", new JsonObject { ["op"] = "add", ["path"] = samples }).Error, Is.True, "already a place");
            var remove = TestTools.CallTool(server, "browser_places", new JsonObject { ["op"] = "remove", ["path"] = samples });
            Assert.That(remove.Error, Is.False, remove.Text);
            Assert.That(remove.Data["places"]!.AsArray(), Is.Empty);
            Assert.That(TestTools.CallTool(server, "browser_places", new JsonObject { ["op"] = "remove", ["path"] = samples }).Error, Is.True, "not a place any more");
        }
    }
}
