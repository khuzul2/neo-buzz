// Fork (docs/specs/midi-mapping.md, MM2): controllers. What a person set for each MIDI input (its role, profile, output, knob group and takeover,
// saved with the computer's map), the profiles in use, and the rows Preferences ▸ Controllers and the agent read and edit. A profile's routes and
// lights are made by Rebuild (MidiMapping.Router.cs, MidiMapping.Feedback.cs); this file is the settings side.

using ReBuzz.Midi;
using ReBuzz.Midi.Mapping;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace ReBuzz.Core
{
    /// <summary>One MIDI input as Preferences ▸ Controllers shows it.</summary>
    internal sealed class ControllerRow
    {
        public string Device = "";
        /// <summary>The input is open now.</summary>
        public bool Connected;
        /// <summary>The role in use (what was set, else the profile's, else Both).</summary>
        public DeviceRole Role;
        /// <summary>What was set for the role (null: from the profile).</summary>
        public DeviceRole? RoleSet;
        /// <summary>What was set for the profile: "" (Auto), "none" or an id.</summary>
        public string ProfileSet = "";
        /// <summary>The profile in use: its id, or "" for none.</summary>
        public string ProfileId = "";
        public string ProfileName = "None";
        /// <summary>What was set for the paired output ("" the output of the same name), and the output that gets the lights ("" none).</summary>
        public string OutputSet = "", Output = "";
        /// <summary>Knobs in the device's group (taught, or the profile's) and the size that was set (0: all).</summary>
        public int Knobs, KnobGroup;
        public Takeover Takeover;
        /// <summary>The last message it sent, as text ("CC 21 · ch 1 · 64"), or "".</summary>
        public string Last = "";
        /// <summary>A control surface script (the Session View's own) has the device: a profile is not used on it.</summary>
        public bool Claimed;
    }

    internal sealed partial class MidiMapping
    {
        // ------------------------------------------------------------------ the profiles

        ProfileCatalog catalog;

        /// <summary>A folder of profile files a person adds (JSON, as the built-in ones). For tests it can be set.</summary>
        internal string ProfileFolder { get; set; } = Path.Combine(AppContext.BaseDirectory, "Profiles");

        /// <summary>The profiles in use: the built-in files (embedded) and the ones in the profile folder.</summary>
        public ProfileCatalog Profiles => catalog ??= LoadProfiles();

        /// <summary>Reads the profiles again (a file was added).</summary>
        public void ReloadProfiles()
        {
            catalog = null;
            Rebuild();
            Changed?.Invoke();
        }

        ProfileCatalog LoadProfiles()
        {
            var texts = new List<string>();
            var assembly = typeof(MidiMapping).Assembly;
            foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("ReBuzz.Profiles.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
            {
                try
                {
                    using var stream = assembly.GetManifestResourceStream(name);
                    if (stream == null) continue;
                    using var reader = new StreamReader(stream);
                    texts.Add(reader.ReadToEnd());
                }
                catch (Exception e) { buzz.DCWriteLine("Controller profile " + name + ": " + e.Message, BuzzGUI.Interfaces.DCLogLevel.Warning); }
            }
            try
            {
                if (Directory.Exists(ProfileFolder))
                    foreach (var file in Directory.GetFiles(ProfileFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                        texts.Add(File.ReadAllText(file));
            }
            catch (Exception e) { buzz.DCWriteLine("Controller profiles: " + e.Message, BuzzGUI.Interfaces.DCLogLevel.Warning); }
            return new ProfileCatalog(texts);
        }

        /// <summary>The profile a device uses: what was set, else the one its port name matches (Auto), else none.</summary>
        public ControllerProfile ProfileOf(string device)
        {
            var set = computer.SettingOf(device)?.Profile ?? DeviceSetting.AutoProfile;
            if (string.Equals(set, DeviceSetting.NoProfile, StringComparison.OrdinalIgnoreCase)) return null;
            if (set.Length > 0) return Profiles.Find(set) is { Problems.Count: 0 } chosen ? chosen : null;
            return Profiles.Auto(device);
        }

        /// <summary>The role of a device: what was set, else (a device left out of the keys by MM1's role) controls, else the profile's, else both.</summary>
        public DeviceRole RoleOf(string device) => RoleOf(device, ProfileOf(device));

        DeviceRole RoleOf(string device, ControllerProfile profile) =>
            computer.SettingOf(device)?.Role ?? (computer.NoKeys.Contains(device, StringComparer.OrdinalIgnoreCase) ? DeviceRole.Controls : profile?.Role ?? DeviceRole.Both);

        /// <summary>The takeover a control of this device is mapped with unless the map says another.</summary>
        public Takeover TakeoverOf(string device) => computer.SettingOf(device)?.Takeover ?? Takeover.Pickup;

        // ------------------------------------------------------------------ the rows

        /// <summary>One row for every MIDI input open now and every device the settings, a map or a group name.</summary>
        public IReadOnlyList<ControllerRow> ControllerRows()
        {
            var ids = new List<string>();
            var open = buzz.MidiInOutEngine?.OpenInputKeys() ?? Array.Empty<string>();
            ids.AddRange(open);
            var c = computer;
            foreach (var d in c.Groups.Select(g => g.Device).Concat(c.NoKeys).Concat(c.Devices.Select(d => d.Device)).Concat((song.Global ?? c.Global).Select(e => e.Source.Device)).Where(d => !string.IsNullOrEmpty(d)))
                if (!ids.Contains(d, StringComparer.OrdinalIgnoreCase)) ids.Add(d);
            var rows = new List<ControllerRow>();
            foreach (var id in ids)
            {
                var setting = c.SettingOf(id);
                var profile = ProfileOf(id);
                string output = OutputOf(id, profile);
                rows.Add(new ControllerRow
                {
                    Device = id,
                    Connected = open.Contains(id, StringComparer.OrdinalIgnoreCase),
                    Role = RoleOf(id, profile),
                    RoleSet = setting?.Role,
                    ProfileSet = setting?.Profile ?? DeviceSetting.AutoProfile,
                    ProfileId = profile?.Id ?? "",
                    ProfileName = profile?.Name ?? "None",
                    OutputSet = setting?.Output ?? "",
                    Output = output,
                    Knobs = c.GroupOf(id)?.Count ?? (profile == null ? 0 : profile.Of("knob").Count()),
                    KnobGroup = setting?.KnobGroup ?? 0,
                    Takeover = setting?.Takeover ?? Takeover.Pickup,
                    Last = LastMessageText(id),
                    Claimed = buzz.SessionControllers?.ClaimsInput(id) == true,
                });
            }
            return rows;
        }

        /// <summary>The output names a controller's lights can go to.</summary>
        public IReadOnlyList<string> OutputNames() => Output.Names();

        /// <summary>The output that gets a device's lights: what was set, else the output of the same name (without " #2"), if there is one.</summary>
        string OutputOf(string device, ControllerProfile profile)
        {
            string set = computer.SettingOf(device)?.Output ?? "";
            var names = Output.Names();
            if (set.Length > 0) return names.Contains(set, StringComparer.OrdinalIgnoreCase) ? set : "";
            if (profile == null) return "";
            string same = ControllerProfile.StripInstance(device);
            return names.FirstOrDefault(n => string.Equals(n, same, StringComparison.OrdinalIgnoreCase)) ?? "";
        }

        // ------------------------------------------------------------------ the settings (one undo step each)

        /// <summary>Why a profile choice is refused, or null: "" (Auto), "none" or the id of a profile in use.</summary>
        public string ValidateProfile(string profile)
        {
            if (profile == null) return "Name a profile: auto, none or one of " + ProfileIds() + ".";
            if (profile.Length == 0 || profile.Equals("auto", StringComparison.OrdinalIgnoreCase) || profile.Equals(DeviceSetting.NoProfile, StringComparison.OrdinalIgnoreCase)) return null;
            var found = Profiles.Find(profile);
            if (found == null) return $"There is no profile \"{profile}\". Use auto, none or one of {ProfileIds()}.";
            if (found.Problems.Count > 0) return $"The profile \"{profile}\" has a problem: {found.Problems[0]}";
            return null;
        }

        string ProfileIds() => string.Join(", ", Profiles.All.Where(p => p.Problems.Count == 0).Select(p => p.Id));

        /// <summary>Several settings of one controller at once (what is not null is set); one undo step.</summary>
        internal sealed class ControllerChange
        {
            /// <summary>"auto" (or ""), "none" or a profile's id.</summary>
            public string Profile;
            public DeviceRole? Role;
            /// <summary>The role goes back to the profile's.</summary>
            public bool ClearRole;
            public string Output;
            public int? KnobGroup;
            public Takeover? Takeover;
        }

        /// <summary>Sets what <paramref name="change"/> names for a device, as one undo step. Returns the error (nothing is changed then), or null.</summary>
        internal string SetController(string device, ControllerChange change)
        {
            if (string.IsNullOrEmpty(device)) return "Name the controller (see controller_list).";
            string profile = null;
            if (change.Profile != null)
            {
                string error = ValidateProfile(change.Profile);
                if (error != null) return error;
                profile = change.Profile.Length == 0 || change.Profile.Equals("auto", StringComparison.OrdinalIgnoreCase) ? DeviceSetting.AutoProfile
                    : change.Profile.Equals(DeviceSetting.NoProfile, StringComparison.OrdinalIgnoreCase) ? DeviceSetting.NoProfile : Profiles.Find(change.Profile).Id;
            }
            if (change.Output != null && change.Output.Length > 0 && !Output.Names().Contains(change.Output, StringComparer.OrdinalIgnoreCase))
                return $"There is no MIDI output \"{change.Output}\". The outputs are: " + (Output.Names().Count == 0 ? "none" : string.Join(", ", Output.Names())) + ".";
            if (change.KnobGroup is < 0 or > 64) return "A knob group has 0 to 64 knobs (0: all of them).";
            Edit((c, s) =>
            {
                var d = c.SettingOf(device);
                if (d == null) { d = new DeviceSetting { Device = device }; c.Devices.Add(d); }
                if (profile != null) d.Profile = profile;
                if (change.Role != null || change.ClearRole)
                {
                    c.NoKeys.Remove(device);
                    d.Role = change.Role;
                }
                if (change.Output != null) d.Output = change.Output;
                if (change.KnobGroup != null) d.KnobGroup = change.KnobGroup.Value;
                if (change.Takeover != null) d.Takeover = change.Takeover.Value;
                if (d.IsDefault) c.Devices.Remove(d);
            });
            return null;
        }

        /// <summary>Sets the profile of a device: "auto" (or ""), "none" or an id. Returns the error, or null.</summary>
        public string SetControllerProfile(string device, string profile) => SetController(device, new ControllerChange { Profile = profile ?? "\0" });

        /// <summary>Sets the role of a device (null: from its profile). The old "keys" role of MM1 follows it.</summary>
        public string SetControllerRole(string device, DeviceRole? role) => SetController(device, new ControllerChange { Role = role, ClearRole = role == null });

        /// <summary>Sets the output that gets the device's lights ("" the output of the same name).</summary>
        public string SetControllerOutput(string device, string output) => SetController(device, new ControllerChange { Output = output ?? "" });

        /// <summary>Sets how many knobs a device's group has (0: all of the profile's, or of the taught group).</summary>
        public string SetControllerKnobGroup(string device, int knobs) => SetController(device, new ControllerChange { KnobGroup = knobs });

        /// <summary>Sets the takeover a control of the device is mapped with unless the map says another.</summary>
        public string SetControllerTakeover(string device, Takeover takeover) => SetController(device, new ControllerChange { Takeover = takeover });

        // ------------------------------------------------------------------ the last message

        readonly ConcurrentDictionary<string, int[]> lastIn = new ConcurrentDictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>MIDI thread: remembers the last message of a device (no allocation once the device has been heard).</summary>
        void NoteMessage(MidiDevice device, int data)
        {
            string id = device.Id;
            if (id.Length == 0) return;
            if (!lastIn.TryGetValue(id, out var cell)) cell = lastIn.GetOrAdd(id, _ => new int[2]);
            Volatile.Write(ref cell[0], data);
            Interlocked.Increment(ref cell[1]);
        }

        /// <summary>The last message a device sent, and how many it has sent since the app started.</summary>
        public (int Data, int Count) LastMessageOf(string device) => lastIn.TryGetValue(device ?? "", out var cell) ? (Volatile.Read(ref cell[0]), Volatile.Read(ref cell[1])) : (0, 0);

        string LastMessageText(string device)
        {
            var (data, count) = LastMessageOf(device);
            if (count == 0) return "";
            if (!MapSource.TryDecode(data, out var kind, out int channel, out int number, out int value, out _))
                return "Message " + (data & 0xFF).ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
            string control = new MapSource("", kind, channel, number).ShortControl;
            return control + " · ch " + (channel + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " · " + value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
