// Fork (docs/specs/ux-overhaul.md P3e, decision 66): the device view's edits. Every edit is one step on the song's undo history
// (ParameterEdits.ParameterChange, as the old parameter window's); a drag is live while it lasts and one step when it ends, and the
// wheel and arrow steps on one parameter merge while they follow each other.

using System;
using System.Globalization;
using System.Text.Json.Nodes;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.Studio.Parameters;

namespace ReBuzz.Studio.Views.Device
{
    internal static class DeviceEdits
    {
        public static ReBuzzCore CoreOf(IMachine machine) => machine?.Graph?.Buzz as ReBuzzCore;

        /// <summary>The machine hears the value now (a drag's move); no undo step yet.</summary>
        public static void SetLive(DeviceParam p, int raw) => ParameterEdits.SetLive(p.Parameter, p.Track, Math.Clamp(raw, p.Min, p.Max));

        /// <summary>A value already set live (<paramref name="from"/> to <paramref name="to"/>) as one undo step.</summary>
        public static void Changed(ReBuzzCore core, DeviceParam p, int from, int to, bool stepped = false) =>
            ParameterEdits.Changed(core, p.Parameter, p.Track, from, to, stepped);

        /// <summary>Sets a value and records it as one undo step (a click on a choice, a typed value, a reset).</summary>
        public static void Set(ReBuzzCore core, DeviceParam p, int raw, bool stepped = false)
        {
            raw = Math.Clamp(raw, p.Min, p.Max);
            int from = p.Parameter.GetValue(p.Track);
            if (from == raw) return;
            ParameterEdits.SetLive(p.Parameter, p.Track, raw);
            ParameterEdits.Changed(core, p.Parameter, p.Track, from, raw, stepped);
        }

        /// <summary>Back to the parameter's default, one undo step.</summary>
        public static void Reset(ReBuzzCore core, DeviceParam p) => Set(core, p, p.Default);

        /// <summary>The step of the wheel and the arrows: 1, or a hundredth of a wide range.</summary>
        public static int Step(DeviceParam p) => Math.Max(1, (p.Max - p.Min) / 100);

        /// <summary>
        /// What was typed as a raw value: a number in the unit the value is shown in ("800 Hz", "-6 dB", "35%", "1/8"), a choice
        /// by name, or "raw:512"; the machine's own texts first where they say it. False with the reason when it isn't one.
        /// </summary>
        public static bool TryType(DeviceParam p, string text, out int raw, out string error)
        {
            raw = 0;
            error = null;
            text = (text ?? "").Trim();
            if (text.Length == 0)
            {
                error = "Type a value.";
                return false;
            }
            try
            {
                JsonNode input = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && !double.IsNaN(number) && !double.IsInfinity(number)
                    ? JsonValue.Create(number)
                    : JsonValue.Create(text);
                raw = Math.Clamp(ValueResolver.Resolve(p.Info, input, p.Parameter.GetValue(p.Track)), p.Min, p.Max);
                return true;
            }
            catch (ToolError e)
            {
                error = e.Message;
                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>Types a value: sets it as one undo step; the reason where it isn't one, null when it was set.</summary>
        public static string Type(ReBuzzCore core, DeviceParam p, string text)
        {
            if (!TryType(p, text, out int raw, out string error)) return error;
            Set(core, p, raw);
            return null;
        }
    }
}
