// Convert to MIDI drums CD1: the drum converters on kits the classifier was NOT fitted on, AM2's rules (Am2RuleDrums, the comparison baseline) against
// CD1's network (DrumConverter). Evaluation only; the data are read where the runner put them and never kept.
//
//  IDMT-SMT-Drums (Zenodo 7544164, CC BY-NC-ND 4.0): the mixes of the real, techno and wave kits; kick, snare, hat (closed and open together).
//  MDB Drums (Southall et al., ISMIR 2017, CC BY-NC-SA 4.0): 23 MedleyDB tracks, acoustic kits, full mix and drum-only; kick, snare, hat, tom, cymbal.
// A hit is right when its group matches and its time is within 50 ms; "hat" is closed and open hat together (these sets do not tell them apart),
// "cymbal" is crash and ride, "tom" all toms.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ReBuzz.Core.AudioLibrary.AudioToMidi;

namespace NeoBuzz.ConvertEval
{
    internal static class OutOfDomain
    {
        internal sealed record Item(string Set, string Name, string Wav, List<(double T, string C)> Truth);
        static readonly string[] Cls = { "kick", "snare", "hat", "tom", "cymbal" };

        static string GroupOfLabel(string label) => label switch
        {
            DrumConverter.Kick => "kick", DrumConverter.Snare => "snare", DrumConverter.ClosedHat or DrumConverter.OpenHat => "hat",
            DrumConverter.Crash => "cymbal", _ => "tom",
        };

        public static List<Item> Idmt(string dir)
        {
            string audio = Directory.Exists(Path.Combine(dir, "audio")) ? Path.Combine(dir, "audio") : dir;
            string ann = Directory.Exists(Path.Combine(dir, "annotation_xml")) ? Path.Combine(dir, "annotation_xml") : dir;
            var items = new List<Item>();
            foreach (var f in Directory.GetFiles(audio, "*#MIX.wav", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(f).Replace("#MIX.wav", "");
                string xml = Directory.GetFiles(ann, stem + "#MIX.xml", SearchOption.AllDirectories).FirstOrDefault();
                if (xml == null) continue;
                var truth = new List<(double, string)>();
                foreach (var ev in XDocument.Load(xml).Descendants("event"))
                {
                    string c = (string)ev.Element("instrument") switch { "KD" => "kick", "SD" => "snare", "HH" => "hat", _ => null };
                    if (c != null) truth.Add((double.Parse((string)ev.Element("onsetSec"), CultureInfo.InvariantCulture), c));
                }
                string kit = stem.StartsWith("Real") ? "real" : stem.StartsWith("Techno") ? "techno" : stem.StartsWith("Wave") ? "wave" : "other";
                items.Add(new Item("IDMT-SMT-Drums " + kit + " kit", stem, f, truth));
            }
            return items;
        }

        public static List<Item> MdbDrums(string dir)
        {
            string root = Directory.Exists(Path.Combine(dir, "MDB Drums")) ? Path.Combine(dir, "MDB Drums") : dir;
            var items = new List<Item>();
            foreach (var (sub, suffix, set) in new[] { ("full_mix", "_MIX.wav", "MDB Drums full mix"), ("drum_only", "_Drum.wav", "MDB Drums drum-only") })
            {
                string folder = Path.Combine(root, "audio", sub);
                if (!Directory.Exists(folder)) continue;
                foreach (var f in Directory.GetFiles(folder, "*" + suffix).OrderBy(f => f, StringComparer.Ordinal))
                {
                    string stem = Path.GetFileName(f)[..^suffix.Length];
                    string txt = Path.Combine(root, "annotations", "class", stem + "_class.txt");
                    if (!File.Exists(txt)) continue;
                    var truth = new List<(double, string)>();
                    foreach (var line in File.ReadAllLines(txt))
                    {
                        var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length < 2) continue;
                        string c = p[1] switch { "KD" => "kick", "SD" => "snare", "HH" => "hat", "TT" => "tom", "CY" => "cymbal", _ => null };
                        if (c != null) truth.Add((double.Parse(p[0], CultureInfo.InvariantCulture), c));
                    }
                    items.Add(new Item(set, stem, f, truth));
                }
            }
            return items;
        }

        public static void Run(List<Item> items, StringBuilder md, string title)
        {
            md.AppendLine("## " + title);
            md.AppendLine();
            md.AppendLine("Onset F-measure (50 ms) per class, **AM2 rules** (main, as it was) against **CD1 network**; precision and recall in brackets. hat = closed and open hat together; `-` = the set has no such labels. Gate per set: CD1 no more than 2 points under AM2 on kick, snare and hat, and not under AM2 on tom and cymbal where labelled.");
            md.AppendLine();
            foreach (var set in items.Select(i => i.Set).Distinct())
            {
                var mine = items.Where(i => i.Set == set).ToList();
                var am2 = Cls.ToDictionary(c => c, _ => new Counts()); var cd1 = Cls.ToDictionary(c => c, _ => new Counts());
                double seconds = 0, workAm2 = 0, workCd1 = 0;
                foreach (var it in mine)
                {
                    var (x, rate) = NeoBuzz.PitchEval.Wav.Read(it.Wav);
                    seconds += x.Length / (double)rate;
                    var o = new ConvertOptions { Mode = ConvertMode.Drums };
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var a = Am2RuleDrums.Convert(x, rate, o, null, default); workAm2 += sw.Elapsed.TotalSeconds; sw.Restart();
                    var b = DrumConverter.Convert(x, rate, o, null, default); workCd1 += sw.Elapsed.TotalSeconds;
                    foreach (var c in Cls)
                    {
                        var refs = it.Truth.Where(t => t.C == c).Select(t => new Ref(t.T, t.T, 0, c)).ToList();
                        am2[c].Add(Program.Match(a.Where(n => GroupOfLabel(n.Label) == c).ToList(), refs, 0.05, (_, _) => true));
                        cd1[c].Add(Program.Match(b.Where(n => GroupOfLabel(n.Label) == c).ToList(), refs, 0.05, (_, _) => true));
                    }
                }
                md.AppendLine($"### {set} ({mine.Count} recordings, {seconds / 60:0.#} min, {mine.Sum(i => i.Truth.Count)} labelled hits; speed AM2 {seconds / Math.Max(1e-9, workAm2):0}x, CD1 {seconds / Math.Max(1e-9, workCd1):0}x)");
                md.AppendLine();
                md.AppendLine("| class | hits | AM2 rules | CD1 network | change | gate |");
                md.AppendLine("|---|---:|---:|---:|---:|---|");
                var allA = new Counts(); var allB = new Counts();
                foreach (var c in Cls)
                {
                    long n = am2[c].Tp + am2[c].Fn;
                    if (n == 0) { md.AppendLine($"| {c} | 0 | - | - | - | not labelled |"); continue; }
                    allA.Add(am2[c]); allB.Add(cd1[c]);
                    double fa = am2[c].F, fb = cd1[c].F;
                    bool core = c is "kick" or "snare" or "hat";
                    bool ok = core ? fb >= fa - 0.02 : fb >= fa;
                    md.AppendLine($"| {c} | {n} | {Counts.Pc(fa)} ({Counts.Pc(am2[c].P)} / {Counts.Pc(am2[c].R)}) | {Counts.Pc(fb)} ({Counts.Pc(cd1[c].P)} / {Counts.Pc(cd1[c].R)}) | {(fb - fa) * 100:+0.0;-0.0} | {(ok ? "pass" : "FAIL")} |");
                }
                md.AppendLine($"| all labelled classes | {allA.Tp + allA.Fn} | {Counts.Pc(allA.F)} | {Counts.Pc(allB.F)} | {(allB.F - allA.F) * 100:+0.0;-0.0} | |");
                md.AppendLine();
            }
        }
    }
}
