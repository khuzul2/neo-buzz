// Fork (docs/specs/studio-mvp.md §7, test reliability): input in the headless tests that doesn't depend on
// the wall clock. The tests' steps take as long as the runner makes them (a press and release of a studio
// window took over 500 ms on a loaded CI runner), so nothing a step does may hang on how long it took:
//  - Double clicks. Avalonia's mouse counts a press as a double click's second when it comes within
//    GetDoubleTapTime (500 ms) of the last press at that place, timed by the headless window's stopwatch:
//    a test's double click became two single clicks on a slow step, and two separate clicks could become a
//    double click on a quick one. Here every mouse press is a first click, except the second press of
//    MachineViewTests.DoubleClick, which is a double click's second however long the first one took. The
//    presses still go through the platform's mouse as any other: hit testing, capture, gestures.
//  - Menus. A submenu opens (and a sibling's closes) on hover after MenuShowDelay of wall-clock time; a
//    slow step let those timers fire in the middle of a test. The tests had put the delay off for an hour,
//    which left a timer holding each hovered menu item, and its closed window, for the rest of the run.
//    Here the delay is zero: a hover does at once, on the dispatcher, what it does once the user waits.
//  - Tooltips. They open on a timer under a resting pointer, and on a slow step one could open between two
//    steps and take the next click. No test window has them (some fixtures turned them off already).
//
// Avalonia has no public way to change the platform's settings: the headless platform binds them in its
// AvaloniaLocator (a private API) after AppBuilder.With's options are bound, so the test app replaces the
// binding in AfterPlatformServicesSetup, with AvaloniaAccessUnstablePrivateApis (the csproj). The settings
// are the headless platform's own for everything else.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform;

namespace ReBuzz.Studio.Tests
{
    internal static class HeadlessInput
    {
        /// <summary>Puts the tests' input settings in place (AppBuilder.AfterPlatformServicesSetup).</summary>
        public static void Install()
        {
            var platform = AvaloniaLocator.Current.GetRequiredService<IPlatformSettings>();
            AvaloniaLocator.CurrentMutable.Bind<IPlatformSettings>().ToConstant(new HeadlessInputSettings(platform));
            DefaultMenuInteractionHandler.MenuShowDelay = TimeSpan.Zero;
            Window.WindowOpenedEvent.AddClassHandler<Window>((w, e) => ToolTip.SetServiceEnabled(w, false), handledEventsToo: true);
        }
    }

    internal sealed class HeadlessInputSettings : IPlatformSettings
    {
        readonly IPlatformSettings platform;

        internal HeadlessInputSettings(IPlatformSettings platform) => this.platform = platform;

        /// <summary>
        /// While true, a mouse press at the last press's place is that click's second (a double click), whatever
        /// time passed; otherwise every press is a first click. Only MachineViewTests.DoubleClick sets it.
        /// </summary>
        internal static bool SecondPress { get; set; }

        // MouseDevice: a press is a first click when "now - last press > this" (in ms): -1 ms always is, a day never.
        public TimeSpan GetDoubleTapTime(PointerType type) =>
            type != PointerType.Mouse ? platform.GetDoubleTapTime(type)
            : SecondPress ? TimeSpan.FromDays(1) : TimeSpan.FromMilliseconds(-1);

        public Size GetTapSize(PointerType type) => platform.GetTapSize(type);

        public Size GetDoubleTapSize(PointerType type) => platform.GetDoubleTapSize(type);

        public TimeSpan HoldWaitDuration => platform.HoldWaitDuration;

        public PlatformHotkeyConfiguration HotkeyConfiguration => platform.HotkeyConfiguration;

        public PlatformColorValues GetColorValues() => platform.GetColorValues();

        public event EventHandler<PlatformColorValues> ColorValuesChanged
        {
            add => platform.ColorValuesChanged += value;
            remove => platform.ColorValuesChanged -= value;
        }
    }
}
