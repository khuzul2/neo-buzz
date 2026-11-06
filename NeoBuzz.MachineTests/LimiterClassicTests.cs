using System.Security.Cryptography;
using NeoBuzz.Dsp;
using NeoBuzz.Limiter;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Limiter v2 (docs/specs/mastering-tools.md §5.2): at the new parameters' defaults ("Classic") it renders v1's bits.
    /// The hashes were taken from v1's engine (main before M2) on these signals and settings.
    /// </summary>
    [TestFixture]
    public class LimiterClassicTests
    {
        internal static readonly (string Name, Action<ParamValues> Setup)[] Settings =
        {
            ("defaults", v => { }),
            ("+12 dB, auto", v => v.Set(P.InputGain, Params.Table[P.InputGain].Raw(12))),
            ("+18 dB, set 50 ms, 0.5 ms", v =>
            {
                v.Set(P.InputGain, Params.Table[P.InputGain].Raw(18));
                v.Set(P.AutoRelease, 0);
                v.Set(P.Release, Params.Table[P.Release].Raw(0.05));
                v.Set(P.Lookahead, Params.Table[P.Lookahead].Raw(0.5));
            }),
            ("+9 dB, link 40 %, 5 ms, −3 dB", v =>
            {
                v.Set(P.InputGain, Params.Table[P.InputGain].Raw(9));
                v.Set(P.Link, Params.Table[P.Link].Raw(0.4));
                v.Set(P.Lookahead, Params.Table[P.Lookahead].Raw(5));
                v.Set(P.Ceiling, Params.Table[P.Ceiling].Raw(-3));
            }),
        };

        internal static string Hash(Action<ParamValues> setup, int block = 256)
        {
            var v = new ParamValues(Params.Table);
            setup(v);
            var e = new LimiterEngine(v);
            e.SetSampleRate(Measure.Rate);
            var (l, r) = LimiterTests.Drums(Measure.Rate * 3);
            var noise = Measure.PinkNoise(Measure.Rate * 3, 0.3, 5);
            for (int i = 0; i < l.Length; i++) { l[i] += noise[i]; r[i] -= 0.5f * noise[i]; }
            var bytes = new List<byte>();
            var bl = new float[256];
            var br = new float[256];
            for (int pos = 0; pos < l.Length; pos += block)
            {
                int n = Math.Min(block, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                for (int i = 0; i < n; i++)
                {
                    bytes.AddRange(BitConverter.GetBytes(bl[i]));
                    bytes.AddRange(BitConverter.GetBytes(br[i]));
                }
            }
            return Convert.ToHexString(SHA256.HashData(bytes.ToArray()))[..16];
        }

        /// <summary>v1's output for each setting (main at 8747dd3, before M2): the same at blocks of 256 and 117.</summary>
        static readonly string[] V1 = { "5D10AE84E9FFDB61", "83C4E0ECEAEA0726", "34428A12058A0397", "A91162D42C7F2D52" };

        [Test]
        public void ClassicRendersV1BitForBit()
        {
            for (int k = 0; k < Settings.Length; k++)
            {
                Assert.That(Hash(Settings[k].Setup), Is.EqualTo(V1[k]), Settings[k].Name);
                Assert.That(Hash(Settings[k].Setup, 117), Is.EqualTo(V1[k]), Settings[k].Name + ", blocks of 117");
            }
            Measure.Report($"Neo Limiter Classic: v1's bits in {Settings.Length} settings, at blocks of 256 and 117");
        }
    }
}
