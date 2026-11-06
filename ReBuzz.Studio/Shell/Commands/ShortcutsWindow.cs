// Fork (docs/specs/ux-overhaul.md §4, UX review mac-331): Help ▸ Keyboard Shortcuts, a searchable window made from the
// same keymap as the menus (StudioKeymap): the app's commands by menu, then each view's own keys. It replaced a 12-line
// message box; the Pattern editor's keys come from its own help panel, the Session View's from its key list.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell.Commands
{
    internal sealed class ShortcutsWindow : Window
    {
        /// <summary>One line of the list: its section, its keys as read, what they do.</summary>
        internal sealed record Row(string Section, string Keys, string What);

        readonly TextBox search;
        readonly StackPanel list = new StackPanel { Margin = new Thickness(16, 8, 16, 16) };
        readonly TextBlock none;
        readonly List<Row> rows;

        static ShortcutsWindow current;

        /// <summary>Opens the window (or brings it to the front), its search box focused.</summary>
        public static ShortcutsWindow Open(Window owner = null)
        {
            if (current == null)
            {
                current = new ShortcutsWindow(AllRows(KeyText.IsMac));
                current.Closed += (s, e) => current = null;
                if (owner != null) current.Show(owner);
                else current.Show();
            }
            else
            {
                current.Activate();
            }
            current.search.Focus();
            return current;
        }

        internal static ShortcutsWindow Current => current;

        /// <summary>Every row: the commands (by menu, with their keys and alternates), then the views' own keys.</summary>
        internal static List<Row> AllRows(bool mac)
        {
            var result = new List<Row>();
            foreach (var c in StudioKeymap.Commands)
            {
                if (c.Menu == "App" || c.Id == "help.about" || (mac && (c.Id is "file.exit" or "edit.preferences")) || (!mac && c.Id is "window.front"))
                    continue;
                string keys = c.KeyLabel(mac);
                if (keys.Length == 0) continue;
                string what = c.Text.TrimEnd('…');
                if (!string.IsNullOrEmpty(c.Help)) what += ": " + Lower(c.Help);
                if (!string.IsNullOrEmpty(c.Where)) what += ". In " + c.Where;
                result.Add(new Row(c.Menu, keys, what));
            }
            if (mac) result.Insert(result.FindIndex(r => r.Section == "Edit"), new Row("App", "⌘,", "Preferences"));
            foreach (var k in StudioKeymap.ViewKeys(mac)) result.Add(new Row(k.Section, k.Keys, k.What));
            foreach (var k in StudioKeymap.PatternKeys(PatternEditorRows(), mac)) result.Add(new Row(k.Section, k.Keys, k.What));
            return result;
        }

        static string Lower(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s.Substring(1) : s;

        /// <summary>The Pattern editor's key table (its help panel's), from its view's assembly once Gear has loaded it.</summary>
        static IEnumerable<string[]> PatternEditorRows()
        {
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("WDE.ModernPatternEditor.AvaloniaView.PatternEditorView", false))
                    .FirstOrDefault(t => t != null);
                return type?.GetField("KeyRows", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) as string[][];
            }
            catch (Exception)
            {
                return null;
            }
        }

        ShortcutsWindow(List<Row> rows)
        {
            this.rows = rows;
            Title = "Keyboard Shortcuts";
            Width = 620;
            Height = 680;
            MinWidth = 420;
            MinHeight = 300;
            Background = StudioTokens.SurfaceViewBrush;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            search = new TextBox
            {
                PlaceholderText = "Search keys and commands",
                Margin = new Thickness(16, 16, 16, 8),
                FontSize = 13,
                Tag = "search",
            };
            search.TextChanged += (s, e) => Fill(search.Text);
            none = new TextBlock { Text = "No key matches.", Foreground = StudioTokens.TextSecondaryBrush, Margin = new Thickness(0, 8), IsVisible = false };
            var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            var root = new DockPanel();
            DockPanel.SetDock(search, Dock.Top);
            root.Children.Add(search);
            root.Children.Add(scroll);
            Content = root;
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    if (!string.IsNullOrEmpty(search.Text)) search.Text = "";
                    else Close();
                    e.Handled = true;
                }
            };
            Fill(null);
        }

        /// <summary>The rows shown now (tests).</summary>
        internal IReadOnlyList<Row> Shown { get; private set; } = Array.Empty<Row>();

        internal string Search
        {
            get => search.Text;
            set => search.Text = value;
        }

        /// <summary>The rows whose section, keys or words have every word of the search in them.</summary>
        internal static IEnumerable<Row> Filter(IEnumerable<Row> rows, string text)
        {
            var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return rows.Where(r => words.All(w =>
                r.Section.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                r.Keys.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                r.What.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }

        void Fill(string text)
        {
            list.Children.Clear();
            var shown = Filter(rows, text).ToList();
            Shown = shown;
            foreach (var section in shown.GroupBy(r => r.Section))
            {
                list.Children.Add(new TextBlock
                {
                    Text = section.Key,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = StudioTokens.TextPrimaryBrush,
                    Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 16, 0, 6),
                });
                foreach (var r in section)
                {
                    var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*"), Margin = new Thickness(0, 2) };
                    var keys = new Border
                    {
                        Background = StudioTokens.SurfaceControlBrush,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 0, 12, 0),
                        Child = new TextBlock { Text = r.Keys, FontSize = 12, Foreground = StudioTokens.TextPrimaryBrush, TextWrapping = TextWrapping.Wrap },
                    };
                    var what = new TextBlock { Text = r.What, FontSize = 12, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(what, 1);
                    grid.Children.Add(keys);
                    grid.Children.Add(what);
                    list.Children.Add(grid);
                }
            }
            none.IsVisible = shown.Count == 0;
            list.Children.Add(none);
        }
    }
}
