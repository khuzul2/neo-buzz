using System.Globalization;
using NeoBuzz.Dsp;
namespace NeoBuzz.PitchEval
{
    /// <summary>For a look at one stem: the tracker's estimates against the reference around a time (seconds).</summary>
    internal static class Dbg
    {
        public static void Track(string wav, string csv, int atSecond)
        {
            var (x, rate) = Wav.Read(wav);
            var refs = File.ReadLines(csv).Select(l => l.Split(',')).Select(p => (T: double.Parse(p[0], CultureInfo.InvariantCulture), F: double.Parse(p[1], CultureInfo.InvariantCulture))).ToList();
            if (atSecond < 0) { int k = 0; while (k < refs.Count - 400 && !Enumerable.Range(k, 400).All(j => refs[j].F > 0)) k += 50; atSecond = (int)refs[k].T + 1; Console.WriteLine("first voiced second " + atSecond); }
            foreach (var cfg in new[] { Config.All[0], Config.All[4] })
            {
                var t = PitchEval.Track.Run(x, rate, cfg.Range, cfg.Quality);
                Console.WriteLine(cfg.Name + $" rate {rate} samples {x.Length} frames {t.Count}");
                for (int i = 0; i < t.Count; i++)
                    if (t.T[i] >= atSecond && t.T[i] < atSecond + 0.3 && i % 6 == 0)
                    {
                        var r = refs.OrderBy(q => Math.Abs(q.T - t.T[i])).First();
                        Console.WriteLine($"  t {t.T[i]:F3} raw {t.Raw[i]:F1} voiced {t.Voiced[i]} conf {t.Conf[i]:F2} rms {t.Rms[i]:F4}  ref {r.F:F1}");
                    }
            }
        }
    }
}
