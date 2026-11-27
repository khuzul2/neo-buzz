using System.Diagnostics;
using System.Xml.Linq;
using NeoBuzz.Dsp;
using NeoBuzz.Keys;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Keys (docs/specs/neo-keys.md): the acceptance gates, measured.</summary>
    [TestFixture]
    public class KeysTests
    {
        const int Rate = 48000;
        static readonly string[] ModelNames = { "Tine", "Reed", "String" };
        public static IEnumerable<int> Models => Enumerable.Range(0, 3);

        internal sealed class Take { public float[] L, R; }

        /// <summary>A parameter in its real unit; a percentage is given as a percentage (the engine's own value runs 0 to 1).</summary>
        static void Set(ParamValues v, int p, double physical)
        {
            var d = Params.Table[p];
            v.Set(p, d.Raw(d.Kind == MapKind.Percent || d.Kind == MapKind.Bipolar ? physical / 100.0 : physical));
        }

        /// <summary>A plain voice for measuring: no key noise, no tremolo, no drive, a clean pickup, flat tone, full ring, Volume 0 dB.</summary>
        static void Plain(ParamValues v, int model, double decay = 3)
        {
            v.Set(P.Model, model);
            Set(v, P.Decay, decay);
            Set(v, P.KeyNoise, 0);
            Set(v, P.TremDepth, 0);
            Set(v, P.Stereo, 0);
            Set(v, P.Drive, 0);
            Set(v, P.Tone, 0);
            Set(v, P.Pickup, 20);
            Set(v, P.Hardness, 1.5);
            Set(v, P.Touch, 60);
            Set(v, P.VelCurve, 0);
            Set(v, P.Release, 300);
            Set(v, P.Volume, 0);
            v.Set(P.Mute, 0);
            v.Set(P.PickupSel, 2);
        }

        static (KeysEngine E, ParamValues V) Make(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new KeysEngine(v);
            e.SetSampleRate(Rate);
            return (e, v);
        }

        static Take Render(KeysEngine e, int length, int block, List<(int At, Action Do)> events = null, Action<int> perBlock = null)
        {
            var t = new Take { L = new float[length], R = new float[length] };
            var l = new float[KeysEngine.MaxBlock];
            var r = new float[KeysEngine.MaxBlock];
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

        static float[] Note(Action<ParamValues> setup, int note, float velocity, int length = Rate)
        {
            var (e, _) = Make(setup);
            return Mono(Render(e, length, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, note, velocity)) }));
        }

        static double[] Window(float[] x, int offset, int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * (i + 0.5) / n;
                w[i] = (float)(x[offset + i] * (0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t)));
            }
            var m = Fft.Magnitudes(w, 0, n);
            return m.Select(a => (double)a).ToArray();
        }

        /// <summary>The spectral centroid in Hz of n samples from offset.</summary>
        static double Centroid(float[] x, int offset, int n)
        {
            var m = Window(x, offset, n);
            double num = 0, den = 0;
            for (int k = 1; k < m.Length; k++) { double f = k * (double)Rate / n, p = m[k] * m[k]; num += f * p; den += p; }
            return num / Math.Max(den, 1e-30);
        }

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

        static double MaxStep(float[] y, int from)
        {
            double m = 0;
            for (int i = Math.Max(1, from); i < y.Length; i++) m = Math.Max(m, Math.Abs(y[i] - y[i - 1]));
            return m;
        }

        /// <summary>The loudest 50 ms RMS (dB) of a render, as the level tests read it.</summary>
        static double Loudest50(float[] y)
        {
            double loudest = 0;
            for (int at = 0; at + 2400 <= y.Length * 2 / 3; at += 1200) loudest = Math.Max(loudest, Measure.Rms(y, at, 2400));
            return Measure.Db(loudest);
        }

        // ------------------------------------------------------------------------------------------ gate: tuning

        [Test]
        public void EveryNoteIsWithinThreeCentsOfEqualTemperamentOverThreeOctaves([ValueSource(nameof(Models))] int model, [Values(36, 48, 60, 72, 84)] int note)
        {
            // Bar and body at 0 and at 100 %: the slow partial that sits a few cents off must not pull the note out of tune.
            foreach (double bar in new[] { 0.0, 100.0 })
            {
                var y = Note(v => { Plain(v, model, 4); Set(v, P.Bar, bar); }, note, 0.7f, Rate + Rate / 2);
                double f0 = DspMath.NoteToHz(note);
                double f = PeakHz(y, 4800, Rate, f0);
                double c = Cents(f, f0);
                TestContext.Out.WriteLine($"{ModelNames[model]} note {note} bar {bar}: {c:+0.00;-0.00} cents");
                Assert.That(Math.Abs(c), Is.LessThanOrEqualTo(3.0), $"{ModelNames[model]} note {note}, bar {bar} %");
            }
        }

        [Test]
        public void TheFundamentalsT60IsWithinFivePercentOfDecay([ValueSource(nameof(Models))] int model, [Values(0.4, 2.0, 8.0)] double decay, [Values(48, 60, 72)] int note)
        {
            var (e, _) = Make(v => { Plain(v, model, decay); Set(v, P.Release, 1500); });
            e.NoteOn(7, note, 0.8f);
            var l = new float[256]; var r = new float[256];
            e.Process(l, r, 256);
            var hz = new double[16]; var t60 = new double[16];
            int count = e.Snapshot(7, hz, t60);
            double f0 = DspMath.NoteToHz(note);
            int best = -1;
            for (int m = 0; m < count; m++) if (hz[m] > 0 && Math.Abs(Cents(hz[m], f0)) < 4 && (best < 0 || t60[m] < t60[best])) best = m;
            Assert.That(best, Is.GreaterThanOrEqualTo(0), "a mode at the note");
            TestContext.Out.WriteLine($"{ModelNames[model]} decay {decay} note {note}: T60 {t60[best]:0.000} s");
            Assert.That(t60[best], Is.EqualTo(decay).Within(decay * 0.05));
        }

        // ------------------------------------------------------------------------------------------ gate: velocity

        [Test]
        public void LevelRisesWithVelocityAndSoDoesTheCentroid([ValueSource(nameof(Models))] int model)
        {
            var levels = new double[128]; var centroid = new double[128];
            for (int vel = 1; vel <= 127; vel++)
            {
                var y = Note(v => Plain(v, model, 3), 60, vel / 127f, 24000);
                levels[vel] = Measure.Db(Measure.Rms(y, 0, 14400));
                if (vel == 10 || vel == 30 || vel == 60 || vel == 90 || vel == 127) centroid[vel] = Centroid(y, 0, 8192);
            }
            for (int vel = 2; vel <= 127; vel++) Assert.That(levels[vel], Is.GreaterThan(levels[vel - 1] - 1e-9), $"{ModelNames[model]}: velocity {vel} is not louder than {vel - 1}");
            double span = levels[127] - levels[30];
            TestContext.Out.WriteLine($"{ModelNames[model]}: 30 to 127 is {span:0.0} dB; centroid 10: {centroid[10]:0} 30: {centroid[30]:0} 60: {centroid[60]:0} 90: {centroid[90]:0} 127: {centroid[127]:0} Hz");
            Assert.That(span, Is.GreaterThanOrEqualTo(12.0), $"{ModelNames[model]}: at least 12 dB between 30 and 127");
            Assert.That(centroid[30], Is.GreaterThan(centroid[10]), "centroid 10 to 30");
            Assert.That(centroid[60], Is.GreaterThan(centroid[30]), "centroid 30 to 60");
            Assert.That(centroid[90], Is.GreaterThan(centroid[60]), "centroid 60 to 90");
            Assert.That(centroid[127], Is.GreaterThan(centroid[90]), "centroid 90 to 127");
        }

        [Test]
        public void HarderHammersBrightenAndAHarderCurveNeedsAHarderPress()
        {
            double Bright(double ms) => Centroid(Note(v => { Plain(v, 0, 3); Set(v, P.Hardness, ms); Set(v, P.Touch, 0); }, 60, 0.8f, 24000), 0, 8192);
            Assert.That(Bright(0.3), Is.GreaterThan(Bright(5.0) * 1.3), "a hard hammer is brighter than a soft one");
            double Level(double curve) => Measure.Db(Measure.Rms(Note(v => { Plain(v, 0, 3); Set(v, P.VelCurve, curve); }, 60, 0.4f, 24000), 0, 14400));
            Assert.That(Level(100), Is.LessThan(Level(0) - 3), "+100 % needs a harder press");
            Assert.That(Level(-100), Is.GreaterThan(Level(0) + 3), "-100 % answers to a light touch");
        }

        // ------------------------------------------------------------------------------------------ gate: notes and the sustain pedal

        [Test]
        public void TheSustainPedalHoldsNotesAfterTheKeyGoesUpAndReleasesThemWhenItLifts()
        {
            double Tail(bool pedal, bool lift)
            {
                var (e, _) = Make(v => { Plain(v, 0, 20); Set(v, P.Release, 30); });
                var ev = new List<(int, Action)>();
                if (pedal) ev.Add((0, () => e.Sustain(true)));
                ev.Add((256, () => e.NoteOn(1, 60, 0.8f)));
                ev.Add((24000, () => e.NoteOff(1)));
                if (pedal && lift) ev.Add((72000, () => e.Sustain(false)));
                var y = Mono(Render(e, 120000, 256, ev));
                return Measure.Db(Measure.Rms(y, 96000, 4800)) - Measure.Db(Measure.Rms(y, 20000, 2400));
            }
            double free = Tail(false, false), held = Tail(true, false), lifted = Tail(true, true);
            TestContext.Out.WriteLine($"level 1.5 s after the key went up against the level before: no pedal {free:0.0} dB, pedal down {held:0.0} dB, pedal lifted at 1.5 s {lifted:0.0} dB");
            Assert.That(free, Is.LessThan(-40), "without the pedal the note is damped");
            Assert.That(held, Is.GreaterThan(-12), "with the pedal down it rings on");
            Assert.That(lifted, Is.LessThan(-40), "lifting the pedal damps it");
        }

        [Test]
        public void AKeyPressedAgainUnderThePedalStrikesAgain()
        {
            var (e, _) = Make(v => Plain(v, 0, 10));
            var ev = new List<(int, Action)>
            {
                (0, () => e.Sustain(true)), (256, () => e.NoteOn(1, 60, 0.8f)), (9600, () => e.NoteOff(1)), (48000, () => e.NoteOn(1, 60, 0.8f)),
            };
            var y = Mono(Render(e, 72000, 256, ev));
            Assert.That(Measure.Rms(y, 48500, 2400), Is.GreaterThan(Measure.Rms(y, 47000, 960) * 1.5), "the second press is heard");
        }

        // ------------------------------------------------------------------------------------------ gate: clicks

        static List<(int At, Action Do)> ClickScenario(KeysEngine e)
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
        public void NoClicksAtNoteStartReleaseAndStealAtBlockSizes16And64And256([ValueSource(nameof(Models))] int model, [Values(16, 64, 256)] int block, [Values(1.5, 0.2)] double hardness)
        {
            var (e, _) = Make(v => { Plain(v, model, 3); Set(v, P.Hardness, hardness); Set(v, P.KeyNoise, 40); Set(v, P.Pickup, 60); Set(v, P.Release, 40); Set(v, P.Volume, -12); });
            var t = Render(e, 144000, block, ClickScenario(e));
            var clicks = ClickDetector.Find(t.L, t.R);
            Assert.That(clicks, Is.Empty, $"{ModelNames[model]}, hardness {hardness} ms, block {block}: clicks at {string.Join(", ", clicks)}");
            Assert.That(Measure.Peak(t.L, 0, t.L.Length), Is.GreaterThan(0.02), "it played");
        }

        [Test]
        public void AStolenVoiceFadesOutOverAtLeast3Ms()
        {
            Assert.That(KeysEngine.FadeSeconds, Is.GreaterThanOrEqualTo(0.003));
            Assert.That(KeysEngine.FadeSeconds, Is.LessThanOrEqualTo(0.007), "5 ms steal fade");
            // 16 notes are held, the first a high one; a 17th takes its voice. The victim's level, read in 1 ms frames by demodulation, falls from 95 % to 5 % in 3 to 7 ms.
            var (e, _) = Make(v => { Plain(v, 0, 30); Set(v, P.Hardness, 0.5); Set(v, P.Release, 1500); Set(v, P.Bar, 0); Set(v, P.Pickup, 0); });
            var ev = new List<(int, Action)>();
            ev.Add((0, () => e.NoteOn(100, 96, 1f)));
            for (int i = 0; i < 15; i++) { int k = i; ev.Add((256, () => e.NoteOn(k, 40 + k, 0.2f))); }
            ev.Add((96000, () => e.NoteOn(200, 50, 0.1f)));
            var x = Mono(Render(e, 100000, 256, ev));
            double f = DspMath.NoteToHz(96);
            int frame = 48;
            double Level(int at)
            {
                double re = 0, im = 0;
                for (int i = 0; i < frame; i++) { double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / frame); double ph = 2 * Math.PI * f * (at + i) / Rate; re += w * x[at + i] * Math.Cos(ph); im -= w * x[at + i] * Math.Sin(ph); }
                return Math.Sqrt(re * re + im * im);
            }
            double full = Level(95000);
            Assert.That(full, Is.GreaterThan(1e-5), "the victim rings");
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

        [Test]
        public void ParameterJumpsAreSmooth()
        {
            // A held note of 20 s decay and a new note every 8192 samples, with each parameter jumping between its ends every 4096. The detector finds no click, and the
            // largest step stays within 1.5 times the largest of the steady settings at either end.
            var problems = new List<string>();
            var lines = new List<string>();
            foreach (var d in Params.Table)
            {
                void Setup(ParamValues v) { Plain(v, 0, 20); Set(v, P.Volume, -6); Set(v, P.Hardness, 1.5); }
                List<(int, Action)> Notes(KeysEngine e)
                {
                    var ev = new List<(int, Action)> { (0, () => e.NoteOn(1, 52, 0.7f)), (0, () => e.NoteOn(2, 64, 0.6f)) };
                    for (int i = 1; i < 14; i++) { int k = 10 + i; ev.Add((i * 8192 + 1024, () => e.NoteOn(k, 55 + k % 7, 0.6f))); }
                    return ev;
                }
                double Steady(int raw)
                {
                    var (e, _) = Make(v => { Setup(v); v.Set(d.Index, raw); });
                    var t = Render(e, 12 * 8192, 256, Notes(e));
                    return Math.Max(MaxStep(t.L, 8192), MaxStep(t.R, 8192));
                }
                double reference = Math.Max(Steady(d.Min), Steady(d.Max));
                var (je, jv) = Make(Setup);
                var jt = Render(je, 12 * 8192, 256, Notes(je), pos => { if (pos >= 8192 && pos % 4096 == 0) jv.Set(d.Index, (pos / 4096) % 2 == 0 ? d.Min : d.Max); });
                double jump = Math.Max(MaxStep(jt.L, 8192), MaxStep(jt.R, 8192));
                double ratio = jump / Math.Max(reference, 1e-9);
                lines.Add($"{d.Name} {ratio:F2}");
                if (ratio > 1.5) problems.Add($"{d.Name} jumps make steps {ratio:F2}x the steady signal's ({jump:F4} against {reference:F4})");
                var clicks = ClickDetector.Find(jt.L, jt.R);
                if (clicks.Count > 0) problems.Add($"{d.Name}: the click detector found {clicks.Count}, at {string.Join(", ", clicks.Take(5))}");
            }
            Measure.Report($"Neo Keys parameter jumps, largest step against the steady signal's: {string.Join(", ", lines)}");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [Test]
        public void AutomationMakesNoZipperNoise()
        {
            // A long soft note of a tine at a clean pickup; from the first second on every block moves Volume, Tone, Drive, Pickup, Bar and the tremolo. Nothing above 2 kHz may
            // rise above -60 dB of the carrier.
            var (e, v) = Make(vv => { Plain(vv, 0, 20); Set(vv, P.Hardness, 6); Set(vv, P.Touch, 0); Set(vv, P.Pickup, 0); });
            int len = Rate * 6;
            var rng = new Random(9);
            var x = Mono(Render(e, len, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, 52, 0.5f)) }, pos =>
            {
                if (pos < Rate) return;
                v.Set(P.Volume, 700 + rng.Next(300)); v.Set(P.Tone, 300 + rng.Next(400)); v.Set(P.Pickup, rng.Next(300)); v.Set(P.Bar, rng.Next(1001));
                v.Set(P.TremDepth, rng.Next(400)); v.Set(P.TremRate, rng.Next(1001)); v.Set(P.Stereo, rng.Next(1001));
            }));
            int start = 2 * Rate, n = 1 << 16;
            var mag = Window(x, start, n);
            double carrier = 0, high = 0;
            for (int k = 1; k < mag.Length; k++)
            {
                double hz = k * (double)Rate / n;
                if (hz > 100 && hz < 300) carrier = Math.Max(carrier, mag[k]);
                if (hz > 2000) high = Math.Max(high, mag[k]);
            }
            double db = 20 * Math.Log10(high / carrier);
            TestContext.Out.WriteLine($"highest line above 2 kHz: {db:0.0} dB under the carrier");
            Assert.That(db, Is.LessThan(-60));
        }

        // ------------------------------------------------------------------------------------------ gate: the audio thread and cpu

        [Test]
        public void ARenderMakesNoGarbageWith16VoicesOfEveryModel()
        {
            var (e, v) = Make(vv => { Plain(vv, 0, 5); Set(vv, P.TremDepth, 50); Set(vv, P.Stereo, 80); Set(vv, P.KeyNoise, 30); Set(vv, P.Drive, 12); });
            var l = new float[KeysEngine.MaxBlock]; var r = new float[KeysEngine.MaxBlock];
            var rng = new Random(5);
            for (int n = 0; n < 16; n++) e.NoteOn(n, 36 + 3 * n, 0.7f);
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                if (b % 40 == 0) { int k = rng.Next(16); e.NoteOff(k); e.NoteOn(k, 36 + rng.Next(60), 0.3f + 0.7f * (float)rng.NextDouble()); }
                if (b % 97 == 0) { e.NoteOn(100 + b, 60 + rng.Next(12), 0.8f); }
                if (b % 211 == 0) e.Sustain(b % 422 == 0);
                if (b % 7 == 0)
                {
                    foreach (var d in Params.Table) v.Set(d.Index, d.Min + rng.Next(d.Max - d.Min + 1));
                    v.Set(P.Volume, 600 + rng.Next(400));
                }
                e.Process(l, r, KeysEngine.MaxBlock);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void TheTailsLeaveNoDenormalsAndTheMachineGoesQuiet([ValueSource(nameof(Models))] int model)
        {
            var (e, _) = Make(v => { Plain(v, model, 0.3); Set(v, P.Release, 50); Set(v, P.KeyNoise, 50); Set(v, P.Drive, 12); Set(v, P.TremDepth, 50); });
            for (int n = 36; n < 72; n += 3) e.NoteOn(n, n, 1f);
            var ev = new List<(int, Action)>();
            for (int n = 36; n < 72; n += 3) { int k = n; ev.Add((24000, () => e.NoteOff(k))); }
            var t = Render(e, 30 * Rate, 256, ev);
            int tiny = 0;
            foreach (var x in t.L.Concat(t.R)) if (x != 0 && Math.Abs(x) < 1.2e-38f) tiny++;
            Assert.That(tiny, Is.EqualTo(0), "no denormal in the output over a 30 s tail");
            Assert.That(Measure.Peak(t.L, 25 * Rate, 5 * Rate), Is.EqualTo(0), "silent again");
            Assert.That(Measure.Peak(t.R, 25 * Rate, 5 * Rate), Is.EqualTo(0), "silent again (right)");
            Assert.That(e.IsActive, Is.False);
            var (e2, _) = Make(v => Plain(v, model, 0.05));
            e2.NoteOn(1, 60, 1f);
            Render(e2, 3 * Rate, 256);
            Assert.That(e2.IsActive, Is.False, "a held note that has died away is freed");
        }

        static double VoiceCost(Func<(Action<float[], float[], int> Process, Action Start)> make)
        {
            double best = double.MaxValue;
            var l = new float[256]; var r = new float[256];
            for (int round = 0; round < 3; round++)
            {
                var (process, start) = make();
                start();
                for (int b = 0; b < 100; b++) process(l, r, 256);
                var sw = Stopwatch.StartNew();
                for (int b = 0; b < 500; b++) process(l, r, 256);
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalSeconds / (500 * 256.0 / Rate));
            }
            return best;
        }

        [Test]
        public void SixteenVoicesOfEachModelCostAtMostTwiceNeoMalletsSixteen()
        {
            double Mallet(int modes)
            {
                return VoiceCost(() =>
                {
                    var v = new ParamValues(NeoBuzz.Mallet.Params.Table);
                    v.Set(NeoBuzz.Mallet.P.Model, (int)NeoBuzz.Mallet.MalletModel.Custom);
                    v.Set(NeoBuzz.Mallet.P.Modes, modes);
                    var d = NeoBuzz.Mallet.Params.Table[NeoBuzz.Mallet.P.Decay]; v.Set(NeoBuzz.Mallet.P.Decay, d.Raw(20.0));
                    var e = new NeoBuzz.Mallet.MalletEngine(v); e.SetSampleRate(Rate);
                    return ((l, r, n) => e.Process(l, r, n), () => { for (int k = 0; k < 16; k++) e.NoteOn(k, 36 + k, 0.7f); });
                });
            }
            double mallet16 = Mallet(16), mallet32 = Mallet(32);
            double worst = 0;
            foreach (int model in Models)
            {
                double load = VoiceCost(() =>
                {
                    var (e, _) = Make(v => { Plain(v, model, 20); Set(v, P.Release, 1500); Set(v, P.KeyNoise, 30); Set(v, P.Drive, 12); Set(v, P.TremDepth, 50); Set(v, P.Pickup, 60); });
                    return ((l, r, n) => e.Process(l, r, n), () => { for (int k = 0; k < 16; k++) e.NoteOn(k, 36 + k, 0.7f); });
                });
                TestContext.Out.WriteLine($"{ModelNames[model]}: 16 voices use {load * 100:0.00} % of one core at 48 kHz ({load / mallet16:0.00}x Neo Mallet's 16 voices of 16 modes)");
                worst = Math.Max(worst, load);
            }
            TestContext.Out.WriteLine($"Neo Mallet, 16 voices: {mallet16 * 100:0.00} % with 16 modes, {mallet32 * 100:0.00} % with 32 modes");
            Assert.That(worst, Is.LessThanOrEqualTo(2.0 * mallet16), "at most 2x Neo Mallet's 16 voices");
        }

        // ------------------------------------------------------------------------------------------ the sound

        [Test]
        public void ThePickupBarksWhenCloseAndHardPlayedAndSmoothsOutAsTheNoteDies()
        {
            // Harmonics 2 to 5 of the note against its first, as the pickup moves close.
            double Ratio(float[] y, int at, double f0)
            {
                var m = Window(y, at, 16384);
                double Peak(double hz) { int c = (int)Math.Round(hz * 16384 / Rate); double p = 0; for (int k = c - 3; k <= c + 3; k++) p = Math.Max(p, m[k]); return p; }
                double first = Peak(f0), rest = 0;
                for (int h = 2; h <= 5; h++) { double p = Peak(h * f0); rest += p * p; }
                return 20 * Math.Log10(Math.Sqrt(rest) / first);
            }
            double f = DspMath.NoteToHz(48);
            var far = Note(v => { Plain(v, 0, 6); Set(v, P.Pickup, 0); Set(v, P.Hardness, 3); Set(v, P.Touch, 0); }, 48, 1f, 3 * Rate);
            var near = Note(v => { Plain(v, 0, 6); Set(v, P.Pickup, 100); Set(v, P.Hardness, 3); Set(v, P.Touch, 0); }, 48, 1f, 3 * Rate);
            var soft = Note(v => { Plain(v, 0, 6); Set(v, P.Pickup, 100); Set(v, P.Hardness, 3); Set(v, P.Touch, 0); }, 48, 0.15f, 3 * Rate);
            double farR = Ratio(far, 4800, f), nearR = Ratio(near, 4800, f), softR = Ratio(soft, 4800, f), late = Ratio(near, 2 * Rate, f);
            TestContext.Out.WriteLine($"harmonics 2-5 against the first: far {farR:0.0} dB, close and hard {nearR:0.0} dB, close and soft {softR:0.0} dB, close and hard 2 s later {late:0.0} dB");
            Assert.That(nearR, Is.GreaterThan(farR + 6), "close is barkier than far");
            Assert.That(nearR, Is.GreaterThan(softR + 3), "hard is barkier than soft");
            Assert.That(late, Is.LessThan(nearR - 3), "the bark smooths out as the note dies");
        }

        [Test]
        public void TheReedHasMoreOddThanEvenHarmonicsAtACloseElectrostaticPickup()
        {
            var y = Note(v => { Plain(v, 1, 4); Set(v, P.Pickup, 100); Set(v, P.Hardness, 3); Set(v, P.Touch, 0); Set(v, P.Bar, 0); }, 48, 1f, 2 * Rate);
            var m = Window(y, 4800, 16384);
            double f0 = DspMath.NoteToHz(48);
            double Peak(int h) { int c = (int)Math.Round(h * f0 * 16384 / Rate); double p = 0; for (int k = c - 3; k <= c + 3; k++) p = Math.Max(p, m[k]); return p; }
            double odd = Peak(3) + Peak(5), even = Peak(2) + Peak(4);
            TestContext.Out.WriteLine($"reed, harmonics 3+5 against 2+4: {20 * Math.Log10(odd / even):0.0} dB");
            Assert.That(odd, Is.GreaterThan(even), "odd harmonics dominate");
        }

        [Test]
        public void TheStringsMuteShortensTheDecayAndThePickupsDifferInTone()
        {
            double Rms(Action<ParamValues> s, int at) => Measure.Db(Measure.Rms(Note(v => { Plain(v, 2, 3); s(v); }, 55, 0.8f, 2 * Rate), at, 4800));
            double open = Rms(v => v.Set(P.Mute, 0), 24000), muted = Rms(v => v.Set(P.Mute, 1), 24000);
            Assert.That(muted, Is.LessThan(open - 20), "muted notes die away much sooner");
            double Cent(int sel) => Centroid(Note(v => { Plain(v, 2, 3); v.Set(P.PickupSel, sel); }, 55, 0.8f, Rate), 0, 8192);
            double neck = Cent(0), bridge = Cent(1), both = Cent(2);
            TestContext.Out.WriteLine($"string centroid: neck {neck:0} Hz, bridge {bridge:0} Hz, both {both:0} Hz");
            Assert.That(bridge, Is.GreaterThan(neck * 1.3), "the bridge pickup is thinner and brighter");
            Assert.That(both, Is.Not.EqualTo(neck).Within(5).And.Not.EqualTo(bridge).Within(5), "both is neither");
        }

        [Test]
        public void TheTremoloSwingsTheLevelAndTheAutoPanMovesItBetweenTheChannels()
        {
            var (e, _) = Make(v => { Plain(v, 0, 20); Set(v, P.TremRate, 4); Set(v, P.TremDepth, 100); Set(v, P.Stereo, 100); Set(v, P.Hardness, 6); });
            var t = Render(e, 4 * Rate, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, 60, 0.8f)) });
            double lmin = 1e9, lmax = 0, rmin = 1e9, rmax = 0;
            for (int at = Rate; at + 1200 <= 3 * Rate; at += 1200)
            {
                double l = Measure.Rms(t.L, at, 1200), r = Measure.Rms(t.R, at, 1200);
                lmin = Math.Min(lmin, l); lmax = Math.Max(lmax, l); rmin = Math.Min(rmin, r); rmax = Math.Max(rmax, r);
            }
            TestContext.Out.WriteLine($"left swings {Measure.Db(lmax) - Measure.Db(lmin):0.0} dB, right {Measure.Db(rmax) - Measure.Db(rmin):0.0} dB");
            Assert.That(Measure.Db(lmax) - Measure.Db(lmin), Is.GreaterThan(20), "the level swings");
            Assert.That(Measure.Db(rmax) - Measure.Db(rmin), Is.GreaterThan(20));
            var (e2, _) = Make(v => { Plain(v, 0, 20); Set(v, P.TremDepth, 0); Set(v, P.Stereo, 100); });
            var q = Render(e2, Rate, 256, new List<(int, Action)> { (0, () => e2.NoteOn(1, 60, 0.8f)) });
            Assert.That(Measure.Rms(q.L, 12000, 12000), Is.EqualTo(Measure.Rms(q.R, 12000, 12000)).Within(1e-6), "no tremolo, no pan");
        }

        [Test]
        public void KeyNoiseIsThereOnlyWhenAskedFor()
        {
            double Clunk(double noise)
            {
                var (e, _) = Make(v => { Plain(v, 0, 3); Set(v, P.Hardness, 6); Set(v, P.Bar, 0); Set(v, P.KeyNoise, noise); Set(v, P.Release, 8); });
                var t = Render(e, 3 * Rate, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, 60, 0.7f)), (Rate, () => e.NoteOff(1)) });
                return Measure.Rms(Mono(t), Rate + 1200, 4800);
            }
            double quiet = Clunk(0), loud = Clunk(100);
            TestContext.Out.WriteLine($"after the key goes up: {Measure.Db(quiet):0.0} dB without key noise, {Measure.Db(loud):0.0} dB with it");
            Assert.That(loud, Is.GreaterThan(quiet * 4), "the damper's thump is heard");
        }

        // ------------------------------------------------------------------------------------------ state and presets

        [Test]
        public void EveryParameterSurvivesAMigrationAndAReopenedStateSoundsTheSame()
        {
            var table = Params.Table;
            Assert.That(table.Length, Is.EqualTo(P.Count));
            Assert.That(table.Length, Is.EqualTo(17));
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
                var e = new KeysEngine(v); e.SetSampleRate(Rate);
                return Render(e, 24000, 256, new List<(int, Action)> { (0, () => { e.NoteOn(1, 55, 0.9f); e.NoteOn(2, 62, 0.6f); }) });
            }
            var ra = Go(a); var rb = Go(b);
            Assert.That(rb.L, Is.EqualTo(ra.L)); Assert.That(rb.R, Is.EqualTo(ra.R));
            foreach (var p in table) { Assert.That(p.Default, Is.InRange(p.Min, p.Max), p.Id); Assert.That(p.Id, Is.Not.Empty); }
        }

        internal static IEnumerable<(string Name, Dictionary<int, int> Values)> FactoryPresets()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "ReBuzz.sln"))) d = d.Parent;
            var file = Path.Combine(d!.FullName, "Machines", "NeoKeys", "Neo Keys.prs.xml");
            foreach (var item in XDocument.Load(file).Root!.Elements("Item"))
            {
                var vals = new Dictionary<int, int>();
                foreach (var p in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter")) vals[(int)p.Attribute("Index")!] = (int)p.Attribute("Value")!;
                yield return ((string)item.Attribute("Key")!, vals);
            }
        }

        static ParamValues Values(Dictionary<int, int> preset)
        {
            var v = new ParamValues(Params.Table);
            foreach (var kv in preset) v.Set(kv.Key, kv.Value);
            return v;
        }

        /// <summary>The level test's chord: C3 E3 G3 B3 at velocity 0.8, 1 s.</summary>
        static float[] Chord(ParamValues v)
        {
            var e = new KeysEngine(v); e.SetSampleRate(Rate);
            var t = Render(e, Rate, 256, new List<(int, Action)> { (0, () => { e.NoteOn(0, 48, 0.8f); e.NoteOn(1, 52, 0.8f); e.NoteOn(2, 55, 0.8f); e.NoteOn(3, 59, 0.8f); }) });
            var y = new float[t.L.Length];
            for (int i = 0; i < y.Length; i++) y[i] = Math.Max(Math.Abs(t.L[i]), Math.Abs(t.R[i]));
            return y;
        }

        [Test]
        public void TheDefaultPatchAndEveryPresetSitAtTheNeoLevelOfMinus22Db()
        {
            double def = Loudest50(Chord(new ParamValues(Params.Table)));
            TestContext.Out.WriteLine($"default patch: {def:0.0} dB");
            Assert.That(def, Is.InRange(-23.0, -21.0), "the default patch");
            foreach (var (name, vals) in FactoryPresets())
            {
                double level = Loudest50(Chord(Values(vals)));
                TestContext.Out.WriteLine($"{name}: {level:0.0} dB");
                Assert.That(level, Is.InRange(-23.0, -21.0), name);
            }
        }

        internal static Take PlayMelody(Dictionary<int, int> preset, int length)
        {
            var e = new KeysEngine(Values(preset)); e.SetSampleRate(Rate);
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
                var x = Mono(PlayMelody(vals, len));
                double peak = Measure.Peak(x, 0, len), rms = Measure.Rms(x, 0, len);
                TestContext.Out.WriteLine($"{name}: peak {Measure.Db(peak):0.0} dBFS, RMS {Measure.Db(rms):0.0} dB");
                Assert.That(Measure.Db(peak), Is.InRange(-24.0, -1.0), $"{name}: audible, not hot");
                Assert.That(Measure.Db(rms), Is.GreaterThan(-42.0), $"{name}: audible at first listen");
                int n = 1 << 17;
                var spec = Fft.Magnitudes(x, 0, n);
                var b = new List<double>();
                for (double c = 100; c < 12600; c *= Math.Pow(2, 1.0 / 3))
                {
                    int lo = (int)(c / Math.Pow(2, 1.0 / 6) * n / Rate), hi = (int)(c * Math.Pow(2, 1.0 / 6) * n / Rate);
                    double p = 0; for (int k = lo; k <= hi; k++) p += spec[k] * spec[k];
                    b.Add(10 * Math.Log10(p + 1e-20));
                }
                b.Add(Measure.Db(Measure.Rms(x, Rate * 3, Rate)) - Measure.Db(Measure.Rms(x, 0, Rate)));
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

        [Test]
        public void EveryFactoryPresetPlaysWithoutClicksAndAFourNoteChordStaysUnderTheCeiling()
        {
            foreach (var (name, vals) in FactoryPresets())
            {
                var t = PlayMelody(vals, Rate * 6);
                var clicks = ClickDetector.Find(t.L, t.R);
                Assert.That(clicks, Is.Empty, $"{name}: clicks at {string.Join(", ", clicks)}");
                var e = new KeysEngine(Values(vals)); e.SetSampleRate(Rate);
                var ev = new List<(int, Action)>();
                int[] chord = { 48, 55, 60, 64 };
                for (int i = 0; i < chord.Length; i++) { int n = chord[i], k = i; ev.Add((0, () => e.NoteOn(k, n, 1f))); ev.Add((Rate * 2, () => e.NoteOff(k))); }
                var c = Render(e, Rate * 4, 256, ev);
                double cp = Math.Max(Measure.Peak(c.L, 0, c.L.Length), Measure.Peak(c.R, 0, c.R.Length));
                Assert.That(Measure.Db(cp), Is.LessThan(-1.0 + 1e-6), $"{name}: under the -1 dBFS ceiling");
                Assert.That(ClickDetector.Find(c.L, c.R), Is.Empty, $"{name}: no clicks in the chord");
            }
        }

        static string RepoFile(params string[] parts)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "ReBuzz.sln"))) d = d.Parent;
            return Path.Combine(new[] { d!.FullName }.Concat(parts).ToArray());
        }

        /// <summary>Neo Keys is an instrument: a generator with no audio input, no Mix and no Amount, in the manifest, both projects and the machine's declaration.</summary>
        [Test]
        public void TheMachineIsAGeneratorWithNoAudioInputMixOrAmount()
        {
            var manifest = File.ReadAllText(RepoFile("Machines", "NeoKeys", "Neo Keys.machine.json"));
            Assert.That(System.Text.Json.JsonDocument.Parse(manifest).RootElement.GetProperty("kind").GetString(), Is.EqualTo("generator"), "manifest kind");
            foreach (var proj in new[] { RepoFile("Machines", "Portable", "NeoKeys", "NeoKeys.Portable.csproj"), RepoFile("Machines", "Studio", "NeoKeys", "NeoKeys.Studio.csproj") })
                Assert.That(File.ReadAllText(proj), Does.Contain("<NeoKind>Generators</NeoKind>"), proj);
            Assert.That(File.ReadAllText(RepoFile("Machines", "NeoKeys", "NeoKeys.cs")), Does.Contain("InputCount = 0, OutputCount = 1"), "no audio input");
            foreach (var id in new[] { "Source", "InDrive", "Mix", "Amount" })
                Assert.That(Params.Table.Any(t => t.Id == id), Is.False, $"no {id} parameter");
        }

        [Test]
        public void TheModelNamesAndRangesAreTheDocumented()
        {
            Assert.That(Params.Table[P.Model].Names, Is.EqualTo(ModelNames));
            Assert.That(Params.Table[P.Hardness].Value(Params.Table[P.Hardness].Min), Is.EqualTo(6.0).Within(1e-9), "softest: 6 ms");
            Assert.That(Params.Table[P.Hardness].Value(Params.Table[P.Hardness].Max), Is.EqualTo(0.15).Within(1e-9), "hardest: 0.15 ms");
            Assert.That(Params.Table[P.Decay].Value(Params.Table[P.Decay].Max), Is.EqualTo(20.0).Within(1e-9));
            Assert.That(Params.Table[P.Drive].Value(Params.Table[P.Drive].Max), Is.EqualTo(30.0).Within(1e-9));
        }
    }
}
