using System.Globalization;
using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Organ;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Rock Drive's retune (OR1 round 3): the first Rock Drive (Drive 100 %, a fast, deep, wide speaker) hurt with its panning and
    /// its distortion. Two candidates, measured against the presets the user kept, on the listening pack's progression:
    /// panning is the 95th percentile of |L - R| level per 10 ms (dB), fizz is the 3 - 8 kHz band's level re the full band (dB).
    /// </summary>
    internal static class OrganRockFigures
    {
        const int Rate = 48000;

        /// <summary>The first Rock Drive, raw values on top of the current preset (which only differs in these).</summary>
        public static readonly Dictionary<int, int> OldChanges = new()
        {
            [P.Drive] = 1000, [P.Speed] = 1, [P.RotDepth] = 800, [P.Spread] = 800, [P.Doppler] = 500, [P.Volume] = 778,
        };

        /// <summary>Candidate A: the fast speaker, tamed (Drive 60 %, Depth 45 %, Mic spread 35 %, Doppler 25 %).</summary>
        public static readonly Dictionary<int, int> AChanges = new()
        {
            [P.Drive] = 600, [P.Speed] = 1, [P.RotDepth] = 450, [P.Spread] = 350, [P.Doppler] = 125,
        };

        /// <summary>Candidate B, the one the user chose and the preset ships: the slow speaker with a little more drive (Drive 70 %, Depth 60 %, Mic spread 40 %, Doppler 40 %).</summary>
        public static readonly Dictionary<int, int> BChanges = new()
        {
            [P.Drive] = 700, [P.Speed] = 0, [P.RotDepth] = 600, [P.Spread] = 400, [P.Doppler] = 200,
        };

        public static Dictionary<int, int> Preset(string name) => OrganPack.Presets().First(p => p.Name.Contains(name)).Values;

        public static Dictionary<int, int> With(Dictionary<int, int> preset, Dictionary<int, int> changes)
        {
            var v = new Dictionary<int, int>(preset);
            foreach (var kv in changes) v[kv.Key] = kv.Value;
            return v;
        }

        /// <summary>95th percentile of |L - R| in dB over 10 ms windows where either channel is above -60 dBFS.</summary>
        public static double PanSwingDb(float[] l, float[] r)
        {
            int w = Rate / 100;
            var d = new List<double>();
            for (int k = 0; k + w <= l.Length; k += w)
            {
                double sl = 0, sr = 0;
                for (int i = k; i < k + w; i++) { sl += l[i] * (double)l[i]; sr += r[i] * (double)r[i]; }
                double rl = Math.Sqrt(sl / w), rr = Math.Sqrt(sr / w);
                if (Math.Max(rl, rr) < 1e-3) continue;
                d.Add(Math.Abs(20 * Math.Log10((rl + 1e-12) / (rr + 1e-12))));
            }
            d.Sort();
            return d[(int)(0.95 * (d.Count - 1))];
        }

        static double BandEnergy(float[] x, double lo, double hi)
        {
            var f = new Biquad[4];
            f[0].Set(BiquadType.HighPass, lo, Rate, 0.5412); f[1].Set(BiquadType.HighPass, lo, Rate, 1.3066);
            f[2].Set(BiquadType.LowPass, hi, Rate, 0.5412); f[3].Set(BiquadType.LowPass, hi, Rate, 1.3066);
            double e = 0;
            foreach (var s in x)
            {
                float y = s;
                for (int k = 0; k < f.Length; k++) y = f[k].Process(y);
                e += (double)y * y;
            }
            return e;
        }

        /// <summary>The 3 - 8 kHz band's level re the whole signal, both channels (dB).</summary>
        public static double FizzDb(float[] l, float[] r)
        {
            double band = BandEnergy(l, 3000, 8000) + BandEnergy(r, 3000, 8000), all = 0;
            for (int i = 0; i < l.Length; i++) all += l[i] * (double)l[i] + r[i] * (double)r[i];
            return 10 * Math.Log10(band / all);
        }

        /// <summary>Loudness: BS.1770 K-weighting (48 kHz coefficients), the mean square of both channels over the take (ungated).</summary>
        public static double Lufs(float[] l, float[] r)
        {
            double Ms(float[] x)
            {
                double z1 = 0, z2 = 0, y1 = 0, y2 = 0, s = 0;
                double w1 = 0, w2 = 0, v1 = 0, v2 = 0;
                foreach (var xs in x)
                {
                    double a = 1.53512485958697 * xs - 2.69169618940638 * z1 + 1.19839281085285 * z2 + 1.69065929318241 * y1 - 0.73248077421585 * y2;
                    z2 = z1; z1 = xs; y2 = y1; y1 = a;
                    double b = a - 2 * w1 + w2 + 1.99004745483398 * v1 - 0.99007225036621 * v2;
                    w2 = w1; w1 = a; v2 = v1; v1 = b;
                    s += b * b;
                }
                return s / x.Length;
            }
            return -0.691 + 10 * Math.Log10(Ms(l) + Ms(r));
        }

        public readonly record struct Figures(double Pan, double Fizz, double Lufs, double DriveFizz);

        /// <summary>The figures of a take; DriveFizz is how much the drive raises the 3 - 8 kHz band (re the full band) over the same
        /// take with Drive at 0: the distortion's own share of the fizz.</summary>
        public static Figures Of(Dictionary<int, int> values) => Of(values, OrganPack.Play(values));

        public static Figures Of(Dictionary<int, int> values, (float[] L, float[] R, List<int> Events) take)
        {
            var clean = OrganPack.Play(With(values, new Dictionary<int, int> { [P.Drive] = 0 }));
            double fizz = FizzDb(take.L, take.R);
            return new Figures(PanSwingDb(take.L, take.R), fizz, Lufs(take.L, take.R), fizz - FizzDb(clean.L, clean.R));
        }
    }

    [TestFixture]
    public class OrganRockCandidates
    {
        /// <summary>The Rock Drive preset as shipped is no wider (panning) than the wider of Full Gospel and Fast Whirl and no fizzier
        /// than Full Gospel, on the pack's progression, and its drive still adds clearly to its 3 - 8 kHz band (a driven organ).</summary>
        [Test]
        public void RockDriveIsNoWiderNorFizzierThanTheKeptPresets()
        {
            var gospel = OrganRockFigures.Of(OrganRockFigures.Preset("Full Gospel"));
            var whirl = OrganRockFigures.Of(OrganRockFigures.Preset("Fast Whirl"));
            var rock = OrganRockFigures.Of(OrganRockFigures.Preset("Rock Drive"));
            Measure.Report(string.Create(CultureInfo.InvariantCulture,
                $"Neo Organ Rock Drive: panning {rock.Pan:F2} dB (Full Gospel {gospel.Pan:F2}, Fast Whirl {whirl.Pan:F2}); fizz {rock.Fizz:F1} dB (Full Gospel {gospel.Fizz:F1}), the drive adding {rock.DriveFizz:F1} dB"));
            Assert.That(rock.Pan, Is.LessThanOrEqualTo(Math.Max(gospel.Pan, whirl.Pan)), "panning");
            Assert.That(rock.Fizz, Is.LessThanOrEqualTo(gospel.Fizz), "fizz");
            Assert.That(rock.DriveFizz, Is.GreaterThan(3), "still a driven organ: the drive at least doubles its 3 - 8 kHz band");
        }

        /// <summary>
        /// Renders the first Rock Drive and candidates A and B on the pack's progression, at the same loudness, into ORGAN_PACK/rock,
        /// with the click detector on each, and prints the figures against Full Gospel and Fast Whirl. Both candidates must be no
        /// wider than the wider of those two and no fizzier than Full Gospel.
        /// </summary>
        [Test, Explicit("writes the Rock Drive candidates into the local listening pack")]
        public void RenderTheRockCandidates()
        {
            var dir = Environment.GetEnvironmentVariable("ORGAN_PACK");
            Assert.That(dir, Is.Not.Null.And.Not.Empty, "ORGAN_PACK names the folder");
            var rock = Path.Combine(dir!, "rock");
            Directory.CreateDirectory(rock);
            var current = OrganRockFigures.Preset("Rock Drive");
            var takes = new (string File, string Name, Dictionary<int, int> Values)[]
            {
                ("rock-old.wav", "Rock Drive, first (old)", OrganRockFigures.With(current, OrganRockFigures.OldChanges)),
                ("rock-A.wav", "Rock Drive A (fast, tamed)", OrganRockFigures.With(current, OrganRockFigures.AChanges)),
                ("rock-B.wav", "Rock Drive B (slow, more drive)", OrganRockFigures.With(current, OrganRockFigures.BChanges)),
            };
            var gospel = OrganRockFigures.Of(OrganRockFigures.Preset("Full Gospel"));
            var whirl = OrganRockFigures.Of(OrganRockFigures.Preset("Fast Whirl"));
            double panLimit = Math.Max(gospel.Pan, whirl.Pan), fizzLimit = gospel.Fizz;
            var sb = new StringBuilder("take\tpanning p95 (dB)\tfizz 3-8 kHz re full (dB)\tof which the drive adds (dB)\tloudness as set (LUFS, ungated)\tclicks\t1-ms windows > 20x median\n");
            void Row(string name, OrganRockFigures.Figures f, string clicks = "", string windows = "") =>
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{name}\t{f.Pan:0.00}\t{f.Fizz:0.0}\t{f.DriveFizz:+0.0;-0.0}\t{f.Lufs:0.0}\t{clicks}\t{windows}"));
            Row("Full Gospel (kept)", gospel);
            Row("Fast Whirl (kept)", whirl);
            const double Target = -20;                                  // every take written at the same loudness
            var figures = new Dictionary<string, OrganRockFigures.Figures>();
            foreach (var (file, name, values) in takes)
            {
                var take = OrganPack.Play(values);
                var (l, r, _) = take;
                var f = OrganRockFigures.Of(values, take);
                figures[file] = f;
                float g = (float)Math.Pow(10, (Target - f.Lufs) / 20);
                for (int i = 0; i < l.Length; i++) { l[i] *= g; r[i] *= g; }
                int c = OrganPack.Clicks(l, false) + OrganPack.Clicks(r, false);
                var (windows, _) = OrganPack.D2Windows(l, r);
                OrganPack.WriteWav(Path.Combine(rock, file), l, r);
                Row(name, f, c.ToString(CultureInfo.InvariantCulture), windows.ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"limits: panning <= {panLimit:0.00} dB (the wider of Full Gospel and Fast Whirl), fizz <= {fizzLimit:0.0} dB (Full Gospel); files written at {Target} LUFS (ungated K-weighted)"));
            File.WriteAllText(Path.Combine(rock, "figures.txt"), sb.ToString());
            TestContext.Progress.WriteLine(sb.ToString());
            foreach (var file in new[] { "rock-A.wav", "rock-B.wav" })
            {
                Assert.That(figures[file].Pan, Is.LessThanOrEqualTo(panLimit), $"{file}: panning");
                Assert.That(figures[file].Fizz, Is.LessThanOrEqualTo(fizzLimit), $"{file}: fizz");
            }
        }
    }
}
