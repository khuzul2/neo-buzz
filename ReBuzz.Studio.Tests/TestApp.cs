// Fork (docs/specs/studio-mvp.md §3.7): the headless application the tests run in, with Skia
// drawing, so a window can be captured as a PNG.

using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(ReBuzz.Studio.Tests.TestApp))]
// One application and dispatcher for all tests, as there is one engine (Studio.Host). Per test (the
// default), the engine's threads could post into the gap between two tests' dispatchers, and
// Dispatcher.UIThread was then made on that thread: the next test failed to start.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace ReBuzz.Studio.Tests
{
    public static class TestApp
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<ReBuzz.Studio.App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
