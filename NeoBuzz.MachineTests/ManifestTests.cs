using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Machine manifests (docs/specs/mcp-server.md §8.3): each Neo machine's "&lt;Name&gt;.machine.json"
    /// says exactly what its parameter table does, so the agent server lands values on the right
    /// raw value. A stale manifest (Params.def changed, generator not rerun) fails here.
    /// </summary>
    [TestFixture]
    public class ManifestTests
    {
        static string MachinesDir([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Machines"));

        static readonly Dictionary<MapKind, string> Kinds = new()
        {
            [MapKind.Linear] = "linear", [MapKind.Exp] = "exp", [MapKind.Power] = "power", [MapKind.Db] = "db",
            [MapKind.Time] = "time", [MapKind.Freq] = "freq", [MapKind.Percent] = "percent", [MapKind.Bipolar] = "bipolar",
            [MapKind.Int] = "int", [MapKind.Enum] = "enum", [MapKind.Switch] = "switch", [MapKind.Ratio] = "ratio",
        };

        [Test]
        public void EveryManifestMatchesItsParameterTable()
        {
            foreach (var (gear, (table, _)) in PresetTests.Machines)
            {
                string folder = Directory.GetDirectories(MachinesDir()).Where(PresetTests.IsNeoFamily).Single(d => File.Exists(Path.Combine(d, gear + ".machine.json")));
                var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, gear + ".machine.json")))!.AsObject();
                Assert.That(doc["gear"]!.GetValue<string>(), Is.EqualTo(gear));
                var ps = doc["parameters"]!.AsArray();
                Assert.That(ps.Count, Is.EqualTo(table.Length), $"{gear}: regenerate with python3 tools/gen-machine-params.py --all");
                for (int i = 0; i < table.Length; i++)
                {
                    var d = table[i];
                    var p = ps[i]!.AsObject();
                    string what = $"{gear} {d.Name}";
                    Assert.That(p["name"]!.GetValue<string>(), Is.EqualTo(d.Name), what);
                    Assert.That(p["index"]!.GetValue<int>(), Is.EqualTo(i), what);
                    Assert.That(p["min"]!.GetValue<int>(), Is.EqualTo(d.Min), what);
                    Assert.That(p["max"]!.GetValue<int>(), Is.EqualTo(d.Max), what);
                    Assert.That(p["default"]!.GetValue<int>(), Is.EqualTo(d.Default), what);
                    Assert.That(p["kind"]!.GetValue<string>(), Is.EqualTo(Kinds[d.Kind]), what);
                    Assert.That(p["group"]!.GetValue<string>(), Is.EqualTo(d.Group), what);
                    if (d.Kind is MapKind.Enum)
                        Assert.That(p["choices"]!.AsArray().Select(c => c!.GetValue<string>()), Is.EqualTo(d.Names), what);
                    else if (d.Kind is MapKind.Switch)
                        Assert.That(p["choices"]!.AsArray().Select(c => c!.GetValue<string>()), Is.EqualTo(d.Names ?? new[] { "Off", "On" }), what);
                    else if (d.Kind is not (MapKind.Percent or MapKind.Bipolar))
                    {
                        // Same physical values: the agent reads and writes through these.
                        double a = p["a"]?.GetValue<double>() ?? 0, b = p["b"]?.GetValue<double>() ?? 1, exp = p["exponent"]?.GetValue<double>() ?? 1;
                        bool flag = p["flag"]?.GetValue<bool>() ?? false;
                        Assert.That(a, Is.EqualTo(d.A), what + " a");
                        if (d.Kind != MapKind.Int) Assert.That(b, Is.EqualTo(d.B), what + " b");
                        Assert.That(exp, Is.EqualTo(d.Exponent), what + " exponent");
                        Assert.That(flag, Is.EqualTo(d.Flag), what + " flag");
                    }
                }
            }
        }
    }
}
