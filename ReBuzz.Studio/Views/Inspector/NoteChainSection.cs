// Fork (docs/specs/note-wires.md §6, NW1): the Inspector's "Notes" section of the selected machine: the track's note chain (the inserts
// before the instrument, in order, each with move up, move down and remove) and the free note wires into and out of the machine.

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

namespace ReBuzz.Studio.Views.Inspector
{
    internal sealed class NoteChainSection : StackPanel
    {
        readonly ReBuzzCore core;
        IMachine machine;
        bool attached;

        public NoteChainSection(ReBuzzCore core)
        {
            this.core = core;
            Margin = new Thickness(0, 10, 0, 0);
            IsVisible = false;
            AutomationProperties.SetName(this, "Note chain");
        }

        /// <summary>The rows shown, for tests: "1 Name" per insert, then "in: X" and "out: Y" for the free wires.</summary>
        public IReadOnlyList<string> Rows { get; private set; } = Array.Empty<string>();

        public void Show(IMachine m)
        {
            machine = m;
            if (!attached && core.NoteWires != null) { core.NoteWires.Changed += OnChanged; attached = true; }
            Rebuild();
        }

        public void Detach()
        {
            if (attached) core.NoteWires.Changed -= OnChanged;
            attached = false;
        }

        void OnChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);

        void Rebuild()
        {
            Children.Clear();
            var rows = new List<string>();
            var store = core.NoteWires;
            var ports = machine == null ? null : store?.PortsOf(machine.Name);
            if (machine == null || ports is not { Control: false } p || !(p.In || p.Out)) { IsVisible = false; Rows = rows; return; }
            IsVisible = true;
            Children.Add(new TextBlock { Text = "NOTES", FontSize = StudioTokens.FontCaption, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextSecondaryBrush, Margin = new Thickness(0, 0, 0, 4) });

            var state = store.State;
            string instrument = p.In ? machine.Name : null;
            string owner = state.InstrumentOf(machine.Name);
            if (owner != null) instrument = owner;
            if (instrument != null)
            {
                var chain = state.ChainOf(instrument);
                Children.Add(Label(chain.Count == 0 ? $"No note inserts before {instrument}. Notes go straight in." : $"Note chain into {instrument}, first to last:"));
                for (int i = 0; i < chain.Count; i++)
                {
                    int at = i;
                    string name = chain[i];
                    rows.Add($"{i + 1} {name}");
                    var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1) };
                    var remove = Small("Remove", "Take it out of the chain (its neighbours are joined)", () => store.RemoveInsert(name));
                    var down = Small("v", "Move later", () => store.AddInsert(instrument, name, at + 1), at < chain.Count - 1);
                    var up = Small("^", "Move earlier", () => store.AddInsert(instrument, name, at - 1), at > 0);
                    foreach (var b in new[] { remove, down, up }) DockPanel.SetDock(b, Dock.Right);
                    row.Children.Add(remove); row.Children.Add(down); row.Children.Add(up);
                    row.Children.Add(new TextBlock { Text = $"{i + 1}. {name}", FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextPrimaryBrush, VerticalAlignment = VerticalAlignment.Center });
                    Children.Add(row);
                }
            }
            foreach (var w in state.Wires.Where(w => w.To == machine.Name))
            {
                rows.Add("in: " + w.From);
                string from = w.From;
                Children.Add(WireRow($"From {from}", () => store.Disconnect(from, machine.Name)));
            }
            foreach (var w in state.Wires.Where(w => w.From == machine.Name))
            {
                rows.Add("out: " + w.To);
                string to = w.To;
                Children.Add(WireRow($"To {to}", () => store.Disconnect(machine.Name, to)));
            }
            Rows = rows;
        }

        static TextBlock Label(string text) => new TextBlock { Text = text, FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap };

        Avalonia.Controls.Control WireRow(string text, Action remove)
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1) };
            var b = Small("Remove", "Remove this note wire", remove);
            DockPanel.SetDock(b, Dock.Right);
            row.Children.Add(b);
            row.Children.Add(new TextBlock { Text = text + " (note wire)", FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextPrimaryBrush, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        static Button Small(string text, string tip, Action run, bool enabled = true)
        {
            var b = new Button { Content = text, Padding = new Thickness(6, 1), Margin = new Thickness(2, 0, 0, 0), FontSize = StudioTokens.FontCaption, IsEnabled = enabled };
            ToolTip.SetTip(b, tip);
            AutomationProperties.SetName(b, text == "v" ? "Move later" : text == "^" ? "Move earlier" : text);
            b.Click += (s, e) => run();
            return b;
        }
    }
}
