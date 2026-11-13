// Agent server (docs/specs/neo-learn.md §5.3): the Explain fields machine_describe adds to a parameter whose
// manifest has How and Sound text (Neo Learn): id, unit, min, max, default_number, curve, how and sound. The
// numbers are in the parameter's unit (Hz, s, dB, %, a count), not raw values.
using System;
using System.Text.Json.Nodes;

namespace ReBuzz.Agent.Model
{
    public static class ExplainFields
    {
        /// <summary>Whether the parameter has Explain text.</summary>
        public static bool Has(ManifestParameter p) => p != null && (p.How.Length > 0 || p.Sound.Length > 0);

        /// <summary>How the control spreads its range: linear, logarithmic, bipolar or power (choices: choice).</summary>
        public static string Curve(ManifestParameter p) => p.Mapping.Kind switch
        {
            MappingKind.Exp or MappingKind.Time or MappingKind.Freq or MappingKind.Ratio => "logarithmic",
            MappingKind.Bipolar => "bipolar",
            MappingKind.Power => "power",
            MappingKind.Enum or MappingKind.Switch => "choice",
            _ => "linear",
        };

        /// <summary>The unit its numbers are in ("" for a count or a choice).</summary>
        public static string Unit(ManifestParameter p) => p.Mapping.Kind switch
        {
            MappingKind.Freq => "Hz",
            MappingKind.Time => "s",
            MappingKind.Db => "dB",
            MappingKind.Percent or MappingKind.Bipolar => "%",
            MappingKind.Ratio => ":1",
            MappingKind.Enum or MappingKind.Switch => "",
            _ => p.Mapping.Unit ?? "",
        };

        /// <summary>A raw value in the parameter's unit (a fraction as a percentage).</summary>
        public static double Number(ManifestParameter p, int raw)
        {
            double x = p.Mapping.Value(raw, p.Min, p.Max);
            return p.Mapping.Kind is MappingKind.Percent or MappingKind.Bipolar ? x * 100 : x;
        }

        static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        /// <summary>Adds the fields to <paramref name="o"/> (the parameter's JSON); nothing without Explain text.</summary>
        public static void Add(JsonObject o, ManifestParameter p)
        {
            if (!Has(p)) return;
            o["id"] = p.Id;
            string unit = Unit(p);
            if (unit.Length > 0) o["unit"] = unit;
            if (p.Mapping.Kind is not (MappingKind.Enum or MappingKind.Switch))
            {
                if (Finite(Number(p, p.Min))) o["min"] = Math.Round(Number(p, p.Min), 4);
                if (Finite(Number(p, p.Max))) o["max"] = Math.Round(Number(p, p.Max), 4);
                if (Finite(Number(p, p.Default))) o["default_number"] = Math.Round(Number(p, p.Default), 4);
            }
            o["curve"] = Curve(p);
            if (p.How.Length > 0) o["how"] = p.How;
            if (p.Sound.Length > 0) o["sound"] = p.Sound;
        }
    }
}
