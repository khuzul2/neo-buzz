// Fork (docs/specs/studio-mvp.md S7): the studio's Preferences (Command+, on a Mac): ReBuzz.exe's
// Preferences window and its Settings window in one, a page each: Audio, MIDI, Sync, Agent (S8)
// and General. One window at a time; choices apply and are saved as they are made, as macOS
// settings windows do (no OK or Cancel).

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class PreferencesWindow : Window
    {
        readonly TabControl tabs = new TabControl { Padding = new Thickness(0, 6, 0, 0) };
        readonly List<PrefsPage> pages = new List<PrefsPage>();

        /// <summary>The window while it is open.</summary>
        public static PreferencesWindow Current { get; private set; }

        public const string AudioPageName = "Audio", MidiPageName = "MIDI", SyncPageName = "Sync", AgentPageName = "Agent", GeneralPageName = "General";

        PreferencesWindow(StudioHost host)
        {
            Title = "Preferences";
            Width = 760;
            Height = 600;
            MinWidth = 560;
            MinHeight = 360;
            ShowInTaskbar = false;
            var core = host.Core;
            pages.Add(new AudioPage(core, host.Registry));
            pages.Add(new MidiPage(core.MidiInOutEngine, host.Registry));
            if (core.SessionSync != null) pages.Add(new SyncPage(core.SessionSync));
            if (host.Agent != null) pages.Add(new AgentPage(host.Agent));
            pages.Add(new GeneralPage());
            foreach (var page in pages) tabs.Items.Add(new TabItem { Header = new TextBlock { Text = page.Title, FontSize = 17 }, Content = page, MinHeight = 36 });
            tabs.SelectionChanged += (s, e) =>
            {
                if (e.Source == tabs) Selected?.Show();
            };
            Content = tabs;

            var cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new Command(Close) });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.W, cmd), Command = new Command(Close) });
        }

        public IReadOnlyList<PrefsPage> Pages => pages;

        public PrefsPage Selected => (tabs.SelectedItem as TabItem)?.Content as PrefsPage;

        public T Page<T>() where T : PrefsPage => pages.OfType<T>().FirstOrDefault();

        /// <summary>Shows a page (by its title), the window opening first if it isn't.</summary>
        public void ShowPage(string title)
        {
            int index = pages.FindIndex(p => p.Title == title);
            if (index < 0) index = Math.Max(0, tabs.SelectedIndex);
            if (tabs.SelectedIndex != index) tabs.SelectedIndex = index;
            else Selected?.Show();
        }

        /// <summary>Opens Preferences (or brings it forward) at a page; null keeps the page shown last.</summary>
        public static PreferencesWindow Open(StudioHost host, string page = null)
        {
            if (host?.Core == null) return null;
            if (Current == null)
            {
                Current = new PreferencesWindow(host);
                Current.Closed += (s, e) =>
                {
                    foreach (var p in Current.pages) p.Closed();
                    lastPage = Current.Selected?.Title;
                    Current = null;
                };
                var owner = Dialogs.Owner;
                Current.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
                Current.ShowPage(page ?? lastPage ?? AudioPageName);
                if (owner != null && owner != Current) Current.Show(owner);
                else Current.Show();
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
