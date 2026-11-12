using System.Collections.Generic;
using NeoBuzz.Dsp;
using NeoBuzz.Gate;
using NeoBuzz.MachineTests;

namespace NeoBuzz.PedalTests
{
    /// <summary>
    /// Neo Gate against Pedal Gate (docs/specs/mastering-tools.md §5.13): the same noise bursts through both at settings the
    /// machine_replace mapping carries over (Machines/Manifests/Successors/Pedal Gate to Neo Gate.successor.json), compared
    /// as loudness, as the level in 50 ms windows and as sample differences. The successor's tolerance is stated from these.
    /// </summary>
    [TestFixture]
    public class PedalGateSuccessorTests
    {
        const int Rate = FakeHost.Rate;

        sealed class Setting
        {
            public string Name;
            public int Threshold = 60, Hysteresis = 6, Attack = 5, Hold = 100, Release = 200, Range = 0, Detection = 0, Lookahead = 0;
        }

        static readonly Setting[] Settings =
        {
            new Setting { Name = "defaults (−30 dB, 6 dB, 5 ms, 100 ms, 200 ms, −90 dB, peak)" },
            new Setting { Name = "tight (−24 dB, 3 dB, 1 ms, 30 ms, 80 ms, range −50 dB)", Threshold = 72, Hysteresis = 3, Attack = 1, Hold = 30, Release = 80, Range = 40 },
            new Setting { Name = "RMS (−30 dB, 6 dB, 5 ms, 100 ms, 200 ms)", Detection = 1 },
            new Setting { Name = "lookahead 10 ms (−36 dB, 8 dB, 2 ms, 60 ms, 150 ms)", Threshold = 48, Hysteresis = 8, Attack = 2, Hold = 60, Release = 150, Lookahead = 10 },
            new Setting { Name = "soft (−42 dB, 10 dB, 40 ms, 300 ms, 600 ms, range −24 dB)", Threshold = 36, Hysteresis = 10, Attack = 40, Hold = 300, Release = 600, Range = 66 },
        };

        /// <summary>Stereo noise in 0.5 s segments at these levels (dB rms), the first and last in silence-ish.</summary>
        static (float[] L, float[] R) Material()
        {
            double[] levels = { -12, -45, -20, -50, -15, -38, -25, -47, -18, -55, -10, -40 };
            int seg = Rate / 2, n = seg * levels.Length;
            var pl = Measure.PinkNoise(n, 1, 41);
            var pr = Measure.PinkNoise(n, 1, 42);
            var l = new float[n];
            var r = new float[n];
            for (int s = 0; s < levels.Length; s++)
            {
                float g = (float)Math.Pow(10, levels[s] / 20);
                for (int i = s * seg; i < (s + 1) * seg; i++) { l[i] = pl[i] * g; r[i] = pr[i] * g; }
            }
            return (l, r);
        }

        static ParamValues NeoValues(Setting s)
        {
            var v = new ParamValues(Params.Table);
            void Set(int p, double value) => v.Set(p, Params.Table[p].Raw(value));
            Set(P.Threshold, s.Threshold * 0.5 - 60);
            Set(P.Hysteresis, s.Hysteresis);
            Set(P.Attack, Math.Max(0.0001, s.Attack * 0.001));
            Set(P.Hold, Math.Max(0.001, s.Hold * 0.001));
            Set(P.Release, s.Release * 0.001);
            Set(P.Range, s.Range - 90);
            v.Set(P.Detection, s.Detection);
            Set(P.Lookahead, s.Lookahead);
            return v;
        }

        static (float[] L, float[] R) Neo(Setting s, float[] l, float[] r)
        {
            var e = new GateEngine(NeoValues(s));
            e.SetSampleRate(Rate);
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[256];
            var br = new float[256];
            for (int pos = 0; pos < l.Length; pos += 256)
            {
                int n = Math.Min(256, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, null, null, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        static (float[] L, float[] R) Pedal(Setting s, float[] l, float[] r) =>
            Runner.Render(Effect.Named("Pedal Gate"), l, r, 256, m =>
            {
                var g = (WDE.PedalGate.PedalGateMachine)m;
                g.ThresholdParam = s.Threshold; g.HysteresisParam = s.Hysteresis; g.AttackMs = s.Attack; g.HoldMs = s.Hold;
                g.ReleaseMs = s.Release; g.RangeParam = s.Range; g.DetectionMode = s.Detection; g.LookaheadMs = s.Lookahead;
            });

        static double Db(double x) => 20 * Math.Log10(Math.Max(1e-12, x));

        [Test]
        public void NeoGateAgainstPedalGateOnNoiseBursts()
        {
            var (l, r) = Material();
            foreach (var s in Settings)
            {
                var (nl, _) = Neo(s, l, r);
                var (pl, _) = Pedal(s, l, r);
                double sumN = 0, sumP = 0, maxDiff = 0, worstWindow = 0;
                int differing = 0;
                for (int i = 0; i < l.Length; i++)
                {
                    sumN += (double)nl[i] * nl[i];
                    sumP += (double)pl[i] * pl[i];
                    double d = Math.Abs(nl[i] - pl[i]);
                    if (d > maxDiff) maxDiff = d;
                    if (d > 1e-5) differing++;
                }
                const int Win = Rate / 20;
                for (int at = 0; at + Win <= l.Length; at += Win)
                {
                    double a = Measure.Rms(nl, at, Win), b = Measure.Rms(pl, at, Win);
                    if (Math.Max(a, b) < 1e-4) continue;            // both under −80 dBFS
                    worstWindow = Math.Max(worstWindow, Math.Abs(Db(a) - Db(b)));
                }
                double loud = 10 * Math.Log10(Math.Max(1e-20, sumN) / Math.Max(1e-20, sumP));
                PedalReport.Report($"Neo Gate against Pedal Gate, {s.Name}: loudness {loud:+0.00;-0.00;0.00} dB, worst 50 ms window {worstWindow:F2} dB, " +
                                   $"largest sample difference {maxDiff:E2}, {100.0 * differing / l.Length:F2} % of samples differ by more than 1e-5");
                Assert.That(loud, Is.EqualTo(0).Within(0.05), s.Name);
                Assert.That(worstWindow, Is.LessThan(0.3), s.Name);
            }
        }
    }
}
