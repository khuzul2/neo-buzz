using System.Security.Cryptography;
using NeoBuzz.Dsp;
using NeoBuzz.Sampler;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Sampler, a sample per note (docs/specs/machines.md, Neo Sampler Tracker mode; TM1).</summary>
    [TestFixture]
    public class SamplerTrackerTests
    {
        const int Rate = Measure.Rate;

        [OneTimeSetUp]
        public void BuildTheBuiltIns() => BuiltIns.BuildNow();

        static string Hash(float[] l, float[] r)
        {
            using var sha = SHA256.Create();
            var bytes = new byte[(l.Length + r.Length) * 4];
            Buffer.BlockCopy(l, 0, bytes, 0, l.Length * 4);
            Buffer.BlockCopy(r, 0, bytes, l.Length * 4, r.Length * 4);
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        /// <summary>The render the sampler made before Tracker mode existed: Instrument and Kit modes, every path of NoteOn.</summary>
        static string Render(int mode)
        {
            var (e, v, bank) = SamplerTests.Make(v =>
            {
                v.Set(P.Mode, mode);
                v.Set(SamplerTests.Z(0, SamplerEngine.FWave), 221);
                v.Set(SamplerTests.Z(0, SamplerEngine.FKeyHi), 59);
                v.Set(SamplerTests.Z(1, SamplerEngine.FWave), 1);
                v.Set(SamplerTests.Z(1, SamplerEngine.FKeyLo), 60);
                v.Set(SamplerTests.Z(1, SamplerEngine.FLoop), 2);
                SamplerTests.Set(v, SamplerTests.Z(1, SamplerEngine.FLoopS), 30.0);
                SamplerTests.Set(v, SamplerTests.Z(1, SamplerEngine.FLoopE), 70.0);
                v.Set(SamplerTests.Z(1, SamplerEngine.FChoke), 2);
                v.Set(P.FType, 2);
                v.Set(P.FReso, 500);
                v.Set(P.Polyphony, 3);
                v.Set(P.VelAmp, 700);
            });
            bank.Set(1, SamplerTests.Slot("noise", SamplerTests.Mono(Measure.PinkNoise(Rate, 0.2, 3), root: 64, rate: 44100)));
            var (l, r) = SamplerTests.Run(e, 2 * Rate, 64,
                (0, () => e.NoteOn(0, 36, 0.8f)),
                (1000, () => e.NoteOn(1, 37, 0.5f)),
                (7777, () => e.NoteOn(2, 62, 1f)),
                (9001, () => e.PitchBend(0.3f)),
                (12000, () => e.NoteOn(3, 66, 0.7f)),
                (20000, () => e.NoteOn(4, 52, 0.9f)),
                (30011, () => e.NoteOff(0)),
                (50000, () => e.NoteOff(2)),
                (70000, () => e.AllNotesOff(false)));
            return Hash(l, r);
        }

        [Test]
        public void InstrumentAndKitRenderAsBeforeTrackerMode()
        {
            Assert.That(Render(0), Is.EqualTo("ACEAC71C1538B82568952015CF6975605395ED59CC332D476811C1613491EF8F"), "Instrument mode");
            Assert.That(Render(1), Is.EqualTo("76A7C91F0DA1367667F718806FAC54E97291C3539602ADA530BB13873783B480"), "Kit mode");
        }
    }
}
