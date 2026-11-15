// Versioned machine state (docs/specs/machines.md §3.4): the migration chain on a test machine with three versions (reorder, rename and
// rescale, removal), the saved envelope, and the guard that no machine's layout changes without a version: every machine's current table
// is fingerprinted in Layouts/state-layouts.txt (index, key, range, mapping), and a changed table fails until a version and its line are added.

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Buzz.MachineInterface;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    public class StateVersionTests
    {
        // ------------------------------------------------------------------------------------------ a test machine with three versions
        //
        // v0 (the layout songs had): level 0…100, freq 0…1000, mode 0…2, old_pan 0…10
        // v1 (reorder):              freq, level, mode, old_pan
        // v2 (rename + rescale):     cutoff (was freq), gain (was level, now 0…1000), mode, old_pan
        // v3 (removal, current):     cutoff, gain, mode           (old_pan dropped; its data element is dropped from the blob too)

        static ParamDef D(int i, string id, int min, int max, int def) => new ParamDef(i, id, id, min, max, def, MapKind.Linear, a: 0, b: 1);

        static readonly ParamDef[] T0 = { D(0, "level", 0, 100, 50), D(1, "freq", 0, 1000, 500), D(2, "mode", 0, 2, 0), D(3, "old_pan", 0, 10, 5) };
        static readonly ParamDef[] T1 = { D(0, "freq", 0, 1000, 500), D(1, "level", 0, 100, 50), D(2, "mode", 0, 2, 0), D(3, "old_pan", 0, 10, 5) };
        static readonly ParamDef[] T2 = { D(0, "cutoff", 0, 1000, 500), D(1, "gain", 0, 1000, 500), D(2, "mode", 0, 2, 0), D(3, "old_pan", 0, 10, 5) };
        static readonly ParamDef[] T3 = { D(0, "cutoff", 0, 1000, 500), D(1, "gain", 0, 1000, 500), D(2, "mode", 0, 2, 0) };
        static readonly ParamDef[][] Tables = { T0, T1, T2, T3 };

        static string[] Keys(ParamDef[] t) => t.Select(p => p.Id).ToArray();

        static StateMigrations Chain() => new StateMigrations(T3)
            .Step(Keys(T0), s => s.Data(d => Replace(d, "<Pan>", "<Pan>"))) // v0 -> v1: a pure reorder (no values change)
            .Step(Keys(T1), s => s
                .Rename("freq", "cutoff")
                .Rename("level", "gain")
                .Rescale("gain", 0, 100, 0, 1000)
                .Data(d => Replace(Replace(d, "<Level>", "<Gain>"), "</Level>", "</Gain>")))
            .Step(Keys(T2), s => s
                .Remove("old_pan")
                .Data(d => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(d).Replace("<Pan>5</Pan>", ""))));

        static byte[] Replace(byte[] d, string a, string b) => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(d).Replace(a, b));

        /// <summary>What a song saved at <paramref name="version"/> holds for the settings: cutoff 0.8, gain 0.25, mode 2 (and a pan, until v3).</summary>
        static int[] SavedAt(int version)
        {
            var t = Tables[version];
            var r = new int[t.Length];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = t[i].Id switch
                {
                    "freq" or "cutoff" => 800,
                    "level" => 25,
                    "gain" => 250,
                    "mode" => 2,
                    "old_pan" => 9,
                    _ => throw new InvalidOperationException(t[i].Id),
                };
            }
            return r;
        }

        [Test]
        public void ChainIsValid()
        {
            var c = Chain();
            Assert.That(c.Validate(), Is.Empty);
            Assert.That(c.Current, Is.EqualTo(3));
            Assert.That(c.Keys, Is.EqualTo(new[] { "cutoff", "gain", "mode" }));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void SongSavedAtEachVersionLoadsToTheSameSound(int version)
        {
            var c = Chain();
            var now = c.MigrateGlobals(version, SavedAt(version));
            Assert.That(now, Is.EqualTo(new[] { 800, 250, 2 }), "cutoff 0.8, gain 0.25, mode 2 at v" + version);
            // The same sound in physical terms: every value reads the same through the current table.
            Assert.That(T3[0].Value(now[0]), Is.EqualTo(0.8).Within(1e-9));
            Assert.That(T3[1].Value(now[1]), Is.EqualTo(0.25).Within(1e-9));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void KeyedApiAgrees(int version)
        {
            var c = Chain();
            var layout = c.Layout(version);
            var saved = SavedAt(version);
            var byKey = new Dictionary<string, int>();
            for (int i = 0; i < layout.Length; i++) byKey[layout[i]] = saved[i];
            var r = c.Migrate(version, byKey);
            Assert.That(r["cutoff"], Is.EqualTo(800));
            Assert.That(r["gain"], Is.EqualTo(250));
            Assert.That(r["mode"], Is.EqualTo(2));
            Assert.That(r.Count, Is.EqualTo(3));
        }

        [Test]
        public void DataIsMigratedStepByStep()
        {
            var c = Chain();
            string v0 = "<S><Level>7</Level><Pan>5</Pan></S>";
            string now = Encoding.UTF8.GetString(c.MigrateData(0, Encoding.UTF8.GetBytes(v0)));
            Assert.That(now, Is.EqualTo("<S><Gain>7</Gain></S>"));
            // A song saved at v2 has already renamed Level; it only loses the pan.
            now = Encoding.UTF8.GetString(c.MigrateData(2, Encoding.UTF8.GetBytes("<S><Gain>7</Gain><Pan>5</Pan></S>")));
            Assert.That(now, Is.EqualTo("<S><Gain>7</Gain></S>"));
            // The current version's data is left alone, and a song with no data stays without.
            var same = Encoding.UTF8.GetBytes(v0);
            Assert.That(c.MigrateData(3, same), Is.SameAs(same));
            Assert.That(c.MigrateData(0, Array.Empty<byte>()), Is.Empty);
        }

        [Test]
        public void ValuesNotSavedKeepTheDefaultsAndAreClamped()
        {
            var c = Chain();
            // A v0 song saved only level and freq (a shorter machine): mode gets its default.
            var r = c.MigrateGlobals(0, new[] { 100, 1000 });
            Assert.That(r, Is.EqualTo(new[] { 1000, 1000, 0 }));
            // NotSaved entries are skipped the same way.
            r = c.MigrateGlobals(0, new[] { StateMigrations.NotSaved, 400, 1, 3 });
            Assert.That(r, Is.EqualTo(new[] { 400, T3[1].Default, 1 }));
            // Out of range values land in range (level 250 was never legal; the rescale clamps it).
            r = c.MigrateGlobals(0, new[] { 250, 0, 0, 0 });
            Assert.That(r[1], Is.EqualTo(1000));
        }

        [Test]
        public void NewerOrUnknownVersionsAreRefused()
        {
            var c = Chain();
            Assert.That(c.MigrateGlobals(4, new[] { 1, 2, 3 }), Is.Null);
            Assert.That(c.MigrateGlobals(-1, new[] { 1, 2, 3 }), Is.Null);
            Assert.That(c.MigrateData(4, new byte[1]), Is.Null);
            Assert.That(c.Migrate(9, new Dictionary<string, int>()), Is.Null);
            Assert.That(c.MapIndex(7, 0), Is.EqualTo(-1));
        }

        [Test]
        public void IndexMapFollowsRenamesAndDrops()
        {
            var c = Chain();
            // v0: level, freq, mode, old_pan -> v3: cutoff 0, gain 1, mode 2
            Assert.That(new[] { c.MapIndex(0, 0), c.MapIndex(0, 1), c.MapIndex(0, 2), c.MapIndex(0, 3) }, Is.EqualTo(new[] { 1, 0, 2, -1 }));
            // v1: freq, level, mode, old_pan
            Assert.That(new[] { c.MapIndex(1, 0), c.MapIndex(1, 1), c.MapIndex(1, 2), c.MapIndex(1, 3) }, Is.EqualTo(new[] { 0, 1, 2, -1 }));
            Assert.That(c.MapIndex(3, 2), Is.EqualTo(2));
            Assert.That(c.MapIndex(0, 9), Is.EqualTo(-1));
        }

        [Test]
        public void SplitAndMerge()
        {
            var cur = new[] { D(0, "low", 0, 100, 0), D(1, "high", 0, 100, 0), D(2, "mix", 0, 100, 0) };
            var c = new StateMigrations(cur).Step(new[] { "range", "wet", "dry" }, s => s
                .Split("range", new[] { "low", "high" }, v => new[] { v / 2, v - v / 2 })
                .Merge(new[] { "wet", "dry" }, "mix", v => v[0] - v[1] + 50));
            Assert.That(c.Validate(), Is.Empty);
            Assert.That(c.MigrateGlobals(0, new[] { 80, 70, 20 }), Is.EqualTo(new[] { 40, 40, 100 }));
            Assert.That(c.MapIndex(0, 0), Is.EqualTo(0));
            Assert.That(c.MapIndex(0, 1), Is.EqualTo(2));
            Assert.That(c.MapIndex(0, 2), Is.EqualTo(2));
        }

        [Test]
        public void AddedParameterTakesTheOldBehaviour()
        {
            var cur = new[] { D(0, "a", 0, 10, 3), D(1, "legacy", 0, 1, 0) };
            var c = new StateMigrations(cur).Step(new[] { "a" }, s => s.Add("legacy", 1));
            Assert.That(c.MigrateGlobals(0, new[] { 7 }), Is.EqualTo(new[] { 7, 1 }));
            Assert.That(c.MigrateGlobals(1, new[] { 7, 0 }), Is.EqualTo(new[] { 7, 0 }));
        }

        [Test]
        public void ValidateFindsMistakes()
        {
            var cur = new[] { D(0, "a", 0, 10, 0), D(1, "b", 0, 10, 0) };
            // A rename that names the wrong old key leaves "x" behind.
            var bad = new StateMigrations(cur).Step(new[] { "x", "b" }, s => s.Rename("y", "a"));
            Assert.That(bad.Validate(), Is.Not.Empty);
            // A step that does not start from what the previous one made.
            var gap = new StateMigrations(cur)
                .Step(new[] { "p", "b" }, s => s.Rename("p", "q"))
                .Step(new[] { "a", "b" }, s => { });
            Assert.That(gap.Validate(), Is.Not.Empty);
        }

        [Test]
        public void AMachineWithoutStepsIsVersionZeroAndLoadsAsItDid()
        {
            var cur = new[] { D(0, "a", 0, 10, 3), D(1, "b", 0, 10, 4) };
            var c = new StateMigrations(cur);
            Assert.That(c.Current, Is.EqualTo(0));
            var data = new byte[] { 1, 2, 3 };
            Assert.That(c.MigrateData(0, data), Is.SameAs(data));
            Assert.That(c.MigrateGlobals(0, new[] { 9, 8 }), Is.EqualTo(new[] { 9, 8 }));
            Assert.That(c.MapIndex(0, 1), Is.EqualTo(1));
            Assert.That(c.MigrateGlobals(1, new[] { 9, 8 }), Is.Null);
        }

        // ------------------------------------------------------------------------------------------ the saved envelope

        /// <summary>What MachineManager.GetMachineData wrote before versions: byte 2, the size as 4 bytes, the content.</summary>
        static byte[] Legacy(byte[] data)
        {
            using var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(2), 0, 1);
            int size = data != null ? data.Length : 0;
            ms.Write(BitConverter.GetBytes(size), 0, 4);
            if (data != null) ms.Write(data, 0, size);
            return ms.ToArray();
        }

        [Test]
        public void VersionZeroSavesExactlyWhatItAlwaysDid()
        {
            foreach (var data in new byte[][] { null, Array.Empty<byte>(), new byte[] { 1 }, Encoding.UTF8.GetBytes("<State><A>1</A></State>") })
                Assert.That(StateEnvelope.Write(data, 0), Is.EqualTo(Legacy(data)));
        }

        [Test]
        public void EnvelopeRoundTrips()
        {
            var data = Encoding.UTF8.GetBytes("<State/>");
            var (c0, v0) = StateEnvelope.Read(Legacy(data));
            Assert.That(c0, Is.EqualTo(data));
            Assert.That(v0, Is.EqualTo(0));
            var (c3, v3) = StateEnvelope.Read(StateEnvelope.Write(data, 3));
            Assert.That(c3, Is.EqualTo(data));
            Assert.That(v3, Is.EqualTo(3));
            var (e, ev) = StateEnvelope.Read(StateEnvelope.Write(null, 2));
            Assert.That(e, Is.Empty);
            Assert.That(ev, Is.EqualTo(2));
            // Nothing, or too little, to say anything: empty and version 0.
            Assert.That(StateEnvelope.Read(null), Is.EqualTo((Array.Empty<byte>(), 0)));
            Assert.That(StateEnvelope.Read(new byte[] { 2, 1 }).Version, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------------------------------ every machine's layout is fingerprinted

        /// <summary>Every machine's Params table, by the namespace of its machine.</summary>
        static SortedDictionary<string, ParamDef[]> AllTables()
        {
            var r = new SortedDictionary<string, ParamDef[]>(StringComparer.Ordinal);
            foreach (var t in typeof(ParamDef).Assembly.GetTypes().Where(t => t.Name == "Params" && t.Namespace != null && t.Namespace.StartsWith("NeoBuzz.")))
            {
                var table = t.GetField("Table", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as ParamDef[];
                if (table != null) r[t.Namespace] = table;
            }
            return r;
        }

        static string Fingerprint(ParamDef[] table, int count = -1)
        {
            var sb = new StringBuilder();
            foreach (var p in table.Take(count < 0 ? table.Length : count))
                sb.Append(p.Index).Append('|').Append(p.Id).Append('|').Append(p.Min).Append('|').Append(p.Max).Append('|').Append(p.Default).Append('|')
                  .Append(p.Kind).Append('|').Append(p.A.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                  .Append(p.B.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                  .Append(p.Exponent.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(p.Flag).Append('\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 16).ToLowerInvariant();
        }

        static string LayoutFile => Path.Combine(TestContext.CurrentContext.TestDirectory, "Layouts", "state-layouts.txt");

        /// <summary>namespace → (version, count, fingerprint) lines, oldest first.</summary>
        static Dictionary<string, List<(int Version, int Count, string Print)>> Recorded()
        {
            var r = new Dictionary<string, List<(int, int, string)>>();
            foreach (var line in File.ReadAllLines(LayoutFile))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('\t');
                if (!r.TryGetValue(f[0], out var l)) r[f[0]] = l = new List<(int, int, string)>();
                l.Add((int.Parse(f[1]), int.Parse(f[2]), f[3]));
            }
            return r;
        }

        [Test]
        public void EveryMachineHasUniqueKeys()
        {
            var tables = AllTables();
            Assert.That(tables.Count, Is.GreaterThan(40));
            foreach (var (ns, table) in tables)
            {
                Assert.That(table.All(p => !string.IsNullOrEmpty(p.Id)), Is.True, ns + " has a parameter without an id");
                Assert.That(table.Select(p => p.Id).Distinct().Count(), Is.EqualTo(table.Length), ns + " has duplicate ids");
                for (int i = 0; i < table.Length; i++) Assert.That(table[i].Index, Is.EqualTo(i), ns + " index " + i);
            }
        }

        [Test]
        public void EveryMachineLayoutIsRecordedAndOnlyChangesWithAVersion()
        {
            if (Environment.GetEnvironmentVariable("NEO_WRITE_LAYOUTS") is { Length: > 0 } outPath)
            {
                var sb = new StringBuilder("# Every Neo machine's current parameter table, fingerprinted (StateVersionTests; docs/specs/machines.md §3.4).\n" +
                                           "# namespace<TAB>state version<TAB>parameters<TAB>fingerprint. The last line of a namespace is the table as it is now (parameters appended since are not yet in it: appending needs no version).\n" +
                                           "# Changing a table (order, key, range, mapping, default) without adding its next version and line fails EveryMachineLayoutIsRecordedAndOnlyChangesWithAVersion.\n");
                foreach (var (ns, table) in AllTables()) sb.Append(ns).Append('\t').Append(0).Append('\t').Append(table.Length).Append('\t').Append(Fingerprint(table)).Append('\n');
                File.WriteAllText(outPath, sb.ToString());
                Assert.Inconclusive("written to " + outPath);
            }

            var recorded = Recorded();
            var problems = new List<string>();
            foreach (var (ns, table) in AllTables())
            {
                if (!recorded.TryGetValue(ns, out var lines)) { problems.Add(ns + ": no line in state-layouts.txt (a new machine adds its version 0 line)"); continue; }
                var last = lines[^1];
                // Appending parameters at the end never moves what songs saved, so it needs no version: only the recorded prefix is guarded.
                if (table.Length < last.Count || Fingerprint(table, last.Count) != last.Print)
                    problems.Add($"{ns}: the first {last.Count} parameters changed (now {Fingerprint(table, Math.Min(last.Count, table.Length))} of {table.Length}) but its last line is version {last.Version} ({last.Print}). " +
                                 "Songs store values by position: add a migration step, bump the machine's version and add its line (machines.md §3.4).");
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i].Version != i) problems.Add($"{ns}: versions in state-layouts.txt must run 0, 1, 2 … (line {i} says {lines[i].Version})");
            }
            foreach (var ns in recorded.Keys)
                if (!AllTables().ContainsKey(ns)) problems.Add(ns + ": in state-layouts.txt but no machine has it");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }
    }
}
