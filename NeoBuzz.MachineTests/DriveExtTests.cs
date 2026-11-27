using NeoBuzz.Drive;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Drive's five added Modes (EXT3, issue #192): the gates, measured.</summary>
    [TestFixture]
    public class DriveExtTests
    {
        static readonly DriveMode[] NewModes = { DriveMode.Overdrive, DriveMode.Fuzz, DriveMode.HardClip, DriveMode.Rectify, DriveMode.Transformer };
        static readonly DriveMode[] AllModes = Enum.GetValues<DriveMode>();

        static (DriveEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new DriveEngine(v);
            e.SetSampleRate(Measure.Rate);
            return (e, v);
        }

        static int DriveRaw(double db) => Params.Table[P.Drive].Raw(db);

        static float[] Run(DriveEngine e, float[] x, int block = DriveEngine.MaxBlock, Action<int> beforeBlock = null)
        {
            var y = new float[x.Length];
            var l = new float[DriveEngine.MaxBlock];
            var r = new float[DriveEngine.MaxBlock];
            for (int pos = 0, b = 0; pos < x.Length; pos += block, b++)
            {
                beforeBlock?.Invoke(b);
                int n = Math.Min(block, x.Length - pos);
                Array.Copy(x, pos, l, 0, n);
                Array.Copy(x, pos, r, 0, n);
                e.Process(l, r, n);
                Array.Copy(l, 0, y, pos, n);
                for (int i = 0; i < n; i++) Assert.That(l[i], Is.EqualTo(r[i]), "channels match");
            }
            return y;
        }

        [Test]
        public void TheModeListKeepsItsOldIndices()
        {
            var names = Params.Table[P.Mode].Names;
            Assert.That(names, Is.EqualTo(new[] { "Tape", "Tube", "Transistor", "Diode", "Fold", "Overdrive", "Fuzz", "Hard clip", "Rectify", "Transformer" }));
            Assert.That(Params.Table[P.Mode].Max, Is.EqualTo(9));
            for (int i = 0; i < 5; i++) Assert.That((int)(DriveMode)i, Is.EqualTo(i));
            Assert.That((int)DriveMode.Overdrive, Is.EqualTo(5));
            Assert.That((int)DriveMode.Transformer, Is.EqualTo(9));
        }

        // ------------------------------------------------------------------------------------------------ saved states and automation

        [Test]
        public void AnOldSavedStateLoadsAndRendersBitIdentical()
        {
            // A song saved before EXT3 (version 0, twelve values, Mode in 0 … 4): the chain keeps every value, and the engine renders it as before.
            Assert.That(DriveState.Chain.Current, Is.EqualTo(1));
            Assert.That(DriveState.Chain.Validate(), Is.Empty);
            Assert.That(DriveState.Chain.Layout(0), Is.EqualTo(DriveState.Version0Keys));
            var x = Measure.PinkNoise(8192, 0.2);
            for (int mode = 0; mode <= 4; mode++)
            {
                var saved = new[] { mode, 640, 430, 720, 100, 380, 900, 560, 1, 850, 2, 1000 };
                var migrated = DriveState.Chain.MigrateGlobals(0, saved);
                Assert.That(migrated, Is.EqualTo(saved), $"mode {mode}: every saved value stays");
                var (a, _) = Make(v => { for (int i = 0; i < saved.Length; i++) v.Set(i, saved[i]); });
                var (b, _) = Make(v => { for (int i = 0; i < migrated.Length; i++) v.Set(i, migrated[i]); });
                Assert.That(Run(b, x), Is.EqualTo(Run(a, x)));
            }
            // Eleven values (saved before Amount existed): Amount takes its default.
            var old11 = new[] { 3, 640, 430, 720, 100, 380, 900, 560, 1, 850, 2 };
            var m11 = DriveState.Chain.MigrateGlobals(0, old11);
            Assert.That(m11.Take(11), Is.EqualTo(old11));
            Assert.That(m11[P.Amount], Is.EqualTo(Params.Table[P.Amount].Default));
        }

        [Test]
        public void ASavedModeAndAnAutomationLanePlayTheSameModesAsBefore()
        {
            // The raw value is the Mode's index: 0 … 4 are the five old ones, 5 … 9 the new ones. A pattern value or a lane writes those.
            var expected = new[] { DriveMode.Tape, DriveMode.Tube, DriveMode.Transistor, DriveMode.Diode, DriveMode.Fold };
            var v = new ParamValues(Params.Table);
            for (int raw = 0; raw <= 9; raw++)
            {
                v.Set(P.Mode, raw);
                Assert.That(v.Choice(P.Mode), Is.EqualTo(raw));
                Assert.That((DriveMode)v.Choice(P.Mode), raw < 5 ? Is.EqualTo(expected[raw]) : Is.EqualTo((DriveMode)raw));
            }
            // A lane stepping through the old Modes plays each one (the settled level matches that Mode held still).
            var x = Measure.PinkNoise(48000 * 6, 0.2);
            var (e, ev) = Make(q => q.Set(P.Drive, DriveRaw(18)));
            var y = Run(e, x, 256, b => ev.Set(P.Mode, Math.Min(4, b * 256 / 48000)));
            for (int m = 0; m <= 4; m++)
            {
                var (still, _) = Make(q => { q.Set(P.Mode, m); q.Set(P.Drive, DriveRaw(18)); });
                var ys = Run(still, x.AsSpan(0, 48000).ToArray());
                double a = Measure.Rms(ys, 30000, 16000), c = Measure.Rms(y, m * 48000 + 30000, 16000);
                Assert.That(Math.Abs(Measure.Db(c / a)), Is.LessThan(m == 0 ? 0.01 : 1.5), $"lane step {m}");
            }
        }

        // ------------------------------------------------------------------------------------------------ auto gain

        [TestCase((int)DriveMode.Overdrive)]
        [TestCase((int)DriveMode.Fuzz)]
        [TestCase((int)DriveMode.HardClip)]
        [TestCase((int)DriveMode.Rectify)]
        [TestCase((int)DriveMode.Transformer)]
        public void AutoGainHoldsTheLevelWithinTwoDbAcrossTheDriveSweep(int modeIndex)
        {
            var mode = (DriveMode)modeIndex;
            var x = Measure.PinkNoise(96000, 0.125);          // −18 dBFS RMS
            double inRms = Measure.Rms(x, 48000, 48000);
            double worst = 0;
            for (double db = 0; db <= 36.01; db += 3)
            {
                var (e, _) = Make(v => { v.Set(P.Mode, (int)mode); v.Set(P.Drive, DriveRaw(db)); });
                var y = Run(e, x);
                double diff = Measure.Db(Measure.Rms(y, 48000, 48000) / inRms);
                TestContext.Out.WriteLine($"{mode} drive {db} dB: {diff:+0.00;−0.00} dB");
                worst = Math.Max(worst, Math.Abs(diff));
                Assert.That(Math.Abs(diff), Is.LessThan(2.0), $"{mode} at {db} dB");
            }
            Measure.Report($"Neo Drive {mode}: auto gain, Drive 0 to 36 dB on pink noise at −18 dBFS: worst deviation {worst:F2} dB");
        }

        // ------------------------------------------------------------------------------------------------ aliasing

        [TestCase((int)DriveMode.Overdrive)]
        [TestCase((int)DriveMode.Fuzz)]
        [TestCase((int)DriveMode.HardClip)]
        [TestCase((int)DriveMode.Rectify)]
        [TestCase((int)DriveMode.Transformer)]
        public void AliasingAtEightTimes(int modeIndex)
        {
            var mode = (DriveMode)modeIndex;
            double worst = -300;
            string where = "";
            // A 1 to 10 kHz sweep in steps (each a sine on an odd FFT bin, so aliases fall off the harmonic bins).
            foreach (int bin in new[] { 1365, 2047, 2731, 3413, 4231, 5461, 6827, 8191, 9557, 11003, 13653 })
            {
                var (e, _) = Make(v => { v.Set(P.Mode, (int)mode); v.Set(P.Drive, DriveRaw(24)); v.Set(P.Quality, 2); });
                var y = Run(e, Measure.Sine(Measure.BinFrequency(bin), 8192 + Measure.N, 0.25));
                double floor = Measure.AliasFloorDb(Measure.Spectrum(y, 8192), bin);
                if (floor > worst) { worst = floor; where = $"{Measure.BinFrequency(bin):F0} Hz"; }
            }
            Measure.Report($"Neo Drive {mode} 8×, Drive 24 dB, 1 to 10 kHz sweep: aliased components at most {worst:F1} dB below the fundamental's level (worst at {where})");
            if (mode == DriveMode.HardClip || mode == DriveMode.Fuzz) Assert.That(worst, Is.LessThan(-60), $"{mode}");
        }

        // ------------------------------------------------------------------------------------------------ Mode changes

        static float[] ModeSource(int length)
        {
            var chord = FxGoldenTests.Chord(Measure.Rate, length / (double)Measure.Rate + 0.01);
            var pink = Measure.PinkNoise(length, 0.02);
            var x = new float[length];
            for (int i = 0; i < length; i++) x[i] = chord[i] + pink[i];
            return x;
        }

        [TestCase(2400)]
        [TestCase(5000)]
        [TestCase(256)]
        public void AnAutomatedSwitchThroughEveryModeMakesNoClicks(int hold)
        {
            int total = hold * AllModes.Length * 2 + 4800;
            var x = ModeSource(total);
            var (e, v) = Make(q => { q.Set(P.Drive, DriveRaw(12)); });
            int clicks = 0;
            var y = Run(e, x, 64, b =>
            {
                int step = (b * 64) / hold;
                int m = step < AllModes.Length * 2 ? (step < AllModes.Length ? step : AllModes.Length * 2 - 1 - step) : 0;
                v.Set(P.Mode, m);
            });
            var found = ClickDetector.Find(y);
            clicks = found.Count;
            Measure.Report($"Neo Drive Mode switch through all {AllModes.Length} Modes and back (every {hold} samples, 64-sample blocks): {clicks} clicks");
            Assert.That(clicks, Is.EqualTo(0), "clicks at " + string.Join(", ", found.Take(10)));
        }

        [Test]
        public void ARapidModeLaneMakesNoClicks()
        {
            var rng = new Random(11);
            int total = 96000;
            var x = ModeSource(total);
            var (e, v) = Make(q => { q.Set(P.Drive, DriveRaw(18)); });
            var y = Run(e, x, 256, b => { if (b % 3 == 0) v.Set(P.Mode, rng.Next(0, 10)); });
            var found = ClickDetector.Find(y);
            Measure.Report($"Neo Drive random Mode every ~768 samples for 2 s: {found.Count} clicks");
            Assert.That(found.Count, Is.EqualTo(0), "clicks at " + string.Join(", ", found.Take(10)));
        }

        [TestCase(32)]
        [TestCase(64)]
        [TestCase(256)]
        public void AModeChangeCrossfadesInAboutTenMilliseconds(int block)
        {
            var x = Measure.PinkNoise(48000, 0.2);
            var (e, v) = Make(q => { q.Set(P.Drive, DriveRaw(24)); q.Set(P.AutoGain, 0); });
            var l = new float[block]; var r = new float[block];
            Assert.That(e.Fading, Is.False);
            int fading = 0;
            for (int b = 0; b < 100; b++)
            {
                if (b == 20) v.Set(P.Mode, (int)DriveMode.HardClip);
                Array.Copy(x, b * block, l, 0, block); Array.Copy(x, b * block, r, 0, block);
                bool before = e.Fading;
                e.Process(l, r, block);
                if (b == 20 || before || e.Fading) fading++;
            }
            double ms = fading * block * 1000.0 / Measure.Rate;
            Measure.Report($"Neo Drive Mode change at {block}-sample blocks: crossfade of {ms:F1} ms");
            Assert.That(e.Fading, Is.False);
            Assert.That(ms, Is.InRange(8, 22));
        }

        static float MaxDiff(float[] a, float[] b)
        {
            float m = 0;
            for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
            return m;
        }

        [TestCase((int)DriveMode.Overdrive)]
        [TestCase((int)DriveMode.Fuzz)]
        [TestCase((int)DriveMode.Rectify)]
        [TestCase((int)DriveMode.Transformer)]
        public void AModeLaneSettlesOnTheModeItNames(int modeIndex)
        {
            // An automation lane (the value written at a block) ends up playing the Mode it names: the level after it settles matches a
            // static render of that Mode.
            var x = Measure.PinkNoise(96000, 0.2);
            var (still, _) = Make(v => { v.Set(P.Mode, modeIndex); v.Set(P.Drive, DriveRaw(18)); });
            var (lane, lv) = Make(v => { v.Set(P.Mode, 0); v.Set(P.Drive, DriveRaw(18)); });
            var ys = Run(still, x);
            var yl = Run(lane, x, 256, b => { if (b == 100) lv.Set(P.Mode, modeIndex); });
            double a = Measure.Rms(ys, 72000, 24000), c = Measure.Rms(yl, 72000, 24000);
            Assert.That(Math.Abs(Measure.Db(c / a)), Is.LessThan(0.6), $"{(DriveMode)modeIndex}");
        }

        // ------------------------------------------------------------------------------------------------ Mix and Amount

        [TestCase((int)DriveMode.Overdrive)]
        [TestCase((int)DriveMode.Fuzz)]
        [TestCase((int)DriveMode.HardClip)]
        [TestCase((int)DriveMode.Rectify)]
        [TestCase((int)DriveMode.Transformer)]
        public void AmountZeroIsBitExactDryAndMixZeroIsTheDelayedInput(int modeIndex)
        {
            var x = Measure.PinkNoise(16384, 0.2);
            int L = DriveEngine.Latency;
            foreach (var setup in new Action<ParamValues>[]
            {
                v => v.Set(P.Amount, 0),
                v => v.Set(P.Mix, 0),
            })
            {
                var (e, _) = Make(v => { v.Set(P.Mode, modeIndex); v.Set(P.Drive, 1000); setup(v); });
                var y = Run(e, x);
                for (int i = L; i < x.Length; i++) Assert.That(y[i], Is.EqualTo(x[i - L]), $"{(DriveMode)modeIndex} sample {i}");
            }
        }

        [TestCase((int)DriveMode.Overdrive)]
        [TestCase((int)DriveMode.Fuzz)]
        [TestCase((int)DriveMode.HardClip)]
        [TestCase((int)DriveMode.Rectify)]
        [TestCase((int)DriveMode.Transformer)]
        public void AmountScalesDriveAndBiasAndMixBlends(int modeIndex)
        {
            var x = Measure.PinkNoise(32768, 0.2);
            var (a, _) = Make(v => { v.Set(P.Mode, modeIndex); v.Set(P.Drive, DriveRaw(24)); v.Set(P.Amount, 500); });
            var (b, _) = Make(v => { v.Set(P.Mode, modeIndex); v.Set(P.Drive, DriveRaw(12)); });
            var ya = Run(a, x); var yb = Run(b, x);
            double worst = 0;
            for (int i = 0; i < x.Length; i++) worst = Math.Max(worst, Math.Abs(ya[i] - yb[i]));
            Assert.That(worst, Is.LessThan(1e-3), "Amount 50 % at Drive 24 dB plays Drive 12 dB");
            var (full, _) = Make(v => { v.Set(P.Mode, modeIndex); v.Set(P.Drive, DriveRaw(24)); });
            var (half, _) = Make(v => { v.Set(P.Mode, modeIndex); v.Set(P.Drive, DriveRaw(24)); v.Set(P.Mix, 500); });
            var yf = Run(full, x); var yh = Run(half, x);
            int L = DriveEngine.Latency;
            double w2 = 0;
            for (int i = L; i < x.Length; i++) w2 = Math.Max(w2, Math.Abs(yh[i] - 0.5f * (yf[i] + x[i - L])));
            Assert.That(w2, Is.LessThan(1e-4), "Mix 50 % is the average of dry and wet");
        }

        // ------------------------------------------------------------------------------------------------ the audio thread

        [TestCase((int)DriveMode.Overdrive)]
        [TestCase((int)DriveMode.Fuzz)]
        [TestCase((int)DriveMode.HardClip)]
        [TestCase((int)DriveMode.Rectify)]
        [TestCase((int)DriveMode.Transformer)]
        public void NoAllocationAndNoDenormals(int modeIndex)
        {
            var (e, v) = Make(q => { q.Set(P.Mode, modeIndex); q.Set(P.Drive, DriveRaw(24)); });
            int blocks = 2400;
            var x = Measure.PinkNoise(256 * blocks, 0.3);
            // a burst, then a long decay to silence: the states run down through the denormal range
            for (int i = 256 * 600; i < x.Length; i++) x[i] *= (float)Math.Exp(-(i - 256 * 600) / 20000.0);
            var l = new float[256]; var r = new float[256];
            int denormals = 0;
            void Block(int b)
            {
                if (b % 400 == 0) v.Set(P.Drive, DriveRaw(6 + (b / 400) * 4));      // a parameter move now and then
                Array.Copy(x, b * 256, l, 0, 256); Array.Copy(x, b * 256, r, 0, 256);
                e.Process(l, r, 256);
                for (int i = 0; i < 256; i++) if (float.IsSubnormal(l[i]) || float.IsSubnormal(r[i])) denormals++;
            }
            for (int b = 0; b < 100; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(blocks - 100, b => Block(b + 100));
            Assert.That(bytes, Is.EqualTo(0), why);
            Assert.That(denormals, Is.EqualTo(0));
        }

        [Test]
        public void AModeChangeAllocatesNothing()
        {
            var (e, v) = Make(q => { q.Set(P.Drive, DriveRaw(18)); });
            var x = Measure.PinkNoise(256 * 600, 0.3);
            var l = new float[256]; var r = new float[256];
            void Block(int b)
            {
                v.Set(P.Mode, (b / 4) % 10);
                Array.Copy(x, b * 256, l, 0, 256); Array.Copy(x, b * 256, r, 0, 256);
                e.Process(l, r, 256);
            }
            for (int b = 0; b < 100; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(500, b => Block(b + 100));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void SilenceStaysSilentInEveryNewMode()
        {
            foreach (var mode in NewModes)
            {
                var (e, _) = Make(v => { v.Set(P.Mode, (int)mode); v.Set(P.Bias, 900); v.Set(P.Drive, 1000); });
                var y = Run(e, new float[8192]);
                Assert.That(Measure.Peak(y, 0, y.Length), Is.EqualTo(0), mode.ToString());
            }
        }

        [Test]
        public void ExtremeSettingsStayBoundedAndFinite()
        {
            var rng = new Random(5);
            var (e, v) = Make();
            var x = Measure.PinkNoise(48000, 0.7);
            for (int round = 0; round < 30; round++)
            {
                foreach (var d in Params.Table) v.Set(d.Index, rng.Next(2) == 0 ? d.Min : d.Max);
                v.Set(P.Mode, 5 + rng.Next(5));
                var y = Run(e, x);
                foreach (var s in y)
                {
                    Assert.That(float.IsFinite(s), Is.True);
                    Assert.That(Math.Abs(s), Is.LessThan(64));
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ the presets

        static DriveEngine Engine(ParamValues v)
        {
            var e = new DriveEngine(v);
            e.SetSampleRate(Measure.Rate);
            return e;
        }

        static ParamValues PresetValues(string preset)
        {
            var found = PresetTests.AllPresets().First(p => p.Machine == "Neo Drive" && p.Preset == preset);
            var v = new ParamValues(Params.Table);
            foreach (var kv in found.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            return v;
        }

        [Test]
        public void TheFiveNewPresetsAreClearlyAudibleAndClearlyDifferent()
        {
            string[] names = { "Overdrive — Warm Crunch", "Fuzz — Sputter Fuzz", "Hard clip — Loud Clip", "Rectify — Octave Grit", "Transformer — Heavy Iron" };
            var sources = new Dictionary<string, float[]>
            {
                ["pink"] = Measure.PinkNoise(96000, 0.5),
                ["drums"] = FxGoldenTests.DrumLoop(Measure.Rate, 2),
                ["chord"] = FxGoldenTests.Chord(Measure.Rate, 2),
            };
            int L = DriveEngine.Latency;
            foreach (var (src, x) in sources)
            {
                var outs = names.Select(n => Run(Engine(PresetValues(n)), x)).ToArray();
                for (int a = 0; a < names.Length; a++)
                {
                    double d = 0, s = 0;
                    for (int i = 24000 + L; i < x.Length; i++) { double e = outs[a][i] - x[i - L]; d += e * e; s += (double)x[i - L] * x[i - L]; }
                    double dry = 10 * Math.Log10(d / s);
                    Measure.Report($"Neo Drive '{names[a]}' on {src}: differs from the dry source by {dry:F1} dB");
                    Assert.That(dry, Is.GreaterThan(-9), $"{names[a]} on {src} is audible");
                    for (int b = a + 1; b < names.Length; b++)
                    {
                        double dd = 0, ss = 0;
                        for (int i = 24000; i < x.Length; i++) { double e = outs[a][i] - outs[b][i]; dd += e * e; ss += (double)outs[a][i] * outs[a][i]; }
                        double apart = 10 * Math.Log10(dd / Math.Max(ss, 1e-30));
                        Assert.That(apart, Is.GreaterThan(-9), $"{names[a]} against {names[b]} on {src}");
                    }
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ the Modes do what they say

        /// <summary>Level of a harmonic in dB re the input amplitude (a sine of that amplitude reads 0).</summary>
        static double Level(double[] mag, int bin, int h, double amp) => 20 * Math.Log10(Math.Max(1e-30, mag[bin * h]) / amp);

        static double[] Spectrum(DriveMode mode, double hz, double db, Action<ParamValues> more = null, double amp = 0.25)
        {
            int bin = (int)Math.Round(hz * Measure.N / Measure.Rate);
            var (e, _) = Make(v => { v.Set(P.Mode, (int)mode); v.Set(P.Drive, DriveRaw(db)); v.Set(P.AutoGain, 0); more?.Invoke(v); });
            var y = Run(e, Measure.Sine(Measure.BinFrequency(bin), 8192 + Measure.N, amp));
            return Measure.Spectrum(y, 8192);
        }

        [Test]
        public void RectifyMovesFromHalfWaveToFullWaveAndGivesAnOctaveUp()
        {
            const int bin = 1365;
            double hz = Measure.BinFrequency(bin);
            var h = Spectrum(DriveMode.Rectify, hz, 12, v => v.Set(P.Character, 0));
            var f = Spectrum(DriveMode.Rectify, hz, 12, v => v.Set(P.Character, 1000));
            double f1h = Level(h, bin, 1, 0.25), f2h = Level(h, bin, 2, 0.25), f1f = Level(f, bin, 1, 0.25), f2f = Level(f, bin, 2, 0.25);
            Measure.Report($"Neo Drive Rectify: half-wave fundamental {f1h:F1} dB, octave {f2h:F1} dB; full-wave fundamental {f1f:F1} dB, octave {f2f:F1} dB");
            Assert.That(f1h - f2h, Is.LessThan(12), "half-wave: the octave is close to the fundamental");
            Assert.That(f2f, Is.GreaterThan(f1f + 20), "full-wave: the octave leads, the fundamental is gone");
            Assert.That(f2f, Is.GreaterThan(f2h), "full-wave has more octave than half-wave");
        }

        [Test]
        public void TransformerSaturatesTheLowsFirst()
        {
            // The same level at 60 Hz and 1 kHz: the low one is compressed much more (lower third harmonic share means less distortion).
            var (_, _) = Make();
            double Thd(double hz)
            {
                int bin = (int)Math.Round(hz * Measure.N / Measure.Rate);
                var m = Spectrum(DriveMode.Transformer, Measure.BinFrequency(bin), 6, v => { v.Set(P.Character, 500); }, 0.25);
                return Measure.Thd(m, bin);
            }
            double lo = Thd(61), hi = Thd(1000);
            Measure.Report($"Neo Drive Transformer at Character 50 % (corner about 110 Hz): THD at 61 Hz {lo * 100:F1} %, at 1 kHz {hi * 100:F1} %");
            Assert.That(lo, Is.GreaterThan(hi * 3));
        }

        [Test]
        public void TransformerCornerFollowsCharacter()
        {
            double Thd(int character, double hz)
            {
                int bin = (int)Math.Round(hz * Measure.N / Measure.Rate);
                var m = Spectrum(DriveMode.Transformer, Measure.BinFrequency(bin), 6, v => v.Set(P.Character, character));
                return Measure.Thd(m, bin);
            }
            // At 250 Hz a 40 Hz corner leaves the signal mostly on the highs' side; a 300 Hz corner puts it in the lows.
            double low = Thd(0, 250), high = Thd(1000, 250);
            Measure.Report($"Neo Drive Transformer at 250 Hz: THD {low * 100:F1} % with the corner at 40 Hz, {high * 100:F1} % with it at 300 Hz");
            Assert.That(high, Is.GreaterThan(low * 1.5));
        }

        [Test]
        public void TransformerKeepsAMemory()
        {
            // The compression follows the path: the curve on the way up differs from the way down (a hysteresis loop of nonzero width).
            var (e, _) = Make(v => { v.Set(P.Mode, (int)DriveMode.Transformer); v.Set(P.Drive, DriveRaw(12)); v.Set(P.AutoGain, 0); });
            var x = Measure.Sine(80, 48000, 0.4);
            var y = Run(e, x);
            double width = 0;
            // For each input level in a late cycle, the output on the rising and the falling half.
            int L = DriveEngine.Latency, start = 24000;
            var up = new SortedDictionary<int, double>(); var down = new SortedDictionary<int, double>();
            for (int i = start; i < start + 600; i++)
            {
                double xin = x[i - L]; int key = (int)Math.Round(xin * 20);
                bool rising = x[i - L] > x[i - L - 1];
                (rising ? up : down)[key] = y[i];
            }
            foreach (var k in up.Keys) if (down.ContainsKey(k) && Math.Abs(k) < 6) width = Math.Max(width, Math.Abs(up[k] - down[k]));
            Measure.Report($"Neo Drive Transformer hysteresis: the output differs between rising and falling input by up to {width:F3} (80 Hz, Drive 12 dB; includes the filter's phase)");
            Assert.That(width, Is.GreaterThan(0.001));
        }

        [Test]
        public void HardClipKneeGoesFromHardToSoft()
        {
            const int bin = 1365;
            double H5(int c)
            {
                var m = Spectrum(DriveMode.HardClip, Measure.BinFrequency(bin), 18, v => v.Set(P.Character, c), 0.25);
                return Level(m, bin, 5, 0.25) - Level(m, bin, 1, 0.25);
            }
            double hard = H5(0), soft = H5(1000);
            Measure.Report($"Neo Drive Hard clip: 5th harmonic {hard:F1} dB re the fundamental at the hard knee, {soft:F1} dB at the soft knee");
            Assert.That(soft, Is.LessThan(hard - 6));
        }

        [Test]
        public void OverdriveHumpFollowsCharacter()
        {
            // A small signal (clean): the gain at 500 Hz against 2 kHz swaps between Character 0 and 100 %.
            double Gain(int c, double hz)
            {
                int bin = (int)Math.Round(hz * Measure.N / Measure.Rate);
                var m = Spectrum(DriveMode.Overdrive, Measure.BinFrequency(bin), 0, v => { v.Set(P.Character, c); v.Set(P.Drive, 0); }, 0.01);
                return Level(m, bin, 1, 0.01);
            }
            double a500 = Gain(0, 500), a2k = Gain(0, 2000), b500 = Gain(1000, 500), b2k = Gain(1000, 2000);
            Measure.Report($"Neo Drive Overdrive hump: Character 0 % gives 500 Hz {a500:F1} dB, 2 kHz {a2k:F1} dB; 100 % gives 500 Hz {b500:F1} dB, 2 kHz {b2k:F1} dB");
            Assert.That(a500 - a2k, Is.GreaterThan(3));
            Assert.That(b2k - b500, Is.GreaterThan(3));
        }

        [Test]
        public void FuzzGatesOnDecaysAndPlainFuzzDoesNot()
        {
            // A decaying note: with Character at 100 % the tail is gated away, at 0 % it rings on.
            int total = 48000;
            var x = new float[total];
            for (int i = 0; i < total; i++) x[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 220 * i / 48000.0) * Math.Exp(-i / 6000.0));
            double Tail(int c)
            {
                var (e, _) = Make(v => { v.Set(P.Mode, (int)DriveMode.Fuzz); v.Set(P.Drive, DriveRaw(30)); v.Set(P.Character, c); v.Set(P.AutoGain, 0); });
                var y = Run(e, x);
                return Measure.Rms(y, 30000, 10000);
            }
            double plain = Tail(0), starved = Tail(1000);
            Measure.Report($"Neo Drive Fuzz tail of a decaying note (RMS 0.6 to 0.8 s): Character 0 % {plain:F4}, 100 % {starved:F4}");
            Assert.That(starved, Is.LessThan(plain * 0.5));
        }
    }
}
