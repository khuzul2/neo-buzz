#nullable disable
// The per-instance manifest of a Neo Plug machine (docs/specs/plugin-host.md §3.6): the agent server's
// "rebuzz-machine-manifest/1" built from the plug-in's own parameters, with units read from the plug-in's value texts
// (CLAP has no unit metadata at all, and VST3's is a free string), so machine_describe shows "-6.0 dB" and not 21845.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NeoBuzz.Plug
{
    static class PlugManifest
    {
        static readonly Regex Number = new(@"^\s*([-+−]?\d+(?:[.,]\d+)?)\s*([^\d\s].{0,11})?\s*$", RegexOptions.Compiled);
        static readonly Regex Inf = new(@"^\s*([-+−]?)\s*(?:inf(?:inity)?|∞|oo)\s*([A-Za-z%]{0,4})\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>A value text as a number in a base unit ("1.5 kHz" → 1500 Hz), or null when it isn't one.</summary>
        internal static (JsonNode Value, string Unit)? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var inf = Inf.Match(text);
            if (inf.Success) return (JsonValue.Create(inf.Groups[1].Value is "-" or "−" ? "-inf" : "inf"), inf.Groups[2].Value.Trim());
            var m = Number.Match(text);
            if (!m.Success) return null;
            string num = m.Groups[1].Value.Replace('−', '-').Replace(',', '.');
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
            string unit = m.Groups[2].Value.Trim();
            if (unit.Length > 0 && unit.Any(char.IsWhiteSpace)) return null;   // "3 dB boost": words, not a unit
            switch (unit)
            {
                case "kHz": v *= 1000; unit = "Hz"; break;
                case "ms": v *= 0.001; unit = "s"; break;
                case "°": unit = "°"; break;
            }
            return (JsonValue.Create(v), unit);
        }

        static string Unit(string declared, string parsed)
        {
            if (!string.IsNullOrEmpty(parsed)) return parsed;
            return declared ?? "";
        }

        /// <summary>Builds the manifest. Call on PlugMain: it asks the plug-in for value texts.</summary>
        public static string Build(PlugCore core, string gear, bool effect)
        {
            var parameters = new JsonArray();
            foreach (var b in core.Bindings())
            {
                int max = b.Max;
                var p = new JsonObject
                {
                    ["index"] = b.Slot,
                    ["id"] = "slot" + (b.Slot + 1),
                    ["name"] = b.Name,
                    ["group"] = b.Module.Length > 0 ? b.Module : "Parameters",
                    ["min"] = 0,
                    ["max"] = max,
                    ["default"] = b.Default,
                };
                string desc = b.Name + (b.Units.Length > 0 ? " [" + b.Units + "]" : "");
                p["description"] = desc;
                if (b.Steps > 0 && b.Steps <= 128)
                {
                    var choices = new JsonArray();
                    var seen = new HashSet<string>();
                    for (int v = 0; v <= b.Steps; v++)
                    {
                        string t = core.SlotText(b.Slot, v);
                        if (string.IsNullOrWhiteSpace(t)) t = v.ToString(CultureInfo.InvariantCulture);
                        string u = t;
                        for (int k = 2; !seen.Add(u); k++) u = t + " " + k;
                        choices.Add(u);
                    }
                    p["kind"] = "enum";
                    p["choices"] = choices;
                }
                else
                {
                    var points = new JsonArray();
                    string unit = null;
                    bool ok = true;
                    int count = Math.Min(33, max + 1);
                    int numeric = 0;
                    for (int k = 0; k < count && ok; k++)
                    {
                        int raw = count == 1 ? 0 : (int)Math.Round((double)max * k / (count - 1));
                        var parsed = Parse(core.SlotText(b.Slot, raw));
                        if (parsed == null)
                        {
                            if (k == 0 || k == count - 1) continue;   // an end that reads "Off" or "Mute": the points between still say what the unit is
                            ok = false;
                            break;
                        }
                        string u = Unit(b.Units, parsed.Value.Unit);
                        if (unit == null) unit = u;
                        else if (parsed.Value.Unit.Length > 0 && unit.Length > 0 && unit != u) { ok = false; break; }
                        else if (unit.Length == 0 && u.Length > 0) unit = u;
                        points.Add(new JsonArray(raw, parsed.Value.Value));
                        if (parsed.Value.Value is JsonValue jv && jv.TryGetValue<double>(out _)) numeric++;
                    }
                    if (ok && numeric >= 3)
                    {
                        p["kind"] = "table";
                        p["points"] = points;
                        if (!string.IsNullOrEmpty(unit)) p["unit"] = unit;
                    }
                    else
                    {
                        p["kind"] = "linear";
                        p["a"] = 0.0;
                        p["b"] = 1.0;
                        p["description"] = desc + " (shown as 0..1 of its range)";
                    }
                }
                parameters.Add(p);
            }
            var root = new JsonObject
            {
                ["format"] = "rebuzz-machine-manifest/1",
                ["gear"] = gear,
                ["kind"] = effect ? "effect" : "generator",
                ["status"] = "generated",
                ["source"] = "generated at run time from the loaded plug-in's parameters",
                ["summary"] = $"{core.Name}{(core.State.Vendor is { Length: > 0 } vendor ? " by " + vendor : "")} ({(core.State.Format ?? "").ToUpperInvariant()}), hosted: " +
                              (effect ? "an effect plug-in. " : "an instrument plug-in. ") +
                              $"Its first {parameters.Count} automatable parameters are slots; values are shown with the plug-in's own units.",
                ["category"] = effect ? "Plug-in effect" : "Plug-in instrument",
                ["tags"] = new JsonArray("plug-in", (core.State.Format ?? "").ToLowerInvariant()),
                ["use_for"] = effect ? "Run a CLAP or VST3 effect on a Buzz signal." : "Play a CLAP or VST3 instrument from patterns, MIDI and the agent.",
                ["tracks"] = effect ? 0 : PlugCore.MaxTracks,
                ["track_parameters"] = effect ? new JsonArray() : new JsonArray("Note", "Velocity", "Delay", "Param", "Value"),
                ["parameters"] = parameters,
            };
            if (!effect) root["notes"] = new JsonObject { ["input"] = "midi", ["pitched"] = true };
            return root.ToJsonString();
        }
    }
}
