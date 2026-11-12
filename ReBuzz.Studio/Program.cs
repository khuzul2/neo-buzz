// Fork (docs/specs/studio-mvp.md, P3): the studio's entry point.
//
//   ReBuzz.Studio [song] [--settings <folder>] [--no-audio] [--smoke] [--agent-port <port> [--agent-token <token>]]
//
// --smoke starts the engine, shows the window, and quits with 0 once it has drawn (CI's check
// that the app runs on macOS). --agent-port allows agents on that port for this run, with the
// token given (else the saved one), without changing the saved settings (S9: CI's live tests).

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
            string[] valued = { "--settings", "--agent-port", "--agent-token" };
            for (int i = 0; i < args.Length && Song == null; i++)
            {
                if (valued.Any(v => string.Equals(args[i], v, StringComparison.OrdinalIgnoreCase))) i++;
                else if (!args[i].StartsWith("-")) Song = Path.GetFullPath(args[i]);
            }
            if (Option(args, "--agent-port") is string agentPort)
            {
                if (!int.TryParse(agentPort, out int port) || port <= 0 || port > 65535)
                {
                    Console.Error.WriteLine($"--agent-port: {agentPort} isn't a port (1-65535)");
                    return 2;
                }
                AgentServer.StartupOption = (port, Option(args, "--agent-token"));
            }
            StudioHost.Create(settings, openAudio: !args.Contains("--no-audio"));
            int exit = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            StudioHost.Current?.Shutdown();
            return exit;
        }

        /// <summary>
        /// Also what the headless tests start (ReBuzz.Studio.Tests). The macOS app menu is the studio's own, whole
        /// (MainWindow.AppMenuModel): Avalonia's standard items are left out (ui-gallery.md).
        /// </summary>
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace()
                .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true })
                // Fork (ux-overhaul.md P2a): Inter everywhere, the design tokens' typeface (bundled: Avalonia.Fonts.Inter).
                .With(App.FontOptions);
    }
}
