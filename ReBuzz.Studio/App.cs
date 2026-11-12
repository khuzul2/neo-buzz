// Fork (docs/specs/studio-mvp.md, P3): the studio application: one dark theme (Fluent), the main
// window, and the engine started once the window shows; then (S7) an autosave left over is offered
// back and autosaving starts. On a Mac the app menu has About and Preferences (S7), and (ui-gallery.md) every window
// has the menu bar's menus, so they stay while Preferences or a dialog is the key window.

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio
{
    public sealed class App : Application
    {
        /// <summary>The app's default typeface: Inter, the design tokens' (ux-overhaul.md P2a), bundled.</summary>
        internal static FontManagerOptions FontOptions => new FontManagerOptions { DefaultFamilyName = Shell.Design.StudioTokens.FontFamilyName };

        /// <summary>The macOS menu bar (NativeMenu) rather than the windows' own menus; tests set it to check the Mac's.</summary>
        internal static bool MacMenus { get; set; } = OperatingSystem.IsMacOS();

        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Dark;
            // Fork (ux-overhaul.md §3.1): placeholders at the placeholder token, drawn whole: Fluent draws them at half opacity
            // over its own grey, 2.54:1 in Song Notes (gallery mac-014); the token is 4.6:1 or more on the views and panels.
            foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" })
                Resources[key] = Shell.Design.StudioTokens.TextPlaceholderBrush;
            Avalonia.Controls.Primitives.TemplatedControl.TemplateAppliedEvent.AddClassHandler<TextBox>((box, e) =>
            {
                // The template sets its opacity itself, which a style can't override: a local value can.
                if (e.NameScope.Find<Avalonia.Controls.Control>("PART_Placeholder") is { } placeholder) placeholder.Opacity = 1;
            }, RoutingStrategies.Direct);
            // Fork (ux-overhaul.md P2c): the windows' ground is the base surface, not Fluent's black.
            Styles.Add(new Avalonia.Styling.Style(x => x.OfType<Window>())
            {
                Setters = { new Avalonia.Styling.Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, Shell.Design.StudioTokens.SurfaceBaseBrush) },
            });
            Name = Product.Name;     // the macOS app menu: About, Hide and Quit NeoBuzz Live
            // Fork (ui-gallery.md): the app menu here, before Avalonia's exporter starts (it starts after Initialize and
            // makes its own "About Avalonia" menu when the application has none; a menu set later never reached it).
            if (MacMenus) NativeMenu.SetMenu(this, AppMenu.ToNativeMenu(MainWindow.AppMenuModel(StudioHost.Current)));
            Window.WindowOpenedEvent.AddClassHandler<Window>((w, e) =>
            {
                windows.Remove(w);
                windows.Add(w);
                GiveMenus(w);
            }, RoutingStrategies.Direct, handledEventsToo: true);
            Window.WindowClosedEvent.AddClassHandler<Window>((w, e) => windows.Remove(w), RoutingStrategies.Direct, handledEventsToo: true);
            // Fork (ux-overhaul.md §3.5): Preferences name notes another way: every window draws its notes again.
            if (!namingWatched)
            {
                namingWatched = true;
                BuzzGUI.Common.NoteNames.Changed += () => Dispatcher.UIThread.Post(RedrawAll);
                // Fork (ux-overhaul.md §3.3): Preferences ▸ Appearance names another theme: every window draws again.
                Shell.Design.StudioTheme.Changed += () => Dispatcher.UIThread.Post(RedrawAll);
            }
        }

        static bool namingWatched;

        /// <summary>Every open window's controls draw themselves again (the names they draw changed).</summary>
        internal static void RedrawAll()
        {
            foreach (var w in windows.ToList())
                foreach (var v in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w))
                    v.InvalidateVisual();
        }

        static readonly System.Collections.Generic.List<Window> windows = new System.Collections.Generic.List<Window>();

        /// <summary>The app's open windows, in the order they opened (the lifetime's list, which the headless tests lack).</summary>
        internal static System.Collections.Generic.IReadOnlyList<Window> Windows => windows;

        /// <summary>
        /// A window without menus of its own (Preferences, About, dialogs, machine windows) gets the main window's: macOS
        /// shows the key window's menus, and a window without any showed the app menu alone.
        /// </summary>
        internal static void GiveMenus(Window window)
        {
            if (!MacMenus || window is MainWindow || NativeMenu.GetMenu(window) != null) return;
            var main = window.Owner as MainWindow ?? MainWindow.OpenWindows.LastOrDefault();
            if (main != null) NativeMenu.SetMenu(window, AppMenu.ToNativeMenu(main.MenuModel(mac: true)));
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var host = StudioHost.Current;
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && host != null)
            {
                var window = new MainWindow(host);
                desktop.MainWindow = window;
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                window.Opened += (s, e) => Dispatcher.UIThread.Post(() => StartEngine(host, window, desktop), DispatcherPriority.Background);
            }
            base.OnFrameworkInitializationCompleted();
        }

        static void StartEngine(StudioHost host, MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                host.Start(step =>
                {
                    host.Log($"{clock.Elapsed.TotalSeconds:F1} s: {step}");
                    window.ShowStep(step);
                });
                _ = Shell.Design.StudioTheme.Settings;      // the saved theme (Preferences ▸ Appearance), once the settings store is there
                _ = AfterStart(host);
                if (Program.Smoke)
                {
                    // Quit once the window has drawn with the views in it.
                    Dispatcher.UIThread.Post(() =>
                    {
                        host.Log($"smoke: ok ({window.Tabs.ItemCount} views, {host.Core.MachineDLLs.Count} machines installed)");
                        desktop.Shutdown(0);
                    }, DispatcherPriority.ApplicationIdle);
                }
            }
            catch (Exception e)
            {
                host.Log("start-up failed: " + e);
                window.ShowStep(Product.Name + " couldn't start: " + e.Message);
                if (Program.Smoke) desktop.Shutdown(1);
            }
        }

        /// <summary>
        /// As ReBuzz.exe after its start-up: an autosave left by a session that didn't close is
        /// offered back, then the song from the command line opens, and autosaving starts.
        /// </summary>
        static async Task AfterStart(StudioHost host)
        {
            if (!Program.Smoke) await Autosave.OfferRecovery(host.Core);
            if (Program.Song != null) await SongFiles.Open(Program.Song);
            Autosave.Start(host.Core);
        }
    }
}
