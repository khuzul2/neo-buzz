// Fork (docs/specs/ux-overhaul.md P5b): the modulation matrix, the keyboard's way to what drag-to-modulate does with the pointer. A table of
// every modulation in the song (source, target machine, parameter, depth, polarity, remove) and, above it, "+ Add": a source, a machine
// and a parameter, then the button. Edits go through ModulationEdits (the agent's model, limits and safe ranges), each one an undo step.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Modulation
{
    using Control = Avalonia.Controls.Control;

    internal sealed class ModulationMatrixWindow : Window
    {
        static ModulationMatrixWindow current;

        readonly ComboBox source = new ComboBox { MinWidth = 150 };
        readonly ComboBox machine = new ComboBox { MinWidth = 150 };
        readonly ComboBox parameter = new ComboBox { MinWidth = 190 };
        readonly Button add = new Button { Content = "+ Add" };
        readonly TextBlock message = new TextBlock { FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap };
        readonly StackPanel rowsPanel = new StackPanel { Spacing = 2 };
        readonly TextBlock empty = new TextBlock
        {
            Text = "No modulation yet. Drag a modulator's handle onto a knob, or choose a source, a machine and a parameter above and press + Add.",
            Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8),
        };
        List<IModulator> sources = new List<IModulator>();
        List<IMachine> machines = new List<IMachine>();
        List<(IParameter Parameter, int Track, string Label)> choices = new List<(IParameter, int, string)>();
        string signature = "\0";
        bool filling;
        readonly Action refresh;

        /// <summary>Opens the matrix (or brings it to the front), the given source chosen for "+ Add".</summary>
        public static ModulationMatrixWindow Open(IModulator preselect = null, Window owner = null)
        {
            if (current == null)
            {
                current = new ModulationMatrixWindow();
                current.Closed += (s, e) => current = null;
                if (owner != null) current.Show(owner); else current.Show();
            }
            else current.Activate();
            current.FillSources();
            if (preselect != null) current.Select(preselect);
            current.source.Focus();
            return current;
        }

        internal static ModulationMatrixWindow Current => current;

        ModulationMatrixWindow()
        {
            Title = "Modulation Matrix";
            Width = 760;
            Height = 460;
            MinWidth = 600;
            MinHeight = 300;
            Background = StudioTokens.SurfaceViewBrush;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            AutomationProperties.SetName(this, "Modulation Matrix");

            AutomationProperties.SetName(source, "Source");
            AutomationProperties.SetName(machine, "Target machine");
            AutomationProperties.SetName(parameter, "Target parameter");
            AutomationProperties.SetName(add, "Add");
            ToolTip.SetTip(add, "Adds the modulation: the source drives the parameter at half depth; change it in the table. One undo step.");
            source.SelectionChanged += (s, e) => { if (!filling) UpdateAdd(); };
            machine.SelectionChanged += (s, e) => { if (!filling) FillParameters(); };
            parameter.SelectionChanged += (s, e) => { if (!filling) UpdateAdd(); };
            add.Click += (s, e) => AddNow();
            add.IsDefault = true;

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto,*,Auto"), Margin = new Thickness(16, 16, 16, 4) };
            void Put(Control c, int col) { Grid.SetColumn(c, col); header.Children.Add(c); }
            Label("Source", 0, header);
            Put(source, 1);
            Label("Machine", 2, header);
            Put(machine, 3);
            Label("Parameter", 4, header);
            Put(parameter, 5);
            Put(add, 6);
            foreach (var c in new Control[] { source, machine, parameter, add }) c.Margin = new Thickness(6, 0, 12, 0);
            message.Margin = new Thickness(16, 4, 16, 8);

            var columns = new Grid { ColumnDefinitions = Columns, Margin = new Thickness(16, 4, 16, 0) };
            string[] titles = { "Source", "Machine", "Parameter", "Depth %", "Polarity", "" };
            for (int i = 0; i < titles.Length; i++)
            {
                var t = new TextBlock { Text = titles[i], FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush };
                Grid.SetColumn(t, i);
                columns.Children.Add(t);
            }
            var top = new StackPanel { Children = { header, message, columns } };
            var scroll = new ScrollViewer
            {
                Content = new StackPanel { Margin = new Thickness(16, 0, 16, 16), Children = { rowsPanel, empty } },
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            };
            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            root.Children.Add(scroll);
            Content = root;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };

            refresh = () => Dispatcher.UIThread.Post(Refill);
            Opened += (s, e) => { ModulationBoard.Changed += refresh; Refill(); FillSources(); };
            Closed += (s, e) => ModulationBoard.Changed -= refresh;
        }

        static ColumnDefinitions Columns => new ColumnDefinitions("140,140,*,110,90,80");

        static void Label(string text, int column, Grid grid)
        {
            var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush, FontSize = StudioTokens.FontBody };
            Grid.SetColumn(t, column);
            grid.Children.Add(t);
        }

        static ReBuzzCore Core => StudioHost.Current?.Core ?? ModulationBoard.All().FirstOrDefault()?.Machine?.Graph?.Buzz as ReBuzzCore;

        // ------------------------------------------------------------------ + Add

        void FillSources()
        {
            filling = true;
            var keep = source.SelectedIndex >= 0 && source.SelectedIndex < sources.Count ? sources[source.SelectedIndex] : null;
            sources = ModulationBoard.All().OrderBy(m => m.Machine?.Name, StringComparer.OrdinalIgnoreCase).ToList();
            source.ItemsSource = sources.Select(m => $"{m.Machine.Name} ({m.Kind})").ToList();
            int at = keep == null ? -1 : sources.IndexOf(keep);
            source.SelectedIndex = at >= 0 ? at : sources.Count > 0 ? 0 : -1;

            var graph = sources.FirstOrDefault()?.Machine?.Graph;
            var chosen = SelectedSource;
            string keepMachine = SelectedMachine?.Name;
            machines = (graph?.Machines ?? Enumerable.Empty<IMachine>())
                .Where(m => !ReferenceEquals(m, chosen?.Machine) && ModulationBoard.Of(m) == null)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            machine.ItemsSource = machines.Select(m => m.Name).ToList();
            int mi = keepMachine == null ? -1 : machines.FindIndex(m => m.Name == keepMachine);
            machine.SelectedIndex = mi >= 0 ? mi : machines.Count > 0 ? 0 : -1;
            filling = false;
            FillParameters();
        }

        void FillParameters()
        {
            filling = true;
            string keep = parameter.SelectedIndex >= 0 && parameter.SelectedIndex < choices.Count ? choices[parameter.SelectedIndex].Label : null;
            choices = new List<(IParameter, int, string)>();
            if (SelectedMachine is { } m)
                foreach (var group in m.ParameterGroups)
                {
                    bool global = group.Type == ParameterGroupType.Global, tracks = group.Type == ParameterGroupType.Track;
                    if (!global && !tracks) continue;
                    foreach (var p in group.Parameters)
                    {
                        if (p.Type == ParameterType.Note) continue;
                        if (global) choices.Add((p, 0, p.Name));
                        else for (int t = 0; t < Math.Min(16, Math.Max(1, group.TrackCount)); t++)
                                choices.Add((p, t, group.TrackCount > 1 ? $"{p.Name} (track {t + 1})" : p.Name));
                    }
                }
            parameter.ItemsSource = choices.Select(c => c.Label).ToList();
            int at = keep == null ? -1 : choices.FindIndex(c => c.Label == keep);
            parameter.SelectedIndex = at >= 0 ? at : choices.Count > 0 ? 0 : -1;
            filling = false;
            UpdateAdd();
        }

        IModulator SelectedSource => source.SelectedIndex >= 0 && source.SelectedIndex < sources.Count ? sources[source.SelectedIndex] : null;

        IMachine SelectedMachine => machine.SelectedIndex >= 0 && machine.SelectedIndex < machines.Count ? machines[machine.SelectedIndex] : null;

        void UpdateAdd()
        {
            add.IsEnabled = SelectedSource != null && SelectedMachine != null && parameter.SelectedIndex >= 0 && parameter.SelectedIndex < choices.Count;
        }

        void Select(IModulator m)
        {
            int at = sources.FindIndex(s => ReferenceEquals(s.Machine, m.Machine));
            if (at >= 0) source.SelectedIndex = at;
        }

        /// <summary>The chosen source, machine and parameter, for the tests: picks them.</summary>
        internal void Choose(string sourceName, string machineName, string parameterName)
        {
            FillSources();
            int s = sources.FindIndex(m => m.Machine.Name == sourceName);
            if (s >= 0) source.SelectedIndex = s;
            int mi = machines.FindIndex(m => m.Name == machineName);
            if (mi >= 0) machine.SelectedIndex = mi;
            int p = choices.FindIndex(c => c.Label == parameterName || c.Parameter.Name == parameterName);
            if (p >= 0) parameter.SelectedIndex = p;
        }

        /// <summary>"+ Add" (tests, and the button): null when it was added, else the reason.</summary>
        internal string AddNow()
        {
            if (SelectedSource is not { } mod || SelectedMachine is not { } target || parameter.SelectedIndex < 0 || parameter.SelectedIndex >= choices.Count)
                return Say("Choose a source, a machine and a parameter.");
            var (p, track, _) = choices[parameter.SelectedIndex];
            string refusal = ModulationEdits.Add(Core, mod, target, p, track);
            Say(refusal ?? $"{mod.Machine.Name} now modulates {target.Name} {p.Name}. Undo takes it back.");
            return refusal;
        }

        string Say(string text)
        {
            message.Text = text;
            return text;
        }

        internal string Message => message.Text;

        // ------------------------------------------------------------------ the table

        internal sealed record RowView(IModulator Source, ModulationInfo Info, NumericUpDown Depth, ToggleButton Polarity, Button Remove);

        internal IReadOnlyList<RowView> Rows { get; private set; } = Array.Empty<RowView>();

        static string Signature() => string.Join("|", ModulationBoard.All().OrderBy(m => m.Machine?.Name)
            .SelectMany(m => m.Targets.Select(t => $"{m.Machine?.Name}/{t.Slot}/{t.Machine}/{t.Param}/{t.Track}")));

        /// <summary>Rebuilds the table when the set of modulations changed, else only shows the values now.</summary>
        internal void Refill()
        {
            string now = Signature();
            if (now != signature)
            {
                signature = now;
                Rebuild();
                FillSources();
            }
            else foreach (var r in Rows) Show(r);
        }

        void Show(RowView r)
        {
            var info = r.Source.Targets.FirstOrDefault(t => t.Slot == r.Info.Slot);
            if (info == null) return;
            filling = true;
            if (!r.Depth.IsKeyboardFocusWithin && r.Depth.Value != info.Depth) r.Depth.Value = info.Depth;
            r.Polarity.IsChecked = info.Bipolar;
            r.Polarity.Content = info.Bipolar ? "Bi" : "Uni";
            filling = false;
        }

        void Rebuild()
        {
            rowsPanel.Children.Clear();
            var list = new List<RowView>();
            foreach (var mod in ModulationBoard.All().OrderBy(m => m.Machine?.Name, StringComparer.OrdinalIgnoreCase))
                foreach (var info in mod.Targets.OrderBy(t => t.Slot))
                {
                    var m = mod;
                    int slot = info.Slot;
                    string what = $"{m.Machine.Name} to {info.Machine} {info.Param}";
                    var depth = new NumericUpDown { Minimum = -100, Maximum = 100, Increment = 5, FormatString = "0", Value = info.Depth, Width = 100, MinHeight = 0, Height = 26, FontSize = StudioTokens.FontBody };
                    AutomationProperties.SetName(depth, "Depth of " + what);
                    var polarity = new ToggleButton { Content = info.Bipolar ? "Bi" : "Uni", IsChecked = info.Bipolar, Width = 64 };
                    AutomationProperties.SetName(polarity, "Bipolar: " + what);
                    ToolTip.SetTip(polarity, "Uni: the modulator only pushes up. Bi: up and down around the value you set.");
                    var remove = new Button { Content = "Remove" };
                    AutomationProperties.SetName(remove, "Remove " + what);
                    var row = new RowView(m, info, depth, polarity, remove);
                    depth.ValueChanged += (s, e) =>
                    {
                        if (filling || e.NewValue is not decimal v) return;
                        string refusal = ModulationEdits.Edit(Core, m, slot, i => i.Depth = (int)v);
                        if (refusal != null) Say(refusal);
                    };
                    polarity.IsCheckedChanged += (s, e) =>
                    {
                        if (filling) return;
                        bool bi = polarity.IsChecked == true;
                        string refusal = ModulationEdits.Edit(Core, m, slot, i => i.Bipolar = bi);
                        if (refusal != null) Say(refusal);
                    };
                    remove.Click += (s, e) => { ModulationEdits.Remove(Core, m, slot); Say("Removed. Undo puts it back."); };
                    var grid = new Grid { ColumnDefinitions = Columns, Height = 30 };
                    void Put(Control c, int col) { c.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(c, col); grid.Children.Add(c); }
                    Put(Cell(m.Machine.Name), 0);
                    Put(Cell(info.Machine), 1);
                    Put(Cell(info.IsTrack ? $"{info.Param} (track {info.Track + 1})" : info.Param), 2);
                    Put(depth, 3);
                    Put(polarity, 4);
                    Put(remove, 5);
                    rowsPanel.Children.Add(grid);
                    list.Add(row);
                }
            Rows = list;
            empty.IsVisible = list.Count == 0;
            foreach (var r in list) Show(r);
        }

        static TextBlock Cell(string text) => new TextBlock { Text = text, FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextPrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };

        // Tests
        internal ComboBox SourceBox => source;
        internal ComboBox MachineBox => machine;
        internal ComboBox ParameterBox => parameter;
        internal Button AddButton => add;
    }
}
