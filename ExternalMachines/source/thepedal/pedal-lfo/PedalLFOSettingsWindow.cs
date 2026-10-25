// PedalLFOSettingsWindow.cs — target picker.
//
// Opened from the embedded GUI's Configure button.  Hosted on its own
// STA thread (Core §23) so its ShowDialog() blocks only that thread,
// not the ReBuzz main dispatcher.  All IMachine references passed in
// are snapshotted on the UI thread before the dialog opens — we never
// enumerate Song.Machines from this thread.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BuzzGUI.Interfaces;

namespace WDE.PedalLFO
{
    public class PedalLFOSettingsWindow : Window
    {
        readonly Dictionary<string, IMachine> _machines;

        ListBox _machineList;
        ListBox _paramList;
        Button  _ok;
        Button  _cancel;
        Button  _clear;

        public string SelectedMachineName   { get; private set; }
        public string SelectedParameterName { get; private set; }
        public bool   ClearRequested        { get; private set; }

        public PedalLFOSettingsWindow(
            IEnumerable<IMachine> machinesSnapshot,
            string                selfName,
            string                currentMachineName,
            string                currentParameterName)
        {
            _machines = new Dictionary<string, IMachine>(StringComparer.Ordinal);
            foreach (var m in machinesSnapshot ?? Enumerable.Empty<IMachine>())
            {
                if (m == null || string.IsNullOrEmpty(m.Name)) continue;
                if (m.Name == selfName) continue;                                     // refuse self
                if (PedalLFOMachine.GetGlobalParameterNames(m).Count == 0) continue;  // no globals to drive
                _machines[m.Name] = m;
            }

            BuildUi();

            foreach (var name in _machines.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                _machineList.Items.Add(name);

            // Restore prior selection if still valid.
            if (!string.IsNullOrEmpty(currentMachineName) &&
                _machines.ContainsKey(currentMachineName))
            {
                _machineList.SelectedItem = currentMachineName;
                if (!string.IsNullOrEmpty(currentParameterName))
                {
                    foreach (var item in _paramList.Items)
                    {
                        if ((string)item == currentParameterName)
                        {
                            _paramList.SelectedItem = item;
                            break;
                        }
                    }
                }
            }

            UpdateOkEnabled();
        }

        // ── UI construction (no XAML) ────────────────────────────────────
        void BuildUi()
        {
            Title = "Pedal LFO — Configure Target";
            Width = 580;
            Height = 380;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26));
            Foreground = Brushes.White;
            ShowInTaskbar = true;

            var root = new DockPanel { Margin = new Thickness(10) };

            // ── Bottom bar ────────────────────────────────────────────
            var bottom = new Grid();
            DockPanel.SetDock(bottom, Dock.Bottom);
            bottom.Margin = new Thickness(0, 10, 0, 0);
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _clear = new Button
            {
                Content = "Clear assignment",
                Padding = new Thickness(10, 4, 10, 4),
                MinWidth = 130,
            };
            _clear.Click += (_, __) => { ClearRequested = true; DialogResult = true; Close(); };
            Grid.SetColumn(_clear, 0);
            bottom.Children.Add(_clear);

            _ok = new Button
            {
                Content = "OK",
                IsDefault = true,
                Padding = new Thickness(10, 4, 10, 4),
                MinWidth = 80,
                Margin = new Thickness(0, 0, 8, 0),
            };
            _ok.Click += OkClick;
            Grid.SetColumn(_ok, 2);
            bottom.Children.Add(_ok);

            _cancel = new Button
            {
                Content = "Cancel",
                IsCancel = true,
                Padding = new Thickness(10, 4, 10, 4),
                MinWidth = 80,
            };
            Grid.SetColumn(_cancel, 3);
            bottom.Children.Add(_cancel);

            // ── Two-column lists ──────────────────────────────────────
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            grid.Children.Add(MakeLabel("Machine", 0));
            grid.Children.Add(MakeLabel("Parameter (globals only)", 2));

            _machineList = MakeList(0);
            _machineList.SelectionChanged += MachineList_SelectionChanged;
            grid.Children.Add(_machineList);

            _paramList = MakeList(2);
            _paramList.SelectionChanged += (_, __) => UpdateOkEnabled();
            _paramList.MouseDoubleClick  += (_, e) =>
            {
                if (_paramList.SelectedItem != null && _machineList.SelectedItem != null)
                    OkClick(this, null);
            };
            grid.Children.Add(_paramList);

            root.Children.Add(bottom);
            root.Children.Add(grid);
            Content = root;
        }

        TextBlock MakeLabel(string text, int col)
        {
            var t = new TextBlock
            {
                Text = text,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xCC)),
                FontWeight = FontWeights.SemiBold,
            };
            Grid.SetColumn(t, col);
            Grid.SetRow(t, 0);
            return t;
        }

        ListBox MakeList(int col)
        {
            var lb = new ListBox
            {
                Background  = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x14)),
                Foreground  = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x46)),
                BorderThickness = new Thickness(1),
            };
            Grid.SetColumn(lb, col);
            Grid.SetRow(lb, 1);
            return lb;
        }

        void MachineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _paramList.Items.Clear();
            string sel = _machineList.SelectedItem as string;
            if (sel != null && _machines.TryGetValue(sel, out var m))
            {
                foreach (var p in PedalLFOMachine.GetGlobalParameterNames(m)
                                                  .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                    _paramList.Items.Add(p);
            }
            UpdateOkEnabled();
        }

        void UpdateOkEnabled()
        {
            _ok.IsEnabled = _machineList.SelectedItem != null
                         && _paramList.SelectedItem   != null;
        }

        void OkClick(object sender, RoutedEventArgs e)
        {
            string m = _machineList.SelectedItem as string;
            string p = _paramList.SelectedItem  as string;
            if (string.IsNullOrEmpty(m) || string.IsNullOrEmpty(p)) return;
            SelectedMachineName   = m;
            SelectedParameterName = p;
            DialogResult = true;
            Close();
        }
    }
}
