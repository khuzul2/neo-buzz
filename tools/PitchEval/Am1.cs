// Neo Audio to MIDI on real recordings (docs/specs/audio-to-midi.md §8): the engine as the machine runs it (256 samples at a time), scored on
//  - GuitarSet (Zenodo 3371780, CC BY 4.0): the hexaphonic pickup's per-string mono lines against the JAMS note annotations of each string:
//    note F-measure (onset within 50 ms, pitch within 50 cents; the exact semitone too), latency to the annotated onset, octave errors, extra notes
//    per minute, Melody mode, Guitar range, Fast and Stable.
//  - IDMT-SMT-Drums (Zenodo 7544164, CC BY-NC-ND 4.0, evaluation only): the drum mixes of three kits (real, techno, wave) against the onset and
//    class annotations (kick, snare, hat): per class F-measure and timing, with no calibration and with five hits of each drum calibrated from the
//    same kit's single-drum training recordings.
//  - MDB-stem-synth: the live engine on the stems (note F-measure against notes cut from the annotated f0, and pitch accuracy on the voiced frames).
// And the piano-roll data (truth against output notes) of three excerpts per mode, for tools/am1/plot_rolls.py. Nothing here is committed or uploaded
// but the report and those figures' numbers.

using System.Globalization;
using System.Text;
using System.Text.Json;
using NeoBuzz.AudioToMidi;
using NeoBuzz.Dsp;

namespace NeoBuzz.PitchEval
{
    internal static class Am1
    {
        sealed class Note { public double Start, End; public int Pitch, Vel; }
        sealed class Truth { public double Start, End, Midi; }

        /// <summary>Matching counts, latencies and octave errors of one configuration over many lines.</summary>
        sealed class Acc
        {
            public int Ref, Out, Hit, HitExact, Octave;
            public double Seconds;
            public readonly List<double> Latency = new();
            public void Add(Acc o) { Ref += o.Ref; Out += o.Out; Hit += o.Hit; HitExact += o.HitExact; Octave += o.Octave; Seconds += o.Seconds; Latency.AddRange(o.Latency); }
            public double P => Out == 0 ? 0 : (double)Hit / Out;
            public double R => Ref == 0 ? 0 : (double)Hit / Ref;
            public double F => P + R == 0 ? 0 : 2 * P * R / (P + R);
            public double FExact => Out + Ref == 0 ? 0 : 2.0 * HitExact / (Out + Ref);
            public double ChatterPerMinute => Seconds <= 0 ? 0 : (Out - Hit - Octave) / (Seconds / 60);
        }

        static string Pc(double v) => (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
        static string Ms(double v) => double.IsNaN(v) ? "-" : (v * 1000).ToString("F1", CultureInfo.InvariantCulture) + " ms";
        static double Median(List<double> l) => l.Count == 0 ? double.NaN : l.OrderBy(v => v).ElementAt(l.Count / 2);
        static double Pctl(List<double> l, double p) => l.Count == 0 ? double.NaN : l.OrderBy(v => v).ElementAt(Math.Min(l.Count - 1, (int)(l.Count * p)));

        // ---------------------------------------------------------------------------------------------------------------- the engine

        static ParamValues Values(Action<ParamValues> setup)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Monitor, 0);
            setup?.Invoke(v);
            if (int.TryParse(Environment.GetEnvironmentVariable("AM1_LEVEL"), out var lv)) v.Set(P.Level, lv);
            return v;
        }

        /// <summary>Runs the engine over a line and returns the notes (note ons with their offs) and the drum hits (time, note).</summary>
        static (List<Note> Notes, List<(double T, int Note, int Vel)> Hits) Run(float[] x, int rate, AudioMidiEngine e)
        {
            var notes = new List<Note>();
            var hits = new List<(double, int, int)>();
            var open = new Dictionary<int, Note>();
            var buf = new float[AudioMidiEngine.MaxBlock];
            void Take(long basePos, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    var m = e.Events[i];
                    double t = (basePos + m.Offset) / (double)rate;
                    if (m.Stream == MidiStream.Trigger)
                    {
                        if (m.Kind == MidiKind.NoteOn) hits.Add((t, m.Data1, m.Data2));
                    }
                    else if (m.Stream == MidiStream.Pitch)
                    {
                        if (m.Kind == MidiKind.NoteOn) { var n = new Note { Start = t, End = double.NaN, Pitch = m.Data1, Vel = m.Data2 }; notes.Add(n); open[m.Data1] = n; }
                        else if (m.Kind == MidiKind.NoteOff && open.TryGetValue(m.Data1, out var n)) { n.End = t; open.Remove(m.Data1); }
                    }
                }
            }
            for (int pos = 0; pos < x.Length; pos += 256)
            {
                int n = Math.Min(256, x.Length - pos);
                Array.Copy(x, pos, buf, 0, n);
                Take(pos, e.Process(buf, n));
            }
            Take(x.Length, e.Flush());
            foreach (var n in notes) if (double.IsNaN(n.End)) n.End = x.Length / (double)rate;
            return (notes, hits);
        }

        static AudioMidiEngine NewEngine(int rate, Action<ParamValues> setup)
        {
            if (double.TryParse(Environment.GetEnvironmentVariable("AM1_CONF"), CultureInfo.InvariantCulture, out var cf)) AudioMidiEngine.CalConfidence = (float)cf;
            if (double.TryParse(Environment.GetEnvironmentVariable("AM1_GATE"), CultureInfo.InvariantCulture, out var g)) MelodyNotes.GateDb = (float)g;
            if (double.TryParse(Environment.GetEnvironmentVariable("AM1_RISE"), CultureInfo.InvariantCulture, out var r)) MelodyNotes.RiseRatio = (float)r;
            var e = new AudioMidiEngine(Values(setup));
            e.SetSampleRate(rate);
            return e;
        }

        /// <summary>One to one, nearest in time first: a note is right when its onset is within <paramref name="tol"/> of a reference's and its pitch within 50 cents of it.</summary>
        static Acc Match(List<Note> notes, List<Truth> refs, double seconds, double tol = 0.05)
        {
            var a = new Acc { Ref = refs.Count, Out = notes.Count, Seconds = seconds };
            var used = new bool[notes.Count];
            foreach (var r in refs.OrderBy(r => r.Start))
            {
                int best = -1; double bd = double.MaxValue;
                for (int i = 0; i < notes.Count; i++)
                {
                    if (used[i] || Math.Abs(notes[i].Pitch - r.Midi) > 0.5) continue;
                    double d = Math.Abs(notes[i].Start - r.Start);
                    if (d <= tol && d < bd) { bd = d; best = i; }
                }
                if (best >= 0)
                {
                    used[best] = true; a.Hit++; a.Latency.Add(notes[best].Start - r.Start);
                    if (notes[best].Pitch == (int)Math.Round(r.Midi)) a.HitExact++;
                }
            }
            foreach (var r in refs)
                for (int i = 0; i < notes.Count; i++)
                    if (!used[i] && Math.Abs(notes[i].Start - r.Start) <= tol && Math.Abs(notes[i].Pitch - r.Midi) > 0.5 && Math.Abs(((notes[i].Pitch - r.Midi) % 12 + 12) % 12) is < 0.5 or > 11.5) { used[i] = true; a.Octave++; break; }
            return a;
        }

        // ---------------------------------------------------------------------------------------------------------------- GuitarSet

        /// <summary>The note annotation of each string, by the string's index (0 = low E), from a JAMS file.</summary>
        static List<Truth>[] JamsNotes(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var strings = new List<Truth>[6];
            for (int i = 0; i < 6; i++) strings[i] = new List<Truth>();
            foreach (var ann in doc.RootElement.GetProperty("annotations").EnumerateArray())
            {
                if (ann.GetProperty("namespace").GetString() != "note_midi") continue;
                int idx = int.Parse(ann.GetProperty("annotation_metadata").GetProperty("data_source").GetString(), CultureInfo.InvariantCulture);
                foreach (var d in ann.GetProperty("data").EnumerateArray())
                {
                    double t = d.GetProperty("time").GetDouble(), dur = d.GetProperty("duration").GetDouble(), v = d.GetProperty("value").GetDouble();
                    strings[idx].Add(new Truth { Start = t, End = t + dur, Midi = v });
                }
            }
            return strings;
        }

        sealed class Excerpt { public string Title, Mode; public double From, To; public List<(double S, double E, double M)> Truth = new(); public List<(double S, double E, double M)> Out = new(); public List<(double T, int Cls)> TruthHits = new(), OutHits = new(); }

        public static void Guitar(string dir, int every, int offset, StringBuilder md, string plotsDir, string players = "")
        {
            string audio = Directory.Exists(Path.Combine(dir, "audio")) ? Path.Combine(dir, "audio") : dir, ann = Directory.Exists(Path.Combine(dir, "annotation")) ? Path.Combine(dir, "annotation") : dir;
            var files = Directory.GetFiles(audio, "*_hex_cln.wav", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (players.Length > 0) files = files.Where(f => players.Split(',').Contains(Path.GetFileName(f).Substring(0, 2))).ToList();
            if (every > 1) files = files.Where((f, k) => k % every == offset).ToList();
            Console.WriteLine($"GuitarSet: {files.Count} recordings");
            var cfgs = new (string Name, int Response)[] { ("Guitar range, Fast", 0), ("Guitar range, Stable", 1) };
            var total = new Acc[cfgs.Length]; var wide = new Acc[cfgs.Length]; var perStr = new Acc[cfgs.Length][];
            for (int c = 0; c < cfgs.Length; c++) { total[c] = new Acc(); wide[c] = new Acc(); perStr[c] = Enumerable.Range(0, 6).Select(_ => new Acc()).ToArray(); }
            var excerpts = new List<Excerpt>();
            double seconds = 0; int lines = 0, done = 0;
            var sync = new object();
            var picks = new HashSet<int>(files.Count == 0 ? new int[0] : new[] { 0, files.Count / 2, files.Count - 1 });
            Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, fi =>
            {
                string name = Path.GetFileName(files[fi]).Replace("_hex_cln.wav", "");
                string jams = Directory.GetFiles(ann, name + ".jams", SearchOption.AllDirectories).FirstOrDefault();
                if (jams == null) return;
                var truth = JamsNotes(jams);
                var (ch, rate) = Wav.ReadChannels(files[fi]);
                var local = new Acc[cfgs.Length]; var localW = new Acc[cfgs.Length]; var localS = new Acc[cfgs.Length][];
                for (int c = 0; c < cfgs.Length; c++) { local[c] = new Acc(); localW[c] = new Acc(); localS[c] = Enumerable.Range(0, 6).Select(_ => new Acc()).ToArray(); }
                Excerpt best = null; int bestCount = -1;
                for (int s = 0; s < 6 && s < ch.Length; s++)
                {
                    double sec = ch[s].Length / (double)rate;
                    for (int c = 0; c < cfgs.Length; c++)
                    {
                        int resp = cfgs[c].Response;
                        var e = NewEngine(rate, v => { v.Set(P.Mode, 0); v.Set(P.Range, 0); v.Set(P.Response, resp); });
                        var (notes, _) = Run(ch[s], rate, e);
                        var a = Match(notes, truth[s], sec);
                        local[c].Add(a); localS[c][s].Add(a);
                        localW[c].Add(Match(notes, truth[s], sec, 0.1));
                        if (c == 0 && picks.Contains(fi))
                        {
                            // The excerpt of this recording: the 8 s window of the string with the most annotated notes.
                            double from = Math.Min(Math.Max(0, sec - 8), 4);
                            int count = truth[s].Count(t => t.Start >= from && t.Start < from + 8);
                            if (count > bestCount)
                            {
                                bestCount = count;
                                best = new Excerpt { Title = $"GuitarSet {name}, string {s + 1} ({new[] { "low E", "A", "D", "G", "B", "high E" }[s]}), Melody, Guitar range, Fast", Mode = "melody", From = from, To = from + 8 };
                                foreach (var t in truth[s]) if (t.End > from && t.Start < from + 8) best.Truth.Add((t.Start, t.End, t.Midi));
                                foreach (var n in notes) if (n.End > from && n.Start < from + 8) best.Out.Add((n.Start, n.End, n.Pitch));
                            }
                        }
                    }
                }
                lock (sync)
                {
                    for (int c = 0; c < cfgs.Length; c++) { total[c].Add(local[c]); wide[c].Add(localW[c]); for (int s = 0; s < 6; s++) perStr[c][s].Add(localS[c][s]); }
                    seconds += ch.Sum(l => l.Length / (double)rate); lines += ch.Length;
                    if (best != null) excerpts.Add(best);
                    if (++done % 10 == 0) Console.WriteLine($"  {done}/{files.Count}");
                }
            });
            md.AppendLine("## Neo Audio to MIDI, Melody mode: GuitarSet single strings (hexaphonic, secondary)");
            md.AppendLine();
            md.AppendLine($"{files.Count} recordings ({lines} string lines, {seconds / 60:0} minutes) of GuitarSet's hexaphonic pickup (debleeded, 6 channels, one string each), against the JAMS note annotation of each string. The engine is the machine's (Melody, Guitar range, default settings, 256-sample blocks); a note is right when its onset is within 50 ms of an annotated note's and its pitch within 50 cents of it. Latency is the note on's time less the annotated onset (the annotation's own accuracy is a few tens of milliseconds). Octave errors are notes at the right time an octave out; extra notes are notes that match nothing.");
            md.AppendLine();
            md.AppendLine("| configuration | notes ref | notes out | precision | recall | F-measure | F exact semitone | F at 100 ms | latency median | p95 | octave errors | extra notes / min |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            for (int c = 0; c < cfgs.Length; c++)
            {
                var a = total[c];
                md.AppendLine($"| {cfgs[c].Name} | {a.Ref} | {a.Out} | {Pc(a.P)} | {Pc(a.R)} | **{Pc(a.F)}** | {Pc(a.FExact)} | {Pc(wide[c].F)} | {Ms(Median(a.Latency))} | {Ms(Pctl(a.Latency, 0.95))} | {a.Octave} | {a.ChatterPerMinute:F1} |");
            }
            md.AppendLine();
            md.AppendLine("F-measure by string (Fast / Stable): " + string.Join("; ", Enumerable.Range(0, 6).Select(s => $"{new[] { "low E", "A", "D", "G", "B", "high E" }[s]} {Pc(perStr[0][s].F)} / {Pc(perStr[1][s].F)}")));
            md.AppendLine();
            WriteExcerpts(plotsDir, excerpts.OrderBy(x => x.Title, StringComparer.Ordinal).Take(3).ToList(), "gs");
        }


        // ---------------------------------------------------------------------------------------------------------------- GuitarSet, a single-note line

        /// <summary>Every string's notes merged into one line; where notes overlap the earlier one's end is trimmed to the next onset.</summary>
        static List<Truth> MergedNotes(string jams)
        {
            var all = JamsNotes(jams).SelectMany(l => l).OrderBy(t => t.Start).ToList();
            for (int i = 0; i + 1 < all.Count; i++) if (all[i].End > all[i + 1].Start) all[i].End = all[i + 1].Start;
            return all;
        }

        /// <summary>The headline: the solo takes (a single-note line) as mono audio (mic, pickup mix) against the merged notes of all strings, Fast and Stable.</summary>
        public static void GuitarMono(string dir, string players, StringBuilder md, string plotsDir)
        {
            string audio = Path.Combine(dir, "audio"), ann = Path.Combine(dir, "annotation");
            var keep = new HashSet<string>(players.Split(',', StringSplitOptions.RemoveEmptyEntries));
            var takes = Directory.GetFiles(audio, "*_solo_mic.wav").Select(f => Path.GetFileName(f).Replace("_mic.wav", "")).Where(n => keep.Count == 0 || keep.Contains(n.Substring(0, 2))).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Console.WriteLine($"GuitarSet solo takes ({(players.Length == 0 ? "all players" : "players " + players)}): {takes.Count}");
            var kinds = new[] { ("mic", "_mic.wav"), ("pickup mix", "_mix.wav") };
            var cfgs = new[] { ("Fast", 0), ("Stable", 1) };
            var acc = new Acc[kinds.Length, cfgs.Length]; var wide = new Acc[kinds.Length, cfgs.Length];
            for (int k = 0; k < kinds.Length; k++) for (int c = 0; c < cfgs.Length; c++) { acc[k, c] = new Acc(); wide[k, c] = new Acc(); }
            var excerpts = new List<Excerpt>(); var sync = new object(); int done = 0;
            Parallel.For(0, takes.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, ti =>
            {
                string name = takes[ti];
                var truth = MergedNotes(Path.Combine(ann, name + ".jams"));
                for (int k = 0; k < kinds.Length; k++)
                {
                    string f = Path.Combine(audio, name + kinds[k].Item2);
                    if (!File.Exists(f)) continue;
                    var (x, rate) = Wav.Read(f);
                    double sec = x.Length / (double)rate;
                    for (int c = 0; c < cfgs.Length; c++)
                    {
                        int resp = cfgs[c].Item2;
                        var e = NewEngine(rate, v => { v.Set(P.Mode, 0); v.Set(P.Range, 0); v.Set(P.Response, resp); });
                        var (notes, _) = Run(x, rate, e);
                        if (Environment.GetEnvironmentVariable("AM1_DUMPTAKE") == name + kinds[k].Item2 && c == 0)
                            lock (sync) foreach (var n in notes)
                            {
                                var near = truth.OrderBy(t => Math.Abs(t.Start - n.Start)).First();
                                bool ok = Math.Abs(near.Start - n.Start) <= 0.05 && Math.Abs(near.Midi - n.Pitch) <= 0.5;
                                double lv = 0; int a0 = (int)(n.Start * rate), a1 = Math.Min(x.Length, a0 + (int)(0.03 * rate)); for (int q = a0; q < a1; q++) lv = Math.Max(lv, Math.Abs(x[q]));
                                Console.WriteLine($"{(ok ? "ok " : "EXT")} t={n.Start:F3} note={n.Pitch} len={n.End - n.Start:F2} db={20 * Math.Log10(lv + 1e-9):F0} near dt={near.Start - n.Start:F3} dm={near.Midi - n.Pitch:F1}");
                            }
                        var a = Match(notes, truth, sec); var w = Match(notes, truth, sec, 0.1);
                        lock (sync)
                        {
                            acc[k, c].Add(a); wide[k, c].Add(w);
                            if (k == 0 && c == 0 && ti % Math.Max(1, takes.Count / 3) == 0 && excerpts.Count < 3)
                            {
                                double from = Math.Min(4, Math.Max(0, sec - 8));
                                var ex = new Excerpt { Title = $"GuitarSet {name}, mic, Melody, Guitar range, Fast", Mode = "melody", From = from, To = from + 8 };
                                foreach (var t in truth) if (t.End > from && t.Start < from + 8) ex.Truth.Add((t.Start, t.End, t.Midi));
                                foreach (var n in notes) if (n.End > from && n.Start < from + 8) ex.Out.Add((n.Start, n.End, n.Pitch));
                                excerpts.Add(ex);
                            }
                        }
                    }
                }
                lock (sync) if (++done % 10 == 0) Console.WriteLine($"  {done}/{takes.Count}");
            });
            md.AppendLine("## Neo Audio to MIDI, Melody mode: GuitarSet solo takes (a single-note line)");
            md.AppendLine();
            md.AppendLine($"{takes.Count} solo takes ({(players.Length == 0 ? "all players" : "players " + players)}), mono audio (the microphone, the pickup mix) against every string's notes merged into one line (an earlier note is trimmed to the next onset). The engine is the machine's (Melody, Guitar range, default settings). A note is right when its onset is within 50 ms of a truth note's and its pitch within 50 cents; F exact is the same with the semitone exact. Extra notes are notes that match nothing.");
            md.AppendLine();
            md.AppendLine("| audio | response | notes ref | notes out | precision | recall | F-measure | F exact semitone | F at 100 ms | latency median | p95 | octave errors | extra notes / min |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            for (int k = 0; k < kinds.Length; k++)
                for (int c = 0; c < cfgs.Length; c++)
                {
                    var a = acc[k, c];
                    md.AppendLine($"| {kinds[k].Item1} | {cfgs[c].Item1} | {a.Ref} | {a.Out} | {Pc(a.P)} | {Pc(a.R)} | **{Pc(a.F)}** | {Pc(a.FExact)} | {Pc(wide[k, c].F)} | {Ms(Median(a.Latency))} | {Ms(Pctl(a.Latency, 0.95))} | {a.Octave} | {a.ChatterPerMinute:F1} |");
                }
            md.AppendLine();
            WriteExcerpts(plotsDir, excerpts.OrderBy(x => x.Title, StringComparer.Ordinal).ToList(), "gs");
        }

        // ---------------------------------------------------------------------------------------------------------------- IDMT-SMT-Drums

        static readonly string[] ClassNames = { "kick", "snare", "hat" };
        static readonly string[] Tags = { "KD", "SD", "HH" };

        static List<(double T, int Cls)> DrumTruth(string xml)
        {
            var list = new List<(double, int)>();
            foreach (var ev in System.Xml.Linq.XDocument.Load(xml).Descendants("event"))
            {
                int c = Array.IndexOf(Tags, (string)ev.Element("instrument"));
                if (c < 0) continue;
                list.Add((double.Parse((string)ev.Element("onsetSec"), CultureInfo.InvariantCulture), c));
            }
            return list.OrderBy(t => t.Item1).ToList();
        }

        /// <summary>The note a class plays (the machine's defaults).</summary>
        static readonly int[] Notes = { 36, 38, 42 };

        sealed class DrumAcc
        {
            public int[] Ref = new int[3], Out = new int[3], Hit = new int[3];
            public readonly List<double>[] Offset = { new(), new(), new() };
            public void Add(DrumAcc o) { for (int c = 0; c < 3; c++) { Ref[c] += o.Ref[c]; Out[c] += o.Out[c]; Hit[c] += o.Hit[c]; Offset[c].AddRange(o.Offset[c]); } }
            public double P(int c) => Out[c] == 0 ? 0 : (double)Hit[c] / Out[c];
            public double R(int c) => Ref[c] == 0 ? 0 : (double)Hit[c] / Ref[c];
            public double F(int c) => P(c) + R(c) == 0 ? 0 : 2 * P(c) * R(c) / (P(c) + R(c));
        }

        static DrumAcc ScoreDrums(List<(double T, int Cls)> truth, List<(double T, int Note, int Vel)> hits, double tol = 0.05)
        {
            var a = new DrumAcc();
            var used = new bool[hits.Count];
            foreach (var (t, c) in truth)
            {
                a.Ref[c]++;
                int best = -1; double bd = double.MaxValue;
                for (int i = 0; i < hits.Count; i++)
                {
                    if (used[i] || hits[i].Note != Notes[c]) continue;
                    double d = Math.Abs(hits[i].T - t);
                    if (d <= tol && d < bd) { bd = d; best = i; }
                }
                if (best >= 0) { used[best] = true; a.Hit[c]++; a.Offset[c].Add(hits[best].T - t); }
            }
            foreach (var h in hits) { int c = Array.IndexOf(Notes, h.Note); if (c >= 0) a.Out[c]++; }
            return a;
        }

        /// <summary>Calibrates an engine's three drums from the single-drum training recordings of the same kit: the next five hits of each.</summary>
        static void Calibrate(AudioMidiEngine e, string audioDir, string stem)
        {
            for (int c = 0; c < 3; c++)
            {
                string f = Path.Combine(audioDir, $"{stem}#{Tags[c]}#train.wav");
                if (!File.Exists(f)) return;
                var (x, rate) = Wav.Read(f);
                e.StartLearning(c);
                var buf = new float[256];
                for (int pos = 0; pos < x.Length && e.Learning >= 0; pos += 256)
                {
                    int n = Math.Min(256, x.Length - pos);
                    Array.Copy(x, pos, buf, 0, n);
                    e.Process(buf, n);
                }
                // Let the last hits' slices complete.
                Array.Clear(buf, 0, buf.Length);
                for (int k = 0; k < 8 && e.Learning >= 0; k++) e.Process(buf, 256);
                e.StopLearning();
                var f2 = new float[VoiceFeatures.Dimension];
                while (e.TakeLearned(out int cls, f2)) e.Classifier.Add(cls, f2);
            }
            e.Classifier.Train();
        }

        static readonly object bandsLock = new();
        static string bandsCsv => Environment.GetEnvironmentVariable("AM1_BANDS");

        public static void Drums(string dir, StringBuilder md, string plotsDir)
        {
            string audio = Directory.Exists(Path.Combine(dir, "audio")) ? Path.Combine(dir, "audio") : dir;
            string ann = Directory.Exists(Path.Combine(dir, "annotation_xml")) ? Path.Combine(dir, "annotation_xml") : dir;
            var mixes = Directory.GetFiles(audio, "*#MIX.wav", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
            Console.WriteLine($"IDMT-SMT-Drums: {mixes.Count} mixes");
            string[] kits = { "RealDrum", "TechnoDrum", "WaveDrum" };
            var plain = new Dictionary<string, DrumAcc>(); var calib = new Dictionary<string, DrumAcc>();
            foreach (var k in kits.Append("all").Append("odd")) { plain[k] = new DrumAcc(); calib[k] = new DrumAcc(); }
            var excerpts = new List<Excerpt>();
            double seconds = 0; int done = 0, withTraining = 0;
            var sync = new object();
            Parallel.For(0, mixes.Count, new ParallelOptions { MaxDegreeOfParallelism = bandsCsv != null ? 1 : Math.Max(1, Environment.ProcessorCount) }, fi =>
            {
                string f = mixes[fi];
                string stem = Path.GetFileName(f).Replace("#MIX.wav", "");
                string xml = Directory.GetFiles(ann, stem + "#MIX.xml", SearchOption.AllDirectories).FirstOrDefault();
                if (xml == null) return;
                var truth = DrumTruth(xml);
                var (x, rate) = Wav.Read(f);
                string kit = kits.FirstOrDefault(k => stem.StartsWith(k)) ?? "WaveDrum";
                var e0 = NewEngine(rate, v => { v.Set(P.Mode, 1); });
                var (_, hits0) = Run(x, rate, e0);
                if (bandsCsv != null)
                {
                    var rows = new List<string>(); var times = new List<double>();
                    AudioMidiEngine.ProbeHandler hook = (in OnsetEvent o, HitBands h) =>
                    {
                        double t = o.Sample / (double)rate; int lab = 0; times.Add(t);
                        foreach (var (tt, cc) in truth) if (Math.Abs(tt - t) <= 0.05) lab |= 1 << cc;
                        rows.Add(string.Join(",", new[] { kit, lab.ToString(), h.S0.ToString("F3"), h.S1.ToString("F3"), h.S2.ToString("F3"), h.S3.ToString("F3"), h.R0.ToString("F1"), h.R1.ToString("F1"), h.R2.ToString("F1"), h.R3.ToString("F1"), h.Rise.ToString("F1"), h.LevelDb.ToString("F1"), stem }));
                    };
                    lock (bandsLock)
                    {
                        AudioMidiEngine.Probe = hook;
                        var ep = NewEngine(rate, v => { v.Set(P.Mode, 1); });
                        Run(x, rate, ep);
                        AudioMidiEngine.Probe = null;
                        File.AppendAllLines(bandsCsv, rows);
                        foreach (var (tt, cc) in truth)
                            File.AppendAllText(bandsCsv + ".truth", $"{kit},{cc},{(times.Any(q => Math.Abs(q - tt) <= 0.05) ? 1 : 0)}\n");
                    }
                }
                var a0 = ScoreDrums(truth, hits0);
                DrumAcc a1 = null; List<(double T, int Note, int Vel)> hits1 = null;
                var e1 = NewEngine(rate, v => { v.Set(P.Mode, 1); });
                Calibrate(e1, Path.GetDirectoryName(f), stem);
                if (e1.Classifier.ClassesTrained >= 3)
                {
                    e1.Reset();
                    (_, hits1) = Run(x, rate, e1);
                    a1 = ScoreDrums(truth, hits1);
                }
                lock (sync)
                {
                    plain[kit].Add(a0); plain["all"].Add(a0); bool odd = int.Parse(stem.Substring(stem.LastIndexOf('_') + 1)) % 2 == 1; if (odd) plain["odd"].Add(a0);
                    if (a1 != null) { calib[kit].Add(a1); calib["all"].Add(a1); if (odd) calib["odd"].Add(a1); withTraining++; }
                    seconds += x.Length / (double)rate;
                    // An excerpt of the first recording of each kit.
                    if (stem.EndsWith("_00") || stem.EndsWith("_01") && !excerpts.Any(q => q.Title.Contains(kit)))
                    {
                        if (!excerpts.Any(q => q.Title.Contains(kit)))
                        {
                            var ex = new Excerpt { Title = $"IDMT-SMT-Drums {stem}, Drums mode, no calibration", Mode = "drums", From = 1, To = 9 };
                            foreach (var (t, c) in truth) if (t >= ex.From && t < ex.To) ex.TruthHits.Add((t, c));
                            foreach (var h in hits0) { int c = Array.IndexOf(Notes, h.Note); if (c >= 0 && h.T >= ex.From && h.T < ex.To) ex.OutHits.Add((h.T, c)); }
                            excerpts.Add(ex);
                        }
                    }
                    if (++done % 20 == 0) Console.WriteLine($"  {done}/{mixes.Count}");
                }
            });
            md.AppendLine("## Neo Audio to MIDI, Drums mode: IDMT-SMT-Drums");
            md.AppendLine();
            md.AppendLine($"{mixes.Count} drum mixes ({seconds / 60:0} minutes; real, techno and wave kits) against the onset and class annotations (KD kick, SD snare, HH hat; the dataset has no other classes). The engine is the machine's (Drums mode, default notes 36, 38 and 42, default settings). A hit is right when its class matches and its onset is within 50 ms. Calibrated: five hits of each drum, taken from that kit's single-drum training recordings (`#KD#train`, `#SD#train`, `#HH#train`) by the machine's calibration pass ({withTraining} mixes had all three). The rule thresholds were fitted on the even-numbered takes (and, in an earlier pass, on all of them); the odd-numbered takes are the held-out rows. The dataset is CC BY-NC-ND 4.0: used here for measurement only.");
            md.AppendLine();
            md.AppendLine("| set | drum | reference | precision | recall | F-measure | offset median | offset p95 |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
            foreach (var (label, dict) in new[] { ("no calibration", plain), ("calibrated", calib) })
                foreach (var k in kits.Append("all").Append("odd"))
                    for (int c = 0; c < 3; c++)
                    {
                        var a = dict[k];
                        md.AppendLine($"| {label}, {(k == "all" ? "all kits" : k == "odd" ? "HELD-OUT: odd-numbered takes, all kits" : k)} | {ClassNames[c]} | {a.Ref[c]} | {Pc(a.P(c))} | {Pc(a.R(c))} | **{Pc(a.F(c))}** | {Ms(Median(a.Offset[c]))} | {Ms(Pctl(a.Offset[c], 0.95))} |");
                    }
            md.AppendLine();
            WriteExcerpts(plotsDir, excerpts.OrderBy(x => x.Title, StringComparer.Ordinal).Take(3).ToList(), "idmt");
        }

        // ---------------------------------------------------------------------------------------------------------------- MDB-stem-synth

        static List<Truth> NotesOf(List<double> t, List<double> f0)
        {
            var notes = new List<Truth>();
            int n = f0.Count;
            double Midi(int k) => 69 + 12 * Math.Log2(f0[k] / 440);
            int i = 0;
            while (i < n)
            {
                if (f0[i] <= 0) { i++; continue; }
                int start = i, end = i;
                var pitches = new List<double> { Midi(i) };
                int away = 0;
                double median = pitches[0];
                for (int k = i + 1; k < n && f0[k] > 0; k++)
                {
                    double m = Midi(k);
                    if (Math.Abs(m - median) > 0.8) { if (++away >= 3) break; continue; }
                    away = 0; pitches.Add(m); end = k;
                    var sorted = pitches.OrderBy(v => v).ToList();
                    median = sorted[sorted.Count / 2];
                }
                if (t[end] - t[start] >= 0.05) notes.Add(new Truth { Start = t[start], End = t[end], Midi = median });
                i = end + 1;
            }
            return notes;
        }

        public static void Mdb(string dir, int limit, StringBuilder md)
        {
            string audio = Path.Combine(dir, "audio_stems"), ann = Path.Combine(dir, "annotation_stems");
            if (!Directory.Exists(audio))
            {
                var nested = Directory.GetDirectories(dir).FirstOrDefault(d => Directory.Exists(Path.Combine(d, "audio_stems")));
                if (nested != null) { audio = Path.Combine(nested, "audio_stems"); ann = Path.Combine(nested, "annotation_stems"); }
            }
            var files = Directory.GetFiles(audio, "*.wav").Where(f => !Path.GetFileName(f).StartsWith("._")).OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (limit > 0 && files.Count > limit) files = Enumerable.Range(0, limit).Select(i => files[(int)((long)i * files.Count / limit)]).ToList();
            Console.WriteLine($"MDB-stem-synth (Neo Audio to MIDI): {files.Count} stems");
            var cfgs = new (string Name, int Range, int Response)[] { ("Full range, Fast", 2, 0), ("Full range, Stable", 2, 1), ("Guitar range, Fast", 0, 0), ("Bass range, Fast", 1, 0) };
            var acc = cfgs.Select(_ => new Acc()).ToArray();
            var voiced = new long[cfgs.Length]; var voicedRight = new long[cfgs.Length]; var noteRight = new long[cfgs.Length]; var refVoiced = new long[cfgs.Length];
            var perStem = cfgs.Select(_ => new List<double>()).ToArray();
            var sync = new object(); int done = 0;
            Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, fi =>
            {
                var (x, rate) = Wav.Read(files[fi]);
                var t = new List<double>(); var f0 = new List<double>();
                foreach (var line in File.ReadLines(Path.Combine(ann, Path.GetFileName(files[fi]).Replace(".wav", ".csv"))))
                {
                    var p = line.Split(',');
                    if (p.Length < 2) continue;
                    t.Add(double.Parse(p[0], CultureInfo.InvariantCulture)); f0.Add(double.Parse(p[1], CultureInfo.InvariantCulture));
                }
                var refs = NotesOf(t, f0);
                for (int c = 0; c < cfgs.Length; c++)
                {
                    var (cr, cresp) = (cfgs[c].Range, cfgs[c].Response);
                    var e = NewEngine(rate, v => { v.Set(P.Mode, 0); v.Set(P.Range, cr); v.Set(P.Response, cresp); });
                    // Frame by frame: the engine's pitch (SungMidi) and its sounding note at each annotated frame.
                    var notes = new List<Note>(); var open = new Dictionary<int, Note>();
                    var buf = new float[256];
                    int ti = 0; long vo = 0, vr = 0, nr = 0, rv = 0;
                    for (int pos = 0; pos < x.Length; pos += 256)
                    {
                        int n = Math.Min(256, x.Length - pos);
                        Array.Copy(x, pos, buf, 0, n);
                        int cnt = e.Process(buf, n);
                        for (int i = 0; i < cnt; i++)
                        {
                            var m = e.Events[i]; double tm = (pos + m.Offset) / (double)rate;
                            if (m.Stream != MidiStream.Pitch) continue;
                            if (m.Kind == MidiKind.NoteOn) { var nn = new Note { Start = tm, End = double.NaN, Pitch = m.Data1 }; notes.Add(nn); open[m.Data1] = nn; }
                            else if (m.Kind == MidiKind.NoteOff && open.TryGetValue(m.Data1, out var on)) { on.End = tm; open.Remove(m.Data1); }
                        }
                        double now = (pos + n) / (double)rate;
                        while (ti < t.Count && t[ti] <= now)
                        {
                            if (f0[ti] > 0)
                            {
                                rv++;
                                double rm = 69 + 12 * Math.Log2(f0[ti] / 440);
                                if (e.Voiced) { vo++; if (Math.Abs(e.SungMidi - rm) <= 0.5) vr++; }
                                if (e.Melody.Sounding && Math.Abs(e.Melody.CurrentNote - rm) <= 0.5) nr++;
                            }
                            ti++;
                        }
                    }
                    foreach (var nn in notes) if (double.IsNaN(nn.End)) nn.End = x.Length / (double)rate;
                    var a = Match(notes, refs, x.Length / (double)rate);
                    lock (sync) { acc[c].Add(a); voiced[c] += vo; voicedRight[c] += vr; noteRight[c] += nr; refVoiced[c] += rv; if (a.Ref + a.Out > 0) perStem[c].Add(a.F); }
                }
                lock (sync) if (++done % 20 == 0) Console.WriteLine($"  {done}/{files.Count}");
            });
            md.AppendLine("## Neo Audio to MIDI, Melody mode: MDB-stem-synth");
            md.AppendLine();
            md.AppendLine($"{files.Count} resynthesised stems (voices and instruments, f0 every 2.9 ms) through the machine's engine in four configurations. The reference notes are cut from the annotated f0 (runs within 0.8 semitone of their median, at least 50 ms); a note is right when its onset is within 50 ms and its pitch within 50 cents. Pitch on voiced frames: of the annotated voiced frames, the share where the engine's pitch estimate (when it calls the frame voiced) is within 50 cents, and the share where the note it sounds is within 50 cents (a played note is a semitone, so this one is lower than a tracker's accuracy by the quantisation).");
            md.AppendLine();
            md.AppendLine("| configuration | notes ref | notes out | precision | recall | F-measure | per-stem median F | latency median | octave errors | pitch right on voiced frames | note right on voiced frames |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            for (int c = 0; c < cfgs.Length; c++)
            {
                var a = acc[c];
                md.AppendLine($"| {cfgs[c].Name} | {a.Ref} | {a.Out} | {Pc(a.P)} | {Pc(a.R)} | **{Pc(a.F)}** | {Pc(Median(perStem[c]))} | {Ms(Median(a.Latency))} | {a.Octave} | {Pc((double)voicedRight[c] / Math.Max(1, refVoiced[c]))} | {Pc((double)noteRight[c] / Math.Max(1, refVoiced[c]))} |");
            }
            md.AppendLine();
        }

        // ---------------------------------------------------------------------------------------------------------------- piano-roll data

        static void WriteExcerpts(string dir, List<Excerpt> list, string prefix)
        {
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            int k = 1;
            foreach (var ex in list)
            {
                var sb = new StringBuilder();
                sb.Append("{\"title\":").Append(JsonSerializer.Serialize(ex.Title)).Append(",\"mode\":\"").Append(ex.Mode).Append("\",\"from\":").Append(ex.From.ToString(CultureInfo.InvariantCulture)).Append(",\"to\":").Append(ex.To.ToString(CultureInfo.InvariantCulture));
                string Notes(List<(double S, double E, double M)> l) => "[" + string.Join(",", l.Select(n => $"[{n.S.ToString("F4", CultureInfo.InvariantCulture)},{n.E.ToString("F4", CultureInfo.InvariantCulture)},{n.M.ToString("F3", CultureInfo.InvariantCulture)}]")) + "]";
                string Hits(List<(double T, int Cls)> l) => "[" + string.Join(",", l.Select(n => $"[{n.T.ToString("F4", CultureInfo.InvariantCulture)},{n.Cls}]")) + "]";
                sb.Append(",\"truth\":").Append(Notes(ex.Truth)).Append(",\"out\":").Append(Notes(ex.Out));
                sb.Append(",\"truth_hits\":").Append(Hits(ex.TruthHits)).Append(",\"out_hits\":").Append(Hits(ex.OutHits)).Append('}');
                File.WriteAllText(Path.Combine(dir, $"{prefix}-{k++}.json"), sb.ToString());
            }
        }

        /// <summary>--am1-map FILE: which hex channel is which string (the median pitch of each against the annotation's).</summary>
        public static void Map(string wav, string jams)
        {
            var truth = JamsNotes(jams);
            var (ch, rate) = Wav.ReadChannels(wav);
            if (double.TryParse(Environment.GetEnvironmentVariable("AM1_TRACK"), CultureInfo.InvariantCulture, out var t0))
            {
                var tr = new PitchTracker(rate, PitchRange.Guitar);
                var fr = new PitchEstimate[64];
                var xs = ch[int.Parse(Environment.GetEnvironmentVariable("AM1_CH") ?? "0")];
                for (int pos = 0; pos < xs.Length; pos += 256)
                {
                    int got = tr.Process(xs, pos, Math.Min(256, xs.Length - pos), fr, 0);
                    for (int i = 0; i < got; i++) { var f = fr[i]; double tt = f.EndSample / (double)rate; if (tt >= t0 && tt < t0 + 0.5) Console.WriteLine($"{tt:F3} v={f.Voiced} conf={f.Confidence:F2} raw={f.RawF0:F1} f0={f.F0:F1} midi={f.Midi:F1} rms={20 * Math.Log10(f.Rms + 1e-9):F0}"); }
                }
                return;
            }
            for (int c = 0; c < ch.Length; c++)
            {
                var e = NewEngine(rate, v => { v.Set(P.Mode, 0); v.Set(P.Range, 0); });
                var (notes, _) = Run(ch[c], rate, e);
                var med = notes.Select(n => (double)n.Pitch).OrderBy(v => v).ToList();
                if (Environment.GetEnvironmentVariable("AM1_DUMP") == c.ToString())
                    foreach (var n in notes)
                    {
                        var near = truth[c].OrderBy(t => Math.Abs(t.Start - n.Start)).FirstOrDefault();
                        bool ok = near != null && Math.Abs(near.Start - n.Start) <= 0.05 && Math.Abs(near.Midi - n.Pitch) <= 0.5;
                        double lv = 0; int a0 = (int)(n.Start * rate), a1 = Math.Min(ch[c].Length, a0 + (int)(0.03 * rate)); for (int k = a0; k < a1; k++) lv = Math.Max(lv, Math.Abs(ch[c][k]));
                        Console.WriteLine($"{(ok ? "ok " : "EXT")} t={n.Start:F3} note={n.Pitch} len={n.End - n.Start:F2} vel={n.Vel} peakdb={20 * Math.Log10(lv + 1e-9):F0} near dt={(near == null ? 0 : near.Start - n.Start):F3} dm={(near == null ? 0 : near.Midi - n.Pitch):F1}");
                    }
                if (Environment.GetEnvironmentVariable("AM1_DUMP") == c.ToString())
                    foreach (var r in truth[c])
                    {
                        bool got = notes.Any(n => Math.Abs(n.Start - r.Start) <= 0.05 && Math.Abs(n.Pitch - r.Midi) <= 0.5);
                        if (got) continue;
                        double lv = 0; int a0 = (int)(r.Start * rate), a1 = Math.Min(ch[c].Length, a0 + (int)(0.05 * rate)); for (int k = a0; k < a1; k++) lv = Math.Max(lv, Math.Abs(ch[c][k]));
                        var nn = notes.OrderBy(n => Math.Abs(n.Start - r.Start)).FirstOrDefault();
                        Console.WriteLine($"MISS t={r.Start:F3} midi={r.Midi:F1} dur={r.End - r.Start:F2} peakdb={20 * Math.Log10(lv + 1e-9):F0} nearest out dt={(nn == null ? 0 : nn.Start - r.Start):F3} note={nn?.Pitch}");
                    }
                Console.WriteLine($"channel {c}: {notes.Count} notes, median {(med.Count > 0 ? med[med.Count / 2] : double.NaN)}; annotation string {c}: {truth[c].Count} notes, median {(truth[c].Count > 0 ? truth[c].Select(t => t.Midi).OrderBy(v => v).ElementAt(truth[c].Count / 2) : double.NaN):F1}");
            }
        }
    }
}
