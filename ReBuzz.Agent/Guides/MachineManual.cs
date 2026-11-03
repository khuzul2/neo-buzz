#nullable disable
// Agent server (docs/specs/mcp-server.md §6, phase A5): machine manuals, served as
// rebuzz://machines/<gear>/manual. The reference part is generated from the gear's manifest
// (every parameter with its unit, range, default and meaning, by group); a curated page
// (Guides/machines/<Gear>.md, embedded) says how it sounds and how to use it; the factory presets
// close it. Pure: the ReBuzz side finds the manifests and preset names of the installed gear and
// calls Resource (or Build).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;

namespace ReBuzz.Agent.Guides
{
    public static class MachineManual
    {
        public const string UriPrefix = "rebuzz://machines/";
        public const string UriSuffix = "/manual";
        const string CuratedPrefix = "ReBuzz.Agent.Guides.machines.";

        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        /// <summary>"Neo Poly" → "neo-poly" (the manual's URI segment).</summary>
        public static string Slug(string gear) =>
            Regex.Replace((gear ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

        /// <summary>The manual's resource URI: rebuzz://machines/neo-poly/manual.</summary>
        public static string UriFor(string gear) => UriPrefix + Slug(gear) + UriSuffix;

        /// <summary>The gear named by a manual URI ("neo-poly", or the gear name escaped), or null when it isn't one.</summary>
        public static string SlugOf(string uri)
        {
            if (uri == null || !uri.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase) || !uri.EndsWith(UriSuffix, StringComparison.OrdinalIgnoreCase)) return null;
            string middle = uri.Substring(UriPrefix.Length, uri.Length - UriPrefix.Length - UriSuffix.Length);
            return middle.Length == 0 ? null : Slug(System.Uri.UnescapeDataString(middle));
        }

        /// <summary>Gear with a curated page (Guides/machines/&lt;Gear&gt;.md).</summary>
        public static IReadOnlyList<string> CuratedGear =>
            typeof(MachineManual).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(CuratedPrefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal))
                .Select(n => n.Substring(CuratedPrefix.Length, n.Length - CuratedPrefix.Length - 3))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

        /// <summary>A gear's curated page ("how it sounds, how to use it"), or null. Matches the name or its slug.</summary>
        public static string Curated(string gear)
        {
            string name = CuratedGear.FirstOrDefault(g => string.Equals(g, gear, StringComparison.OrdinalIgnoreCase))
                          ?? CuratedGear.FirstOrDefault(g => Slug(g) == Slug(gear));
            if (name == null) return null;
            using var stream = typeof(MachineManual).Assembly.GetManifestResourceStream(CuratedPrefix + name + ".md");
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// The manual resource for one installed gear. The ReBuzz side calls this once per gear with
        /// a manifest; <paramref name="presetNames"/> is read each time the manual is (presets saved later show up).
        /// </summary>
        public static ResourceDef Resource(MachineManifest manifest, Func<IEnumerable<string>> presetNames = null) => new()
        {
            Uri = UriFor(manifest.Gear),
            Name = Slug(manifest.Gear) + "-manual",
            Title = manifest.Gear + " manual",
            Description = (manifest.Summary.Length > 0 ? manifest.Summary + " " : "") +
                          "Every parameter with unit, range, default and meaning, how to use it, and its presets.",
            Read = () => Build(manifest, Curated(manifest.Gear), presetNames?.Invoke()),
        };

        /// <summary>The manual with the gear's embedded curated page, if it has one.</summary>
        public static string Build(MachineManifest manifest, IEnumerable<string> presetNames) =>
            Build(manifest, Curated(manifest.Gear), presetNames);

        /// <summary>
        /// The manual as markdown: summary, the curated page (may be null), every parameter by group
        /// (unit, range, default, meaning; numbered groups that repeat, such as operators, are shown
        /// once with the defaults that differ), how to write its values, and the presets.
        /// </summary>
        public static string Build(MachineManifest manifest, string curated, IEnumerable<string> presetNames)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            var presets = (presetNames ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
            var sb = new StringBuilder();
            sb.Append("# ").Append(manifest.Gear).Append(" manual\n\n");
            if (manifest.Summary.Length > 0) sb.Append(manifest.Summary).Append("\n\n");
            var facts = new List<string>();
            if (manifest.Kind.Length > 0) facts.Add(manifest.Kind == "generator" ? "Generator (makes sound)" : manifest.Kind == "effect" ? "Effect (processes its input)" : manifest.Kind);
            facts.Add(manifest.IsDraft
                ? $"{manifest.Parameters.Count} parameters in units from a profiled draft manifest (tools/machine-profiler.py, not curated: check by ear)"
                : $"{manifest.Parameters.Count} parameters in exact units");
            if (manifest.Tracks > 0) facts.Add($"{manifest.Tracks} tracks: one note each (Note and Velocity per track), played from patterns as MIDI");
            facts.Add(presets.Count == 1 ? "1 preset" : $"{presets.Count} presets");
            sb.Append(string.Join(" · ", facts)).Append(".\n\n");
            sb.Append($"Read the live values with `machine_describe(machine=…)`; set them with `param_set`. Values below are what `param_set` takes.\n\n");

            if (!string.IsNullOrWhiteSpace(curated))
                sb.Append(curated.Trim()).Append("\n\n");

            sb.Append("## Parameters\n\n");
            AppendParameters(sb, manifest);

            sb.Append("## Writing values\n\n");
            AppendValueForms(sb, manifest);

            sb.Append("## Factory presets\n\n");
            if (presets.Count == 0) sb.Append("None listed. `preset_list(machine=…)` shows what the installed gear has.\n");
            else
            {
                foreach (var p in presets) sb.Append("- ").Append(p).Append('\n');
                sb.Append($"\nLoad one with `preset_load(machine=…, preset=\"{presets[0]}\")`, then adjust with `param_set`. `preset_list(machine=…)` also shows presets saved since.\n");
            }
            return sb.ToString();
        }

        // ---- Parameters ------------------------------------------------------------------------

        sealed class Group
        {
            public string Name;
            public List<ManifestParameter> Parameters;
        }

        static void AppendParameters(StringBuilder sb, MachineManifest manifest)
        {
            var groups = new List<Group>();
            foreach (var p in manifest.Parameters)
            {
                string name = p.Group.Length > 0 ? p.Group : "Parameters";
                var g = groups.LastOrDefault();
                if (g == null || g.Name != name) groups.Add(g = new Group { Name = name, Parameters = new List<ManifestParameter>() });
                g.Parameters.Add(p);
            }
            for (int i = 0; i < groups.Count; i++)
            {
                var family = Family(groups, i);
                var first = groups[i];
                if (family.Count > 1)
                {
                    var last = family[^1];
                    sb.Append("### ").Append(first.Name).Append(" … ").Append(last.Name).Append("\n\n");
                    sb.Append($"{family.Count} groups with the same parameters; the table shows {first.Name}. " +
                              $"The others are named the same way with their own number ({string.Join(", ", family.Skip(1).Select(f => Quote(f.Parameters[0].Name)))} …).\n\n");
                    AppendTable(sb, first.Parameters);
                    var differ = new List<string>();
                    foreach (var other in family.Skip(1))
                    {
                        var changed = other.Parameters.Where((p, k) => p.Default != first.Parameters[k].Default).Select(p => $"{p.Name} {Format(p, p.Default)}").ToList();
                        if (changed.Count > 0) differ.Add($"{other.Name}: {string.Join(", ", changed)}");
                    }
                    if (differ.Count > 0) sb.Append("Defaults that differ: ").Append(string.Join("; ", differ)).Append(".\n\n");
                    i += family.Count - 1;
                }
                else
                {
                    sb.Append("### ").Append(first.Name).Append("\n\n");
                    AppendTable(sb, first.Parameters);
                }
            }
        }

        static string Quote(string s) => "\"" + s + "\"";

        /// <summary>The groups from <paramref name="start"/> on that repeat its parameters under the next numbers ("Operator 1", "Operator 2" …).</summary>
        static List<Group> Family(List<Group> groups, int start)
        {
            var result = new List<Group> { groups[start] };
            var m = Regex.Match(groups[start].Name, @"^(.*?)(\d+)$");
            if (!m.Success) return result;
            string stem = m.Groups[1].Value;
            int n = int.Parse(m.Groups[2].Value, C);
            for (int i = start + 1; i < groups.Count; i++)
            {
                var mi = Regex.Match(groups[i].Name, @"^(.*?)(\d+)$");
                if (!mi.Success || mi.Groups[1].Value != stem) break;
                int k = int.Parse(mi.Groups[2].Value, C);
                if (k != n + (i - start) || !SameShape(groups[start], n, groups[i], k)) break;
                result.Add(groups[i]);
            }
            return result;
        }

        /// <summary>
        /// Group <paramref name="b"/> (number <paramref name="nb"/>) repeats group <paramref name="a"/>: the same kinds and
        /// ranges, names with the number changed ("Op1 rate 1" → "Op2 rate 1"), descriptions the same or with the number changed.
        /// </summary>
        static bool SameShape(Group a, int na, Group b, int nb)
        {
            if (a.Parameters.Count != b.Parameters.Count) return false;
            var first = new Regex($@"(?<!\d){na}(?!\d)");
            string Renumber(string s) => first.Replace(s, nb.ToString(C), 1);
            for (int i = 0; i < a.Parameters.Count; i++)
            {
                var p = a.Parameters[i];
                var q = b.Parameters[i];
                if (Renumber(p.Name) != q.Name) return false;
                if (p.Description != q.Description && Renumber(p.Description) != q.Description) return false;
                if (p.Mapping.Kind != q.Mapping.Kind || p.Min != q.Min || p.Max != q.Max || p.Mapping.Unit != q.Mapping.Unit ||
                    p.Mapping.A != q.Mapping.A || p.Mapping.B != q.Mapping.B ||
                    !(p.Mapping.Names ?? Array.Empty<string>()).SequenceEqual(q.Mapping.Names ?? Array.Empty<string>()))
                    return false;
            }
            return true;
        }

        static void AppendTable(StringBuilder sb, IReadOnlyList<ManifestParameter> parameters)
        {
            sb.Append("| Parameter | Range | Default | Meaning |\n|---|---|---|---|\n");
            foreach (var p in parameters)
                sb.Append("| ").Append(Cell(p.Name)).Append(" | ").Append(Cell(Range(p))).Append(" | ").Append(Cell(Format(p, p.Default)))
                  .Append(" | ").Append(Cell(p.Description)).Append(" |\n");
            sb.Append('\n');
        }

        static string Cell(string s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ");

        /// <summary>"20 Hz … 20 kHz", "Poly · Mono · Legato", "-inf … +6.0 dB".</summary>
        public static string Range(ManifestParameter p)
        {
            var m = p.Mapping;
            if (m.Kind is MappingKind.Enum or MappingKind.Switch)
                return string.Join(" · ", Enumerable.Range(p.Min, p.Max - p.Min + 1).Select(r => Format(p, r)));
            return $"{Format(p, p.Min)} … {Format(p, p.Max)}";
        }

        // ---- Values ----------------------------------------------------------------------------

        /// <summary>A raw value as text in the parameter's unit, in a form param_set takes back.</summary>
        public static string Format(ManifestParameter p, int raw)
        {
            var m = p.Mapping;
            double v = m.Value(raw, p.Min, p.Max);
            switch (m.Kind)
            {
                case MappingKind.Enum:
                case MappingKind.Switch:
                {
                    int i = Math.Clamp(raw, p.Min, p.Max) - p.Min;
                    return m.Names != null && i < m.Names.Count ? m.Names[i] : i.ToString(C);
                }
                case MappingKind.Db:
                    return double.IsNegativeInfinity(v) ? "-inf dB" : Signed(v, "0.0") + " dB";
                case MappingKind.Percent:
                    return Number(v * 100, 1) + "%";
                case MappingKind.Bipolar:
                    return Signed(v * 100, "0.#") + "%";
                case MappingKind.Ratio:
                    return double.IsPositiveInfinity(v) ? "∞:1" : Number(v, 1) + ":1";
                case MappingKind.Time:
                    return Seconds(v);
                case MappingKind.Freq:
                    return Hz(v);
                case MappingKind.Int:
                {
                    bool signed = m.A < 0;
                    string number = signed ? Signed(v, "0") : v.ToString("0", C);
                    return m.Unit.Length > 0 ? number + " " + m.Unit : number;
                }
                default:
                    return WithUnit(v, m.Unit);
            }
        }

        static string WithUnit(double v, string unit)
        {
            switch (unit)
            {
                case "s": return Seconds(v);
                case "Hz": return Hz(v);
                case "dB": return Signed(v, "0.0") + " dB";
                case "%": return Number(v, 1) + "%";
                case "ct": return Signed(v, "0.#") + " ct";
                case "×": return v.ToString("0.00", C) + "×";
                case "": return Number(v, 2);
                default: return Number(v, 2) + " " + unit;
            }
        }

        static string Signed(double v, string format) =>
            Math.Abs(v) < 1e-9 ? 0.0.ToString(format, C) : v.ToString("+" + format + ";-" + format, C);

        /// <summary>Up to <paramref name="decimals"/> decimals, without trailing zeros.</summary>
        static string Number(double v, int decimals) => Math.Round(v, decimals).ToString("0." + new string('#', Math.Max(1, decimals)), C);

        static string Seconds(double s)
        {
            if (s <= 0) return "0 ms";
            if (s < 0.01) return Number(s * 1000, 2) + " ms";
            if (s < 0.1) return Number(s * 1000, 1) + " ms";
            if (s < 1) return Number(s * 1000, 0) + " ms";
            return Number(s, 2) + " s";
        }

        static string Hz(double hz)
        {
            if (hz >= 1000) return Number(hz / 1000, 2) + " kHz";
            if (hz >= 100) return Number(hz, 0) + " Hz";
            if (hz >= 1) return Number(hz, 1) + " Hz";
            return Number(hz, 2) + " Hz";
        }

        /// <summary>One example per kind of value this machine has, in param_set's change form.</summary>
        static void AppendValueForms(StringBuilder sb, MachineManifest manifest)
        {
            var examples = new List<string>();
            var seen = new HashSet<string>();
            foreach (var p in manifest.Parameters)
            {
                string kind = p.Mapping.Kind is MappingKind.Linear or MappingKind.Exp or MappingKind.Power or MappingKind.Int
                    ? p.Mapping.Kind + p.Mapping.Unit : p.Mapping.Kind.ToString();
                if (!seen.Add(kind)) continue;
                int raw = p.Mapping.Kind is MappingKind.Enum or MappingKind.Switch ? p.Max : p.Min + (int)Math.Round((p.Max - p.Min) * 0.75);
                examples.Add($"{{\"param\": \"{p.Name}\", \"value\": \"{Format(p, raw)}\"}}");
                if (examples.Count >= 6) break;
            }
            sb.Append("Write values with their unit, exactly as the tables show them; `by` changes by an amount (\"+3 dB\", \"-200 Hz\", 1 step of a choice) and `times` scales (2, 0.5). ")
              .Append("min, max and default work everywhere. For example:\n\n");
            sb.Append("`param_set(machine=\"…\", changes=[").Append(string.Join(", ", examples)).Append("])`\n\n");
        }
    }
}
