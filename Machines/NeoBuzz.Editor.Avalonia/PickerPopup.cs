// Fork (docs/specs/neo-patch.md §3, phase NM): a pick-from-a-list popup for the canvas modulators' target picker, on Avalonia: a flyout
// at the pointer with the list and, for long lists (every parameter of a machine), a search box that filters as you type.
// Same API as the WPF kit's PickerPopup (NeoBuzz.Editor/PickerPopup.cs).

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;

namespace NeoBuzz.Editor
{
    internal static class PickerPopup
    {
        /// <summary>The popup that is open (for tests); null when none.</summary>
        public static Flyout Open { get; private set; }

        /// <summary>The list inside it (for tests).</summary>
        public static ListBox List { get; private set; }

        /// <summary>The search box inside it (for tests); null when the list has none.</summary>
        public static TextBox Search { get; private set; }

        /// <summary>Shows <paramref name="items"/> at the pointer over <paramref name="owner"/>; <paramref name="pick"/> gets the picked item's index in <paramref name="items"/>.</summary>
        public static void Show(Control owner, double x, double y, IReadOnlyList<string> items, Action<int> pick, bool search)
        {
            var flyout = new Flyout { Placement = PlacementMode.Pointer };
            var list = new ListBox { MaxHeight = 260, MinWidth = 200, SelectionMode = SelectionMode.Single };
            TextBox box = null;
            var shown = new List<int>();
            void Fill(string filter)
            {
                shown.Clear();
                for (int i = 0; i < items.Count; i++)
                    if (string.IsNullOrEmpty(filter) || items[i].Contains(filter, StringComparison.OrdinalIgnoreCase)) shown.Add(i);
                list.ItemsSource = shown.Select(i => items[i]).ToList();
            }
            Fill(null);
            list.SelectionChanged += (s, e) =>
            {
                int at = list.SelectedIndex;
                if (at < 0 || at >= shown.Count) return;
                int index = shown[at];
                flyout.Hide();
                pick(index);
            };
            var panel = new StackPanel { Spacing = 4, Margin = new Thickness(4) };
            if (search)
            {
                box = new TextBox { Watermark = "Search…", MinWidth = 200 };
                AutomationProperties.SetName(box, "Search");
                box.PropertyChanged += (s, e) => { if (e.Property == TextBox.TextProperty) Fill(box.Text); };
                box.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter && shown.Count > 0)
                    {
                        int index = shown[0];
                        flyout.Hide();
                        pick(index);
                        e.Handled = true;
                    }
                };
                panel.Children.Add(box);
            }
            panel.Children.Add(list);
            AutomationProperties.SetName(list, "Choices");
            flyout.Content = panel;
            flyout.Closed += (s, e) => { if (ReferenceEquals(Open, flyout)) { Open = null; List = null; Search = null; } };
            Open = flyout;
            List = list;
            Search = box;
            flyout.ShowAt(owner, showAtPointer: true);
            box?.Focus();
        }
    }
}
