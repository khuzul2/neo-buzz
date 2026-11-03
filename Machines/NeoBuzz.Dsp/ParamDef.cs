#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §3): one parameter of a machine, as declared in
// its Params.def. Maps a Buzz parameter value (an int) to the physical value the engine uses, and
// describes it for the parameter window, pattern editor and the machine's editor.

using System;
using System.Globalization;

namespace NeoBuzz.Dsp
{
    internal enum MapKind
    {
        /// <summary>A + t·(B − A).</summary>
        Linear,
        /// <summary>A·(B/A)^t.</summary>
        Exp,
        /// <summary>A + t^Exponent·(B − A).</summary>
        Power,
        /// <summary>Decibels, linear in dB from A to B (the bottom is −∞ when flagged).</summary>
        Db,
        /// <summary>Seconds, exponential from A to B.</summary>
        Time,
        /// <summary>Hertz, exponential from A to B.</summary>
        Freq,
        /// <summary>0 … 1 shown as a percentage.</summary>
        Percent,
        /// <summary>−1 … +1 shown as a signed percentage.</summary>
        Bipolar,
        /// <summary>The raw value plus an offset (semitones, steps…).</summary>
        Int,
        /// <summary>A choice from Names.</summary>
        Enum,
        /// <summary>Off / On.</summary>
        Switch,
        /// <summary>A ratio n:1, exponential from A to B, ∞ at the top.</summary>
        Ratio,
    }

    internal sealed class ParamDef
    {
        public ParamDef(int index, string id, string name, int min, int max, int def, MapKind kind,
            double a = 0, double b = 1, double exponent = 1, string unit = "", int digits = -1,
            string[] names = null, bool flag = false, string group = "", string description = "")
        {
            Index = index;
            Id = id;
            Name = name;
            Min = min;
            Max = max;
            Default = def;
            Kind = kind;
            A = a;
            B = b;
            Exponent = exponent;
            Unit = unit ?? "";
            Digits = digits;
            Names = names;
            Flag = flag;
            Group = group ?? "";
            Description = description ?? name;
        }

        public int Index { get; }
        public string Id { get; }
        public string Name { get; }
        public int Min { get; }
        public int Max { get; }
        public int Default { get; }
        public MapKind Kind { get; }
        public double A { get; }
        public double B { get; }
        public double Exponent { get; }
        public string Unit { get; }
        public int Digits { get; }
        public string[] Names { get; }
        /// <summary>Db: the bottom is −∞. Int: shown with a sign. Ratio: the top is ∞.</summary>
        public bool Flag { get; }
        public string Group { get; }
        public string Description { get; }

        /// <summary>Position of a raw value in its range, 0 … 1.</summary>
        public double Normalized(int raw) => Max == Min ? 0 : Math.Clamp((raw - Min) / (double)(Max - Min), 0.0, 1.0);

        /// <summary>The physical value of a raw value (dB, seconds, Hz, ratio, index…).</summary>
        public double Value(int raw)
        {
            double t = Normalized(raw);
            switch (Kind)
            {
                case MapKind.Linear: return A + t * (B - A);
                case MapKind.Exp:
                case MapKind.Time:
                case MapKind.Freq: return A * PortableMath.Pow(B / A, t);
                case MapKind.Power: return A + PortableMath.Pow(t, Exponent) * (B - A);
                case MapKind.Db: return Flag && raw <= Min ? double.NegativeInfinity : A + t * (B - A);
                case MapKind.Percent: return t;
                case MapKind.Bipolar: return t * 2.0 - 1.0;
                case MapKind.Int: return Math.Clamp(raw, Min, Max) + A;
                case MapKind.Enum:
                case MapKind.Switch: return Math.Clamp(raw, Min, Max) - Min;
                case MapKind.Ratio: return Flag && raw >= Max ? double.PositiveInfinity : A * PortableMath.Pow(B / A, t);
                default: return t;
            }
        }

        /// <summary>The raw value whose physical value is closest to <paramref name="value"/>.</summary>
        public int Raw(double value)
        {
            double t;
            switch (Kind)
            {
                case MapKind.Linear: t = (value - A) / (B - A); break;
                case MapKind.Exp:
                case MapKind.Time:
                case MapKind.Freq: t = value <= 0 ? 0 : PortableMath.Log(value / A) / PortableMath.Log(B / A); break;
                case MapKind.Power: t = PortableMath.Pow(Math.Max(0, (value - A) / (B - A)), 1.0 / Exponent); break;
                case MapKind.Db: t = double.IsNegativeInfinity(value) ? 0 : (value - A) / (B - A); break;
                case MapKind.Percent: t = value; break;
                case MapKind.Bipolar: t = (value + 1.0) * 0.5; break;
                case MapKind.Int: return Math.Clamp((int)Math.Round(value - A), Min, Max);
                case MapKind.Enum:
                case MapKind.Switch: return Math.Clamp((int)Math.Round(value) + Min, Min, Max);
                case MapKind.Ratio: t = double.IsPositiveInfinity(value) ? 1 : PortableMath.Log(value / A) / PortableMath.Log(B / A); break;
                default: t = value; break;
            }
            t = Math.Clamp(double.IsNaN(t) ? 0 : t, 0.0, 1.0);
            return Math.Clamp((int)Math.Round(Min + t * (Max - Min)), Min, Max);
        }

        /// <summary>Optional: the machine's own text for a raw value (note names, ratios, …).</summary>
        public Func<int, string> Describer { get; set; }

        /// <summary>The text shown for a raw value.</summary>
        public string Describe(int raw)
        {
            raw = Math.Clamp(raw, Min, Max);
            if (Describer != null) return Describer(raw);
            double v = Value(raw);
            var c = CultureInfo.InvariantCulture;
            switch (Kind)
            {
                case MapKind.Enum:
                    return Names != null && (int)v < Names.Length ? Names[(int)v] : ((int)v).ToString(c);
                case MapKind.Switch:
                    return Names != null && Names.Length == 2 ? Names[(int)v] : v > 0 ? "On" : "Off";
                case MapKind.Percent:
                    return (v * 100).ToString(Digits >= 0 ? "F" + Digits : "F0", c) + "%";
                case MapKind.Bipolar:
                {
                    double p = v * 100;
                    string s = Math.Abs(p).ToString(Digits >= 0 ? "F" + Digits : "F0", c);
                    return s == "0" || Math.Abs(p) < 1e-9 ? "0%" : (p > 0 ? "+" : "−") + s + "%";
                }
                case MapKind.Db:
                    if (double.IsNegativeInfinity(v)) return "−∞ dB";
                    return Signed(v, Digits >= 0 ? Digits : 1, c) + " dB";
                case MapKind.Time:
                    return Time(v, c);
                case MapKind.Freq:
                    return Freq(v, c);
                case MapKind.Int:
                    return (Flag && v > 0 ? "+" : "") + ((int)v).ToString(c).Replace("-", "−") + Suffix();
                case MapKind.Ratio:
                    return double.IsPositiveInfinity(v) ? "∞:1" : v.ToString(v < 10 ? "F1" : "F0", c) + ":1";
                default:
                    return Number(v, c) + Suffix();
            }
        }

        string Suffix() => Unit.Length == 0 ? "" : Unit == "%" || Unit == "×" || Unit == "°" ? Unit : " " + Unit;

        string Number(double v, CultureInfo c)
        {
            int d = Digits >= 0 ? Digits : Math.Abs(v) >= 100 ? 0 : Math.Abs(v) >= 10 ? 1 : 2;
            return v.ToString("F" + d, c).Replace("-", "−");
        }

        static string Signed(double v, int digits, CultureInfo c)
        {
            string s = Math.Abs(v).ToString("F" + digits, c);
            if (double.Parse(s, c) == 0) return s;
            return (v > 0 ? "+" : "−") + s;
        }

        /// <summary>"12.5 ms", "1.20 s".</summary>
        public static string Time(double seconds, CultureInfo c = null)
        {
            c ??= CultureInfo.InvariantCulture;
            if (seconds < 0.001) return (seconds * 1000).ToString("F2", c) + " ms";
            if (seconds < 0.01) return (seconds * 1000).ToString("F2", c) + " ms";
            if (seconds < 0.1) return (seconds * 1000).ToString("F1", c) + " ms";
            if (seconds < 1) return (seconds * 1000).ToString("F0", c) + " ms";
            if (seconds < 10) return seconds.ToString("F2", c) + " s";
            return seconds.ToString("F1", c) + " s";
        }

        /// <summary>"440 Hz", "1.25 kHz".</summary>
        public static string Freq(double hz, CultureInfo c = null)
        {
            c ??= CultureInfo.InvariantCulture;
            if (hz >= 10000) return (hz / 1000).ToString("F1", c) + " kHz";
            if (hz >= 1000) return (hz / 1000).ToString("F2", c) + " kHz";
            if (hz >= 100) return hz.ToString("F0", c) + " Hz";
            if (hz >= 10) return hz.ToString("F1", c) + " Hz";
            return hz.ToString("F2", c) + " Hz";
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// A machine's parameters, their raw values as ReBuzz last set them, and change tracking for
    /// the engine. Setters are called from ReBuzz's tick (audio thread); the engine reads in Work.
    /// </summary>
    internal sealed class ParamValues
    {
        readonly int[] raw;
        readonly bool[] dirty;
        volatile bool anyDirty;

        public ParamValues(ParamDef[] table)
        {
            Table = table;
            raw = new int[table.Length];
            dirty = new bool[table.Length];
            for (int i = 0; i < table.Length; i++)
            {
                raw[i] = table[i].Default;
                dirty[i] = true;
            }
            anyDirty = true;
        }

        public ParamDef[] Table { get; }

        public int this[int index] => raw[index];

        public double Value(int index) => Table[index].Value(raw[index]);

        public float F(int index) => (float)Table[index].Value(raw[index]);

        public int Choice(int index) => (int)Table[index].Value(raw[index]);

        public bool On(int index) => Table[index].Value(raw[index]) > 0;

        public void Set(int index, int value)
        {
            var d = Table[index];
            value = Math.Clamp(value, d.Min, d.Max);
            if (raw[index] == value && !dirty[index]) return;
            raw[index] = value;
            dirty[index] = true;
            anyDirty = true;
        }

        /// <summary>Whether any value changed since the last <see cref="Clean"/>.</summary>
        public bool AnyDirty => anyDirty;

        public bool IsDirty(int index) => dirty[index];

        /// <summary>Marks everything as applied.</summary>
        public void Clean()
        {
            Array.Clear(dirty, 0, dirty.Length);
            anyDirty = false;
        }

        /// <summary>Sets every parameter to its default.</summary>
        public void ResetToDefaults()
        {
            for (int i = 0; i < Table.Length; i++) Set(i, Table[i].Default);
        }
    }
}
