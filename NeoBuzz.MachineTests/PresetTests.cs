using System.Runtime.CompilerServices;
using System.Xml.Linq;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Factory presets (docs/specs/machines.md §3): every preset file names real parameters at
    /// their positions, in range, and every preset makes a sensible sound through its engine.
    /// </summary>
    [TestFixture]
    public class PresetTests
    {
        static string MachinesDir([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Machines"));

        /// <summary>Machine display name → parameter table and a renderer (pink noise in, output out).</summary>
        internal static readonly Dictionary<string, (ParamDef[] Table, Func<ParamValues, float[], float[]> Render)> Machines = new()
        {
            ["Neo Drive"] = (NeoBuzz.Drive.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Drive(v).Process(l, r, n))),
            ["Neo Comp"] = (NeoBuzz.Comp.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Comp(v).Process(l, r, null, null, n))),
            ["Neo Verb"] = (NeoBuzz.Verb.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Verb(v).Process(l, r, n))),
        };

        // One engine per preset render (created lazily by the closures above).
        static NeoBuzz.Drive.DriveEngine driveEngine; static ParamValues driveValues;
        static NeoBuzz.Comp.CompEngine compEngine; static ParamValues compValues;
        static NeoBuzz.Verb.VerbEngine verbEngine; static ParamValues verbValues;

        static NeoBuzz.Drive.DriveEngine Drive(ParamValues v)
        {
            if (!ReferenceEquals(v, driveValues)) { driveValues = v; driveEngine = new NeoBuzz.Drive.DriveEngine(v); driveEngine.SetSampleRate(Measure.Rate); }
            return driveEngine;
        }

        static NeoBuzz.Comp.CompEngine Comp(ParamValues v)
        {
            if (!ReferenceEquals(v, compValues)) { compValues = v; compEngine = new NeoBuzz.Comp.CompEngine(v); compEngine.SetSampleRate(Measure.Rate); }
            return compEngine;
        }

        static NeoBuzz.Verb.VerbEngine Verb(ParamValues v)
        {
            if (!ReferenceEquals(v, verbValues)) { verbValues = v; verbEngine = new NeoBuzz.Verb.VerbEngine(v); verbEngine.SetSampleRate(Measure.Rate); }
            return verbEngine;
        }

        static float[] RenderEffect(float[] x, Action<float[], float[], int> process)
        {
            var y = new float[x.Length];
            var l = new float[256];
            var r = new float[256];
            for (int pos = 0; pos < x.Length; pos += 256)
            {
                int n = Math.Min(256, x.Length - pos);
                Array.Copy(x, pos, l, 0, n);
                Array.Copy(x, pos, r, 0, n);
                process(l, r, n);
                Array.Copy(l, 0, y, pos, n);
            }
            return y;
        }

        internal static IEnumerable<(string File, string Machine, string Preset, Dictionary<int, int> Values)> AllPresets()
        {
            foreach (var file in Directory.GetFiles(MachinesDir(), "*.prs.xml", SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(Path.GetDirectoryName(file)!).StartsWith("Neo")) continue;
                var doc = XDocument.Load(file);
                foreach (var item in doc.Root!.Elements("Item"))
                {
                    var preset = item.Element("Preset")!;
                    var values = new Dictionary<int, int>();
                    foreach (var p in preset.Element("Parameters")!.Elements("Parameter"))
                    {
                        int index = (int)p.Attribute("Index")!;
                        values[index] = (int)p.Attribute("Value")!;
                        // Name check happens in the test (needs the table).
                        values[-1 - index] = 0;
                    }
                    yield return (file, (string)preset.Attribute("Machine")!, (string)item.Attribute("Key")!, values);
                }
            }
        }

        [Test]
        public void PresetFilesMatchTheirMachines()
        {
            int count = 0;
            foreach (var file in Directory.GetFiles(MachinesDir(), "*.prs.xml", SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(Path.GetDirectoryName(file)!).StartsWith("Neo")) continue;
                var doc = XDocument.Load(file);
                foreach (var item in doc.Root!.Elements("Item"))
                {
                    var preset = item.Element("Preset")!;
                    string machine = (string)preset.Attribute("Machine")!;
                    Assert.That(Machines.ContainsKey(machine), Is.True, $"{file}: unknown machine {machine}");
                    Assert.That(Path.GetFileName(file), Is.EqualTo(machine + ".prs.xml"), "ReBuzz finds presets by the DLL's name");
                    var table = Machines[machine].Table;
                    foreach (var p in preset.Element("Parameters")!.Elements("Parameter"))
                    {
                        int index = (int)p.Attribute("Index")!;
                        int value = (int)p.Attribute("Value")!;
                        Assert.That(index, Is.InRange(0, table.Length - 1));
                        Assert.That((string)p.Attribute("Name")!, Is.EqualTo(table[index].Name), $"{item.Attribute("Key")}: parameter {index}");
                        Assert.That(value, Is.InRange(table[index].Min, table[index].Max), $"{item.Attribute("Key")}: {table[index].Name}");
                    }
                    count++;
                }
            }
            TestContext.Out.WriteLine($"{count} presets");
            Assert.That(count, Is.GreaterThan(30));
        }

        [Test]
        public void EveryEffectPresetSoundsSensible()
        {
            var x = Measure.PinkNoise(Measure.Rate, DspMath.DbToGain(-18));
            double inRms = Measure.Rms(x, Measure.Rate / 2, Measure.Rate / 2);
            foreach (var (_, machine, name, values) in AllPresets())
            {
                var (table, render) = Machines[machine];
                var v = new ParamValues(table);
                foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                var y = render(v, x);
                double rms = Measure.Rms(y, Measure.Rate / 2, Measure.Rate / 2);
                double peak = Measure.Peak(y, 0, y.Length);
                double db = Measure.Db(rms / inRms);
                TestContext.Out.WriteLine($"{machine} / {name}: {db:+0.0;−0.0} dB, peak {Measure.Db(peak):+0.0;−0.0} dBFS");
                Assert.That(double.IsFinite(peak), Is.True, name);
                Assert.That(db, Is.InRange(-24.0, 12.0), $"{machine} / {name}");
                Assert.That(peak, Is.LessThan(1.5), $"{machine} / {name}: clips");
            }
        }
    }
}
