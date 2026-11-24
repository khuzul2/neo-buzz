using System.Linq;
using System.Text.Json;
using ReBuzz.Core.Clips;

namespace ReBuzz.SessionTests.AutoWarp
{
    /// <summary>
    /// AW4 (docs/specs/neo-audio.md, AW4 as built): the pretrained beat network's front end, windows and peak picking against the upstream package (golden data made by
    /// tools/autowarp/beat_this_golden.py), the fallback when the weights are missing or corrupt, and, where the weights are here, the network itself. The weights are never
    /// in the repository: tests that need them skip cleanly (tools/autowarp/beat_this_model.py fetch, then NEO_BEAT_THIS_MODEL=the file).
    /// </summary>
    [TestFixture, NonParallelizable]
    public class BeatThisTests
    {
        static string Data(string name) => Path.Combine(TestContext.CurrentContext.TestDirectory, "AutoWarp", name);

        sealed class Golden
        {
            public float[] Signal, Spect, Exact, Logits;
            public int Frames;
        }

        static Golden LoadGolden()
        {
            var b = File.ReadAllBytes(Data("beat-this-golden.bin"));
            int n = BitConverter.ToInt32(b, 0), frames = BitConverter.ToInt32(b, 4);
            int at = 8;
            float[] Read(int count) { var f = new float[count]; Buffer.BlockCopy(b, at, f, 0, count * 4); at += count * 4; return f; }
            return new Golden { Frames = frames, Signal = Read(n), Spect = Read(frames * 128), Exact = Read(frames * 128), Logits = Read(2 * frames) };
        }

        [Test]
        public void FrontEndMatchesTheReference()
        {
            var g = LoadGolden();
            var mine = BeatThisFront.Compute(g.Signal, out int frames);
            Assert.That(frames, Is.EqualTo(g.Frames), "one frame every 441 samples, the signal reflected at both ends");
            double vsExact = 0, vsPackage = 0;
            for (int i = 0; i < mine.Length; i++) { vsExact = Math.Max(vsExact, Math.Abs(mine[i] - g.Exact[i])); vsPackage = Math.Max(vsPackage, Math.Abs(mine[i] - g.Spect[i])); }
            Assert.That(vsExact, Is.LessThan(1e-4), "largest difference from the reference's spectrogram in float64");
            Assert.That(vsPackage, Is.LessThan(1e-4), "largest difference from the package's float32 spectrogram");
        }

        [Test]
        public void WindowsAreTheReferences()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Data("beat-this-golden.json")));
            foreach (var w in doc.RootElement.GetProperty("windows").EnumerateObject())
            {
                int frames = int.Parse(w.Name);
                var starts = BeatThisFront.WindowStarts(frames);
                Assert.That(starts, Is.EqualTo(w.Value.GetProperty("starts").EnumerateArray().Select(e => e.GetInt32()).ToArray()), $"starts for {frames} frames");
                var lengths = w.Value.GetProperty("lengths").EnumerateArray().Select(e => e.GetInt32()).ToArray();
                var spect = new float[frames * 128];
                var input = new float[BeatThisFront.Chunk * BeatThisFront.Mels];
                for (int i = 0; i < starts.Length; i++)
                    Assert.That(BeatThisFront.FillWindow(spect, frames, starts[i], input), Is.EqualTo(lengths[i]), $"window {i} of {frames} frames");
            }
        }

        [TestCase(100)]
        [TestCase(1488)]
        [TestCase(1490)]
        [TestCase(3000)]
        [TestCase(4000)]
        [TestCase(30001)]
        public void PuttingTheWindowsTogetherGivesEveryFrameItsOwnAnswer(int frames)
        {
            // A network that answers per position (frame i gives g(i); frames of padding give junk): the whole piece must read g(0..frames-1) back, wherever the windows overlap.
            double G(int i) => Math.Sin(i * 0.37) * 5 + i * 1e-3;
            var starts = BeatThisFront.WindowStarts(frames);
            var spect = new float[frames * 128];
            var per = new float[starts.Length][];
            var input = new float[BeatThisFront.Chunk * BeatThisFront.Mels];
            for (int w = 0; w < starts.Length; w++)
            {
                int len = BeatThisFront.FillWindow(spect, frames, starts[w], input);
                per[w] = new float[len];
                for (int j = 0; j < len; j++) per[w][j] = (float)(starts[w] + j >= 0 && starts[w] + j < frames ? G(starts[w] + j) : 123.0);
            }
            var whole = BeatThisFront.Aggregate(frames, starts, per);
            for (int i = 0; i < frames; i++) Assert.That(whole[i], Is.EqualTo((float)G(i)), $"frame {i}");
        }

        [Test]
        public void PeakPickingIsTheReferences()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Data("beat-this-golden.json")));
            foreach (var p in doc.RootElement.GetProperty("peaks").EnumerateArray())
            {
                float[] beat = p.GetProperty("beat").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
                float[] down = p.GetProperty("down").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
                double[] beats = p.GetProperty("beats").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                double[] downs = p.GetProperty("downbeats").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                BeatThisFront.PickPeaks(beat, down, out var mb, out var md);
                Assert.That(mb.Select(v => v / 50.0).ToArray(), Is.EqualTo(beats).Within(1e-9), "beats");
                Assert.That(md.Select(v => v / 50.0).ToArray(), Is.EqualTo(downs).Within(1e-9), "downbeats");
                Assert.That(downs.Length, Is.GreaterThan(5));
            }
        }

        // ---- weights missing or corrupt: no error, AW2 and then AW1's DSP answer ----

        [Test]
        public void ABrokenModelFileIsNoModel()
        {
            Assert.That(BeatThisNet.Load(new byte[] { 1, 2, 3, 4 }, "test"), Is.Null, "not the pinned file");
            Assert.That(BeatThisNet.Load(new byte[] { 1, 2, 3, 4 }, "test", expectedSha256: null), Is.Null, "not an ONNX file, even with the checksum not asked for");
            Assert.That(BeatThisNet.LoadFile(Path.Combine(Path.GetTempPath(), "no-such-beat-model.onnx")), Is.Null, "no file");
            string path = Path.Combine(Path.GetTempPath(), "corrupt-" + Guid.NewGuid().ToString("N") + ".onnx");
            File.WriteAllBytes(path, Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray());
            try { Assert.That(BeatThisNet.LoadFile(path), Is.Null, "a file that is not the pinned one"); }
            finally { File.Delete(path); }
        }

        [Test]
        public void AutoWarpFallsBackWithoutTheNetworkAndWithoutAnError()
        {
            var line = WarpSignals.Make(WarpSignals.Tempo.Drift, WarpSignals.Style.Drums, 40, seed: 1);
            var src = new AudioSource(line.Audio, line.Audio, line.Rate);
            using (BeatThis.Scope(null))
            using (BeatModel.Scope(null))
            {
                var r = AudioAnalysis.AutoWarp(src, 4);
                Assert.That(r.Found);
                Assert.That(r.Source, Is.EqualTo("dsp"), "no network, no AW2 model: AW1's tracker");
            }
            var src2 = new AudioSource(line.Audio, line.Audio, line.Rate);
            using (BeatThis.Scope(null))
            {
                var r = AudioAnalysis.AutoWarp(src2, 4);
                Assert.That(r.Found);
                Assert.That(r.Source, Is.Not.EqualTo("beatthis"));
            }
        }

        [Test]
        public void ACorruptFileOnThePathOfTheSettingIsIgnored()
        {
            string path = Path.Combine(Path.GetTempPath(), "corrupt-" + Guid.NewGuid().ToString("N") + ".onnx");
            File.WriteAllBytes(path, Enumerable.Range(0, 12345).Select(i => (byte)(i * 7)).ToArray());
            string before = Environment.GetEnvironmentVariable("NEO_BEAT_THIS_MODEL");
            try
            {
                Environment.SetEnvironmentVariable("NEO_BEAT_THIS_MODEL", path);
                BeatThis.Forget();
                Assert.That(BeatThis.Found(), Is.Null, "the file fails its checksum: no model, and nothing is thrown");
                var line = WarpSignals.Make(WarpSignals.Tempo.Steady, WarpSignals.Style.Drums, 30, seed: 2);
                var r = AudioAnalysis.AutoWarp(new AudioSource(line.Audio, line.Audio, line.Rate), 4);
                Assert.That(r.Found);
                Assert.That(r.Source, Is.Not.EqualTo("beatthis"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("NEO_BEAT_THIS_MODEL", before);
                BeatThis.Forget();
                File.Delete(path);
            }
        }

        [Test]
        public void TheSourceSettingChoosesTheSource()
        {
            var line = WarpSignals.Make(WarpSignals.Tempo.Steady, WarpSignals.Style.Drums, 30, seed: 2);
            var old = AutoWarpPolicy.Source;
            try
            {
                AutoWarpPolicy.Source = BeatSource.Dsp;
                Assert.That(AudioAnalysis.AutoWarp(new AudioSource(line.Audio, line.Audio, line.Rate), 4).Source, Is.EqualTo("dsp"), "dsp: AW1's tracker whatever else is here");
            }
            finally { AutoWarpPolicy.Source = old; }
        }

        // ---- with the weights ----

        static BeatThisNet NetOrSkip()
        {
            var net = BeatThis.Found();
            if (net == null) Assert.Ignore("the beat network's file is not here (tools/autowarp/beat_this_model.py fetch OUT, then NEO_BEAT_THIS_MODEL=OUT/beat_this_small.onnx)");
            return net;
        }

        [Test]
        public void TheNetworkGivesTheReferencesLogits()
        {
            var net = NetOrSkip();
            var g = LoadGolden();
            net.Predict(g.Spect, g.Frames, out var beat, out var down);
            double worst = 0;
            for (int i = 0; i < g.Frames; i++) { worst = Math.Max(worst, Math.Abs(beat[i] - g.Logits[i])); worst = Math.Max(worst, Math.Abs(down[i] - g.Logits[g.Frames + i])); }
            Assert.That(worst, Is.LessThan(1e-3), "largest difference from PyTorch's logits");
        }

        [Test]
        public void ALongPieceIsCutAndPutTogetherAsTheReferenceDoes()
        {
            var net = NetOrSkip();
            // 100 s of the golden spectrogram repeated: windows of 1500 frames overlapping by 12; every frame answered, none left at the fill value.
            var g = LoadGolden();
            int frames = 5000;
            var spect = new float[frames * 128];
            for (int i = 0; i < frames; i++) Array.Copy(g.Spect, (i % g.Frames) * 128, spect, i * 128, 128);
            net.Predict(spect, frames, out var beat, out var down);
            Assert.That(beat.Length, Is.EqualTo(frames));
            Assert.That(beat.Min(), Is.GreaterThan(-999f).And.LessThan(999f));
            Assert.That(down.Min(), Is.GreaterThan(-999f));
        }

        [Test]
        public void AutoWarpUsesTheNetworkAndKeepsItsSessionOnceOnABeat()
        {
            var net = NetOrSkip();
            var line = WarpSignals.Make(WarpSignals.Tempo.Steady, WarpSignals.Style.Drums, 60, seed: 3);
            var src = new AudioSource(line.Audio, line.Audio, line.Rate);
            using (BeatThis.Scope(net))
            {
                var r = AudioAnalysis.AutoWarp(src, 4);
                Assert.That(r.Found);
                Assert.That(r.Source, Is.EqualTo("beatthis"));
                int runs = BeatTracker.Runs;
                Assert.That(AudioAnalysis.AutoWarp(src, 4).Source, Is.EqualTo("beatthis"));
                Assert.That(BeatTracker.Runs, Is.EqualTo(runs), "the analysis of a source is computed once");
            }
            Assert.That(BeatThis.Found(), Is.SameAs(net), "the model is loaded once and kept");
        }
    }
}
