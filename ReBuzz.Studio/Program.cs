// Fork (docs/specs/studio-mvp.md, P3): the studio's entry point.

using System;
using Avalonia;

namespace ReBuzz.Studio
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        /// <summary>Also what the headless tests start (ReBuzz.Studio.Tests).</summary>
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
    }
}
