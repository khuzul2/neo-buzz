using System.Globalization;
using NeoBuzz.Dsp;
using NeoBuzz.Keys;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Keys' key noise (docs/specs/neo-keys.md): the thump of a Tine and a Reed is a mechanical knock with no hiss, and String's is unchanged.</summary>
    [TestFixture]
    public class KeysThumpTests
    {
        const int Rate = 48000;
        const int Len = 16384;

        /// <summary>The thump alone: a note at Key noise 100 % minus the same note at 0 %, the strike (and, with <paramref name="damper"/>, the damper's thump after the key goes up).</summary>
        static float[] ThumpAlone(int model, bool damper, float velocity = 0.8f, int note = 60)
        {
            float[] Go(double noise)
            {
                var v = new ParamValues(Params.Table);
                void S(int p, double phys) { var d = Params.Table[p]; v.Set(p, d.Raw(d.Kind == MapKind.Percent || d.Kind == MapKind.Bipolar ? phys / 100.0 : phys)); }
                v.Set(P.Model, model);
                S(P.Decay, 3); S(P.KeyNoise, noise); S(P.TremDepth, 0); S(P.Stereo, 0); S(P.Drive, 0); S(P.Tone, 0);
                S(P.Pickup, 20); S(P.Hardness, 1.5); S(P.Touch, 60); S(P.VelCurve, 0); S(P.Release, 300); S(P.Volume, 0);
                v.Set(P.Mute, 0); v.Set(P.PickupSel, 2);
                var e = new KeysEngine(v); e.SetSampleRate(Rate);
                var y = new float[Len];
                var l = new float[256]; var r = new float[256];
                for (int pos = 0; pos < Len; pos += 256)
                {
                    if (pos == 0) e.NoteOn(1, note, velocity);
                    if (damper && pos == 8192) e.NoteOff(1);
                    Array.Clear(l, 0, 256); Array.Clear(r, 0, 256);
                    e.Process(l, r, 256);
                    for (int i = 0; i < 256; i++) y[pos + i] = 0.5f * (l[i] + r[i]);
                }
                return y;
            }
            var a = Go(100); var b = Go(0);
            var d2 = new float[Len];
            for (int i = 0; i < Len; i++) d2[i] = a[i] - b[i];
            return d2;
        }

        /// <summary>Energy of <paramref name="x"/> from <paramref name="from"/> to <paramref name="to"/> samples, between two frequencies (rectangular frame: the thump has died by the end).</summary>
        static double Band(float[] x, int from, int to, double lo, double hi)
        {
            int n = to - from;
            var seg = new float[n];
            Array.Copy(x, from, seg, 0, n);
            var m = Fft.Magnitudes(seg, 0, n);
            double s = 0;
            for (int k = 1; k < m.Length; k++) { double f = k * (double)Rate / n; if (f >= lo && f < hi) s += m[k] * m[k]; }
            return s;
        }

        static double Db(double a, double b) => 10 * Math.Log10(Math.Max(a, 1e-30) / Math.Max(b, 1e-30));

        [TestCase(0)]
        [TestCase(1)]
        public void TheThumpOfATineAndAReedHasNoHiss(int model)
        {
            foreach (bool damper in new[] { false, true })
            {
                var x = ThumpAlone(model, damper);
                // The strike's thump sits in the first 8192 samples, the damper's in the second half; each is measured on its own frame.
                int from = damper ? 8192 : 0, to = damper ? Len : 8192;
                double tot = Band(x, from, to, 0, Rate), b5 = Band(x, from, to, 5000, Rate), b10 = Band(x, from, to, 10000, Rate), low = Band(x, from, to, 0, 1000);
                TestContext.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} {1}: below 1 kHz {2:0.0} dB, above 5 kHz {3:0.0} dB, above 10 kHz {4:0.0} dB (energy {5:0.0} dB re 1)",
                    model == 0 ? "Tine" : "Reed", damper ? "damper" : "strike", Db(low, tot), Db(b5, tot), Db(b10, tot), Db(tot, 1.0)));
                Assert.That(tot, Is.GreaterThan(1e-12), "there is a thump");
                Assert.That(Db(b5, tot), Is.LessThanOrEqualTo(-50), "above 5 kHz");
                Assert.That(Db(b10, tot), Is.LessThanOrEqualTo(-60), "above 10 kHz");
            }
        }
        // The presets Alessandro sent back after listening round 1, with the band above 10 kHz at the loudest onset of the phrase and the worst onset (dB under the total) as round 1 had them.
        // Bark Drive's bark is the sound itself: it stays 24 dB under with Key noise at 0 % (the hammer at 0.7 ms into the driven preamp), so it has its own limit; see the issue.
        static readonly (string Name, double Limit, double Round1Worst)[] SentBack =
        {
            ("Tine — Classic Ballad", -35, -20.1), ("Tine — Bark Drive", -20, -23.6), ("Tine — Stereo Tremolo", -35, -24.1),
            ("Reed — Soul Keys", -35, -24.1), ("Reed — Gritty", -35, -23.8),
        };

        [Test]
        public void TheSentBackPresetsHaveNoHissAtTheirOnsets()
        {
            int length = (int)(Rate * (60.0 / 96 * 24 + 1.6));
            var presets = KeysTests.FactoryPresets().ToDictionary(p => p.Name, p => p.Values);
            foreach (var (name, limit, r1) in SentBack)
            {
                var on = new List<int>();
                var t = KeysPack.Play(presets[name], length, on);
                var (worst, loud) = KeysPack.OnsetGaps(t, on);
                TestContext.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0}: band above 10 kHz at the worst onset {1:0.0} dB under the total (round 1: {2:0.0}), at the loudest onset {3:0.0}", name, worst, r1, loud));
                Assert.That(worst, Is.LessThanOrEqualTo(limit), name);
                Assert.That(ClickDetector.Find(t.L, t.R).Count, Is.EqualTo(0), name + ": clicks");
            }
        }

        // SHA-256 of the 32-bit float samples (left, then right) of the pack phrase on the two String presets that were kept; the same as at 8f1690bd5, before the thump changed.
        [TestCase("String — Funky Pluck", "07EAC21D74FAF02F3123D6D9EDE926EA39C8D4543712F86956B906329F7A34D7")]
        [TestCase("String — Muted Staccato", "1B49DF22BAC70FBB24FD867F5B433C948ACED12E7276364C435FF08AC1D2CE8F")]
        public void TheKeptStringPresetsAreBitIdenticalToBeforeTheKnock(string name, string sha)
        {
            int length = (int)(Rate * (60.0 / 96 * 24 + 1.6));
            var t = KeysPack.Play(KeysTests.FactoryPresets().First(p => p.Name == name).Values, length);
            var bytes = new byte[length * 8];
            Buffer.BlockCopy(t.L, 0, bytes, 0, length * 4); Buffer.BlockCopy(t.R, 0, bytes, length * 4, length * 4);
            Assert.That(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), Is.EqualTo(sha));
        }
    }
}
