// Fork (docs/specs/ux-overhaul.md P3a, decisions 31-34): the browser, the main window's panel on the left. A search box over the
// installed machines (Instruments, Effects, Control) that a double-click adds to the song, and the patterns of the selected
// machine (first: a long list of machines is below them) (a click selects one in every view, a double-click opens it in the Tracker). It collapses to a rail, and its open
// state and width are remembered in the settings. Presets and Samples come with the machine browser (P3d).

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Machines;

namespace ReBuzz.Studio.Views.Browser
{
    internal sealed class BrowserPanel : Border
    {
        internal const string RegistrySection = "Studio";
        internal const string OpenKey = "BrowserOpen";
        internal const string WidthKey = "BrowserWidth";

        public const double DefaultWidth = 224;
        public const double MinPanelWidth = 180;
        public const double MaxPanelWidth = 480;
        public const double RailWidth = 28;

        readonly ReBuzzCore core;
        readonly IRegistryEx registry;
        readonly StudioSelection selection;
        readonly Action<IPattern> openPattern;
        readonly TextBox search;
        readonly ListBox list;
        readonly Border grip;
        readonly Button collapseButton, railButton;
        readonly Grid frame;
        readonly DockPanel full;
        readonly Avalonia.Controls.Control rail;
        IReadOnlyList<(string Kind, IReadOnlyList<GearItem> Items)> gear;

        bool open, updating, disposed;
        double width;
        string lastRows, lastPattern;

        public BrowserPanel(ReBuzzCore core, IRegistryEx registry, Action<IPattern> openPattern)
        {
            this.core = core;
            this.registry = registry;
            this.openPattern = openPattern;
            selection = StudioSelection.For(core);
            open = Read(OpenKey, false);
            width = Math.Clamp(Read(WidthKey, DefaultWidth), MinPanelWidth, MaxPanelWidth);

            Background = StudioTokens.SurfacePanelBrush;
            BorderBrush = StudioTokens.SurfaceBorderBrush;
            BorderThickness = new Thickness(0, 0, 1, 0);
            ClipToBounds = true;
            AutomationProperties.SetName(this, "Browser");

            var title = new TextBlock { Text = "Browser", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextPrimaryBrush, VerticalAlignment = VerticalAlignment.Center };
            collapseButton = IconButton("‹", "Hide the browser (" + Shell.Commands.StudioKeymap.Get("view.browser").KeyLabel() + ")", "Hide browser", Close);
            DockPanel.SetDock(collapseButton, Dock.Right);
            var header = new DockPanel { Margin = new Thickness(12, 8, 8, 4), LastChildFill = true, Children = { collapseButton, title } };
            DockPanel.SetDock(header, Dock.Top);

            search = new TextBox { Watermark = "Search machines and patterns", FontSize = 12, Margin = new Thickness(8, 0, 8, 6), MinHeight = 26 };
            AutomationProperties.SetName(search, "Search the browser");
            search.TextChanged += (s, e) => Rebuild(force: false);
            search.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && search.Text?.Length > 0) { search.Text = ""; e.Handled = true; }
                else if (e.Key == Key.Down && list.ItemCount > 0) { list.Focus(); e.Handled = true; }
            };
            DockPanel.SetDock(search, Dock.Top);

            list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetName(list, "Browser list");
            list.SelectionChanged += OnListSelected;
            list.DoubleTapped += (s, e) => Activate();
            list.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { Activate(); e.Handled = true; }
            };

            full = new DockPanel { LastChildFill = true };
            full.Children.Add(header);
            full.Children.Add(search);
            full.Children.Add(list);

            railButton = IconButton("›", "Show the browser (" + Shell.Commands.StudioKeymap.Get("view.browser").KeyLabel() + ")", "Show browser", Open);
            railButton.Margin = new Thickness(2, 8, 2, 0);
            railButton.VerticalAlignment = VerticalAlignment.Top;
            rail = new Border { Child = railButton };

            grip = new Border { Width = 5, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeWestEast), HorizontalAlignment = HorizontalAlignment.Right };
            AutomationProperties.SetName(grip, "Resize browser");
            grip.PointerPressed += GripPressed;
            grip.PointerMoved += GripMoved;
            grip.PointerReleased += GripReleased;

            frame = new Grid();
            frame.Children.Add(full);
            frame.Children.Add(rail);
            frame.Children.Add(grip);
            Child = frame;

            DockPanel.SetDock(this, Dock.Left);
            ApplyOpen();
            selection.Changed += OnSelectionChanged;
            Refresh();
        }

        // ------------------------------------------------------------------ state

        public bool IsOpen => open;

        /// <summary>The layout around it changed: the window fits the panels to its width.</summary>
        public event Action Changed;

        public double PanelWidth
        {
            get => width;
            set
            {
                value = Math.Clamp(value, MinPanelWidth, MaxPanelWidth);
                if (value == width) return;
                width = value;
                Write(WidthKey, width);
                ApplyOpen();
            }
        }

        /// <summary>
        /// The room the window leaves it (a narrow window): less than its least width, it shows as the rail while it is open
        /// (the setting is unchanged), else it is as wide as the room if that is less than its width.
        /// </summary>
        public void Fit(double available)
        {
            if (available == fit) return;
            fit = available;
            ApplyOpen(notify: false);
        }

        double fit = double.PositiveInfinity;

        /// <summary>Open and squeezed into the rail by a narrow window.</summary>
        public bool IsSqueezed => open && fit < MinPanelWidth;

        /// <summary>The width it takes now.</summary>
        public double EffectiveWidth => open && !IsSqueezed ? Math.Min(width, fit) : RailWidth;

        internal TextBox Search => search;
        internal ListBox List => list;
        internal Button CollapseButton => collapseButton;
        internal Button RailButton => railButton;
        internal Avalonia.Controls.Control Grip => grip;

        /// <summary>The list as "kind: text" rows: headers are "#Instruments", machines "gear:Name", patterns "pattern:Name".</summary>
        internal IReadOnlyList<string> Rows => list.Items.OfType<ListBoxItem>().Select(i => i.Tag as string).ToList();

        public void Open() => SetOpen(true);

        public void Close() => SetOpen(false);

        public void Toggle() => SetOpen(!open);

        /// <summary>Opens the panel with the keyboard in the search box.</summary>
        public void FocusSearch()
        {
            SetOpen(true);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => search.Focus(), Avalonia.Threading.DispatcherPriority.Input);
        }

        void SetOpen(bool value)
        {
            if (open == value) return;
            open = value;
            Write(OpenKey, open);
            ApplyOpen();
            if (open) Refresh();
        }

        void ApplyOpen(bool notify = true)
        {
            bool shown = open && !IsSqueezed;
            full.IsVisible = shown;
            rail.IsVisible = !shown;
            grip.IsVisible = shown;
            Width = EffectiveWidth;
            if (notify) Changed?.Invoke();
        }

        T Read<T>(string key, T def)
        {
            try { return registry == null ? def : registry.Read(key, def, RegistrySection); }
            catch { return def; }
        }

        void Write<T>(string key, T value)
        {
            try { registry?.Write(key, value, RegistrySection); }
            catch { /* a setting that can't be saved is not worth a failed click */ }
        }

        // ------------------------------------------------------------------ the list

        void OnSelectionChanged(object source) => Refresh();

        /// <summary>Brings the list up to date with the selection and the song (the window's timer calls it).</summary>
        public void Refresh()
        {
            if (!open || disposed) return;
            Rebuild(force: false);
        }

        static bool Matches(string text, string query) => query.Length == 0 || text.Contains(query, StringComparison.OrdinalIgnoreCase);

        void Rebuild(bool force)
        {
            if (!open || disposed) return;
            gear ??= MachineEdits.GearByKind(core);
            string query = (search.Text ?? "").Trim();
            var machine = selection.Machine;
            var pattern = selection.Pattern;
            var patterns = machine == null ? new List<IPattern>() : machine.Patterns.Where(p => Matches(p.Name, query) || Matches(machine.Name, query)).ToList();

            var rows = new List<(string Tag, string Text, object Payload, bool Header)>();
            if (machine != null)
            {
                rows.Add(("#Patterns of " + machine.Name, "Patterns of " + machine.Name, null, true));
                if (patterns.Count == 0 && query.Length == 0) rows.Add(("none:", "No patterns yet", null, true));
                foreach (var p in patterns) rows.Add(("pattern:" + p.Name, $"{p.Name}   {p.Length}", p, false));
            }
            foreach (var (kind, items) in gear)
            {
                var shown = items.Where(g => Matches(g.DisplayName, query)).ToList();
                if (shown.Count == 0) continue;
                rows.Add(("#" + KindTitle(kind), KindTitle(kind), null, true));
                foreach (var g in shown) rows.Add(("gear:" + g.DisplayName, g.DisplayName, g, false));
            }
            if (rows.Count == 0) rows.Add(("none:", query.Length > 0 ? "Nothing matches “" + query + "”" : "No machines installed", null, true));

            string tags = string.Join("\n", rows.Select(r => r.Tag));
            string shownPattern = pattern?.Name ?? "";
            if (tags == lastRows && shownPattern == lastPattern && !force) return;
            bool sameRows = tags == lastRows;
            lastRows = tags;
            lastPattern = shownPattern;
            updating = true;
            try
            {
                if (!sameRows)
                {
                    list.ItemsSource = rows.Select(r => Row(r.Tag, r.Text, r.Payload, r.Header)).ToList();
                }
                var current = pattern == null ? null : list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == "pattern:" + pattern.Name);
                if (current != null) list.SelectedItem = current;
                else if (list.SelectedItem is ListBoxItem { Tag: string t } && t.StartsWith("pattern:")) list.SelectedItem = null;
            }
            finally
            {
                updating = false;
            }
        }

        static string KindTitle(string kind) => kind == "Generators" ? "Instruments" : kind;

        static ListBoxItem Row(string tag, string text, object payload, bool header)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = header ? 11 : 12,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = header ? StudioTokens.TextSecondaryBrush : StudioTokens.TextPrimaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var item = new ListBoxItem
            {
                Content = label,
                Tag = tag,
                DataContext = payload,
                MinHeight = header ? 22 : 24,
                Padding = new Thickness(header ? 12 : 20, header ? 6 : 2, 8, 2),
                Focusable = !header,
                IsHitTestVisible = !header,
            };
            AutomationProperties.SetName(item, text);
            return item;
        }

        void OnListSelected(object sender, SelectionChangedEventArgs e)
        {
            if (updating || list.SelectedItem is not ListBoxItem { DataContext: IPattern p }) return;
            selection.Set(p.Machine, p, this);
        }

        /// <summary>A double-click or Enter: a machine is added to the song, a pattern opens in the Tracker.</summary>
        internal void Activate()
        {
            if (list.SelectedItem is not ListBoxItem item) return;
            switch (item.DataContext)
            {
                case GearItem g:
                    var made = MachineEdits.Create(core, g.Instrument, 0, 0);
                    if (made != null) selection.Set(made, null, this);
                    break;
                case IPattern p:
                    selection.Set(p.Machine, p, this);
                    openPattern(p);
                    break;
            }
        }

        // ------------------------------------------------------------------ resizing

        void GripPressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            e.Pointer.Capture(grip);
            e.Handled = true;
        }

        void GripMoved(object sender, PointerEventArgs e)
        {
            if (!ReferenceEquals(e.Pointer.Captured, grip) || Parent is not Visual parent) return;
            PanelWidth = e.GetPosition(parent).X;
        }

        void GripReleased(object sender, PointerReleasedEventArgs e)
        {
            if (!ReferenceEquals(e.Pointer.Captured, grip)) return;
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        static Button IconButton(string glyph, string tip, string name, Action run)
        {
            var b = new Button { Content = glyph, FontSize = 15, Padding = new Thickness(6, 0, 6, 2), MinWidth = 24, MinHeight = 24, Focusable = false, Background = Brushes.Transparent };
            ToolTip.SetTip(b, tip);
            AutomationProperties.SetName(b, name);
            b.Click += (s, e) => run();
            return b;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            selection.Changed -= OnSelectionChanged;
        }
    }
}
