#nullable disable
// Agent server (docs/specs/mcp-server.md §3.3): parameter values in units. Agents write
// "800 Hz", "-6 dB", "35%", "Saw", "1/8", "raw:512", or change a value by an amount or a factor;
// the server finds the raw Buzz value. Neo machines publish exact mappings (their manifest);
// other machines are read through their value descriptions.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.Model
{
    public enum MappingKind { Linear, Exp, Power, Db, Time, Freq, Percent, Bipolar, Int, Enum, Switch, Ratio }

    /// <summary>An exact raw ↔ physical mapping (Neo machines' Params.def, via their manifest).</summary>
    public sealed class ParamMapping
    {
        public MappingKind Kind { get; init; }
        public double A { get; init; }
        public double B { get; init; } = 1;
        public double Exponent { get; init; } = 1;
        public bool Flag { get; init; }
        public string Unit { get; init; } = "";
        public IReadOnlyList<string> Names { get; init; }

        public double Value(int raw, int min, int max)
        {
            double t = max == min ? 0 : Math.Clamp((raw - min) / (double)(max - min), 0, 1);
            return Kind switch
            {
                MappingKind.Linear => A + t * (B - A),
                MappingKind.Exp or MappingKind.Time or MappingKind.Freq => A * Math.Pow(B / A, t),
                MappingKind.Power => A + Math.Pow(t, Exponent) * (B - A),
                MappingKind.Db => Flag && raw <= min ? double.NegativeInfinity : A + t * (B - A),
                MappingKind.Percent => t,
                MappingKind.Bipolar => t * 2 - 1,
                MappingKind.Int => Math.Clamp(raw, min, max) + A,
                MappingKind.Enum or MappingKind.Switch => Math.Clamp(raw, min, max) - min,
                MappingKind.Ratio => Flag && raw >= max ? double.PositiveInfinity : A * Math.Pow(B / A, t),
                _ => t,
            };
        }

        public int Raw(double value, int min, int max)
        {
            double t;
            switch (Kind)
            {
                case MappingKind.Linear: t = (value - A) / (B - A); break;
                case MappingKind.Exp:
                case MappingKind.Time:
                case MappingKind.Freq: t = value <= 0 ? 0 : Math.Log(value / A) / Math.Log(B / A); break;
                case MappingKind.Power: t = Math.Pow(Math.Max(0, (value - A) / (B - A)), 1.0 / Exponent); break;
                case MappingKind.Db: t = double.IsNegativeInfinity(value) ? 0 : (value - A) / (B - A); break;
                case MappingKind.Percent: t = value; break;
                case MappingKind.Bipolar: t = (value + 1) * 0.5; break;
                case MappingKind.Int: return Math.Clamp((int)Math.Round(value - A), min, max);
                case MappingKind.Enum:
                case MappingKind.Switch: return Math.Clamp((int)Math.Round(value) + min, min, max);
                case MappingKind.Ratio: t = double.IsPositiveInfinity(value) ? 1 : Math.Log(value / A) / Math.Log(B / A); break;
                default: t = value; break;
            }
            t = Math.Clamp(double.IsNaN(t) ? 0 : t, 0, 1);
            return Math.Clamp((int)Math.Round(min + t * (max - min)), min, max);
        }

        /// <summary>The quantity the physical value is in (for unit conversion of what the agent writes).</summary>
        public string Dimension => Kind switch
        {
            MappingKind.Db => "dB",
            MappingKind.Time => "s",
            MappingKind.Freq => "Hz",
            MappingKind.Percent or MappingKind.Bipolar => "%",
            MappingKind.Ratio => "ratio",
            _ => Quantity.DimensionOf(Unit) ?? "",
        };
    }

    /// <summary>A parameter as the agent server sees it.</summary>
    public sealed class ParamInfo
    {
        public required string Name { get; init; }
        public int Index { get; init; }
        public string Group { get; init; } = "";
        public string Description { get; init; } = "";
        public int Min { get; init; }
        public int Max { get; init; }
        public int Default { get; init; }
        /// <summary>Exact mapping (Neo machines); null: read values through <see cref="Describe"/>.</summary>
        public ParamMapping Mapping { get; init; }
        /// <summary>The machine's own text for a raw value (null when it has none).</summary>
        public required Func<int, string> Describe { get; init; }
    }

    /// <summary>A number with a unit, normalised for comparison ("0.8 kHz" = 800 Hz, "250 ms" = 0.25 s).</summary>
    public static class Quantity
    {
        static readonly Regex Number = new(@"^\s*([+-]?(?:\d+(?:[.,]\d*)?|[.,]\d+)(?:[eE][+-]?\d+)?)\s*([^\d\s].*)?$", RegexOptions.Compiled);

        public static string DimensionOf(string unit)
        {
            switch ((unit ?? "").Trim().ToLowerInvariant())
            {
                case "hz": case "khz": return "Hz";
                case "s": case "sec": case "ms": return "s";
                case "db": case "dbfs": case "db fs": return "dB";
                case "%": return "%";
                case "st": case "semi": case "semitones": case "semitone": return "st";
                case "ct": case "cents": case "cent": return "ct";
                case "oct": case "octave": case "octaves": return "oct";
                case "bpm": return "bpm";
                case "x": case "×": return "x";
                case ":1": return "ratio";
                case "": return null;
                default: return null;
            }
        }

        /// <summary>Parses "800 Hz", "-6dB", "35 %", "4:1", "0.8 kHz", "-inf dB", "∞". Unit is "" when absent.</summary>
        public static bool TryParse(string text, out double value, out string unit)
        {
            value = 0;
            unit = "";
            string t = (text ?? "").Trim().Replace('−', '-').Replace("∞", "inf");
            var ratio = Regex.Match(t, @"^\s*([\d.]+|inf)\s*:\s*1\s*$", RegexOptions.IgnoreCase);
            if (ratio.Success)
            {
                unit = ":1";
                value = ratio.Groups[1].Value.ToLowerInvariant() == "inf" ? double.PositiveInfinity : double.Parse(ratio.Groups[1].Value, CultureInfo.InvariantCulture);
                return true;
            }
            var inf = Regex.Match(t, @"^\s*([+-]?)inf(?:inity)?\s*(.*)$", RegexOptions.IgnoreCase);
            if (inf.Success)
            {
                value = inf.Groups[1].Value == "-" ? double.NegativeInfinity : double.PositiveInfinity;
                unit = inf.Groups[2].Value.Trim();
                return true;
            }
            var m = Number.Match(t);
            if (!m.Success) return false;
            if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
            unit = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
            return true;
        }

        /// <summary>Converts to the dimension's base (Hz, s, dB, %…); false when the unit is unknown.</summary>
        public static bool ToBase(double value, string unit, out double baseValue, out string dimension)
        {
            dimension = DimensionOf(unit);
            baseValue = value;
            if (dimension == null) return unit.Length == 0;
            string u = unit.Trim().ToLowerInvariant();
            if (u == "khz") baseValue = value * 1000;
            else if (u == "ms") baseValue = value / 1000;
            return true;
        }
    }

    public static class ValueResolver
    {
        /// <summary>How a change is given: an absolute value, an amount added in the parameter's unit, or a factor.</summary>
        public enum Mode { Value, By, Times }

        /// <summary>The raw value for what the agent wrote. Throws a ToolError naming the valid forms.</summary>
        public static int Resolve(ParamInfo p, JsonNode input, int current, Mode mode = Mode.Value)
        {
            string text = input is JsonValue v && v.TryGetValue(out string s) ? s.Trim() : null;
            bool isNumber = Schema.TryNumber(input, out double number);
            if (text == null && !isNumber) throw new ToolError($"{p.Name}: give a number or text such as \"800 Hz\", \"-6 dB\", \"35%\", a choice name, or \"raw:512\".");

            if (mode == Mode.Value && text != null)
            {
                string lower = text.ToLowerInvariant();
                if (lower.StartsWith("raw:"))
                {
                    if (!int.TryParse(text.Substring(4).Trim(), out int raw)) throw new ToolError($"{p.Name}: \"{text}\" needs a whole number after raw:.");
                    if (raw < p.Min || raw > p.Max) throw new ToolError($"{p.Name}: raw {raw} is outside {p.Min}…{p.Max}.");
                    return raw;
                }
                if (lower == "min") return p.Min;
                if (lower == "max") return p.Max;
                if (lower == "default") return p.Default;
            }

            if (mode == Mode.Times)
            {
                double factor = isNumber ? number : text != null && Quantity.TryParse(text.TrimStart('x', 'X', '*', '×'), out double f, out _) ? f : double.NaN;
                if (double.IsNaN(factor) || factor <= 0) throw new ToolError($"{p.Name}: times needs a positive factor such as 2 or 0.5.");
                double now = Physical(p, current, out string dim);
                if (double.IsNaN(now)) throw new ToolError($"{p.Name} has no numeric value to scale (it reads \"{p.Describe(current)}\"); set a value instead.");
                return ToRaw(p, now * factor, dim);
            }

            if (p.Mapping is { Kind: MappingKind.Enum or MappingKind.Switch } choice)
                return ResolveChoice(p, choice, input, text, isNumber, number, current, mode);

            // A number with an optional unit.
            double value;
            string unit = "";
            if (isNumber) value = number;
            else if (!Quantity.TryParse(text, out value, out unit))
            {
                if (mode == Mode.Value && p.Mapping == null && MatchDescription(p, text) is int byText) return byText;
                throw new ToolError($"{p.Name}: \"{text}\" isn't a value it takes. {Examples(p)}");
            }
            double physicalNow = Physical(p, current, out string dimension);
            if (!Quantity.ToBase(value, unit, out double inBase, out string inDim))
                throw new ToolError($"{p.Name}: unknown unit \"{unit}\". {Examples(p)}");
            if (inDim != null && dimension.Length > 0 && inDim != dimension)
                throw new ToolError($"{p.Name} is in {dimension}, not {inDim}. {Examples(p)}");
            // Percentages: "35%" and a bare 35 on a % parameter both mean 35 %. Percent and bipolar
            // mappings (and described values) hold fractions; a linear "2 … 98 %" holds percents.
            if (dimension == "%" && (inDim == "%" || inDim == null) && Fractional(p)) inBase /= 100;
            if (mode == Mode.By)
            {
                if (double.IsNaN(physicalNow)) throw new ToolError($"{p.Name} has no numeric value to change by an amount (it reads \"{p.Describe(current)}\"); set a value instead.");
                inBase = physicalNow + inBase;
            }
            return ToRaw(p, inBase, dimension);
        }

        static bool Fractional(ParamInfo p) => p.Mapping == null || p.Mapping.Kind is MappingKind.Percent or MappingKind.Bipolar;

        static int ResolveChoice(ParamInfo p, ParamMapping m, JsonNode input, string text, bool isNumber, double number, int current, Mode mode)
        {
            var names = Choices(p).ToList();
            if (mode == Mode.By)
            {
                if (!isNumber) throw new ToolError($"{p.Name}: by needs a whole number of steps for a choice.");
                return Math.Clamp(current + (int)Math.Round(number), p.Min, p.Max);
            }
            if (isNumber) return m.Raw(number, p.Min, p.Max);
            string t = Normalise(text);
            if (m.Kind == MappingKind.Switch)
            {
                if (t is "on" or "true" or "yes" or "1") return p.Max;
                if (t is "off" or "false" or "no" or "0") return p.Min;
            }
            for (int i = 0; i < names.Count; i++) if (Normalise(names[i]) == t) return p.Min + i;
            var starts = names.Select((n, i) => (n, i)).Where(x => Normalise(x.n).StartsWith(t)).ToList();
            if (starts.Count == 1) return p.Min + starts[0].i;
            var near = Text.Closest(text, names);
            throw new ToolError($"{p.Name}: no choice \"{text}\". Choices: {string.Join(", ", names)}." + (near.Count > 0 ? $" Did you mean \"{near[0]}\"?" : ""));
        }

        static string Normalise(string s) => Regex.Replace((s ?? "").ToLowerInvariant().Replace('_', ' ').Replace('−', '-'), @"\s+", " ").Trim();

        /// <summary>The choice names (manifest names, else the machine's descriptions).</summary>
        public static IEnumerable<string> Choices(ParamInfo p) =>
            p.Mapping?.Names ?? Enumerable.Range(p.Min, p.Max - p.Min + 1).Select(r => p.Describe(r) ?? r.ToString(CultureInfo.InvariantCulture));

        /// <summary>The physical value at a raw value and its dimension (NaN when not numeric).</summary>
        public static double Physical(ParamInfo p, int raw, out string dimension)
        {
            if (p.Mapping != null)
            {
                dimension = p.Mapping.Dimension;
                return p.Mapping.Value(raw, p.Min, p.Max);
            }
            string d = p.Describe(raw);
            if (d != null && Quantity.TryParse(d, out double value, out string unit) && Quantity.ToBase(value, unit, out double b, out string dim))
            {
                dimension = dim ?? "";
                return dim == "%" ? b / 100 : b;
            }
            dimension = "";
            return d == null ? raw : double.NaN;
        }

        static int ToRaw(ParamInfo p, double target, string dimension)
        {
            if (p.Mapping != null) return p.Mapping.Raw(target, p.Min, p.Max);
            // Described values: the raw whose description is nearest (log scale for Hz and seconds).
            bool log = dimension is "Hz" or "s";
            int best = -1;
            double bestDistance = double.MaxValue;
            foreach (int raw in Candidates(p))
            {
                double v = Physical(p, raw, out string dim);
                if (double.IsNaN(v) || (dimension.Length > 0 && dim != dimension)) continue;
                double d = log && v > 0 && target > 0 ? Math.Abs(Math.Log(v / target)) : Math.Abs(v - target);
                if (double.IsInfinity(target) && double.IsInfinity(v) && Math.Sign(target) == Math.Sign(v)) d = 0;
                if (d < bestDistance) { bestDistance = d; best = raw; }
            }
            if (best < 0) throw new ToolError($"{p.Name}: none of its values reads as a number to compare with; use a choice or raw:N. {Examples(p)}");
            return best;
        }

        /// <summary>Raw values to try: all of them for small ranges, a grid plus the ends for big ones.</summary>
        static IEnumerable<int> Candidates(ParamInfo p)
        {
            int span = p.Max - p.Min;
            if (span <= 4096) return Enumerable.Range(p.Min, span + 1);
            int step = (int)Math.Ceiling(span / 4096.0);
            return Enumerable.Range(0, span / step + 1).Select(i => p.Min + i * step).Append(p.Max);
        }

        static int? MatchDescription(ParamInfo p, string text)
        {
            string t = Normalise(text);
            foreach (int raw in Candidates(p))
                if (Normalise(p.Describe(raw)) == t) return raw;
            return null;
        }

        /// <summary>A short line of valid examples for errors.</summary>
        public static string Examples(ParamInfo p)
        {
            if (p.Mapping is { Kind: MappingKind.Enum or MappingKind.Switch })
                return $"Choices: {string.Join(", ", Choices(p))}.";
            string lo = p.Describe(p.Min) ?? p.Min.ToString(CultureInfo.InvariantCulture);
            string hi = p.Describe(p.Max) ?? p.Max.ToString(CultureInfo.InvariantCulture);
            return $"It ranges from \"{lo}\" to \"{hi}\" (or raw:{p.Min}…raw:{p.Max}).";
        }
    }
}
