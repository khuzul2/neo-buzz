#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §3.4): versioned machine state. A machine's StateVersion is the number of steps in its
// chain; step n maps what version n saved onto version n + 1 (rename, reorder, rescale, split, merge, drop with a default, and the
// machine's own data). A song saved at version v is migrated by steps v … Current - 1 when it loads. Version 0 is the layout every
// song had before versions existed (the machine's table as it was), so a machine with no steps loads old songs byte for byte as before.
//
// Values travel by key (ParamDef.Id), never by position, so a reorder needs no code: the layout a step starts from is declared, the
// result is laid out by the current table. All of it runs on the UI/loader thread (a load, an agent call); nothing here is on the audio path.

using System;
using System.Collections.Generic;
using System.Linq;

namespace NeoBuzz.Dsp
{
    /// <summary>One step of a chain, built by <see cref="StateMigrations.Step"/>: operations on the values by key, then the machine's own data.</summary>
    internal sealed class MigrationStep
    {
        internal readonly List<(Action<Dictionary<string, int>> Apply, Func<string, string> MapKey)> Ops = new List<(Action<Dictionary<string, int>>, Func<string, string>)>();
        internal Func<byte[], byte[]> DataMap;

        /// <summary>The parameter is the same one under a new key.</summary>
        public MigrationStep Rename(string oldKey, string newKey)
        {
            Ops.Add((b => { if (b.Remove(oldKey, out int v)) b[newKey] = v; }, k => k == oldKey ? newKey : k));
            return this;
        }

        /// <summary>The same range moved to another: the saved raw value keeps its place in the range.</summary>
        public MigrationStep Rescale(string key, int oldMin, int oldMax, int newMin, int newMax)
        {
            double scale = oldMax == oldMin ? 0 : (newMax - newMin) / (double)(oldMax - oldMin);
            return Map(key, v => newMin + (int)Math.Round((Math.Clamp(v, oldMin, oldMax) - oldMin) * scale));
        }

        /// <summary>Any change to one parameter's saved value.</summary>
        public MigrationStep Map(string key, Func<int, int> f)
        {
            Ops.Add((b => { if (b.TryGetValue(key, out int v)) b[key] = f(v); }, k => k));
            return this;
        }

        /// <summary>The parameter is gone; what a song saved for it is dropped.</summary>
        public MigrationStep Remove(string key)
        {
            Ops.Add((b => b.Remove(key), k => k == key ? null : k));
            return this;
        }

        /// <summary>A new parameter takes this value in an old song (not the table's default, which is for a new machine).</summary>
        public MigrationStep Add(string key, int value)
        {
            Ops.Add((b => b[key] = value, k => k));
            return this;
        }

        /// <summary>One parameter became several: <paramref name="f"/> turns the saved value into one value per new key. The old position maps to the first.</summary>
        public MigrationStep Split(string oldKey, string[] newKeys, Func<int, int[]> f)
        {
            Ops.Add((b =>
            {
                if (!b.Remove(oldKey, out int v)) return;
                var r = f(v);
                for (int i = 0; i < newKeys.Length; i++) b[newKeys[i]] = r[i];
            }, k => k == oldKey ? newKeys[0] : k));
            return this;
        }

        /// <summary>Several parameters became one: <paramref name="f"/> turns their saved values (in the order given) into the new one. Skipped when a source was not saved.</summary>
        public MigrationStep Merge(string[] oldKeys, string newKey, Func<int[], int> f)
        {
            Ops.Add((b =>
            {
                if (oldKeys.Any(k => !b.ContainsKey(k))) return;
                var vals = oldKeys.Select(k => b[k]).ToArray();
                foreach (var k in oldKeys) b.Remove(k);
                b[newKey] = f(vals);
            }, k => Array.IndexOf(oldKeys, k) >= 0 ? newKey : k));
            return this;
        }

        /// <summary>The machine's own data (the XML of its MachineState) from this version's to the next. Empty when the song saved none.</summary>
        public MigrationStep Data(Func<byte[], byte[]> f)
        {
            DataMap = f;
            return this;
        }
    }

    internal sealed class StateMigrations
    {
        readonly ParamDef[] table;
        readonly List<string[]> before = new List<string[]>();
        readonly List<MigrationStep> steps = new List<MigrationStep>();

        /// <param name="table">The machine's current table: the layout of <see cref="Current"/>.</param>
        public StateMigrations(ParamDef[] table)
        {
            this.table = table;
            Keys = Array.AsReadOnly(table.Select(p => p.Id).ToArray());
        }

        /// <summary>In an array of saved values: this one was not saved (the default stands).</summary>
        public const int NotSaved = int.MinValue;

        /// <summary>The keys of the current layout, in index order.</summary>
        public IReadOnlyList<string> Keys { get; }

        /// <summary>The version this build saves: the number of steps.</summary>
        public int Current => steps.Count;

        /// <summary>
        /// Appends the step from version <see cref="Current"/> to the next. <paramref name="layoutBefore"/> is the keys, in order, of the
        /// layout it starts from (copy them from the table before you change it: they are what songs saved at that version contain).
        /// </summary>
        public StateMigrations Step(string[] layoutBefore, Action<MigrationStep> ops)
        {
            var s = new MigrationStep();
            ops?.Invoke(s);
            before.Add((string[])layoutBefore.Clone());
            steps.Add(s);
            return this;
        }

        /// <summary>The keys, in order, of the layout version <paramref name="version"/> saved; null when this build does not know that version.</summary>
        public string[] Layout(int version)
        {
            if (version == Current) return Keys.ToArray();
            return version >= 0 && version < Current ? before[version] : null;
        }

        /// <summary>The saved values by key (of <paramref name="from"/>) as the current layout has them. Null for a version newer than this build or below 0.</summary>
        public Dictionary<string, int> Migrate(int from, IReadOnlyDictionary<string, int> saved)
        {
            if (from < 0 || from > Current) return null;
            var bag = new Dictionary<string, int>(saved.Count);
            foreach (var kv in saved) bag[kv.Key] = kv.Value;
            for (int v = from; v < Current; v++)
                foreach (var op in steps[v].Ops) op.Apply(bag);
            var r = new Dictionary<string, int>(table.Length);
            foreach (var p in table)
                r[p.Id] = bag.TryGetValue(p.Id, out int x) ? Math.Clamp(x, p.Min, p.Max) : p.Default;
            return r;
        }

        /// <summary>Values saved in the order of version <paramref name="from"/>, in the current order. Null for a version newer than this build or below 0.</summary>
        public int[] MigrateGlobals(int from, int[] saved)
        {
            var layout = Layout(from);
            if (layout == null) return null;
            var byKey = new Dictionary<string, int>();
            for (int i = 0; i < layout.Length && i < saved.Length; i++)
                if (saved[i] != NotSaved) byKey[layout[i]] = saved[i];
            var m = Migrate(from, byKey);
            var r = new int[table.Length];
            for (int i = 0; i < r.Length; i++) r[i] = m[table[i].Id];
            return r;
        }

        /// <summary>The machine's own data of version <paramref name="from"/> as the current version reads it. Null for a version newer than this build or below 0.</summary>
        public byte[] MigrateData(int from, byte[] data)
        {
            if (from < 0 || from > Current) return null;
            for (int v = from; v < Current; v++)
                if (steps[v].DataMap != null) data = steps[v].DataMap(data ?? Array.Empty<byte>());
            return data;
        }

        /// <summary>Where the parameter at <paramref name="savedIndex"/> of version <paramref name="from"/> is in the current table; -1 when it is gone.</summary>
        public int MapIndex(int from, int savedIndex)
        {
            var layout = Layout(from);
            if (layout == null || savedIndex < 0 || savedIndex >= layout.Length) return -1;
            string key = layout[savedIndex];
            for (int v = from; v < Current && key != null; v++)
                foreach (var op in steps[v].Ops)
                {
                    key = op.MapKey(key);
                    if (key == null) break;
                }
            return key == null ? -1 : Array.IndexOf(Keys.ToArray(), key);
        }

        /// <summary>What is wrong with the chain (empty: nothing). Run by the tests of every machine that has steps.</summary>
        public IList<string> Validate()
        {
            var problems = new List<string>();
            if (table.Any(p => string.IsNullOrEmpty(p.Id))) problems.Add("a parameter has no id");
            if (Keys.Distinct().Count() != Keys.Count) problems.Add("the table has duplicate ids");
            HashSet<string> carried = null;
            for (int v = 0; v < Current; v++)
            {
                var start = before[v];
                if (start.Distinct().Count() != start.Length) problems.Add($"version {v}: duplicate keys in its layout");
                if (carried != null && !carried.IsSubsetOf(start)) problems.Add($"version {v}: its layout lacks what step {v - 1} produces: {string.Join(",", carried.Except(start).Take(5))} (a key a step left behind: rename or remove it)");
                try
                {
                    var bag = start.ToDictionary(k => k, k => 0);
                    foreach (var op in steps[v].Ops) op.Apply(bag);
                    carried = new HashSet<string>(bag.Keys);
                }
                catch (Exception e)
                {
                    problems.Add($"step {v} fails on a full layout: {e.Message}");
                    carried = null;
                }
            }
            if (carried != null && !carried.IsSubsetOf(Keys))
                problems.Add($"the last step leaves keys the current table does not have: {string.Join(",", carried.Except(Keys).Take(5))} (rename or remove them)");
            return problems;
        }
    }
}
