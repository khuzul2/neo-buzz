using NeoBuzz.Dsp;
using NeoBuzz.EnvelopeMachine;
using NeoBuzz.Patch;

namespace NeoBuzz.MachineTests
{
    /// <summary>The engine side of notes.md G2 (Neo Patch's Vel -> level) and G4 (Neo Envelope's Velocity), and the songs saved before them.</summary>
    [TestFixture]
    public class NoteGapsTests
    {
        const int Rate = Measure.Rate;

        // ------------------------------------------------------------------ G2: Neo Patch

        static double PatchLevel(int velocity, int velLevelRaw)
        {
            var v = new ParamValues(NeoBuzz.Patch.Params.Table);
            v.Set(NeoBuzz.Patch.P.VelLevel, velLevelRaw);
            v.Set(NeoBuzz.Patch.P.Voices, 8);
            var e = new PatchEngine(v);
            e.SetSampleRate(Rate);
            e.SetGraph(PatchPresets.Build(0));
            e.Prepare();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (e.Runtime.Compiling && sw.ElapsedMilliseconds < 30000) Thread.Sleep(1);
            e.NoteOn(0, 60, velocity / 127f);
            var l = new float[128];
            var r = new float[128];
            var y = new float[Rate / 2];
            for (int pos = 0; pos < y.Length; pos += 128)
            {
                e.Process(l, r, 128);
                Array.Copy(l, 0, y, pos, Math.Min(128, y.Length - pos));
            }
            return Measure.Db(Measure.Rms(y, 0, Rate * 3 / 10));
        }

        [Test]
        public void ANewNeoPatchDefaultsToSeventyPercentVelocityToLevel()
        {
            var d = NeoBuzz.Patch.Params.Table[NeoBuzz.Patch.P.VelLevel];
            Assert.That(d.Id, Is.EqualTo("vel_level"));
            Assert.That(d.Default, Is.EqualTo(700));
            Assert.That(NeoBuzz.Patch.Params.Table.Length, Is.EqualTo(21));
            Assert.That(NeoBuzz.Patch.P.VelLevel, Is.EqualTo(20), "appended after the sixteen macros");
        }

        [Test]
        public void AtSeventyPercentAHardNoteIsAtLeastSixDbOverASoftOne()
        {
            double soft = PatchLevel(30, 700), hard = PatchLevel(127, 700);
            Measure.Report($"Neo Patch, Vel -> level 70 %: velocity 30 {soft:F1} dB, 127 {hard:F1} dB, difference {hard - soft:F1} dB");
            Assert.That(hard - soft, Is.GreaterThanOrEqualTo(6.0));
        }

        [Test]
        public void AtZeroPercentVelocityDoesNotChangeTheLevelAndFullVelocityIsAlwaysUntouched()
        {
            double soft = PatchLevel(30, 0), hard = PatchLevel(127, 0);
            Assert.That(soft, Is.EqualTo(hard).Within(0.01), "0 %: as the patch makes it, whatever the velocity");
            Assert.That(PatchLevel(127, 700), Is.EqualTo(hard).Within(0.01), "velocity 127 is the same at 0 % and at 70 %");
        }

        static int[] Saved20() => Enumerable.Range(0, 20).Select(i => 100 + i).ToArray();

        [Test]
        public void ASongSavedBeforeVelLevelLoadsWithItAtZeroAndEveryOtherValueWhereItWas()
        {
            var c = PatchMigrations.Chain;
            Assert.That(c.Validate(), Is.Empty);
            Assert.That(c.Current, Is.EqualTo(1));
            Assert.That(PatchMigrations.Version0, Is.EqualTo(NeoBuzz.Patch.Params.Table.Take(20).Select(p => p.Id)), "version 0 is the table as it was");
            var saved = Saved20();
            var now = c.MigrateGlobals(0, saved);
            Assert.That(now.Length, Is.EqualTo(21));
            for (int i = 0; i < 20; i++) Assert.That(now[i], Is.EqualTo(Math.Clamp(saved[i], NeoBuzz.Patch.Params.Table[i].Min, NeoBuzz.Patch.Params.Table[i].Max)), NeoBuzz.Patch.Params.Table[i].Id);
            Assert.That(now[NeoBuzz.Patch.P.VelLevel], Is.EqualTo(0), "an old song sounds as it did");
            for (int i = 0; i < 20; i++) Assert.That(c.MapIndex(0, i), Is.EqualTo(i));
        }

        [Test]
        public void ASongSavedAtVersionOneKeepsItsVelLevel()
        {
            var c = PatchMigrations.Chain;
            var saved = Saved20().Concat(new[] { 350 }).ToArray();
            var now = c.MigrateGlobals(1, saved);
            Assert.That(now[NeoBuzz.Patch.P.VelLevel], Is.EqualTo(350));
        }

        [Test]
        public void ANewMachineSavesVersionOneAndLoadsItselfBack()
        {
            var c = PatchMigrations.Chain;
            var defaults = NeoBuzz.Patch.Params.Table.Select(p => p.Default).ToArray();
            Assert.That(c.MigrateGlobals(c.Current, defaults), Is.EqualTo(defaults));
            Assert.That(defaults[NeoBuzz.Patch.P.VelLevel], Is.EqualTo(700));
        }

        // ------------------------------------------------------------------ G4: Neo Envelope

        static EnvelopeEngine Env(int velocityRaw)
        {
            var t = NeoBuzz.EnvelopeMachine.Params.Table;
            var v = new ParamValues(t);
            v.Set(NeoBuzz.EnvelopeMachine.P.Attack, t[NeoBuzz.EnvelopeMachine.P.Attack].Raw(0.002));
            v.Set(NeoBuzz.EnvelopeMachine.P.Sustain, 1000);
            v.Set(NeoBuzz.EnvelopeMachine.P.Curve, 500);
            v.Set(NeoBuzz.EnvelopeMachine.P.Velocity, velocityRaw);
            var e = new EnvelopeEngine(v);
            e.SetSampleRate(44100);
            return e;
        }

        static float Top(EnvelopeEngine e, int velocity)
        {
            double dt = 64 / 44100.0;
            if (velocity > 0) e.NoteOn(velocity); else e.NoteOn();
            float y = 0;
            for (int i = 0; i < 200; i++) y = e.Step(64, dt, 0);
            return y;
        }

        [Test]
        public void TheVelocityAmountIsAppendedAtZeroPercent()
        {
            var d = NeoBuzz.EnvelopeMachine.Params.Table[NeoBuzz.EnvelopeMachine.P.Velocity];
            Assert.That(d.Id, Is.EqualTo("velocity"));
            Assert.That(d.Default, Is.EqualTo(0));
            Assert.That(NeoBuzz.EnvelopeMachine.P.Velocity, Is.EqualTo(9));
            var c = EnvelopeMigrations.Chain;
            Assert.That(c.Validate(), Is.Empty);
            Assert.That(EnvelopeMigrations.Version0, Is.EqualTo(NeoBuzz.EnvelopeMachine.Params.Table.Take(9).Select(p => p.Id)));
            var old = new[] { 0, 313, 500, 700, 500, 0, 500, 0, 408 };
            var now = c.MigrateGlobals(0, old);
            Assert.That(now.Take(9), Is.EqualTo(old));
            Assert.That(now[9], Is.EqualTo(0), "an old song opens fully on every note");
        }

        [Test]
        public void AtZeroPercentEveryVelocityOpensTheEnvelopeFully()
        {
            Assert.That(Top(Env(0), 30), Is.EqualTo(1f).Within(0.01f));
            Assert.That(Top(Env(0), 127), Is.EqualTo(1f).Within(0.01f));
            Assert.That(Top(Env(0), 0), Is.EqualTo(1f).Within(0.01f), "a note with no velocity (a Note parameter, the Gate switch) is full");
        }

        [Test]
        public void AtOneHundredPercentTheTopIsTheVelocity()
        {
            double soft = Top(Env(1000), 30), mid = Top(Env(1000), 64), hard = Top(Env(1000), 127);
            Assert.That(soft, Is.EqualTo(30 / 127.0).Within(0.02));
            Assert.That(mid, Is.EqualTo(64 / 127.0).Within(0.02));
            Assert.That(hard, Is.EqualTo(1.0).Within(0.02));
            Assert.That(soft, Is.LessThan(mid));
            Assert.That(mid, Is.LessThan(hard));
        }

        [Test]
        public void AtHalfTheTopIsHalfWayBetween()
        {
            Assert.That(Top(Env(500), 1), Is.EqualTo(1 - 0.5 * (1 - 1 / 127.0)).Within(0.02));
        }
    }
}
