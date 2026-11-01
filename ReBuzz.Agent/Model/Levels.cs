#nullable disable
// Agent server (docs/specs/mcp-server.md §3.3): levels in dB and pan from −1 to +1. Buzz keeps a
// connection's gain as an amp (0x4000 = unity, up to 0xFFFE ≈ +12 dB) and its pan as 0 … 0x8000.

using System;
using System.Globalization;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.Model
{
    public static class Levels
    {
        public const int UnityAmp = 0x4000;
        public const int MaxAmp = 0xFFFE;
        public const int PanCenter = 0x4000;
        public const int PanMax = 0x8000;

        /// <summary>The loudest a connection can be: +12.04 dB.</summary>
        public static readonly double MaxDb = 20 * Math.Log10(MaxAmp / (double)UnityAmp);

        public static double AmpToDb(int amp) => amp <= 0 ? double.NegativeInfinity : Math.Round(20 * Math.Log10(amp / (double)UnityAmp), 2);

        public static int DbToAmp(double db) =>
            double.IsNegativeInfinity(db) ? 0 : Math.Clamp((int)Math.Round(UnityAmp * Math.Pow(10, db / 20)), 0, MaxAmp);

        public static double PanToUnit(int pan) => Math.Round((pan - PanCenter) / (double)PanCenter, 3);

        public static int UnitToPan(double pan) => Math.Clamp((int)Math.Round(PanCenter + pan * PanCenter), 0, PanMax);

        /// <summary>A gain in dB from a number or text ("-6", "-6 dB", "-inf"); checked against the connection's range.</summary>
        public static double ParseDb(JsonNode value, string what)
        {
            double db;
            if (Schema.TryNumber(value, out double n)) db = n;
            else if (value is JsonValue v && v.TryGetValue(out string s) && Quantity.TryParse(s, out double q, out string unit)
                     && (unit.Length == 0 || Quantity.DimensionOf(unit) == "dB"))
                db = q;
            else throw new ToolError($"{what}: give a gain in dB, e.g. -6, \"-6 dB\" or \"-inf\" (silent).");
            if (double.IsPositiveInfinity(db) || db > MaxDb + 0.005)
                throw new ToolError($"{what}: {Text(db)} is above a connection's maximum of +{MaxDb.ToString("F1", CultureInfo.InvariantCulture)} dB. " +
                                    "Turn the source machine up instead, or add gain with an effect.");
            return db;
        }

        /// <summary>"-6.0 dB", "-inf dB".</summary>
        public static string Text(double db) =>
            double.IsNegativeInfinity(db) ? "-inf dB" : db.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";

        /// <summary>A dB value for JSON: a number, or the text "-inf" (JSON has no infinities).</summary>
        public static JsonNode Json(double db) =>
            double.IsNegativeInfinity(db) ? JsonValue.Create("-inf") : JsonValue.Create(Math.Round(db, 2));

        /// <summary>"left 40%", "center", "right 100%".</summary>
        public static string PanText(double pan) =>
            Math.Abs(pan) < 0.005 ? "center" : (pan < 0 ? "left " : "right ") + Math.Round(Math.Abs(pan) * 100).ToString(CultureInfo.InvariantCulture) + "%";
    }
}
