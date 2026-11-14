using System;
using System.IO;
using NUnit.Framework;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;
using ReBuzzTests.Automation;

namespace ReBuzz.AgentTests
{
    /// <summary>render(bits, dither) (docs/specs/mastering-tools.md §5.9): the master rounded to a 16 or 24 bit grid and written as exact PCM.</summary>
    [TestFixture]
    public class RequantizeTests
    {
        static StereoAudio Noise(int frames)
        {
            var rng = new Random(4);
            var l = new float[frames];
            var r = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                l[i] = (float)(0.5 * Math.Sin(i * 0.05) + 0.01 * (rng.NextDouble() - 0.5));
                r[i] = (float)(0.4 * Math.Sin(i * 0.031) + 0.01 * (rng.NextDouble() - 0.5));
            }
            l[10] = 1.5f;     // over full scale: clipped to the top code
            r[11] = -2f;
            return new StereoAudio { Left = l, Right = r, Rate = 48000 };
        }

        [TestCase(16, "tpdf")]
        [TestCase(16, "psychoacoustic")]
        [TestCase(24, "light")]
        [TestCase(24, "off")]
        public void TheFileHoldsExactlyTheRoundedSamples(int bits, string dither)
        {
            var a = Noise(48000);
            var q = Requantize.To(a, bits, dither);
            double scale = Math.Pow(2, bits - 1);
            foreach (var ch in new[] { q.Left, q.Right })
                foreach (float x in ch)
                {
                    double k = x * scale;
                    Assert.That(k, Is.EqualTo(Math.Round(k)));
                    Assert.That(k, Is.InRange(-scale, scale - 1));
                }
            string path = TestTemp.Path($"requantize-{bits}-{dither}-{Guid.NewGuid():N}.wav");
            try
            {
                var inter = new float[q.Frames * 2];
                for (int i = 0; i < q.Frames; i++) { inter[2 * i] = q.Left[i]; inter[2 * i + 1] = q.Right[i]; }
                Wav.WriteStereoPcm(path, inter, q.Frames, q.Rate, bits);
                var back = Wav.Read(path);
                Assert.That(back.Left, Is.EqualTo(q.Left));
                Assert.That(back.Right, Is.EqualTo(q.Right));
                Assert.That(back.Rate, Is.EqualTo(48000));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void TheSameRenderGivesTheSameBits()
        {
            var a = Noise(20000);
            Assert.That(Requantize.To(a, 16, "tpdf").Left, Is.EqualTo(Requantize.To(a, 16, "tpdf").Left));
        }

        [Test]
        public void ADitherThatIsntOneIsRefused()
        {
            Assert.Throws<ToolError>(() => Requantize.To(Noise(100), 16, "triangle"));
            Assert.Throws<ToolError>(() => Requantize.To(Noise(100), 20, "tpdf"));
        }
    }
}
