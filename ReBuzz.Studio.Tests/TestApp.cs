// Fork (docs/specs/studio-mvp.md §3.7): the headless application the tests run in, with Skia
// drawing, so a window can be captured as a PNG.

using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(ReBuzz.Studio.Tests.TestApp))]

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
