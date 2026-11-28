using System.Xml.Linq;
using NeoBuzz.Drums;
using NeoBuzz.Dsp;
using NeoBuzz.FollowMachine;
using F = NeoBuzz.FollowMachine.P;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Follow (issue #199, docs/specs/neo-follow.md): the engine's gates, measured. The machine's own gates (the audio passing through unchanged, the targets saved and loaded) are in ReBuzz.Studio.Tests/NeoEditorTests.cs.</summary>
    [TestFixture]
    public class NeoFollowTests
    {
        const int Rate = NeoFollowRig.Rate;
        static void Set(ParamValues v, int p, double value) => NeoFollowRig.Set(v, p, value);
        static void Say(string s) => Measure.Report("Neo Follow " + s);

        // ------------------------------------------------------------------ Envelope

        /// <summary>The seconds from the step at <paramref name="from"/> until the series has covered 63.2 % (rising) or 63.2 % (falling) of the way to its new value.</summary>
        static double StepTime(float[] y, int block, double from, double lo, double hi, bool rising)
        {
            int start = (int)(from * Rate / block);
            double target = rising ? lo + (hi - lo) * (1 - Math.Exp(-1)) : lo + (hi - lo) * Math.Exp(-1);
            for (int i = start; i < y.Length; i++)
                if (rising ? y[i] >= target : y[i] <= target) return (i + 1) * (double)block / Rate - from;
            return double.NaN;
        }

        [TestCase(0, 5.0, 50.0)]
        [TestCase(0, 20.0, 200.0)]
        [TestCase(0, 100.0, 1000.0)]
        [TestCase(1, 10.0, 100.0)]
        [TestCase(1, 20.0, 200.0)]
        [TestCase(1, 50.0, 500.0)]
        [TestCase(1, 100.0, 1000.0)]
        public void ALevelStepGivesAttackAndReleaseTimesWithinTenPercentOfTheirSettings(int detector, double attackMs, double releaseMs)
        {
            var (e, _) = NeoFollowRig.Make(v =>
            {
                Set(v, F.Mode, 0);
                Set(v, F.Detector, detector);
                Set(v, F.Attack, attackMs);
                Set(v, F.Release, releaseMs);
            });
            // 1 kHz at 0.05, up to 0.5 at 2 s, back at 6 s (long enough for the slowest release to settle).
            var x = NeoFollowRig.Steps(1000, 0.05, 0.5, 2.0, 6.0, 12.0);
            const int block = 16;
            var y = NeoFollowRig.Run(e, x, block, en => en.Level);
            double lo = y[(int)(1.9 * Rate / block)], hi = y[(int)(5.9 * Rate / block)], lo2 = y[(int)(11.9 * Rate / block)];
            double up = StepTime(y, block, 2.0, lo, hi, rising: true) * 1000;
            double down = StepTime(y, block, 6.0, lo2, hi, rising: false) * 1000;
            Say($"{(detector == 0 ? "Peak" : "RMS")} attack {attackMs} ms: {up:F2} ms ({(up / attackMs - 1) * 100:+0.0;-0.0} %), release {releaseMs} ms: {down:F1} ms ({(down / releaseMs - 1) * 100:+0.0;-0.0} %)");
            Assert.That(up, Is.EqualTo(attackMs).Within(10).Percent, "attack");
            Assert.That(down, Is.EqualTo(releaseMs).Within(10).Percent, "release");
        }

        [TestCase(0.0)]
        [TestCase(50.0)]
        [TestCase(-50.0)]
        [TestCase(100.0)]
        public void TheLevelFollowsFloorCeilingAndCurveWithinHalfADecibelAtFivePoints(double curve)
        {
            foreach (int detector in new[] { 0, 1 })
            {
                double worst = 0;
                foreach (double dbPeak in new[] { -54.0, -42.0, -30.0, -18.0, -9.0 })
                {
                    var (e, _) = NeoFollowRig.Make(v =>
                    {
                        Set(v, F.Mode, 0);
                        Set(v, F.Detector, detector);
                        Set(v, F.Attack, 10);
                        Set(v, F.Release, 100);
                        Set(v, F.Floor, -60);
                        Set(v, F.Ceiling, -6);
                        Set(v, F.Curve, curve / 100.0);   // a fraction: -1 … +1
                    });
                    var x = Measure.Sine(1000, Rate, Math.Pow(10, dbPeak / 20));
                    var y = NeoFollowRig.Run(e, x, 32);
                    double u = 0;
                    int from = y.Length * 3 / 4;
                    for (int i = from; i < y.Length; i++) u += y[i];
                    u /= y.Length - from;
                    double trueDb = dbPeak - (detector == 1 ? 3.0103 : 0.0);
                    // What level this output stands for under the settings: undo the curve, then the line from floor to ceiling.
                    double t = u <= 0 || u >= 1 || curve == 0 ? u : Math.Pow(u, 1.0 / Math.Pow(2.0, curve / 50.0));
                    double seen = -60 + t * 54.0;
                    double want = Math.Clamp(trueDb, -60, -6);
                    worst = Math.Max(worst, Math.Abs(seen - want));
                    Assert.That(seen, Is.EqualTo(want).Within(0.5), $"{(detector == 0 ? "Peak" : "RMS")} at {dbPeak} dB, curve {curve} %");
                }
                Say($"{(detector == 0 ? "Peak" : "RMS")} level to output, curve {curve} %, floor -60 dB, ceiling -6 dB, 5 points from -54 to -9 dB: worst {worst:F3} dB");
            }
        }

        // ------------------------------------------------------------------ Pitch

        static (FollowEngine E, ParamValues V) PitchEngine(int range = 3, double confidence = 0.5, double glideMs = 0, double low = 36, double high = 84)
            => NeoFollowRig.Make(v =>
            {
                Set(v, F.Mode, 1);
                Set(v, F.PitchRange, range);
                Set(v, F.Confidence, confidence);
                Set(v, F.PitchGlide, glideMs);
                Set(v, F.Low, low);
                Set(v, F.High, high);
            });

        [TestCase(8.0)]
        [TestCase(16.0)]
        public void ASineGlideFromC2ToC6IsFollowedWithinFiveCents(double seconds)
        {
            var (e, _) = PitchEngine();
            const double lead = 0.6, tail = 0.6;
            var (x, note) = NeoFollowRig.Glide(36, 84, lead, seconds, tail);
            const int block = 64;
            var seen = NeoFollowRig.Run(e, x, block, en => en.Note);
            double worst = 0, worstRaw = 0;
            int checkedBlocks = 0, delayAtLow = e.ControlDelay(FollowMode.Pitch), delayAtHigh = e.ControlDelay(FollowMode.Pitch, 1046.5);
            for (int b = 0; b < seen.Length; b++)
            {
                int end = Math.Min(x.Length, (b + 1) * block);
                if (end < (int)(0.4 * Rate) || seen[b] == 0) continue;
                // The note the engine says now describes the audio a control delay ago, which depends on the pitch (the window and a period).
                int at = end - 1;
                for (int it = 0; it < 4; it++)
                    at = Math.Max(0, end - 1 - e.ControlDelay(FollowMode.Pitch, 440.0 * Math.Pow(2.0, (note[at] - 69.0) / 12.0)));
                worst = Math.Max(worst, Math.Abs(seen[b] - note[at]) * 100);
                worstRaw = Math.Max(worstRaw, Math.Abs(seen[b] - note[end - 1]) * 100);
                checkedBlocks++;
            }
            Say($"pitch glide C2 to C6 in {seconds} s ({48.0 / seconds:F1} semitones a second): worst {worst:F2} cents against the note a control delay earlier ({delayAtLow} samples at C2, {delayAtHigh} at C6), {worstRaw:F1} cents against the note now; {checkedBlocks} blocks");
            Assert.That(checkedBlocks, Is.GreaterThan(seen.Length / 2));
            Assert.That(worst, Is.LessThan(5.0), "cents against the note the control delay earlier");
        }

        [Test]
        public void ThePitchOutputMatchesTheLowAndHighMappingWithinAHalfPercent()
        {
            double worst = 0;
            foreach (var (low, high) in new[] { (36.0, 84.0), (48.0, 72.0), (60.0, 96.0) })
                foreach (double note in new[] { 24.0, 36.0, 43.0, 52.0, 60.0, 69.0, 81.0, 84.0, 96.0 })
                {
                    var (e, _) = PitchEngine(low: low, high: high);
                    double hz = 440.0 * Math.Pow(2.0, (note - 69.0) / 12.0);
                    if (hz < 38 || hz > 1900) continue;
                    var x = Measure.Sine(hz, Rate * 2, 0.3);
                    var y = NeoFollowRig.Run(e, x, 64);
                    double want = Math.Clamp((note - low) / (high - low), 0, 1);
                    double got = y[^1];
                    worst = Math.Max(worst, Math.Abs(got - want));
                    Assert.That(got, Is.EqualTo(want).Within(0.005), $"note {note} on {low} … {high}");
                }
            Say($"pitch output against the Low and High mapping: worst {worst * 100:F3} % of the range");
        }

        [Test]
        public void SilenceHoldsTheLastValueAndSoDoesAnUnclearSound()
        {
            var (e, _) = PitchEngine(confidence: 0.8);
            var tone = Measure.Sine(440, Rate, 0.3);
            var quiet = new float[Rate * 2];
            var noise = new float[Rate];
            var rng = new Random(5);
            for (int i = 0; i < noise.Length; i++) noise[i] = (float)((rng.NextDouble() * 2 - 1) * 0.3);
            var yTone = NeoFollowRig.Run(e, tone, 64);
            Assert.That(yTone[^1], Is.GreaterThan(0.4f).And.LessThan(0.8f), "A4 on C2 … C6 is 69 on 36 … 84");
            var yQuiet = NeoFollowRig.Run(e, quiet, 64);
            // The estimates of the last window of the tone still arrive for a few blocks after it; from 100 ms on, nothing does.
            var settled = yQuiet.Skip(Rate / 10 / 64).ToArray();
            float held = settled[0];
            Assert.That(settled.Min(), Is.EqualTo(held), "silence: the output does not move");
            Assert.That(settled.Max(), Is.EqualTo(held));
            Assert.That(held, Is.EqualTo(yTone[^1]).Within(0.003f));
            var yNoise = NeoFollowRig.Run(e, noise, 64);
            Say($"pitch hold: tone {yTone[^1]:F4}, then silence {settled.Min():F6} … {settled.Max():F6}, then noise {yNoise.Min():F4} … {yNoise.Max():F4}");
            Assert.That(yNoise.Min(), Is.EqualTo(held).Within(0.005), "noise (no pitch): the output holds");
            Assert.That(yNoise.Max(), Is.EqualTo(held).Within(0.005));
        }

        // ------------------------------------------------------------------ Onset

        static readonly List<long> Delays = new();

        /// <summary>The onset times (stream positions of the detector's own refined onset sample) the engine finds in a signal: the engine's pulse rises at each.</summary>
        static List<long> Onsets(float[] x, Action<ParamValues> setup = null, int block = 64)
        {
            var (e, _) = NeoFollowRig.Make(v =>
            {
                Set(v, F.Mode, 2);
                setup?.Invoke(v);
            });
            var found = new List<long>();
            Delays.Clear();
            long seen = 0;
            for (int pos = 0; pos < x.Length; pos += block)
            {
                int n = Math.Min(block, x.Length - pos);
                NeoFollowRig.Feed(e, x, pos, n);
                if (e.OnsetCount > seen)
                {
                    for (long k = seen; k < e.OnsetCount; k++) { found.Add(e.LastOnsetSample); Delays.Add(e.LastOnsetDelay); }
                    seen = e.OnsetCount;
                }
            }
            return found;
        }

        [TestCase(0.5, 90.0)]
        [TestCase(0.5, 120.0)]
        [TestCase(0.5, 150.0)]
        public void OnANeoDrumsLoopAtLeast95PercentOfTheHitsAreFoundEachWithinTenMilliseconds(double sensitivity, double bpm)
        {
            var (x, truth) = NeoFollowRig.DrumLoop(8, bpm);
            var found = Onsets(x, v => Set(v, F.Sensitivity, sensitivity));
            int hit = 0, extra = 0;
            double worst = 0;
            var used = new bool[found.Count];
            foreach (long t in truth)
            {
                int best = -1;
                double bd = 0.010 * Rate;
                for (int i = 0; i < found.Count; i++)
                    if (!used[i] && Math.Abs(found[i] - t) <= bd) { bd = Math.Abs(found[i] - t); best = i; }
                if (best >= 0) { used[best] = true; hit++; worst = Math.Max(worst, bd * 1000.0 / Rate); }
            }
            for (int i = 0; i < used.Length; i++) if (!used[i]) extra++;
            double pct = 100.0 * hit / truth.Count;
            var ds = Delays.OrderBy(d => d).ToList();
            Say($"onset report delay on that loop: median {ds[ds.Count / 2] * 1000.0 / Rate:F1} ms, 90th percentile {ds[ds.Count * 9 / 10] * 1000.0 / Rate:F1} ms, worst {ds[^1] * 1000.0 / Rate:F1} ms");
            Say($"onsets on a Neo Drums loop at {bpm} bpm, sensitivity {sensitivity * 100:F0} %: {hit} of {truth.Count} onsets found ({pct:F1} %), worst offset {worst:F2} ms, {extra} extra");
            Assert.That(pct, Is.GreaterThanOrEqualTo(95.0));
        }

        /// <summary>A sustained Neo Poly pad: a saw chord with a slow attack, held for <paramref name="seconds"/>.</summary>
        static float[] PolyPad(double seconds, double attack = 0.4, int[] chord = null, double o2Level = 0.7, double o2Fine = 8.0, double cutoff = 1800.0)
        {
            var pv = new ParamValues(NeoBuzz.Poly.Params.Table);
            void SetPoly(int p, double val) => pv.Set(p, NeoBuzz.Poly.Params.Table[p].Raw(val));
            SetPoly(NeoBuzz.Poly.P.O1Shape, 2.0);
            SetPoly(NeoBuzz.Poly.P.O2Shape, 2.5);
            SetPoly(NeoBuzz.Poly.P.O2Level, o2Level);
            SetPoly(NeoBuzz.Poly.P.O2Fine, o2Fine);
            pv.Set(NeoBuzz.Poly.P.F1Type, 0);
            SetPoly(NeoBuzz.Poly.P.F1Cutoff, cutoff);
            SetPoly(NeoBuzz.Poly.P.F1Reso, 0.3);
            SetPoly(NeoBuzz.Poly.P.F1Env, 0.0);
            SetPoly(NeoBuzz.Poly.P.AmpA, attack);
            SetPoly(NeoBuzz.Poly.P.AmpD, 0.10);
            SetPoly(NeoBuzz.Poly.P.AmpS, 1.0);
            SetPoly(NeoBuzz.Poly.P.Volume, -12.0);
            var poly = new NeoBuzz.Poly.PolyEngine(pv);
            poly.SetSampleRate(Rate);
            chord ??= new[] { 45, 52, 55, 59, 60 };
            for (int k = 0; k < chord.Length; k++) poly.NoteOn(1000 + k, chord[k], 0.8f);
            int total = (int)(seconds * Rate);
            var x = new float[total];
            var l = new float[256];
            var r = new float[256];
            for (int pos = 0; pos < total; pos += 256)
            {
                int n = Math.Min(256, total - pos);
                poly.Process(l, r, n);
                for (int i = 0; i < n; i++) x[pos + i] = 0.5f * (l[i] + r[i]);
            }
            return x;
        }

        [Test]
        public void ASustainedNeoPolyPadFiresNoPulse()
        {
            // A pad: a saw chord on two detuned oscillators (they beat), 0.8 s attack, held for 12 s.
            var x = PolyPad(12.0, 0.8, new[] { 48, 55, 60, 64, 67 }, 0.5, 5.0);
            double peak = Measure.Peak(x, 0, x.Length);
            var found = Onsets(x, v => Set(v, F.Sensitivity, 0.5));
            var eager = Onsets(x, v => Set(v, F.Sensitivity, 1.0));
            var lowChord = Onsets(PolyPad(12.0, 0.4), v => Set(v, F.Sensitivity, 0.5));
            Say($"onsets on a sustained Neo Poly pad (12 s, peak {Measure.Db(peak):F1} dBFS): {found.Count} at sensitivity 50 %, {eager.Count} at 100 %; an A minor chord with its roots at 110 and 165 Hz and 8 cents of detune: {lowChord.Count}");
            Assert.That(peak, Is.GreaterThan(0.05), "the pad sounds");
            Assert.That(found.Count, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ the pulse

        /// <summary>One Neo Drums hit of <paramref name="voice"/> at <paramref name="velocity"/> after 0.3 s of silence, 2 s long, mono.</summary>
        static float[] OneHit(int voice, float velocity, double seconds = 2.0)
        {
            var v = new ParamValues(NeoBuzz.Drums.Params.Table);
            var d = new DrumsEngine(v);
            d.SetSampleRate(Rate);
            var (l, r) = DrumsTests.Run(d, (int)(seconds * Rate), DrumsEngine.MaxBlock, ((int)(0.3 * Rate), () => d.Hit(voice, velocity)));
            var x = new float[l.Length];
            for (int i = 0; i < x.Length; i++) x[i] = 0.5f * (l[i] + r[i]);
            return x;
        }

        [Test]
        public void APulseHoldsThenFallsWithTheDecayTimeConstantAndDynamicScalesIt()
        {
            var hit = OneHit(1, 1f);
            foreach (var (holdMs, decayMs) in new[] { (0.0, 200.0), (100.0, 100.0), (250.0, 400.0) })
            {
                var (e, _) = NeoFollowRig.Make(v =>
                {
                    Set(v, F.Mode, 2);
                    Set(v, F.Dynamic, 0);
                    Set(v, F.Hold, holdMs);
                    Set(v, F.Decay, decayMs);
                });
                const int block = 32;
                var p = NeoFollowRig.Run(e, hit, block, en => en.Pulse);
                int first = Array.FindIndex(p, y => y > 0.99f);
                Assert.That(first, Is.GreaterThan(0), "a pulse starts");
                double heldMs = 0;
                int i = first;
                while (i < p.Length && p[i] > 0.999f) { heldMs += block * 1000.0 / Rate; i++; }
                // The fall: the time from the end of the hold until the pulse has lost 63 %.
                int j = i;
                while (j < p.Length && p[j] > Math.Exp(-1)) j++;
                double fallMs = (j - i) * block * 1000.0 / Rate;
                Say($"pulse: hold {holdMs} ms gave {heldMs:F1} ms at full, decay {decayMs} ms gave {fallMs:F1} ms to 37 %");
                Assert.That(Math.Abs(heldMs - holdMs), Is.LessThanOrEqualTo(2 * block * 1000.0 / Rate), "the hold");
                Assert.That(fallMs, Is.EqualTo(decayMs).Within(5).Percent.Or.EqualTo(decayMs).Within(block * 1000.0 / Rate), "the decay");
            }

            // Dynamic: a soft hit makes a smaller pulse; with it off every hit is full.
            float Peak(float velocity, bool dynamic)
            {
                var (e, _) = NeoFollowRig.Make(v => { Set(v, F.Mode, 2); Set(v, F.Dynamic, dynamic ? 1 : 0); Set(v, F.Decay, 500); });
                return NeoFollowRig.Run(e, OneHit(1, velocity), 32, en => en.Pulse).Max();
            }
            float loud = Peak(1f, true), soft = Peak(0.2f, true), fixedSoft = Peak(0.2f, false);
            Say($"pulse height: loud {loud:F3}, soft {soft:F3} with Dynamic on; soft {fixedSoft:F3} with it off");
            Assert.That(loud, Is.GreaterThan(0.5f));
            Assert.That(soft, Is.LessThan(0.75f * loud));
            Assert.That(soft, Is.GreaterThan(0.1f - 1e-6f), "never nothing: a found hit makes a pulse");
            Assert.That(fixedSoft, Is.EqualTo(1f).Within(1e-6f));
        }

        // ------------------------------------------------------------------ latency

        [Test]
        public void TheControlDelayOfEachModeIsStatedAndMeasured()
        {
            // Envelope: none of its own; the first block of a step already moves the output (attack 0.1 ms).
            {
                var (e, _) = NeoFollowRig.Make(v => { Set(v, F.Mode, 0); Set(v, F.Attack, 0.1); Set(v, F.Release, 100); Set(v, F.Floor, -60); Set(v, F.Ceiling, -6); });
                var x = NeoFollowRig.Steps(1000, 0.0, 0.5, 0.5, 5.0, 1.0);
                var y = NeoFollowRig.Run(e, x, 32);
                int at = (int)(0.5 * Rate / 32);
                int moved = Array.FindIndex(y, at, v => v > 0.5f * y[at + 20]);
                double delay = (moved + 1 - at) * 32.0;
                Say($"control delay, Envelope: stated {e.ControlDelay(FollowMode.Envelope)} samples, measured {delay} samples (the first 32-sample block of the step)");
                Assert.That(e.ControlDelay(FollowMode.Envelope), Is.EqualTo(0));
                Assert.That(delay, Is.LessThanOrEqualTo(64));
            }
            // Pitch: A3 to A4 at 0.5 s; the time until the note has covered half the way (the middle of the step is the delay of the window).
            {
                var (e, _) = PitchEngine(range: 3, glideMs: 0);
                var a = Measure.Sine(220, Rate / 2, 0.3);
                var b = Measure.Sine(440, Rate, 0.3);
                var x = new float[a.Length + b.Length];
                Array.Copy(a, x, a.Length);
                for (int i = 0; i < b.Length; i++) x[a.Length + i] = (float)(0.3 * Math.Sin(2 * Math.PI * 440.0 * i / Rate + 0.0)) ;
                var y = NeoFollowRig.Run(e, x, 32, en => en.Note);
                int at = a.Length / 32;
                int moved = Array.FindIndex(y, at, v => v >= 63.0);
                double delay = (moved + 1) * 32.0 - a.Length;
                int stated = e.ControlDelay(FollowMode.Pitch, 440);
                Say($"control delay, Pitch (Full range) on A3 to A4: stated {stated} samples ({stated * 1000.0 / Rate:F1} ms), measured {delay} samples ({delay * 1000.0 / Rate:F1} ms) to the middle of the step");
                // A step is crossed when the new pitch fills most of the analysis window: up to about two of the stated (glide) delays.
                Assert.That(delay, Is.InRange(0.5 * stated, 2.2 * stated));
            }
            // Onset: from the hit to the pulse rising, on a Neo Drums loop.
            {
                var (x, truth) = NeoFollowRig.DrumLoop(8);
                var (e, _) = NeoFollowRig.Make(v => { Set(v, F.Mode, 2); Set(v, F.Dynamic, 0); Set(v, F.Decay, 100); });
                var ds = new List<double>();
                long seen = 0;
                for (int pos = 0; pos < x.Length; pos += 32)
                {
                    int n = Math.Min(32, x.Length - pos);
                    NeoFollowRig.Feed(e, x, pos, n);
                    if (e.OnsetCount > seen)
                    {
                        seen = e.OnsetCount;
                        long hit = truth.OrderBy(t => Math.Abs(t - e.LastOnsetSample)).First();
                        ds.Add(pos + n - hit);
                    }
                }
                ds.Sort();
                int stated = e.ControlDelay(FollowMode.Onset);
                Say($"control delay, Onset: stated {stated} samples ({stated * 1000.0 / Rate:F1} ms), measured from the hit to the pulse: median {ds[ds.Count / 2] * 1000.0 / Rate:F1} ms, worst {ds[^1] * 1000.0 / Rate:F1} ms over {ds.Count} hits");
                Assert.That(ds[ds.Count / 2], Is.InRange(0.5 * stated, 1.5 * stated));
                Assert.That(ds[^1], Is.LessThanOrEqualTo(2 * stated));
            }
        }

        // ------------------------------------------------------------------ the shape

        [Test]
        public void SmoothInvertRangeDepthAndBiasShapeTheControl()
        {
            // A steady sine of 0.25 on Floor -40 dB, Ceiling 0 dB gives (20 log10 0.25 + 40) / 40 = 0.699.
            double raw = (20 * Math.Log10(0.25) + 40) / 40.0;
            float Run(Action<ParamValues> setup)
            {
                var (e, _) = NeoFollowRig.Make(v =>
                {
                    Set(v, F.Mode, 0); Set(v, F.Attack, 5); Set(v, F.Release, 50); Set(v, F.Floor, -40); Set(v, F.Ceiling, 0);
                    setup(v);
                });
                return NeoFollowRig.Run(e, Measure.Sine(1000, Rate / 2, 0.25), 32)[^1];
            }
            Assert.That(Run(v => { }), Is.EqualTo(raw).Within(0.002), "plain");
            Assert.That(Run(v => Set(v, F.Invert, 1)), Is.EqualTo(1 - raw).Within(0.002), "invert");
            Assert.That(Run(v => Set(v, F.Depth, 0.5)), Is.EqualTo(0.5 * raw).Within(0.002), "depth 50 %");
            Assert.That(Run(v => { Set(v, F.Depth, 0.5); Set(v, F.Bias, 0.2); }), Is.EqualTo(0.5 * raw + 0.2).Within(0.002), "depth and bias, unipolar");
            Assert.That(Run(v => { Set(v, F.Range, 1); Set(v, F.Depth, 0.5); Set(v, F.Bias, 0.2); }), Is.EqualTo(0.5 + 0.5 * (raw - 0.5) + 0.1).Within(0.002), "depth and bias, bipolar");
            Assert.That(Run(v => Set(v, F.Bias, 0.6)), Is.EqualTo(1f), "clamped at the top");
            Assert.That(Run(v => { Set(v, F.Invert, 1); Set(v, F.Bias, -0.5); }), Is.EqualTo(0f), "clamped at the bottom");

            // Smooth: a one-pole of the set time on the control.
            var (e, _) = NeoFollowRig.Make(v => { Set(v, F.Mode, 0); Set(v, F.Attack, 0.1); Set(v, F.Release, 1); Set(v, F.Floor, -60); Set(v, F.Ceiling, 0); Set(v, F.Smooth, 100); });
            var x = NeoFollowRig.Steps(1000, 0.0, 1.0, 1.0, 9.0, 3.0);
            var y = NeoFollowRig.Run(e, x, 32);
            int at = Rate / 32;
            double final = y[^1];
            int t63 = Array.FindIndex(y, at, v => v >= final * (1 - Math.Exp(-1)));
            double ms = (t63 + 1 - at) * 32.0 * 1000.0 / Rate;
            Say($"smooth 100 ms: the control covered 63 % of a step in {ms:F1} ms");
            Assert.That(ms, Is.EqualTo(100).Within(8).Percent);
        }

        [Test]
        public void TheOutputIsTheSameWhateverTheHostsBlockSize()
        {
            var (loop, _) = NeoFollowRig.DrumLoop(2);
            var (mel, _) = NeoFollowRig.Melody(3.0);
            foreach (var (mode, x) in new[] { (0, loop), (1, mel), (2, loop) })
            {
                var results = new List<(double Level, double Note, long Onsets, long Last)>();
                foreach (int block in new[] { 32, 64, 128, 256 })
                {
                    var (e, _) = NeoFollowRig.Make(v => { Set(v, F.Mode, mode); Set(v, F.PitchGlide, 0); });
                    int len = x.Length / 256 * 256;
                    for (int pos = 0; pos < len; pos += block) NeoFollowRig.Feed(e, x, pos, block);
                    results.Add((e.Level, e.Note, e.OnsetCount, e.LastOnsetSample));
                }
                for (int k = 1; k < results.Count; k++)
                {
                    Assert.That(results[k].Level, Is.EqualTo(results[0].Level), $"mode {mode}: the level after the same audio");
                    Assert.That(results[k].Note, Is.EqualTo(results[0].Note), $"mode {mode}: the note");
                    Assert.That(results[k].Onsets, Is.EqualTo(results[0].Onsets), $"mode {mode}: the onsets");
                    Assert.That(results[k].Last, Is.EqualTo(results[0].Last), $"mode {mode}: the last onset");
                }
            }
        }

        // ------------------------------------------------------------------ the audio thread

        [Test]
        public void ZeroBytesAreAllocatedOverTwoThousandBlocksInEveryMode()
        {
            var (loop, _) = NeoFollowRig.DrumLoop(4);
            var (mel, _) = NeoFollowRig.Melody(4.0);
            var mix = new float[Math.Min(loop.Length, mel.Length)];
            for (int i = 0; i < mix.Length; i++) mix[i] = loop[i] + mel[i];
            foreach (int mode in new[] { 0, 1, 2 })
                foreach (int detector in new[] { 0, 1 })
                {
                    if (mode != 0 && detector == 1) continue;
                    var (e, v) = NeoFollowRig.Make(p => { Set(p, F.Mode, mode); Set(p, F.Detector, detector); Set(p, F.Smooth, 30); Set(p, F.PitchGlide, 40); Set(p, F.Hold, 20); });
                    var bank = new ModBank { GlideSeconds = 0.02 };
                    var port = new NeoFollowRig.Port { Value = 300 };
                    bank.Slots[0].Used = true;
                    bank.Slots[0].Port = port;
                    bank.Slots[0].Depth = 60;
                    double dt = 64.0 / Rate;
                    float sink = 0;
                    var (bytes, why) = AllocationCheck.Run(2000, b =>
                    {
                        int pos = b * 64 % (mix.Length - 64);
                        // The parameters move with the blocks too (the host's automation).
                        if ((b & 63) == 0) { v.Set(F.Attack, 300 + (b & 255)); v.Set(F.Release, 500 + (b & 127)); }
                        NeoFollowRig.Feed(e, mix, pos, 64);
                        bank.Update(e.Output, dt);
                        sink += e.Output;
                    });
                    Assert.That(bytes, Is.EqualTo(0), $"mode {mode}, detector {detector}: {why}");
                    Assert.That(float.IsFinite(sink), Is.True);
                }
        }

        [Test]
        public void NothingGoesDenormalWhenTheSoundStopsInAnyMode()
        {
            var (loop, _) = NeoFollowRig.DrumLoop(2);
            foreach (int mode in new[] { 0, 1, 2 })
                foreach (int detector in new[] { 0, 1 })
                {
                    var (e, _) = NeoFollowRig.Make(p => { Set(p, F.Mode, mode); Set(p, F.Detector, detector); Set(p, F.Release, 5000); Set(p, F.Decay, 2000); Set(p, F.Smooth, 1000); Set(p, F.PitchGlide, 500); });
                    NeoFollowRig.Run(e, loop, 64);
                    var quiet = new float[Rate * 20];
                    for (int pos = 0; pos < quiet.Length; pos += 4096)
                    {
                        NeoFollowRig.Run(e, new float[4096], 256);
                        foreach (double s in new[] { e.Level, e.Pulse, e.Note, (double)e.Output, (double)e.Raw })
                            Assert.That(s == 0 || double.IsNormal(s), Is.True, $"mode {mode}, detector {detector}: {s} is subnormal");
                    }
                }
        }

        // ------------------------------------------------------------------ the targets

        [Test]
        public void TargetsMoveWithinRangeAndWriteGlideLikeNeoRandomsAndTheBaseComesBack()
        {
            var (loop, _) = NeoFollowRig.DrumLoop(4);
            var (e, _) = NeoFollowRig.Make(v => { Set(v, F.Mode, 0); Set(v, F.Attack, 1); Set(v, F.Release, 120); Set(v, F.Floor, -40); Set(v, F.Ceiling, -6); Set(v, F.Glide, 0.05); });
            var port = new NeoFollowRig.Port { Min = 0, Max = 1000, SafeMin = 150, SafeMax = 850, Value = 500 };
            var bank = new ModBank { GlideSeconds = e.Glide };
            var slot = bank.Slots[0];
            slot.Used = true;
            slot.Port = port;
            slot.Depth = 80;
            slot.Bipolar = false;
            slot.RangeMin = 200;
            slot.RangeMax = 800;
            double dt = 64.0 / Rate;
            double maxStep = 600.0 * dt / e.Glide + 1.0;   // the span is 800 - 200
            int last = port.Value, worstJump = 0, lo = port.Value, hi = port.Value;
            for (int pos = 0; pos + 64 <= loop.Length; pos += 64)
            {
                NeoFollowRig.Feed(e, loop, pos, 64);
                bank.Update(e.Output, dt);
                Assert.That(port.Value, Is.InRange(500, 800), "a unipolar target rises from its base and stays in its range");
                worstJump = Math.Max(worstJump, Math.Abs(port.Value - last));
                last = port.Value;
                lo = Math.Min(lo, port.Value);
                hi = Math.Max(hi, port.Value);
            }
            Say($"target on a drum loop: moved {lo} … {hi} (base 500, depth 80 % of 600 within 200 … 800), worst write {worstJump} against {maxStep:F1} allowed by a 50 ms glide");
            Assert.That(worstJump, Is.LessThanOrEqualTo(maxStep), "Write glide limits each write");
            Assert.That(hi, Is.GreaterThan(650), "it opens on the hits");
            Assert.That(port.Puts, Is.GreaterThan(100));
            bank.Release(0);
            Assert.That(port.Value, Is.EqualTo(500), "taking the target away puts its base back");

            // A negative depth ducks: the kick lowers the target from its base.
            var (e2, _) = NeoFollowRig.Make(v => { Set(v, F.Mode, 0); Set(v, F.Attack, 1); Set(v, F.Release, 300); Set(v, F.Floor, -30); Set(v, F.Ceiling, -4); Set(v, F.Glide, 0.006); });
            var duck = new NeoFollowRig.Port { Value = 800 };
            var bank2 = new ModBank { GlideSeconds = e2.Glide };
            bank2.Slots[0].Used = true; bank2.Slots[0].Port = duck; bank2.Slots[0].Depth = -60;
            int min = 800;
            for (int pos = 0; pos + 64 <= loop.Length; pos += 64)
            {
                NeoFollowRig.Feed(e2, loop, pos, 64);
                bank2.Update(e2.Output, dt);
                Assert.That(duck.Value, Is.LessThanOrEqualTo(800));
                min = Math.Min(min, duck.Value);
            }
            Assert.That(min, Is.LessThan(500), "a negative depth ducks the target on the kicks");
        }

        [Test]
        public void ATargetOnAListThatGrewKeepsItsSpan()
        {
            // The same arithmetic as Neo Random's targets (the machine shares its base class): Neo Drive's Mode went from 0 … 4 to 0 … 9, Neo Filter's Type from 0 … 6 to 0 … 7.
            Assert.That(LegacyChoiceSpans.OldMax("Neo Drive", "Mode"), Is.EqualTo(4));
            Assert.That(LegacyChoiceSpans.OldMax("Neo Filter", "Type"), Is.EqualTo(6));
            Assert.That(LegacyChoiceSpans.OldMax("Neo Filter", "Cutoff"), Is.Null);
            var p = new NeoFollowRig.Port { Min = 0, Max = 9, SafeMin = 0, SafeMax = 9, SpanMax = 4, Value = 0 };
            var bank = new ModBank { GlideSeconds = 0 };
            bank.Slots[0].Used = true; bank.Slots[0].Port = p; bank.Slots[0].Depth = 50;
            bank.Update(1.0, 64.0 / Rate);
            Assert.That(p.Value, Is.EqualTo(2), "50 % of the old span 4, not of 9");
            var q = new NeoFollowRig.Port { Min = 0, Max = 9, SafeMin = 0, SafeMax = 9, SpanMax = 4, Value = 0 };
            var wide = new ModBank { GlideSeconds = 0 };
            wide.Slots[0].Used = true; wide.Slots[0].Port = q; wide.Slots[0].Depth = 100; wide.Slots[0].RangeMin = 0; wide.Slots[0].RangeMax = 9;
            wide.Update(1.0, 64.0 / Rate);
            Assert.That(q.Value, Is.EqualTo(9), "a target that names its range reaches the new choices");
        }

        // ------------------------------------------------------------------ state and presets

        static string PrsPath => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Machines", "NeoFollow", "Neo Follow.prs.xml"));

        [Test]
        public void StateIsVersionedAtZeroAndTheEightPresetsAreValidAudibleAndDifferent()
        {
            var chain = new StateMigrations(NeoBuzz.FollowMachine.Params.Table);
            Assert.That(chain.Validate(), Is.Empty);
            Assert.That(chain.Current, Is.EqualTo(0));
            var saved = new int[NeoBuzz.FollowMachine.Params.Table.Length];
            for (int i = 0; i < saved.Length; i++) saved[i] = NeoBuzz.FollowMachine.Params.Table[i].Min + (NeoBuzz.FollowMachine.Params.Table[i].Max - NeoBuzz.FollowMachine.Params.Table[i].Min) / 3;
            Assert.That(chain.MigrateGlobals(0, saved), Is.EqualTo(saved));

            var items = XDocument.Load(PrsPath).Root!.Elements("Item").ToList();
            Assert.That(items.Count, Is.EqualTo(8), "eight factory presets");

            var (loop, _) = NeoFollowRig.DrumLoop(8);
            var (kicks, _) = NeoFollowRig.DrumLoop(8, kicksOnly: true);
            var (mel, _) = NeoFollowRig.Melody(8.0);
            var pad = PolyPad(8.0, 0.8, new[] { 48, 55, 60, 64, 67 }, 0.5, 5.0);
            var traces = new List<(string Name, float[] Trace)>();
            var weak = new List<string>();
            foreach (var item in items)
            {
                string name = (string)item.Attribute("Key")!;
                var v = new ParamValues(NeoBuzz.FollowMachine.Params.Table);
                foreach (var p in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter"))
                {
                    int index = (int)p.Attribute("Index")!, value = (int)p.Attribute("Value")!;
                    Assert.That(value, Is.InRange(NeoBuzz.FollowMachine.Params.Table[index].Min, NeoBuzz.FollowMachine.Params.Table[index].Max), $"{name}: parameter {index}");
                    v.Set(index, value);
                }
                var e = new FollowEngine(v);
                e.SetSampleRate(Rate);
                int mode = v.Choice(F.Mode);
                // What each preset is made for: drums for the level and the hits (the kick on the sidechain, a pad on the input, for the sidechain one), a lead line for the pitch.
                float[] input = mode == 1 ? mel : v.Choice(F.Listen) == 1 ? pad : loop;
                if (name.Contains("Slow Swell"))
                {
                    // A section that builds: the loop swelling over the whole length.
                    input = (float[])input.Clone();
                    for (int k = 0; k < input.Length; k++) input[k] *= (float)(0.03 + 0.97 * k / input.Length);
                }
                float[] key = mode == 1 ? mel : kicks;
                int len = Math.Min(Math.Min(input.Length, key.Length), 8 * Rate) / 64 * 64;
                var tr = new float[len / 64];
                var a = new float[64];
                var b = new float[64];
                for (int k = 0; k < tr.Length; k++)
                {
                    Array.Copy(input, k * 64, a, 0, 64);
                    Array.Copy(key, k * 64, b, 0, 64);
                    e.Process(a, a, b, b, 64);
                    Assert.That(e.Output, Is.InRange(0f, 1f));
                    tr[k] = e.Output;
                }
                float lo = tr.Skip(tr.Length / 8).Min(), hi = tr.Max();
                Say($"preset {name}: control {lo:F2} … {hi:F2}, mean {tr.Average():F2}");
                if (hi - lo <= 0.5f) weak.Add($"{name} ({hi - lo:F2})");
                traces.Add((name, tr));
            }
            Assert.That(weak, Is.Empty, "every preset moves its control clearly (range above 0.5)");
            for (int i = 0; i < traces.Count; i++)
                for (int j = i + 1; j < traces.Count; j++)
                {
                    int n = Math.Min(traces[i].Trace.Length, traces[j].Trace.Length);
                    double d = 0;
                    for (int k = 0; k < n; k++) d += Math.Abs(traces[i].Trace[k] - traces[j].Trace[k]);
                    d /= n;
                    Assert.That(d, Is.GreaterThan(0.04), $"{traces[i].Name} and {traces[j].Name} are clearly different (mean difference {d:F3})");
                }
        }
    }
}
