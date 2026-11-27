using NeoBuzz.Dsp;
using NeoBuzz.Vocoder;
using static NeoBuzz.MachineTests.VocoderRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>The Neo Vocoder gates of docs/specs/neo-vocoder.md §7: bands, envelope match, formant, followers, notes, sidechain, Mix and Amount, the audio thread and the CPU.</summary>
    [TestFixture]
    public class NeoVocoderTests
    {
        static double Profile(double f) => -3 * Math.Log2(Math.Max(f, 20) / 700) + 6 * Math.Exp(-Math.Pow(Math.Log(f / 700), 2) / (2 * 0.8 * 0.8)) - 6 * Math.Exp(-Math.Pow(Math.Log(f / 3000), 2) / (2 * 0.8 * 0.8)) + 4 * Math.Exp(-Math.Pow(Math.Log(f / 8000), 2) / (2 * 0.7 * 0.7));

        static ParamValues Setup(string carrier, int bands, Action<ParamValues> more = null) => Fresh(p =>
        {
            Choice(p, P.Carrier, carrier); Phys(p, P.Bands, bands); Phys(p, P.Sibilance, 0); Phys(p, P.Formant, 0);
            Phys(p, P.Attack, 30); Phys(p, P.Release, 30); Phys(p, P.Low, 100); Phys(p, P.High, 16000); Phys(p, P.Width, 100);
            more?.Invoke(p);
        });

        // ================================================================================== bands

        [TestCase(8)]
        [TestCase(16)]
        [TestCase(40)]
        public void BandCentresAreLogSpacedFromLowToHighAndEachFilterPeaksAtItsCentre(int bands)
        {
            var bank = new VocoderBank(48000) { Bands = bands, LowHz = 80, HighHz = 12000, WidthFraction = 1, ShiftSemitones = 0 };
            bank.Settle();
            Assert.That(bank.BuiltBands, Is.EqualTo(bands));
            Assert.That(bank.Centre(0), Is.EqualTo(80).Within(0.8));
            Assert.That(bank.Centre(bands - 1), Is.EqualTo(12000).Within(120));
            double ratio = bank.Centre(1) / bank.Centre(0);
            for (int i = 1; i < bands; i++) Assert.That(bank.Centre(i) / bank.Centre(i - 1), Is.EqualTo(ratio).Within(ratio * 0.001));
            for (int i = 0; i < bands; i++)
            {
                double best = 0, at = 0;
                for (double f = bank.Centre(i) / 1.3; f < bank.Centre(i) * 1.3; f *= 1.0005)
                {
                    double m = bank.ModulatorResponse(i, f);
                    if (m > best) { best = m; at = f; }
                }
                Assert.That(at, Is.EqualTo(bank.Centre(i)).Within(bank.Centre(i) * 0.01), $"band {i}");
            }
        }

        // The output follows the modulator's spectrum: white-noise carrier (and a sidechain noise carrier), a flat and a shaped noise modulator, level per band within +-1.5 dB, Low to High.
        [TestCase(16, "Noise", true)] [TestCase(16, "Noise", false)] [TestCase(16, "Sidechain", false)]
        [TestCase(40, "Noise", true)] [TestCase(40, "Noise", false)] [TestCase(40, "Sidechain", false)]
        public void AWhiteCarrierFollowsTheModulatorsSpectrumWithinOneAndAHalfDbPerBand(int bands, string carrier, bool flat)
        {
            int len = Rate * 24;
            var mod = flat ? White(len, 0.1, 5) : ShapedNoise(len, Profile, 0.1);
            var e = Engine(Setup(carrier, bands));
            float[] car = carrier == "Sidechain" ? White(len, 0.25, 99) : null;
            var (ol, _) = Render(e, mod, (float[])mod.Clone(), car, car);
            const int nfft = 32768;
            var pm = Power(mod, Rate, len - Rate, nfft); var po = Power(ol, Rate, len - Rate, nfft);
            double spacing = Math.Pow(e.Bank.Centre(bands - 1) / e.Bank.Centre(0), 1.0 / (bands - 1)), half = Math.Pow(spacing, 0.3);
            var worst = 0.0;
            for (int b = 0; b < bands; b++)
            {
                double c = e.Bank.Centre(b);
                double d = Db(BandPower(po, c / half, c * half, nfft)) - Db(BandPower(pm, c / half, c * half, nfft));
                TestContext.Out.WriteLine($"{bands} bands {carrier} {(flat ? "flat" : "shaped")}: band {b} {c:0} Hz {d:+0.00;-0.00} dB");
                worst = Math.Max(worst, Math.Abs(d));
            }
            TestContext.Out.WriteLine($"worst {worst:0.00} dB");
            Assert.That(worst, Is.LessThanOrEqualTo(1.5));
        }

        // ================================================================================== envelope match

        [Test]
        public void AVowelOnASawCarrierKeepsTheVowelsBandEnvelope()
        {
            int len = Rate * 6;
            var mod = Vowel(len, 140, new[] { 700.0, 1150, 2600 }, new[] { 120.0, 150, 250 });
            var car = SawPad(len, new[] { 110.0, 164.81, 220.0 }, 0.2);
            var e = Engine(Setup("Sidechain", 16, p => { Phys(p, P.Attack, 5); Phys(p, P.Release, 20); Phys(p, P.Low, 50); }));
            var (ol, _) = Render(e, mod, (float[])mod.Clone(), car, car);
            const int nfft = 16384;
            var pm = Power(mod, Rate, len - Rate, nfft); var po = Power(ol, Rate, len - Rate, nfft);
            var a = new double[16]; var b = new double[16];
            for (int i = 0; i < 16; i++)
            {
                double lo = i == 0 ? 0 : Math.Sqrt(e.Bank.Centre(i - 1) * e.Bank.Centre(i)), hi = i == 15 ? 24000 : Math.Sqrt(e.Bank.Centre(i) * e.Bank.Centre(i + 1));
                a[i] = Db(BandPower(pm, lo, hi, nfft)); b[i] = Db(BandPower(po, lo, hi, nfft));
            }
            double r = Pearson(a, b);
            TestContext.Out.WriteLine($"correlation of the 16 band levels (dB): {r:0.000}");
            Assert.That(r, Is.GreaterThanOrEqualTo(0.9));
        }

        // ================================================================================== formant

        [Test]
        public void PlusTwelveSemitonesMovesTheSpectralPeaksOneOctaveUp()
        {
            int len = Rate * 10;
            var mod = ShapedNoise(len, f => 40 * Math.Exp(-Math.Pow(Math.Log2(Math.Max(f, 20) / 700.0), 2) / (2 * 0.2 * 0.2)), 0.2, 3);
            double[] centroid = new double[2];
            for (int k = 0; k < 2; k++)
            {
                var e = Engine(Setup("Noise", 40, p => { Phys(p, P.Formant, k == 0 ? 0 : 12); Phys(p, P.Low, 100); Phys(p, P.High, 16000); }));
                var (ol, _) = Render(e, mod, (float[])mod.Clone());
                const int nfft = 16384;
                var po = Power(ol, Rate, len - Rate, nfft);
                double sw = 0, sf = 0, lo = k == 0 ? 350 : 700, hi = k == 0 ? 1400 : 2800;
                for (int i = 1; i < po.Length; i++)
                {
                    double f = (double)i * Rate / nfft;
                    if (f < lo || f > hi) continue;
                    sw += po[i]; sf += po[i] * Math.Log(f);
                }
                centroid[k] = Math.Exp(sf / sw);
            }
            double ratio = centroid[1] / centroid[0];
            TestContext.Out.WriteLine($"peak at {centroid[0]:0} Hz, shifted {centroid[1]:0} Hz, ratio {ratio:0.000}");
            Assert.That(ratio, Is.EqualTo(2.0).Within(2.0 * 0.03));
        }

        // ================================================================================== followers

        [TestCase(1.0)] [TestCase(10.0)] [TestCase(50.0)]
        public void AFollowersAttackAndReleaseTimesAreTenToNinetyPercentWithinTenPercent(double ms)
        {
            var f = new LevelFollower();
            f.Set(ms, ms * 3, Rate);
            double up = 0, level = 0;
            int t10 = -1, t90 = -1;
            for (int i = 1; i < Rate; i++)
            {
                level = f.Next(1f);
                if (t10 < 0 && level >= 0.1) t10 = i;
                if (t90 < 0 && level >= 0.9) { t90 = i; break; }
            }
            up = (t90 - t10) * 1000.0 / Rate;
            int d10 = -1, d90 = -1;
            for (int i = 1; i < Rate; i++)
            {
                level = f.Next(0f);
                if (d90 < 0 && level <= 0.9) d90 = i;
                if (d10 < 0 && level <= 0.1) { d10 = i; break; }
            }
            double down = (d10 - d90) * 1000.0 / Rate;
            TestContext.Out.WriteLine($"attack {ms} ms measured {up:0.00}; release {ms * 3} ms measured {down:0.00}");
            Assert.That(up, Is.EqualTo(ms).Within(ms * 0.1));
            Assert.That(down, Is.EqualTo(ms * 3).Within(ms * 3 * 0.1));
        }

        [TestCase(20.0, 60.0)] [TestCase(40.0, 120.0)]
        public void ABandAboveTwoKilohertzFollowsAToneBurstWithTheSetAttackAndRelease(double attack, double release)
        {
            int len = Rate * 2, on = Rate / 2, off = Rate;
            var tone = Sine(len, 3000, 0.4);
            for (int i = 0; i < len; i++) if (i < on || i >= off) tone[i] = 0;
            var e = Engine(Setup("Noise", 16, p => { Phys(p, P.Attack, attack); Phys(p, P.Release, release); Phys(p, P.High, 8000); }));
            int band = 0; double bestD = 1e9;
            for (int b = 0; b < 16; b++) { double d = Math.Abs(Math.Log(e.Bank.Centre(b) / 3000)); if (d < bestD) { bestD = d; band = b; } }
            var trace = new List<float>();
            Render(e, tone, (float[])tone.Clone(), block: 16, atBlock: pos => trace.Add(e.Bank.BandLevels[band]));
            double peak = trace.Max();
            double at = Index(trace, peak * 0.1, on / 16, true), at9 = Index(trace, peak * 0.9, on / 16, true);
            double rt9 = Index(trace, peak * 0.9, off / 16, false), rt1 = Index(trace, peak * 0.1, off / 16, false);
            double up = (at9 - at) * 16 * 1000.0 / Rate, down = (rt1 - rt9) * 16 * 1000.0 / Rate;
            TestContext.Out.WriteLine($"band {band} at {e.Bank.Centre(band):0} Hz: attack {attack} ms measured {up:0.0}, release {release} ms measured {down:0.0}");
            Assert.That(up, Is.EqualTo(attack).Within(attack * 0.1));
            Assert.That(down, Is.EqualTo(release).Within(release * 0.1));
        }

        static double Index(List<float> x, double level, int from, bool rising)
        {
            for (int i = from; i < x.Count - 1; i++)
                if (rising ? x[i] >= level : x[i] <= level)
                    return i - 1 + (level - x[i - 1]) / (x[i] - x[i - 1]);
            return double.NaN;
        }

        // ================================================================================== notes

        static readonly int[] Chord8 = { 36, 48, 55, 60, 64, 67, 72, 76 };

        [Test]
        public void AnEightNoteChordSoundsWithAllEightVoicesAndVelocityIsLinearInLevel()
        {
            int len = Rate * 3;
            var mod = ShapedNoise(len, Profile, 0.1);
            double[] rms = new double[2];
            for (int k = 0; k < 2; k++)
            {
                var e = Engine(Setup("Synth", 16));
                var notes = Chord8.Select(n => new Note(0, true, n, k == 0 ? 32 : 127)).ToList();
                var (ol, _) = Render(e, mod, (float[])mod.Clone(), notes: notes);
                Assert.That(e.ActiveVoices, Is.EqualTo(8));
                Assert.That(e.HeldNotes, Is.EqualTo(8));
                rms[k] = Rms(ol, Rate, len);
            }
            double ratio = 20 * Math.Log10(rms[1] / rms[0]);
            TestContext.Out.WriteLine($"velocity 127 against 32: {ratio:0.00} dB (linear: {20 * Math.Log10(127 / 32.0):0.00})");
            Assert.That(ratio, Is.EqualTo(20 * Math.Log10(127 / 32.0)).Within(1.0));
            Assert.That(rms[1], Is.GreaterThan(0.02), "a chord is clearly heard");
            Assert.That(rms[1], Is.LessThan(0.5), "and leaves headroom");
        }

        [Test]
        public void NoteStartsNoteEndsAndGlidesDoNotClick()
        {
            int len = Rate * 8;
            var mod = ShapedNoise(len, Profile, 0.12);
            foreach (double glide in new[] { 0.0, 300.0 })
            {
                var e = Engine(Setup("Synth", 16, p => { Phys(p, P.Glide, glide); Phys(p, P.SynAttack, 1); Phys(p, P.SynRelease, 5); }));
                var notes = new List<Note>
                {
                    new(Rate / 2, true, 48), new(Rate / 2, true, 55), new(Rate / 2, true, 64),
                    new(Rate * 2, false, 55), new(Rate * 2 + 100, true, 59),
                    new(Rate * 3, false, 48), new(Rate * 3, false, 64), new(Rate * 3, false, 59),
                    new(Rate * 4, true, 40), new(Rate * 4 + 4800, true, 52), new(Rate * 4 + 9600, true, 67), new(Rate * 4 + 14400, true, 71),
                    new(Rate * 6, false, 40), new(Rate * 6, false, 52), new(Rate * 6, false, 67), new(Rate * 6, false, 71),
                };
                var (ol, or) = Render(e, mod, (float[])mod.Clone(), notes: notes);
                var clicks = ClickDetector.Find(ol).Union(ClickDetector.Find(or)).ToList();
                TestContext.Out.WriteLine($"glide {glide} ms: {clicks.Count} clicks");
                Assert.That(clicks, Is.Empty);
            }
        }

        [Test]
        public void ANewNoteAtEveryVoiceStealDoesNotClick()
        {
            int len = Rate * 6;
            var mod = ShapedNoise(len, Profile, 0.12);
            var e = Engine(Setup("Synth", 16));
            var notes = new List<Note>();
            for (int i = 0; i < 16; i++) notes.Add(new Note(Rate / 4 + i * 4000, true, 40 + i * 2));
            var (ol, or) = Render(e, mod, (float[])mod.Clone(), notes: notes);
            Assert.That(ClickDetector.Find(ol).Union(ClickDetector.Find(or)), Is.Empty);
        }

        // ================================================================================== sidechain

        [TestCase(256)] [TestCase(37)] [TestCase(1000)]
        public void TheSidechainCarrierIsSampleExact(int shift)
        {
            int len = Rate * 2;
            var mod = ShapedNoise(len, Profile, 0.1, 4);
            var car = SawPad(len, new[] { 110.0, 165.0 }, 0.15);
            var e0 = Engine(Setup("Sidechain", 16));
            var (a, _) = Render(e0, mod, (float[])mod.Clone(), car, car);
            var mod2 = new float[len]; var car2 = new float[len];
            Array.Copy(mod, 0, mod2, shift, len - shift); Array.Copy(car, 0, car2, shift, len - shift);
            var e1 = Engine(Setup("Sidechain", 16));
            var (b, _) = Render(e1, mod2, (float[])mod2.Clone(), car2, car2);
            double worst = 0;
            for (int i = 0; i < len - shift; i++) worst = Math.Max(worst, Math.Abs(a[i] - b[i + shift]));
            for (int i = 0; i < shift; i++) worst = Math.Max(worst, Math.Abs(b[i]));
            TestContext.Out.WriteLine($"carrier and modulator moved {shift} samples: largest difference {worst:0.0e+0}");
            Assert.That(worst, Is.LessThan(1e-6));
        }

        [Test]
        public void AnEmptySidechainGivesSilenceAndTheEditorsMessage()
        {
            int len = Rate * 2;
            var mod = ShapedNoise(len, Profile, 0.2);
            var e = Engine(Setup("Sidechain", 16));
            var (ol, or) = Render(e, mod, (float[])mod.Clone());
            Assert.That(e.NeedsWire, Is.True);
            Assert.That(VocoderEngine.WireMessage, Is.EqualTo("Wire a carrier into the sidechain"));
            Assert.That(Peak(ol, Rate / 2, len), Is.EqualTo(0).Within(1e-7));
            Assert.That(Peak(or, Rate / 2, len), Is.EqualTo(0).Within(1e-7));
            var car = SawPad(len, new[] { 110.0 }, 0.2);
            var e2 = Engine(Setup("Sidechain", 16));
            Render(e2, mod, (float[])mod.Clone(), car, car);
            Assert.That(e2.NeedsWire, Is.False);
        }

        // ================================================================================== Mix and Amount

        [Test]
        public void AmountZeroAndMixZeroAreTheDryBitExactAndAmountScalesTheModulationParameters()
        {
            int len = Rate;
            var x = ShapedNoise(len, Profile, 0.3, 8);
            var notes = new List<Note> { new(0, true, 48), new(0, true, 60) };
            foreach (var which in new[] { "Amount", "Mix" })
            {
                var e = Engine(Fresh(p => { if (which == "Amount") Phys(p, P.Amount, 0); else Phys(p, P.Mix, 0); }));
                var (ol, or) = Render(e, x, (float[])x.Clone(), notes: notes);
                for (int i = 0; i < len; i++)
                    if (ol[i] != x[i] || or[i] != x[i]) Assert.Fail($"{which} 0: sample {i} is {ol[i]}, the dry {x[i]}");
            }
            foreach (var (amount, expect) in new[] { (0.0, 0.0), (50.0, 3.0), (100.0, 6.0), (200.0, 12.0) })
            {
                var v = Fresh(p => { Phys(p, P.Formant, 6); Phys(p, P.Amount, amount); });
                var e = Engine(v);
                Render(e, x, (float[])x.Clone(), length: 512);
                Assert.That(e.Effective.Value(P.Formant), Is.EqualTo(expect).Within(0.01), $"Amount {amount} %");
            }
        }

        // ================================================================================== the audio thread

        [Test]
        public void NothingIsAllocatedPerBlockOverTwoThousandBlocksWithParametersAndNotesMoving()
        {
            var v = Fresh();
            var e = Engine(v);
            var x = ShapedNoise(Rate * 12, Profile, 0.2);
            var car = SawPad(Rate * 12, new[] { 110.0, 165.0 }, 0.1);
            var l = new float[256]; var r = new float[256]; var cl = new float[256];
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                int pos = (b % 2000) * 256;
                Array.Copy(x, pos, l, 0, 256); Array.Copy(x, pos, r, 0, 256); Array.Copy(car, pos, cl, 0, 256);
                if (b % 97 == 3) e.NoteOn(1000 + b % 8, 48 + b % 24, 0.8f);
                if (b % 97 == 60) e.NoteOff(1000 + (b - 57) % 8);
                v.Set(P.Low, 100 + b % 200); v.Set(P.Width, 300 + b % 100); v.Set(P.Formant, b % 25); v.Set(P.Spread, b % 100);
                v.Set(P.Attack, 100 + b % 200); v.Set(P.Freeze, b % 300 > 280 ? 1 : 0);
                if (b % 400 == 399) v.Set(P.Bands, (b / 400) % 33);
                if (b % 700 == 699) v.Set(P.Carrier, (b / 700) % 3);
                e.SidechainWired = true;
                e.Process(l, r, cl, cl, 256);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void NoDenormalsRingOutInAThirtySecondTailAndTheOutputIsFinite()
        {
            var v = Fresh(p => { Phys(p, P.Release, 1000); Phys(p, P.SynRelease, 5000); });
            var e = Engine(v);
            int total = Rate * 34;
            var x = new float[total];
            var rng = new Rng(5);
            for (int i = 0; i < Rate / 2; i++) x[i] = 0.8f * rng.NextBipolar();
            var notes = new List<Note> { new(0, true, 48), new(0, true, 60), new(Rate / 2, false, 48), new(Rate / 2, false, 60) };
            var (ol, or) = Render(e, x, (float[])x.Clone(), notes: notes);
            for (int i = 0; i < total; i++)
            {
                if (float.IsSubnormal(ol[i]) || float.IsSubnormal(or[i])) Assert.Fail($"a denormal at {i}: {ol[i]:E3} {or[i]:E3} around {ol[i - 1]:E3} {ol[i + 1]:E3}; band max {e.Bank.MaxLevel:E3} voices {e.ActiveVoices}");
                if (!float.IsFinite(ol[i]) || !float.IsFinite(or[i])) Assert.Fail($"not finite at {i}");
            }
            Assert.That(Peak(ol, Rate * 32, total), Is.LessThan(1e-6), "quiet at the end");
        }

        [Test]
        public void TheCpuOfFortyBandsAtFortyEightKilohertzStereoIsStatedAgainstTheDyeEssenceVocoder()
        {
            int len = Rate * 10;
            var x = ShapedNoise(len, Profile, 0.2);
            var e = Engine(Fresh(p => { Phys(p, P.Bands, 40); Phys(p, P.Carrier, 0); }));
            e.NoteOn(1060, 60, 0.8f); e.NoteOn(1064, 64, 0.8f); e.NoteOn(1067, 67, 0.8f); e.NoteOn(1072, 72, 0.8f);
            var ref32 = new ChannelVocoder(Rate) { Bands = 32 };
            var l = new float[256]; var r = new float[256]; var m = new float[256];
            double mine = 0, theirs = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int pos = 0; pos + 256 <= len; pos += 256) { Array.Copy(x, pos, l, 0, 256); Array.Copy(x, pos, r, 0, 256); e.Process(l, r, null, null, 256); }
                sw.Stop(); mine = sw.Elapsed.TotalSeconds / 10;
                sw.Restart();
                for (int pos = 0; pos + 256 <= len; pos += 256) { Array.Copy(x, pos, l, 0, 256); Array.Copy(x, pos, r, 0, 256); Array.Copy(x, pos, m, 0, 256); ref32.Process(l, r, m, 256); }
                sw.Stop(); theirs = sw.Elapsed.TotalSeconds / 10;
            }
            TestContext.Out.WriteLine($"Neo Vocoder 40 bands: {mine * 100:0.0} % of one core; Dye Essence vocoder FX (its most, 32 bands): {theirs * 100:0.0} %; ratio {mine / theirs:0.0}");
            Assert.That(mine, Is.LessThan(0.25));
        }
    
        [Test]
        public void ANoteOrAChordOnTheSynthIsAsLoudAsTheNoiseCarrierWithinFourDb()
        {
            int len = Rate * 6;
            var mod = ShapedNoise(len, Profile, 0.1);
            var eN = Engine(Setup("Noise", 16));
            var (n, _) = Render(eN, mod, (float[])mod.Clone());
            TestContext.Out.WriteLine($"noise carrier rms {Rms(n, Rate, len):0.0000}");
            foreach (int[] keys in new[] { new[] { 48 }, new[] { 60 }, new[] { 72 }, new[] { 48, 55, 60, 64 }, new[] { 36, 48, 55, 60, 64, 67, 72, 76 } })
            {
                var e = Engine(Setup("Synth", 16));
                var (o, _) = Render(e, mod, (float[])mod.Clone(), notes: keys.Select(k => new Note(0, true, k, 127)).ToList());
                double db = 20 * Math.Log10(Rms(o, Rate, len) / Rms(n, Rate, len));
                TestContext.Out.WriteLine($"synth keys {string.Join(",", keys)}: {db:+0.0;-0.0} dB against the noise carrier");
                Assert.That(db, Is.InRange(-4.0, 4.0));
            }
        }
    
        [Test]
        public void FreezeHoldsTheBandLevelsAfterTheModulatorStopsAlsoWhenItIsOnFromTheStart()
        {
            int len = Rate * 4;
            var mod = ShapedNoise(len, Profile, 0.2);
            for (int i = Rate; i < len; i++) mod[i] = 0;
            foreach (bool fromStart in new[] { true, false })
            {
                var e = Engine(Setup("Noise", 16, p => { if (fromStart) Switch(p, P.Freeze, true); }));
                var v = e.Effective;
                var (ol, _) = Render(e, mod, (float[])mod.Clone(), atBlock: pos => { });
                // Turned on during the sound, the hold is immediate.
                if (!fromStart)
                {
                    var e2 = Engine(Setup("Noise", 16));
                    var raw = Fresh(p => { Choice(p, P.Carrier, "Noise"); Phys(p, P.Bands, 16); Phys(p, P.Sibilance, 0); Phys(p, P.Attack, 30); Phys(p, P.Release, 30); });
                    e2 = Engine(raw);
                    (ol, _) = Render(e2, mod, (float[])mod.Clone(), atBlock: pos => { if (pos >= Rate / 2) Switch(raw, P.Freeze, true); });
                }
                double held = Rms(ol, Rate * 2, Rate * 4);
                TestContext.Out.WriteLine($"{(fromStart ? "on from the start" : "switched on at 0.5 s")}: held level {held:0.0000}");
                Assert.That(held, Is.GreaterThan(0.01));
            }
        }
    }
}
