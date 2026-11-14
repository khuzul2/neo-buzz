using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.MachineTests
{
    /// <summary>Dye Essence P5b (docs/specs/dye-essence.md §4.13): partials imported into the Harmonic engine and modes into the Modal engine from the Spectral engine's analysis.</summary>
    [TestFixture]
    public class DyeEssenceImportTests
    {
        const int Rate = Measure.Rate;

        // ---- Signals with known content.

        static float[] Tone(double[] hz, double[] amp, double seconds, int rate = 48000)
        {
            var x = new float[(int)(seconds * rate)];
            for (int k = 0; k < hz.Length; k++)
                for (int i = 0; i < x.Length; i++) x[i] += (float)(amp[k] * Math.Sin(2 * Math.PI * hz[k] * i / rate + 1.3 * k));
            return x;
        }

        /// <summary>The partials 1 … <paramref name="n"/> of 220 Hz at 1/k.</summary>
        static float[] Saw(int n, double seconds)
        {
            var hz = new double[n];
            var amp = new double[n];
            for (int k = 0; k < n; k++) { hz[k] = 220.0 * (k + 1); amp[k] = 0.4 / (k + 1); }
            return Tone(hz, amp, seconds);
        }

        // An inharmonic tone: a free bar's partials (1, 2.757, 5.404, 8.933, 13.344) over 220 Hz (the analysis tells partials apart from about 9 bins, 210 Hz, on).
        static readonly double[] BarRatios = { 1, 2.757, 5.404, 8.933, 13.344 };
        static readonly double[] BarAmps = { 0.40, 0.30, 0.22, 0.15, 0.10 };

        // A synthetic impulse response: decaying sinusoids with known frequency, T60 and amplitude (gain).
        static readonly double[] ModeHz = { 310, 523, 877, 1450, 2310 }, ModeT60 = { 1.6, 1.1, 0.8, 0.5, 0.35 }, ModeGain = { 1.0, 0.7, 0.5, 0.3, 0.2 };

        static float[] Ir(double seconds, int rate = 48000)
        {
            var x = new float[(int)(seconds * rate)];
            for (int k = 0; k < ModeHz.Length; k++)
            {
                double sigma = 6.908 / ModeT60[k];
                for (int i = 0; i < x.Length; i++) x[i] += (float)(0.2 * ModeGain[k] * Math.Exp(-sigma * i / rate) * Math.Sin(2 * Math.PI * ModeHz[k] * i / rate + 0.7 * k));
            }
            return x;
        }

        static SpectralModel Model(float[] x) => SpectralAnalysis.Analyse(x, 48000);

        static double Cents(double a, double b) => 1200 * Math.Log2(a / b);
        static double Db(double v) => 20 * Math.Log10(Math.Max(1e-12, v));

        // ---- Banks.

        static NoteBank Bank(SlotType type, SpectralModel m, LaneKind kind = LaneKind.Auto, Action<OscSettings> tweak = null)
        {
            var b = new NoteBank(kind);
            b.S.Oversample = 2;
            b.SetSampleRate(Rate);
            var o = b.S.Osc[0];
            o.Type = type;
            o.Spectrum = m;
            o.Random = 0;
            o.Phase = 0;
            o.Partials = 64;
            b.S.Osc[1].Type = b.S.Osc[2].Type = SlotType.Off;
            b.S.Filter[0].Type = b.S.Filter[1].Type = FilterType.Off;
            b.S.Amp.A = 0.001f;
            b.S.Amp.S = 1;
            b.S.Volume = 1;
            b.S.VelAmp = 0;
            tweak?.Invoke(o);
            return b;
        }

        static float[] Render(NoteBank b, int frames, int block = 256)
        {
            var y = new float[2 * frames];
            var l = new float[NoteBank.MaxBlock];
            var r = new float[NoteBank.MaxBlock];
            for (int pos = 0, n; pos < frames; pos += n)
            {
                n = Math.Min(block, frames - pos);
                b.Process(l, r, n);
                for (int i = 0; i < n; i++) { y[2 * (pos + i)] = l[i]; y[2 * (pos + i) + 1] = r[i]; }
            }
            return y;
        }

        static float[] Mono(float[] stereo)
        {
            var y = new float[stereo.Length / 2];
            for (int i = 0; i < y.Length; i++) y[i] = (stereo[2 * i] + stereo[2 * i + 1]) * 0.5f;
            return y;
        }

        static float[] Harm(SpectralModel m, double seconds, int key, Action<OscSettings> tweak = null)
        {
            var b = Bank(SlotType.Harmonic, m, tweak: o => { o.ImpOn = true; tweak?.Invoke(o); });
            b.NoteOn(0, key, 1f);
            return Mono(Render(b, (int)(seconds * Rate)));
        }

        static float[] Modal(SpectralModel m, double seconds, int key, Action<OscSettings> tweak = null, LaneKind kind = LaneKind.Auto)
        {
            var b = Bank(SlotType.Modal, m, kind, o => { o.ModeOn = true; o.Exciter = (int)ModalExciter.Plucked; o.Modes = 64; o.ModeLen = 1f; tweak?.Invoke(o); });
            b.NoteOn(0, key, 1f);
            return Mono(Render(b, (int)(seconds * Rate)));
        }

        // ---- Harmonic: import partials.

        [Test]
        public void AKnownHarmonicTonesPartialsComeBackAsTheTable()
        {
            var t = new ImportedPartials();
            t.Make(Model(Saw(12, 1.5)), 0.5f, 0f);
            double worstCents = 0, worstDb = 0;
            Assert.That(t.Count, Is.EqualTo(12));
            for (int k = 0; k < 12; k++)
            {
                worstCents = Math.Max(worstCents, Math.Abs(Cents(t.Ratio[k], k + 1)));
                worstDb = Math.Max(worstDb, Math.Abs(Db(t.Level[k] / t.Level[0]) - Db(1.0 / (k + 1))));
            }
            Measure.Report($"Dye Essence import partials, a steady 12-partial tone at 1/k: table of {t.Count} rows, reference {t.Reference:0.00} Hz, ratios within {worstCents:0.000} cent, levels within {worstDb:0.000} dB (targets 1 cent, 0.5 dB)");
            Assert.That(t.Reference, Is.EqualTo(220.0).Within(0.1));
            Assert.That(worstCents, Is.LessThan(1.0));
            Assert.That(worstDb, Is.LessThan(0.5));
        }

        [Test]
        public void TheHarmonicEngineSoundsTheImportedPartialsAtTheirLevelsAndPlaces()
        {
            var m = Model(Saw(12, 1.5));
            foreach (int key in new[] { 57, 64, 69 })
            {
                var y = Harm(m, 1.5, key);
                // What it plays, read with the same analysis: its partials against the reference pitch of the note.
                var got = new ImportedPartials();
                var rm = Model(y.AsSpan(24000, 48000).ToArray());
                got.Make(rm, 0.5f, 0f);
                double hz = 440.0 * Math.Pow(2, (key - 69) / 12.0);
                double worstCents = 0, worstDb = 0;
                Assert.That(got.Count, Is.GreaterThanOrEqualTo(8), $"key {key}");
                for (int k = 0; k < Math.Min(got.Count, 12); k++)
                {
                    if (hz * (k + 1) > 0.45 * Rate) break;
                    worstCents = Math.Max(worstCents, Math.Abs(Cents(got.Ratio[k] * got.Reference, hz * (k + 1))));
                    worstDb = Math.Max(worstDb, Math.Abs(Db(got.Level[k] / got.Level[0]) - Db(1.0 / (k + 1))));
                }
                Measure.Report($"Dye Essence Harmonic engine with the imported partials, key {key} ({hz:0.0} Hz): {got.Count} partials read back, places within {worstCents:0.000} cent, levels within {worstDb:0.00} dB of 1/k");
                Assert.That(worstCents, Is.LessThan(1.0), $"key {key} places");
                Assert.That(worstDb, Is.LessThan(0.5), $"key {key} levels");
            }
        }

        [Test]
        public void AnInharmonicTonesSpacingIsKeptAndTheRatiosControlBlendsItToWholeMultiples()
        {
            var hz = BarRatios.Select(r => 220 * r).ToArray();
            var m = Model(Tone(hz, BarAmps, 1.5));
            var t = new ImportedPartials();
            t.Make(m, 0.5f, 0f);
            Assert.That(t.Count, Is.EqualTo(5));
            double worst = 0;
            for (int j = 0; j < 5; j++) worst = Math.Max(worst, Math.Abs(Cents(t.Ratio[j] * t.Reference, hz[j])));
            Measure.Report($"Dye Essence import partials, a free bar's partials (ratios 1, 2.757, 5.404, 8.933, 13.344): table within {worst:0.000} cent of the true frequencies");
            Assert.That(worst, Is.LessThan(1.0));
            // As analysed (100 %): the engine sounds them where they are, against the note. Reference here is the fundamental's 220 Hz (key 57).
            var y = Harm(m, 1.5, 57);
            var got = new ImportedPartials();
            got.Make(Model(y.AsSpan(24000, 48000).ToArray()), 0.5f, 0f);
            double w2 = 0;
            for (int j = 0; j < 5; j++) w2 = Math.Max(w2, Math.Abs(Cents(got.Ratio[j] * got.Reference, hz[j])));
            Measure.Report($"  played at its own pitch, 100 % ratios: within {w2:0.000} cent");
            Assert.That(w2, Is.LessThan(1.0));
            // 0 %: the rows at the whole multiples of the note (row j at (j + 1) times 220 Hz).
            var y0 = Harm(m, 1.5, 57, o => o.ImpRatio = 0f);
            var g0 = new ImportedPartials();
            g0.Make(Model(y0.AsSpan(24000, 48000).ToArray()), 0.5f, 0f);
            double w0 = 0;
            for (int j = 0; j < 5; j++) w0 = Math.Max(w0, Math.Abs(Cents(g0.Ratio[j] * g0.Reference, 220.0 * (j + 1))));
            Measure.Report($"  ratios 0 %: rows within {w0:0.000} cent of the whole multiples");
            Assert.That(w0, Is.LessThan(1.0));
        }

        [Test]
        public void ChangingTheSpacingDuringANoteRetunesItsPartials()
        {
            var m = Model(Tone(BarRatios.Select(r => 220 * r).ToArray(), BarAmps, 1.5));
            var b = Bank(SlotType.Harmonic, m, tweak: o => o.ImpOn = true);
            b.NoteOn(0, 57, 1f);
            var first = Mono(Render(b, 48000));
            b.S.Osc[0].ImpRatio = 0f;
            var second = Mono(Render(b, 72000));
            var g1 = new ImportedPartials(); g1.Make(Model(first.AsSpan(12000, 36000).ToArray()), 0.5f, 0f);
            var g2 = new ImportedPartials(); g2.Make(Model(second.AsSpan(24000, 48000).ToArray()), 0.5f, 0f);
            Assert.That(g1.Count, Is.GreaterThanOrEqualTo(4));
            Assert.That(Cents(g1.Ratio[1] * g1.Reference, 220 * 2.757), Is.EqualTo(0).Within(1), "as analysed before");
            Assert.That(Cents(g2.Ratio[1] * g2.Reference, 440), Is.EqualTo(0).Within(1), "whole multiples after the change, with no new note");
        }

        [Test]
        public void ARangeAveragesTheFramesAndWeightsAPartialByTheShareOfThemItSoundsIn()
        {
            // 1 s of partials 1 and 2 (equal), then 1 s of partial 1 only; the range over both keeps partial 2 at half the power.
            var a = Tone(new[] { 220.0, 440.0 }, new[] { 0.4, 0.4 }, 1.0);
            var b = Tone(new[] { 220.0 }, new[] { 0.4 }, 1.0);
            var x = new float[a.Length + b.Length];
            a.CopyTo(x, 0);
            b.CopyTo(x, a.Length);
            var m = Model(x);
            var first = new ImportedPartials();
            first.Make(m, 0.25f, 0f);
            Assert.That(Db(first.Level[1] / first.Level[0]), Is.EqualTo(0).Within(0.3), "a frame in the first half: equal");
            var late = new ImportedPartials();
            late.Make(m, 0.75f, 0f);
            Assert.That(late.Count, Is.EqualTo(1), "a frame in the second half: one partial");
            var range = new ImportedPartials();
            range.Make(m, 0.05f, 0.9f);
            Assert.That(range.Count, Is.EqualTo(2));
            double db = Db(range.Level[1] / range.Level[0]);
            Measure.Report($"Dye Essence import partials, a range over half a tone's length with partial 2 and half without: partial 2 {db:0.0} dB under partial 1 (the share of frames: about -3 dB at half, expected between -2 and -5)");
            Assert.That(db, Is.InRange(-5.5, -1.5));
        }

        [Test]
        public void WithoutAModelTheHarmonicSlotPlaysItsSetsAndANewModelChangesTheTable()
        {
            var plain = Harm(null, 0.5, 57);
            Assert.That(plain.Max(Math.Abs), Is.GreaterThan(0.05f), "no model: the sets sound");
            var m = Model(Saw(6, 1.5));
            var a = Harm(m, 0.5, 57, o => { o.ImpFrom = 0.2f; });
            var b = Harm(m, 0.5, 57, o => { o.ImpFrom = 0.8f; });
            Assert.That(a.Max(Math.Abs), Is.GreaterThan(0.05f));
            Assert.That(Hash(a), Is.Not.EqualTo(Hash(plain)));
            Assert.That(b.Length, Is.EqualTo(a.Length));
        }

        // ---- Modal: the mode table from an impulse response.

        [Test]
        public void ASyntheticImpulseResponsesModesAreFoundWithTheirFrequenciesDecaysAndGains()
        {
            var t = new ImportedModes();
            t.Make(Model(Ir(2.0)), 0f, 1.0f);
            Assert.That(t.Count, Is.EqualTo(5), "five modes");
            double wCents = 0, wDecay = 0, wDb = 0;
            for (int j = 0; j < 5; j++)
            {
                wCents = Math.Max(wCents, Math.Abs(Cents(t.Hz[j], ModeHz[j])));
                wDecay = Math.Max(wDecay, Math.Abs(t.Sigma[j] / (6.908 / ModeT60[j]) - 1));
                wDb = Math.Max(wDb, Math.Abs(Db(t.Gain[j] / t.Gain[0]) - Db(ModeGain[j])));
            }
            Measure.Report($"Dye Essence mode table from an impulse response, five known modes: frequencies within {wCents:0.000} cent, decays within {wDecay * 100:0.0} %, gains within {wDb:0.00} dB (targets 2 cent, 10 %, 1.5 dB); reference {t.Reference:0.0} Hz");
            Assert.That(wCents, Is.LessThan(2.0));
            Assert.That(wDecay, Is.LessThan(0.10));
            Assert.That(wDb, Is.LessThan(1.5));
            Assert.That(t.Reference, Is.EqualTo(310).Within(1));
        }

        [Test]
        public void TheModalEngineRingsTheImportedModesAsAnalysed()
        {
            var m = Model(Ir(2.0));
            // Key 63 is 311.1 Hz: the table's reference (310 Hz) sounds the note, so every mode is scaled by 311.13 / 310.
            double note = 440.0 * Math.Pow(2, (63 - 69) / 12.0), scale = note / 310.0;
            var y = Modal(m, 2.0, 63);
            var got = new ImportedModes();
            got.Make(Model(y), 0f, 1.0f);
            Assert.That(got.Count, Is.EqualTo(5), "the five modes sound");
            double wCents = 0, wDecay = 0, wDb = 0;
            for (int j = 0; j < 5; j++)
            {
                wCents = Math.Max(wCents, Math.Abs(Cents(got.Hz[j], ModeHz[j] * scale)));
                wDecay = Math.Max(wDecay, Math.Abs(got.Sigma[j] / (6.908 / ModeT60[j]) - 1));
                wDb = Math.Max(wDb, Math.Abs(Db(got.Gain[j] / got.Gain[0]) - Db(ModeGain[j])));
            }
            Measure.Report($"Dye Essence Modal engine on the imported modes, plucked, key 63: frequencies within {wCents:0.000} cent of the table's (scaled to the note), decays within {wDecay * 100:0.0} %, gains within {wDb:0.00} dB (targets 3 cent, 15 %, 2 dB)");
            Assert.That(wCents, Is.LessThan(3.0));
            Assert.That(wDecay, Is.LessThan(0.15));
            Assert.That(wDb, Is.LessThan(2.0));
        }

        [Test]
        public void TheModesCountKeepsTheStrongestAndTheDecayControlScalesThem()
        {
            var m = Model(Ir(2.0));
            var y = Modal(m, 2.0, 63, o => o.Modes = 3);
            var got = new ImportedModes();
            got.Make(Model(y), 0f, 1.0f);
            Assert.That(got.Count, Is.EqualTo(3), "three modes sound");
            double scale = 440.0 * Math.Pow(2, (63 - 69) / 12.0) / 310.0;
            for (int j = 0; j < 3; j++) Assert.That(Cents(got.Hz[j], ModeHz[j] * scale), Is.EqualTo(0).Within(3), $"mode {j}: the strongest three");
            // Decay 200 %: every mode rings twice as long.
            var y2 = Modal(m, 2.0, 63, o => o.ModeDecay = 2f);
            var g2 = new ImportedModes();
            g2.Make(Model(y2), 0f, 1.0f);
            double ratio = g2.Sigma[0] / (6.908 / ModeT60[0]);
            Measure.Report($"Dye Essence Modal engine on the imported modes: 3 modes keep the 3 strongest; decay 200 % gives the lowest mode a decay rate {ratio:0.00} of the measured (expected 0.5)");
            Assert.That(ratio, Is.EqualTo(0.5).Within(0.08));
        }

        [Test]
        public void ABuiltInSampleReadAsAnImpulseResponseGivesModesThatSound()
        {
            var m = SpectralSources.GetNow(2);   // Bell
            var t = new ImportedModes();
            t.Make(m, 0f, 1.0f);
            Measure.Report($"Dye Essence mode table from the built-in Bell sample: {t.Count} modes, reference {t.Reference:0.0} Hz, lowest T60 {6.908 / t.Sigma.Take(t.Count).Min():0.00} s");
            Assert.That(t.Count, Is.GreaterThanOrEqualTo(4));
            var y = Modal(m, 1.0, 60);
            Assert.That(y.Max(Math.Abs), Is.GreaterThan(0.05f));
            Assert.That(y.All(float.IsFinite), Is.True);
        }

        // ---- Bits, safety, speed.

        static readonly LaneKind[] Kinds = Avx2Lanes.Supported
            ? new[] { LaneKind.Scalar, LaneKind.Pair, LaneKind.Avx2 }
            : new[] { LaneKind.Scalar, LaneKind.Pair };

        static ulong Hash(float[] x)
        {
            ulong h = 1469598103934665603UL;
            foreach (var v in x) h = (h ^ BitConverter.SingleToUInt32Bits(v)) * 1099511628211UL;
            return h;
        }

        static float[] Patch(LaneKind kind, int block, SpectralModel hm, SpectralModel mm)
        {
            var b = new NoteBank(kind);
            b.SetSampleRate(Rate);
            var h = b.S.Osc[0];
            h.Type = SlotType.Harmonic; h.Spectrum = hm; h.ImpOn = true; h.ImpFrom = 0.3f; h.ImpSpan = 0.2f; h.ImpRatio = 0.7f; h.Random = 0.5f; h.Unison = 3; h.Detune = 12; h.Partials = 40;
            var o = b.S.Osc[1];
            o.Type = SlotType.Modal; o.Spectrum = mm; o.ModeOn = true; o.Exciter = (int)ModalExciter.Struck; o.Force = 0.6f; o.Modes = 24; o.ModeStart = 0.05f; o.ModeLen = 1f; o.ModeDecay = 1.3f; o.Unison = 2; o.Detune = 8;
            b.S.Osc[2].Type = SlotType.Off;
            b.S.Volume = 0.7f;
            b.NoteOn(0, 52, 0.8f);
            b.NoteOn(1, 59, 0.6f);
            b.NoteOn(2, 64, 0.9f);
            return Mono(Render(b, 48000, block));
        }

        const ulong PinnedHash = 0x499A6212455D179AUL;

        [Test]
        public void EveryLaneImplementationAndBlockSizeGivesTheSameBitsAndTheyAreThePinnedOnes()
        {
            var hm = SpectralSources.GetNow(1);
            var mm = Model(Ir(2.0));
            var want = Hash(Patch(LaneKind.Scalar, 256, hm, mm));
            foreach (var kind in Kinds)
                foreach (int block in new[] { 256, 117, 16 })
                    Assert.That(Hash(Patch(kind, block, hm, mm)), Is.EqualTo(want), $"{kind} in blocks of {block}");
            Measure.Report($"Dye Essence import patch hash 0x{want:X16}");
            Assert.That(want, Is.EqualTo(PinnedHash));
        }

        [Test]
        public void ASongsStoredModelGivesTheSameTablesAndTheSameBits()
        {
            var m = Model(Saw(10, 1.5));
            var back = SpectralModel.FromBase64(m.ToBase64());
            var a = new ImportedPartials(); a.Make(m, 0.3f, 0.2f);
            var b = new ImportedPartials(); b.Make(back, 0.3f, 0.2f);
            Assert.That(b.Count, Is.EqualTo(a.Count));
            for (int j = 0; j < a.Count; j++) { Assert.That(b.Level[j], Is.EqualTo(a.Level[j])); Assert.That(b.Ratio[j], Is.EqualTo(a.Ratio[j])); }
            var ir = Model(Ir(2.0));
            var irBack = SpectralModel.FromBase64(ir.ToBase64());
            Assert.That(Hash(Modal(irBack, 0.5, 60)), Is.EqualTo(Hash(Modal(ir, 0.5, 60))), "the modes' render");
            Assert.That(Hash(Harm(back, 0.5, 60)), Is.EqualTo(Hash(Harm(m, 0.5, 60))), "the partials' render");
        }

        [Test]
        public void NothingIsAllocatedOnTheAudioThreadAndEveryControlAtItsExtremesStaysFinite()
        {
            foreach (var kind in Kinds.Where(k => k != LaneKind.Scalar))
            {
                var v = new ParamValues(Params.Table);
                for (int i = 0; i < SpectralSources.Count; i++) SpectralSources.GetNow(i);
                for (int i = 0; i < SampleSources.Count; i++) SampleSources.GetNow(i);
                var e = new DyeEngine(v, kind);
                e.SetSampleRate(Rate);
                var l = new float[256];
                var r = new float[256];
                v.Set(P.AType, (int)SlotType.Harmonic); v.Set(P.AHImp, 1);
                v.Set(P.BType, (int)SlotType.Modal); v.Set(P.BMImp, 1);
                v.Set(P.CType, (int)SlotType.Harmonic); v.Set(P.CHImp, 1);
                for (int k = 0; k < 8; k++) e.NoteOn(k, 40 + 5 * k, 0.8f);
                for (int i = 0; i < 20; i++) e.Process(l, r, 256);
                void Block(int i)
                {
                    if (i % 50 == 0) e.NoteOn(i / 50 % 24, 40 + i % 40, 0.8f);
                    if (i % 50 == 25) e.NoteOff((i / 50 + 7) % 24);
                    v.Set(P.ASmpSrc, i / 37 % 5);
                    v.Set(P.BSmpSrc, i / 41 % 5);
                    v.Set(P.AHImpFrom, i * 7 % 1000);
                    v.Set(P.AHImpSpan, i * 3 % 1000);
                    v.Set(P.CHImpRatio, i * 11 % 1000);
                    v.Set(P.BMImpStart, i * 5 % 1000);
                    v.Set(P.BMImpLen, i * 13 % 1000);
                    v.Set(P.BMImpDecay, i * 17 % 1000);
                    v.Set(P.BMModes, 1 + i % 64);
                    v.Set(P.AHPartials, 1 + i % 128);
                    v.Set(P.AUnison, 1 + i / 100 % 16);
                    v.Set(P.BMImp, i / 300 % 2);
                    v.Set(P.AHImp, i / 400 % 2);
                    e.Process(l, r, 1 + i % 256);
                    for (int k = 0; k < 4; k++) if (!float.IsFinite(l[k]) || !float.IsFinite(r[k])) throw new InvalidOperationException("not finite");
                }
                for (int i = 0; i < 100; i++) Block(i);
                var (bytes, why) = AllocationCheck.Run(2000, Block);
                Measure.Report($"Dye Essence import ({kind}): {bytes} bytes allocated over 2000 blocks with the samples, ranges, ratios, ir windows, decays, counts and the imports switching");
                Assert.That(bytes, Is.EqualTo(0), why);
            }
        }

        static (double Wall, double Cpu) Speed(NoteBank b, double seconds, int notes)
        {
            var l = new float[256];
            var r = new float[256];
            for (int n = 0; n < notes; n++) b.NoteOn(n, 36 + 3 * n, 0.4f + 0.6f * n / 16);
            for (int i = 0; i < 40; i++) b.Process(l, r, 256);
            int blocks = (int)(seconds * Rate / 256);
            var p = Process.GetCurrentProcess();
            var cpu0 = p.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < blocks; i++) b.Process(l, r, 256);
            double wall = sw.Elapsed.TotalSeconds;
            p.Refresh();
            double cpu = (p.TotalProcessorTime - cpu0).TotalSeconds;
            double audio = blocks * 256.0 / Rate;
            return (audio / wall, audio / Math.Max(1e-9, cpu));
        }

        // Floors, times real time with 16 notes + a ladder (x64, arm64): a fifth or less of the figures measured on the Linux box (29 and 26).
        static readonly double[] FloorX64 = { 5.0, 5.0 }, FloorArm = { 2.5, 2.5 };

        [Test]
        public void TheImportedEnginesRunInRealTime()
        {
            bool arm = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
            var m = SpectralSources.GetNow(2);
            (string Name, NoteBank Bank, double X64, double Arm)[] cases =
            {
                ("Harmonic, 64 imported partials", Bank(SlotType.Harmonic, m, tweak: o => { o.ImpOn = true; o.Partials = 64; }), FloorX64[0], FloorArm[0]),
                ("Modal, 64 imported modes", Bank(SlotType.Modal, m, tweak: o => { o.ModeOn = true; o.Modes = 64; o.Exciter = (int)ModalExciter.Continuous; }), FloorX64[1], FloorArm[1]),
            };
            foreach (var (name, b, x64, armFloor) in cases)
            {
                b.S.Filter[0].Type = FilterType.Ladder24;
                b.S.Filter[0].Cutoff = 6000;
                double floor = arm ? armFloor : x64;
                var (wall, cpu) = GuardPolicy.Retry(() => Speed(b, 4, 16), s => s.Wall > floor, out _);
                Measure.Report($"Dye Essence import, {name}, 16 notes + ladder, 2×: {wall:F2}× real time (CPU {cpu:F2}×)");
                Assert.That(wall, Is.GreaterThan(floor), name);
            }
        }
    }
}
