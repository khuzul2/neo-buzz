#nullable disable
// Agent server (docs/specs/mcp-server.md §12.3, §12.11, U4): factory presets for the curated machines,
// written in their manifests' units and compiled to ReBuzz's preset format. A source file beside the
// manifest (Machines/Manifests/<Generators|Effects>/<Gear>.presets.txt, the Neo machines' Presets.txt
// format) names each preset and sets parameters by name or curated plain name, values as param_set
// takes them ("800 Hz", "-6 dB", "35%", a choice); unset parameters keep their defaults. It compiles
// through the manifest with param_set's own parser (ValueResolver) into "<Gear>.prs.xml", which the
// builds put beside the machine, where preset_load, the preset bars and the parameter window find it.
// A value outside a parameter's safe range (machine-safety.md §3.4) is refused, as param_set refuses it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using ReBuzz.Agent.Guides;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.Model
{
    public sealed class FactoryPreset
    {
        public required string Name { get; init; }
        public string Comment { get; set; } = "";
        /// <summary>The values it sets, by manifest entry (track parameters: the first track).</summary>
        public Dictionary<ManifestParameter, int> Values { get; } = new();
    }

    public static class FactoryPresets
    {
        public const string SourceSuffix = ".presets.txt";
        public const string FileSuffix = ".prs.xml";

        /// <summary>A manifest entry as param_set reads it, without a machine: the manifest's own texts and mapping.</summary>
        static ParamInfo Info(ManifestParameter p) => new()
        {
            Name = p.Name,
            Index = p.Index,
            Group = p.Group,
            Description = p.Description,
            Min = p.Min,
            Max = p.Max,
            Default = p.Default,
            Mapping = p.Mapping,
            TextsFirst = true,
            Describe = raw => MachineManual.Format(p, raw),
            SafeRange = p.SafeRange,
        };

        /// <summary>
        /// Reads a source file against the gear's manifest. Throws FormatException naming the line for an unknown
        /// parameter, a value param_set wouldn't take or outside a safe range, a missing or other machine, a repeated
        /// preset or parameter, and a preset that sets nothing.
        /// </summary>
        public static List<FactoryPreset> Parse(string text, MachineManifest manifest, string file = "presets")
        {
            var presets = new List<FactoryPreset>();
            FactoryPreset current = null;
            string machine = null;
            int n = 0;
            foreach (var rawLine in (text ?? "").Split('\n'))
            {
                n++;
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string Where() => $"{file}:{n}";
                var quoted = Regex.Match(line, "^(machine|preset|comment)\\s+\"(.*)\"$");
                if (quoted.Success)
                {
                    string value = quoted.Groups[2].Value;
                    switch (quoted.Groups[1].Value)
                    {
                        case "machine":
                            if (value != manifest.Gear) throw new FormatException($"{Where()}: the manifest is {manifest.Gear}'s, not {value}'s.");
                            machine = value;
                            break;
                        case "preset":
                            if (machine == null) throw new FormatException($"{Where()}: machine \"<gear>\" comes first.");
                            if (current != null && current.Values.Count == 0) throw new FormatException($"{Where()}: preset \"{current.Name}\" sets nothing.");
                            if (presets.Any(p => p.Name == value)) throw new FormatException($"{Where()}: a second preset \"{value}\".");
                            current = new FactoryPreset { Name = value };
                            presets.Add(current);
                            break;
                        default:
                            if (current == null) throw new FormatException($"{Where()}: a comment belongs to a preset.");
                            current.Comment = value;
                            break;
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || current == null) throw new FormatException($"{Where()}: expected \"Parameter = value\" inside a preset.");
                string name = line.Substring(0, eq).Trim();
                string text2 = line.Substring(eq + 1).Trim();
                var entry = Find(manifest, name) ?? throw new FormatException($"{Where()}: {manifest.Gear} has no parameter \"{name}\".");
                if (current.Values.ContainsKey(entry)) throw new FormatException($"{Where()}: \"{name}\" is set twice in \"{current.Name}\".");
                var info = Info(entry);
                int raw;
                try
                {
                    raw = ValueResolver.Resolve(info, JsonValue.Create(text2), entry.Default);
                }
                catch (ToolError e)
                {
                    throw new FormatException($"{Where()}: {e.Message}");
                }
                if (SafeRange.Refusal(info, raw) is string refused) throw new FormatException($"{Where()}: {refused}");
                current.Values[entry] = raw;
            }
            if (machine == null) throw new FormatException($"{file}: no machine \"<gear>\" line.");
            if (current != null && current.Values.Count == 0) throw new FormatException($"{file}: preset \"{current.Name}\" sets nothing.");
            return presets;
        }

        /// <summary>A parameter by name, by curated plain name, or ("track Volume") a track parameter by either.</summary>
        static ManifestParameter Find(MachineManifest m, string name)
        {
            bool track = name.StartsWith("track ", StringComparison.OrdinalIgnoreCase);
            if (track) name = name.Substring(6).Trim();
            IEnumerable<ManifestParameter> Of(IReadOnlyList<ManifestParameter> list) =>
                list.Where(p => p.Name.Trim() == name || (p.PlainName.Length > 0 && p.PlainName == name))
                    .Concat(list.Where(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase) || string.Equals(p.PlainName, name, StringComparison.OrdinalIgnoreCase)));
            return track ? Of(m.TrackParameters).FirstOrDefault() : Of(m.Parameters).FirstOrDefault() ?? Of(m.TrackParameters).FirstOrDefault();
        }

        /// <summary>
        /// The ".prs.xml" ReBuzz reads (BuzzGUI.Common.Presets.PresetDictionary): every manifest parameter, global then
        /// the first track's, in the machine's order, at the preset's value or its default (so presets of machines whose
        /// parameter names repeat, applied by position, line up); the comment says what it is. UTF-8 with a BOM and CRLF,
        /// as the Neo machines' preset files are.
        /// </summary>
        public static string ToXml(MachineManifest manifest, IEnumerable<FactoryPreset> presets)
        {
            var sb = new StringBuilder();
            void Line(string s) => sb.Append(s).Append("\r\n");
            Line("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            Line("<PresetDictionary>");
            foreach (var preset in presets)
            {
                Line($"  <Item Key={Attr(preset.Name)}>");
                Line($"    <Preset Machine={Attr(manifest.Gear)}>");
                Line("      <Parameters>");
                foreach (var (p, group) in manifest.Parameters.OrderBy(p => p.Index).Select(p => (p, 1)).Concat(manifest.TrackParameters.OrderBy(p => p.Index).Select(p => (p, 2))))
                {
                    int value = preset.Values.TryGetValue(p, out int raw) ? raw : p.Default;
                    Line($"        <Parameter Name={Attr(p.Name)} Group=\"{group}\" Index=\"{p.Index}\" Track=\"0\" Value=\"{value.ToString(CultureInfo.InvariantCulture)}\" />");
                }
                Line("      </Parameters>");
                Line("      <Attributes />");
                Line($"      <Comment>{Escape(preset.Comment)}</Comment>");
                Line("    </Preset>");
                Line("  </Item>");
            }
            Line("</PresetDictionary>");
            return "﻿" + sb;
        }

        static string Attr(string s) => "\"" + Escape(s).Replace("\"", "&quot;") + "\"";

        static string Escape(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
                sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", _ => c.ToString() });
            return sb.ToString();
        }

        /// <summary>A preset file's names and comments (for checks), read as ReBuzz's serializer would.</summary>
        public static List<(string Name, string Comment, List<(string Name, int Group, int Index, int Value)> Values)> Read(string xml)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml.TrimStart('﻿'));
            var list = new List<(string, string, List<(string, int, int, int)>)>();
            foreach (XmlElement item in doc.DocumentElement!.GetElementsByTagName("Item"))
            {
                var preset = (XmlElement)item.GetElementsByTagName("Preset")[0]!;
                var values = new List<(string, int, int, int)>();
                foreach (XmlElement p in preset.GetElementsByTagName("Parameter"))
                    values.Add((p.GetAttribute("Name"), int.Parse(p.GetAttribute("Group"), CultureInfo.InvariantCulture),
                        int.Parse(p.GetAttribute("Index"), CultureInfo.InvariantCulture), int.Parse(p.GetAttribute("Value"), CultureInfo.InvariantCulture)));
                list.Add((item.GetAttribute("Key"), preset.GetElementsByTagName("Comment")[0]?.InnerText ?? "", values));
            }
            return list;
        }
    }
}
