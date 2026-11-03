// Fork (docs/specs/studio-mvp.md, P3): the studio's entry point.
//
//   ReBuzz.Studio [song] [--settings <folder>] [--no-audio] [--smoke]
//
// --smoke starts the engine, shows the window, and quits with 0 once it has drawn (CI's check
// that the app runs on macOS).

using System;
using System.IO;
using System.Linq;
using Avalonia;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio
{
    internal static class Program
    {
        /// <summary>A song to open once the engine runs.</summary>
        public static string Song { get; private set; }

        public static bool Smoke { get; private set; }

        [STAThread]
        public static int Main(string[] args)
        {
            static string Option(string[] a, string name)
            {
                int i = Array.FindIndex(a, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
            }
            Smoke = args.Contains("--smoke");
            string settings = Option(args, "--settings");
            for (int i = 0; i < args.Length && Song == null; i++)
            {
                if (string.Equals(args[i], "--settings", StringComparison.OrdinalIgnoreCase)) i++;
                else if (!args[i].StartsWith("-")) Song = Path.GetFullPath(args[i]);
            }
            StudioHost.Create(settings, openAudio: !args.Contains("--no-audio"));
            int exit = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            StudioHost.Current?.Shutdown();
            return exit;
        }

        /// <summary>Also what the headless tests start (ReBuzz.Studio.Tests).</summary>
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
    }
}
