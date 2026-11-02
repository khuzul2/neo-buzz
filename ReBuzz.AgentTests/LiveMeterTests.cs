using System.Text.Json.Nodes;
using ReBuzz.Agent.Audio;

namespace ReBuzz.AgentTests
{
    /// <summary>
    /// Live meters (docs/specs/mcp-server.md §5.8, A7): a window filled from a tap's blocks, in
    /// Buzz's ±32768 scale, then measured as analyze measures renders.
    /// </summary>
    [TestFixture]
    public class LiveMeterTests
    {
        const int Rate = 48000;

        /// <summary>Interleaved stereo blocks of a 1 kHz sine, <paramref name="dbfs"/> peak, in Buzz's scale.</summary>
        static IEnumerable<float[]> Blocks(double seconds, double dbfs, int block = 512)
        {
            double a = Math.Pow(10, dbfs / 20) * 32768;
            int frames = (int)(seconds * Rate);
            for (int at = 0; at < frames; at += block)
            {
                int n = Math.Min(block, frames - at);
                var b = new float[n * 2];
                for (int i = 0; i < n; i++)
                    b[2 * i] = b[2 * i + 1] = (float)(a * Math.Sin(2 * Math.PI * 1000 * (at + i) / Rate));
                yield return b;
            }
        }

        static double Number(JsonObject o, string key) => o[key]!.GetValue<double>();

        [Test]
        public void TheWindowFillsFromBlocksAndStopsAtItsLength()
        {
            var meter = new LiveMeter("Master", Rate / 2, 1 / 32768f);
            foreach (var b in Blocks(1.0, -6)) meter.Add(b, stereo: true);
            Assert.That(meter.Full, Is.True);
            Assert.That(meter.Frames, Is.EqualTo(Rate / 2), "half a second, not the whole second offered");
            var audio = meter.Audio(Rate);
            Assert.That(audio.Left.Max(), Is.EqualTo(Math.Pow(10, -6 / 20.0)).Within(0.001), "scaled to full scale = 1");
        }

        [Test]
        public void MonoBlocksFeedBothChannelsAndGainScales()
        {
            var meter = new LiveMeter("Bass", 4, 1 / 32768f);
            meter.Add(new[] { 32768f, -16384f, 0f, 8192f }, 0, 4, stereo: false, gain: 0.5f);
            var audio = meter.Audio(Rate);
            Assert.That(audio.Left, Is.EqualTo(new[] { 0.5f, -0.25f, 0f, 0.125f }));
            Assert.That(audio.Right, Is.EqualTo(audio.Left));
        }

        [TestCase(-14.0)]
        [TestCase(-23.0)]
        public void ASteadySineReadsItsLevelNowAndOverTheWindow(double dbfs)
        {
            var meter = new LiveMeter("Master", 4 * Rate, 1 / 32768f);
            foreach (var b in Blocks(4, dbfs)) meter.Add(b, stereo: true);
            var r = LiveMeter.Report(meter.Audio(Rate));
            Assert.That(r["silent"]!.GetValue<bool>(), Is.False);
            Assert.That(Number(r, "momentary_lufs"), Is.EqualTo(dbfs).Within(0.1));
            Assert.That(Number(r, "short_term_lufs"), Is.EqualTo(dbfs).Within(0.1), "a 4 s window has a short-term reading");
            Assert.That(Number(r, "integrated_lufs"), Is.EqualTo(dbfs).Within(0.1));
            Assert.That(Number(r, "true_peak_dbtp"), Is.EqualTo(dbfs).Within(0.1));
            Assert.That(Number(r, "rms_dbfs"), Is.EqualTo(dbfs - 3.01).Within(0.05), "a sine's RMS is 3 dB under its peak");
            Assert.That(r["clipped_samples"]!.GetValue<int>(), Is.EqualTo(0));
        }

        [Test]
        public void NowIsTheEndOfTheWindow()
        {
            // Loud, then quiet: momentary reads the quiet end; the window's loudest moment, the loud start.
            var meter = new LiveMeter("Master", 2 * Rate, 1 / 32768f);
            foreach (var b in Blocks(1, -10).Concat(Blocks(1, -30))) meter.Add(b, stereo: true);
            var r = LiveMeter.Report(meter.Audio(Rate));
            Assert.That(Number(r, "momentary_lufs"), Is.EqualTo(-30).Within(0.2));
            Assert.That(Number(r, "momentary_max_lufs"), Is.EqualTo(-10).Within(0.2));
            Assert.That(r["short_term_lufs"], Is.Null, "no short-term reading from a window under 3 s");
        }

        [Test]
        public void SilenceAndClippingAreSaid()
        {
            var quiet = new LiveMeter("Pad", Rate, 1 / 32768f);
            quiet.Add(new float[2 * Rate], stereo: true);
            var q = LiveMeter.Report(quiet.Audio(Rate));
            Assert.That(q["silent"]!.GetValue<bool>(), Is.True);
            Assert.That(q["momentary_lufs"]!.GetValue<string>(), Is.EqualTo("-inf"));
            Assert.That(LiveMeter.Line("Pad", q), Is.EqualTo("Pad: silent"));

            var hot = new LiveMeter("Master", Rate, 1 / 32768f);
            foreach (var b in Blocks(1, 3)) hot.Add(b, stereo: true);
            var h = LiveMeter.Report(hot.Audio(Rate));
            Assert.That(h["clipped_samples"]!.GetValue<int>(), Is.GreaterThan(1000));
            Assert.That(LiveMeter.Line("Master", h), Does.Contain("clipped samples"));
        }

        [Test]
        public void AnEmptyWindowIsSilent()
        {
            var r = LiveMeter.Report(new LiveMeter("Master", 100, 1).Audio(Rate));
            Assert.That(r["silent"]!.GetValue<bool>(), Is.True);
            Assert.That(Number(r, "seconds"), Is.EqualTo(0));
        }
    }
}
