using System.Diagnostics;
using System.Numerics;
using NeoBuzz.Delay;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Delay (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class DelayTests
    {
        const int Rate = Measure.Rate;

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        /// <summary>Filters open, no wow, no drive, no ducking, wet only: the plain delay the setup then changes.</summary>
        static void Plain(ParamValues v)
        {
            v.Set(P.LowCut, Params.Table[P.LowCut].Min);
            v.Set(P.HighCut, Params.Table[P.HighCut].Max);
            v.Set(P.Mix, 1000);
        }

        static (DelayEngine Engine, ParamValues Values) Make(Action<ParamValues> setup, int rate = Rate, double tempo = 120)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new DelayEngine(v) { Tempo = tempo };
            e.SetSampleRate(rate);
            return (e, v);
        }

        static (float[] L, float[] R) Run(DelayEngine e, float[] l, float[] r, int block = DelayEngine.MaxBlock, Action<int> before = null)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[DelayEngine.MaxBlock];
            var br = new float[DelayEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += block)
            {
                int n = Math.Min(block, l.Length - pos);
                before?.Invoke(pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        static float[] Noise(int length, double rms, uint seed) => Measure.PinkNoise(length, rms, seed);

        static double Db(double x) => Measure.Db(x);

        static float[] Impulse(int length, float a, int at = 0)
        {
            var x = new float[length];
            x[at] = a;
            return x;
        }

        /// <summary>The magnitude of a stretch of a signal's DTFT at <paramref name="hz"/>, its time origin at <paramref name="from"/>.</summary>
        static Complex Dtft(float[] h, int from, int length, double hz)
        {
            double w = 2 * Math.PI * hz / Rate, re = 0, im = 0;
            for (int k = 0; k < length && from + k < h.Length; k++)
            {
                re += h[from + k] * Math.Cos(w * k);
                im -= h[from + k] * Math.Sin(w * k);
            }
            return new Complex(re, im);
        }

        /// <summary>Indices and values of the samples above <paramref name="floor"/> (an impulse's echoes, filters open).</summary>
        static List<(int At, float Value)> Spikes(float[] y, float floor)
        {
            var list = new List<(int, float)>();
            for (int i = 0; i < y.Length; i++) if (Math.Abs(y[i]) > floor) list.Add((i, y[i]));
            return list;
        }

        // ------------------------------------------------------------------ Mix 0 %

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void MixZeroPassesTheInputBitForBit(int mode)
        {
            // Full feedback, the deepest wow at its fastest, drive, ducking and a short time moving: at Mix 0 %
            // (Output 0 dB) the input comes out bit for bit, negative zeros and the smallest numbers included.
            var (e, v) = Make(pv =>
            {
                pv.Set(P.Mode, mode);
                pv.Set(P.Mix, 0);
                pv.Set(P.Feedback, Params.Table[P.Feedback].Max);
                pv.Set(P.ModDepth, 1000);
                pv.Set(P.ModRate, 1000);
                pv.Set(P.Drive, 240);
                pv.Set(P.Duck, 400);
                pv.Set(P.Sync, 0);
                Set(pv, P.Time, 0.01);
            });
            var x = Noise(Rate, 0.3, 7);
            x[10] = -0f;
            x[11] = float.Epsilon;
            x[12] = -float.Epsilon;
            var y = (float[])x.Clone();
            var (l, r) = Run(e, x, y, block: 117, before: pos => { if (pos > Rate / 2) Set(v, P.Time, 0.2); });
            for (int i = 0; i < x.Length; i++)
            {
                Assert.That(BitConverter.SingleToInt32Bits(l[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(x[i])), $"left {i}");
                Assert.That(BitConverter.SingleToInt32Bits(r[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(x[i])), $"right {i}");
            }
            Assert.That(e.Latency, Is.EqualTo(0));
        }

        [Test]
        public void TheOutputDoesNotDependOnHowTheHostSplitsIt()
        {
            // The wow, the ducking gain and the coefficients move in segments that run across blocks, the delays per
            // sample: blocks of 256 and of 117 give the same samples, with the time gliding and crossfading meanwhile.
            foreach (int mode in new[] { 0, 1, 2 })
            {
                Action<ParamValues> setup = v =>
                {
                    v.Set(P.Mode, mode); v.Set(P.Feedback, 1700); v.Set(P.ModDepth, 600); v.Set(P.ModRate, 800);
                    v.Set(P.Duck, 200); v.Set(P.Drive, 120); v.Set(P.Sync, 0); Set(v, P.Time, 0.05); v.Set(P.Offset, 700);
                };
                var x = Noise(Rate, 0.2, 3);
                var y = Noise(Rate, 0.2, 4);
                ParamValues va = null, vb = null;
                var (ea, a) = Make(setup);
                va = a;
                var (eb, b) = Make(setup);
                vb = b;
                // The time changes at sample 11 776 (a multiple of both block sizes: 256 · 46 and 117 · ... is not, so at the
                // first block start on or after it, which is the same sample in both runs).
                var (la, ra) = Run(ea, x, y, block: 256, before: pos => { if (pos == 11776) { Set(va, P.Time, 0.06); } if (pos == 23296) Set(va, P.Time, 0.3); });
                var (lb, rb) = Run(eb, x, y, block: 128, before: pos => { if (pos == 11776) { Set(vb, P.Time, 0.06); } if (pos == 23296) Set(vb, P.Time, 0.3); });
                Assert.That(lb, Is.EqualTo(la), $"mode {mode}, left");
                Assert.That(rb, Is.EqualTo(ra), $"mode {mode}, right");
                var (ec, c) = Make(setup);
                var (lc, rc) = Run(ec, x, y, block: 117);
                var (ed, d) = Make(setup);
                var (ld, rd) = Run(ed, x, y, block: 256);
                Assert.That(lc, Is.EqualTo(ld), $"mode {mode}, steady, blocks of 117 and 256");
                Assert.That(rc, Is.EqualTo(rd));
            }
        }

        // ------------------------------------------------------------------ time

        [TestCase(0.001)]
        [TestCase(0.0137)]
        [TestCase(0.375)]
        [TestCase(1.234)]
        [TestCase(5.0)]
        public void FreeTimeIsExactAndEachEchoIsFeedbackTimesTheLast(double seconds)
        {
            // An impulse: the echoes come exactly round(T · fs) samples apart (the time's raw value read back), each
            // Feedback times the one before, with the filters open.
            var (e, v) = Make(pv => { Plain(pv); pv.Set(P.Sync, 0); Set(pv, P.Time, seconds); Set(pv, P.Feedback, 60); });
            double t = v.Value(P.Time);
            int d = (int)Math.Round(t * Rate);
            int length = Math.Min(4 * d + 10, 22 * Rate);
            var (l, r) = Run(e, Impulse(length, 0.5f), Impulse(length, 0.5f));
            var spikes = Spikes(l, 1e-6f);
            Assert.That(spikes.Count, Is.GreaterThanOrEqualTo(Math.Min(4, (length - 1) / d)));
            double fb = v.Value(P.Feedback) / 100;
            for (int k = 0; k < spikes.Count; k++)
            {
                Assert.That(spikes[k].At, Is.EqualTo((k + 1) * d), $"echo {k + 1} at {(k + 1) * d} samples");
                Assert.That(spikes[k].Value, Is.EqualTo(0.5 * Math.Pow(fb, k)).Within(1e-6), $"echo {k + 1}'s level");
            }
            Assert.That(r, Is.EqualTo(l), "a centred impulse: both sides alike (Stereo)");
            Measure.Report($"Neo Delay free {t * 1000:F3} ms: echoes every {d} samples (round(T · fs) = {t * Rate:F2}), each ×{fb:F2}");
        }

        [Test]
        public void SyncFollowsTheSongsTempo()
        {
            // Each note value, straight, dotted and triplet, at 90, 120, 128, 140 and 174 BPM: the line settles on
            // 60 / BPM × beats × fs, rounded to a sample, and an impulse's echo arrives exactly then.
            var lines = new List<string>();
            double worst = 0;
            foreach (double bpm in new[] { 90.0, 120, 128, 140, 174 })
                for (int sync = 1; sync < DelayDesign.SyncBeats.Length; sync++)
                    for (int feel = 0; feel < 3; feel++)
                    {
                        var (e, v) = Make(pv => { pv.Set(P.Sync, sync); pv.Set(P.Feel, feel); }, tempo: bpm);
                        double exact = 60.0 / bpm * DelayDesign.Beats(sync, feel) * Rate;
                        double want = Math.Min(Math.Round(exact), DelayDesign.MaxSeconds * Rate);
                        Assert.That(e.Delay(0), Is.EqualTo(want), $"{DelayDesign.NoteName(sync, feel)} at {bpm} BPM");
                        if (exact < DelayDesign.MaxSeconds * Rate) worst = Math.Max(worst, Math.Abs(e.Delay(0) - exact));
                    }
            // An impulse at 128 BPM, 1/8 dotted: 0.3515625 s = 16 875 samples.
            var (ei, _) = Make(pv => { Plain(pv); pv.Set(P.Sync, 4); pv.Set(P.Feel, 1); }, tempo: 128);
            var (li, _) = Run(ei, Impulse(20000, 0.5f), Impulse(20000, 0.5f));
            Assert.That(Spikes(li, 1e-6f).First().At, Is.EqualTo(16875));
            Measure.Report($"Neo Delay sync: 8 note values × 3 feels × 5 tempos, each within {worst:F2} sample of 60 / BPM × beats (rounded to a sample); 1/8 dotted at 128 BPM = 16 875 samples");
        }

        [Test]
        public void TempoChangesGlideAndNoteValueChangesCrossfade()
        {
            // 120 → 126 BPM at 1/8 dotted (375 → 357 ms: 18 ms, under the 50 ms jump) glides and lands exactly on the new
            // delay; 1/8 → 1/2 (250 → 1000 ms) crossfades; both settle exactly.
            var (e, v) = Make(pv => { Plain(pv); pv.Set(P.Sync, 4); pv.Set(P.Feel, 1); });
            var x = new float[Rate / 4];
            Run(e, x, x);
            e.Tempo = 126;
            bool faded = false;
            double maxBend = 0;
            Run(e, new float[3 * Rate], new float[3 * Rate], block: 64, before: _ => { faded |= e.Motion(0).Fading; maxBend = Math.Max(maxBend, Math.Abs(e.Motion(0).Velocity)); });
            Assert.That(faded, Is.False, "a small change glides");
            Assert.That(e.Delay(0), Is.EqualTo(Math.Round(60.0 / 126 * 0.75 * Rate)), "and lands exactly");
            Assert.That(maxBend, Is.LessThanOrEqualTo(DelayEngine.MaxBend + 1e-9).And.GreaterThan(0.01));
            v.Set(P.Feel, 0);
            v.Set(P.Sync, 6);
            Run(e, new float[256], new float[256]);
            Assert.That(e.Motion(0).Fading, Is.True, "a large change crossfades");
            Run(e, new float[Rate / 10], new float[Rate / 10]);
            Assert.That(e.Motion(0).Fading, Is.False);
            Assert.That(e.Delay(0), Is.EqualTo(Math.Round(60.0 / 126 * 2 * Rate)));
            Measure.Report($"Neo Delay: 120 → 126 BPM glides (the bend at most {maxBend * 100:F1} % of speed), 1/8 → 1/2 crossfades over {DelayEngine.FadeSeconds * 1000:F0} ms; both land exactly");
        }

        /// <summary>Instantaneous frequency from the rising zero crossings of a sine, one value per period.</summary>
        static List<double> Frequencies(float[] y, int from, int to)
        {
            var list = new List<double>();
            double last = double.NaN;
            for (int i = from + 1; i < to; i++)
            {
                if (y[i - 1] < 0 && y[i] >= 0)
                {
                    double t = i - 1 + y[i - 1] / (double)(y[i - 1] - y[i]);
                    if (!double.IsNaN(last)) list.Add(Rate / (t - last));
                    last = t;
                }
            }
            return list;
        }

        static double MaxStep(float[] y, int from, int to = int.MaxValue)
        {
            double m = 0;
            for (int i = Math.Max(1, from); i < Math.Min(y.Length, to); i++) m = Math.Max(m, Math.Abs(y[i] - y[i - 1]));
            return m;
        }

        [Test]
        public void TimeChangesNeverClickAndBendThePitchTastefully()
        {
            // A 440 Hz sine, wet only. The time stepped by 30 ms (a glide) bends the pitch by at most 20 % (about 4 semitones down, 3 up),
            // smoothly; stepped by 400 ms (a crossfade) the pitch never moves. Neither makes a step larger than the sine's own.
            const double f = 440;
            var x = Measure.Sine(f, 4 * Rate, 0.5);
            double steady = MaxStep(x, 0);
            foreach (var (to, glide) in new[] { (0.23, true), (0.6, false), (0.17, true), (0.05, false) })
            {
                var (e, v) = Make(pv => { Plain(pv); pv.Set(P.Sync, 0); Set(pv, P.Time, 0.2); pv.Set(P.Feedback, 990); });
                bool moved = false;
                var (y, _) = Run(e, x, x, before: pos => { if (pos >= Rate && !moved) { Set(v, P.Time, to); moved = true; } });
                var freq = Frequencies(y, Rate, 3 * Rate);
                double lo = freq.Min(), hi = freq.Max();
                double step = MaxStep(y, Rate / 2, 3 * Rate);
                Measure.Report($"Neo Delay 200 → {to * 1000:F0} ms ({(glide ? "glide" : "crossfade")}): pitch {Measure.Cents(lo, f):+0;−0} … {Measure.Cents(hi, f):+0;−0} cents, largest step {step / steady:F2}× the sine's");
                Assert.That(step, Is.LessThan(1.25 * steady), "no click");
                if (glide)
                {
                    Assert.That(lo, Is.GreaterThan(f * (1 - DelayEngine.MaxBend) * 0.999), "bent at most 20 % down");
                    Assert.That(hi, Is.LessThan(f * (1 + DelayEngine.MaxBend) * 1.001), "or up");
                    Assert.That(Math.Max(Math.Abs(Measure.Cents(lo, f)), Math.Abs(Measure.Cents(hi, f))), Is.GreaterThan(20), "audibly");
                }
                else
                    Assert.That(Math.Max(Math.Abs(Measure.Cents(lo, f)), Math.Abs(Measure.Cents(hi, f))), Is.LessThan(30), "a crossfade doesn't bend");
            }
        }

        // ------------------------------------------------------------------ modes

        [Test]
        public void StereoPingPongAndCrossRouteTheEchoesAsDesigned()
        {
            // Impulses, filters open, feedback 50 %, left 100 ms, right 150 ms (Offset +50 %). Each mode's echoes, both
            // channels, against DelayDesign.Echoes (what the editor draws), for a centred impulse and for one on the left.
            var lines = new List<string>();
            foreach (int mode in new[] { 0, 1, 2 })
                foreach (bool centred in new[] { true, false })
                {
                    var (e, v) = Make(pv => { Plain(pv); pv.Set(P.Mode, mode); pv.Set(P.Sync, 0); Set(pv, P.Time, 0.1); Set(pv, P.Offset, 50); Set(pv, P.Feedback, 50); });
                    int length = Rate;
                    var (l, r) = Run(e, Impulse(length, 0.5f), centred ? Impulse(length, 0.5f) : new float[length]);
                    var s = DelayDesign.From(v, 120, Rate);
                    var got = Spikes(l, 1e-6f).Select(p => (p.At, 0, (double)p.Value)).Concat(Spikes(r, 1e-6f).Select(p => (p.At, 1, (double)p.Value))).OrderBy(p => p.At).ThenBy(p => p.Item2).ToList();
                    // The expected echoes of this impulse (the design's are for a centred one; Stereo and Cross take the left alone too).
                    var expected = Expected(s, centred, length);
                    Assert.That(got.Count, Is.EqualTo(expected.Count), $"{(DelayMode)mode}, {(centred ? "centred" : "left")}: {string.Join(" ", got.Select(g => $"{g.At}{(g.Item2 == 0 ? "L" : "R")}"))}");
                    for (int k = 0; k < got.Count; k++)
                    {
                        Assert.That(got[k].At, Is.EqualTo(expected[k].At), $"{(DelayMode)mode} echo {k}");
                        Assert.That(got[k].Item2, Is.EqualTo(expected[k].Channel), $"{(DelayMode)mode} echo {k}'s side");
                        Assert.That(got[k].Item3, Is.EqualTo(expected[k].Gain).Within(1e-6), $"{(DelayMode)mode} echo {k}'s level");
                    }
                    lines.Add($"{(DelayMode)mode} {(centred ? "centred" : "left")}: {string.Join(" ", got.Take(6).Select(g => $"{g.At * 1000.0 / Rate:F0}{(g.Item2 == 0 ? "L" : "R")}"))}");
                }
            Measure.Report("Neo Delay routing (100 / 150 ms, feedback 50 %): " + string.Join("; ", lines));
        }

        /// <summary>The echoes of a 0.5 impulse into the left (and the right, when centred), by the routing rules.</summary>
        static List<(int At, int Channel, double Gain)> Expected(in DelaySetting s, bool centred, int length)
        {
            int dl = (int)Math.Round(s.SecondsL * Rate), dr = (int)Math.Round(s.SecondsR * Rate);
            var pending = new List<(int T, int Line, double G)>();
            if (s.Mode == DelayMode.PingPong) pending.Add((0, 0, centred ? 0.5 : 0.25));
            else
            {
                pending.Add((0, 0, 0.5));
                if (centred) pending.Add((0, 1, 0.5));
            }
            var result = new List<(int, int, double)>();
            while (pending.Count > 0)
            {
                var p = pending.OrderBy(q => q.T).ThenBy(q => q.Line).First();
                pending.Remove(p);
                int at = p.T + (p.Line == 0 ? dl : dr);
                if (at >= length || Math.Abs(p.G) < 1e-6) continue;
                result.Add((at, p.Line, p.G));
                int next = s.Mode == DelayMode.Stereo ? p.Line : 1 - p.Line;
                pending.Add((at, next, p.G * s.Feedback));
            }
            // Coinciding echoes on one side add.
            return result.GroupBy(x => (x.Item1, x.Item2)).Select(g => (g.Key.Item1, g.Key.Item2, g.Sum(x => x.Item3))).OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList();
        }

        [Test]
        public void TheEditorsEchoesAreTheEngines()
        {
            // DelayDesign.Echoes (the editor's picture) for a centred impulse equals the engine's echoes in each mode.
            foreach (int mode in new[] { 0, 1, 2 })
            {
                var (e, v) = Make(pv => { Plain(pv); pv.Set(P.Mode, mode); pv.Set(P.Sync, 0); Set(pv, P.Time, 0.1); Set(pv, P.Offset, 30); Set(pv, P.Feedback, -60); });
                var s = DelayDesign.From(v, 120, Rate);
                var echoes = DelayDesign.Echoes(s, Rate, 0.9, 10000, 2e-6);
                var expected = Expected(s, true, (int)(0.9 * Rate) + 1);
                var merged = echoes.GroupBy(x => ((int)Math.Round(x.Seconds * Rate), x.Channel)).Select(g => (g.Key.Item1, g.Key.Channel, g.Sum(x => x.Gain) * 0.5)).OrderBy(x => x.Item1).ThenBy(x => x.Channel).ToList();
                Assert.That(merged.Select(m => (m.Item1, m.Channel)), Is.EqualTo(expected.Select(x => (x.At, x.Channel))), ((DelayMode)mode).ToString());
                for (int k = 0; k < merged.Count; k++) Assert.That(merged[k].Item3, Is.EqualTo(expected[k].Gain).Within(1e-9));
            }
        }

        // ------------------------------------------------------------------ the loop's filters

        [Test]
        public void TheEchoesResponseIsTheDesign()
        {
            // The first echo of an impulse (its DTFT over 160 frequencies, 20 Hz – 20 kHz) against DelayDesign.Pass, and the
            // third against Pass³ (feedback 100 % of the way: each pass through the filters once more): within 0.02 dB down to
            // −60 dB. The low cut and the high cut −3 dB at their settings (±0.01 dB), 12 dB/oct far from them.
            var cases = new (string Name, double Low, double High)[] { ("low cut 300 Hz, high cut 3 kHz", 300, 3000), ("low cut 80 Hz", 80, 0), ("high cut 8 kHz", 0, 8000), ("low cut 1 kHz, high cut 12 kHz", 1000, 12000) };
            double worst = 0;
            foreach (var (name, low, high) in cases)
            {
                var (e, v) = Make(pv =>
                {
                    Plain(pv); pv.Set(P.Sync, 0); Set(pv, P.Time, 0.5); Set(pv, P.Feedback, 99);
                    if (low > 0) Set(pv, P.LowCut, low);
                    if (high > 0) Set(pv, P.HighCut, high);
                });
                var s = DelayDesign.From(v, 120, Rate);
                int d = (int)Math.Round(s.SecondsL * Rate);
                var (h, _) = Run(e, Impulse(3 * d + d, 1e-3f), Impulse(3 * d + d, 1e-3f));
                double fb = s.Feedback;
                double g = DelayDesign.LowCutG(s.LowCutHz, Rate);
                double w1 = 0, w3 = 0;
                for (int k = 0; k < 160; k++)
                {
                    double hz = 20 * Math.Pow(1000, k / 159.0);
                    double w = 2 * Math.PI * hz / Rate;
                    var pass = DelayDesign.Pass(s.HighCutA, s.LowCutOn, g, w);
                    double d1 = Db(pass.Magnitude), d3 = Db(Math.Pow(pass.Magnitude, 3) * fb * fb);
                    double m1 = Db(Dtft(h, d, d, hz).Magnitude / 1e-3), m3 = Db(Dtft(h, 3 * d, d, hz).Magnitude / 1e-3);
                    if (d1 > -60) w1 = Math.Max(w1, Math.Abs(m1 - d1));
                    if (d3 > -60) w3 = Math.Max(w3, Math.Abs(m3 - d3));
                }
                // The corners.
                if (s.LowCutOn) Assert.That(DelayDesign.PassDb(s with { HighCutA = 0 }, Rate, s.LowCutHz), Is.EqualTo(-3.0103).Within(0.01), $"{name}: low cut −3 dB");
                if (s.HighCutA > 0) Assert.That(DelayDesign.PassDb(s with { LowCutOn = false }, Rate, s.HighCutHz), Is.EqualTo(-3.0103).Within(0.01), $"{name}: high cut −3 dB");
                Measure.Report($"Neo Delay echo response = design ({name}): first echo within {w1:F4} dB, third within {w3:F4} dB");
                worst = Math.Max(worst, Math.Max(w1, w3));
            }
            Assert.That(worst, Is.LessThan(0.02));
            // Slopes: the low cut two to three octaves below its corner, the high cut two to three octaves above (12 dB/oct; the
            // high cut's one-poles flatten towards Nyquist, each at (1 − a)/(1 + a) there).
            var flat = DelayDesign.From(new ParamValues(Params.Table), 120, Rate);
            var lowOnly = flat with { HighCutA = 0, LowCutOn = true, LowCutHz = 400 };
            var highOnly = flat with { LowCutOn = false, HighCutA = DelayDesign.HighCutCoefficient(500, Rate), HighCutHz = 500 };
            double lowSlope = DelayDesign.PassDb(lowOnly, Rate, 100) - DelayDesign.PassDb(lowOnly, Rate, 50);
            double highSlope = DelayDesign.PassDb(highOnly, Rate, 4000) - DelayDesign.PassDb(highOnly, Rate, 8000);
            Measure.Report($"Neo Delay filter slopes: low cut {lowSlope:F2} dB/oct (2–3 octaves under 400 Hz), high cut {highSlope:F2} dB/oct (3–4 octaves over 500 Hz: two real poles reach their asymptote slowly)");
            Assert.That(lowSlope, Is.EqualTo(12).Within(0.3));
            Assert.That(highSlope, Is.EqualTo(12).Within(1));
        }

        // ------------------------------------------------------------------ the bound, proven and measured

        [Test]
        public void EachFactorOfTheLoopIsAtMostOne()
        {
            // The factors: the read (linear: its two weights ≥ 0, summing to 1; at a fixed fraction its gain ≤ 1 at every
            // frequency), the high cut's one-poles (a in [0, 1): taps ≥ 0 summing to 1) and their gain, the low cut's
            // gain at every frequency and corner (≤ 1, though its taps aren't convex: the reason the ℓ∞ argument needs it
            // off), and the saturator (slope ≤ 1, |S(u)| ≤ |u|, never past Ceiling × t, NaN to 0) at every Drive.
            double hc = 0, hcA = 0, lc = 0, lcTaps = 0;
            foreach (double hz in new[] { 200.0, 500, 2000, 8000, 12000, 19000, 0.45 * Rate })
            {
                double a = DelayDesign.HighCutCoefficient(hz, Rate);
                hcA = Math.Max(hcA, a);
                Assert.That(a, Is.InRange(0.0, 1.0 - 1e-9));
                double g = DelayDesign.LowCutG(Math.Min(hz, 5000), Rate);
                for (int j = 0; j <= 1024; j++)
                {
                    double w = Math.PI * j / 1024;
                    var z1 = new Complex(Math.Cos(w), -Math.Sin(w));
                    hc = Math.Max(hc, DelayDesign.HighCut(a, z1).Magnitude);
                    if (j > 0) lc = Math.Max(lc, DelayDesign.LowCut(g, w).Magnitude);
                }
            }
            // The low cut's taps (its impulse response) sum to more than 1 in magnitude: measured, for the record.
            {
                var (e, _) = Make(pv => { Plain(pv); pv.Set(P.Sync, 0); Set(pv, P.Time, 0.05); Set(pv, P.LowCut, 200); pv.Set(P.Feedback, 990); });
                var (h, _) = Run(e, Impulse(Rate / 4, 1e-3f), Impulse(Rate / 4, 1e-3f));
                int d = (int)Math.Round(0.05 * Rate);
                for (int k = d; k < h.Length; k++) lcTaps += Math.Abs(h[k]) / 1e-3;
            }
            double slope = 0, ratio = 0, top = 0;
            foreach (double drive in new[] { 0.0, 6, 12, 24 })
            {
                float t = (float)DspMath.DbToGain(-drive);
                float prev = DelayDesign.Saturate(-50f, t);
                for (int k = -50000 + 1; k <= 50000; k++)
                {
                    float u = k * 0.001f;
                    float s = DelayDesign.Saturate(u, t);
                    slope = Math.Max(slope, (s - prev) / 0.001);
                    if (u != 0) ratio = Math.Max(ratio, s / u);
                    top = Math.Max(top, Math.Abs(s) / t);
                    prev = s;
                }
                Assert.That(DelayDesign.Saturate(float.NaN, t), Is.EqualTo(0f));
                Assert.That(DelayDesign.Saturate(float.PositiveInfinity, t), Is.EqualTo(DelayDesign.Ceiling * t));
                Assert.That(DelayDesign.Saturate(float.NegativeInfinity, t), Is.EqualTo(-DelayDesign.Ceiling * t));
                top = Math.Max(top, Math.Abs(DelayDesign.Saturate(1e30f, t)) / t);
            }
            Assert.That(DspMath.DbToGain(-0.0), Is.EqualTo(1.0), "Drive 0 dB: the knee at full scale exactly");
            Measure.Report($"Neo Delay loop factors: high cut ≤ {hc:F12} (a ≤ {hcA:F4}, taps convex), low cut ≤ {lc:F12} at every frequency (its taps sum to {lcTaps:F2} in magnitude: not convex), saturator slope ≤ {slope:F4}, |S(u)/u| ≤ {ratio:F6}, |S| ≤ {top:F4} × the threshold");
            Assert.That(hc, Is.LessThanOrEqualTo(1 + 1e-12));
            Assert.That(lc, Is.LessThanOrEqualTo(1 + 1e-9));
            Assert.That(slope, Is.LessThanOrEqualTo(1.0001));
            Assert.That(ratio, Is.LessThanOrEqualTo(1));
            Assert.That(top, Is.LessThanOrEqualTo(DelayDesign.Ceiling));
        }

        /// <summary>A pink noise above 30 Hz at −20 dBFS RMS, <paramref name="seconds"/> long, then <paramref name="tail"/> of silence; another for the right.</summary>
        static (float[] L, float[] R) NoiseThenSilence(int seconds, int tail, uint seed = 5)
        {
            int noise = seconds * Rate;
            var x = new float[noise + tail * Rate];
            var pink = Noise(noise, 0.1, seed);
            var hp1 = new Biquad();
            var hp2 = new Biquad();
            hp1.Set(BiquadType.HighPass, 30, Rate, 0.7071);
            hp2.Set(BiquadType.HighPass, 30, Rate, 0.7071);
            for (int i = 0; i < noise; i++) x[i] = hp2.Process(hp1.Process(pink[i]));
            float scale = (float)(0.1 / Measure.Rms(x, 0, noise));
            for (int i = 0; i < noise; i++) x[i] *= scale;
            var y = (float[])x.Clone();
            for (int i = 0; i < noise; i++) y[i] = x[(i + 7777) % noise];
            return (x, y);
        }

        static IEnumerable<TestCaseData> Extremes()
        {
            // Each parameter alone at each end (Feedback at its most unless it is the parameter), then in each mode ±99 %
            // with the low cut off and at its top, the high cut open, the deepest and fastest wow, the shortest and the
            // longest time, the most drive; every parameter at each end at once.
            int fbMax = Params.Table[P.Feedback].Max, fbMin = Params.Table[P.Feedback].Min;
            foreach (var d in Params.Table)
                foreach (int raw in new[] { d.Min, d.Max })
                {
                    var changes = new List<(int, int)> { (d.Index, raw) };
                    if (d.Index != P.Feedback) changes.Add((P.Feedback, fbMax));
                    yield return new TestCaseData(d.Name + "=" + raw, changes.ToArray()).SetName($"Extremes({d.Name} {(raw == d.Min ? "min" : "max")})");
                }
            foreach (int mode in new[] { 0, 1, 2 })
                foreach (int fb in new[] { fbMin, fbMax })
                    foreach (int low in new[] { 0, 1000 })
                        foreach (double time in new[] { 0.001, 0.25 })
                        {
                            string name = $"{(DelayMode)mode} {(fb == fbMax ? "+99 %" : "−99 %")}, low cut {(low > 0 ? "5 kHz" : "off")}, {time * 1000} ms, wow 5 ms at 5 Hz";
                            yield return new TestCaseData(name, new[]
                            {
                                (P.Mode, mode), (P.Feedback, fb), (P.LowCut, low), (P.HighCut, 1000), (P.Sync, 0), (P.Time, Params.Table[P.Time].Raw(time)),
                                (P.ModDepth, 1000), (P.ModRate, 1000), (P.Offset, 1500),
                            }).SetName($"Extremes({name})");
                        }
            yield return new TestCaseData("every parameter at its maximum", Params.Table.Select(d => (d.Index, d.Max)).ToArray()).SetName("Extremes(every parameter at its maximum)");
            yield return new TestCaseData("every parameter at its minimum", Params.Table.Select(d => (d.Index, d.Min)).ToArray()).SetName("Extremes(every parameter at its minimum)");
        }

        [TestCaseSource(nameof(Extremes))]
        public void ExtremesStayBoundedAndDecay(string what, (int Index, int Raw)[] changes)
        {
            // 20 s of pink noise (above 30 Hz) at −20 dBFS RMS, then 10 s of silence: finite; nothing in the lines past the
            // saturator's ceiling (+6 dB over the threshold) at any time; once the input stops, decaying at least half as
            // fast as |Feedback| per time round the loop.
            var (e, v) = Make(pv => { pv.Set(P.Mix, 1000); foreach (var (i, raw) in changes) pv.Set(i, raw); });
            var (x, y) = NoiseThenSilence(20, 10);
            float bound = DelayDesign.Ceiling * (float)DspMath.DbToGain(-v.Value(P.Drive));
            float worstLine = 0;
            var (l, r) = Run(e, x, y, before: pos => { if (pos % (Rate / 4) == 0) worstLine = Math.Max(worstLine, e.LinePeak()); });
            worstLine = Math.Max(worstLine, e.LinePeak());
            double peak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
            double Level(int from, int seconds) => Db(Math.Max(Measure.Rms(l, from, seconds * Rate), Measure.Rms(r, from, seconds * Rate)));
            int noise = 20 * Rate;
            double first = Level(noise + Rate, 1), last = Level(noise + 8 * Rate, 1);
            var s = DelayDesign.From(v, 120, Rate);
            double fb = Math.Abs(s.Feedback);
            double loop = Math.Max(s.SecondsL, s.SecondsR) + s.ModSeconds;
            if (s.Mode != DelayMode.Stereo) loop = s.SecondsL + s.SecondsR + 2 * s.ModSeconds;
            double needDrop = fb < 0.01 ? 60 : Math.Min(60, 0.5 * 7 / loop * 20 * Math.Log10(1 / fb));
            Measure.Report($"Neo Delay {what}: peak {Db(peak):+0.0;−0.0} dBFS, the lines at most {Db(worstLine):+0.0;−0.0} dBFS (bound {Db(bound):+0.0;−0.0}), tail {Math.Max(-200, last - first):+0.0;−0.0} dB over 7 s (at least −{needDrop:F1})");
            Assert.That(double.IsFinite(peak), Is.True, "finite");
            Assert.That(worstLine, Is.LessThanOrEqualTo(bound), "the lines never pass the saturator's ceiling");
            Assert.That(Db(peak) - v.Value(P.Output), Is.LessThan(18), "far under the guard's +60 dBFS (before Output's gain)");
            if (first > -120) Assert.That(last - first, Is.LessThan(-needDrop), "decays after the input stops");
        }

        [Test]
        public void FastAutomationStaysBoundedAndDecays()
        {
            // The worst moves: the time jumping every block between its ends and in between (glides and crossfades without
            // end), Sync and Feel switching, the wow at its deepest and fastest, ±99 % feedback flipping, the low cut at its
            // top and off, every mode; 20 s of noise, then the automation stops and 10 s of silence: bounded throughout,
            // decaying after.
            var rng = new Random(3);
            var lines = new List<string>();
            foreach (int mode in new[] { 0, 1, 2 })
                foreach (bool lowCut in new[] { false, true })
                {
                    var (e, v) = Make(pv =>
                    {
                        pv.Set(P.Mix, 1000); pv.Set(P.Mode, mode); pv.Set(P.ModDepth, 1000); pv.Set(P.ModRate, 1000);
                        pv.Set(P.Feedback, Params.Table[P.Feedback].Max); pv.Set(P.LowCut, lowCut ? 1000 : 0); pv.Set(P.HighCut, 1000);
                    });
                    var (x, y) = NoiseThenSilence(20, 10, (uint)(11 + mode));
                    float worstLine = 0;
                    int noise = 20 * Rate;
                    var (l, r) = Run(e, x, y, block: 64, before: pos =>
                    {
                        if (pos % 2048 == 0) worstLine = Math.Max(worstLine, e.LinePeak());
                        if (pos >= noise) return;
                        switch (rng.Next(6))
                        {
                            case 0: v.Set(P.Sync, 0); v.Set(P.Time, rng.Next(2) == 0 ? 0 : Params.Table[P.Time].Max); break;
                            case 1: v.Set(P.Sync, 0); v.Set(P.Time, rng.Next(Params.Table[P.Time].Max + 1)); break;
                            case 2: v.Set(P.Sync, rng.Next(9)); v.Set(P.Feel, rng.Next(3)); break;
                            case 3: v.Set(P.Feedback, rng.Next(2) == 0 ? Params.Table[P.Feedback].Min : Params.Table[P.Feedback].Max); break;
                            case 4: v.Set(P.Offset, rng.Next(1501)); break;
                            default: v.Set(P.Time, Math.Clamp(v[P.Time] + rng.Next(-300, 301), 0, Params.Table[P.Time].Max)); break;
                        }
                    });
                    worstLine = Math.Max(worstLine, e.LinePeak());
                    double peak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
                    double Level(int from) => Db(Math.Max(Measure.Rms(l, from, Rate), Measure.Rms(r, from, Rate)));
                    // The tail's longest loop: both lines at their last setting.
                    var s = DelayDesign.From(v, 120, Rate);
                    double loop = s.Mode == DelayMode.Stereo ? Math.Max(s.SecondsL, s.SecondsR) : s.SecondsL + s.SecondsR;
                    double first = Level(noise + Rate), last = Level(noise + 9 * Rate);
                    double need = 0.5 * 8 / Math.Max(loop, 0.01) * 20 * Math.Log10(1 / Math.Abs(s.Feedback));
                    lines.Add($"{(DelayMode)mode}{(lowCut ? " low cut 5 kHz" : "")}: peak {Db(peak):+0.0;−0.0} dBFS, lines ≤ {Db(worstLine):+0.0;−0.0} dBFS, tail {last - first:+0.0;−0.0} dB in 8 s");
                    Assert.That(double.IsFinite(peak), Is.True);
                    Assert.That(worstLine, Is.LessThanOrEqualTo(DelayDesign.Ceiling), "bounded");
                    if (first > -120) Assert.That(last - first, Is.LessThan(-Math.Min(60, need)), "decays");
                }
            Measure.Report("Neo Delay fast automation (a move every 64 samples, wow 5 ms at 5 Hz, ±99 %): " + string.Join("; ", lines));
        }

        // ------------------------------------------------------------------ ducking, width

        [Test]
        public void DuckingTurnsTheEchoesDownWhileTheInputPlays()
        {
            // Duck 24 dB, threshold −30 dB: a −12 dBFS sine for 1 s, then silence. While it plays the echoes are 24 dB down
            // (the gain 10^(−24/20) within 0.1 dB); once it stops they come back, within 1 dB of full after the
            // envelope's fall through the knee (12 dB) and the threshold's distance from the sine's peak (18 dB): 30 dB at
            // 8.7 dB per time constant, about 3.5 × Duck release.
            const double release = 0.1;
            var (e, v) = Make(pv => { Plain(pv); pv.Set(P.Sync, 0); Set(pv, P.Time, 0.15); Set(pv, P.Feedback, 90); Set(pv, P.Duck, 24); Set(pv, P.DuckThresh, -30); Set(pv, P.DuckRelease, release); });
            var x = Measure.Sine(500, 2 * Rate, DspMath.DbToGain(-12));
            for (int i = Rate; i < x.Length; i++) x[i] = 0;
            var gains = new List<(int At, float Gain)>();
            Run(e, x, x, block: 64, before: pos => gains.Add((pos, e.DuckGain)));
            double during = gains.Where(g => g.At > Rate / 4 && g.At < Rate).Max(g => Db(g.Gain));
            double back = gains.First(g => g.At > Rate && g.Gain > DspMath.DbToGain(-1)).At - Rate;
            double rel = v.Value(P.DuckRelease);
            Measure.Report($"Neo Delay ducking 24 dB at −30 dB, release {rel * 1000:F0} ms: {during:F2} dB while a −12 dBFS sine plays; back within 1 dB {back * 1000 / Rate:F0} ms after it stops (expected about {3.5 * rel * 1000:F0})");
            Assert.That(during, Is.EqualTo(-24).Within(0.1));
            Assert.That(back / Rate, Is.InRange(2.5 * rel, 4.5 * rel));
            // Duck 0: no change at all.
            var (e0, _) = Make(pv => { Plain(pv); pv.Set(P.Sync, 0); Set(pv, P.Time, 0.15); });
            var (y0, _) = Run(e0, x, x);
            var (e1, _) = Make(pv => { Plain(pv); pv.Set(P.Sync, 0); Set(pv, P.Time, 0.15); pv.Set(P.DuckThresh, 0); pv.Set(P.DuckRelease, 1000); });
            var (y1, _) = Run(e1, x, x);
            Assert.That(y1, Is.EqualTo(y0), "Duck 0: the other ducking settings change nothing");
        }

        [Test]
        public void WidthScalesTheEchoesSide()
        {
            // Ping-pong (the echoes alternate sides), wet only: Width 0 % makes both sides equal, 100 % leaves them as they are,
            // 200 % doubles the side signal; the mid untouched.
            var x = Noise(Rate, 0.2, 9);
            float[] Side(float[] l, float[] r) => l.Zip(r, (a, b) => (a - b) * 0.5f).ToArray();
            float[] Mid(float[] l, float[] r) => l.Zip(r, (a, b) => (a + b) * 0.5f).ToArray();
            (float[], float[]) At(int width)
            {
                var (e, _) = Make(pv => { Plain(pv); pv.Set(P.Mode, 1); pv.Set(P.Width, width); pv.Set(P.Sync, 0); Set(pv, P.Time, 0.05); });
                return Run(e, x, x);
            }
            var (l1, r1) = At(100);
            var (l0, r0) = At(0);
            var (l2, r2) = At(200);
            Assert.That(r0, Is.EqualTo(l0), "0 %: mono echoes");
            var s1 = Side(l1, r1);
            var s2 = Side(l2, r2);
            double worst = 0, mid = 0;
            var m1 = Mid(l1, r1);
            var m2 = Mid(l2, r2);
            for (int i = 0; i < s1.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(s2[i] - 2 * s1[i]));
                mid = Math.Max(mid, Math.Abs(m2[i] - m1[i]));
            }
            Assert.That(worst, Is.LessThan(1e-6), "200 %: twice the side");
            Assert.That(mid, Is.LessThan(1e-6), "the mid untouched");
        }

        // ------------------------------------------------------------------ hygiene

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void SilenceAfterNoiseLeavesNoSubnormalsAndRunsAtFullSpeed(int mode)
        {
            // 2 s of noise, then 20 s of silence with the loop decaying (feedback 90 %, both filters on, the wow moving): nothing
            // subnormal in the lines or the filters, and silence costs what noise does. The machine stops working once the
            // lines are silent (Idle).
            var (e, _) = Make(v => { v.Set(P.Mode, mode); Set(v, P.Feedback, 90); v.Set(P.Sync, 0); Set(v, P.Time, 0.02); v.Set(P.ModDepth, 300); });
            var noise = Noise(2 * Rate, 0.3, 9);
            var silent = new float[20 * Rate];
            var sw = Stopwatch.StartNew();
            Run(e, noise, noise);
            double noiseSeconds = sw.Elapsed.TotalSeconds / 2;
            sw.Restart();
            var (l, r) = Run(e, silent, silent);
            double silenceSeconds = sw.Elapsed.TotalSeconds / 20;
            Assert.That(e.HoldsSubnormals(), Is.False, "no subnormal state");
            Assert.That(l.Concat(r).Any(float.IsSubnormal), Is.False, "no subnormal output");
            Assert.That(l.Concat(r).All(float.IsFinite), Is.True);
            Assert.That(e.Idle, Is.True, "silent lines: idle");
            Measure.Report($"Neo Delay {(DelayMode)mode}, feedback 90 %: silence after noise costs {silenceSeconds / noiseSeconds:F2}× noise; no subnormals; idle after the tail");
            Assert.That(silenceSeconds, Is.LessThan(3 * noiseSeconds + 0.002));
        }

        [Test]
        public void NanAndInfinityInTheInputLeaveTheLoopClean()
        {
            // A burst of NaN, then of ±infinity, in the middle of noise, feedback 99 %: the saturator turns NaN into silence
            // and infinity into its ceiling, so no state becomes NaN; the output is finite again as soon as the input is
            // (Mix 100 %: the bad samples only reach the output through the dry signal at other mixes).
            var (e, _) = Make(v => { v.Set(P.Feedback, 1980); v.Set(P.Mix, 1000); v.Set(P.Duck, 200); v.Set(P.ModDepth, 500); });
            var x = Noise(3 * Rate, 0.1, 13);
            for (int i = Rate; i < Rate + 100; i++) x[i] = float.NaN;
            for (int i = Rate + 1000; i < Rate + 1100; i++) x[i] = (i & 1) == 0 ? float.PositiveInfinity : float.NegativeInfinity;
            var (l, r) = Run(e, x, x);
            Assert.That(e.HoldsNonFinite(), Is.False, "no NaN or infinity in the lines, the filters or the envelope");
            for (int i = 0; i < x.Length; i++)
                if (float.IsFinite(x[i])) Assert.That(float.IsFinite(l[i]) && float.IsFinite(r[i]), Is.True, $"the output is finite wherever the input is ({i})");
            Assert.That(e.LinePeak(), Is.LessThanOrEqualTo(DelayDesign.Ceiling));
        }

        [Test]
        public void TheAudioPathAllocatesNothing()
        {
            // 2000 blocks with the time gliding and crossfading, Sync and Mode switching, the wow and ducking working: no allocation.
            var (e, v) = Make(pv => { pv.Set(P.ModDepth, 500); pv.Set(P.Duck, 200); pv.Set(P.Drive, 100); });
            var l = new float[DelayEngine.MaxBlock];
            var r = new float[DelayEngine.MaxBlock];
            var rng = new Random(1);
            void Block(int b)
            {
                for (int i = 0; i < l.Length; i++) l[i] = r[i] = (float)Math.Sin(i * 0.05 + b) * 0.3f;
                if (b % 3 == 0) v.Set(P.Time, rng.Next(10001));
                if (b % 50 == 7) v.Set(P.Sync, rng.Next(9));
                if (b % 70 == 9) v.Set(P.Mode, rng.Next(3));
                if (b % 90 == 11) e.Tempo = 90 + rng.Next(80);
                e.Process(l, r, l.Length);
            }
            for (int b = 0; b < 200; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, Block);
            Measure.Report($"Neo Delay: {bytes} bytes allocated over 2000 blocks with the time, Sync, Mode and tempo moving");
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void ParameterJumpsAreSmooth()
        {
            // A 220 Hz sine at −12 dBFS, Mix 50 %, feedback 50 %, 80 ms; once the echoes have built up (1 s), each parameter
            // jumps between its ends every 2048 samples. The largest step between samples stays within 1.5× the largest step
            // the steady settings at either end make on their own: no clicks.
            var problems = new List<string>();
            var lines = new List<string>();
            foreach (var d in Params.Table)
            {
                Action<ParamValues> baseSetup = v => { v.Set(P.Mix, 500); Set(v, P.Feedback, 50); v.Set(P.Sync, 0); Set(v, P.Time, 0.08); };
                var x = Measure.Sine(220, 3 * Rate, 0.25);
                double Steady(int raw)
                {
                    var (e, _) = Make(v => { baseSetup(v); v.Set(d.Index, raw); });
                    var (l, r) = Run(e, x, x);
                    return Math.Max(MaxStep(l, Rate), MaxStep(r, Rate));
                }
                double reference = Math.Max(Steady(d.Min), Steady(d.Max));
                var (je, jv) = Make(baseSetup);
                var (jl, jr) = Run(je, x, x, before: pos => { if (pos >= Rate && pos % 2048 == 0) jv.Set(d.Index, (pos / 2048) % 2 == 0 ? d.Min : d.Max); });
                double jump = Math.Max(MaxStep(jl, Rate), MaxStep(jr, Rate));
                double ratio = jump / reference;
                lines.Add($"{d.Name} {ratio:F2}");
                if (ratio > 1.5) problems.Add($"{d.Name} jumps make steps {ratio:F2}× the steady signal's ({jump:F4} against {reference:F4})");
            }
            Measure.Report($"Neo Delay parameter jumps, largest step against the steady signal's: {string.Join(", ", lines)}");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        // ------------------------------------------------------------------ presets, CPU

        [Test]
        public void EveryPresetLoadsAndStaysWithinLevelBounds()
        {
            // Each factory preset on pink noise at −18 dBFS RMS for 3 s: within ±6 dB of the input's level, peaks under
            // full scale plus 3 dB, finite.
            var x = Noise(3 * Rate, DspMath.DbToGain(-18), 21);
            double inRms = Measure.Rms(x, Rate, 2 * Rate);
            int count = 0;
            foreach (var (_, machine, name, values) in PresetTests.AllPresets())
            {
                if (machine != "Neo Delay") continue;
                var (e, v) = Make(pv => { foreach (var kv in values) if (kv.Key >= 0) pv.Set(kv.Key, kv.Value); });
                var (l, r) = Run(e, x, x);
                double rms = Math.Max(Measure.Rms(l, Rate, 2 * Rate), Measure.Rms(r, Rate, 2 * Rate));
                double peak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
                Measure.Report($"Neo Delay / {name}: {Db(rms / inRms):+0.0;−0.0} dB, peak {Db(peak):+0.0;−0.0} dBFS");
                Assert.That(double.IsFinite(peak), Is.True, name);
                Assert.That(Db(rms / inRms), Is.InRange(-6.0, 6.0), name);
                Assert.That(Db(peak), Is.LessThan(3), name);
                count++;
            }
            Assert.That(count, Is.InRange(12, 18));
        }

        [Test]
        public void CpuIsSmall()
        {
            // Stereo at 48 kHz, every part working: both filters, the wow, drive, ducking; in each mode.
            foreach (int mode in new[] { 0, 1, 2 })
            {
                var (e, _) = Make(v => { v.Set(P.Mode, mode); v.Set(P.ModDepth, 400); v.Set(P.Duck, 120); v.Set(P.Drive, 120); Set(v, P.Feedback, 70); });
                var x = Noise(10 * Rate, 0.2, 4);
                Run(e, x, x);   // warm
                var sw = Stopwatch.StartNew();
                Run(e, x, x);
                double speed = 10 / sw.Elapsed.TotalSeconds;
                Measure.Report($"Neo Delay CPU ({(DelayMode)mode}, everything on): {speed:F0}× real time");
                Assert.That(speed, Is.GreaterThan(50));
            }
        }
    }
}
