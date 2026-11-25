// Fork (docs/specs/ux-overhaul.md P3a, decisions 31-34): the browser, the main window's panel on the left. A search box over the
// installed machines that a double-click adds to the song, and the patterns of the selected machine (first: a long list of
// machines is below them) (a click selects one in every view, a double-click opens it in the Tracker). It collapses to a rail, and
// its open state and width are remembered in the settings.
// Fork (P3d, decisions 56-57): the machines are the machine browser's: by category (the manifest catalogue's ten), with tags,
// use_for and brand, recents, favourites and starters, from the one MachineBrowserModel the Machine View's popup uses too.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Browser;
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

        public const double DefaultWidth = 400;
        public const double MinPanelWidth = 180;
        public const double MaxPanelWidth = 640;

        /// <summary>BR1: under this width the sources are a dropdown above the results (one column).</summary>
        public const double NarrowWidth = 380;
        internal const string SourceKey = "BrowserSource", TreeKey = "BrowserTree";
        public const double RailWidth = 28;

        readonly ReBuzzCore core;
        readonly IRegistryEx registry;
        readonly StudioSelection selection;
        readonly Action<IClip> openClip;
        readonly TextBox search;
        readonly ListBox list;
        readonly Border grip;
        readonly Button collapseButton, railButton;
        readonly Grid frame;
        readonly DockPanel full;
        readonly Avalonia.Controls.Control rail;
        readonly MachineBrowserModel model;
        readonly FilterBar filter = new FilterBar { Margin = new Thickness(8, 0, 8, 6) };
        string lastStamp;
        readonly ListBox sourcesList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 4) };
        readonly ComboBox sourcesBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(8, 0, 8, 6) };
        readonly Border sourcesColumn;
        readonly ToggleButton treeToggle;
        readonly Button addPlace;
        readonly TextBlock indexText = new TextBlock { FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, Margin = new Thickness(8, 0, 8, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Border swapBanner;
        readonly TextBlock swapText = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        readonly Grid body;
        readonly PreviewStrip strip;
        readonly Dictionary<string, BrowserItem> itemsByTag = new();
        readonly HashSet<string> folded = new();
        string source = BrowserSource.All;
        bool treeMode = true, oneColumn;

        bool open, updating, disposed;
        double width;
        string lastRows, lastClip;

        public BrowserPanel(ReBuzzCore core, IRegistryEx registry, Action<IClip> openClip)
        {
            this.core = core;
            this.registry = registry;
            this.openClip = openClip;
            selection = StudioSelection.For(core);
            model = MachineBrowserModel.For(core, registry);
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

            search = new TextBox { Watermark = "Search machines, plug-ins, presets and samples", FontSize = 12, Margin = new Thickness(8, 0, 8, 6), MinHeight = 26 };
            AutomationProperties.SetName(search, "Search the browser");
            search.TextChanged += (s, e) => Rebuild(force: false);
            search.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && search.Text?.Length > 0) { search.Text = ""; e.Handled = true; }
                else if (e.Key == Key.Down && list.ItemCount > 0) { list.Focus(); e.Handled = true; }
            };
            DockPanel.SetDock(search, Dock.Top);
            filter.Changed += () => Rebuild(force: false);
            model.Changed += OnModelChanged;

            list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetName(list, "Browser list");
            list.SelectionChanged += OnListSelected;
            list.DoubleTapped += (s, e) => Activate();
            list.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { Activate(); e.Handled = true; }
            };

            treeMode = Read(TreeKey, true);
            source = Read(SourceKey, BrowserSource.All);
            model.StartIndexing();
            model.Library.ProgressChanged += OnIndexProgress;
            model.SwapChanged += OnSwapChanged;

            treeToggle = StudioButtons.KeepFocus(new ToggleButton { Content = "☰", IsChecked = treeMode, Padding = new Thickness(6, 1), FontSize = 12 });
            AutomationProperties.SetName(treeToggle, "Show results as a tree");
            ToolTip.SetTip(treeToggle, "Results as a tree (grouped) or a list");
            treeToggle.IsCheckedChanged += (s, e) => { treeMode = treeToggle.IsChecked == true; Write(TreeKey, treeMode); Rebuild(force: true); };
            addPlace = IconButton("＋", "Add a folder to Places: its samples are indexed and searchable", "Add a place", () => _ = PickPlace());
            var toolbar = new DockPanel { Margin = new Thickness(8, 0, 8, 4), LastChildFill = true };
            DockPanel.SetDock(treeToggle, Dock.Right);
            toolbar.Children.Add(treeToggle);
            toolbar.Children.Add(indexText);
            DockPanel.SetDock(toolbar, Dock.Top);

            swapText.Foreground = StudioTokens.TextPrimaryBrush;
            swapBanner = new Border { Background = StudioTokens.SelectionFillBrush, BorderBrush = StudioTokens.AccentBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(8, 0, 8, 6), Padding = new Thickness(8, 4), IsVisible = false, Child = swapText };
            AutomationProperties.SetName(swapBanner, "Hot-swap");
            DockPanel.SetDock(swapBanner, Dock.Top);

            strip = new PreviewStrip(() => core);
            DockPanel.SetDock(strip, Dock.Bottom);
            strip.Changed += () => { };

            sourcesList.SelectionChanged += OnSourcePicked;
            AutomationProperties.SetName(sourcesList, "Sources");
            sourcesBox.SelectionChanged += OnSourcePicked;
            AutomationProperties.SetName(sourcesBox, "Source");
            var sourcesHead = new DockPanel { Margin = new Thickness(8, 0, 4, 2), LastChildFill = true };
            DockPanel.SetDock(addPlace, Dock.Right);
            sourcesHead.Children.Add(addPlace);
            sourcesHead.Children.Add(new TextBlock { Text = "Sources", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(sourcesHead, Dock.Top);
            sourcesColumn = new Border { Width = 132, BorderBrush = StudioTokens.SurfaceBorderBrush, BorderThickness = new Thickness(0, 0, 1, 0), Child = new DockPanel { Children = { sourcesHead, sourcesList } } };

            var results = new DockPanel { LastChildFill = true };
            results.Children.Add(swapBanner);
            DockPanel.SetDock(filter, Dock.Top);
            results.Children.Add(filter);
            results.Children.Add(toolbar);
            results.Children.Add(list);
            body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(sourcesColumn, 0);
            Grid.SetColumn(results, 1);
            body.Children.Add(sourcesColumn);
            body.Children.Add(results);

            full = new DockPanel { LastChildFill = true };
            full.Children.Add(header);
            full.Children.Add(search);
            DockPanel.SetDock(sourcesBox, Dock.Top);
            full.Children.Add(sourcesBox);
            full.Children.Add(strip);
            full.Children.Add(body);
            AddHandler(KeyDownEvent, (s, e) => { if (e.Key == Key.Escape && model.SwapTarget != null) { model.EndSwap(); e.Handled = true; } });
            FillSources();

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
        internal FilterBar Filter => filter;
        internal MachineBrowserModel Model => model;
        internal Button CollapseButton => collapseButton;
        internal Button RailButton => railButton;
        internal Avalonia.Controls.Control Grip => grip;
        internal ListBox SourcesList => sourcesList;
        internal ComboBox SourcesBox => sourcesBox;
        internal PreviewStrip Strip => strip;
        internal ToggleButton TreeToggle => treeToggle;
        internal Border SwapBanner => swapBanner;
        internal string IndexText => indexText.Text ?? "";

        /// <summary>The panel shows one column: the sources are a dropdown (under <see cref="NarrowWidth"/>).</summary>
        internal bool IsOneColumn => oneColumn;

        /// <summary>The source whose results show (<see cref="BrowserSource.All"/> by default).</summary>
        internal string CurrentSource => source;

        /// <summary>Tree (grouped, a header folds) or list (flat).</summary>
        internal bool TreeMode => treeMode;

        /// <summary>The item behind a row's tag, for the rows that are not machines or patterns of the selection.</summary>
        internal BrowserItem ItemOf(string tag) => itemsByTag.TryGetValue(tag, out var i) ? i : null;

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
            bool narrow = EffectiveWidth < NarrowWidth;
            if (narrow != oneColumn || sourcesColumn.IsVisible == narrow)
            {
                oneColumn = narrow;
                sourcesColumn.IsVisible = !narrow;
                sourcesBox.IsVisible = narrow;
            }
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

        // ------------------------------------------------------------------ sources

        bool fillingSources;

        void FillSources()
        {
            var sources = model.Sources();
            fillingSources = true;
            try
            {
                string section = null;
                var rows = new List<ListBoxItem>();
                foreach (var src in sources)
                {
                    if (src.Section.Length > 0 && src.Section != section)
                    {
                        section = src.Section;
                        rows.Add(BrowserRows.Header("#" + section, section));
                    }
                    var line = new DockPanel { LastChildFill = true };
                    int colour = src.Id.StartsWith("col:", StringComparison.Ordinal) && int.TryParse(src.Id.AsSpan(4), out int c) ? c : 0;
                    if (colour > 0)
                        line.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = BrowserRows.ColourBrush(colour), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                    line.Children.Add(new TextBlock { Text = src.Title, FontSize = 12, Foreground = StudioTokens.TextPrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis });
                    var item = new ListBoxItem { Content = line, Tag = "src:" + src.Id, DataContext = src, Padding = new Thickness(10, 2, 4, 2), MinHeight = 24 };
                    AutomationProperties.SetName(item, src.Title);
                    if (src.Id.StartsWith("place:", StringComparison.Ordinal))
                    {
                        string path = src.Id[6..];
                        var remove = new MenuItem { Header = "Remove this place" };
                        remove.Click += (s, e) => model.Library.RemovePlace(path);
                        item.ContextMenu = new ContextMenu { ItemsSource = new[] { remove } };
                        ToolTip.SetTip(item, path);
                    }
                    rows.Add(item);
                }
                sourcesList.ItemsSource = rows;
                sourcesBox.ItemsSource = sources.Select(x => (x.Section.Length > 0 ? x.Section + ": " : "") + x.Title).ToList();
                if (!sources.Any(x => x.Id == source)) source = BrowserSource.All;
                var current = rows.FirstOrDefault(r => r.Tag as string == "src:" + source);
                sourcesList.SelectedItem = current;
                sourcesBox.SelectedIndex = Math.Max(0, sources.FindIndex(x => x.Id == source));
                filter.IsVisible = source == BrowserSource.All;
            }
            finally { fillingSources = false; }
        }

        void OnSourcePicked(object sender, SelectionChangedEventArgs e)
        {
            if (fillingSources) return;
            string id = null;
            if (sender == sourcesList && sourcesList.SelectedItem is ListBoxItem { DataContext: BrowserSource s1 }) id = s1.Id;
            else if (sender == sourcesBox && sourcesBox.SelectedIndex >= 0) id = model.Sources().ElementAtOrDefault(sourcesBox.SelectedIndex)?.Id;
            if (id != null) SelectSource(id);
        }

        /// <summary>Shows a source's results (BrowserSource ids: "all", "col:fav", "col:3", "cat:drums", "place:/path", "gear:samples", "song:audio", "recent:files").</summary>
        internal void SelectSource(string id)
        {
            if (id == source) return;
            source = id;
            Write(SourceKey, id);
            fillingSources = true;
            try
            {
                sourcesList.SelectedItem = sourcesList.Items.OfType<ListBoxItem>().FirstOrDefault(r => r.Tag as string == "src:" + id);
                sourcesBox.SelectedIndex = Math.Max(0, model.Sources().FindIndex(x => x.Id == id));
            }
            finally { fillingSources = false; }
            filter.IsVisible = id == BrowserSource.All;
            Rebuild(force: true);
        }

        /// <summary>Adds a folder as a place (indexed in the background); false when it isn't a folder or is one already.</summary>
        internal bool AddPlaceFolder(string folder)
        {
            bool ok = model.Library.AddPlace(folder);
            if (ok) FillSources();
            return ok;
        }

        async System.Threading.Tasks.Task PickPlace()
        {
            try
            {
                var top = TopLevel.GetTopLevel(this);
                if (top == null) return;
                var picked = await top.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Add a folder of samples", AllowMultiple = false });
                var path = picked.Count > 0 ? picked[0].Path?.LocalPath : null;
                if (path != null) AddPlaceFolder(path);
            }
            catch (Exception) { /* a picker that didn't open is not worth a failed click */ }
        }

        void OnIndexProgress()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (disposed) return;
                string t = model.Library.Progress.Text;
                if (indexText.Text != t) indexText.Text = t;
                if (!model.Library.IsScanning) { lastStamp = null; Refresh(); }
            });
        }

        void OnSwapChanged()
        {
            var target = model.SwapTarget;
            swapBanner.IsVisible = target != null;
            if (target != null)
            {
                swapText.Text = $"Hot-swap {target.Name}: double-click a machine, plug-in or preset to put it in its place (Esc cancels).";
                Open();
                lastStamp = null;
            }
        }

        // ------------------------------------------------------------------ the list

        void OnSelectionChanged(object source) => Refresh();

        /// <summary>Brings the list up to date with the selection and the song (the window's timer calls it).</summary>
        public void Refresh()
        {
            if (!open || disposed) return;
            Rebuild(force: false);
            strip.Poll();
        }

        static bool Matches(string text, string query) => query.Length == 0 || text.Contains(query, StringComparison.OrdinalIgnoreCase);

        void OnModelChanged()
        {
            FillSources();
            lastStamp = null;
            Refresh();
        }

        void Rebuild(bool force)
        {
            if (!open || disposed) return;
            string query = (search.Text ?? "").Trim();
            var machine = selection.Machine;
            var clip = selection.Clip;
            bool allView = source == BrowserSource.All || query.Length > 0;

            // The rows: (tag, text, kind of row). Machines and patterns of the selection are as they were; the other kinds are BrowserItems.
            var rows = new List<MachineBrowserModel.Row>();
            var objects = new Dictionary<string, IClip>();
            var others = new List<BrowserRow>();
            itemsByTag.Clear();
            if (allView)
            {
                var clips = machine == null ? new List<IClip>() : machine.Clips.Where(p => Matches(p.Name, query) || Matches(machine.Name, query)).ToList();
                if (machine != null)
                {
                    rows.Add(new MachineBrowserModel.Row("#Patterns of " + machine.Name, "Patterns of " + machine.Name, null, true));
                    if (clips.Count == 0 && query.Length == 0) rows.Add(new MachineBrowserModel.Row("none:", "No patterns yet: make one in the Tracker", null, true));
                    foreach (var p in clips)
                    {
                        rows.Add(new MachineBrowserModel.Row("pattern:" + p.Name, $"{p.Name}   {p.Length}", null, false));
                        objects["pattern:" + p.Name] = p;
                    }
                }
                rows.AddRange(model.Rows(query, source == BrowserSource.All ? filter.Category : null, source == BrowserSource.All && filter.FavouritesOnly));
                if (query.Length > 0)
                {
                    // One search over everything: what is not a machine follows them, by kind.
                    var found = model.SearchAll(query).Where(i => i.Machine == null || i.Kind == BrowserKind.Preset).ToList();
                    foreach (var group in found.GroupBy(i => i.Kind))
                    {
                        string title = group.Key switch { BrowserKind.Preset => "Presets", BrowserKind.Clip => "Clips", BrowserKind.Midi => "MIDI files", _ => "Samples" };
                        others.Add(new BrowserRow("#" + title, title, null, true));
                        foreach (var i in group.Take(MachineBrowserModel.MaxRows)) others.Add(new BrowserRow(i.Tag, i.Name, i, false));
                    }
                }
                if (others.Count == 0 && (rows.Count == 0 || rows.All(r => r.Tag.StartsWith("none:"))))
                    rows.Add(new MachineBrowserModel.Row("none:", query.Length > 0 || filter.Category != null || filter.FavouritesOnly ? "Nothing matches" + (query.Length > 0 ? " “" + query + "”" : "") + ": clear the search" + (filter.Category != null ? ", or pick All" : "") : "No machines installed", null, true));
            }
            else
            {
                var items = model.ItemsOf(source);
                bool grouped = treeMode && items.Any(i => i.Group.Length > 0);
                if (items.Count == 0)
                    rows.Add(new MachineBrowserModel.Row("none:", EmptyText(source), null, true));
                IEnumerable<IGrouping<string, BrowserItem>> groups = items.Count == 0 ? Enumerable.Empty<IGrouping<string, BrowserItem>>() : grouped ? items.GroupBy(i => i.Group) : items.GroupBy(i => "");
                int shown = 0;
                foreach (var g in groups)
                {
                    if (grouped) others.Add(new BrowserRow("#" + g.Key, g.Key, null, true));
                    if (grouped && folded.Contains(source + "|" + g.Key)) continue;
                    foreach (var i in g)
                    {
                        if (shown++ >= MachineBrowserModel.MaxRows) break;
                        others.Add(new BrowserRow(i.Tag, i.Name, i, false));
                    }
                }
                if (items.Count > MachineBrowserModel.MaxRows) others.Add(new BrowserRow("none:", $"{items.Count - MachineBrowserModel.MaxRows:N0} more: search to narrow it", null, true));
            }

            foreach (var r in others) if (r.Item != null) itemsByTag[r.Tag] = r.Item;
            string tags = string.Join("\n", rows.Select(r => r.Tag).Concat(others.Select(r => r.Tag)));
            string shownClip = clip?.Name ?? "";
            string stamp = source + "|" + treeMode + "|" + string.Join(",", folded) + "\n" + tags + "\n" + string.Join(",", model.Favourites) + "|" + string.Join(",", model.Collections.All()) + "|" + string.Join(",", model.Collections.All().Select(model.Collections.Colour));
            if (stamp == lastStamp && shownClip == lastClip && !force) return;
            bool sameRows = stamp == lastStamp;
            lastRows = tags;
            lastStamp = stamp;
            lastClip = shownClip;
            updating = true;
            try
            {
                if (!sameRows)
                {
                    var built = new List<ListBoxItem>();
                    foreach (var r in rows)
                        built.Add(r.Entry != null || r.Header ? BrowserRows.Item(r, model, detail: query.Length > 0) : ClipRow(r, objects[r.Tag]));
                    foreach (var r in others)
                    {
                        if (r.Item == null)
                        {
                            if (r.Tag == "none:") { built.Add(BrowserRows.Item(new MachineBrowserModel.Row(r.Tag, r.Text, null, true), model, false)); continue; }
                            string key = source + "|" + r.Text;
                            bool isFolded = folded.Contains(key);
                            built.Add(allView ? BrowserRows.Header(r.Tag, r.Text) : BrowserRows.Group(r.Text, isFolded, () => { if (!folded.Add(key)) folded.Remove(key); lastStamp = null; Rebuild(force: true); }));
                            continue;
                        }
                        built.Add(r.Item.Machine != null && r.Item.Kind != BrowserKind.Preset
                            ? BrowserRows.Item(new MachineBrowserModel.Row(r.Tag, r.Item.Name, r.Item.Machine, false), model, detail: false)
                            : BrowserRows.ItemRow(r.Item, model, grouped: !allView && treeMode));
                    }
                    list.ItemsSource = built;
                }
                var current = clip == null ? null : list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == "pattern:" + clip.Name);
                if (current != null) list.SelectedItem = current;
                else if (list.SelectedItem is ListBoxItem { Tag: string t } && t.StartsWith("pattern:")) list.SelectedItem = null;
            }
            finally
            {
                updating = false;
            }
        }

        static string EmptyText(string source) => source switch
        {
            BrowserSource.Favourites => "No favourites yet: star an item",
            var s when s.StartsWith("col:") => "Nothing has this tag yet: right-click an item",
            "cat:presets" => "No factory presets found for the installed machines",
            "cat:clips" => "The song has no patterns yet",
            "cat:samples" => "No samples: add a folder with + under Sources",
            "cat:plugins" => "No plug-ins found: Preferences > Plug-ins shows the folders scanned",
            "song:audio" => "The song has no audio in its waves",
            "recent:files" => "No recent samples",
            var s when s.StartsWith("place:") => "No audio files indexed under this folder (yet)",
            _ => "Nothing here",
        };

        static ListBoxItem ClipRow(MachineBrowserModel.Row r, IClip p)
        {
            var item = new ListBoxItem
            {
                Content = new TextBlock { Text = r.Text, FontSize = 12, Foreground = StudioTokens.TextPrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis },
                Tag = r.Tag,
                DataContext = p,
                MinHeight = 24,
                Padding = new Thickness(20, 2, 8, 2),
            };
            AutomationProperties.SetName(item, r.Text);
            BrowserDragSource.Attach(item, () => new BrowserDragData(BrowserKind.Clip, p.Machine.Name + "/" + p.Name, null, p.Name));
            return item;
        }

        void OnListSelected(object sender, SelectionChangedEventArgs e)
        {
            if (updating) return;
            if (list.SelectedItem is ListBoxItem { DataContext: IClip p })
            {
                selection.Set(p.Machine, p, this);
                return;
            }
            // A result of another kind: the preview strip shows it (a sample plays when Listen is on).
            var item = SelectedItem();
            if (item != null) strip.Show(item);
        }

        /// <summary>The selected result as an item (machines and plug-ins too), or null.</summary>
        internal BrowserItem SelectedItem() => list.SelectedItem switch
        {
            ListBoxItem { DataContext: MachineEntry m } => MachineBrowserModel.MachineItem(m),
            ListBoxItem { Tag: string t } when itemsByTag.TryGetValue(t, out var i) => i,
            _ => null,
        };

        /// <summary>A double-click or Enter: a machine is added to the song (or, in swap mode, put in the machine's place), a pattern opens in the Tracker, a sample plays, a preset loads.</summary>
        internal void Activate()
        {
            if (list.SelectedItem is not ListBoxItem item) return;
            var picked = SelectedItem();
            if (model.SwapTarget != null && picked != null && (picked.Drag.IsGear || picked.Kind == BrowserKind.Preset))
            {
                var made = model.Swap(model.SwapTarget, picked.Drag, out string why);
                if (made != null) { selection.Set(made, null, this); model.EndSwap(); }
                else SwapSaid = why;
                if (why != null) { swapText.Text = why; }
                return;
            }
            switch (item.DataContext)
            {
                case MachineEntry entry:
                    var added = model.Add(entry, 0, 0);
                    if (added != null) selection.Set(added, null, this);
                    break;
                case IClip p:
                    selection.Set(p.Machine, p, this);
                    openClip(p);
                    break;
                default:
                    if (picked == null) break;
                    if (picked.Kind == BrowserKind.Preset)
                    {
                        var target = selection.Machine;
                        var made = target != null && target.DLL.Name == picked.Machine?.Gear.Instrument.MachineDLL.Name
                            ? model.Swap(target, picked.Drag, out _)
                            : model.AddDropped(picked.Drag, 0, 0, out _);
                        if (made != null) selection.Set(made, null, this);
                    }
                    else if (picked.Kind == BrowserKind.Audio && picked.Path != null)
                    {
                        strip.Listening = true;
                        strip.Show(picked);
                        model.Library.MarkRecent(picked.Path);
                    }
                    break;
            }
        }

        /// <summary>What a refused hot-swap said (tests).</summary>
        internal string SwapSaid { get; private set; }

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
            var b = StudioButtons.KeepFocus(StudioButtons.Icon(new Button { Content = glyph }, name, tip));
            b.Click += (s, e) => run();
            return b;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            selection.Changed -= OnSelectionChanged;
            model.Changed -= OnModelChanged;
            model.Library.ProgressChanged -= OnIndexProgress;
            model.SwapChanged -= OnSwapChanged;
            strip.Stop();
        }
    }
}
