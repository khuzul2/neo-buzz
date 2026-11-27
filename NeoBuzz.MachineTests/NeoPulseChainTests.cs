using System.Xml.Linq;
using NeoBuzz.Dsp;
using NeoBuzz.Pulse;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Pulse chain render tests (docs/specs/neo-pulse.md): drives Neo Drums (presets 1–7) and Neo Poly
    /// (preset 8) over block-level note delivery as the host note wire does, verifying rhythmic placement,
    /// audibility and pairwise separation of all 8 factory presets.
    /// </summary>
    internal static class PulseChainRig
    {
        public const int Rate = 48000;
        public const double Bpm = 120.0;

        public static string RepoRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "ReBuzz.sln"))) return d.FullName;
            throw new DirectoryNotFoundException("Repository root not found");
        }

        public static List<(string Name, string Comment, ParamValues Values)> LoadPresets()
        {
            string file = Path.Combine(RepoRoot(), "Machines", "NeoPulse", "Neo Pulse.prs.xml");
            var list = new List<(string, string, ParamValues)>();
            foreach (var item in XDocument.Load(file).Root!.Elements("Item"))
            {
                var v = new ParamValues(Params.Table);
                foreach (var p in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter"))
                    v.Set((int)p.Attribute("Index")!, (int)p.Attribute("Value")!);
                list.Add(((string)item.Attribute("Key")!, (string)item.Element("Preset")!.Element("Comment") ?? "", v));
            }
            return list;
        }

        public static bool IsMelodicPreset(string name) => name.StartsWith("Melodic", StringComparison.OrdinalIgnoreCase);

        public static ParamValues SynthPatch()
        {
            var v = new ParamValues(NeoBuzz.Poly.Params.Table);
            void S(int p, double x) => v.Set(p, NeoBuzz.Poly.Params.Table[p].Raw(x));
            S(NeoBuzz.Poly.P.VoiceMode, 0);        // Poly
            S(NeoBuzz.Poly.P.O1Shape, 2.0);        // saw
            S(NeoBuzz.Poly.P.O2Shape, 2.5);        // pulse-saw
            S(NeoBuzz.Poly.P.O2Level, 0.45);
            S(NeoBuzz.Poly.P.Drift, 0.0);
            S(NeoBuzz.Poly.P.F1Cutoff, 2600.0);
            S(NeoBuzz.Poly.P.F1Reso, 0.18);
            S(NeoBuzz.Poly.P.F1Env, 0.35);
            S(NeoBuzz.Poly.P.FltA, 0.004);
            S(NeoBuzz.Poly.P.FltD, 0.25);
            S(NeoBuzz.Poly.P.FltS, 0.2);
            S(NeoBuzz.Poly.P.AmpA, 0.004);
            S(NeoBuzz.Poly.P.AmpD, 0.28);
            S(NeoBuzz.Poly.P.AmpS, 0.35);
            S(NeoBuzz.Poly.P.AmpR, 0.14);
            S(NeoBuzz.Poly.P.Volume, -9.0);
            return v;
        }

        public static (float[] L, float[] R, int NoteOnCount) RenderIntoDrums(ParamValues pulseValues, double seconds, double bpm = Bpm)
        {
            var pulse = new PulseEngine(pulseValues);
            pulse.SetSampleRate(Rate);

            var drumValues = new ParamValues(NeoBuzz.Drums.Params.Table);
            var drums = new NeoBuzz.Drums.DrumsEngine(drumValues);
            drums.SetSampleRate(Rate);

            int total = (int)Math.Round(seconds * Rate);
            var outL = new float[total];
            var outR = new float[total];
            var bl = new float[64];
            var br = new float[64];
            double spb = Rate * 60.0 / bpm;
            double beat = 0.0;
            int noteOns = 0;

            for (int pos = 0; pos < total; pos += 64)
            {
                int n = Math.Min(64, total - pos);
                var blk = new PulseBlock { Samples = n, Playing = true, Beat = beat, SamplesPerBeat = spb, BeatsPerBar = 4.0 };
                pulse.Process(in blk);
                for (int i = 0; i < pulse.OutCount; i++)
                {
                    var ev = pulse.Out[i];
                    if (ev.Kind == PulseKind.NoteOn)
                    {
                        drums.NoteOn(1000 + ev.Data1, ev.Data1, ev.Data2 / 127f);
                        noteOns++;
                    }
                    else if (ev.Kind == PulseKind.NoteOff)
                    {
                        drums.NoteOff(1000 + ev.Data1);
                    }
                }
                drums.Process(bl, br, n);
                Array.Copy(bl, 0, outL, pos, n);
                Array.Copy(br, 0, outR, pos, n);
                beat += n / spb;
            }
            return (outL, outR, noteOns);
        }

        public static (float[] L, float[] R, int NoteOnCount) RenderIntoPoly(ParamValues pulseValues, double seconds, double bpm = Bpm)
        {
            var pulse = new PulseEngine(pulseValues);
            pulse.SetSampleRate(Rate);

            var poly = new NeoBuzz.Poly.PolyEngine(SynthPatch());
            poly.SetSampleRate(Rate);

            int total = (int)Math.Round(seconds * Rate);
            var outL = new float[total];
            var outR = new float[total];
            var bl = new float[64];
            var br = new float[64];
            double spb = Rate * 60.0 / bpm;
            double beat = 0.0;
            int noteOns = 0;

            for (int pos = 0; pos < total; pos += 64)
            {
                int n = Math.Min(64, total - pos);
                var blk = new PulseBlock { Samples = n, Playing = true, Beat = beat, SamplesPerBeat = spb, BeatsPerBar = 4.0 };
                pulse.Process(in blk);
                for (int i = 0; i < pulse.OutCount; i++)
                {
                    var ev = pulse.Out[i];
                    if (ev.Kind == PulseKind.NoteOn)
                    {
                        poly.NoteOn(1000 + ev.Data1, ev.Data1, ev.Data2 / 127f);
                        noteOns++;
                    }
                    else if (ev.Kind == PulseKind.NoteOff)
                    {
                        poly.NoteOff(1000 + ev.Data1);
                    }
                }
                poly.Process(bl, br, n);
                Array.Copy(bl, 0, outL, pos, n);
                Array.Copy(br, 0, outR, pos, n);
                beat += n / spb;
            }
            return (outL, outR, noteOns);
        }
    }

    [TestFixture]
    public class NeoPulseChainTests
    {
        [Test]
        public void ChainIntoNeoDrumsPlaysEuclideanRhythmOnExactBeats()
        {
            // Lane 1: Kick (36) E(4,16) at 1/16, 120 BPM -> hits on beats 0, 1, 2, 3 (every 24,000 samples).
            var v = new ParamValues(Params.Table);
            for (int lane = 0; lane < PulseEngine.LaneCount; lane++) v.Set(PulseEngine.LaneParam(lane, PulseEngine.FPulses), 0);
            v.Set(P.Rate, 4);
            v.Set(P.L1Steps, 16);
            v.Set(P.L1Pulses, 4);
            v.Set(P.L1Note, 36);
            v.Set(P.L1Vel, 120);

            var (l, r, ons) = PulseChainRig.RenderIntoDrums(v, 2.0);
            Assert.That(ons, Is.EqualTo(4), "4 kick hits over 1 bar at 120 BPM");
            // Check loud onset energy in the 20 ms window right after each beat and quiet before beat 1..3.
            for (int beat = 0; beat < 4; beat++)
            {
                int start = beat * 24000;
                double hitRms = Measure.Rms(l, start, 960);
                Assert.That(Measure.Db(hitRms), Is.GreaterThan(-24.0), $"beat {beat} kick audible");
                if (beat > 0)
                {
                    double preRms = Measure.Rms(l, start - 2000, 1000);
                    Assert.That(hitRms, Is.GreaterThan(preRms * 3.0), $"beat {beat} has clear transient onset");
                }
            }
            Measure.Report($"Neo Pulse -> Neo Drums chain: 4/4 kick verified on beats 0..3, peak {Measure.Db(Measure.Peak(l, 0, l.Length)):F1} dBFS");
        }

        [Test]
        public void AllEightPresetsAreAudibleAndDistinctThroughTargets()
        {
            var presets = PulseChainRig.LoadPresets();
            Assert.That(presets.Count, Is.EqualTo(8), "expected 8 factory presets");

            var renders = new List<(string Name, float[] L)>();
            foreach (var (name, _, v) in presets)
            {
                var (l, r, ons) = PulseChainRig.IsMelodicPreset(name)
                    ? PulseChainRig.RenderIntoPoly(v, 6.0)
                    : PulseChainRig.RenderIntoDrums(v, 6.0);
                double rmsDb = Measure.Db(Measure.Rms(l, 0, l.Length));
                double peakDb = Measure.Db(Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length)));
                Measure.Report($"Neo Pulse preset '{name}': {ons} hits, RMS {rmsDb:F1} dBFS, peak {peakDb:F1} dBFS");
                Assert.That(ons, Is.GreaterThan(10), $"preset '{name}' fires notes");
                Assert.That(rmsDb, Is.GreaterThan(-30.0), $"preset '{name}' clearly audible");
                Assert.That(peakDb, Is.LessThanOrEqualTo(0.0), $"preset '{name}' stays within headroom");
                renders.Add((name, l));
            }

            // Pairwise energy difference across normalized 50 ms envelopes proves every preset is clearly distinct.
            double minDist = double.MaxValue;
            string closestPair = "";
            for (int i = 0; i < renders.Count; i++)
            {
                for (int j = i + 1; j < renders.Count; j++)
                {
                    double d = EnvelopeDistance(renders[i].L, renders[j].L);
                    if (d < minDist) { minDist = d; closestPair = $"{renders[i].Name} vs {renders[j].Name}"; }
                }
            }
            Measure.Report($"Neo Pulse 8 presets min pairwise envelope distance: {minDist:F3} ({closestPair})");
            Assert.That(minDist, Is.GreaterThan(0.15), $"all 8 presets clearly distinct ({closestPair})");
        }

        static double EnvelopeDistance(float[] a, float[] b)
        {
            const int Win = 2400; // 50 ms
            int frames = Math.Min(a.Length, b.Length) / Win;
            var ea = new double[frames];
            var eb = new double[frames];
            double na = 0, nb = 0;
            for (int f = 0; f < frames; f++)
            {
                ea[f] = Measure.Rms(a, f * Win, Win);
                eb[f] = Measure.Rms(b, f * Win, Win);
                na += ea[f] * ea[f];
                nb += eb[f] * eb[f];
            }
            na = Math.Sqrt(Math.Max(1e-20, na));
            nb = Math.Sqrt(Math.Max(1e-20, nb));
            double sum = 0;
            for (int f = 0; f < frames; f++)
            {
                double d = ea[f] / na - eb[f] / nb;
                sum += d * d;
            }
            return Math.Sqrt(sum);
        }
    }
}
