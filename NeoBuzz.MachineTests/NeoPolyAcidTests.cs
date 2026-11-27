using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.Poly;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Poly's acid extension (AC1, docs/specs/legacy-machines.md G3): Diode 18, accent and slide, measured.</summary>
    [TestFixture]
    public class NeoPolyAcidTests
    {
        const int Rate = Measure.Rate;

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        // ── Diode 18 ────────────────────────────────────────────────────────

        static double DiodeGainDb(double cutoff, double hz, double reso = 0)
        {
            var f = new Diode18();
            f.Set((float)Math.Tan(Math.PI * cutoff / Rate), (float)reso);
            int n = 16384, skip = 8192;
            var y = new float[n];
            for (int i = 0; i < n; i++) y[i] = f.Process((float)(0.01 * Math.Sin(2 * Math.PI * hz * i / Rate)), 1f);
            return Measure.Db(Measure.Rms(y, skip, n - skip) / (0.01 / Math.Sqrt(2)));
        }

        [TestCase(250.0, 1000.0)]
        [TestCase(500.0, 2000.0)]
        [TestCase(1000.0, 3000.0)]
        public void Diode18SlopeIs18DbPerOctave(double cutoff, double f1)
        {
            double slope = DiodeGainDb(cutoff, 2 * f1) - DiodeGainDb(cutoff, f1);
            Measure.Report($"Diode 18, cutoff {cutoff:F0} Hz, {f1:F0} to {2 * f1:F0} Hz: {slope:F2} dB/octave");
            Assert.That(slope, Is.InRange(-19.5, -16.5));
        }

        [Test]
        public void Diode18PassbandIsUnityAtZeroResonance()
        {
            double g = DiodeGainDb(2000, 50);
            Measure.Report($"Diode 18 passband at 50 Hz, cutoff 2 kHz: {g:F2} dB");
            Assert.That(Math.Abs(g), Is.LessThan(0.3));
        }

        [Test]
        public void Diode18KeepsMoreBassThanTheLadderAtTheSameResonance()
        {
            double lad(double reso)
            {
                var f = new Ladder();
                f.Set((float)Math.Tan(Math.PI * 1000.0 / Rate), (float)reso);
                int n = 16384;
                var y = new float[n];
                for (int i = 0; i < n; i++) y[i] = f.Process((float)(0.01 * Math.Sin(2 * Math.PI * 40 * i / Rate)), 1f, false);
                return Measure.Db(Measure.Rms(y, 8192, n - 8192) / (0.01 / Math.Sqrt(2)));
            }
            foreach (double reso in new[] { 0.3, 0.5, 0.7 })
            {
                double d = DiodeGainDb(1000, 40, reso), l = lad(reso);
                Measure.Report($"Resonance {reso:P0}, 40 Hz under a 1 kHz cutoff: Diode 18 {d:F2} dB, ladder 24 {l:F2} dB");
                Assert.That(d, Is.GreaterThan(l + 1.0), $"resonance {reso}");
            }
        }

        /// <summary>A filter-only voice: oscillators off, a trace of noise to start it, no drift, filter 1 on the key.</summary>
        static (PolyEngine E, ParamValues V) SelfOscillator(int note, double cutoff)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.F1Type, 6);
            v.Set(P.AcidRange, Params.Table[P.AcidRange].Max);   // the filter's own pitch: not the acid range's soft ceiling
            Set(v, P.F1Cutoff, cutoff);
            v.Set(P.F1Reso, 1000);
            v.Set(P.F1Key, 1000);
            v.Set(P.F1Env, 500);
            v.Set(P.F1Vel, 0);
            v.Set(P.Drift, 0);
            v.Set(P.O1Level, 0);
            v.Set(P.NoiseLevel, 2);
            v.Set(P.AmpS, 1000);
            for (int m = 0; m < 8; m++) v.Set(P.M1Src + 3 * m, 0);
            var e = new PolyEngine(v);
            e.SetSampleRate(Rate);
            e.NoteOn(0, note, 1f);
            return (e, v);
        }

        [TestCase(48, 500.0)]
        [TestCase(60, 500.0)]
        [TestCase(72, 500.0)]
        [TestCase(60, 200.0)]
        [TestCase(60, 2000.0)]
        public void Diode18SelfOscillatesAtTheCutoffAndTracksPitch(int note, double cutoff)
        {
            var (e, _) = SelfOscillator(note, cutoff);
            var y = PolyTests.Render(e, 4 * Rate / 2 + 32768);
            double want = cutoff * Math.Pow(2, (note - 60) / 12.0);
            double got = Measure.PeakFrequency(y, 2 * Rate, 32768);
            Measure.Report($"Diode 18, note {note}, cutoff {cutoff:F0} Hz: self-oscillation at {got:F1} Hz (cutoff after the key {want:F1} Hz, {100 * (got / want - 1):+0.00;-0.00} %)");
            Assert.That(Measure.Peak(y, 2 * Rate, 32768), Is.GreaterThan(0.01), "it sounds");
            Assert.That(Math.Abs(got / want - 1), Is.LessThan(0.01));
        }

        [Test]
        public void Diode18DoesNotOscillateBelowTheTop()
        {
            var (e, v) = SelfOscillator(60, 500);
            v.Set(P.F1Reso, 700);
            var y = PolyTests.Render(e, 3 * Rate);
            // A trace of noise stays a trace: it rings, it does not grow into a tone.
            Assert.That(Measure.Peak(y, 2 * Rate, Rate), Is.LessThan(0.01));
        }

        // ── accent ──────────────────────────────────────────────────────────

        /// <summary>Mono voice, one note at a time, the filter envelope and accent set; the depth (octaves) the filter envelope reached at its peak for each note.</summary>
        static float[] AccentPeaks(Action<ParamValues> setup, float[] velocities, double gapSeconds)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.VoiceMode, 1);
            v.Set(P.Drift, 0);
            v.Set(P.F1Env, 650);              // +30 % = 1.8 octaves
            v.Set(P.AmpS, 1000);
            setup?.Invoke(v);
            var e = new PolyEngine(v);
            e.SetSampleRate(Rate);
            var l = new float[256];
            var r = new float[256];
            var peaks = new float[velocities.Length];
            int gap = (int)(gapSeconds * Rate) / 256 * 256;
            for (int i = 0; i < velocities.Length; i++)
            {
                e.NoteOn(0, 48, velocities[i]);
                for (int pos = 0; pos < gap; pos += 256)
                {
                    if (pos == gap - 4096) e.NoteOff(0);
                    e.Process(l, r, 256);
                    peaks[i] = Math.Max(peaks[i], PeakOf(e));
                }
            }
            return peaks;
        }

        static float PeakOf(PolyEngine e)
        {
            float best = 0;
            for (int i = 0; i < PolyEngine.VoiceCount; i++) best = Math.Max(best, e.Voice(i).EnvDepthOct);
            return best;
        }

        [Test]
        public void AccentRaisesTheFilterEnvelopePeakByTheAccentAmountOnAccentedNotesOnly()
        {
            // Threshold 110: velocities 109 and 110 either side of it, a soft one and a full one.
            var vel = new[] { 109 / 127f, 110 / 127f, 60 / 127f, 127 / 127f };
            var off = AccentPeaks(v => v.Set(P.Accent, 0), vel, 0.4);
            double amount = 0.4 * 6;   // Accent 40 % = 2.4 octaves of depth
            var on = AccentPeaks(v => Set(v, P.Accent, 0.4), vel, 0.4);
            Measure.Report($"Filter envelope peak (octaves) with Accent 0: {string.Join(", ", off.Select(x => x.ToString("F3")))}; with Accent 40 %: {string.Join(", ", on.Select(x => x.ToString("F3")))}");
            Assert.That(off[0], Is.GreaterThan(1.0f), "the envelope reaches its peak");
            Assert.That(on[0] - off[0], Is.EqualTo(0).Within(1e-4), "109: not accented");
            Assert.That(on[1] - off[1], Is.EqualTo(amount).Within(0.005), "110: accented, at the threshold");
            Assert.That(on[2] - off[2], Is.EqualTo(0).Within(1e-4), "60: not accented");
            Assert.That(on[3] - off[3], Is.EqualTo(amount).Within(0.005), "127: accented");
        }

        [Test]
        public void AccentThresholdMovesWhichNotesAreAccented()
        {
            var vel = new[] { 80 / 127f, 100 / 127f };
            var on = AccentPeaks(v => { Set(v, P.Accent, 0.4); Set(v, P.AccentThr, 70); }, vel, 0.4);
            var off = AccentPeaks(v => Set(v, P.AccentThr, 70), vel, 0.4);
            Assert.That(on[0] - off[0], Is.EqualTo(2.4).Within(0.005), "80 is above 70");
            Assert.That(on[1] - off[1], Is.EqualTo(2.4).Within(0.005));
        }

        [Test]
        public void AccentShortensTheFilterDecayAndLiftsTheLevelOnAccentedNotesOnly()
        {
            double Early(float vel, double decay, double level)
            {
                var v = new ParamValues(Params.Table);
                v.Set(P.VoiceMode, 1); v.Set(P.Drift, 0); v.Set(P.F1Env, 500); v.Set(P.AmpS, 1000);
                Set(v, P.AccentLevel, level);
                var e = new PolyEngine(v);
                e.SetSampleRate(Rate);
                e.NoteOn(0, 36, vel);
                var y = PolyTests.Render(e, 9600);
                return Measure.Rms(y, 4800, 4800);
            }
            // Level: +6 dB on the accented note, none on the other.
            double plain = Early(0.5f, 0.0005, 0), soft = Early(0.5f, 0.0005, 6), hard = Early(1f, 0.0005, 0), hardLift = Early(1f, 0.0005, 6);
            Assert.That(Measure.Db(soft / plain), Is.EqualTo(0).Within(0.05), "not accented: no lift (a patch with an Accent level is an acid patch: its filter is oversampled and its cutoff rate-limited, which differs from the plain path by hundredths of a dB)");
            Assert.That(Measure.Db(hardLift / hard), Is.EqualTo(6).Within(0.2), "accented: +6 dB");

            // Decay: measured on the filter envelope as the time its contribution takes to fall to half the peak.
            double HalfTime(float vel, double decay)
            {
                var v = new ParamValues(Params.Table);
                v.Set(P.VoiceMode, 1); v.Set(P.Drift, 0); v.Set(P.F1Env, 800); v.Set(P.FltS, 0); v.Set(P.AmpS, 1000);
                Set(v, P.FltD, 0.8);
                Set(v, P.AccentDecay, decay);
                var e = new PolyEngine(v);
                e.SetSampleRate(Rate);
                e.NoteOn(0, 36, vel);
                var l = new float[16];
                var r = new float[16];
                float peak = 0;
                for (int i = 0; i < 3 * Rate / 16; i++)
                {
                    e.Process(l, r, 16);
                    float now = e.Voice(0).EnvNowOct;
                    peak = Math.Max(peak, now);
                    if (peak > 0.5f && now < 0.5f * peak) return i * 16.0 / Rate;
                }
                return double.NaN;
            }
            double longDecay = HalfTime(0.5f, 0.1), accentedShort = HalfTime(1f, 0.1), accentedLong = HalfTime(1f, 20), unaccentedShort = HalfTime(0.5f, 0.1);
            Measure.Report($"Filter envelope half-time: plain {longDecay:F3} s, accented with Accent decay 100 ms {accentedShort:F3} s, accented with it off {accentedLong:F3} s");
            Assert.That(accentedShort, Is.LessThan(0.5 * longDecay), "the accented decay is shorter");
            Assert.That(unaccentedShort, Is.EqualTo(longDecay).Within(0.002), "a plain note keeps the decay");
            Assert.That(accentedLong, Is.EqualTo(longDecay).Within(0.002), "at the top the decay stays");
        }

        [Test]
        public void ARunOfAccentsRaisesThePeakToTheCapAndNoFurtherAndItRelaxes()
        {
            var vel = Enumerable.Repeat(1f, 12).ToArray();
            double cap = 1.5;
            Action<ParamValues> setup = v => { Set(v, P.Accent, 0.2); Set(v, P.AccentSweep, cap); };
            var p = AccentPeaks(setup, vel, 0.125);
            var none = AccentPeaks(v => Set(v, P.Accent, 0.2), new[] { 1f }, 0.4)[0];
            Measure.Report("Accent run (125 ms apart), peak above one lone accent (octaves): " + string.Join(", ", p.Select(x => (x - none).ToString("F3"))));
            for (int i = 1; i < p.Length; i++) Assert.That(p[i], Is.GreaterThanOrEqualTo(p[i - 1] - 1e-4), $"note {i} does not fall");
            Assert.That(p[0] - none, Is.EqualTo(cap / 3).Within(0.005), "the first accent adds a third of the cap");
            Assert.That(p[^1] - none, Is.EqualTo(cap).Within(0.005), "the run ends at the cap");
            Assert.That(p[^1], Is.EqualTo(p[^2]).Within(1e-3), "and goes no further");
            Assert.That(p.Max() - none, Is.LessThanOrEqualTo(cap + 0.005));
            // It relaxes: a lone accent after a long pause is back near one accent's swell.
            var gapped = AccentPeaks(setup, new[] { 1f, 1f }, 3.0);
            Assert.That(gapped[1] - none, Is.EqualTo(cap / 3).Within(0.01), "a pause of 3 s lets the swell go");
            // Not accented notes in the run take no swell.
            var mixed = AccentPeaks(setup, new[] { 1f, 1f, 0.3f }, 0.125);
            var plain = AccentPeaks(v => { Set(v, P.Accent, 0.2); Set(v, P.AccentSweep, cap); }, new[] { 0.3f }, 0.4)[0];
            Assert.That(mixed[2], Is.EqualTo(plain).Within(1e-4), "a plain note in a run is plain");
        }

        // ── slide ───────────────────────────────────────────────────────────

        /// <summary>Time (s) a second, overlapping note takes to bring the pitch to its own; the amp envelope's level before and after, to see it was not retriggered.</summary>
        static (double Seconds, float LevelBefore, float LevelAfter, int Active) SlideOf(Action<ParamValues> setup, int from, int to)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.VoiceMode, 2);
            v.Set(P.Drift, 0);
            v.Set(P.Slide, 1);
            Set(v, P.AmpA, 0.4);               // a long attack: a retrigger would send the level back to zero
            v.Set(P.AmpS, 1000);
            setup?.Invoke(v);
            var e = new PolyEngine(v);
            e.SetSampleRate(Rate);
            var l = new float[16];
            var r = new float[16];
            e.NoteOn(0, from, 1f);
            for (int i = 0; i < Rate / 4 / 16; i++) e.Process(l, r, 16);
            float before = e.Voice(0).Loudness;
            e.NoteOn(1, to, 1f);
            int blocks = 0;
            float after = 0;
            while (blocks < Rate / 16)
            {
                e.Process(l, r, 16);
                blocks++;
                if (blocks == 1) after = e.Voice(0).Loudness;
                if (Math.Abs(e.Voice(0).Pitch - to) < 1e-4f) break;
            }
            return (blocks * 16.0 / Rate, before, after, e.ActiveVoices);
        }

        [TestCase(36, 43, 60)]
        [TestCase(36, 38, 60)]
        [TestCase(48, 36, 60)]     // down an octave: the same time
        [TestCase(36, 43, 8)]
        [TestCase(36, 43, 200)]
        [TestCase(36, 43, 550)]
        public void SlideTakesTheSlideTimeForAnyInterval(int from, int to, int ms)
        {
            var (t, before, after, active) = SlideOf(v => Set(v, P.SlideTime, ms / 1000.0), from, to);
            double want = Params.Table[P.SlideTime].Value(Params.Table[P.SlideTime].Raw(ms / 1000.0));
            Measure.Report($"Slide {from} to {to}, Slide time {want * 1000:F1} ms: pitch arrives after {t * 1000:F1} ms");
            Assert.That(t, Is.EqualTo(want).Within(want * 0.05 + 0.0005));
            Assert.That(active, Is.EqualTo(1), "one voice");
            Assert.That(after, Is.GreaterThanOrEqualTo(before), "the amp envelope goes on, it was not retriggered");
        }

        [TestCase(0)]   // glide off
        [TestCase(1)]   // glide always, a long glide time
        [TestCase(2)]   // glide legato
        public void SlideIgnoresTheGlideMode(int glideMode)
        {
            var (t, before, after, _) = SlideOf(v => { v.Set(P.GlideMode, glideMode); v.Set(P.Glide, 900); }, 36, 43);
            Assert.That(t, Is.EqualTo(0.06).Within(0.0035));
            Assert.That(after, Is.GreaterThanOrEqualTo(before));
        }

        [Test]
        public void SlideInMonoModeDoesNotRetriggerEither()
        {
            var (t, before, after, _) = SlideOf(v => v.Set(P.VoiceMode, 1), 36, 43);
            Assert.That(t, Is.EqualTo(0.06).Within(0.0035));
            Assert.That(after, Is.GreaterThanOrEqualTo(before));
        }

        [Test]
        public void ANoteThatDoesNotOverlapDoesNotSlide()
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.VoiceMode, 2);
            v.Set(P.Slide, 1);
            v.Set(P.Drift, 0);
            var e = new PolyEngine(v);
            e.SetSampleRate(Rate);
            var l = new float[16];
            var r = new float[16];
            e.NoteOn(0, 36, 1f);
            for (int i = 0; i < 100; i++) e.Process(l, r, 16);
            e.NoteOff(0);
            for (int i = 0; i < 20; i++) e.Process(l, r, 16);
            e.NoteOn(0, 43, 1f);
            for (int i = 0; i < 30; i++) e.Process(l, r, 16);   // the old note fades out for a few ms, then the new one starts
            for (int i = 0; i < PolyEngine.VoiceCount; i++)
                if (e.Voice(i).IsActive && e.Voice(i).Pitch == 43f) return;
            Assert.Fail("the pitch should jump to the new note");
        }

        // ── the per-note Slide flag (FU2b) ──────────────────────────────────

        /// <summary>
        /// Plays <paramref name="from"/>, ends it (<paramref name="overlap"/> false) or holds it, then plays <paramref name="to"/> (told it is a slide note when <paramref name="flagged"/>).
        /// Returns the seconds the second note's pitch takes to arrive (-1: it began there) and the voice's pitch right after the note-on.
        /// </summary>
        static (double Seconds, float Start) FlaggedSlide(Action<ParamValues> setup, int voiceMode, bool flagged, bool overlap, int from = 36, int to = 43)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.VoiceMode, voiceMode);
            v.Set(P.Drift, 0);
            v.Set(P.AmpS, 1000);
            setup?.Invoke(v);
            var e = new PolyEngine(v);
            e.SetSampleRate(Rate);
            var l = new float[16];
            var r = new float[16];
            e.NoteOn(0, from, 1f);
            for (int i = 0; i < Rate / 4 / 16; i++) e.Process(l, r, 16);
            if (!overlap)
            {
                e.NoteOff(0);
                for (int i = 0; i < 40; i++) e.Process(l, r, 16);
            }
            if (flagged) e.SlideNextNote();
            e.NoteOn(1, to, 1f);
            int blocks = 0;
            float start = 0;
            bool found = false;
            var pitch = new float[PolyEngine.VoiceCount];
            while (blocks < Rate / 16)
            {
                e.Process(l, r, 16);
                blocks++;
                float best = float.NaN;
                for (int i = 0; i < PolyEngine.VoiceCount; i++)
                    if (e.Voice(i).IsActive && (float.IsNaN(best) || Math.Abs(e.Voice(i).Pitch - to) < Math.Abs(best - to))) best = e.Voice(i).Pitch;
                if (blocks == 1) start = best;
                if (!float.IsNaN(best) && Math.Abs(best - to) < 1e-4f) { found = true; break; }
            }
            return (found ? blocks * 16.0 / Rate : -1, start);
        }

        [TestCase(0, 0)]
        [TestCase(0, 1)]
        [TestCase(0, 2)]
        [TestCase(1, 0)]
        [TestCase(1, 1)]
        [TestCase(1, 2)]
        [TestCase(2, 0)]
        [TestCase(2, 2)]
        public void AFlaggedNoteGlidesIntoItsPitchAtTheSlideTimeWhateverTheGlideModeAndTheVoiceMode(int voiceMode, int glideMode)
        {
            foreach (bool overlap in new[] { false, true })
            {
                var (t, start) = FlaggedSlide(v => { v.Set(P.Slide, 1); v.Set(P.GlideMode, glideMode); v.Set(P.Glide, 900); Set(v, P.SlideTime, 0.2); }, voiceMode, true, overlap);
                Measure.Report($"Flagged note, voice mode {voiceMode}, glide mode {glideMode}, {(overlap ? "overlapping" : "after the note ended")}: pitch arrives after {t * 1000:F1} ms, starts at {start:F2}");
                Assert.That(t, Is.EqualTo(0.2).Within(0.2 * 0.06 + 0.001), $"the Slide time, not the glide time (voice {voiceMode}, glide {glideMode}, overlap {overlap})");
                Assert.That(start, Is.LessThan(43f - 1f), "it starts from the note before it, not at its own pitch");
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ANoteWithoutTheFlagIsPlayedAsItAlwaysWasAndTheFlagIsIgnoredWithTheSlideSwitchOff(int voiceMode)
        {
            // The glide mode is off and the note is not flagged: it jumps to its pitch (no slide from a gap).
            var (t, _) = FlaggedSlide(v => { v.Set(P.Slide, 1); v.Set(P.GlideMode, 0); Set(v, P.SlideTime, 0.2); }, voiceMode, false, false);
            Assert.That(t, Is.InRange(0, 0.02), "an unflagged note is on its pitch within the steal's fade, a few ms");
            // Slide off: the flag has nothing to say.
            var (t2, _) = FlaggedSlide(v => { v.Set(P.Slide, 0); v.Set(P.GlideMode, 0); Set(v, P.SlideTime, 0.2); }, voiceMode, true, false);
            Assert.That(t2, Is.InRange(0, 0.02), "with Slide off, a flagged note is a plain note");
        }

        [Test]
        public void AFlaggedNoteWithNothingBeforeItStartsAsANoteDoesAndTheFlagDoesNotLeakToTheNextNote()
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.VoiceMode, 1);
            v.Set(P.Drift, 0);
            v.Set(P.Slide, 1);
            var e = new PolyEngine(v);
            e.SetSampleRate(Rate);
            var l = new float[16];
            var r = new float[16];
            e.SlideNextNote();
            e.NoteOn(0, 40, 1f);
            e.Process(l, r, 16);
            Assert.That(e.Voice(0).Pitch, Is.EqualTo(40f).Within(1e-3f), "nothing before it: no slide");
            for (int i = 0; i < 100; i++) e.Process(l, r, 16);
            e.NoteOff(0);
            for (int i = 0; i < 40; i++) e.Process(l, r, 16);
            e.NoteOn(1, 47, 1f);        // not flagged: the flag was for the first note only
            for (int i = 0; i < 40; i++) e.Process(l, r, 16);
            Assert.That(e.Voice(0).Pitch, Is.EqualTo(47f).Within(1e-3f), "the flag was used up by its note: on its pitch after the steal's few ms, no 500 ms slide");
        }

        // ── cost ────────────────────────────────────────────────────────────

        static double TimeVoices(int type, int reps)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.F1Type, type);
            v.Set(P.AcidQuality, 0);   // the filter's own cost: the oversampling of an acid patch is measured apart
            v.Set(P.F1Reso, 700);
            v.Set(P.Polyphony, 8);
            v.Set(P.Drift, 0);
            v.Set(P.AmpS, 1000);
            var e = new PolyEngine(v);
            e.SetSampleRate(Rate);
            for (int k = 0; k < 8; k++) e.NoteOn(k, 40 + 3 * k, 0.8f);
            var l = new float[256];
            var r = new float[256];
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++) e.Process(l, r, 256);
            return sw.Elapsed.TotalSeconds;
        }

        [Test]
        public void Diode18CostsAtMost1AndAHalfTimesTheLadderPerVoice()
        {
            TimeVoices(0, 200); TimeVoices(6, 200);   // warm up
            double ladder = double.MaxValue, diode = double.MaxValue;
            for (int rep = 0; rep < 9; rep++)
            {
                // Interleaved, the first of each pair alternating, the fastest of each kept: a busy runner slows both alike.
                if (rep % 2 == 0) { ladder = Math.Min(ladder, TimeVoices(0, 400)); diode = Math.Min(diode, TimeVoices(6, 400)); }
                else { diode = Math.Min(diode, TimeVoices(6, 400)); ladder = Math.Min(ladder, TimeVoices(0, 400)); }
            }
            Measure.Report($"Cost of 8 voices: Diode 18 {diode * 1000:F1} ms, ladder 24 {ladder * 1000:F1} ms for {400 * 256 / 48.0:F0} ms of sound: {diode / ladder:F2}x");
            Assert.That(diode / ladder, Is.LessThanOrEqualTo(1.5));
        }

        [Test]
        public void AcidRendersAllocateNothingPerBuffer()
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.VoiceMode, 2);
            v.Set(P.F1Type, 6);
            v.Set(P.F1Reso, 800);
            Set(v, P.Accent, 0.5);
            Set(v, P.AccentSweep, 2);
            Set(v, P.AccentLevel, 4);
            Set(v, P.AccentDecay, 0.1);
            v.Set(P.Slide, 1);
            var e = new PolyEngine(v);
            e.SetSampleRate(Rate);
            var l = new float[256];
            var r = new float[256];
            int[] notes = { 36, 36, 48, 39, 36, 43, 36, 51 };
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                if (b % 20 == 0)
                {
                    int s = b / 20 % notes.Length;
                    e.NoteOn(0, notes[s], s % 3 == 0 ? 1f : 0.5f);
                    if (s % 2 == 1) e.NoteOff(0);
                }
                e.Process(l, r, 256);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        // ── the listening pack (local only: NEO_ACID_PACK=<dir> dotnet test --filter WritesTheAcidListeningPack) ──

        // 16 steps of 16ths: note, accent, slide into the next step.
        static readonly (int Note, bool Accent, bool Slide)[] Line =
        {
            (33, true, false), (33, false, false), (45, false, true), (43, true, false), (36, false, false), (33, false, true), (48, true, false), (31, false, false),
            (33, true, false), (45, false, false), (33, false, true), (36, true, false), (38, false, false), (33, false, false), (43, true, true), (33, false, false),
        };

        /// <summary>The line twice at 130 BPM through an engine set as the preset says (<paramref name="filterType"/> ≥ 0 replaces its F1 type); mono float samples.</summary>
        static float[] RenderLine(Dictionary<int, int> preset, int filterType)
        {
            var v = new ParamValues(Params.Table);
            foreach (var kv in preset) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            if (filterType >= 0) v.Set(P.F1Type, filterType);
            var e = new PolyEngine(v);
            e.SetSampleRate(Rate);
            double step = 60.0 / 130 / 4 * Rate;
            int total = (int)(step * 32) + Rate / 2;
            var events = new List<(int At, int Key, int Note, float Vel)>();
            for (int bar = 0; bar < 2; bar++)
                for (int i = 0; i < 16; i++)
                {
                    int n = bar * 16 + i;
                    var (note, accent, slide) = Line[i];
                    int key = n % 2;
                    int on = (int)(n * step);
                    int off = on + (int)(step * (slide ? 1.2 : 0.55));
                    events.Add((on, key, note, accent ? 127 / 127f : 90 / 127f));
                    events.Add((off, key, -1, 0));
                }
            events.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : (a.Note < 0 ? -1 : 1).CompareTo(b.Note < 0 ? -1 : 1));
            var y = new float[total];
            var l = new float[16];
            var r = new float[16];
            int ev = 0;
            for (int pos = 0; pos < total; pos += 16)
            {
                while (ev < events.Count && events[ev].At <= pos)
                {
                    var x = events[ev++];
                    if (x.Note < 0) e.NoteOff(x.Key); else e.NoteOn(x.Key, x.Note, x.Vel);
                }
                int n = Math.Min(16, total - pos);
                e.Process(l, r, n);
                for (int i = 0; i < n; i++) y[pos + i] = 0.5f * (l[i] + r[i]);
            }
            return y;
        }

        static void WriteWav(string path, float[] x)
        {
            using var w = new BinaryWriter(File.Create(path));
            int bytes = x.Length * 4;
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)32);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            foreach (var f in x) w.Write(f);
        }

        [Test]
        public void WritesTheAcidListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_ACID_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("local only: set NEO_ACID_PACK to a folder to write the listening pack");
            Directory.CreateDirectory(dir);
            var acid = PresetTests.AllPresets().Where(p => p.Machine == "Neo Poly" && p.Preset.StartsWith("Acid")).ToList();
            Assert.That(acid.Count, Is.EqualTo(6));

            // Blind items: the presets in a seeded order; Diode 18 in slot A in exactly half of them (3 of 6), the rest of the slots the 24 dB ladder.
            var rng = new Random(20261123);
            var order = Enumerable.Range(0, acid.Count).OrderBy(_ => rng.Next()).ToArray();
            var diodeFirst = Enumerable.Range(0, acid.Count).OrderBy(_ => rng.Next()).Take(acid.Count / 2).ToHashSet();
            var key = new System.Text.StringBuilder("item\tpreset\tslot A\tslot B\tgain applied to the ladder render (dB)\n");
            Directory.CreateDirectory(Path.Combine(dir, "blind"));
            Directory.CreateDirectory(Path.Combine(dir, "named"));
            for (int item = 0; item < acid.Count; item++)
            {
                var p = acid[order[item]];
                var diode = RenderLine(p.Values, 6);
                var ladder = RenderLine(p.Values, 0);
                // The same loudness (RMS over the line) on both, so the level does not decide: the ladder render is brought to the diode's.
                double gain = Measure.Rms(diode, 0, diode.Length) / Math.Max(1e-9, Measure.Rms(ladder, 0, ladder.Length));
                var ladderMatched = ladder.Select(x => (float)(x * gain)).ToArray();
                bool diodeInA = diodeFirst.Contains(item);
                string n = (item + 1).ToString("00");
                WriteWav(Path.Combine(dir, "blind", $"{n}_A.wav"), diodeInA ? diode : ladderMatched);
                WriteWav(Path.Combine(dir, "blind", $"{n}_B.wav"), diodeInA ? ladderMatched : diode);
                string safe = p.Preset.Replace(" — ", " ").Replace(' ', '-');
                WriteWav(Path.Combine(dir, "named", $"{safe}-diode18.wav"), diode);
                WriteWav(Path.Combine(dir, "named", $"{safe}-ladder24.wav"), ladder);
                key.Append($"{n}\t{p.Preset}\t{(diodeInA ? "Diode 18" : "Ladder 24")}\t{(diodeInA ? "Ladder 24" : "Diode 18")}\t{Measure.Db(gain):F2}\n");
            }
            File.WriteAllText(Path.Combine(dir, "KEY-do-not-open-before-listening.txt"), key.ToString());
        }

        // ── state ───────────────────────────────────────────────────────────

        [Test]
        public void TheAcidParametersAreAppendedWithDefaultsThatChangeNothing()
        {
            var t = Params.Table;
            Assert.That(t.Length, Is.EqualTo(102));
            Assert.That(t[P.F1Type].Max, Is.EqualTo(6));
            Assert.That(t[P.F1Type].Names[6], Is.EqualTo("Diode 18"));
            Assert.That(P.AccentThr, Is.EqualTo(91));
            Assert.That(t[P.AccentThr].Value(t[P.AccentThr].Default), Is.EqualTo(110), "Accent threshold 110");
            Assert.That(t[P.SlideTime].Value(t[P.SlideTime].Default), Is.EqualTo(0.06).Within(1e-6), "Slide time 60 ms");
            Assert.That(t[P.Accent].Value(t[P.Accent].Default), Is.EqualTo(0));
            Assert.That(t[P.AccentSweep].Value(t[P.AccentSweep].Default), Is.EqualTo(0));
            Assert.That(t[P.AccentLevel].Value(t[P.AccentLevel].Default), Is.EqualTo(0));
            Assert.That(t[P.Slide].Value(t[P.Slide].Default), Is.EqualTo(0));
            Assert.That(t[P.AccentDecay].Default, Is.EqualTo(t[P.AccentDecay].Max), "the decay stays as it is");
        }

        [Test]
        public void ASongSavedBeforeLoadsWithTheAcidDefaults()
        {
            // Version 0's 91 keys, as the machine's step lists them; its first 91 table entries are the same by key and position.
            var v0 = Params.Table.Take(91).Select(p => p.Id).ToArray();
            var v1 = Params.Table.Take(98).Select(p => p.Id).ToArray();
            var chain = new StateMigrations(Params.Table).Step(v0, s => { })
                .Step(v1, s => s.Add("AcidRange", 797).Add("AcidSweepMax", 500).Add("AcidQuality", 1).Add("AcidSoft", 650));
            Assert.That(chain.Validate(), Is.Empty);
            Assert.That(chain.Current, Is.EqualTo(2));
            var saved = new int[91];
            for (int i = 0; i < 91; i++) saved[i] = Params.Table[i].Default;
            saved[P.F1Type] = 5;
            saved[P.F1Cutoff] = 321;
            var now = chain.MigrateGlobals(0, saved);
            Assert.That(now.Length, Is.EqualTo(102));
            for (int i = 0; i < 91; i++) Assert.That(now[i], Is.EqualTo(saved[i]), Params.Table[i].Id);
            for (int i = 91; i < 102; i++) Assert.That(now[i], Is.EqualTo(Params.Table[i].Default), Params.Table[i].Id);
        }
    }
}
