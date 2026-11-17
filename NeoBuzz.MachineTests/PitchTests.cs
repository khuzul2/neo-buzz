// The shared pitch engine (docs/specs/pitch.md §6): the tracker, the onset detector, the trigger classifier and the vowel
// estimator, measured on synthetic sources whose truth is known. Each test prints its figures (the spec quotes them).

using System.Diagnostics;
using System.Globalization;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class PitchTests
    {
        static void Say(string s) => TestContext.Out.WriteLine(s);
        static string F(double v, string fmt = "F1") => v.ToString(fmt, CultureInfo.InvariantCulture);

        // ── running a tracker ────────────────────────────────────────────

        internal sealed class Run
        {
            public List<(double T, PitchEstimate E)> Frames = new();
            public PitchTracker Tracker;
        }

        internal static Run Track(Source s, PitchRange range, PitchQuality q = PitchQuality.Quality, int block = 256, Action<PitchTracker> setup = null)
        {
            var tr = new PitchTracker(s.Rate, range, q);
            setup?.Invoke(tr);
            var run = new Run { Tracker = tr };
            var buf = new PitchEstimate[tr.MaxFrames(block)];
            for (int pos = 0; pos < s.X.Length; pos += block)
            {
                int n = Math.Min(block, s.X.Length - pos);
                int got = tr.Process(s.X, pos, n, buf, 0);
                for (int i = 0; i < got; i++)
                {
                    var e = buf[i];
                    // The analysed audio is the newest window and the same one a period earlier: its middle.
                    double period = e.RawF0 > 0 ? s.Rate / e.RawF0 : tr.TauMax / 2.0;
                    double t = (e.EndSample - (tr.Window + period) / 2) / s.Rate;
                    run.Frames.Add((t, e));
                }
            }
            return run;
        }

        internal sealed class Score
        {
            public int Truth, Voiced, Correct, ChromaOk, Octave, Falsely;
            public List<double> Err = new();
            public double Rpa => Truth == 0 ? 0 : (double)Correct / Truth;
            public double Rca => Truth == 0 ? 0 : (double)ChromaOk / Truth;
            public double VoicingRecall => Truth == 0 ? 0 : (double)Voiced / Truth;
            public double Median { get { if (Err.Count == 0) return 0; var a = Err.Select(Math.Abs).OrderBy(v => v).ToList(); return a[a.Count / 2]; } }
            public double P95 { get { if (Err.Count == 0) return 0; var a = Err.Select(Math.Abs).OrderBy(v => v).ToList(); return a[(int)(a.Count * 0.95)]; } }
            public override string ToString() => $"RPA {F(Rpa * 100)}%  chroma {F(Rca * 100)}%  voiced {F(VoicingRecall * 100)}%  octave errors {Octave}  median {F(Median)} ct  p95 {F(P95)} ct  ({Truth} frames)";
        }

        internal static Score Evaluate(Source s, Run run, double from, double to)
        {
            var sc = new Score();
            foreach (var (t, e) in run.Frames)
            {
                if (t < from || t > to) continue;
                double truth = s.F0(t);
                if (truth <= 0)
                {
                    if (e.Voiced) sc.Falsely++;
                    continue;
                }
                sc.Truth++;
                if (!e.Voiced) continue;
                sc.Voiced++;
                double cents = 1200 * Math.Log2(e.F0 / truth);
                double folded = cents - 1200 * Math.Round(cents / 1200);
                if (Math.Abs(cents) < 50) { sc.Correct++; sc.Err.Add(cents); }
                if (Math.Abs(folded) < 50) sc.ChromaOk++;
                if (Math.Abs(cents) >= 50 && Math.Abs(folded) < 50) sc.Octave++;
            }
            return sc;
        }

        // ── pitch: steady tones ──────────────────────────────────────────

        public static IEnumerable<TestCaseData> ToneCases()
        {
            foreach (double hz in new[] { 70.0, 110, 220, 440, 880, 1046.5 }) yield return new TestCaseData((int)PitchRange.Voice, hz);
            foreach (double hz in new[] { 82.41, 196, 440, 1000 }) yield return new TestCaseData((int)PitchRange.Guitar, hz);
            foreach (double hz in new[] { 41.2, 55, 98, 196, 392 }) yield return new TestCaseData((int)PitchRange.Bass, hz);
            foreach (double hz in new[] { 41.2, 261.6, 1318.5, 1900 }) yield return new TestCaseData((int)PitchRange.Full, hz);
        }

        [TestCaseSource(nameof(ToneCases))]
        public void SineSawAndPulseAreWithinAFewCents(int rangeId, double hz)
        {
            var range = (PitchRange)rangeId;
            foreach (var s in new[] { PitchSignals.Sine(48000, hz, 1.0), PitchSignals.Saw(48000, hz, 1.0), PitchSignals.Pulse(48000, hz, 1.0) })
            {
                var run = Track(s, range);
                var sc = Evaluate(s, run, 0.25, 0.95);
                Say($"{range} {s.Name}: {sc}");
                Assert.That(sc.VoicingRecall, Is.GreaterThan(0.99), s.Name);
                Assert.That(sc.Octave, Is.EqualTo(0), s.Name + " octave errors");
                Assert.That(sc.Rpa, Is.GreaterThan(0.99), s.Name);
                Assert.That(sc.P95, Is.LessThan(s.Name.StartsWith("sine") && hz > 700 ? 12 : 6), s.Name + " p95 error in cents");
            }
        }

        [Test]
        public void TheFigureForEachRangeAtOtherSampleRates()
        {
            foreach (double sr in new[] { 44100.0, 96000.0 })
            {
                var s = PitchSignals.Saw(sr, 220, 0.8);
                var sc = Evaluate(s, Track(s, PitchRange.Voice), 0.25, 0.7);
                Say($"{sr} Hz: {sc}");
                Assert.That(sc.Rpa, Is.GreaterThan(0.99));
                Assert.That(sc.P95, Is.LessThan(6));
            }
        }

        // ── pitch: a singing voice ───────────────────────────────────────

        [Test]
        public void AVoiceWithJitterAndVibratoIsTrackedThroughThreeVowels()
        {
            int total = 0, correct = 0, voiced = 0, octave = 0;
            var all = new List<double>();
            ulong seed = 1;
            foreach (var low in new[] { true, false })
                foreach (int v in new[] { 0, 1, 2 })
                    foreach (double f0 in low ? new[] { 98.0, 130.8, 196 } : new[] { 220.0, 329.6, 523.3 })
                    {
                        var o = new PitchSignals.VoiceOptions { Formants = (low ? PitchSignals.VowelsLow : PitchSignals.VowelsHigh)[v], VibratoCents = 40, Seed = seed++ };
                        var s = PitchSignals.Voice(48000, f0, 1.4, o);
                        var sc = Evaluate(s, Track(s, PitchRange.Voice), 0.15, 1.3);
                        total += sc.Truth; correct += sc.Correct; voiced += sc.Voiced; octave += sc.Octave; all.AddRange(sc.Err);
                        Assert.That(sc.Rpa, Is.GreaterThan(0.9), $"{s.Name} vowel {v} {(low ? "low" : "high")}: {sc}");
                    }
            var ab = all.Select(Math.Abs).OrderBy(x => x).ToList();
            Say($"voice (36 sources, jitter 0.6 %, shimmer 4 %, vibrato ±40 ct): RPA {F(100.0 * correct / total)}%  voiced {F(100.0 * voiced / total)}%  octave errors {octave}  median {F(ab[ab.Count / 2])} ct  p95 {F(ab[(int)(ab.Count * 0.95)])} ct");
            Assert.That((double)correct / total, Is.GreaterThan(0.97));
            Assert.That(octave, Is.LessThanOrEqualTo(total / 200));
            Assert.That(ab[ab.Count / 2], Is.LessThan(8));
        }

        [Test]
        public void ScoopsAndGlidesConvergeWithoutAnOctaveJump()
        {
            int octave = 0, total = 0, correct = 0;
            foreach (double f0 in new[] { 130.8, 220.0, 440.0 })
            {
                var o = new PitchSignals.VoiceOptions { ScoopCents = 300, ScoopSeconds = 0.06, GlideTo = f0 * 2, GlideStart = 0.6, GlideSeconds = 0.25, Seed = 5 };
                var s = PitchSignals.Voice(48000, f0, 1.3, o);
                var sc = Evaluate(s, Track(s, PitchRange.Voice), 0.1, 1.2);
                Say($"scoop + octave glide from {f0} Hz: {sc}");
                octave += sc.Octave; total += sc.Truth; correct += sc.Correct;
            }
            Assert.That(octave, Is.EqualTo(0));
            Assert.That((double)correct / total, Is.GreaterThan(0.93));
        }

        // ── pitch: silence and noise ─────────────────────────────────────

        [Test]
        public void NoiseAndSilenceAreNotVoiced()
        {
            var rng = new SynthRng(3);
            foreach (var (name, x) in new[] {
                ("white -20 dB", PitchSignals.Noise(48000, 3, 0.2, rng)),
                ("white -6 dB", PitchSignals.Noise(48000, 3, 0.5, rng)),
                ("pink -20 dB", PitchSignals.Pink(48000, 3, 0.2, rng)),
                ("silence", PitchSignals.Silence(48000, 2)),
                ("hum -80 dB", PitchSignals.Sine(48000, 220, 2, 0.0001).X) })
                foreach (var range in new[] { PitchRange.Voice, PitchRange.Guitar, PitchRange.Bass, PitchRange.Full })
                {
                    var s = new Source { X = x, Rate = 48000, F0 = _ => 0, Name = name };
                    var run = Track(s, range);
                    int v = run.Frames.Count(f => f.E.Voiced);
                    double rate = (double)v / Math.Max(1, run.Frames.Count);
                    Say($"{name} / {range}: {F(rate * 100)}% voiced");
                    Assert.That(rate, Is.LessThan(0.02), $"{name} / {range}");
                }
        }

        [Test]
        public void ANoteWithNoiseUnderItStaysVoicedAndSilenceAfterItIsNot()
        {
            // 0.5 s of silence, 0.8 s of a 196 Hz voice at -26 dB, 0.5 s of silence; noise at -50 dB throughout.
            var o = new PitchSignals.VoiceOptions { Level = 0.05, Seed = 11 };
            var v = PitchSignals.Voice(48000, 196, 0.8, o);
            var x = new float[(int)(1.8 * 48000)];
            var rng = new SynthRng(9);
            for (int i = 0; i < x.Length; i++) x[i] = (float)(0.003 * rng.Gauss());
            for (int i = 0; i < v.X.Length; i++) x[(int)(0.5 * 48000) + i] += v.X[i];
            var s = new Source { X = x, Rate = 48000, Name = "gated", F0 = t => t > 0.55 && t < 1.25 ? 196 : (t < 0.45 || t > 1.4 ? 0 : -1) };
            var run = Track(s, PitchRange.Voice);
            int inVoiced = 0, inCount = 0, outVoiced = 0, outCount = 0;
            foreach (var (t, e) in run.Frames)
            {
                if (t > 0.6 && t < 1.2) { inCount++; if (e.Voiced) inVoiced++; }
                if (t < 0.4 || t > 1.5) { outCount++; if (e.Voiced) outVoiced++; }
            }
            Say($"noisy note: {inVoiced}/{inCount} voiced inside, {outVoiced}/{outCount} outside");
            Assert.That((double)inVoiced / inCount, Is.GreaterThan(0.95));
            Assert.That((double)outVoiced / outCount, Is.LessThan(0.02));
        }

        // ── pitch: plucked strings ───────────────────────────────────────

        [Test]
        public void GuitarAndBassPlucksWithSlidesAreTracked()
        {
            var cases = new (string, PitchRange, double, double)[]
            {
                ("guitar E2", PitchRange.Guitar, 82.41, 82.41), ("guitar A3 slide up a tone", PitchRange.Guitar, 220, 247),
                ("guitar E4", PitchRange.Guitar, 329.6, 329.6), ("guitar E5", PitchRange.Guitar, 659.3, 659.3),
                ("bass B0..", PitchRange.Bass, 41.2, 41.2), ("bass A1", PitchRange.Bass, 55, 55),
                ("bass D2 slide up a fourth", PitchRange.Bass, 73.4, 98), ("bass G3", PitchRange.Bass, 196, 196),
            };
            ulong seed = 20;
            foreach (var (name, range, a, b) in cases)
            {
                Func<double, double> hz = t => a * Math.Pow(b / a, Math.Clamp((t - 0.6) / 0.3, 0, 1));
                var s = PitchSignals.Pluck(48000, hz, 1.6, seed++, decay: a < 60 ? 0.9993 : 0.998);
                var run = Track(s, range);
                var sc = Evaluate(s, run, 0.1, 1.5);
                Say($"{name}: {sc}");
                Assert.That(sc.Octave, Is.LessThanOrEqualTo(sc.Truth / 50), name);
                Assert.That(sc.Rpa, Is.GreaterThan(0.93), name);
            }
        }

        // ── latency ──────────────────────────────────────────────────────

        static double OnsetLatency(PitchRange range, PitchQuality q, double hz, bool voice)
        {
            // 100 ms of silence, then a note: the time from its start to the first voiced estimate within 50 cents.
            var note = voice ? PitchSignals.Voice(48000, hz, 0.5, new PitchSignals.VoiceOptions { Level = 0.5, Seed = 3, Jitter = 0.003 }).X : PitchSignals.Saw(48000, hz, 0.5).X;
            var x = new float[(int)(0.7 * 48000)];
            Array.Copy(note, 0, x, (int)(0.1 * 48000), note.Length);
            var s = new Source { X = x, Rate = 48000, F0 = _ => hz, Name = "onset" };
            var tr = new PitchTracker(48000, range, q);
            var buf = new PitchEstimate[tr.MaxFrames(64)];
            for (int pos = 0; pos + 64 <= x.Length; pos += 64)
            {
                int got = tr.Process(x, pos, 64, buf, 0);
                for (int i = 0; i < got; i++)
                    if (buf[i].Voiced && Math.Abs(1200 * Math.Log2(buf[i].F0 / hz)) < 50)
                        return (buf[i].EndSample - 0.1 * 48000) / 48000 * 1000;
            }
            return double.NaN;
        }

        [Test]
        public void DetectionLatencyPerRange()
        {
            Say($"{"range",-18}{"window+lag",12}{"span/2",8}   first correct estimate after a note starts (ms), at");
            foreach (var (range, q, hzs) in new (PitchRange, PitchQuality, double[])[]
            {
                (PitchRange.Voice, PitchQuality.LowLatency, new[] { 130.8, 220, 440, 880 }),
                (PitchRange.Voice, PitchQuality.Quality, new[] { 130.8, 220, 440, 880 }),
                (PitchRange.Guitar, PitchQuality.Quality, new[] { 98.0, 196, 392, 784 }),
                (PitchRange.Bass, PitchQuality.Quality, new[] { 41.2, 55, 98, 196 }),
                (PitchRange.Full, PitchQuality.Quality, new[] { 41.2, 110, 440, 1760 }),
            })
            {
                var tr = new PitchTracker(48000, range, q);
                var line = new List<string>();
                double worst = 0;
                foreach (double hz in hzs)
                {
                    double ms = OnsetLatency(range, q, hz, voice: false);
                    Assert.That(double.IsNaN(ms), Is.False, $"{range} {q} {hz}");
                    worst = Math.Max(worst, ms);
                    line.Add($"{hz:F0} Hz {ms:F1}");
                }
                Say($"{range + (q == PitchQuality.LowLatency ? " (low)" : ""),-18}{F(tr.Window * 1000.0 / 48000),12}{F(tr.Latency * 1000.0 / 48000),8}   {string.Join("  ", line)}");
                double bound = range == PitchRange.Voice && q == PitchQuality.LowLatency ? 15 : range == PitchRange.Voice ? 40 : range == PitchRange.Guitar ? 40 : 90;
                Assert.That(worst, Is.LessThan(bound), $"{range} {q}");
            }
            Assert.That(OnsetLatency(PitchRange.Voice, PitchQuality.LowLatency, 220, true), Is.LessThan(OnsetLatency(PitchRange.Voice, PitchQuality.Quality, 220, true)));
        }

        [Test]
        public void LowLatencyAccuracyOnAVoiceIsMeasuredNextToQuality()
        {
            int tq = 0, cq = 0, tl = 0, cl = 0, oq = 0, ol = 0;
            ulong seed = 40;
            foreach (double f0 in new[] { 130.8, 196, 261.6, 392, 523.3 })
                foreach (int v in new[] { 0, 1, 2 })
                {
                    var s = PitchSignals.Voice(48000, f0, 1.2, new PitchSignals.VoiceOptions { Formants = PitchSignals.VowelsLow[v], VibratoCents = 40, Seed = seed++ });
                    var q = Evaluate(s, Track(s, PitchRange.Voice), 0.15, 1.1);
                    var l = Evaluate(s, Track(s, PitchRange.Voice, PitchQuality.LowLatency), 0.15, 1.1);
                    Say($"  {f0} Hz vowel {v}: Quality {q.Rpa * 100:F0}% ({q.Octave} oct)  Low latency {l.Rpa * 100:F0}% ({l.Octave} oct)");
                    tq += q.Truth; cq += q.Correct; oq += q.Octave; tl += l.Truth; cl += l.Correct; ol += l.Octave;
                }
            Say($"voice 130-523 Hz: Quality RPA {F(100.0 * cq / tq)}% ({oq} octave errors), Low latency RPA {F(100.0 * cl / tl)}% ({ol} octave errors)");
            Assert.That((double)cl / tl, Is.GreaterThan(0.9));
        }

        // ── CPU and allocation ───────────────────────────────────────────

        [Test]
        public void CpuPerBlockOf256At48kHz()
        {
            var sig = PitchSignals.Voice(48000, 220, 2.0, new PitchSignals.VoiceOptions { VibratoCents = 40 }).X;
            double budget = 256 / 48000.0 * 1e6;
            var lines = new List<string>();
            foreach (var range in new[] { PitchRange.Voice, PitchRange.Guitar, PitchRange.Bass, PitchRange.Full })
            {
                var tr = new PitchTracker(48000, range);
                var buf = new PitchEstimate[tr.MaxFrames(256)];
                for (int pos = 0; pos + 256 <= 48000; pos += 256) tr.Process(sig, pos, 256, buf, 0);
                var sw = Stopwatch.StartNew();
                int blocks = 0;
                for (int rep = 0; rep < 3; rep++)
                    for (int pos = 0; pos + 256 <= sig.Length; pos += 256) { tr.Process(sig, pos, 256, buf, 0); blocks++; }
                double us = sw.Elapsed.TotalMilliseconds * 1000 / blocks;
                lines.Add($"tracker {range}: {F(us)} µs per block ({F(100 * us / budget)}% of the {F(budget, "F0")} µs block)");
                Assert.That(us / budget, Is.LessThan(0.15), range.ToString());
            }
            {
                var tr = new PitchTracker(48000, PitchRange.Voice, PitchQuality.LowLatency);
                var buf = new PitchEstimate[tr.MaxFrames(256)];
                for (int pos = 0; pos + 256 <= 48000; pos += 256) tr.Process(sig, pos, 256, buf, 0);
                var sw = Stopwatch.StartNew(); int blocks = 0;
                for (int rep = 0; rep < 3; rep++) for (int pos = 0; pos + 256 <= sig.Length; pos += 256) { tr.Process(sig, pos, 256, buf, 0); blocks++; }
                double us = sw.Elapsed.TotalMilliseconds * 1000 / blocks;
                lines.Add($"tracker Voice, low latency: {F(us)} µs per block ({F(100 * us / budget)}%)");
                Assert.That(us / budget, Is.LessThan(0.15));
            }
            {
                var od = new OnsetDetector(48000);
                var ev = new OnsetEvent[od.MaxEvents(256)];
                for (int pos = 0; pos + 256 <= 48000; pos += 256) od.Process(sig, pos, 256, ev, 0);
                var sw = Stopwatch.StartNew(); int blocks = 0;
                for (int rep = 0; rep < 3; rep++) for (int pos = 0; pos + 256 <= sig.Length; pos += 256) { od.Process(sig, pos, 256, ev, 0); blocks++; }
                double us = sw.Elapsed.TotalMilliseconds * 1000 / blocks;
                lines.Add($"onset detector: {F(us)} µs per block ({F(100 * us / budget)}%)");
                Assert.That(us / budget, Is.LessThan(0.1));
            }
            {
                var te = new TriggerEngine(48000, new TriggerClassifier());
                var kit = PitchSignals.Kit(48000, Enumerable.Range(0, 24).Select(i => i % 4).ToArray(), 0.1, 0.15, 5);
                var ev = new TriggerEvent[te.MaxEvents(256) + 32];
                var x = kit.X;
                var sw = Stopwatch.StartNew(); int blocks = 0;
                for (int pos = 0; pos + 256 <= x.Length; pos += 256) { te.Process(x, pos, 256, ev, 0); blocks++; }
                double us = sw.Elapsed.TotalMilliseconds * 1000 / blocks;
                lines.Add($"onsets + features + classifier (24 hits in {F(x.Length / 48000.0)} s): {F(us)} µs per block ({F(100 * us / budget)}%)");
                Assert.That(us / budget, Is.LessThan(0.1));
            }
            {
                var ve = new VowelEstimator(48000);
                var sw = Stopwatch.StartNew(); int blocks = 0;
                for (int rep = 0; rep < 3; rep++)
                    for (int pos = 0; pos + 256 <= sig.Length; pos += 256) { ve.Push(sig, pos, 256); if ((blocks & 1) == 0) ve.Update(true); blocks++; }
                double us = sw.Elapsed.TotalMilliseconds * 1000 / blocks;
                lines.Add($"vowel estimator (an update every other block, 5.3 ms): {F(us)} µs per block ({F(100 * us / budget)}%)");
                Assert.That(us / budget, Is.LessThan(0.1));
            }
            foreach (var l in lines) Say(l);
        }

        [Test]
        public void NothingAllocatesPerBlock()
        {
            var sig = PitchSignals.Voice(48000, 220, 1.0, new PitchSignals.VoiceOptions { VibratoCents = 40 }).X;
            var kit = PitchSignals.Kit(48000, Enumerable.Range(0, 16).Select(i => i % 4).ToArray(), 0.1, 0.15, 5).X;
            var trackers = new[] { PitchRange.Voice, PitchRange.Guitar, PitchRange.Bass, PitchRange.Full }.Select(r => new PitchTracker(48000, r)).Append(new PitchTracker(48000, PitchRange.Voice, PitchQuality.LowLatency)).ToArray();
            var pe = new PitchEstimate[trackers.Max(t => t.MaxFrames(256))];
            var od = new OnsetDetector(48000);
            var oe = new OnsetEvent[od.MaxEvents(256)];
            var cls = new TriggerClassifier();
            var feat = new float[TriggerFeatures.Dimension];
            var teach = new TriggerEngine(48000, cls) { Learning = 0 };
            var te = new TriggerEvent[teach.MaxEvents(256) + 32];
            for (int pos = 0; pos + 256 <= kit.Length; pos += 256) teach.Process(kit, pos, 256, te, 0);
            var te2 = new TriggerEngine(48000, cls);
            cls.Train();
            var ve = new VowelEstimator(48000);
            int blocks = kit.Length / 256 - 1;
            var (bytes, why) = AllocationCheck.Run(Math.Min(blocks, 300), b =>
            {
                int pos = (b * 256) % (sig.Length - 256);
                foreach (var t in trackers) t.Process(sig, pos, 256, pe, 0);
                od.Process(kit, b * 256, 256, oe, 0);
                te2.Process(kit, b * 256, 256, te, 0);
                ve.Push(sig, pos, 256);
                if ((b & 1) == 0) ve.Update(true);
                cls.Classify(feat);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }
    }
}
