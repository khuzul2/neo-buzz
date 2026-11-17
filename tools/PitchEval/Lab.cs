// The Amateur Vocal Percussion hits for the Neo Voice to MIDI evaluation (VoiceTriggers.cs): per participant and modality, the hits the onset
// detector found (at a given minimum interval) matched to the annotations within 50 ms, each with the recording it is in.
using System.Globalization;
using System.Text;
using NeoBuzz.Dsp;

namespace NeoBuzz.PitchEval
{
    internal static class Lab
    {
        public sealed class H { public int Cls; public float[] Ring; public long Onset; public int Rate; public bool Improv; }
        public sealed class P { public string Name; public List<H>[] Class = new List<H>[4]; public List<H> Improv = new(); public int[] Ann = new int[4]; public int ImprovAnn; }

        public static List<P> Collect(string dir, double minInterval, int limit = 0)
        {
            string root = Directory.Exists(Path.Combine(dir, "AVP_Dataset")) ? Path.Combine(dir, "AVP_Dataset") : dir;
            var res = new List<P>();
            foreach (var modality in new[] { "Fixed", "Personal" })
            {
                var parts = Directory.GetDirectories(Path.Combine(root, modality)).OrderBy(d => int.Parse(Path.GetFileName(d).Split('_')[1])).ToList();
                if (limit > 0) parts = parts.Take(limit).ToList();
                foreach (var pd in parts)
                {
                    var p = new P { Name = Path.GetFileName(pd) + "/" + modality };
                    foreach (var wav in Directory.GetFiles(pd, "*.wav").Where(f => !Path.GetFileName(f).StartsWith("._")))
                    {
                        string csv = Path.ChangeExtension(wav, ".csv");
                        if (!File.Exists(csv)) continue;
                        string kind = Path.GetFileNameWithoutExtension(wav).Split('_')[1];
                        var (x, rate) = Wav.Read(wav);
                        var ann = new List<(double T, int C)>();
                        foreach (var line in File.ReadLines(csv))
                        {
                            var q = line.Split(',');
                            if (q.Length < 2 || !double.TryParse(q[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) continue;
                            ann.Add((t, q[1].Trim() switch { "kd" => 0, "sd" => 1, "hhc" => 2, "hho" => 3, _ => -1 }));
                        }
                        var od = new OnsetDetector(rate) { MinIntervalSeconds = minInterval };
                        var buf = new OnsetEvent[od.MaxEvents(256)];
                        var found = new List<OnsetEvent>();
                        for (int pos = 0; pos + 256 <= x.Length; pos += 256) { int n = od.Process(x, pos, 256, buf, 0); for (int i = 0; i < n; i++) found.Add(buf[i]); }
                        var used = new bool[found.Count];
                        var hits = new List<H>();
                        int size = 1; while (size < x.Length + 8192) size <<= 1;
                        var ring = new float[size]; Array.Copy(x, ring, x.Length);
                        for (int a = 0; a < ann.Count; a++)
                        {
                            int best = -1; double bd = double.MaxValue;
                            for (int i = 0; i < found.Count; i++) { if (used[i]) continue; double d = Math.Abs(found[i].Sample / (double)rate - ann[a].T); if (d < bd) { bd = d; best = i; } }
                            if (best < 0 || bd > 0.05 || ann[a].C < 0) continue;
                            used[best] = true;
                            hits.Add(new H { Cls = ann[a].C, Ring = ring, Onset = found[best].Sample, Rate = rate });
                        }
                        int annCount = ann.Count(a => a.C >= 0);
                        if (kind == "Improvisation") { p.Improv = hits; p.ImprovAnn = annCount; }
                        else { int c = kind switch { "Kick" => 0, "Snare" => 1, "HHclosed" => 2, "HHopened" => 3, _ => -1 }; if (c >= 0) { p.Class[c] = hits; p.Ann[c] = annCount; } }
                    }
                    if (p.Class.All(c => c != null)) res.Add(p);
                }
            }
            return res;
        }
    }
}
