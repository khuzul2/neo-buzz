// Fork (docs/specs/midi-mapping.md, MM2): Preferences ▸ Controllers. One card for every MIDI input: whether it is used (its box is the MIDI page's), what it is for (keys, controls
// or both), its profile (Auto matches the port name; None works through maps alone), the output its lights go to, how many knobs its group has, the takeover its maps get, and the
// last message it sent (live). A change is one undo step of the song's history, as the map's own edits are; the controls are changed in place, so the keyboard focus stays where
// it is while the page follows the state. Tab goes from control to control, a list opens with Alt+Down or Space.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Common;
using ReBuzz.Common;
using ReBuzz.Core;
using ReBuzz.Midi;
using ReBuzz.Midi.Mapping;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class ControllersPage : PrefsPage
    {
        /// <summary>A controller's card and the controls on it.</summary>
        internal sealed class DeviceCard
        {
            public string Device = "";
            public Border Box;
            public CheckBox Use;
            public TextBlock State, Last;
            public ComboBox Role, Profile, Output, Takeover;
            public NumericUpDown Knobs;
        }

        const string AutoOutput = "Output of the same name";

        readonly MidiEngine midi;
        readonly IRegistryEx registry;
        readonly MidiMapping mapping;
        readonly StackPanel cards = new StackPanel { Spacing = 8 };
        readonly List<DeviceCard> deviceCards = new List<DeviceCard>();
        string structure;

        public ControllersPage(MidiMapping mapping, MidiEngine midi, IRegistryEx registry, PrefsModel model) : base(model)
        {
            this.mapping = mapping;
            this.midi = midi;
            this.registry = registry;

            Heading("Controllers");
            Note("Each MIDI input is a controller. A profile says what its faders, knobs, buttons and pads do and light, and it adds to your own maps (a control you mapped keeps what you gave it). " +
                 "With none, a controller works through maps alone.");
            Body.Children.Add(cards);
            PollWhileShown(TimeSpan.FromMilliseconds(250));
        }

        public override string Title => "Controllers";

        /// <summary>Restore Defaults: every controller goes back to Auto, its profile's role, no output chosen, all its knobs, pickup.</summary>
        public override void RestoreOwn()
        {
            foreach (var row in mapping.ControllerRows())
            {
                mapping.SetController(row.Device, new MidiMapping.ControllerChange { Profile = "auto", ClearRole = true, Output = "", KnobGroup = 0, Takeover = Takeover.Pickup });
            }
        }

        internal IReadOnlyList<DeviceCard> Cards => deviceCards;

        protected override void OnShown()
        {
            structure = null;
            Refresh();
        }

        /// <summary>The cards follow the controllers: new ones when a device comes, the state and the last message of each as it is.</summary>
        public override void Refresh()
        {
            var rows = mapping.ControllerRows();
            var backend = midi.Backend;
            var names = new List<string>();
            try { for (int i = 0; i < backend.InputCount; i++) names.Add(backend.InputName(i)); } catch (Exception) { }
            var saved = registry.ReadDictionary("MIDI In List").KeyValuesToStringInt();
            var devices = rows.Select(r => r.Device).Concat(names).Concat(saved.Keys).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var profiles = mapping.Profiles.All.Where(p => p.Problems.Count == 0).ToList();
            var outputs = mapping.OutputNames();
            string now = string.Join("|", devices) + "#" + string.Join("|", profiles.Select(p => p.Id)) + "#" + string.Join("|", outputs);
            if (now != structure)
            {
                structure = now;
                Build(devices, profiles, outputs);
            }
            Filling = true;
            try
            {
                foreach (var card in deviceCards)
                {
                    var row = rows.FirstOrDefault(r => string.Equals(r.Device, card.Device, StringComparison.OrdinalIgnoreCase));
                    Apply(card, row, saved, profiles, outputs);
                }
            }
            finally
            {
                Filling = false;
            }
        }

        void Build(List<string> devices, List<ControllerProfile> profiles, IReadOnlyList<string> outputs)
        {
            cards.Children.Clear();
            deviceCards.Clear();
            if (devices.Count == 0)
            {
                cards.Children.Add(new TextBlock { Text = "No MIDI inputs.", Foreground = Dim, Margin = new Thickness(4, 2) });
                return;
            }
            foreach (var device in devices)
            {
                var card = new DeviceCard { Device = device };
                card.Use = new CheckBox { Content = device, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(card.Use, device + ": use this input");
                card.Use.IsCheckedChanged += (s, e) => { if (!Filling) Toggle(card.Device, card.Use.IsChecked == true); };
                card.State = new TextBlock { Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                card.Last = new TextBlock { Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MinWidth = 160 };
                AutomationProperties.SetName(card.Last, device + ": last message");

                card.Role = Combo(device, "Role", new[] { "Keys", "Controls", "Both" }, 100);
                card.Role.SelectionChanged += (s, e) =>
                {
                    if (Filling || card.Role.SelectedIndex < 0) return;
                    mapping.SetControllerRole(card.Device, (DeviceRole)new[] { DeviceRole.Keys, DeviceRole.Controls, DeviceRole.Both }[card.Role.SelectedIndex]);
                    Dispatcher.UIThread.Post(Refresh);
                };
                card.Profile = Combo(device, "Profile", new[] { "Auto", "None" }.Concat(profiles.Select(p => p.Name)).ToArray(), 220);
                card.Profile.SelectionChanged += (s, e) =>
                {
                    int i = card.Profile.SelectedIndex;
                    if (Filling || i < 0) return;
                    mapping.SetControllerProfile(card.Device, i == 0 ? "auto" : i == 1 ? "none" : profiles[i - 2].Id);
                    Dispatcher.UIThread.Post(Refresh);
                };
                card.Output = Combo(device, "Output for lights", new[] { AutoOutput }.Concat(outputs).ToArray(), 220);
                card.Output.SelectionChanged += (s, e) =>
                {
                    int i = card.Output.SelectedIndex;
                    if (Filling || i < 0) return;
                    mapping.SetControllerOutput(card.Device, i == 0 ? "" : outputs[i - 1]);
                    Dispatcher.UIThread.Post(Refresh);
                };
                card.Knobs = new NumericUpDown { Minimum = 0, Maximum = 64, Increment = 1, FormatString = "0", Width = 110, VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(card.Knobs, device + ": knobs in its group (0 is all)");
                card.Knobs.ValueChanged += (s, e) =>
                {
                    if (Filling || card.Knobs.Value == null) return;
                    mapping.SetControllerKnobGroup(card.Device, (int)card.Knobs.Value.Value);
                    Dispatcher.UIThread.Post(Refresh);
                };
                card.Takeover = Combo(device, "Default takeover", new[] { "Pickup", "Jump", "Scale" }, 100);
                card.Takeover.SelectionChanged += (s, e) =>
                {
                    int i = card.Takeover.SelectedIndex;
                    if (Filling || i < 0) return;
                    mapping.SetControllerTakeover(card.Device, (Takeover)i);
                    Dispatcher.UIThread.Post(Refresh);
                };

                var header = new DockPanel { LastChildFill = false };
                DockPanel.SetDock(card.Last, Dock.Right);
                header.Children.Add(card.Use);
                header.Children.Add(card.State);
                header.Children.Add(card.Last);
                var fields = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                fields.Children.Add(Field("Role", card.Role));
                fields.Children.Add(Field("Profile", card.Profile));
                fields.Children.Add(Field("Output for lights", card.Output));
                fields.Children.Add(Field("Knobs (0: all)", card.Knobs));
                fields.Children.Add(Field("Default takeover", card.Takeover));
                var body = new StackPanel { Children = { header, fields } };
                card.Box = new Border
                {
                    Child = body, Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                    BorderBrush = Design.StudioTokens.SurfaceBorderBrush, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
                };
                cards.Children.Add(card.Box);
                deviceCards.Add(card);
            }
        }

        static ComboBox Combo(string device, string label, string[] items, double width)
        {
            var box = new ComboBox { ItemsSource = items, Width = width, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(box, device + ": " + label);
            return box;
        }

        static StackPanel Field(string label, Avalonia.Controls.Control control) => new StackPanel
        {
            Margin = new Thickness(0, 0, 14, 4),
            Children = { new TextBlock { Text = label, Foreground = Dim, FontSize = 12, Margin = new Thickness(2, 0, 0, 2) }, control },
        };

        void Apply(DeviceCard card, ControllerRow row, Dictionary<string, int> saved, List<ControllerProfile> profiles, IReadOnlyList<string> outputs)
        {
            bool on = saved.TryGetValue(card.Device, out int flags) && (flags & 1) != 0;
            bool connected = row?.Connected == true;
            card.Use.IsChecked = on;
            card.State.Text = !connected ? (on ? "not connected" : "") : row.Claimed ? "run by the Session View's own controller script" : "open";
            card.Last.Text = row == null || row.Last.Length == 0 ? "" : "Last: " + row.Last;
            if (row == null) row = new ControllerRow { Device = card.Device };
            card.Role.SelectedIndex = row.Role switch { DeviceRole.Keys => 0, DeviceRole.Controls => 1, _ => 2 };
            card.Profile.SelectedIndex = row.ProfileSet.Length == 0 ? 0 : row.ProfileSet == DeviceSetting.NoProfile ? 1 : Math.Max(0, profiles.FindIndex(p => string.Equals(p.Id, row.ProfileSet, StringComparison.OrdinalIgnoreCase)) + 2);
            card.Output.SelectedIndex = row.OutputSet.Length == 0 ? 0 : Math.Max(0, outputs.ToList().FindIndex(o => string.Equals(o, row.OutputSet, StringComparison.OrdinalIgnoreCase)) + 1);
            card.Knobs.Value = row.KnobGroup;
            card.Takeover.SelectedIndex = (int)row.Takeover;
            string used = row.ProfileId.Length == 0 ? "no profile" : row.ProfileName;
            ToolTip.SetTip(card.Profile, row.ProfileSet.Length == 0 ? "Auto uses: " + used : "Chosen: " + used);
            ToolTip.SetTip(card.Output, row.Output.Length == 0 ? "No output gets its lights" : "Its lights go to: " + row.Output);
        }

        /// <summary>A device on or off (saved by name, as the MIDI page does); then the inputs open again as the settings say.</summary>
        void Toggle(string name, bool on)
        {
            midi.SetMidiInputDevices(new Dictionary<string, int> { [name] = on ? 1 : 0 });
            midi.DisposeMidiIn();
            midi.OpenMidiInDevices();
            Dispatcher.UIThread.Post(Refresh);
        }
    }
}
