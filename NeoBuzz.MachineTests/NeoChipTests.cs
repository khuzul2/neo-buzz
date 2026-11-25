using System.Xml.Linq;
using NeoBuzz.Chip;
using NeoBuzz.Dsp;
using NeoBuzz.TestSupport;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Chip (docs/specs/neo-chip.md): headless gate tests and hygiene measurements.</summary>
    [TestFixture]
    public class NeoChipTests
    {
        internal static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        internal static (ChipEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new ChipEngine(v);
            e.SetSampleRate(Measure.Rate);
            return (e, v);
        }

        internal static (float[] L, float[] R) RenderStereo(ChipEngine e, int length, Action<int> atBlock = null, int block = ChipEngine.MaxBlock)
        {
            var outL = new float[length];
            var outR = new float[length];
            var l = new float[block];
            var r = new float[block];
            for (int pos = 0; pos < length; pos += block)
            {
                atBlock?.Invoke(pos);
                int n = Math.Min(block, length - pos);
                e.Process(l, r, n);
                Array.Copy(l, 0, outL, pos, n);
                Array.Copy(r, 0, outR, pos, n);
            }
            return (outL, outR);
        }

        // ------------------------------------------------------------------ 1. Duty cycle gate

        [TestCase(0, 0.125, false)]
        [TestCase(1, 0.250, false)]
        [TestCase(2, 0.500, false)]
        [TestCase(3, 0.750, false)]
        [TestCase(0, 0.125, true)]
        [TestCase(1, 0.250, true)]
        [TestCase(2, 0.500, true)]
        [TestCase(3, 0.750, true)]
        public void PulseDutyWithinOnePercentAt440Hz(int dutyChoice, double expectedDuty, bool raw)
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.P1Duty, dutyChoice);
                v.Set(P.P1Pwm, 0);
                v.Set(P.Raw, raw ? 1 : 0);
                v.Set(P.EnvSustain, 15);
                Set(v, P.EnvAttack, 0.001);
                v.Set(P.Spread, 0);
                Set(v, P.Volume, 0);
            });
            e.NoteOn(0, 69, 1f); // A4 = 440 Hz on Track 1 (Pulse 1)
            // Let the 5 Hz DC blocker settle (~0.6 s), then measure over 100 exact cycles (~10909 samples).
            int settle = 32000;
            int measureLen = (int)Math.Round(100.0 * Measure.Rate / 440.0);
            var (l, _) = RenderStereo(e, settle + measureLen);

            float min = float.MaxValue, max = float.MinValue;
            for (int i = settle; i < settle + measureLen; i++)
            {
                if (l[i] < min) min = l[i];
                if (l[i] > max) max = l[i];
            }
            float mid = 0.5f * (min + max);
            int above = 0;
            for (int i = settle; i < settle + measureLen; i++)
                if (l[i] > mid) above++;
            double measured = (double)above / measureLen;
            Measure.Report($"Neo Chip duty ({Params.Table[P.P1Duty].Describe(dutyChoice)}, raw={raw}): measured {measured * 100:F2}% (target {expectedDuty * 100:F1}%)");
            Assert.That(Math.Abs(measured - expectedDuty), Is.LessThan(0.01), "duty within +-1%");
        }

        // ------------------------------------------------------------------ 2. Band-limited vs Raw gate

        [TestCase(60, 2)] // C4, 50%
        [TestCase(69, 2)] // A4, 50%
        [TestCase(84, 2)] // C6, 50%
        [TestCase(96, 2)] // C7 (2093 Hz), 50%
        [TestCase(96, 1)] // C7 (2093 Hz), 25%
        [TestCase(96, 0)] // C7 (2093 Hz), 12.5%
        public void BandLimitedAliasesBelowMinus60DbAndRawIsMeasurablyHigher(int midiNote, int dutyChoice)
        {
            double f0 = DspMath.NoteToHz(midiNote);
            double MeasureFloor(bool raw)
            {
                var (e, _) = Make(v =>
                {
                    v.Set(P.P1Duty, dutyChoice);
                    v.Set(P.P1Pwm, 0);
                    v.Set(P.Raw, raw ? 1 : 0);
                    v.Set(P.EnvSustain, 15);
                    Set(v, P.EnvAttack, 0.001);
                    v.Set(P.Spread, 0);
                    Set(v, P.Volume, 0);
                });
                e.NoteOn(0, midiNote, 1f);
                var (l, _) = RenderStereo(e, 16384 + Measure.N);
                return PolyTests.AliasFloorWindowed(l, 16384, Measure.N, f0);
            }

            double cleanFloor = MeasureFloor(false);
            double rawFloor = MeasureFloor(true);
            Measure.Report($"Neo Chip alias floor MIDI {midiNote} ({f0:F1} Hz, duty {Params.Table[P.P1Duty].Describe(dutyChoice)}): band-limited {cleanFloor:F1} dB, raw {rawFloor:F1} dB");
            Assert.That(cleanFloor, Is.LessThan(-60.0), "band-limited aliases <= -60 dB");
            Assert.That(rawFloor, Is.GreaterThan(cleanFloor + 15.0), "raw mode aliases measurably higher");
        }

        // ------------------------------------------------------------------ 3. Noise LFSR gate

        [TestCase(false, ChipNoiseLfsr.LongPeriod)]
        [TestCase(true, ChipNoiseLfsr.ShortPeriod)]
        public void NoiseLfsrPeriodIsExact(bool shortMode, int expectedPeriod)
        {
            var lfsr = new ChipNoiseLfsr();
            lfsr.Reset(1u);
            uint start = lfsr.State;
            var seen = new HashSet<uint>();
            int steps = 0;
            do
            {
                lfsr.Step(shortMode);
                steps++;
                Assert.That(lfsr.State, Is.Not.EqualTo(0u), "LFSR must never lock at 0");
                seen.Add(lfsr.State);
                if (steps > expectedPeriod + 10) Assert.Fail($"LFSR did not repeat within {expectedPeriod + 10} steps");
            }
            while (lfsr.State != start);

            Measure.Report($"Neo Chip LFSR ({(shortMode ? "7-bit short" : "15-bit long")}): period {steps} clock steps, {seen.Count} unique states");
            Assert.That(steps, Is.EqualTo(expectedPeriod));
            Assert.That(seen.Count, Is.EqualTo(expectedPeriod));
        }

        [Test]
        public void NoiseClockRateTracksNoteIncludingAboveSampleRate()
        {
            // Short (7-bit, period 127) metallic mode at A4 with NoisePitch = 440 * 127 = 55880 Hz (via A5 with 27940 Hz base -> 55880 Hz > 48 kHz!).
            var (e, _) = Make(v =>
            {
                v.Set(P.NoiseMode, 1); // Short 7-bit
                Set(v, P.NoisePitch, 27940.0); // at A4 = 27940 Hz -> at A5 (MIDI 81) = 55880 Hz -> 55880 / 127 = 440 Hz!
                v.Set(P.EnvSustain, 15);
                Set(v, P.EnvAttack, 0.001);
                v.Set(P.Spread, 0);
                Set(v, P.Volume, 0);
            });
            e.NoteOn(3, 81, 1f); // Track 4 (Noise), MIDI 81 (A5)
            var (l, _) = RenderStereo(e, 8192 + 32768);
            var mag = Measure.Spectrum(l, 8192, 32768);
            double binHz = (double)Measure.Rate / 32768;
            int k440 = (int)Math.Round(440.0 / binHz);
            double peak440 = 0;
            for (int k = k440 - 3; k <= k440 + 3; k++) peak440 = Math.Max(peak440, mag[k]);
            double median = mag.Skip(20).Take(1000).OrderBy(x => x).ElementAt(500);
            double snrDb = Measure.Db(peak440 / Math.Max(1e-12, median));
            Measure.Report($"Neo Chip 7-bit noise at 55.88 kHz clock (> 48 kHz SR): 440 Hz metallic peak +{snrDb:F1} dB above floor");
            Assert.That(snrDb, Is.GreaterThan(15.0), "7-bit metallic fundamental clearly tracks above sample rate");

            // Long mode spectral centroid rises from low note to high note.
            double CentroidFor(int note)
            {
                var (en, _) = Make(v =>
                {
                    v.Set(P.NoiseMode, 0);
                    Set(v, P.NoisePitch, 6000.0);
                    v.Set(P.EnvSustain, 15);
                    Set(v, P.EnvAttack, 0.001);
                    Set(v, P.Volume, 0);
                });
                en.NoteOn(3, note, 1f);
                var (buf, _) = RenderStereo(en, 8192 + 16384);
                var sp = Measure.Spectrum(buf, 8192, 16384);
                double num = 0, den = 0;
                for (int k = 1; k < sp.Length; k++)
                {
                    double f = k * (double)Measure.Rate / 16384;
                    double p = sp[k] * sp[k];
                    num += f * p;
                    den += p;
                }
                return num / Math.Max(1e-20, den);
            }
            double cLow = CentroidFor(48);
            double cHigh = CentroidFor(84);
            Measure.Report($"Neo Chip long noise centroid: C3 {cLow:F0} Hz, C6 {cHigh:F0} Hz");
            Assert.That(cHigh, Is.GreaterThan(cLow * 1.5), "noise brightness follows played note");
        }

        // ------------------------------------------------------------------ 4. Volume steps (exact 16 levels)

        [Test]
        public void EnvelopeTakesExactly16LevelsInSteppedMode()
        {
            // ADSR Attack (0 -> 15) + Decay (15 -> 0): discrete step target visits all 16 exact k / 15f levels.
            var env = new ChipEnvelope();
            env.NoteOn(ChipEnvMode.Adsr);
            var levels = new HashSet<float>();
            for (int i = 0; i < 48000; i++)
            {
                float g = env.StepNext(ChipEnvMode.Adsr, 0.10, 0.20, 0, 0.10, 0.10, Measure.Rate);
                levels.Add(g);
                double scaled = g * 15.0;
                Assert.That(Math.Abs(scaled - Math.Round(scaled)), Is.LessThan(1e-5), $"level {g} not on 16-step grid");
            }
            Assert.That(levels.Count, Is.EqualTo(16), "ADSR visits all 16 discrete levels 0..15");

            // Hardware shapes all take exactly 16 discrete target levels.
            foreach (var mode in new[] { ChipEnvMode.SawDown, ChipEnvMode.SawUp, ChipEnvMode.Triangle, ChipEnvMode.DecayShot, ChipEnvMode.AttackHold })
            {
                var hw = new ChipEnvelope();
                hw.NoteOn(mode);
                var hwLevels = new HashSet<float>();
                for (int i = 0; i < 24000; i++)
                {
                    float g = hw.StepNext(mode, 0.01, 0.05, 8, 0.05, 0.10, Measure.Rate);
                    hwLevels.Add(g);
                    double scaled = g * 15.0;
                    Assert.That(Math.Abs(scaled - Math.Round(scaled)), Is.LessThan(1e-5), $"{mode} level {g} not on 16-step grid");
                }
                Assert.That(hwLevels.Count, Is.EqualTo(16), $"{mode} visits all 16 discrete levels 0..15");
            }

            // Slewed envelope still settles onto all 16 discrete step plateaus when dwell > slew.
            var slewed = new ChipEnvelope();
            slewed.NoteOn(ChipEnvMode.Adsr);
            var settled = new HashSet<int>();
            for (int i = 0; i < 48000; i++)
            {
                float g = slewed.Next(ChipEnvMode.Adsr, 0.15, 0.25, 0, 0.15, 0.10, Measure.Rate, raw: false);
                double scaled = g * 15.0;
                int nearest = (int)Math.Round(scaled);
                if (Math.Abs(scaled - nearest) < 1e-4) settled.Add(nearest);
            }
            Assert.That(settled.Count, Is.EqualTo(16), "slewed ADSR settles onto all 16 discrete step plateaus 0..15");
            Measure.Report("Neo Chip volume steps: ADSR and all 5 hardware envelope shapes verified at exactly 16 discrete levels (0/15..15/15) with slewed plateaus");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HeldNoteAdsrGainTraceSlewsEveryStepWithoutClicks(bool raw)
        {
            // Gate from listening round 1: on a held note through ADSR and release, every 1/15 step
            // glides over at least the slew (1.8 ms clean, 0.5 ms raw), never changes by more than
            // 1/15 in any 1 ms window during step-separated ADSR stages, and produces 0 gain-trace clicks.
            var (e, _) = Make(v =>
            {
                v.Set(P.VoiceMode, (int)ChipVoiceMode.Mono);
                v.Set(P.Raw, raw ? 1 : 0);
                v.Set(P.EnvMode, (int)ChipEnvMode.Adsr);
                Set(v, P.EnvAttack, 0.15);   // 10 ms/step
                Set(v, P.EnvDecay, 0.18);    // 36 ms/step (15 -> 10)
                v.Set(P.EnvSustain, 10);
                Set(v, P.EnvRelease, 0.15);  // 15 ms/step (10 -> 0)
            });

            int total = 36000; // 0.75 s
            var gainTrace = new float[total];
            var blockTrace = new[] { new float[ChipEngine.MaxBlock], new float[ChipEngine.MaxBlock], new float[ChipEngine.MaxBlock], new float[ChipEngine.MaxBlock] };
            e.GainTrace = blockTrace;
            var bl = new float[ChipEngine.MaxBlock];
            var br = new float[ChipEngine.MaxBlock];

            e.NoteOn(1060, 60, 0.9f, 0);
            for (int pos = 0; pos < total; pos += ChipEngine.MaxBlock)
            {
                if (pos == 22016) e.NoteOff(1060, 60, 0); // release at ~0.46 s
                int n = Math.Min(ChipEngine.MaxBlock, total - pos);
                e.Process(bl, br, n);
                Array.Copy(blockTrace[0], 0, gainTrace, pos, n);
            }

            const int OneMs = 48;
            const double OneStep = 1.0 / 15.0;
            double slewSec = raw ? ChipEnvelope.RawStepSlewSec : ChipEnvelope.CleanStepSlewSec;
            double slewSamples = Math.Round(slewSec * Measure.Rate);
            // Smoothstep peak slope is 1.5 * (1/15) / slewSamples.
            double maxAllowedPerSample = 1.55 * OneStep / slewSamples;

            double maxDelta1Ms = 0;
            double maxPerSample = 0;
            double maxD2 = 0;
            for (int i = 1; i < total; i++)
            {
                double d1 = Math.Abs(gainTrace[i] - gainTrace[i - 1]);
                if (d1 > maxPerSample) maxPerSample = d1;
                if (i >= OneMs)
                {
                    double d1ms = Math.Abs(gainTrace[i] - gainTrace[i - OneMs]);
                    if (d1ms > maxDelta1Ms) maxDelta1Ms = d1ms;
                }
                if (i + 1 < total)
                {
                    double d2 = Math.Abs(gainTrace[i + 1] - 2.0 * gainTrace[i] + gainTrace[i - 1]);
                    if (d2 > maxD2) maxD2 = d2;
                }
            }

            int gainClicks = NeoChipPack.DetectGainClicks(gainTrace, Measure.Rate);
            Measure.Report($"Neo Chip ADSR gain trace (raw={raw}): max 1 ms delta {maxDelta1Ms:F5} (1/15={OneStep:F5}), max d1 {maxPerSample:F6} (limit {maxAllowedPerSample:F6}), max d2 {maxD2:F6}, clicks {gainClicks}");

            Assert.That(maxDelta1Ms, Is.LessThanOrEqualTo(OneStep + 1e-4), "within any 1 ms, gain changes by <= 1/15");
            Assert.That(maxPerSample, Is.LessThanOrEqualTo(maxAllowedPerSample), "each step is spread over at least the slew");
            Assert.That(gainClicks, Is.EqualTo(0), "gain-trace click detector finds 0 clicks");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HardEnvelopeAndHardNoteOffGainTraceHaveSmoothEdgesWithoutClicks(bool raw)
        {
            // Gate from listening round 1: hard-envelope note (SawDown starting at step 15 and wrapping
            // 0 -> 15) has >= 1 ms note-on fade, >= 2 ms hard note-off fade (AllNotesOff(true) and
            // min-release NoteOff), and 0 gain-trace clicks.
            var (e, _) = Make(v =>
            {
                v.Set(P.VoiceMode, (int)ChipVoiceMode.Mono);
                v.Set(P.Raw, raw ? 1 : 0);
                v.Set(P.EnvMode, (int)ChipEnvMode.SawDown);
                Set(v, P.EnvPeriod, 0.12); // 120 ms repeating SawDown
                Set(v, P.EnvRelease, 0.001); // minimum 1 ms setting (clamped >= 2 ms)
            });

            int total = 16384; // ~341 ms (2 full SawDown cycles + hard note-off)
            var gainTrace = new float[total];
            var blockTrace = new[] { new float[ChipEngine.MaxBlock], new float[ChipEngine.MaxBlock], new float[ChipEngine.MaxBlock], new float[ChipEngine.MaxBlock] };
            e.GainTrace = blockTrace;
            var bl = new float[ChipEngine.MaxBlock];
            var br = new float[ChipEngine.MaxBlock];

            e.NoteOn(1060, 60, 1f, 0);
            int hardOffAt = 12288; // mid-cycle (~256 ms) where SawDown is active
            for (int pos = 0; pos < total; pos += ChipEngine.MaxBlock)
            {
                if (pos == hardOffAt) e.AllNotesOff(true);
                int n = Math.Min(ChipEngine.MaxBlock, total - pos);
                e.Process(bl, br, n);
                Array.Copy(blockTrace[0], 0, gainTrace, pos, n);
            }

            // 1. Note-on fade from 0 to >= 0.99 takes at least 1 ms (48 samples at 48 kHz).
            int riseSamples = 0;
            while (riseSamples < 500 && gainTrace[riseSamples] < 0.99f) riseSamples++;
            Assert.That(riseSamples, Is.GreaterThanOrEqualTo(48), $"note-on fade must be >= 1 ms (was {riseSamples} samples)");

            // 2. Hard note-off fade from active gain down to <= 0.001 takes at least 2 ms (96 samples at 48 kHz).
            Assert.That(gainTrace[hardOffAt - 1], Is.GreaterThan(0.2f), "voice sounding before hard note-off");
            int fallSamples = 0;
            while (hardOffAt + fallSamples < total && gainTrace[hardOffAt + fallSamples] > 1e-3f) fallSamples++;
            Assert.That(fallSamples, Is.GreaterThanOrEqualTo(96), $"hard note-off fade must be >= 2 ms (was {fallSamples} samples)");
            Assert.That(gainTrace[total - 1], Is.EqualTo(0f), "voice reaches exact 0 after hard fade-out");

            // 3. Minimum-release (1 ms param) normal NoteOff also fades over >= 2 ms.
            var (eRel, _) = Make(v =>
            {
                v.Set(P.VoiceMode, (int)ChipVoiceMode.Mono);
                v.Set(P.Raw, raw ? 1 : 0);
                v.Set(P.EnvMode, (int)ChipEnvMode.Adsr);
                Set(v, P.EnvAttack, 0.002);
                v.Set(P.EnvSustain, 15);
                Set(v, P.EnvRelease, 0.001);
            });
            var relTrace = new float[4096];
            eRel.GainTrace = blockTrace;
            eRel.NoteOn(1060, 60, 1f, 0);
            int relAt = 1024;
            for (int pos = 0; pos < relTrace.Length; pos += ChipEngine.MaxBlock)
            {
                if (pos == relAt) eRel.NoteOff(1060, 60, 0);
                int n = Math.Min(ChipEngine.MaxBlock, relTrace.Length - pos);
                eRel.Process(bl, br, n);
                Array.Copy(blockTrace[0], 0, relTrace, pos, n);
            }
            int relFall = 0;
            while (relAt + relFall < relTrace.Length && relTrace[relAt + relFall] > 1e-3f) relFall++;
            Assert.That(relFall, Is.GreaterThanOrEqualTo(96), $"min-release NoteOff must fade >= 2 ms (was {relFall} samples)");

            // 4. Gain-trace click detector finds 0 clicks on both traces.
            int hwClicks = NeoChipPack.DetectGainClicks(gainTrace, Measure.Rate);
            int relClicks = NeoChipPack.DetectGainClicks(relTrace, Measure.Rate);
            Measure.Report($"Neo Chip hard-env gain trace (raw={raw}): note-on rise {riseSamples} smp, hard-off fall {fallSamples} smp, min-rel fall {relFall} smp, clicks {hwClicks}/{relClicks}");
            Assert.That(hwClicks, Is.EqualTo(0), "SawDown + hard note-off gain trace has 0 clicks");
            Assert.That(relClicks, Is.EqualTo(0), "min-release ADSR gain trace has 0 clicks");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RetriggerFadesThroughZeroBeforePhaseResetWithZeroClicks(bool raw)
        {
            // Retriggering a sounding mono voice must fade smoothly through exact 0 (2 ms C1 steal fade)
            // before resetting oscillator phase and starting the next note — zero gain-trace clicks.
            var (e, _) = Make(v =>
            {
                v.Set(P.VoiceMode, (int)ChipVoiceMode.Mono);
                v.Set(P.Raw, raw ? 1 : 0);
                v.Set(P.EnvMode, (int)ChipEnvMode.SawDown);
                Set(v, P.EnvPeriod, 0.20);
            });
            int total = 8192;
            var gainTrace = new float[total];
            var blockTrace = new[] { new float[ChipEngine.MaxBlock], new float[ChipEngine.MaxBlock], new float[ChipEngine.MaxBlock], new float[ChipEngine.MaxBlock] };
            e.GainTrace = blockTrace;
            var bl = new float[ChipEngine.MaxBlock];
            var br = new float[ChipEngine.MaxBlock];

            e.NoteOn(1060, 60, 1f, 0);
            int retrigAt = 2048;
            for (int pos = 0; pos < total; pos += ChipEngine.MaxBlock)
            {
                if (pos == retrigAt) e.NoteOn(1064, 64, 1f, 0);
                int n = Math.Min(ChipEngine.MaxBlock, total - pos);
                e.Process(bl, br, n);
                Array.Copy(blockTrace[0], 0, gainTrace, pos, n);
            }

            // Confirm the voice reaches exact 0 during the 2 ms steal fade before rising for the new note.
            int zeroAt = -1;
            for (int i = retrigAt; i < retrigAt + 256; i++)
            {
                if (gainTrace[i] == 0f) { zeroAt = i; break; }
            }
            Assert.That(zeroAt, Is.GreaterThanOrEqualTo(retrigAt + 90), "steal fade takes ~2 ms before reaching exact 0");
            Assert.That(gainTrace[zeroAt + 128], Is.GreaterThan(0.5f), "new note rises after reaching silence");

            int gainClicks = NeoChipPack.DetectGainClicks(gainTrace, Measure.Rate);
            Assert.That(gainClicks, Is.EqualTo(0), "retrigger gain trace has 0 clicks");
        }

        // ------------------------------------------------------------------ 5. Timing gate (Arp, ADSR, HW Env)

        [TestCase(120.0)]
        [TestCase(174.0)]
        public void ArpeggioStepsLandWithinOneSampleAt1_32Synced(double bpm)
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.ArpMode, (int)ChipArpMode.Up);
                v.Set(P.ArpSteps, 3);
                Set(v, P.ArpStep1, 0);
                Set(v, P.ArpStep2, 7);
                Set(v, P.ArpStep3, 12);
                v.Set(P.ArpSync, 3); // 1/32
                v.Set(P.EnvSustain, 15);
                Set(v, P.EnvAttack, 0.001);
            });
            e.Tempo = bpm;
            double expectedStepSamples = (60.0 / bpm) * 0.125 * Measure.Rate;
            int totalSteps = 16;
            int totalSamples = (int)Math.Ceiling((totalSteps + 1) * expectedStepSamples);

            e.NoteOn(0, 60, 1f);
            var transitions = new List<(int Sample, int Offset)>();
            var l = new float[1];
            var r = new float[1];
            int prevOff = int.MinValue;
            for (int s = 0; s < totalSamples; s++)
            {
                e.Process(l, r, 1);
                int off = e.ChannelArpOffset(0);
                if (off != prevOff)
                {
                    transitions.Add((s, off));
                    prevOff = off;
                }
            }

            Assert.That(transitions.Count, Is.GreaterThanOrEqualTo(totalSteps));
            int[] pattern = { 0, 7, 12 };
            double worstErr = 0;
            for (int k = 0; k < totalSteps; k++)
            {
                double ideal = k * expectedStepSamples;
                double err = Math.Abs(transitions[k].Sample - ideal);
                worstErr = Math.Max(worstErr, err);
                Assert.That(transitions[k].Offset, Is.EqualTo(pattern[k % 3]), $"step {k} offset");
                Assert.That(err, Is.LessThanOrEqualTo(1.0), $"step {k} at {bpm} BPM landed at {transitions[k].Sample} vs ideal {ideal:F2}");
            }
            Measure.Report($"Neo Chip arp 1/32 at {bpm:F0} BPM ({expectedStepSamples:F2} smp/step): max step error {worstErr:F2} samples over {totalSteps} steps");
        }

        [Test]
        public void ArpeggioModesAndStepCountsFollowExactOrder()
        {
            int[] RunSeq(ChipArpMode mode, int steps, int s1, int s2, int s3, int count)
            {
                var (e, pv) = Make(v =>
                {
                    v.Set(P.ArpMode, (int)mode);
                    v.Set(P.ArpSteps, steps);
                    Set(v, P.ArpStep1, s1);
                    Set(v, P.ArpStep2, s2);
                    Set(v, P.ArpStep3, s3);
                    v.Set(P.ArpSync, 0); // Free
                    Set(v, P.ArpRate, 48.0);
                    v.Set(P.EnvSustain, 15);
                });
                e.NoteOn(0, 60, 1f);
                double stepSamples = Measure.Rate / pv.Value(P.ArpRate);
                var l = new float[1];
                var r = new float[1];
                var seq = new int[count];
                int samplePos = 0;
                for (int k = 0; k < count; k++)
                {
                    int target = (int)Math.Round((k + 0.5) * stepSamples);
                    while (samplePos <= target)
                    {
                        e.Process(l, r, 1);
                        samplePos++;
                    }
                    seq[k] = e.ChannelArpOffset(0);
                }
                return seq;
            }

            // Unsorted input offsets (0, +7, +4) verify Up vs AsWritten differ.
            Assert.That(RunSeq(ChipArpMode.Up, 3, 0, 7, 4, 6), Is.EqualTo(new[] { 0, 4, 7, 0, 4, 7 }));
            Assert.That(RunSeq(ChipArpMode.Down, 3, 0, 7, 4, 6), Is.EqualTo(new[] { 7, 4, 0, 7, 4, 0 }));
            Assert.That(RunSeq(ChipArpMode.UpDown, 3, 0, 7, 4, 8), Is.EqualTo(new[] { 0, 4, 7, 4, 0, 4, 7, 4 }));
            Assert.That(RunSeq(ChipArpMode.AsWritten, 3, 0, 7, 4, 6), Is.EqualTo(new[] { 0, 7, 4, 0, 7, 4 }));
            Assert.That(RunSeq(ChipArpMode.AsWritten, 2, 0, 5, 12, 4), Is.EqualTo(new[] { 0, 5, 0, 5 }));
            Assert.That(RunSeq(ChipArpMode.Up, 1, 7, 0, 12, 3), Is.EqualTo(new[] { 7, 7, 7 }));
        }

        [TestCase(0.020, 0.050, 0.040)]
        [TestCase(0.100, 0.250, 0.200)]
        [TestCase(0.400, 0.600, 0.500)]
        public void AdsrStageTimesWithinFivePercent(double atkSec, double decSec, double relSec)
        {
            var env = new ChipEnvelope();
            env.NoteOn(ChipEnvMode.Adsr);

            int atkSamples = 0;
            while (env.State == ChipEnvelope.EnvState.Attack && atkSamples < 5 * Measure.Rate)
            {
                env.Next(ChipEnvMode.Adsr, atkSec, decSec, 0, relSec, 0.1, Measure.Rate);
                atkSamples++;
            }
            int decSamples = 0;
            while (env.State == ChipEnvelope.EnvState.Decay && decSamples < 5 * Measure.Rate)
            {
                env.Next(ChipEnvMode.Adsr, atkSec, decSec, 0, relSec, 0.1, Measure.Rate);
                decSamples++;
            }

            // Release from full sustain (15).
            var envRel = new ChipEnvelope();
            envRel.NoteOn(ChipEnvMode.Adsr);
            for (int i = 0; i < 4800; i++)
                envRel.Next(ChipEnvMode.Adsr, 0.002, 0.01, 15, relSec, 0.1, Measure.Rate);
            envRel.NoteOff();
            int relSamples = 0;
            while (envRel.State == ChipEnvelope.EnvState.Release && relSamples < 5 * Measure.Rate)
            {
                envRel.Next(ChipEnvMode.Adsr, 0.002, 0.01, 15, relSec, 0.1, Measure.Rate);
                relSamples++;
            }

            double atkErr = Math.Abs(atkSamples / (double)Measure.Rate - atkSec) / atkSec;
            double decErr = Math.Abs(decSamples / (double)Measure.Rate - decSec) / decSec;
            double relErr = Math.Abs(relSamples / (double)Measure.Rate - relSec) / relSec;
            Measure.Report($"Neo Chip ADSR timing (A={atkSec * 1000:F0}ms, D={decSec * 1000:F0}ms, R={relSec * 1000:F0}ms): errors {atkErr * 100:F2}%, {decErr * 100:F2}%, {relErr * 100:F2}%");
            Assert.That(atkErr, Is.LessThan(0.05), "attack within +-5%");
            Assert.That(decErr, Is.LessThan(0.05), "decay within +-5%");
            Assert.That(relErr, Is.LessThan(0.05), "release within +-5%");
        }

        [TestCase(1, 0.050)] // SawDown
        [TestCase(2, 0.200)] // SawUp
        [TestCase(3, 0.125)] // Triangle
        public void HardwareEnvelopePeriodWithinOnePercent(int modeIndex, double periodSec)
        {
            var mode = (ChipEnvMode)modeIndex;
            var env = new ChipEnvelope();
            env.NoteOn(mode);
            int total = (int)Math.Ceiling(periodSec * Measure.Rate * 6);
            var starts = new List<int>();
            int prev = -1;
            int triggerStep = mode == ChipEnvMode.SawDown ? 15 : 0;
            int prevExpected = mode == ChipEnvMode.SawDown ? 0 : mode == ChipEnvMode.SawUp ? 15 : 1;
            for (int i = 0; i < total; i++)
            {
                env.Next(mode, 0.01, 0.05, 12, 0.05, periodSec, Measure.Rate);
                int st = env.CurrentStep;
                if (i == 0 || (st == triggerStep && prev == prevExpected))
                    starts.Add(i);
                prev = st;
            }
            Assert.That(starts.Count, Is.GreaterThanOrEqualTo(4));
            double measuredPeriod = (starts[^1] - starts[0]) / (double)(starts.Count - 1) / Measure.Rate;
            double err = Math.Abs(measuredPeriod - periodSec) / periodSec;
            Measure.Report($"Neo Chip HW env {mode} ({periodSec * 1000:F1} ms): measured {measuredPeriod * 1000:F2} ms (err {err * 100:F3}%)");
            Assert.That(err, Is.LessThan(0.01), "hardware envelope period within +-1%");
        }

        [Test]
        public void HardwareEnvelopeTempoSyncMatchesBpm()
        {
            // 1/16 at 174 BPM -> 60/174 * 0.25 = 0.086207 s
            double expected = ChipEngine.EnvSyncSeconds(5, 174.0);
            Assert.That(Math.Abs(expected - (60.0 / 174.0) * 0.25), Is.LessThan(1e-9));
            var (e, _) = Make(v =>
            {
                v.Set(P.EnvMode, (int)ChipEnvMode.SawDown);
                v.Set(P.EnvSync, 5); // 1/16
            });
            e.Tempo = 174.0;
            e.NoteOn(0, 60, 1f);
            var l = new float[1];
            var r = new float[1];
            var wraps = new List<int>();
            int prev = -1;
            for (int i = 0; i < (int)(expected * Measure.Rate * 6); i++)
            {
                e.Process(l, r, 1);
                int st = e.ChannelEnvStep(0);
                if (i > 0 && st == 15 && prev == 0) wraps.Add(i);
                prev = st;
            }
            Assert.That(wraps.Count, Is.GreaterThanOrEqualTo(4));
            double measured = (wraps[^1] - wraps[0]) / (double)(wraps.Count - 1) / Measure.Rate;
            Assert.That(Math.Abs(measured - expected) / expected, Is.LessThan(0.01));
        }

        // ------------------------------------------------------------------ 6. Notes & Voice allocation gate

        [Test]
        public void PolyChordStealingNoteOffAndRoundRobin()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.VoiceMode, (int)ChipVoiceMode.Poly);
                v.Set(P.EnvSustain, 15);
                Set(v, P.EnvAttack, 0.001);
                Set(v, P.EnvRelease, 0.015);
            });
            // 3-note chord on MIDI ch 1 -> uses all 3 pulse channels.
            e.NoteOn(1060, 60, 0.9f, 0);
            e.NoteOn(1064, 64, 0.9f, 0);
            e.NoteOn(1067, 67, 0.9f, 0);
            RenderStereo(e, 512);
            Assert.That(e.ActivePulseMask, Is.EqualTo(0b111), "3-note chord activates all 3 pulse channels");
            Assert.That(e.HeldPulseMask, Is.EqualTo(0b111));
            Assert.That(e.ChannelNote(0), Is.EqualTo(60));
            Assert.That(e.ChannelNote(1), Is.EqualTo(64));
            Assert.That(e.ChannelNote(2), Is.EqualTo(67));

            // 4th note steals the oldest (channel 0, note 60).
            e.NoteOn(1072, 72, 0.9f, 0);
            RenderStereo(e, 512); // past 2 ms steal fade (96 samples)
            Assert.That(e.ChannelNote(0), Is.EqualTo(72), "4th note steals oldest channel 0");
            Assert.That(e.ChannelNote(1), Is.EqualTo(64), "channel 1 untouched");
            Assert.That(e.ChannelNote(2), Is.EqualTo(67), "channel 2 untouched");

            // Note-off for 2nd note (64) releases only channel 1.
            e.NoteOff(1064, 64, 0);
            Assert.That(e.HeldPulseMask, Is.EqualTo(0b101), "only channel 1 released");
            RenderStereo(e, 4800); // 100 ms > 15 ms release
            Assert.That(e.ActivePulseMask, Is.EqualTo(0b101), "channel 1 decayed to idle while channels 0 and 2 remain sounding");

            // All notes off, then sequential single notes rotate round-robin 0 -> 1 -> 2.
            e.AllNotesOff(true);
            var (e2, _) = Make(v => { v.Set(P.VoiceMode, (int)ChipVoiceMode.Poly); Set(v, P.EnvRelease, 0.002); });
            var used = new List<int>();
            foreach (int n in new[] { 60, 62, 64 })
            {
                e2.NoteOn(1000 + n, n, 0.9f, 0);
                RenderStereo(e2, 256);
                int mask = e2.HeldPulseMask;
                used.Add(mask == 1 ? 0 : mask == 2 ? 1 : 2);
                e2.NoteOff(1000 + n, n, 0);
                RenderStereo(e2, 512);
            }
            Assert.That(used, Is.EqualTo(new[] { 0, 1, 2 }), "sequential notes rotate round-robin over Pulse 1..3");
        }

        [TestCase(0.0)]
        [TestCase(0.5)]
        [TestCase(1.0)]
        public void VelocityOneAnd127DifferByVelocityDepth(double depth)
        {
            double MeasureRmsFor(int vel)
            {
                var (e, _) = Make(v =>
                {
                    v.Set(P.VoiceMode, (int)ChipVoiceMode.Mono);
                    v.Set(P.P1Duty, 2); // 50%
                    Set(v, P.VelDepth, depth);
                    v.Set(P.EnvSustain, 15);
                    Set(v, P.EnvAttack, 0.001);
                    v.Set(P.Spread, 0);
                    Set(v, P.Volume, 0);
                });
                e.NoteOn(1069, 69, vel / 127f, 0);
                var (l, _) = RenderStereo(e, 12000);
                return Measure.Rms(l, 4000, 8000);
            }

            double r1 = MeasureRmsFor(1);
            double r127 = MeasureRmsFor(127);
            double expectedRatio = (1.0 - depth) + depth * (1.0 / 127.0);
            double measuredDb = Measure.Db(r1 / r127);
            double expectedDb = Measure.Db(expectedRatio);
            Measure.Report($"Neo Chip velocity depth {depth * 100:F0}%: vel 1 vs 127 = {measuredDb:F2} dB (expected {expectedDb:F2} dB)");
            Assert.That(Math.Abs(measuredDb - expectedDb), Is.LessThan(0.5));
        }

        [Test]
        public void NoiseMidiChannelRoutingAndUnisonDetune()
        {
            // Default Noise MIDI ch = 10 (index 9): ch 10 plays noise, ch 1 plays pulse.
            var (e, _) = Make();
            e.NoteOn(1060, 60, 1f, 9); // MIDI ch 10
            RenderStereo(e, 256);
            Assert.That(e.NoiseActive, Is.True);
            Assert.That(e.ActivePulseMask, Is.EqualTo(0));

            // Custom Noise MIDI ch = 4 (index 3).
            var (e4, _) = Make(v => v.Set(P.NoiseMidiCh, 4));
            e4.NoteOn(1060, 60, 1f, 3);
            RenderStereo(e4, 256);
            Assert.That(e4.NoiseActive, Is.True);
            Assert.That(e4.ActivePulseMask, Is.EqualTo(0));

            // Unison activates all 3 pulse channels on a single MIDI note and detunes them.
            var (eu, _) = Make(v =>
            {
                v.Set(P.VoiceMode, (int)ChipVoiceMode.Unison);
                Set(v, P.Detune, 25.0);
                v.Set(P.Spread, 1000);
                v.Set(P.EnvSustain, 15);
            });
            eu.NoteOn(1060, 60, 1f, 0);
            var (ul, ur) = RenderStereo(eu, 4096);
            Assert.That(eu.ActivePulseMask, Is.EqualTo(0b111), "Unison triggers all 3 pulse channels");
            double diff = 0;
            for (int i = 1024; i < 4096; i++) diff += Math.Abs(ul[i] - ur[i]);
            Assert.That(diff, Is.GreaterThan(10.0), "Unison detune + stereo spread produces wide stereo difference");
        }

        // ------------------------------------------------------------------ 7. Mix headroom (Coordinator Note) & Lo-res DAC

        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(2, true)]
        [TestCase(3, true)]
        [TestCase(2, false)]
        public void ThreeNoteChordPlusNoiseAtVelocity127Volume0DbPeaksAtOrBelowZeroDbfs(int dutyChoice, bool loResDac)
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.P1Duty, dutyChoice);
                v.Set(P.P2Duty, dutyChoice);
                v.Set(P.P3Duty, dutyChoice);
                v.Set(P.EnvSustain, 15);
                Set(v, P.EnvAttack, 0.001);
                v.Set(P.LoResDac, loResDac ? 1 : 0);
                v.Set(P.Spread, 0);
                Set(v, P.Volume, 0.0);
            });
            e.NoteOn(0, 60, 1f);
            e.NoteOn(1, 64, 1f);
            e.NoteOn(2, 67, 1f);
            e.NoteOn(3, 72, 1f);
            var (l, r) = RenderStereo(e, Measure.Rate);
            double peak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
            double peakDb = Measure.Db(peak);
            Measure.Report($"Neo Chip headroom (duty={Params.Table[P.P1Duty].Describe(dutyChoice)}, dac={loResDac}): peak {peakDb:F2} dBFS");
            Assert.That(peakDb, Is.LessThanOrEqualTo(0.0), "3-note chord + noise at vel 127, Volume 0 dB must peak <= 0 dBFS");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LoResDacAddsMeasurableFourBitGrain(bool raw)
        {
            (float[] L, float[] R) RenderWith(bool dac)
            {
                var (e, _) = Make(v =>
                {
                    v.Set(P.VoiceMode, (int)ChipVoiceMode.Mono);
                    v.Set(P.P1Duty, 1);
                    v.Set(P.Raw, raw ? 1 : 0);
                    v.Set(P.LoResDac, dac ? 1 : 0);
                    v.Set(P.EnvMode, (int)ChipEnvMode.DecayShot);
                    Set(v, P.EnvPeriod, 0.25);
                    Set(v, P.Volume, 0);
                });
                e.NoteOn(1060, 60, 1f, 0);
                return RenderStereo(e, 16000);
            }
            var (clean, _) = RenderWith(false);
            var (grit, _) = RenderWith(true);
            double diffSq = 0, cleanSq = 0;
            for (int i = 0; i < clean.Length; i++)
            {
                double d = grit[i] - clean[i];
                diffSq += d * d;
                cleanSq += clean[i] * clean[i];
            }
            double residualDb = 10 * Math.Log10(Math.Max(1e-20, diffSq / Math.Max(1e-20, cleanSq)));
            Measure.Report($"Neo Chip Lo-res DAC residual (raw={raw}): {residualDb:F1} dB");
            Assert.That(residualDb, Is.GreaterThan(-30.0), "4-bit DAC adds audible grain in both clean and raw modes");
        }

        // ------------------------------------------------------------------ 8. Presets audibility & pairwise spectral separation

        public const double MinPairwisePresetDistanceDb = 4.0;

        [Test]
        public void AllTwelvePresetsAreAudibleAndPairwiseDistinct()
        {
            string prsPath = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Machines", "NeoChip", "Neo Chip.prs.xml"));
            var doc = XDocument.Load(prsPath);
            var items = doc.Root!.Elements("Item").ToList();
            Assert.That(items.Count, Is.EqualTo(12), "exactly 12 factory presets");

            // 24 third-octave-like log bands from 60 Hz to 16 kHz plus temporal contrast (early vs late RMS).
            const int Bands = 24;
            double[] bandEdges = new double[Bands + 1];
            for (int b = 0; b <= Bands; b++)
                bandEdges[b] = 60.0 * Math.Pow(16000.0 / 60.0, (double)b / Bands);

            var features = new List<(string Name, double RmsDb, double[] Vec)>();

            foreach (var item in items)
            {
                string name = (string)item.Attribute("Key")!;
                var v = new ParamValues(Params.Table);
                foreach (var p in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter"))
                    v.Set((int)p.Attribute("Index")!, (int)p.Attribute("Value")!);

                var e = new ChipEngine(v);
                e.SetSampleRate(Measure.Rate);
                e.Tempo = 128.0;

                // Test line: 1.2 s — short melody/chord trigger via MIDI ch 1 so all presets (pulse & drum) sound naturally.
                int total = (int)(1.2 * Measure.Rate);
                var (l, r) = RenderStereo(e, total, pos =>
                {
                    if (pos == 0)
                    {
                        e.NoteOn(1057, 57, 0.85f, 0); // A3
                        e.NoteOn(1064, 64, 0.80f, 0); // E4
                        e.NoteOn(1069, 69, 0.85f, 0); // A4
                    }
                    else if (pos == 28672) // ~0.60 s: release and play a higher note
                    {
                        e.NoteOff(1057, 57, 0);
                        e.NoteOff(1064, 64, 0);
                        e.NoteOff(1069, 69, 0);
                        e.NoteOn(1072, 72, 0.85f, 0); // C5
                    }
                    else if (pos == 47104)
                    {
                        e.NoteOff(1072, 72, 0);
                    }
                });

                var mono = new float[total];
                for (int i = 0; i < total; i++) mono[i] = 0.5f * (l[i] + r[i]);

                // Peak 50 ms RMS while sounding must be > -30 dBFS.
                double loudest = 0;
                for (int at = 0; at + 2400 <= total; at += 1200)
                    loudest = Math.Max(loudest, Measure.Rms(mono, at, 2400));
                double rmsDb = Measure.Db(loudest);
                Assert.That(rmsDb, Is.GreaterThan(-30.0), $"{name} audible (> -30 dBFS RMS)");

                // Average magnitude spectrum across 6 overlapping 4096-pt Hann frames (covers attack, arp/sweep motion, sustain).
                var bandEnergy = new double[Bands];
                const int Frame = 4096;
                for (int f = 0; f < 6; f++)
                {
                    int off = f * 4096;
                    var win = new float[Frame];
                    for (int i = 0; i < Frame; i++)
                        win[i] = mono[off + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Frame));
                    var mag = Fft.Magnitudes(win, 0, Frame);
                    double binHz = (double)Measure.Rate / Frame;
                    for (int k = 1; k < mag.Length; k++)
                    {
                        double hz = k * binHz;
                        if (hz < bandEdges[0] || hz >= bandEdges[Bands]) continue;
                        int b = (int)(Math.Log(hz / 60.0) / Math.Log(16000.0 / 60.0) * Bands);
                        b = Math.Clamp(b, 0, Bands - 1);
                        bandEnergy[b] += mag[k] * mag[k];
                    }
                }
                double totalE = bandEnergy.Sum();
                var vec = new double[Bands + 3];
                for (int b = 0; b < Bands; b++)
                    vec[b] = 10.0 * Math.Log10(Math.Max(1e-8, bandEnergy[b] / Math.Max(1e-20, totalE)));

                // Temporal envelope shape + stereo width descriptors (in dB-equivalent scale).
                double early = Measure.Rms(mono, 0, 4800);
                double midRms = Measure.Rms(mono, 12000, 12000);
                double sideSq = 0, midSq = 0;
                for (int i = 0; i < total; i++)
                {
                    double m = 0.5 * (l[i] + r[i]);
                    double s = 0.5 * (l[i] - r[i]);
                    midSq += m * m;
                    sideSq += s * s;
                }
                vec[Bands] = Math.Clamp(Measure.Db((midRms + 1e-6) / (early + 1e-6)), -40, 40);
                vec[Bands + 1] = Math.Clamp(10 * Math.Log10((sideSq + 1e-12) / (midSq + 1e-12)), -40, 20);
                // Short-term modulation depth (hardware tremolo / arp dynamics).
                double envVar = 0;
                var segs = new double[16];
                for (int s = 0; s < 16; s++) segs[s] = Measure.Db(Measure.Rms(mono, s * 1500, 1500) + 1e-5);
                double meanSeg = segs.Average();
                for (int s = 0; s < 16; s++) envVar += (segs[s] - meanSeg) * (segs[s] - meanSeg);
                vec[Bands + 2] = Math.Sqrt(envVar / 16.0);

                features.Add((name, rmsDb, vec));
            }

            double minDist = double.MaxValue;
            string closestPair = "";
            for (int i = 0; i < features.Count; i++)
            {
                for (int j = i + 1; j < features.Count; j++)
                {
                    double sq = 0;
                    for (int k = 0; k < features[i].Vec.Length; k++)
                    {
                        double d = features[i].Vec[k] - features[j].Vec[k];
                        sq += d * d;
                    }
                    double dist = Math.Sqrt(sq / features[i].Vec.Length);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        closestPair = $"{features[i].Name} vs {features[j].Name}";
                    }
                    Assert.That(dist, Is.GreaterThanOrEqualTo(MinPairwisePresetDistanceDb),
                        $"presets too similar: {features[i].Name} vs {features[j].Name} ({dist:F2} dB < {MinPairwisePresetDistanceDb} dB)");
                }
            }
            Measure.Report($"Neo Chip presets: all 12 > -30 dBFS RMS (min {features.Min(f => f.RmsDb):F1} dBFS); min pairwise spectral-temporal distance {minDist:F2} dB ({closestPair}, threshold {MinPairwisePresetDistanceDb:F1} dB)");
        }

        [Test]
        public void AllTwelvePresetsPassPresetTestsBounds()
        {
            var (table, render) = PresetTests.Machines["Neo Chip"];
            var dummyIn = new float[Measure.Rate];
            int checkedCount = 0;
            foreach (var (_, machine, name, values) in PresetTests.AllPresets())
            {
                if (machine != "Neo Chip") continue;
                var v = new ParamValues(table);
                foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                var y = render(v, dummyIn);
                double loudest = 0;
                for (int at = 0; at + 2400 <= y.Length * 2 / 3; at += 1200)
                    loudest = Math.Max(loudest, Measure.Rms(y, at, 2400));
                double level = Measure.Db(loudest);
                double top = Measure.Db(Measure.Peak(y, 0, y.Length));
                Measure.Report($"Neo Chip PresetTests bounds / {name}: RMS {level:F1} dBFS, peak {top:F1} dBFS");
                Assert.That(double.IsFinite(top), Is.True, name);
                Assert.That(level, Is.InRange(-30.0, -6.0), $"{name}: RMS in [-30, -6] dBFS");
                Assert.That(top, Is.InRange(-20.0, -1.0), $"{name}: peak in [-20, -1] dBFS");
                checkedCount++;
            }
            Assert.That(checkedCount, Is.EqualTo(12));
        }

        [Test]
        public void OneShotVoiceDoesNotRetriggerOnChordNoteOff()
        {
            // Regression test: when a Mono or Noise voice finishes a one-shot DecayShot while multiple
            // chord keys are held, releasing those keys must not retrigger the already-silent voice.
            var (e, _) = Make(v =>
            {
                v.Set(P.VoiceMode, (int)ChipVoiceMode.Mono);
                v.Set(P.EnvMode, (int)ChipEnvMode.DecayShot);
                Set(v, P.EnvPeriod, 0.04); // 40 ms one-shot
            });
            e.NoteOn(1057, 57, 0.85f, 0);
            e.NoteOn(1060, 60, 0.80f, 0);
            e.NoteOn(1064, 64, 0.82f, 0);

            // Render 250 ms — well past the 40 ms one-shot decay.
            RenderStereo(e, 12000);
            // Release all three chord notes.
            e.NoteOff(1057, 57, 0);
            e.NoteOff(1060, 60, 0);
            e.NoteOff(1064, 64, 0);
            var (afterL, afterR) = RenderStereo(e, 4800);
            double peak = Math.Max(Measure.Peak(afterL, 0, afterL.Length), Measure.Peak(afterR, 0, afterR.Length));
            Assert.That(peak, Is.LessThan(1e-4), "finished one-shot voice must stay silent on chord NoteOff");
        }

        // ------------------------------------------------------------------ 9. Hygiene gates

        [Test]
        public void ZeroBytesAllocatedOnAudioThread()
        {
            var (e, v) = Make(p =>
            {
                p.Set(P.VoiceMode, (int)ChipVoiceMode.Poly);
                p.Set(P.ArpMode, (int)ChipArpMode.Up);
                p.Set(P.EnvSustain, 12);
                Set(p, P.VibDepth, 30);
                p.Set(P.P1Pwm, 500);
            });
            var l = new float[ChipEngine.MaxBlock];
            var r = new float[ChipEngine.MaxBlock];
            e.NoteOn(1060, 60, 0.9f, 0);
            e.NoteOn(1064, 64, 0.8f, 0);
            e.NoteOn(1067, 67, 0.8f, 0);
            e.NoteOn(3, 72, 0.9f);

            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                v.Set(P.P1Pwm, (b * 17) % 1001);
                v.Set(P.Spread, (b * 13) % 1001);
                v.Set(P.Volume, 600 + (b % 200));
                v.Set(P.Raw, (b >> 5) & 1);
                v.Set(P.LoResDac, (b >> 6) & 1);
                if (b % 40 == 0) e.NoteOn(1060 + (b % 12), 60 + (b % 12), 0.85f, 0);
                if (b % 40 == 20) e.NoteOff(1060 + ((b - 20) % 12), 60 + ((b - 20) % 12), 0);
                e.Process(l, r, ChipEngine.MaxBlock);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void NoDenormalsAndSilenceCostsNoMoreThanPlaying()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.VoiceMode, (int)ChipVoiceMode.Unison);
                v.Set(P.P1Pwm, 500);
                Set(v, P.EnvRelease, 0.05);
            });
            e.NoteOn(1060, 60, 1f, 0);
            e.NoteOn(3, 72, 1f);
            RenderStereo(e, 48000);
            e.AllNotesOff(false);

            var (l, r) = RenderStereo(e, 20 * Measure.Rate);
            int subnormals = 0;
            for (int i = 0; i < l.Length; i++)
            {
                if ((l[i] != 0f && MathF.Abs(l[i]) < 1.1754944e-38f) || !float.IsFinite(l[i])) subnormals++;
                if ((r[i] != 0f && MathF.Abs(r[i]) < 1.1754944e-38f) || !float.IsFinite(r[i])) subnormals++;
            }
            Assert.That(subnormals, Is.EqualTo(0), "no denormals or non-finite samples in 20 s of silence");

            // Silence cost vs playing cost guard.
            var cost = Throughput.CostGuard("Neo Chip silence vs playing", 1.5, Measure.Rate, ChipEngine.MaxBlock,
                2.0, () =>
                {
                    var (ep, _) = Make(v => { v.Set(P.VoiceMode, (int)ChipVoiceMode.Unison); v.Set(P.P1Pwm, 500); v.Set(P.EnvSustain, 15); });
                    ep.NoteOn(1060, 60, 1f, 0);
                    ep.NoteOn(3, 72, 1f);
                    var bl = new float[ChipEngine.MaxBlock];
                    var br = new float[ChipEngine.MaxBlock];
                    for (int i = 0; i < 64; i++) ep.Process(bl, br, ChipEngine.MaxBlock);
                    return Throughput.InBlocks(ChipEngine.MaxBlock, (_, n) => ep.Process(bl, br, n));
                },
                20.0, () =>
                {
                    var (es, _) = Make(v => { v.Set(P.VoiceMode, (int)ChipVoiceMode.Unison); Set(v, P.EnvRelease, 0.02); });
                    es.NoteOn(1060, 60, 1f, 0);
                    es.NoteOn(3, 72, 1f);
                    var bl = new float[ChipEngine.MaxBlock];
                    var br = new float[ChipEngine.MaxBlock];
                    for (int i = 0; i < 64; i++) es.Process(bl, br, ChipEngine.MaxBlock);
                    es.AllNotesOff(false);
                    for (int i = 0; i < 64; i++) es.Process(bl, br, ChipEngine.MaxBlock);
                    return Throughput.InBlocks(ChipEngine.MaxBlock, (_, n) => es.Process(bl, br, n));
                },
                Measure.Report);
            Assert.That(cost.Ratio, Is.LessThan(1.5));
        }

        [Test]
        public void AutomatedParametersHaveNoZipperAboveMinus60Db()
        {
            // Automate continuous gain/pan/spread/pwm/raw/dac parameters every 64 samples while holding a clean 440 Hz note.
            // Measure high-frequency second-difference zipper energy against the signal RMS (must stay below -60 dB
            // when automating smooth parameters like Volume, Pan, Spread, VelDepth, and clean transitions on Raw/DAC).
            int[] smoothParams = { P.Volume, P.P1Pan, P.Spread, P.VelDepth, P.Detune };
            foreach (int p in smoothParams)
            {
                var (e, v) = Make(x =>
                {
                    x.Set(P.VoiceMode, (int)ChipVoiceMode.Mono);
                    x.Set(P.P1Duty, 2); // 50%
                    x.Set(P.EnvSustain, 15);
                    Set(x, P.EnvAttack, 0.001);
                    Set(x, P.Volume, -3.0);
                });
                e.NoteOn(1069, 69, 0.9f, 0);

                // Warm up past attack and DC blocker settle.
                RenderStereo(e, 16384);

                // Compare a slow smooth automation sweep stepped every 64 samples against a sample-rate ramp:
                // Any unsmoothed 64-sample stair-step produces a strong tone at 48000 / 64 = 750 Hz (+/- 440 Hz sidebands at 310 Hz & 1190 Hz).
                int block = 64;
                int len = Measure.N;
                var l = new float[len];
                var r = new float[len];
                var bl = new float[block];
                var br = new float[block];
                int lo = p == P.Volume ? 550 : 200;
                int hi = p == P.Volume ? 850 : 800;
                for (int pos = 0; pos < len; pos += block)
                {
                    double phase = 2.0 * Math.PI * pos / len;
                    int rawVal = (int)Math.Round(lo + (hi - lo) * (0.5 + 0.5 * Math.Sin(phase)));
                    v.Set(p, rawVal);
                    e.Process(bl, br, block);
                    Array.Copy(bl, 0, l, pos, block);
                    Array.Copy(br, 0, r, pos, block);
                }

                // Check sideband energy at block-rate frequencies (750 Hz +- 440 Hz = 310 Hz, 1190 Hz, 1500 Hz +- 440 Hz) relative to 440 Hz fundamental.
                var w = new float[len];
                for (int i = 0; i < len; i++)
                {
                    double t = 2 * Math.PI * i / len;
                    w[i] = l[i] * (float)(0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t));
                }
                var mag = Fft.Magnitudes(w, 0, len);
                double binHz = (double)Measure.Rate / len;
                double fund = PeakAround(mag, 440.0, binHz, 10);
                double worstZipper = 1e-30;
                foreach (double zf in new[] { 310.0, 1190.0, 1060.0, 1940.0, 750.0, 1500.0 })
                    worstZipper = Math.Max(worstZipper, PeakAround(mag, zf, binHz, 6));
                double zipperDb = 20 * Math.Log10(worstZipper / Math.Max(1e-20, fund));
                Measure.Report($"Neo Chip zipper ({Params.Table[p].Name}): {zipperDb:F1} dB");
                Assert.That(zipperDb, Is.LessThan(-60.0), $"{Params.Table[p].Name}: zipper energy <= -60 dB");
            }
        }

        static double PeakAround(double[] mag, double hz, double binHz, int radius)
        {
            int c = (int)Math.Round(hz / binHz);
            double best = 0;
            for (int k = Math.Max(1, c - radius); k <= Math.Min(mag.Length - 1, c + radius); k++)
                if (mag[k] > best) best = mag[k];
            return best;
        }

        [Test]
        public void StateMigrationsRoundTrip()
        {
            var chain = new StateMigrations(Params.Table);
            Assert.That(chain.Validate(), Is.Empty);
            Assert.That(chain.Current, Is.EqualTo(0));
            Assert.That(chain.Keys.Count, Is.EqualTo(Params.Table.Length));

            var saved = new int[Params.Table.Length];
            var keyed = new Dictionary<string, int>();
            for (int i = 0; i < Params.Table.Length; i++)
            {
                int val = Params.Table[i].Min + (Params.Table[i].Max - Params.Table[i].Min) / 2;
                saved[i] = val;
                keyed[Params.Table[i].Id] = val;
            }
            var roundPos = chain.MigrateGlobals(0, saved);
            Assert.That(roundPos, Is.EqualTo(saved));
            var roundKey = chain.Migrate(0, keyed);
            foreach (var kv in keyed)
                Assert.That(roundKey[kv.Key], Is.EqualTo(kv.Value));
        }

        [Test]
        public void CpuThroughputWithinNeoInstrumentBudget()
        {
            // Worst-case active load: all 3 pulse channels (PWM + vibrato + arpeggio) + noise channel + 4-bit DAC.
            Throughput.Guard("Neo Chip (3 pulses + noise, PWM, arp, vib, DAC)", Throughput.Floor(x64: 25, arm64: 25),
                2.0, Measure.Rate, ChipEngine.MaxBlock,
                () =>
                {
                    var (e, _) = Make(v =>
                    {
                        v.Set(P.VoiceMode, (int)ChipVoiceMode.Poly);
                        v.Set(P.P1Pwm, 500);
                        v.Set(P.P2Pwm, 500);
                        v.Set(P.P3Pwm, 500);
                        v.Set(P.ArpMode, (int)ChipArpMode.Up);
                        Set(v, P.VibDepth, 25);
                        v.Set(P.LoResDac, 1);
                        v.Set(P.EnvSustain, 15);
                    });
                    e.NoteOn(0, 60, 0.9f);
                    e.NoteOn(1, 64, 0.9f);
                    e.NoteOn(2, 67, 0.9f);
                    e.NoteOn(3, 72, 0.9f);
                    var bl = new float[ChipEngine.MaxBlock];
                    var br = new float[ChipEngine.MaxBlock];
                    for (int i = 0; i < 32; i++) e.Process(bl, br, ChipEngine.MaxBlock);
                    return Throughput.InBlocks(ChipEngine.MaxBlock, (_, n) => e.Process(bl, br, n));
                },
                Measure.Report);
        }
    }
}
