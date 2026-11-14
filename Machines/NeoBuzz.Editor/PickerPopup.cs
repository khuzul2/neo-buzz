#nullable disable
// Fork (docs/specs/neo-patch.md §3, phase NM): a pick-from-a-list popup for the canvas modulators' target picker, on WPF: a popup at the
// pointer with the list and, for long lists (every parameter of a machine), a search box that filters as you type.
// Same API as the studio kit's PickerPopup (NeoBuzz.Editor.Avalonia/PickerPopup.cs).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NeoBuzz.Editor
{
    internal static class PickerPopup
    {
        /// <summary>Shows <paramref name="items"/> at the pointer over <paramref name="owner"/>; <paramref name="pick"/> gets the picked item's index in <paramref name="items"/>.</summary>
        public static void Show(UIElement owner, double x, double y, IReadOnlyList<string> items, Action<int> pick, bool search)
        {
            var popup = new Popup { PlacementTarget = owner, Placement = PlacementMode.MousePoint, StaysOpen = false, AllowsTransparency = false };
            var list = new ListBox { MaxHeight = 260, MinWidth = 200 };
            var shown = new List<int>();
            void Fill(string filter)
            {
                shown.Clear();
                for (int i = 0; i < items.Count; i++)
                    if (string.IsNullOrEmpty(filter) || items[i].IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) shown.Add(i);
                list.ItemsSource = shown.Select(i => items[i]).ToList();
            }
            Fill(null);
            list.SelectionChanged += (s, e) =>
            {
                int at = list.SelectedIndex;
                if (at < 0 || at >= shown.Count) return;
                int index = shown[at];
                popup.IsOpen = false;
                pick(index);
            };
            var panel = new StackPanel { Margin = new Thickness(4) };
            TextBox box = null;
            if (search)
            {
                box = new TextBox { MinWidth = 200, Margin = new Thickness(0, 0, 0, 4), ToolTip = "Search" };
                box.TextChanged += (s, e) => Fill(box.Text);
                box.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter && shown.Count > 0)
                    {
                        int index = shown[0];
                        popup.IsOpen = false;
                        pick(index);
                        e.Handled = true;
                    }
                };
                panel.Children.Add(box);
            }
            panel.Children.Add(list);
            popup.Child = new Border { Child = panel, BorderThickness = new Thickness(1), BorderBrush = SystemColors.ControlDarkBrush, Background = SystemColors.WindowBrush };
            popup.IsOpen = true;
            box?.Focus();
        }
    }
}
