// Fork (docs/specs/mcp-server.md §12.13, U6): the Neo machines' measurement suite (machines.md §6) on the Pedal
// effects U6 changed, run on their own Work (PedalHarness.cs):
//  - silence after sound settles to exact zero, with no denormal number on the way (U6's fixes);
//  - blocks of 256 and of 117 give the same output;
//  - nothing allocates per block once running (each block measured by AllocationCheck);
//  - the level at the defaults, and finite output with each parameter alone at each end;
//  - the Shaper's aliasing (a distortion without oversampling: measured and recorded, not judged);
//  - parameter changes glide: a step in a gain or a mix moves the output by small steps, and lands.
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Buzz.MachineInterface;
using NeoBuzz.MachineTests;

namespace NeoBuzz.PedalTests
{
    [TestFixture]
    public class PedalEffectTests
    {
        const int Rate = FakeHost.Rate;

        static IEnumerable<Effect> Effects() => Effect.All;

        /// <summary>Pink noise at −20 dBFS RMS, a little different on the right.</summary>
        static (float[] L, float[] R) Noise(int frames, uint seed = 12345)
        {
            var l = Measure.PinkNoise(frames, 0.1, seed);
            var r = Measure.PinkNoise(frames, 0.1, seed + 1);
            return (l, r);
        }

        static bool Subnormal(float x) => x != 0 && Math.Abs(x) < float.Epsilon * 8388608f;   // below 2^-126

        [TestCaseSource(nameof(Effects))]
        public void SilenceAfterSoundSettlesToExactZero(Effect e)
        {
            int sound = Rate, quiet = 30 * Rate;
            var (l, r) = Noise(sound + quiet);
            for (int i = sound; i < l.Length; i++) l[i] = r[i] = 0;
            var (ol, or) = Runner.Render(e, l, r, 256);
            int lastNonZero = -1, subnormals = 0;
            for (int i = sound; i < ol.Length; i++)
            {
                if (ol[i] != 0 || or[i] != 0) lastNonZero = i;
                if (Subnormal(ol[i]) || Subnormal(or[i])) subnormals++;
            }
            double settled = (lastNonZero + 1 - sound) / (double)Rate;
            PedalReport.Report($"{e.Name}: silent input settles to exact zero {Math.Max(0, settled):F2} s after it stops; {subnormals} denormal output samples");
            Assert.That(subnormals, Is.EqualTo(0), "no denormal numbers come out");
            Assert.That(lastNonZero, Is.LessThan(sound + quiet - Rate), "exact zero for the last second of 30 s of silence");
        }

        /// <summary>Settings whose tails ring longest: what a silence report must wait for.</summary>
        static IEnumerable<TestCaseData> Ringing()
        {
            foreach (var e in Effect.All)
                yield return new TestCaseData(e.Name, "defaults").SetName($"{e.Name} at its defaults");
            yield return new TestCaseData("Pedal EQ", "LMFreq=0 LMQ=16 LMGain=96").SetName("Pedal EQ, low-mid at 100 Hz, Q 10, +24 dB");
            yield return new TestCaseData("Pedal EQ", "LSFreq=0 LSGain=96").SetName("Pedal EQ, low shelf at 20 Hz, +24 dB");
            yield return new TestCaseData("Pedal Plate", "Decay=100").SetName("Pedal Plate, Decay 100");
            yield return new TestCaseData("Pedal Plate", "Decay=100 Feedback=95").SetName("Pedal Plate, Decay 100, Feedback 95");
            yield return new TestCaseData("Pedal Filter", "CutoffHz=60 Resonance=100").SetName("Pedal Filter, 60 Hz, Resonance 100");
        }

        /// <summary>
        /// ReBuzz passes an effect's input on when its Work returns false ("silent"), so a machine that says so while
        /// its tail still rings cuts the tail off. Each time a machine starts saying so after the input has faded out,
        /// the block before it was under −80 dBFS (U6: Pedal EQ stopped a ringing band at −30 dBFS, Pedal Plate at full
        /// Decay at −53 dBFS 8 s on; the machines' own thresholds are −90 dBFS on a block's input, output or state).
        /// </summary>
        [TestCaseSource(nameof(Ringing))]
        public void ASilenceReportCutsNoTail(string name, string setting)
        {
            var e = Effect.Named(name);
            int sound = Rate, quiet = 60 * Rate, fade = Rate / 20, block = 256;
            var (l, r) = Noise(sound + quiet);
            for (int i = sound - fade; i < sound; i++)
            {
                float g = 0.5f * (1f + MathF.Cos(MathF.PI * (i - (sound - fade)) / fade));
                l[i] *= g; r[i] *= g;
            }
            for (int i = sound; i < l.Length; i++) l[i] = r[i] = 0;
            var run = new Runner(e, m =>
            {
                if (setting == "defaults") return;
                foreach (var kv in setting.Split(' '))
                    m.GetType().GetProperty(kv.Split('=')[0])!.SetValue(m, int.Parse(kv.Split('=')[1]));
            }) { Reported = new List<int>() };
            var ol = new float[l.Length];
            var or = new float[l.Length];
            run.Run(l, r, 0, l.Length, block, ol, or);
            double worst = 0;
            int first = -1, previous = int.MinValue;
            foreach (int at in run.Reported)
            {
                bool starts = previous != at - block && at >= block;   // only where it starts saying so
                previous = at;
                if (!starts) continue;
                double before = 0;
                for (int i = at - block; i < at; i++)
                    before = Math.Max(before, Math.Max(Math.Abs(ol[i]), Math.Abs(or[i])));
                worst = Math.Max(worst, before);
                if (first < 0) first = at;
            }
            PedalReport.Report(first < 0 ? $"{name} ({setting}): never reports silence (60 s after its input stops)"
                : $"{name} ({setting}): reports silence from {(first - sound) / (double)Rate:F2} s after its input stops, the block before at {Measure.Db(worst):F1} dBFS at most");
            Assert.That(Measure.Db(worst), Is.LessThan(-80), "silence reported only once the tail is silent");
        }

        [TestCaseSource(nameof(Effects))]
        public void BlocksOf256And117GiveTheSameOutput(Effect e)
        {
            var (l, r) = Noise(3 * Rate);
            for (int i = 2 * Rate; i < l.Length; i++) l[i] = r[i] = 0;
            var a = Runner.Render(e, l, r, 256);
            var b = Runner.Render(e, l, r, 117);
            int differ = 0, first = -1;
            double worst = 0;
            for (int i = 0; i < l.Length; i++)
            {
                double d = Math.Max(Math.Abs(a.L[i] - b.L[i]), Math.Abs(a.R[i] - b.R[i]));
                if (d > 0) { differ++; if (first < 0) first = i; worst = Math.Max(worst, d); }
            }
            PedalReport.Report(differ == 0 ? $"{e.Name}: blocks of 256 and 117 give the same output, bit for bit"
                : $"{e.Name}: blocks of 256 and 117 differ in {differ} samples from {first / (double)Rate:F3} s, by up to {Measure.Db(worst):F1} dBFS");
            // Bit for bit, but for where a machine decides once a block that its input and tail are silent (Pedal
            // Filter, EQ, Chorus, Plate return false then, and ReBuzz passes the input): the tail they stop is under
            // their silence thresholds, −90 dBFS and less.
            Assert.That(Measure.Db(worst), Is.LessThan(-90), "the same output, whatever the block size");
        }

        [TestCaseSource(nameof(Effects))]
        public void ProcessingAllocatesNothing(Effect e)
        {
            var (l, r) = Noise(3 * Rate);
            var run = new Runner(e);
            var ol = new float[l.Length];
            var or = new float[l.Length];
            run.Run(l, r, 0, Rate, 256, ol, or);                    // warm up: first-block set-up, the JIT
            // Each block measured on its own (AllocationCheck: a failure says which blocks allocated, and what the thread
            // did in them).
            // What the test's stand-in for ReBuzz's IBuzz allocates, answering (Pedal Comp asks it for the rate), is taken off.
            long perCall = StubCallBytes();
            var (allocated, why) = AllocationCheck.Run(2 * Rate / 256, b => run.Run(l, r, Rate + 256 * b, 256, 256, ol, or), () => Stub.Calls * perCall);
            PedalReport.Report($"{e.Name}: {allocated} bytes allocated over 2 s of blocks of 256");
            Assert.That(allocated, Is.EqualTo(0), why);
        }

        /// <summary>Bytes the IBuzz stand-in allocates for one answer (it boxes it).</summary>
        static long StubCallBytes()
        {
            var buzz = BuzzGUI.Common.Global.Buzz;
            int sink = buzz.SelectedAudioDriverSampleRate;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) sink += buzz.SelectedAudioDriverSampleRate;
            return (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
        }

        [Test]
        public void TheEqInSoloAllocatesNothing()
        {
            var e = Effect.Named("Pedal EQ");
            var (l, r) = Noise(2 * Rate);
            var run = new Runner(e, m => ((WDE.PedalEQ.PedalEQMachine)m).GetType().GetProperty("LMSolo")!.SetValue(m, 1));
            var ol = new float[l.Length];
            var or = new float[l.Length];
            run.Run(l, r, 0, Rate, 256, ol, or);
            var (bytes, why) = AllocationCheck.Run(Rate / 256, b => run.Run(l, r, Rate + 256 * b, 256, 256, ol, or));
            Assert.That(bytes, Is.EqualTo(0), "soloed bands: no arrays per sample. " + why);
        }

        [TestCaseSource(nameof(Effects))]
        public void LevelsAtTheDefaultsAndEveryParameterEndAreFinite(Effect e)
        {
            var (l, r) = Noise(Rate);
            var (ol, or) = Runner.Render(e, l, r, 256);
            double gain = Measure.Db(Measure.Rms(ol, Rate / 4, 3 * Rate / 4) / Measure.Rms(l, Rate / 4, 3 * Rate / 4));
            double peak = Measure.Db(Math.Max(Measure.Peak(ol, 0, ol.Length), Measure.Peak(or, 0, or.Length)));
            PedalReport.Report($"{e.Name}: at its defaults {gain:+0.0;-0.0} dB on pink noise at -20 dBFS RMS, peak {peak:F1} dBFS");
            Assert.That(double.IsFinite(gain) && double.IsFinite(peak), Is.True);
            var problems = new List<string>();
            var probe = new Runner(e);
            foreach (var p in e.Parameters(probe.Machine))
            {
                var decl = p.GetCustomAttribute<ParameterDecl>()!;
                if (p.PropertyType != typeof(int)) continue;
                int lo = decl.MinValue, hi = decl.ValueDescriptions != null && decl.MaxValue < decl.ValueDescriptions.Length - 1 ? decl.ValueDescriptions.Length - 1 : decl.MaxValue;
                foreach (int v in new[] { lo, hi })
                {
                    var (a, b) = Runner.Render(e, l, r, 256, m => p.SetValue(m, v));
                    double pk = Math.Max(Measure.Peak(a, 0, a.Length), Measure.Peak(b, 0, b.Length));
                    if (!double.IsFinite(pk) || a.Concat(b).Any(x => !float.IsFinite(x))) problems.Add($"{p.Name} = {v}: NaN or infinity");
                    else if (pk > 1000) problems.Add($"{p.Name} = {v}: {Measure.Db(pk):F1} dBFS");
                }
            }
            Assert.That(problems, Is.Empty, string.Join("; ", problems));
        }

        [TestCase(0, "Soft")]
        [TestCase(1, "Hard")]
        [TestCase(2, "Tube")]
        [TestCase(3, "Fold")]
        [TestCase(4, "Crush")]
        public void TheShapersAliasingIsMeasured(int shape, string name)
        {
            // A 4.7 kHz sine at −6 dBFS, Drive +12 dB: harmonics above Nyquist fold back between them.
            int n = Measure.N, bin = 6417;   // odd: aliases stay off the harmonics' bins
            double hz = Measure.BinFrequency(bin, n, Rate);
            var x = Measure.Sine(hz, n + Rate / 2, 0.5, Rate);
            var (ol, _) = Runner.Render(Effect.Named("Pedal Shaper"), x, x, 256, m =>
            {
                var s = (PedalShaper.PedalShaperMachine)m;
                s.Shape = shape;
                s.Drive = 72;
            });
            var mag = Measure.Spectrum(ol, Rate / 2, n);
            double alias = Measure.AliasFloorDb(mag, bin, 20, 20000, n, Rate);
            PedalReport.Report($"Pedal Shaper ({name}, Drive +12 dB, 4.7 kHz at -6 dBFS): aliases up to {alias:F1} dB under the fundamental (no oversampling)");
            Assert.That(double.IsFinite(alias), Is.True);
        }

        /// <summary>A gain or mix stepped while a sine plays: how much the output's gain moves between two samples.</summary>
        [TestCase("Pedal Shaper", "Output", 48, 60, false)]
        [TestCase("Pedal Shaper", "Drive", 24, 36, false)]
        [TestCase("Pedal Shaper", "Mix", 0, 127, true)]
        [TestCase("Pedal Comp", "MakeupGain", 0, 6, false)]
        [TestCase("Pedal Comp", "WetDry", 0, 100, true)]
        [TestCase("Pedal MComp", "OutputGain", 24, 30, false)]
        [TestCase("Pedal MComp", "DryWet", 0, 100, true)]
        [TestCase("Pedal Plate", "Mix", 0, 100, true)]
        [TestCase("Pedal Chorus", "Mix", 0, 100, true)]
        [TestCase("Pedal Filter", "Mix", 0, 100, true)]
        [TestCase("Pedal EQ", "OutGain", 48, 60, false)]
        public void ASteppedParameterGlides(string gear, string property, int from, int to, bool mix)
        {
            var e = Effect.Named(gear);
            int frames = Rate, step = Rate / 2;
            var x = Measure.Sine(Measure.BinFrequency(1201, Measure.N, Rate), frames, 0.25, Rate);
            Action<object> setup = gear switch
            {
                "Pedal Shaper" => m => ((PedalShaper.PedalShaperMachine)m).Shape = 1,                // Hard: linear at this level
                "Pedal Comp" when !mix => m => ((WDE.PedalComp.PedalCompMachine)m).Ratio = 1,          // no compression: the makeup alone
                "Pedal Comp" => m => ((WDE.PedalComp.PedalCompMachine)m).Threshold = 40,
                "Pedal EQ" => m => m.GetType().GetProperty("LMGain")!.SetValue(m, 60),
                _ => null,
            };
            void At(Runner r, int pos, int value) { if (pos == 0) r.Set(property, from); if (pos >= step && pos < step + 256) r.Set(property, value); }
            var held = Runner.Render(e, x, x, 256, setup, (r, pos) => At(r, pos, from)).L;
            var moved = Runner.Render(e, x, x, 256, setup, (r, pos) => At(r, pos, to)).L;
            var other = Runner.Render(e, x, x, 256, setup, (r, pos) => { if (pos == 0) r.Set(property, to); }).L;
            double worst = 0;
            int glideEnd = step + 256 + (int)(0.020 * Rate);
            if (!mix)
            {
                // The gain the parameter applies, sample by sample: moved over held where held is loud.
                double pk = Measure.Peak(held, step, Rate / 10);
                double prev = double.NaN;
                for (int i = step; i < glideEnd + 1000; i++)
                {
                    if (Math.Abs(held[i]) < 0.5 * pk) { prev = double.NaN; continue; }
                    double g = Measure.Db(Math.Abs(moved[i] / held[i]));
                    if (!double.IsNaN(prev)) worst = Math.Max(worst, Math.Abs(g - prev));
                    prev = g;
                }
            }
            else
            {
                // The mix, sample by sample: (moved − held) over (other − held), where the two ends differ.
                double pk = 0;
                for (int i = step; i < step + Rate / 10; i++) pk = Math.Max(pk, Math.Abs(other[i] - held[i]));
                double prev = double.NaN;
                for (int i = step; i < glideEnd + 1000; i++)
                {
                    double d = other[i] - held[i];
                    if (Math.Abs(d) < 0.5 * pk) { prev = double.NaN; continue; }
                    double m = (moved[i] - held[i]) / d;
                    if (!double.IsNaN(prev)) worst = Math.Max(worst, Math.Abs(m - prev));
                    prev = m;
                }
            }
            // After the glide, it plays what a machine set there from the start plays.
            double after = 0;
            for (int i = glideEnd + 2000; i < frames; i++) after = Math.Max(after, Math.Abs(moved[i] - other[i]));
            PedalReport.Report($"{gear}: {property} stepped {from} → {to}: at most {(mix ? $"{worst:F5} of the mix" : $"{worst:F4} dB")} between two samples; " +
                               $"after the glide {(after == 0 ? "the same as set from the start" : $"within {Measure.Db(after):F1} dBFS of it")}");
            Assert.That(worst, Is.LessThan(mix ? 0.002 : 0.05), "a glide, not a step");
        }
    }
}
