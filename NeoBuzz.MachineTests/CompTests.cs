using NeoBuzz.Comp;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Comp (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class CompTests
    {
        static (CompEngine Engine, ParamValues Values) Make(Action<ParamValues> setup)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Knee, 0);
            v.Set(P.Ratio, Params.Table[P.Ratio].Raw(4));
            v.Set(P.Threshold, Params.Table[P.Threshold].Raw(-20));
            setup?.Invoke(v);
            var e = new CompEngine(v);
            e.SetSampleRate(Measure.Rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (float[] L, float[] R) Run(CompEngine e, float[] l, float[] r, float[] keyL = null, float[] keyR = null)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[CompEngine.MaxBlock];
            var br = new float[CompEngine.MaxBlock];
            var kl = keyL != null ? new float[CompEngine.MaxBlock] : null;
            var kr = keyR != null ? new float[CompEngine.MaxBlock] : null;
            for (int pos = 0; pos < l.Length; pos += CompEngine.MaxBlock)
            {
                int n = Math.Min(CompEngine.MaxBlock, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                if (kl != null) Array.Copy(keyL, pos, kl, 0, n);
                if (kr != null) Array.Copy(keyR, pos, kr, 0, n);
                e.Process(bl, br, kl, kr, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        static float[] Constant(double db, int n)
        {
            var x = new float[n];
            float a = (float)DspMath.DbToGain(db);
            for (int i = 0; i < n; i++) x[i] = a;
            return x;
        }

        static double Ideal(double inDb, double threshold, double ratio) =>
            inDb <= threshold ? inDb : threshold + (inDb - threshold) / ratio;

        [TestCase((int)CompMode.Clean)]
        [TestCase((int)CompMode.Fet)]
        [TestCase((int)CompMode.Glue)]
        public void StaticCurveMatchesTheIdeal(int modeIndex)
        {
            double worst = 0;
            for (double inDb = -60; inDb <= 0; inDb += 6)
            {
                var (e, _) = Make(v =>
                {
                    v.Set(P.Mode, modeIndex);
                    Set(v, P.Attack, 0.0005);
                    Set(v, P.Release, 1.0);
                });
                // A steady level: DC for the peak detectors, a 1 kHz sine for the RMS one (Glue).
                float[] x = (CompMode)modeIndex == CompMode.Glue
                    ? Measure.Sine(1000, 48000, DspMath.DbToGain(inDb))
                    : Constant(inDb, 48000);
                var (y, _) = Run(e, x, x);
                double outDb = (CompMode)modeIndex == CompMode.Glue
                    ? Measure.Db(Measure.Peak(y, 40000, 4800))
                    : Measure.Db(Math.Abs(y[47999]));
                double err = outDb - Ideal(inDb, -20, 4);
                worst = Math.Max(worst, Math.Abs(err));
                TestContext.Out.WriteLine($"{(CompMode)modeIndex} in {inDb} dB → {outDb:F2} dB (ideal {Ideal(inDb, -20, 4):F2})");
            }
            Measure.Report($"Neo Comp {(CompMode)modeIndex} static curve: worst error {worst:F3} dB");
            Assert.That(worst, Is.LessThan(0.25));
        }

        [TestCase(0.001)]
        [TestCase(0.01)]
        [TestCase(0.1)]
        public void AttackReaches63PercentInItsTime(double attack)
        {
            var (e, _) = Make(v =>
            {
                Set(v, P.Attack, attack);
                Set(v, P.Release, 1.0);
            });
            int n = (int)(attack * 10 * Measure.Rate) + 4800;
            var x = Constant(-40, n);
            for (int i = 2400; i < n; i++) x[i] = (float)DspMath.DbToGain(-10);
            var (y, _) = Run(e, x, x);
            double full = 7.5;   // (−10 − (−20)) · (1 − 1/4)
            int hit = -1;
            for (int i = 2400; i < n; i++)
            {
                double gr = -Measure.Db(y[i] / x[i]);
                if (gr >= 0.632 * full) { hit = i - 2400; break; }
            }
            double t = hit / (double)Measure.Rate;
            Measure.Report($"Neo Comp attack {attack * 1000} ms: 63 % after {t * 1000:F3} ms");
            Assert.That(t / attack, Is.InRange(0.9, 1.1));
        }

        [TestCase(0.05)]
        [TestCase(0.3)]
        [TestCase(2.0)]
        public void ReleaseRecovers63PercentInItsTime(double release)
        {
            var (e, _) = Make(v =>
            {
                Set(v, P.Attack, 0.0005);
                Set(v, P.Release, release);
            });
            int n = 4800 + (int)(release * 5 * Measure.Rate);
            var x = Constant(-10, n);
            for (int i = 4800; i < n; i++) x[i] = (float)DspMath.DbToGain(-40);
            var (y, _) = Run(e, x, x);
            double full = 7.5;
            int hit = -1;
            for (int i = 4800; i < n; i++)
            {
                double gr = -Measure.Db(y[i] / x[i]);
                if (gr <= 0.368 * full) { hit = i - 4800; break; }
            }
            double t = hit / (double)Measure.Rate;
            Measure.Report($"Neo Comp release {release * 1000} ms: 37 % after {t * 1000:F1} ms");
            Assert.That(t / release, Is.InRange(0.9, 1.1));
        }

        [Test]
        public void BelowThresholdIsTransparent()
        {
            const int bin = 1365;
            var (e, _) = Make(v => v.Set(P.Mode, (int)CompMode.Clean));
            var x = Measure.Sine(Measure.BinFrequency(bin), 8192 + Measure.N, DspMath.DbToGain(-22));
            var (y, _) = Run(e, x, x);
            var mag = Measure.Spectrum(y, 8192);
            double thd = Measure.Db(Measure.Thd(mag, bin));
            double gain = Measure.Db(mag[bin] / DspMath.DbToGain(-22));
            TestContext.Out.WriteLine($"THD {thd:F1} dB, gain {gain:F4} dB");
            Assert.That(thd, Is.LessThan(-100));
            Assert.That(Math.Abs(gain), Is.LessThan(0.01));
        }

        [Test]
        public void FullStereoLinkGivesBothChannelsTheSameGain()
        {
            var (e, _) = Make(v => v.Set(P.Link, 1000));
            var l = Measure.Sine(500, 24000, 0.9);
            var r = Measure.Sine(700, 24000, 0.05);
            var (yl, yr) = Run(e, l, r);
            for (int i = 0; i < l.Length; i += 37)
            {
                if (Math.Abs(l[i]) < 0.05 || Math.Abs(r[i]) < 0.005) continue;
                Assert.That(yl[i] / l[i], Is.EqualTo(yr[i] / r[i]).Within(1e-4), $"sample {i}");
            }
        }

        [Test]
        public void MixZeroIsTheInputDelayedByTheLookahead()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mix, 0);
                Set(v, P.Lookahead, 2);
                Set(v, P.Threshold, -50);
                v.Set(P.Character, 1000);
            });
            var x = Measure.PinkNoise(8192, 0.3);
            var (y, _) = Run(e, x, x);
            int L = e.Latency;
            Assert.That(L, Is.EqualTo(96));
            double worst = 0;
            for (int i = L; i < x.Length; i++) worst = Math.Max(worst, Math.Abs(y[i] - x[i - L]));
            Assert.That(worst, Is.LessThan(1e-7));
        }

        [Test]
        public void LookaheadCatchesThePeak()
        {
            var (e, _) = Make(v =>
            {
                Set(v, P.Lookahead, 5);
                v.Set(P.Ratio, 1000);   // ∞
                Set(v, P.Attack, 0.00005);
                Set(v, P.Release, 0.2);
            });
            var x = Constant(-40, 9600);
            for (int i = 4800; i < 9600; i++) x[i] = 1f;
            var (y, _) = Run(e, x, x);
            double peak = Measure.Db(Measure.Peak(y, 4800, 4800));
            TestContext.Out.WriteLine($"peak {peak:F2} dB with threshold −20");
            Assert.That(peak, Is.LessThan(-19));
        }

        [Test]
        public void ExternalSidechainDucks()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Sidechain, 1);
                Set(v, P.Attack, 0.001);
            });
            var x = Measure.Sine(220, 24000, 0.3);
            var key = new float[24000];
            for (int i = 12000; i < 24000; i++) key[i] = 0.9f;
            var (y, _) = Run(e, x, x, key, key);
            double before = Measure.Rms(y, 6000, 4800), during = Measure.Rms(y, 18000, 4800);
            TestContext.Out.WriteLine($"before {Measure.Db(before):F1} dB, ducked {Measure.Db(during):F1} dB");
            Assert.That(Measure.Db(before / Measure.Rms(x, 6000, 4800)), Is.InRange(-0.01, 0.01), "no key, no reduction");
            Assert.That(Measure.Db(during / before), Is.LessThan(-10));
        }

        [Test]
        public void OptoReleasesInTwoStages()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, (int)CompMode.Opto);
                Set(v, P.Release, 2.0);
                Set(v, P.Attack, 0.01);
            });
            var x = Constant(-5, 48000 * 3);
            for (int i = 48000; i < x.Length; i++) x[i] = (float)DspMath.DbToGain(-50);
            var (y, _) = Run(e, x, x);
            double gr0 = -Measure.Db(y[47999] / x[47999]);
            double gr100ms = -Measure.Db(y[48000 + 4800] / x[48000 + 4800]);
            double gr1s = -Measure.Db(y[48000 + 48000] / x[48000 + 48000]);
            TestContext.Out.WriteLine($"GR {gr0:F1} dB → {gr100ms:F1} dB after 100 ms → {gr1s:F1} dB after 1 s");
            Assert.That(gr100ms, Is.LessThan(0.65 * gr0), "fast first stage");
            Assert.That(gr1s, Is.GreaterThan(0.1 * gr0), "slow second stage");
        }

        // ------------------------------------------------------------------ the shared dynamics

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

        /// <summary>
        /// Neo Comp's renders before its gain computer and detector moved into the shared library (NeoBuzz.Dsp
        /// Dynamics.cs, for Neo Multiband): each mode with its defaults and a hard knee, auto release, lookahead,
        /// the external sidechain, ∞:1 and auto makeup, every factory preset, and threshold and ratio moved every
        /// block, on pink noise (other noise in each channel and at input 2) at 44.1 and 48 kHz. Pinned at
        /// origin/main 99471d4; the portable maths makes them the same on every platform.
        /// </summary>
        internal static IEnumerable<(string Name, Action<ParamValues> Setup, Action<ParamValues, int> Block, bool Key)> PinnedCases()
        {
            foreach (CompMode mode in Enum.GetValues<CompMode>())
            {
                var m = mode;
                yield return ($"{m} defaults", v => v.Set(P.Mode, (int)m), null, false);
                yield return ($"{m} hard knee −30 dB 6:1", v => { v.Set(P.Mode, (int)m); v.Set(P.Knee, 0); Set(v, P.Threshold, -30); Set(v, P.Ratio, 6); }, null, false);
                yield return ($"{m} auto release, character", v => { v.Set(P.Mode, (int)m); v.Set(P.AutoRelease, 1); v.Set(P.Character, 700); Set(v, P.Threshold, -32); }, null, false);
                yield return ($"{m} lookahead 3 ms, ∞:1, auto makeup, link 40 %", v => { v.Set(P.Mode, (int)m); Set(v, P.Lookahead, 3); v.Set(P.Ratio, 1000); v.Set(P.AutoMakeup, 1); v.Set(P.Link, 400); }, null, false);
                yield return ($"{m} external sidechain, SC low cut", v => { v.Set(P.Mode, (int)m); v.Set(P.Sidechain, 1); Set(v, P.ScLowCut, 120); Set(v, P.Threshold, -36); }, null, true);
                yield return ($"{m} moves", v => v.Set(P.Mode, (int)m), (v, pos) =>
                {
                    int block = pos / CompEngine.MaxBlock;
                    v.Set(P.Threshold, 200 + (block * 37) % 700);
                    v.Set(P.Ratio, (block * 53) % 1001);
                    v.Set(P.Knee, (block * 11) % 600);
                    v.Set(P.Makeup, (block * 7) % 300);
                }, false);
            }
            foreach (var (_, machine, name, values) in PresetTests.AllPresets())
            {
                if (machine != "Neo Comp") continue;
                var vals = values;
                yield return ("preset " + name, v => { foreach (var kv in vals) if (kv.Key >= 0) v.Set(kv.Key, kv.Value); }, null, vals.GetValueOrDefault(P.Sidechain) == 1);
            }
        }

        static readonly Dictionary<string, ulong> Pinned = new()
        {
            ["44100 Clean defaults"] = 0xD772D5AA8494BFF4UL,
            ["44100 Clean hard knee −30 dB 6:1"] = 0x60E89E40BE9AC2ABUL,
            ["44100 Clean auto release, character"] = 0x072CD25C4030200DUL,
            ["44100 Clean lookahead 3 ms, ∞:1, auto makeup, link 40 %"] = 0x58D84CD17197F6E3UL,
            ["44100 Clean external sidechain, SC low cut"] = 0x09405EA725E9359BUL,
            ["44100 Clean moves"] = 0xB434E05081925E59UL,
            ["44100 Glue defaults"] = 0x5BCB2A91B97B58EBUL,
            ["44100 Glue hard knee −30 dB 6:1"] = 0xBAB145482EBC72D6UL,
            ["44100 Glue auto release, character"] = 0x92D9F4F2D3DB9C05UL,
            ["44100 Glue lookahead 3 ms, ∞:1, auto makeup, link 40 %"] = 0x950ACA70D8E87655UL,
            ["44100 Glue external sidechain, SC low cut"] = 0x097D46A13D9D4CCFUL,
            ["44100 Glue moves"] = 0xB7F9C5062B346C39UL,
            ["44100 Fet defaults"] = 0xD672E9E82BE877E6UL,
            ["44100 Fet hard knee −30 dB 6:1"] = 0xFA2E3C99980960B6UL,
            ["44100 Fet auto release, character"] = 0x1CA934C3163E56C8UL,
            ["44100 Fet lookahead 3 ms, ∞:1, auto makeup, link 40 %"] = 0x6619812E7C095BBAUL,
            ["44100 Fet external sidechain, SC low cut"] = 0x09405EA725E9359BUL,
            ["44100 Fet moves"] = 0xC7D3984C4C80FEEBUL,
            ["44100 Opto defaults"] = 0xE03A119E91867CB2UL,
            ["44100 Opto hard knee −30 dB 6:1"] = 0x7202509A577A3D59UL,
            ["44100 Opto auto release, character"] = 0x58224DE664CE3DB0UL,
            ["44100 Opto lookahead 3 ms, ∞:1, auto makeup, link 40 %"] = 0x1D6A753204313575UL,
            ["44100 Opto external sidechain, SC low cut"] = 0x283C1EC520DF75BEUL,
            ["44100 Opto moves"] = 0x1E44278C17EF1A15UL,
            ["44100 preset Clean — Gentle Leveler"] = 0xC79433C6F1343FF1UL,
            ["44100 preset Clean — Vocal Control"] = 0x88AC9C310347939CUL,
            ["44100 preset Clean — Brickwall"] = 0x55B73D95F6C24586UL,
            ["44100 preset Glue — Mix Bus"] = 0x54E68C453564F2C1UL,
            ["44100 preset Glue — Drum Bus"] = 0x7CAE750675265200UL,
            ["44100 preset FET — Vocal Grab"] = 0x9683EE59CDB90D5DUL,
            ["44100 preset FET — All Buttons"] = 0x4817DAADBC2E2891UL,
            ["44100 preset FET — Bass Tamer"] = 0x2D89ED3E44A8F510UL,
            ["44100 preset Opto — Smooth Vocal"] = 0x17E848067B8E8775UL,
            ["44100 preset Opto — Bass Leveler"] = 0xD0B66D97B5D20B6EUL,
            ["44100 preset Parallel — New York Drums"] = 0xEE69683054183D20UL,
            ["44100 preset Sidechain — Kick Duck"] = 0xC97EDFD2E101F678UL,
            ["44100 preset Sidechain — Pump"] = 0xB6F8F78232428763UL,
            ["44100 preset Mastering — Transparent"] = 0xB1F07D6AC202355BUL,
            ["48000 Clean defaults"] = 0xD7F75B7D8DD55B8AUL,
            ["48000 Clean hard knee −30 dB 6:1"] = 0x9C2FCACA032A4A24UL,
            ["48000 Clean auto release, character"] = 0x98A6B0747A4CAAFAUL,
            ["48000 Clean lookahead 3 ms, ∞:1, auto makeup, link 40 %"] = 0x9EF30F9CF2E63B8BUL,
            ["48000 Clean external sidechain, SC low cut"] = 0x14E0AB32635E4D9DUL,
            ["48000 Clean moves"] = 0xA2515A298E67CBFDUL,
            ["48000 Glue defaults"] = 0x3EB094FA8ED1C856UL,
            ["48000 Glue hard knee −30 dB 6:1"] = 0xDFD2D57206A82DE8UL,
            ["48000 Glue auto release, character"] = 0x779CF9A656B3699DUL,
            ["48000 Glue lookahead 3 ms, ∞:1, auto makeup, link 40 %"] = 0x01C5DE708C417E4AUL,
            ["48000 Glue external sidechain, SC low cut"] = 0xB40F41E6A8C4E24EUL,
            ["48000 Glue moves"] = 0x340E8B017A09FEB4UL,
            ["48000 Fet defaults"] = 0x43AE0286E5244C4BUL,
            ["48000 Fet hard knee −30 dB 6:1"] = 0xB121DA5AC1B6F657UL,
            ["48000 Fet auto release, character"] = 0xF3460CA35FA4A2B6UL,
            ["48000 Fet lookahead 3 ms, ∞:1, auto makeup, link 40 %"] = 0x622BFF1C9291DC8AUL,
            ["48000 Fet external sidechain, SC low cut"] = 0x14E0AB32635E4D9DUL,
            ["48000 Fet moves"] = 0xB1260B023889EBA1UL,
            ["48000 Opto defaults"] = 0x9A4043E744C34F33UL,
            ["48000 Opto hard knee −30 dB 6:1"] = 0xD810C6648EE1A37FUL,
            ["48000 Opto auto release, character"] = 0xB69695FB6E63A9AFUL,
            ["48000 Opto lookahead 3 ms, ∞:1, auto makeup, link 40 %"] = 0xE96CCAB7D2EF42A6UL,
            ["48000 Opto external sidechain, SC low cut"] = 0x5CF62276F3B64B0BUL,
            ["48000 Opto moves"] = 0xC1EF5EB1CD76A5A5UL,
            ["48000 preset Clean — Gentle Leveler"] = 0x1955500230FBC85FUL,
            ["48000 preset Clean — Vocal Control"] = 0x6AE0391EBAC18918UL,
            ["48000 preset Clean — Brickwall"] = 0xA0A093310E282F89UL,
            ["48000 preset Glue — Mix Bus"] = 0xEE63EA3E46A55D65UL,
            ["48000 preset Glue — Drum Bus"] = 0x8E175AD2A82FB540UL,
            ["48000 preset FET — Vocal Grab"] = 0x0B3DD20D078E3199UL,
            ["48000 preset FET — All Buttons"] = 0x34242FF042903214UL,
            ["48000 preset FET — Bass Tamer"] = 0x1416EC99E8A39A1DUL,
            ["48000 preset Opto — Smooth Vocal"] = 0xEF76EAFC962A1BFEUL,
            ["48000 preset Opto — Bass Leveler"] = 0x1379104BFA314CB2UL,
            ["48000 preset Parallel — New York Drums"] = 0x1D054A0FF65EAEBEUL,
            ["48000 preset Sidechain — Kick Duck"] = 0xF3251E791114DF23UL,
            ["48000 preset Sidechain — Pump"] = 0x7EC94DEEF66DA0DCUL,
            ["48000 preset Mastering — Transparent"] = 0x45F623DCABC3D2EBUL,
        };

        [Test]
        public void SharedDynamicsLeaveNeoCompBitIdentical()
        {
            var changed = new List<string>();
            var lines = new List<string>();
            int count = 0;
            foreach (int rate in new[] { 44100, 48000 })
                foreach (var (name, setup, block, key) in PinnedCases())
                {
                    var v = new ParamValues(Params.Table);
                    setup(v);
                    var e = new CompEngine(v);
                    e.SetSampleRate(rate);
                    var l = Measure.PinkNoise(rate / 2, 0.25, 71);
                    var r = Measure.PinkNoise(rate / 2, 0.2, 72);
                    var k = Measure.PinkNoise(rate / 2, 0.4, 73);
                    var ol = new float[l.Length];
                    var or = new float[r.Length];
                    var bl = new float[CompEngine.MaxBlock];
                    var br = new float[CompEngine.MaxBlock];
                    var kl = new float[CompEngine.MaxBlock];
                    for (int pos = 0; pos < l.Length; pos += CompEngine.MaxBlock)
                    {
                        int n = Math.Min(CompEngine.MaxBlock, l.Length - pos);
                        block?.Invoke(v, pos);
                        Array.Copy(l, pos, bl, 0, n);
                        Array.Copy(r, pos, br, 0, n);
                        Array.Copy(k, pos, kl, 0, n);
                        e.Process(bl, br, key ? kl : null, key ? kl : null, n);
                        Array.Copy(bl, 0, ol, pos, n);
                        Array.Copy(br, 0, or, pos, n);
                    }
                    string id = $"{rate} {name}";
                    ulong h = Hash(ol, or);
                    lines.Add($"            [\"{id}\"] = 0x{h:X16}UL,");
                    if (!Pinned.TryGetValue(id, out ulong before) || before != h) changed.Add(id);
                    count++;
                }
            TestContext.Out.WriteLine(string.Join("\n", lines));
            Measure.Report($"Neo Comp on the shared dynamics (NeoBuzz.Dsp Dynamics.cs): {count - changed.Count} of {count} renders bit-identical to before (each mode's defaults, hard knee, auto release, lookahead with ∞:1 and auto makeup, external sidechain, automation; the factory presets; 44.1 and 48 kHz)");
            Assert.That(count, Is.EqualTo(Pinned.Count));
            Assert.That(changed, Is.Empty);
        }

        [Test]
        public void ExtremeSettingsStayBoundedAndFinite()
        {
            var rng = new Random(5);
            var (e, v) = Make(null);
            var x = Measure.PinkNoise(24000, 0.7);
            for (int round = 0; round < 30; round++)
            {
                foreach (var d in Params.Table) v.Set(d.Index, rng.Next(3) switch { 0 => d.Min, 1 => d.Max, _ => rng.Next(d.Min, d.Max + 1) });
                var (y, _) = Run(e, x, x, x, x);
                foreach (var s in y)
                {
                    Assert.That(float.IsFinite(s), Is.True);
                    // Makeup, auto makeup and output can add 72 dB to quiet settings: bounded, not small.
                    Assert.That(Math.Abs(s), Is.LessThan(5000));
                }
            }
        }
    }
}
