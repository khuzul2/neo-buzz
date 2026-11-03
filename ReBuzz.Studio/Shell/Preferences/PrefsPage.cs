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

namespace ReBuzz.Studio.Shell.Preferences
{
    using Control = Avalonia.Controls.Control;

    internal abstract class PrefsPage : ScrollViewer
    {
        public static readonly IBrush Dim = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0x9a));
        public static readonly IBrush Problem = new SolidColorBrush(Color.FromRgb(0xe0, 0x8a, 0x6a));

        protected readonly StackPanel Body = new StackPanel { Margin = new Thickness(24, 12, 24, 20), Spacing = 6 };
        readonly List<Action> settingRefreshes = new List<Action>();
        readonly Dictionary<string, Control> settingControls = new Dictionary<string, Control>();

        protected PrefsPage()
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Content = Body;
        }

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
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            poll?.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>True while the page sets its controls from the settings.</summary>
        protected bool Filling { get; set; }

        protected TextBlock Heading(string text)
        {
            var t = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 14, Margin = new Thickness(0, Body.Children.Count == 0 ? 0 : 14, 0, 2) };
            Body.Children.Add(t);
            return t;
        }

        /// <summary>A line of text under a heading.</summary>
        protected TextBlock Note(string text, IBrush brush = null)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = brush ?? Dim, FontSize = 12, Margin = new Thickness(0, 0, 0, 2) };
            Body.Children.Add(t);
            return t;
        }

        /// <summary>A labelled row; the hint goes under the control.</summary>
        protected Grid Row(string label, Control control, string hint = null)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), Margin = new Thickness(0, 1) };
            var name = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 12, 0), TextWrapping = TextWrapping.Wrap };
            grid.Children.Add(name);
            var right = new StackPanel { Spacing = 2 };
            if (control is not TextBox || !double.IsNaN(control.Width)) control.HorizontalAlignment = HorizontalAlignment.Left;
            right.Children.Add(control);
            if (!string.IsNullOrEmpty(hint)) right.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 11.5 });
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

        // ------------------------------------------------------------------ [BuzzSetting]s

        /// <summary>
        /// A settings class's [BuzzSetting]s, a row each, as ReBuzz.exe's Settings window lists them:
        /// a choice of the setting's options (the default in bold), on/off as a check box, text in
        /// a box. A change goes to the setting at once (the engine follows its PropertyChanged) and
        /// is saved, as the Settings window saves them.
        /// </summary>
        protected void AddSettings(BuzzGUI.Common.Settings.Settings settings, params string[] hidden)
        {
            foreach (var setting in settings.List.Where(s => !hidden.Contains(s.Name)))
            {
                var s = setting;
                string key = settings.GetType().Name + "/" + s.Name;
                void Set(string value)
                {
                    if (Filling || value == null || value == s.Value) return;
                    try
                    {
                        s.Value = value;
                        settings.Save();
                    }
                    catch (Exception e)
                    {
                        StudioHost.Current?.Log($"{key}: {e.Message}");
                    }
                }

                if (s.Presets == null && s.Options != null && s.Options.Length == 2 && s.Options.Contains("True") && s.Options.Contains("False"))
                {
                    var box = new CheckBox { Content = Pretty(s.Name) };
                    box.IsCheckedChanged += (o, e) => Set(box.IsChecked == true ? "True" : "False");
                    settingRefreshes.Add(() => box.IsChecked = s.Value == "True");
                    Row("", box, s.Description);
                    settingControls[key] = box;
                }
                else if (s.Options != null)
                {
                    var combo = Choices(240);
                    combo.ItemsSource = s.Options.Select(o => new ComboBoxItem { Content = o.Replace('_', ' '), FontWeight = o == s.Default ? FontWeight.Bold : FontWeight.Normal }).ToList();
                    OnPick(combo, i => Set(s.Options[i]));
                    settingRefreshes.Add(() => combo.SelectedIndex = Array.IndexOf(s.Options, s.Value));
                    Row(Pretty(s.Name), combo, s.Description);
                    settingControls[key] = combo;
                }
                else
                {
                    var text = new TextBox { Width = 240 };
                    text.LostFocus += (o, e) => Set(text.Text);
                    text.KeyDown += (o, e) =>
                    {
                        if (e.Key == Key.Enter) Set(text.Text);
                    };
                    settingRefreshes.Add(() => text.Text = s.Value);
                    Row(Pretty(s.Name), text, s.Description);
                    settingControls[key] = text;
                }
            }
        }

        /// <summary>A setting's control by "Class/Name" ("EngineSettings/AccurateBPM"), for tests.</summary>
        internal Control SettingControl(string key) => settingControls.TryGetValue(key, out var c) ? c : null;

        /// <summary>"LowLatencyGC" → "Low latency GC": words in sentence case, acronyms kept.</summary>
        internal static string Pretty(string name)
        {
            var words = Regex.Split(name, "(?<=[a-z])(?=[A-Z0-9])|(?<=[A-Z])(?=[A-Z][a-z])");
            return string.Join(" ", words.Select((w, i) => i == 0 || w.Length > 1 && w.All(char.IsUpper) ? w : w.ToLowerInvariant()));
        }
    }
}
