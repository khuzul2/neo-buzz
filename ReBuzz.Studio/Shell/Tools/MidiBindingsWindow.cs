// Fork (docs/specs/ux-overhaul.md P6b, §9): View ▸ MIDI Bindings, after the Machine View's MIDI Bindings tab: every parameter that a MIDI
// controller drives, by machine, parameter and track, with its channel (1 to 16) and controller (0 to 127), a filter, and a Clear
// button on each row (and Clear all). Binding itself is done in the parameter window, as before, or by the agent (midi_bind,
// midi_learn). The list is the one the tools give (MidiBackend.List); clearing is one undo step (MidiBackend.Unbind).

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using ReBuzz.Agent;
using ReBuzz.Agent.App;
using ReBuzz.Studio.Shell.Design;


namespace ReBuzz.Studio.Shell.Tools
{
    internal sealed class MidiBindingsWindow : ToolWindow
    {
        public const string PlacementName = "MidiBindings";
        static MidiBindingsWindow current;

        readonly Grid grid = new Grid();
        internal readonly TextBox Filter = new TextBox { Watermark = "Filter by machine or parameter", MinWidth = 240 };
        internal readonly Button ClearAll = new Button { Content = "Clear all" };
        readonly TextBlock empty = Label("No MIDI controller is bound to a parameter. Bind one from a machine's parameter window.", secondary: true);
        readonly TextBlock count = Label("", secondary: true);
        string shown;
        List<MidiBindingInfo> rows = new List<MidiBindingInfo>();

        MidiBindingsWindow(StudioHost host) : base(host, "MIDI Bindings", PlacementName, 640, 380, refreshMs: 500)
        {
            AutomationProperties.SetName(this, "MIDI Bindings");
            AutomationProperties.SetName(Filter, "Filter the bindings");
            ToolTip.SetTip(ClearAll, "Remove every binding of the song (one undo step per machine)");
            Filter.PropertyChanged += (s, e) => { if (e.Property == TextBox.TextProperty) { shown = null; Tick(); } };
            ClearAll.Click += (s, e) => Clear(rows);
            foreach (var w in new[] { new GridLength(160), new GridLength(180), new GridLength(60), new GridLength(70), new GridLength(80), GridLength.Auto })
                grid.ColumnDefinitions.Add(new ColumnDefinition(w));
            empty.Margin = new Thickness(12, 8);
            empty.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(12, 8), Children = { Filter, ClearAll, count } };
            var dock = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(new ScrollViewer { Content = new StackPanel { Children = { grid, empty } } });
            Content = dock;
            Closed += (s, e) => { if (current == this) current = null; };
        }

        /// <summary>Opens the list (or brings it to the front).</summary>
        public static MidiBindingsWindow Open(StudioHost host, Window owner = null)
        {
            if (current != null)
            {
                current.Activate();
                return current;
            }
            current = new MidiBindingsWindow(host);
            ShowOver(current, owner);
            return current;
        }

        internal static MidiBindingsWindow Current => current;

        /// <summary>The rows as "machine parameter track: Chan 1, Ctrl 7" lines (tests).</summary>
        internal IReadOnlyList<string> Lines => rows.Select(Describe).ToList();

        static string Describe(MidiBindingInfo b) => $"{b.Machine} {b.Parameter} {b.Track}: Chan {b.Channel}, Ctrl {b.Controller}";

        MidiBackend Backend => new MidiBackend(Core);

        void Clear(IEnumerable<MidiBindingInfo> which)
        {
            var core = Core;
            if (core == null) return;
            var backend = Backend;
            foreach (var b in which.ToList()) backend.Unbind(b.Machine, b.Parameter, b.Track);
            shown = null;
            Tick();
        }

        /// <summary>Clears the binding on row <paramref name="index"/> (the row's button).</summary>
        internal void ClearRow(int index)
        {
            if (index >= 0 && index < rows.Count) Clear(new[] { rows[index] });
        }

        internal override void Tick()
        {
            if (Core?.SongCore == null) return;
            string filter = Filter.Text?.Trim();
            var all = Backend.List(null);
            var list = all.Where(b => string.IsNullOrEmpty(filter) || b.Machine.Contains(filter, StringComparison.OrdinalIgnoreCase) || b.Parameter.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            string key = string.Join("\n", list.Select(Describe));
            if (key == shown) return;
            shown = key;
            rows = list;
            count.Text = all.Count == 0 ? "" : list.Count == all.Count ? $"{all.Count} binding{(all.Count == 1 ? "" : "s")}" : $"{list.Count} of {all.Count}";
            ClearAll.IsEnabled = list.Count > 0;
            empty.IsVisible = all.Count == 0;
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            string[] heads = { "Machine", "Parameter", "Track", "Channel", "Controller" };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < heads.Length; c++) Cell(Label(heads[c], secondary: true), 0, c);
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                int r = i + 1, row = i;
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Cell(Label(b.Machine), r, 0);
                Cell(Label(b.Parameter), r, 1);
                Cell(Label(b.Track.ToString()), r, 2);
                Cell(Label(b.Channel.ToString()), r, 3);
                Cell(Label(b.Controller.ToString()), r, 4);
                var clear = new Button { Content = "Clear", Margin = new Thickness(8, 2) };
                AutomationProperties.SetName(clear, $"Clear the binding of {b.Machine} {b.Parameter}");
                clear.Click += (s, e) => ClearRow(row);
                Cell(clear, r, 5);
            }
        }

        void Cell(Avalonia.Controls.Control c, int row, int col)
        {
            if (c is TextBlock) c.Margin = new Thickness(12, 4, 0, 4);
            Grid.SetRow(c, row);
            Grid.SetColumn(c, col);
            grid.Children.Add(c);
        }
    }
}
