// Fork (docs/specs/plugin-host.md §3.7, H1b): Preferences ▸ Plug-ins. Where the plug-in host looks for CLAP and VST3 plug-ins
// (the standard folders, NEOBUZZ_PLUGIN_PATH and the folders added here), a rescan, what was found, and the blocklist: a plug-in
// that crashed, hung or failed to load while it was scanned is left out, with the reason, until it is unblocked or its file changes.
//
// The page shares the machines' library code (Machines/NeoPlug/PlugLibrary.cs, linked into the studio) and the files it keeps in
// the plug-in data folder (library.json, folders.txt); the Neo Plug machines read library.json again when it changes, so what a
// rescan finds is on the Gear menu the next time it is built.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using NeoBuzz.Plug;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class PluginsPage : PrefsPage
    {
        readonly StackPanel foldersPanel = new StackPanel { Spacing = 2 };
        readonly StackPanel foundPanel = new StackPanel { Spacing = 2 };
        readonly StackPanel blockedPanel = new StackPanel { Spacing = 4 };
        readonly StackPanel sandboxPanel = new StackPanel { Spacing = 2 };
        readonly ComboBox defaultPlacement = new ComboBox { MinWidth = 220 };
        bool fillingPlacement;
        readonly TextBox folderBox = new TextBox { Watermark = "A folder with .vst3 or .clap plug-ins", Width = 380 };
        readonly Button addButton = new Button { Content = "Add folder" };
        readonly Button rescanButton = new Button { Content = "Rescan" };
        readonly Button rescanAllButton = new Button { Content = "Rescan everything" };
        readonly Button unblockAllButton = new Button { Content = "Unblock all" };
        readonly TextBlock status = new TextBlock { Foreground = Dim, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        string shown;
        bool wasScanning;

        public PluginsPage(PrefsModel model) : base(model)
        {
            Heading("Folders");
            Note("The plug-in host looks for CLAP and VST3 plug-ins in these folders. Plug-ins are scanned in a process of their own, so one that crashes or hangs costs the scanner and nothing else.");
            Body.Children.Add(foldersPanel);
            var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4) };
            add.Children.Add(folderBox);
            add.Children.Add(addButton);
            Body.Children.Add(add);
            AutomationProperties.SetName(folderBox, "Plug-in folder to add");
            AutomationProperties.SetName(addButton, "Add folder");
            AutomationProperties.SetName(rescanButton, "Rescan plug-ins");
            AutomationProperties.SetName(rescanAllButton, "Rescan everything");
            AutomationProperties.SetName(unblockAllButton, "Unblock all");
            addButton.Click += (_, _) => AddFolder();
            folderBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) AddFolder(); };

            Heading("Scan");
            var scan = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            scan.Children.Add(rescanButton);
            scan.Children.Add(rescanAllButton);
            Body.Children.Add(scan);
            Body.Children.Add(status);
            Note("Rescan looks at what changed since the last scan. Rescan everything looks at every plug-in again, blocked ones included.");
            rescanButton.Click += (_, _) => Rescan(force: false);
            rescanAllButton.Click += (_, _) => Rescan(force: true);

            Heading("Found");
            Body.Children.Add(foundPanel);

            Heading("Where plug-ins run");
            Note("A sandbox is a process of its own: a plug-in that crashes there is silent and can be reloaded, and the song plays on. In process is the lightest. "
                 + "A blocked plug-in that was unblocked always runs in a sandbox. The default is a sandbox per plug-in on Windows and in process elsewhere; a song remembers the choice made on its machine.");
            var defRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 2) };
            defRow.Children.Add(new TextBlock { Text = "Default", VerticalAlignment = VerticalAlignment.Center });
            defaultPlacement.ItemsSource = PlacementChoices(true);
            defaultPlacement.SelectedIndex = 0;
            AutomationProperties.SetName(defaultPlacement, "Default placement");
            defaultPlacement.SelectionChanged += (_, _) =>
            {
                if (fillingPlacement || defaultPlacement.SelectedIndex < 0) return;
                PlugPlacement.Default = PlacementValue(defaultPlacement.SelectedIndex, true);
                Refresh();
            };
            defRow.Children.Add(defaultPlacement);
            Body.Children.Add(defRow);
            Body.Children.Add(sandboxPanel);

            Heading("Blocked");
            Note("A blocked plug-in is not offered on the Gear menu. Unblocking it tries it again at the next scan.");
            Body.Children.Add(blockedPanel);
            Body.Children.Add(unblockAllButton);
            unblockAllButton.Click += (_, _) =>
            {
                PlugLibrary.UnblockAll();
                Refresh();
            };

            PollWhileShown(TimeSpan.FromSeconds(1));
        }

        public override string Title => "Plug-ins";

        /// <summary>The default's combo box and the rows for plug-ins with a choice of their own (for tests).</summary>
        public ComboBox DefaultPlacement => defaultPlacement;
        public IReadOnlyList<(PlugEntry Entry, ComboBox Choice)> PlacementRows => placementRows;
        readonly List<(PlugEntry, ComboBox)> placementRows = new();

        static List<string> PlacementChoices(bool isDefault)
        {
            var l = new List<string> { isDefault ? "Platform default (" + PlugPlacement.Label(PlugPlacement.PlatformDefault) + ")" : "Default" };
            foreach (var v in PlugPlacement.Values) l.Add(PlugPlacement.Label(v));
            return l;
        }

        static string PlacementValue(int index, bool isDefault) => index <= 0 ? PlugPlacement.Auto : PlugPlacement.Values[index - 1];
        static int PlacementIndex(string value) => string.IsNullOrEmpty(value) ? 0 : Array.IndexOf(PlugPlacement.Values, value) + 1;

        public TextBox FolderBox => folderBox;
        public Button AddButton => addButton;
        public Button RescanButton => rescanButton;
        public Button RescanAllButton => rescanAllButton;
        public Button UnblockAllButton => unblockAllButton;

        /// <summary>The blocked plug-ins as listed: a row each, with its Unblock button (for tests).</summary>
        public IReadOnlyList<(string Path, string Reason, Button Unblock)> BlockedRows => blockedRows;
        readonly List<(string, string, Button)> blockedRows = new();

        /// <summary>The folders as listed: the path, whether the user added it, and its Remove button (for tests).</summary>
        public IReadOnlyList<(string Path, bool Custom, Button Remove)> FolderRows => folderRows;
        readonly List<(string, bool, Button)> folderRows = new();

        public string StatusText => status.Text;

        public override void RestoreOwn() { }

        protected override void OnShown()
        {
            shown = null;
            Refresh();
        }

        void AddFolder()
        {
            string folder = folderBox.Text?.Trim() ?? "";
            if (folder.Length == 0) return;
            if (!Directory.Exists(folder))
            {
                status.Text = $"{folder} isn't a folder that exists.";
                status.Foreground = Problem;
                return;
            }
            PlugPaths.AddFolder(folder);
            folderBox.Text = "";
            status.Foreground = Dim;
            status.Text = $"Added {folder}. Rescan to look in it.";
            Refresh();
        }

        void Rescan(bool force)
        {
            if (PlugLibrary.Scanning) return;
            if (PlugPaths.ScannerPath() == null)
            {
                status.Text = "The plug-in scanner (neoplug-scan) isn't installed beside the app.";
                status.Foreground = Problem;
                return;
            }
            status.Foreground = Dim;
            status.Text = "Scanning…";
            PlugLibrary.ScanInBackground(force);
            wasScanning = true;
            Refresh();
        }

        /// <summary>Rebuilds the lists when the library, the folders or a scan changed.</summary>
        public override void Refresh()
        {
            bool scanning = PlugLibrary.Scanning;
            var entries = PlugLibrary.All();
            var blocked = PlugLibrary.Blocked();
            var user = PlugPaths.UserFolders();
            var dirs = PlugPaths.SearchDirs();
            string state = string.Join("|", dirs) + "#" + string.Join("|", user) + "#" + string.Join("|", entries.Select(e => e.Display + e.Path)) + "#" +
                           string.Join("|", blocked.Select(b => b.Path + b.Reason)) + "#" + scanning + "#" +
                           PlugPlacement.Default + "#" + string.Join("|", PlugPlacement.Chosen().Select(kv => kv.Key + "=" + kv.Value)) + "#" +
                           string.Join("|", PlugPlacement.FlaggedModules().Keys);
            if (wasScanning && !scanning)
            {
                wasScanning = false;
                var report = PlugLibrary.LastReport;
                status.Foreground = Dim;
                status.Text = report == null ? "Scan finished." : "Scan finished: " + report;
            }
            else if (scanning)
            {
                status.Text = "Scanning…";
            }
            rescanButton.IsEnabled = rescanAllButton.IsEnabled = !scanning;
            unblockAllButton.IsEnabled = blocked.Count > 0;
            if (state == shown) return;
            shown = state;
            FillFolders(dirs, user);
            FillFound(entries);
            FillBlocked(blocked);
            FillPlacement(entries);
        }

        void FillPlacement(List<PlugEntry> entries)
        {
            fillingPlacement = true;
            try { defaultPlacement.SelectedIndex = PlacementIndex(PlugPlacement.Default); }
            finally { fillingPlacement = false; }
            sandboxPanel.Children.Clear();
            placementRows.Clear();
            var chosen = PlugPlacement.Chosen();
            var flagged = PlugPlacement.FlaggedModules();
            // The plug-ins with a choice of their own, and the ones that are forced into a sandbox; any other plug-in follows the default.
            foreach (var e in entries)
            {
                string key = PlugPlacement.Key(e.Format, e.Id, e.Name);
                bool forced = PlugPlacement.Forced(e.Path) != null;
                if (!chosen.ContainsKey(key) && !forced) continue;
                AddPlacementRow(e, forced);
            }
            foreach (var kv in flagged)
            {
                var path = kv.Key;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 2) };
                row.Children.Add(new TextBlock { Text = Path.GetFileName(path.TrimEnd('/', '\\')) + ": always in a sandbox (" + kv.Value + ")", VerticalAlignment = VerticalAlignment.Center });
                var clear = new Button { Content = "Clear flag" };
                AutomationProperties.SetName(clear, "Clear flag " + Path.GetFileName(path.TrimEnd('/', '\\')));
                clear.Click += (_, _) => { PlugPlacement.Unflag(path); Refresh(); };
                row.Children.Add(clear);
                sandboxPanel.Children.Add(row);
            }
            // Pick a plug-in to give it a choice of its own.
            var picker = new ComboBox { MinWidth = 260, PlaceholderText = "Choose a plug-in…" };
            var cands = entries.Where(e => !chosen.ContainsKey(PlugPlacement.Key(e.Format, e.Id, e.Name))).Take(400).ToList();
            picker.ItemsSource = cands.Select(e => e.Display).ToList();
            AutomationProperties.SetName(picker, "Choose a plug-in for its own placement");
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex < 0 || picker.SelectedIndex >= cands.Count) return;
                var e = cands[picker.SelectedIndex];
                PlugPlacement.SetForPlugin(e.Format, e.Id, e.Name, PlugPlacement.Native(PlugPlacement.PlatformDefault) == 0 ? PlugPlacement.PerPlugin : PlugPlacement.InProcess);
                Refresh();
            };
            if (cands.Count > 0) sandboxPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 4, 2), Children = { picker } });
        }

        void AddPlacementRow(PlugEntry e, bool forced)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 2) };
            row.Children.Add(new TextBlock { Text = e.Display, VerticalAlignment = VerticalAlignment.Center, Width = 260, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
            var combo = new ComboBox { MinWidth = 220, ItemsSource = PlacementChoices(false), IsEnabled = true };
            combo.SelectedIndex = PlacementIndex(PlugPlacement.ForPlugin(e.Format, e.Id, e.Name));
            AutomationProperties.SetName(combo, "Placement of " + e.Display);
            var entry = e;
            combo.SelectionChanged += (_, _) =>
            {
                if (fillingPlacement || combo.SelectedIndex < 0) return;
                PlugPlacement.SetForPlugin(entry.Format, entry.Id, entry.Name, PlacementValue(combo.SelectedIndex, false));
                Refresh();
            };
            row.Children.Add(combo);
            if (forced) row.Children.Add(new TextBlock { Text = "sandboxed whatever this says: " + PlugPlacement.Forced(e.Path), Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            sandboxPanel.Children.Add(row);
            placementRows.Add((e, combo));
        }

        void FillFolders(List<string> dirs, List<string> user)
        {
            foldersPanel.Children.Clear();
            folderRows.Clear();
            foreach (var dir in dirs)
            {
                bool custom = user.Contains(dir);
                bool exists = Directory.Exists(dir);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
                var text = new TextBlock
                {
                    Text = dir + (exists ? "" : "  (not there)"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(4, 2),
                };
                if (!exists) text.Foreground = Dim;        // an explicit null Foreground would draw nothing, not the inherited brush
                Grid.SetColumn(text, 0);
                row.Children.Add(text);
                Button remove = null;
                if (custom)
                {
                    remove = new Button { Content = "Remove", Tag = dir };
                    AutomationProperties.SetName(remove, "Remove folder " + dir);
                    string d = dir;
                    remove.Click += (_, _) =>
                    {
                        PlugPaths.RemoveFolder(d);
                        Refresh();
                    };
                    Grid.SetColumn(remove, 1);
                    row.Children.Add(remove);
                }
                else
                {
                    var kind = new TextBlock { Text = "standard", Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
                    Grid.SetColumn(kind, 1);
                    row.Children.Add(kind);
                }
                foldersPanel.Children.Add(row);
                folderRows.Add((dir, custom, remove));
            }
            if (dirs.Count == 0) foldersPanel.Children.Add(new TextBlock { Text = "No folders.", Foreground = Dim, Margin = new Thickness(4, 2) });
        }

        void FillFound(List<PlugEntry> entries)
        {
            foundPanel.Children.Clear();
            int vst3 = entries.Count(e => e.Format == "vst3"), clap = entries.Count(e => e.Format == "clap");
            int instruments = entries.Count(e => e.Instrument);
            foundPanel.Children.Add(new TextBlock
            {
                Text = entries.Count == 0
                    ? "No plug-ins found yet. Add a folder or rescan."
                    : $"{entries.Count} plug-ins: {instruments} instruments and {entries.Count - instruments} effects ({vst3} VST3, {clap} CLAP).",
                Margin = new Thickness(4, 2),
            });
        }

        void FillBlocked(List<BlockedModule> blocked)
        {
            blockedPanel.Children.Clear();
            blockedRows.Clear();
            if (blocked.Count == 0)
            {
                blockedPanel.Children.Add(new TextBlock { Text = "None blocked.", Foreground = Dim, Margin = new Thickness(4, 2) });
                return;
            }
            foreach (var b in blocked)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
                var text = new StackPanel { Spacing = 0, Margin = new Thickness(4, 2) };
                text.Children.Add(new TextBlock { Text = Path.GetFileName(b.Path.TrimEnd('/', '\\')), FontWeight = Avalonia.Media.FontWeight.SemiBold });
                text.Children.Add(new TextBlock { Text = b.Reason, Foreground = Problem, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                text.Children.Add(new TextBlock { Text = b.Path, Foreground = Dim, FontSize = 11, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
                Grid.SetColumn(text, 0);
                row.Children.Add(text);
                var unblock = new Button { Content = "Unblock", VerticalAlignment = VerticalAlignment.Center, Tag = b.Path };
                AutomationProperties.SetName(unblock, "Unblock " + Path.GetFileName(b.Path.TrimEnd('/', '\\')));
                string path = b.Path;
                unblock.Click += (_, _) =>
                {
                    PlugLibrary.Unblock(path);
                    Refresh();
                };
                Grid.SetColumn(unblock, 1);
                row.Children.Add(unblock);
                blockedPanel.Children.Add(row);
                blockedRows.Add((b.Path, b.Reason, unblock));
            }
        }
    }
}
