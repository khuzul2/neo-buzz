// Fork (docs/specs/studio-mvp.md S7, ux-overhaul.md P3f): the studio's Preferences (Command+, on a Mac): ReBuzz.exe's
// Preferences window and its Settings window in one, a page each: General, Audio, MIDI (and Sync), Appearance, Agent, Safety and
// Advanced (the engine flags). One window at a time; choices apply and are saved as they are made, as macOS settings windows do
// (no OK or Cancel), with Restore Defaults for the page shown. The settings are the agent's (settings_get's list and keys): a change
// here is logged in settings_history as Manual, one the agent makes shows here at once, and a setting that needs a restart says so.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Layout;
using ReBuzz.Agent.App;
using ReBuzz.Core;
using Avalonia.Threading;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class PreferencesWindow : Window
    {
        readonly TabControl tabs = new TabControl { Padding = new Thickness(0, 6, 0, 0), TabStripPlacement = Dock.Left };
        readonly TextBlock restartNote = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = Design.StudioTokens.FontBody, IsVisible = false };
        readonly Button restore = new Button { Content = "Restore Defaults", MinHeight = 28, Padding = new Thickness(12, 3) };
        readonly PrefsModel model;
        readonly Dictionary<string, string> restartFrom = new Dictionary<string, string>();
        readonly List<PrefsPage> pages = new List<PrefsPage>();

        /// <summary>The window while it is open.</summary>
        public static PreferencesWindow Current { get; private set; }

        public const string AudioPageName = "Audio", MidiPageName = "MIDI", SyncPageName = "Sync", AgentPageName = "Agent", GeneralPageName = "General", AppearancePageName = "Appearance", SafetyPageName = "Safety", AdvancedPageName = "Advanced";

        PreferencesWindow(StudioHost host)
        {
            Title = "Preferences";
            Width = 880;
            Height = 640;
            MinWidth = 640;
            MinHeight = 360;
            ShowInTaskbar = false;
            WindowBounds.KeepOnScreen(this);
            var core = host.Core;
            model = host.Agent != null ? new PrefsModel(host.Agent.Host.AppControl) : null;
            pages.Add(new GeneralPage(model));
            pages.Add(new AudioPage(core, host.Registry, model));
            pages.Add(new MidiPage(core.MidiInOutEngine, host.Registry, model));
            if (core.SessionSync != null) pages.Add(new SyncPage(core.SessionSync, model));
            pages.Add(new AppearancePage(model));
            if (host.Agent != null) pages.Add(new AgentPage(host.Agent, model));
            pages.Add(new SafetyPage(core, model));
            pages.Add(new AdvancedPage(model));
            foreach (var page in pages)
            {
                var item = new TabItem { Header = new TextBlock { Text = page.Title, FontSize = Design.StudioTokens.FontTitle }, Content = page, MinHeight = 36 };
                Avalonia.Automation.AutomationProperties.SetName(item, page.Title);
                tabs.Items.Add(item);
            }
            tabs.SelectionChanged += (s, e) =>
            {
                if (e.Source == tabs && !filling) Selected?.Show();
            };
            Avalonia.Automation.AutomationProperties.SetName(restore, "Restore Defaults");
            ToolTip.SetTip(restore, "Puts this page's settings back to their defaults");
            restore.Click += (s, e) => _ = RestoreDefaults();
            var footer = new DockPanel { Margin = new Thickness(16, 8), LastChildFill = true };
            DockPanel.SetDock(restore, Dock.Right);
            footer.Children.Add(restore);
            footer.Children.Add(restartNote);
            var root = new DockPanel();
            var rule = new Border { BorderBrush = Design.StudioTokens.SurfaceBorderBrush, BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
            DockPanel.SetDock(rule, Dock.Bottom);
            root.Children.Add(rule);
            root.Children.Add(tabs);
            Content = root;

            var cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new Command(Close) });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.W, cmd), Command = new Command(Close) });
        }

        public IReadOnlyList<PrefsPage> Pages => pages;

        public PrefsModel Model => model;

        public Button RestoreButton => restore;

        /// <summary>The line under the pages naming the settings that wait for a restart; empty when none does.</summary>
        public string RestartText => restartNote.IsVisible ? restartNote.Text : "";

        /// <summary>
        /// Puts the page shown back to its defaults (the engine flags and the safety settings ask first): its sections through the same reset the
        /// agent's settings_reset uses, logged as Manual, and the page's own controls.
        /// </summary>
        public async Task RestoreDefaults()
        {
            var page = Selected;
            if (page == null) return;
            if (page.ConfirmRestore)
            {
                var answer = await Dialogs.Ask(page.RestoreWarning, "Restore Defaults", UserButtons.YesNo, DialogChoices.Restore);
                if (answer != UserAnswer.Yes) return;
            }
            if (model != null) foreach (var section in page.Sections) model.Restore(section);
            page.RestoreOwn();
            page.RefreshSettings();
        }

        void OnSettingChanged(SettingChange change)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Current != this) return;
                if (change.RestartRequired)
                {
                    if (!restartFrom.TryGetValue(change.Key, out string first)) restartFrom[change.Key] = first = change.From;
                    if (string.Equals(first, change.To, StringComparison.OrdinalIgnoreCase)) restartFrom.Remove(change.Key);
                    var keys = restartFrom.Keys.Select(k => k.Substring(k.IndexOf('.') + 1)).Select(PrefsPage.Pretty).ToList();
                    restartNote.IsVisible = keys.Count > 0;
                    restartNote.Text = "Restart " + Product.Name + " for these to take effect: " + string.Join(", ", keys) + ".";
                    restartNote.Foreground = PrefsPage.Problem;
                }
                // What the agent changed shows at once; the window's own change is already on its page.
                if (change.Source != SettingsService.ManualSource) foreach (var p in pages) p.RefreshSettings();
            });
        }

        public PrefsPage Selected => (tabs.SelectedItem as TabItem)?.Content as PrefsPage;

        public T Page<T>() where T : PrefsPage => pages.OfType<T>().FirstOrDefault();

        /// <summary>Shows a page (by its title), the window opening first if it isn't.</summary>
        public void ShowPage(string title)
        {
            int index = IndexOf(title);
            if (tabs.SelectedIndex != index) tabs.SelectedIndex = index;
            else Selected?.Show();
        }

        int IndexOf(string title)
        {
            int index = pages.FindIndex(p => p.Title == title);
            return index < 0 ? Math.Max(0, tabs.SelectedIndex) : index;
        }

        /// <summary>Picks a page's tab before the window shows, with its state but what is slow to ask for (filled once it has drawn).</summary>
        void PickPage(string title)
        {
            filling = true;
            try
            {
                tabs.SelectedIndex = IndexOf(title);
            }
            finally
            {
                filling = false;
            }
            var page = Selected;
            if (page == null) return;
            page.LeaveOutSlowState = true;
            try
            {
                page.Show();
            }
            finally
            {
                page.LeaveOutSlowState = false;
            }
        }

        bool filling;

        /// <summary>Opens Preferences (or brings it forward) at a page; null keeps the page shown last.</summary>
        public static PreferencesWindow Open(StudioHost host, string page = null)
        {
            if (host?.Core == null) return null;
            if (Current == null)
            {
                var clock = Stopwatch.StartNew();
                var window = Current = new PreferencesWindow(host);
                SettingsService.Changed += window.OnSettingChanged;
                window.Closed += (s, e) =>
                {
                    SettingsService.Changed -= window.OnSettingChanged;
                    foreach (var p in window.pages) p.Closed();
                    lastPage = window.Selected?.Title;
                    if (Current == window) Current = null;
                };
                var owner = Dialogs.Owner;
                window.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
                // Fork (ui-gallery.md): the page's slow state (PortAudio's devices) once the window has drawn, so it never
                // shows blank while they're asked for (2 s after ⌘, it was still blank on a Mac).
                window.PickPage(page ?? lastPage ?? AudioPageName);
                long made = clock.ElapsedMilliseconds;
                window.Opened += (s, e) => window.RequestAnimationFrame(_ =>
                    host.Log($"Preferences: made in {made} ms, drawn at {clock.ElapsedMilliseconds} ms"));
                if (owner != null && owner != window) window.Show(owner);
                else window.Show();
                Dispatcher.UIThread.Post(() =>
                {
                    if (Current != window) return;
                    long start = clock.ElapsedMilliseconds;
                    window.Selected?.Show();
                    host.Log($"Preferences: {window.Selected?.Title} filled at {clock.ElapsedMilliseconds} ms ({clock.ElapsedMilliseconds - start} ms)");
                }, DispatcherPriority.Background);
            }
            else
            {
                if (page != null) Current.ShowPage(page);
                Current.Activate();
            }
            return Current;
        }

        static string lastPage;
    }
}
