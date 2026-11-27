// Fork (docs/specs/multi-output.md): the Inspector's "Wires" section of the selected machine when a wire leaves one of several outputs: each
// such wire with a "From output" picker of the source's named outputs. Choosing one moves the wire, one undo step. A machine with one
// output has none of this (nothing to pick), so the section is hidden.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Machines;

namespace ReBuzz.Studio.Views.Inspector
{
    internal sealed class WireOutputsSection : StackPanel
    {
        readonly ReBuzzCore core;
        IMachine machine;
        string signature = "";
        bool building;
        readonly List<(IMachineConnection Wire, ComboBox Picker)> pickers = new();

        public WireOutputsSection(ReBuzzCore core)
        {
            this.core = core;
            Margin = new Thickness(0, 10, 0, 0);
            IsVisible = false;
            AutomationProperties.SetName(this, "Wire outputs");
        }

        /// <summary>The rows shown, for tests: "To X: Low" and "From Y: High" per wire from a several-output source.</summary>
        public IReadOnlyList<string> Rows { get; private set; } = Array.Empty<string>();

        /// <summary>The From output pickers shown, for tests, each with its wire.</summary>
        public IReadOnlyList<(IMachineConnection Wire, ComboBox Picker)> Pickers => pickers;

        static string NameOf(IMachine m, int i)
        {
            string name = m.GetChannelName(false, i);
            return string.IsNullOrWhiteSpace(name) ? "Output " + (i + 1) : name;
        }

        IEnumerable<IMachineConnection> WiresOf(IMachine m) =>
            m == null ? Enumerable.Empty<IMachineConnection>()
            : m.Outputs.Where(w => w.Source.OutputChannelCount > 1).Concat(m.Inputs.Where(w => w.Source.OutputChannelCount > 1 && !w.Source.Equals(m)));

        public void Show(IMachine m)
        {
            machine = m;
            var wires = WiresOf(m).ToList();
            string now = (m?.Name ?? "") + "|" + string.Join(";", wires.Select(w => w.Source.Name + ">" + w.Destination.Name + ":" + w.SourceChannel + ":" +
                string.Join(",", Enumerable.Range(0, w.Source.OutputChannelCount).Select(i => NameOf(w.Source, i)))));
            if (now == signature) return;
            signature = now;
            Rebuild(wires);
        }

        void Rebuild(List<IMachineConnection> wires)
        {
            building = true;
            Children.Clear();
            pickers.Clear();
            var rows = new List<string>();
            IsVisible = wires.Count > 0;
            if (wires.Count > 0)
            {
                Children.Add(new TextBlock { Text = "WIRES", FontSize = StudioTokens.FontCaption, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextSecondaryBrush, Margin = new Thickness(0, 0, 0, 4) });
                foreach (var wire in wires)
                {
                    bool outgoing = wire.Source == machine;
                    string other = outgoing ? wire.Destination.Name : wire.Source.Name;
                    var names = Enumerable.Range(0, wire.Source.OutputChannelCount).Select(i => NameOf(wire.Source, i)).ToList();
                    rows.Add((outgoing ? "To " : "From ") + other + ": " + names[Math.Min(wire.SourceChannel, names.Count - 1)]);
                    Children.Add(new TextBlock
                    {
                        Text = outgoing ? "To " + other : "From " + other,
                        FontSize = StudioTokens.FontBody, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextPrimaryBrush, Margin = new Thickness(0, 4, 0, 1),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1) };
                    var label = new TextBlock { Text = "From output", FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                    DockPanel.SetDock(label, Dock.Left);
                    var picker = new ComboBox
                    {
                        ItemsSource = names.Select((n, i) => $"{i + 1}  {n}").ToList(),
                        SelectedIndex = Math.Min(wire.SourceChannel, names.Count - 1),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        FontSize = StudioTokens.FontBody,
                    };
                    AutomationProperties.SetName(picker, $"From output of the wire to {wire.Destination.Name}");
                    ToolTip.SetTip(picker, $"Which of {wire.Source.Name}'s outputs this wire carries to {wire.Destination.Name}");
                    var captured = wire;
                    picker.SelectionChanged += (s, e) =>
                    {
                        if (building || picker.SelectedIndex < 0 || picker.SelectedIndex == captured.SourceChannel) return;
                        MachineEdits.SetWireOutput(core, captured, picker.SelectedIndex);
                    };
                    row.Children.Add(label);
                    row.Children.Add(picker);
                    Children.Add(row);
                    pickers.Add((wire, picker));
                }
            }
            Rows = rows;
            building = false;
        }
    }
}
