using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Learn;
using ReBuzzTests.Automation;
using YP = NeoBuzz.LearnPd.P;
using ZP = NeoBuzz.LearnVector.P;
using QP = NeoBuzz.LearnSampler.P;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Learn (docs/specs/neo-learn.md §9, L2b): what the Phase Distortion, Vector and Sampler machines and the engine code under them (the phase-distortion
    /// warp, the level a map moves, the chorus, grit, room and tone stages) must do. The shared bars run over all the Learn machines in LearnL1Tests.
    /// </summary>
    public partial class LearnL1Tests
    {
        static readonly Action<ParamValues> Sustained = x => { x.Set(YP.EnvAttack, 0); x.Set(YP.EnvSustain, 1000); x.Set(YP.EnvDecay, 800); };

        // ------------------------------------------------------------------ Phase Distortion

        [Test]
        public void EveryPdGeometryStartsAtZeroEndsAtOneAndNeverGoesBack()
        {
            for (int shape = 0; shape < Warps.PdShapes; shape++)
            {
                double prev = Warps.PdTarget(shape, 0);
                Assert.That(prev, Is.EqualTo(0).Within(1e-6), $"shape {shape} starts at 0");
                for (int i = 1; i <= 1000; i++)
                {
                    double t = Warps.PdTarget(shape, i / 1000.0);
                    Assert.That(t, Is.GreaterThanOrEqualTo(prev - 1e-9), $"shape {shape} at {i / 1000.0}");
                    Assert.That(t - prev, Is.LessThan(Warps.PdMaxSlope / 1000.0 * 1.5), $"shape {shape} at {i / 1000.0}: the slope stays under the cap");
                    prev = t;
                }
                double end = Warps.PdTarget(shape, 1);
                Assert.That(end, Is.EqualTo(shape == 3 ? 2 : 1).Within(1e-6), $"shape {shape} ends a whole cycle on");
            }
        }

        [Test]
        public void NoBendReadsThePhaseEvenlyAndFullBendReadsTheGeometry()
        {
            for (int shape = 0; shape < Warps.PdShapes; shape++)
                for (int i = 0; i <= 20; i++)
                {
                    double p = i / 20.0;
                    Assert.That(PhaseDistortionMap.Phase(shape, 0, p), Is.EqualTo(p).Within(1e-9));
                    Assert.That(PhaseDistortionMap.Phase(shape, 1, p), Is.EqualTo(Warps.PdTarget(shape, p)).Within(1e-9));
                }
        }

        [Test]
        public void WithNoBendResonanceOrRingThePdMachineIsAPureSine()
        {
            var y = L2Note(Pd, x => { Sustained(x); x.Set(YP.PhaseBend, 0); x.Set(YP.Resonance, 0); x.Set(YP.RingMod, 0); x.Set(YP.Chorus, 0); }, 57, Rate);
            double f0 = DspMath.NoteToHz(57), fund = BandDb(y, 9600, 0.9 * f0, 1.1 * f0), rest = BandDb(y, 9600, 1.9 * f0, 12 * f0);
            Measure.Report($"Neo Learn Phase Distortion, Phase Bend 0 %: harmonics {rest - fund:F1} dB below the fundamental");
            Assert.That(rest - fund, Is.LessThan(-35.0));
        }

        [Test]
        public void BendingAddsHarmonicsForEveryGeometryAndStaysBounded()
        {
            double f0 = DspMath.NoteToHz(48);
            for (int shape = 0; shape < Warps.PdShapes; shape++)
            {
                double Rest(int bend, out double peak)
                {
                    var y = L2Note(Pd, x => { Sustained(x); x.Set(YP.PhaseBend, bend); x.Set(YP.Resonance, 0); x.Set(YP.BendDecay, 0); x.Set(YP.Chorus, 0); x.Set(YP.PdWaveShape, shape); }, 48, Rate);
                    peak = y.Skip(9600).Max(Math.Abs);
                    return BandDb(y, 9600, 1.9 * f0, 12 * f0) - BandDb(y, 9600, 0.9 * f0, 1.1 * f0);
                }
                double flat = Rest(0, out _), bent = Rest(1000, out double peak);
                Measure.Report($"Neo Learn Phase Distortion, geometry {shape}: harmonics {flat:F1} dB at 0 % bend, {bent:F1} dB at 100 %; peak {peak:F2}");
                Assert.That(bent, Is.GreaterThan(flat + 10), $"geometry {shape}");
                Assert.That(peak, Is.LessThan(1.6), $"geometry {shape}");
            }
        }

        /// <summary>Energy (dB) of a band over a short window of <paramref name="n"/> samples from <paramref name="at"/>.</summary>
        static double ShortBandDb(float[] x, int at, int n, double lo, double hi)
        {
            var mag = Fft.Magnitudes(x, at, n);
            double e = 0;
            for (int k = 1; k < mag.Length; k++)
            {
                double hz = (double)k * Rate / n;
                if (hz >= lo && hz < hi) e += mag[k] * mag[k];
            }
            return 10 * Math.Log10(Math.Max(1e-30, e));
        }

        [Test]
        public void TheBendRelaxesAfterTheNoteStartsByBendDecay()
        {
            // Bend Decay moves the bend from all of it at the note's start to what the note keeps: the note is brighter at its start than later.
            var y = L2Note(Pd, x => { Sustained(x); x.Set(YP.PhaseBend, 900); x.Set(YP.Resonance, 0); x.Set(YP.BendDecay, 600); x.Set(YP.Chorus, 0); }, 48, Rate * 5);
            double f0 = DspMath.NoteToHz(48);
            double Bright(int at) => ShortBandDb(y, at, 2048, 1.9 * f0, 12 * f0) - ShortBandDb(y, at, 2048, 0.9 * f0, 1.1 * f0);
            double early = Bright(100), late = Bright(Rate * 3);
            Measure.Report($"Neo Learn Phase Distortion, bend at 90 %, Bend Decay 0.37 s: harmonics {early:F1} dB in the first 40 ms, {late:F1} dB after 3 s");
            Assert.That(early, Is.GreaterThan(late + 3));
        }

        [Test]
        public void RingModPutsSidebandsAtTheCarrierPlusAndMinusTheNote()
        {
            double f0 = DspMath.NoteToHz(52);
            double Side(int ring)
            {
                var y = L2Note(Pd, x => { Sustained(x); x.Set(YP.PhaseBend, 0); x.Set(YP.Resonance, 0); x.Set(YP.RingMod, ring); x.Set(YP.Chorus, 0); }, 52, Rate);
                return BandDb(y, 9600, (PhaseDistortionMap.RingRatio - 1 - 0.05) * f0, (PhaseDistortionMap.RingRatio - 1 + 0.05) * f0) - BandDb(y, 9600, 0.9 * f0, 1.1 * f0);
            }
            double none = Side(0), full = Side(1000);
            Measure.Report($"Neo Learn Phase Distortion, sideband at {PhaseDistortionMap.RingRatio - 1:F3} x the note, against the note: Ring 0 % {none:F1} dB, 100 % {full:F1} dB");
            Assert.That(full, Is.GreaterThan(none + 20));
        }

        [Test]
        public void ResonanceAddsAWindowedSineAboveTheNoteAndIsContinuous()
        {
            double f0 = DspMath.NoteToHz(48);
            double Above(int res)
            {
                var y = L2Note(Pd, x => { Sustained(x); x.Set(YP.PhaseBend, 0); x.Set(YP.Resonance, res); x.Set(YP.BendDecay, 0); x.Set(YP.Chorus, 0); }, 48, Rate);
                return BandDb(y, 9600, 1.9 * f0, 20000);
            }
            double a = Above(0), b = Above(500), c = Above(1000);
            Measure.Report($"Neo Learn Phase Distortion, energy above the note: Resonance 0 % {a:F1} dB, 50 % {b:F1} dB, 100 % {c:F1} dB");
            Assert.That(b, Is.GreaterThan(a + 10));
            Assert.That(c, Is.GreaterThan(b - 1));
            double d = Above(10), e = Above(0);
            Assert.That(Math.Abs(d - e), Is.LessThan(25), "a step of 1 % does not jump");
        }

        // ------------------------------------------------------------------ Vector

        [Test]
        public void TheCornerWeightsSumToOneAndAreOneAtTheCorners()
        {
            for (int ix = -12; ix <= 12; ix++)
                for (int iy = -12; iy <= 12; iy++)
                {
                    VectorMap.CornerWeights(ix / 8.0, iy / 8.0, out double w, out double e, out double n, out double s);
                    Assert.That(w + e + n + s, Is.EqualTo(1).Within(1e-9), $"{ix},{iy}");
                    Assert.That(Math.Min(Math.Min(w, e), Math.Min(n, s)), Is.GreaterThanOrEqualTo(0));
                }
            VectorMap.CornerWeights(-1, 0, out double w0, out _, out _, out _);
            VectorMap.CornerWeights(1, 0, out _, out double e0, out _, out _);
            VectorMap.CornerWeights(0, 1, out _, out _, out double n0, out _);
            VectorMap.CornerWeights(0, -1, out _, out _, out _, out double s0);
            Assert.That(new[] { w0, e0, n0, s0 }, Is.All.EqualTo(1).Within(1e-9));
            VectorMap.CornerWeights(0, 0, out double a, out double b, out double c, out double d);
            Assert.That(new[] { a, b, c, d }, Is.All.EqualTo(0.25).Within(1e-9));
        }

        [Test]
        public void TheOrbitPathsStayInsideTheUnitSquareAndTheStepVisitsFourCorners()
        {
            for (int path = 0; path < 3; path++)
                for (int i = 0; i < 400; i++)
                {
                    VectorMap.Orbit(path, i / 400.0, out double dx, out double dy);
                    Assert.That(Math.Abs(dx), Is.LessThanOrEqualTo(1 + 1e-9));
                    Assert.That(Math.Abs(dy), Is.LessThanOrEqualTo(1 + 1e-9));
                }
            var seen = new HashSet<(double, double)>();
            for (int i = 0; i < 400; i++)
            {
                VectorMap.Orbit(2, i / 400.0, out double dx, out double dy);
                seen.Add((dx, dy));
            }
            Assert.That(seen.Count, Is.EqualTo(4));
        }

        [Test]
        public void TheWestAndEastCornersPlayTheirOwnSources()
        {
            // Poly Classic: west is a saw (an octave at -6 dB), east a square (no even harmonics).
            double f0 = DspMath.NoteToHz(52);
            double Even(int x)
            {
                var y = L2Note(Vec, v => { v.Set(ZP.EnvAttack, 0); v.Set(ZP.EnvSustain, 1000); v.Set(ZP.VectorX, x); v.Set(ZP.VectorY, 500); v.Set(ZP.OrbitRadius, 0); }, 52, Rate);
                return BandDb(y, 9600, 1.9 * f0, 2.1 * f0) - BandDb(y, 9600, 0.9 * f0, 1.1 * f0);
            }
            double west = Even(0), east = Even(1000);
            Measure.Report($"Neo Learn Vector, Poly Classic: the octave against the note: west {west:F1} dB, east {east:F1} dB");
            Assert.That(west, Is.GreaterThan(east + 12));
        }

        [Test]
        public void TheOrbitMovesTheCursorAndANeutralOrbitDoesNot()
        {
            (double Min, double Max) Range(int radius)
            {
                var (h, _) = Make(Vec, v => { v.Set(ZP.OrbitRadius, radius); v.Set(ZP.OrbitSpeed, 600); v.Set(ZP.OrbitShape, 0); });
                var map = (VectorMap)h.Map;
                h.Engine.NoteOn(0, 48, 0.9f);
                var l = new float[Block]; var r = new float[Block];
                double lo = 9, hi = -9;
                for (int b = 0; b < 400; b++)
                {
                    h.Process(l, r, Block);
                    lo = Math.Min(lo, map.CursorX); hi = Math.Max(hi, map.CursorX);
                    map.Weights(out float w, out float e, out float n, out float s);
                    Assert.That(w + e + n + s, Is.EqualTo(1).Within(1e-4));
                }
                return (lo, hi);
            }
            var still = Range(0);
            var moving = Range(800);
            Measure.Report($"Neo Learn Vector, the cursor's x over 2 s: no orbit {still.Min:F2} to {still.Max:F2}; orbit depth 80 % {moving.Min:F2} to {moving.Max:F2}");
            Assert.That(still.Max - still.Min, Is.LessThan(1e-6));
            Assert.That(moving.Max - moving.Min, Is.GreaterThan(0.8));
        }

        [Test]
        public void TheRandomDriftIsReproducibleAndBounded()
        {
            double[] Run()
            {
                var (h, _) = Make(Vec, v => { v.Set(ZP.OrbitShape, 3); v.Set(ZP.OrbitRadius, 1000); v.Set(ZP.OrbitSpeed, 700); });
                var map = (VectorMap)h.Map;
                h.Engine.NoteOn(0, 48, 0.9f);
                var l = new float[Block]; var r = new float[Block];
                var xs = new List<double>();
                for (int b = 0; b < 600; b++) { h.Process(l, r, Block); xs.Add(map.CursorX); }
                return xs.ToArray();
            }
            var a = Run(); var b = Run();
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a.Max(Math.Abs), Is.LessThanOrEqualTo(1 + 1e-6));
            Assert.That(a.Max() - a.Min(), Is.GreaterThan(0.2), "it wanders");
        }

        /// <summary>A map that sets slot A's level multiplier on top of another's patch.</summary>
        sealed class LevelMap : MacroMap
        {
            readonly MacroMap inner;
            readonly double level;
            public LevelMap(MacroMap inner, double level) { this.inner = inner; this.level = level; }
            public override ParamDef[] Controls => inner.Controls;
            public override void Base(IDyeSink dye) => inner.Base(dye);
            public override void Map(double[] c, IDyeSink dye) { inner.Map(c, dye); dye.Ext(0, SlotExt.LevelExt, level); }
        }

        [Test]
        public void TheLevelAMapMovesScalesTheSourceAndLeavesTheRestAlone()
        {
            // SlotExt.LevelExt multiplies a slot's level (the host moves it between ticks): 0.5 is 6 dB down, 0 is silent.
            double Level(double ext)
            {
                Tables();
                var v = new ParamValues(NeoBuzz.LearnPd.Params.Table);
                Sustained(v);
                v.Set(YP.PhaseBend, 0); v.Set(YP.Resonance, 0); v.Set(YP.Chorus, 0);
                var h = new LearnHost(new LevelMap(new PhaseDistortionMap(NeoBuzz.LearnPd.Params.Table), ext), v);
                h.SetSampleRate(Rate);
                h.Engine.NoteOn(0, 57, 0.9f);
                var y = Render(h, Rate).L;
                return Measure.Db(Measure.Rms(y, 9600, 9600));
            }
            double full = Level(1), half = Level(0.5), none = Level(0);
            Measure.Report($"LevelExt: 1 -> {full:F1} dB, 0.5 -> {half:F1} dB, 0 -> {none:F1} dB");
            Assert.That(full - half, Is.EqualTo(6.02).Within(0.3));
            Assert.That(none, Is.LessThan(-100));
        }

        // ------------------------------------------------------------------ Sampler

        [Test]
        public void StartOffsetSkipsTheAttack()
        {
            double First(int offset)
            {
                var y = L2Note(Smp, x => { x.Set(QP.Source, 3); x.Set(QP.SampleOffset, offset); x.Set(QP.RoomSpace, 0); x.Set(QP.EnvAttack, 0); x.Set(QP.EnvSustain, 1000); }, 60, Rate / 2);
                return Measure.Db(Measure.Rms(y, 0, 2400));
            }
            double zero = First(0), far = First(900);
            Measure.Report($"Neo Learn Sampler, the Pluck's first 50 ms: Start Offset 0 % {zero:F1} dB, 90 % {far:F1} dB");
            Assert.That(zero, Is.GreaterThan(far + 6));
        }

        [Test]
        public void LoopKeepsAShortSampleSoundingWhileTheNoteIsHeld()
        {
            double Late(int loop)
            {
                var y = L2Note(Smp, x => { x.Set(QP.Source, 3); x.Set(QP.Loop, loop); x.Set(QP.RoomSpace, 0); x.Set(QP.EnvAttack, 0); x.Set(QP.EnvSustain, 1000); x.Set(QP.EnvDecay, 900); }, 60, Rate * 8);
                return Measure.Db(Measure.Rms(y, Rate * 6, 4800));
            }
            double once = Late(0), looped = Late(100);
            Measure.Report($"Neo Learn Sampler, the Pluck held for 6 s: Loop 0 % {once:F1} dB, 10 % {looped:F1} dB");
            Assert.That(looped, Is.GreaterThan(once + 20));
        }

        [Test]
        public void UserWithNoSampleLoadedPlaysTheFirstBuiltIn()
        {
            double[] Peaks(bool user, int source)
            {
                var map = new SamplerMap(NeoBuzz.LearnSampler.Params.Table) { UserAvailable = user };
                Tables();
                var v = new ParamValues(NeoBuzz.LearnSampler.Params.Table);
                v.Set(QP.Source, source);
                var h = new LearnHost(map, v);
                h.SetSampleRate(Rate);
                h.Engine.NoteOn(0, 60, 0.9f);
                var y = Render(h, 24000).L;
                return new[] { y.Max(Math.Abs), Measure.Rms(y, 0, 24000) };
            }
            Assert.That(Peaks(false, 5), Is.EqualTo(Peaks(false, 0)));
        }

        [Test]
        public void ToneTiltsTheSpectrumAroundOneKilohertz()
        {
            double Ratio(int tone)
            {
                var y = L2Note(Smp, x => { x.Set(QP.Source, 4); x.Set(QP.TimbreTilt, tone); x.Set(QP.RoomSpace, 0); x.Set(QP.EnvAttack, 0); x.Set(QP.EnvSustain, 1000); }, 60, Rate);
                return BandDb(y, 9600, 3000, 16000) - BandDb(y, 9600, 100, 600);
            }
            double dark = Ratio(0), flat = Ratio(500), bright = Ratio(1000);
            Measure.Report($"Neo Learn Sampler, highs against lows: Tone -100 % {dark:F1} dB, 0 {flat:F1} dB, +100 % {bright:F1} dB");
            Assert.That(dark, Is.LessThan(flat - 6));
            Assert.That(bright, Is.GreaterThan(flat + 6));
        }

        [Test]
        public void RoomAddsATailAfterTheNoteEnds()
        {
            double Tail(int room)
            {
                var (h, _) = Make(Smp, x => { x.Set(QP.Source, 3); x.Set(QP.RoomSpace, room); x.Set(QP.EnvAttack, 0); x.Set(QP.EnvRelease, 0); });
                h.Engine.NoteOn(0, 60, 0.9f);
                var l = new float[Block]; var r = new float[Block];
                var y = new List<float>();
                for (int b = 0; b < 400; b++)
                {
                    if (b == 40) h.Engine.NoteOff(0);
                    h.Process(l, r, Block);
                    y.AddRange(l);
                }
                var a = y.ToArray();
                return Measure.Db(Measure.Rms(a, 40 * Block + 3000, 2400));
            }
            double dry = Tail(0), wet = Tail(1000);
            Measure.Report($"Neo Learn Sampler, 60 ms after the note ends: Room 0 % {dry:F1} dB, 100 % {wet:F1} dB");
            Assert.That(wet, Is.GreaterThan(dry + 10));
        }
    }
}
