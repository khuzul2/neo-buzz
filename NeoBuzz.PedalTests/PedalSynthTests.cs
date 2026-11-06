// Fork (docs/specs/mcp-server.md §12.15): the Neo machines' measurement suite (machines.md §6) on the five Pedal synths,
// run on their own Work (SynthHarness.cs), and their MIDI input:
//  - a MIDI note plays as a track's note does (the same samples where the voices are alike), at its velocity; chords
//    take a voice each (SH101: last-note priority), pitch bend moves ±2 semitones, the mod wheel brings in vibrato,
//    the sustain pedal holds, the transport stopping releases MIDI notes (a track's note rings on where it did);
//  - silence after sound settles to exact zero, with no denormal number on the way, and the machine says it is silent
//    once it is, and only then;
//  - silence costs no more than before the first note;
//  - nothing allocates per block once running, notes, MIDI and moving parameters included;
//  - blocks of 256 and of 117 give the same output;
//  - every parameter alone at each end, and MIDI at its ends, stay finite;
//  - parameter changes glide: a stepped gain moves the output by small steps, a stepped cutoff or width comes in over
//    the glide, and both land; a setting changed in silence starts the next note at it, with no glide.
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Buzz.MachineInterface;
using NeoBuzz.MachineTests;

namespace NeoBuzz.PedalTests
{
    [TestFixture]
    public class PedalSynthTests
    {
        const int Rate = SynthRunner.Rate, Tick = SynthRunner.SamplesPerTick;
        const int A4 = 69;

        static IEnumerable<Synth> Synths() => Synth.All;
        static IEnumerable<Synth> Polys() => Synth.All.Where(s => s.Polyphonic);

        static bool Subnormal(float x) => x != 0 && Math.Abs(x) < float.Epsilon * 8388608f;   // below 2^-126

        static List<SynthEvent> Events(params (int At, Action<SynthRunner> Do)[] e)
        {
            // In time order; events at the same time in the order given.
            return e.Select((x, i) => (x, i)).OrderBy(p => p.x.At).ThenBy(p => p.i).Select(p => new SynthEvent(p.x.At, p.x.Do)).ToList();
        }

        /// <summary>
        /// A tone whose loudest partial is its pitch, for the pitch tests: Pedal FM's op 4 alone (ops 1–3 at level 0, a
        /// sine), Juno106's saw through its filter at about 460 Hz with no envelope (its defaults are brighter); the
        /// others as they are.
        /// </summary>
        static Action<object> PureTone(Synth s) => s.Name switch
        {
            "Pedal FM" => m => { var f = (PedalFM.PedalFMMachine)m; f.Op1Level = 0; f.Op2Level = 0; f.Op3Level = 0; },
            "Pedal Juno106" => m => { var j = (PedalJuno106.PedalJuno106Machine)m; j.VcfFrequency = 55; j.VcfEnvAmount = 0; j.VcfResonance = 0; },
            _ => null,
        };

        /// <summary>How long a released note takes to end at the defaults (Pedal FM's release is 1.24 s to 1e-4: 11 s).</summary>
        static int Tail(Synth s) => s.Name == "Pedal FM" ? 13 * Rate : 2 * Rate;

        static double Pitch(float[] x, int from, int n = 32768) => Measure.PeakFrequency(x, from, n);

        /// <summary>A figure with its sign, rounded first (so a small negative one reads "+0.00", not "-+0.00").</summary>
        static string Signed(double x, int decimals)
        {
            double r = Math.Round(x, decimals);
            if (r == 0) r = 0;   // no negative zero
            return (r >= 0 ? "+" : "") + r.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
        }

        // ── MIDI ────────────────────────────────────────────────────────────────────────────────────────────────

        [TestCaseSource(nameof(Synths))]
        public void AMidiNotePlaysAsATracksNoteDoes(Synth s)
        {
            int off = Rate * 3 / 2, frames = off + Tail(s) + Rate / 2;
            var track = SynthRunner.Render(s, frames, 256, Events((0, r => r.Note(0, A4)), (off, r => r.Off(0))), PureTone(s));
            var midi = SynthRunner.Render(s, frames, 256, Events((0, r => r.Midi(A4, 127)), (off, r => r.Midi(A4, 0))), PureTone(s));
            double ht = Pitch(track.L, Rate / 4), hm = Pitch(midi.L, Rate / 4);
            double lt = Measure.Db(Measure.Rms(track.L, Rate / 4, Rate)), lm = Measure.Db(Measure.Rms(midi.L, Rate / 4, Rate));
            int differ = 0;
            for (int i = 0; i < frames; i++) if (track.L[i] != midi.L[i] || track.R[i] != midi.R[i]) differ++;
            PedalReport.Report($"{s.Name}: A4 through MIDI at velocity 127 {hm:F2} Hz at {lm:F2} dBFS RMS, through its track {ht:F2} Hz at {lt:F2} dBFS RMS; " +
                               (differ == 0 ? "the same samples" : $"{differ} samples differ (another voice: its own analog detune)"));
            if (s.Name == "Pedal Juno106")
            {
                // The note takes another voice (the highest free, voice 6; a track's is its own), with its own analog
                // detune. Juno106's FastPow2 is about 10 cents short just under a whole octave, so its voices 1, 3 and 5,
                // whose static detunes are negative, play that much flat (the profile's −11 cents on track 1), as before.
                Assert.That(Measure.Cents(hm, 440), Is.EqualTo(0).Within(2), "A4 is 440 Hz (voice 6: +0.5 cents)");
                Assert.That(Measure.Cents(ht, 440), Is.EqualTo(-10.4).Within(2), "track 1's voice: as before");
                Assert.That(lm, Is.EqualTo(lt).Within(0.2));
            }
            else
            {
                Assert.That(Measure.Cents(hm, 440), Is.EqualTo(0).Within(1), "A4 is 440 Hz");
                Assert.That(differ, Is.EqualTo(0), "the same samples as the track's note");
            }
            Assert.That(Measure.Peak(midi.L, frames - Rate / 2, Rate / 2), Is.EqualTo(0), "its note off released it");
        }

        /// <summary>The level a MIDI note's velocity gives, by each synth's design (at its defaults).</summary>
        static double ExpectedVelocityDb(string synth, int velocity)
        {
            double v = velocity / 127.0;
            switch (synth)
            {
                case "Pedal M1":      // Amp Vel 80: 1 − a + a·v
                    double a = 80 / 127.0;
                    return Measure.Db(1 - a + a * v);
                case "Pedal Tracker": // the Volume column's curve
                    return Measure.Db(v * v);
                default:              // a level on the voice (Juno106, SH101), or the track Velocity's (FM)
                    return Measure.Db(v);
            }
        }

        [TestCaseSource(nameof(Synths))]
        public void VelocitySetsTheLevel(Synth s)
        {
            int frames = Rate;
            double Level(int velocity)
            {
                var o = SynthRunner.Render(s, frames, 256, Events((0, r => r.Midi(A4, velocity))), PureTone(s));
                return Measure.Db(Measure.Rms(o.L, Rate / 2, Rate / 4));
            }
            double full = Level(127);
            var rows = new List<string>();
            foreach (int v in new[] { 100, 64, 20 })
            {
                double got = Level(v) - full, want = ExpectedVelocityDb(s.Name, v);
                rows.Add($"{v}: {got:F2} dB");
                Assert.That(got, Is.EqualTo(want).Within(0.05), $"velocity {v}");
            }
            PedalReport.Report($"{s.Name}: MIDI velocity against 127: {string.Join(", ", rows)}");
        }

        /// <summary>A chord's notes each take a voice: each is as loud in the chord as alone.</summary>
        [TestCaseSource(nameof(Polys))]
        public void AMidiChordTakesAVoiceEachNote(Synth s)
        {
            int frames = Rate;
            int[] keys = { 60, 64, 67 };
            (float[] L, float[] R) Play(params int[] k) => SynthRunner.Render(s, frames, 256, Events(k.Select(key => (0, (Action<SynthRunner>)(r => r.Midi(key, 100)))).ToArray()), PureTone(s));
            var chord = Play(keys);
            int n = 16384, at = Rate / 2;
            var mag = Measure.Spectrum(Window(chord.L, at, n), 0, n);
            var rows = new List<string>();
            foreach (int k in keys)
            {
                var alone = Measure.Spectrum(Window(Play(k).L, at, n), 0, n);
                int bin = (int)Math.Round(440 * Math.Pow(2, (k - 69) / 12.0) * n / Rate);
                double a = Around(alone, bin), c = Around(mag, bin);
                rows.Add($"{k}: {Signed(Measure.Db(c / a), 2)} dB");
                Assert.That(Measure.Db(c / a), Is.EqualTo(0).Within(1.0), $"key {k} in the chord");
            }
            PedalReport.Report($"{s.Name}: a MIDI chord C4 E4 G4: each note's fundamental against it alone {string.Join(", ", rows)}");
        }

        static float[] Window(float[] x, int at, int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = x[at + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            return w;
        }

        /// <summary>The magnitude of the bins within ±4 of <paramref name="bin"/> (a voice's few cents of detune stay inside).</summary>
        static double Around(double[] mag, int bin)
        {
            double e = 0;
            for (int k = bin - 4; k <= bin + 4; k++) e += mag[k] * mag[k];
            return Math.Sqrt(e);
        }

        [Test]
        public void TheSh101PlaysTheLastMidiKeyAndGoesBackToTheOneHeld()
        {
            var s = Synth.Named("Pedal SH101");
            int q = Rate / 2;
            var o = SynthRunner.Render(s, 4 * q + Rate, 256, Events(
                (0, r => r.Midi(57, 100)),            // A3
                (q, r => r.Midi(64, 100)),            // E4 over it
                (2 * q, r => r.Midi(64, 0)),          // back to A3
                (3 * q, r => r.Midi(57, 0))));        // all keys up
            double a = Pitch(o.L, q / 4, 16384), e = Pitch(o.L, q + q / 4, 16384), back = Pitch(o.L, 2 * q + q / 4, 16384);
            PedalReport.Report($"Pedal SH101: MIDI A3 {a:F1} Hz, E4 over it {e:F1} Hz, E4 let go {back:F1} Hz (A3 again, no retrigger)");
            Assert.That(Measure.Cents(a, 220), Is.EqualTo(0).Within(2));
            Assert.That(Measure.Cents(e, 329.63), Is.EqualTo(0).Within(2));
            Assert.That(Measure.Cents(back, 220), Is.EqualTo(0).Within(2));
            Assert.That(Measure.Peak(o.L, 4 * q + Rate / 2, Rate / 2), Is.EqualTo(0), "released");
        }

        /// <summary>Bent all the way, A4 plays B4 and G4 as the synth plays them unbent (M1's pitch is its own approximation of 2^x).</summary>
        [TestCaseSource(nameof(Synths))]
        public void PitchBendMovesTwoSemitones(Synth s)
        {
            int frames = 2 * Rate;
            double Heard(int key, int bend) => Pitch(SynthRunner.Render(s, frames, 256, Events((0, r => r.Control(0xE0, bend)), (0, r => r.Midi(key, 100))), PureTone(s)).L, Rate / 2);
            double up = Heard(A4, 16383), down = Heard(A4, 0), b4 = Heard(A4 + 2, 8192), g4 = Heard(A4 - 2, 8192), a4 = Heard(A4, 8192);
            double cu = Measure.Cents(up, a4), cd = Measure.Cents(down, a4);
            PedalReport.Report($"{s.Name}: pitch bend at the top {Signed(cu, 1)} cents ({Signed(Measure.Cents(up, b4), 2)} from its B4), at the bottom {Signed(cd, 1)} cents ({Signed(Measure.Cents(down, g4), 2)} from its G4)");
            // The top is 8191/8192 of the way: 0.02 cents short of B4. Juno106's voices each have their own detune.
            double tol = s.Name == "Pedal Juno106" ? 3 : 0.5;
            Assert.That(Measure.Cents(up, b4), Is.EqualTo(0).Within(tol));
            Assert.That(Measure.Cents(down, g4), Is.EqualTo(0).Within(tol));
        }

        static IEnumerable<Synth> Vibrato() => Synth.All.Where(s => s.Name != "Pedal Tracker");

        /// <summary>The spread of the pitch over 2 s, in 8192-sample windows (cents, top to bottom).</summary>
        static double PitchSpread(float[] x, int from, int to)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int at = from; at + 8192 <= to; at += 2048)
            {
                double c = Measure.Cents(Pitch(x, at, 8192), 440);
                lo = Math.Min(lo, c); hi = Math.Max(hi, c);
            }
            return hi - lo;
        }

        [TestCaseSource(nameof(Vibrato))]
        public void TheModWheelBringsInVibrato(Synth s)
        {
            int frames = 3 * Rate;
            var still = SynthRunner.Render(s, frames, 256, Events((0, r => r.Midi(A4, 100))), PureTone(s));
            var moved = SynthRunner.Render(s, frames, 256, Events((0, r => r.Control(1, 127)), (0, r => r.Midi(A4, 100))), PureTone(s));
            double a = PitchSpread(still.L, Rate / 2, frames), b = PitchSpread(moved.L, Rate / 2, frames);
            PedalReport.Report($"{s.Name}: the mod wheel at the top: the pitch spreads over {b:F0} cents (without it {a:F0})");
            Assert.That(a, Is.LessThan(10), "no vibrato without the wheel");
            Assert.That(b, Is.GreaterThan(100), "up to a semitone either way");
        }

        [TestCaseSource(nameof(Synths))]
        public void TheSustainPedalHoldsMidiNotes(Synth s)
        {
            int q = Rate / 2;
            var o = SynthRunner.Render(s, 4 * q + Tail(s) + Rate, 256, Events(
                (0, r => r.Control(64, 127)), (0, r => r.Midi(A4, 100)),
                (q, r => r.Midi(A4, 0)),              // let go while the pedal is down
                (4 * q, r => r.Control(64, 0))));     // the pedal up
            double held = Measure.Db(Measure.Rms(o.L, 3 * q, q)), sounding = Measure.Db(Measure.Rms(o.L, q / 2, q / 2));
            PedalReport.Report($"{s.Name}: a MIDI note let go with the sustain pedal down: {Signed(held - sounding, 1)} dB 1 s on (held), silent after the pedal comes up");
            Assert.That(held - sounding, Is.GreaterThan(-6), "held while the pedal is down");
            Assert.That(Measure.Peak(o.L, 4 * q + Tail(s), Rate), Is.EqualTo(0), "released when it comes up");
        }

        [TestCaseSource(nameof(Synths))]
        public void TheTransportStoppingReleasesMidiNotes(Synth s)
        {
            int q = Rate / 2;
            var r = new SynthRunner(s, withSong: true);
            var o = r.Run(q + Tail(s) + Rate, 256, Events((0, x => { x.Playing = true; x.Midi(A4, 100); }), (q, x => x.Playing = false)));
            Assert.That(Measure.Peak(o.L, 0, q), Is.GreaterThan(0.01), "playing");
            Assert.That(Measure.Peak(o.L, q + Tail(s), Rate), Is.EqualTo(0), "released when the transport stops");
            // A track's note on the SH101 and FM rang on after Stop, and still does (as before); the others release it.
            var t = new SynthRunner(s, withSong: true);
            var ot = t.Run(q + Tail(s) + Rate, 256, Events((0, x => { x.Playing = true; x.Note(0, A4); }), (q, x => x.Playing = false)));
            bool ringsOn = Measure.Peak(ot.L, q + Tail(s), Rate) > 0.001;
            PedalReport.Report($"{s.Name}: the transport stopping releases a MIDI note; a track's note {(ringsOn ? "rings on, as before" : "is released, as before")}");
            Assert.That(ringsOn, Is.EqualTo(s.Name == "Pedal SH101" || s.Name == "Pedal FM"), "a track's note as before");
        }

        [TestCaseSource(nameof(Synths))]
        public void AllNotesOffAndAllSoundOff(Synth s)
        {
            int q = Rate / 2;
            var off = SynthRunner.Render(s, q + Tail(s) + Rate, 256, Events((0, r => r.Midi(A4, 100)), (q, r => r.Control(123, 0))));
            var sound = SynthRunner.Render(s, q + Rate, 256, Events((0, r => r.Midi(A4, 100)), (q, r => r.Control(120, 0))));
            Assert.That(Measure.Peak(off.L, q + Tail(s), Rate), Is.EqualTo(0), "all notes off releases");
            Assert.That(Measure.Peak(sound.L, q + Rate / 4, Rate * 3 / 4), Is.EqualTo(0), "all sound off silences within 250 ms");
        }

        // ── Silence ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Settings to hear a tail at: each synth's defaults, and where a fix applies; each with its notes through its
        /// tracks (as before: these runs hold without MIDI too) and with half of them through MIDI.
        /// </summary>
        static IEnumerable<TestCaseData> Tails()
        {
            foreach (bool midi in new[] { false, true })
            {
                string how = midi ? ", tracks and MIDI" : "";
                foreach (var s in Synth.All)
                    yield return new TestCaseData(s.Name, "defaults", midi).SetName($"{s.Name} at its defaults{how}");
                yield return new TestCaseData("Pedal Juno106", "ChorusMode=2 HpfPosition=3", midi).SetName($"Pedal Juno106, Chorus II, HPF 3{how}");
                yield return new TestCaseData("Pedal Juno106", "ChorusMode=3 HpfPosition=1", midi).SetName($"Pedal Juno106, Chorus III, HPF 1{how}");
                yield return new TestCaseData("Pedal M1", "short wave", midi).SetName($"Pedal M1, its wave ending while the notes hold{how}");
                yield return new TestCaseData("Pedal Tracker", "muted", midi).SetName($"Pedal Tracker, muted while it plays{how}");
            }
        }

        static Action<object> Setting(string setting) => m =>
        {
            if (setting == "defaults" || setting == "muted" || setting == "short wave") return;
            foreach (var kv in setting.Split(' '))
                m.GetType().GetProperty(kv.Split('=')[0])!.SetValue(m, int.Parse(kv.Split('=')[1]));
        };

        /// <summary>A runner at a setting: "short wave" gives M1 a wave of 0.1 s, not looped, that ends while a note holds.</summary>
        static SynthRunner At(Synth s, string setting) => setting == "short wave"
            ? new SynthRunner(s, null, SynthRunner.TestWave().Take(Rate / 10).ToArray(), loop: false)
            : new SynthRunner(s, Setting(setting));

        /// <summary>A chord (a note on the SH101) for a second, through tracks (and half of it through MIDI), then silence.</summary>
        static List<SynthEvent> ChordThenSilence(Synth s, string setting, bool midi)
        {
            var e = new List<(int, Action<SynthRunner>)>();
            int[] keys = s.Polyphonic ? new[] { 48, 55, 60, 64 } : new[] { 45 };
            for (int i = 0; i < keys.Length; i++)
            {
                int k = keys[i], t = i;
                if (i % 2 == 0 || !midi) { e.Add((0, r => r.Note(t, k))); e.Add((Rate, r => r.Off(t))); }
                else { e.Add((0, r => r.Midi(k, 110))); e.Add((Rate, r => r.Midi(k, 0))); }
            }
            if (!s.Polyphonic && midi) { e.Clear(); e.Add((0, r => r.Midi(45, 110))); e.Add((Rate, r => r.Midi(45, 0))); }
            if (setting == "muted") e.Add((Rate / 2, r => r.Set("MuteParam", 1)));
            return Events(e.ToArray());
        }

        [TestCaseSource(nameof(Tails))]
        public void SilenceAfterSoundSettlesToExactZero(string name, string setting, bool midi)
        {
            var s = Synth.Named(name);
            int sound = Rate, quiet = 30 * Rate;
            var r = At(s, setting);
            var (l, rr) = r.Run(sound + quiet, 256, ChordThenSilence(s, setting, midi));
            int lastNonZero = -1, subnormals = 0;
            for (int i = 0; i < l.Length; i++)
            {
                if (l[i] != 0 || rr[i] != 0) lastNonZero = i;
                if (Subnormal(l[i]) || Subnormal(rr[i])) subnormals++;
            }
            double settled = (lastNonZero + 1 - sound) / (double)Rate;
            PedalReport.Report($"{name} ({setting}{(midi ? ", tracks and MIDI" : "")}): silence after its notes settles to exact zero {Math.Max(0, settled):F2} s after the last note off; {subnormals} denormal output samples");
            Assert.That(subnormals, Is.EqualTo(0), "no denormal numbers come out");
            Assert.That(lastNonZero, Is.LessThan(sound + quiet - Rate), "exact zero for the last second of 30 s of silence");
        }

        /// <summary>
        /// ReBuzz plays silence where a generator's Work returns false: a synth that says so while it sounds cuts itself
        /// off (the Tracker, multi-output, is heard whatever it says). So it says it only for blocks of exact silence, and
        /// says it once its tail has ended (FM and M1 never did: a released voice stayed active; Juno106 with its chorus or
        /// HPF on never went quiet (its states decayed among the denormals); the Tracker, muted while it played, said silent while
        /// its fade was still at −28.6 dBFS).
        /// </summary>
        [TestCaseSource(nameof(Tails))]
        public void ItSaysItIsSilentOnceItIsAndOnlyThen(string name, string setting, bool midi)
        {
            var s = Synth.Named(name);
            int sound = Rate, quiet = 30 * Rate, block = 256;
            var r = At(s, setting);
            r.Reported = new List<int>();
            r.Run(sound + quiet, block, ChordThenSilence(s, setting, midi));
            int first = r.Reported.FirstOrDefault(at => at >= sound, -1);
            bool atTheEnd = r.Reported.Count > 0 && r.Reported[^1] >= sound + quiet - block;
            PedalReport.Report($"{name} ({setting}{(midi ? ", tracks and MIDI" : "")}): " + (first < 0 ? "never says it is silent (30 s after its last note off)"
                : $"says it is silent from {(first - sound) / (double)Rate:F2} s after its last note off; its own output where it said so peaked at {(r.ReportedPeak == 0 ? "exact zero" : $"{Measure.Db(r.ReportedPeak):F1} dBFS")}"));
            Assert.That(r.ReportedPeak, Is.EqualTo(0), "silent wherever it says so");
            Assert.That(atTheEnd, Is.True, "silent at the end of 30 s of silence, and says so");
        }

        /// <summary>
        /// The machine's own work time, in ms per second of audio, of each runner: groups of 16 blocks of 256 from each in
        /// turn, 40 rounds, each runner's fastest group (a shared runner's noise drops out; interleaved, a slow spell of the
        /// machine running the tests falls on all of them alike).
        /// </summary>
        static double[] WorkMsPerSecond(params SynthRunner[] runners)
        {
            const int group = 16, rounds = 40;
            var l = new float[256];
            var rr = new float[256];
            var best = Enumerable.Repeat(double.MaxValue, runners.Length).ToArray();
            var sw = new Stopwatch();
            for (int round = 0; round < rounds; round++)
                for (int k = 0; k < runners.Length; k++)
                {
                    sw.Restart();
                    for (int b = 0; b < group; b++) runners[k].Block(256, l, rr, 0);
                    best[k] = Math.Min(best[k], sw.Elapsed.TotalMilliseconds);
                }
            double seconds = group * 256.0 / Rate;
            return best.Select(t => t / seconds).ToArray();
        }

        static IEnumerable<TestCaseData> Slowdowns()
        {
            foreach (bool midi in new[] { false, true })
            {
                string how = midi ? ", tracks and MIDI" : "";
                foreach (var s in Synth.All)
                    yield return new TestCaseData(s.Name, "defaults", midi).SetName($"{s.Name} at its defaults, after silence{how}");
                yield return new TestCaseData("Pedal Juno106", "ChorusMode=2 HpfPosition=3", midi).SetName($"Pedal Juno106, Chorus II, HPF 3, after silence{how}");
            }
        }

        /// <summary>
        /// Silence after notes costs about what silence before the first did (within 2×: M1 keeps its ended voices' LFOs
        /// in time, about half a millisecond a second for four): a decaying state among the denormal floats (Juno106's HPF
        /// and chorus), or voices that stayed active after their notes (FM, M1, rendering silence), cost as much as playing.
        /// </summary>
        [TestCaseSource(nameof(Slowdowns))]
        public void SilenceCostsNoMoreAfterNotesThanBefore(string name, string setting, bool midi)
        {
            var s = Synth.Named(name);
            var fresh = new SynthRunner(s, Setting(setting));
            fresh.Run(Rate, 256, null);                                   // before any note, warmed up
            var played = new SynthRunner(s, Setting(setting));
            played.Run(Rate + Tail(s) + 2 * Rate, 256, ChordThenSilence(s, setting, midi));   // the notes end at 1 s; then their tails
            var playing = new SynthRunner(s, Setting(setting));
            var held = ChordThenSilence(s, setting, midi).Where(e => e.At == 0).ToList();     // the chord, held
            playing.Run(Rate / 2, 256, held);
            var t = WorkMsPerSecond(fresh, played, playing);
            double before = t[0], after = t[1], during = t[2];
            PedalReport.Report($"{name} ({setting}{(midi ? ", tracks and MIDI" : "")}): work {before:F2} ms/s before its first note, {during:F2} playing, {after:F2} after silence ({after / Math.Max(before, 1e-9):F2}× before)");
            Assert.That(after, Is.LessThanOrEqualTo(2 * before + 0.1), "silence after notes costs about what silence before them did");
        }

        // ── The audio thread ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Bytes a Fake answer allocates (its argument array).</summary>
        static long FakeCallBytes()
        {
            var f = Fake.Of<BuzzGUI.Interfaces.IBuzz>(new Dictionary<string, object> { ["Playing"] = (object)true });
            bool sink = f.Playing;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) sink ^= f.Playing;
            return (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
        }

        static IEnumerable<TestCaseData> SynthsBothWays()
        {
            foreach (bool midi in new[] { false, true })
                foreach (var s in Synth.All)
                    yield return new TestCaseData(s, midi).SetName($"{s.Name}{(midi ? ", tracks and MIDI" : "")}");
        }

        /// <summary>
        /// 2 s of blocks once running, with notes through tracks (and MIDI, its controllers), and the glided parameters
        /// moving: no allocation (the test's stand-in for ReBuzz allocates its answers' argument arrays: taken off). The
        /// Tracker formatted a diagnostics line on every note.
        /// </summary>
        [TestCaseSource(nameof(SynthsBothWays))]
        public void ProcessingAllocatesNothing(Synth s, bool midi)
        {
            var r = new SynthRunner(s, withSong: true);
            var glided = GlideCases().Where(c => (string)c.Arguments[0] == s.Name).Select(c => ((string)c.Arguments[1], (int)c.Arguments[2], (int)c.Arguments[3])).ToList();
            var l = new float[256];
            var rr = new float[256];
            void Step(int b)
            {
                // Something every few blocks: notes on several voices, through tracks and MIDI, bend, mod, pedal, parameters.
                switch (b % 24)
                {
                    case 0: r.Note(0, 48); r.Note(1, 55); if (midi) { r.Midi(60, 100); r.Midi(64, 90); } else r.Note(2, 60); break;
                    case 3: if (midi) { r.Control(0xE0, 12000); r.Control(1, 64); } break;
                    case 6: if (midi) { r.Control(64, 127); r.Midi(60, 0); } else r.Off(2); break;
                    case 9: if (midi) r.Midi(67, 70); r.Note(0, 50); break;
                    case 12: if (midi) { r.Control(64, 0); r.Control(0xE0, 8192); r.Control(1, 0); } break;
                    case 15: r.Off(0); r.Off(1); if (midi) { r.Midi(64, 0); r.Midi(67, 0); } break;
                }
                if (glided.Count > 0 && b % 8 == 4)
                {
                    var (p, from, to) = glided[(b / 8) % glided.Count];
                    r.Set(p, (b / 8) % 2 == 0 ? to : from);
                }
                r.Playing = (b / 96) % 2 == 0;
                r.Block(256, l, rr, 0);
            }
            for (int b = 0; b < 3 * Rate / 256; b++) Step(b);              // warm up: first notes, waves, the JIT
            long calls = Fake.Calls;
            var (bytes, why) = AllocationCheck.Run(2 * Rate / 256, b => Step(b));
            long fake = (Fake.Calls - calls) * FakeCallBytes();
            PedalReport.Report($"{s.Name}: {bytes - fake} bytes allocated over 2 s of blocks of 256 (notes{(midi ? ", MIDI" : "")} and parameters moving)");
            Assert.That(bytes - fake, Is.EqualTo(0), why);
        }

        [TestCaseSource(nameof(SynthsBothWays))]
        public void BlocksOf256And117GiveTheSameOutput(Synth s, bool midi)
        {
            // Notes start where both block sizes do (0 and 256 · 117 samples), as ReBuzz starts a block at each tick.
            int common = 256 * 117, frames = 3 * common;
            var e = midi
                ? Events((0, r => r.Note(0, 48)), (0, r => r.Midi(60, 100)), (0, r => r.Midi(64, 80)),
                         (common, r => r.Off(0)), (common, r => r.Midi(60, 0)), (common, r => r.Note(1, 67)),
                         (2 * common, r => r.Off(1)), (2 * common, r => r.Midi(64, 0)))
                : Events((0, r => r.Note(0, 48)), (0, r => r.Note(2, 60)), (0, r => r.Note(3, 64)),
                         (common, r => r.Off(0)), (common, r => r.Off(2)), (common, r => r.Note(1, 67)),
                         (2 * common, r => r.Off(1)), (2 * common, r => r.Off(3)));
            var a = SynthRunner.Render(s, frames, 256, e);
            var b = SynthRunner.Render(s, frames, 117, e);
            int differ = 0, first = -1;
            double worst = 0;
            for (int i = 0; i < frames; i++)
            {
                double d = Math.Max(Math.Abs(a.L[i] - b.L[i]), Math.Abs(a.R[i] - b.R[i]));
                if (d > 0) { differ++; if (first < 0) first = i; worst = Math.Max(worst, d); }
            }
            if (s.Name == "Pedal Juno106")
            {
                // Its analog drift steps once a block (a random walk of a few hundredths of a cent): another block size,
                // another walk. Left as it is (a change would move every song's drift); the pitch and level agree.
                double ca = Pitch(a.L, Rate / 4), cb = Pitch(b.L, Rate / 4);
                double la = Measure.Db(Measure.Rms(a.L, 0, common)), lb = Measure.Db(Measure.Rms(b.L, 0, common));
                PedalReport.Report($"Pedal Juno106{(midi ? " (tracks and MIDI)" : "")}: blocks of 256 and 117 differ by up to {Measure.Db(worst):F1} dBFS from {first / (double)Rate:F3} s (its drift steps once a block): pitch {Signed(Measure.Cents(cb, ca), 3)} cents, level {Signed(lb - la, 3)} dB apart");
                Assert.That(Measure.Cents(cb, ca), Is.EqualTo(0).Within(0.1));
                Assert.That(lb - la, Is.EqualTo(0).Within(0.05));
                return;
            }
            string who = s.Name + (midi ? " (tracks and MIDI)" : "");
            PedalReport.Report(differ == 0 ? $"{who}: blocks of 256 and 117 give the same output, bit for bit"
                : $"{who}: blocks of 256 and 117 differ in {differ} samples from {first / (double)Rate:F3} s, by up to {Measure.Db(worst):F1} dBFS");
            Assert.That(differ, Is.EqualTo(0), "the same output, whatever the block size");
        }

        static void CheckFinite(List<string> problems, string what, (float[] L, float[] R) o)
        {
            double pk = Math.Max(Measure.Peak(o.L, 0, o.L.Length), Measure.Peak(o.R, 0, o.R.Length));
            if (o.L.Concat(o.R).Any(x => !float.IsFinite(x))) problems.Add($"{what}: NaN or infinity");
            else if (pk > 1000) problems.Add($"{what}: {Measure.Db(pk):F1} dBFS");
        }

        [TestCaseSource(nameof(Synths))]
        public void EveryParameterAtEachEndStaysFinite(Synth s)
        {
            int frames = Rate;
            var chord = Events((0, r => r.Note(0, 48)), (0, r => r.Note(1, 60)), (0, r => r.Note(2, 107)), (0, r => r.Note(3, 12)),
                               (Rate / 2, r => r.Off(0)), (Rate / 2, r => r.Off(2)));
            var problems = new List<string>();
            void Check(string what, (float[] L, float[] R) o) => CheckFinite(problems, what, o);
            var probe = new SynthRunner(s);
            foreach (var p in s.Parameters(probe.Machine))
            {
                var decl = p.GetCustomAttribute<ParameterDecl>()!;
                if (p.SetMethod == null) continue;
                var ends = p.PropertyType == typeof(bool) ? new[] { 0, 1 }
                    : new[] { decl.MinValue, decl.ValueDescriptions != null && decl.MaxValue < decl.ValueDescriptions.Length - 1 ? decl.ValueDescriptions.Length - 1 : decl.MaxValue };
                foreach (int v in ends)
                    Check($"{p.Name} = {v}", SynthRunner.Render(s, frames, 256, chord, m => { if (p.PropertyType == typeof(bool)) p.SetValue(m, v != 0); else p.SetValue(m, v); }));
            }
            PedalReport.Report($"{s.Name}: every parameter alone at each end: {(problems.Count == 0 ? "finite, under +60 dBFS" : string.Join("; ", problems))}");
            Assert.That(problems, Is.Empty, string.Join("; ", problems));
        }

        /// <summary>MIDI at its ends: the lowest and highest keys, velocity 1 and 127, the bend and the mod wheel at each end, with every parameter at its top.</summary>
        [TestCaseSource(nameof(Synths))]
        public void MidiAtItsEndsStaysFinite(Synth s)
        {
            var problems = new List<string>();
            foreach (bool tops in new[] { false, true })
                foreach (var (what, ctrl, value) in new[] { ("bend down", 0xE0, 0), ("bend up", 0xE0, 16383), ("mod wheel", 1, 127) })
                {
                    Action<object> top = !tops ? null : m =>
                    {
                        foreach (var p in s.Parameters(m))
                        {
                            var d = p.GetCustomAttribute<ParameterDecl>()!;
                            if (p.SetMethod == null || p.PropertyType != typeof(int) || d.IsWaveNumber) continue;
                            p.SetValue(m, d.ValueDescriptions != null && d.MaxValue < d.ValueDescriptions.Length - 1 ? d.ValueDescriptions.Length - 1 : d.MaxValue);
                        }
                    };
                    CheckFinite(problems, (tops ? "every parameter at its top, " : "") + what, SynthRunner.Render(s, Rate, 256,
                        Events((0, r => r.Control(ctrl, value)), (0, r => r.Midi(127, 127)), (0, r => r.Midi(0, 127)), (0, r => r.Midi(1, 1)),
                               (Rate / 2, r => r.Midi(127, 0))), top));
                }
            PedalReport.Report($"{s.Name}: MIDI at its ends (keys 0 and 127, velocities 1 and 127, the bend and the mod wheel at their ends): {(problems.Count == 0 ? "finite, under +60 dBFS" : string.Join("; ", problems))}");
            Assert.That(problems, Is.Empty, string.Join("; ", problems));
        }

        // ── Glides ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The parameters that glide, each stepped between two values while a note holds: (synth, property, from, to,
        /// gain) — a gain (about 6 dB here) is measured as one (the output over the output with the parameter held), the
        /// others by how they come in (below). The setting around them makes the output what the parameter scales, where
        /// it's a gain.
        /// </summary>
        static IEnumerable<TestCaseData> GlideCases()
        {
            TestCaseData C(string synth, string p, int from, int to, bool gain, string setup = "") =>
                new TestCaseData(synth, p, from, to, gain, setup).SetName($"{synth} {p} {from} to {to}");
            yield return C("Pedal Juno106", "VcaLevel", 100, 50, true, "VcfFrequency=55 VcfEnvAmount=0 VcfResonance=0");   // a smooth wave
            // Into the filter's soft clip, which shapes a square's edges by its level: measured as it comes in.
            yield return C("Pedal Juno106", "DcoSubLevel", 60, 127, false, "DcoSawOn=0 VcfFrequency=60 VcfEnvAmount=0 VcfResonance=0");
            // The filter at 6.4 kHz, under the edge's top (the old defaults' VCF Reso 30 rang at 19.8 kHz; open, at the
            // new defaults, the step's first 5 ms measure −4.3 dB, the edge sharp as it starts to move).
            yield return C("Pedal Juno106", "DcoPulseWidth", 0, 127, false, "DcoSawOn=0 DcoPulseOn=1 VcfFrequency=70 VcfEnvAmount=0");
            yield return C("Pedal Juno106", "VcfFrequency", 40, 90, false, "VcfEnvAmount=0");
            yield return C("Pedal Juno106", "VcfResonance", 0, 110, false, "VcfEnvAmount=0 VcfFrequency=70");
            yield return C("Pedal SH101", "Volume", 96, 48, true);
            yield return C("Pedal SH101", "PulseLevel", 100, 50, true);
            yield return C("Pedal SH101", "SawLevel", 60, 127, true, "PulseLevel=0");
            yield return C("Pedal SH101", "SubLevel", 50, 100, true, "PulseLevel=0");
            yield return C("Pedal SH101", "PwmAmount", 0, 127, false);
            yield return C("Pedal SH101", "Cutoff", 50, 100, false);
            yield return C("Pedal SH101", "Resonance", 0, 110, false, "Cutoff=70");
            yield return C("Pedal FM", "OutLevel", 100, 60, true);
            yield return C("Pedal FM", "Op4Level", 127, 60, true, "Op1Level=0 Op2Level=0 Op3Level=0");
            yield return C("Pedal FM", "Op3Level", 70, 0, false);
            yield return C("Pedal M1", "Volume", 100, 50, true);
            yield return C("Pedal M1", "Osc1Level", 100, 40, true);
            yield return C("Pedal M1", "Cutoff", 127, 60, false);
            yield return C("Pedal M1", "Resonance", 0, 127, false, "Cutoff=70");
            yield return C("Pedal Tracker", "MasterParam", 0x8000, 0x4000, true);
        }

        /// <summary>
        /// A step in a gain or level: the output's gain (over 17 samples about each) moves by at most 0.05 dB from one sample
        /// to the next (a step moves it its whole way over those 17). A step in a cutoff, width, level into a filter or
        /// modulator: what it changes in the output comes in over the glide — its first 5 ms at least 5 dB under 20–40 ms
        /// on (stepped, it is there at once: −1.7 … +0.3 dB; gliding, −7.7 dB for a pulse width, whose edge moves, to −23 dB).
        /// Then the output lands where a machine set there plays (reported).
        /// </summary>
        [TestCaseSource(nameof(GlideCases))]
        public void ASteppedParameterGlides(string name, string property, int from, int to, bool gain, string setup)
        {
            var s = Synth.Named(name);
            int frames = Rate, step = Rate / 2 + 128;   // a block's start, mid-note (a tick's would do as well)
            Action<object> set = m =>
            {
                foreach (var kv in setup.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = m.GetType().GetProperty(kv.Split('=')[0])!;
                    int v = int.Parse(kv.Split('=')[1]);
                    if (p.PropertyType == typeof(bool)) p.SetValue(m, v != 0); else p.SetValue(m, v);
                }
                var q = m.GetType().GetProperty(property)!;
                q.SetValue(m, from);
            };
            var note = Events((0, r => r.Note(0, 57)));
            var held = SynthRunner.Render(s, frames, 256, note, set).L;
            var movedEvents = Events((0, r => r.Note(0, 57)), (step, r => r.Set(property, to)));
            var moved = SynthRunner.Render(s, frames, 256, movedEvents, set).L;
            for (int i = 0; i < step; i++) Assert.That(moved[i], Is.EqualTo(held[i]), "the same until the step");
            int glideEnd = step + (int)(0.020 * Rate);
            string result;
            double worst;
            if (gain)
            {
                // The gain the parameter applies, sample by sample: moved over held by least squares over 17 samples
                // around each (a wave's own zero crossings and edges drop out), where held has the energy to say.
                double pk = Measure.Peak(held, step, Rate / 10);
                const int h = 8;
                double prev = double.NaN;
                worst = 0;
                for (int i = step - 64; i < glideEnd + 1000; i++)
                {
                    double mh = 0, hh = 0;
                    for (int k = i - h; k <= i + h; k++) { mh += moved[k] * held[k]; hh += held[k] * held[k]; }
                    if (hh < (2 * h + 1) * 0.01 * pk * pk) { prev = double.NaN; continue; }   // under −20 dB of its peak
                    double g = Measure.Db(Math.Abs(mh / hh));
                    if (!double.IsNaN(prev)) worst = Math.Max(worst, Math.Abs(g - prev));
                    prev = g;
                }
                result = $"at most {worst:F4} dB between two samples";
            }
            else
            {
                // What the step changes in the output (moved − held), its RMS over its first 5 ms against 20–40 ms on (a
                // stepped change is there at once: about 0 dB; one gliding over 20 ms about −17 dB, if what it changes
                // grows with it, or −8 dB for a moving edge, whose difference grows as its square root).
                int ms = Rate / 1000;
                double early = 0, late = 0;
                for (int i = step; i < step + 5 * ms; i++) early += (moved[i] - held[i]) * (moved[i] - held[i]);
                for (int i = step + 20 * ms; i < step + 40 * ms; i++) late += (moved[i] - held[i]) * (moved[i] - held[i]);
                worst = Math.Sqrt((early / 5) / Math.Max(late / 20, 1e-30));
                result = $"its change's first 5 ms at {Measure.Db(worst):F1} dB against 20–40 ms on";
            }
            // After the glide, it plays what a machine set there from the start plays (a gain after the voices, exactly;
            // before them, as their filters' states converge).
            var other = SynthRunner.Render(s, frames, 256, note, m => { set(m); m.GetType().GetProperty(property)!.SetValue(m, to); }).L;
            double after = 0;
            for (int i = frames - Rate / 5; i < frames; i++) after = Math.Max(after, Math.Abs(moved[i] - other[i]));
            PedalReport.Report($"{name}: {property} stepped {from} → {to}: {result}; " +
                               $"after the glide {(after == 0 ? "the same as set from the start" : $"within {Measure.Db(after):F1} dBFS of it")}");
            Assert.That(gain ? worst : Measure.Db(worst), Is.LessThan(gain ? 0.05 : -5), "a glide, not a step");
        }

        /// <summary>
        /// A setting changed while the machine is silent (a preset loaded, a parameter set between phrases) needs no glide:
        /// the next note starts at it, as when it was set well before (the glide would sweep that note's first 20 ms from
        /// the old setting). The same samples: the voices keep what the first note left them either way.
        /// </summary>
        [TestCaseSource(nameof(GlideCases))]
        public void ASettingChangedInSilenceStartsTheNextNoteAtIt(string name, string property, int from, int to, bool gain, string setup)
        {
            var s = Synth.Named(name);
            int again = Rate / 2 + Tail(s) + 1000, frames = again + Rate / 2;
            Action<object> set = m =>
            {
                foreach (var kv in setup.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = m.GetType().GetProperty(kv.Split('=')[0])!;
                    int v = int.Parse(kv.Split('=')[1]);
                    if (p.PropertyType == typeof(bool)) p.SetValue(m, v != 0); else p.SetValue(m, v);
                }
                m.GetType().GetProperty(property)!.SetValue(m, from);
            };
            // A note, silence, and the setting changed a tenth of a second before the next note, or with it (in the block
            // the note starts). Both set it at both times, to split their blocks alike (Juno106's drift steps by blocks).
            int early = again - Rate / 10;
            float[] Phrase(int earlyValue) => SynthRunner.Render(s, frames, 256,
                Events((0, r => r.Note(0, 57)), (Rate / 2, r => r.Off(0)), (early, r => r.Set(property, earlyValue)),
                       (again, r => r.Set(property, to)), (again, r => r.Note(0, 57))), set).L;
            var before = Phrase(to);
            var with = Phrase(from);
            Assert.That(Measure.Peak(with, again - Rate / 10, Rate / 10), Is.EqualTo(0), "silent before the second note");
            int n = Rate / 20;
            double diff = 0, level = 0;
            for (int i = again; i < again + n; i++)
            {
                diff += (with[i] - before[i]) * (with[i] - before[i]);
                level += before[i] * before[i];
            }
            double db = diff == 0 ? double.NegativeInfinity : 10 * Math.Log10(diff / level);
            PedalReport.Report($"{name}: {property} {from} → {to} in silence, with the next note: its first 50 ms " +
                               (diff == 0 ? "the same as set before" : $"{db:F1} dB from it"));
            Assert.That(diff, Is.EqualTo(0), "the next note starts at the setting");
        }

        // ── The follow-ups (§12.15) ─────────────────────────────────────────────────────────────────────────────

        /// <summary>The share of a span's energy above <paramref name="hz"/> (a Hann window over 8192 samples).</summary>
        static double ShareAbove(float[] x, int from, double hz)
        {
            const int n = 8192;
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = (float)(x[from + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n)));
            var m = Measure.Spectrum(w, 0, n);
            double all = 0, high = 0;
            for (int k = 1; k < m.Length; k++)
            {
                all += m[k] * m[k];
                if (Measure.BinFrequency(k, n) >= hz) high += m[k] * m[k];
            }
            return all > 0 ? high / all : 0;
        }

        /// <summary>
        /// A new Juno106 (every parameter at its declared default) doesn't ring: held from C2 to C7, no 8192-sample span of
        /// its first second has more than 5 % of its energy above 16 kHz. At the old defaults (VCF Reso 30) the envelope
        /// opened the filter to its ceiling, where the resonance rang on at 0.41 × the rate: 39 % (C2) to 83 % (C7).
        /// </summary>
        [Test]
        public void TheJuno106sDefaultPatchDoesNotRing()
        {
            var s = Synth.Named("Pedal Juno106");
            var worst = new List<string>();
            double most = 0;
            foreach (int key in new[] { 36, 48, 60, 72, 84, 96 })
            {
                var o = SynthRunner.Render(s, Rate, 256, Events((0, r => r.Note(0, key))));
                double share = 0;
                for (int at = 0; at + 8192 <= Rate; at += 8192) share = Math.Max(share, ShareAbove(o.L, at, 16000));
                worst.Add($"{key}: {share * 100:F2} %");
                most = Math.Max(most, share);
            }
            PedalReport.Report($"Pedal Juno106 at its defaults: its energy above 16 kHz, the most in any 8192 samples of a held note's first second, by key: {string.Join(", ", worst)}");
            Assert.That(most, Is.LessThan(0.05), "no ring at the top of the band");
        }
    }
}
