// Fork (docs/specs/studio-mvp.md, P3): the studio application: one dark theme (Fluent), the main
// window, and the engine started once the window shows.

using System;
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
            Name = "ReBuzz";
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
                if (Program.Song != null) _ = SongFiles.Open(Program.Song);
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
                window.ShowStep("ReBuzz couldn't start: " + e.Message);
                if (Program.Smoke) desktop.Shutdown(1);
            }
        }
    }
}
