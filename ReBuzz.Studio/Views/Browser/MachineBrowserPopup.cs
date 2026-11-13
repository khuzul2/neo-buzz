// Fork (docs/specs/ux-overhaul.md P3d, decisions 56-58): the machine browser on the Machine View's canvas. A double click on empty
// canvas opens it there, and typing on the canvas (with nothing selected) opens it with the typed text as the search. Enter or a
// double click adds the machine where the click was (one undo step); Esc or a click elsewhere closes it. It lists what the left
// panel lists, from the same MachineBrowserModel.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Browser
{
    internal sealed class MachineBrowserPopup : Border
    {
        public const double PopupWidth = 340, PopupHeight = 360;

        readonly MachineBrowserModel model;
        readonly TextBox search;
        readonly ListBox list;
        readonly FilterBar filter = new FilterBar { Margin = new Thickness(8, 0, 8, 6) };
        bool updating;
        string lastKey;

        public MachineBrowserPopup(MachineBrowserModel model)
        {
            this.model = model;
            IsVisible = false;
            Width = PopupWidth;
            Height = PopupHeight;
            Background = StudioTokens.SurfacePanelBrush;
            BorderBrush = StudioTokens.SurfaceBorderBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(6);
            ClipToBounds = true;
            AutomationProperties.SetName(this, "Machine browser");

            search = new TextBox { Watermark = "Search machines: bass, reverb, compressor…", FontSize = 12, MinHeight = 28, Margin = new Thickness(8, 8, 8, 6) };
            AutomationProperties.SetName(search, "Search machines");
            search.TextChanged += (s, e) => Rebuild();
            search.KeyDown += OnSearchKey;
            filter.Changed += Rebuild;
            list = new ListBox { Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetName(list, "Machines");
            list.DoubleTapped += (s, e) => Pick();
            list.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { Pick(); e.Handled = true; }
                else if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
            };

            var panel = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(search, Dock.Top);
            DockPanel.SetDock(filter, Dock.Top);
            panel.Children.Add(search);
            panel.Children.Add(filter);
            panel.Children.Add(list);
            Child = panel;
        }

        void OnModelChanged()
        {
            if (IsVisible) { lastKey = null; Rebuild(); }
        }

        /// <summary>The model outlives the window: the popup listens only while its view is shown.</summary>
        internal void Attach() => model.Changed += OnModelChanged;

        internal void Detach() => model.Changed -= OnModelChanged;

        /// <summary>Where the machine goes: a song position (the canvas point the double click was at).</summary>
        public Point Position { get; private set; }

        /// <summary>Raised with the new machine after it is added.</summary>
        public event Action<BuzzGUI.Interfaces.IMachine> Added;

        public event Action Closed;

        public bool IsOpen => IsVisible;

        internal TextBox Search => search;
        internal ListBox List => list;
        internal FilterBar Filter => filter;

        internal IReadOnlyList<string> Rows => list.Items.OfType<ListBoxItem>().Select(i => i.Tag as string).ToList();

        /// <summary>Opens it with a search text (typed on the canvas, or empty) for a song position.</summary>
        public void Open(Point songPosition, string seed)
        {
            Position = songPosition;
            filter.Category = null;
            filter.FavouritesOnly = false;
            IsVisible = true;
            lastKey = null;
            search.Text = seed ?? "";
            Rebuild();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                search.Focus();
                search.CaretIndex = search.Text?.Length ?? 0;
            }, Avalonia.Threading.DispatcherPriority.Input);
        }

        public void Hide()
        {
            if (!IsVisible) return;
            IsVisible = false;
            Closed?.Invoke();
        }

        void Rebuild()
        {
            if (!IsVisible) return;
            string query = (search.Text ?? "").Trim();
            var rows = model.Rows(query, filter.Category, filter.FavouritesOnly);
            string key = query + "\n" + filter.Category + filter.FavouritesOnly + "\n" + string.Join("\n", rows.Select(r => r.Tag)) + "\n" + string.Join(",", model.Favourites);
            if (key == lastKey) return;
            lastKey = key;
            updating = true;
            try
            {
                var items = rows.Select(r => BrowserRows.Item(r, model, detail: true)).ToList();
                if (rows.Count == 0) items.Add(BrowserRows.Header("none:", "Nothing matches" + (query.Length > 0 ? " “" + query + "”" : "")));
                list.ItemsSource = items;
                // With a search, the best match is ready: Enter adds it.
                list.SelectedItem = query.Length > 0 ? items.FirstOrDefault(i => i.DataContext is MachineEntry) : null;
                list.ScrollIntoView(list.SelectedItem ?? (items.Count > 0 ? items[0] : null));
            }
            finally
            {
                updating = false;
            }
        }

        void OnSearchKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Hide();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Pick();
                    e.Handled = true;
                    break;
                case Key.Down:
                case Key.Up:
                    Move(e.Key == Key.Down ? 1 : -1);
                    e.Handled = true;
                    break;
            }
        }

        void Move(int step)
        {
            var machines = list.Items.OfType<ListBoxItem>().Where(i => i.DataContext is MachineEntry).ToList();
            if (machines.Count == 0) return;
            int at = list.SelectedItem is ListBoxItem cur ? machines.IndexOf(cur) : -1;
            at = Math.Clamp(at + step, 0, machines.Count - 1);
            list.SelectedItem = machines[at];
            list.ScrollIntoView(machines[at]);
        }

        /// <summary>Adds the chosen machine (a search chooses its best match) at the position, in one undo step; closes.</summary>
        public bool Pick()
        {
            var entry = (list.SelectedItem as ListBoxItem)?.DataContext as MachineEntry;
            if (entry == null || updating) return false;
            var made = model.Add(entry, (float)Position.X, (float)Position.Y);
            Hide();
            if (made != null) Added?.Invoke(made);
            return made != null;
        }
    }
}
