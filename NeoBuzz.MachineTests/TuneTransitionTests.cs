// Neo Tune NT3 (docs/specs/neo-tune.md section 12): clean note transitions. The artefact detector (tools/PitchEval/TuneDetector.cs) and the engine's event log
// (Engine/TuneEvents.cs) on synthetic voices: the detector finds a planted click and nothing in a clean copy; the engine joins no waveforms a fraction of a period apart
// inside a voiced note; a splice is made where the next period matches; the start of a phrase is not bent at high Natural; the event log does not change the sound.

using System.Globalization;
using NeoBuzz.Dsp;
using NeoBuzz.PitchEval;
using NeoBuzz.Tune;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class TuneTransitionTests
    {
        const int Rate = 48000;
        static void Say(string s) => TestContext.Out.WriteLine(s);
        static string F(double v, string fmt = "F1") => v.ToString(fmt, CultureInfo.InvariantCulture);
        static double Hz(double midi) => 440 * Math.Pow(2, (midi - 69) / 12);

        static float[] Run(float[] x, double natural, bool log, out TuneEvent[] events, Action<ParamValues> tweak = null)
        {
            var v = new ParamValues(Params.Table);
            TuneNatural.Apply(v, natural); v.Set(P.Scale, 0); v.Set(P.Range, 1);
            tweak?.Invoke(v);
            var e = new TuneEngine(v);
            e.SetSampleRate(Rate);
            if (log) e.EnableEventLog(1 << 16);
            int latency = e.Latency;
            int total = x.Length + latency + 256;
            var l = new float[256]; var r = new float[256];
            var o = new float[x.Length];
            for (int pos = 0; pos < total; pos += 256)
            {
                int n = Math.Min(256, total - pos);
                for (int i = 0; i < n; i++) l[i] = r[i] = pos + i < x.Length ? x[pos + i] : 0f;
                e.Process(l, r, n);
                for (int i = 0; i < n; i++) { int at = pos + i - latency; if (at >= 0 && at < o.Length) o[at] = l[i]; }
            }
            events = log ? e.Events(out _) : Array.Empty<TuneEvent>();
            return o;
        }

        static float[] Note(double midi, double cents, double sec, double vib, ulong seed, double delay = 0.25)
        {
            double f0 = Hz(midi) * Math.Pow(2, cents / 1200.0);
            return PitchSignals.Voice(Rate, f0, sec, new PitchSignals.VoiceOptions
            {
                Seed = seed, VibratoCents = vib, VibratoHz = 5.5, VibratoDelay = delay, HighLift = 0.95, Aspiration = 0.02,
                Formants = f0 > 300 ? PitchSignals.VowelsHigh[0] : PitchSignals.VowelsLow[0],
            }).X;
        }

        static float[] Phrase(int[] notes, double sec, double vib, double off)
        {
            var x = new float[(int)(notes.Length * sec * Rate)];
            int pos = 0; ulong seed = 31;
            foreach (int n in notes)
            {
                var v = Note(n, off, sec, vib, seed++);
                Array.Copy(v, 0, x, pos, Math.Min(v.Length, x.Length - pos));
                pos += v.Length;
            }
            return x;
        }

        [Test]
        public void TheDetectorFindsAPlantedClickAndNothingInACleanCopy()
        {
            var x = Note(57, 0, 2.0, 30, 7, delay: 0.1);
            // a clean copy: another level, delayed by a fraction of a sample
            var copy = new float[x.Length];
            for (int i = 1; i < x.Length; i++) copy[i] = (float)(0.7 * (0.63 * x[i] + 0.37 * x[i - 1]));
            var none = TuneDetector.Detect(x, TuneDetector.LevelMatch(x, copy), Rate);
            // a bad splice: a quarter of a period dropped at 1.0 s and joined by a 2 ms crossfade (a whole period would be seamless)
            double T = Rate / Hz(57);
            int at = Rate, cut = (int)(T / 4), fade = (int)(0.002 * Rate);
            var bad = new float[x.Length];
            Array.Copy(x, bad, x.Length);
            for (int i = at; i < x.Length - cut; i++)
            {
                float a = x[i], b = x[i + cut];
                float w = i < at + fade ? (float)(i - at) / fade : 1f;
                bad[i] = (1 - w) * a + w * b;
            }
            var found = TuneDetector.Detect(x, TuneDetector.LevelMatch(x, bad), Rate);
            Say($"clean copy: {none.Count} events; a planted splice of a quarter period at 1.000 s: {found.Count} events " + string.Join(" ", found.Select(h => $"{h.Time:F3}s x{h.Excess:F0} {h.LevelDb:F0}dB")));
            Assert.That(none.Count, Is.EqualTo(0));
            Assert.That(found.Any(h => Math.Abs(h.Time - 1.0) < 0.02), Is.True, "the click at the splice is found");
        }

        [Test]
        public void TheEventLogDoesNotChangeTheSound()
        {
            var x = Phrase(new[] { 57, 60, 64, 60 }, 0.8, 40, 25);
            var a = Run(x, 0.0, false, out _);
            var b = Run(x, 0.0, true, out var ev);
            Say($"{ev.Length} events logged at Natural 0 on a 4-note phrase");
            Assert.That(ev.Length, Is.GreaterThan(10));
            Assert.That(b, Is.EqualTo(a), "the same bits with the log on");
        }

        [TestCase(1.0)]
        [TestCase(0.5)]
        public void NoWaveformsAFractionOfAPeriodApartAreJoinedInsideAVoicedNote(double n)
        {
            // Sung 25 ct sharp with a vibrato, then 18 ct flat: the shift is nil for stretches and not nil at others; NT2 crossfaded back to the input's position over about 1.5 periods
            // each time the correction became nil, between waveforms an offset apart (clicks at 4.7 s and 6.3 s in the blind test's excerpts).
            var x = Phrase(new[] { 57, 57, 62, 62, 59 }, 1.2, 35, 25);
            var y = Run(x, n, true, out var ev);
            int voicedJoins = ev.Count(e => e.Kind == TuneEventKind.SpliceReturn && e.D == 1 && e.B > 0);
            var hits = TuneDetector.Detect(x, TuneDetector.LevelMatch(x, y), Rate);
            Say($"Natural {n * 100:F0}: crossfade joins of a voiced note: {voicedJoins}; detector events {hits.Count} " + string.Join(" ", hits.Select(h => $"{h.Time:F2}s x{h.Excess:F0}")));
            Assert.That(voicedJoins, Is.EqualTo(0));
            // Natural 100 finds nothing; at 50 the note change of the synthetic phrase (an instant change of the voice itself) may show as a weak one (under 4 times the input's residual); NT2 had 5 to 40 times.
            Assert.That(hits.Count(h => n > 0.99 || h.Excess >= 4), Is.EqualTo(0), "no click found by the detector");
        }

        [Test]
        public void ASpliceIsMadeWhereTheNextPeriodMatchesTheOneBeforeIt()
        {
            // Natural 0 on a voice 60 ct sharp: the reader drifts and jumps a whole period every few periods; each join's mismatch (the log's energy of the difference of the
            // two segments over the first) must be well below the signal's.
            var x = Note(52, 60, 3.0, 0, 11);
            Run(x, 0.0, true, out var ev);
            var jumps = ev.Where(e => e.Kind == TuneEventKind.SpliceJump).ToList();
            double worst = jumps.Count == 0 ? -99 : jumps.Max(e => e.C);
            Say($"{jumps.Count} splice jumps on a steady voice 60 ct sharp, the worst mismatch {F(worst)} dB, mean {F(jumps.Count == 0 ? 0 : jumps.Average(e => e.C))} dB");
            Assert.That(jumps.Count, Is.GreaterThan(3));
            Assert.That(worst, Is.LessThan(-15));
        }

        [TestCase(1.0)]
        [TestCase(0.75)]
        public void TheStartOfAPhraseIsNotBent(double n)
        {
            // A voice 25 ct sharp that begins after silence: at high Natural nothing is corrected during the first 250 ms (the shift is the log's trace; NT2 started to bend after 50 ms).
            var x = new float[(int)(2.2 * Rate)];
            var v = Note(57, 25, 2.0, 0, 3);
            Array.Copy(v, 0, x, (int)(0.2 * Rate), v.Length);
            Run(x, n, true, out var ev);
            double start = ev.First(e => e.Kind == TuneEventKind.VoicedStart).Sample / (double)Rate;
            var early = ev.Where(e => e.Kind == TuneEventKind.Trace && e.Sample / (double)Rate > start && e.Sample / (double)Rate < start + 0.25).ToList();
            var late = ev.Where(e => e.Kind == TuneEventKind.Trace && e.Sample / (double)Rate > start + 1.0 && e.Sample / (double)Rate < start + 1.6).ToList();
            double maxEarly = early.Count == 0 ? 0 : early.Max(e => Math.Abs(e.A)) * 100, maxLate = late.Count == 0 ? 0 : late.Max(e => Math.Abs(e.A)) * 100;
            Say($"Natural {n * 100:F0}: voiced from {start:F3} s; the largest shift in the first 250 ms {F(maxEarly)} ct, later {F(maxLate)} ct");
            Assert.That(maxEarly, Is.LessThan(2.0));
            Assert.That(maxLate, Is.GreaterThan(10.0), "and the note is corrected once it has settled");
        }

        [Test]
        public void TheShiftChangesNoFasterThanTheSlewRate()
        {
            // The hard effect on notes a semitone and more apart: no two trace points (5.3 ms apart at 48 kHz) differ by more than 500 st/s allows.
            var x = Phrase(new[] { 57, 59, 62, 60 }, 0.9, 0, 35);
            Run(x, 0.0, true, out var ev);
            var tr = ev.Where(e => e.Kind == TuneEventKind.Trace).OrderBy(e => e.Sample).ToList();
            double worst = 0;
            for (int i = 1; i < tr.Count; i++) worst = Math.Max(worst, Math.Abs(tr[i].A - tr[i - 1].A) / ((tr[i].Sample - tr[i - 1].Sample) / (double)Rate));
            Say($"the fastest change of the shift between trace points: {F(worst)} st/s");
            Assert.That(worst, Is.LessThanOrEqualTo(520));
        }
    }
}
