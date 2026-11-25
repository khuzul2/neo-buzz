// Neo Arp's listening pack (docs/specs/neo-arp.md §9, AGENTS.md §5): each factory preset playing one 4-bar chord progression (twice, so about 16 s) through
// Neo Poly on one fixed plain patch, at 120 BPM, as 48 kHz WAV, with a README and a click check. Explicit: it runs only when asked, and writes to the folder
// in ARP_PACK (kept local, never committed):
//   ARP_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter FullyQualifiedName~NeoArpPack
using System.Globalization;
using System.Text;
using NeoBuzz.Arp;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class NeoArpPack
    {
        static string Slug(string s) =>
            string.Join("-", new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Split('-', StringSplitOptions.RemoveEmptyEntries));

        static void WriteWav(string path, float[] l, float[] r)
        {
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);
            int n = l.Length;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 4); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(ArpPack.Rate); w.Write(ArpPack.Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 4);
            for (int i = 0; i < n; i++)
            {
                w.Write((short)Math.Clamp(l[i] * 32767f, -32768f, 32767f));
                w.Write((short)Math.Clamp(r[i] * 32767f, -32768f, 32767f));
            }
        }

        static string Settings(Dictionary<int, int> values)
        {
            var t = Params.Table;
            var parts = new List<string>();
            for (int i = 0; i < t.Length; i++)
            {
                if (!values.TryGetValue(i, out int raw) || raw == t[i].Default) continue;
                if (i >= P.S1) continue;                                                     // the step row is listed on its own line
                parts.Add($"{t[i].Name} {t[i].Describe(raw)}");
            }
            return string.Join(", ", parts);
        }

        static string Row(Dictionary<int, int> values)
        {
            var t = Params.Table;
            if (!values.TryGetValue(P.Pattern, out int pattern) || pattern != 11) return null;                   // the row shapes the Steps pattern only
            int len = values.TryGetValue(P.StepLen, out int l) ? l : 16;
            var sb = new StringBuilder();
            for (int i = 0; i < len; i++)
            {
                string kind = t[P.S1 + i].Describe(values.TryGetValue(P.S1 + i, out int k) ? k : t[P.S1 + i].Default);
                string tr = t[P.X1 + i].Describe(values.TryGetValue(P.X1 + i, out int x) ? x : t[P.X1 + i].Default);
                sb.Append($"{i + 1}:{kind}{(tr == "0" ? "" : " " + tr)}  ");
            }
            return sb.ToString().TrimEnd();
        }

        [Test, Explicit("writes the local listening pack")]
        public void Pack()
        {
            var dir = Environment.GetEnvironmentVariable("ARP_PACK");
            Assert.That(dir, Is.Not.Null.And.Not.Empty, "ARP_PACK names the folder");
            Directory.CreateDirectory(dir!);
            var presets = ArpPack.Presets();
            Assert.That(presets.Count, Is.LessThanOrEqualTo(8));
            const int bars = 8;
            int total = bars * ArpPack.Bar + 24000;                                 // the progression twice, and a beat's tail
            var readme = new StringBuilder();
            readme.AppendLine("Neo Arp listening pack (ARP1, issue #183)");
            readme.AppendLine("==========================================");
            readme.AppendLine();
            readme.AppendLine("Each item is one factory preset playing the same four-bar chord progression twice (8 bars, about 16 s) through Neo Poly on one fixed plain patch.");
            readme.AppendLine("120 BPM, 48 kHz, 16-bit stereo WAV, every chord held for its whole bar and played out of key order (so As played differs from Up).");
            readme.AppendLine();
            readme.AppendLine("The chords (MIDI notes in the order they are played): bar 1 C major 7  = 64 71 60 67 (E B C G); bar 2 A minor 7 = 60 67 57 64; bar 3 F major 7 = 57 64 53 60; bar 4 G dominant 7 = 62 55 65 59.");
            readme.AppendLine("The progression is in the test code (NeoBuzz.MachineTests/NeoArpChainTests.cs, ArpPack.Chords); the render is reproducible: ARP_PACK=<folder> dotnet test NeoBuzz.MachineTests --filter FullyQualifiedName~NeoArpPack");
            readme.AppendLine();
            readme.AppendLine("Neo Poly patch (the same for every item): oscillator 1 a saw, filter 1 the 24 dB ladder at 3 kHz with 10 % resonance and the default envelope amount, amp envelope attack 3 ms, decay 0.35 s, sustain 45 %, release 0.15 s, no drift, volume -6 dB; everything else at its default.");
            readme.AppendLine("Neo Poly takes the arpeggiator's events at the start of each block (blocks of 64 samples here, as in the host), so the notes sit within 1.3 ms of where Neo Arp put them.");
            readme.AppendLine();
            readme.AppendLine("Items");
            readme.AppendLine("-----");
            var levels = new StringBuilder("item\tnotes\trms dBFS\tpeak dBFS\tclicks\n");
            int clicks = 0, index = 1;
            foreach (var (name, comment, values) in presets)
            {
                var rig = ArpPack.Play(values, bars);
                var problems = ArpRig.Balance(rig.Log).Where(p => !p.Contains("never released")).ToList();
                Assert.That(problems, Is.Empty, name);
                var (l, r) = ArpPack.Render(rig.Log, total, ArpPack.PlainPatch());
                int c = ClickDetector.Find(l).Count + ClickDetector.Find(r).Count;
                clicks += c;
                double s = 0, pk = 0;
                for (int i = 0; i < l.Length; i++) { s += l[i] * (double)l[i] + r[i] * (double)r[i]; pk = Math.Max(pk, Math.Max(Math.Abs(l[i]), Math.Abs(r[i]))); }
                string file = $"{index:00}-{Slug(name)}.wav";
                WriteWav(Path.Combine(dir!, file), l, r);
                readme.AppendLine();
                readme.AppendLine($"{index:00}  {file}   \"{name}\"");
                readme.AppendLine($"    {comment}");
                readme.AppendLine($"    Settings changed from the defaults: {Settings(values)}");
                var row = Row(values);
                if (row != null) readme.AppendLine($"    Step row: {row}");
                readme.AppendLine($"    {rig.Ons.Count()} notes, {c} clicks (the detector's second-difference test), peak {20 * Math.Log10(pk + 1e-20):0.0} dBFS");
                levels.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{file}\t{rig.Ons.Count()}\t{10 * Math.Log10(s / (2.0 * l.Length) + 1e-20):0.0}\t{20 * Math.Log10(pk + 1e-20):0.0}\t{c}"));
                index++;
            }
            readme.AppendLine();
            readme.AppendLine("Defaults of Neo Arp, for reading the settings: Up, 1 octave, Octave mode Up, 1 repeat, Transpose 0, Timing Song tempo, Rate 1/16, Feel Straight, Swing 0 %, Gate 80 %, Velocity As played, Fixed velocity 100, Accent 60 %, no humanize, Latch off, Retrigger Free running.");
            readme.AppendLine($"Click detector (ClickDetector.Find, the project's): {clicks} clicks over all items.");
            File.WriteAllText(Path.Combine(dir!, "README.txt"), readme.ToString());
            File.WriteAllText(Path.Combine(dir!, "levels.txt"), levels.ToString());
            TestContext.Progress.WriteLine(levels.ToString());
            Assert.That(clicks, Is.EqualTo(0), "clicks in the pack's renders");
        }
    }
}
