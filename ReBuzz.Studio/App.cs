// Fork (docs/specs/studio-mvp.md, P3): the studio application: one dark theme (Fluent), the main
// window, and the engine started once the window shows; then (S7) an autosave left over is offered
// back and autosaving starts. On a Mac the app menu has About and Preferences (S7).

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio
{
    public sealed class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Dark;
            Name = Product.Name;     // the macOS app menu: About, Hide and Quit NeoBuzz Live
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var host = StudioHost.Current;
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && host != null)
            {
                var window = new MainWindow(host);
                desktop.MainWindow = window;
                if (OperatingSystem.IsMacOS())
                    NativeMenu.SetMenu(this, AppMenu.ToNativeMenu(window.AppMenuModel()));
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
