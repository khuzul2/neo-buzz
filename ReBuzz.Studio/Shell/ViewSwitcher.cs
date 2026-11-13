// Fork (docs/specs/ux-overhaul.md P3a, decision 37): the segmented view switcher, Arrangement | Session | Machines, above the
// main view. It replaces the TabControl's own strip (which wrapped at 640 pt); the keys, ShowView and the menu still go
// through the window, the switcher only shows the selected view and asks for another.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell
{
    /// <summary>The names the main window's views had, and have: <c>ShowView</c> takes either.</summary>
    internal static class ViewNames
    {
        public static string Canonical(string name) => name switch
        {
            "Sequence" => "Arrangement",
            _ => name,
        };
    }

    internal sealed class ViewSwitcher : Border
    {
        readonly StackPanel strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        readonly Dictionary<string, Button> buttons = new Dictionary<string, Button>();

        public ViewSwitcher()
        {
            Background = StudioTokens.SurfacePanelBrush;
            Padding = new Thickness(8, 4);
            Child = new Border
            {
                Background = StudioTokens.SurfaceBaseBrush,
                BorderBrush = StudioTokens.SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = strip,
            };
            AutomationProperties.SetName(this, "View");
        }

        /// <summary>The segments, in order (for tests).</summary>
        public IReadOnlyDictionary<string, Button> Segments => buttons;

        public void Add(string title, string tip, Action run)
        {
            var b = new Segment
            {
                Content = title,
                FontSize = 12,
                Padding = new Thickness(12, 3),
                MinHeight = 24,
                MinWidth = 72,
                Focusable = true,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            AutomationProperties.SetName(b, title);
            ToolTip.SetTip(b, tip);
            b.Click += (s, e) => run();
            // Tab reaches each segment (Enter picks it); a ring shows where the keyboard is.
            b.GotFocus += (s, e) => { focusRing = b; Mark(b, selected == title); };
            b.LostFocus += (s, e) => { if (focusRing == b) focusRing = null; Mark(b, selected == title); };
            Mark(b, false);
            buttons[title] = b;
            strip.Children.Add(b);
        }

        /// <summary>Lights the segment of the view shown.</summary>
        string selected;
        static Button focusRing;

        public void Select(string title)
        {
            selected = title;
            foreach (var pair in buttons) Mark(pair.Value, pair.Key == title);
        }

        static void Mark(Button b, bool on)
        {
            b.Background = on ? StudioTokens.SelectionFillBrush : Brushes.Transparent;
            bool ring = focusRing == b && b.IsKeyboardFocusWithin;
            b.BorderBrush = ring ? StudioTokens.TextPrimaryBrush : on ? StudioTokens.AccentBrush : Brushes.Transparent;
            b.BorderThickness = new Thickness(ring ? 2 : 1);
            b.Foreground = on ? StudioTokens.TextPrimaryBrush : StudioTokens.TextSecondaryBrush;
            b.FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    /// <summary>
    /// A segment of the switcher: Tab-focusable, Enter and a click pick it, and Space is left to the window (it plays and stops everywhere,
    /// studio-mvp.md §3.11), so a focused segment never takes it.
    /// </summary>
    internal sealed class Segment : Button
    {
        protected override Type StyleKeyOverride => typeof(Button);

        protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Space) return;
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Space) return;
            base.OnKeyUp(e);
        }
    }
}
