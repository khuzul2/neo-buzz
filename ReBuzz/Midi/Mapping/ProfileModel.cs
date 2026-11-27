#nullable disable
// Fork (docs/specs/midi-mapping.md, MM2): controller profiles. A profile is a data file (JSON): the controls of a controller with their message
// numbers, the LEDs they have, a default layout (what each control does) and the port-name patterns "Auto" matches. Nothing here knows a
// model: the two built-in profiles are files (ReBuzz/Midi/Profiles) like any a person adds. Pure (no ReBuzz types), so ReBuzz.SessionTests
// compiles this file and checks the built-in files against the makers' published protocols.
//
//   {
//     "id": "clip-grid-8x5", "name": "Clip-grid controller (8 x 5)", "match": ["APC40 mkII"],
//     "role": "controls",                       // keys, controls or both: what the controller is for when nothing says
//     "knobs": 8, "takeover": "pickup",
//     "sources": ["the makers' documents this was written from"],
//     "enter": ["F0 ... F7"], "exit": ["F0 ... F7"],     // sent when the profile starts and ends (the mode switch)
//     "controls": [ { "id": "fader", "action": "volume", "kind": "cc", "channel": 0, "number": 7, "count": 8, "channelStep": 1, "index": 1,
//                     "led": { "on": 127, "off": 0 } } ],
//     "grid": { "cols": 8, "rows": 5, "kind": "note", "first": 0, "rowStride": 8, "bottomRowFirst": true, "states": { ... } }
//   }
//
// A control with a "count" is that many controls: each step adds "channelStep", "numberStep" and "indexStep" (1 when it is not said, 0 for the
// channel and the number). Its "index" is what the action is about: the bank track (1 to 8), the knob (1 to N), the scene row (from 1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ReBuzz.Midi.Mapping
{
    /// <summary>What a controller is for: its keys (notes play the selected track), its controls (knobs, faders and buttons are mapped) or both.</summary>
    public enum DeviceRole : byte { Both, Keys, Controls }

    /// <summary>A control's LED: the message that lights it (the control's own kind, channel and number unless it says another) and what it shows.</summary>
    public sealed class ProfileLed
    {
        /// <summary>The value that lights it, and the value that puts it out (a ring's value is the control's position).</summary>
        public int On = 127, Off = 0;
        public MapKind? Kind;
        public int? Channel, Number;
        /// <summary>A ring's type message (what the ring shows: off, a dot, a volume bar, a pan), if the device has one.</summary>
        public int? TypeNumber, TypeChannel;
        public int TypeValue = 2, TypePanValue = 3;
    }

    /// <summary>One control of a profile.</summary>
    public sealed class ProfileControl
    {
        public string Id = "";
        public MapKind Kind = MapKind.ControlChange;
        public int Channel, Number;
        public string Action = "";
        public int Index;
        public ProfileLed Led;

        public MapSource Source(string device) => new MapSource(device ?? "", Kind, Channel, Number);
    }

    /// <summary>The pad grid's look of a clip state: the colour (a palette entry, or "clip": the clip's own), the channel that sets pulsing or blinking, and the colour it goes back to.</summary>
    public sealed class PadLook
    {
        public int Velocity = -1;           // -1: the clip's colour
        public int Channel;
        public int BaseVelocity = -2;       // -2: none; -1: the clip's colour
        public int BaseChannel;
    }

    /// <summary>A grid of clip pads.</summary>
    public sealed class ProfileGrid
    {
        public int Cols = 8, Rows = 5;
        public MapKind Kind = MapKind.Note;
        public int Channel, First, RowStride = 8;
        public bool BottomRowFirst = true;
        public readonly Dictionary<string, PadLook> States = new Dictionary<string, PadLook>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The control number of the pad in a column and a row (row 0 is the top).</summary>
        public int NumberOf(int column, int row) => First + (BottomRowFirst ? Rows - 1 - row : row) * RowStride + column;

        public PadLook Look(string state) => States.TryGetValue(state, out var l) ? l : new PadLook { Velocity = 0 };
    }

    public sealed class ControllerProfile
    {
        public string Id = "", Name = "";
        public string Description = "";
        public readonly List<string> Match = new List<string>();
        public DeviceRole Role = DeviceRole.Both;
        public int Knobs = 8;
        public Takeover Takeover = Takeover.Pickup;
        public readonly List<string> Sources = new List<string>();
        public readonly List<byte[]> Enter = new List<byte[]>(), Exit = new List<byte[]>();
        public readonly List<ProfileControl> Controls = new List<ProfileControl>();
        public ProfileGrid Grid;
        /// <summary>What was wrong in the file (a control left out and why); empty for a good one.</summary>
        public readonly List<string> Problems = new List<string>();

        public bool HasGrid => Grid != null;

        /// <summary>The controls of an action, by index.</summary>
        public IEnumerable<ProfileControl> Of(string action) => Controls.Where(c => c.Action == action).OrderBy(c => c.Index);

        /// <summary>Whether a port name is this controller's: it contains one of the patterns (case and spaces aside). The " #2" of a second device of the same name does not count.</summary>
        public bool Matches(string port)
        {
            if (string.IsNullOrWhiteSpace(port)) return false;
            string name = Squash(StripInstance(port));
            foreach (var m in Match)
                if (m.Length > 0 && name.Contains(Squash(m), StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The name of a controller as the OS reports it, without the " #2" a second one of the same name gets.</summary>
        public static string StripInstance(string port)
        {
            int at = port.LastIndexOf(" #", StringComparison.Ordinal);
            return at > 0 && int.TryParse(port.AsSpan(at + 2), NumberStyles.None, CultureInfo.InvariantCulture, out _) ? port.Substring(0, at) : port;
        }

        static string Squash(string s) => new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToLowerInvariant();

        // ------------------------------------------------------------------ reading

        /// <summary>Reads a profile; never throws. A file that is not a profile has an empty Id and a problem.</summary>
        public static ControllerProfile Parse(string json)
        {
            var p = new ControllerProfile();
            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { p.Problems.Add("A profile is a JSON object."); return p; }
                p.Id = Str(root, "id");
                p.Name = Str(root, "name");
                p.Description = Str(root, "description");
                if (p.Id.Length == 0) p.Problems.Add("A profile needs an id.");
                if (p.Name.Length == 0) p.Problems.Add("A profile needs a name.");
                foreach (var s in Strings(root, "match")) p.Match.Add(s);
                foreach (var s in Strings(root, "sources")) p.Sources.Add(s);
                p.Role = Str(root, "role").ToLowerInvariant() switch { "keys" => DeviceRole.Keys, "controls" => DeviceRole.Controls, _ => DeviceRole.Both };
                p.Knobs = Math.Clamp(Int(root, "knobs", 8), 1, 64);
                p.Takeover = Str(root, "takeover").ToLowerInvariant() switch { "jump" => Takeover.Jump, "scale" => Takeover.Scale, _ => Takeover.Pickup };
                foreach (var s in Strings(root, "enter")) AddSysEx(p, p.Enter, s);
                foreach (var s in Strings(root, "exit")) AddSysEx(p, p.Exit, s);
                if (root.TryGetProperty("controls", out var controls) && controls.ValueKind == JsonValueKind.Array)
                    foreach (var c in controls.EnumerateArray()) Expand(p, c);
                if (root.TryGetProperty("grid", out var grid) && grid.ValueKind == JsonValueKind.Object) p.Grid = ReadGrid(p, grid);
                CheckClashes(p);
            }
            catch (JsonException e)
            {
                p.Problems.Add("Not valid JSON: " + e.Message);
            }
            return p;
        }

        static void AddSysEx(ControllerProfile p, List<byte[]> into, string text)
        {
            try
            {
                var bytes = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();
                if (bytes.Length < 3 || bytes[0] != 0xF0 || bytes[^1] != 0xF7 || bytes.Skip(1).Take(bytes.Length - 2).Any(b => b > 0x7F)) throw new FormatException();
                into.Add(bytes);
            }
            catch (FormatException)
            {
                p.Problems.Add($"\"{text}\" is not a system exclusive message (F0 ... F7, data bytes up to 7F).");
            }
        }

        static void Expand(ControllerProfile p, JsonElement c)
        {
            // "anyChannel": the control is the same on every channel (a device whose channel is a setting).
            if (c.TryGetProperty("anyChannel", out var any) && any.ValueKind == JsonValueKind.True)
            {
                for (int ch = 0; ch < 16; ch++) ExpandOne(p, c, ch);
                return;
            }
            ExpandOne(p, c, Int(c, "channel", 0));
        }

        static void ExpandOne(ControllerProfile p, JsonElement c, int channel)
        {
            string id = Str(c, "id"), action = Str(c, "action");
            if (!MapSource.TryKind(Str(c, "kind", "cc"), out var kind)) { p.Problems.Add($"{id}: unknown kind."); return; }
            int count = Math.Clamp(Int(c, "count", 1), 1, 64);
            int number = Int(c, "number", -1), index = Int(c, "index", 0);
            int channelStep = Int(c, "channelStep", 0), numberStep = Int(c, "numberStep", 0), indexStep = Int(c, "indexStep", 1);
            if (action.Length == 0) { p.Problems.Add($"{id}: no action."); return; }
            ProfileLed led = null;
            if (c.TryGetProperty("led", out var l) && l.ValueKind == JsonValueKind.Object)
            {
                led = new ProfileLed { On = Int(l, "on", 127), Off = Int(l, "off", 0), TypeValue = Int(l, "typeValue", 2), TypePanValue = Int(l, "typePanValue", 3) };
                if (l.TryGetProperty("kind", out _) && MapSource.TryKind(Str(l, "kind"), out var lk)) led.Kind = lk;
                if (l.TryGetProperty("channel", out _)) led.Channel = Int(l, "channel", 0);
                if (l.TryGetProperty("number", out _)) led.Number = Int(l, "number", 0);
                if (l.TryGetProperty("typeNumber", out _)) led.TypeNumber = Int(l, "typeNumber", 0);
                if (l.TryGetProperty("typeChannel", out _)) led.TypeChannel = Int(l, "typeChannel", 0);
            }
            for (int i = 0; i < count; i++)
            {
                int ch = channel + i * channelStep, num = number + i * numberStep;
                if (ch < 0 || ch > 15 || num < 0 || num > 127) { p.Problems.Add($"{id} {i + 1}: channel {ch + 1}, number {num} is out of range."); continue; }
                p.Controls.Add(new ProfileControl
                {
                    Id = (count == 1 ? id : id + " " + (i + 1).ToString(CultureInfo.InvariantCulture)) + (c.TryGetProperty("anyChannel", out _) ? " ch" + (ch + 1).ToString(CultureInfo.InvariantCulture) : ""),
                    Kind = kind, Channel = ch, Number = num, Action = action, Index = index + i * indexStep,
                    Led = led == null ? null : new ProfileLed
                    {
                        On = led.On, Off = led.Off, Kind = led.Kind, TypeValue = led.TypeValue, TypePanValue = led.TypePanValue,
                        Channel = led.Channel.HasValue ? led.Channel + i * channelStep : null,
                        Number = led.Number.HasValue ? led.Number + i * numberStep : null,
                        TypeNumber = led.TypeNumber.HasValue ? led.TypeNumber + i * numberStep : null,
                        TypeChannel = led.TypeChannel.HasValue ? led.TypeChannel + i * channelStep : null,
                    },
                });
            }
        }

        static ProfileGrid ReadGrid(ControllerProfile p, JsonElement g)
        {
            var grid = new ProfileGrid
            {
                Cols = Math.Clamp(Int(g, "cols", 8), 1, 16), Rows = Math.Clamp(Int(g, "rows", 5), 1, 16),
                Channel = Int(g, "channel", 0), First = Int(g, "first", 0), RowStride = Int(g, "rowStride", 8),
                BottomRowFirst = !g.TryGetProperty("bottomRowFirst", out var b) || b.ValueKind != JsonValueKind.False,
            };
            if (MapSource.TryKind(Str(g, "kind", "note"), out var kind)) grid.Kind = kind;
            if (g.TryGetProperty("states", out var states) && states.ValueKind == JsonValueKind.Object)
                foreach (var s in states.EnumerateObject())
                {
                    var look = new PadLook { Velocity = Color(s.Value, "velocity", -1), Channel = Int(s.Value, "channel", 0), BaseVelocity = Color(s.Value, "base", -2), BaseChannel = Int(s.Value, "baseChannel", 0) };
                    grid.States[s.Name] = look;
                }
            if (grid.NumberOf(grid.Cols - 1, 0) > 127 || grid.NumberOf(0, grid.Rows - 1) < 0) p.Problems.Add("The grid's numbers run outside 0 to 127.");
            return grid;
        }

        /// <summary>A colour value: a palette entry, or the word "clip" (the clip's own).</summary>
        static int Color(JsonElement e, string name, int fallback)
        {
            if (!e.TryGetProperty(name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.String) return v.GetString() == "clip" ? -1 : fallback;
            return v.TryGetInt32(out int n) ? n : fallback;
        }

        /// <summary>A control number two controls share is a profile that cannot be right.</summary>
        static void CheckClashes(ControllerProfile p)
        {
            var seen = new Dictionary<(MapKind, int, int), string>();
            foreach (var c in p.Controls)
            {
                var key = (c.Kind, c.Channel, c.Number);
                if (seen.TryGetValue(key, out var other)) p.Problems.Add($"{c.Id} and {other} are the same control ({c.Source("").ShortControl}).");
                else seen[key] = c.Id;
            }
            if (p.Grid != null)
                for (int r = 0; r < p.Grid.Rows; r++)
                    for (int col = 0; col < p.Grid.Cols; col++)
                        if (seen.TryGetValue((p.Grid.Kind, p.Grid.Channel, p.Grid.NumberOf(col, r)), out var other))
                            p.Problems.Add($"The grid's pad {col + 1}/{r + 1} and {other} are the same control.");
        }

        static string Str(JsonElement e, string name, string fallback = "") =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

        static int Int(JsonElement e, string name, int fallback) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : fallback;

        static IEnumerable<string> Strings(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()) : Enumerable.Empty<string>();
    }

    /// <summary>The profiles in use: the built-in files and any a person adds, each by id.</summary>
    public sealed class ProfileCatalog
    {
        readonly List<ControllerProfile> profiles = new List<ControllerProfile>();

        public ProfileCatalog(IEnumerable<string> jsonTexts)
        {
            foreach (var text in jsonTexts)
            {
                var p = ControllerProfile.Parse(text);
                if (p.Id.Length == 0 || profiles.Any(o => string.Equals(o.Id, p.Id, StringComparison.OrdinalIgnoreCase))) continue;
                profiles.Add(p);
            }
        }

        public IReadOnlyList<ControllerProfile> All => profiles;

        public ControllerProfile Find(string id) => string.IsNullOrEmpty(id) ? null : profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The profile "Auto" picks for a port: the first whose patterns match it, or null.</summary>
        public ControllerProfile Auto(string port) => profiles.FirstOrDefault(p => p.Problems.Count == 0 && p.Matches(port));
    }

    /// <summary>What a person set for one controller (Preferences ▸ Controllers).</summary>
    public sealed class DeviceSetting
    {
        public const string AutoProfile = "", NoProfile = "none";

        public string Device = "";
        /// <summary>Null: the profile's role, else Both.</summary>
        public DeviceRole? Role;
        /// <summary>"" Auto, "none", or a profile's id.</summary>
        public string Profile = AutoProfile;
        /// <summary>The MIDI output that gets its lights: "" is the output of the same name.</summary>
        public string Output = "";
        /// <summary>Knobs in its group when it has none taught: 0 is the profile's.</summary>
        public int KnobGroup;
        public Takeover Takeover = Takeover.Pickup;

        public DeviceSetting Clone() => (DeviceSetting)MemberwiseClone();

        public bool IsDefault => Role == null && Profile == AutoProfile && Output == "" && KnobGroup == 0 && Takeover == Takeover.Pickup;
    }
}
