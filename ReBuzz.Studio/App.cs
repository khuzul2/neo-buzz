// Fork (docs/specs/studio-mvp.md, P3): the studio application: one dark theme (Fluent).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace ReBuzz.Studio
{
    public sealed class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Dark;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new Window { Title = "ReBuzz", Width = 1200, Height = 800 };
            base.OnFrameworkInitializationCompleted();
        }
    }
}
