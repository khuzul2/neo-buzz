// Fork (docs/specs/studio-mvp.md S7): the studio's Preferences (Command+, on a Mac): ReBuzz.exe's
// Preferences window and its Settings window in one, a page each: Audio, MIDI, Sync, Agent (S8)
// and General. One window at a time; choices apply and are saved as they are made, as macOS
// settings windows do (no OK or Cancel).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class PreferencesWindow : Window
    {
        readonly TabControl tabs = new TabControl { Padding = new Thickness(0, 6, 0, 0) };
        readonly List<PrefsPage> pages = new List<PrefsPage>();

        /// <summary>The window while it is open.</summary>
        public static PreferencesWindow Current { get; private set; }

        public const string AudioPageName = "Audio", MidiPageName = "MIDI", SyncPageName = "Sync", AgentPageName = "Agent", GeneralPageName = "General", AppearancePageName = "Appearance";

        PreferencesWindow(StudioHost host)
        {
            Title = "Preferences";
            Width = 760;
            Height = 600;
            MinWidth = 560;
            MinHeight = 360;
            ShowInTaskbar = false;
            WindowBounds.KeepOnScreen(this);
            var core = host.Core;
            pages.Add(new AudioPage(core, host.Registry));
            pages.Add(new MidiPage(core.MidiInOutEngine, host.Registry));
            if (core.SessionSync != null) pages.Add(new SyncPage(core.SessionSync));
            if (host.Agent != null) pages.Add(new AgentPage(host.Agent));
            pages.Add(new GeneralPage());
            pages.Add(new AppearancePage());
            foreach (var page in pages) tabs.Items.Add(new TabItem { Header = new TextBlock { Text = page.Title, FontSize = Design.StudioTokens.FontTitle }, Content = page, MinHeight = 36 });
            tabs.SelectionChanged += (s, e) =>
            {
                if (e.Source == tabs && !filling) Selected?.Show();
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
                window.Closed += (s, e) =>
                {
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
