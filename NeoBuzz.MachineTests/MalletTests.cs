using System.Diagnostics;
using System.Xml.Linq;
using NeoBuzz.Dsp;
using NeoBuzz.Mallet;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Mallet (docs/specs/neo-mallet.md): the acceptance gates, measured.</summary>
    [TestFixture]
    public class MalletTests
    {
        const int Rate = 48000;
        static readonly string[] ModelNames = { "Soft bar", "Hard bar", "Metal bar", "Bright bar", "Tube", "Bell", "Tine", "Pluck", "Glass", "Plate", "Custom" };
        public static IEnumerable<int> Models => Enumerable.Range(0, 11);

        /// <summary>A parameter in its real unit; a percentage is given as a percentage (the engine's own value runs 0 to 1).</summary>
        static void Set(ParamValues v, int p, double physical)
        {
            var d = Params.Table[p];
            v.Set(p, d.Raw(d.Kind == MapKind.Percent || d.Kind == MapKind.Bipolar ? physical / 100.0 : physical));
        }

        /// <summary>A plain voice for measuring: no noise, no tremolo, no velocity to hardness, no stereo spread, full ring.</summary>
        static void Plain(ParamValues v, int model, double decay = 3, double tilt = 0, int modes = 16)
        {
            v.Set(P.Model, model);
            Set(v, P.Decay, decay);
            Set(v, P.Tilt, tilt);
            Set(v, P.Noise, 0);
            Set(v, P.VelHard, 0);
            Set(v, P.Spread, 0);
            Set(v, P.Volume, 0);
            Set(v, P.Modes, modes);
            Set(v, P.Hardness, 2);
            Set(v, P.Bright, 0);
            Set(v, P.TremDepth, 0);
            Set(v, P.Release, 300);
        }

        static (MalletEngine E, ParamValues V) Make(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new MalletEngine(v);
            e.SetSampleRate(Rate);
            return (e, v);
        }

        /// <summary>The strike position at which the note's own mode is heard best and the modes next to it in pitch least.</summary>
        static double BestPosition(MalletModel model, int modes)
        {
            var set = new ModeSet();
            MalletModels.Fill(set, model, modes, 0, 0);
            double best = 0.5, bestScore = double.MinValue;
            for (double x = 0.02; x <= 0.981; x += 0.01)
            {
                double w = Math.Abs(MalletModels.Weight(set, set.Ref, x)), near = 0;
                for (int k = 0; k < set.Count; k++)
                    if (k != set.Ref && Math.Abs(set.Ratio[k] / set.Ratio[set.Ref] - 1) < 0.05) near = Math.Max(near, Math.Abs(MalletModels.Weight(set, k, x)));
                double score = w - near;
                if (w >= 0.8 && score > bestScore) { bestScore = score; best = x; }
            }
            return best;
        }

        internal sealed class Take
        {
            public float[] L, R;
        }

        /// <summary>Renders <paramref name="length"/> samples in blocks of <paramref name="block"/>; the events run at their sample position (a multiple of 256, so at a block start whatever the block size).</summary>
        static Take Render(MalletEngine e, int length, int block, List<(int At, Action Do)> events = null, Action<int> perBlock = null)
        {
            var t = new Take { L = new float[length], R = new float[length] };
            var l = new float[MalletEngine.MaxBlock];
            var r = new float[MalletEngine.MaxBlock];
            int ei = 0;
            events?.Sort((a, b) => a.At.CompareTo(b.At));
            for (int pos = 0; pos < length; pos += block)
            {
                int n = Math.Min(block, length - pos);
                while (events != null && ei < events.Count && events[ei].At <= pos) events[ei++].Do();
                perBlock?.Invoke(pos);
                Array.Clear(l, 0, n); Array.Clear(r, 0, n);
                e.Process(l, r, n);
                Array.Copy(l, 0, t.L, pos, n);
                Array.Copy(r, 0, t.R, pos, n);
            }
            return t;
        }

        static float[] Mono(Take t) => t.L.Zip(t.R, (a, b) => 0.5f * (a + b)).ToArray();

        /// <summary>A Blackman-Harris windowed, zero-padded spectrum peak near <paramref name="expected"/> Hz (parabolic interpolation on the log magnitude).</summary>
        static double PeakHz(float[] x, int offset, int n, double expected, double tol = 0.03, int pad = 2)
        {
            int nfft = 1; while (nfft < n * pad) nfft <<= 1;
            var w = new float[nfft];
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * (i + 0.5) / n;
                w[i] = (float)(x[offset + i] * (0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t)));
            }
            var mag = Fft.Magnitudes(w, 0, nfft);
            int lo = Math.Max(2, (int)(expected * (1 - tol) * nfft / Rate)), hi = Math.Min(mag.Length - 3, (int)(expected * (1 + tol) * nfft / Rate) + 1);
            int best = lo;
            for (int k = lo; k <= hi; k++) if (mag[k] > mag[best]) best = k;
            double a = Math.Log(mag[best - 1] + 1e-30), b = Math.Log(mag[best] + 1e-30), c = Math.Log(mag[best + 1] + 1e-30);
            double delta = 0.5 * (a - c) / (a - 2 * b + c);
            return (best + delta) * Rate / nfft;
        }

        static double Cents(double f, double reference) => 1200 * Math.Log2(f / reference);

        static double MaxD2(float[] x, int from, int to)
        {
            double m = 0;
            from = Math.Max(1, from); to = Math.Min(x.Length - 1, to);
            for (int i = from; i < to; i++) m = Math.Max(m, Math.Abs(x[i + 1] - 2.0 * x[i] + x[i - 1]));
            return m;
        }

        // ------------------------------------------------------------------------------------------ the mode tables

        [Test]
        public void TheTablesAreTheTextbookRatios()
        {
            var s = new ModeSet();
            MalletModels.Fill(s, MalletModel.BrightBar, 8, 0, 0);
            Assert.That(s.Ratio.Take(5), Is.EqualTo(new[] { 1.0, 2.7565, 5.4039, 8.9330, 13.3440 }).Within(0.002), "a free bar: 1 : 2.756 : 5.404 : 8.933 : 13.34");
            MalletModels.Fill(s, MalletModel.SoftBar, 8, 0, 0);
            Assert.That(s.Ratio.Take(3), Is.EqualTo(new[] { 1.0, 4.0, 10.0 }), "an undercut bar: 1 : 4 : 10");
            Assert.That(s.Ratio[3], Is.GreaterThan(10.0), "above the tuned modes the free bar's spacing goes on");
            MalletModels.Fill(s, MalletModel.HardBar, 8, 0, 0);
            Assert.That(s.Ratio.Take(3), Is.EqualTo(new[] { 1.0, 3.0, 6.0 }), "the 1 : 3 : 6 bar");
            MalletModels.Fill(s, MalletModel.Bell, 12, 0, 0);
            Assert.That(s.Ratio.Take(5), Is.EqualTo(new[] { 0.5, 1.0, 1.183, 1.506, 2.0 }), "hum, prime, tierce, quint, nominal");
            Assert.That(s.Ref, Is.EqualTo(1), "a bell sounds its prime");
            MalletModels.Fill(s, MalletModel.Tine, 5, 0, 0);
            Assert.That(s.Ratio.Take(4), Is.EqualTo(new[] { 1.0, 6.267, 17.548, 34.386 }).Within(0.01), "a clamped-free bar: 1 : 6.267 : 17.55 : 34.39");
            MalletModels.Fill(s, MalletModel.Pluck, 8, 0, 1.0);
            double b = MalletModels.StiffnessB(1.0);
            Assert.That(b, Is.EqualTo(0.02).Within(1e-12));
            for (int i = 0; i < 8; i++) Assert.That(s.Ratio[i], Is.EqualTo((i + 1) * Math.Sqrt(1 + b * (i + 1) * (i + 1))).Within(1e-12), "a stiff string");
            MalletModels.Fill(s, MalletModel.Custom, 8, 12.0, 0);
            for (int i = 0; i < 8; i++) Assert.That(s.Ratio[i], Is.EqualTo((i + 1) * Math.Pow(2, i * 12.0 / 1200)).Within(1e-12), "stretch: cents per mode number step");
            MalletModels.Fill(s, MalletModel.Glass, 8, 0, 0);
            Assert.That(s.Ratio[1] / s.Ratio[0], Is.EqualTo(1.0035).Within(1e-9), "a glass mode is a doublet");
            Assert.That(s.Ratio[2] / s.Ratio[0], Is.EqualTo(2.828).Within(0.01), "ring modes 2 and 3: n(n^2-1)/sqrt(n^2+1)");
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        [TestCase(6)] [TestCase(7)] [TestCase(8)] [TestCase(9)] [TestCase(10)]
        public void EveryTableHasUpToThirtyTwoPositiveModesAndOneAtTheNote(int model)
        {
            var s = new ModeSet();
            foreach (int n in new[] { 4, 12, 32 })
            {
                MalletModels.Fill(s, (MalletModel)model, n, 5, 0.3);
                Assert.That(s.Count, Is.EqualTo(n));
                Assert.That(s.Ratio[s.Ref], Is.GreaterThan(0.4), "the played note's mode is in the table (the engine tunes it to the note)");
                for (int k = 0; k < n; k++)
                {
                    Assert.That(s.Ratio[k], Is.GreaterThan(0), $"mode {k}");
                    Assert.That(s.BaseGain[k], Is.GreaterThan(0).And.LessThan(8), $"gain {k}");
                    if (k > 0 && model != 8 && model != 9) Assert.That(s.Ratio[k], Is.GreaterThan(s.Ratio[k - 1]), $"mode {k} above mode {k - 1}");
                }
            }
        }

        [Test]
        public void TheStrikePositionWeighsModesBySineAndANodeStaysAudible()
        {
            var s = new ModeSet();
            MalletModels.Fill(s, MalletModel.SoftBar, 8, 0, 0);
            Assert.That(MalletModels.Weight(s, 0, 0.5), Is.EqualTo(1f).Within(1e-6), "the centre drives the fundamental");
            Assert.That(Math.Abs(MalletModels.Weight(s, 1, 0.5)), Is.EqualTo(MalletModels.PositionFloor).Within(1e-6), "and sits on the second mode's node");
            Assert.That(Math.Abs(MalletModels.Weight(s, 0, 0)), Is.EqualTo(MalletModels.PositionFloor).Within(1e-6), "an end strike still sounds, 30 dB down");
            Assert.That(MalletModels.PositionFloor, Is.InRange(0.02f, 0.05f));
        }

        // ------------------------------------------------------------------------------------------ gate: tuning

        [TestCaseSource(nameof(Models))]
        public void TheFundamentalIsWithinOneCentOfTheNoteFromC2ToC7(int model)
        {
            double worst = 0; int worstNote = 0;
            var lines = new List<string>();
            double pos = BestPosition((MalletModel)model, 16);
            for (int note = 36; note <= 96; note++)
            {
                var (e, _) = Make(v => { Plain(v, model); Set(v, P.Position, pos * 100); });
                e.NoteOn(1, note, 0.8f);
                var t = Render(e, 65536 + 4800, 256);
                double f0 = DspMath.NoteToHz(note);
                double hz = PeakHz(Mono(t), 4800, 65536, f0, 0.04);
                double cents = Cents(hz, f0);
                if (Math.Abs(cents) > Math.Abs(worst)) { worst = cents; worstNote = note; }
                Assert.That(Math.Abs(cents), Is.LessThan(1.0), $"{ModelNames[model]} note {note}: {hz:0.000} Hz against {f0:0.000} Hz");
            }
            TestContext.Out.WriteLine($"{ModelNames[model]}: worst fundamental error {worst:+0.000;-0.000} cents (note {worstNote}), strike position {pos:0.00}");
        }

        [TestCaseSource(nameof(Models))]
        public void EveryModeIsWithinOneCentOfItsTableRatioAndHighOnesAreDropped(int model)
        {
            double worst = 0;
            var set = new ModeSet();
            var hz = new double[32]; var t60 = new double[32]; var g = new double[32];
            foreach (var (stretch, inh) in new[] { (0.0, 0.0), (15.0, 0.5), (-20.0, 1.0) })
                for (int note = 36; note <= 96; note += 3)
                {
                    var (e, _) = Make(v => { Plain(v, model, 2, 40, 32); Set(v, P.Stretch, stretch); Set(v, P.Inharm, inh * 100); });
                    MalletModels.Fill(set, (MalletModel)model, 32, stretch, inh);
                    e.NoteOn(7, note, 0.9f);
                    int count = e.Snapshot(7, hz, t60, g);
                    Assert.That(count, Is.EqualTo(32));
                    double f0 = DspMath.NoteToHz(note);
                    for (int k = 0; k < 32; k++)
                    {
                        double want = f0 * set.Ratio[k] / set.Ratio[set.Ref];
                        if (want >= MalletEngine.TopFraction * Rate) { Assert.That(hz[k], Is.EqualTo(0), $"{ModelNames[model]} mode {k} at {want:0} Hz is dropped"); continue; }
                        double c = Cents(hz[k], want);
                        if (Math.Abs(c) > Math.Abs(worst)) worst = c;
                        Assert.That(Math.Abs(c), Is.LessThan(1.0), $"{ModelNames[model]} note {note} mode {k}: {hz[k]:0.000} Hz against {want:0.000} Hz");
                    }
                }
            TestContext.Out.WriteLine($"{ModelNames[model]}: worst mode error {worst:+0.0000;-0.0000} cents over 32 modes, notes C2 to C7, three stretch / inharmonicity settings");
        }

        [Test]
        public void TheModesInTheAudioSitAtTheirTableRatios()
        {
            // The first eight modes of every model that a strike at the best position drives, found in the spectrum of a C3.
            double worst = 0; int checkedModes = 0;
            for (int model = 0; model < 11; model++)
            {
                var set = new ModeSet();
                MalletModels.Fill(set, (MalletModel)model, 12, 8, 0.4);
                double pos = BestPosition((MalletModel)model, 12);
                var (e, _) = Make(v => { Plain(v, model, 5, 0, 12); Set(v, P.Position, pos * 100); Set(v, P.Stretch, 8); Set(v, P.Inharm, 40); Set(v, P.Hardness, 1.0); });
                e.NoteOn(1, 48, 1f);
                var t = Render(e, 98304 + 4800, 256);
                var x = Mono(t);
                double f0 = DspMath.NoteToHz(48);
                for (int k = 0; k < 8; k++)
                {
                    double want = f0 * set.Ratio[k] / set.Ratio[set.Ref];
                    bool alone = true;
                    for (int j = 0; j < 12; j++) if (j != k && Math.Abs(set.Ratio[j] / set.Ratio[k] - 1) < 0.04) alone = false;
                    if (!alone || Math.Abs(MalletModels.Weight(set, k, pos)) < 0.25 || want > 0.4 * Rate) continue;
                    double c = Cents(PeakHz(x, 4800, 98304, want, 0.02), want);
                    worst = Math.Max(worst, Math.Abs(c));
                    checkedModes++;
                    Assert.That(Math.Abs(c), Is.LessThan(1.0), $"{ModelNames[model]} mode {k} ({want:0.0} Hz)");
                }
            }
            TestContext.Out.WriteLine($"{checkedModes} modes found in the audio, worst error {worst:0.000} cents");
            Assert.That(checkedModes, Is.GreaterThan(40));
        }

        [Test]
        public void TheFundamentalsT60IsWithinFivePercentOfDecay([Values(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10)] int model, [Values(0.4, 2.0, 8.0)] double decay, [Values(36, 60, 84)] int note)
        {
            double pos = BestPosition((MalletModel)model, 16);
            var (e, _) = Make(v => { Plain(v, model, decay, 0); Set(v, P.Position, pos * 100); Set(v, P.Hardness, 1.5); });
            e.NoteOn(1, note, 0.8f);
            int len = (int)(Rate * (0.15 + decay * 0.55));
            var x = Mono(Render(e, len, 256));
            var set = new ModeSet();
            MalletModels.Fill(set, (MalletModel)model, 16, 0, 0);
            double f = DspMath.NoteToHz(note);
            // The amplitude of the note's own mode by complex demodulation in 100 ms Hann frames, from 0.05 to 0.5 of the decay.
            int frame = Math.Max(2400, (int)(10.0 / f * Rate));
            var tt = new List<double>(); var dbs = new List<double>();
            for (double at = 0.05 * decay + 0.02; at <= 0.5 * decay; at += decay * 0.03)
            {
                int start = (int)(at * Rate);
                if (start + frame > len) break;
                double re = 0, im = 0, w2 = 0;
                for (int i = 0; i < frame; i++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / frame);
                    double ph = 2 * Math.PI * f * (start + i) / Rate;
                    re += w * x[start + i] * Math.Cos(ph); im -= w * x[start + i] * Math.Sin(ph); w2 += w;
                }
                tt.Add(at + frame / 2.0 / Rate);
                dbs.Add(20 * Math.Log10(Math.Sqrt(re * re + im * im) / w2 + 1e-30));
            }
            Assert.That(tt.Count, Is.GreaterThan(6));
            double mt = tt.Average(), md = dbs.Average();
            double slope = tt.Zip(dbs, (a, b) => (a - mt) * (b - md)).Sum() / tt.Select(a => (a - mt) * (a - mt)).Sum();
            double t60 = -60.0 / slope;
            TestContext.Out.WriteLine($"T60 {ModelNames[model]} note {note} decay {decay}: {t60:0.000} s ({100 * (t60 / decay - 1):+0.00;-0.00} %)");
            Assert.That(t60, Is.EqualTo(decay).Within(0.05 * decay), $"{ModelNames[model]} note {note}: T60 {t60:0.000} s");
        }

        [Test]
        public void HigherModesDieSoonerByTheTiltAndALongerDecayRingsLonger()
        {
            var hz = new double[32]; var t60 = new double[32]; var g = new double[32];
            var (e, _) = Make(v => { Plain(v, (int)MalletModel.BrightBar, 4, 100, 8); });
            e.NoteOn(1, 60, 0.8f);
            e.Snapshot(1, hz, t60, g);
            var set = new ModeSet(); MalletModels.Fill(set, MalletModel.BrightBar, 8, 0, 0);
            Assert.That(t60[0], Is.EqualTo(4).Within(0.05));
            for (int k = 1; k < 4; k++)
                Assert.That(t60[k], Is.EqualTo(4 * Math.Pow(set.Ratio[k], -2.0 * 0.5)).Within(0.06), $"mode {k}: T60 falls as ratio^-1 at the bright bar's tilt scale 0.5");
            var (e0, _) = Make(v => Plain(v, 0, 4, 0, 8));
            e0.NoteOn(1, 60, 0.8f);
            e0.Snapshot(1, hz, t60, g);
            for (int k = 0; k < 8; k++) Assert.That(t60[k], Is.EqualTo(4).Within(0.05), "no tilt, one T60");
        }

        // ------------------------------------------------------------------------------------------ gate: clean

        static List<(int At, Action Do)> ClickScenario(MalletEngine e)
        {
            var ev = new List<(int, Action)>();
            int[] chord = { 48, 52, 55, 59, 64, 67 };
            for (int i = 0; i < chord.Length; i++) { int k = i, n = chord[i]; ev.Add((0, () => e.NoteOn(k, n, 0.85f))); ev.Add((19200, () => e.NoteOff(k))); }
            // Twenty notes at once: four more than the voices, so four are stolen.
            for (int i = 0; i < 20; i++) { int k = 10 + i, n = 40 + 3 * i; ev.Add((28928, () => e.NoteOn(k, n, 0.5f + 0.02f * k))); ev.Add((72192 + 256 * i, () => e.NoteOff(k))); }
            ev.Add((96000, () => e.NoteOn(40, 72, 1f)));
            ev.Add((105728, () => e.AllNotesOff(true)));
            ev.Add((115200, () => e.NoteOn(41, 60, 0.7f)));
            ev.Add((124928, () => e.NoteOff(41)));
            return ev;
        }

        [Test]
        public void NoClicksAtNoteStartReleaseAndStealAtBlockSizes16And64And256([Values(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10)] int model, [Values(16, 64, 256)] int block, [Values(2.0, 0.3)] double hardness)
        {
            var (e, _) = Make(v => { Plain(v, model, 3, 40, 16); Set(v, P.Hardness, hardness); Set(v, P.Noise, 10); Set(v, P.Position, 30); Set(v, P.Release, 60); Set(v, P.Spread, 50); Set(v, P.VelHard, 50); Set(v, P.Volume, -12); });
            var t = Render(e, 144000, block, ClickScenario(e));
            var clicks = ClickDetector.Find(t.L, t.R);
            Assert.That(clicks, Is.Empty, $"{ModelNames[model]}, hardness {hardness} ms, block {block}: clicks at {string.Join(", ", clicks)}");
            Assert.That(Measure.Peak(t.L, 0, t.L.Length), Is.GreaterThan(0.05), "it played");
        }

        [Test]
        public void ABlockOf16And64And256GiveTheSameSamples([Values(0, 5, 8)] int model)
        {
            Take reference = null;
            foreach (int block in new[] { 256, 64, 16 })
            {
                var (e, _) = Make(v => { Plain(v, model, 3, 40, 24); Set(v, P.Noise, 20); Set(v, P.TremDepth, 40); Set(v, P.Spread, 60); });
                var t = Render(e, 144000, block, ClickScenario(e));
                if (reference == null) reference = t;
                else
                {
                    Assert.That(t.L, Is.EqualTo(reference.L), $"left, block {block}");
                    Assert.That(t.R, Is.EqualTo(reference.R), $"right, block {block}");
                }
            }
        }

        /// <summary>The ear-proxy of the brief: the largest |second difference| in the first and the last 5 ms of a note is no higher than in its middle.</summary>
        [Test]
        public void TheFirstAndLast5MsHaveNoSharperEdgesThanTheMiddle([Values(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10)] int model, [Values(0.8f, 1f)] float velocity)
        {
            // The ear-proxy: the first and last 5 ms of a note (held for a second, 20 s decay, no tilt) are no sharper than the sharpest 5 ms of its body.
            double pos = BestPosition((MalletModel)model, 16);
            foreach (bool kill in new[] { false, true })
            {
                var (e, _) = Make(v => { Plain(v, model, 20, 0, 16); Set(v, P.Hardness, model == 6 || model == 7 ? 12 : 6); Set(v, P.Release, 80); Set(v, P.Position, pos * 100); });
                var ev = new List<(int, Action)> { (0, () => e.NoteOn(1, 60, velocity)), (48000, () => { if (kill) e.AllNotesOff(true); else e.NoteOff(1); }) };
                var x = Mono(Render(e, 96000, 256, ev));
                double peak = Measure.Peak(x, 0, x.Length);
                int end = x.Length - 1;
                // The end of the note: where it falls to a ten-thousandth of its peak for good (a kill's fade ends in exact silence).
                while (end > 48000 && Math.Abs(x[end]) < 1e-4 * peak) end--;
                double first = MaxD2(x, 0, 240), last = MaxD2(x, end - 240, end + 1), middle = MaxD2(x, 240, 48000);   // the body: the sharpest the note itself ever is, up to its release
                Assert.That(first, Is.LessThanOrEqualTo(middle), $"{ModelNames[model]}: the first 5 ms ({first:0.00000}) against the middle ({middle:0.00000})");
                Assert.That(last, Is.LessThanOrEqualTo(middle), $"{ModelNames[model]} {(kill ? "killed" : "released")}: the last 5 ms ({last:0.00000}) against the middle ({middle:0.00000})");
            }
        }

        [Test]
        public void AStolenVoiceFadesOutOverAtLeast3Ms()
        {
            Assert.That(MalletEngine.FadeSeconds, Is.GreaterThanOrEqualTo(0.003), "the fade is at least 3 ms");
            // 16 held notes, the first a high one (period 0.5 ms); a 17th takes its voice. Its level, read in 0.5 ms frames by demodulation, falls from 95 % to 5 % in at least 3 ms.
            var (e, _) = Make(v => { Plain(v, (int)MalletModel.Custom, 30, 0, 4); Set(v, P.Hardness, 0.5); Set(v, P.Position, 37); Set(v, P.Release, 2000); });
            var ev = new List<(int, Action)>();
            ev.Add((0, () => e.NoteOn(100, 96, 1f)));                      // C7, the oldest: the one that is stolen
            for (int i = 0; i < 15; i++) { int k = i; ev.Add((256, () => e.NoteOn(k, 40 + k, 0.2f))); }
            ev.Add((96000, () => e.NoteOn(200, 50, 0.1f)));
            var x = Mono(Render(e, 100000, 256, ev));
            double f = DspMath.NoteToHz(96);
            int frame = 48;                                                  // 1 ms: two periods of the victim
            double Level(int at)
            {
                double re = 0, im = 0;
                for (int i = 0; i < frame; i++) { double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / frame); double ph = 2 * Math.PI * f * (at + i) / Rate; re += w * x[at + i] * Math.Cos(ph); im -= w * x[at + i] * Math.Sin(ph); }
                return Math.Sqrt(re * re + im * im);
            }
            double full = Level(95000);
            Assert.That(full, Is.GreaterThan(1e-4), "the victim rings");
            int t90 = -1, t10 = -1;
            for (int at = 96000; at < 96000 + 960; at += 4)
            {
                double l = Level(at) / full;
                if (t90 < 0 && l < 0.95) t90 = at;
                if (t10 < 0 && l < 0.05) { t10 = at; break; }
            }
            Assert.That(t90, Is.GreaterThan(0), "it starts to fade");
            Assert.That(t10, Is.GreaterThan(0), "it fades out");
            double ms = (t10 - t90) / (Rate / 1000.0);
            TestContext.Out.WriteLine($"95 % to 5 %: {ms:0.00} ms");
            Assert.That(ms, Is.InRange(3.0, 7.0));
        }

        // ------------------------------------------------------------------------------------------ gate: the audio thread

        [Test]
        public void ARenderMakesNoGarbageWith16VoicesAnd32Modes()
        {
            var (e, v) = Make(vv => { Plain(vv, (int)MalletModel.Custom, 5, 30, 32); Set(vv, P.TremDepth, 50); Set(vv, P.Spread, 80); Set(vv, P.Noise, 30); });
            var l = new float[MalletEngine.MaxBlock]; var r = new float[MalletEngine.MaxBlock];
            var rng = new Random(5);
            for (int n = 0; n < 16; n++) e.NoteOn(n, 36 + 3 * n, 0.7f);
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                if (b % 40 == 0) { int k = rng.Next(16); e.NoteOff(k); e.NoteOn(k, 36 + rng.Next(60), 0.3f + 0.7f * (float)rng.NextDouble()); }
                if (b % 97 == 0) { e.NoteOn(100 + b, 60 + rng.Next(12), 0.8f); }         // more than 16: steals
                if (b % 7 == 0)
                {
                    v.Set(P.Model, rng.Next(11)); v.Set(P.Decay, rng.Next(1001)); v.Set(P.Tilt, rng.Next(1001)); v.Set(P.Bright, rng.Next(1001)); v.Set(P.Position, rng.Next(1001));
                    v.Set(P.Volume, 600 + rng.Next(400)); v.Set(P.TremDepth, rng.Next(1001)); v.Set(P.TremRate, rng.Next(1001)); v.Set(P.Spread, rng.Next(1001));
                    v.Set(P.Modes, rng.Next(29)); v.Set(P.Stretch, rng.Next(1001));
                }
                e.Process(l, r, MalletEngine.MaxBlock);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void CpuPerVoiceModeIsReported()
        {
            var (e, _) = Make(vv => { Plain(vv, (int)MalletModel.Custom, 20, 0, 32); Set(vv, P.Release, 2000); });
            var l = new float[256]; var r = new float[256];
            for (int n = 0; n < 16; n++) e.NoteOn(n, 36 + n, 0.7f);
            for (int b = 0; b < 100; b++) e.Process(l, r, 256);
            Assert.That(e.ActiveVoices, Is.EqualTo(16));
            var sw = Stopwatch.StartNew();
            const int blocks = 750;                                       // 4 s of audio
            for (int b = 0; b < blocks; b++) e.Process(l, r, 256);
            sw.Stop();
            double perSample = sw.Elapsed.TotalMilliseconds * 1e6 / (blocks * 256.0 * 16 * 32);
            double load = sw.Elapsed.TotalSeconds / (blocks * 256.0 / Rate);
            TestContext.Out.WriteLine($"{perSample:0.00} ns per voice-mode-sample, 16 voices of 32 modes use {load * 100:0.0} % of one core at 48 kHz");
            Assert.That(perSample, Is.LessThan(30), "per voice-mode-sample");
        }

        [Test]
        public void TheTailsLeaveNoDenormalsAndTheMachineGoesQuiet()
        {
            var (e, _) = Make(v => { Plain(v, 0, 0.3, 0, 32); Set(v, P.Release, 50); });
            for (int n = 36; n < 72; n += 3) e.NoteOn(n, n, 1f);
            var ev = new List<(int, Action)>();
            for (int n = 36; n < 72; n += 3) { int k = n; ev.Add((24000, () => e.NoteOff(k))); }
            var t = Render(e, 6 * Rate, 256, ev);
            int tiny = 0;
            foreach (var x in t.L.Concat(t.R)) if (x != 0 && Math.Abs(x) < 1.2e-38f) tiny++;
            Assert.That(tiny, Is.EqualTo(0), "no denormal in the output");
            Assert.That(Measure.Peak(t.L, 4 * Rate, Rate), Is.EqualTo(0), "silent again");
            Assert.That(e.IsActive, Is.False);
            // A held note that has rung out is freed as well.
            var (e2, _) = Make(v => { Plain(v, 0, 0.05, 0, 8); });
            e2.NoteOn(1, 60, 1f);
            Render(e2, 3 * Rate, 256);
            Assert.That(e2.IsActive, Is.False, "a held note that has died away is freed");
        }

        [Test]
        public void AutomationMakesNoZipperNoise()
        {
            // A long, soft strike of a harmonic series (Custom, 20 s decay, a 12 ms felt head, so almost nothing rings above 2 kHz); from the first second on every
            // block moves Volume, Brightness, Position, Decay, the spread and the tremolo. Nothing above 2 kHz may rise above -60 dB of the carrier: the modes
            // there have nothing but what the changes make.
            var (e, v) = Make(vv => { Plain(vv, (int)MalletModel.Custom, 20, 0, 16); Set(vv, P.Hardness, 12); Set(vv, P.Spread, 50); });
            int len = Rate * 6;
            var rng = new Random(9);
            var x = Mono(Render(e, len, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, 60, 1f)) }, pos =>
            {
                if (pos < Rate) return;
                v.Set(P.Volume, 700 + rng.Next(300)); v.Set(P.Bright, rng.Next(1001)); v.Set(P.Position, 200 + rng.Next(700)); v.Set(P.Decay, 500 + rng.Next(400));
                v.Set(P.Spread, rng.Next(1001)); v.Set(P.TremDepth, rng.Next(400));
            }));
            int start = 2 * Rate, n = 1 << 16;
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = (float)(x[start + i] * (0.35875 - 0.48829 * Math.Cos(2 * Math.PI * (i + 0.5) / n) + 0.14128 * Math.Cos(4 * Math.PI * (i + 0.5) / n) - 0.01168 * Math.Cos(6 * Math.PI * (i + 0.5) / n)));
            var mag = Fft.Magnitudes(w, 0, n);
            double carrier = 0, high = 0;
            for (int k = 1; k < mag.Length; k++)
            {
                double hz = k * (double)Rate / n;
                if (hz > 100 && hz < 600) carrier = Math.Max(carrier, mag[k]);
                if (hz > 2000) high = Math.Max(high, mag[k]);
            }
            double db = 20 * Math.Log10(high / carrier);
            TestContext.Out.WriteLine($"highest line above 2 kHz: {db:0.0} dB under the carrier");
            Assert.That(db, Is.LessThan(-60));
        }

        // ------------------------------------------------------------------------------------------ state and presets

        [Test]
        public void EveryParameterSurvivesAMigrationAndAReopenedStateSoundsTheSame()
        {
            var table = Params.Table;
            Assert.That(table.Length, Is.EqualTo(P.Count));
            var rng = new Random(31);
            var a = new ParamValues(table);
            for (int i = 0; i < table.Length; i++) a.Set(i, table[i].Min + rng.Next(table[i].Max - table[i].Min + 1));
            var saved = Enumerable.Range(0, table.Length).Select(i => a[i]).ToArray();
            var chain = new StateMigrations(table);
            Assert.That(chain.Current, Is.EqualTo(0), "version 0");
            Assert.That(chain.MigrateGlobals(0, saved), Is.EqualTo(saved), "a v0 song loads unchanged");
            var b = new ParamValues(table);
            for (int i = 0; i < table.Length; i++) b.Set(i, saved[i]);
            for (int i = 0; i < table.Length; i++) Assert.That(table[i].Raw(table[i].Value(a[i])), Is.EqualTo(a[i]).Within(1), $"{table[i].Id} round trip");
            Take Go(ParamValues v)
            {
                var e = new MalletEngine(v); e.SetSampleRate(Rate);
                return Render(e, 24000, 256, new List<(int, Action)> { (0, () => { e.NoteOn(1, 55, 0.9f); e.NoteOn(2, 62, 0.6f); }) });
            }
            var ra = Go(a); var rb = Go(b);
            Assert.That(rb.L, Is.EqualTo(ra.L)); Assert.That(rb.R, Is.EqualTo(ra.R));
            // Every parameter has an id and a unit-bearing mapping; the defaults are in range.
            foreach (var p in table) { Assert.That(p.Default, Is.InRange(p.Min, p.Max), p.Id); Assert.That(p.Id, Is.Not.Empty); }
        }

        internal static IEnumerable<(string Name, Dictionary<int, int> Values)> FactoryPresets()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "ReBuzz.sln"))) d = d.Parent;
            var file = Path.Combine(d!.FullName, "Machines", "NeoMallet", "Neo Mallet.prs.xml");
            foreach (var item in XDocument.Load(file).Root!.Elements("Item"))
            {
                var vals = new Dictionary<int, int>();
                foreach (var p in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter")) vals[(int)p.Attribute("Index")!] = (int)p.Attribute("Value")!;
                yield return ((string)item.Attribute("Key")!, vals);
            }
        }

        /// <summary>A short melody and chord of our own, on the engine of a preset: (left, right).</summary>
        internal static Take PlayMelody(Dictionary<int, int> preset, int length)
        {
            var v = new ParamValues(Params.Table);
            foreach (var kv in preset) v.Set(kv.Key, kv.Value);
            var e = new MalletEngine(v); e.SetSampleRate(Rate);
            var ev = new List<(int, Action)>();
            int[] melody = { 72, 76, 79, 76, 74, 71, 67, 72 };
            for (int i = 0; i < melody.Length; i++) { int n = melody[i], k = 20 + i; ev.Add((i * 9216, () => e.NoteOn(k, n, 0.85f))); ev.Add((i * 9216 + 7680, () => e.NoteOff(k))); }
            int[] chord = { 48, 55, 60 };
            for (int i = 0; i < chord.Length; i++) { int n = chord[i], k = i; ev.Add((0, () => e.NoteOn(k, n, 0.7f))); ev.Add((length - 9600, () => e.NoteOff(k))); }
            return Render(e, length, 256, ev);
        }

        [Test]
        public void TheFactoryPresetsAreAudibleAndClearlyDifferent()
        {
            var presets = FactoryPresets().ToList();
            Assert.That(presets.Count, Is.EqualTo(8), "eight presets");
            int len = Rate * 4;
            var bands = new List<double[]>();
            var names = new List<string>();
            foreach (var (name, vals) in presets)
            {
                var t = PlayMelody(vals, len);
                var x = Mono(t);
                double peak = Measure.Peak(x, 0, len), rms = Measure.Rms(x, 0, len);
                TestContext.Out.WriteLine($"{name}: peak {Measure.Db(peak):0.0} dBFS, RMS {Measure.Db(rms):0.0} dB");
                Assert.That(Measure.Db(peak), Is.InRange(-24.0, -1.0), $"{name}: audible, not hot");
                Assert.That(Measure.Db(rms), Is.GreaterThan(-42.0), $"{name}: audible at first listen");
                // One-third-octave band levels (dB) from 100 Hz to 12.5 kHz over the whole take, and the level of the last second against the first.
                int n = 1 << 17;
                var spec = Fft.Magnitudes(x, 0, n);
                var b = new List<double>();
                for (double c = 100; c < 12600; c *= Math.Pow(2, 1.0 / 3))
                {
                    int lo = (int)(c / Math.Pow(2, 1.0 / 6) * n / Rate), hi = (int)(c * Math.Pow(2, 1.0 / 6) * n / Rate);
                    double p = 0; for (int k = lo; k <= hi; k++) p += spec[k] * spec[k];
                    b.Add(10 * Math.Log10(p + 1e-20));
                }
                b.Add(Measure.Db(Measure.Rms(x, Rate * 3, Rate)) - Measure.Db(Measure.Rms(x, 0, Rate)));   // how much of it is left in the last second
                bands.Add(b.ToArray()); names.Add(name);
            }
            double least = double.MaxValue; string pair = "";
            for (int i = 0; i < bands.Count; i++)
                for (int j = i + 1; j < bands.Count; j++)
                {
                    double d = 0; int m = bands[i].Length;
                    for (int k = 0; k < m; k++) d += (bands[i][k] - bands[j][k]) * (bands[i][k] - bands[j][k]);
                    d = Math.Sqrt(d / m);
                    if (d < least) { least = d; pair = names[i] + " / " + names[j]; }
                }
            TestContext.Out.WriteLine($"the two closest presets: {pair}, {least:0.0} dB apart (RMS over one-third-octave bands)");
            Assert.That(least, Is.GreaterThan(4.0), $"{pair}: clearly different");
        }

        /// <summary>The level of an instrument: the pack's melody peaks between -6 and -3 dBFS, and four notes struck together at full velocity stay under -1 dBFS (the limiter's ceiling) with no clicks.</summary>
        [Test]
        public void TheLevelIsThatOfAnInstrumentAndAFourNoteChordDoesNotClip()
        {
            int length = (int)(Rate * (MalletPack.Beat * 24 + 1.8));
            foreach (var (name, vals) in FactoryPresets())
            {
                var t = MalletPack.Play(vals, MalletPack.Tune(), length);
                double peak = Math.Max(Measure.Peak(t.L, 0, t.L.Length), Measure.Peak(t.R, 0, t.R.Length));
                Assert.That(Measure.Db(peak), Is.InRange(-6.0, -3.0), $"{name}: the melody's peak");
                var v = new ParamValues(Params.Table);
                foreach (var kv in vals) v.Set(kv.Key, kv.Value);
                var e = new MalletEngine(v); e.SetSampleRate(Rate);
                var ev = new List<(int, Action)>();
                int[] chord = { 48, 55, 60, 64 };
                for (int i = 0; i < chord.Length; i++) { int n = chord[i], k = i; ev.Add((0, () => e.NoteOn(k, n, 1f))); ev.Add((Rate * 2, () => e.NoteOff(k))); }
                var c = Render(e, Rate * 4, 256, ev);
                double cp = Math.Max(Measure.Peak(c.L, 0, c.L.Length), Measure.Peak(c.R, 0, c.R.Length));
                TestContext.Out.WriteLine($"{name}: melody peak {Measure.Db(peak):0.0} dBFS, four-note chord at full velocity {Measure.Db(cp):0.0} dBFS");
                Assert.That(Measure.Db(cp), Is.LessThan(-1.0 + 1e-6), $"{name}: the chord stays under the limiter's -1 dBFS ceiling");
                Assert.That(ClickDetector.Find(c.L, c.R), Is.Empty, $"{name}: no clicks in the chord (a limiter that bites would make them)");
            }
        }

        [Test]
        public void TheLimiterIsTransparentBelowItsKneeAndHoldsTheCeiling()
        {
            // Below the knee (0.7, -3.1 dBFS) the output is linear in Volume: the same note 6 dB down is half.
            Take Note(double volume)
            {
                var (e, _) = Make(v => { Plain(v, 4, 20, 0, 8); Set(v, P.Hardness, 4); Set(v, P.Volume, volume); });
                return Render(e, 24000, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, 60, 0.6f)) });
            }
            var loud = Note(12.0); var half = Note(12.0 - 6.0206);
            double top = Measure.Peak(loud.L, 0, loud.L.Length);
            Assert.That(top, Is.InRange(0.15, 0.69), "the note sits under the knee");
            for (int i = 0; i < loud.L.Length; i++) Assert.That(half.L[i], Is.EqualTo(0.5f * loud.L[i]).Within(5e-3 * top), $"sample {i}: linear below the knee");
            var (e2, _) = Make(v => { Plain(v, 4, 20, 0, 32); Set(v, P.Hardness, 0.3); Set(v, P.Volume, 12); });
            for (int k = 0; k < 16; k++) e2.NoteOn(k, 48 + k, 1f);
            var hot = Render(e2, 24000, 256);
            Assert.That(Measure.Db(Math.Max(Measure.Peak(hot.L, 0, hot.L.Length), Measure.Peak(hot.R, 0, hot.R.Length))), Is.LessThanOrEqualTo(-1.0 + 1e-6), "16 hard strikes at +12 dB stay under the -1 dBFS ceiling");
        }

        [Test]
        public void EveryFactoryPresetPlaysWithoutClicks()
        {
            int len = Rate * 6;
            foreach (var (name, vals) in FactoryPresets())
            {
                var t = PlayMelody(vals, len);
                var clicks = ClickDetector.Find(t.L, t.R);
                Assert.That(clicks, Is.Empty, $"{name}: clicks at {string.Join(", ", clicks)}");
            }
        }

        static string RepoFile(params string[] parts)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "ReBuzz.sln"))) d = d.Parent;
            return Path.Combine(new[] { d!.FullName }.Concat(parts).ToArray());
        }

        /// <summary>Neo Mallet is an instrument: a generator with no audio input, no Mix and no Amount, in the manifest, both projects and the machine's declaration.</summary>
        [Test]
        public void TheMachineIsAGeneratorWithNoAudioInputMixOrAmount()
        {
            var manifest = File.ReadAllText(RepoFile("Machines", "NeoMallet", "Neo Mallet.machine.json"));
            Assert.That(System.Text.Json.JsonDocument.Parse(manifest).RootElement.GetProperty("kind").GetString(), Is.EqualTo("generator"), "manifest kind");
            foreach (var proj in new[] { RepoFile("Machines", "Portable", "NeoMallet", "NeoMallet.Portable.csproj"), RepoFile("Machines", "Studio", "NeoMallet", "NeoMallet.Studio.csproj") })
                Assert.That(File.ReadAllText(proj), Does.Contain("<NeoKind>Generators</NeoKind>"), proj);
            Assert.That(File.ReadAllText(RepoFile("Machines", "NeoMallet", "NeoMallet.cs")), Does.Contain("InputCount = 0, OutputCount = 1"), "no audio input");
            Assert.That(File.ReadAllText(RepoFile("Machines", "NeoMallet", "NeoMallet.cs")), Does.Not.Contain("Sample[] input"), "Work takes no input");
            foreach (var id in new[] { "Source", "InDrive", "Mix", "Amount" })
                Assert.That(Params.Table.Any(t => t.Id == id), Is.False, $"no {id} parameter");
            Assert.That(Params.Table.Length, Is.EqualTo(16));
        }

        [Test]
        public void TheModelNamesAndRangesAreTheDocumented()
        {
            Assert.That(Params.Table[P.Model].Names, Is.EqualTo(ModelNames));
            Assert.That(Params.Table[P.Hardness].Value(Params.Table[P.Hardness].Min), Is.EqualTo(12.0).Within(1e-9), "softest: 12 ms");
            Assert.That(Params.Table[P.Hardness].Value(Params.Table[P.Hardness].Max), Is.EqualTo(0.3).Within(1e-9), "hardest: 0.3 ms");
            Assert.That(Params.Table[P.Modes].Value(Params.Table[P.Modes].Min), Is.EqualTo(4));
            Assert.That(Params.Table[P.Modes].Value(Params.Table[P.Modes].Max), Is.EqualTo(32));
        }

        [Test]
        public void HarderMalletsBrightenAndVelocityHardensWithVelToHardness()
        {
            double Centroid(double hardness, float velocity, double velHard)
            {
                var (e, _) = Make(v => { Plain(v, 0, 1, 0, 24); Set(v, P.Hardness, hardness); Set(v, P.VelHard, velHard); Set(v, P.Position, 22); });
                e.NoteOn(1, 60, velocity);
                var x = Mono(Render(e, 16384, 256));
                var mag = Fft.Magnitudes(x, 0, 16384);
                double num = 0, den = 0;
                for (int k = 1; k < mag.Length; k++) { double p = mag[k] * mag[k]; num += p * k * Rate / 16384.0; den += p; }
                return num / den;
            }
            double soft = Centroid(8, 0.8f, 0), hard = Centroid(0.4, 0.8f, 0);
            TestContext.Out.WriteLine($"spectral centroid, soft mallet {soft:0} Hz, hard mallet {hard:0} Hz");
            Assert.That(hard, Is.GreaterThan(soft * 1.5), "a harder mallet excites more of the highs");
            double lightTouch = Centroid(2, 0.2f, 100), heavyTouch = Centroid(2, 1.0f, 100), flat = Centroid(2, 0.2f, 0), flat2 = Centroid(2, 1.0f, 0);
            Assert.That(heavyTouch, Is.GreaterThan(lightTouch * 1.3), "with velocity to hardness a hard hit is brighter");
            Assert.That(flat2 / flat, Is.EqualTo(1.0).Within(0.02), "without it the colour does not move with velocity");
        }

        [Test]
        public void TheTremoloSwingsTheLevelAtItsRate()
        {
            var (e, _) = Make(v => { Plain(v, (int)MalletModel.MetalBar, 20, 0, 8); Set(v, P.TremRate, 5); Set(v, P.TremDepth, 80); });
            e.NoteOn(1, 60, 1f);
            var x = Mono(Render(e, Rate * 2, 256));
            // Envelope in 10 ms RMS frames: its maxima every 0.2 s, its swing 80 %.
            var env = new List<double>();
            for (int i = 24000; i + 480 <= x.Length; i += 480) env.Add(Measure.Rms(x, i, 480) / Math.Pow(10, -3.0 * (i + 240) / Rate / 20.0));   // undo the 20 s decay
            double max = env.Max(), min = env.Min();
            Assert.That(1 - min / max, Is.EqualTo(0.8).Within(0.06), "depth");
            int peaks = 0;
            for (int i = 10; i < env.Count - 10; i++) if (env[i] > 0.9 * max && Enumerable.Range(i - 10, 21).All(j => env[j] <= env[i])) peaks++;     // the highest frame within 100 ms either side
            Assert.That(peaks, Is.InRange(4, 6), "five swings a second over 1 s");
        }

        [Test]
        public void ReleaseDampingSetsHowFastAReleasedNoteDies([Values(20.0, 200.0, 1500.0)] double release)
        {
            var (e, _) = Make(v => { Plain(v, (int)MalletModel.BrightBar, 20, 0, 8); Set(v, P.Release, release); });
            var ev = new List<(int, Action)> { (0, () => e.NoteOn(1, 72, 1f)), (24000, () => e.NoteOff(1)) };
            var x = Mono(Render(e, Rate * 4, 256, ev));
            double before = Measure.Rms(x, 23700, 240);
            // The level 0.3 of the way to -60 dB after the key goes up: -18 dB at release * 0.3.
            int at = 24000 + (int)(release * 0.3 * Rate / 1000.0);
            double after = Measure.Rms(x, at, 240);
            double db = Measure.Db(after / before);
            Assert.That(db, Is.InRange(-23.0, -13.0), $"release {release} ms: {db:0.0} dB at 30 % of the release time");
        }
    }
}
