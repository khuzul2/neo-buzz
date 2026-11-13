using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.AgentTests
{
    /// <summary>The Opus preview (docs/specs/mastering-tools.md §5.15): a render through Concentus's encoder and decoder, measured.</summary>
    [TestFixture]
    public class OpusPreviewTests
    {
        static StereoAudio Music(int rate, double seconds, bool top = true)
        {
            int n = (int)(rate * seconds);
            var rng = new Random(9);
            var l = new float[n];
            var r = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)rate;
                double lo = 0.3 * Math.Sin(2 * Math.PI * 220 * t) + 0.2 * Math.Sin(2 * Math.PI * 1320 * t) + 0.1 * Math.Sin(2 * Math.PI * 4400 * t);
                // Broadband content to the top of the spectrum, so a codec's cutoff is something to lose.
                double hiss = top ? 0.05 * (rng.NextDouble() - 0.5) : 0;
                l[i] = (float)(lo + hiss);
                r[i] = (float)(0.9 * lo + (top ? 0.05 * (rng.NextDouble() - 0.5) : 0));
            }
            return new StereoAudio { Left = l, Right = r, Rate = rate };
        }

        [TestCase(48000, 128)]
        [TestCase(44100, 128)]
        [TestCase(48000, 64)]
        public void DecodedAudioComesBackAlignedWithTheOriginal(int rate, int kbps)
        {
            var a = Music(rate, 3, top: false);
            var o = OpusPreview.Run(a, kbps);
            Assert.That(o.Decoded.Frames, Is.EqualTo(a.Frames));
            Assert.That(o.Decoded.Rate, Is.EqualTo(rate));
            Assert.That(o.DelaySamples, Is.InRange(100, 1500), "the codec's lookahead (6.5 ms) and the resamplers'");
            // Tones are what Opus keeps well: the residual is far under the signal when the two are aligned.
            Assert.That(o.ResidualBelowDb, Is.LessThan(-15), $"residual {o.ResidualLufs:F1} LUFS against {o.OriginalLufs:F1}");
            Assert.That(o.ActualKbps, Is.InRange(kbps * 0.3, kbps * 1.5));
            TestContext.Out.WriteLine($"[notice] {OpusPreview.Line(o)} aligned by {o.DelaySamples} samples at {rate} Hz; {o.ActualKbps:F0} kb/s actual");
        }

        [Test]
        public void ALowBitrateLosesTheTopOfTheSpectrumAndReportsWhere()
        {
            var a = Music(48000, 4);
            var low = OpusPreview.Run(a, 64);
            // Opus is full band to about 20 kHz at every bitrate here: the noise above that is what the codec drops.
            Assert.That(low.CutoffHz, Is.InRange(19000, 21500), "a cutoff is found");
            Assert.That(low.LossAboveCutoffDb, Is.LessThan(-15), "and it is deep");
            var high = OpusPreview.Run(a, 256);
            Assert.That(high.CutoffHz, Is.InRange(19000, 21500));
            Assert.That(high.ResidualBelowDb, Is.LessThan(low.ResidualBelowDb), "more bits leave less in the residual");
            TestContext.Out.WriteLine($"[notice] {OpusPreview.Line(low)} | {OpusPreview.Line(high)}");
        }

        [Test]
        public void AMasterNearFullScaleCanComeBackOverIt()
        {
            // A loud square-ish tone at -0.1 dBFS: the decoder's overshoot is measured as true peak.
            int rate = 48000;
            var l = new float[rate * 2];
            for (int i = 0; i < l.Length; i++)
                l[i] = (float)Math.Max(-0.99, Math.Min(0.99, 1.6 * Math.Sin(2 * Math.PI * 1000 * i / rate)));
            var o = OpusPreview.Run(new StereoAudio { Left = l, Right = (float[])l.Clone(), Rate = rate }, 96);
            Assert.That(o.DecodedTruePeakDb - o.OriginalTruePeakDb, Is.GreaterThan(-3).And.LessThan(6));
            TestContext.Out.WriteLine($"[notice] {OpusPreview.Line(o)}");
        }

        [Test]
        public void ItIsDeterministic()
        {
            var a = Music(44100, 1.5);
            var x = OpusPreview.Run(a, 96);
            var y = OpusPreview.Run(a, 96);
            Assert.That(x.Decoded.Left, Is.EqualTo(y.Decoded.Left));
            Assert.That(x.Decoded.Right, Is.EqualTo(y.Decoded.Right));
        }

        [Test]
        public void MonoWorks()
        {
            var a = Music(48000, 1.5, top: false);
            var o = OpusPreview.Run(new StereoAudio { Left = a.Left, Rate = 48000 }, 96);
            Assert.That(o.Decoded.Right, Is.Null);
            Assert.That(o.ResidualBelowDb, Is.LessThan(-10));
        }

        [TestCase(63)]
        [TestCase(257)]
        [TestCase(0)]
        public void ABitrateOutsideTheRangeIsAToolError(int kbps)
        {
            Assert.Throws<ToolError>(() => OpusPreview.Run(Music(48000, 0.5), kbps));
            Assert.Throws<ToolError>(() => OpusPreview.Validate(kbps));
        }

        [Test]
        public void SilenceIsReportedWithoutFailing()
        {
            var o = OpusPreview.Run(new StereoAudio { Left = new float[48000], Right = new float[48000], Rate = 48000 }, 128);
            Assert.That(OpusPreview.Json(o)["read"]!.GetValue<string>(), Does.Contain("Opus at 128"));
        }

        [Test]
        public void TheReportCarriesTheThreeMeasurements()
        {
            var j = OpusPreview.Json(OpusPreview.Run(Music(48000, 2), 128));
            foreach (var k in new[] { "kbps", "true_peak_dbtp", "cutoff_hz", "loss_above_cutoff_db", "residual_lufs", "residual_below_original_db", "read" })
                Assert.That(j.ContainsKey(k), k);
        }

        [Test]
        public void TheWorkIsCancellable()
        {
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => OpusPreview.Run(Music(48000, 2), 128, cts.Token));
        }
    }
}
