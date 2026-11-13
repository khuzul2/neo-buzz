using System.Threading;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Learn;
using ReBuzzTests.Automation;
using DyeP = NeoBuzz.DyeEssence.P;
using GP = NeoBuzz.LearnGrain.P;
using PP = NeoBuzz.LearnPhys.P;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Learn (docs/specs/neo-learn.md §5.5), L1b: Neo Learn Granular and Physical. The shared bars (controls, macro-map ranges and continuity,
    /// allocation, finite output, pinned renders, levels, CPU) run over all the Learn machines in LearnL1Tests; this adds what these two and their
    /// shared-engine additions must do: the grain-window morph, octave grains and the grain cap; the exciter morph, the geometry blend, the material
    /// and damping.
    /// </summary>
    public partial class LearnL1Tests
    {
        // ------------------------------------------------------------------ Neo Learn Granular

        /// <summary>The grains started over <paramref name="frames"/> frames of a note: (all, octave up, a share of the sample's length at which they start).</summary>
        static (int All, int Up, double MinPos, double MaxPos) Grains(Action<ParamValues> tweak, int frames, int note = 60)
        {
            var (h, _) = Make(Grain, tweak);
            h.Engine.NoteOn(0, note, 0.9f);
            var bank = h.Engine.Bank;
            int seen = Volatile.Read(ref bank.GrainLogAt), all = 0, up = 0;
            double lo = 2, hi = -1;
            var l = new float[Block];
            var r = new float[Block];
            for (int pos = 0; pos < frames; pos += Block)
            {
                h.Process(l, r, Block);
                int at = Volatile.Read(ref bank.GrainLogAt);
                Assert.That(at - seen, Is.LessThanOrEqualTo(NoteBank.GrainLogSize), "the log keeps up");
                for (; seen < at; seen++)
                {
                    int li = seen & (NoteBank.GrainLogSize - 1);
                    all++;
                    if (bank.GrainLogUp[li]) up++;
                    lo = Math.Min(lo, bank.GrainLogPos[li]);
                    hi = Math.Max(hi, bank.GrainLogPos[li]);
                }
            }
            return (all, up, lo, hi);
        }

        [Test]
        public void TheGrainWindowMorphsFromATrapezoidToAGaussianWithoutAStep()
        {
            Assert.That(GranularMap.Window(0, 0.5), Is.EqualTo(1).Within(1e-6), "a trapezoid is flat in the middle");
            Assert.That(GranularMap.Window(0, 0.04), Is.EqualTo(0.5).Within(0.01), "and ramps straight over its first 8 %");
            Assert.That(GranularMap.Window(0, 0.3), Is.EqualTo(1).Within(1e-6));
            Assert.That(GranularMap.Window(1, 0.5), Is.EqualTo(1).Within(1e-6), "the Gaussian peaks in the middle");
            Assert.That(GranularMap.Window(1, 0.1), Is.LessThan(0.1), "and has gone by the edges");
            Assert.That(GranularMap.Window(0.5, 0.1), Is.InRange(GranularMap.Window(1, 0.1), GranularMap.Window(0, 0.1)), "in between: a blend");
            for (double x = 0; x <= 1; x += 0.01)
            {
                double prev = GranularMap.Window(0, x);
                for (int k = 1; k <= 1000; k++)
                {
                    double v = GranularMap.Window(k / 1000.0, x);
                    Assert.That(Math.Abs(v - prev), Is.LessThan(0.003), $"contour {k / 10.0} % at {x:F2}");
                    prev = v;
                }
            }
            for (int i = 0; i <= GrainWindows.Size; i++)
            {
                Assert.That(GrainWindows.Trapezoid[i], Is.InRange(0f, 1f));
                Assert.That(GrainWindows.Morphed(0.3, i), Is.InRange(0f, 1f));
            }
            Assert.That(GrainWindows.Trapezoid[0], Is.EqualTo(0f), "every grain starts and ends at zero");
            Assert.That(GrainWindows.Trapezoid[GrainWindows.Size], Is.EqualTo(0f));
        }

        [Test]
        public void TheMorphKeepsACloudsLoudnessAcrossTheContour()
        {
            double[] db = new double[3];
            int c = 0;
            foreach (int contour in new[] { 0, 500, 1000 })
            {
                var l = Note(Grain, 60, Rate * 2, x => { x.Set(GP.GrainShape, contour); x.Set(GP.EnvAttack, 0); x.Set(GP.EnvSustain, 1000); x.Set(GP.GrainSize, 780); x.Set(GP.GrainDensity, 760); });
                db[c++] = Measure.Db(Measure.Rms(l, Rate / 2, Rate));
            }
            Measure.Report($"Neo Learn Granular, cloud RMS at Grain Contour 0 / 50 / 100 %: {db[0]:F1} / {db[1]:F1} / {db[2]:F1} dB");
            Assert.That(db.Max() - db.Min(), Is.LessThan(4.0), "the window's energy is compensated, so sweeping the contour is not a level change");
        }

        [Test]
        public void ASharpContourSoundsClickyAndASmoothOneDoesNot()
        {
            // The same cloud of 5 ms grains on a held note. A trapezoid starts and stops on straight edges (0.4 ms: a click), a Gaussian fades: the edges are
            // broadband, so the energy far above the source's own (the Choir has almost nothing above 6 kHz) is higher for the trapezoid.
            double hf(int contour)
            {
                var l = Note(Grain, 60, Rate * 2, x => { x.Set(GP.GrainShape, contour); x.Set(GP.EnvAttack, 0); x.Set(GP.EnvSustain, 1000); x.Set(GP.GrainSize, 0); x.Set(GP.GrainDensity, 1000); x.Set(GP.PositionSpray, 0); });
                return HighShare(l, Rate / 2, 16384, 6000);
            }
            double sharp = hf(0), smooth = hf(1000);
            Measure.Report($"Neo Learn Granular, energy above 6 kHz with 5 ms grains: trapezoid {sharp:F1} dB, Gaussian {smooth:F1} dB (of the whole)");
            Assert.That(sharp, Is.GreaterThan(smooth + 2), "straight edges click; faded ones do not");
        }

        [Test]
        public void PitchShimmerAddsOctaveGrainsUpToHalfWithoutNewGrains()
        {
            var none = Grains(x => { x.Set(GP.PitchShimmer, 0); x.Set(GP.GrainDensity, 760); x.Set(GP.GrainSize, 500); }, Rate * 4);
            var half = Grains(x => { x.Set(GP.PitchShimmer, 500); x.Set(GP.GrainDensity, 760); x.Set(GP.GrainSize, 500); }, Rate * 4);
            var full = Grains(x => { x.Set(GP.PitchShimmer, 1000); x.Set(GP.GrainDensity, 760); x.Set(GP.GrainSize, 500); }, Rate * 4);
            Measure.Report($"Neo Learn Granular, grains in 4 s: shimmer 0: {none.All} ({none.Up} up); 50 %: {half.All} ({half.Up} up); 100 %: {full.All} ({full.Up} up)");
            Assert.That(none.Up, Is.EqualTo(0), "none at 0 %");
            Assert.That(half.Up / (double)half.All, Is.InRange(0.15, 0.35), "a quarter at 50 %");
            Assert.That(full.Up / (double)full.All, Is.InRange(0.40, 0.60), "half at 100 %");
            Assert.That(full.All, Is.InRange(none.All * 0.85, none.All * 1.15), "an octave-up grain takes the place of one, it is not added: the density and the cap hold");
        }

        [Test]
        public void OctaveGrainsAddEnergyAnOctaveAbove()
        {
            // Grains read at twice the speed put the source's partials an octave up: the energy above 3.2 kHz (where the Choir has almost none) rises.
            double up(int shimmer)
            {
                var l = Note(Grain, 60, Rate * 3, x =>
                {
                    x.Set(GP.PitchShimmer, shimmer); x.Set(GP.EnvAttack, 0); x.Set(GP.EnvSustain, 1000); x.Set(GP.PositionSpray, 0); x.Set(GP.GrainShape, 1000); x.Set(GP.GrainSize, 800); x.Set(GP.GrainDensity, 800);
                });
                return HighShare(l, Rate, 16384, 3200);
            }
            double plain = up(0), shimmer = up(1000);
            Measure.Report($"Neo Learn Granular, energy above 3.2 kHz: shimmer 0 {plain:F1} dB, 100 % {shimmer:F1} dB (of the whole)");
            Assert.That(shimmer, Is.GreaterThan(plain + 10), "grains an octave up raise the spectrum");
        }

        [Test]
        public void GrainsComeAtTheDensityAndTheCapHolds()
        {
            // 8 grains a second ((20 per second minus the clock's wobble: ±30 %): about 32 in 4 s.
            var slow = Grains(x => { x.Set(GP.GrainDensity, 500); x.Set(GP.GrainSize, 300); x.Set(GP.EnvAttack, 0); }, Rate * 4);
            double rate = slow.All / 4.0;
            Assert.That(rate, Is.InRange(NeoBuzz.LearnGrain.Params.Table[GP.GrainDensity].Value(500) * 0.75, NeoBuzz.LearnGrain.Params.Table[GP.GrainDensity].Value(500) * 1.25), $"{rate:F1} grains a second");
            // 64 grains a second of 500 ms: 32 would overlap; the engine takes the oldest over, so it still starts grains at the rate asked.
            var dense = Grains(x => { x.Set(GP.GrainDensity, 1000); x.Set(GP.GrainSize, 1000); }, Rate * 4);
            Assert.That(dense.All / 4.0, Is.InRange(50, 80), "the cap fades the oldest, it does not stop the clock");
            Assert.That(NeoBuzz.LearnGrain.Params.Table[GP.GrainDensity].Sound, Does.Contain("At most 10 grains overlap per note"));
        }

        [Test]
        public void ScanPositionAndSprayPlaceTheGrains()
        {
            var at30 = Grains(x => { x.Set(GP.SamplePosition, 300); x.Set(GP.PositionSpray, 0); }, Rate * 2);
            var at80 = Grains(x => { x.Set(GP.SamplePosition, 800); x.Set(GP.PositionSpray, 0); }, Rate * 2);
            Assert.That(at30.MinPos, Is.EqualTo(0.3).Within(0.01));
            Assert.That(at30.MaxPos, Is.EqualTo(0.3).Within(0.01), "no spray: every grain from the scan position");
            Assert.That(at80.MinPos, Is.EqualTo(0.8).Within(0.01));
            var wide = Grains(x => { x.Set(GP.SamplePosition, 500); x.Set(GP.PositionSpray, 1000); x.Set(GP.GrainDensity, 800); }, Rate * 4);
            Assert.That(wide.MaxPos - wide.MinPos, Is.GreaterThan(0.6), "full spray: anywhere in the source");
            var some = Grains(x => { x.Set(GP.SamplePosition, 500); x.Set(GP.PositionSpray, 200); x.Set(GP.GrainDensity, 800); }, Rate * 4);
            Assert.That(some.MaxPos - some.MinPos, Is.InRange(0.05, 0.25), "20 %: a band of that width");
        }

        [Test]
        public void TheSourceIsChosenWithTheMapAndTheHostFollowsAtTheNextBlock()
        {
            Tables();
            var map = new GranularMap(NeoBuzz.LearnGrain.Params.Table);
            var v = new ParamValues(NeoBuzz.LearnGrain.Params.Table);
            var h = new LearnHost(map, v);
            h.SetSampleRate(Rate);
            Assert.That(h.DyeValues.Choice(DyeP.ASmpSrc), Is.EqualTo(0), "Choir to start with");
            h.Engine.NoteOn(0, 60, 0.9f);
            var a = Render(h, Rate / 2).L;
            map.Source = 3;
            h.Remap();
            Render(h, Block);
            Assert.That(h.DyeValues.Choice(DyeP.ASmpSrc), Is.EqualTo(3), "Pluck: the host mapped again with no control touched");
            var (h2, _) = Make(Grain);
            h2.Engine.NoteOn(0, 60, 0.9f);
            Assert.That(Hash(Render(h2, Rate / 2).L), Is.EqualTo(Hash(a)), "the same render for the same state");
        }

        [Test]
        public void EverySourceSoundsAndTheGrainWindowIsPlayedInTheEngine()
        {
            Tables();
            for (int src = 0; src < SampleSources.Count; src++)
            {
                var map = new GranularMap(NeoBuzz.LearnGrain.Params.Table) { Source = src };
                var h = new LearnHost(map, new ParamValues(NeoBuzz.LearnGrain.Params.Table));
                h.SetSampleRate(Rate);
                h.Engine.NoteOn(0, 60, 0.9f);
                var l = Render(h, Rate).L;
                Assert.That(Measure.Rms(l, Rate / 2, Rate / 4), Is.GreaterThan(0.002), SampleSources.Names[src]);
            }
        }

        [TestCase(72)]
        [TestCase(84)]
        [TestCase(96)]
        public void GranularReportsItsOutOfBandEnergyAtHighNotes(int note)
        {
            // Grains are read through a windowed sinc whose cutoff follows the step, so a sample played up is band-limited: nothing should stand above 22 kHz.
            var l = Note(Grain, note, Rate * 2, x => { x.Set(GP.EnvAttack, 0); x.Set(GP.EnvSustain, 1000); x.Set(GP.GrainShape, 0); x.Set(GP.GrainSize, 250); x.Set(GP.GrainDensity, 600); });
            double db = OutOfBand(l, Rate / 2, 16384);
            Measure.Report($"Neo Learn Granular, note {note} ({DspMath.NoteToHz(note):F0} Hz), trapezoid grains: above 22 kHz {db:F1} dB under the loudest bin");
            // Above note 90 a partial of the note itself stands in that band (the 11th harmonic of 2 kHz is at 23 kHz): reported, not judged.
            if (note <= 84) Assert.That(db, Is.LessThan(-60));
        }

        /// <summary>The loudest bin above 0.46 of the sample rate (22.08 kHz at 48 kHz: where the engines fade a partial out) against the loudest bin of all, dB.</summary>
        static double OutOfBand(float[] x, int at, int n)
        {
            var mag = Spec(x, at, n);
            double bin = (double)Rate / n, top = 1e-30, peak = 1e-30;
            for (int k = 1; k < mag.Length; k++)
            {
                peak = Math.Max(peak, mag[k]);
                if (k * bin > 0.46 * Rate) top = Math.Max(top, mag[k]);
            }
            return 20 * Math.Log10(top / peak);
        }

        /// <summary>The energy above <paramref name="hz"/> as a share of the whole, dB.</summary>
        static double HighShare(float[] x, int at, int n, double hz)
        {
            var mag = Spec(x, at, n);
            double bin = (double)Rate / n, hi = 1e-30, all = 1e-30;
            for (int k = 1; k < mag.Length; k++)
            {
                all += mag[k] * mag[k];
                if (k * bin > hz) hi += mag[k] * mag[k];
            }
            return 10 * Math.Log10(hi / all);
        }

        // ------------------------------------------------------------------ Neo Learn Physical

        /// <summary>Neo Learn Physical's map with Dye's own exciter choice in place of the morph: what the morph's ends must equal.</summary>
        sealed class DyeExciterMap : MacroMap
        {
            readonly PhysicalMap inner = new PhysicalMap(NeoBuzz.LearnPhys.Params.Table);
            readonly int exciter;

            public DyeExciterMap(int exciter) { this.exciter = exciter; }

            public override ParamDef[] Controls => inner.Controls;

            public override void Base(IDyeSink dye) => inner.Base(dye);

            public override void Map(double[] c, IDyeSink dye)
            {
                inner.Map(c, new Filter(dye));
                dye.Set(DyeP.AMExc, exciter);
            }

            sealed class Filter : IDyeSink
            {
                readonly IDyeSink to;
                public Filter(IDyeSink to) { this.to = to; }
                public void Set(int dyeParam, double value) => to.Set(dyeParam, value);
                public void Ext(int slot, SlotExt what, double value)
                {
                    if (what != SlotExt.ExciterMorph) to.Ext(slot, what, value);
                }
            }
        }

        static float[] PhysNote(MacroMap map, Action<ParamValues> tweak, int note, int frames, float velocity = 0.9f)
        {
            Tables();
            var v = new ParamValues(NeoBuzz.LearnPhys.Params.Table);
            tweak?.Invoke(v);
            var h = new LearnHost(map, v);
            h.SetSampleRate(Rate);
            h.Engine.NoteOn(0, note, velocity);
            return Render(h, frames).L;
        }

        static float[] PhysNote(Action<ParamValues> tweak, int note, int frames, float velocity = 0.9f) =>
            PhysNote(new PhysicalMap(NeoBuzz.LearnPhys.Params.Table), tweak, note, frames, velocity);

        [TestCase(0)]
        [TestCase(100)]
        [TestCase(1000)]
        public void ASoftOrHardMalletIsNeverHotOnAnyNote(int force)
        {
            // A mallet whose pulse has a null at the lowest modes was levelled up by up to 20 dB (peak +1.5 dBFS at Strike 0 on A4):
            // the morph's levelling now floors the energy it divides by. Every note, full velocity, Init otherwise.
            double worst = -200; int at = 0;
            for (int note = 24; note <= 108; note++)
            {
                var l = PhysNote(x => { x.Set(PP.ExciterType, 0); x.Set(PP.ExciterForce, force); }, note, Rate / 2, 1f);
                double pk = Measure.Db(l.Max(Math.Abs));
                if (pk > worst) { worst = pk; at = note; }
            }
            Measure.Report($"Neo Learn Physical, mallet at Strike {force / 10} %: loudest peak over notes 24-108 {worst:F1} dBFS (note {at})");
            Assert.That(worst, Is.LessThan(-3.0), $"note {at}");
        }

        [Test]
        public void PhysicalInitFollowsThePitchYouPlay()
        {
            // The default Geometry is the string (whole multiples), so the note is the pitch heard (a bar's spacing is not harmonic).
            var v = new ParamValues(NeoBuzz.LearnPhys.Params.Table);
            Assert.That(v[PP.ResonatorShape], Is.EqualTo(0), "Init is a string");
        }

        static double DiffDb(float[] a, float[] b)
        {
            double d = 0, e = 1e-30;
            for (int i = 0; i < a.Length; i++) { d += (double)(a[i] - b[i]) * (a[i] - b[i]); e += (double)a[i] * a[i]; }
            return 10 * Math.Log10(Math.Max(d, 1e-30) / e);
        }

        [TestCase(0, 0, "mallet")]
        [TestCase(500, 1, "pick")]
        [TestCase(1000, 2, "bow")]
        public void TheMorphsEndsPlayDyesThreeExciters(int morph, int dyeExciter, string name)
        {
            // The ends of Exciter Mode are not approximations: at 0 %, 50 % and 100 % the engine plays what Dye's exciter choice plays.
            Action<ParamValues> set = x => { x.Set(PP.ExciterType, morph); x.Set(PP.EnvSustain, 1000); x.Set(PP.Damping, 100); };
            var a = PhysNote(set, 52, Rate);
            var b = PhysNote(new DyeExciterMap(dyeExciter), set, 52, Rate);
            // The morph's bow stands lower than Dye's own (0.45 against 0.7 rms, so the crossfade keeps one loudness): the same noise, scaled.
            if (dyeExciter == 2) for (int i = 0; i < b.Length; i++) b[i] *= 0.45f / 0.7f;
            double diff = DiffDb(a, b);
            Measure.Report($"Neo Learn Physical, {name}: morph vs Dye's own exciter, difference {diff:F1} dB");
            Assert.That(Measure.Rms(a, 0, Rate), Is.GreaterThan(0.003), "it sounds");
            Assert.That(diff, Is.LessThan(-90), name);
        }

        [Test]
        public void TheExciterMorphCrossfadesWithoutALevelHoleOrStep()
        {
            // The loudest 50 ms of a held note at each place on the morph: equal-power crossfades keep it within a few dB of the ends.
            var level = new List<double>();
            for (int m = 0; m <= 1000; m += 100)
            {
                var l = PhysNote(x => { x.Set(PP.ExciterType, m); x.Set(PP.EnvSustain, 1000); x.Set(PP.Damping, 300); }, 52, Rate);
                double loud = 0;
                for (int at = 0; at + 2400 <= l.Length; at += 1200) loud = Math.Max(loud, Measure.Rms(l, at, 2400));
                level.Add(Measure.Db(loud));
            }
            Measure.Report("Neo Learn Physical, loudest 50 ms at Exciter Mode 0, 10 ... 100 %: " + string.Join(" ", level.Select(x => x.ToString("F1"))) + " dB");
            Assert.That(level.Max() - level.Min(), Is.LessThan(8.0), "no hole between the exciters");
            for (int i = 1; i < level.Count; i++) Assert.That(Math.Abs(level[i] - level[i - 1]), Is.LessThan(4.0), $"step {i}");
        }

        [Test]
        public void ABowSustainsWhileTheNoteIsHeldAndAMalletDoesNot()
        {
            double Held(int morph)
            {
                var l = PhysNote(x => { x.Set(PP.ExciterType, morph); x.Set(PP.EnvSustain, 1000); x.Set(PP.Damping, 1000); x.Set(PP.EnvAttack, 100); }, 52, Rate * 3);
                return Measure.Db(Measure.Rms(l, Rate * 2, Rate));
            }
            double mallet = Held(0), bow = Held(1000), mix = Held(750);
            Measure.Report($"Neo Learn Physical, level 2 s into a held note at full damping: mallet {mallet:F1} dB, bow {bow:F1} dB, 75 % {mix:F1} dB");
            Assert.That(bow, Is.GreaterThan(-45), "a bow keeps driving the body");
            Assert.That(mallet, Is.LessThan(bow - 20), "a mallet strikes once and the body falls silent");
            Assert.That(mix, Is.GreaterThan(mallet + 10), "the blend carries the bow's share");
        }

        [Test]
        public void AMidMorphNoteHasBothTheStrikeAndTheStream()
        {
            // At 25 % (mallet and pick) the note has a transient and decays; at 75 % (pick and bow) it keeps sounding.
            var m25 = PhysNote(x => { x.Set(PP.ExciterType, 250); x.Set(PP.EnvSustain, 1000); x.Set(PP.Damping, 1000); }, 52, Rate * 3);
            var m75 = PhysNote(x => { x.Set(PP.ExciterType, 750); x.Set(PP.EnvSustain, 1000); x.Set(PP.Damping, 1000); }, 52, Rate * 3);
            Assert.That(Measure.Rms(m25, 0, 2400), Is.GreaterThan(Measure.Rms(m25, Rate * 2, 2400) * 10), "struck or plucked: it falls");
            Assert.That(Measure.Rms(m75, Rate * 2, 2400), Is.GreaterThan(Measure.Rms(m75, 0, 2400) * 0.05), "bowed in part: it holds");
        }

        [Test]
        public void GeometryBlendsFromAStringThroughATubeAndABarToAPlateWithoutAJump()
        {
            var a = new double[PhysicalMap.Modes];
            var b = new double[PhysicalMap.Modes];
            int n0 = PhysicalMap.BlendedRatios(0, a);
            Assert.That(a[0], Is.EqualTo(1).Within(1e-6));
            Assert.That(a[2], Is.EqualTo(3).Within(1e-6), "a string: whole multiples");
            for (int raw = 1; raw <= 1000; raw++)
            {
                PhysicalMap.BlendedRatios((raw - 1) / 1000.0, a);
                int n = PhysicalMap.BlendedRatios(raw / 1000.0, b);
                Assert.That(n, Is.GreaterThanOrEqualTo(24));
                for (int j = 0; j < 12; j++) Assert.That(Math.Abs(b[j] / a[j] - 1), Is.LessThan(0.02), $"Geometry {raw / 10.0} %, mode {j + 1}");
            }
            PhysicalMap.BlendedRatios(1.0 / 3, a);
            Assert.That(a[1], Is.EqualTo(3).Within(1e-6), "a third of the way is the tube: odd multiples");
            PhysicalMap.BlendedRatios(2.0 / 3, a);
            Assert.That(a[1], Is.EqualTo(ModalBodies.Ratios[(int)ModalGeometry.Bar][1]).Within(1e-4), "two thirds is the bar");
            PhysicalMap.BlendedRatios(1.0, a);
            Assert.That(a[1], Is.EqualTo(ModalBodies.Ratios[(int)ModalGeometry.Plate][1]).Within(1e-4), "and the end is the plate");
            Assert.That(n0, Is.EqualTo(PhysicalMap.Modes));
        }

        [Test]
        public void TheEngineSoundsTheBlendedSpacing()
        {
            // A string has every harmonic, a tube only the odd ones: the second harmonic is there at 0 % and gone at 33 %.
            double second(int geometry)
            {
                var l = PhysNote(x => { x.Set(PP.ResonatorShape, geometry); x.Set(PP.ExciterType, 500); x.Set(PP.ExciterForce, 300); x.Set(PP.Material, 0); x.Set(PP.EnvSustain, 1000); x.Set(PP.Damping, 100); }, 45, Rate);
                var mag = Spec(l, 2400, 16384);
                double f0 = DspMath.NoteToHz(45);
                double h1 = Near(mag, 16384, f0, out _, 0.01), h2 = Near(mag, 16384, 2 * f0, out _, 0.01);
                return 20 * Math.Log10(Math.Max(h2, 1e-12) / h1);
            }
            double string_ = second(0), tube = second(333);
            Measure.Report($"Neo Learn Physical, second harmonic against the first: string {string_:F1} dB, tube {tube:F1} dB");
            Assert.That(string_, Is.GreaterThan(tube + 15), "a tube leaves out the even harmonics");
        }

        [Test]
        public void MaterialRingsLongerFromWoodToMetalAndDampingShortensIt()
        {
            double Ring(int material, int damping)
            {
                var l = PhysNote(x => { x.Set(PP.Material, material); x.Set(PP.Damping, damping); x.Set(PP.ExciterType, 0); x.Set(PP.EnvSustain, 1000); x.Set(PP.EnvRelease, 1000); }, 57, Rate * 3);
                // How far the level has fallen from the first quarter second to the second one.
                return Measure.Db(Measure.Rms(l, Rate, Rate / 2)) - Measure.Db(Measure.Rms(l, 0, Rate / 2));
            }
            double wood = Ring(0, 300), nylon = Ring(333, 300), glass = Ring(667, 300), metal = Ring(1000, 300);
            Measure.Report($"Neo Learn Physical, fall in level between 0-0.5 s and 1-1.5 s: wood {wood:F1}, nylon {nylon:F1}, glass {glass:F1}, metal {metal:F1} dB");
            Assert.That(wood, Is.LessThan(nylon), "wood dies first");
            Assert.That(nylon, Is.LessThan(glass));
            Assert.That(glass, Is.LessThan(metal), "metal rings on");
            double free = Ring(667, 0), muted = Ring(667, 1000);
            Assert.That(muted, Is.LessThan(free - 10), "damping mutes the ring");
        }

        [Test]
        public void AHarderStrikeIsBrighterAndALargerBodyRingsLonger()
        {
            double Bright(int force)
            {
                var l = PhysNote(x => { x.Set(PP.ExciterForce, force); x.Set(PP.ExciterType, 500); x.Set(PP.Material, 600); x.Set(PP.EnvSustain, 1000); x.Set(PP.Damping, 0); }, 45, Rate / 2);
                return HighShare(l, 0, 16384, 1500);
            }
            double soft = Bright(0), hard = Bright(1000);
            Measure.Report($"Neo Learn Physical, energy above 1.5 kHz: force soft {soft:F1} dB, hard {hard:F1} dB (of the whole)");
            Assert.That(hard, Is.GreaterThan(soft + 5), "a harder strike is brighter");

            double Fall(int size)
            {
                var l = PhysNote(x => { x.Set(PP.BodySize, size); x.Set(PP.ExciterType, 0); x.Set(PP.Material, 500); x.Set(PP.Damping, 300); x.Set(PP.EnvSustain, 1000); x.Set(PP.EnvRelease, 1000); }, 57, Rate * 3);
                return Measure.Db(Measure.Rms(l, Rate, Rate / 2)) - Measure.Db(Measure.Rms(l, 0, Rate / 2));
            }
            double small = Fall(0), large = Fall(1000);
            Measure.Report($"Neo Learn Physical, fall in level from 0-0.5 s to 1-1.5 s: small body {small:F1} dB, large body {large:F1} dB");
            Assert.That(large, Is.GreaterThan(small + 3), "a larger body rings longer");
        }

        [TestCase(70)]
        [TestCase(82)]
        [TestCase(94)]
        public void PhysicalReportsItsOutOfBandEnergyAtHighNotes(int note)
        {
            // Modes above the band are faded out, not folded back: the bank is a sum of resonators, so there is nothing to alias.
            foreach (var (name, material, shape) in new[] { ("nylon string", 333, 0), ("glass plate", 667, 1000) })
            {
                var l = PhysNote(x => { x.Set(PP.Material, material); x.Set(PP.ResonatorShape, shape); x.Set(PP.ExciterForce, 900); x.Set(PP.BodySize, 0); }, note, 24000);
                double db = OutOfBand(l, 200, 16384);
                Measure.Report($"Neo Learn Physical, note {note} ({DspMath.NoteToHz(note):F0} Hz), {name}: above 22 kHz {db:F1} dB under the loudest bin");
                if (note <= 82) Assert.That(db, Is.LessThan(-60), name);
            }
        }

        [Test]
        public void TheExciterMorphAndGeometryBlendAreOffUnlessAMapTurnsThemOn()
        {
            var os = new OscSettings();
            Assert.That(os.ExciterMorph, Is.LessThan(0f), "Dye Essence's own exciter choice rules");
            Assert.That(os.GrainMorph, Is.LessThan(0f), "and its own window");
            Assert.That(os.GeomBlend, Is.EqualTo(0f));
            Assert.That(os.GrainOctave, Is.EqualTo(0f));
        }
    }
}
