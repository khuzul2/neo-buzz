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
            Assert.That(track.MedianBpm, Is.EqualTo(bpm).Within(bpm * 0.01), "the tempo the pulses have, whatever the octave");
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
    }
}
