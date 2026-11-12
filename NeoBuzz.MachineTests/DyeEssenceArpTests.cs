using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Dye Essence P4a (docs/specs/dye-essence.md §4.9), measured: the arpeggiator (modes, octaves, rates, feels, gate, swing,
    /// latch, the seeded random), the step sequencer (notes, ties, rests, probabilities, the modulation sources), both on the
    /// song's grid whatever the block size, live MPE (per-note bend, pressure and slide), and no allocation.
    /// </summary>
    [TestFixture]
    public class DyeEssenceArpTests
    {
        const int Rate = Measure.Rate;

        sealed class Rec : IDriverOut
        {
            public readonly List<(long At, bool On, int Note)> Events = new();
            public readonly List<(long At, float Step, float Vel)> Seq = new();
            public Func<long> Now;
            public void Play(int key, int note, float velocity) => Events.Add((Now(), true, note));
            public void Stop(int key) => Events.Add((Now(), false, key - NoteDriver.KeyBase));
            public void SeqValues(float step, float velocity) => Seq.Add((Now(), step, velocity));
            public long[] Ons() => Events.Where(e => e.On).Select(e => e.At).ToArray();
            public int[] Notes() => Events.Where(e => e.On).Select(e => e.Note).ToArray();
        }

        sealed class Rig
        {
            public readonly ParamValues V = new(Params.Table);
            public readonly DyeEngine E;
            public readonly Rec Rec = new();
            public long Pos;
            readonly float[] l = new float[NoteBank.MaxBlock], r = new float[NoteBank.MaxBlock];

            public Rig(Action<ParamValues> set = null, LaneKind kind = LaneKind.Auto)
            {
                for (int i = 0; i < TableBank.Count; i++) TableBank.GetNow(i);
                for (int i = 0; i < SampleSources.Count; i++) SampleSources.GetNow(i);
                set?.Invoke(V);
                E = new DyeEngine(V, kind);
                E.SetSampleRate(Rate);
                E.Tempo = 120;
                Rec.Now = () => Pos + E.EventAt;
                E.Tap = Rec;
                Block(64);   // the modes take hold at a block's start
                E.ResetClock();
                Pos = 0;
                Rec.Events.Clear();
                Rec.Seq.Clear();
            }

            public float[] Out = new float[0];

            /// <summary>Renders <paramref name="frames"/> in blocks of <paramref name="block"/>, keeping the output (interleaved).</summary>
            public void Run(int frames, int block = 256, Action<long> at = null, Func<long, DriverTransport> song = null)
            {
                var y = new float[2 * frames];
                for (int done = 0, n; done < frames; done += n)
                {
                    at?.Invoke(Pos);
                    if (song != null) E.Transport = song(Pos);
                    n = Math.Min(block, frames - done);
                    E.Process(l, r, n);
                    for (int i = 0; i < n; i++) { y[2 * (done + i)] = l[i]; y[2 * (done + i) + 1] = r[i]; }
                    Pos += n;
                }
                Out = y;
            }

            void Block(int n)
            {
                E.Process(l, r, n);
            }
        }

        static long At(double beats) => (long)Math.Ceiling(beats * Rate * 0.5 - 1e-4);   // 120 BPM: a beat is half a second

        static Action<ParamValues> Arp(int mode, int rate = 1, int octaves = 1, int gate = 600, int feel = 0, int swing = 0, int latch = 0, int seed = 0) => v =>
        {
            v.Set(P.ArpMode, mode);
            v.Set(P.ArpRate, rate);
            v.Set(P.ArpOct, octaves - 1);
            v.Set(P.ArpGate, gate);
            v.Set(P.ArpFeel, feel);
            v.Set(P.ArpSwing, swing);
            v.Set(P.ArpLatch, latch);
            v.Set(P.ArpSeed, seed);
        };

        static void Hold(Rig g, params int[] notes)
        {
            for (int i = 0; i < notes.Length; i++) g.E.NoteOn(1000 + notes[i], notes[i], 0.8f);
        }

        [Test]
        public void TheModesPlayTheirOrders()
        {
            int[] chord = { 55, 48, 52 };   // pressed G, C, E
            var want = new Dictionary<int, int[]>
            {
                [(int)ArpMode.Up] = new[] { 48, 52, 55, 48, 52, 55, 48 },
                [(int)ArpMode.Down] = new[] { 55, 52, 48, 55, 52, 48, 55 },
                [(int)ArpMode.UpDown] = new[] { 48, 52, 55, 52, 48, 52, 55 },
                [(int)ArpMode.Played] = new[] { 55, 48, 52, 55, 48, 52, 55 },
            };
            foreach (var (mode, notes) in want)
            {
                var g = new Rig(Arp(mode));
                Hold(g, chord);
                g.Run(Rate);
                Assert.That(g.Rec.Notes().Take(notes.Length).ToArray(), Is.EqualTo(notes), ((ArpMode)mode).ToString());
            }
        }

        [Test]
        public void TheOctaveRangeStacksOctavesUpwards()
        {
            var g = new Rig(Arp((int)ArpMode.Up, octaves: 2));
            Hold(g, 48, 52, 55);
            g.Run(2 * Rate);
            Assert.That(g.Rec.Notes().Take(8).ToArray(), Is.EqualTo(new[] { 48, 52, 55, 60, 64, 67, 48, 52 }));
            var d = new Rig(Arp((int)ArpMode.UpDown, octaves: 2));
            Hold(d, 48, 52);
            d.Run(2 * Rate);
            Assert.That(d.Rec.Notes().Take(8).ToArray(), Is.EqualTo(new[] { 48, 52, 60, 64, 60, 52, 48, 52 }));
            var c = new Rig(Arp((int)ArpMode.Chord, octaves: 2));
            Hold(c, 48, 52);
            c.Run(Rate);
            Assert.That(c.Rec.Notes().Take(6).ToArray(), Is.EqualTo(new[] { 48, 52, 60, 64, 48, 52 }), "chord: the whole chord, up an octave every second step");
        }

        [Test]
        public void StepsLandOnTheGridInEveryRateAndFeel()
        {
            // 1/32, 1/16, 1/8, 1/4, 1/2, 1 bar; straight, dotted, triplet.
            double[] beats = { 0.125, 0.25, 0.5, 1, 2, 4 };
            double[] feel = { 1, 1.5, 2.0 / 3.0 };
            for (int rate = 0; rate < 6; rate++)
                for (int f = 0; f < 3; f++)
                {
                    var g = new Rig(Arp((int)ArpMode.Up, rate, feel: f));
                    Hold(g, 60);
                    double step = beats[rate] * feel[f];
                    g.Run((int)(Rate * (step * 0.5 * 5 + 0.1)));
                    var ons = g.Rec.Ons();
                    Assert.That(ons.Length, Is.GreaterThanOrEqualTo(5));
                    for (int k = 0; k < 5; k++) Assert.That(ons[k], Is.EqualTo(At(k * step)), $"rate {rate} feel {f} step {k}");
                }
        }

        [Test]
        public void TheGateSetsTheNoteLengthAndSwingDelaysEveryOtherStep()
        {
            var g = new Rig(Arp((int)ArpMode.Up, 1, gate: 250, swing: 1000));
            Hold(g, 60);
            g.Run(Rate);
            double step = 0.25;
            var ev = g.Rec.Events;
            // Step k starts at k * step (odd ones half a step late at 100%), ends a quarter of a step later.
            var starts = ev.Where(e => e.On).Select(e => e.At).Take(4).ToArray();
            Assert.That(starts, Is.EqualTo(new[] { At(0), At(step * 1.5), At(2 * step), At(3 * step + 0.5 * step) }));
            var offs = ev.Where(e => !e.On).Select(e => e.At).Take(3).ToArray();
            Assert.That(offs, Is.EqualTo(new[] { At(0.25 * step), At(step * 1.5 + 0.25 * step), At(2 * step + 0.25 * step) }));
        }

        [Test]
        public void ALatchKeepsTheNotesUntilTheNextKeyReplacesThem()
        {
            var g = new Rig(Arp((int)ArpMode.Up, latch: 1));
            Hold(g, 48, 52);
            g.Run(Rate / 2);
            for (int k = 0; k < 2; k++) g.E.NoteOff(1000 + (k == 0 ? 48 : 52));
            g.Run(Rate);
            var notes = g.Rec.Notes();
            Assert.That(notes.Skip(2).Take(4).ToArray(), Is.EqualTo(new[] { 48, 52, 48, 52 }), "the notes go on after the keys are up");
            g.Rec.Events.Clear();
            g.E.NoteOn(1067, 67, 0.8f);
            g.Run(Rate);
            Assert.That(g.Rec.Notes().Distinct().ToArray(), Is.EqualTo(new[] { 67 }), "a new key replaces the latched notes");
        }

        [Test]
        public void TheRandomOrderIsSeededAndTheSameInEveryBlockSize()
        {
            int[] Run(int seed, int block)
            {
                var g = new Rig(Arp((int)ArpMode.Random, 1, octaves: 1, seed: seed));
                Hold(g, 48, 52, 55, 59);
                g.Run(2 * Rate, block);
                return g.Rec.Notes();
            }
            var a = Run(5, 256);
            Assert.That(a.Length, Is.GreaterThanOrEqualTo(8));
            Assert.That(Run(5, 117), Is.EqualTo(a), "blocks of 117");
            Assert.That(Run(5, 16), Is.EqualTo(a), "blocks of 16");
            Assert.That(Run(6, 256), Is.Not.EqualTo(a), "another seed");
            Assert.That(a.Distinct().Count(), Is.GreaterThan(2), "it does wander");
            Assert.That(a.All(n => n == 48 || n == 52 || n == 55 || n == 59));
        }

        static DriverTransport Song(long pos, int samplesPerTick = 6000, int ticksPerBeat = 4, long start = 0) => new()
        {
            Playing = true,
            Tick = (int)((pos + start) / samplesPerTick),
            PosInTick = (int)((pos + start) % samplesPerTick),
            SamplesPerTick = samplesPerTick,
            TicksPerBeat = ticksPerBeat,
        };

        [Test]
        public void WhileTheSongPlaysStepsFollowItAndTheBlockSizeChangesNothing()
        {
            // A tick is 6000 samples (4 a beat: 120 BPM at 48 kHz); the song is 3 ticks in when the key goes down.
            long[] Run(int block)
            {
                var g = new Rig(Arp((int)ArpMode.Up, 1));
                g.E.NoteOn(1060, 60, 0.8f);
                g.Run(2 * Rate, block, null, p => Song(p, start: 3 * 6000 + 17));
                return g.Rec.Ons();
            }
            var a = Run(256);
            // 1/16 = one tick; the song is 17 samples past tick 3: the next step is tick 4, 5983 samples in.
            Assert.That(a.Take(4).ToArray(), Is.EqualTo(new long[] { 5983, 11983, 17983, 23983 }));
            Assert.That(Run(117), Is.EqualTo(a));
            Assert.That(Run(200), Is.EqualTo(a));
            Assert.That(Run(1), Is.EqualTo(a).Within(0), "single samples");
        }

        [Test]
        public void TheSameNotesGiveTheSameSamplesInEveryBlockSizeAndTheSameBitsOnEveryLaneImplementation()
        {
            float[] Render(int block, LaneKind kind)
            {
                var g = new Rig(v =>
                {
                    Arp((int)ArpMode.UpDown, 2, octaves: 1, gate: 700, swing: 300)(v);
                    v.Set(P.AType, (int)SlotType.Analog);
                    v.Set(P.AShape, 2);
                    v.Set(P.AUnison, 3);
                }, kind);
                Hold(g, 48, 55, 60);
                g.Run(2 * Rate, block);
                return g.Out;
            }
            ulong Hash(float[] y)
            {
                ulong h = 1469598103934665603UL;
                foreach (var x in y) h = (h ^ BitConverter.SingleToUInt32Bits(x)) * 1099511628211UL;
                return h;
            }
            var want = Render(256, LaneKind.Auto);
            double worst = 0;
            foreach (int block in new[] { 117, 16, 200 })
            {
                var y = Render(block, LaneKind.Auto);
                for (int i = 0; i < y.Length; i++) worst = Math.Max(worst, Math.Abs(y[i] - want[i]));
            }
            // Notes land on the same samples; what may differ is the tail of a silent bank (it rests after 100 ms of silence, decided at
            // a block's end), a few parts in 10^15 of full scale.
            Assert.That(worst, Is.LessThan(1e-9), "blocks of 117, 16, 200");
            Assert.That(Hash(Render(256, LaneKind.Scalar)), Is.EqualTo(Hash(want)), "scalar lanes");
            if (Avx2Lanes.Supported) Assert.That(Hash(Render(256, LaneKind.Avx2)), Is.EqualTo(Hash(want)), "AVX2 lanes");
            Measure.Report($"Dye Essence arpeggiator (up-down, 1/8, swing 30%, 3 unison): 0x{Hash(want):X16}; blocks of 117, 16 and 200 differ by {Measure.Db(worst):F0} dBFS at most (the resting bank's tails); lane implementations equal");
        }

        static Action<ParamValues> Seq(int mode, int length = 16, int rate = 1, int seed = 0, int slew = 0, int swing = 0) => v =>
        {
            v.Set(P.SeqMode, mode);
            v.Set(P.SeqLength, length - 1);
            v.Set(P.SeqRate, rate);
            v.Set(P.ArpSeed, seed);
            v.Set(P.SeqSlew, slew);
            v.Set(P.SeqSwing, swing);
        };

        [Test]
        public void TheSequencerPlaysItsStepsOnTheLastKeyWithOffsetsRestsAndTies()
        {
            // 4 steps: root, +7, a rest, +12 tied to the next step (which is +12 again: the same note goes on), then -5 legato.
            var g = new Rig(Seq((int)SeqMode.Notes, 5));
            g.E.SetPattern(SeqPattern.Parse("0,100,50 7,90,50 0,0 12,100,100,1 12,100,50,0"));
            Hold(g, 48, 60);
            g.Run(Rate * 2);
            var notes = g.Rec.Events.Where(e => e.On).Select(e => e.Note).Take(5).ToArray();
            Assert.That(notes, Is.EqualTo(new[] { 60, 67, 72, 60, 67 }), "step 3 is a rest; the tied 72 isn't played again; the sequence loops");
            var ev = g.Rec.Events;
            Assert.That(ev.Count(e => e.On && e.Note == 72 && e.At < At(1.2)), Is.EqualTo(1), "tie to the same note: no retrigger");
            long end72 = ev.First(e => !e.On && e.Note == 72).At;
            Assert.That(end72, Is.EqualTo(At(4 * 0.25 + 0.25 * 0.5)), "the tied note ends with the next step's gate (50 % of step 5)");
            // A change of pitch under a tie is legato: the new note first, then the old one ends.
            var h = new Rig(Seq((int)SeqMode.Notes, 2));
            h.E.SetPattern(SeqPattern.Parse("0,100,100,1 5,100,50"));
            Hold(h, 60);
            h.Run(Rate / 2);
            var e0 = h.Rec.Events.Take(3).ToArray();
            Assert.That(e0[0], Is.EqualTo((0L, true, 60)));
            Assert.That(e0[1].On && e0[1].Note == 65 && e0[2].On == false && e0[2].Note == 60 && e0[1].At == e0[2].At, Is.True, "legato: on 65 then off 60, together");
        }

        [Test]
        public void StepProbabilitiesAreSeededAndRepeatable()
        {
            string pattern = string.Join(" ", Enumerable.Range(0, 32).Select(i => "0,100,50,0,50"));
            int[] Run(int seed, int block)
            {
                var g = new Rig(Seq((int)SeqMode.Notes, 32, 0, seed));
                g.E.SetPattern(SeqPattern.Parse(pattern));
                Hold(g, 60);
                g.Run(2 * Rate, block);
                return g.Rec.Ons().Select(x => (int)(x / 3000)).ToArray();   // which 1/32 step (3000 samples) each note is on
            }
            var a = Run(1, 256);
            Assert.That(a.Length, Is.InRange(10, 22), "about half of 32 steps");
            Assert.That(Run(1, 117), Is.EqualTo(a));
            Assert.That(Run(2, 256), Is.Not.EqualTo(a));
            Measure.Report($"Dye Essence sequencer: 50 % probability, {a.Length} of 32 steps play (seed 1)");
        }

        [Test]
        public void TheSequencerAlsoRunsAsTwoModulationSources()
        {
            var g = new Rig(v =>
            {
                Seq((int)SeqMode.Mod, 4)(v);
                v.Set(P.AType, (int)SlotType.Analog);
                v.Set(P.AShape, 0);
                v.Set(P.R1Src, (int)RouteSource.SeqStep); v.Set(P.R1Dst, (int)RouteDest.Pitch); v.Set(P.R1Amt, 1000);
                v.Set(P.R2Src, (int)RouteSource.SeqVelocity); v.Set(P.R2Dst, (int)RouteDest.Amp); v.Set(P.R2Amt, 0);
            });
            g.E.SetPattern(SeqPattern.Parse("0,100 12,50 -12,127 24,0"));
            g.E.NoteOn(1, 69, 1f);   // a track note: unaffected by the driver in Mod mode
            g.Run(Rate);
            Assert.That(g.Rec.Seq.Count, Is.GreaterThanOrEqualTo(4));
            var s = g.Rec.Seq.Take(4).ToArray();
            Assert.That(s.Select(x => x.Step).ToArray(), Is.EqualTo(new[] { 0f, 0.5f, -0.5f, 1f }));
            Assert.That(s.Select(x => (int)Math.Round(x.Vel * 127)).ToArray(), Is.EqualTo(new[] { 100, 50, 127, 0 }));
            Assert.That(g.Rec.Events.Count(e => e.On), Is.EqualTo(0), "Mod mode plays no notes");
            // The pitch really follows: step 2 is 12/24 = 0.5, and 100 % of Pitch is 12 st: +6 st, measured by the tone.
            double Tone(float[] y, double hz, int from, int len)
            {
                double re = 0, im = 0, w = 2 * Math.PI * hz / Rate;
                for (int i = 0; i < len; i++) { double x = y[2 * (from + i)] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / len)); re += x * Math.Cos(w * i); im += x * Math.Sin(w * i); }
                return Math.Sqrt(re * re + im * im);
            }
            int stepLen = Rate / 8;   // 1/16 at 120 BPM
            double b = Tone(g.Out, 440 * Math.Pow(2, 0.5), stepLen + 500, stepLen - 1000);
            Assert.That(b, Is.GreaterThan(20 * Tone(g.Out, 440, stepLen + 500, stepLen - 1000)), "step 2: +6 st, not 440 Hz");
        }

        [Test]
        public void TheSourcesGlideWithTheSlewAndStopWhenTheSequencerIsOff()
        {
            var g = new Rig(Seq((int)SeqMode.Mod, 2, 3, slew: 1000));
            g.E.SetPattern(SeqPattern.Parse("0,0 24,127"));
            g.Run(Rate / 8, 64);   // a quarter note is half a second; the first step ends it
            g.Run(Rate / 2, 64);
            float mid = g.E.Bank.SeqStepNow;
            Assert.That(g.E.Bank.S.SeqVel, Is.InRange(0f, 1f));
            Assert.That(mid, Is.GreaterThan(0f).And.LessThan(1f), "gliding between 0 and 1, not at a step's value");
            g.V.Set(P.SeqMode, 0);
            g.Run(512);
            Assert.That(g.E.Bank.SeqStepNow, Is.EqualTo(0f), "off: no source");
        }

        [Test]
        public void TheKeysSoundAsPlayedWhenBothAreOffAndAreHandedBackWhenTheArpStops()
        {
            var g = new Rig();
            Hold(g, 60);
            g.Run(2000);
            Assert.That(g.E.ActiveNotes, Is.EqualTo(1));
            Assert.That(g.Rec.Events.Count, Is.EqualTo(0), "nothing runs with both off");
            g.V.Set(P.ArpMode, (int)ArpMode.Up);
            g.Run(512);
            g.E.AllNotesOff(true);
            g.Run(2000);
            Assert.That(g.E.ActiveNotes, Is.EqualTo(0));
            var h = new Rig(Arp((int)ArpMode.Up));
            Hold(h, 60);
            h.Run(1000);
            h.V.Set(P.ArpMode, (int)ArpMode.Off);
            h.Run(512);
            h.Run(Rate * 3);
            Assert.That(h.E.ActiveNotes, Is.EqualTo(1), "the held key plays as it is, not through the arpeggiator");
        }

        [Test]
        public void SequencesParseAndWriteAndRejectNonsense()
        {
            var p = SeqPattern.Parse("3 -5,90,60,1,40 0,0");
            Assert.That(p.Offset[0], Is.EqualTo(3));
            Assert.That(p.Offset[1], Is.EqualTo(-5));
            Assert.That(p.Velocity[1], Is.EqualTo(90));
            Assert.That(p.Gate[1], Is.EqualTo(60));
            Assert.That(p.Tie[1], Is.True);
            Assert.That(p.Prob[1], Is.EqualTo(40));
            Assert.That(p.Velocity[2], Is.EqualTo(0), "a rest");
            Assert.That(SeqPattern.Parse(p.ToString()).SameAs(p), Is.True, p.ToString());
            Assert.That(SeqPattern.Default.ToString(), Is.EqualTo(""));
            Assert.That(SeqPattern.TryParse("a", out _, out string err), Is.False);
            Assert.That(err, Does.Contain("step 1"));
            Assert.That(SeqPattern.TryParse(string.Join(" ", Enumerable.Repeat("0", 33)), out _, out _), Is.False);
            Assert.That(SeqPattern.Parse("999,999,999,0,999").Offset[0], Is.EqualTo(48), "clamped");
        }

        // ---- live MPE -----------------------------------------------------------------------------------------

        static double Tone(float[] y, double hz, int from, int n)
        {
            double re = 0, im = 0, w = 2 * Math.PI * hz / Rate;
            for (int i = 0; i < n; i++)
            {
                double x = y[2 * (from + i)] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
                re += x * Math.Cos(w * i);
                im += x * Math.Sin(w * i);
            }
            return Math.Sqrt(re * re + im * im);
        }

        static Action<ParamValues> Sine(int mpe = 1, int range = 12) => v =>
        {
            v.Set(P.AType, (int)SlotType.Analog);
            v.Set(P.AShape, 0);
            v.Set(P.AUnison, 1);
            v.Set(P.ARandom, 0);
            v.Set(P.F1Type, 0);
            v.Set(P.F2Type, 0);
            v.Set(P.AmpS, 1000);
            v.Set(P.MpeMode, mpe);
            v.Set(P.MpeBend, range);
        };

        [Test]
        public void EachMemberChannelBendsItsOwnNote()
        {
            var g = new Rig(Sine());
            g.E.NoteOn(1069, 69, 0.8f, 1);    // 440 Hz on channel 2 (index 1)
            g.E.NoteOn(1081, 81, 0.8f, 2);    // 880 Hz on channel 3 (index 2)
            Assert.That(g.E.Control(1, 0xE0, 8192 + 4096), Is.True, "member bend is taken");
            g.Run(Rate / 2);
            double n = Rate / 4;
            double bent = Tone(g.Out, 440 * Math.Pow(2, 0.5), Rate / 8, (int)n);   // +6 st of the 12 st range
            double wrong = Tone(g.Out, 440, Rate / 8, (int)n);
            double other = Tone(g.Out, 880, Rate / 8, (int)n), otherWrong = Tone(g.Out, 880 * Math.Pow(2, 0.5), Rate / 8, (int)n);
            Assert.That(bent, Is.GreaterThan(30 * wrong), "channel 2's note moved up 6 st");
            Assert.That(other, Is.GreaterThan(30 * otherWrong), "channel 3's note stayed");
            Measure.Report($"Dye Essence MPE: a bent note {Measure.Db(bent / wrong):F0} dB stronger at its new pitch; its neighbour stays ({Measure.Db(other / otherWrong):F0} dB)");
        }

        [Test]
        public void TheBendRangeIsConfigurableAndTheMasterChannelBendsEveryNote()
        {
            var g = new Rig(Sine(1, 2));
            g.E.NoteOn(1069, 69, 0.8f, 1);
            g.E.Control(1, 0xE0, 16383);   // +1 of 2 st
            g.Run(Rate / 2);
            int n = Rate / 4;
            Assert.That(Tone(g.Out, 440 * Math.Pow(2, 2 / 12.0), Rate / 8, n), Is.GreaterThan(30 * Tone(g.Out, 440, Rate / 8, n)));
            // The master channel (index 0) goes the global way, in the Bend range parameter (2 st by default).
            var h = new Rig(Sine(1, 12));
            h.E.NoteOn(1069, 69, 0.8f, 1);
            Assert.That(h.E.Control(0, 0xE0, 16383), Is.False, "the master's bend takes the plain path");
            h.E.PitchBend(1f);
            h.Run(Rate / 2);
            Assert.That(Tone(h.Out, 440 * Math.Pow(2, 2 / 12.0), Rate / 8, n), Is.GreaterThan(30 * Tone(h.Out, 440, Rate / 8, n)));
        }

        [Test]
        public void PressureAndSlideAreModulationSourcesPerNote()
        {
            var g = new Rig(v =>
            {
                Sine()(v);
                v.Set(P.R1Src, (int)RouteSource.MpePressure); v.Set(P.R1Dst, (int)RouteDest.APitch); v.Set(P.R1Amt, 1000);
                v.Set(P.R2Src, (int)RouteSource.MpeSlide); v.Set(P.R2Dst, (int)RouteDest.APitch); v.Set(P.R2Amt, 1000);
            });
            g.E.NoteOn(1069, 69, 0.8f, 1);
            g.E.NoteOn(1081, 81, 0.8f, 2);
            g.E.Control(1, 0xD0, 127);                     // channel 2's pressure, full: its pitch +12 st
            g.E.Control(2, 74, 127);                       // channel 3's slide, full: its pitch +12 st (an octave up)
            g.Run(Rate / 2);
            int n = Rate / 4;
            double c3 = Tone(g.Out, 1760, Rate / 8, n), c2 = Tone(g.Out, 880, Rate / 8, n);
            Assert.That(c3, Is.GreaterThan(30 * Tone(g.Out, 880 * Math.Pow(2, 1 / 12.0), Rate / 8, n)), "slide moved channel 3's note up an octave (880 -> 1760)");
            Assert.That(c2, Is.GreaterThan(30 * Tone(g.Out, 440, Rate / 8, n)), "pressure moved channel 2's note up an octave (440 -> 880), and channel 3's pressure is 0");
            Assert.That(Tone(g.Out, 880, Rate / 8, n), Is.GreaterThan(30 * Tone(g.Out, 1760 * 2, Rate / 8, n)), "channel 2's slide is 0: it didn't move again");
        }

        [Test]
        public void WithMpeOffChannelsAreIgnoredAndPatternNotesAreUnchanged()
        {
            var g = new Rig(Sine(0));
            g.E.NoteOn(1069, 69, 0.8f, 1);
            Assert.That(g.E.Control(1, 0xE0, 16383), Is.False, "no MPE: a bend on a channel is the plain bend");
            g.E.NoteOn(5, 81, 0.8f);   // a pattern track's note
            g.Run(Rate / 2);
            int n = Rate / 4;
            Assert.That(Tone(g.Out, 440, Rate / 8, n), Is.GreaterThan(0));
            Assert.That(Tone(g.Out, 880, Rate / 8, n), Is.GreaterThan(0));
            g.E.NoteOff(1069, 69, 1);
            g.E.NoteOff(5);
            g.Run(Rate);
            Assert.That(g.E.ActiveNotes, Is.EqualTo(0));
        }

        [Test]
        public void MpeNotesStartAndStopPerChannel()
        {
            var g = new Rig(Sine());
            g.E.NoteOn(1060, 60, 0.8f, 1);
            g.E.NoteOn(1060, 60, 0.8f, 2);   // the same note on two channels: two voices
            g.Run(2000);
            Assert.That(g.E.ActiveNotes, Is.EqualTo(2));
            g.E.NoteOff(1060, 60, 1);
            g.Run(Rate * 3);
            Assert.That(g.E.ActiveNotes, Is.EqualTo(1), "only channel 2's note is left");
        }

        // ---- allocation ---------------------------------------------------------------------------------------

        [Test]
        public void NothingIsAllocatedOnTheAudioThread()
        {
            var g = new Rig(v =>
            {
                Arp((int)ArpMode.Random, 0, octaves: 3, gate: 500, swing: 200, seed: 3)(v);
                Seq((int)SeqMode.Mod, 32, 0, slew: 300)(v);
                Sine()(v);
                v.Set(P.R1Src, (int)RouteSource.SeqStep); v.Set(P.R1Dst, (int)RouteDest.F1Cutoff); v.Set(P.R1Amt, 800);
                v.Set(P.R2Src, (int)RouteSource.MpePressure); v.Set(P.R2Dst, (int)RouteDest.ALevel); v.Set(P.R2Amt, 800);
            });
            g.E.SetPattern(SeqPattern.Parse(string.Join(" ", Enumerable.Range(0, 32).Select(i => $"{i % 12 - 6},{20 + i * 3},{30 + i * 2},{i % 3 / 2},{50 + i}"))));
            Hold(g, 48, 52, 55, 59, 62);
            g.E.Tap = null;   // the recorder allocates
            var l = new float[256];
            var r = new float[256];
            for (int i = 0; i < 40; i++) g.E.Process(l, r, 256);
            void Block(int i)
            {
                if (i % 40 == 0) g.E.NoteOn(1000 + 30 + i % 60, 30 + i % 60, 0.8f, i / 40 % 4);
                if (i % 40 == 20) g.E.NoteOff(1000 + 30 + (i - 20) % 60, 30 + (i - 20) % 60, (i - 20) / 40 % 4);
                g.E.Control(1 + i % 3, 0xE0, 4000 + i * 7 % 8000);
                g.E.Control(1 + i % 3, 0xD0, i % 128);
                g.E.Control(1 + i % 3, 74, i * 3 % 128);
                g.V.Set(P.ArpMode, 1 + i / 50 % 6);
                g.V.Set(P.ArpRate, i / 30 % 6);
                g.V.Set(P.ArpFeel, i / 70 % 3);
                g.V.Set(P.SeqMode, i / 90 % 3);
                g.V.Set(P.SeqLength, i % 32);
                g.V.Set(P.ArpLatch, i / 110 % 2);
                g.E.Transport = i % 200 < 100 ? Song(i * 128L) : default;
                g.E.Process(l, r, 1 + i % 256);
            }
            for (int i = 0; i < 200; i++) Block(i);
            var (bytes, why) = AllocationCheck.Run(2000, Block);
            Measure.Report($"Dye Essence arpeggiator, sequencer and MPE: {bytes} bytes allocated over 2000 blocks with modes, rates, feels, latch, the song's transport and MPE controllers moving");
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void TheFastestStepsInTinyBlocksFinish()
        {
            var g = new Rig(v =>
            {
                Arp((int)ArpMode.Chord, 0, octaves: 3, gate: 50)(v);
                Seq((int)SeqMode.Mod, 32, 0)(v);
            });
            g.E.Tempo = 500;
            Hold(g, 36, 40, 43, 47, 50, 53, 57, 60);
            g.Run(Rate, 3);
            Assert.That(g.Rec.Ons().Length, Is.GreaterThan(100));
        }
    }
}
