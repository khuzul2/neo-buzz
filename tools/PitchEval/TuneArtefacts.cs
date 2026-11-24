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
            public float[] Rho; public double[] Pos;      // the splice reader's speed and position per output sample (NT4, for the null render)
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
            e.EnableReaderTrace(total);
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
            e.ReaderTrace(out var trR, out var trP);
            return new Render { Out = o, Events = ev, Latency = latency, Dropped = dropped, Rho = trR, Pos = trP };
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
            TuneEval.WriteWav(outWav, TuneDetector.LevelMatch(x, rd.Out), rate);      // level-matched to the input, as the detector sees it (NT4)
            TuneEval.WriteWav(Path.ChangeExtension(outWav, ".null.wav"), TuneDetector.LevelMatch(x, NullRender(x, rd).Y), rate);
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

        /// <summary>--tune-resid IN.wav OUT1.wav OUT2.wav CSV: the 1 ms residual envelopes (input and the two outputs, each against its own period) every 0.5 ms, for the diagnosis plots (NT4).</summary>
        internal static void ResidCommand(string inPath, string out1, string out2, string csv)
        {
            var (x, rate) = Wav.Read(inPath);
            var (y1, _) = Wav.Read(out1); var (y2, _) = Wav.Read(out2);
            var e0 = TuneDetector.Envelope(TuneDetector.Residual(x, rate), rate);
            var e1 = TuneDetector.Envelope(TuneDetector.Residual(y1, rate), rate);
            var e2 = TuneDetector.Envelope(TuneDetector.Residual(y2, rate), rate);
            var sb = new StringBuilder("t,input,a,b\n");
            for (int i = 0; i < x.Length && i < y1.Length && i < y2.Length; i += rate / 2000)
                sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{i / (double)rate:F5},{e0[i]:G4},{e1[i]:G4},{e2[i]:G4}"));
            File.WriteAllText(csv, sb.ToString());
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
        /// A change of the pitch ratio is the hard effect, and it is not free of a burst in the aperiodic residual whatever makes it: the cycle after a step is not the cycle before.
        /// An ideal resampler (cubic spline, the engine's own ratio trajectory, no splice) finds the very same hit at the very same place (NT3 report: 02 Natural 0 at 0.735 s, x15, +1 dB
        /// in both), and a synthetic voice stepped by one semitone has one at -9 dB. So a hit near a target change or a shift step (the shift moved by 0.2 semitones or more
        /// within 8 ms) is counted as an intended step, listed apart, and every hit with another cause is an artefact: a splice, a mark, a hold, or nothing the engine logged.
        /// </summary>
        internal static void Classify(TuneDetector.Hit h, TuneEvent[] events, int rate)
        {
            double sum = 0; bool stepCause = h.Cause.StartsWith("ShiftStep") || h.Cause.StartsWith("TargetChange");
            foreach (var e in events)
                if (e.Kind == TuneEventKind.ShiftStep && Math.Abs(e.Sample / (double)rate - h.Time) < 0.008) sum += Math.Abs(e.B - e.A);
            h.StepSemis = sum;
            h.Step = stepCause && sum >= 0.2;
        }

        /// <summary>
        /// The null render (NT4): the input read by an ideal resampler (the engine's 16-tap sinc) at the engine's own reading speed per sample, with no splice, no crossfade and no ease. Where the
        /// engine's reader is at the nominal position and its speed is 1 (no correction) the null path is the input itself. Returns the signal and the input position it reads per sample.
        /// What the detector finds in it, at the same output time, is not the engine's: it is the voice (or the change of ratio) itself.
        /// </summary>
        internal const double DefaultNullShare = 0.5;

        internal static (float[] Y, double[] Src) NullRender(float[] x, Render rd)
        {
            int n = x.Length;
            var y = new float[n]; var src = new double[n];
            var tab = TuneSinc.Table;
            double p = 0;
            for (int t = 0; t < n; t++)
            {
                double rho = t < rd.Rho.Length && rd.Rho[t] > 0 ? rd.Rho[t] : 1.0;
                double ePos = t < rd.Pos.Length ? rd.Pos[t] : t;
                if (rho == 1.0 && Math.Abs(ePos - t) < 0.5) p = t;      // no correction: the input as it is
                src[t] = p;
                long pi = (long)Math.Floor(p); double f = p - pi;
                double ph = f * TuneSinc.Phases; int i = (int)ph; float u = (float)(ph - i);
                int b0 = i * TuneSinc.Taps, b1 = b0 + TuneSinc.Taps;
                float s0 = 0, s1 = 0;
                for (int k = 0; k < TuneSinc.Taps; k++)
                {
                    long q = pi - 7 + k;
                    if (q < 0 || q >= n) continue;
                    s0 += tab[b0 + k] * x[q]; s1 += tab[b1 + k] * x[q];
                }
                y[t] = s0 + u * (s1 - s0);
                p += rho;
            }
            return (y, src);
        }

        /// <summary>
        /// Attribution of the hits of one render (NT4): each hit is looked up in the null render. A hit that the null render has within 6 ms is not made by the engine (class "voice" when the
        /// shift is about constant around it, "ratio step" when it changes by 0.2 semitones or more within 8 ms); the rest are "engine".
        /// </summary>
        internal static void Attribute(float[] x, int rate, Render rd, float[] qi, List<TuneDetector.Hit> hits)
        {
            var (y0, src) = NullRender(x, rd);
            var nullLow = TuneDetector.Detect(x, TuneDetector.LevelMatch(x, y0), rate, 1.0, qi, src);
            var ei = TuneDetector.Envelope(qi, rate);
            var lvIn = TuneDetector.Envelope(x, rate, 10.0);
            foreach (var h in hits)
            {
                int i = (int)(h.Time * rate);
                // the same disturbance is a few ms apart in the two renders (the splice moves the reader by a period), so +-25 ms (a disturbance lasts two or three periods); the null render explains a hit when it has half of its excess
                h.NullExcess = nullLow.Where(q => Math.Abs(q.Time - h.Time) <= 0.025).Select(q => q.Excess).DefaultIfEmpty(0).Max();
                h.InNull = h.NullExcess >= DefaultNullShare * h.Excess;
                h.SrcTime = (i < src.Length ? src[i] : i) / rate;
                double lo = 1e9, hi = -1e9;
                for (int k = Math.Max(0, i - (int)(0.008 * rate)); k < Math.Min(rd.Rho.Length, i + (int)(0.008 * rate)); k += 8)
                {
                    double r = rd.Rho[k] > 0 ? rd.Rho[k] : 1.0, sh = 12 * Math.Log2(r);
                    lo = Math.Min(lo, sh); hi = Math.Max(hi, sh);
                }
                h.ShiftRange = hi >= lo ? hi - lo : 0;
                int si = Math.Clamp((int)(h.SrcTime * rate), 0, x.Length - 1);
                double best = 0; for (int k = Math.Max(0, si - (int)(0.004 * rate)); k < Math.Min(x.Length, si + (int)(0.004 * rate)); k++) best = Math.Max(best, ei[k]);
                h.InputRoughDb = 20 * Math.Log10((best + 1e-9) / (lvIn[si] + 1e-9));
                h.Class = !h.InNull ? "engine" : h.ShiftRange >= 0.2 || h.Step ? "ratio step" : "voice";
            }
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
                    Attribute(x, rate, rd, qi, hits);
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
            md.AppendLine();
            md.AppendLine("Attribution by the null render (an ideal resampler along the engine's own ratio path, no splice): engine = not in the null render; voice = in it, the shift about constant; ratio step = in it, the shift moving by 0.2 st or more.");
            md.AppendLine();
            md.AppendLine("| Natural | hits | not intended steps (NT3 count) | engine-made | voice's own | ratio step |");
            md.AppendLine("|---:|---:|---:|---:|---:|---:|");
            foreach (var g in res.GroupBy(r => r.Natural).OrderBy(g => g.Key))
            {
                var hs = g.SelectMany(r => r.Hits).ToList();
                md.AppendLine($"| {g.Key * 100:F0} | {hs.Count} | {hs.Count(h => !h.Step)} | {hs.Count(h => h.Class == "engine")} | {hs.Count(h => h.Class == "voice")} | {hs.Count(h => h.Class == "ratio step")} |");
            }
            md.AppendLine();
            md.AppendLine("| Excerpt | Natural | time (s) | reads input (s) | class | nearest engine event | shift range (st) | input's own residual (dB re level) | hit level (dB) | excess (engine / null) |");
            md.AppendLine("|---|---:|---:|---:|---|---|---:|---:|---:|---:|");
            foreach (var r in res.Where(r => r.Natural <= 0.5))
                foreach (var h in r.Hits)
                    md.AppendLine($"| {r.Id} | {r.Natural * 100:F0} | {h.Time:F3} | {h.SrcTime:F3} | {h.Class} | {h.Cause} | {h.ShiftRange:F2} | {h.InputRoughDb:F0} | {h.LevelDb:F0} | {h.Excess:F1} / {h.NullExcess:F1} |");
            md.AppendLine("Artefacts by nearest cause: " + string.Join(", ", causes));
            File.WriteAllText(outMd, md.ToString());
            Console.Write(md.ToString());
        }

        /// <summary>
        /// --tune-hypotheses DIR OUT.md: the same files and detector with the engine's other paths forced (Method PSOLA, with and without the Transients dry hold, and with a formant shift),
        /// to put a number on each suspected cause: how many artefacts, how many of them near a transient toggle, a method switch, a PSOLA bypass toggle. A switch of method in the middle of a phrase
        /// cannot happen at a fixed setting (a change of Method, Range, Target or Formant resets the engine), so the count of MethodSwitch events is 0 by construction and is reported as measured.
        /// </summary>
        internal static void HypothesesCommand(string dir, string outMd)
        {
            var md = new StringBuilder();
            md.AppendLine("| Variant | Natural | files | artefacts | steps | method switches | transient on/off | PSOLA bypass toggles | by nearest cause |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---|");
            var variants = new (string Name, Action<ParamValues> Tweak)[]
            {
                ("Auto (splice)", null),
                ("PSOLA, Transients on", pv => pv.Set(P.Method, 2)),
                ("PSOLA, Transients off", pv => { pv.Set(P.Method, 2); pv.Set(P.Transients, 0); }),
                ("PSOLA, Formant +1 st", pv => { pv.Set(P.Method, 2); pv.Set(P.Formant, Params.Table[P.Formant].Raw(1)); }),
            };
            foreach (var (name, tweak) in variants)
                foreach (var n in new[] { 0.0, 0.5, 1.0 })
                {
                    int files = 0, art = 0, steps = 0, sw = 0, tr = 0, by = 0;
                    var causes = new Dictionary<string, int>();
                    foreach (var f in Directory.GetFiles(dir, "*-original.wav").OrderBy(x => x, StringComparer.Ordinal))
                    {
                        var (x, rate) = Wav.Read(f);
                        var rd = RenderFile(x, rate, n, 1, tweak);
                        var y = TuneDetector.LevelMatch(x, rd.Out);
                        var hits = TuneDetector.Detect(x, y, rate);
                        foreach (var h in hits) { h.Cause = Cause(h.Time, rd.Events, rate); Classify(h, rd.Events, rate); }
                        files++; art += hits.Count(h => !h.Step); steps += hits.Count(h => h.Step);
                        sw += rd.Events.Count(e => e.Kind == TuneEventKind.MethodSwitch);
                        tr += rd.Events.Count(e => e.Kind == TuneEventKind.TransientOn || e.Kind == TuneEventKind.TransientOff);
                        by += rd.Events.Count(e => e.Kind == TuneEventKind.PsolaBypassOn || e.Kind == TuneEventKind.PsolaBypassOff);
                        foreach (var h in hits.Where(h => !h.Step)) { var k = h.Cause.Split('+')[0]; causes[k] = causes.GetValueOrDefault(k) + 1; }
                    }
                    md.AppendLine($"| {name} | {n * 100:F0} | {files} | {art} | {steps} | {sw} | {tr} | {by} | {string.Join(", ", causes.OrderByDescending(c => c.Value).Select(c => c.Key + " " + c.Value))} |");
                }
            File.WriteAllText(outMd, md.ToString());
            Console.Write(md.ToString());
        }

        /// <summary>The Natural value the blind test's listener picked as the most natural, per excerpt number (01 - 06); the pack of the next blind test renders it.</summary>
        static readonly double[] PickedNatural = { 1.0, 1.0, 0.5, 1.0, 0.5, 1.0 };

        /// <summary>
        /// --tune-pack LABEL ORIGDIR OUTDIR [N1,N2,..]: for each NN-*-original.wav, the engine this binary was built with ("nt2" or "nt3" in LABEL) at Natural 0 and at the picked Natural, scaled to the
        /// original's RMS as the NT2 pack was, written as NN-excerpt--LABEL-natural-XXX.wav (the original is copied once as NN-excerpt--original.wav).
        /// </summary>
        internal static void PackCommand(string label, string dir, string outDir, string naturals = null)
        {
            double[] fixedN = naturals == null ? null : naturals.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Directory.CreateDirectory(outDir);
            foreach (var f in Directory.GetFiles(dir, "*-original.wav").OrderBy(x => x, StringComparer.Ordinal))
            {
                var id = Path.GetFileName(f).Replace("-original.wav", "");
                int no = int.Parse(id.Substring(0, 2)) - 1;
                var (x, rate) = Wav.Read(f);
                double rms0 = Math.Sqrt(x.Sum(v => (double)v * v) / Math.Max(1, x.Length));
                TuneEval.WriteWav(Path.Combine(outDir, id + "--original.wav"), x, rate);
                foreach (var n in fixedN ?? new[] { 0.0, PickedNatural[Math.Clamp(no, 0, 5)] })
                {
                    var y = RenderFile(x, rate, n).Out;
                    double ry = Math.Sqrt(y.Sum(v => (double)v * v) / Math.Max(1, y.Length));
                    double g = ry > 1e-9 ? rms0 / ry : 1.0;
                    var o = new float[y.Length];
                    for (int i = 0; i < o.Length; i++) o[i] = (float)Math.Clamp(y[i] * g, -0.98, 0.98);
                    TuneEval.WriteWav(Path.Combine(outDir, $"{id}--{label}-natural-{(int)Math.Round(n * 100):000}.wav"), o, rate);
                }
            }
        }
    }
}
