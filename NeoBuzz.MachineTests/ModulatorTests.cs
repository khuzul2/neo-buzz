using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The canvas modulators (docs/specs/neo-patch.md §3, phase NM): the targets' arithmetic (ModBank: base plus depth times output, the range and
    /// safe range, the glide, a changed base, a reload, removal) and the three engines that make the output (LFO, Envelope, Step), measured.
    /// </summary>
    [TestFixture]
    public class ModulatorTests
    {
        /// <summary>A parameter as the bank sees it.</summary>
        sealed class Port : IModPort
        {
            public int Min { get; set; } = 0;
            public int Max { get; set; } = 1000;
            public int SafeMin { get; set; } = 0;
            public int SafeMax { get; set; } = 1000;
            public int Value;
            public int Puts;
            public int Get() => Value;
            public void Put(int raw) { Value = raw; Puts++; }
        }

        static ModBank Bank(Port port, int depth = 50, bool bipolar = false, int rangeMin = ModSlot.None, int rangeMax = ModSlot.None, int curve = 0, double glide = 0.05)
        {
            var bank = new ModBank { GlideSeconds = glide };
            var s = bank.Slots[0];
            s.Used = true;
            s.Port = port;
            s.Depth = depth;
            s.Bipolar = bipolar;
            s.RangeMin = rangeMin;
            s.RangeMax = rangeMax;
            s.Curve = curve;
            return bank;
        }

        const double Dt = 64 / 44100.0;

        [Test]
        public void TheOutputIsAddedToTheBaseByDepthAndSpan()
        {
            var p = new Port { Value = 200 };
            var bank = Bank(p, depth: 50);
            bank.GlideSeconds = 0;                         // no slew: the value lands at once
            bank.Update(1.0, Dt);
            Assert.That(p.Value, Is.EqualTo(700), "base 200 + 50 % of 1000");
            bank.Update(0.0, Dt);
            Assert.That(p.Value, Is.EqualTo(200), "output 0 is the base");
            bank.Slots[0].Bipolar = true;
            bank.Update(0.0, Dt);
            Assert.That(p.Value, Is.EqualTo(0), "bipolar: 0 swings down by the depth (clamped to the range)");
            bank.Update(0.5, Dt);
            Assert.That(p.Value, Is.EqualTo(200), "and the middle is the base again");
            bank.Slots[0].Depth = -50;
            bank.Slots[0].Bipolar = false;
            bank.Update(1.0, Dt);
            Assert.That(p.Value, Is.EqualTo(0), "a negative depth subtracts");
        }

        [Test]
        public void TheRangeAndTheSafeRangeBoundTheResult()
        {
            var p = new Port { Value = 500, SafeMin = 100, SafeMax = 800 };
            var bank = Bank(p, depth: 100, bipolar: true);
            bank.GlideSeconds = 0;
            bank.Update(1.0, Dt);
            Assert.That(p.Value, Is.EqualTo(800), "never past the safe range");
            bank.Update(0.0, Dt);
            Assert.That(p.Value, Is.EqualTo(100));

            var q = new Port { Value = 500 };
            var ranged = Bank(q, depth: 100, bipolar: true, rangeMin: 300, rangeMax: 600);
            ranged.GlideSeconds = 0;
            ranged.Update(1.0, Dt);
            Assert.That(q.Value, Is.EqualTo(600), "a range confines it");
            ranged.Update(0.0, Dt);
            Assert.That(q.Value, Is.EqualTo(300));

            // A base the person put outside the safe range is theirs: the modulation doesn't move it into the range.
            var r = new Port { Value = 950, SafeMin = 100, SafeMax = 800 };
            var outside = Bank(r, depth: 10, bipolar: true);
            outside.GlideSeconds = 0;
            outside.Update(0.5, Dt);
            Assert.That(r.Value, Is.EqualTo(950));
            outside.Update(1.0, Dt);
            Assert.That(r.Value, Is.EqualTo(950), "up is not allowed past where it is");
        }

        [Test]
        public void TheMasterVolumeStyleSafeRangeHoldsAtTheTop()
        {
            // The master's volume is raw 0 (0 dB) … 0x4000; the host's safe range for it is that whole span, never more.
            var p = new Port { Min = 0, Max = 0x4000, SafeMin = 0, SafeMax = 0x4000, Value = 0 };
            var bank = Bank(p, depth: 100, bipolar: true);
            bank.GlideSeconds = 0;
            for (double u = 0; u <= 1.0001; u += 0.05)
            {
                bank.Update(u, Dt);
                Assert.That(p.Value, Is.InRange(0, 0x4000));
            }
        }

        [Test]
        public void NoWriteIsBiggerThanTheGlideAllows()
        {
            var p = new Port { Value = 500 };
            var bank = Bank(p, depth: 100, bipolar: true, glide: 0.1);
            double span = 1000, allowed = span * Dt / 0.1 + 1;
            int last = p.Value, worst = 0;
            for (int i = 0; i < 4000; i++)
            {
                double u = (i / 200) % 2 == 0 ? 1.0 : 0.0;     // square jumps from one end to the other
                bank.Update(u, Dt);
                worst = Math.Max(worst, Math.Abs(p.Value - last));
                last = p.Value;
            }
            Assert.That(worst, Is.LessThanOrEqualTo(allowed), "every step is inside the glide");
            Assert.That(worst, Is.GreaterThan(allowed / 2), "and the glide is used");
        }

        [Test]
        public void AValueWrittenOnlyWhenItChanges()
        {
            var p = new Port { Value = 300 };
            var bank = Bank(p, depth: 50);
            bank.Update(0.3, Dt);
            for (int i = 0; i < 2000; i++) bank.Update(0.3, Dt);
            int puts = p.Puts;
            for (int i = 0; i < 100; i++) bank.Update(0.3, Dt);
            Assert.That(p.Puts, Is.EqualTo(puts), "a steady output writes nothing");
        }

        [Test]
        public void AnEditMovesTheBaseAndRemovingPutsTheBaseBack()
        {
            var p = new Port { Value = 200 };
            var bank = Bank(p, depth: 50);
            bank.GlideSeconds = 0;
            bank.Update(1.0, Dt);
            Assert.That(bank.Slots[0].Base, Is.EqualTo(200));
            p.Value = 100;                                   // a hand, the agent or automation
            bank.Update(0.0, Dt);
            Assert.That(bank.Slots[0].Base, Is.EqualTo(100), "the edit is the base now");
            Assert.That(p.Value, Is.EqualTo(100));
            bank.Update(1.0, Dt);
            Assert.That(p.Value, Is.EqualTo(600));
            bank.Release(0);
            Assert.That(p.Value, Is.EqualTo(100), "removing it restores the base");

            // A value somebody else wrote since stays: it is theirs.
            bank.Update(1.0, Dt);
            p.Value = 321;
            bank.Release(0);
            Assert.That(p.Value, Is.EqualTo(321));
        }

        [Test]
        public void AReloadResumesFromTheSavedBase()
        {
            // The song holds the modulated value (700); the modulator's own state holds the base (200) and what it last wrote.
            var p = new Port { Value = 700 };
            var bank = Bank(p, depth: 50);
            bank.Slots[0].SavedBase = 200;
            bank.Slots[0].SavedApplied = 700;
            bank.GlideSeconds = 0;
            bank.Update(0.0, Dt);
            Assert.That(bank.Slots[0].Base, Is.EqualTo(200), "the base came back");
            Assert.That(p.Value, Is.EqualTo(200));

            // If the value differs from what was saved (the song was edited), what is there is the base.
            var q = new Port { Value = 650 };
            var other = Bank(q, depth: 50);
            other.Slots[0].SavedBase = 200;
            other.Slots[0].SavedApplied = 700;
            other.GlideSeconds = 0;
            other.Update(0.0, Dt);
            Assert.That(other.Slots[0].Base, Is.EqualTo(650));
        }

        [Test]
        public void TheCurveBendsTheResponse()
        {
            Assert.That(ModBank.Bend(0.5, 0), Is.EqualTo(0.5));
            Assert.That(ModBank.Bend(0.5, 100), Is.LessThan(0.5), "a positive curve eases in");
            Assert.That(ModBank.Bend(0.5, -100), Is.GreaterThan(0.5), "a negative one eases out");
            Assert.That(ModBank.Bend(0, 80), Is.EqualTo(0));
            Assert.That(ModBank.Bend(1, 80), Is.EqualTo(1));
        }

        [Test]
        public void TheBankAllocatesNothing()
        {
            var ports = Enumerable.Range(0, 8).Select(i => new Port { Value = 100 * i }).ToArray();
            var bank = new ModBank { GlideSeconds = 0.05 };
            for (int i = 0; i < 8; i++)
            {
                var s = bank.Slots[i];
                s.Used = true;
                s.Port = ports[i];
                s.Depth = 30 + i * 5;
                s.Bipolar = i % 2 == 0;
                s.Curve = i * 10 - 30;
            }
            var (bytes, why) = AllocationCheck.Run(2000, b => bank.Update(0.5 + 0.5 * Math.Sin(b * 0.05), Dt));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        // ------------------------------------------------------------------ the engines

        [Test]
        public void AnLfoIsTheShapeAtTheRateAndStaysInZeroToOne()
        {
            var v = new ParamValues(NeoBuzz.LfoMachine.Params.Table);
            v.Set(NeoBuzz.LfoMachine.P.Rate, NeoBuzz.LfoMachine.Params.Table[NeoBuzz.LfoMachine.P.Rate].Raw(2.0));
            var e = new NeoBuzz.LfoMachine.LfoEngine(v);
            double dt = 64 / 44100.0, t = 0;
            float min = 1, max = 0;
            int crossings = 0;
            float prev = e.Step(dt, 0, false, 120);
            for (int i = 0; i < 44100 * 5 / 64; i++)
            {
                float y = e.Step(dt, 0, false, 120);
                t += dt;
                min = Math.Min(min, y);
                max = Math.Max(max, y);
                if (prev < 0.5f && y >= 0.5f) crossings++;
                prev = y;
            }
            Assert.That(min, Is.GreaterThanOrEqualTo(0).And.LessThan(0.05f));
            Assert.That(max, Is.LessThanOrEqualTo(1).And.GreaterThan(0.95f));
            Assert.That(crossings, Is.InRange(9, 11), "2 Hz for 5 s: ten cycles");
        }

        [Test]
        public void AnLfoSyncedToTheSongLocksToTheBeats()
        {
            var v = new ParamValues(NeoBuzz.LfoMachine.Params.Table);
            v.Set(NeoBuzz.LfoMachine.P.Sync, 6);                    // 1/4: one cycle a beat
            var e = new NeoBuzz.LfoMachine.LfoEngine(v);
            double dt = 64 / 44100.0;
            var at = new List<float>();
            for (int i = 0; i < 2000; i++)
            {
                double beats = i * dt * 2;                   // 120 BPM: 2 beats a second
                float y = e.Step(dt, beats, true, 120);
                if (Math.Abs(beats - Math.Round(beats)) < dt) at.Add(y);
            }
            Assert.That(at.Count, Is.GreaterThan(5));
            Assert.That(at.Max() - at.Min(), Is.LessThan(0.1f), "the same value on every beat");
        }

        [Test]
        public void ANoteEnvelopeRisesSustainsAndReleases()
        {
            var v = new ParamValues(NeoBuzz.EnvelopeMachine.Params.Table);
            v.Set(NeoBuzz.EnvelopeMachine.P.Attack, NeoBuzz.EnvelopeMachine.Params.Table[NeoBuzz.EnvelopeMachine.P.Attack].Raw(0.01));
            v.Set(NeoBuzz.EnvelopeMachine.P.Decay, NeoBuzz.EnvelopeMachine.Params.Table[NeoBuzz.EnvelopeMachine.P.Decay].Raw(0.05));
            v.Set(NeoBuzz.EnvelopeMachine.P.Sustain, NeoBuzz.EnvelopeMachine.Params.Table[NeoBuzz.EnvelopeMachine.P.Sustain].Raw(0.5));
            v.Set(NeoBuzz.EnvelopeMachine.P.Release, NeoBuzz.EnvelopeMachine.Params.Table[NeoBuzz.EnvelopeMachine.P.Release].Raw(0.05));
            var e = new NeoBuzz.EnvelopeMachine.EnvelopeEngine(v);
            e.SetSampleRate(44100);
            double dt = 64 / 44100.0;
            Assert.That(e.Step(64, dt, 0), Is.EqualTo(0).Within(1e-6));
            e.NoteOn();
            float peak = 0, y = 0;
            for (int i = 0; i < 100; i++) { y = e.Step(64, dt, 0); peak = Math.Max(peak, y); }
            Assert.That(peak, Is.GreaterThan(0.95f), "it reaches the top");
            for (int i = 0; i < 300; i++) y = e.Step(64, dt, 0);
            Assert.That(y, Is.EqualTo(0.5f).Within(0.03f), "and holds the sustain");
            e.NoteOff();
            for (int i = 0; i < 300; i++) y = e.Step(64, dt, 0);
            Assert.That(y, Is.LessThan(0.01f), "and releases");
        }

        [Test]
        public void AFollowerEnvelopeTracksTheLevelAboveTheThreshold()
        {
            var v = new ParamValues(NeoBuzz.EnvelopeMachine.Params.Table);
            v.Set(NeoBuzz.EnvelopeMachine.P.Mode, 1);
            v.Set(NeoBuzz.EnvelopeMachine.P.Threshold, NeoBuzz.EnvelopeMachine.Params.Table[NeoBuzz.EnvelopeMachine.P.Threshold].Raw(-40));
            v.Set(NeoBuzz.EnvelopeMachine.P.Attack, NeoBuzz.EnvelopeMachine.Params.Table[NeoBuzz.EnvelopeMachine.P.Attack].Raw(0.005));
            v.Set(NeoBuzz.EnvelopeMachine.P.Release, NeoBuzz.EnvelopeMachine.Params.Table[NeoBuzz.EnvelopeMachine.P.Release].Raw(0.05));
            var e = new NeoBuzz.EnvelopeMachine.EnvelopeEngine(v);
            e.SetSampleRate(44100);
            double dt = 64 / 44100.0;
            float y = 0;
            for (int i = 0; i < 200; i++) y = e.Step(64, dt, 1.0f);
            Assert.That(y, Is.GreaterThan(0.95f), "full scale is the top");
            for (int i = 0; i < 200; i++) y = e.Step(64, dt, 0.1f);                // -20 dB: halfway from -40 to 0
            Assert.That(y, Is.EqualTo(0.5f).Within(0.03f));
            for (int i = 0; i < 400; i++) y = e.Step(64, dt, 0.0001f);             // -80 dB: below the threshold
            Assert.That(y, Is.LessThan(0.01f));
        }

        [Test]
        public void AStepSequencePlaysItsLevelsInTime()
        {
            var v = new ParamValues(NeoBuzz.StepMachine.Params.Table);
            v.Set(NeoBuzz.StepMachine.P.Steps, 4);
            v.Set(NeoBuzz.StepMachine.P.Rate, 3);                                          // 1/16: a quarter beat
            var levels = new[] { 1.0, 0.25, 0.75, 0.0 };
            for (int i = 0; i < 4; i++) v.Set(NeoBuzz.StepMachine.P.S1 + i, NeoBuzz.StepMachine.Params.Table[NeoBuzz.StepMachine.P.S1 + i].Raw(levels[i]));
            var e = new NeoBuzz.StepMachine.StepEngine(v);
            double dt = 64 / 44100.0;
            for (int s = 0; s < 8; s++)
            {
                double beats = (s % 4) * 0.25 + 0.1;
                float y = 0;
                for (int k = 0; k < 4; k++) y = e.Step(dt, beats + 8 + (s / 4) * 1.0, true, 120);
                Assert.That(y, Is.EqualTo((float)levels[s % 4]).Within(0.02f), "step " + (s % 4 + 1));
            }
        }

        [Test]
        public void TheEnginesAllocateNothing()
        {
            var lv = new ParamValues(NeoBuzz.LfoMachine.Params.Table);
            lv.Set(NeoBuzz.LfoMachine.P.Shape, 6);
            var lfo = new NeoBuzz.LfoMachine.LfoEngine(lv);
            var ev = new ParamValues(NeoBuzz.EnvelopeMachine.Params.Table);
            var env = new NeoBuzz.EnvelopeMachine.EnvelopeEngine(ev);
            env.SetSampleRate(44100);
            var sv = new ParamValues(NeoBuzz.StepMachine.Params.Table);
            sv.Set(NeoBuzz.StepMachine.P.Random, 500);
            var step = new NeoBuzz.StepMachine.StepEngine(sv);
            double dt = 64 / 44100.0;
            float sink = 0;
            var (bytes, why) = AllocationCheck.Run(4000, b =>
            {
                sink += lfo.Step(dt, b * 0.01, true, 120);
                if (b % 500 == 0) env.NoteOn();
                if (b % 500 == 250) env.NoteOff();
                sink += env.Step(64, dt, 0.3f);
                sink += step.Step(dt, b * 0.01, true, 120);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
            Assert.That(float.IsNaN(sink), Is.False);
        }
    }
}
