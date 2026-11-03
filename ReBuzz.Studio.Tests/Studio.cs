// Fork (docs/specs/studio-mvp.md §3.7): the engine the tests share (one per process, as its
// globals are), started headless in a settings folder of its own, and screenshots.

using System.IO;
using Avalonia.Controls;
using Avalonia.Headless;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    internal static class Studio
    {
        static StudioHost host;

        /// <summary>The started engine (call on the UI thread, inside an [AvaloniaTest]).</summary>
        public static StudioHost Host
        {
            get
            {
                if (host != null) return host;
                string settings = Path.Combine(Path.GetTempPath(), "rebuzz-studio-tests-" + System.Environment.ProcessId);
                host = StudioHost.Create(settings, openAudio: false);
                host.Start(_ => { });
                return host;
            }
        }

        /// <summary>Where screenshots go: SCREENSHOTS_DIR, else beside the tests.</summary>
        public static string ScreenshotFolder
        {
            get
            {
                string dir = System.Environment.GetEnvironmentVariable("SCREENSHOTS_DIR") ?? Path.Combine(System.AppContext.BaseDirectory, "screenshots");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>Draws the window and saves it as name.png; returns the path.</summary>
        public static string Screenshot(TopLevel window, string name)
        {
            var frame = window.CaptureRenderedFrame();
            Assert.That(frame, Is.Not.Null, "the window drew");
            string path = Path.Combine(ScreenshotFolder, name + ".png");
            frame!.Save(path);
            return path;
        }
    }
}
