using System.Text.Json.Nodes;
using NeoBuzz.Analysis;
using NeoBuzz.Eq;
using ReBuzz.Agent.Audio;
using static ReBuzz.AgentTests.Signals;

namespace ReBuzz.AgentTests
{
    /// <summary>
    /// The mastering measures (docs/specs/mastering-tools.md §6, M1) against constructed signals: microdynamics, the side/mid
    /// ratio in four bands, the tonal curve against a target, the platform preview, style targets, and eq_fit.
    /// </summary>
    [TestFixture]
    public class MasteringTests
    {
        static StereoAudio Stereo(float[] l, float[]? r = null) => new() { Left = l, Right = r ?? (float[])l.Clone(), Rate = Rate };

        static List<string> Reads(JsonNode m) => m["reads"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();

        // ---- Microdynamics ---------------------------------------------------------------------

        /// <summary>Noise bursts, 40 ms on in every 250 ms, over a quiet bed: what a punchy drum part looks like to LDR.</summary>
        static float[] Bursts(double seconds, int seed)
        {
            var x = Noise(0.2, seconds, seed);
            for (int i = 0; i < x.Length; i++)
            {
                double t = i / (double)Rate % 0.25;
                x[i] *= (float)(t < 0.04 ? 1 : 0.1);
            }
            return x;
        }

        /// <summary>A fast leveller (10:1 above −40 dBFS on a 5 ms RMS detector, no release tail to pump): it flattens bursts.</summary>
        static float[] Compress(float[] x)
        {
            var y = new float[x.Length];
            double env = 0, k = Math.Exp(-1 / (0.005 * Rate));
            for (int i = 0; i < x.Length; i++)
            {
                env = k * env + (1 - k) * x[i] * x[i];
                double db = 10 * Math.Log10(Math.Max(env, 1e-12)), over = Math.Max(0, db + 40);
                y[i] = (float)(x[i] * Math.Pow(10, -over * 0.9 / 20));
            }
            return y;
        }

        [Test]
        public void MicrodynamicsTellsBurstsFromSteadySound()
        {
            var steady = Microdynamics.Measure(Noise(0.1, 8, 1), Rate);
            var bursts = Microdynamics.Measure(Bursts(8, 1), Rate);
            TestContext.Out.WriteLine($"steady {steady.LdrDb:F2} dB (median {steady.MedianDb:F2}), bursts {bursts.LdrDb:F2} dB (median {bursts.MedianDb:F2})");
            foreach (var b in bursts.Bands) TestContext.Out.WriteLine($"  {b.Name}: {b.LdrDb:F2} dB at {b.LevelDb:F1} dB");
            // Steady noise: 20 ms frames of noise vary by a few tenths of a dB around their long level.
            Assert.That(steady.LdrDb, Is.InRange(0.0, 1.0));
            Assert.That(steady.MedianDb, Is.InRange(-0.5, 0.5));
            // Bursts 40 ms in 250, 20 dB over the bed: a 20 ms frame inside one is about 10·log10(1 / (0.16 + 0.84·0.01)) = 7.7 dB over the second.
            Assert.That(bursts.LdrDb, Is.EqualTo(7.7).Within(0.5));
            foreach (var b in bursts.Bands.Where(b => b.Hi > 200))
                Assert.That(b.LdrDb, Is.GreaterThan(6), $"{b.Name}: white noise bursts burst in every band");
            // The measure's floor: steady noise reads 3.2, 1.2, 0.5 and 0.4 dB in the four bands (a 20 ms frame holds fewer
            // independent cycles the lower the band, so its level varies more).
            Assert.That(steady.Bands.Select(b => b.LdrDb), Is.EqualTo(new[] { 3.2, 1.2, 0.5, 0.4 }).Within(0.3));
        }

        [Test]
        public void MicrodynamicsDoesNotMoveWithLevelAndFallsWithCompression()
        {
            var x = Bursts(8, 2);
            var loud = Microdynamics.Measure(x, Rate);
            var quiet = Microdynamics.Measure(Scale(x, 0.25), Rate);
            Assert.That(quiet.LdrDb, Is.EqualTo(loud.LdrDb).Within(1e-9), "a level change is not a dynamics change");
            for (int b = 0; b < loud.Bands.Length; b++)
                Assert.That(quiet.Bands[b].LdrDb, Is.EqualTo(loud.Bands[b].LdrDb).Within(1e-9), loud.Bands[b].Name);
            var squashed = Microdynamics.Measure(Compress(x), Rate);
            TestContext.Out.WriteLine($"LDR {loud.LdrDb:F2} dB, compressed {squashed.LdrDb:F2} dB");
            Assert.That(squashed.LdrDb, Is.LessThan(loud.LdrDb - 3), "compression lowers LDR");
        }

        [Test]
        public void MicrodynamicsIsPerBand()
        {
            // A kick every half second over steady noise above 2 kHz: punch in the lows, none in the highs.
            var hiss = Noise(0.05, 8, 3);
            var kick = Kick(8, 0.5, 0.5);
            var m = Microdynamics.Measure(Mix(kick, hiss), Rate);
            foreach (var b in m.Bands) TestContext.Out.WriteLine($"{b.Name}: LDR {b.LdrDb:F2} dB, level {b.LevelDb:F1} dB");
            Assert.That(m.Bands[0].LdrDb, Is.GreaterThan(4), "the kick's band");
            Assert.That(m.Bands[3].LdrDb, Is.LessThan(1), "steady noise above 10 kHz");

            var report = AudioReport.Measure(Stereo(Mix(kick, hiss)), new[] { "microdynamics" });
            var json = report["microdynamics"]!;
            Assert.That(json["bands"]!.AsObject().Select(p => p.Key), Is.EqualTo(new[] { "below_120", "120_2k", "2k_10k", "above_10k" }));
            Assert.That(json["bands"]!["above_10k"]!["hi_hz"], Is.Null, "open above");
            Assert.That(json["bands"]!["below_120"]!["ldr_db"]!.GetValue<double>(), Is.EqualTo(Math.Round(m.Bands[0].LdrDb, 1)));
            Assert.That(Reads(report), Has.Some.StartsWith("Microdynamics"));
        }

        // ---- Side/mid in four bands ------------------------------------------------------------

        [Test]
        public void SideToMidIsMeasuredInTheFourBands()
        {
            // Mono below 120 Hz (a 60 Hz sine in both), wide from 2 kHz (independent noise, high-passed by a crude difference),
            // and mid only between (a 500 Hz sine in both).
            int n = 8 * Rate;
            var low = Sine(60, 0.3, 8);
            var mid = Sine(500, 0.1, 8);
            static float[] Hp(float[] x)
            {
                // Four first differences: about 24 dB/oct below a few kHz, so the noise sits almost all above 2 kHz.
                var y = (float[])x.Clone();
                for (int k = 0; k < 4; k++)
                    for (int i = y.Length - 1; i > 0; i--) y[i] = 0.5f * (y[i] - y[i - 1]);
                return y;
            }
            var a = Hp(Noise(0.2, 8, 4));
            var b = Hp(Noise(0.2, 8, 5));
            var l = Mix(low, mid, a);
            var r = Mix(low, mid, b);
            Assert.That(l.Length, Is.EqualTo(n));
            var m = AudioReport.Measure(new StereoAudio { Left = l, Right = r, Rate = Rate }, new[] { "stereo" });
            var bands = m["stereo"]!["ms_bands"]!;
            TestContext.Out.WriteLine(bands.ToJsonString());
            Assert.That(bands.AsObject().Select(p => p.Key), Is.EqualTo(new[] { "below_120", "120_2k", "2k_10k", "above_10k" }));
            Assert.That(bands["below_120"]!["side_to_mid_db"]!.GetValue<double>(), Is.LessThan(-30), "mono lows");
            Assert.That(bands["2k_10k"]!["side_to_mid_db"]!.GetValue<double>(), Is.EqualTo(0).Within(0.5), "uncorrelated: side as loud as mid");
            Assert.That(bands["above_10k"]!["correlation"]!.GetValue<double>(), Is.EqualTo(0).Within(0.05));
            Assert.That(bands["120_2k"]!["side_to_mid_db"]!.GetValue<double>(), Is.LessThan(-6), "the 500 Hz sine is mono");

            // Mono: no side at all.
            var mono = AudioReport.Measure(Stereo(Noise(0.1, 4, 6)), new[] { "stereo" });
            Assert.That(mono["stereo"]!["ms_bands"]!["2k_10k"]!["side_to_mid_db"]!.GetValue<string>(), Is.EqualTo("-inf"));
        }

        // ---- Tonal balance ---------------------------------------------------------------------

        static StereoAudio Pinkish(double slope, double rms = 0.1, int seed = 5) => Stereo(PeriodicNoise(slope, rms, 12, seed: seed));

        [Test]
        public void TonalDifferenceOfASignalWithItselfIsZeroAndATiltIsRecovered()
        {
            var pink = new Analysed(Pinkish(-1));
            var self = Tonal.Difference(pink.Tonal, pink.Tonal);
            Assert.That(self.RmsDb, Is.EqualTo(0).Within(1e-9));
            Assert.That(self.OffsetDb, Is.EqualTo(0).Within(1e-9));

            // The same noise 12 dB quieter: level matching takes the offset out.
            var quiet = new Analysed(Pinkish(-1, 0.025));
            var level = Tonal.Difference(quiet.Tonal, pink.Tonal);
            Assert.That(level.OffsetDb, Is.EqualTo(-20 * Math.Log10(4)).Within(0.01), "the target, 12 dB louder, is moved down to match");
            Assert.That(level.RmsDb, Is.LessThan(0.01));

            // A brighter target, power per bin ∝ f^−0.8 against f^−1: the difference rises 2 dB a decade.
            var bright = new Analysed(Pinkish(-0.8));
            var d = Tonal.Difference(pink.Tonal, bright.Tonal);
            double At(double hz) => d.Db[Array.FindIndex(d.Hz, h => h >= hz * 0.99)];
            TestContext.Out.WriteLine($"diff at 100 Hz {At(100):F2}, 1 kHz {At(1000):F2}, 10 kHz {At(10000):F2}, RMS {d.RmsDb:F2} dB");
            Assert.That(At(10000) - At(100), Is.EqualTo(4).Within(0.15));
            Assert.That(At(1000) - At(100), Is.EqualTo(2).Within(0.1));
            Assert.That(d.SmoothedDb[Array.FindIndex(d.Hz, h => h >= 990)], Is.EqualTo(At(1000)).Within(0.05), "smoothing keeps a straight line");

            var m = MasteringReport.Measure(Pinkish(-1), new[] { "tonal" }, MasteringReport.TargetFrom("bright", Pinkish(-0.8)));
            var t = m["tonal"]!["target"]!;
            Assert.That(t["name"]!.GetValue<string>(), Is.EqualTo("bright"));
            Assert.That(t["diff_db"]!.AsArray().Count, Is.EqualTo(m["tonal"]!["hz"]!.AsArray().Count));
            Assert.That(t["most_off"]![0]!["hz"]!.GetValue<double>(), Is.GreaterThan(10000).Or.LessThan(80), "a tilt is most off at an end");
            Assert.That(Reads(m), Has.Some.StartsWith("Tonal balance against bright"));
            Assert.That(m["target"]!["name"]!.GetValue<string>(), Is.EqualTo("bright"));
        }

        [Test]
        public void TheTonalCurveCoversTheBandAtASixthOfAnOctave()
        {
            var c = new Analysed(Pinkish(-1)).Tonal;
            Assert.That(c.Hz[0], Is.EqualTo(20).Within(0.5));
            Assert.That(c.Hz[^1], Is.LessThanOrEqualTo(20000));
            Assert.That(c.Hz.Length, Is.EqualTo(60), "ten octaves at six points each");
            for (int i = 1; i < c.Hz.Length; i++) Assert.That(c.Hz[i] / c.Hz[i - 1], Is.EqualTo(Math.Pow(2, 1 / 6.0)).Within(1e-9));
            // Pink noise: equal power per sixth of an octave, so a flat curve.
            double lo = c.Db.Skip(6).Take(48).Min(), hi = c.Db.Skip(6).Take(48).Max();
            Assert.That(hi - lo, Is.LessThan(0.3), "pink is flat from 40 Hz to 10 kHz");
        }

        // ---- Streaming platforms ---------------------------------------------------------------

        [Test]
        public void PlatformPreviewFollowsSpotifysRules()
        {
            // Spotify's own example: a −20 LUFS master with a −5 dBTP true peak is raised to −16 LUFS, keeping 1 dB of headroom.
            var quiet = Platform.Preview(-20, -5, -5.3);
            var normal = quiet.Single(p => p.Service == "Spotify" && p.Mode == "normal");
            Assert.That(normal.GainDb, Is.EqualTo(4).Within(1e-9));
            Assert.That(normal.PlaysAtLufs, Is.EqualTo(-16).Within(1e-9));
            Assert.That(normal.TruePeakDb, Is.EqualTo(-1).Within(1e-9));
            var loud = quiet.Single(p => p.Service == "Spotify" && p.Mode == "loud");
            Assert.That(loud.GainDb, Is.EqualTo(9).Within(1e-9), "Loud raises all the way, into its limiter");
            Assert.That(loud.LimitedDb, Is.EqualTo(-5.3 + 9 + 1).Within(1e-9));
            Assert.That(quiet.Single(p => p.Service == "Spotify" && p.Mode == "quiet").GainDb, Is.EqualTo(1).Within(1e-9));
            Assert.That(quiet.Where(p => p.Service != "Spotify").Select(p => p.GainDb), Has.All.EqualTo(0), "the others only turn down");

            // A loud master is turned down everywhere.
            var hot = Platform.Preview(-8, -0.2, -0.3);
            Assert.That(hot.Single(p => p.Service == "Spotify" && p.Mode == "normal").GainDb, Is.EqualTo(-6).Within(1e-9));
            Assert.That(hot.Single(p => p.Service == "Apple Music").GainDb, Is.EqualTo(-8).Within(1e-9));
            Assert.That(hot.Select(p => p.LimitedDb), Has.All.EqualTo(0));
            Assert.That(Platform.Preview(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity), Is.Empty, "silence");
        }

        [Test]
        public void PlatformMetricReadsWhatTheServicesDo()
        {
            // Pink noise at about −10 LUFS: every service turns it down.
            var m = AudioReport.Measure(Pinkish(-1, 0.3), new[] { "loudness", "platform" });
            double lufs = m["loudness"]!["integrated_lufs"]!.GetValue<double>();
            var services = m["platform"]!["services"]!.AsArray();
            Assert.That(services.Select(s => s!["service"]!.GetValue<string>()).Distinct(),
                        Is.EqualTo(new[] { "Spotify", "Apple Music", "YouTube", "TIDAL", "Amazon Music", "Deezer" }));
            var spotify = services.First(s => s!["service"]!.GetValue<string>() == "Spotify" && s["mode"]!.GetValue<string>() == "normal")!;
            Assert.That(spotify["gain_db"]!.GetValue<double>(), Is.EqualTo(Math.Round(-14 - lufs, 1)).Within(0.051));
            Assert.That(spotify["source"]!.GetValue<string>(), Is.EqualTo("published"));
            Assert.That(Reads(m), Has.Some.Contains("On Spotify (normal) it plays"));
        }

        // ---- Style targets ---------------------------------------------------------------------

        [Test]
        public void AStyleTargetAveragesItsSourcesAndRoundTripsAsAFile()
        {
            var a = Pinkish(-1, 0.1, 5);
            var b = Pinkish(-0.9, 0.2, 7);
            var target = MasteringReport.Build("test style", new[] { ("a", a), ("b", b) });
            var t = target.Target;
            Assert.That(t.Sources.Select(s => s.Name), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(t.Lufs.Min, Is.LessThan(t.Lufs.Max - 5), "6 dB apart");
            Assert.That(t.Lufs.Mean, Is.EqualTo((t.Lufs.Min + t.Lufs.Max) / 2).Within(1e-9));
            // The curve is the mean of the two, each moved to 0 dB from 100 Hz to 10 kHz.
            var ca = Tonal.Normalise(new Analysed(a).Tonal);
            var cb = Tonal.Normalise(new Analysed(b).Tonal);
            for (int i = 0; i < t.Curve.Hz.Length; i += 7)
                Assert.That(t.Curve.Db[i], Is.EqualTo((ca.Db[i] + cb.Db[i]) / 2).Within(1e-6), $"{t.Curve.Hz[i]:F0} Hz");
            Assert.That(t.LdrDb.Length, Is.EqualTo(4));
            Assert.That(t.SideToMidDb.Select(s => s.Mean), Has.All.EqualTo(double.NegativeInfinity), "mono references: no side");

            string file = Path.Combine(Path.GetTempPath(), "neobuzz-mastering-tests", Guid.NewGuid().ToString("N"), "style" + MasteringReport.TargetExtension);
            try
            {
                target.Save(file);
                var back = MasteringReport.LoadTarget(file);
                Assert.That(back.ToJson().ToJsonString(), Is.EqualTo(target.ToJson().ToJsonString()), "saved and read back the same");
                Assert.That(back.Name, Is.EqualTo("test style"));
                Assert.That(back.Sources, Is.EqualTo(2));
                Assert.That(MasteringReport.Describe(back), Does.StartWith("test style: 2 source(s)"));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
            }
            Assert.Throws<FormatException>(() => StyleTarget.FromJson(new JsonObject { ["format"] = "something else" }));
        }

        [Test]
        public void MeasuringAgainstATargetReadsEveryMeasureAgainstIt()
        {
            var master = Stereo(Compress(Bursts(8, 8)));
            var reference = MasteringReport.TargetFrom("reference", Stereo(Bursts(8, 9)));
            var m = MasteringReport.Measure(master, AudioReport.Metrics, reference);
            var reads = Reads(m);
            TestContext.Out.WriteLine(string.Join("\n", reads));
            Assert.That(m["microdynamics"]!["bands"]!["2k_10k"]!["diff_db"]!.GetValue<double>(), Is.LessThan(-2), "the compressed master is flatter");
            Assert.That(reads, Has.Some.StartsWith("Against reference:"));
            Assert.That(reads, Has.Some.StartsWith("Loudness"));
            Assert.That(reads, Has.Some.StartsWith("PLR"));
            Assert.That(reads, Has.Some.StartsWith("Tonal balance against reference"));
            Assert.That(m["target"]!["sources"]!.AsArray().Single()!.GetValue<string>(), Is.EqualTo("reference"));
        }

        // ---- eq_fit ----------------------------------------------------------------------------

        static readonly double[] Grid = Enumerable.Range(0, 56).Select(i => 25 * Math.Pow(2, i / 6.0)).Where(f => f <= 16000).ToArray();

        static BandSettings Bell(double hz, double db, double q) => new() { Type = BandType.Bell, Hz = hz, GainDb = db, Q = q };

        [Test]
        public void EqFitRecoversKnownBands()
        {
            var truth = new List<BandSettings>
            {
                Bell(300, -3, 1.2),
                Bell(2500, 1.5, 2),
                new() { Type = BandType.HighShelf, Hz = 8000, GainDb = 2, Q = EqFit.ShelfQ },
            };
            var curve = EqFit.Model(Grid, truth, 48000);
            var fit = EqFit.Fit(Grid, curve, 48000, maxBands: 4, maxGainDb: 6);
            foreach (var b in fit.Bands) TestContext.Out.WriteLine($"{b.Type} {b.Hz:F0} Hz {b.GainDb:+0.00;-0.00} dB Q {b.Q:F2}");
            TestContext.Out.WriteLine($"RMS {fit.RmsBeforeDb:F2} → {fit.RmsAfterDb:F3} dB, worst {fit.WorstDb:F2} at {fit.WorstHz:F0} Hz");
            Assert.That(fit.RmsAfterDb, Is.LessThan(0.1), "a curve the bands can make is fitted within 0.1 dB RMS");
            Assert.That(Math.Abs(fit.WorstDb), Is.LessThan(0.3));
            var cut = fit.Bands.OrderBy(b => b.GainDb).First();
            Assert.That(cut.Hz, Is.EqualTo(300).Within(30));
            Assert.That(cut.GainDb, Is.EqualTo(-3).Within(0.3));
            Assert.That(fit.Bands.Select(b => b.Hz), Is.Ordered, "low to high");
        }

        [Test]
        public void EqFitClipsTheWantedChangeAndKeepsGainsInBounds()
        {
            // A 10 dB dip at 1 kHz, wanted at most ±3 dB: the fit follows the clipped curve, not the dip.
            var curve = EqFit.Model(Grid, new List<BandSettings> { Bell(1000, -10, 1) }, 48000);
            var r = MasteringReport.FitEq(Grid, curve, bands: 3, maxGainDb: 4, limitDb: 3, rate: 48000);
            TestContext.Out.WriteLine(r.ToJsonString());
            var wanted = r["curve"]!["wanted_db"]!.AsArray().Select(x => x!.GetValue<double>()).ToList();
            Assert.That(wanted.Min(), Is.EqualTo(-3).Within(1e-9));
            var bands = r["bands"]!.AsArray();
            Assert.That(bands.Count, Is.InRange(1, 3));
            Assert.That(bands.Select(b => Math.Abs(b!["gain_db"]!.GetValue<double>())), Has.All.LessThanOrEqualTo(4));
            Assert.That(r["rms_after_db"]!.GetValue<double>(), Is.LessThan(r["rms_before_db"]!.GetValue<double>() / 3));

            // A flat curve needs no band.
            var flat = MasteringReport.FitEq(Grid, new double[Grid.Length], bands: 4, maxGainDb: 4, limitDb: 3, rate: 48000);
            Assert.That(flat["bands"]!.AsArray(), Is.Empty);
        }

        [Test]
        public void EqFitFromAMasterAndATargetAndItsNeoEqChanges()
        {
            var r = MasteringReport.FitEq(Pinkish(-1), MasteringReport.TargetFrom("bright", Pinkish(-0.8)), bands: 4, maxGainDb: 4, limitDb: 3, rate: 48000);
            TestContext.Out.WriteLine(r.ToJsonString());
            var bands = r["bands"]!.AsArray();
            Assert.That(bands.Count, Is.InRange(1, 4));
            Assert.That(r["rms_after_db"]!.GetValue<double>(), Is.LessThan(0.5 * r["rms_before_db"]!.GetValue<double>()));
            Assert.That(r["target"]!.GetValue<string>(), Is.EqualTo("bright"));
            // Brighter wanted: the top band boosts.
            Assert.That(bands[^1]!["gain_db"]!.GetValue<double>(), Is.GreaterThan(0));

            var changes = MasteringReport.NeoEqChanges(bands);
            var byParam = changes.ToDictionary(c => c!["param"]!.GetValue<string>(), c => c!["value"]!.ToJsonString());
            Assert.That(byParam["Mode"], Is.EqualTo("\"Parametric\""));
            Assert.That(byParam["B2 on"], Is.EqualTo("\"On\""));
            Assert.That(byParam["B2 freq"], Does.EndWith(" Hz\""));
            Assert.That(byParam["B2 gain"], Does.EndWith(" dB\""));
            Assert.That(byParam["B7 gain"], Is.EqualTo(bands.Count == 6 ? byParam["B7 gain"] : "\"0 dB\""), "the bands left over are transparent");
            // v2 (M3): the bands written are plain stereo bands, as the fit assumes.
            foreach (string b in new[] { "B2", "B7" })
            {
                Assert.That(byParam[$"{b} channel"], Is.EqualTo("\"Stereo\""));
                Assert.That(byParam[$"{b} shape"], Is.EqualTo("\"Standard\""));
                Assert.That(byParam[$"{b} dynamic"], Is.EqualTo("\"Off\""));
            }
            Assert.That(byParam.Keys, Has.None.StartsWith("B1 "), "the cuts are left alone");
            Assert.That(byParam.Keys, Has.None.StartsWith("B8 "));
        }
    }
}
