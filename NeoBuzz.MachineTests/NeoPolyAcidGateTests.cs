using NeoBuzz.Dsp;
using static NeoBuzz.MachineTests.AcidLines;

namespace NeoBuzz.MachineTests
{
    /// <summary>The AC3 click gate on known signals, and on the AC2 listening pack (local only: it proves the gate flags what the listener heard).</summary>
    [TestFixture]
    public class NeoPolyAcidGateTests
    {
        const int Rate = Measure.Rate;

        /// <summary>24-bit mono PCM WAV (the pack's format) to floats.</summary>
        internal static float[] ReadWav24(string path)
        {
            var b = File.ReadAllBytes(path);
            int at = 12;
            while (at + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, at, 4);
                int len = BitConverter.ToInt32(b, at + 4);
                if (id == "data")
                {
                    var x = new float[len / 3];
                    for (int i = 0; i < x.Length; i++)
                    {
                        int v = b[at + 8 + 3 * i] | (b[at + 9 + 3 * i] << 8) | (b[at + 10 + 3 * i] << 16);
                        if ((v & 0x800000) != 0) v -= 1 << 24;
                        x[i] = v / 8388607f;
                    }
                    return x;
                }
                at += 8 + len;
            }
            throw new InvalidDataException(path);
        }

        static float[] Bass(int seconds, Action<float[]> damage = null)
        {
            // A 55 Hz sine with a smooth (raised-cosine) envelope per 0.25 s note: a dark bass where nothing else lives above 1 kHz.
            var x = new float[seconds * Rate];
            for (int i = 0; i < x.Length; i++)
            {
                double t = (i % (Rate / 4)) / (double)Rate;
                double env = t < 0.2 ? Math.Pow(Math.Sin(Math.PI * Math.Min(1, t / 0.02) / 2), 2) : t < 0.23 ? 0.5 * (1 + Math.Cos(Math.PI * (t - 0.2) / 0.03)) : 0;
                x[i] = (float)(0.4 * env * Math.Sin(2 * Math.PI * 55 * i / Rate));
            }
            damage?.Invoke(x);
            return x;
        }

        static List<int> Starts(int seconds) => Enumerable.Range(0, seconds * 4).Select(i => i * (Rate / 4)).ToList();
        static List<int> Ends(int seconds) => Enumerable.Range(0, seconds * 4).Select(i => i * (Rate / 4) + (int)(0.2 * Rate)).ToList();

        [Test]
        public void ACleanBassPasses()
        {
            var x = Bass(2);
            var rows = AcidGate.Analyse(x, Starts(2), Ends(2), true);
            Assert.That(rows.Where(r => r.Bad), Is.Empty, AcidGate.Table(rows));
        }

        [Test]
        public void ATinyStepOnADarkBassAtANoteStartIsFlagged()
        {
            // 0.003 of a step in level, the size of Sub's note-start clicks in the AC2 pack: the old detector's floor was 0.02.
            var x = Bass(2, y => { for (int i = 3 * (Rate / 4) + 100; i < y.Length; i++) y[i] += 0.0025f; });
            var rows = AcidGate.Analyse(x, Starts(2), Ends(2), true);
            
            
            Assert.That(rows[3].Near2d, Is.GreaterThan(0.002), "the step reads as its own size in the second difference");
            Assert.That(rows[3].BadRatio, Is.True, AcidGate.Table(rows));
        }

        [Test]
        public void ASawsOwnEdgesThroughAWideFilterAreNotABoundarySpike()
        {
            var osc = new BlepOscillator();
            osc.SetShape(2f, 0.5f);
            var x = new float[Rate];
            for (int i = 0; i < x.Length; i++) x[i] = 0.5f * osc.Next(110.0 / Rate, out _, out _);
            var rows = AcidGate.Analyse(x, new List<int> { 0 }, new List<int> { 40000 });
            Assert.That(rows[0].BadRatio, Is.False, "a bright saw is loud in the second difference everywhere: a buzz is not a boundary spike, and the ratio gate leaves it alone");
        }

        [Test]
        public void AGainStepInAnAcidLineIsFlagged()
        {
            var x = Bass(2, y => { for (int i = 5 * (Rate / 4) + 100; i < y.Length; i++) y[i] *= 1.5f; });
            var rows = AcidGate.Analyse(x, Starts(2), Ends(2), true);
            Assert.That(rows[5].Bad, Is.True, AcidGate.Table(rows));
        }

        // ── the AC2 pack: what the gate says about the renders the listener heard clicks in (local only) ──

        static readonly Dictionary<string, Note[]> PackLines = new() { ["pack"] = Pack, ["busy"] = Busy };

        static string PackDir => Environment.GetEnvironmentVariable("NEO_ACID_PACK2_DIR");

        static List<AcidGate.Row> PackRows(string file, Note[] line, bool dark = false)
        {
            var x = ReadWav24(Path.Combine(PackDir, "named", file));
            return AcidGate.Analyse(x, Sequence(line, 2), dark);
        }

        [Test]
        public void TheGateFlagsThePackTwoRendersAtTheStepsTheListenerNamed()
        {
            if (string.IsNullOrEmpty(PackDir)) Assert.Ignore("local only: NEO_ACID_PACK2_DIR = the AC2 pack folder");
            var squelch = PackRows("Acid-Squelch-ladder24.wav", Pack);
            var s = squelch.Where(r => r.Bad).Select(r => r.Step).ToList();
            Measure.Report("AC2 Squelch ladder: " + AcidGate.Flags(squelch));
            Assert.That(s, Does.Contain(5).And.Contain(9), "Squelch steps 5 to 9");
            var squelchD = PackRows("Acid-Squelch-diode18.wav", Pack);
            Assert.That(squelchD.Where(r => r.Bad).Select(r => r.Step), Does.Contain(9));
            foreach (var f in new[] { "Acid-Rubber-diode18.wav", "Acid-Rubber-ladder24.wav" })
            {
                var rows = PackRows(f, Pack);
                Measure.Report($"AC2 {f}: " + AcidGate.Flags(rows));
                Assert.That(rows.Count(r => r.Bad), Is.GreaterThanOrEqualTo(3), f + " clicks everywhere");
            }
            var sub = PackRows("Acid-Sub-busy-line.wav", Busy, true);
            Measure.Report("AC2 Sub busy: " + AcidGate.Flags(sub));
            Assert.That(sub.Where(r => r.Bad).Select(r => r.Step), Is.SupersetOf(new[] { 9, 11, 13, 17, 25, 27 }), "Sub's high-pitched note starts");
            // Sub: "a lot of high pitched clicking" - the gate flags most of its steps (note starts and note ends).
            Assert.That(sub.Count(r => r.Bad), Is.GreaterThanOrEqualTo(10));
        }
    }
}
