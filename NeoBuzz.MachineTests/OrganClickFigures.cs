using System.Globalization;
using System.Text;
using System.Xml.Linq;
using NeoBuzz.Dsp;
using NeoBuzz.Organ;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Organ's key click measured on a chord at a preset's settings (docs/specs/neo-organ.md §4): the Cmaj7 chord C3 E3 G3 B3
    /// struck from silence, rendered with the preset as it is and again with Key click at 0 (the engine is otherwise
    /// deterministic, so the difference is the click as it comes out, through the drive and the speaker).
    /// </summary>
    internal static class OrganClickFigures
    {
        public const int Rate = 48000;
        public static readonly int[] Chord = { 48, 55, 59, 64 };
        const int Length = 28800;                                   // 0.6 s

        public static List<(string Name, Dictionary<int, int> Values)> Presets()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "ReBuzz.sln"))) d = d.Parent;
            var file = Path.Combine(d!.FullName, "Machines", "NeoOrgan", "Neo Organ.prs.xml");
            var list = new List<(string, Dictionary<int, int>)>();
            foreach (var item in XDocument.Load(file).Root!.Elements("Item"))
            {
                var v = new Dictionary<int, int>();
                foreach (var p in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter")) v[(int)p.Attribute("Index")!] = (int)p.Attribute("Value")!;
                list.Add(((string)item.Attribute("Key")!, v));
            }
            return list;
        }

        /// <summary>The click must stay under the note by this much (peak to peak): 12 dB for the two presets built on it, 20 dB for the rest.</summary>
        public static double BurstLimitDb(string preset) => preset.Contains("Funk Stabs") || preset.Contains("Reggae Bubble") ? -12 : -20;

        /// <summary>The chord, left and right, <see cref="Length"/> samples from the strike.</summary>
        public static (float[] L, float[] R) RenderChord(Dictionary<int, int> preset, bool click = true)
        {
            var v = new ParamValues(Params.Table);
            foreach (var kv in preset) v.Set(kv.Key, kv.Value);
            if (!click) v.Set(P.KeyClick, 0);
            var e = new OrganEngine(v);
            e.SetSampleRate(Rate);
            foreach (int n in Chord) e.NoteOn(1000 + n, n, 1f);
            var yl = new float[Length];
            var yr = new float[Length];
            var l = new float[OrganEngine.MaxBlock];
            var r = new float[OrganEngine.MaxBlock];
            for (int pos = 0; pos < Length; pos += OrganEngine.MaxBlock)
            {
                int n = Math.Min(OrganEngine.MaxBlock, Length - pos);
                e.Process(l, r, n);
                Array.Copy(l, 0, yl, pos, n);
                Array.Copy(r, 0, yr, pos, n);
            }
            return (yl, yr);
        }

        static double Peak(float[] a, float[] b, int from, int to)
        {
            double p = 0;
            for (int i = from; i < to; i++) p = Math.Max(p, Math.Max(Math.Abs(a[i]), Math.Abs(b[i])));
            return p;
        }

        /// <summary>The 8 sections of a 16th-order Butterworth high-pass: a sharp "above 6 kHz" (-56 dB at 4 kHz, -75 dB at 3.5 kHz), so
        /// the click's own band (1 - 3.5 kHz) and the organ's partials below are not counted as high frequencies.</summary>
        static readonly double[] HighPassQ = { 0.5024, 0.5225, 0.5669, 0.6468, 0.7882, 1.0607, 1.7224, 5.1011 };

        /// <summary>Mean square above 6 kHz (the high-pass over the whole render), both channels, in [from, to).</summary>
        static double HighBandPower(float[] l, float[] r, int from, int to, out double totalPower)
        {
            double hi = 0, all = 0;
            foreach (var x in new[] { l, r })
            {
                var hp = new Biquad[HighPassQ.Length];
                for (int k = 0; k < hp.Length; k++) hp[k].Set(BiquadType.HighPass, 6000, Rate, HighPassQ[k]);
                for (int i = 0; i < to; i++)
                {
                    float y = x[i];
                    for (int k = 0; k < hp.Length; k++) y = hp[k].Process(y);
                    if (i >= from) { hi += (double)y * y; all += (double)x[i] * x[i]; }
                }
            }
            totalPower = all / (2.0 * (to - from));
            return hi / (2.0 * (to - from));
        }

        public readonly record struct Figures(double BurstDb, double HfOnsetOverSteadyDb, double HfOnsetReChordDb, double HfSteadyReChordDb, double HfOnsetNoClickDb, double BurstPeak, double SteadyPeak);

        /// <summary>The floor under the 30 - 80 ms window's power above 6 kHz: 60 dB under the chord's whole power there. A tonewheel
        /// chord with no drive has next to nothing above 6 kHz (its top wheel is 5.9 kHz): 105 to 135 dB under the chord. Against that,
        /// any start at all (the tone's own 6 ms fade-in, with the click at 0, reads 75 to 85 dB under the chord) would count as
        /// "hash", so the comparison starts at a floor that is still far below hearing.</summary>
        public const double FloorDb = -60;

        /// <summary>
        /// BurstDb: the click's peak (the difference of the two renders, first 40 ms) against the chord's steady peak (300 - 500 ms,
        /// without the click). HfOnsetOverSteadyDb: power above 6 kHz in the first 10 ms of the chord (with the click) over the
        /// same in its 30 - 80 ms window, or over <see cref="FloorDb"/> under the chord's power there if that is higher. The re-chord
        /// figures put each window's power above 6 kHz against the chord's whole power in that window (no floor).
        /// </summary>
        public static Figures Of(Dictionary<int, int> preset)
        {
            var (l, r) = RenderChord(preset, true);
            var (ql, qr) = RenderChord(preset, false);
            var dl = new float[Length];
            var dr = new float[Length];
            for (int i = 0; i < Length; i++) { dl[i] = l[i] - ql[i]; dr[i] = r[i] - qr[i]; }
            int ms = Rate / 1000;
            double burst = Peak(dl, dr, 0, 40 * ms), steady = Peak(ql, qr, 300 * ms, 500 * ms);
            double onset = HighBandPower(l, r, 0, 10 * ms, out double onsetAll);
            double later = HighBandPower(l, r, 30 * ms, 80 * ms, out double laterAll);
            double qOnset = HighBandPower(ql, qr, 0, 10 * ms, out _), qLater = HighBandPower(ql, qr, 30 * ms, 80 * ms, out double qLaterAll);
            double floor = laterAll * Math.Pow(10, FloorDb / 10), qFloor = qLaterAll * Math.Pow(10, FloorDb / 10);
            return new Figures(
                Measure.Db(burst / steady),
                10 * Math.Log10((onset + 1e-30) / (Math.Max(later, floor) + 1e-30)),
                10 * Math.Log10((onset + 1e-30) / (onsetAll + 1e-30)),
                10 * Math.Log10((later + 1e-30) / (laterAll + 1e-30)),
                10 * Math.Log10((qOnset + 1e-30) / (Math.Max(qLater, qFloor) + 1e-30)),
                burst, steady);
        }

        /// <summary>The table for the report and the listening pack's README.</summary>
        public static string Table()
        {
            var sb = new StringBuilder("preset\tKey click\tclick peak re steady peak (dB)\tlimit\t>6 kHz 0-10 ms over 30-80 ms, floor -60 dB re chord (dB)\t>6 kHz re chord 0-10 ms / 30-80 ms (dB)\tsame with Key click 0 (dB)\n");
            foreach (var (name, values) in Presets())
            {
                var f = Of(values);
                values.TryGetValue(P.KeyClick, out int kc);
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{name}\t{kc / 10.0:0}%\t{f.BurstDb:0.0}\t{BurstLimitDb(name):0}\t{f.HfOnsetOverSteadyDb:+0.0;-0.0}\t{f.HfOnsetReChordDb:0.0} / {f.HfSteadyReChordDb:0.0}\t{f.HfOnsetNoClickDb:+0.0;-0.0}"));
            }
            return sb.ToString();
        }
    }

    [TestFixture]
    public class OrganClickTable
    {
        /// <summary>Prints the per-preset click figures (for the report's before/after table); the gates are in OrganTests.</summary>
        [Test, Explicit("prints the per-preset click table")]
        public void PrintTheClickTable() => TestContext.Progress.WriteLine(OrganClickFigures.Table());
    }
}
