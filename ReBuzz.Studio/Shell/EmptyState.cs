// Fork (docs/specs/ux-overhaul.md P3i, decisions 88-90): the empty state of a view: what is missing, in two lines, and the next step as
// buttons. In a song with only its Master the steps are the starters (Neo Poly, Neo Drums, Neo Wave, one click each) and Add Machine…;
// everywhere the next step is the one that makes the view stop being empty. One look for every view: a 15 pt title, 12 pt text in the
// secondary colour, 13 pt buttons (all on the tokens: text 4.5:1 or more on a view or a panel).

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Browser;
using ReBuzz.Studio.Views.Machines;

namespace ReBuzz.Studio.Shell
{
    /// <summary>What the empty states do for a next step: the window sets <see cref="AddMachine"/> (it knows the Machine View).</summary>
    internal static class EmptyStates
    {
        /// <summary>Add Machine…: the Machine View with the browser open (set by the main window).</summary>
        public static Action AddMachine { get; set; }

        /// <summary>One starter, added to the song (one undo step) and selected; null when the machine isn't installed.</summary>
        public static IMachine AddStarter(ReBuzzCore core, string name)
        {
            var model = MachineBrowserModel.For(core, StudioHost.Current?.Registry);
            var entry = model.Find(name);
            if (entry == null) return null;
            int n = core.SongCore.Machines.Count;                     // a little apart from the ones before it
            var made = model.Add(entry, -0.35f + 0.1f * (n - 1), -0.2f + 0.1f * (n - 1));
            if (made != null) StudioSelection.For(core).SelectMachine(made, null);
            return made;
        }
    }

    internal sealed class EmptyState : Border
    {
        readonly TextBlock title = new TextBlock { FontSize = StudioTokens.FontTitle, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextPrimaryBrush, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        readonly TextBlock body = new TextBlock { FontSize = 12, Foreground = StudioTokens.TextSecondaryBrush, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        readonly WrapPanel steps = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        readonly List<Button> buttons = new List<Button>();
        readonly ReBuzzCore core;
        string shownKey;

        public EmptyState(ReBuzzCore core)
        {
            this.core = core;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            MaxWidth = 460;
            Padding = new Thickness(16);
            IsVisible = false;
            Child = new StackPanel { Spacing = 8, Children = { title, body, steps } };
            Avalonia.Automation.AutomationProperties.SetName(this, "Empty view");
        }

        public string Title => title.Text;

        public string Text => body.Text;

        /// <summary>The next steps now shown, in order (tests, the gallery).</summary>
        public IReadOnlyList<Button> Buttons => buttons;

        /// <summary>The step with this label, or null.</summary>
        public Button Step(string label) => buttons.FirstOrDefault(b => (b.Content as string) == label);

        /// <summary>Shows or hides it; with <paramref name="show"/> the content is set only when it changed (the views call this on a timer).</summary>
        public void Update(bool show, string key, string titleText, string text, IEnumerable<(string Label, Action Do)> next, bool starters)
        {
            if (IsVisible != show) IsVisible = show;
            if (!show) { shownKey = null; return; }
            var list = next?.ToList() ?? new List<(string Label, Action Do)>();
            key += "|" + list.Count;
            if (key == shownKey) return;
            shownKey = key;
            title.Text = titleText;
            body.Text = text;
            steps.Children.Clear();
            buttons.Clear();
            if (starters)
                foreach (var name in MachineBrowserModel.StarterNames)
                {
                    string starter = name;
                    Add(starter, () => EmptyStates.AddStarter(core, starter), "Start with " + starter);
                }
            foreach (var (label, act) in list) Add(label, act, label);
        }

        void Add(string label, Action act, string automationName)
        {
            var b = new Button { Content = label, Margin = new Thickness(4, 4, 4, 0), Tag = label };
            Avalonia.Automation.AutomationProperties.SetName(b, automationName);
            b.Click += (s, e) => act();
            steps.Children.Add(b);
            buttons.Add(b);
        }

        /// <summary>The song has only its Master (the starters are the next step).</summary>
        public static bool SongIsEmpty(ReBuzzCore core) => core.SongCore.Machines.Count <= 1;

        /// <summary>The step that adds a machine, when the window can open its browser.</summary>
        public static IEnumerable<(string, Action)> AddMachineStep() =>
            EmptyStates.AddMachine == null ? Enumerable.Empty<(string, Action)>() : new (string, Action)[] { ("Add machine…", () => EmptyStates.AddMachine?.Invoke()) };
    }
}
