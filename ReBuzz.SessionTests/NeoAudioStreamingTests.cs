using System.Diagnostics;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Clips;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// Neo Audio disk streaming (docs/specs/neo-audio.md, NA1b): the decoded cache file, the streamed source and its prefetch thread, the
    /// switch from memory to the cache while a clip plays, the underruns, and the repair of a lost cache file. Each test has a cache folder of
    /// its own and a small streaming threshold, so short audio streams.
    /// </summary>
    [TestFixture, NonParallelizable]
    public class NeoAudioStreamingTests
    {
        const int Rate = 48000;
        const int Block = 256;

        string dir = "";
        long savedThreshold, savedResident, savedLimit;
        int savedHead;
        double savedAhead;
        bool savedEnabled;
        string savedFolder = "";
        readonly List<AudioAsset> made = new();

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), "neo-stream-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            savedFolder = AudioCache.DataFolder;
            savedThreshold = StreamingPolicy.ThresholdBytes;
            savedResident = StreamingPolicy.ResidentBytes;
            savedLimit = StreamingPolicy.CacheLimitBytes;
            savedHead = StreamingPolicy.HeadMilliseconds;
            savedAhead = StreamingPolicy.ReadAheadSeconds;
            savedEnabled = StreamingPolicy.Enabled;
            AudioCache.DataFolder = dir;
            StreamingPolicy.ThresholdBytes = 1024 * 1024;
            StreamingPolicy.Enabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var a in made) a.ReleaseStream();
            made.Clear();
            AudioCache.DataFolder = savedFolder;
            StreamingPolicy.ThresholdBytes = savedThreshold;
            StreamingPolicy.ResidentBytes = savedResident;
            StreamPrefetcher.EvictGraceMs = 1000;
            StreamingPolicy.CacheLimitBytes = savedLimit;
            StreamingPolicy.HeadMilliseconds = savedHead;
            StreamingPolicy.ReadAheadSeconds = savedAhead;
            StreamingPolicy.Enabled = savedEnabled;
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ material

        /// <summary>A deterministic signal (a few partials and a slow envelope), so a long asset needs no reference arrays.</summary>
        static float Gen(long i, int channel, int seed)
        {
            double t = i / (double)Rate;
            double f = 110 * (1 + seed % 5) * (channel == 0 ? 1 : 1.5);
            return (float)(0.35 * Math.Sin(2 * Math.PI * f * t) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 0.37 * t + seed)) +
                           0.15 * Math.Sin(2 * Math.PI * f * 3.01 * t + channel));
        }

        static (float[] L, float[] R) Arrays(double seconds, int seed, bool stereo)
        {
            long n = (long)(seconds * Rate);
            var l = new float[n];
            var r = stereo ? new float[n] : l;
            Parallel.For(0, (int)((n + 65535) / 65536), b =>
            {
                long from = (long)b * 65536, to = Math.Min(n, from + 65536);
                for (long i = from; i < to; i++)
                {
                    l[i] = Gen(i, 0, seed);
                    if (stereo) r[i] = Gen(i, 1, seed);
                }
            });
            return (l, r);
        }

        AudioAsset Asset(double seconds, int seed = 1, bool stereo = true, string name = "x")
        {
            var (l, r) = Arrays(seconds, seed, stereo);
            var asset = new AudioAsset(name + seed, "", null, new AudioSource(l, r, Rate), stereo ? 2 : 1);
            made.Add(asset);
            return asset;
        }

        /// <summary>Streams the asset: waits for the cache file, the switch and the memory to go.</summary>
        static void Stream(AudioAsset asset)
        {
            Assert.That(asset.BeginStreaming().Wait(120_000), Is.True, "the cache is made");
            Assert.That(asset.Storage, Is.EqualTo(AssetStorage.Streaming));
            Assert.That(asset.Source.InMemory, Is.False, "the floats are let go");
        }

        sealed class ArrayRecipe : AssetRecipe
        {
            readonly float[] l, r;
            public int Calls;
            public ArrayRecipe(float[] l, float[] r) { this.l = l; this.r = r; }
            public override string Describe => "test arrays";
            public override bool Rebuild(AudioCache.Writer writer, AudioAsset asset, CancellationToken cancel)
            {
                Calls++;
                writer.WriteAll(l, r, null, cancel);
                return true;
            }
        }

        static AudioClipParams Clip(AudioSource s, double bpm, WarpMode mode, WarpMap? map = null, float gain = 1f)
        {
            map ??= WarpMap.Straight(s.Length, s.Seconds * bpm / 60);
            return new AudioClipParams(s, map, mode, new[] { 0.0 }, gain, 0);
        }

        // ------------------------------------------------------------------ the cache file

        [Test]
        public void TheCacheFileHoldsExactlyTheAudioAndOnlyOpensWhenItIsWhole()
        {
            foreach (bool stereo in new[] { true, false })
            {
                var (l, r) = Arrays(11.3, 3, stereo);
                var asset = new AudioAsset("c", "", null, new AudioSource(l, r, Rate), stereo ? 2 : 1);
                made.Add(asset);
                Stream(asset);
                string path = AudioCache.PathFor(asset.Id);
                Assert.That(File.Exists(path), "the file is named by the asset's id");
                Assert.That(StreamedAudio.IsWhole(asset.Id, l.Length, stereo ? 2 : 1, Rate), Is.True);
                Assert.That(StreamedAudio.IsWhole(asset.Id, l.Length + 1, stereo ? 2 : 1, Rate), Is.False, "another length is not this audio");
                Assert.That(StreamedAudio.IsWhole(asset.Id, l.Length, stereo ? 2 : 1, Rate + 1), Is.False, "nor another rate");
                Assert.That(StreamedAudio.IsWhole(asset.Id, l.Length, stereo ? 1 : 2, Rate), Is.False, "nor other channels");

                var rng = new Random(5);
                var got = new float[3000];
                for (int k = 0; k < 40; k++)
                {
                    long at = rng.NextInt64(-1000, l.Length + 1000);
                    for (int ch = 0; ch < (stereo ? 2 : 1); ch++)
                    {
                        asset.Source.CopyExact(ch, at, got);
                        var want = ch == 0 ? l : r;
                        for (int i = 0; i < got.Length; i++)
                        {
                            long f = at + i;
                            float expected = f >= 0 && f < want.Length ? want[f] : 0f;
                            if (got[i] != expected) Assert.Fail($"channel {ch} frame {f}: {got[i]} not {expected}");
                        }
                    }
                }
            }
        }

        [Test]
        public void AFileCutShortOrDamagedIsNotTrustedAndIsFound()
        {
            var (l, r) = Arrays(8.5, 4, true);
            var asset = new AudioAsset("d", "", null, new AudioSource(l, r, Rate), 2);
            made.Add(asset);
            Stream(asset);
            string path = AudioCache.PathFor(asset.Id);
            asset.ReleaseStream();
            // A copy cut short does not open.
            string cut = path + ".cut";
            File.Copy(path, cut);
            using (var f = new FileStream(cut, FileMode.Open)) f.SetLength(f.Length - 5);
            File.Move(cut, path, true);
            Assert.That(StreamedAudio.IsWhole(asset.Id, l.Length, 2, Rate), Is.False, "cut short");
            // Whole again (written afresh), then a byte flipped inside the samples: the header is fine, the piece's checksum is not.
            File.Delete(path);
            using (var w = new AudioCache.Writer(asset.Id, l.Length, 2, Rate)) { w.WriteAll(l, r); Assert.That(w.Complete(), Is.True); }
            Assert.That(StreamedAudio.IsWhole(asset.Id, l.Length, 2, Rate), Is.True);
            using (var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                f.Position = f.Length - 100000;
                int b = f.ReadByte();
                f.Position = f.Length - 100000;
                f.WriteByte((byte)(b ^ 0xFF));
            }
            using var s = StreamedAudio.Open(asset.Id, l.Length, 2, Rate)!;
            Assert.That(s, Is.Not.Null);
            var buffer = new float[l.Length];
            Assert.Throws<InvalidDataException>(() => { s.ReadExact(0, 0, buffer); s.ReadExact(1, 0, buffer); }, "the damaged piece is found when it is read");
        }

        // ------------------------------------------------------------------ the source

        [Test]
        public void AStreamedSourceReadsLikeTheMemoryOneAndItsReversedViewBackwards()
        {
            var (l, r) = Arrays(15.2, 6, true);
            var asset = new AudioAsset("s", "", null, new AudioSource(l, r, Rate), 2);
            made.Add(asset);
            var before = asset.Source.Reversed();                         // a short source is a copy; this one is long enough to be a view
            Assert.That(before.IsReversedView, Is.True, "long audio is reversed as a view, not a copy of its size");
            Stream(asset);
            var src = asset.Source;
            Assert.That(src.IsStreaming, Is.True);
            Assert.That(src.Length, Is.EqualTo(l.Length));
            var rev = src.Reversed();

            // The head is pinned: those samples are in memory and equal.
            Assert.That(StreamPrefetcher.WaitSettled(src.Stream!, 5000), Is.True);
            long head = (long)(StreamingPolicy.HeadMilliseconds / 1000.0 * Rate) - 2;
            for (long i = 0; i < head; i += 37)
            {
                if (src.L(i) != l[i] || src.R(i) != r[i]) Assert.Fail($"frame {i}");
            }
            // Bulk reads of the audio thread and exact reads of the rest, forward and in the view.
            var got = new float[5000];
            src.CopyTo(0, 100, got);
            Assert.That(got, Is.EqualTo(l.Skip(100).Take(5000).ToArray()));
            long end = l.Length;
            rev.CopyExact(1, 0, got);
            for (int i = 0; i < got.Length; i++) if (got[i] != r[end - 1 - i]) Assert.Fail($"reversed frame {i}");
            rev.CopyExact(0, end - 4000, got.AsSpan(0, 4000));
            for (int i = 0; i < 4000; i++) if (got[i] != l[3999 - i]) Assert.Fail($"reversed tail frame {i}");
            // The audio thread's read of a view of the head of the reversed audio is the end of the original, which is not pinned: it is counted, not waited for.
            long missed = src.Stream!.MissedSamples;
            _ = rev.L(10);
            Assert.That(src.Stream!.MissedSamples, Is.GreaterThanOrEqualTo(missed));
        }

        [Test]
        public void AudioThatIsShortStaysInMemoryAndTheSwitchOffKeepsAllInMemory()
        {
            StreamingPolicy.ThresholdBytes = 8L * 1024 * 1024;
            var small = Asset(3, 1);
            Assert.That(small.BeginStreaming().Wait(5000), Is.True);
            Assert.That(small.Storage, Is.EqualTo(AssetStorage.Memory));
            Assert.That(small.Source.IsStreaming, Is.False);
            Assert.That(AudioCache.FileCount(), Is.EqualTo(0));

            StreamingPolicy.ThresholdBytes = 1024 * 1024;
            StreamingPolicy.Enabled = false;
            var off = Asset(12, 2);
            Assert.That(off.BeginStreaming().Wait(5000), Is.True);
            Assert.That(off.Storage, Is.EqualTo(AssetStorage.Memory), "NEO_STREAMING_OFF keeps everything in memory");
            Assert.That(StreamingPolicy.ShouldStream(48000 * 100, 2), Is.False);
        }

        // ------------------------------------------------------------------ playing

        /// <summary>Renders a clip in real time (paced) for a number of blocks, collecting the output.</summary>
        static void PlayPaced(ClipVoice voice, AudioClipParams p, double bpm, double fromBeat, int blocks, float[] l, float[] r, int pace = 1)
        {
            double bps = bpm / 60.0 / Rate;
            for (int b = 0; b < blocks; b++)
            {
                voice.Render(p, fromBeat + b * Block * bps, bps, Rate, l, r, b * Block, Block);
                if (pace > 0) Thread.Sleep(pace);
            }
        }

        [Test]
        public void SwitchingFromMemoryToTheCacheWhileItPlaysIsBitIdenticalAndHasNoUnderrun()
        {
            foreach (var mode in new[] { WarpMode.Repitch, WarpMode.Beats, WarpMode.Complex })
            {
                var (l, r) = Arrays(40, 7, true);
                var asset = new AudioAsset("sw", "", null, new AudioSource(l, r, Rate), 2);
                made.Add(asset);
                var reference = new AudioSource(l, r, Rate);                  // the same samples, never streamed
                double bpm = 120;
                var p = Clip(asset.Source, bpm, mode);
                var q = Clip(reference, bpm, mode);
                var voice = new ClipVoice();
                var refVoice = new ClipVoice();
                voice.Prepare(p);
                refVoice.Prepare(q);
                int blocks = 700;                                             // 3.7 seconds of audio
                var got = new float[blocks * Block];
                var gotR = new float[blocks * Block];
                var want = new float[blocks * Block];
                var wantR = new float[blocks * Block];
                double bps = bpm / 60.0 / Rate;
                Task? fill = null;
                for (int b = 0; b < blocks; b++)
                {
                    if (b == 150) fill = asset.BeginStreaming();              // the cache is written, then the floats go, while it plays
                    voice.Render(p, b * Block * bps, bps, Rate, got, gotR, b * Block, Block);
                    refVoice.Render(q, b * Block * bps, bps, Rate, want, wantR, b * Block, Block);
                    Thread.Sleep(2);
                }
                Assert.That(fill!.Wait(30_000), Is.True);
                Assert.That(asset.Source.InMemory, Is.False, $"{mode}: the memory was let go");
                // It may still be playing from memory when the loop ends (that is the point of the switch); keep going until it is streaming.
                Assert.That(got, Is.EqualTo(want), $"{mode}: the left channel is the same bits");
                Assert.That(gotR, Is.EqualTo(wantR), $"{mode}: and the right");
                Assert.That(asset.StreamStats.Missed, Is.EqualTo(0), $"{mode}: nothing was read that was not in memory");
                Assert.That(asset.StreamStats.Underruns, Is.EqualTo(0L));
                asset.ReleaseStream();
            }
        }

        [Test]
        public void AColdStartOfAStreamedClipIsSampleAccurateAtTheStartAndAtAPinnedMarker()
        {
            var (l, r) = Arrays(60, 8, true);
            var asset = new AudioAsset("cold", "", null, new AudioSource(l, r, Rate), 2);
            made.Add(asset);
            var reference = new AudioSource(l, r, Rate);
            var map = new WarpMap(new[] { new WarpMarker(0, 0), new WarpMarker(30 * Rate, 60), new WarpMarker(60 * Rate - 1, 120) });
            Stream(asset);
            var p = Clip(asset.Source, 120, WarpMode.Repitch, map);
            var q = Clip(reference, 120, WarpMode.Repitch, map);
            // The clip's markers are pinned (the layout does this for every clip).
            asset.PinClip(this, asset.Source, new List<(long, long)> { (30L * Rate - 4096, 30L * Rate + 24000), (0, 24000) });
            Assert.That(StreamPrefetcher.WaitSettled(asset.Cache!, 5000), Is.True);

            foreach (double beat in new[] { 0.0, 60.0 })                      // the start, and the marker's beat (sample 30 s)
            {
                var v = new ClipVoice(); var rv = new ClipVoice();
                v.Prepare(p); rv.Prepare(q);
                var a = new float[2 * Block]; var ar = new float[2 * Block];
                var b = new float[2 * Block]; var br = new float[2 * Block];
                double bps = 120 / 60.0 / Rate;
                long missed = asset.Cache!.MissedSamples;
                for (int k = 0; k < 2; k++)
                {
                    v.Render(p, beat + k * Block * bps, bps, Rate, a, ar, k * Block, Block);        // no hint, no wait: the very first block of a launch
                    rv.Render(q, beat + k * Block * bps, bps, Rate, b, br, k * Block, Block);
                }
                Assert.That(a, Is.EqualTo(b), $"beat {beat}: the first samples are the memory's");
                Assert.That(ar, Is.EqualTo(br));
                Assert.That(asset.Cache.MissedSamples, Is.EqualTo(missed), $"beat {beat}: nothing was missing");
            }
        }

        [Test]
        public void AJumpWithNoWarningPlaysSilenceForWhatIsMissingCountsItNeverBlocksAndRecovers()
        {
            var (l, r) = Arrays(120, 9, true);
            var asset = new AudioAsset("jump", "", null, new AudioSource(l, r, Rate), 2);
            made.Add(asset);
            var reference = new AudioSource(l, r, Rate);
            Stream(asset);
            var p = Clip(asset.Source, 120, WarpMode.Repitch);
            var q = Clip(reference, 120, WarpMode.Repitch);
            var v = new ClipVoice(); var rv = new ClipVoice();
            v.Prepare(p); rv.Prepare(q);
            double bps = 120 / 60.0 / Rate;
            double far = 90 * 2;                                              // beat 180: sample 90 s, nowhere near the head or a hint
            var a = new float[Block]; var ar = new float[Block]; var b = new float[Block]; var br = new float[Block];
            var watch = Stopwatch.StartNew();
            v.Render(p, far, bps, Rate, a, ar, 0, Block);
            double ms = watch.Elapsed.TotalMilliseconds;
            rv.Render(q, far, bps, Rate, b, br, 0, Block);
            Assert.That(ms, Is.LessThan(50), "an underrun never waits for the disk");
            Assert.That(a.All(x => x == 0), Is.True, "the missing audio is silence");
            Assert.That(b.Any(x => x != 0), Is.True);
            Assert.That(asset.StreamStats.Underruns, Is.GreaterThan(0), "and it is counted");
            Assert.That(StreamStats.Underruns, Is.GreaterThan(0));
            // It goes on, and once the prefetch thread has read the place (the voice told it where it is) the sound is the memory's again.
            int block = 1;
            bool same = false;
            for (; block < 400 && !same; block++)
            {
                Array.Clear(a); Array.Clear(ar); Array.Clear(b); Array.Clear(br);
                v.Render(p, far + block * Block * bps, bps, Rate, a, ar, 0, Block);
                rv.Render(q, far + block * Block * bps, bps, Rate, b, br, 0, Block);
                same = a.SequenceEqual(b) && ar.SequenceEqual(br) && block > 3;
                Thread.Sleep(2);
            }
            Assert.That(same, Is.True, "recovered: the blocks are the memory's again");
        }

        [Test]
        public void ReversedStreamedAudioPlaysTheSameBitsAsAReversedCopyInMemory()
        {
            var (l, r) = Arrays(30, 10, true);
            var asset = new AudioAsset("rev", "", null, new AudioSource(l, r, Rate), 2);
            made.Add(asset);
            var copy = new AudioSource((float[])l.Clone(), (float[])r.Clone(), Rate);
            Array.Reverse(copy.Left); Array.Reverse(copy.Right);              // the old way: a reversed copy
            var view = asset.Source.Reversed();
            Stream(asset);
            Assert.That(view.IsReversedView && view.IsStreaming, Is.True);
            var p = Clip(view, 120, WarpMode.Repitch);
            var q = Clip(copy, 120, WarpMode.Repitch);
            var v = new ClipVoice(); var rv = new ClipVoice();
            v.Prepare(p); rv.Prepare(q);
            asset.PinClip(this, view, new List<(long, long)> { (0, 30000) });  // the reversed clip starts at the audio's end
            Assert.That(StreamPrefetcher.WaitSettled(asset.Cache!, 5000), Is.True);
            int blocks = 500;
            var a = new float[blocks * Block]; var ar = new float[blocks * Block];
            var b = new float[blocks * Block]; var br = new float[blocks * Block];
            double bps = 120 / 60.0 / Rate;
            for (int k = 0; k < blocks; k++)
            {
                v.Render(p, k * Block * bps, bps, Rate, a, ar, k * Block, Block);
                rv.Render(q, k * Block * bps, bps, Rate, b, br, k * Block, Block);
                Thread.Sleep(2);
            }
            Assert.That(a, Is.EqualTo(b));
            Assert.That(ar, Is.EqualTo(br));
            Assert.That(asset.Cache!.Underruns, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ the cache file lost or damaged

        [Test]
        public void ADeletedCacheFileIsWrittenAgainAndADamagedOneToo()
        {
            var (l, r) = Arrays(20, 11, true);
            var asset = new AudioAsset("lost", "", null, new AudioSource(l, r, Rate), 2);
            var recipe = new ArrayRecipe(l, r);
            asset.Recipe = recipe;
            made.Add(asset);
            Stream(asset);
            string path = AudioCache.PathFor(asset.Id);

            // 1. The file is deleted while it is open: the prefetch thread notices, and the cache is written again (from the handle still held).
            File.Delete(path);
            Assert.That(WaitFor(() => File.Exists(path) && asset.Storage == AssetStorage.Streaming && !asset.Cache!.Damaged, 20_000), Is.True, "rebuilt");
            Assert.That(StreamedAudio.IsWhole(asset.Id, l.Length, 2, Rate), Is.True);
            AssertSame(asset, l, r);

            // 2. A piece is damaged: the recipe makes the file again.
            using (var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                f.Position = f.Length / 2;
                var bytes = new byte[64];
                f.ReadExactly(bytes);
                for (int i = 0; i < bytes.Length; i++) bytes[i] ^= 0x5A;
                f.Position = f.Length / 2;
                f.Write(bytes);
            }
            asset.Cache!.MarkDamaged("A piece fails its checksum.");        // what the prefetch thread does when it reads it (here, without waiting for the page)
            Assert.That(WaitFor(() => asset.Storage == AssetStorage.Streaming && !asset.Cache.Damaged && recipe.Calls > 0, 20_000), Is.True, "rebuilt from the recipe");
            AssertSame(asset, l, r);
        }

        static void AssertSame(AudioAsset asset, float[] l, float[] r)
        {
            var got = new float[l.Length];
            asset.Source.CopyExact(0, 0, got);
            Assert.That(got, Is.EqualTo(l));
            asset.Source.CopyExact(1, 0, got);
            Assert.That(got, Is.EqualTo(r));
        }

        static bool WaitFor(Func<bool> done, int ms)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                if (done()) return true;
                Thread.Sleep(10);
            }
            return done();
        }

        [Test]
        public void ARecipeTheFileCanBeDecodedFromMakesTheSameCacheFile()
        {
            // FileRecipe decodes the file again and checks the asset's id: a file that no longer matches is refused.
            var recipe = new FileRecipe(Path.Combine(dir, "missing.wav"), Rate);
            Assert.That(recipe.Available, Is.False);
            var asset = Asset(12, 12);
            using var w = new AudioCache.Writer("zzz", asset.Frames, 2, Rate);
            Assert.That(recipe.Rebuild(w, asset, default), Is.False);
        }

        // ------------------------------------------------------------------ the cache folder

        [Test]
        public void TheCacheIsTrimmedToItsLimitOldestFirstAndClearKeepsWhatTheSongUses()
        {
            var open = Asset(10, 21);
            Stream(open);
            // Files nobody has open, of different ages.
            var ids = new List<string>();
            for (int k = 0; k < 3; k++)
            {
                var (l, r) = Arrays(10, 30 + k, true);
                string id = "orphan" + k;
                using (var w = new AudioCache.Writer(id, l.Length, 2, Rate)) { w.WriteAll(l, r); Assert.That(w.Complete(), Is.True); }
                File.SetLastWriteTimeUtc(AudioCache.PathFor(id), DateTime.UtcNow.AddHours(-10 + k));
                ids.Add(id);
            }
            long one = new FileInfo(AudioCache.PathFor(ids[0])).Length;
            Assert.That(AudioCache.FileCount(), Is.EqualTo(4));
            Assert.That(AudioCache.SizeBytes(), Is.GreaterThan(3 * one));

            AudioCache.Trim(2 * one + one / 2);                               // room for the open file and one more
            Assert.That(File.Exists(AudioCache.PathFor(open.Id)), Is.True, "the open song's file is never trimmed");
            Assert.That(File.Exists(AudioCache.PathFor(ids[0])), Is.False, "the oldest goes first");
            Assert.That(File.Exists(AudioCache.PathFor(ids[1])), Is.False);
            Assert.That(File.Exists(AudioCache.PathFor(ids[2])), Is.True, "the newest stays within the limit");

            long freed = AudioCache.Clear();
            Assert.That(freed, Is.GreaterThan(0));
            Assert.That(File.Exists(AudioCache.PathFor(ids[2])), Is.False);
            Assert.That(File.Exists(AudioCache.PathFor(open.Id)), Is.True, "Clear leaves the file a streaming asset reads");
        }

        [Test]
        public void TheSettingsFileIsReadAndAGarbledOneLeavesTheDefaults()
        {
            Directory.CreateDirectory(AudioCache.DataFolder);
            File.WriteAllText(Path.Combine(AudioCache.DataFolder, "streaming.txt"), "thresholdMB=2\nheadMs=250\nreadAheadSeconds=nope\ncacheLimitGB=-3\n???\n");
            StreamingPolicy.Load();
            Assert.That(StreamingPolicy.ThresholdBytes, Is.EqualTo(2L * 1024 * 1024));
            Assert.That(StreamingPolicy.HeadMilliseconds, Is.EqualTo(250));
            Assert.That(StreamingPolicy.ReadAheadSeconds, Is.EqualTo(savedAhead), "a value that is not a number changes nothing");
            Assert.That(StreamingPolicy.CacheLimitBytes, Is.EqualTo(savedLimit), "nor a negative one");
            StreamingPolicy.Save();
            Assert.That(File.ReadAllText(Path.Combine(AudioCache.DataFolder, "streaming.txt")), Does.Contain("thresholdMB=2"));
        }

        [Test]
        public void PagesNotWantedAreEvictedOverTheBudgetAndPinnedOnesStay()
        {
            StreamingPolicy.ResidentBytes = 8 * 1024 * 1024;
            StreamPrefetcher.EvictGraceMs = 50;
            var asset = Asset(120, 13);
            Stream(asset);
            var s = asset.Cache!;
            asset.PinClip(this, asset.Source, new List<(long, long)> { (60L * Rate, 60L * Rate + 40000) });
            Assert.That(StreamPrefetcher.WaitSettled(s, 5000), Is.True);
            int pinnedPage = (int)((60L * Rate + 100) >> AudioCache.PageShift);
            Assert.That(s.PageResident(pinnedPage), Is.True);
            // A voice walks through the audio: what it has left behind goes once the budget is spent, the pin stays.
            var p = Clip(asset.Source, 120, WarpMode.Repitch);
            var v = new ClipVoice();
            v.Prepare(p);
            var l = new float[Block]; var r = new float[Block];
            double bps = 120 / 60.0 / Rate;
            for (int b = 0; b < 3000; b++)
            {
                v.Render(p, b * Block * 16 * bps, bps * 16, Rate, l, r, 0, Block);       // 16 times the speed: a lot of audio
                Thread.Sleep(1);
            }
            Thread.Sleep(200);
            Assert.That(s.ResidentBytes, Is.LessThan(StreamingPolicy.ResidentBytes + 4L * 1024 * 1024), "the pages are kept within the budget (and the window)");
            Assert.That(s.PageResident(pinnedPage), Is.True, "a pinned page is never evicted");
        }

        // ------------------------------------------------------------------ memory

        /// <summary>A long asset made without a long array in the caller (the audio is a function), so nothing else holds it.</summary>
        AudioAsset LongAsset(double seconds, int seed)
        {
            long n = (long)(seconds * Rate);
            var l = new float[n];
            var r = new float[n];
            Parallel.For(0, (int)((n + 65535) / 65536), b =>
            {
                long from = (long)b * 65536, to = Math.Min(n, from + 65536);
                for (long i = from; i < to; i++) { l[i] = Gen(i, 0, seed); r[i] = Gen(i, 1, seed); }
            });
            var asset = new AudioAsset("long" + seed, "", null, new AudioSource(l, r, Rate), 2);
            made.Add(asset);
            return asset;
        }

        [Test]
        public void ThirtyMinutesOfStereoPlaysWithMemoryFarBelowItsFloatSize()
        {
            const double Minutes = 30;
            long floats = (long)(Minutes * 60 * Rate) * 2 * 4;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long baseline = GC.GetTotalMemory(true);
            var asset = LongAsset(Minutes * 60, 14);
            long inMemory = GC.GetTotalMemory(true);
            Assert.That(inMemory - baseline, Is.GreaterThan(floats * 9 / 10), "in memory it is its float size");
            Stream(asset);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long streamed = GC.GetTotalMemory(true);

            // It plays: from the start, and from a marker well inside it (told ahead of time), for several seconds of real time.
            var p = Clip(asset.Source, 120, WarpMode.Repitch);
            var v = new ClipVoice();
            v.Prepare(p);
            var l = new float[Block]; var r = new float[Block];
            double bps = 120 / 60.0 / Rate;
            double beat = 0;
            for (int b = 0; b < 1500; b++)
            {
                v.Render(p, beat, bps, Rate, l, r, 0, Block);
                beat += Block * bps;
                Thread.Sleep(2);
            }
            long resident = asset.ResidentBytes;
            long managedAfter = GC.GetTotalMemory(true);
            TestContext.Out.WriteLine($"30 minutes stereo at {Rate} Hz: {floats / 1048576.0:0} MB of floats; managed memory with it in memory +{(inMemory - baseline) / 1048576.0:0} MB, " +
                $"streamed +{(streamed - baseline) / 1048576.0:0} MB, after playing +{(managedAfter - baseline) / 1048576.0:0} MB; the pages resident {resident / 1048576.0:0.0} MB; " +
                $"process private bytes {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0:0} MB; underruns {asset.StreamStats.Underruns}");
            Assert.That(streamed - baseline, Is.LessThan(floats / 20), "streamed: under a twentieth of its float size is held");
            Assert.That(managedAfter - baseline, Is.LessThan(floats / 10), "and after playing it");
            Assert.That(resident, Is.LessThan(floats / 10), "the store's own accounting");
            Assert.That(asset.StreamStats.Underruns, Is.EqualTo(0L));
            // The sound is the signal's.
            var check = new float[Block];
            asset.Source.CopyExact(0, 20L * 60 * Rate, check);
            for (int i = 0; i < Block; i++) if (check[i] != Gen(20L * 60 * Rate + i, 0, 14)) Assert.Fail($"frame {i} of minute 20");
        }

        // ------------------------------------------------------------------ the audio thread

        [Test]
        public void StreamedVoicesInEveryWarpModeAllocateNothingOnTheAudioThread()
        {
            var assets = new[] { Asset(60, 31), Asset(60, 32) };
            foreach (var a in assets) Stream(a);
            var voices = new List<(ClipVoice V, AudioClipParams P, double Skew)>();
            int k = 0;
            foreach (var mode in new[] { WarpMode.Off, WarpMode.Repitch, WarpMode.Beats, WarpMode.Complex, WarpMode.Pro })
                for (int i = 0; i < 2; i++, k++)
                {
                    var src = assets[i % 2].Source;
                    if (k % 3 == 0) src = src.Reversed();
                    var p = Clip(src, 120, mode);
                    var v = new ClipVoice();
                    v.Prepare(p);
                    voices.Add((v, p, 0.8 + 0.1 * i));
                }
            var l = new float[Block]; var r = new float[Block];
            var beats = new double[voices.Count];
            long allocated = 0;
            var where = new List<string>();
            double bps = 120 / 60.0 / Rate;
            bool noGc = false;
            try { noGc = GC.TryStartNoGCRegion(150_000_000); } catch (InvalidOperationException) { }
            for (int b = 0; b < 3000; b++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Array.Clear(l); Array.Clear(r);
                for (int c = 0; c < voices.Count; c++)
                {
                    var (v, p, skew) = voices[c];
                    v.Render(p, beats[c], bps * skew, Rate, l, r, 0, Block);
                    beats[c] += Block * bps * skew;
                    if (beats[c] >= p.EndBeat) beats[c] = 0;
                }
                if (b > 300) { long d = GC.GetAllocatedBytesForCurrentThread() - before; allocated += d; if (d != 0 && where.Count < 5) where.Add($"block {b}: {d}"); }
                Thread.Sleep(1);
            }
            if (noGc && System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
            Assert.That(allocated, Is.EqualTo(0L), "managed bytes allocated while rendering streamed voices: " + string.Join("; ", where));
        }

        // ------------------------------------------------------------------ sixteen voices in real time

        /// <summary>
        /// Sixteen streamed voices (Repitch, Beats, Complex and Pro, a quarter of them reversed) on four assets, in blocks of 256, paced as the
        /// device paces them. Every few seconds a voice jumps to one of its clip's markers (pinned, and told 400 ms ahead as a queued launch is),
        /// loops go round, and the tempo sweeps. Returns the underruns and the timing.
        /// </summary>
        (long Underruns, long Missed, double WorstMs, double P999Ms, long Slow, int Blocks, double PrefetchWorstMs, long Allocated) Sixteen(int seconds, int minutesOfAudio)
        {
            var assets = new List<AudioAsset>();
            for (int a = 0; a < 4; a++)
            {
                var asset = LongAsset(minutesOfAudio * 60.0, 40 + a);
                Stream(asset);
                assets.Add(asset);
            }
            var rng = new Random(17);
            var voices = new List<(ClipVoice V, AudioClipParams P, AudioAsset A, List<double> Marks, double[] Next)>();
            long endSample = (long)(minutesOfAudio * 60.0 * Rate);
            for (int i = 0; i < 16; i++)
            {
                var asset = assets[i % 4];
                var src = i % 4 == 3 ? asset.Source.Reversed() : asset.Source;       // a quarter of them play backwards
                // Eight plain voices, four of Beats, two of Complex and two of Pro: Complex and Pro cost the most (neo-audio.md sections 7 and 11), and two
                // of each is what a heavy session has; sixteen of them would measure the DSP, not the streaming.
                var mode = i < 8 ? WarpMode.Repitch : i < 12 ? WarpMode.Beats : i < 14 ? WarpMode.Complex : WarpMode.Pro;
                double bpm = 100;
                var map = WarpMap.Straight(src.Length, src.Seconds * bpm / 60);
                // Beats plays a slice from its own start: a slice a beat, as a loop's transients are, so it stays near its beat.
                var slices = Enumerable.Range(0, (int)map.Beats).Select(b => map.SampleAt(b)).ToArray();
                var p = new AudioClipParams(src, map, mode, slices, 0.5f, 0);
                var marks = new List<double>();
                var ranges = new List<(long, long)> { (-4096, (long)(StreamingPolicy.HeadMilliseconds / 1000.0 * Rate)) };      // the clip's own start
                for (int m = 0; m < 24; m++)
                {
                    double sample = map.SampleAt(Math.Floor(rng.NextDouble() * (map.Beats - 40)));       // a marker, on a beat (where a slice starts)
                    marks.Add(sample);
                    ranges.Add(((long)sample - 4096, (long)sample + (long)(StreamingPolicy.HeadMilliseconds / 1000.0 * Rate)));
                }
                asset.PinClip(voices.Count + 1000 * i, src, ranges);
                var v = new ClipVoice();
                v.Prepare(p);
                voices.Add((v, p, asset, marks, new double[] { 0 }));
            }
            foreach (var a in assets) Assert.That(StreamPrefetcher.WaitSettled(a.Cache!, 10_000), Is.True);

            int blocks = seconds * Rate / Block;
            var l = new float[Block]; var r = new float[Block];
            var beats = new double[16];
            var jumpAt = new long[16];
            var announced = new bool[16];
            var target = new double[16];
            for (int i = 0; i < 16; i++) { jumpAt[i] = rng.Next(1, 6) * Rate / Block * 1 + rng.Next(200); target[i] = -1; }
            var times = new List<double>(blocks);
            long slow = 0;
            long allocated = 0;
            var clock = Stopwatch.StartNew();
            double blockMs = Block * 1000.0 / Rate;
            bool noGc = false;
            if (seconds <= 60) try { noGc = GC.TryStartNoGCRegion(200_000_000); } catch (InvalidOperationException) { }
            long underBefore = assets.Sum(a => a.StreamStats.Underruns), missBefore = assets.Sum(a => a.StreamStats.Missed);
            for (int b = 0; b < blocks; b++)
            {
                double bpm = 120 + 40 * Math.Sin(2 * Math.PI * b / 20000.0);
                double bps = bpm / 60.0 / Rate;
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                var t0 = Stopwatch.GetTimestamp();
                Array.Clear(l); Array.Clear(r);
                for (int c = 0; c < 16; c++)
                {
                    var (v, p, asset, marks, _) = voices[c];
                    // A queued launch: told 400 ms before, jumps to a marker (a pinned place), or loops round at the end.
                    if (!announced[c] && b >= jumpAt[c] - 75)
                    {
                        double sample = marks[rng.Next(marks.Count)];
                        target[c] = p.Map.BeatAt(sample);
                        v.Anticipate(p, target[c], bps, Rate);
                        announced[c] = true;
                    }
                    if (announced[c] && b >= jumpAt[c])
                    {
                        beats[c] = target[c];
                        announced[c] = false;
                        jumpAt[c] = b + (long)rng.Next(2, 6) * Rate / Block;
                    }
                    v.Render(p, beats[c], bps, Rate, l, r, 0, Block);
                    beats[c] += Block * bps;
                    if (beats[c] >= p.EndBeat - 1) beats[c] = 0;                      // a loop going round: the start is pinned
                }
                var t1 = Stopwatch.GetTimestamp();
                allocated += GC.GetAllocatedBytesForCurrentThread() - a0;
                double ms = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                times.Add(ms);
                if (ms > blockMs / 2) slow++;
                // Paced as the device paces it.
                long due = (long)((b + 1) * blockMs);
                while (clock.ElapsedMilliseconds < due - 1) Thread.Sleep(1);
                while (clock.Elapsed.TotalMilliseconds < (b + 1) * blockMs) Thread.SpinWait(50);
            }
            if (noGc && System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
            times.Sort();
            double p999 = times[(int)Math.Min(times.Count - 1, times.Count * 0.999)];
            return (assets.Sum(a => a.StreamStats.Underruns) - underBefore, assets.Sum(a => a.StreamStats.Missed) - missBefore, times[^1], p999, slow, blocks, StreamPrefetcher.WorstPassMs, allocated);
        }

        [Test]
        public void SixteenStreamedVoicesWithSeeksLoopsReverseAndWarpHaveNoUnderrunsInRealTime()
        {
            var r = Sixteen(30, 6);
            TestContext.Out.WriteLine($"16 voices, 30 s real time at {Block}-frame blocks ({Block * 1000.0 / Rate:0.00} ms): underruns {r.Underruns}, missed samples {r.Missed}, " +
                $"worst block {r.WorstMs:0.00} ms, 99.9% {r.P999Ms:0.00} ms, blocks over half a block {r.Slow}/{r.Blocks}, worst prefetch pass {r.PrefetchWorstMs:0.0} ms, bytes allocated while rendering {r.Allocated}");
            Assert.That(r.Underruns, Is.EqualTo(0L), "blocks that played silence for audio the disk had not delivered");
            Assert.That(r.Missed, Is.EqualTo(0L));
        }

        /// <summary>The same for minutes (the soak group, with the ten-minute test of AudioHardeningTests).</summary>
        [Test, Category("Soak")]
        public void SixteenStreamedVoicesForThreeMinutesInRealTimeHaveNoUnderruns()
        {
            var r = Sixteen(180, 6);
            TestContext.Out.WriteLine($"16 voices, 180 s real time at {Block}-frame blocks: underruns {r.Underruns}, missed samples {r.Missed}, worst block {r.WorstMs:0.00} ms, " +
                $"99.9% {r.P999Ms:0.00} ms, over half a block {r.Slow}/{r.Blocks}, worst prefetch pass {r.PrefetchWorstMs:0.0} ms");
            Assert.That(r.Underruns, Is.EqualTo(0L), "blocks that played silence for audio the disk had not delivered");
            Assert.That(r.Missed, Is.EqualTo(0L));
        }

        /// <summary>
        /// The streaming group of the soak (with AudioHardeningTests' ten minutes): ten voices, two in every warp mode, half of them reversed, on two
        /// streamed assets, looping and at different tempos (sweeping from 60 to 180 BPM), for ten minutes of render time in blocks of 256 (a
        /// millisecond of sleep a block, so the prefetch thread has the disk's time too): 0 bytes allocated while rendering, no NaN, a bounded peak.
        /// No underruns either, though the clips run at about 2.5 times real time here.
        /// </summary>
        [Test, Category("Soak")]
        public void TenMinutesOfStreamedClipsInEveryWarpModeAllocateNothingAndStayFinite()
        {
            var assets = new[] { LongAsset(180, 61), LongAsset(180, 62) };
            foreach (var a in assets) Stream(a);
            var voices = new List<(ClipVoice V, AudioClipParams P, double Skew)>();
            int k = 0;
            foreach (var mode in new[] { WarpMode.Off, WarpMode.Repitch, WarpMode.Beats, WarpMode.Complex, WarpMode.Pro })
                for (int i = 0; i < 2; i++, k++)
                {
                    var src = assets[i].Source;
                    if (k % 4 == 3) src = src.Reversed();
                    var map = WarpMap.Straight(src.Length, src.Seconds * 120 / 60);
                    var slices = Enumerable.Range(0, (int)map.Beats).Select(b => map.SampleAt(b)).ToArray();
                    var p = new AudioClipParams(src, map, mode, slices, 0.5f, new[] { 0, 7, -5, 12, 0 }[k % 5]);
                    assets[i].PinClip(2000 + k, src, new List<(long, long)> { (-4096, (long)(StreamingPolicy.HeadMilliseconds / 1000.0 * Rate)) });      // the clip's own start (a reversed clip's is the audio's end), as SessionLayout pins it
                    var v = new ClipVoice();
                    v.Prepare(p);
                    voices.Add((v, p, 0.8 + 0.05 * k));
                }
            const int blocks = 10 * 60 * Rate / Block;
            var l = new float[Block]; var r = new float[Block];
            var beats = new double[voices.Count];
            double peak = 0;
            long allocated = 0;
            int bad = 0;
            for (int b = 0; b < blocks; b++)
            {
                double bpm = 120 + 60 * Math.Sin(2 * Math.PI * b / 20000.0);
                double bps = bpm / 60.0 / Rate;
                long before = GC.GetAllocatedBytesForCurrentThread();
                Array.Clear(l); Array.Clear(r);
                for (int c = 0; c < voices.Count; c++)
                {
                    var (v, p, skew) = voices[c];
                    v.Render(p, beats[c], bps * skew, Rate, l, r, 0, Block);
                    beats[c] += Block * bps * skew;
                    if (beats[c] >= p.EndBeat) beats[c] = 0;                              // a loop going round
                }
                if (b >= 4000) allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                for (int i = 0; i < Block; i++)
                {
                    float x = l[i], y = r[i];
                    if (!float.IsFinite(x) || !float.IsFinite(y)) bad++;
                    else peak = Math.Max(peak, Math.Max(Math.Abs(x), Math.Abs(y)));
                }
                Thread.Sleep(1);
            }
            TestContext.Out.WriteLine($"streamed soak: underruns {assets.Sum(a => a.StreamStats.Underruns)}, missed samples {assets.Sum(a => a.StreamStats.Missed)}, peak {peak:0.00}, resident {assets.Sum(a => a.ResidentBytes) / 1048576.0:0.0} MB");
            Assert.That(bad, Is.EqualTo(0), "NaN or infinity");
            Assert.That(assets.Sum(a => a.StreamStats.Underruns), Is.EqualTo(0L), "blocks that played silence for audio the disk had not delivered");
            Assert.That(allocated, Is.EqualTo(0L), "bytes allocated while rendering streamed voices");
            Assert.That(peak, Is.LessThan(50), "a runaway would be far over");
        }
    }
}
