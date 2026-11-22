using System.Linq;
using ReBuzz.Core.Clips;

namespace ReBuzz.SessionTests.AutoWarp
{
    /// <summary>The beat model of AW2 (docs/specs/neo-audio.md): the C# forward pass against PyTorch, the decoder on known activations, and the DSP fallback.</summary>
    [TestFixture]
    public class BeatModelTests
    {
        [Test]
        public void ForwardPassMatchesPyTorch()
        {
            var b = File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "AutoWarp", "aw2-parity.bin"));
            int len = BitConverter.ToInt32(b, 0), T = BitConverter.ToInt32(b, 4);
            var net = BeatModelNet.Load(b.AsSpan(8, len).ToArray());
            var q = b.AsSpan(8 + len, T * 128).ToArray();
            var expected = new float[2 * T];
            Buffer.BlockCopy(b, 8 + len + T * 128, expected, 0, expected.Length * 4);
            var logits = new float[2 * T];
            net.Forward(q, T, logits, null);
            double worst = 0;
            for (int i = 0; i < logits.Length; i++) worst = Math.Max(worst, Math.Abs(logits[i] - expected[i]));
            Assert.That(worst, Is.LessThan(1e-4), "largest difference from PyTorch's logits");
            Assert.That(net.Version, Is.EqualTo(99));
        }

        static BeatModelTracker.Activations Pulses(double bpm, double seconds, int bpb, double downbeatAt = 0)
        {
            int T = (int)(seconds * 50);
            var act = new BeatModelTracker.Activations { Beat = new float[T], Down = new float[T] };
            double period = 60.0 / bpm * 50;
            int i = 0;
            for (double t = 0.5 * 50; t < T - 2; t += period, i++)
            {
                int f = (int)Math.Round(t);
                act.Beat[f] = 0.9f; act.Beat[f - 1] = 0.3f; act.Beat[f + 1] = 0.3f;
                if (i % bpb == 0) act.Down[f] = 0.8f;
            }
            act.TempoWindows = Array.Empty<float[]>();
            act.TempoCentres = Array.Empty<int>();
            return act;
        }

        [TestCase(62.0)]
        [TestCase(100.0)]
        [TestCase(128.0)]
        [TestCase(190.0)]
        public void DecoderKeepsTheModelsOctave(double bpm)
        {
            var track = BeatModelTracker.Decode(Pulses(bpm, 60, 4), 22050, 22050 * 60, 5, 68);
            Assert.That(track, Is.Not.Null);
            Assert.That(track.FromModel);
            Assert.That(track.MedianBpm, Is.EqualTo(bpm).Within(bpm * 0.03), "the tempo the pulses have, whatever the octave");
            Assert.That(track.Beats.Length, Is.GreaterThan(40));
            var phases = new HashSet<int>();
            int downs = 0;
            for (int i = 0; i < track.Down.Length; i++) if (track.Down[i] > 0.5) { downs++; phases.Add(i % 4); }
            Assert.That(phases.Count, Is.EqualTo(1), "the downbeats are one beat in four");
            Assert.That(downs, Is.GreaterThan(8));
        }

        [Test]
        public void SilenceGivesNoBeats()
        {
            var act = new BeatModelTracker.Activations { Beat = new float[3000], Down = new float[3000], TempoWindows = Array.Empty<float[]>(), TempoCentres = Array.Empty<int>() };
            Assert.That(BeatModelTracker.Decode(act, 22050, 22050 * 60, 5, 68), Is.Null);
        }

        [Test]
        public void WithoutAModelTheDspTrackerIsUsed()
        {
            var line = WarpSignals.Make(WarpSignals.Tempo.Drift, WarpSignals.Style.Drums, 40, seed: 1);
            var src = new AudioSource(line.Audio, line.Audio, line.Rate);
            using (BeatModel.Scope(null))
            {
                var track = BeatTracker.Of(src);
                Assert.That(track.FromModel, Is.False, "no model: AW1's tracker");
                Assert.That(track.Found);
                Assert.That(AudioAnalysis.AutoWarp(src, 4).Found);
            }
        }

        [Test]
        public void AModelThatIsNotSureFallsBackAndTheCacheIsKeyedByVersion()
        {
            var b = File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "AutoWarp", "aw2-parity.bin"));
            int len = BitConverter.ToInt32(b, 0);
            var net = BeatModelNet.Load(b.AsSpan(8, len).ToArray());       // random weights: no beat is believed
            var line = WarpSignals.Make(WarpSignals.Tempo.Drift, WarpSignals.Style.Drums, 40, seed: 1);
            var src = new AudioSource(line.Audio, line.Audio, line.Rate);
            BeatTrack dsp, withModel;
            using (BeatModel.Scope(null)) dsp = BeatTracker.Of(src);
            using (BeatModel.Scope(net)) withModel = BeatTracker.Of(src);
            Assert.That(ReferenceEquals(dsp, withModel), Is.False, "another model version is not the cached analysis");
            Assert.That(withModel.Found, "the model was not sure, so the DSP tracker answered");
            Assert.That(withModel.FromModel, Is.False);
            using (BeatModel.Scope(net)) Assert.That(ReferenceEquals(BeatTracker.Of(src), withModel), "the same version is analysed once");
        }

        static BeatTrack Line(double bpm, double wobble, int n = 64)
        {
            var t = new BeatTrack { Rate = 44100, Length = 44100 * 200 };
            var b = new double[n];
            double at = 0;
            for (int i = 0; i < n; i++) { b[i] = at; at += 44100 * 60.0 / bpm * (1 + wobble * Math.Sin(i * 0.15)); }
            t.Beats = b;
            t.MedianBpm = bpm;
            return t;
        }

        /// <summary>AW2b: AW1's steady track stands when the model agrees up to an octave or a 3:2 ratio; an unsteady one, or a model that disagrees, goes to the model.</summary>
        [Test]
        public void SteadyDspWithAgreeingModelIsKept()
        {
            var dsp = Line(120, 0);
            Assert.That(BeatTracker.PreferDsp(dsp, Line(120, 0)), Is.True);
            Assert.That(BeatTracker.PreferDsp(dsp, Line(60, 0)), Is.True, "half time");
            Assert.That(BeatTracker.PreferDsp(dsp, Line(180, 0)), Is.True, "3:2");
            Assert.That(BeatTracker.PreferDsp(dsp, Line(133, 0)), Is.False, "another tempo: the model decides");
            Assert.That(BeatTracker.PreferDsp(Line(120, 0.06), Line(120, 0)), Is.False, "AW1 is not steady");
            Assert.That(BeatTracker.PreferDsp(null, Line(120, 0)), Is.False);
        }

        /// <summary>The generated file of the shipped weights (ReBuzz/Core/Clips/BeatModelData.cs), read as text from the source tree (this project does not link it, so the other tests stay on AW1's DSP path).</summary>
        static BeatModelNet ShippedModel()
        {
            string dir = TestContext.CurrentContext.TestDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "ReBuzz", "Core", "Clips", "BeatModelData.cs"))) dir = Path.GetDirectoryName(dir);
            Assert.That(dir, Is.Not.Null, "BeatModelData.cs is in the source tree");
            var text = File.ReadAllText(Path.Combine(dir, "ReBuzz", "Core", "Clips", "BeatModelData.cs"));
            var parts = System.Text.RegularExpressions.Regex.Matches(text, "^\\s+\"([A-Za-z0-9+/=]+)\",$", System.Text.RegularExpressions.RegexOptions.Multiline);
            return BeatModelNet.Load(System.Convert.FromBase64String(string.Concat(parts.Select(m => m.Groups[1].Value))));
        }

        /// <summary>A regression test for the weights: the shipped model finds the beats of a generated drift line (held out: the training lines use other seeds) to within 10 ms for most of them.</summary>
        [Test]
        public void ShippedModelFollowsAGeneratedDriftLine()
        {
            var net = ShippedModel();
            Assert.That(net.Version, Is.EqualTo(2));
            var line = WarpSignals.Make(WarpSignals.Tempo.Drift, WarpSignals.Style.Mix, 64, rate: 22050, seed: 5, scale: 1.1);
            var src = new AudioSource(line.Audio, line.Audio, line.Rate);
            BeatTrack t;
            using (BeatModel.Scope(net)) t = BeatTracker.Of(src);
            Assert.That(t.Found);
            int near = 0, total = 0;
            foreach (double truth in line.Beats.Where(b => b > 3 && b < 60))
            {
                total++;
                double best = t.Beats.Min(b => Math.Abs(b / line.Rate - truth));
                if (best < 0.010) near++;
            }
            Assert.That(near / (double)total, Is.GreaterThan(0.85), $"{near} of {total} beats within 10 ms");
        }
    }
}
