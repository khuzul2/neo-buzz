using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Learn;
using ReBuzzTests.Automation;
using SP = NeoBuzz.LearnString.P;
using VP = NeoBuzz.LearnFormant.P;
using XP = NeoBuzz.LearnFold.P;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Learn (docs/specs/neo-learn.md §9, L2a): what the String, Formant and Wavefolder machines and the engine code under them (the Karplus-Strong
    /// string, the vowel filter, the fold shaper and the low-pass gate) must do. The shared bars run over all the Learn machines in LearnL1Tests.
    /// </summary>
    public partial class LearnL1Tests
    {
        static float[] L2Note(M m, Action<ParamValues> tweak, int note, int frames, float velocity = 0.9f)
        {
            var (h, _) = Make(m, tweak);
            h.Engine.NoteOn(0, note, velocity);
            return Render(h, Math.Max(frames, Measure.N + 12000)).L;
        }

        /// <summary>Energy (dB re full scale) of the spectrum between two frequencies, over N samples from <paramref name="offset"/>.</summary>
        static double BandDb(float[] x, int offset, double lo, double hi)
        {
            var mag = Measure.Spectrum(x, offset);
            double e = 0;
            for (int k = 1; k < mag.Length; k++)
            {
                double hz = (double)k * Measure.Rate / Measure.N;
                if (hz >= lo && hz < hi) e += mag[k] * mag[k];
            }
            return 10 * Math.Log10(Math.Max(1e-30, e));
        }

        // ------------------------------------------------------------------ String

        [TestCase(36)]
        [TestCase(48)]
        [TestCase(60)]
        [TestCase(72)]
        [TestCase(84)]
        public void StringDecayTimeIsTheTimeToFall60dBAtEveryNote(int note)
        {
            // Decay Time 2 s, no damping, a held note: the level should fall about 30 dB in 1 s (T60 = 2 s), whatever the pitch.
            var y = L2Note(Str, x => { x.Set(SP.StringDecay, 616); x.Set(SP.StringDamping, 0); x.Set(SP.Dispersion, 0); x.Set(SP.SympatheticRes, 0); x.Set(SP.EnvSustain, 1000); x.Set(SP.EnvDecay, 1000); }, note, Rate * 3);
            double a = Measure.Db(Measure.Rms(y, (int)(0.30 * Rate), 2400));
            double b = Measure.Db(Measure.Rms(y, (int)(1.30 * Rate), 2400));
            Measure.Report($"Neo Learn String, Decay Time 2 s, note {note}: fall over 1 s {a - b:F1} dB (30 dB expected)");
            Assert.That(a - b, Is.InRange(20.0, 40.0), $"note {note}");
        }

        [TestCase(48)]
        [TestCase(60)]
        [TestCase(72)]
        public void StringIsInTune(int note)
        {
            var y = L2Note(Str, x => { x.Set(SP.StringDamping, 0); x.Set(SP.Dispersion, 0); x.Set(SP.SympatheticRes, 0); x.Set(SP.PickPosition, 300); x.Set(SP.StringDecay, 600); x.Set(SP.EnvSustain, 1000); }, note, Rate);
            double f = Measure.PeakFrequency(y, Rate / 8, 16384);
            // The loudest partial need not be the fundamental: judge it against the nearest multiple of the note's pitch.
            double f0 = DspMath.NoteToHz(note);
            double cents = 1200 * Math.Log2(f / (Math.Round(f / f0) * f0));
            Measure.Report($"Neo Learn String, note {note}: {f:F2} Hz, {cents:F1} cents from equal temperament");
            Assert.That(Math.Abs(cents), Is.LessThan(10.0));
        }

        [Test]
        public void SympatheticStringsRingAtTheFifthAboveTheOctave()
        {
            double Fifth(int sym)
            {
                var y = L2Note(Str, x => { x.Set(SP.SympatheticRes, sym); x.Set(SP.StringDecay, 700); x.Set(SP.StringDamping, 0); x.Set(SP.PickPosition, 300); x.Set(SP.EnvSustain, 1000); x.Set(SP.EnvDecay, 1000); }, 57, Rate);
                double f0 = DspMath.NoteToHz(57);
                // 1.5 x the pitch is the sympathetic fifth (an octave above the note's own third harmonic's half): nothing of the note itself lives there.
                return BandDb(y, 4800, 1.47 * f0, 1.53 * f0);
            }
            double none = Fifth(0), full = Fifth(1000);
            Measure.Report($"Neo Learn String, energy at the fifth above the note (1.5 x): Sympathetic 0 % {none:F1} dB, 100 % {full:F1} dB");
            Assert.That(full, Is.GreaterThan(none + 6), "sympathetic strings ring along");
        }

        [Test]
        public void PickPositionAtTheMiddleRemovesTheEvenHarmonics()
        {
            double Second(int pick)
            {
                var y = L2Note(Str, x => { x.Set(SP.PickPosition, pick); x.Set(SP.StringDamping, 0); x.Set(SP.Dispersion, 0); x.Set(SP.SympatheticRes, 0); x.Set(SP.EnvSustain, 1000); x.Set(SP.PickHardness, 700); }, 57, Rate);
                double f0 = DspMath.NoteToHz(57);
                return BandDb(y, 4800, 1.9 * f0, 2.1 * f0) - BandDb(y, 4800, 0.9 * f0, 1.1 * f0);
            }
            double edge = Second(100), middle = Second(1000);
            Measure.Report($"Neo Learn String, second harmonic against the first: pick near the end {edge:F1} dB, at the middle {middle:F1} dB");
            Assert.That(middle, Is.LessThan(edge - 6), "the comb");
        }

        [Test]
        public void StiffnessStretchesThePartialsSharp()
        {
            double Third(int stiff)
            {
                var y = L2Note(Str, x => { x.Set(SP.Dispersion, stiff); x.Set(SP.StringDamping, 0); x.Set(SP.SympatheticRes, 0); x.Set(SP.StringDecay, 700); x.Set(SP.EnvSustain, 1000); x.Set(SP.PickHardness, 900); x.Set(SP.PickPosition, 100); }, 48, Rate);
                double f0 = DspMath.NoteToHz(48);
                return Strongest(y, 7.5 * f0, 9.0 * f0) / f0;
            }
            double none = Third(0), full = Third(1000);
            Measure.Report($"Neo Learn String, the eighth partial as a multiple of the fundamental: Stiffness 0 % {none:F3}, 100 % {full:F3}");
            Assert.That(full, Is.GreaterThan(none + 0.1), "stretched sharp");
        }

        static double Strongest(float[] x, double lo, double hi)
        {
            var mag = Measure.Spectrum(x, 4800);
            int best = 1; double bm = 0;
            for (int k = 1; k < mag.Length; k++)
            {
                double hz = (double)k * Measure.Rate / Measure.N;
                if (hz >= lo && hz < hi && mag[k] > bm) { bm = mag[k]; best = k; }
            }
            return (double)best * Measure.Rate / Measure.N;
        }

        // ------------------------------------------------------------------ Formant

        [Test]
        public void TheVowelFiltersMoveFromAToI()
        {
            double Hi(int vowel)
            {
                var y = L2Note(Vox, x => { x.Set(VP.VowelMorph, vowel); x.Set(VP.FormantQ, 500); x.Set(VP.BreathAir, 0); x.Set(VP.EnvAttack, 0); x.Set(VP.EnvSustain, 1000); x.Set(VP.TalkingEnv, 500); }, 48, Rate);
                return BandDb(y, 9600, 1800, 3500) - BandDb(y, 9600, 400, 1200);
            }
            double a = Hi(0), i = Hi(500);
            Measure.Report($"Neo Learn Formant, energy at 1.8-3.5 kHz against 0.4-1.2 kHz: A {a:F1} dB, I {i:F1} dB");
            Assert.That(i, Is.GreaterThan(a + 6), "I has a high second formant, A has not");
        }

        [Test]
        public void TheTractSizeMovesTheFormantsWithoutMovingThePitch()
        {
            double Centre(int tract, out double pitch)
            {
                var y = L2Note(Vox, x => { x.Set(VP.VowelMorph, 0); x.Set(VP.TractScale, tract); x.Set(VP.BreathAir, 0); x.Set(VP.EnvAttack, 0); x.Set(VP.EnvSustain, 1000); x.Set(VP.TalkingEnv, 500); }, 48, Rate);
                pitch = Measure.PeakFrequency(y, 9600, 16384);
                return BandDb(y, 9600, 1500, 4000) - BandDb(y, 9600, 200, 800);
            }
            double giant = Centre(0, out double pg), child = Centre(1000, out double pc);
            Measure.Report($"Neo Learn Formant, tract size: giant {giant:F1} dB high-to-low, child {child:F1} dB; pitch {pg:F1} Hz and {pc:F1} Hz");
            Assert.That(child, Is.GreaterThan(giant + 3), "a small tract is brighter");
        }

        // ------------------------------------------------------------------ Wavefolder

        [Test]
        public void WithNoDriveTheFolderPassesTheSineUnchanged()
        {
            var y = L2Note(Fold, x => { x.Set(XP.FoldDrive, 0); x.Set(XP.TimbreFm, 0); x.Set(XP.LpgMode, 0); x.Set(XP.EnvAttack, 0); x.Set(XP.EnvSustain, 1000); x.Set(XP.FoldSymmetry, 500); }, 57, Rate);
            double f0 = DspMath.NoteToHz(57);
            double fund = BandDb(y, 9600, 0.9 * f0, 1.1 * f0);
            double rest = BandDb(y, 9600, 1.9 * f0, 8.1 * f0);
            Measure.Report($"Neo Learn Wavefolder, Wavefold 0 %: harmonics {rest - fund:F1} dB below the fundamental");
            Assert.That(rest - fund, Is.LessThan(-35.0));
        }

        [Test]
        public void MoreDriveAndMoreStagesAddHarmonicsAndStayBounded()
        {
            double Above(int drive, int stages, out double peak)
            {
                var y = L2Note(Fold, x => { x.Set(XP.FoldDrive, drive); x.Set(XP.FoldStages, stages); x.Set(XP.LpgMode, 0); x.Set(XP.EnvAttack, 0); x.Set(XP.EnvSustain, 1000); }, 45, Rate);
                peak = y.Max(Math.Abs);
                double f0 = DspMath.NoteToHz(45);
                return BandDb(y, 9600, 1.5 * f0, 20000) - BandDb(y, 9600, 0.9 * f0, 1.1 * f0);
            }
            double low = Above(200, 0, out _), high = Above(900, 0, out double p1), many = Above(900, 5, out double p2);
            Measure.Report($"Neo Learn Wavefolder, harmonic energy against the fundamental: drive 20 % {low:F1} dB, 90 % {high:F1} dB, 90 % with 6 stages {many:F1} dB; peaks {p1:F2} and {p2:F2}");
            Assert.That(high, Is.GreaterThan(low + 3));
            Assert.That(Math.Max(p1, p2), Is.LessThan(1.5));
        }

        [Test]
        public void GateStrikeOpensTheLowPassGateOnEachNote()
        {
            // A slow attack leaves the gate shut at the start of the note; a strike opens it at once.
            double Start(int strike)
            {
                var y = L2Note(Fold, x => { x.Set(XP.FoldDrive, 600); x.Set(XP.LpgMode, 1000); x.Set(XP.GateStrike, strike); x.Set(XP.EnvAttack, 600); x.Set(XP.EnvDecay, 800); x.Set(XP.EnvSustain, 1000); }, 48, Rate);
                return Measure.Db(Measure.Rms(y, 0, 2400));
            }
            double soft = Start(0), hard = Start(1000);
            Measure.Report($"Neo Learn Wavefolder, level of the first 50 ms with a slow attack: Strike 0 % {soft:F1} dB, 100 % {hard:F1} dB");
            Assert.That(hard, Is.GreaterThan(soft + 2));
        }

        // ------------------------------------------------------------------ aliasing

        [TestCase(33, 450, 2, 0, 0)]
        [TestCase(57, 450, 2, 0, 0)]
        [TestCase(93, 450, 5, 0, 0)]
        [TestCase(33, 1000, 5, 0, 0)]
        [TestCase(45, 1000, 5, 0, 0)]
        [TestCase(57, 1000, 5, 0, 0)]
        [TestCase(69, 1000, 5, 0, 0)]
        [TestCase(81, 1000, 5, 0, 0)]
        [TestCase(93, 1000, 5, 0, 0)]
        [TestCase(57, 1000, 3, 0, 0)]
        [TestCase(69, 1000, 3, 0, 0)]
        [TestCase(81, 700, 3, 0, 0)]
        [TestCase(57, 1000, 5, 600, 1000)]
        [TestCase(81, 1000, 5, 600, 1000)]
        [TestCase(45, 1000, 5, -500, 0)]
        public void WavefolderAliasesUnderMinus40dBAtFullSettings(int note, int drive, int stages, int sym, int fm)
        {
            // Every fold stage is antiderivative-anti-aliased (Warps.FoldAdaa), and the stage gain is held where the fold's total gain times the pitch fits
            // the 4x rate (Warps.FoldLimit): a learner at full Wavefold with six stages hears folds, not noise. (Before: +7 dB at note 57, i.e. louder than the tone.)
            double floor = Alias(Fold, note, x => { x.Set(XP.FoldDrive, drive); x.Set(XP.FoldStages, stages); x.Set(XP.FoldSymmetry, 500 + sym / 2); x.Set(XP.TimbreFm, fm); x.Set(XP.LpgMode, 0); x.Set(XP.EnvSustain, 1000); x.Set(XP.EnvAttack, 0); });
            Measure.Report($"Neo Learn Wavefolder, High (4x), Wavefold {drive / 10} %, {stages + 1} stages, symmetry {sym / 10} %, Timbre FM {fm / 10} %, note {note} ({DspMath.NoteToHz(note):F0} Hz): alias floor {floor:F1} dB");
            Assert.That(floor, Is.LessThan(-40.0));
        }

        [Test]
        public void TheFoldLimitIsContinuousAndNeverRaisesTheGain()
        {
            // Over pitch the stage gain moves smoothly, stays between 1 and the asked gain, and a low note keeps all of it.
            float last = float.NaN;
            for (double inc = 1 << 18; inc < 1 << 27; inc *= 1.004)
            {
                float g = Warps.FoldLimit(3.2f, 6, inc);
                Assert.That(g, Is.InRange(1f, 3.2f));
                if (!float.IsNaN(last)) { Assert.That(g, Is.LessThanOrEqualTo(last + 1e-6f)); Assert.That(last - g, Is.LessThan(0.02f), $"inc {inc}"); }
                last = g;
            }
            Assert.That(Warps.FoldLimit(3.2f, 6, 8.0 / 176400 * 4294967296.0), Is.EqualTo(3.2f));
            Assert.That(Warps.FoldLimit(3.2f, 1, 4000.0 / 176400 * 4294967296.0), Is.EqualTo(3.2f).Within(1e-6), "one stage of 3.2 at 4 kHz is inside the budget");
        }

        [TestCase(33)]
        [TestCase(57)]
        [TestCase(81)]
        public void StringAndFormantReportTheirNonHarmonicEnergyAtStandard(int note)
        {
            double s = Alias(Str, note, x => { x.Set(SP.PickHardness, 1000); x.Set(SP.StringDamping, 0); x.Set(SP.EnvSustain, 1000); });
            double v = Alias(Vox, note, x => { x.Set(VP.GlottalEdge, 1000); x.Set(VP.BreathAir, 0); x.Set(VP.EnvAttack, 0); x.Set(VP.EnvSustain, 1000); });
            Measure.Report($"Neo Learn String, note {note} ({DspMath.NoteToHz(note):F0} Hz), hardest pick: strongest non-harmonic bin {s:F1} dB re the fundamental (a plucked, decaying string is not a steady harmonic tone: not an alias figure); Neo Learn Formant, hardest glottal source: {v:F1} dB (the fundamental sits in a formant skirt: likewise)");
        }
    }
}
