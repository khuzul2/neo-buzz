// The shared onset detector, trigger classifier and vowel estimator (docs/specs/pitch.md §6), measured on synthetic sources.

using System.Globalization;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class PitchEngineTests
    {
        static void Say(string s) => TestContext.Out.WriteLine(s);
        static string F(double v, string fmt = "F1") => v.ToString(fmt, CultureInfo.InvariantCulture);

        static List<OnsetEvent> Onsets(float[] x, double sr = 48000, int block = 256, Action<OnsetDetector> setup = null)
        {
            var od = new OnsetDetector(sr);
            setup?.Invoke(od);
            var buf = new OnsetEvent[od.MaxEvents(block)];
            var list = new List<OnsetEvent>();
            for (int pos = 0; pos + block <= x.Length; pos += block)
            {
                int n = od.Process(x, pos, block, buf, 0);
                for (int i = 0; i < n; i++) list.Add(buf[i]);
            }
            return list;
        }

        sealed class OnsetScore
        {
            public int Truth, Found, Hit, FalsePositive, Missed;
            public List<double> ErrMs = new(), DelayMs = new();
            public double F => Found + Truth == 0 ? 0 : 2.0 * Hit / (Found + Truth);
            public double MedianAbs => ErrMs.Count == 0 ? 0 : ErrMs.Select(Math.Abs).OrderBy(v => v).ToList()[ErrMs.Count / 2];
            public double P95Abs => ErrMs.Count == 0 ? 0 : ErrMs.Select(Math.Abs).OrderBy(v => v).ToList()[(int)(ErrMs.Count * 0.95)];
            public double MedianDelay => DelayMs.Count == 0 ? 0 : DelayMs.OrderBy(v => v).ToList()[DelayMs.Count / 2];
            public override string ToString() => $"F {F(F * 100)}% ({Hit}/{Truth} found, {FalsePositive} false)  timing median {F(MedianAbs)} ms p95 {F(P95Abs)} ms  reported {F(MedianDelay)} ms after the onset";
        }

        static OnsetScore ScoreOnsets(List<OnsetEvent> found, IEnumerable<long> truth, double sr, double windowMs = 25)
        {
            var sc = new OnsetScore { Found = found.Count };
            var used = new HashSet<int>();
            foreach (long t in truth)
            {
                sc.Truth++;
                int best = -1; double bd = double.MaxValue;
                for (int i = 0; i < found.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    double d = Math.Abs(found[i].Sample - t) / sr * 1000;
                    if (d < bd) { bd = d; best = i; }
                }
                if (best >= 0 && bd <= windowMs)
                {
                    used.Add(best); sc.Hit++;
                    sc.ErrMs.Add((found[best].Sample - t) / sr * 1000);
                    sc.DelayMs.Add((found[best].ReportedAt - found[best].Sample) / sr * 1000);
                }
            }
            sc.FalsePositive = found.Count - sc.Hit;
            sc.Missed = sc.Truth - sc.Hit;
            return sc;
        }

        // ── onsets ───────────────────────────────────────────────────────

        [Test]
        public void KitHitsAreFoundWithinAFewMilliseconds()
        {
            var order = Enumerable.Range(0, 80).Select(i => (i * 5 + i / 8) % 8).ToArray();
            var (x, truth) = PitchSignals.Kit(48000, order, 0.12, 0.3, 21);
            var sc = ScoreOnsets(Onsets(x), truth.Select(t => t.Sample), 48000);
            Say($"kit, 80 hits of 8 classes, 120-300 ms apart: {sc}");
            Assert.That(sc.F, Is.GreaterThan(0.97));
            Assert.That(sc.MedianAbs, Is.LessThan(2.0));
            Assert.That(sc.P95Abs, Is.LessThan(6.0));
        }

        [Test]
        public void FastHitsOfTheKitAreFound()
        {
            var order = Enumerable.Range(0, 60).Select(i => i % 5 == 0 ? 0 : i % 5 == 2 ? 1 : 2).ToArray();
            var (x, truth) = PitchSignals.Kit(48000, order, 0.075, 0.1, 22);
            var sc = ScoreOnsets(Onsets(x), truth.Select(t => t.Sample), 48000);
            Say($"kit, 60 hits 75-100 ms apart (about 16ths at 150 bpm): {sc}");
            Assert.That(sc.F, Is.GreaterThan(0.85));
        }

        [Test]
        public void PluckedNotesAndSungNotesHaveTheirOnsetsAndSlidesHaveNone()
        {
            const double sr = 48000;
            // Five plucks, 0.5 s apart.
            var x = new float[(int)(3.2 * sr)];
            var truth = new List<long>();
            double[] hz = { 110, 146.8, 196, 246.9, 329.6 };
            for (int i = 0; i < hz.Length; i++)
            {
                var p = PitchSignals.Pluck(sr, _ => hz[i], 0.55, (ulong)(30 + i), 0.998, 0.5);
                int at = (int)((0.3 + i * 0.55) * sr);
                for (int k = 0; k < p.X.Length && at + k < x.Length; k++) x[at + k] += p.X[k];
                truth.Add(at);
            }
            var sc = ScoreOnsets(Onsets(x), truth, sr);
            Say($"five plucks: {sc}");
            Assert.That(sc.Hit, Is.EqualTo(5));
            Assert.That(sc.FalsePositive, Is.EqualTo(0));
            Assert.That(sc.MedianAbs, Is.LessThan(3.0));

            // Sung notes (the voice fades in over 10 ms): the onset is where it comes in.
            var y = new float[(int)(3.0 * sr)];
            var truth2 = new List<long>();
            for (int i = 0; i < 4; i++)
            {
                var v = PitchSignals.Voice(sr, 150 + 40 * i, 0.5, new PitchSignals.VoiceOptions { Seed = (ulong)(50 + i), Level = 0.3 });
                int at = (int)((0.3 + i * 0.6) * sr);
                for (int k = 0; k < v.X.Length; k++) y[at + k] += v.X[k];
                truth2.Add(at);
            }
            var sc2 = ScoreOnsets(Onsets(y), truth2, sr);
            Say($"four sung notes: {sc2}");
            Assert.That(sc2.Hit, Is.EqualTo(4));
            Assert.That(sc2.FalsePositive, Is.EqualTo(0));

            // A vibrato voice and a slide: nothing new starts.
            var vib = PitchSignals.Voice(sr, 220, 2.0, new PitchSignals.VoiceOptions { VibratoCents = 80, VibratoDelay = 0, GlideTo = 330, GlideStart = 0.5, GlideSeconds = 1.0, Level = 0.4 });
            var found = Onsets(vib.X);
            Say($"vibrato and slide: {found.Count} onsets (the voice's own start counts one)");
            Assert.That(found.Count, Is.LessThanOrEqualTo(1));
        }

        [Test]
        public void QuietNoiseAndLevelsBelowTheGateFireNothing()
        {
            var rng = new SynthRng(5);
            // Steady noise 14 dB over the gate still fires now and then (about once in 3 s at the default sensitivity); the gate is the cure.
            Assert.That(Onsets(PitchSignals.Noise(48000, 3, 0.02, rng)).Count, Is.LessThanOrEqualTo(2), "noise at -40 dB");
            Assert.That(Onsets(PitchSignals.Noise(48000, 3, 0.002, rng)).Count, Is.EqualTo(0), "noise at -60 dB, under the gate");
            Assert.That(Onsets(PitchSignals.Pink(48000, 3, 0.1, rng)).Count, Is.LessThanOrEqualTo(6), "pink noise at -20 dB (slow swells fire a few: about 4 in 3 s)");
            Assert.That(Onsets(PitchSignals.Silence(48000, 2)).Count, Is.EqualTo(0), "silence");
        }

        [Test]
        public void OnsetsDoNotDependOnTheBlockSize()
        {
            var order = Enumerable.Range(0, 24).Select(i => i % 4).ToArray();
            var (x, _) = PitchSignals.Kit(48000, order, 0.15, 0.25, 23);
            var a = Onsets(x, block: 64).Select(e => e.Sample).ToList();
            var b = Onsets(x, block: 1024).Select(e => e.Sample).ToList();
            var c = Onsets(x, block: 100).Select(e => e.Sample).ToList();
            Assert.That(b, Is.EqualTo(a));
            Assert.That(c, Is.EqualTo(a));
        }

        // ── triggers ─────────────────────────────────────────────────────

        static int[] Shuffled(int classes, int perClass, ulong seed)
        {
            var rng = new SynthRng(seed);
            var l = new List<int>();
            for (int c = 0; c < classes; c++) for (int i = 0; i < perClass; i++) l.Add(c);
            return l.OrderBy(_ => rng.Next()).ToArray();
        }

        sealed class TriggerResultSet
        {
            public int[,] Confusion;   // [truth, said]; said == classes: none / missed
            public int Total, Correct, Rejected, Missed, Extra;
            public List<double> ReadyMs = new();
            public double Accuracy => (double)Correct / Total;
        }

        static TriggerResultSet RunTriggers(int classes, int train, int test, bool fisher, ulong seed, float reject = 2.5f, double noiseDb = -60, double gapLo = 0.12, double gapHi = 0.25)
        {
            const double sr = 48000;
            var cls = new TriggerClassifier { FisherWeights = fisher, RejectScale = reject };
            // Training: one class at a time, `train` hits each.
            var engine = new TriggerEngine(sr, cls);
            var events = new TriggerEvent[engine.MaxEvents(256) + 32];
            for (int c = 0; c < classes; c++)
            {
                engine.Reset();
                engine.Learning = c;
                var (x, _) = PitchSignals.Kit(sr, Enumerable.Repeat(c, train).ToArray(), 0.2, 0.3, seed * 100 + (ulong)c, noiseDb);
                for (int pos = 0; pos + 256 <= x.Length; pos += 256) engine.Process(x, pos, 256, events, 0);
            }
            engine.Learning = -1;
            engine.Reset();
            Assert.That(cls.Train(), Is.True);
            for (int c = 0; c < classes; c++) Assert.That(cls.ExamplesOf(c), Is.GreaterThanOrEqualTo(train - 2), $"class {c} examples");

            var (y, truth) = PitchSignals.Kit(sr, Shuffled(classes, test, seed), gapLo, gapHi, seed * 1000 + 7, noiseDb);
            var found = new List<TriggerEvent>();
            for (int pos = 0; pos + 256 <= y.Length; pos += 256)
            {
                int n = engine.Process(y, pos, 256, events, 0);
                for (int i = 0; i < n; i++) found.Add(events[i]);
            }
            var r = new TriggerResultSet { Confusion = new int[classes, classes + 1], Total = truth.Count };
            var used = new HashSet<int>();
            foreach (var (s, c) in truth)
            {
                int best = -1; double bd = double.MaxValue;
                for (int i = 0; i < found.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    double d = Math.Abs(found[i].Sample - s) / sr * 1000;
                    if (d < bd) { bd = d; best = i; }
                }
                if (best < 0 || bd > 25) { r.Missed++; r.Confusion[c, classes]++; continue; }
                used.Add(best);
                int said = found[best].Class;
                r.ReadyMs.Add((found[best].ReadyAt - s) / sr * 1000);
                if (said < 0) { r.Rejected++; r.Confusion[c, classes]++; }
                else { r.Confusion[c, said]++; if (said == c) r.Correct++; }
            }
            r.Extra = found.Count - used.Count;
            return r;
        }

        static string Matrix(TriggerResultSet r, int classes)
        {
            var sb = new System.Text.StringBuilder();
            for (int c = 0; c < classes; c++)
            {
                sb.Append($"    {PitchSignals.KitNames[c],-11}");
                for (int k = 0; k <= classes; k++) sb.Append($"{r.Confusion[c, k],4}");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        [TestCase(4, 10, 40, 0.12, 0.25, 0.78)]
        [TestCase(8, 10, 40, 0.12, 0.25, 0.74)]
        [TestCase(3, 10, 40, 0.12, 0.25, 0.88)]
        [TestCase(4, 10, 40, 0.45, 0.6, 0.9)]
        [TestCase(8, 10, 40, 0.45, 0.6, 0.85)]
        public void TrainedOnTenHitsEachTheKitIsTold(int classes, int train, int test, double gapLo, double gapHi, double floor)
        {
            double sum = 0, worst = 1;
            var all = new List<TriggerResultSet>();
            foreach (ulong seed in new ulong[] { 1, 2, 3 })
            {
                var r = RunTriggers(classes, train, test, true, seed, gapLo: gapLo, gapHi: gapHi);
                all.Add(r);
                sum += r.Accuracy; worst = Math.Min(worst, r.Accuracy);
                if (seed == 1) Say($"{classes} classes, seed 1 (columns: the {classes} classes, then none/missed):\n{Matrix(r, classes)}");
            }
            double ready = all.SelectMany(r => r.ReadyMs).OrderBy(v => v).ElementAt(all.Sum(r => r.ReadyMs.Count) / 2);
            Say($"{classes} classes, hits {gapLo * 1000:F0}-{gapHi * 1000:F0} ms apart, {train} examples each, {test} test hits each, 3 seeds: accuracy mean {F(100 * sum / 3)}%, worst {F(100 * worst)}%; rejected {all.Sum(r => r.Rejected)}, missed {all.Sum(r => r.Missed)}, extra onsets {all.Sum(r => r.Extra)}; classified {F(ready)} ms after the onset (median)");
            Assert.That(sum / 3, Is.GreaterThan(floor));
            Assert.That(worst, Is.GreaterThan(floor - 0.05));
        }

        [Test]
        public void FisherWeightsAreMeasuredAgainstPlainStandardisation()
        {
            foreach (int classes in new[] { 4, 8 })
            {
                double plain = 0, fisher = 0;
                foreach (ulong seed in new ulong[] { 1, 2, 3 })
                {
                    plain += RunTriggers(classes, 10, 40, false, seed).Accuracy / 3;
                    fisher += RunTriggers(classes, 10, 40, true, seed).Accuracy / 3;
                }
                Say($"{classes} classes: plain standardisation {F(100 * plain)}%, with Fisher weights {F(100 * fisher)}%");
            }
        }

        [Test]
        public void SoundsOutsideTheClassesAreMostlyRejected()
        {
            const double sr = 48000;
            var cls = new TriggerClassifier();
            var engine = new TriggerEngine(sr, cls);
            var events = new TriggerEvent[engine.MaxEvents(256) + 32];
            for (int c = 0; c < 4; c++)
            {
                engine.Reset();
                engine.Learning = c;
                var (x, _) = PitchSignals.Kit(sr, Enumerable.Repeat(c, 10).ToArray(), 0.2, 0.3, 900 + (ulong)c);
                for (int pos = 0; pos + 256 <= x.Length; pos += 256) engine.Process(x, pos, 256, events, 0);
            }
            engine.Learning = -1; engine.Reset(); cls.Train();
            // What the four don't include: a clap, a rim, a tom, a "k" click (kit classes 4 to 7 of the 8).
            var (y, truth) = PitchSignals.Kit(sr, Shuffled(8, 20, 5).Where(c => c >= 4).ToArray(), 0.12, 0.25, 77);
            int said = 0, none = 0;
            var perClass = new int[8]; var perNone = new int[8];
            var list = new List<TriggerEvent>();
            for (int pos = 0; pos + 256 <= y.Length; pos += 256) { int n = engine.Process(y, pos, 256, events, 0); for (int i = 0; i < n; i++) list.Add(events[i]); }
            foreach (var (s, c) in truth)
            {
                var e = list.OrderBy(v => Math.Abs(v.Sample - s)).First();
                if (Math.Abs(e.Sample - s) > 1200) continue;
                if (e.Class < 0) { none++; perNone[c]++; } else said++;
                perClass[c]++;
            }
            Say($"hits of four classes it wasn't taught (tom, clap, rim, k click): {none} rejected, {said} given a class; rejected per class: " + string.Join(", ", Enumerable.Range(4, 4).Select(c => $"{PitchSignals.KitNames[c]} {perNone[c]}/{perClass[c]}")));
            // Not a strong promise: a tom is a kick to a four-class model. Reported, with a floor.
            Assert.That(none + said, Is.GreaterThan(40));
            Assert.That((double)none / (none + said), Is.GreaterThan(0.3));
        }

        [Test]
        public void TheExamplesSaveAsPlainTextAndLoadBackToTheSameAnswers()
        {
            const double sr = 48000;
            var cls = new TriggerClassifier();
            var engine = new TriggerEngine(sr, cls);
            var events = new TriggerEvent[engine.MaxEvents(256) + 32];
            for (int c = 0; c < 4; c++)
            {
                engine.Reset();
                engine.Learning = c;
                var (x, _) = PitchSignals.Kit(sr, Enumerable.Repeat(c, 10).ToArray(), 0.2, 0.3, 300 + (ulong)c);
                for (int pos = 0; pos + 256 <= x.Length; pos += 256) engine.Process(x, pos, 256, events, 0);
            }
            cls.Train();
            string text = cls.Serialize();
            Say($"profile: {text.Length} bytes, first line '{text.Substring(0, text.IndexOf('\n'))}'");
            var copy = new TriggerClassifier();
            Assert.That(copy.Deserialize(text), Is.True);
            Assert.That(copy.Count, Is.EqualTo(cls.Count));
            var feat = new TriggerFeatures(sr);
            var ring = new float[8192];
            var (y, truth) = PitchSignals.Kit(sr, Shuffled(4, 6, 8), 0.2, 0.3, 11);
            var f = new float[TriggerFeatures.Dimension];
            foreach (var (s, c) in truth)
            {
                Array.Clear(ring, 0, ring.Length);
                for (long i = s - 200; i < s + 1500; i++) ring[(int)(i & 8191)] = y[i];
                feat.Extract(ring, 8191, s, f);
                var a = cls.Classify(f);
                var b = copy.Classify(f);
                Assert.That(b.Class, Is.EqualTo(a.Class));
                Assert.That(b.Confidence, Is.EqualTo(a.Confidence).Within(1e-4));
            }
            Assert.That(new TriggerClassifier().Deserialize("garbage"), Is.False);
            Assert.That(new TriggerClassifier().Deserialize("NBTRIG 2 19 0\n"), Is.False);
            // Untrained: everything is "none".
            Assert.That(new TriggerClassifier().Classify(f).Class, Is.EqualTo(-1));
        }

        // ── vowels ───────────────────────────────────────────────────────

        [Test]
        public void ThreeVowelsAreToldApartAcrossPitchesAndVoices()
        {
            const double sr = 48000;
            string[] names = { "aa", "oo", "ee" };
            int total = 0, right = 0;
            var rows = new List<string>();
            ulong seed = 100;
            foreach (var (label, table, f0s) in new[] { ("lower voice", PitchSignals.VowelsLow, new[] { 100.0, 130.8, 165, 196 }), ("higher voice", PitchSignals.VowelsHigh, new[] { 196.0, 220, 262, 294 }) })
                for (int v = 0; v < 3; v++)
                    foreach (double f0 in f0s)
                    {
                        var s = PitchSignals.Voice(sr, f0, 0.6, new PitchSignals.VoiceOptions { Formants = table[v], HighLift = 0.95, VibratoCents = 30, VibratoDelay = 0.1, Seed = seed++ });
                        var ve = new VowelEstimator(sr);
                        VowelResult res = default;
                        for (int pos = 0; pos + 256 <= s.X.Length; pos += 256)
                        {
                            ve.Push(s.X, pos, 256);
                            if ((pos / 256) % 2 == 1) res = ve.Update(true);
                        }
                        float[] w = { res.Aa, res.Oo, res.Ee };
                        int arg = Array.IndexOf(w, w.Max());
                        total++; if (arg == v) right++;
                        rows.Add($"  {label,-12}{names[v]} at {f0,5:F0} Hz: aa {w[0]:F2} oo {w[1]:F2} ee {w[2]:F2}  F1 {res.F1:F0} F2 {res.F2:F0}  L/M/H {ve.Regions.Low:F0}/{ve.Regions.Mid:F0}/{ve.Regions.High:F0} dB{(arg == v ? "" : "   WRONG")}");
                    }
            Say(string.Join("\n", rows));
            Say($"vowel of the three: {right}/{total} ({F(100.0 * right / total)}%)");
            Assert.That((double)right / total, Is.GreaterThan(0.9));
        }

        [Test]
        public void VowelWeightsFallWhenTheVoiceStopsAndFollowTheSmoothing()
        {
            const double sr = 48000;
            var s = PitchSignals.Voice(sr, 130.8, 1.0, new PitchSignals.VoiceOptions { Formants = PitchSignals.VowelsLow[0], Seed = 4, HighLift = 0.95 });
            var ve = new VowelEstimator(sr);
            VowelResult res = default;
            for (int pos = 0; pos + 480 <= s.X.Length; pos += 480) { ve.Push(s.X, pos, 480); res = ve.Update(true); }
            Assert.That(res.Aa, Is.GreaterThan(0.5));
            Assert.That(res.Valid, Is.True);
            // Unvoiced: the weights fall away (the one-pole: under 5 % after three time constants).
            for (int i = 0; i < 20; i++) { ve.Push(new float[480], 0, 480); res = ve.Update(false); }
            Say($"after 200 ms unvoiced: aa {res.Aa:F3}");
            Assert.That(res.Aa, Is.LessThan(0.05));
            Assert.That(res.Valid, Is.False);
        }
    }
}
