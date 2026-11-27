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
        internal const int Rate = 48000;
        internal const double Bpm = 128.0;

        internal readonly record struct NoteEvent(double StartBeat, double DurationBeats, int Note, float Velocity, int MidiChannel, bool UseTrack4 = false);

        internal static string RepoRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "Machines")) && Directory.Exists(Path.Combine(d.FullName, "NeoBuzz.MachineTests")))
                    return d.FullName;
            throw new DirectoryNotFoundException("Repository root not found");
        }

        internal static string Slug(string name) =>
            Regex.Replace(name.ToLowerInvariant().Replace("—", "-").Replace("%", "pct"), @"[^a-z0-9]+", "-").Trim('-');

        // 1. Heroic 4-bar melody (MIDI ch 1) with accents and real note-offs.
        internal static NoteEvent[] MelodyLine() => new[]
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
        internal static NoteEvent[] ArpeggioLine() => new[]
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
        internal static NoteEvent[] NoiseDrumLine()
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

        internal static (float[] L, float[] R, float[][] Gains) RenderLine(ParamValues v, NoteEvent[] events, double totalBeats, int exactSamples = 0)
        {
            var e = new ChipEngine(v);
            e.SetSampleRate(Rate);
            e.Tempo = Bpm;

            double secPerBeat = 60.0 / Bpm;
            int totalSamples = exactSamples > 0 ? exactSamples : (int)Math.Ceiling((totalBeats + 2.0) * secPerBeat * Rate);
            var outL = new float[totalSamples];
            var outR = new float[totalSamples];
            var gains = new float[4][];
            var blockGains = new float[4][];
            for (int c = 0; c < 4; c++)
            {
                gains[c] = new float[totalSamples];
                blockGains[c] = new float[ChipEngine.MaxBlock];
            }
            e.GainTrace = blockGains;
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
                for (int c = 0; c < 4; c++) Array.Copy(blockGains[c], 0, gains[c], pos, n);
            }
            return (outL, outR, gains);
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

        /// <summary>
        /// Direct second-difference click detector on a channel gain trace (listening round 1 gate).
        /// An un-slewed 1/15 envelope step produces d2 = 0.0667; a hard note edge produces d2 = 1.0.
        /// Any sample with |g[i+1] - 2*g[i] + g[i-1]| > 0.015 (with 5 ms refractory window) is flagged.
        /// </summary>
        internal static int DetectGainClicks(float[] gain, int rate = Rate)
        {
            int refractory = Math.Max(1, (int)(0.005 * rate));
            int last = -refractory;
            int clicks = 0;
            for (int i = 1; i < gain.Length - 1; i++)
            {
                double d2 = Math.Abs(gain[i + 1] - 2.0 * gain[i] + gain[i - 1]);
                if (d2 > 0.015 && i - last >= refractory)
                {
                    clicks++;
                    last = i;
                }
            }
            return clicks;
        }

        static double MaxGainD2(float[][] gains)
        {
            double max = 0;
            for (int c = 0; c < gains.Length; c++)
            {
                var g = gains[c];
                for (int i = 1; i < g.Length - 1; i++)
                {
                    double d2 = Math.Abs(g[i + 1] - 2.0 * g[i] + g[i - 1]);
                    if (d2 > max) max = d2;
                }
            }
            return max;
        }

        internal static byte[] WavBytes(float[] l, float[] r, int rate = Rate)
        {
            int n = l.Length;
            using var ms = new MemoryStream(44 + n * 4);
            using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
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
            bw.Flush();
            return ms.ToArray();
        }

        static void WriteWav16(string path, float[] l, float[] r, int rate = Rate) =>
            File.WriteAllBytes(path, WavBytes(l, r, rate));

        internal static readonly string[] SmoothPresetNames =
        {
            "Lead — Square hero",
            "Lead — Thin 12 %",
            "Bass — Pulse bass",
            "Pad — Unison detune",
        };

        [Test]
        public void BuildListeningPack186()
        {
            string root = RepoRoot();
            string outDir = Path.Combine(root, "listening-packs", "186");
            Directory.CreateDirectory(outDir);
            foreach (var oldWav in Directory.GetFiles(outDir, "*.wav"))
                File.Delete(oldWav);

            string prsPath = Path.Combine(root, "Machines", "NeoChip", "Neo Chip.prs.xml");
            var doc = XDocument.Load(prsPath);
            var byName = doc.Root!.Elements("Item").ToDictionary(x => (string)x.Attribute("Key")!);
            var melody = MelodyLine();
            const int EightSecSamples = 8 * Rate;

            var readme = new StringBuilder();
            readme.AppendLine("# Neo Chip — Listening Pack, Round 3 Smooth Envelope (Issue #186)");
            readme.AppendLine();
            readme.AppendLine("Rendered by `NeoBuzz.MachineTests/NeoChipPack.cs` at 48 kHz, 16-bit stereo WAV, 128 BPM, 8.0 s each (git-ignored, never committed).");
            readme.AppendLine();
            readme.AppendLine("## Contents");
            readme.AppendLine("The four Smooth-envelope presets (`Envelope = Smooth`) on the 4-bar `melody` line (MIDI channel 1 with accents and real note-offs). The other 8 presets remain `Envelope = Stepped` and bit-identical to `cb8c406a`.");
            readme.AppendLine();
            readme.AppendLine("## Click detectors (audio + gain trace)");
            readme.AppendLine("- **Audio:** Frame-peak second-difference detector (128-sample frame max `d2` vs local mean of 14 neighbouring frames, threshold `4.5x` & `> 0.15`, 20 ms refractory window).");
            readme.AppendLine("- **Gain trace:** Direct second-difference detector on each channel's live `envGain * velGain * stealGain` trace, threshold `|g[i+1] - 2g[i] + g[i-1]| > 0.015` (5 ms refractory; un-slewed 1/15 step = `0.0667`).");
            readme.AppendLine();
            readme.AppendLine("| # | Preset | Line | File | RMS (dBFS) | Peak (dBFS) | Audio clicks | Gain clicks | Max gain d2 | Notes |");
            readme.AppendLine("|---|---|---|---|---:|---:|---:|---:|---:|---|");

            int totalAudioClicks = 0;
            int totalGainClicks = 0;
            int idx = 1;
            foreach (string name in SmoothPresetNames)
            {
                var item = byName[name];
                var v = new ParamValues(Params.Table);
                foreach (var p in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter"))
                    v.Set((int)p.Attribute("Index")!, (int)p.Attribute("Value")!);

                var (l, r, gains) = RenderLine(v, melody, 16.0, exactSamples: EightSecSamples);
                string file = $"{idx:00}-{Slug(name)}__melody.wav";
                WriteWav16(Path.Combine(outDir, file), l, r);

                double rms = Math.Sqrt(0.5 * (Measure.Rms(l, 0, l.Length) * Measure.Rms(l, 0, l.Length) + Measure.Rms(r, 0, r.Length) * Measure.Rms(r, 0, r.Length)));
                double peak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
                var (audioClicks, times) = DetectClicks(l, r);
                int gainClicks = 0;
                for (int c = 0; c < 4; c++) gainClicks += DetectGainClicks(gains[c]);
                double maxGd2 = MaxGainD2(gains);
                totalAudioClicks += audioClicks;
                totalGainClicks += gainClicks;
                string note = audioClicks == 0 && gainClicks == 0
                    ? "clean"
                    : $"audio={audioClicks} ({string.Join(", ", times.Take(3).Select(t => $"{t:F2}s"))}), gain={gainClicks}";
                readme.AppendLine($"| {idx:00} | {name} | melody | `{file}` | {Measure.Db(rms):F1} | {Measure.Db(peak):F1} | {audioClicks} | {gainClicks} | {maxGd2:F5} | {note} |");
                idx++;
            }

            readme.AppendLine();
            readme.AppendLine($"**Total files:** {SmoothPresetNames.Length}. **Total audio clicks:** {totalAudioClicks}. **Total gain-trace clicks:** {totalGainClicks}.");
            File.WriteAllText(Path.Combine(outDir, "README.md"), readme.ToString(), new UTF8Encoding(false));
            TestContext.Out.WriteLine($"Wrote {SmoothPresetNames.Length} WAVs and README.md to {outDir} (audio clicks: {totalAudioClicks}, gain-trace clicks: {totalGainClicks})");
            Assert.That(totalAudioClicks, Is.EqualTo(0), $"Expected 0 audio clicks across all renders (got {totalAudioClicks})");
            Assert.That(totalGainClicks, Is.EqualTo(0), $"Expected 0 gain-trace clicks across all renders (got {totalGainClicks})");
        }
    }
}
