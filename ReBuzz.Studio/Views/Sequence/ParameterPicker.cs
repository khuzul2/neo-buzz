// Fork (docs/specs/daw-modes.md AR4, §4.3): the list a track's "A" button offers: the state parameters of
// the machine (a track parameter once for each track), with a filter box. A double click, Enter or the Add
// button picks one; the arrangement then shows its lane under the track.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.Studio.Views.Sequence
{
    internal sealed class ParameterPicker : StackPanel
    {
        readonly List<AutoParam> all;
        readonly TextBox filter;
        readonly ListBox list;
        readonly Button add;

        public ParameterPicker(IMachine machine)
        {
            all = AutomationParams.List(machine);
            Width = 260;
            Spacing = 6;
            filter = new TextBox { Watermark = "Find a parameter" };
            AutomationProperties.SetName(filter, "Find a parameter");
            list = new ListBox { Height = 220 };
            AutomationProperties.SetName(list, "Parameters");
            add = new Button { Content = "Add lane", HorizontalAlignment = HorizontalAlignment.Right, IsEnabled = false };
            AutomationProperties.SetName(add, "Add lane");
            Children.Add(filter);
            Children.Add(list);
            Children.Add(add);
            filter.TextChanged += (s, e) => Fill();
            list.SelectionChanged += (s, e) => add.IsEnabled = list.SelectedItem != null;
            list.DoubleTapped += (s, e) => Pick();
            list.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Pick(); e.Handled = true; } };
            filter.KeyDown += (s, e) => { if (e.Key == Key.Enter) { if (list.SelectedItem == null && list.ItemCount > 0) list.SelectedIndex = 0; Pick(); e.Handled = true; } };
            add.Click += (s, e) => Pick();
            Fill();
        }

        /// <summary>A parameter was picked.</summary>
        public event Action<AutoParam> Chosen;

        public IReadOnlyList<AutoParam> Parameters => all;

        void Fill()
        {
            string text = filter.Text?.Trim() ?? "";
            var shown = all.Where(p => text.Length == 0 || p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
            list.ItemsSource = shown.Select(p => p.Name).ToList();
            shownParams = shown;
        }

        List<AutoParam> shownParams = new();

        void Pick()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= shownParams.Count) return;
            Chosen?.Invoke(shownParams[list.SelectedIndex]);
        }

        /// <summary>Picks a parameter by name (and track, 1-based, for a track parameter): what the list does for a click on it.</summary>
        public bool Choose(string name)
        {
            filter.Text = "";
            Fill();
            int i = shownParams.FindIndex(p => p.Name == name);
            if (i < 0) return false;
            list.SelectedIndex = i;
            Pick();
            return true;
        }
    }
}
