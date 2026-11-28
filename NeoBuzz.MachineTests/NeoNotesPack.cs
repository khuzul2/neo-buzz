#nullable disable
// Neo Notes' listening pack (NOT1, AGENTS.md §5): each factory preset on the same two-bar phrase, played dry, then 0.5 s of gap, then through the preset, on one Neo Poly pluck
// patch ("Pluck — Bright" with a held tail: Amp sustain 35 % and Amp release 0.15 s, so that Length and Echo can be heard), at 100 BPM, 48 kHz, 16-bit WAV, about 15 s each, with a README.
// Local only (never committed). Two tests:
//   * EveryPresetRendersCleanAndRepeatable runs in CI: the renders are finite, click-free (the click detector on every one: 0 clicks) and the same twice.
//   * Pack is explicit and writes the folder named in NOTES_PACK:
//       NOTES_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter FullyQualifiedName~NeoNotesPack
using System.Globalization;
using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Notes;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class NeoNotesPack
    {
        const int Rate = 48000;
        const int Block = 64;
        const int Total = Rate * 15;
        static readonly long PhraseLength = (long)Math.Round(NotesPhrase.Beats * NotesPhrase.SamplesPerBeat);
        static readonly long Gap = Rate / 2;

        static ParamValues PluckPatch()
        {
            var v = new ParamValues(NeoBuzz.Poly.Params.Table);
            var bright = PresetTests.AllPresets().First(p => p.Machine == "Neo Poly" && p.Preset == "Pluck — Bright");
            foreach (var kv in bright.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            v.Set(NeoBuzz.Poly.P.AmpS, NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.AmpS].Raw(0.35));
            v.Set(NeoBuzz.Poly.P.AmpR, NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.AmpR].Raw(0.15));
            return v;
        }

        /// <summary>The phrase dry (straight to the synth), a gap, then the phrase through the preset: stereo, <see cref="Total"/> samples. Events reach the synth at the start of their block.</summary>
        internal static (float[] L, float[] R, NotesRig Rig) Render(IReadOnlyDictionary<int, int> preset)
        {
            var synth = new NeoBuzz.Poly.PolyEngine(PluckPatch());
            synth.SetSampleRate(Rate);
            long processedStart = PhraseLength + Gap;
            var rig = new NotesRig(Block, v => { foreach (var kv in preset) v.Set(kv.Key, kv.Value); }, NotesPhrase.SamplesPerBeat);
            NotesPhrase.Feed(rig, processedStart);
            var dry = NotesPhrase.Events(0);
            int next = 0;
            var l = new float[Total];
            var r = new float[Total];
            var bl = new float[Block];
            var br = new float[Block];
            for (long pos = 0; pos < Total; pos += Block)
            {
                while (next < dry.Count && dry[next].T < pos + Block)
                {
                    var (_, on, note, vel) = dry[next++];
                    if (on) synth.NoteOn(note, note, vel / 127f); else synth.NoteOff(note);
                }
                int before = rig.Sent.Count;
                rig.Run(pos + 1);
                for (int i = before; i < rig.Sent.Count; i++)
                {
                    var ev = rig.Sent[i].Ev;
                    if (ev.Kind == NotesEvent.On) synth.NoteOn(1000 + ev.Channel * 128 + ev.Data1, ev.Data1, ev.Data2 / 127f);
                    else if (ev.Kind == NotesEvent.Off) synth.NoteOff(1000 + ev.Channel * 128 + ev.Data1);
                }
                int n = (int)Math.Min(Block, Total - pos);
                synth.Process(bl, br, n);
                Array.Copy(bl, 0, l, pos, n);
                Array.Copy(br, 0, r, pos, n);
            }
            return (l, r, rig);
        }

        static float[] Mono(float[] l, float[] r) => l.Zip(r, (a, b) => 0.5f * (a + b)).ToArray();

        static string Slug(string s) =>
            string.Join("-", new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Split('-', StringSplitOptions.RemoveEmptyEntries));

        static void WriteWav(string path, float[] l, float[] r, float gain)
        {
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);
            int n = l.Length;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 4); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 4);
            for (int i = 0; i < n; i++)
            {
                w.Write((short)Math.Clamp((int)Math.Round(l[i] * gain * 32767.0), -32768, 32767));
                w.Write((short)Math.Clamp((int)Math.Round(r[i] * gain * 32767.0), -32768, 32767));
            }
        }

        static string Settings(IReadOnlyDictionary<int, int> values)
        {
            var parts = new List<string>();
            foreach (var p in Params.Table)
            {
                if (!values.TryGetValue(p.Index, out int raw) || raw == p.Default) continue;
                parts.Add($"{p.Name} {p.Describe(raw)}");
            }
            return string.Join(", ", parts);
        }

        static (double PeakDb, double RmsDb) Level(float[] l, float[] r, int from, int to, float gain = 1)
        {
            double peak = 0, sum = 0;
            for (int i = from; i < to; i++)
            {
                double a = 0.5 * (l[i] + r[i]) * gain, b = Math.Max(Math.Abs(l[i]), Math.Abs(r[i])) * gain;
                peak = Math.Max(peak, b);
                sum += a * a;
            }
            return (20 * Math.Log10(Math.Max(peak, 1e-9)), 10 * Math.Log10(Math.Max(sum / (to - from), 1e-18)));
        }

        static List<(string Name, Dictionary<int, int> Values)> Presets() =>
            PresetTests.AllPresets().Where(p => p.Machine == "Neo Notes")
                .Select(p => (p.Preset, p.Values.Where(kv => kv.Key >= 0).ToDictionary(kv => kv.Key, kv => kv.Value))).ToList();

        [Test]
        public void EveryPresetRendersCleanAndRepeatable()
        {
            var presets = Presets();
            Assert.That(presets.Count, Is.EqualTo(8));
            foreach (var (name, values) in presets)
            {
                var (l, r, rig) = Render(values);
                var mono = Mono(l, r);
                int clicks = ClickDetector.Find(mono).Count;
                Assert.That(mono.All(float.IsFinite), Is.True, name);
                Assert.That(mono.Max(Math.Abs), Is.GreaterThan(0.01f), name + " is audible");
                Assert.That(clicks, Is.EqualTo(0), name + ": clicks");
                Assert.That(rig.E.SoundingPitches, Is.EqualTo(0), name);
                if (name.StartsWith("Humanize") || name.StartsWith("Echo — Rising"))
                {
                    var (l2, r2, _) = Render(values);
                    Assert.That(l2.SequenceEqual(l) && r2.SequenceEqual(r), Is.True, name + ": the second render is bit-identical");
                }
            }
        }

        /// <summary>Round 2 (NOT1b, issue #189): Humanize "Loose Player" with Keep chords, against its round 1 settings. Writes the folder named in NOTES_PACK2.</summary>
        [Test, Explicit("writes the local round 2 listening pack")]
        public void PackRound2()
        {
            var dir = Environment.GetEnvironmentVariable("NOTES_PACK2");
            Assert.That(dir, Is.Not.Null.And.Not.Empty, "NOTES_PACK2 names the folder");
            Directory.CreateDirectory(dir!);
            var loose = Presets().First(p => p.Name == "Humanize — Loose Player").Values;
            var old = new Dictionary<int, int>(loose) { [P.HumChord] = 0 };
            Assert.That(loose[P.HumChord], Is.EqualTo(1));
            var inv = CultureInfo.InvariantCulture;
            long processedStart = PhraseLength + Gap;
            var runs = new[] { ("01-humanize-loose-player-keep-chords.wav", "the new preset (Keep chords on)", loose), ("02-humanize-loose-player-old.wav", "the round 1 settings (Keep chords off)", old) };
            var renders = runs.Select(r => (r.Item1, r.Item2, R: Render(r.Item3))).ToList();
            float loudest = renders.Max(x => Math.Max(x.R.L.Max(Math.Abs), x.R.R.Max(Math.Abs)));
            float gain = 0.708f / loudest;
            var readme = new StringBuilder();
            readme.AppendLine("Neo Notes listening pack, round 2 (NOT1b, issue #189): Humanize - Loose Player with Keep chords");
            readme.AppendLine("========================================================================================");
            readme.AppendLine();
            readme.AppendLine("Two files, same phrase and same Neo Poly pluck patch as round 1 (100 BPM, 48 kHz, 16-bit stereo WAV, 15 s). Each plays the phrase dry first, then a 0.5 s gap, then through the machine.");
            readme.AppendLine($"Dry phrase starts at 0.0 s (it lasts {PhraseLength / (double)Rate:F1} s); the processed phrase starts at {processedStart / (double)Rate:F1} s (sample {processedStart}).");
            readme.AppendLine($"One gain for both files: {20 * Math.Log10(gain):F1} dB (the louder peaks at -3 dBFS). The phrase has three chords: C major triad (beat 2), F major triad (beat 4), G chord of four notes (beat 6).");
            readme.AppendLine();
            foreach (var (file, what, (l, r, rig)) in renders)
            {
                var mono = Mono(l, r);
                int clicks = ClickDetector.Find(mono).Count;
                Assert.That(clicks, Is.EqualTo(0), file + ": clicks");
                WriteWav(Path.Combine(dir!, file), l, r, gain);
                // The chords' note-on gaps: for each chord of the phrase, the first and last note on of its notes (the first on of each note at or after its nominal sample).
                var ons = rig.Ons.ToList();
                var gaps = new List<double>();
                var lates = new List<double>();
                foreach (var (beat, len, notes, vel) in NotesPhrase.Items)
                {
                    if (notes.Length < 3) continue;
                    long nominal = processedStart + (long)Math.Round(beat * NotesPhrase.SamplesPerBeat);
                    var t = notes.Select(n => ons.First(o => o.Note == n && o.T >= nominal).T).ToList();
                    gaps.Add((t.Max() - t.Min()) * 1000.0 / Rate);
                    lates.Add((t.Min() - nominal) * 1000.0 / Rate);
                }
                readme.AppendLine($"{file}: {what}.");
                readme.AppendLine($"  chord gaps (first to last note on), the three chords: {string.Join(", ", gaps.Select(g => g.ToString("F1", inv) + " ms"))}; largest {gaps.Max().ToString("F1", inv)} ms;");
                readme.AppendLine($"  chord lateness (earliest note of each chord after its nominal time): {string.Join(", ", lates.Select(g => g.ToString("F1", inv) + " ms"))};");
                readme.AppendLine($"  clicks (second difference against its local mean): {clicks}.");
                TestContext.Out.WriteLine($"PACK2 {file}: chord gaps {string.Join(", ", gaps.Select(g => g.ToString("F2", inv)))} ms, clicks {clicks}");
            }
            File.WriteAllText(Path.Combine(dir!, "README.txt"), readme.ToString());
        }

        [Test, Explicit("writes the local listening pack")]
        public void Pack()
        {
            var dir = Environment.GetEnvironmentVariable("NOTES_PACK");
            Assert.That(dir, Is.Not.Null.And.Not.Empty, "NOTES_PACK names the folder");
            Directory.CreateDirectory(dir!);
            var presets = Presets();
            Assert.That(presets.Count, Is.EqualTo(8));
            var renders = new List<(string Name, Dictionary<int, int> Values, float[] L, float[] R, int Clicks, bool Same)>();
            foreach (var (name, values) in presets)
            {
                var (l, r, _) = Render(values);
                var (l2, r2, _) = Render(values);
                bool same = l.SequenceEqual(l2) && r.SequenceEqual(r2);
                int clicks = ClickDetector.Find(Mono(l, r)).Count;
                Assert.That(same, Is.True, name + ": the render is bit-identical twice");
                Assert.That(clicks, Is.EqualTo(0), name + ": clicks");
                renders.Add((name, values, l, r, clicks, same));
            }
            float loudest = renders.Max(x => Math.Max(x.L.Max(Math.Abs), x.R.Max(Math.Abs)));
            float gain = 0.708f / loudest;      // one gain for every file: the loudest peaks at -3 dBFS and the levels between files stay as they are
            int dryEnd = (int)PhraseLength, processedStart = (int)(PhraseLength + Gap);
            var inv = CultureInfo.InvariantCulture;
            var readme = new StringBuilder();
            readme.AppendLine("Neo Notes listening pack (NOT1, issue #189)");
            readme.AppendLine("============================================");
            readme.AppendLine();
            readme.AppendLine("One file per factory preset. Each file plays the same two-bar phrase twice on the same Neo Poly pluck patch: first dry (0.0 s to 4.8 s), then a 0.5 s gap,");
            readme.AppendLine("then the phrase through the preset, starting at 5.3 s. The tail after it is the echoes and the pluck's release. 15 s, 100 BPM, 48 kHz, 16-bit stereo WAV.");
            readme.AppendLine("The phrase (C major with chromatic passing notes, 20 notes): C4, E4, G4 as single notes, a C major triad, A4 and B4 (short, the B hard), an F major triad held two beats,");
            readme.AppendLine("C5 and D5 (the D soft), a four-note G chord (G3 B3 D4 F4), then E5, E-flat 5 and D5. Velocities 40 to 120; lengths 1/4 to 2 beats.");
            readme.AppendLine("The patch: Neo Poly \"Pluck — Bright\" with Amp sustain 35 % and Amp release 0.15 s (a held tail, so that Length and Echo can be heard).");
            readme.AppendLine($"One gain for every file: {20 * Math.Log10(gain):F1} dB, so that the loudest peak is -3 dBFS and the levels between the files stay as they are.");
            readme.AppendLine("Click detector (second difference against its local mean) on every file: 0 clicks. Each file was rendered twice and the two renders are bit-identical.");
            readme.AppendLine();
            string[] listen =
            {
                "Listen for: the processed phrase is the same tune turned minor. The third, sixth and seventh (E, A, B) fall a semitone to E-flat, A-flat and B-flat; the C, D, F, G and the passing E-flat stay.",
                "Listen for: the tune tumbling down the keyboard. Each note is an octave higher than played (the transpose), then falls by 0, 1, 2 and 3 octaves in turn; the melody's outline is scattered across a four-octave range, the chords spread into a staircase.",
                "Listen for: every note answered by four quieter repeats a dotted eighth later, each quieter than the one before and half the note's length. A lilting, syncopated trail over the whole phrase.",
                "Listen for: every note followed by five repeats an eighth apart, each a fifth higher than the last and a little quieter. A glassy climb of almost three octaves above every note.",
                "Listen for: the same notes much harder. Every note is at least velocity 100: the soft C5-D5 pair and the quiet E4 are now as bright and loud as the rest, and the dynamics are flat and aggressive.",
                "Listen for: the same notes much softer. Nothing is above velocity 55: a dark, gentle phrase, with the hard B4 no louder than the rest.",
                "Listen for: dry, clipped notes. Every note lasts a thirty-second note (75 ms) however long it was held, so the chords and long notes lose their sustain and the phrase turns percussive.",
                "Listen for: a sloppy hand. Every note is up to 40 ms late and 25 % louder or softer by chance, with an extra random 20 % on the velocity; the chords are smeared and the rhythm is loose (but never early).",
            };
            readme.AppendLine("Files");
            readme.AppendLine("-----");
            int index = 1;
            foreach (var (name, values, l, r, clicks, same) in renders)
            {
                string file = $"{index:00}-{Slug(name)}.wav";
                WriteWav(Path.Combine(dir, file), l, r, gain);
                var all = Level(l, r, 0, Total);
                var dry = Level(l, r, 0, dryEnd);
                var proc = Level(l, r, processedStart, Total);
                readme.AppendLine();
                readme.AppendLine(file);
                readme.AppendLine($"    Preset: {name}");
                readme.AppendLine($"    Settings (not default): {Settings(values)}");
                readme.AppendLine($"    {listen[index - 1]}");
                readme.AppendLine("    Dry phrase: 0.0 s to 4.8 s. Processed phrase: starts at 5.3 s.");
                readme.AppendLine(string.Format(inv, "    Peak {0:F1} dBFS, RMS {1:F1} dBFS (whole file); dry part peak {2:F1}, RMS {3:F1}; processed part peak {4:F1}, RMS {5:F1}; clicks {6}; renders identical: {7}",
                    all.PeakDb + 20 * Math.Log10(gain), all.RmsDb + 20 * Math.Log10(gain), dry.PeakDb + 20 * Math.Log10(gain), dry.RmsDb + 20 * Math.Log10(gain),
                    proc.PeakDb + 20 * Math.Log10(gain), proc.RmsDb + 20 * Math.Log10(gain), clicks, same ? "yes" : "NO"));
                TestContext.Out.WriteLine(string.Format(inv, "{0}: peak {1:F1} dBFS, RMS {2:F1} dBFS (after the pack gain), clicks {3}", file, all.PeakDb + 20 * Math.Log10(gain), all.RmsDb + 20 * Math.Log10(gain), clicks));
                index++;
            }
            File.WriteAllText(Path.Combine(dir, "README.txt"), readme.ToString());
        }
    }
}
