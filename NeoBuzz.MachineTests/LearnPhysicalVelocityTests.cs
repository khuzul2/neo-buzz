using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Learn;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Learn Physical: the level rises with the velocity (docs/specs/notes.md section 8, CU6b). The mallet's contact pulse has spectral nulls
    /// that move with the velocity; the level of a note must not wobble with them. One note at velocities 1 to 127 in steps of 6, each step at least as
    /// loud as the one before, on the default settings and on every factory preset, at three pitches.
    /// </summary>
    [TestFixture]
    public class LearnPhysicalVelocityTests
    {
        const int Rate = Measure.Rate;
        const int Block = 128;

        public static readonly int[] Velocities = Enumerable.Range(0, 22).Select(i => 1 + 6 * i).ToArray();      // 1, 7, ... 127
        static readonly int[] Pitches = { 48, 60, 72 };

        internal static IEnumerable<(string Name, Dictionary<int, int> Values)> Settings()
        {
            yield return ("default", new Dictionary<int, int>());
            foreach (var (_, machine, preset, values) in PresetTests.AllPresets())
                if (machine == "Neo Learn Physical") yield return (preset, values);
        }

        /// <summary>The level (RMS, dB) of one note over the first 0.3 s, as the contract test measures it.</summary>
        internal static double LevelDb(Dictionary<int, int> values, int pitch, int velocity, out float peak)
        {
            TableBank.GetNow(0); TableBank.GetNow(1);
            var table = NeoBuzz.LearnPhys.Params.Table;
            var v = new ParamValues(table);
            foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            var h = new LearnHost(new PhysicalMap(table), v);
            h.SetSampleRate(Rate);
            h.Engine.NoteOn(0, pitch, velocity / 127f);
            int frames = Rate / 2;
            var y = new float[frames];
            var l = new float[Block]; var r = new float[Block];
            for (int pos = 0; pos < frames; pos += Block)
            {
                int n = Math.Min(Block, frames - pos);
                h.Process(l, r, n);
                for (int i = 0; i < n; i++) y[pos + i] = 0.5f * (l[i] + r[i]);
            }
            peak = y.Max(Math.Abs);
            return Measure.Db(Measure.Rms(y, 0, Rate * 3 / 10));
        }

        [Test]
        public void LevelRisesWithVelocity()
        {
            var bad = new List<string>();
            foreach (var (name, values) in Settings())
                foreach (int pitch in Pitches)
                {
                    double prev = double.NegativeInfinity, at30 = 0, at127 = 0;
                    var line = new StringBuilder();
                    foreach (int vel in Velocities)
                    {
                        double db = LevelDb(values, pitch, vel, out _);
                        line.Append($"{db:F1} ");
                        if (db < prev - 1e-9) bad.Add($"{name} @{pitch}: velocity {vel} is {prev - db:F2} dB quieter than the step before");
                        prev = db;
                        if (vel == 31) at30 = db;      // the step nearest 30
                        if (vel == 127) at127 = db;
                    }
                    Measure.Report($"{name} @{pitch}: {line}");
                    if (at127 - at30 < 6.0) bad.Add($"{name} @{pitch}: 30 to 127 spreads only {at127 - at30:F1} dB");
                }
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
        }

        [Test]
        public void AFourNoteChordIsSixDbLouderAtVelocity127Than30()
        {
            // What the plug-in self-test plays (60, 64, 67, 72; the peak of the first 0.3 s), on the default settings and on every preset.
            var bad = new List<string>();
            foreach (var (name, values) in Settings())
            {
                double Peak(int vel)
                {
                    TableBank.GetNow(0); TableBank.GetNow(1);
                    var table = NeoBuzz.LearnPhys.Params.Table;
                    var v = new ParamValues(table);
                    foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                    var h = new LearnHost(new PhysicalMap(table), v);
                    h.SetSampleRate(Rate);
                    foreach (int key in new[] { 60, 64, 67, 72 }) h.Engine.NoteOn(0, key, vel / 127f);
                    var l = new float[Block]; var r = new float[Block];
                    float peak = 0;
                    for (int pos = 0; pos < Rate * 3 / 10; pos += Block)
                    {
                        h.Process(l, r, Block);
                        for (int i = 0; i < Block; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
                    }
                    return peak;
                }
                double span = 20 * Math.Log10(Peak(127) / Math.Max(Peak(30), 1e-9));
                Measure.Report($"{name}: chord peak 30 to 127 {span:F1} dB");
                if (span < 6.0) bad.Add($"{name}: {span:F1} dB");
            }
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
        }

        // ---- the listening pack (explicit; kept local, never committed): CU6B_PACK=folder CU6B_TAG=before|after, 'before' first with the old engine
        static readonly string[][] PackPairs =
        {
            new[] { "Marimba", "Glass" }, new[] { "Kalimba", "Plate" }, new[] { "Pluck", "Harp" }, new[] { "Bell", "default" },
        };

        static (float[] L, float[] R) Ramp(Dictionary<int, int> values, int pitch)
        {
            TableBank.GetNow(0); TableBank.GetNow(1);
            var table = NeoBuzz.LearnPhys.Params.Table;
            var v = new ParamValues(table);
            foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            var h = new LearnHost(new PhysicalMap(table), v);
            h.SetSampleRate(Rate);
            int total = Rate * 26 / 10, step = Rate / 4, hold = Rate / 5;
            var L = new float[total]; var R = new float[total];
            var l = new float[Block]; var r = new float[Block];
            for (int pos = 0; pos < total; pos += Block)
            {
                for (int k = 0; k < 10; k++)
                {
                    if (k * step >= pos && k * step < pos + Block) h.Engine.NoteOn(k, pitch, (1 + 14 * k) / 127f);
                    if (k * step + hold >= pos && k * step + hold < pos + Block) h.Engine.NoteOff(k);
                }
                int n = Math.Min(Block, total - pos);
                h.Process(l, r, n);
                Array.Copy(l, 0, L, pos, n); Array.Copy(r, 0, R, pos, n);
            }
            int fade = Rate * 30 / 1000;
            for (int i = 0; i < fade; i++) { float g = 1f - (float)i / fade; L[total - fade + i] *= g; R[total - fade + i] *= g; }
            return (L, R);
        }

        [Test, Explicit("writes the CU6b listening pack")]
        public void WriteThePack()
        {
            var dir = Environment.GetEnvironmentVariable("CU6B_PACK");
            var tag = Environment.GetEnvironmentVariable("CU6B_TAG");
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(tag)) Assert.Ignore("Set CU6B_PACK and CU6B_TAG (before or after).");
            var work = Path.Combine(dir!, "work"); Directory.CreateDirectory(work);
            var all = Settings().ToList();
            foreach (var pair in PackPairs)
                foreach (var name in pair)
                {
                    var (full, vals) = all.First(x => x.Name.StartsWith(name));
                    var (L, R) = Ramp(vals, 67);
                    var bytes = new byte[(L.Length + R.Length) * 4];
                    Buffer.BlockCopy(L, 0, bytes, 0, L.Length * 4); Buffer.BlockCopy(R, 0, bytes, L.Length * 4, R.Length * 4);
                    File.WriteAllBytes(Path.Combine(work, $"{name}-{tag}.f32"), bytes);
                }
            if (tag != "after") return;
            var readme = new StringBuilder();
            readme.AppendLine("Neo Learn Physical velocity pack (CU6b, #168). 48 kHz, 16-bit stereo WAV, about 10 s each. Generated by NeoBuzz.MachineTests/LearnPhysicalVelocityTests.cs; kept local, never committed.");
            readme.AppendLine();
            readme.AppendLine("Each file has four 2.6 s parts: preset A before, preset A after, preset B before, preset B after. Each part is a velocity ramp on note 67 (G4): ten notes, one every 0.25 s, velocity 1, 15, 29 ... 127 (0.2 s held, 30 ms fade at the part's end). \"Before\" is the engine of origin/main (wave 68), \"after\" this branch. Presets are at their own Output (each \"after\" preset is levelled to the same -22 dB as before, so a difference in level is the velocity law).");
            readme.AppendLine("What to listen for: before, the level wobbles between neighbouring velocities (the mallet's null moves) and the quiet notes can be as loud as the middle ones; after, each note is a little louder than the one before and a harder key is brighter and louder. The sound should stay the same instrument.");
            readme.AppendLine("Bow and Breath (the bow's share) are not in the pack: their level is unchanged by this work, bit for bit.");
            readme.AppendLine("Click detector: the project's (second difference against its local mean) on every part, 0 clicks.");
            readme.AppendLine();
            readme.AppendLine("| # | file | parts | peak dBFS | RMS dB | clicks |");
            readme.AppendLine("|---|---|---|---|---|---|");
            int index = 1;
            foreach (var pair in PackPairs)
            {
                var L = new List<float>(); var R = new List<float>(); int clicks = 0;
                foreach (var name in pair)
                    foreach (var tg in new[] { "before", "after" })
                    {
                        var raw = File.ReadAllBytes(Path.Combine(work, $"{name}-{tg}.f32"));
                        int n = raw.Length / 8; var pl = new float[n]; var pr = new float[n];
                        Buffer.BlockCopy(raw, 0, pl, 0, n * 4); Buffer.BlockCopy(raw, n * 4, pr, 0, n * 4);
                        int c = ClickDetector.Find(pl, pr).Count; clicks += c;
                        Assert.That(c, Is.EqualTo(0), $"{name} {tg}: clicks");
                        L.AddRange(pl); R.AddRange(pr);
                    }
                var al = L.ToArray(); var ar = R.ToArray();
                double peak = Math.Max(Measure.Peak(al, 0, al.Length), Measure.Peak(ar, 0, ar.Length));
                double rl = Measure.Rms(al, 0, al.Length), rr = Measure.Rms(ar, 0, ar.Length);
                string file = $"{index:00}-{pair[0].ToLowerInvariant()}-{pair[1].ToLowerInvariant()}.wav";
                OrganPack.WriteWav(Path.Combine(dir!, file), al, ar);
                readme.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "| {0} | {1} | {2} before, {2} after, {3} before, {3} after | {4:0.0} | {5:0.0} | {6} |", index, file, pair[0], pair[1], Measure.Db(peak), Measure.Db(Math.Sqrt((rl * rl + rr * rr) / 2)), clicks));
                index++;
            }
            File.WriteAllText(Path.Combine(dir!, "README.md"), readme.ToString());
        }
    }
}
