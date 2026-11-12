#nullable enable
using System.Security.Cryptography;
using NeoBuzz.Dsp;
using NeoBuzz.Eq;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo EQ v2 (docs/specs/mastering-tools.md §5.3): at the new parameters' defaults the parametric and graphic modes render
    /// as before v2, bit for bit. The hashes were taken from the engine before v2 (machines/mastering-m2 at ed3d09a9) on
    /// these signals, settings and automation.
    /// </summary>
    [TestFixture]
    public class EqClassicTests
    {
        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static void Mastering(ParamValues v)
        {
            EqTests.Band(v, 0, BandType.LowCut, 30, slope: 1);
            EqTests.Band(v, 1, BandType.LowShelf, 100, 2);
            EqTests.Band(v, 2, BandType.Bell, 300, -3, 1.4);
            EqTests.Band(v, 3, BandType.Bell, 3000, 1.5, 0.7);
            EqTests.Band(v, 4, BandType.Notch, 6000, 0, 8);
            EqTests.Band(v, 6, BandType.HighShelf, 10000, 2);
            EqTests.Band(v, 7, BandType.HighCut, 18000, slope: 2);
            Set(v, P.Output, -1);
        }

        static void Graphic(ParamValues v)
        {
            v.Set(P.Mode, 1);
            for (int k = 0; k < GraphicDesign.Bands; k++) Set(v, GraphicDesign.Slider(k), Math.Round(9 * Math.Sin(k * 0.7), 1));
        }

        internal static readonly (string Name, int Rate, Action<ParamValues> Setup, Action<ParamValues, int>? Automate)[] Settings =
        {
            ("defaults", 48000, v => { }, null),
            ("mastering curve", 48000, Mastering, null),
            ("mastering curve, 44.1 kHz", 44100, Mastering, null),
            ("moving bands", 48000, Mastering, (v, block) =>
            {
                Set(v, EqDesign.Freq(2), 200 * Math.Pow(2, (block % 40) / 10.0));
                Set(v, EqDesign.Gain(3), (block % 25) - 12);
                if (block == 60) v.Set(EqDesign.TypeOf(2), (int)BandType.HighShelf);
                if (block == 90) v.Set(EqDesign.On(6), 0);
                if (block == 120) v.Set(EqDesign.Slope(0), 2);
            }),
            ("graphic", 48000, Graphic, null),
            ("switching modes", 48000, Mastering, (v, block) =>
            {
                if (block == 50) Graphic(v);
                if (block == 150) v.Set(P.Mode, 0);
                if (block == 200) Set(v, GraphicDesign.Slider(12), -6);
            }),
        };

        internal static string Hash((string Name, int Rate, Action<ParamValues> Setup, Action<ParamValues, int>? Automate) s, int block = 256)
        {
            var v = new ParamValues(Params.Table);
            s.Setup(v);
            var e = new EqEngine(v);
            e.SetSampleRate(s.Rate);
            int length = s.Rate * 3;
            var (l, r) = LimiterTests.Drums(length);
            var noise = Measure.PinkNoise(length, 0.3, 5);
            for (int i = 0; i < length; i++) { l[i] = 0.5f * l[i] + noise[i]; r[i] = 0.5f * r[i] - 0.5f * noise[i]; }
            var bytes = new List<byte>();
            var bl = new float[EqEngine.MaxBlock];
            var br = new float[EqEngine.MaxBlock];
            int at256 = -1;
            for (int pos = 0; pos < length; pos += block)
            {
                // Automation on a 256-sample clock whatever the block, so both block sizes see the same moves.
                if (s.Automate != null && pos / 256 != at256)
                {
                    at256 = pos / 256;
                    s.Automate(v, at256);
                }
                int n = Math.Min(block, length - pos);
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

        /// <summary>
        /// The engine's output before v2 for each setting (the same at blocks of 256 and of 128), but "moving bands": it changes a
        /// live low cut's slope (block 120), which v2's fix now hears (EqGraphicTests.SlopeFix; before the fix it rendered
        /// 92808B034F3BFF6E, as v1). Every other render is v1's, bit for bit.
        /// </summary>
        static readonly string[] Before = { "0E06D4BFB7E296C5", "2ADECB5AC0D0069E", "0F75D5FF4F3A6C35", "B47DCA3D22D0D9EF", "E6B8A72DA9CDA39B", "0620CE7D93D616AB" };

        [Test]
        public void ParametricAndGraphicRenderAsBeforeV2BitForBit()
        {
            for (int k = 0; k < Settings.Length; k++)
            {
                Assert.That(Hash(Settings[k]), Is.EqualTo(Before[k]), Settings[k].Name);
                Assert.That(Hash(Settings[k], 128), Is.EqualTo(Before[k]), Settings[k].Name + ", blocks of 128");
            }
            Measure.Report($"Neo EQ: the renders before v2 in {Settings.Length} settings, at blocks of 256 and 128");
        }
    }
}
