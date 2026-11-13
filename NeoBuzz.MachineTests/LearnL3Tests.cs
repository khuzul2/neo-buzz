using NeoBuzz.Editor;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Learn;
using LS = NeoBuzz.LearnSpectral.P;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Learn (docs/specs/neo-learn.md §9, L3): what Neo Learn Spectral's controls must do over the Spectral engine of Dye Essence P5a. The shared bars
    /// (range, continuity, no drone, presets, pins, CPU floors) run over all the Learn machines in LearnL1Tests.
    /// </summary>
    public partial class LearnL1Tests
    {
        static readonly Action<ParamValues> SpcHeld = x => { x.Set(LS.EnvAttack, 0); x.Set(LS.EnvSustain, 1000); x.Set(LS.EnvDecay, 800); x.Set(LS.UnisonDetune, 0); };

        static double Centroid(float[] x, int offset)
        {
            var mag = Measure.Spectrum(x, offset);
            double s = 0, w = 0;
            for (int k = 1; k < mag.Length; k++) { double hz = (double)k * Measure.Rate / Measure.N; s += hz * mag[k] * mag[k]; w += mag[k] * mag[k]; }
            return s / Math.Max(1e-30, w);
        }

        [Test]
        public void PartialGateKeepsTheLoudestPartialsFromAllToOne()
        {
            Assert.That(SpectralMap.Partials(0), Is.EqualTo(64));
            Assert.That(SpectralMap.Partials(1), Is.EqualTo(1));
            int prev = 65;
            for (int i = 0; i <= 1000; i++)
            {
                int n = SpectralMap.Partials(i / 1000.0);
                Assert.That(n, Is.LessThanOrEqualTo(prev), $"gate {i / 10.0} %");
                prev = n;
            }
            double Rms(int gate)
            {
                var y = L2Note(Spc, x => { SpcHeld(x); x.Set(LS.Source, 1); x.Set(LS.PartialGate, gate); x.Set(LS.ToneNoise, 1000); }, 60, Rate * 2);
                return Measure.Db(Measure.Rms(y, Rate / 2, Rate));
            }
            double all = Rms(0), few = Rms(900), one = Rms(1000);
            Measure.Report($"Neo Learn Spectral, the Strings: Partial Gate 0 % {all:F1} dB, 90 % {few:F1} dB, 100 % {one:F1} dB");
            Assert.That(few, Is.LessThan(all - 1), "fewer partials, less energy");
            Assert.That(one, Is.LessThan(few), "one partial is the quietest");
            Assert.That(one, Is.GreaterThan(-90), "and it still sounds");
        }

        [Test]
        public void FreezeHoldsOneMomentWhereTheSampleOtherwiseMoves()
        {
            double Wander(int freeze)
            {
                var y = L2Note(Spc, x => { SpcHeld(x); x.Set(LS.Source, 3); x.Set(LS.SpectralFreeze, freeze); }, 60, Rate * 3);
                double lo = 1e9, hi = -1e9;
                for (int at = Rate / 2; at + 4800 <= Rate * 3; at += 4800)
                {
                    double d = Measure.Db(Measure.Rms(y, at, 4800));
                    lo = Math.Min(lo, d); hi = Math.Max(hi, d);
                }
                return hi - lo;
            }
            double moving = Wander(0), frozen = Wander(1000);
            Measure.Report($"Neo Learn Spectral, the Pluck held: level range over 2.5 s: Freeze 0 % {moving:F1} dB, 100 % {frozen:F1} dB");
            Assert.That(frozen, Is.LessThan(1.5), "a frozen moment holds its level");
            Assert.That(moving, Is.GreaterThan(frozen + 3), "the sample unfrozen moves");
        }

        [Test]
        public void StretchSpreadsThePartialsAndLeavesThePitch()
        {
            double Cent(int stretch)
            {
                var y = L2Note(Spc, x => { SpcHeld(x); x.Set(LS.Source, 1); x.Set(LS.SpectralShift, stretch); x.Set(LS.ToneNoise, 1000); }, 60, Rate * 2);
                return Centroid(y, Rate / 2);
            }
            double neg = Cent(0), mid = Cent(500), pos = Cent(1000);
            Measure.Report($"Neo Learn Spectral, the Strings: spectral centroid Stretch -100 % {neg:F0} Hz, 0 {mid:F0} Hz, +100 % {pos:F0} Hz");
            Assert.That(pos, Is.GreaterThan(mid * 1.1), "positive stretch moves the partials up");
            Assert.That(neg, Is.LessThan(mid * 0.95), "negative stretch moves them down");
        }

        [Test]
        public void ToneNoiseBalancesThePartialsAgainstTheNoise()
        {
            double Level(int balance)
            {
                var y = L2Note(Spc, x => { SpcHeld(x); x.Set(LS.Source, 4); x.Set(LS.ToneNoise, balance); }, 60, Rate * 2);
                return Measure.Db(Measure.Rms(y, Rate / 2, Rate));
            }
            double noise = Level(0), both = Level(500), tone = Level(1000);
            Measure.Report($"Neo Learn Spectral, the Texture: Tone / Noise 0 % {noise:F1} dB, 50 % {both:F1} dB, 100 % {tone:F1} dB");
            Assert.That(both, Is.GreaterThan(Math.Max(noise, tone) - 1), "both halves sound at least as loud as either");
            Assert.That(Math.Abs(noise - tone), Is.GreaterThan(0.5), "the halves are not the same sound");
        }

        [Test]
        public void TimeSmearChangesTheSoundWithoutLosingItsLevel()
        {
            double Level(int smear)
            {
                var y = L2Note(Spc, x => { SpcHeld(x); x.Set(LS.Source, 2); x.Set(LS.SpectralBlur, smear); }, 60, Rate * 2);
                return Measure.Db(Measure.Rms(y, Rate / 2, Rate));
            }
            double none = Level(0), full = Level(1000);
            Measure.Report($"Neo Learn Spectral, the Bell: Time Smear 0 % {none:F1} dB, 100 % {full:F1} dB");
            Assert.That(full, Is.GreaterThan(none - 12), "smeared, it still sounds");
            Assert.That(full, Is.LessThan(none + 6), "and does not run away");
        }

        [Test]
        public void UserWithNoSampleLoadedPlaysTheFirstBuiltInSpectral()
        {
            double[] Peaks(bool user, int source)
            {
                var map = new SpectralMap(NeoBuzz.LearnSpectral.Params.Table) { UserAvailable = user };
                Tables();
                var v = new ParamValues(NeoBuzz.LearnSpectral.Params.Table);
                v.Set(LS.Source, source);
                var h = new LearnHost(map, v);
                h.SetSampleRate(Rate);
                h.Engine.NoteOn(0, 60, 0.9f);
                var y = Render(h, 24000).L;
                return new[] { y.Max(Math.Abs), Measure.Rms(y, 0, 24000) };
            }
            Assert.That(Peaks(false, 5), Is.EqualTo(Peaks(false, 0)));
        }

        [Test]
        public void FreezeReadsTheSampleAtFortyPercentAndStopsIt()
        {
            SpectralMap.Reading(0, out double p0, out double s0);
            SpectralMap.Reading(1, out double p1, out double s1);
            Assert.That(p0, Is.EqualTo(0)); Assert.That(s0, Is.EqualTo(1));
            Assert.That(p1, Is.EqualTo(SpectralMap.FreezeAt)); Assert.That(s1, Is.EqualTo(0));
        }

        [Test]
        public void TheSpectrogramDrawsTheKeptAndTheGatedPartials()
        {
            var canvas = new RecordingCanvas();
            var kept = new float[24 * 10]; var dropped = new float[24 * 10];
            for (int i = 0; i < kept.Length; i++) { kept[i] = i % 3 == 0 ? 0.8f : 0; dropped[i] = i % 3 == 1 ? 0.5f : 0; }
            LearnPictureParts.Spectrogram(canvas, new NeoRect(0, 0, 240, 100), kept, dropped, 24, 10, 0.5, true, NeoColor.FromRgb(0x4F, 0xC3, 0xA1));
            Assert.That(canvas.Calls.Count, Is.GreaterThan(20));
        }
    }
}
