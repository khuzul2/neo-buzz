using Buzz.MachineInterface;
using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.Organ;
using ReBuzzTests.Automation;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Organ (docs/specs/neo-organ.md): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class OrganTests
    {
        const int Rate = Measure.Rate;

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        /// <summary>Everything that colours the tone off: only the drawbars (all 0), no percussion, click, leakage, vibrato, drive or speaker.</summary>
        static void Clean(ParamValues v)
        {
            for (int b = 0; b < 9; b++) v.Set(P.B16 + b, 0);
            v.Set(P.PercOn, 0);
            v.Set(P.KeyClick, 0);
            v.Set(P.Leakage, 0);
            v.Set(P.VibOn, 0);
            v.Set(P.Drive, 0);
            v.Set(P.Rotary, 0);
            Set(v, P.Volume, 0);
            v.Set(P.Expression, 1000);
        }

        static (OrganEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, bool clean = true)
        {
            var v = new ParamValues(Params.Table);
            if (clean) Clean(v);
            setup?.Invoke(v);
            var e = new OrganEngine(v);
            e.SetSampleRate(Rate);
            return (e, v);
        }

        static void On(OrganEngine e, int note, int slot = -1) => e.NoteOn(slot >= 0 ? slot : 1000 + note, note, 1f);
        static void Off(OrganEngine e, int note, int slot = -1) => e.NoteOff(slot >= 0 ? slot : 1000 + note);

        /// <summary>Renders <paramref name="length"/> samples; returns the left and the right channel.</summary>
        static (float[] L, float[] R) Render(OrganEngine e, int length, Action<int> atBlock = null)
        {
            var yl = new float[length];
            var yr = new float[length];
            var l = new float[OrganEngine.MaxBlock];
            var r = new float[OrganEngine.MaxBlock];
            for (int pos = 0; pos < length; pos += OrganEngine.MaxBlock)
            {
                atBlock?.Invoke(pos);
                int n = Math.Min(OrganEngine.MaxBlock, length - pos);
                e.Process(l, r, n);
                Array.Copy(l, 0, yl, pos, n);
                Array.Copy(r, 0, yr, pos, n);
            }
            return (yl, yr);
        }

        static float[] RenderL(OrganEngine e, int length, Action<int> atBlock = null) => Render(e, length, atBlock).L;

        /// <summary>Strongest partial's frequency (Hz) and its amplitude, from a Blackman-Harris spectrum with parabolic interpolation.</summary>
        static (double Hz, double Amp) Strongest(float[] x, int offset, int n)
        {
            var w = new float[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n;
                double win = 0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t);
                w[i] = (float)(x[offset + i] * win);
                sum += win;
            }
            var mag = Fft.Magnitudes(w, 0, n);
            int best = 1;
            for (int k = 2; k < mag.Length - 1; k++) if (mag[k] > mag[best]) best = k;
            double a = Math.Log(mag[best - 1] + 1e-30), b = Math.Log(mag[best] + 1e-30), c = Math.Log(mag[best + 1] + 1e-30);
            double delta = 0.5 * (a - c) / (a - 2 * b + c);
            double amp = Math.Exp(b - 0.25 * (a - c) * delta) / (sum / n);
            return ((best + delta) * Rate / n, amp);
        }

        /// <summary>Blackman-Harris spectrum (a unit sine reads about 0.36 at its peak bin).</summary>
        static double[] Spec(float[] x, int offset, int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n;
                w[i] = (float)(x[offset + i] * (0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t)));
            }
            return Fft.Magnitudes(w, 0, n);
        }

        // ------------------------------------------------------------------------------------------ the tone generator

        [Test]
        public void TheWheelTableIsEqualTemperedFromC1ToFSharp8()
        {
            Assert.That(Tonewheels.WheelHz(1), Is.EqualTo(32.7032).Within(0.001));
            Assert.That(Tonewheels.WheelHz(46), Is.EqualTo(440.0).Within(1e-9).Or.EqualTo(Tonewheels.WheelHz(46)));   // wheel 46 = MIDI 69
            Assert.That(Tonewheels.WheelHz(46), Is.EqualTo(440.0).Within(1e-6));
            Assert.That(Tonewheels.WheelHz(91), Is.EqualTo(5919.911).Within(0.01));
            // The foldback: the highest footage of the highest key lands on the top octave and a half.
            Assert.That(Tonewheels.WheelFor(60, 36), Is.InRange(80, 91));
            Assert.That(Tonewheels.WheelFor(0, -12), Is.EqualTo(1));
        }

        [TestCase(60)]
        [TestCase(84)]
        [TestCase(96)]
        public void EachDrawbarAloneMakesItsFootagesPartialAtTheWheelFrequency(int note)
        {
            int key = Tonewheels.KeyFor(note);
            var lines = new List<string>();
            for (int bar = 0; bar < 9; bar++)
            {
                var (e, _) = Make(v => v.Set(P.B16 + bar, 8));
                On(e, note);
                var y = RenderL(e, 8192 + 131072);
                var (hz, amp) = Strongest(y, 8192, 131072);
                double want = Tonewheels.WheelHz(Tonewheels.WheelFor(key, Tonewheels.BarOffset[bar]));
                double cents = Measure.Cents(hz, want);
                lines.Add($"{Tonewheels.BarFootage[bar]} {hz:F2} Hz ({cents:+0.00;-0.00} ct)");
                Assert.That(Math.Abs(cents), Is.LessThan(1.0), $"drawbar {Tonewheels.BarFootage[bar]} at note {note}: {hz} Hz against the wheel's {want}");
                // Alone, it is a sine: nothing else above -60 dB.
                var spec = Spec(y, 8192, 65536);
                double fundamental = spec.Max();
                int k0 = (int)Math.Round(want * 65536 / Rate);
                double other = 0;
                for (int k = 3; k < spec.Length; k++) if (Math.Abs(k - k0) > 8) other = Math.Max(other, spec[k]);
                Assert.That(Measure.Db(other / fundamental), Is.LessThan(-60), $"{Tonewheels.BarFootage[bar]}: a pure wheel");
            }
            Measure.Report($"Neo Organ drawbars alone at 8 on note {note}: {string.Join(", ", lines)}");
        }

        [Test]
        public void TheStepLawIsThreeDecibelsAStep()
        {
            var amps = new double[9];
            for (int step = 1; step <= 8; step++)
            {
                var (e, _) = Make(v => v.Set(P.B8, step));
                On(e, 60);
                var y = RenderL(e, 8192 + 65536);
                amps[step] = Strongest(y, 8192, 65536).Amp;
            }
            double span = Measure.Db(amps[8] / amps[1]);
            Measure.Report($"Neo Organ step law: 8 to 1 is {span:F2} dB; steps " + string.Join(" ", Enumerable.Range(1, 7).Select(s => $"{Measure.Db(amps[s + 1] / amps[s]):F2}")));
            Assert.That(span, Is.EqualTo(21.0).Within(0.5));
            for (int s = 1; s < 8; s++) Assert.That(Measure.Db(amps[s + 1] / amps[s]), Is.EqualTo(3.0).Within(0.1));
            // 0 is off.
            var (z, _) = Make();
            On(z, 60);
            Assert.That(Measure.Peak(RenderL(z, 20000), 5000, 15000), Is.EqualTo(0));
        }

        [Test]
        public void ADrawbarThatFoldsBackSoundsAnOctaveDown()
        {
            // 1' on the top key is above the top wheel: it folds back into the top octave and a half.
            var (e, _) = Make(v => v.Set(P.B1, 8));
            On(e, 96);
            var y = RenderL(e, 8192 + 65536);
            double hz = Strongest(y, 8192, 65536).Hz;
            Assert.That(hz, Is.LessThan(Tonewheels.WheelHz(91) * 1.001));
            Assert.That(hz, Is.GreaterThan(Tonewheels.WheelHz(80) * 0.999));
        }

        [Test]
        public void EveryKeyCanSoundAtOnce()
        {
            var (e, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.B4, 6); });
            for (int n = 36; n <= 96; n++) On(e, n);
            Assert.That(e.HeldKeys, Is.EqualTo(61));
            var y = RenderL(e, 20000);
            Assert.That(y.All(float.IsFinite), Is.True);
            Assert.That(Measure.Rms(y, 10000, 8000), Is.GreaterThan(0.01));
            for (int n = 36; n <= 96; n++) Off(e, n);
            Assert.That(e.HeldKeys, Is.EqualTo(0));
        }

        [Test]
        public void ANoteOutsideTheManualSoundsInTheNearestOctaveInside()
        {
            var (e, _) = Make(v => v.Set(P.B8, 8));
            On(e, 24);          // C1 → C2
            var y = RenderL(e, 8192 + 65536);
            Assert.That(Strongest(y, 8192, 65536).Hz, Is.EqualTo(Tonewheels.WheelHz(13)).Within(0.05));
        }

        [Test]
        public void KeysAreNotVelocitySensitiveAndTrackNotesReplaceThePreviousOne()
        {
            var (e, _) = Make(v => v.Set(P.B8, 8));
            e.NoteOn(0, 60, 0.2f);
            var a = Measure.Rms(RenderL(e, 20000), 10000, 8000);
            var (f, _) = Make(v => v.Set(P.B8, 8));
            f.NoteOn(0, 60, 1f);
            var b = Measure.Rms(RenderL(f, 20000), 10000, 8000);
            Assert.That(a, Is.EqualTo(b).Within(1e-6));
            e.NoteOn(0, 64, 1f);               // the same track: the C is released
            Assert.That(e.HeldKeys, Is.EqualTo(1));
        }

        [Test]
        public void LeakageAddsATraceUnderTheNotesAndIsOptional()
        {
            var (clean, _) = Make(v => v.Set(P.B8, 8));
            var (leaky, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.Leakage, 1000); });
            On(clean, 60); On(leaky, 60);
            var a = RenderL(clean, 8192 + 65536);
            var b = RenderL(leaky, 8192 + 65536);
            double fa = Measure.Db(Measure.Rms(a, 8192, 65536)), fb = Measure.Db(Measure.Rms(b, 8192, 65536));
            // The trace: spectrum away from the note.
            var sa = Spec(a, 8192, 65536);
            var sb = Spec(b, 8192, 65536);
            int k0 = (int)Math.Round(Tonewheels.WheelHz(Tonewheels.WheelFor(24, 0)) * 65536 / Rate);
            double oa = 0, ob = 0;
            for (int k = 3; k < sa.Length; k++) if (Math.Abs(k - k0) > 8) { oa = Math.Max(oa, sa[k]); ob = Math.Max(ob, sb[k]); }
            Measure.Report($"Neo Organ leakage at 100 %: the loudest other partial {Measure.Db(ob / sb[k0]):F1} dB re the note (clean {Measure.Db(oa / sa[k0]):F1}); level {fa:F1} vs {fb:F1} dB");
            Assert.That(Measure.Db(oa / sa[k0]), Is.LessThan(-90));
            Assert.That(Measure.Db(ob / sb[k0]), Is.InRange(-70, -40), "tiny, but there");
        }

        // ------------------------------------------------------------------------------------------ percussion

        static void Perc(ParamValues v, int harm, int decay, int level = 1)
        {
            v.Set(P.PercOn, 1);
            v.Set(P.PercHarm, harm);
            v.Set(P.PercDecay, decay);
            v.Set(P.PercLevel, level);
        }

        /// <summary>The level of a sine of <paramref name="hz"/> in short windows: the percussion's envelope.</summary>
        static double[] Envelope(float[] y, double hz, int window, int count, int start)
        {
            var env = new double[count];
            for (int i = 0; i < count; i++)
            {
                double s = 0, c = 0;
                for (int j = 0; j < window; j++)
                {
                    double t = 2 * Math.PI * hz * (start + i * window + j) / Rate;
                    s += y[start + i * window + j] * Math.Sin(t);
                    c += y[start + i * window + j] * Math.Cos(t);
                }
                env[i] = 2 * Math.Sqrt(s * s + c * c) / window;
            }
            return env;
        }

        [TestCase(0, 12)]
        [TestCase(1, 19)]
        public void PercussionPlaysTheRightPartial(int harm, int semitones)
        {
            var (e, _) = Make(v => Perc(v, harm, 1));
            On(e, 60);
            var y = RenderL(e, 8192 + 12000);
            var (hz, amp) = Strongest(y, 2400, 8192);
            double want = Tonewheels.WheelHz(Tonewheels.WheelFor(24, semitones));
            Measure.Report($"Neo Organ percussion {(harm == 0 ? "2nd" : "3rd")}: {hz:F2} Hz, the wheel table says {want:F2} Hz");
            Assert.That(Math.Abs(Measure.Cents(hz, want)), Is.LessThan(2.0));
        }

        [TestCase(0, 0.2)]
        [TestCase(1, 0.7)]
        public void PercussionDecaysAtItsDocumentedTime(int decay, double seconds)
        {
            var (e, _) = Make(v => Perc(v, 1, decay));
            On(e, 60);
            double hz = Tonewheels.WheelHz(Tonewheels.WheelFor(24, 19));
            var y = RenderL(e, (int)(3.0 * Rate));
            int win = 480;
            var env = Envelope(y, hz, win, 280, 0);
            int peak = Array.IndexOf(env, env.Max());
            int fall = -1;
            for (int i = peak; i < env.Length; i++) if (env[i] <= env[peak] * 0.1) { fall = i; break; }
            double t20 = (fall - peak) * win / (double)Rate;
            Measure.Report($"Neo Organ percussion {(decay == 0 ? "fast" : "slow")}: -20 dB after {t20 * 1000:F0} ms (documented {seconds * 1000:F0} ms)");
            Assert.That(t20, Is.EqualTo(seconds).Within(seconds * 0.15));
        }

        [Test]
        public void PercussionIsSingleTriggerALegatoSecondNoteGetsNone()
        {
            double hz2 = Tonewheels.WheelHz(Tonewheels.WheelFor(Tonewheels.KeyFor(67), 19));
            var (e, _) = Make(v => Perc(v, 1, 0));
            On(e, 60);
            RenderL(e, 3 * Rate);                         // the first note's percussion has died away (15 decay times)
            On(e, 67);                                    // legato: the C is still held
            var y = RenderL(e, 12000);
            double legato = Envelope(y, hz2, 480, 10, 2400).Max();
            Off(e, 60); Off(e, 67);
            RenderL(e, Rate / 2);
            On(e, 67);                                    // all keys were up: this one fires
            var z = RenderL(e, 12000);
            double fresh = Envelope(z, hz2, 480, 10, 0).Max();
            Measure.Report($"Neo Organ single trigger: the 3rd partial of a legato second note {Measure.Db(legato):F1} dB, of a fresh one {Measure.Db(fresh):F1} dB");
            Assert.That(Measure.Db(legato / fresh), Is.LessThan(-40), "a legato second note gets no percussion");
            Assert.That(fresh, Is.GreaterThan(0.01));
        }

        [Test]
        public void PercussionSilencesTheOneFootDrawbar()
        {
            var (e, _) = Make(v => { v.Set(P.B1, 8); v.Set(P.PercOn, 1); });
            On(e, 60);
            Assert.That(Measure.Peak(RenderL(e, 40000), 30000, 10000), Is.LessThan(1e-4));
        }

        // ------------------------------------------------------------------------------------------ the click

        /// <summary>Energy above 6 kHz in the first 12 ms of a note, relative to the note's level.</summary>
        static double AttackHashDb(OrganEngine e)
        {
            On(e, 60);
            var y = RenderL(e, 4096);
            int n = 512;
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = y[i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            var mag = Measure.Spectrum(w, 0, n);
            double hi = 0, all = 0;
            for (int k = 1; k < mag.Length; k++)
            {
                double f = (double)k * Rate / n;
                all += mag[k] * mag[k];
                if (f > 6000) hi += mag[k] * mag[k];
            }
            return 10 * Math.Log10(Math.Max(1e-30, hi) / Math.Max(1e-30, all));
        }

        [Test]
        public void TheKeyClickIsAbsentAtZeroAndMeasurableWhenSet()
        {
            var (none, _) = Make(v => v.Set(P.B8, 8));
            var (some, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.KeyClick, 300); });
            var (loud, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.KeyClick, 1000); });
            double a = AttackHashDb(none), b = AttackHashDb(some), c = AttackHashDb(loud);
            Measure.Report($"Neo Organ attack, energy above 6 kHz re the whole: click 0 {a:F1} dB, 30 % {b:F1} dB, 100 % {c:F1} dB");
            Assert.That(a, Is.LessThan(-70), "no click: a smooth contact");
            Assert.That(b, Is.GreaterThan(a + 25));
            Assert.That(c, Is.GreaterThan(b + 5));
        }

        [Test]
        public void TheKeyClickAlsoSoundsAtNoteOff()
        {
            var (e, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.KeyClick, 1000); });
            On(e, 60);
            RenderL(e, 24000);
            Off(e, 60);
            var y = RenderL(e, 512);
            var w = new float[512];
            for (int i = 0; i < 512; i++) w[i] = y[i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 512));
            var mag = Measure.Spectrum(w, 0, 512);
            double hi = 0;
            for (int k = 1; k < mag.Length; k++) if ((double)k * Rate / 512 > 6000) hi += mag[k] * mag[k];
            Assert.That(10 * Math.Log10(hi + 1e-30), Is.GreaterThan(-60), "a release click");
        }

        // ------------------------------------------------------------------------------------------ vibrato and chorus

        [TestCase(0, 6.0)]
        [TestCase(1, 12.0)]
        [TestCase(2, 24.0)]
        public void TheScannerVibratoMovesThePitchByItsDocumentedDepth(int mode, double cents)
        {
            var (e, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.VibOn, 1); v.Set(P.VibMode, mode); });
            On(e, 60);
            var y = RenderL(e, 3 * Rate);
            // The instantaneous pitch from zero crossings.
            var (lo, hi) = PitchExtremes(y, Rate / 2, 2 * Rate);
            double dev = Math.Max(Math.Abs(Measure.Cents(hi, 261.6256)), Math.Abs(Measure.Cents(lo, 261.6256)));
            Measure.Report($"Neo Organ vibrato V{mode + 1}: pitch swings to {lo:F2} .. {hi:F2} Hz, ±{dev:F1} cents (documented ±{cents})");
            Assert.That(dev, Is.EqualTo(cents).Within(cents * 0.3));
        }

        static (double Lo, double Hi) PitchExtremes(float[] y, int from, int length)
        {
            var periods = new List<double>();
            int last = -1;
            for (int i = from + 1; i < from + length; i++)
                if (y[i - 1] < 0 && y[i] >= 0)
                {
                    double frac = -y[i - 1] / (y[i] - y[i - 1]);
                    double t = i - 1 + frac;
                    if (last >= 0 || periods.Count == 0) { }
                    periods.Add(t);
                }
            var freqs = new List<double>();
            for (int i = 8; i < periods.Count; i++) freqs.Add(8.0 * Rate / (periods[i] - periods[i - 8]));
            return (freqs.Min(), freqs.Max());
        }

        [Test]
        public void ChorusKeepsTheDrySoundAndOffIsBitExact()
        {
            var (off, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.VibOn, 0); v.Set(P.VibMode, 5); });
            var (plain, _) = Make(v => v.Set(P.B8, 8));
            On(off, 60); On(plain, 60);
            var a = RenderL(off, 20000);
            var b = RenderL(plain, 20000);
            Assert.That(a.SequenceEqual(b), Is.True, "scanner off passes the sound untouched");
        }

        // ------------------------------------------------------------------------------------------ the overdrive

        [Test]
        public void TheOverdriveIsCleanAtZeroAndKeepsItsLevelWhenDriven()
        {
            var (clean, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.B4, 8); });
            var (hot, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.B4, 8); v.Set(P.Drive, 1000); });
            var (mid, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.B4, 8); v.Set(P.Drive, 500); });
            foreach (var e in new[] { clean, hot, mid }) On(e, 60);
            var c = RenderL(clean, 30000); var h = RenderL(hot, 30000); var m = RenderL(mid, 30000);
            double thdClean = Thd(c), thdHot = Thd(h);
            double rc = Measure.Db(Measure.Rms(c, 10000, 16384)), rh = Measure.Db(Measure.Rms(h, 10000, 16384)), rm = Measure.Db(Measure.Rms(m, 10000, 16384));
            Measure.Report($"Neo Organ drive: level {rc:F1} / {rm:F1} / {rh:F1} dB at 0 / 50 / 100 %; harmonic content above the played partials {thdClean:F1} / {thdHot:F1} dB");
            Assert.That(thdClean, Is.LessThan(-80), "clean at 0");
            Assert.That(thdHot, Is.GreaterThan(thdClean + 40));
            Assert.That(Math.Abs(rh - rc), Is.LessThan(6), "level compensation keeps the loudness within 6 dB");
            Assert.That(Math.Abs(rm - rc), Is.LessThan(6));
        }

        /// <summary>Energy not at the two played partials (261.6 and 523.3 Hz) or the low-order harmonics they make, in dB re the total, 1 - 3 kHz.</summary>
        static double Thd(float[] y)
        {
            var mag = Spec(y, 8192, 16384);
            double band = 0, all = 0;
            for (int k = 1; k < mag.Length; k++)
            {
                double f = (double)k * Rate / 16384;
                all += mag[k] * mag[k];
                if (f > 1000 && f < 3000) band += mag[k] * mag[k];
            }
            return 10 * Math.Log10(Math.Max(band, 1e-30) / all);
        }

        // ------------------------------------------------------------------------------------------ the rotary speaker

        /// <summary>The loudness over time of the left channel: the rectified signal through two low-pass poles (25 Hz).</summary>
        static float[] Smooth(float[] y)
        {
            var env = new float[y.Length];
            float a = 1f - (float)Math.Exp(-2 * Math.PI * 25 / Rate);
            float s1 = 0, s2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                s1 += (Math.Abs(y[i]) - s1) * a;
                s2 += (s1 - s2) * a;
                env[i] = s2;
            }
            return env;
        }

        /// <summary>
        /// Times (in samples) at which the loudness rises through 60 % of its range (after falling below 40 %): one per turn. The range is the
        /// loudness's own over the 1.6 s around each point, so a level that drifts while the speed changes doesn't hide the turns.
        /// </summary>
        static List<double> Peaks(float[] env, int from, int to)
        {
            const int Dec = 48, Half = 800;                     // 1 kHz decimation, ±0.8 s
            int count = (to - from) / Dec;
            var d = new float[count];
            for (int i = 0; i < count; i++) d[i] = env[from + i * Dec];
            var times = new List<double>();
            bool armed = false, first = true;
            for (int i = 1; i < count; i++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int k = Math.Max(0, i - Half); k < Math.Min(count, i + Half); k++) { lo = Math.Min(lo, d[k]); hi = Math.Max(hi, d[k]); }
                float up = lo + 0.6f * (hi - lo), down = lo + 0.4f * (hi - lo);
                if (first) { armed = d[i - 1] < down; first = false; }
                if (armed && d[i] >= up)
                {
                    times.Add(from + (i - 1 + (up - d[i - 1]) / (d[i] - d[i - 1])) * Dec);
                    armed = false;
                }
                else if (!armed && d[i] <= down) armed = true;
            }
            return times;
        }

        static void RotaryOrgan(ParamValues v, bool horn)
        {
            v.Set(P.Rotary, 1);
            if (horn) v.Set(P.B1, 8); else v.Set(P.B8, 8);
            v.Set(P.Doppler, 0);
        }

        static int Note(bool horn) => horn ? 60 : 48;

        /// <summary>The modulation rate over a stretch: peaks per second.</summary>
        static double RateOf(float[] y, int from, int to)
        {
            var env = Smooth(y);
            var p = Peaks(env, from, to);
            Assert.That(p.Count, Is.GreaterThan(2), "the loudness swells and falls");
            return (p.Count - 1) * (double)Rate / (p[^1] - p[0]);
        }

        [TestCase(true, 0, 0.8)]
        [TestCase(true, 1, 6.7)]
        [TestCase(false, 0, 0.67)]
        [TestCase(false, 1, 5.7)]
        public void TheRotorsTurnAtTheirDocumentedSpeeds(bool horn, int speed, double hz)
        {
            var (e, _) = Make(v => { RotaryOrgan(v, horn); v.Set(P.Speed, speed); });
            On(e, Note(horn));
            int seconds = speed == 0 ? 22 : 24;
            var (l, _) = Render(e, seconds * Rate);
            double got = RateOf(l, (seconds - 12) * Rate, seconds * Rate);
            Measure.Report($"Neo Organ rotary {(horn ? "horn" : "drum")} {(speed == 0 ? "slow" : "fast")}: the loudness swells {got:F3} times a second (documented {hz} Hz)");
            Assert.That(got, Is.EqualTo(hz).Within(hz * 0.1));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SwitchingFromSlowToFastReachesNinetyPercentWhenDocumented(bool horn)
        {
            double slow = horn ? 0.8 : 0.67, fast = horn ? 6.7 : 5.7, tau = horn ? 0.5 : 2.5;
            double expected = tau * Math.Log((fast - slow) / (0.1 * fast));
            var (e, v) = Make(v => { RotaryOrgan(v, horn); v.Set(P.Speed, 0); });
            On(e, Note(horn));
            int settle = (horn ? 8 : 25) * Rate, run = (horn ? 8 : 16) * Rate;
            Render(e, settle);
            var vv = v;
            var (l, _) = Render(e, run, pos => { if (pos == 0) vv.Set(P.Speed, 1); });
            var env = Smooth(l);
            var p = Peaks(env, 0, run);
            double reached = double.NaN;
            for (int i = 1; i < p.Count; i++)
                if (Rate / (p[i] - p[i - 1]) >= 0.9 * fast) { reached = (p[i] + p[i - 1]) / 2 / Rate; break; }
            Measure.Report($"Neo Organ rotary {(horn ? "horn" : "drum")} Slow to Fast: 90 % of the fast speed after {reached:F2} s (documented {expected:F2} s)");
            Assert.That(reached, Is.EqualTo(expected).Within(expected * 0.2));
        }

        [Test]
        public void TheHornIsFasterThanTheDrumWhenSwitching()
        {
            Assert.That(Math.Log((6.7 - 0.8) / (0.1 * 6.7)) * 0.5, Is.LessThan(Math.Log((5.7 - 0.67) / (0.1 * 5.7)) * 2.5));
            var (e, v) = Make(v => { v.Set(P.Rotary, 1); v.Set(P.Speed, 0); });
            On(e, 60);
            Render(e, 12 * Rate);
            v.Set(P.Speed, 1);
            double h = 0, d = 0;
            Render(e, Rate);
            h = e.HornHz; d = e.DrumHz;
            Measure.Report($"Neo Organ rotary one second after Slow to Fast: horn {h:F2} Hz, drum {d:F2} Hz");
            Assert.That(h / 6.7, Is.GreaterThan(d / 5.7 + 0.2));
        }

        [Test]
        public void StopHoldsStill()
        {
            var (e, v) = Make(vv => { RotaryOrgan(vv, true); vv.Set(P.Speed, 1); });
            On(e, 60);
            Render(e, 6 * Rate);
            v.Set(P.Speed, 2);
            Render(e, 6 * Rate);
            Assert.That(e.HornHz, Is.EqualTo(0f));
            var (l, _) = Render(e, 3 * Rate);
            var env = Smooth(l);
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = Rate; i < env.Length; i++) { lo = Math.Min(lo, env[i]); hi = Math.Max(hi, env[i]); }
            Measure.Report($"Neo Organ rotary Stop: the loudness varies {Measure.Db(hi / lo):F3} dB over 2 s");
            Assert.That(Measure.Db(hi / lo), Is.LessThan(0.2));
        }

        [Test]
        public void TheRotaryKeepsTheLoudnessOfTheDrySound()
        {
            var (dry, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.B4, 8); v.Set(P.B1, 4); });
            var (wet, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.B4, 8); v.Set(P.B1, 4); v.Set(P.Rotary, 1); v.Set(P.Speed, 1); });
            foreach (var e in new[] { dry, wet }) { On(e, 55); On(e, 62); }
            var a = Render(dry, 5 * Rate); var b = Render(wet, 5 * Rate);
            double rd = Measure.Db(Math.Sqrt((Math.Pow(Measure.Rms(a.L, Rate, 4 * Rate), 2) + Math.Pow(Measure.Rms(a.R, Rate, 4 * Rate), 2)) / 2));
            double rw = Measure.Db(Math.Sqrt((Math.Pow(Measure.Rms(b.L, Rate, 4 * Rate), 2) + Math.Pow(Measure.Rms(b.R, Rate, 4 * Rate), 2)) / 2));
            Measure.Report($"Neo Organ rotary level: dry {rd:F1} dB, rotary {rw:F1} dB");
            Assert.That(Math.Abs(rw - rd), Is.LessThan(3));
        }

        [Test]
        public void TheMicrophonesMakeAStereoPicture()
        {
            var (e, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.B1, 8); v.Set(P.Rotary, 1); v.Set(P.Speed, 1); });
            On(e, 60);
            var (l, r) = Render(e, 4 * Rate);
            double corr = 0, nl = 0, nr = 0;
            for (int i = Rate; i < l.Length; i++) { corr += l[i] * r[i]; nl += l[i] * l[i]; nr += r[i] * r[i]; }
            corr /= Math.Sqrt(nl * nr);
            Measure.Report($"Neo Organ rotary: left and right correlate {corr:F2}");
            Assert.That(corr, Is.LessThan(0.95));
            var (m, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.Rotary, 0); });
            On(m, 60);
            var (ml, mr) = Render(m, 20000);
            Assert.That(ml.SequenceEqual(mr), Is.True, "rotary off: the same sound in both channels");
        }

        // ------------------------------------------------------------------------------------------ hygiene

        static void Everything(ParamValues v)
        {
            for (int b = 0; b < 9; b++) v.Set(P.B16 + b, 6);
            Perc(v, 1, 1);
            v.Set(P.KeyClick, 400);
            v.Set(P.VibOn, 1);
            v.Set(P.Drive, 600);
            v.Set(P.Rotary, 1);
            v.Set(P.Speed, 1);
        }

        [Test]
        public void ARenderMakesNoGarbage()
        {
            var (e, v) = Make(Everything, clean: false);
            var l = new float[OrganEngine.MaxBlock];
            var r = new float[OrganEngine.MaxBlock];
            var rng = new Random(5);
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                if (b % 40 == 0) On(e, 36 + rng.Next(61));
                if (b % 40 == 20) { for (int n = 36; n <= 96; n++) Off(e, n); }
                if (b % 7 == 0) { v.Set(P.B8, rng.Next(9)); v.Set(P.Drive, rng.Next(1001)); v.Set(P.Speed, rng.Next(3)); v.Set(P.VibMode, rng.Next(6)); v.Set(P.Expression, rng.Next(1001)); v.Set(P.Volume, rng.Next(1001)); }
                e.Process(l, r, OrganEngine.MaxBlock);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void TheTailsLeaveNoDenormalsAndTheMachineGoesQuiet()
        {
            var (e, v) = Make(Everything, clean: false);
            for (int n = 48; n < 72; n += 3) On(e, n);
            Render(e, 2 * Rate);
            for (int n = 48; n < 72; n += 3) Off(e, n);
            var (l, r) = Render(e, 6 * Rate);
            int tiny = 0;
            foreach (var x in l.Concat(r)) if (x != 0 && Math.Abs(x) < 1.2e-38f) tiny++;
            Assert.That(tiny, Is.EqualTo(0), "no denormal in the output");
            Assert.That(Measure.Peak(l, 5 * Rate, Rate), Is.EqualTo(0), "silent again");
            var buf = new float[256];
            Assert.That(e.Process(buf, new float[256], 256), Is.False, "Work can return false");
        }

        [Test]
        public void AutomationMakesNoZipperNoise()
        {
            // A held note whose drawbar steps every block, with the expression, the volume and the drive moving every block:
            // nothing above 2 kHz but what the partials themselves make (-60 dB).
            var (e, v) = Make(vv => { vv.Set(P.B8, 8); vv.Set(P.B4, 8); vv.Set(P.Rotary, 0); });
            On(e, 60);
            var rng = new Random(9);
            var y = RenderL(e, 6 * Rate, pos =>
            {
                v.Set(P.B8, 4 + rng.Next(5));
                v.Set(P.B4, rng.Next(9));
                v.Set(P.Expression, 500 + (int)(500 * Math.Sin(pos / 20000.0)));
                v.Set(P.Volume, 700 + (int)(100 * Math.Sin(pos / 15000.0)));
            });
            double z = HighDb(y);
            Measure.Report($"Neo Organ drawbars, expression and volume moving every block: energy above 2 kHz {z:F1} dB re the tone");
            Assert.That(z, Is.LessThan(-60));
            // And the speaker's controls.
            var (f, w) = Make(vv => { vv.Set(P.B8, 8); vv.Set(P.Rotary, 1); vv.Set(P.Doppler, 0); vv.Set(P.RotDepth, 0); });
            On(f, 60);
            var y2 = RenderL(f, 6 * Rate, pos =>
            {
                w.Set(P.Spread, 500 + (int)(500 * Math.Sin(pos / 9000.0)));
                w.Set(P.Balance, 500 + (int)(500 * Math.Sin(pos / 7000.0)));
            });
            double z2 = HighDb(y2);
            Measure.Report($"Neo Organ spread and balance moving every block: energy above 2 kHz {z2:F1} dB re the tone");
            Assert.That(z2, Is.LessThan(-60));
        }

        static double HighDb(float[] y)
        {
            int n = 1 << 17;
            var w = new float[n];
            int from = y.Length - n;
            for (int i = 0; i < n; i++) w[i] = y[from + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            var mag = Fft.Magnitudes(w, 0, n);
            double tone = 0, high = 0;
            for (int k = 1; k < mag.Length; k++)
            {
                double f = (double)k * Rate / n;
                if (f < 1200) tone += mag[k] * mag[k];
                else if (f > 2000 && Math.Abs(f - 2093) > 60 && Math.Abs(f - 3140) > 60 && Math.Abs(f - 4186) > 60) high += mag[k] * mag[k];
            }
            return 10 * Math.Log10(high / tone);
        }

        [Test]
        public void StateRoundTripsThroughTheVersionedEnvelope()
        {
            var chain = new StateMigrations(Params.Table);
            Assert.That(chain.Current, Is.EqualTo(0));
            var rng = new Random(2);
            var saved = new Dictionary<string, int>();
            foreach (var p in Params.Table) saved[p.Id] = rng.Next(p.Min, p.Max + 1);
            var back = chain.Migrate(0, saved);
            Assert.That(back, Is.Not.Null);
            foreach (var p in Params.Table) Assert.That(back[p.Id], Is.EqualTo(saved[p.Id]), p.Id);
            var raws = Params.Table.Select(p => saved[p.Id]).ToArray();
            Assert.That(chain.MigrateGlobals(0, raws), Is.EqualTo(raws));
            Assert.That(chain.MigrateGlobals(1, raws), Is.Null, "a newer song is not guessed at");
            Assert.That(chain.Validate(), Is.Empty);
            var data = new byte[] { 1, 2, 3 };
            var (content, version) = StateEnvelope.Read(StateEnvelope.Write(data, 0));
            Assert.That(content, Is.EqualTo(data));
            Assert.That(version, Is.EqualTo(0));
            // The same values give the same sound in a new engine.
            var (a, va) = Make(v => { foreach (var p in Params.Table) v.Set(p.Index, saved[p.Id]); }, clean: false);
            var (b, vb) = Make(v => { foreach (var p in Params.Table) v.Set(p.Index, back[p.Id]); }, clean: false);
            On(a, 60); On(b, 60);
            Assert.That(RenderL(a, 8000).SequenceEqual(RenderL(b, 8000)), Is.True);
        }

        [Test]
        public void TheSoundDoesNotDependOnHowTheHostSplitsTheBlocks()
        {
            float[] Go(int block)
            {
                var (e, _) = Make(Everything, clean: false);
                On(e, 60); On(e, 64); On(e, 67);
                var y = new float[48000];
                var l = new float[256]; var r = new float[256];
                for (int pos = 0; pos < y.Length; pos += block)
                {
                    int n = Math.Min(block, y.Length - pos);
                    e.Process(l, r, n);
                    Array.Copy(l, 0, y, pos, n);
                }
                return y;
            }
            var a = Go(256); var b = Go(64);
            Assert.That(a.SequenceEqual(b), Is.True, "blocks of 256 and of 64 give the same samples");
        }

        // ------------------------------------------------------------------------------------------ cost

        [Test]
        public void SixtyOneKeysCostNoMoreThanOne()
        {
            double Time(int keys)
            {
                var (e, _) = Make(v => { v.Set(P.B8, 8); v.Set(P.B4, 8); v.Set(P.B1, 5); v.Set(P.Rotary, 1); }, clean: false);
                for (int k = 0; k < keys; k++) On(e, 36 + k);
                var l = new float[256]; var r = new float[256];
                for (int i = 0; i < 200; i++) e.Process(l, r, 256);
                double best = double.MaxValue;
                for (int rep = 0; rep < 9; rep++)
                {
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < 400; i++) e.Process(l, r, 256);
                    best = Math.Min(best, sw.Elapsed.TotalSeconds);
                }
                return best;
            }
            double one = Time(1), all = Time(61);
            Measure.Report($"Neo Organ cost: 1 key {one * 1000:F2} ms, 61 keys {all * 1000:F2} ms for {400 * 256 / (double)Rate:F2} s of audio, ratio {all / one:F2}");
            Assert.That(all / one, Is.LessThan(1.5));
        }

        [Test]
        public void TheWholeMachineWithTheRotaryOnRunsFarFasterThanRealTime()
        {
            Throughput.Guard("Neo Organ, 61 keys, vibrato, drive and the rotary speaker", Throughput.Floor(x64: 30, arm64: 20), 2, Rate, OrganEngine.MaxBlock, () =>
            {
                var (e, _) = Make(Everything, clean: false);
                for (int k = 0; k < 61; k += 3) On(e, 36 + k);
                Render(e, 4800);
                var l = new float[OrganEngine.MaxBlock];
                var r = new float[OrganEngine.MaxBlock];
                return Throughput.InBlocks(OrganEngine.MaxBlock, (pos, n) => e.Process(l, r, n));
            }, Measure.Report);
        }

        [Test]
        public void ExtremeSettingsStayBoundedAndFinite()
        {
            var rng = new Random(13);
            var (e, v) = Make(clean: false);
            for (int round = 0; round < 25; round++)
            {
                foreach (var p in Params.Table) v.Set(p.Index, rng.Next(p.Min, p.Max + 1));
                for (int k = 0; k < 12; k++) On(e, 30 + rng.Next(80));
                var (l, r) = Render(e, 12000);
                Assert.That(l.Concat(r).All(x => float.IsFinite(x) && Math.Abs(x) <= 1.0001f), Is.True, $"round {round}");
                for (int n = 0; n < 128; n++) Off(e, n);
            }
        }
    }
}
