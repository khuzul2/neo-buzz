using NeoBuzz.Analysis;

namespace ReBuzz.AgentTests
{
    /// <summary>
    /// Loudness and true peak (docs/specs/mcp-server.md §7) against the EBU Tech 3341 and 3342
    /// test signals, generated here: stereo 1 kHz sines at given dBFS (peak), in sections.
    /// </summary>
    [TestFixture]
    public class LoudnessTests
    {
        const int Rate = 48000;

        static (float[] L, float[] R) Sections(params (double Seconds, double Dbfs)[] sections)
        {
            int n = (int)(sections.Sum(s => s.Seconds) * Rate);
            var l = new float[n];
            int at = 0;
            foreach (var (seconds, db) in sections)
            {
                double a = Math.Pow(10, db / 20);
                int len = (int)(seconds * Rate);
                for (int i = 0; i < len; i++) l[at + i] = (float)(a * Math.Sin(2 * Math.PI * 1000 * (at + i) / Rate));
                at += len;
            }
            return (l, (float[])l.Clone());
        }

        [TestCase(-23.0)]
        [TestCase(-33.0)]
        public void SteadySineReadsItsLevel(double dbfs)
        {
            var (l, r) = Sections((20, dbfs));
            var m = Loudness.Measure(l, r, Rate);
            Assert.That(m.Integrated, Is.EqualTo(dbfs).Within(0.1), "integrated");
            Assert.That(m.MomentaryMax, Is.EqualTo(dbfs).Within(0.1), "momentary");
            Assert.That(m.ShortTermMax, Is.EqualTo(dbfs).Within(0.1), "short-term");
        }

        [Test]
        public void GatingIgnoresQuietParts()
        {
            var (l, r) = Sections((10, -36), (60, -23), (10, -36));
            Assert.That(Loudness.Measure(l, r, Rate).Integrated, Is.EqualTo(-23).Within(0.1), "Tech 3341 case 3");
            (l, r) = Sections((10, -72), (10, -36), (60, -23), (10, -36), (10, -72));
            Assert.That(Loudness.Measure(l, r, Rate).Integrated, Is.EqualTo(-23).Within(0.1), "Tech 3341 case 4");
            var silence = Loudness.Measure(new float[Rate * 5], new float[Rate * 5], Rate);
            Assert.That(double.IsNegativeInfinity(silence.Integrated), Is.True);
        }

        [TestCase(-20.0, -30.0, 10.0)]
        [TestCase(-20.0, -15.0, 5.0)]
        [TestCase(-40.0, -20.0, 20.0)]
        public void LoudnessRangeFollowsTech3342(double first, double second, double lra)
        {
            var (l, r) = Sections((20, first), (20, second));
            Assert.That(Loudness.Measure(l, r, Rate).Range, Is.EqualTo(lra).Within(1));
        }

        [Test]
        public void TruePeakSeesBetweenTheSamples()
        {
            // A full-scale sine at a quarter of the sample rate, 45° off: every sample sits at ±0.707.
            int n = Rate;
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = (float)Math.Sin(2 * Math.PI * i / 4 + Math.PI / 4);
            var m = Loudness.Measure(x, x, Rate);
            Assert.That(m.SamplePeakDb, Is.EqualTo(-3.01).Within(0.02));
            Assert.That(m.TruePeakDb, Is.EqualTo(0).Within(0.2), "the true peak is full scale");
            Assert.That(m.Clipped, Is.EqualTo(0));

            var (l, r) = Sections((5, -6));
            var sine = Loudness.Measure(l, r, Rate);
            Assert.That(sine.TruePeakDb, Is.EqualTo(-6).Within(0.05), "a 1 kHz sine's true peak is its amplitude");
        }

        [Test]
        public void MonoCountsAsBothSpeakers()
        {
            var (l, _) = Sections((10, -23));
            Assert.That(Loudness.Measure(l, null, Rate).Integrated, Is.EqualTo(-23).Within(0.1));
        }
    }
}
