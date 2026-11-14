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
            category.ItemsSource = new[] { All }.Concat(MachineBrowserModel.Categories).Append(MachineBrowserModel.Other).ToList();
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
