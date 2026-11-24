using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NeoBuzz.Chip;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Listening pack generator for Neo Chip (issue #186, AGENTS.md §5). Explicit: writes 48 kHz
    /// 16-bit stereo WAVs of all 12 presets on a melody, an arpeggio line and a noise drum line into
    /// listening-packs/186/ (git-ignored), runs the second-difference click detector on every file,
    /// and writes listening-packs/186/README.md.
    ///
    ///   dotnet test NeoBuzz.MachineTests --filter FullyQualifiedName~NeoChipPack
    /// </summary>
    [TestFixture]
    [Explicit("Generates local listening pack in listening-packs/186/ (never committed).")]
    public class NeoChipPack
    {
        const int Rate = 48000;
        const double Bpm = 128.0;

        readonly record struct NoteEvent(double StartBeat, double DurationBeats, int Note, float Velocity, int MidiChannel, bool UseTrack4 = false);

        static string RepoRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "Machines")) && Directory.Exists(Path.Combine(d.FullName, "NeoBuzz.MachineTests")))
                    return d.FullName;
            throw new DirectoryNotFoundException("Repository root not found");
        }

        static string Slug(string name) =>
            Regex.Replace(name.ToLowerInvariant().Replace("—", "-").Replace("%", "pct"), @"[^a-z0-9]+", "-").Trim('-');

        // 1. Heroic 4-bar melody (MIDI ch 1) with accents and real note-offs.
        static NoteEvent[] MelodyLine() => new[]
        {
            new NoteEvent(0.0, 0.75, 64, 0.90f, 0),
            new NoteEvent(1.0, 0.45, 67, 0.80f, 0),
            new NoteEvent(1.5, 0.45, 71, 0.85f, 0),
            new NoteEvent(2.0, 1.50, 72, 0.95f, 0),
            new NoteEvent(3.5, 0.45, 71, 0.75f, 0),
            new NoteEvent(4.0, 0.75, 69, 0.90f, 0),
            new NoteEvent(5.0, 0.45, 71, 0.80f, 0),
            new NoteEvent(5.5, 0.45, 72, 0.85f, 0),
            new NoteEvent(6.0, 1.50, 76, 0.95f, 0),
            new NoteEvent(7.5, 0.45, 74, 0.80f, 0),
            new NoteEvent(8.0, 0.75, 72, 0.90f, 0),
            new NoteEvent(9.0, 0.45, 69, 0.80f, 0),
            new NoteEvent(9.5, 0.45, 67, 0.85f, 0),
            new NoteEvent(10.0, 1.50, 64, 0.92f, 0),
            new NoteEvent(12.0, 0.45, 67, 0.82f, 0),
            new NoteEvent(12.5, 0.45, 71, 0.86f, 0),
            new NoteEvent(13.0, 0.45, 74, 0.90f, 0),
            new NoteEvent(13.5, 2.00, 76, 0.95f, 0),
        };

        // 2. 4-bar chord/arpeggio progression (Am - F - C - G, 3 voices on MIDI ch 1).
        static NoteEvent[] ArpeggioLine() => new[]
        {
            // Bar 1: Am (A3, C4, E4)
            new NoteEvent(0.0, 3.6, 57, 0.85f, 0),
            new NoteEvent(0.0, 3.6, 60, 0.80f, 0),
            new NoteEvent(0.0, 3.6, 64, 0.82f, 0),
            // Bar 2: F (F3, A3, C4)
            new NoteEvent(4.0, 3.6, 53, 0.85f, 0),
            new NoteEvent(4.0, 3.6, 57, 0.80f, 0),
            new NoteEvent(4.0, 3.6, 60, 0.82f, 0),
            // Bar 3: C (C4, E4, G4)
            new NoteEvent(8.0, 3.6, 60, 0.85f, 0),
            new NoteEvent(8.0, 3.6, 64, 0.80f, 0),
            new NoteEvent(8.0, 3.6, 67, 0.82f, 0),
            // Bar 4: G (G3, B3, D4)
            new NoteEvent(12.0, 3.6, 55, 0.85f, 0),
            new NoteEvent(12.0, 3.6, 59, 0.80f, 0),
            new NoteEvent(12.0, 3.6, 62, 0.82f, 0),
        };

        // 3. 4-bar noise drum groove on Track 4 (high hat hits on 8ths + lower snare accents on 2 & 4).
        static NoteEvent[] NoiseDrumLine()
        {
            var evs = new List<NoteEvent>();
            for (int bar = 0; bar < 4; bar++)
            {
                double b0 = bar * 4.0;
                for (int eighth = 0; eighth < 8; eighth++)
                {
                    double beat = b0 + eighth * 0.5;
                    bool snare = eighth == 2 || eighth == 6;
                    int note = snare ? 55 : (eighth % 2 == 0 ? 79 : 74);
                    float vel = snare ? 0.95f : (eighth % 2 == 0 ? 0.78f : 0.55f);
                    double dur = snare ? 0.35 : 0.20;
                    evs.Add(new NoteEvent(beat, dur, note, vel, 9, UseTrack4: true));
                }
            }
            return evs.ToArray();
        }

        static (float[] L, float[] R) RenderLine(ParamValues v, NoteEvent[] events, double totalBeats)
        {
            var e = new ChipEngine(v);
            e.SetSampleRate(Rate);
            e.Tempo = Bpm;

            double secPerBeat = 60.0 / Bpm;
            int totalSamples = (int)Math.Ceiling((totalBeats + 2.0) * secPerBeat * Rate);
            var outL = new float[totalSamples];
            var outR = new float[totalSamples];
            var bl = new float[ChipEngine.MaxBlock];
            var br = new float[ChipEngine.MaxBlock];

            var scheduled = new List<(int Sample, bool On, int Key, int Note, float Vel, int Ch, bool Track4)>();
            for (int i = 0; i < events.Length; i++)
            {
                var ev = events[i];
                int sOn = (int)Math.Round(ev.StartBeat * secPerBeat * Rate);
                int sOff = (int)Math.Round((ev.StartBeat + ev.DurationBeats) * secPerBeat * Rate);
                int key = ev.UseTrack4 ? 3 : 1000 + ev.Note;
                scheduled.Add((sOn, true, key, ev.Note, ev.Velocity, ev.MidiChannel, ev.UseTrack4));
                scheduled.Add((sOff, false, key, ev.Note, 0f, ev.MidiChannel, ev.UseTrack4));
            }
            scheduled.Sort((a, b) => a.Sample != b.Sample ? a.Sample.CompareTo(b.Sample) : (a.On == b.On ? 0 : (a.On ? 1 : -1)));

            int evIdx = 0;
            for (int pos = 0; pos < totalSamples; pos += ChipEngine.MaxBlock)
            {
                int n = Math.Min(ChipEngine.MaxBlock, totalSamples - pos);
                while (evIdx < scheduled.Count && scheduled[evIdx].Sample < pos + n)
                {
                    var s = scheduled[evIdx++];
                    if (s.Track4)
                    {
                        if (s.On) e.NoteOn(3, s.Note, s.Vel);
                        else e.NoteOff(3);
                    }
                    else
                    {
                        if (s.On) e.NoteOn(s.Key, s.Note, s.Vel, s.Ch);
                        else e.NoteOff(s.Key, s.Note, s.Ch);
                    }
                }
                e.Process(bl, br, n);
                Array.Copy(bl, 0, outL, pos, n);
                Array.Copy(br, 0, outR, pos, n);
            }
            return (outL, outR);
        }

        /// <summary>
        /// Frame-peak-normalized second-difference click detector against its local mean (AGENTS.md §5).
        /// On periodic square/pulse waves, every half-cycle edge has identical second-difference peak;
        /// comparing each 128-sample frame's max second difference against the mean of its 14 neighboring
        /// frames (excluding +-1 frame) isolates genuine clicks, dropouts and pops without false-triggering
        /// on normal pulse edges.
        /// </summary>
        internal static (int Count, List<double> TimesSec) DetectClicks(float[] left, float[] right, int rate = Rate)
        {
            int n = left.Length;
            const int Frame = 128;
            int frames = n / Frame;
            var fmax = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                int start = Math.Max(1, f * Frame);
                int end = Math.Min(n - 1, (f + 1) * Frame);
                double m = 0;
                for (int i = start; i < end; i++)
                {
                    double dl = Math.Abs(left[i + 1] - 2.0 * left[i] + left[i - 1]);
                    double dr = Math.Abs(right[i + 1] - 2.0 * right[i] + right[i - 1]);
                    double d = Math.Max(dl, dr);
                    if (d > m) m = d;
                }
                fmax[f] = m;
            }

            const int Neigh = 8;
            int refractoryFrames = Math.Max(1, (int)(0.020 * rate / Frame));
            int lastClick = -refractoryFrames;
            var times = new List<double>();

            for (int f = Neigh; f < frames - Neigh; f++)
            {
                double sum = 0;
                int count = 0;
                for (int k = f - Neigh; k <= f + Neigh; k++)
                {
                    if (Math.Abs(k - f) <= 1) continue;
                    sum += fmax[k];
                    count++;
                }
                double localMean = sum / count;
                if (localMean < 0.02) continue;
                if (fmax[f] > 4.5 * localMean && fmax[f] > 0.15 && f - lastClick >= refractoryFrames)
                {
                    times.Add((f * Frame) / (double)rate);
                    lastClick = f;
                }
            }
            return (times.Count, times);
        }

        static void WriteWav16(string path, float[] l, float[] r, int rate = Rate)
        {
            int n = l.Length;
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);
            int dataBytes = n * 4; // stereo 16-bit
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataBytes);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);      // PCM
            bw.Write((short)2);      // stereo
            bw.Write(rate);
            bw.Write(rate * 4);      // byte rate
            bw.Write((short)4);      // block align
            bw.Write((short)16);     // bits per sample
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataBytes);
            for (int i = 0; i < n; i++)
            {
                short sl = (short)Math.Clamp((int)Math.Round(l[i] * 32767.0), -32768, 32767);
                short sr = (short)Math.Clamp((int)Math.Round(r[i] * 32767.0), -32768, 32767);
                bw.Write(sl);
                bw.Write(sr);
            }
        }

        [Test]
        public void BuildListeningPack186()
        {
            string root = RepoRoot();
            string outDir = Path.Combine(root, "listening-packs", "186");
            Directory.CreateDirectory(outDir);

            string prsPath = Path.Combine(root, "Machines", "NeoChip", "Neo Chip.prs.xml");
            var doc = XDocument.Load(prsPath);
            var items = doc.Root!.Elements("Item").ToList();

            var lines = new (string Name, NoteEvent[] Events)[]
            {
                ("melody", MelodyLine()),
                ("arpeggio", ArpeggioLine()),
                ("drums", NoiseDrumLine()),
            };

            var readme = new StringBuilder();
            readme.AppendLine("# Neo Chip — Listening Pack (Issue #186)");
            readme.AppendLine();
            readme.AppendLine("Rendered by `NeoBuzz.MachineTests/NeoChipPack.cs` at 48 kHz, 16-bit stereo WAV, 128 BPM (git-ignored, never committed).");
            readme.AppendLine();
            readme.AppendLine("## Sources (written in code)");
            readme.AppendLine("- `melody`: 4-bar melodic phrase on MIDI channel 1 with dynamic accents and real note-offs.");
            readme.AppendLine("- `arpeggio`: 4-bar 3-note chord progression (Am – F – C – G) on MIDI channel 1.");
            readme.AppendLine("- `drums`: 4-bar noise percussion groove on Track 4 (8th-note hats and beats 2 & 4 snare accents).");
            readme.AppendLine();
            readme.AppendLine("## Click detector");
            readme.AppendLine("Frame-peak second-difference detector: each 128-sample frame's max `d2[n] = max(|L[n+1]-2L[n]+L[n-1]|, |R[n+1]-2R[n]+R[n-1]|)` compared against the local mean of its 14 neighbouring frames (±8 excluding ±1), threshold `4.5x local mean` and `> 0.15`, 20 ms refractory window.");
            readme.AppendLine();
            readme.AppendLine("| # | Preset | Line | File | RMS (dBFS) | Peak (dBFS) | Clicks | Notes |");
            readme.AppendLine("|---|---|---|---|---:|---:|---:|---|");

            int totalClicks = 0;
            int idx = 1;
            foreach (var item in items)
            {
                string name = (string)item.Attribute("Key")!;
                var v = new ParamValues(Params.Table);
                foreach (var p in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter"))
                    v.Set((int)p.Attribute("Index")!, (int)p.Attribute("Value")!);

                foreach (var (lineName, evs) in lines)
                {
                    var (l, r) = RenderLine(v, evs, 16.0);
                    string file = $"{idx:00}-{Slug(name)}__{lineName}.wav";
                    WriteWav16(Path.Combine(outDir, file), l, r);

                    double rms = Math.Sqrt(0.5 * (Measure.Rms(l, 0, l.Length) * Measure.Rms(l, 0, l.Length) + Measure.Rms(r, 0, r.Length) * Measure.Rms(r, 0, r.Length)));
                    double peak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
                    var (clicks, times) = DetectClicks(l, r);
                    totalClicks += clicks;
                    string note = clicks == 0 ? "clean" : $"clicks at {string.Join(", ", times.Take(5).Select(t => $"{t:F2}s"))}";
                    readme.AppendLine($"| {idx:00} | {name} | {lineName} | `{file}` | {Measure.Db(rms):F1} | {Measure.Db(peak):F1} | {clicks} | {note} |");
                }
                idx++;
            }

            readme.AppendLine();
            readme.AppendLine($"**Total files:** {items.Count * lines.Length}. **Total detected clicks:** {totalClicks}.");
            File.WriteAllText(Path.Combine(outDir, "README.md"), readme.ToString(), new UTF8Encoding(false));
            TestContext.Out.WriteLine($"Wrote {items.Count * lines.Length} WAVs and README.md to {outDir} (total clicks: {totalClicks})");
            Assert.That(totalClicks, Is.EqualTo(0), $"Expected 0 clicks across all renders (got {totalClicks}; see {Path.Combine(outDir, "README.md")})");
        }
    }
}
