using NeoBuzz.Amp;
using NeoBuzz.Dsp;
using NeoBuzz.TestSupport;
using static NeoBuzz.MachineTests.AmpRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Amp (docs/specs/neo-amp.md): the tone stack, the aliasing, the tuner, the gate, the switching and the other gates of the brief (AMP1), measured.</summary>
    [TestFixture]
    public class NeoAmpTests
    {
        // ================================================================================== gate: the tone stack

        static readonly (double Bass, double Mid, double Treble)[] Knobs =
        {
            (0.5, 0.5, 0.5), (0.0, 0.5, 1.0), (1.0, 0.0, 0.0), (0.2, 1.0, 0.3), (0.8, 0.3, 0.8),
        };

        [Test]
        public void TheToneStackFollowsTheAnalyticNetworkWithin05dBFrom20HzTo10kHzAtFiveSettings()
        {
            double worstAll = 0;
            foreach (int q in new[] { 0, 1, 2 })
            {
                double worstQ = 0;
                foreach (var (b, m, t) in Knobs)
                {
                    var v = Values(s => { Voicing(s, 3); Cabinet(s, 0); s.Set(P.Quality, q); Pct(s, P.Bass, b * 100); Pct(s, P.Mid, m * 100); Pct(s, P.Treble, t * 100); });
                    var e = Engine(v);
                    Run(e, Measure.PinkNoise(Rate, 0.05));      // the knobs' smoothing has settled
                    var core = e.ActiveCore;
                    double osRate = Rate * (2 << q);
                    Assert.That(core.Factor, Is.EqualTo(2 << q));
                    var caps = NeoBuzz.Amp.Voicing.All[3].CapScale;
                    double worst = 0, atHz = 0;
                    for (int i = 0; i < 120; i++)
                    {
                        double hz = 20 * Math.Pow(500.0, i / 119.0);   // 20 Hz … 10 kHz
                        double got = core.ToneFilter.MagnitudeDb(hz, osRate);
                        double want = ToneNetwork.ResponseDb(hz, b, m, t, caps);
                        if (Math.Abs(got - want) > worst) { worst = Math.Abs(got - want); atHz = hz; }
                    }
                    Assert.That(worst, Is.LessThan(0.5), $"{2 << q}x, bass {b} mid {m} treble {t}: worst at {atHz:0} Hz");
                    worstQ = Math.Max(worstQ, worst);
                }
                Measure.Report($"Neo Amp tone stack at {2 << q}x: the digital response is within {worstQ:0.000} dB of the analytic network over 20 Hz - 10 kHz at 5 settings");
                worstAll = Math.Max(worstAll, worstQ);
            }
            Assert.That(worstAll, Is.LessThan(0.5));
        }

        [Test]
        public void TheKnobsMoveTheToneStackTheWayTheirNamesSay()
        {
            double Db(double b, double m, double t, double hz) => ToneNetwork.ResponseDb(hz, b, m, t, NeoBuzz.Amp.Voicing.All[3].CapScale);
            Assert.That(Db(1, 0.5, 0.5, 100) - Db(0, 0.5, 0.5, 100), Is.GreaterThan(6), "Bass raises the lows");
            Assert.That(Db(0.5, 0.5, 1, 4000) - Db(0.5, 0.5, 0, 4000), Is.GreaterThan(6), "Treble raises the highs");
            Assert.That(Db(0.5, 1, 0.5, 700) - Db(0.5, 0, 0.5, 700), Is.GreaterThan(4), "Mid raises the middle");
        }

        // ================================================================================== gate: aliasing

        [Test]
        public void AFullChainSineOfHighGainHasEveryAliasedComponentAtLeast70dBUnderTheFundamentalAt4x()
        {
            const int Bin = 3413;          // 2.5 kHz on an exact bin
            double hz = Measure.BinFrequency(Bin);
            var floor = new Dictionary<(int, double), double>();
            foreach (int q in new[] { 0, 1, 2 })
                foreach (double amp in new[] { 0.25, 1.0 })
                {
                    var v = Values(s => { Voicing(s, 5); Cabinet(s, 0); s.Set(P.Quality, q); Set(s, P.Gain, 40); });
                    var e = Engine(v);
                    var y = Run(e, Sine(hz, amp, 65536 * 2 + 8000));
                    var mag = Measure.Spectrum(y, 70000, 65536);
                    double db = Measure.AliasFloorDb(mag, Bin);
                    floor[(q, amp)] = db;
                    Measure.Report($"Neo Amp aliasing, High gain at Gain 40 dB, {hz:0} Hz sine at {Db(amp):0.0} dBFS, {2 << q}x: every non-harmonic component is {-db:0.0} dB under the fundamental");
                }
            Assert.That(floor[(1, 0.25)], Is.LessThan(-70), "4x, a DI at -12 dBFS");
            Assert.That(floor[(1, 1.0)], Is.LessThan(-70), "4x, a full-scale DI");
            Assert.That(floor[(2, 0.25)], Is.LessThan(-70), "8x");
            Assert.That(floor[(2, 1.0)], Is.LessThan(-70), "8x, full scale");
            Assert.That(floor[(0, 0.25)], Is.LessThan(-60), "2x is the draft setting: reported, and not worse than -60 dB");
            Assert.That(floor[(1, 0.25)], Is.LessThan(floor[(0, 0.25)] - 6), "4x is cleaner than 2x");
        }

        // ================================================================================== gate: the tuner

        static (double FirstOk, double Worst, int Missed, string Why) TuneOne(double hz, double centsOff, double tolerance, int reference = 25)
        {
            double f = hz * Math.Pow(2, centsOff / 1200);
            var v = Values(s => { s.Set(P.Tuner, 1); s.Set(P.Reference, reference); });
            var e = Engine(v);
            var x = AmpSources.Pluck(f, 3.0, 0.25, 7);
            var l = new float[Block];
            var r = new float[Block];
            double firstOk = -1, worst = 0;
            int missed = 0;
            string why = "";
            for (int p = 0; p + Block <= x.Length; p += Block)
            {
                Array.Copy(x, p, l, 0, Block); Array.Copy(x, p, r, 0, Block);
                e.Process(l, r, Block);
                double t = (p + Block) / (double)Rate;
                var rd = e.Tuner;
                double pk = 0;
                for (int i = 0; i < Block; i++) pk = Math.Max(pk, Math.Abs(x[p + i]));
                if (rd.Valid)
                {
                    double cents = 1200 * Math.Log2(rd.Hz / f);
                    if (firstOk < 0 && Math.Abs(cents) < tolerance) firstOk = t;
                    if (t >= 0.2) worst = Math.Max(worst, Math.Abs(cents));
                }
                // While the string is still above -65 dBFS the tuner holds the note.
                else if (t >= 0.2 && pk > 0.00056) { missed++; why = $"no reading at {t:0.00} s with the string at {Db(pk):0} dBFS"; }
            }
            return (firstOk, worst, missed, why);
        }

        [Test]
        public void TheTunerReadsAPluckedStringWithin1CentFromE1ToE6AndWithin2CentsOnLowBInUnder200ms()
        {
            foreach (double hz in new[] { 41.203, 55.0, 82.407, 110.0, 164.814, 246.942, 329.628, 440.0, 659.255, 987.767, 1318.51 })
            {
                var (first, worst, missed, why) = TuneOne(hz, 0, 1.0);
                Measure.Report($"Neo Amp tuner {hz:0.0} Hz: within 1 cent after {first * 1000:0} ms, worst error after 200 ms {worst:0.00} cents");
                Assert.That(first, Is.InRange(0.0, 0.2), $"{hz} Hz: settled within 200 ms of the pluck");
                Assert.That(worst, Is.LessThan(1.0), $"{hz} Hz");
                Assert.That(missed, Is.EqualTo(0), why);
            }
            // The 5-string bass's low B (30.87 Hz): 2 cents.
            var low = TuneOne(30.868, 0, 2.0);
            Measure.Report($"Neo Amp tuner 30.9 Hz (low B): within 2 cents after {low.FirstOk * 1000:0} ms, worst error after 200 ms {low.Worst:0.00} cents");
            Assert.That(low.FirstOk, Is.InRange(0.0, 0.2));
            Assert.That(low.Worst, Is.LessThan(2.0));
            Assert.That(low.Missed, Is.EqualTo(0), low.Why);
        }

        [Test]
        public void TheTunerShowsTheNoteTheCentsAndFollowsTheReferencePitch()
        {
            // 110 Hz is A2; 25 cents sharp reads +25.
            var v = Values(s => s.Set(P.Tuner, 1));
            var e = Engine(v);
            var x = AmpSources.Pluck(110.0 * Math.Pow(2, 25 / 1200.0), 1.0, 0.25, 3);
            Run(e, x);
            var r = e.Tuner;
            Assert.That(r.Valid, Is.True);
            Assert.That(r.NoteName, Is.EqualTo("A2"));
            Assert.That(r.Cents, Is.EqualTo(25).Within(1.0));
            Assert.That(r.Hz, Is.EqualTo(110.0 * Math.Pow(2, 25 / 1200.0)).Within(0.1));
            // E1 (41.2 Hz) at 440 Hz.
            var e1 = Engine(Values(s => s.Set(P.Tuner, 1)));
            Run(e1, AmpSources.Pluck(41.2034, 1.0, 0.25, 5));
            Assert.That(e1.Tuner.NoteName, Is.EqualTo("E1"));
            // With the reference at 432 Hz (raw 17) an A of 432 Hz is in tune and a 440 Hz one is 31.8 cents sharp.
            var ref432 = Engine(Values(s => { s.Set(P.Tuner, 1); s.Set(P.Reference, 17); }));
            Run(ref432, AmpSources.Pluck(432.0, 1.0, 0.25, 9));
            Assert.That(ref432.Tuner.NoteName, Is.EqualTo("A4"));
            Assert.That(ref432.Tuner.Cents, Is.EqualTo(0).Within(1.0));
            var ref432b = Engine(Values(s => { s.Set(P.Tuner, 1); s.Set(P.Reference, 17); }));
            Run(ref432b, AmpSources.Pluck(440.0, 1.0, 0.25, 9));
            Assert.That(ref432b.Tuner.Cents, Is.EqualTo(1200 * Math.Log2(440.0 / 432.0)).Within(1.0));
            // The ends of the reference's range.
            Assert.That(Params.Table[P.Reference].Min, Is.EqualTo(0));
            Assert.That(Params.Table[P.Reference].Max, Is.EqualTo(51));
        }

        [Test]
        public void TheTunerShowsNothingOnSilenceAndClearsWhenTheNoteIsGone()
        {
            var e = Engine(Values(s => s.Set(P.Tuner, 1)));
            var silence = new float[Rate];
            var l = new float[Block]; var r = new float[Block];
            for (int p = 0; p + Block <= silence.Length; p += Block) { e.Process(l, r, Block); Assert.That(e.Tuner.Valid, Is.False, $"silence at {p}"); }
            // Noise far below the tracker's gate: nothing either.
            var hiss = Measure.PinkNoise(Rate, 0.00005);
            Run(e, hiss);
            Assert.That(e.Tuner.Valid, Is.False, "hiss at -86 dBFS");
            // A note, then silence: cleared 150 ms after it falls under -70 dBFS (and long before the next second).
            var x = AmpSources.Pluck(196.0, 1.0, 0.25, 11, 0.3);
            Run(e, x);
            var tail = new float[2 * Rate];
            Run(e, tail);
            Assert.That(e.Tuner.Valid, Is.False, "2 s after the note");
        }

        [Test]
        public void TheTunerMutesTheOutputWhileItIsOnAndLetsTheSoundBackWhenItIsOff()
        {
            var x = AmpSources.Chords(4.0, 0.25);
            var v = Values(s => { Cabinet(s, 3); });
            var e = Engine(v);
            var y = Run(e, x, Block, b =>
            {
                if (b == Rate / Block) v.Set(P.Tuner, 1);
                if (b == 2 * Rate / Block) v.Set(P.Tuner, 0);
            });
            Assert.That(Rms(y, 0, Rate / 2), Is.GreaterThan(0.01), "playing before");
            // 10 ms of fade, then silent while the tuner is on.
            Assert.That(Rms(y, Rate + 1000, Rate / 2), Is.EqualTo(0).Within(1e-9), "muted while the tuner is on");
            Assert.That(Rms(y, 3 * Rate, Rate / 2), Is.GreaterThan(0.01), "playing again");
            Assert.That(ClickDetector.Find(y).Count, Is.LessThanOrEqualTo(ClickDetector.Find(Run(Engine(Values(s => Cabinet(s, 3))), x)).Count), "the mute is a 10 ms ramp: no clicks");
        }

        // ================================================================================== gate: the noise gate

        [Test]
        public void TheGateIsSilentUnderTheThresholdOpensForANoteAndDoesNotClick()
        {
            var hiss = Measure.PinkNoise(3 * Rate, 0.0003);     // -70 dBFS RMS: its peaks are under the -50 dB threshold
            var open = Values(s => { Voicing(s, 5); Cabinet(s, 3); Set(s, P.Gain, 30); });
            var closed = Values(s => { Voicing(s, 5); Cabinet(s, 3); Set(s, P.Gain, 30); Set(s, P.GateThresh, -50); });
            var yo = Run(Engine(open), hiss);
            var yc = Run(Engine(closed), hiss);
            double peakOpen = 0, peakClosed = 0;
            for (int i = 2 * Rate; i < yo.Length; i++) { peakOpen = Math.Max(peakOpen, Math.Abs(yo[i])); peakClosed = Math.Max(peakClosed, Math.Abs(yc[i])); }
            Measure.Report($"Neo Amp gate at -50 dB on hiss at -70 dBFS RMS: output peak {Db(peakClosed):0.0} dBFS against {Db(peakOpen):0.0} dBFS with the gate off");
            Assert.That(peakOpen, Is.GreaterThan(0.01), "without the gate the hiss is amplified");
            Assert.That(peakClosed, Is.LessThan(1e-6), "with it: silent");

            // A note at -26 dBFS (peak) after the hiss opens the gate, and its level is what it is without the gate.
            var note = AmpSources.Pluck(196.0, 1.5, 0.05, 4, 2.0);
            var src = new float[hiss.Length];
            Array.Copy(hiss, src, hiss.Length);
            for (int i = 0; i < note.Length; i++) src[Rate + i] += note[i];
            var go = Run(Engine(open), src);
            var gc = Run(Engine(closed), src);
            double a = Rms(go, Rate + 2400, Rate / 2), b = Rms(gc, Rate + 2400, Rate / 2);
            Assert.That(Db(b) - Db(a), Is.InRange(-0.7, 0.7), "the gate has opened for the note");
            Assert.That(ClickDetector.Find(gc).Count, Is.LessThanOrEqualTo(ClickDetector.Find(go).Count), "0 clicks from the gate");
        }

        [Test]
        public void TheGateDoesNotChatterOnANoteDecayingThroughTheThreshold()
        {
            // 220 Hz falling 40 dB in 4 s from -26 dBFS through the threshold at -46 dB, with a 7 Hz wobble of +-20 % on top (a hand's vibrato of level).
            int n = 5 * Rate;
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double env = 0.05 * Math.Pow(10, -2.0 * t / 4.0 * 1.0 - 0.0) ;   // 0.05 → 0.0005 over 4 s
                x[i] = (float)(env * (1 + 0.2 * Math.Sin(2 * Math.PI * 7 * t)) * Math.Sin(2 * Math.PI * 220 * t));
            }
            var v = Values(s => { Voicing(s, 3); Cabinet(s, 0); Set(s, P.GateThresh, -46); Set(s, P.GateRelease, 80); });
            var y = Run(Engine(v), x);
            // The state of the output in 5 ms windows: sound, or exact silence.
            int win = 240;
            int changes = 0; bool? last = null; int firstSilent = -1;
            for (int p = 0; p + win <= y.Length; p += win)
            {
                bool silent = true;
                for (int i = p; i < p + win; i++) if (Math.Abs(y[i]) > 1e-6f) { silent = false; break; }
                if (last != null && last != silent) changes++;
                if (silent && firstSilent < 0) firstSilent = p;
                last = silent;
            }
            Measure.Report($"Neo Amp gate on a 220 Hz note decaying 40 dB through the threshold with a 7 Hz wobble: output closed for good at {firstSilent / (double)Rate:0.00} s, {changes} changes between sound and silence");
            Assert.That(changes, Is.EqualTo(1), "it closes once (the 6 dB of hysteresis keeps the wobble from opening it again)");
            Assert.That(firstSilent, Is.GreaterThan(Rate), "not before the note is near the threshold");
            Assert.That(ClickDetector.Find(y).Count, Is.EqualTo(0), "0 clicks");
        }

        // ================================================================================== gate: switching

        static FakeWaves SongWaves()
        {
            var w = new FakeWaves();
            w.Put(1, "short ir", Burst(0.08, 0.04), null);
            return w;
        }

        [Test]
        public void ChangingTheVoicingTheQualityOrTheCabinetWhilePlayingClicksNothing()
        {
            var x = AmpSources.Chords(8.0, 0.25);
            var waves = SongWaves();
            var v = Values(s => { Voicing(s, 0); Cabinet(s, 1); });
            var e = Engine(v, waves);
            var steps = new Action<ParamValues>[]
            {
                s => Voicing(s, 2), s => Cabinet(s, 3), s => Voicing(s, 5), s => s.Set(P.Quality, 2), s => Cabinet(s, 7), s => Voicing(s, 7),
                s => Cabinet(s, 0), s => Voicing(s, 1), s => s.Set(P.Quality, 0), s => Cabinet(s, 6), s => Voicing(s, 4), s => Cabinet(s, 2),
            };
            int per = Rate * 8 / steps.Length / Block;
            int swaps = 0;
            var y = Run(e, x, Block, b =>
            {
                if (b % per == per / 4 && b / per < steps.Length) { steps[b / per](v); swaps++; }
                if (b % per == per / 4 + 1) e.WaitForBuilder();
            });
            // The same chords through each setting held still: the clicks the playing itself makes (the plucks of a distorted amp).
            int steady = 0;
            foreach (int vo in new[] { 0, 2, 5, 7, 1, 4 })
                foreach (int cab in new[] { 0, 1, 3, 6, 2 })
                {
                    var f = Engine(Values(s => { Voicing(s, vo); Cabinet(s, cab); }));
                    steady = Math.Max(steady, ClickDetector.Find(Run(f, x)).Count);
                }
            int clicks = ClickDetector.Find(y).Count;
            Measure.Report($"Neo Amp: {swaps} changes of voicing, quality and cabinet under a chord strum: {clicks} clicks in the output (the worst setting held still has {steady})");
            Assert.That(swaps, Is.GreaterThanOrEqualTo(12));
            Assert.That(clicks, Is.LessThanOrEqualTo(steady), "0 new clicks");
            Assert.That(e.CurrentCab?.Name, Is.EqualTo(AmpCabs.Names[2]));
            // The crossfade is 30 ms: after it the output is the new setting's alone.
            Assert.That(AmpEngine.FadeSeconds, Is.EqualTo(0.03));
        }

        [Test]
        public void AfterTheFadeTheOutputIsTheNewSettingsAloneAndInsideItTheOldOneIsStillThere()
        {
            var x = Measure.PinkNoise(3 * Rate, 0.05);
            var v = Values(s => { Voicing(s, 0); Cabinet(s, 0); });
            var e = Engine(v);
            int at = Rate / Block;
            var y = Run(e, x, Block, b => { if (b == at) Voicing(v, 4); });
            // A fresh engine on the new voicing with the same history of input (its core's states depend on the input, so
            // compare a stretch long after the fade, where the memory of the old core is gone).
            var f = Engine(Values(s => { Voicing(s, 4); Cabinet(s, 0); }));
            var z = Run(f, x);
            int fade = (int)Math.Round(AmpEngine.FadeSeconds * Rate);
            Assert.That(fade, Is.EqualTo(1440));
            int from = at * Block + fade + 2 * Block + Rate / 2;
            double err = 0, sig = 0;
            for (int i = from; i < from + Rate; i++) { err += Math.Pow(y[i] - z[i], 2); sig += z[i] * (double)z[i]; }
            Assert.That(10 * Math.Log10(err / sig + 1e-30), Is.LessThan(-60), "settled: the same sound as an engine that was on the voicing all along");
            double inside = 0;
            for (int i = at * Block; i < at * Block + fade; i++) inside = Math.Max(inside, Math.Abs(y[i] - z[i]));
            Assert.That(inside, Is.GreaterThan(1e-3), "inside the fade the old voicing is still there");
        }

        [Test]
        public void SwitchingVoicingQualityOrCabinetWhilePlayingAllocatesNothingOnTheAudioThread()
        {
            var waves = SongWaves();
            var v = Values(s => { Voicing(s, 0); Cabinet(s, 1); });
            var e = Engine(v, waves);
            var x = AmpSources.Chords(4.0, 0.25);
            var l = new float[Block];
            var r = new float[Block];
            var steps = new Action<ParamValues>[]
            {
                s => Voicing(s, 5), s => Cabinet(s, 3), s => s.Set(P.Quality, 2), s => { Cabinet(s, 7); WaveSlot(s, 1); }, s => Voicing(s, 7),
                s => { Cabinet(s, 7); WaveSlot(s, 99); }, s => Cabinet(s, 0), s => { Voicing(s, 2); s.Set(P.Quality, 0); }, s => Cabinet(s, 6), s => Voicing(s, 3),
            };
            var plan = new int[steps.Length][];
            var scratch = Values();
            for (int i = 0; i < P.Count; i++) scratch.Set(i, v[i]);
            for (int k = 0; k < steps.Length; k++)
            {
                steps[k](scratch);
                plan[k] = Enumerable.Range(0, P.Count).Select(i => scratch[i]).ToArray();
            }
            int swaps = 0;
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                if (b % 100 == 20) { var raw = plan[(b / 100) % plan.Length]; for (int i = 0; i < raw.Length; i++) v.Set(i, raw[i]); }
                if (b % 100 == 22) { e.WaitForBuilder(); swaps++; }
                int pos = (b % 700) * Block;
                Array.Copy(x, pos, l, 0, Block);
                Array.Copy(x, pos, r, 0, Block);
                e.Process(l, r, Block);
            });
            Measure.Report($"Neo Amp: {swaps} switches over the measured blocks, {bytes} bytes allocated on the audio thread");
            Assert.That(swaps, Is.GreaterThanOrEqualTo(18));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        // ================================================================================== gate: latency

        [Test]
        public void TheLatencyIsTheOversamplersAtEveryQualityTheDryIsDelayedByIt()
        {
            Assert.That(AmpEngine.Latency, Is.EqualTo(FastOversampler.MaxLatency));
            Assert.That(AmpEngine.Latency, Is.GreaterThan(0));
            var x = Measure.PinkNoise(Rate, 0.1);
            var xr = Measure.PinkNoise(Rate, 0.1, 5);
            // Mix 0: the input delayed by the reported latency, bit for bit, on both sides, at every quality and block size.
            foreach (int q in new[] { 0, 1, 2 })
                foreach (int block in new[] { 256, 64, 37 })
                {
                    var v = Values(s => { s.Set(P.Mix, 0); s.Set(P.Quality, q); });
                    var (yl, yr) = Run(Engine(v), x, xr, block);
                    for (int i = AmpEngine.Latency; i < x.Length; i++)
                    {
                        if (yl[i] != x[i - AmpEngine.Latency] || yr[i] != xr[i - AmpEngine.Latency]) Assert.Fail($"quality {q}, blocks of {block}: sample {i} is not the input delayed by {AmpEngine.Latency}");
                    }
                }
            // The wet path, in its linear range (Gain 0, Master 0, a quiet pink noise), lags the input by the latency plus the amp's own filters' delay in its band:
            // the same at every quality (the pad makes up what the oversampling's filters differ by), and for the clean voicings the reported latency to within 6 samples.
            foreach (int vo in new[] { 0, 1, 2, 3, 6 })
            {
                var lags = new List<int>();
                foreach (int q in new[] { 0, 1, 2 })
                {
                    var v = Values(s => { Voicing(s, vo); Cabinet(s, 0); s.Set(P.Quality, q); Set(s, P.Gain, 0); Set(s, P.Master, 0); });
                    var n = Measure.PinkNoise(Rate, 0.01);
                    var bp1 = new Biquad(); var bp2 = new Biquad();
                    bp1.Set(BiquadType.BandPass, 1000, Rate, 1.0); bp2.Set(BiquadType.BandPass, 1000, Rate, 1.0);
                    for (int i = 0; i < n.Length; i++) n[i] = (float)bp2.Process(bp1.Process(n[i]));
                    double pk = 0; foreach (float f in n) pk = Math.Max(pk, Math.Abs(f));
                    for (int i = 0; i < n.Length; i++) n[i] *= (float)(0.002 / pk);
                    var y = Run(Engine(v), n);
                    double best = double.NegativeInfinity; int bestLag = 0;
                    for (int lag = 0; lag < 200; lag++)
                    {
                        double c = 0;
                        for (int i = 4000; i < 4000 + 32768; i++) c += n[i - lag] * (double)y[i];
                        if (c > best) { best = c; bestLag = lag; }
                    }
                    lags.Add(bestLag);
                }
                Measure.Report($"Neo Amp wet path delay, {NeoBuzz.Amp.Voicing.All[vo].Name}, linear range around 1 kHz, 2x/4x/8x: {string.Join(" / ", lags)} samples against the reported {AmpEngine.Latency}");
                Assert.That(lags.Max() - lags.Min(), Is.LessThanOrEqualTo(2), $"{vo}: the same at every quality");
                // The two clean voicings have little phase of their own around 1 kHz; the others' tone stack and bright filter lead or lag by tens of samples there (group delay of their notches).
                if (vo <= 1) Assert.That(lags[1], Is.InRange(AmpEngine.Latency - 6, AmpEngine.Latency + 6), $"{vo}: the reported latency, to 6 samples (0.125 ms) around 1 kHz");
            }
        }

        // ================================================================================== gate: Mix and Amount

        static float[] Material() => AmpSources.Chords(3.0, 0.25);

        [Test]
        public void AmountZeroAndMixZeroAreTheInputDelayedByTheLatencyBitForBit()
        {
            var x = Material();
            var xr = Measure.PinkNoise(x.Length, 0.05, 3);
            foreach (var (name, setup) in new (string, Action<ParamValues>)[]
            {
                ("Amount 0 %", s => s.Set(P.Amount, 0)),
                ("Mix 0 %", s => s.Set(P.Mix, 0)),
            })
            {
                var v = Values(s => { Voicing(s, 5); Cabinet(s, 3); Set(s, P.Gain, 35); setup(s); });
                var (yl, yr) = Run(Engine(v), x, xr);
                for (int i = AmpEngine.Latency; i < x.Length; i++)
                    if (yl[i] != x[i - AmpEngine.Latency] || yr[i] != xr[i - AmpEngine.Latency]) Assert.Fail($"{name}: sample {i}");
            }
            // Brought to 0 during playback: settled, it is the delayed input bit for bit again.
            var w = Values(s => { Voicing(s, 4); Cabinet(s, 3); });
            var (al, _) = Run(Engine(w), x, (float[])x.Clone(), Block, b => { if (b == 40) w.Set(P.Amount, 0); });
            for (int i = (40 + 40) * Block; i < x.Length; i++) Assert.That(al[i], Is.EqualTo(x[i - AmpEngine.Latency]), $"sample {i} after Amount went to 0");
        }

        [Test]
        public void MixBlendsTheDelayedDryWithTheAmpAndAmountScalesTheGainInDecibelsAndTheMaster()
        {
            var x = Material();
            var wet = Run(Engine(Values(s => { Voicing(s, 3); Cabinet(s, 3); })), x);
            // Mix 30 %: 0.7 of the delayed dry and 0.3 of the amp.
            var mix = Run(Engine(Values(s => { Voicing(s, 3); Cabinet(s, 3); Set(s, P.Mix, 0.3); })), x);
            for (int i = Rate / 4; i < x.Length; i += 11)
                Assert.That(mix[i], Is.EqualTo(0.7f * x[i - AmpEngine.Latency] + 0.3f * wet[i]).Within(2e-6), $"sample {i}");
            // Amount 200 %: twice the Gain in dB and twice the Master. The same bits as setting them to those values.
            foreach (var (pct, k) in new[] { (200.0, 2.0), (50.0, 0.5), (150.0, 1.5) })
            {
                var a = Run(Engine(Values(s => { Voicing(s, 3); Cabinet(s, 3); Set(s, P.Gain, 12); Pct(s, P.Master, 30); Set(s, P.Amount, pct); })), x);
                var b = Run(Engine(Values(s => { Voicing(s, 3); Cabinet(s, 3); Set(s, P.Gain, 12 * k); Pct(s, P.Master, 30 * k); })), x);
                double err = 0, sig = 0;
                for (int i = 0; i < x.Length; i++) { err += Math.Pow(a[i] - b[i], 2); sig += b[i] * (double)b[i]; }
                Assert.That(10 * Math.Log10(err / sig + 1e-30), Is.LessThan(-90), $"Amount {pct} % is Gain and Master at {k} times");
            }
            // Output scales the whole output.
            var o = Run(Engine(Values(s => { Voicing(s, 3); Cabinet(s, 3); Set(s, P.Output, -6); })), x);
            double g = Math.Pow(10, -6 / 20.0);
            for (int i = 0; i < x.Length; i += 13) Assert.That(o[i], Is.EqualTo((float)(g * wet[i])).Within(3e-6));
        }

        [Test]
        public void AmountDrivesTheAmpHarderAsItGrowsAndAutomatingMixOrAmountDoesNotClick()
        {
            var x = Material();
            var crests = new List<double>();
            foreach (double pct in new[] { 10.0, 50, 100, 150, 200 })
            {
                var y = Run(Engine(Values(s => { Voicing(s, 2); Cabinet(s, 3); Set(s, P.Gain, 15); Pct(s, P.Master, 40); Set(s, P.Amount, pct); })), x);
                Assert.That(y.All(v => float.IsFinite(v) && Math.Abs(v) < 8), Is.True, "finite and bounded");
                // The crest factor falls as the amp compresses: the peak against the RMS.
                crests.Add(Db(Measure.Peak(y, 12000, y.Length - 12000) / Measure.Rms(y, 12000, y.Length - 12000)));
            }
            Measure.Report($"Neo Amp crest factor of the chords at Amount 10/50/100/150/200 %: {string.Join(" / ", crests.Select(c => c.ToString("0.0")))} dB");
            Assert.That(crests[^1], Is.LessThan(crests[0] - 1.0), "200 % squashes the peaks more than 10 % does");
            // Automation: Amount and Mix swept every block under the chords, no new clicks.
            var v2 = Values(s => { Voicing(s, 3); Cabinet(s, 3); });
            var e = Engine(v2);
            var steady = ClickDetector.Find(Run(Engine(Values(s => { Voicing(s, 3); Cabinet(s, 3); })), x)).Count;
            var y2 = Run(e, x, Block, b =>
            {
                v2.Set(P.Amount, (int)(1000 + 1000 * Math.Sin(b * 0.05)) / 2 * 2);
                v2.Set(P.Mix, (int)(500 + 500 * Math.Sin(b * 0.021)));
                if (b % 190 == 0) v2.Set(P.Amount, 0);
            });
            Assert.That(ClickDetector.Find(y2).Count, Is.LessThanOrEqualTo(steady), "0 new clicks while Mix and Amount move (including through 0 %)");
        }

        // ================================================================================== gate: the audio thread

        [Test]
        public void NothingIsAllocatedPerBlockOver2000Blocks()
        {
            var v = Values(s => { Voicing(s, 5); Cabinet(s, 3); Set(s, P.Gain, 30); });
            var e = Engine(v);
            var x = AmpSources.Chords(4.0, 0.25);
            var l = new float[Block];
            var r = new float[Block];
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                int pos = (b % 700) * Block;
                Array.Copy(x, pos, l, 0, Block);
                Array.Copy(x, pos, r, 0, Block);
                // Every continuous control moves every block (they are live, not a new cabinet).
                v.Set(P.Mix, 700 + (b * 7) % 300);
                v.Set(P.Amount, 900 + (b * 3) % 400);
                v.Set(P.Output, 450 + (b % 100));
                v.Set(P.Gain, 400 + (b * 11) % 400);
                v.Set(P.Bass, 300 + (b * 5) % 400);
                v.Set(P.Mid, 300 + (b * 7) % 400);
                v.Set(P.Treble, 300 + (b * 3) % 400);
                v.Set(P.Master, 400 + (b * 13) % 400);
                v.Set(P.MicPos, (b * 9) % 1000);
                v.Set(P.LowCut, (b * 3) % 400);
                v.Set(P.HighCut, 600 + (b * 5) % 400);
                v.Set(P.Tuner, (b / 400) % 2);
                e.Process(l, r, Block);
            });
            Measure.Report($"Neo Amp: {bytes} bytes allocated per block over 2000 blocks");
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void ThirtySecondsOfTailHoldNoDenormalsAndEndInSilence()
        {
            foreach (int vo in new[] { 5, 0, 7 })
            {
                var v = Values(s => { Voicing(s, vo); Cabinet(s, vo == 7 ? 6 : 3); Set(s, P.Gain, 40); Pct(s, P.Master, 100); Pct(s, P.Sag, 80); });
                var e = Engine(v);
                int frames = 30 * Rate + Rate / 2;
                var x = new float[frames];
                var burst = AmpSources.Chords(1.0, 0.5);
                Array.Copy(burst, x, burst.Length);
                var y = Run(e, x);
                long subnormal = 0, nonFinite = 0;
                for (int i = 0; i < frames; i++)
                {
                    if (float.IsSubnormal(y[i])) subnormal++;
                    if (!float.IsFinite(y[i])) nonFinite++;
                }
                Measure.Report($"Neo Amp 30 s tail, {NeoBuzz.Amp.Voicing.All[vo].Name}: {subnormal} denormals, {nonFinite} non-finite samples");
                Assert.That(subnormal, Is.EqualTo(0));
                Assert.That(nonFinite, Is.EqualTo(0));
                for (int i = 3 * Rate; i < frames; i++) Assert.That(Math.Abs(y[i]), Is.LessThan(1e-7f), $"voicing {vo}, sample {i}");
                Assert.That(Rms(y, 0, Rate), Is.GreaterThan(0.01), "it played");
            }
        }

        [Test]
        public void EveryVoicingAndQualityStaysFiniteAndBoundedOnAFullScaleSignalAtTheTopOfEveryKnob()
        {
            var x = AmpSources.Chords(1.5, 1.0);
            for (int vo = 0; vo < 8; vo++)
                foreach (int q in new[] { 0, 2 })
                {
                    var v = Values(s => { Voicing(s, vo); s.Set(P.Quality, q); Cabinet(s, 3); Set(s, P.Gain, 40); Pct(s, P.Bass, 100); Pct(s, P.Mid, 100); Pct(s, P.Treble, 100); Pct(s, P.Presence, 100); Pct(s, P.Resonance, 100); Pct(s, P.Master, 100); Pct(s, P.Sag, 100); Set(s, P.Trim, 24); Set(s, P.Output, 24); });
                    var y = Run(Engine(v), x);
                    Assert.That(y.All(float.IsFinite), Is.True, $"voicing {vo}, {2 << q}x");
                    Assert.That(y.Max(Math.Abs), Is.LessThan(2000f), $"voicing {vo}, {2 << q}x");
                }
        }

        [Test]
        public void TheEngineFilesUseNoLibmAndNoLockTheAudioThreadCouldWaitFor()
        {
            // The shared PortableMathTests scan every engine folder for libm; this one adds Neo Amp's own rule: no lock statement in the audio path.
            string probe = TestContext.CurrentContext.TestDirectory;
            while (probe != null && !Directory.Exists(Path.Combine(probe, "Machines", "NeoAmp"))) probe = Path.GetDirectoryName(probe);
            var text = File.ReadAllText(Path.Combine(probe, "Machines", "NeoAmp", "Engine", "AmpEngine.cs"));
            int process = text.IndexOf("public void Process(", StringComparison.Ordinal);
            int builder = text.IndexOf("static class AmpBuilder", StringComparison.Ordinal);
            Assert.That(process, Is.GreaterThan(0));
            Assert.That(text.Substring(process, builder - process), Does.Not.Contain("lock ("), "no lock in the audio path");
            foreach (var file in Directory.GetFiles(Path.Combine(probe, "Machines", "NeoAmp", "Engine"), "*.cs"))
            {
                var src = File.ReadAllText(file);
                foreach (var banned in new[] { "Math.Sin(", "Math.Cos(", "Math.Tan(", "Math.Exp(", "Math.Log(", "Math.Pow(", "Math.Tanh(", "MathF.", "Vector<", "Fma." })
                {
                    // Comments and strings may name them; code does not.
                    foreach (var line in src.Split('\n'))
                    {
                        int c = line.IndexOf("//", StringComparison.Ordinal);
                        var code = c >= 0 ? line.Substring(0, c) : line;
                        int at = code.IndexOf(banned, StringComparison.Ordinal);
                        bool own = at > 0 && char.IsLetter(code[at - 1]);       // PortableMath.Sin( contains Math.Sin(
                        Assert.That(at >= 0 && !own, Is.False, $"{Path.GetFileName(file)}: {banned}");
                    }
                }
            }
        }

        // ================================================================================== gate: CPU

        [Test]
        public void ACpuFigureAt4xAgainstNeoDrive()
        {
            var x = AmpSources.Chords(3.0, 0.25);
            const int Warm = Rate;
            var amp = Throughput.Measure("Neo Amp (High gain, 4x, closed 4x12 cabinet, blocks of 256)", 2, Rate, Block, () =>
            {
                var v = Values(s => { Voicing(s, 5); Cabinet(s, 3); Set(s, P.Gain, 30); });
                var e = Engine(v);
                var l = new float[Block];
                var r = new float[Block];
                void Step(int pos, int n)
                {
                    Array.Copy(x, pos % (x.Length - Block), l, 0, n);
                    Array.Copy(x, pos % (x.Length - Block), r, 0, n);
                    e.Process(l, r, n);
                }
                for (int pos = 0; pos < Warm; pos += Block) Step(pos, Block);
                return Throughput.InBlocks(Block, (pos, n) => Step(Warm + pos, n));
            });
            var drive = Throughput.Measure("Neo Drive (default, blocks of 256)", 2, Rate, Block, () =>
            {
                var v = new ParamValues(NeoBuzz.Drive.Params.Table);
                var e = new NeoBuzz.Drive.DriveEngine(v);
                e.SetSampleRate(Rate);
                var l = new float[Block];
                var r = new float[Block];
                void Step(int pos, int n)
                {
                    Array.Copy(x, pos % (x.Length - Block), l, 0, n);
                    Array.Copy(x, pos % (x.Length - Block), r, 0, n);
                    e.Process(l, r, n);
                }
                for (int pos = 0; pos < Warm; pos += Block) Step(pos, Block);
                return Throughput.InBlocks(Block, (pos, n) => Step(Warm + pos, n));
            });
            Measure.Report(amp.ToString());
            Measure.Report(drive.ToString());
            Measure.Report($"Neo Amp at 4x costs {drive.Speed / amp.Speed:F2} times Neo Drive's CPU: {100.0 / amp.Speed:F2} % of a core against {100.0 / drive.Speed:F2} %, at the reference's speed");
            Assert.That(amp.Speed, Is.GreaterThan(Throughput.Floor(15, 10)), "the amp with a cabinet at 4x runs at least 15 times faster than real time at the reference's speed");
        }

        // ================================================================================== gate: state

        [Test]
        public void TheWaveReferenceRoundTripsAndFollowsTheSong()
        {
            var ir = Burst(0.12, 0.05, 31);
            var songA = new FakeWaves();
            songA.Put(7, "my speaker", ir, null);
            var v = Values(s => { Cabinet(s, 7); WaveSlot(s, 7); });
            var e = Engine(v, songA);
            Assert.That(e.CurrentCab.Name, Is.EqualTo("my speaker"));
            Assert.That(e.CurrentCab.FellBack, Is.False);
            var x = Material();
            var a = Run(e, x);

            // Saved: the parameters' raw values (the slot is the reference); the song saves the wave itself, not Neo Amp.
            var saved = Enumerable.Range(0, P.Count).Select(i => v[i]).ToArray();
            Assert.That(saved[P.Cabinet], Is.EqualTo(AmpCabs.WaveCab));
            Assert.That(saved[P.CabWave], Is.EqualTo(6), "the slot is stored as 7 (raw 6), nothing more of the wave");
            // Reopened: a new machine, the song's waves read in again (the same samples, another object), the saved values.
            var songB = new FakeWaves();
            songB.Put(7, "my speaker", (float[])ir.Clone(), null);
            var w = Values(s => { for (int i = 0; i < P.Count; i++) s.Set(i, saved[i]); });
            var b = Run(Engine(w, songB), x);
            Assert.That(b, Is.EqualTo(a), "the same cabinet, bit for bit");
            Assert.That(songB.Wanted, Is.EqualTo(7), "the engine asks the song for the slot");

            // The reference follows the song: another wave in slot 7 gives another cabinet.
            var songC = new FakeWaves();
            songC.Put(7, "another speaker", Burst(0.12, 0.05, 32), null);
            var c = Run(Engine(Values(s => { for (int i = 0; i < P.Count; i++) s.Set(i, saved[i]); }), songC), x);
            Assert.That(c, Is.Not.EqualTo(a));
            var machineState = new StateMigrations(Params.Table);
            Assert.That(machineState.Current, Is.EqualTo(0), "versioned, version 0");
            Assert.That(machineState.Keys, Does.Contain("CabWave"));
        }

        [Test]
        public void AMissingWaveFallsBackToTheClosed4x12AndSaysSoUntilItAppears()
        {
            var songs = new FakeWaves();
            var v = Values(s => { Cabinet(s, 7); WaveSlot(s, 42); });
            var e = Engine(v, songs);
            Assert.That(e.CurrentCab.Name, Is.EqualTo("Closed 4x12"), "the closed 4x12 plays");
            Assert.That(e.CurrentCab.FellBack, Is.True);
            Assert.That(e.Status, Is.EqualTo(AmpCabStatus.WaveMissing), "the editor says the wave is missing");
            var x = Material();
            var a = Run(e, x);
            // It is the very same cabinet the closed 4x12 gives.
            var b = Run(Engine(Values(s => Cabinet(s, 3))), x);
            Assert.That(a, Is.EqualTo(b), "the same bits as the closed 4x12");
            // The wave appears (the song loads it): the cabinet becomes the wave's, the status clears.
            songs.Put(42, "late wave", Burst(0.1, 0.04), null);
            e.Process(new float[Block], new float[Block], Block);
            e.WaitForBuilder();
            e.Process(new float[Block], new float[Block], Block);
            Assert.That(e.CurrentCab.Name, Is.EqualTo("late wave"));
            Assert.That(e.Status, Is.EqualTo(AmpCabStatus.Ready));
            // While a wave is still being read, what plays keeps playing (no fall back yet).
            songs.Clips.Remove(42);
            songs.Loading.Add(42);
            e.Process(new float[Block], new float[Block], Block);
            Assert.That(e.Status, Is.EqualTo(AmpCabStatus.WaveLoading));
            Assert.That(e.CurrentCab.Name, Is.EqualTo("late wave"));
        }

        [Test]
        public void AChangedSampleRateBuildsTheCabinetAgainForTheNewRate()
        {
            var e = Engine(Values(s => Cabinet(s, 3)));
            Assert.That(e.CurrentCab.Rate, Is.EqualTo(Rate));
            int len48 = e.CurrentCab.Ir.Length;
            e.SetSampleRate(44100);
            e.BuildNow();
            Assert.That(e.CurrentCab.Rate, Is.EqualTo(44100));
            Assert.That(Math.Abs(e.CurrentCab.Ir.Length - len48 * 44100.0 / Rate), Is.LessThan(8), "the same cabinet in seconds");
            var y = Run(e, AmpSources.Pluck(196.0, 1.0, 0.25, 4, 2.0, 0.2, 44100));
            Assert.That(y.All(float.IsFinite), Is.True);
            Assert.That(Rms(y, 4000, 8000), Is.GreaterThan(0.01));
        }

        // ================================================================================== the cabinets

        [Test]
        public void SixCabinetsAreMadeByCodeMinimumPhaseUnder50msAndDifferent()
        {
            var irs = new List<float[]>();
            for (int c = AmpCabs.FirstBuiltIn; c <= AmpCabs.LastBuiltIn; c++)
            {
                var ir = AmpCabs.Build(c, Rate);
                irs.Add(ir);
                Assert.That(ir.Length, Is.LessThanOrEqualTo((int)(0.05 * Rate)), $"{AmpCabs.Names[c]} is 50 ms or shorter");
                Assert.That(AmpCabs.Build(c, Rate), Is.EqualTo(ir), "deterministic: the same bits twice");
                // Minimum phase: the energy is at the start (the first millisecond holds nearly all of it) and the peak is within a few samples.
                double e = 0, e1 = 0, peak = 0; int at = 0;
                for (int i = 0; i < ir.Length; i++) { e += ir[i] * (double)ir[i]; if (i < Rate / 1000) e1 += ir[i] * (double)ir[i]; if (Math.Abs(ir[i]) > peak) { peak = Math.Abs(ir[i]); at = i; } }
                Assert.That(e1 / e, Is.GreaterThan(0.9), $"{AmpCabs.Names[c]}: energy in the first millisecond");
                Assert.That(at, Is.LessThan(12), $"{AmpCabs.Names[c]}: the peak is at the start");
                // The response is the analog design's (80 Hz … 8 kHz) within 1.5 dB.
                var re = new double[8192]; var im = new double[8192];
                for (int i = 0; i < ir.Length; i++) re[i] = ir[i];
                Fft.Transform(re, im, false);
                double norm = 0; int nn = 0;
                var diffs = new List<double>();
                for (double hz = 80; hz <= 8000; hz *= 1.12)
                {
                    int k = (int)Math.Round(hz * 8192 / Rate);
                    double got = 20 * Math.Log10(Math.Sqrt(re[k] * re[k] + im[k] * im[k]) + 1e-12);
                    double want = 20 * Math.Log10(AmpCabs.Magnitude(c, k * (double)Rate / 8192));
                    diffs.Add(got - want); norm += got - want; nn++;
                }
                double mean = norm / nn;
                // The level is normalised on a pink spectrum, so the shapes (with the offset taken out) have to agree.
                Assert.That(diffs.Max(d => Math.Abs(d - mean)), Is.LessThan(1.5), $"{AmpCabs.Names[c]}: the impulse response is the designed magnitude");
                Assert.That(AmpCabs.PinkGain(ir, Rate), Is.EqualTo(1.0).Within(0.02), $"{AmpCabs.Names[c]}: normalised on pink noise");
            }
            // Different from each other: the magnitudes differ by at least 1.5 dB rms over the guitar and bass band (the two closest are reported).
            double least = double.MaxValue; string pair = "";
            for (int i = 0; i < irs.Count; i++)
                for (int j = i + 1; j < irs.Count; j++)
                {
                    double acc = 0; int n = 0;
                    for (double hz = 60; hz <= 8000; hz *= 1.12)
                    {
                        double d = 20 * Math.Log10(AmpCabs.Magnitude(i + 1, hz) / AmpCabs.Magnitude(j + 1, hz));
                        acc += d * d; n++;
                    }
                    double dist = Math.Sqrt(acc / n);
                    if (dist < least) { least = dist; pair = $"{AmpCabs.Names[i + 1]} / {AmpCabs.Names[j + 1]}"; }
                }
            Measure.Report($"Neo Amp cabinets: the two closest are {pair}, {least:0.0} dB rms apart");
            Assert.That(least, Is.GreaterThan(1.5));
        }

        [Test]
        public void TheMicPositionAndTheCutsShapeTheCabinetSound()
        {
            var x = Measure.PinkNoise(3 * Rate, 0.05);
            double Band(float[] y, double lo, double hi)
            {
                var mag = Measure.Spectrum(y, Rate, 65536);
                double a = 0;
                for (int k = (int)(lo * 65536 / Rate); k < (int)(hi * 65536 / Rate); k++) a += mag[k] * mag[k];
                return 10 * Math.Log10(a + 1e-30);
            }
            double Treble(ParamValues v) { var y = Run(Engine(v), x); return Band(y, 3000, 8000) - Band(y, 300, 1000); }
            double centre = Treble(Values(s => { Voicing(s, 3); Cabinet(s, 3); Pct(s, P.MicPos, 0); }));
            double edge = Treble(Values(s => { Voicing(s, 3); Cabinet(s, 3); Pct(s, P.MicPos, 100); }));
            Assert.That(centre - edge, Is.GreaterThan(5), "the centre of the cone is brighter than its edge");
            double hi = Treble(Values(s => { Voicing(s, 3); Cabinet(s, 3); Set(s, P.HighCut, 3000); }));
            Assert.That(centre - hi, Is.GreaterThan(6), "the high cut takes the top off");
            var full = Run(Engine(Values(s => { Voicing(s, 3); Cabinet(s, 3); })), x);
            var cut = Run(Engine(Values(s => { Voicing(s, 3); Cabinet(s, 3); Set(s, P.LowCut, 300); })), x);
            Assert.That(Band(full, 40, 150) - Band(cut, 40, 150), Is.GreaterThan(10), "the low cut clears the lows");
            // The cuts' two ends are off: at 20 Hz and 20 kHz the output is the cabinet's alone, bit for bit.
            var off = Run(Engine(Values(s => { Voicing(s, 3); Cabinet(s, 3); Set(s, P.LowCut, 20); Set(s, P.HighCut, 20000); })), x);
            Assert.That(off, Is.EqualTo(full));
        }

        [Test]
        public void AWaveAsTheCabinetIsReadInMonoOrStereoAndTrimmedAndLevelled()
        {
            var l = Burst(0.5, 0.2, 1);
            var r = Burst(0.5, 0.2, 2);
            var ir = AmpCabs.FromWave(l, r, Rate, Rate);
            Assert.That(ir.Length, Is.LessThanOrEqualTo((int)(AmpCabs.MaxWaveSeconds * Rate)), "at most 250 ms");
            Assert.That(ir.All(float.IsFinite), Is.True);
            Assert.That(AmpCabs.PinkGain(ir, Rate), Is.EqualTo(1.0).Within(0.02));
            var mono = AmpCabs.FromWave(l, null, Rate, Rate);
            Assert.That(mono.Length, Is.GreaterThan(0));
            // A wave at another sample rate is resampled to the engine's.
            var at96 = AmpCabs.FromWave(Burst(0.5, 0.2, 1, 96000), null, 96000, Rate);
            Assert.That(Math.Abs(at96.Length - mono.Length), Is.LessThan(Rate / 100));
        }

        // ================================================================================== presets

        static double[] ThirdOctaves(float[] y)
        {
            const int N = 16384;
            var acc = new double[24];
            var bins = new double[N / 2 + 1];
            int frames = 0;
            for (int p = Rate / 2; p + N <= y.Length; p += N)
            {
                var mag = Measure.Spectrum(y, p, N);
                for (int k = 0; k < bins.Length && k < mag.Length; k++) bins[k] += mag[k] * mag[k];
                frames++;
            }
            for (int b = 0; b < 24; b++)
            {
                double lo = 63 * Math.Pow(2, (b - 0.5) / 3.0), hi = 63 * Math.Pow(2, (b + 0.5) / 3.0);
                double sum = 0; int n = 0;
                for (int k = (int)Math.Ceiling(lo * N / Rate); k < (int)(hi * N / Rate) && k < bins.Length; k++) { sum += bins[k]; n++; }
                acc[b] = 10 * Math.Log10(sum / Math.Max(1, n) / Math.Max(1, frames) + 1e-20);
            }
            double mean = acc.Average();
            for (int b = 0; b < 24; b++) acc[b] -= mean;
            return acc;
        }

        /// <summary>
        /// The factory presets are levelled to one loudness on the listening pack's own sources (plucked chords, riff, bass line, peaks at -12 dBFS):
        /// the loudest 400 ms RMS of each is within 1.5 dB of -18 dBFS and no peak reaches -1 dBFS, so a jump from one preset to the next is not
        /// a jump in volume.
        /// </summary>
        [Test]
        public void FactoryPresetsAreLevelledToOneLoudnessOnTheirSources()
        {
            const double target = -18.0;
            var problems = new List<string>();
            var cache = new Dictionary<string, float[]>();
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Amp"))
            {
                var v = new ParamValues(Params.Table);
                foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                string kind = p.Preset.StartsWith("Bass") ? "bass" : (p.Preset.StartsWith("Clean") || p.Preset == "Crunch — Edge of Breakup") ? "chords" : "riff";
                if (!cache.TryGetValue(kind, out var x)) cache[kind] = x = AmpSources.ForPreset(p.Preset, 15.0);
                var y = Run(Engine(v), x);
                double loud = AmpSources.Loudest400Db(y), peak = Db(Measure.Peak(y, 0, y.Length));
                double output = v.Value(P.Output);
                Measure.Report($"Neo Amp level {p.Preset}: loudest 400 ms {loud:0.00} dBFS RMS, peak {peak:0.0} dBFS, Output {output:0.0} dB");
                if (Math.Abs(loud - target) > 1.5) problems.Add($"{p.Preset} loudest 400 ms RMS {loud:0.0} dBFS");
                if (peak >= -1.0) problems.Add($"{p.Preset} peak {peak:0.0} dBFS");
            }
            Assert.That(problems, Is.Empty, string.Join("; ", problems));
        }

        [Test]
        public void EightPresetsAreClearlyDifferentAndClearlyAudibleOnFullScaleMaterial()
        {
            var presets = PresetTests.AllPresets().Where(p => p.Machine == "Neo Amp").ToList();
            Assert.That(presets.Count, Is.EqualTo(8));
            var x = AmpSources.Riff(6.0, 0.9);
            var renders = new Dictionary<string, float[]>();
            var shapes = new Dictionary<string, double[]>();
            foreach (var p in presets)
            {
                var v = new ParamValues(Params.Table);
                foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                var y = Run(Engine(v), x);
                double rms = Db(Rms(y, Rate, y.Length - Rate)), peak = Db(Measure.Peak(y, 0, y.Length));
                Measure.Report($"Neo Amp preset {p.Preset}: {rms:0.0} dB RMS, peak {peak:0.0} dBFS on a riff at -1 dBFS");
                Assert.That(rms, Is.InRange(-30.0, -6.0), $"{p.Preset} is audible and not too loud");
                // A clean preset levelled for a -12 dBFS DI is loud on a DI at -1 dBFS (the output is float, so nothing clips in the machine): 6 dB of headroom is the bound.
                Assert.That(peak, Is.LessThan(6.0), $"{p.Preset} stays within 6 dB over full scale");
                Assert.That(y.All(float.IsFinite), Is.True);
                renders[p.Preset] = y;
                shapes[p.Preset] = ThirdOctaves(y);
            }
            // Different: the third-octave spectra (level taken out) are at least 2 dB apart in rms, or the sounds' dynamics are (the crest factor differs by 3 dB).
            var names = renders.Keys.ToList();
            double least = double.MaxValue; string pair = "";
            for (int i = 0; i < names.Count; i++)
                for (int j = i + 1; j < names.Count; j++)
                {
                    double acc = 0;
                    for (int k = 0; k < 24; k++) acc += Math.Pow(shapes[names[i]][k] - shapes[names[j]][k], 2);
                    double dist = Math.Sqrt(acc / 24);
                    double ci = Db(Measure.Peak(renders[names[i]], Rate, renders[names[i]].Length - Rate) / Rms(renders[names[i]], Rate, renders[names[i]].Length - Rate));
                    double cj = Db(Measure.Peak(renders[names[j]], Rate, renders[names[j]].Length - Rate) / Rms(renders[names[j]], Rate, renders[names[j]].Length - Rate));
                    double score = Math.Max(dist, Math.Abs(ci - cj) * 0.7);
                    if (score < least) { least = score; pair = $"{names[i]} / {names[j]}"; }
                    Assert.That(score, Is.GreaterThan(1.8), $"{names[i]} and {names[j]} are clearly different (spectrum {dist:0.0} dB, crest {Math.Abs(ci - cj):0.0} dB)");
                }
            Measure.Report($"Neo Amp presets: the two closest are {pair} at {least:0.0} dB");
        }

        [Test]
        public void ThePictureHasItsData()
        {
            var v = Values(s => { Voicing(s, 3); Cabinet(s, 3); });
            var e = Engine(v);
            Assert.That(e.CurrentCab.Name, Is.EqualTo("Closed 4x12"));
            Assert.That(e.CurrentCab.Ir.Length, Is.GreaterThan(100));
            for (double hz = 40; hz < 15000; hz *= 1.5) Assert.That(AmpCabs.Magnitude(3, hz), Is.GreaterThan(0).And.LessThan(20));
            // The meters of the editor read the input and the output.
            var y = Run(e, Sine(220, 0.25, Rate));
            Assert.That(e.InMeter.PeakDb, Is.GreaterThan(-14).And.LessThan(-10), "the input meter");
            Assert.That(e.OutMeter.PeakDb, Is.GreaterThan(-60));
        }
    }
}
