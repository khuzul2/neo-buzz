using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.Eq;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo EQ's graphic mode (docs/specs/machines.md §5, machine-safety.md §4): the response against the
    /// sliders, constant-Q bells, the top bands near Nyquist, the extremes, automation, blocks,
    /// subnormals, allocation, CPU; and the parametric mode bit-identical to before it was added.
    /// </summary>
    [TestFixture]
    public class EqGraphicTests
    {
        const int N = GraphicDesign.Bands;

        static (EqEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, int rate = Measure.Rate)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Mode, 1);
            setup?.Invoke(v);
            var e = new EqEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Sliders(ParamValues v, double[] db)
        {
            for (int k = 0; k < N; k++) SetValue(v, GraphicDesign.Slider(k), db[k]);
        }

        static double[] Read(ParamValues v)
        {
            var db = new double[N];
            for (int k = 0; k < N; k++) db[k] = v.Value(GraphicDesign.Slider(k));
            return db;
        }

        /// <summary>Random sliders across the whole range, in 0.1 dB steps.</summary>
        static double[] Random(Random rng, double range = GraphicDesign.MaxDb)
        {
            var db = new double[N];
            for (int k = 0; k < N; k++) db[k] = Math.Round((rng.NextDouble() * 2 - 1) * range * 10) / 10;
            return db;
        }

        static double[] All(double db) => Enumerable.Repeat(db, N).ToArray();

        static double[] Zigzag(double db) => Enumerable.Range(0, N).Select(k => k % 2 == 0 ? db : -db).ToArray();

        /// <summary>A loudness "smile": up to +6 dB at both ends, 0 in the middle.</summary>
        static double[] Smile() => Enumerable.Range(0, N).Select(k => Math.Round(6 * Math.Pow(Math.Cos(Math.PI * k / (N - 1)), 4) * 10) / 10).ToArray();

        static (float[] L, float[] R) Run(EqEngine e, float[] l, float[] r, int block = EqEngine.MaxBlock, Action<int> before = null)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[EqEngine.MaxBlock];
            var br = new float[EqEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += block)
            {
                int n = Math.Min(block, l.Length - pos);
                before?.Invoke(pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        /// <summary>|H| in dB of an impulse response at <paramref name="hz"/> (a direct DTFT, rotation renormalised).</summary>
        static double Dtft(float[] h, double hz, int rate)
        {
            double th = 2 * Math.PI * hz / rate;
            double wr = Math.Cos(th), wi = -Math.Sin(th);
            double zr = 1, zi = 0, sr = 0, si = 0;
            for (int n = 0; n < h.Length; n++)
            {
                sr += h[n] * zr;
                si += h[n] * zi;
                double t = zr * wr - zi * wi;
                zi = zr * wi + zi * wr;
                zr = t;
                if ((n & 4095) == 4095)
                {
                    double m = 1 / Math.Sqrt(zr * zr + zi * zi);
                    zr *= m;
                    zi *= m;
                }
            }
            return 10 * Math.Log10(sr * sr + si * si + 1e-300);
        }

        static IEnumerable<double> LogFrequencies(double lo, double hi, int count)
        {
            for (int i = 0; i < count; i++) yield return lo * Math.Pow(hi / lo, i / (double)(count - 1));
        }

        static double MaxStep(float[] y, int from)
        {
            double m = 0;
            for (int i = Math.Max(1, from); i < y.Length; i++) m = Math.Max(m, Math.Abs(y[i] - y[i - 1]));
            return m;
        }
        static ulong Hash(float[] a, float[] b)
        {
            ulong h = 14695981039346656037UL;
            foreach (var x in a.Concat(b))
            {
                uint bits = (uint)BitConverter.SingleToInt32Bits(x);
                for (int k = 0; k < 4; k++)
                {
                    h ^= (bits >> (8 * k)) & 0xFF;
                    h *= 1099511628211UL;
                }
            }
            return h;
        }

        static void SetValue(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        internal static IEnumerable<(string Name, Action<ParamValues> Setup, Action<ParamValues, int> Block)> ParametricCases()
        {
            yield return ("defaults", _ => { }, null);
            foreach (var c in EqTests.Curves) yield return (c.Name, c.Setup, null);
            foreach (var (_, machine, name, values) in PresetTests.AllPresets())
            {
                if (machine != "Neo EQ" || values.GetValueOrDefault(P.Mode) == 1) continue;   // the graphic presets came with graphic mode
                var vals = values;
                // The 49 parameters songs and presets stored before graphic mode.
                yield return ("preset " + name, v => { foreach (var kv in vals) if (kv.Key >= 0 && kv.Key < 49) v.Set(kv.Key, kv.Value); }, null);
            }
            // Automation: a bell swept and its gain moved every block; type and on/off switches.
            yield return ("moves", v =>
            {
                for (int b = 0; b < EqDesign.Bands; b++) v.Set(EqDesign.On(b), 0);
                EqTests.Band(v, 2, BandType.Bell, 100, 12, 1);
            }, (v, pos) =>
            {
                int block = pos / 256;
                SetValue(v, EqDesign.Freq(2), 100 * Math.Pow(50, (block % 40) / 40.0));
                SetValue(v, EqDesign.Gain(2), 12 - 24 * ((block * 7) % 40) / 40.0);
                SetValue(v, P.Output, (block % 3) - 1);
            });
            yield return ("switches", v =>
            {
                for (int b = 0; b < EqDesign.Bands; b++) v.Set(EqDesign.On(b), 0);
                EqTests.Band(v, 2, BandType.Bell, 100, 12, 1);
                EqTests.Band(v, 5, BandType.LowCut, 300, 0, 2, 2);
            }, (v, pos) =>
            {
                if (pos == 2560) v.Set(EqDesign.TypeOf(2), (int)BandType.HighCut);
                if (pos == 5120) v.Set(EqDesign.On(2), 0);
                if (pos == 7680) v.Set(EqDesign.On(2), 1);
                if (pos == 10240) v.Set(EqDesign.Slope(5), 0);
                if (pos == 12800) v.Set(EqDesign.TypeOf(5), (int)BandType.Notch);
            });
        }

        /// <summary>
        /// The parametric renders' hashes before graphic mode was added (origin/main 80bc711): pink noise
        /// through the defaults, the test curves, every factory preset's 49 values and two automated cases,
        /// at 44.1 and 48 kHz. The portable maths makes them the same on every platform.
        /// </summary>
        static readonly Dictionary<string, ulong> Before = new()
        {
            ["44100 defaults"] = 0x8DAA266672FBC55AUL,
            ["44100 extremes (Q 30 at 20 Hz, ±24 dB, 48 dB/oct)"] = 0x3AC06A39F8A52807UL,
            ["44100 mastering curve"] = 0x9B3A8CAB5CEBE0C6UL,
            ["44100 mix curve (8 bands)"] = 0x830D4C926069566BUL,
            ["44100 moves"] = 0x189036C8190613E6UL,
            ["44100 preset Fix — Hum Notch 50 Hz"] = 0xD340F21BB03CD621UL,
            ["44100 preset Fix — Hum Notch 60 Hz"] = 0x9BE729BE9BC48FD9UL,
            ["44100 preset Fix — Rumble Filter"] = 0x1DA12988F000DE55UL,
            ["44100 preset Mastering — Air Shelf"] = 0x71BEB3362849E2EBUL,
            ["44100 preset Mastering — Gentle Smile"] = 0x05068CB6A739D157UL,
            ["44100 preset Mastering — Low Cut 25 Hz"] = 0xDAE7D8E4B23DB9E5UL,
            ["44100 preset Mastering — Tame Harshness"] = 0x2715FC186C598327UL,
            ["44100 preset Mixing — Acoustic Guitar"] = 0x2811B4ADB3DDBC7FUL,
            ["44100 preset Mixing — Bass Tighten"] = 0x48ED0BBBF6A729E3UL,
            ["44100 preset Mixing — De-mud"] = 0x9C22B007A868F47BUL,
            ["44100 preset Mixing — Kick Punch"] = 0x7B27002255B43F46UL,
            ["44100 preset Mixing — Snare Crack"] = 0x185A869C3DCE91F7UL,
            ["44100 preset Mixing — Telephone"] = 0xDD3D975423C7D82EUL,
            ["44100 preset Mixing — Vocal Clarity"] = 0x1A8765FF34CA4D0FUL,
            ["44100 switches"] = 0x776CD97899909854UL,
            ["48000 defaults"] = 0x064528945F99488FUL,
            ["48000 extremes (Q 30 at 20 Hz, ±24 dB, 48 dB/oct)"] = 0xA75EC0D87BD39751UL,
            ["48000 mastering curve"] = 0xF4C6E4B000395E98UL,
            ["48000 mix curve (8 bands)"] = 0x6459928536C051BEUL,
            ["48000 moves"] = 0xED9A50AFE715ADE8UL,
            ["48000 preset Fix — Hum Notch 50 Hz"] = 0xCE62A77D9EA1B0D9UL,
            ["48000 preset Fix — Hum Notch 60 Hz"] = 0xD5A48B3A0C84DA12UL,
            ["48000 preset Fix — Rumble Filter"] = 0xCCB3B792241C911FUL,
            ["48000 preset Mastering — Air Shelf"] = 0xA5CEA26CB8E9AB45UL,
            ["48000 preset Mastering — Gentle Smile"] = 0x702B56C61C50AF25UL,
            ["48000 preset Mastering — Low Cut 25 Hz"] = 0xCDDF71A25B6B593FUL,
            ["48000 preset Mastering — Tame Harshness"] = 0xAAAB5881E84396D6UL,
            ["48000 preset Mixing — Acoustic Guitar"] = 0xBED2588EEF4C812DUL,
            ["48000 preset Mixing — Bass Tighten"] = 0xAEE8FFDE25A4847CUL,
            ["48000 preset Mixing — De-mud"] = 0x1A473958B270817DUL,
            ["48000 preset Mixing — Kick Punch"] = 0xB1656997D06A101BUL,
            ["48000 preset Mixing — Snare Crack"] = 0xDA921432A2EB574BUL,
            ["48000 preset Mixing — Telephone"] = 0xF5E48738FDCA4A03UL,
            ["48000 preset Mixing — Vocal Clarity"] = 0x06237D3C94985468UL,
            ["48000 switches"] = 0x037ACF32336834B7UL,
        };

        /// <summary>
        /// v2's deliberate fix (docs/specs/mastering-tools.md §5.3): a live cut band ignored a new slope (SectionCount of a cut
        /// at 12 dB/oct is 1, so the slope check passed), until it was switched off and on. Now a slope change fades the band
        /// out and in as a type change does. "switches" changes a live low cut's slope at 0.21 s: its renders after that differ.
        /// </summary>
        static readonly Dictionary<string, ulong> SlopeFix = new()
        {
            ["44100 switches"] = 0x03B95985379EC9DEUL,
            ["48000 switches"] = 0x2870BC33066F6E8FUL,
        };

        [Test]
        public void ParametricModeIsBitIdenticalToBefore()
        {
            var changed = new List<string>();
            int count = 0;
            foreach (int rate in new[] { 44100, 48000 })
                foreach (var (name, setup, block) in ParametricCases())
                {
                    var v = new ParamValues(Params.Table);
                    setup(v);
                    var e = new EqEngine(v);
                    e.SetSampleRate(rate);
                    var l = Measure.PinkNoise(rate / 2, 0.2, 61);
                    var r = Measure.PinkNoise(rate / 2, 0.2, 62);
                    var (yl, yr) = EqTests.Run(e, l, r, block == null ? null : pos => block(v, pos));
                    string key = $"{rate} {name}";
                    Assert.That(Before.ContainsKey(key), Is.True, key);
                    ulong want = SlopeFix.TryGetValue(key, out ulong fixedHash) ? fixedHash : Before[key];
                    if (Hash(yl, yr) != want) changed.Add($"{key} 0x{Hash(yl, yr):X16}UL");
                    count++;
                }
            Measure.Report($"Neo EQ parametric mode: {count - changed.Count} of {count} renders bit-identical to before graphic mode (defaults, test curves, the 14 factory presets' stored values, automation; 44.1 and 48 kHz)");
            Assert.That(count, Is.EqualTo(Before.Count));
            Assert.That(changed, Is.Empty);
        }

        // ------------------------------------------------------------------ the response against the sliders

        [TestCase(44100)]
        [TestCase(48000)]
        public void TheResponseFollowsTheSliders(int rate)
        {
            // 1000 random settings over the whole range (adjacent sliders up to 36 dB apart), a smooth curve and
            // the zigzag ±18 dB: the response at every centre within ±1 dB of its slider. All sliders at +X: flat
            // +X within ±0.5 dB at 600 frequencies from 20 Hz to 20 kHz.
            var solver = new GraphicSolver(rate);
            var rng = new Random(2026);
            double worst = 0, sum = 0, maxGain = 0;
            int count = 0;
            string where = "";
            void Check(string name, double[] sliders)
            {
                solver.Solve(sliders);
                for (int k = 0; k < solver.Active; k++)
                {
                    double err = Math.Abs(solver.ResponseDb(GraphicDesign.Centres[k]) - sliders[k]);
                    sum += err;
                    count++;
                    if (err > worst)
                    {
                        worst = err;
                        where = $"{name} at {GraphicDesign.Labels[k]}";
                    }
                    maxGain = Math.Max(maxGain, Math.Abs(solver.Gains[k]));
                }
            }
            for (int i = 0; i < 1000; i++) Check($"random #{i}", Random(rng));
            Check("smile", Smile());
            Check("zigzag ±18 dB", Zigzag(18));
            Check("zigzag ∓18 dB", Zigzag(-18));
            double flat = 0;
            foreach (double x in new[] { -18, -12, -6, -0.1, 0.1, 6, 12, 18 })
            {
                solver.Solve(All(x));
                foreach (double f in LogFrequencies(20, 20000, 600)) flat = Math.Max(flat, Math.Abs(solver.ResponseDb(f) - x));
            }
            Measure.Report($"Neo EQ graphic at {rate} Hz: the response at the centres within {worst:F3} dB of the sliders (mean {sum / count:F3} dB; worst {where}) over 1000 random settings across ±18 dB, a smile and the zigzags; the bands' gains up to {maxGain:F1} dB. All sliders at +X: flat within {flat:F4} dB, 20 Hz – 20 kHz");
            Assert.That(solver.Active, Is.EqualTo(N));
            Assert.That(worst, Is.LessThan(1.0));
            Assert.That(flat, Is.LessThan(0.5));
            Assert.That(maxGain, Is.LessThanOrEqualTo(GraphicDesign.MaxFilterDb));
        }

        [Test]
        public void TheGraphicPresetsFollowTheirSliders()
        {
            // Each graphic factory preset: the response at every centre within 1 dB of its slider (48 kHz).
            var solver = new GraphicSolver(Measure.Rate);
            var lines = new List<string>();
            foreach (var (_, machine, name, values) in PresetTests.AllPresets())
            {
                if (machine != "Neo EQ" || values.GetValueOrDefault(P.Mode) != 1) continue;
                var v = new ParamValues(Params.Table);
                foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                var sliders = Read(v);
                solver.Solve(sliders);
                double worst = Enumerable.Range(0, N).Max(k => Math.Abs(solver.ResponseDb(GraphicDesign.Centres[k]) - sliders[k]));
                lines.Add($"{name} {worst:F3} dB");
                Assert.That(worst, Is.LessThan(1), name);
            }
            Measure.Report($"Neo EQ graphic presets, the response at the centres against the sliders: {string.Join("; ", lines)}");
            Assert.That(lines, Has.Count.EqualTo(7));
        }

        [TestCase(44100)]
        [TestCase(48000)]
        public void TheEngineRunsTheDesign(int rate)
        {
            // The engine's impulse response (DTFT at 160 frequencies, 20 Hz to 20 kHz) against the solver's curve:
            // a random setting, the smile, the zigzag ±18 dB (bands up to about 54 dB, ringing for seconds) and all
            // at +12 dB with Output −6 dB.
            double worst = 0;
            string where = "";
            var rng = new Random(7);
            foreach (var (name, sliders, output) in new[] { ("random", Random(rng), 0.0), ("smile", Smile(), 0.0), ("zigzag", Zigzag(18), 0.0), ("all +12 dB, Output −6 dB", All(12), -6.0) })
            {
                var (e, v) = Make(p => { Sliders(p, sliders); SetValue(p, P.Output, output); }, rate);
                var x = new float[1 << 19];
                x[0] = 1;
                var (h, _) = Run(e, x, x);
                var solver = new GraphicSolver(rate);
                solver.Solve(Read(v));
                foreach (double f in LogFrequencies(20, 20000, 160))
                {
                    double err = Math.Abs(Dtft(h, f, rate) - (solver.ResponseDb(f) + output));
                    if (err > worst)
                    {
                        worst = err;
                        where = $"{name} at {f:F0} Hz";
                    }
                }
            }
            Measure.Report($"Neo EQ graphic at {rate} Hz: the engine's response = the design within {worst:F5} dB ({where}), 160 frequencies 20 Hz – 20 kHz");
            Assert.That(worst, Is.LessThan(0.05));
        }

        // ------------------------------------------------------------------ the bells

        /// <summary>Where a bell's response crosses <paramref name="level"/> dB between two frequencies (bisection on log f).</summary>
        static double Crossing(Func<double, double> db, double lo, double hi, double level)
        {
            bool loAbove = Math.Abs(db(lo)) > Math.Abs(level);
            for (int i = 0; i < 80; i++)
            {
                double mid = Math.Sqrt(lo * hi);
                if ((Math.Abs(db(mid)) > Math.Abs(level)) == loAbove) lo = mid; else hi = mid;
            }
            return Math.Sqrt(lo * hi);
        }

        [TestCase(44100)]
        [TestCase(48000)]
        public void TheBellsAreConstantQ(int rate)
        {
            // Each band's bell alone, at ±1 … ±40 dB (the solver uses up to ±60): its half-gain points (half the gain in
            // dB) 2/3 octave apart at every gain. The lower point is exact by design. The upper one is checked where it
            // lies below 0.4 fs, strictly up to 0.1 fs (to 2.5 kHz at 44.1 kHz); above that Nyquist warps it a little
            // with the gain (and the top bands' lies beyond Nyquist: TheTopBandsKeepTheirShape).
            var solver = new GraphicSolver(rate);
            double worstLower = 0, worstWidth = 0, worstSpread = 0, lowSpread = 0;
            int bandsWithBoth = 0, lowBands = 0;
            for (int k = 0; k < solver.Active; k++)
            {
                var shape = solver.Shapes[k];
                double f0 = GraphicDesign.Centres[k];
                double upper = f0 * Math.Pow(2, GraphicDesign.Bandwidth / 2);
                double lowest = double.MaxValue, highest = double.MinValue;
                foreach (double g in new[] { 1.0, 3, 6, 12, 18, 24, 40, -1, -3, -6, -12, -18, -24, -40 })
                {
                    Func<double, double> db = f => 10 * Math.Log10(GraphicDesign.Magnitude2(shape, g, GraphicDesign.W2(f, rate)));
                    Assert.That(db(f0), Is.EqualTo(g).Within(1e-6), $"{GraphicDesign.Labels[k]} {g:+0;−0} dB at its centre");
                    double lo = Crossing(db, f0 / 8, f0, g / 2);
                    worstLower = Math.Max(worstLower, Math.Abs(Math.Log2(lo / (f0 * Math.Pow(2, -GraphicDesign.Bandwidth / 2)))));
                    if (upper < 0.4 * rate)
                    {
                        double hi = Crossing(db, Math.Min(4 * f0, 0.4999 * rate), f0, g / 2);
                        double width = Math.Log2(hi / lo);
                        worstWidth = Math.Max(worstWidth, Math.Abs(width - GraphicDesign.Bandwidth));
                        lowest = Math.Min(lowest, width);
                        highest = Math.Max(highest, width);
                    }
                }
                if (lowest <= highest)
                {
                    bandsWithBoth++;
                    worstSpread = Math.Max(worstSpread, highest - lowest);
                    if (upper < 0.1 * rate)
                    {
                        lowBands++;
                        lowSpread = Math.Max(lowSpread, highest - lowest);
                    }
                }
            }
            Measure.Report($"Neo EQ graphic bells at {rate} Hz, ±1 … ±40 dB: the lower half-gain point within {worstLower * 1200:F4} cents of 2^(−1/3) × the centre (every band); the half-gain width varies with the gain by at most {lowSpread:F4} octave up to 0.1 fs ({lowBands} bands), {worstSpread:F4} octave up to 0.4 fs ({bandsWithBoth} bands), within {worstWidth:F4} octave of 2/3");
            Assert.That(worstLower, Is.LessThan(1e-6));
            Assert.That(lowSpread, Is.LessThan(0.005));
            Assert.That(worstSpread, Is.LessThan(0.04));
            Assert.That(worstWidth, Is.LessThan(0.04));
            Assert.That(lowBands, Is.GreaterThanOrEqualTo(22));
        }

        /// <summary>The analog constant-Q prototype at <paramref name="hz"/>, dB.</summary>
        static double PrototypeDb(double f0, double db, double hz)
        {
            double a = Math.Pow(10, db / 40), q = GraphicDesign.Q, w = hz / f0;
            double u = (1 - w * w) * (1 - w * w);
            return 10 * Math.Log10((u + a * a / (q * q) * w * w) / (u + w * w / (a * a * q * q)));
        }

        [TestCase(44100)]
        [TestCase(48000)]
        public void TheTopBandsKeepTheirShape(int rate)
        {
            // The 12.5, 16 and 20 kHz bells at ±6, ±12 and ±18 dB against the analog prototype, 20 Hz up to Nyquist:
            // within 8 % of the gain everywhere (the bilinear transform's bell, prewarped at the centre as the
            // parametric bands are, is squeezed to 0 dB at Nyquist: shown for comparison).
            var solver = new GraphicSolver(rate);
            var lines = new List<string>();
            double worstShare = 0;
            foreach (int k in new[] { 28, 29, 30 })
            {
                double f0 = GraphicDesign.Centres[k];
                double ours = 0, bilinear = 0;
                foreach (double g in new[] { 6.0, 12, 18, -6, -12, -18 })
                {
                    var bell = new BandSettings { Type = BandType.Bell, Hz = f0, GainDb = g, Q = GraphicDesign.Q };
                    double share = 0;
                    foreach (double f in LogFrequencies(20, 0.4999 * rate, 400))
                    {
                        double proto = PrototypeDb(f0, g, f);
                        double err = Math.Abs(10 * Math.Log10(GraphicDesign.Magnitude2(solver.Shapes[k], g, GraphicDesign.W2(f, rate))) - proto);
                        ours = Math.Max(ours, err);
                        share = Math.Max(share, err / Math.Abs(g));
                        bilinear = Math.Max(bilinear, Math.Abs(20 * Math.Log10(EqDesign.Magnitude(bell, f, rate)) - proto));
                    }
                    worstShare = Math.Max(worstShare, share);
                }
                lines.Add($"{GraphicDesign.Labels[k]} within {ours:F2} dB (bilinear bell: {bilinear:F1} dB)");
            }
            Measure.Report($"Neo EQ graphic top bands at {rate} Hz against the analog prototype, ±6 … ±18 dB, 20 Hz – Nyquist: {string.Join("; ", lines)}; at most {worstShare * 100:F1} % of the gain");
            Assert.That(worstShare, Is.LessThan(0.08));
        }

        // ------------------------------------------------------------------ the extremes

        [TestCase(44100)]
        [TestCase(48000)]
        public void EveryBandAtItsEndsGivesExactlyThatGain(int rate)
        {
            // Hyperion reaches +221 dBFS with every band at full boost. Here every slider at +18 dB is a gain of exactly
            // +18 dB (and −18 dB at the bottom), on pink noise and at every frequency; with Output at +24 dB as well,
            // +42 dB and no more. The zigzag ±18 dB stays within 1 dB of ±18.
            var l = Measure.PinkNoise(rate, 0.05, 81);
            var r = Measure.PinkNoise(rate, 0.05, 82);
            var lines = new List<string>();
            foreach (var (name, sliders, output, expect) in new[] { ("all +18 dB", All(18), 0.0, 18.0), ("all −18 dB", All(-18), 0.0, -18.0), ("all +18 dB, Output +24 dB", All(18), 24.0, 42.0) })
            {
                var (e, _) = Make(p => { Sliders(p, sliders); SetValue(p, P.Output, output); }, rate);
                var (yl, yr) = Run(e, l, r);
                double gain = Measure.Db(Measure.Rms(yl, rate / 2, rate / 2) / Measure.Rms(l, rate / 2, rate / 2));
                double worstSample = 0;
                double ratio = Math.Pow(10, expect / 20);
                for (int i = 0; i < yl.Length; i++) worstSample = Math.Max(worstSample, Math.Abs(yl[i] - l[i] * ratio) / ratio);
                lines.Add($"{name}: {gain:+0.0000;−0.0000} dB (each sample within {worstSample:E1} of the input × the gain)");
                Assert.That(gain, Is.EqualTo(expect).Within(0.001), name);
                Assert.That(yl.Concat(yr).All(float.IsFinite), Is.True);
            }
            var solver = new GraphicSolver(rate);
            solver.Solve(Zigzag(18));
            double peak = LogFrequencies(10, 0.49 * rate, 2000).Max(f => Math.Abs(solver.ResponseDb(f)));
            Measure.Report($"Neo EQ graphic extremes at {rate} Hz: {string.Join("; ", lines)}; the zigzag ±18 dB peaks at {peak:F2} dB anywhere up to Nyquist");
            Assert.That(peak, Is.LessThan(19));
        }

        [Test]
        public void FastAutomationStaysStableAndBounded()
        {
            // Pink noise at −20 dBFS RMS for 20 s; the sliders jump every block (32 samples, the shortest a tick gives at
            // high tempo, and 256) between random settings, the zigzags and both ends, with Mode switched now and then:
            // finite, and never more than 24 dB over the input's peak (the static settings reach +18 dB, gliding between
            // them may pass a little over).
            const int rate = Measure.Rate;
            var l = Measure.PinkNoise(20 * rate, 0.1, 91);
            var r = Measure.PinkNoise(20 * rate, 0.1, 92);
            double inPeak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
            var lines = new List<string>();
            foreach (int block in new[] { 32, 256 })
            {
                var rng = new Random(block);
                var (e, v) = Make();
                int jumps = 0;
                var (yl, yr) = Run(e, l, r, block, pos =>
                {
                    var next = (jumps++ % 6) switch { 0 => Zigzag(18), 1 => All(18), 2 => Zigzag(-18), 3 => All(-18), _ => Random(rng) };
                    Sliders(v, next);
                    if (jumps % 97 == 0) v.Set(P.Mode, 1 - v[P.Mode]);
                });
                double peak = Math.Max(Measure.Peak(yl, 0, yl.Length), Measure.Peak(yr, 0, yr.Length));
                Assert.That(yl.Concat(yr).All(float.IsFinite), Is.True, $"block {block}");
                lines.Add($"blocks of {block}: peak {Measure.Db(peak / inPeak):+0.0;−0.0} dB over the input's");
                Assert.That(Measure.Db(peak / inPeak), Is.LessThan(24), $"block {block}");
            }
            Measure.Report($"Neo EQ graphic, every slider jumping every block for 20 s of noise: {string.Join("; ", lines)}");
        }

        /// <summary>Energy above 2 kHz relative to a 220 Hz sine, dB (Hann-windowed spectrum).</summary>
        static double ZipperDb(float[] y)
        {
            int n = 1 << 16;
            var w = new float[n];
            int from = y.Length - n;
            for (int i = 0; i < n; i++) w[i] = y[from + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            var mag = Measure.Spectrum(w, 0, n);
            int k220 = (int)Math.Round(220.0 * n / Measure.Rate), k2k = (int)Math.Round(2000.0 * n / Measure.Rate);
            double sine = 0, high = 0;
            for (int k = k220 - 40; k <= k220 + 40; k++) sine += mag[k] * mag[k];
            for (int k = k2k; k < mag.Length; k++) high += mag[k] * mag[k];
            return 10 * Math.Log10(high / sine);
        }

        [Test]
        public void SliderMovesMakeNoZipperNoise()
        {
            // A 220 Hz sine; the sliders from 100 to 500 Hz move every block (256 samples) through ±12 dB (each its own
            // phase): the bands glide in straight-line segments, so nothing reaches above 2 kHz (−90 dB).
            const int n = 1 << 17;
            var x = Measure.Sine(220, n, 0.25);
            var (e, v) = Make();
            var (y, _) = Run(e, x, x, before: pos =>
            {
                int block = pos / 256;
                for (int k = 7; k <= 14; k++) SetValue(v, GraphicDesign.Slider(k), 12 * Math.Sin(2 * Math.PI * (block / 60.0 + k / 8.0)));
            });
            double zipper = ZipperDb(y);
            Measure.Report($"Neo EQ graphic slider moves (100 – 500 Hz through ±12 dB, per block, on 220 Hz): energy above 2 kHz {zipper:F1} dB re the sine");
            Assert.That(zipper, Is.LessThan(-90));
        }

        [Test]
        public void TheOutputDoesNotDependOnHowTheHostSplitsIt()
        {
            // The glides run in 16-sample segments across blocks: blocks of 256 and of 117 give the same samples, with
            // the sliders and the mode moving at times both split at (multiples of 256 × 117).
            var x = Measure.PinkNoise(4 * 29952, 0.2, 5);
            var rng = new Random(3);
            var settings = Enumerable.Range(0, 4).Select(_ => Random(rng)).ToArray();
            (float[], float[]) Render(int block)
            {
                var (e, v) = Make(p => Sliders(p, settings[0]));
                return Run(e, x, x, block, pos =>
                {
                    if (pos % 29952 != 0 || pos == 0) return;
                    int i = pos / 29952;
                    Sliders(v, settings[i]);
                    if (i == 2) v.Set(P.Mode, 0);
                    if (i == 3) v.Set(P.Mode, 1);
                });
            }
            var (a, ar) = Render(256);
            var (b, br) = Render(117);
            Assert.That(b, Is.EqualTo(a));
            Assert.That(br, Is.EqualTo(ar));
        }

        [Test]
        public void SilenceAfterNoiseLeavesNoSubnormals()
        {
            // 2 s of noise through a random setting (some bands ringing at 40 dB and more), then 20 s of silence: no
            // subnormal number comes out (the sections are flushed, tails below 1e-30 are 0) and silence costs no more
            // than noise.
            const int rate = Measure.Rate;
            var (e, _) = Make(p => Sliders(p, Zigzag(18)));
            var noise = Measure.PinkNoise(2 * rate, 0.3, 9);
            var silent = new float[20 * rate];
            var sw = Stopwatch.StartNew();
            Run(e, noise, noise);
            double noiseSeconds = sw.Elapsed.TotalSeconds / 2;
            sw.Restart();
            var (l, r) = Run(e, silent, silent);
            double silenceSeconds = sw.Elapsed.TotalSeconds / 20;
            Assert.That(l.Concat(r).Any(float.IsSubnormal), Is.False, "no subnormal output");
            Assert.That(l.Concat(r).All(float.IsFinite), Is.True);
            Assert.That(l[^1], Is.EqualTo(0f));
            Measure.Report($"Neo EQ graphic (zigzag ±18 dB): silence after noise costs {silenceSeconds / noiseSeconds:F2}× noise; no subnormals; silent at the end");
            Assert.That(silenceSeconds, Is.LessThan(3 * noiseSeconds + 0.002));
        }

        [Test]
        public void ProcessingAllocatesNothing()
        {
            // Every block a slider moves (the solver runs) and every 50th the mode switches: no allocation.
            var (e, v) = Make();
            var l = new float[EqEngine.MaxBlock];
            var r = new float[EqEngine.MaxBlock];
            var rng = new Random(1);
            var settings = Enumerable.Range(0, 8).Select(_ => Random(rng)).ToArray();
            void Block(int b)
            {
                for (int i = 0; i < l.Length; i++) l[i] = r[i] = (float)Math.Sin(i * 0.05 + b);
                var s = settings[b % settings.Length];
                for (int k = 0; k < N; k++) v.Set(GraphicDesign.Slider(k), Params.Table[GraphicDesign.Slider(k)].Raw(s[k]));
                if (b % 50 == 49) v.Set(P.Mode, 1 - v[P.Mode]);
                e.Process(l, r, l.Length);
            }
            for (int b = 0; b < 200; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, Block);
            Measure.Report($"Neo EQ graphic: {bytes} bytes allocated over 2000 blocks with the sliders moving every block and the mode switching");
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        // ------------------------------------------------------------------ switching and passing

        [Test]
        public void ModeSwitchesFadeInsteadOfClicking()
        {
            // A 100 Hz sine, graphic mode with +12 dB at 100 Hz, parametric with a −12 dB bell there: switching the mode
            // crossfades over 5 ms, with no step larger than the boosted sine's own.
            var x = Measure.Sine(100, 48000, 0.25);
            var (e, v) = Make(p =>
            {
                SetValue(p, GraphicDesign.Slider(7), 12);
                EqTests.Band(p, 2, BandType.Bell, 100, -12, 1);
            });
            var (y, _) = Run(e, x, x, before: pos =>
            {
                if (pos == 12032) v.Set(P.Mode, 0);
                if (pos == 24064) v.Set(P.Mode, 1);
                if (pos == 36096) v.Set(P.Mode, 0);
            });
            double biggest = MaxStep(y, 4800);
            double sineStep = 2 * Math.PI * 100 / 48000 * DspMath.DbToGain(12) * 0.25;
            Measure.Report($"Neo EQ mode switches (+12 dB graphic, −12 dB parametric, at 100 Hz): largest step {biggest:F4} (the boosted sine's own: {sineStep:F4})");
            Assert.That(biggest, Is.LessThan(1.2 * sineStep));
            Assert.That(Measure.Db(Measure.Rms(y, 8000, 4000) / Measure.Rms(x, 8000, 4000)), Is.EqualTo(12).Within(0.1), "graphic");
            Assert.That(Measure.Db(Measure.Rms(y, 20000, 4000) / Measure.Rms(x, 20000, 4000)), Is.EqualTo(-12).Within(0.1), "parametric");
            Assert.That(Measure.Db(Measure.Rms(y, 32000, 4000) / Measure.Rms(x, 32000, 4000)), Is.EqualTo(12).Within(0.1), "graphic again");
        }

        [Test]
        public void FlatGraphicPassesTheInputBitForBit()
        {
            // Graphic mode with every slider at 0 dB: the input bit for bit (negative zero and subnormals included), also
            // after a slider has moved away and back.
            var x = Measure.PinkNoise(48000, 0.3, 4);
            x[10] = -0f;
            x[11] = float.Epsilon;
            x[12] = -float.Epsilon;
            x[40000] = -0f;
            var (e, v) = Make();
            var (y, _) = Run(e, x, x, before: pos =>
            {
                if (pos == 2560) SetValue(v, GraphicDesign.Slider(12), 6);
                if (pos == 5120) SetValue(v, GraphicDesign.Slider(12), 0);
            });
            for (int i = 0; i < 2560; i++) Assert.That(BitConverter.SingleToInt32Bits(y[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(x[i])), $"sample {i}");
            for (int i = 24000; i < x.Length; i++) Assert.That(BitConverter.SingleToInt32Bits(y[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(x[i])), $"sample {i}");
            Assert.That(e.Latency, Is.EqualTo(0));
        }

        [Test]
        public void CpuIsSmall()
        {
            // Stereo at 48 kHz, every band busy (a random setting): ≥ 30× real time, 3.3 % of a core (31 sections; the
            // parametric test's 50× is for 14), timed against the reference workload in the same run (Throughput.cs): 1 s
            // played untimed, then 2 s. And the solve a slider move costs, the best of three passes: other work on a shared
            // runner only ever adds time.
            const int rate = Measure.Rate;
            var l = Measure.PinkNoise(3 * rate, 0.2, 31);
            var r = Measure.PinkNoise(3 * rate, 0.2, 32);
            Throughput.Guard("Neo EQ graphic CPU (31 bands, stereo)", Throughput.Floor(x64: 97, arm64: 42), 2, rate, EqEngine.MaxBlock, () =>
            {
                var (e, _) = Make(p => Sliders(p, Random(new Random(11))));
                var bl = new float[EqEngine.MaxBlock];
                var br = new float[EqEngine.MaxBlock];
                void Block(int pos, int n)
                {
                    Array.Copy(l, pos, bl, 0, n);
                    Array.Copy(r, pos, br, 0, n);
                    e.Process(bl, br, n);
                }
                for (int pos = 0; pos < rate; pos += EqEngine.MaxBlock) Block(pos, EqEngine.MaxBlock);
                return Throughput.InBlocks(EqEngine.MaxBlock, (pos, n) => Block(rate + pos, n));
            }, Measure.Report);
            var solver = new GraphicSolver(rate);
            var rng = new Random(12);
            var settings = Enumerable.Range(0, 50).Select(_ => Random(rng)).ToArray();
            foreach (var s in settings) solver.Solve(s);
            GC.Collect();
            double solveMs = double.MaxValue;
            for (int pass = 0; pass < 3; pass++)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++) solver.Solve(settings[i % settings.Length]);
                solveMs = Math.Min(solveMs, sw.Elapsed.TotalMilliseconds / 200);
            }
            Measure.Report($"Neo EQ graphic: a slider move's solve {solveMs:F3} ms ({solveMs / (256000.0 / rate) * 100:F1} % of a 256-sample block)");
            Assert.That(solveMs, Is.LessThan(2));
        }
    }
}
