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
    public enum MappingKind { Linear, Exp, Power, Db, Time, Freq, Percent, Bipolar, Int, Enum, Switch, Ratio, Table }

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
        /// <summary>Table mappings (profiled drafts): (raw, value) points by raw, joined linearly.</summary>
        public IReadOnlyList<(int Raw, double Value)> Points { get; init; }

        public double Value(int raw, int min, int max)
        {
            if (Kind == MappingKind.Table) return TableValue(raw);
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

        double TableValue(int raw)
        {
            var pts = Points;
            if (raw <= pts[0].Raw) return pts[0].Value;
            if (raw >= pts[^1].Raw) return pts[^1].Value;
            int lo = 0, hi = pts.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (pts[mid].Raw <= raw) lo = mid; else hi = mid;
            }
            var (r0, v0) = pts[lo];
            var (r1, v1) = pts[hi];
            if (r1 == r0 || double.IsInfinity(v0) || double.IsInfinity(v1)) return raw - r0 < r1 - raw ? v0 : v1;
            return v0 + (v1 - v0) * (raw - r0) / (r1 - r0);
        }

        /// <summary>A table's raw value nearest a value (on a log scale for Hz and seconds).</summary>
        int TableRaw(double value, int min, int max)
        {
            bool log = Dimension is "Hz" or "s" && value > 0;
            int best = min;
            double bestDistance = double.MaxValue;
            int step = Math.Max(1, (max - min) / 65536);
            for (int r = min; r <= max; r += step)
            {
                double v = TableValue(r);
                double d = double.IsInfinity(value) || double.IsInfinity(v) ? (value == v ? 0 : double.MaxValue)
                         : log && v > 0 ? Math.Abs(Math.Log(v / value)) : Math.Abs(v - value);
                if (d < bestDistance) { bestDistance = d; best = r; }
            }
            return best;
        }

        public int Raw(double value, int min, int max)
        {
            double t;
            switch (Kind)
            {
                case MappingKind.Table: return TableRaw(value, min, max);
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
            int r = Math.Clamp((int)Math.Round(min + t * (max - min)), min, max);
            // A finite value never lands on the end that means -inf dB or ∞:1.
            if (Flag && max > min && !double.IsInfinity(value))
            {
                if (Kind == MappingKind.Db && r == min) r = min + 1;
                if (Kind == MappingKind.Ratio && r == max) r = max - 1;
            }
            return r;
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
        /// <summary>
        /// A profiled draft's mapping (§12.2): the machine's own texts ("Off", "1/8") are matched
        /// first, and the mapping takes what reads as a number with a unit.
        /// </summary>
        public bool TextsFirst { get; init; }
        /// <summary>The machine's own text for a raw value (null when it has none).</summary>
        public required Func<int, string> Describe { get; init; }
        /// <summary>Where agents may set it (machine-safety.md §3.4), from its manifest; null: its whole range.</summary>
        public SafeRange SafeRange { get; init; }
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
                // A slope (a tilt: dB per octave): its own dimension, so "6 dB/oct" is not taken for a level in dB.
                case "db/oct": case "db/octave": case "db per octave": case "db an octave": return "dB/oct";
                case "%": return "%";
                case "st": case "semi": case "semitones": case "semitone": return "st";
                case "ct": case "cents": case "cent": return "ct";
                case "oct": case "octave": case "octaves": return "oct";
                case "°": case "deg": case "degrees": return "°";   // phases (Neo Ensemble's Spread)
                case "bpm": return "bpm";
                case "tick": case "ticks": return "ticks";
                case "x": case "×": return "x";
                case ":1": return "ratio";
                // Neo Sampler (machines.md §5): keys and roots in MIDI notes, zones' waves by slot.
                case "note": case "notes": return "note";
                case "wave": return "wave";
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
                // A machine's own text for a value ("1/8", "1 bar", "Off") is taken as it is.
                if ((p.Mapping == null || p.TextsFirst) && MatchDescription(p, text) is int exact) return exact;
                string lower = text.ToLowerInvariant();
                if (lower.StartsWith("raw:"))
                {
                    if (!int.TryParse(text.Substring(4).Trim(), out int raw)) throw new ToolError($"{p.Name}: \"{text}\" needs a whole number after raw:.");
                    if (raw < p.Min || raw > p.Max) throw new ToolError($"{p.Name}: raw {raw} is outside {p.Min}…{p.Max}.");
                    return raw;
                }
                // Fork (U6, mcp-server.md §12.13): a choice named like a keyword (EnvelopeBlock's "Default") is that choice.
                if (lower is "min" or "max" or "default" && p.Mapping is { Kind: MappingKind.Enum or MappingKind.Switch, Names: { } named })
                    for (int i = 0; i < named.Count; i++)
                        if (string.Equals(named[i].Trim(), text, StringComparison.OrdinalIgnoreCase)) return Math.Clamp(p.Min + i, p.Min, p.Max);
                if (lower == "min") return p.Min;
                if (lower == "max") return p.Max;
                if (lower == "default") return p.Default;
                // Fork (Neo Sampler, mcp-server.md §3.3): a parameter in MIDI notes takes a pitch's name ("C2",
                // "F#3"), and one in notes or wave slots the machine's own texts ("Wave" for a zone's root that
                // follows the wave, "Built-in Kick", a slot's name), besides numbers.
                if (p.Mapping is { Kind: MappingKind.Int, Unit: "note" or "wave" } im)
                {
                    if (MatchDescription(p, text) is int own) return own;
                    if (im.Unit == "note" && Pitch.TryParse(text, out int midi, out _) && !Quantity.TryParse(text, out _, out _))
                        return im.Raw(midi, p.Min, p.Max);
                }
            }

            if (mode == Mode.Times)
            {
                double factor = isNumber ? number : text != null && Quantity.TryParse(text.TrimStart('x', 'X', '*', '×'), out double f, out _) ? f : double.NaN;
                if (double.IsNaN(factor) || factor <= 0) throw new ToolError($"{p.Name}: times needs a positive factor such as 2 or 0.5.");
                double now = Physical(p, current, out string dim);
                if (double.IsNaN(now)) throw new ToolError($"{p.Name} has no numeric value to scale (it reads \"{p.Describe(current)}\"); set a value instead.");
                if (dim == "dB") throw new ToolError($"{p.Name} is in dB: change it by an amount instead, e.g. by \"+6 dB\" (twice the amplitude) or \"-3 dB\".");
                if (dim.Length == 0) dim = Dimension(p);
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
            double physicalNow = Physical(p, current, out string dimensionNow);
            string dimension = p.Mapping != null || dimensionNow.Length > 0 ? dimensionNow : Dimension(p);
            if (!Quantity.ToBase(value, unit, out double inBase, out string inDim))
                throw new ToolError($"{p.Name}: unknown unit \"{unit}\". {Examples(p)}");
            if (inDim != null && dimension.Length > 0 && inDim != dimension)
                throw new ToolError($"{p.Name} is in {dimension}, not {inDim}. {Examples(p)}");
            // Percentages: "35%" and a bare 35 on a % parameter both mean 35 %. Percent and bipolar
            // mappings (and described values) hold fractions; a linear "2 … 98 %" holds percents.
            if (dimension == "%" && (inDim == "%" || inDim == null) && Fractional(p)) inBase /= 100;
            if (mode == Mode.By)
            {
                if (double.IsNaN(physicalNow) || dimensionNow != dimension) throw new ToolError($"{p.Name} has no numeric value to change by an amount (it reads \"{p.Describe(current)}\"); set a value instead.");
                if (double.IsInfinity(physicalNow)) throw new ToolError($"{p.Name} is at {p.Describe(current)}; set a value instead of changing it by an amount.");
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
            // Described values: the raw whose description is nearest (log scale for Hz and seconds,
            // among the positive values when the target is positive).
            bool log = dimension is "Hz" or "s" && target > 0 && Candidates(p).Any(r => Physical(p, r, out string d) > 0 && d == dimension);
            int best = -1;
            double bestDistance = double.MaxValue;
            foreach (int raw in Candidates(p))
            {
                double v = Physical(p, raw, out string dim);
                if (double.IsNaN(v) || (dimension.Length > 0 && dim != dimension)) continue;
                if (log && v <= 0) continue;
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

        /// <summary>
        /// The quantity a described parameter is in: what most of its value texts read as (one
        /// value may read "Off" or "Sync"), "" when they aren't numbers with units.
        /// </summary>
        static string Dimension(ParamInfo p)
        {
            if (p.Mapping != null) return p.Mapping.Dimension;
            var counts = new Dictionary<string, int>();
            int span = p.Max - p.Min;
            int step = Math.Max(1, span / 64);
            for (int raw = p.Min; raw <= p.Max; raw += step)
            {
                double v = Physical(p, raw, out string d);
                if (double.IsNaN(v) || d.Length == 0) continue;
                counts[d] = counts.TryGetValue(d, out int n) ? n + 1 : 1;
            }
            return counts.Count == 0 ? "" : counts.OrderByDescending(kv => kv.Value).First().Key;
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
