using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>Shared DSP library (docs/specs/machines.md §4): measured against what it promises.</summary>
    [TestFixture]
    public class DspTests
    {
        // ── parameters ──────────────────────────────────────────────────────

        [Test]
        public void ParamMappingsRoundTripAndDescribe()
        {
            var freq = new ParamDef(0, "Cut", "Cutoff", 0, 1000, 500, MapKind.Freq, a: 20, b: 20000);
            Assert.That(freq.Value(0), Is.EqualTo(20).Within(1e-9));
            Assert.That(freq.Value(1000), Is.EqualTo(20000).Within(1e-6));
            Assert.That(freq.Value(500), Is.EqualTo(Math.Sqrt(20 * 20000.0)).Within(1e-6));
            Assert.That(freq.Raw(1000.0), Is.EqualTo(freq.Raw(freq.Value(freq.Raw(1000.0)))));
            Assert.That(freq.Describe(1000), Is.EqualTo("20.0 kHz"));
            Assert.That(freq.Describe(0), Is.EqualTo("20.0 Hz"));

            var db = new ParamDef(1, "Out", "Output", 0, 1000, 500, MapKind.Db, a: -24, b: 24);
            Assert.That(db.Describe(500), Is.EqualTo("0.0"  + " dB"));
            Assert.That(db.Describe(1000), Is.EqualTo("+24.0 dB"));
            Assert.That(db.Describe(0), Is.EqualTo("−24.0 dB"));

            var inf = new ParamDef(2, "Lvl", "Level", 0, 100, 100, MapKind.Db, a: -60, b: 0, flag: true);
            Assert.That(double.IsNegativeInfinity(inf.Value(0)), Is.True);
            Assert.That(inf.Describe(0), Is.EqualTo("−∞ dB"));

            var semis = new ParamDef(3, "Tr", "Transpose", 0, 48, 24, MapKind.Int, a: -24, unit: "st", flag: true);
            Assert.That(semis.Value(24), Is.EqualTo(0));
            Assert.That(semis.Describe(31), Is.EqualTo("+7 st"));
            Assert.That(semis.Describe(12), Is.EqualTo("−12 st"));
            Assert.That(semis.Raw(-5), Is.EqualTo(19));

            var mode = new ParamDef(4, "M", "Mode", 0, 2, 0, MapKind.Enum, names: new[] { "A", "B", "C" });
            Assert.That(mode.Describe(2), Is.EqualTo("C"));

            var time = new ParamDef(5, "T", "Time", 0, 1000, 0, MapKind.Time, a: 0.001, b: 10);
            Assert.That(time.Describe(0), Is.EqualTo("1.00 ms"));
            Assert.That(time.Describe(1000), Is.EqualTo("10.0 s"));

            var ratio = new ParamDef(6, "R", "Ratio", 0, 100, 0, MapKind.Ratio, a: 1, b: 20, flag: true);
            Assert.That(ratio.Describe(0), Is.EqualTo("1.0:1"));
            Assert.That(ratio.Describe(100), Is.EqualTo("∞:1"));

            var bip = new ParamDef(7, "B", "Bal", 0, 200, 100, MapKind.Bipolar);
            Assert.That(bip.Describe(100), Is.EqualTo("0%"));
            Assert.That(bip.Describe(150), Is.EqualTo("+50%"));
            Assert.That(bip.Describe(0), Is.EqualTo("−100%"));
        }

        [Test]
        public void ParamValuesTrackChanges()
        {
            var table = new[]
            {
                new ParamDef(0, "A", "A", 0, 10, 5, MapKind.Linear, a: 0, b: 1),
                new ParamDef(1, "B", "B", 0, 10, 0, MapKind.Linear, a: 0, b: 1),
            };
            var v = new ParamValues(table);
            Assert.That(v.AnyDirty, Is.True);
            v.Clean();
            Assert.That(v.AnyDirty, Is.False);
            v.Set(1, 99);
            Assert.That(v[1], Is.EqualTo(10), "clamped");
            Assert.That(v.IsDirty(1), Is.True);
            Assert.That(v.IsDirty(0), Is.False);
        }

        // ── resampling ──────────────────────────────────────────────────────

        static double DbOf(double m) => 20 * Math.Log10(Math.Max(1e-30, m));

        [TestCase(24, 120.0, 20.0 / 96, 28.0 / 96)]
        [TestCase(11, 125.0, 28.0 / 192, 68.0 / 192)]
        [TestCase(8, 125.0, 28.0 / 384, 164.0 / 384)]
        public void FirHalfbandStagesMeetTheirBands(int k, double attenuation, double passEdge, double stopEdge)
        {
            var d = new FirHalfbandDesign(k, attenuation);
            double ripple = 0, stop = -999;
            for (double f = 0; f <= passEdge; f += passEdge / 200) ripple = Math.Max(ripple, Math.Abs(DbOf(d.Magnitude(f))));
            for (double f = stopEdge; f <= 0.5; f += (0.5 - stopEdge) / 400) stop = Math.Max(stop, DbOf(d.Magnitude(f)));
            TestContext.Out.WriteLine($"K={k}: passband ripple {ripple:F4} dB, stopband {stop:F1} dB");
            Assert.That(ripple, Is.LessThan(0.01));
            Assert.That(stop, Is.LessThan(-115));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(8)]
        public void OversamplerHasItsIntegerLatencyAndUnityGain(int factor)
        {
            var os = new FirOversampler(256);
            os.SetFactor(factor);
            int latency = FirOversampler.LatencyFor(factor);
            // Impulse: the output peaks exactly at the latency and is symmetric around it (linear phase).
            var x = new float[512];
            x[0] = 1;
            var y = new float[512];
            for (int b = 0; b < 2; b++)
            {
                var hi = os.Up(x, b * 256, 256);
                os.Down(y, b * 256, 256);
            }
            int peak = 0;
            for (int i = 1; i < y.Length; i++) if (Math.Abs(y[i]) > Math.Abs(y[peak])) peak = i;
            TestContext.Out.WriteLine($"{factor}×: latency {latency}, impulse peak at {peak} ({y[peak]:F4})");
            Assert.That(peak, Is.EqualTo(latency));
            for (int j = 1; j < 20 && peak - j >= 0; j++)
                Assert.That(y[peak - j], Is.EqualTo(y[peak + j]).Within(1e-5), $"symmetric at ±{j}");

            // 1 kHz sine passes at unity.
            os.Reset();
            var s = Measure.Sine(1000, 9984, 0.5);
            var o = new float[9984];
            for (int b = 0; b < 39; b++)
            {
                os.Up(s, b * 256, 256);
                os.Down(o, b * 256, 256);
            }
            // Whole periods (48 samples each) so the RMS doesn't depend on the latency.
            double gain = Measure.Rms(o, 4800, 4800) / Measure.Rms(s, 4800, 4800);
            Assert.That(DbOf(gain), Is.InRange(-0.01, 0.01));
        }

        [Test]
        public void IirHalfbandDecimatorAttenuatesItsStopband()
        {
            var design = new IirHalfbandDesign(100, (28.0 - 20.0) / 96);
            TestContext.Out.WriteLine($"{design.Coefs.Length} coefficients");
            double worstStop = -999, worstPass = 0;
            foreach (double fHigh in new[] { 1000.0, 10000, 18000, 20000, 28500, 32000, 40000, 47000 })
            {
                var dec = new IirHalfband(design);
                int n = 16384;
                var hi = Measure.Sine(fHigh, 2 * n, 1, 96000);
                var lo = new float[n];
                for (int i = 0; i < n; i++) lo[i] = dec.Down(hi[2 * i], hi[2 * i + 1]);
                double g = Measure.Rms(lo, n / 2, n / 2) / Math.Sqrt(0.5);
                if (fHigh <= 20000) worstPass = Math.Max(worstPass, Math.Abs(DbOf(g)));
                else worstStop = Math.Max(worstStop, DbOf(g));
            }
            TestContext.Out.WriteLine($"passband deviation {worstPass:F3} dB, stopband {worstStop:F1} dB");
            Assert.That(worstPass, Is.LessThan(0.1));
            Assert.That(worstStop, Is.LessThan(-95));
        }

        // ── oscillators ─────────────────────────────────────────────────────

        static float[] RenderOsc(BlepOscillator osc, double hz, int length, int rate = Measure.Rate)
        {
            var y = new float[length];
            double inc = hz / rate;
            for (int i = 0; i < length; i++) y[i] = osc.Next(inc, out _, out _);
            return y;
        }

        const int Warm = 4096;

        [TestCase(2.0f, 0.5f, -80.0)]   // saw
        [TestCase(3.0f, 0.5f, -80.0)]   // square
        [TestCase(3.0f, 0.23f, -80.0)]  // pulse
        [TestCase(1.0f, 0.5f, -90.0)]   // triangle
        [TestCase(2.5f, 0.5f, -80.0)]   // saw/square mix
        public void BlepOscillatorAliasFloor(float shape, float pw, double limit)
        {
            const int bin = 2349;   // 1720.5 Hz
            var osc = new BlepOscillator();
            osc.SetShape(shape, pw);
            var y = RenderOsc(osc, Measure.BinFrequency(bin), Warm + Measure.N);
            var mag = Measure.Spectrum(y, Warm);
            double floor = Measure.AliasFloorDb(mag, bin);
            Measure.Report($"BLEP oscillator, shape {shape} pw {pw} at 1.72 kHz: alias floor {floor:F1} dB");
            Assert.That(floor, Is.LessThan(limit));
        }

        [Test]
        public void BlepSawHasTheRightHarmonics()
        {
            const int bin = 2349;
            var osc = new BlepOscillator();
            osc.SetShape(2, 0.5f);
            var y = RenderOsc(osc, Measure.BinFrequency(bin), Warm + Measure.N);
            var mag = Measure.Spectrum(y, Warm);
            // Ideal saw: harmonic h at 1/h of the fundamental (2/π·1/h amplitude).
            Assert.That(mag[bin], Is.EqualTo(2 / Math.PI).Within(0.01));
            for (int h = 2; h <= 6; h++)
                Assert.That(Measure.HarmonicDb(mag, bin, h), Is.EqualTo(-20 * Math.Log10(h)).Within(0.1), $"harmonic {h}");
        }

        [Test]
        public void HardSyncAliasFloor()
        {
            const int bin = 1173;   // master ~859 Hz
            var master = new BlepOscillator();
            var slave = new BlepOscillator();
            master.SetShape(2, 0.5f);
            slave.SetShape(2, 0.5f);
            double incM = Measure.BinFrequency(bin) / Measure.Rate;
            double incS = incM * 2.37;
            var y = new float[Warm + Measure.N];
            for (int i = 0; i < y.Length; i++)
            {
                master.Next(incM, out bool wrapped, out double ago);
                y[i] = slave.NextSynced(incS, wrapped, ago);
            }
            var mag = Measure.Spectrum(y, Warm);
            double floor = Measure.AliasFloorDb(mag, bin);
            Measure.Report($"BLEP hard sync: alias floor {floor:F1} dB");
            Assert.That(floor, Is.LessThan(-70));
        }

        [Test]
        public void ThroughZeroFmStaysBounded()
        {
            var carrier = new BlepOscillator();
            var mod = new BlepOscillator();
            carrier.SetShape(2, 0.5f);
            mod.SetShape(0, 0.5f);
            double peak = 0;
            for (int i = 0; i < 48000; i++)
            {
                mod.Next(220.0 / 48000, out _, out _);
                double inc = 440.0 / 48000 * (1 + 3.0 * mod.NaiveValue);   // goes negative
                float v = carrier.Next(inc, out _, out _);
                Assert.That(float.IsFinite(v), Is.True);
                peak = Math.Max(peak, Math.Abs(v));
            }
            Assert.That(peak, Is.LessThan(1.3));
        }

        // ── filters ─────────────────────────────────────────────────────────

        [TestCase(100.0)]
        [TestCase(1000.0)]
        [TestCase(10000.0)]
        [TestCase(15000.0)]
        public void SvfLowpassCutoffIsExact(double hz)
        {
            var f = new Svf { Mode = SvfMode.LowPass };
            f.Set(hz, Measure.Rate, Math.Sqrt(0.5));
            var x = Measure.Sine(hz, 48000);
            var y = new float[x.Length];
            for (int i = 0; i < x.Length; i++) y[i] = f.Process(x[i]);
            double g = Measure.Rms(y, 24000, 24000) / Measure.Rms(x, 24000, 24000);
            TestContext.Out.WriteLine($"{hz} Hz: {DbOf(g):F3} dB");
            // −3.01 dB ± the gain change a 2 % frequency error would make (~0.1 dB at this slope).
            Assert.That(DbOf(g), Is.EqualTo(-3.0103).Within(0.12));
        }

        [TestCase(100.0)]
        [TestCase(440.0)]
        [TestCase(1000.0)]
        [TestCase(5000.0)]
        public void LadderSelfOscillatesAtTheCutoff(double hz)
        {
            var f = new Ladder();
            f.Set(Svf.Prewarp((float)hz, Measure.Rate), 1f);
            int n = 65536;
            var y = new float[n + 48000];
            for (int i = 0; i < y.Length; i++) y[i] = f.Process(i == 0 ? 1f : 0f, 1f, false);
            double freq = Measure.PeakFrequency(y, 48000, n);
            double cents = Measure.Cents(freq, hz);
            double level = Measure.Peak(y, 48000, n);
            Measure.Report($"Ladder self-oscillation at {hz} Hz: {Math.Round(cents, 1):+0.0;−0.0;0.0} cents");
            Assert.That(Math.Abs(cents), Is.LessThan(25));
            Assert.That(level, Is.InRange(0.1, 1.0));
        }

        [Test]
        public void LadderLowpassSlopeIs24DbPerOctave()
        {
            double At(double hz)
            {
                var l = new Ladder();
                l.Set(Svf.Prewarp(200, Measure.Rate), 0f);
                var x = Measure.Sine(hz, 48000, 0.01);
                var y = new float[x.Length];
                for (int i = 0; i < x.Length; i++) y[i] = l.Process(x[i], 1f, false);
                return DbOf(Measure.Rms(y, 24000, 24000) / Measure.Rms(x, 24000, 24000));
            }
            double slope = At(1600) - At(3200);
            TestContext.Out.WriteLine($"1.6 k → 3.2 k: {slope:F1} dB");
            Assert.That(slope, Is.InRange(22.0, 25.5));
            Assert.That(At(20), Is.InRange(-0.2, 0.2));
        }

        // ── envelopes ───────────────────────────────────────────────────────

        [TestCase(0.01f)]
        [TestCase(0.1f)]
        [TestCase(1.0f)]
        public void AdsrAttackTakesItsTime(float attack)
        {
            var env = new Adsr();
            env.SetSampleRate(Measure.Rate);
            env.Set(attack, 1, 1, 1);
            env.Gate(true);
            int n = 0;
            while (env.Stage == EnvStage.Attack && n < 10 * Measure.Rate) { env.Next(); n++; }
            double t = n / (double)Measure.Rate;
            Assert.That(t / attack, Is.InRange(0.95, 1.05));
        }

        [TestCase(0.05f)]
        [TestCase(0.5f)]
        [TestCase(3.0f)]
        public void AdsrReleaseFalls60DbInItsTime(float release)
        {
            var env = new Adsr();
            env.SetSampleRate(Measure.Rate);
            env.Set(0, 0.1f, 1, release);
            env.Gate(true);
            for (int i = 0; i < 1000; i++) env.Next();
            env.Gate(false);
            int n = 0;
            while (env.Level > 0.001f && n < 20 * Measure.Rate) { env.Next(); n++; }
            double t = n / (double)Measure.Rate;
            Assert.That(t / release, Is.InRange(0.95, 1.05));
        }

        [Test]
        public void AdsrRetriggerContinuesFromItsLevel()
        {
            var env = new Adsr();
            env.SetSampleRate(Measure.Rate);
            env.Set(0.05f, 0.2f, 0.5f, 0.3f);
            env.Gate(true);
            for (int i = 0; i < 4800; i++) env.Next();
            env.Gate(false);
            for (int i = 0; i < 1000; i++) env.Next();
            float before = env.Level;
            env.Gate(true);
            float after = env.Next();
            Assert.That(Math.Abs(after - before), Is.LessThan(0.01));
        }

        // ── voices ──────────────────────────────────────────────────────────

        sealed class FakeVoice : IVoice
        {
            public bool Held, Active;
            public float Note, Glide = float.NaN;
            public int Unison;
            public int Starts;
            public bool IsActive => Active;
            public float Loudness => Active ? (Held ? 1f : 0.5f) : 0f;
            public void NoteOn(float note, float velocity, int unisonIndex, int unisonCount, float glideFrom)
            {
                Note = note; Unison = unisonIndex; Held = true; Active = true; Glide = glideFrom; Starts++;
            }
            public void Legato(float note, bool glide) { Note = note; }
            public void NoteOff() { Held = false; }
            public void Kill() { Held = false; Active = false; }
        }

        [Test]
        public void PolyAllocationReleasesTheOldestBeyondPolyphony()
        {
            var v = Enumerable.Range(0, 8).Select(_ => new FakeVoice()).ToArray();
            var a = new VoiceAllocator(v) { Polyphony = 4 };
            for (int k = 0; k < 6; k++) a.NoteOn(k, 60 + k, 1);
            Assert.That(v.Count(x => x.Held), Is.EqualTo(4));
            Assert.That(v.Where(x => x.Held).Select(x => (int)x.Note).OrderBy(x => x).ToArray(), Is.EqualTo(new[] { 62, 63, 64, 65 }));
            a.NoteOff(5);
            Assert.That(v.Count(x => x.Held), Is.EqualTo(3));
        }

        [Test]
        public void PolyUnisonUsesVoicesPerNoteAndStealsReleasedFirst()
        {
            var v = Enumerable.Range(0, 8).Select(_ => new FakeVoice()).ToArray();
            var a = new VoiceAllocator(v) { Polyphony = 8, Unison = 3 };
            a.NoteOn(1, 60, 1);
            a.NoteOn(2, 64, 1);
            Assert.That(v.Count(x => x.Held), Is.EqualTo(6));
            a.NoteOff(1);   // three released voices still sounding
            a.NoteOn(3, 67, 1);
            // Two free voices plus one stolen released voice; note 64 keeps its three.
            Assert.That(v.Count(x => x.Held && x.Note == 64), Is.EqualTo(3));
            Assert.That(v.Count(x => x.Held && x.Note == 67), Is.EqualTo(3));
            Assert.That(v.Select(x => x.Unison).Where((u, i) => v[i].Note == 67).OrderBy(u => u).ToArray(), Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void SameKeyReleasesItsPreviousNote()
        {
            var v = Enumerable.Range(0, 4).Select(_ => new FakeVoice()).ToArray();
            var a = new VoiceAllocator(v) { Polyphony = 4 };
            a.NoteOn(0, 60, 1);
            a.NoteOn(0, 62, 1);
            Assert.That(v.Count(x => x.Held), Is.EqualTo(1));
            Assert.That(v.Single(x => x.Held).Note, Is.EqualTo(62));
        }

        [Test]
        public void LegatoKeepsTheNoteStack()
        {
            var v = Enumerable.Range(0, 4).Select(_ => new FakeVoice()).ToArray();
            var a = new VoiceAllocator(v) { Mode = VoiceMode.Legato };
            a.NoteOn(1, 60, 1);
            a.NoteOn(2, 64, 1);
            Assert.That(v[0].Starts, Is.EqualTo(1), "legato: no retrigger");
            Assert.That(v[0].Note, Is.EqualTo(64));
            a.NoteOff(2);
            Assert.That(v[0].Note, Is.EqualTo(60), "back to the held note");
            Assert.That(v[0].Held, Is.True);
            a.NoteOff(1);
            Assert.That(v[0].Held, Is.False);
        }

        [Test]
        public void SustainPedalHoldsReleasedNotes()
        {
            var v = Enumerable.Range(0, 4).Select(_ => new FakeVoice()).ToArray();
            var a = new VoiceAllocator(v) { Polyphony = 4 };
            a.Sustain(true);
            a.NoteOn(1, 60, 1);
            a.NoteOff(1);
            Assert.That(v.Count(x => x.Held), Is.EqualTo(1));
            a.Sustain(false);
            Assert.That(v.Count(x => x.Held), Is.EqualTo(0));
        }

        [Test]
        public void GlideStartsFromTheLastNote()
        {
            var v = Enumerable.Range(0, 4).Select(_ => new FakeVoice()).ToArray();
            var a = new VoiceAllocator(v) { Polyphony = 4, GlideAlways = true };
            a.NoteOn(1, 60, 1);
            a.NoteOn(2, 67, 1);
            Assert.That(v.Single(x => x.Note == 67).Glide, Is.EqualTo(60));
        }

        // ── wavetables ──────────────────────────────────────────────────────

        [Test]
        public void WavetableLevelsAreBandLimited()
        {
            var saw = Wavetable.FromHarmonics("saw", 1, (f, h) => (1.0 / h, 0));
            for (int level = 0; level < Wavetable.Levels; level++)
            {
                var s = saw.FrameSamples(0, level);
                var mag = Fft.Magnitudes(s, 0, s.Length);
                int top = Wavetable.MaxHarmonic(level);
                double above = 0;
                for (int k = top + 1; k < mag.Length; k++) above = Math.Max(above, mag[k]);
                Assert.That(DbOf(above / mag[1]), Is.LessThan(-120), $"level {level}");
                Assert.That(mag[Math.Min(top, mag.Length - 1)], Is.GreaterThan(0), $"level {level} keeps harmonic {top}");
            }
        }

        [Test]
        public void WavetableLevelChoiceKeepsHarmonicsUnderTheLimit()
        {
            double limit = (96000 - 20000) / 96000.0;
            foreach (double hz in new[] { 20.0, 55, 440, 3000, 9000 })
            {
                double inc = hz / 96000;
                int level = Wavetable.LevelFor(inc, limit);
                Assert.That(Wavetable.MaxHarmonic(level) * inc, Is.LessThan(limit + 1e-12), $"{hz} Hz");
                if (level > 0)
                    Assert.That(Wavetable.MaxHarmonic(level - 1) * inc, Is.GreaterThan(limit), $"{hz} Hz takes the richest level allowed");
            }
        }

        [Test]
        public void WavetableFrameMorphIsContinuous()
        {
            var t = Wavetable.FromHarmonics("sweep", 8, (f, h) => (h <= 1 + f * 4 ? 1.0 / h : 0, 0));
            float prev = t.Read(0, 0.1, 3);
            double maxStep = 0;
            for (int i = 1; i <= 7000; i++)
            {
                float v = t.Read(i / 1000f, 0.1, 3);
                maxStep = Math.Max(maxStep, Math.Abs(v - prev));
                prev = v;
            }
            Assert.That(maxStep, Is.LessThan(0.01));
        }
    }
}
