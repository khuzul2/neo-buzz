// Fork (docs/specs/studio-mvp.md S7): a Preferences page: headed sections of labelled rows, a
// label on the left, the control on the right and a short hint under it, scrolled when long. A
// settings class's [BuzzSetting]s show generically, as ReBuzz.exe's Settings window shows them.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ReBuzz.Agent.App;

namespace ReBuzz.Studio.Shell.Preferences
{
    using Control = Avalonia.Controls.Control;

    internal abstract class PrefsPage : ScrollViewer
    {
        public static readonly IBrush Dim = ReBuzz.Studio.Shell.Design.StudioTokens.TextSecondaryBrush;
        public static readonly IBrush Problem = ReBuzz.Studio.Shell.Design.StudioTokens.Brush(ReBuzz.Studio.Shell.Design.StudioTokens.Warning);

        protected readonly StackPanel Body = new StackPanel { Margin = new Thickness(24, 16, 24, 24), Spacing = 8 };
        readonly List<Action> settingRefreshes = new List<Action>();
        readonly List<Action> relabels = new List<Action>();
        readonly Dictionary<string, Control> settingControls = new Dictionary<string, Control>();

        protected PrefsPage(PrefsModel model = null)
        {
            Model = model;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            AllowAutoHide = false;          // a bar that shows there's more (macOS's and Fluent's hide until scrolled)
            Content = Body;
        }

        // Fork (UX review): a ScrollViewer's look and template, which a subclass doesn't get by itself (Avalonia finds a
        // control's theme by its own type). Without it a page didn't scroll at all: the Audio and General pages ran past
        // the window, their last settings cut off, with nothing to show there was more.
        protected override Type StyleKeyOverride => typeof(ScrollViewer);

        /// <summary>The page's tab.</summary>
        public abstract string Title { get; }

        /// <summary>Shows the settings and the state as they are now.</summary>
        public virtual void Refresh() { }

        /// <summary>The window shows this page (again): fresh state.</summary>
        public void Show()
        {
            Filling = true;
            try
            {
                foreach (var refresh in settingRefreshes) refresh();
            }
            finally
            {
                Filling = false;
            }
            OnShown();
        }

        protected virtual void OnShown() => Refresh();

        /// <summary>
        /// Fork (ui-gallery.md): while set, a page leaves out what is slow to ask for (PortAudio's devices) when it is
        /// shown: the window shows the page that way first, draws, and shows it again whole.
        /// </summary>
        public bool LeaveOutSlowState { get; set; }

        /// <summary>The window closes.</summary>
        public virtual void Closed() { }

        DispatcherTimer poll;

        /// <summary>Refreshes the page on a timer while it shows (devices plugged in, Link's peers).</summary>
        protected void PollWhileShown(TimeSpan interval)
        {
            poll = new DispatcherTimer { Interval = interval };
            poll.Tick += (s, e) => Refresh();
        }

        // A tab that isn't selected, or a closed window, leaves the visual tree: its timer stops.
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            poll?.Start();
            BuzzGUI.Common.NoteNames.Changed += Relabel;
        }

        /// <summary>Fork (ux-overhaul.md §3.5): notes are named another way: the choices that name octaves say it.</summary>
        void Relabel() => Dispatcher.UIThread.Post(() =>
        {
            foreach (var relabel in relabels) relabel();
        });

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            BuzzGUI.Common.NoteNames.Changed -= Relabel;
            poll?.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>True while the page sets its controls from the settings.</summary>
        protected bool Filling { get; set; }

        protected TextBlock Heading(string text)
        {
            var t = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, FontSize = Design.StudioTokens.FontTitle, Margin = new Thickness(0, Body.Children.Count == 0 ? 0 : 16, 0, 4) };
            Body.Children.Add(t);
            return t;
        }

        /// <summary>A line of text under a heading.</summary>
        protected TextBlock Note(string text, IBrush brush = null)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = brush ?? Dim, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
            Body.Children.Add(t);
            return t;
        }

        /// <summary>A labelled row; the hint goes under the control.</summary>
        protected Grid Row(string label, Control control, string hint = null, Control extra = null)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), Margin = new Thickness(0, 4) };
            var name = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 12, 0), TextWrapping = TextWrapping.Wrap };
            grid.Children.Add(name);
            var right = new StackPanel { Spacing = 4 };
            if (control is not TextBox || !double.IsNaN(control.Width)) control.HorizontalAlignment = HorizontalAlignment.Left;
            right.Children.Add(control);
            if (!string.IsNullOrEmpty(hint)) right.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 12 });
            if (extra != null) right.Children.Add(extra);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            Body.Children.Add(grid);
            return grid;
        }

        protected static ComboBox Choices(double width = 320) => new ComboBox { MinWidth = width, MaxWidth = 460 };

        /// <summary>Runs <paramref name="apply"/> when the user picks an item (not while the page fills it).</summary>
        protected void OnPick(ComboBox box, Action<int> apply)
        {
            box.SelectionChanged += (s, e) =>
            {
                if (!Filling && box.SelectedIndex >= 0) apply(box.SelectedIndex);
            };
        }

        // ------------------------------------------------------------------ the settings (the agent's list)

        /// <summary>The settings model this page shows (the K3 agent tools' list and keys); null when the agent host isn't there.</summary>
        protected PrefsModel Model { get; }

        readonly List<string> sections = new List<string>();

        /// <summary>The settings sections the page shows (settings_get's "section"), what Restore Defaults puts back.</summary>
        internal IReadOnlyList<string> Sections => sections;

        /// <summary>Restore Defaults asks first (the engine flags and the safety settings).</summary>
        public virtual bool ConfirmRestore => false;

        public virtual string RestoreWarning => "";

        /// <summary>The page's own part of Restore Defaults, besides its sections (a device's choice that isn't a setting key).</summary>
        public virtual void RestoreOwn() { }

        /// <summary>Shows what the settings hold now (the agent changed one while the window was open, or Restore Defaults did).</summary>
        public void RefreshSettings()
        {
            Filling = true;
            try
            {
                foreach (var refresh in settingRefreshes) refresh();
            }
            finally
            {
                Filling = false;
            }
            Refresh();
        }

        /// <summary>
        /// A section's settings, a row each, from the same entries and keys as settings_get: a choice of the setting's options (the default in
        /// bold), on/off as a check box, a number or text in a box that takes only what settings_set would. A change goes through
        /// <see cref="SettingsService"/>: it is saved and logged in settings_history with the source Manual. A setting that needs a restart says so.
        /// </summary>
        protected void AddSection(string section, params string[] hidden)
        {
            sections.Add(section);
            if (Model == null)
            {
                Note("Settings aren't available here: the agent host didn't start.", Problem);
                return;
            }
            foreach (var entry in Model.Section(section).Where(e => e.UseInstead == null && !hidden.Contains(e.Name)))
                AddEntry(entry);
        }

        /// <summary>One setting's row (the key names its control for tests).</summary>
        protected void AddEntry(SettingEntry entry)
        {
            string hint = entry.Description + (entry.RestartRequired && !entry.Description.Contains("restart", StringComparison.OrdinalIgnoreCase) ? " Restart required." : "");
            var problem = new TextBlock { Foreground = Problem, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            void Set(string value)
            {
                if (Filling || value == null) return;
                try
                {
                    problem.IsVisible = false;
                    Model.Apply(entry, value);
                }
                catch (Exception e)
                {
                    problem.Text = e.Message;
                    problem.IsVisible = true;
                    StudioHost.Current?.Log($"{entry.Key}: {e.Message}");
                    RefreshSettings();
                }
            }
            string name = Pretty(entry.Name);
            Control control;
            if (entry.Kind == SettingKind.Bool)
            {
                var box = new CheckBox { Content = name };
                box.IsCheckedChanged += (o, e) => Set(box.IsChecked == true ? "True" : "False");
                settingRefreshes.Add(() => box.IsChecked = SettingsService.Get(entry).Equals("True", StringComparison.OrdinalIgnoreCase));
                Avalonia.Automation.AutomationProperties.SetName(box, name);
                Row("", box, hint, problem);
                control = box;
            }
            else if (entry.Options is { Length: > 0 } options)
            {
                var combo = Choices(240);
                var items = options.Select((o, i) => new ComboBoxItem { Content = OptionLabel(entry, i), FontWeight = o == entry.Default ? FontWeight.Bold : FontWeight.Normal }).ToList();
                combo.ItemsSource = items;
                relabels.Add(() =>
                {
                    for (int i = 0; i < items.Count; i++) items[i].Content = OptionLabel(entry, i);
                });
                OnPick(combo, i => Set(options[i]));
                settingRefreshes.Add(() => combo.SelectedIndex = Array.FindIndex(options, o => o.Equals(SettingsService.Get(entry), StringComparison.OrdinalIgnoreCase)));
                Avalonia.Automation.AutomationProperties.SetName(combo, name);
                Row(name, combo, hint, problem);
                control = combo;
            }
            else
            {
                var text = new TextBox { Width = 240 };
                text.LostFocus += (o, e) => Set(text.Text);
                text.KeyDown += (o, e) =>
                {
                    if (e.Key == Key.Enter) Set(text.Text);
                };
                settingRefreshes.Add(() => text.Text = SettingsService.Get(entry));
                Avalonia.Automation.AutomationProperties.SetName(text, name);
                Row(name, text, hint, problem);
                control = text;
            }
            settingControls[entry.Key] = control;
        }

        /// <summary>
        /// An option as the list shows it: an enum value's [Description] (General ▸ Note names: "C3 = MIDI 60", ux-overhaul.md §3.5), an octave as
        /// notes are named (the default base octave: Buzz's 4 is 2 when C3 is 60), else its name with spaces for underscores.
        /// </summary>
        internal static string OptionLabel(SettingEntry entry, int index)
        {
            string option = entry.Options[index];
            if (entry.Name == nameof(BuzzGUI.Common.Settings.GeneralSettings.DefaultMachineBaseOctave) && int.TryParse(option, out int octave))
                return BuzzGUI.Common.NoteNames.Octave(octave).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return entry.OptionLabels != null && index < entry.OptionLabels.Length ? entry.OptionLabels[index] : option.Replace('_', ' ');
        }

        /// <summary>A setting's control by its key as settings_get lists it ("advanced.AccurateBPM"), for tests.</summary>
        internal Control SettingControl(string key) => settingControls.TryGetValue(key, out var c) ? c : null;

        /// <summary>"LowLatencyGC" → "Low latency GC": words in sentence case, acronyms kept.</summary>
        internal static string Pretty(string name)
        {
            var words = Regex.Split(name, "(?<=[a-z])(?=[A-Z0-9])|(?<=[A-Z])(?=[A-Z][a-z])");
            return string.Join(" ", words.Select((w, i) => i == 0 || w.Length > 1 && w.All(char.IsUpper) ? w : w.ToLowerInvariant()));
        }
    }
}
