using System.Security.Cryptography;
using NeoBuzz.Drive;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// EXT3 changes nothing for what existed: the five original Modes, at every quality and across Drive, Bias, Character, tone, Mix, Amount and
    /// block size, render the same bits as on origin/main. The hashes in Golden/drive-baseline.txt were written from origin/main before the
    /// five new Modes existed; no audio is stored. Regenerate only on purpose: NEO_WRITE_DRIVE_GOLDEN=1 dotnet test --filter DriveUnchanged.
    /// </summary>
    [TestFixture]
    public class DriveUnchangedTests
    {
        static string GoldenFile => Path.Combine(TestContext.CurrentContext.TestDirectory, "Golden", "drive-baseline.txt");

        static float[] Source()
        {
            var drums = FxGoldenTests.DrumLoop(Measure.Rate, 0.5);
            var chord = FxGoldenTests.Chord(Measure.Rate, 0.5);
            var pink = Measure.PinkNoise(drums.Length, 0.15);
            var x = new float[drums.Length];
            for (int i = 0; i < x.Length; i++) x[i] = 0.5f * drums[i] + 0.5f * chord[i] + pink[i];
            return x;
        }

        static string Render(ParamValues v, float[] x, int block)
        {
            var e = new DriveEngine(v);
            e.SetSampleRate(Measure.Rate);
            var l = new float[DriveEngine.MaxBlock];
            var r = new float[DriveEngine.MaxBlock];
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int pos = 0; pos < x.Length; pos += block)
            {
                int n = Math.Min(block, x.Length - pos);
                Array.Copy(x, pos, l, 0, n);
                for (int i = 0; i < n; i++) r[i] = x[pos + i] * 0.8f;
                e.Process(l, r, n);
                sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(l.AsSpan(0, n)));
                sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(r.AsSpan(0, n)));
            }
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant()[..24];
        }

        static IEnumerable<(string Name, Action<ParamValues> Setup, int Block)> Cases()
        {
            for (int mode = 0; mode <= 4; mode++)
                for (int q = 0; q <= 2; q++)
                    foreach (double db in new[] { 0.0, 18, 36 })
                        foreach (int bias in new[] { 500, 800 })
                        {
                            int m = mode, qq = q, b = bias; double d = db;
                            yield return ($"mode{m} q{qq} drive{d} bias{b}", v =>
                            {
                                v.Set(P.Mode, m); v.Set(P.Quality, qq); v.Set(P.Drive, Params.Table[P.Drive].Raw(d)); v.Set(P.Bias, b);
                            }, 256);
                        }
            for (int mode = 0; mode <= 4; mode++)
                foreach (int ch in new[] { 0, 500, 1000 })
                {
                    int m = mode, c = ch;
                    yield return ($"mode{m} character{c} filters", v =>
                    {
                        v.Set(P.Mode, m); v.Set(P.Character, c); v.Set(P.Drive, 600); v.Set(P.LowCut, 400); v.Set(P.HighCut, 700); v.Set(P.Tone, 800);
                    }, 256);
                    yield return ($"mode{m} character{c} nogain mix amount", v =>
                    {
                        v.Set(P.Mode, m); v.Set(P.Character, c); v.Set(P.Drive, 700); v.Set(P.AutoGain, 0); v.Set(P.Mix, 600); v.Set(P.Amount, 1500); v.Set(P.Output, 600);
                    }, 256);
                    yield return ($"mode{m} character{c} block64", v =>
                    {
                        v.Set(P.Mode, m); v.Set(P.Character, c); v.Set(P.Drive, 500);
                    }, 64);
                    yield return ($"mode{m} character{c} block100 amount", v =>
                    {
                        v.Set(P.Mode, m); v.Set(P.Character, c); v.Set(P.Drive, 800); v.Set(P.Amount, 500);
                    }, 100);
                }
            yield return ("defaults", v => { }, 256);
        }

        [Test]
        public void TheFiveOriginalModesRenderWhatTheyRenderedOnOriginMain()
        {
            var x = Source();
            var now = Cases().Select(c =>
            {
                var v = new ParamValues(Params.Table);
                c.Setup(v);
                return c.Name + "\t" + Render(v, x, c.Block);
            }).ToList();
            if (Environment.GetEnvironmentVariable("NEO_WRITE_DRIVE_GOLDEN") == "1")
            {
                string path = Path.Combine(Path.GetDirectoryName(typeof(DriveUnchangedTests).Assembly.Location)!, "..", "..", "..", "Golden", "drive-baseline.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "# Neo Drive's original five Modes, SHA-256 (first 24 hex) of the float32 render (DriveUnchangedTests). Written from origin/main before EXT3.\n" + string.Join("\n", now) + "\n");
                Assert.Inconclusive("written to " + path);
            }
            var golden = File.ReadAllLines(GoldenFile).Where(l => l.Length > 0 && l[0] != '#').ToDictionary(l => l.Split('\t')[0], l => l.Split('\t')[1]);
            var problems = new List<string>();
            foreach (var line in now)
            {
                var parts = line.Split('\t');
                if (!golden.TryGetValue(parts[0], out var old)) problems.Add(parts[0] + ": not in the golden file");
                else if (old != parts[1]) problems.Add(parts[0] + ": the render changed");
            }
            Assert.That(golden.Count, Is.EqualTo(now.Count));
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
            TestContext.Out.WriteLine($"{now.Count} renders compared");
        }
    }
}
