// Neo Tune NT3 (docs/specs/neo-tune.md section 12): the artefact detector and the evaluation-mode render. The render runs the machine's engine over a file with the
// engine's event log on and writes the aligned output and the events (CSV). The detector itself is added below.

using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Tune;

namespace NeoBuzz.PitchEval
{
    internal static class TuneArtefacts
    {
        internal sealed class Render
        {
            public float[] Out;
            public TuneEvent[] Events;
            public int Latency;
            public long Dropped;
        }

        /// <summary>Runs the engine over x in 256-sample blocks at Natural <paramref name="natural"/> (the six fine controls written by the map, chromatic scale, Quality range), output aligned to the input.</summary>
        internal static Render RenderFile(float[] x, int rate, double natural, int range = 1, Action<ParamValues> tweak = null)
        {
            var pv = new ParamValues(Params.Table);
            pv.Set(P.Amount, Params.Table[P.Amount].Raw(100));
            TuneNatural.Apply(pv, natural);
            pv.Set(P.Range, range);
            pv.Set(P.Scale, 0);
            tweak?.Invoke(pv);
            var e = new TuneEngine(pv);
            e.SetSampleRate(rate);
            e.EnableEventLog(1 << 17);
            int latency = e.Latency;
            int total = x.Length + latency + 256;
            var o = new float[x.Length];
            var l = new float[256]; var r = new float[256];
            for (int pos = 0; pos < total; pos += 256)
            {
                int n = Math.Min(256, total - pos);
                for (int i = 0; i < n; i++) l[i] = r[i] = pos + i < x.Length ? x[pos + i] : 0f;
                e.Process(l, r, n);
                for (int i = 0; i < n; i++) { int at = pos + i - latency; if (at >= 0 && at < o.Length) o[at] = l[i]; }
            }
            var ev = e.Events(out long dropped);
            return new Render { Out = o, Events = ev, Latency = latency, Dropped = dropped };
        }

        internal static string EventsCsv(Render rd, int rate)
        {
            var sb = new StringBuilder("time_s,kind,a,b,c,d\n");
            foreach (var e in rd.Events.OrderBy(q => q.Sample))
                sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{e.Sample / (double)rate:F5},{e.Kind},{e.A:G6},{e.B:G6},{e.C:G6},{e.D:G6}"));
            return sb.ToString();
        }

        /// <summary>--tune-render IN.wav NATURAL OUT.wav EVENTS.csv: for the analysis scripts.</summary>
        internal static void RenderCommand(string inPath, double natural, string outWav, string csv, int range)
        {
            var (x, rate) = Wav.Read(inPath);
            var rd = RenderFile(x, rate, natural, range);
            TuneEval.WriteWav(outWav, rd.Out, rate);
            File.WriteAllText(csv, EventsCsv(rd, rate));
            Console.WriteLine($"{Path.GetFileName(inPath)} natural {natural}: {rd.Events.Length} events (dropped {rd.Dropped}), latency {rd.Latency}");
        }

        /// <summary>--tune-detect IN.wav OUT.wav [K]: the detector on two files already aligned and level-matched.</summary>
        internal static void DetectCommand(string inPath, string outPath, double k)
        {
            var (x, rate) = Wav.Read(inPath);
            var (y, _) = Wav.Read(outPath);
            var hits = TuneDetector.Detect(x, y, rate, k);
            Console.WriteLine($"{Path.GetFileName(outPath)}: {hits.Count} events: " + string.Join(" ", hits.Select(h => $"{h.Time:F3}s(x{h.Excess:F0},{h.LevelDb:F0}dB)")));
        }

        static readonly (TuneEventKind Kind, double Before, double After)[] Windows =
        {
            (TuneEventKind.SpliceReturn, 0.002, 0.0), (TuneEventKind.SpliceJump, 0.002, 0.0), (TuneEventKind.MarkOutlier, 0.008, 0.002),
            (TuneEventKind.ShiftStep, 0.010, 0.002), (TuneEventKind.TargetChange, 0.015, 0.003), (TuneEventKind.TransientOn, 0.006, 0.0), (TuneEventKind.TransientOff, 0.006, 0.0),
            (TuneEventKind.PsolaBypassOn, 0.006, 0.0), (TuneEventKind.PsolaBypassOff, 0.006, 0.0), (TuneEventKind.MethodSwitch, 0.010, 0.0),
            (TuneEventKind.VoicedStart, 0.010, 0.0), (TuneEventKind.VoicedEnd, 0.010, 0.0), (TuneEventKind.PitchHold, 0.010, 0.0),
            (TuneEventKind.MarkRunStart, 0.006, 0.0), (TuneEventKind.MarkRunEnd, 0.006, 0.0),
        };

        /// <summary>The engine events around a hit: those whose window (a splice: its crossfade, the others a few ms either side) contains the hit; the nearest first.</summary>
        internal static string Cause(double t, TuneEvent[] events, int rate)
        {
            var found = new List<(double D, TuneEventKind K)>();
            foreach (var e in events)
            {
                double et = e.Sample / (double)rate;
                foreach (var (kind, before, after) in Windows)
                {
                    if (kind != e.Kind) continue;
                    double len = kind == TuneEventKind.SpliceJump ? e.D / rate : kind == TuneEventKind.SpliceReturn ? e.B / rate : 0;
                    double lo = et - before, hi = et + len + after + (kind == TuneEventKind.SpliceReturn || kind == TuneEventKind.SpliceJump ? 0.002 : 0);
                    // marks and shifts are logged when the engine saw them, the audio they concern is up to a period later
                    if (t >= lo && t <= hi) found.Add((Math.Abs(t - et), kind));
                }
            }
            if (found.Count == 0) return "none";
            found.Sort((a, b) => a.D.CompareTo(b.D));
            return string.Join("+", found.Select(f => f.K.ToString()).Distinct().Take(3));
        }

        /// <summary>
        /// An instant pitch step is the hard effect: even an ideal one (a synthetic voice whose pitch is stepped, nothing spliced) has a burst in the aperiodic residual, about
        /// -12 dB of the signal for half a semitone, -9 dB for one, -6 dB for two (measured with the detector on synthetic voices, NT3 report). A hit near a target change or a shift step is
        /// an intended step if its level is within 3 dB of that line, -9 + 3 log2(step / 1 st) dB; anything louder, and every hit with another cause, is an artefact.
        /// </summary>
        internal static void Classify(TuneDetector.Hit h, TuneEvent[] events, int rate)
        {
            double sum = 0; bool stepCause = h.Cause.StartsWith("ShiftStep") || h.Cause.StartsWith("TargetChange");
            foreach (var e in events)
                if (e.Kind == TuneEventKind.ShiftStep && Math.Abs(e.Sample / (double)rate - h.Time) < 0.008) sum += Math.Abs(e.B - e.A);
            h.StepSemis = sum;
            h.Step = stepCause && sum >= 0.2 && h.LevelDb <= -9 + 3 * Math.Log2(Math.Max(sum, 0.25)) + 3;
        }

        internal static readonly double[] Naturals = { 0.0, 0.25, 0.5, 0.75, 1.0 };

        internal sealed class FileResult
        {
            public string Id; public double Natural; public int Count, Steps; public List<TuneDetector.Hit> Hits; public TuneEvent[] Events;
        }

        /// <summary>
        /// --tune-artefacts DIR [OUT.md]: for each NN-*-original.wav in DIR, the engine at Natural 0, 25, 50, 75 and 100 (event log on), the output level-matched to the input, the detector, each
        /// hit tied to the engine event that caused it. The control (the input against itself at another level and a fraction of a sample later) must find nothing.
        /// </summary>
        internal static List<FileResult> Run(string dir, StringBuilder md, Action<ParamValues> tweak = null, string tag = "", string wavDir = null)
        {
            var all = new List<FileResult>();
            md?.AppendLine("| Excerpt | Natural | artefacts + intended steps | artefact times (s), nearest engine event, level |");
            md?.AppendLine("|---|---:|---:|---|");
            foreach (var f in Directory.GetFiles(dir, "*-original.wav").OrderBy(x => x, StringComparer.Ordinal))
            {
                var id = Path.GetFileName(f).Replace("-original.wav", "");
                var (x, rate) = Wav.Read(f);
                var qi = TuneDetector.Residual(x, rate);
                // control: the input delayed by 0.37 sample through a linear interpolation and at -3 dB: nothing to find
                var ctl = new float[x.Length];
                for (int i = 1; i < x.Length; i++) ctl[i] = (float)(0.7 * (0.63 * x[i] + 0.37 * x[i - 1]));
                int cn = TuneDetector.Detect(x, TuneDetector.LevelMatch(x, ctl), rate, TuneDetector.DefaultK, qi).Count;
                md?.AppendLine($"| {id} | control | {cn} | (the input against a level-changed, fractionally delayed copy of itself) |");
                foreach (var n in Naturals)
                {
                    var rd = RenderFile(x, rate, n, 1, tweak);
                    var y = TuneDetector.LevelMatch(x, rd.Out);
                    if (wavDir != null) TuneEval.WriteWav(Path.Combine(wavDir, $"{id}--{tag}natural-{(int)Math.Round(n * 100):000}.wav"), y, rate);
                    var hits = TuneDetector.Detect(x, y, rate, TuneDetector.DefaultK, qi);
                    foreach (var h in hits) { h.Cause = Cause(h.Time, rd.Events, rate); Classify(h, rd.Events, rate); }
                    var r = new FileResult { Id = id, Natural = n, Count = hits.Count(h => !h.Step), Steps = hits.Count(h => h.Step), Hits = hits, Events = rd.Events };
                    all.Add(r);
                    md?.AppendLine($"| {id} | {n * 100:F0} | {r.Count} + {r.Steps} steps | " + string.Join(", ", hits.Where(h => !h.Step).Select(h => $"{h.Time:F3} {h.Cause} {h.LevelDb:F0}dB")) + " |");
                }
            }
            return all;
        }

        internal static void RunCommand(string dir, string outMd, string wavDir)
        {
            var md = new StringBuilder();
            if (wavDir != null) Directory.CreateDirectory(wavDir);
            var res = Run(dir, md, null, "", wavDir);
            md.AppendLine();
            var causes = res.SelectMany(r => r.Hits).Where(h => !h.Step).GroupBy(h => h.Cause.Split('+')[0]).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
            md.AppendLine($"Artefacts {res.Sum(r => r.Count)}, intended steps {res.Sum(r => r.Steps)}.");
            md.AppendLine("Artefacts by nearest cause: " + string.Join(", ", causes));
            File.WriteAllText(outMd, md.ToString());
            Console.Write(md.ToString());
        }
    }
}
