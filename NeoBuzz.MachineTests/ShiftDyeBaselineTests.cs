using System.Security.Cryptography;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Shift adds code beside Dye Essence's shifter effects and changes none of it: the renders of FrequencyShifter and PitchShiftDelay are pinned
    /// by hash. The hashes were taken from the classes as they were before Neo Shift (the base of its branch), so a change to either shows here.
    /// </summary>
    public class ShiftDyeBaselineTests
    {
        static string Hash(float[] l, float[] r)
        {
            var bytes = new byte[(l.Length + r.Length) * 4];
            Buffer.BlockCopy(l, 0, bytes, 0, l.Length * 4);
            Buffer.BlockCopy(r, 0, bytes, l.Length * 4, r.Length * 4);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        static (float[], float[]) Source()
        {
            var l = ShiftRig.Noise(48000 * 2, 0.4, 11);
            var r = ShiftRig.Noise(48000 * 2, 0.4, 12);
            var s = ShiftRig.Sine(48000 * 2, 330, 0.3);
            for (int i = 0; i < l.Length; i++) { l[i] += s[i]; r[i] += s[i] * 0.5f; }
            return (l, r);
        }

        static string Render(Action<float[], float[], int> process)
        {
            var (l, r) = Source();
            for (int pos = 0; pos < l.Length; pos += 256)
            {
                var bl = new float[256]; var br = new float[256];
                Array.Copy(l, pos, bl, 0, 256); Array.Copy(r, pos, br, 0, 256);
                process(bl, br, 256);
                Array.Copy(bl, 0, l, pos, 256); Array.Copy(br, 0, r, pos, 256);
            }
            return Hash(l, r);
        }

        static string FrequencyShifterUp() { var f = new FrequencyShifter(48000) { ShiftHz = 137, Feedback = 0.4f, StereoHz = 3 }; return Render((l, r, n) => f.Process(l, r, n)); }
        static string FrequencyShifterRing() { var f = new FrequencyShifter(48000) { ShiftHz = -60, Ring = true, LfoHz = 2 }; return Render((l, r, n) => f.Process(l, r, n)); }
        static string PitchDelayUp() { var p = new PitchShiftDelay(48000) { Semitones = 7, Feedback = 0.5f, GrainMs = 40, TimeMs = 120, Spread = 0.3f }; return Render((l, r, n) => p.Process(l, r, n)); }
        static string PitchDelayDown() { var p = new PitchShiftDelay(48000) { Semitones = -12, Cents = 14, Feedback = 0.2f, GrainMs = 90, TimeMs = 30 }; return Render((l, r, n) => p.Process(l, r, n)); }

        [Test]
        public void DyeEssencesShifterEffectsRenderBitIdenticalToTheirBase()
        {
            var got = new Dictionary<string, string>
            {
                ["frequency shifter, 137 Hz with feedback"] = FrequencyShifterUp(),
                ["frequency shifter, ring"] = FrequencyShifterRing(),
                ["pitch-shift delay, up a fifth"] = PitchDelayUp(),
                ["pitch-shift delay, down an octave"] = PitchDelayDown(),
            };
            var want = new Dictionary<string, string>
            {
                ["frequency shifter, 137 Hz with feedback"] = "0c927ad1559391dea4bdf2d57762b2528a7010874974ee3e9f366fced8899c0c",
                ["frequency shifter, ring"] = "7cd792cb4005219bc43df6825e94771d201dab689d81d3321c21b43d50990e3a",
                ["pitch-shift delay, up a fifth"] = "050893f89b817805f3d617e23a6f227c61fcee6315c603dce8d52ced83ece963",
                ["pitch-shift delay, down an octave"] = "f18295a05e4c2350cdf8f786916c42cf4f8e99bd9783ef9e92802363055f158e",
            };
            foreach (var kv in got) Console.WriteLine($"SHIFTHASH {kv.Key} = {kv.Value}");
            foreach (var kv in want) Assert.That(got[kv.Key], Is.EqualTo(kv.Value), kv.Key);
        }
    }
}
