// Fork (docs/specs/studio-mvp.md S7): Preferences ▸ MIDI, as ReBuzz.exe's MIDI Input and MIDI
// Output pages: the devices the platform has (CoreMIDI on macOS; none on Linux), checked to open
// them, and the ones the settings remember that aren't connected now. The settings keep them by
// name ("MIDI In List", "MIDI Out List"), so a remembered device opens again when it is plugged in
// (the engine's hot-plug watcher); the page follows plugging while it shows. Below them, the MIDI
// settings (BuzzGUI's MIDISettings) as ReBuzz.exe's Settings ▸ MIDI has them.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using BuzzGUI.Common;
using ReBuzz.Common;
using ReBuzz.Core;
using ReBuzz.Midi;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class MidiPage : PrefsPage
    {
        /// <summary>A device row: its name, whether it is connected and open, and its box.</summary>
        internal sealed record DeviceRow(string Name, bool Connected, bool Open, CheckBox Box);

        readonly MidiEngine midi;
        readonly IRegistryEx registry;
        readonly StackPanel inputPanel = new StackPanel { Spacing = 2 };
        readonly StackPanel outputPanel = new StackPanel { Spacing = 2 };
        readonly DispatcherTimer plugging = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly List<DeviceRow> inputRows = new List<DeviceRow>(), outputRows = new List<DeviceRow>();
        string shown;

        public MidiPage(MidiEngine midi, IRegistryEx registry)
        {
            this.midi = midi;
            this.registry = registry;

            Heading("Inputs");
            Note("Checked inputs play the machine with MIDI focus (and armed Session View tracks).");
            Body.Children.Add(inputPanel);
            Heading("Outputs");
            Note("Checked outputs are what MIDI out machines and MIDI clock (Sync) send to.");
            Body.Children.Add(outputPanel);
            if (OperatingSystem.IsMacOS())
                Note($"Other apps also reach ReBuzz through its virtual \"{CoreMidiBackend.VirtualName}\" ports while it runs.");
            else if (midi.Backend is NoMidiBackend)
                Note("This system has no MIDI devices for ReBuzz (it uses CoreMIDI on macOS).");

            Heading("Options");
            AddSettings(Global.MIDISettings);

            plugging.Tick += (s, e) => Refresh();
        }

        public override string Title => "MIDI";

        internal IReadOnlyList<DeviceRow> Inputs => inputRows;
        internal IReadOnlyList<DeviceRow> Outputs => outputRows;

        protected override void OnShown()
        {
            shown = null;
            Refresh();
            plugging.Start();
        }

        public override void Closed() => plugging.Stop();

        /// <summary>Rebuilds the lists when devices came, went, opened or closed.</summary>
        public override void Refresh()
        {
            var backend = midi.Backend;
            var ins = Enumerable.Range(0, backend.InputCount).Select(backend.InputName).ToList();
            var outs = Enumerable.Range(0, backend.OutputCount).Select(backend.OutputName).ToList();
            var openIns = midi.GetMidiInputDevices().Where(i => i < ins.Count).Select(i => ins[i]).ToHashSet();
            var openOuts = midi.GetMidiOuts().Select(o => o.Item2).ToHashSet();
            var savedIns = registry.ReadDictionary("MIDI In List").KeyValuesToStringInt();
            var savedOuts = registry.ReadDictionary("MIDI Out List").KeyValuesToStringInt();
            string state = string.Join("|", ins) + "#" + string.Join("|", outs) + "#" + string.Join("|", openIns) + "#" + string.Join("|", openOuts) +
                           "#" + string.Join("|", savedIns.Select(kv => kv.Key + kv.Value)) + "#" + string.Join("|", savedOuts.Select(kv => kv.Key + kv.Value));
            if (state == shown) return;
            shown = state;
            Fill(inputPanel, inputRows, ins, openIns, savedIns, input: true);
            Fill(outputPanel, outputRows, outs, openOuts, savedOuts, input: false);
        }

        void Fill(StackPanel panel, List<DeviceRow> rows, List<string> connected, HashSet<string> open, Dictionary<string, int> saved, bool input)
        {
            panel.Children.Clear();
            rows.Clear();
            // Connected devices first, then the remembered ones that aren't (as ReBuzz.exe lists them).
            var names = connected.Distinct().Concat(saved.Keys.Where(k => !connected.Contains(k))).ToList();
            foreach (var name in names)
            {
                bool isConnected = connected.Contains(name);
                bool on = saved.TryGetValue(name, out int flags) && (flags & 1) != 0;
                var box = new CheckBox { Content = name, IsChecked = on, MinWidth = 280 };
                string state = !isConnected ? "not connected" : open.Contains(name) ? "open" : on ? "didn't open" : "";
                var row = new DockPanel { LastChildFill = false };
                var status = new TextBlock { Text = state, Foreground = state == "didn't open" ? Problem : Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                row.Children.Add(box);
                row.Children.Add(status);
                string device = name;
                box.IsCheckedChanged += (s, e) => Toggle(device, input, box.IsChecked == true);
                panel.Children.Add(row);
                rows.Add(new DeviceRow(name, isConnected, open.Contains(name), box));
            }
            if (names.Count == 0) panel.Children.Add(new TextBlock { Text = input ? "No MIDI inputs." : "No MIDI outputs.", Foreground = Dim, Margin = new Thickness(4, 2) });
        }

        /// <summary>
        /// A device on or off, saved by name; then the inputs (or outputs) open again as the
        /// settings say, as ReBuzz.exe's Preferences OK does.
        /// </summary>
        void Toggle(string name, bool input, bool on)
        {
            var flags = new Dictionary<string, int> { [name] = on ? 1 : 0 };
            if (input)
            {
                midi.SetMidiInputDevices(flags);
                midi.DisposeMidiIn();
                midi.OpenMidiInDevices();
            }
            else
            {
                midi.SetMidiOutputDevices(flags);
                midi.DisposeMidiOuts();
                midi.OpenMidiOutDevices();
            }
            Dispatcher.UIThread.Post(Refresh);
        }
    }
}
