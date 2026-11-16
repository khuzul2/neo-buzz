// Fork (docs/specs/ux-overhaul.md P3d, decision 57): the two pieces the browser panel and the canvas popup share, so they cannot
// drift into two browsers: the filter bar (category, favourites only) and the rows (name, brand and tags, what it is for, a star).

using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using System.Linq;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Browser
{
    /// <summary>A category chooser (All, the ten categories, Other) and a "favourites only" toggle.</summary>
    internal sealed class FilterBar : DockPanel
    {
        const string All = "All categories";
        readonly ComboBox category = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        readonly ToggleButton favourites = StudioButtons.KeepFocus(new ToggleButton { Content = "★", Margin = new Thickness(4, 0, 0, 0) });

        public FilterBar()
        {
            category.ItemsSource = new[] { All }.Concat(MachineBrowserModel.Categories).Append(MachineBrowserModel.PlugIns).Append(MachineBrowserModel.Other).ToList();
            category.SelectedIndex = 0;
            AutomationProperties.SetName(category, "Category");
            AutomationProperties.SetName(favourites, "Favourites only");
            ToolTip.SetTip(favourites, "Show only favourites");
            SetDock(favourites, Dock.Right);
            Children.Add(favourites);
            Children.Add(category);
            category.SelectionChanged += (s, e) => Changed?.Invoke();
            favourites.IsCheckedChanged += (s, e) => Changed?.Invoke();
        }

        public event Action Changed;

        /// <summary>The chosen category, or null for all.</summary>
        public string Category
        {
            get => category.SelectedItem as string is { } c && c != All ? c : null;
            set => category.SelectedItem = value ?? All;
        }

        public bool FavouritesOnly
        {
            get => favourites.IsChecked == true;
            set => favourites.IsChecked = value;
        }

        internal ComboBox CategoryBox => category;
        internal ToggleButton FavouritesToggle => favourites;
    }

    internal static class BrowserRows
    {
        /// <summary>A list item for a model row. Machines carry their entry as DataContext and "gear:Name" as Tag; headers "#Title".</summary>
        public static ListBoxItem Item(MachineBrowserModel.Row row, MachineBrowserModel model, bool detail)
        {
            if (row.Header) return Header(row.Tag, row.Text);
            var e = row.Entry;
            var name = new TextBlock { Text = e.Name, FontSize = 12, Foreground = StudioTokens.TextPrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis };
            var stack = new StackPanel { Spacing = 1 };
            stack.Children.Add(name);
            if (e.TagLine.Length > 0) stack.Children.Add(Small(e.TagLine));
            if (detail && e.UseFor.Length > 0) stack.Children.Add(Small(e.UseFor));

            bool fav = model.IsFavourite(e);
            var star = StudioButtons.KeepFocus(StudioButtons.Icon(new Button
            {
                Content = fav ? "★" : "☆",
                Foreground = fav ? StudioTokens.AccentBrush : StudioTokens.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Top,
            }, (fav ? "Unfavourite " : "Favourite ") + e.Name, fav ? "Remove from favourites" : "Add to favourites"));
            star.Click += (s, a) => model.ToggleFavourite(e);
            DockPanel.SetDock(star, Dock.Right);

            var content = new DockPanel { LastChildFill = true, Children = { star, stack } };
            var item = new ListBoxItem
            {
                Content = content,
                Tag = row.Tag,
                DataContext = e,
                Padding = new Thickness(16, 2, 4, 2),
                MinHeight = 24,
            };
            ToolTip.SetTip(item, e.UseFor.Length > 0 ? e.UseFor : e.Name);
            AutomationProperties.SetName(item, e.Name);
            BrowserDragSource.Attach(item, () => MachineBrowserModel.MachineItem(e).Drag);
            return item;
        }

        /// <summary>The seven tag colours.</summary>
        public static IBrush ColourBrush(int colour) => ReBuzz.Studio.Views.Session.SessionPalette.Tags[Math.Clamp(colour, 1, 7) - 1];

        /// <summary>A list item for any other result (preset, pattern, sample, MIDI file, the song's wave): name, what it is, a star, a colour dot, a menu, and it can be dragged.</summary>
        public static ListBoxItem ItemRow(BrowserItem it, MachineBrowserModel model, bool grouped = false)
        {
            var stack = new StackPanel { Spacing = 1 };
            stack.Children.Add(new TextBlock { Text = it.Name, FontSize = 12, Foreground = StudioTokens.TextPrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis });
            if (it.Detail.Length > 0) stack.Children.Add(Small(it.Detail));
            bool fav = model.IsFavourite(it);
            int colour = model.ColourOf(it);
            var star = StudioButtons.KeepFocus(StudioButtons.Icon(new Button
            {
                Content = fav ? "★" : "☆",
                Foreground = colour > 0 ? ColourBrush(colour) : fav ? StudioTokens.AccentBrush : StudioTokens.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Top,
            }, (fav ? "Unfavourite " : "Favourite ") + it.Name, fav ? "Remove from favourites" : "Add to favourites"));
            star.Click += (s, a) => model.ToggleFavourite(it);
            DockPanel.SetDock(star, Dock.Right);
            var item = new ListBoxItem
            {
                Content = new DockPanel { LastChildFill = true, Children = { star, stack } },
                Tag = it.Tag,
                DataContext = it,
                Padding = new Thickness(grouped ? 20 : 10, 2, 4, 2),
                MinHeight = 24,
            };
            var menu = new ContextMenu();
            var items = new System.Collections.Generic.List<object> { new MenuItem { Header = fav ? "Remove from favourites" : "Add to favourites" } };
            ((MenuItem)items[0]).Click += (s, a) => model.ToggleFavourite(it);
            for (int c = 1; c <= 7; c++)
            {
                int tag = c;
                var m = new MenuItem { Header = "Tag " + ReBuzz.Agent.Browser.BrowserCollections.ColourNames[c - 1].ToLowerInvariant(), Icon = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = ColourBrush(c) } };
                m.Click += (s, a) => model.SetColour(it, tag);
                items.Add(m);
            }
            var none = new MenuItem { Header = "Remove the tag" };
            none.Click += (s, a) => model.SetColour(it, 0);
            items.Add(none);
            // Fork (docs/specs/stem-separation.md): a sample file into stems on new tracks.
            if (it.Kind == ReBuzz.Agent.Browser.BrowserKind.Audio && it.Path != null)
            {
                items.Add(new Separator());
                var separate = new MenuItem { Header = "Separate stems…" };
                string file = it.Path;
                separate.Click += (s, a) => _ = ReBuzz.Studio.Shell.StemFlow.SeparateFile(file);
                items.Add(separate);
            }
            menu.ItemsSource = items;
            item.ContextMenu = menu;
            ToolTip.SetTip(item, it.Path ?? it.Name);
            AutomationProperties.SetName(item, it.Name);
            BrowserDragSource.Attach(item, () => it.Drag);
            return item;
        }

        /// <summary>A group header of the tree: a click folds it.</summary>
        public static ListBoxItem Group(string title, bool folded, Action toggle)
        {
            var item = new ListBoxItem
            {
                Content = new TextBlock { Text = (folded ? "▸ " : "▾ ") + title, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextSecondaryBrush, TextTrimming = TextTrimming.CharacterEllipsis },
                Tag = "#" + title,
                MinHeight = 22,
                Padding = new Thickness(8, 6, 8, 2),
                Focusable = false,
            };
            item.Tapped += (s, e) => toggle();
            AutomationProperties.SetName(item, title);
            return item;
        }

        static TextBlock Small(string text) => new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = StudioTokens.TextSecondaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        public static ListBoxItem Header(string tag, string text)
        {
            var item = new ListBoxItem
            {
                Content = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextSecondaryBrush, TextTrimming = TextTrimming.CharacterEllipsis },
                Tag = tag,
                MinHeight = 22,
                Padding = new Thickness(12, 6, 8, 2),
                Focusable = false,
                IsHitTestVisible = false,
            };
            AutomationProperties.SetName(item, text);
            return item;
        }
    }
}
