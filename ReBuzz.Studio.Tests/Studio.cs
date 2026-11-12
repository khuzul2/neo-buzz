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

        /// <summary>
        /// The browser and the detail panel closed again (P3a): their open state is a setting, kept across windows, and a test
        /// that opened one would start the next test's window with it open.
        /// </summary>
        public static void ResetPanels()
        {
            if (host?.Registry is not { } registry) return;
            registry.Write("DetailOpen", false, "Studio");
            registry.Write("BrowserOpen", false, "Studio");
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

        /// <summary>
        /// Runs <paramref name="putBack"/> when the test ends, passed or failed: <c>using var restore = Studio.AfterTest(PutBack);</c>
        /// as the test's first statement, where a fixture would have had a [TearDown] put back the global state its tests
        /// change (pickers, responders, hooks, the transport, the safety settings). Avalonia.Headless.NUnit (12.1.4) runs
        /// an [AvaloniaTest]'s TearDown only after the test method returns, so a failed test skipped it, and the next tests
        /// started from what it left (AudioHygieneTests' Looping in ExitTests, PR #94). It runs after the test's own
        /// finally blocks, as the TearDown did.
        /// </summary>
        public static System.IDisposable AfterTest(System.Action putBack) => new PutBack(putBack);

        sealed class PutBack(System.Action action) : System.IDisposable
        {
            bool done;

            public void Dispose()
            {
                if (done) return;
                done = true;
                action();
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
