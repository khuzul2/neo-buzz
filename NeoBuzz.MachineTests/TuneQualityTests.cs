// Neo Tune NT2 quality gates (docs/specs/neo-tune.md section 12): the objective measures of TuneQuality on synthetic voices with the natural jitter, shimmer and vibrato
// of a singer, each with its number in the test output. The same measures run on VocalSet in the pitch-eval workflow.

using System.Globalization;
using NeoBuzz.Dsp;
using NeoBuzz.Quality;
using NeoBuzz.Tune;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class TuneQualityTests
    {
        const int Rate = 48000;
        static void Say(string s) => TestContext.Out.WriteLine(s);
        static string F(double v, string fmt = "F1") => v.ToString(fmt, CultureInfo.InvariantCulture);
        static double Hz(double midi) => 440 * Math.Pow(2, (midi - 69) / 12);

        static float[] Run(float[] x, Action<ParamValues> set, out int latency, out TuneEngine engine)
        {
            var v = new ParamValues(Params.Table);
            set(v);
            var e = new TuneEngine(v);
            e.SetSampleRate(Rate);
            latency = e.Latency;
            engine = e;
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
            return o;
        }

        static Action<ParamValues> Natural(double n, int range = 1, int method = 0) => v =>
        {
            TuneNatural.Apply(v, n); v.Set(P.Scale, 0); v.Set(P.Range, range); v.Set(P.Method, method);
        };

        static float[] Note(double midi, double cents, double sec, double vib, ulong seed, double vibHz = 5.5, double delay = 0.25)
        {
            double f0 = Hz(midi) * Math.Pow(2, cents / 1200.0);
            return PitchSignals.Voice(Rate, f0, sec, new PitchSignals.VoiceOptions
            {
                Seed = seed, VibratoCents = vib, VibratoHz = vibHz, VibratoDelay = delay, HighLift = 0.95, Aspiration = 0.02,
                Formants = f0 > 300 ? PitchSignals.VowelsHigh[0] : PitchSignals.VowelsLow[0],
            }).X;
        }

        /// <summary>A phrase of notes one after another (each its own voice with a vibrato of <paramref name="vib"/> cents, <paramref name="off"/> cents off).</summary>
        static float[] Phrase(int[] notes, double sec, double vib, double off, double vibHz = 5.5, double delay = 0.25)
        {
            var x = new float[(int)(notes.Length * sec * Rate)];
            int pos = 0;
            ulong seed = 31;
            foreach (int n in notes)
            {
                var v = Note(n, off, sec, vib, seed++, vibHz, delay);
                Array.Copy(v, 0, x, pos, Math.Min(v.Length, x.Length - pos));
                pos += v.Length;
            }
            return x;
        }

        static bool IsSubsequence(IList<int> sub, IList<int> all) { int k = 0; foreach (int a in all) if (k < sub.Count && a == sub[k]) k++; return k == sub.Count; }

        static double Snr(float[] x, float[] y, int from, int to)
        {
            double s = 0, e = 0;
            for (int i = from; i < to; i++) { s += (double)x[i] * x[i]; double d = x[i] - y[i]; e += d * d; }
            return e == 0 ? 200 : 10 * Math.Log10(s / e);
        }

        // ---- the gates

        [Test]
        public void TransparencyAnInTuneVoiceComesOutAsItWentIn()
        {
            // A singer's voice on the note (jitter 0.6 %, shimmer 4 %, a 5.5 Hz vibrato of +-40 cents), Natural 100 and 75: the aligned null.
            var x = Note(57, 0, 3.0, 40, 5, delay: 0.1);
            foreach (double n in new[] { 1.0, 0.75 })
            {
                var y = Run(x, Natural(n), out _, out _);
                double snr = Snr(x, y, Rate, x.Length - Rate / 4);
                var ti = QTrack.Run(x, Rate); var to = QTrack.Run(y, Rate);
                var (ji, si) = TuneQuality.JitterShimmer(x, Rate, ti); var (jo, so) = TuneQuality.JitterShimmer(y, Rate, to);
                Say($"Natural {n * 100:F0}: residual {F(-snr)} dB against the signal (aligned null, 1 s to the end: the vibrato is recognised after about two periods); jitter {F(ji, "F2")} -> {F(jo, "F2")} %, shimmer {F(si, "F2")} -> {F(so, "F2")} %");
                Assert.That(snr, Is.GreaterThanOrEqualTo(n > 0.99 ? 30 : 24), $"Natural {n * 100:F0} (at 75 the centre is still corrected a little, so the null is not exact)");
                Assert.That(jo, Is.EqualTo(ji).Within(0.1 * ji)); Assert.That(so, Is.EqualTo(si).Within(0.1 * si));
            }
        }

        [TestCase(1.0)]
        [TestCase(0.75)]
        public void ExpressionKeptTheVibratoSurvivesWhileTheCentreIsCorrected(double n)
        {
            // 30 cents sharp with a +-50 cent vibrato: the note centre goes to the note within 5 cents, the vibrato is kept to 90 % or more.
            var x = Note(57, 30, 3.5, 50, 5, delay: 0.1);
            var y = Run(x, Natural(n), out _, out _);
            var ti = QTrack.Run(x, Rate); var to = QTrack.Run(y, Rate);
            double vi = TuneQuality.VibratoExtent(ti), vo = TuneQuality.VibratoExtent(to);
            double sum = 0; int c = 0;
            double t0 = 1.5, t1 = t0 + 8 / 5.5;     // whole cycles of the vibrato
            for (int i = 0; i < to.Count; i++) if (to.Voiced[i] && to.T[i] >= t0 && to.T[i] < t1) { sum += to.Cents[i]; c++; }
            double centre = sum / c - 5700;
            Say($"Natural {n * 100:F0}: vibrato extent in {F(vi)} out {F(vo)} ({F(100 * vo / vi, "F0")} %), centre error {F(centre)} cents (sung +30)");
            Assert.That(vo / vi, Is.GreaterThanOrEqualTo(0.9));
            Assert.That(Math.Abs(centre), Is.LessThanOrEqualTo(5));
        }

        [Test]
        public void EffectAtNaturalZeroFlattensTheVibratoAndKeepsTheNoteSequence()
        {
            // An arpeggio with a +-70 cent vibrato (extent 140: wider than a semitone's half) 25 cents flat: C3 E3 G3 C4 G3 E3.
            int[] notes = { 48, 52, 55, 60, 55, 52 };
            var x = Phrase(notes, 1.6, 70, -25, 5.5, 0.1);
            var y = Run(x, Natural(0.0), out _, out _);
            var ti = QTrack.Run(x, Rate); var to = QTrack.Run(y, Rate);
            // the expression inside the notes (1.0 s to 1.5 s of each: a vibrato is recognised after about two of its periods), not the steps between them
            double vi = 0, vo = 0;
            for (int k = 0; k < notes.Length; k++) { vi += TuneQuality.NoteSpread(ti, k * 1.6 + 1.0, k * 1.6 + 1.5); vo += TuneQuality.NoteSpread(to, k * 1.6 + 1.0, k * 1.6 + 1.5); }
            vi /= notes.Length; vo /= notes.Length;
            // flat within 3 cents: the 150 ms mean of the pitch against the note, 0.3 s after each note starts
            double worst = 0;
            for (int k = 0; k < notes.Length; k++)
            {
                double a = k * 1.6 + 0.95, b = a + 0.55;
                var cs = new List<double>();
                for (int i = 0; i < to.Count; i++) if (to.Voiced[i] && to.T[i] >= a && to.T[i] < b) cs.Add(to.Cents[i] - notes[k] * 100);
                double m = cs.Average();
                worst = Math.Max(worst, Math.Abs(m));
            }
            TuneQuality.NoteChanges(to, out var outNotes);
            Say($"Natural 0: expression inside the notes in {F(vi)} out {F(vo)} ({F(100 * vo / vi, "F0")} %), worst note mean {F(worst)} cents off; output notes [{string.Join(' ', outNotes)}] for [{string.Join(' ', notes)}]");
            Assert.That(vo / vi, Is.LessThanOrEqualTo(0.10));
            Assert.That(worst, Is.LessThanOrEqualTo(3.0));
            Assert.That(IsSubsequence(notes.ToList(), outNotes), "every sung note comes out, in order (a step inside a wide vibrato may add one passing note)");
        }

        [TestCase(0.0)]
        [TestCase(0.5)]
        [TestCase(1.0)]
        public void ArtefactsNothingIsAddedToTheSound(double n)
        {
            var x = Phrase(new[] { 55, 59, 62, 67, 62, 59 }, 1.6, 50, 20, 5.5, 0.1);
            var y = Run(x, Natural(n), out _, out _);
            var ti = QTrack.Run(x, Rate); var to = QTrack.Run(y, Rate);
            double sub = TuneQuality.Subharmonic(y, Rate, to) - TuneQuality.Subharmonic(x, Rate, ti);
            var (diff, hin, hout) = TuneQuality.Ltas(x, y, Rate, ti);
            double kept = TuneQuality.VoicedKept(ti, to);
            var ch = TuneQuality.NoteChanges(ti, out _);
            var bi = TuneQuality.Bursts(x, Rate, ti, ch); var bo = TuneQuality.Bursts(y, Rate, to, ch);
            double worstBand = diff.Select(Math.Abs).Max();
            Say($"Natural {n * 100:F0}: sub-harmonic change {F(sub)} dB, band above 5 kHz {F(hout - hin, "F2")} dB, worst third-octave {F(worstBand, "F2")} dB, voiced kept {F(100 * kept)} %, bursts at note changes {bo.AtChanges} (input {bi.AtChanges}), all {bo.Total} (input {bi.Total})");
            Assert.That(sub, Is.LessThanOrEqualTo(1.0), "sub-harmonics");
            Assert.That(Math.Abs(hout - hin), Is.LessThanOrEqualTo(n < 0.01 ? 12.0 : n < 0.99 ? 4.0 : 0.5), "the band above 5 kHz (measured: flattening a vibrato modulates the shift; the hard effect adds about 9 dB here, Natural 50 about 3.5 dB; see spec 11.3)");
            Assert.That(kept, Is.GreaterThanOrEqualTo(0.99), "voiced kept");
            Assert.That(bo.AtChanges, Is.LessThanOrEqualTo(bi.AtChanges), "bursts at note changes");
        }

        [Test]
        public void TheInterpolatorIsFlatToFortyPercentOfTheSampleRate()
        {
            // Every phase of the 16-tap sinc: the gain at 0.05 ... 0.40 of the rate within 0.05 dB, 1 at DC.
            var tab = TuneSinc.Table;
            double worst = 0;
            for (int ph = 0; ph <= TuneSinc.Phases; ph += 7)
            {
                double dc = 0; for (int k = 0; k < TuneSinc.Taps; k++) dc += tab[ph * TuneSinc.Taps + k];
                Assert.That(dc, Is.EqualTo(1.0).Within(1e-6));
                for (double f = 0.05; f <= 0.401; f += 0.05)
                {
                    double re = 0, im = 0, frac = ph / (double)TuneSinc.Phases;
                    for (int k = 0; k < TuneSinc.Taps; k++)
                    {
                        double t = k - 7 - frac;      // the tap's distance from the interpolated point
                        double c = tab[ph * TuneSinc.Taps + k];
                        re += c * Math.Cos(2 * Math.PI * f * t); im += c * Math.Sin(2 * Math.PI * f * t);
                    }
                    worst = Math.Max(worst, Math.Abs(20 * Math.Log10(Math.Sqrt(re * re + im * im))));
                }
            }
            Say($"largest gain error up to 0.40 of the sample rate over all phases: {F(worst, "F3")} dB");
            Assert.That(worst, Is.LessThan(0.05));
        }

        [TestCase(0, 0)]
        [TestCase(1, 0)]
        [TestCase(2, 0)]
        [TestCase(0, 2)]
        [TestCase(1, 2)]
        [TestCase(2, 2)]
        public void LatencyPerRangeAndEngineAndTheInputComesBackAligned(int range, int method)
        {
            var (e, _) = (Make(range, method));
            var x = Note(range == 2 ? 45 : 57, 0, 1.5, 0, 9);
            var y = Run(x, Natural(1.0, range, method), out int lat, out _);
            double snr = Snr(x, y, Rate, x.Length - Rate / 4);
            Say($"range {range} ({new[] { "Low latency", "Quality", "Full" }[range]}), {(method == 0 ? "splice (Auto)" : "PSOLA")}: latency {lat} samples = {F(1000.0 * lat / Rate)} ms; transparent voice residual {F(-snr)} dB");
            Assert.That(lat, Is.EqualTo(new[] { 480, 1344, 1728 }[range]));
            Assert.That(snr, Is.GreaterThanOrEqualTo(method == 0 ? 30 : 20));
        }

        static (TuneEngine, ParamValues) Make(int range, int method)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Range, range); v.Set(P.Method, method);
            var e = new TuneEngine(v); e.SetSampleRate(Rate);
            return (e, v);
        }

        // ---- the hard effect inside the notes (lead's review of the listening pack: the effect did not happen on a heavy vibrato)

        [TestCase(60.0)]
        [TestCase(100.0)]
        [TestCase(140.0)]
        public void TheHardEffectFlattensAHeavyVibratoInsideEveryNote(double vib)
        {
            var notes = new[] { 48, 52, 55, 60, 55, 52 };
            var x = Phrase(notes, 1.6, vib, -25, 5.5, 0.1);
            var y = Run(x, Natural(0.0), out _, out _);
            var ti = QTrack.Run(x, Rate); var to = QTrack.Run(y, Rate);
            var sung = TuneNotes.Segments(ti); var outs = TuneNotes.Segments(to);
            var (flat, nn) = TuneNotes.Flatness(to, outs);
            var delays = TuneNotes.StepDelays(ti, to, sung);
            Say($"vibrato +-{vib} ct, Natural 0: {nn} output notes of {outs.Count} ({sung.Count} sung), median distance from the note's median {F(flat)} ct, step delays [{string.Join(' ', delays.Select(d => F(d * 1000, "F0")))}] ms, notes [{string.Join(' ', TuneNotes.Long(outs))}] for [{string.Join(' ', TuneNotes.Long(sung))}]");
            Assert.That(flat, Is.LessThanOrEqualTo(5.0), "flat inside the notes");
            Assert.That(IsSubsequence(TuneNotes.Long(sung), TuneNotes.Long(outs)), "every sung note comes out, in order (a step inside a wide vibrato may add one passing note)");
        }

        [Test]
        public void TheHardEffectStepsPromptlyOnAStraightScale()
        {
            // A scale of straight notes: the output steps within 30 ms of the sung pitch crossing the midpoint between two notes.
            var notes = new[] { 48, 50, 52, 53, 55, 57, 59, 60 };
            var x = Phrase(notes, 0.5, 6, -20, 5.5);
            var y = Run(x, Natural(0.0), out _, out _);
            var ti = QTrack.Run(x, Rate); var to = QTrack.Run(y, Rate);
            var delays = TuneNotes.StepDelays(ti, to, TuneNotes.Segments(ti));
            double med = delays.Count == 0 ? double.NaN : delays.OrderBy(d => d).ToList()[delays.Count / 2] * 1000;
            Say($"step delays [{string.Join(' ', delays.Select(d => F(d * 1000, "F0")))}] ms, median {F(med, "F0")} ms");
            Assert.That(delays.Count, Is.GreaterThanOrEqualTo(4));
            Assert.That(med, Is.LessThanOrEqualTo(30.0));
        }

        // ---- Natural as a parameter (automation, patterns, knob cards, bindings)

        static TuneEngine Fresh(Action<ParamValues> set, out ParamValues v)
        {
            v = new ParamValues(Params.Table);
            set(v);
            var e = new TuneEngine(v);
            e.SetSampleRate(Rate);
            return e;
        }

        [Test]
        public void NaturalMovedAloneDrivesTheSixFineControls()
        {
            var e = Fresh(v => { v.Set(P.Zone, Params.Table[P.Zone].Raw(33)); v.Set(P.Stick, Params.Table[P.Stick].Raw(0.9)); }, out var v);
            var l = new float[256]; var r = new float[256];
            e.Process(l, r, 256);
            Assert.That(e.ZoneValue, Is.EqualTo(33).Within(0.6), "the fine values as set (the first tick sets everything)");
            Assert.That(e.NaturalDrives, Is.False);
            // automation moves Natural alone: the six follow its map
            v.Set(P.Natural, Params.Table[P.Natural].Raw(0.0));
            e.Process(l, r, 256);
            Assert.That(e.NaturalDrives, Is.True);
            Assert.That(e.ZoneValue, Is.EqualTo(TuneNatural.Value(1, 0.0)).Within(0.6));
            Assert.That(e.TakeNaturalWrite(out double n), Is.True); Assert.That(n, Is.EqualTo(0.0).Within(0.002));
            Assert.That(e.TakeNaturalWrite(out _), Is.False, "once per change");
            v.Set(P.Natural, Params.Table[P.Natural].Raw(1.0));
            e.Process(l, r, 256);
            Assert.That(e.ZoneValue, Is.EqualTo(TuneNatural.Value(1, 1.0)).Within(0.6));
            Assert.That(e.StickValue, Is.EqualTo(TuneNatural.Value(5, 1.0)).Within(0.002));
            // a hand edit of one fine control ends it: the six are the values in the parameters again
            v.Set(P.Zone, Params.Table[P.Zone].Raw(45));
            e.Process(l, r, 256);
            Assert.That(e.NaturalDrives, Is.False);
            Assert.That(e.ZoneValue, Is.EqualTo(45).Within(0.6));
        }

        [Test]
        public void ASongLoadOrAPresetKeepsItsFineValuesWhateverNaturalSays()
        {
            // All the parameters arrive in one tick, Natural 0 with hand-edited fine values: the fine values win.
            var e = Fresh(v =>
            {
                v.Set(P.Natural, Params.Table[P.Natural].Raw(0.0));
                v.Set(P.Zone, Params.Table[P.Zone].Raw(37)); v.Set(P.Retune, Params.Table[P.Retune].Raw(77)); v.Set(P.Glide, Params.Table[P.Glide].Raw(0.5));
                v.Set(P.Stick, Params.Table[P.Stick].Raw(0.8)); v.Set(P.Humanize, Params.Table[P.Humanize].Raw(0.2)); v.Set(P.VibKeep, Params.Table[P.VibKeep].Raw(0.3));
            }, out var v);
            var l = new float[256]; var r = new float[256];
            e.Process(l, r, 256);
            Assert.That(e.NaturalDrives, Is.False);
            Assert.That(e.ZoneValue, Is.EqualTo(37).Within(0.6));
            Assert.That(e.StickValue, Is.EqualTo(0.8).Within(0.002));
            // a later tick that changes Natural and one fine control together: the fine control wins, the other five stay as they were
            v.Set(P.Natural, Params.Table[P.Natural].Raw(1.0)); v.Set(P.Zone, Params.Table[P.Zone].Raw(50));
            e.Process(l, r, 256);
            Assert.That(e.NaturalDrives, Is.False);
            Assert.That(e.ZoneValue, Is.EqualTo(50).Within(0.6));
            Assert.That(e.StickValue, Is.EqualTo(0.8).Within(0.002));
        }
    }
}
