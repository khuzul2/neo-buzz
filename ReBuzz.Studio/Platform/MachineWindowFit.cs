// Fork (UI gallery review): a machine window (a machine's GUI, its manifest editor, its parameter window) never
// larger than the screen's work area. Neo FM6's, Neo Poly's and Neo Wave's editors are 942–1000 pt tall and were
// fixed in size, the parameter window 1080 pt: on a laptop's screen their bottoms were out of reach. A GUI of a
// fixed layout (the Neo editors, the manifest editor) sits in a scroll viewer (NeoBuzz.Editor.MachineKeyboardFrame,
// above the machine keyboard where the machine has one), so where the window is clamped it keeps its layout and
// scrolls; the parameter window scrolls its rows itself. Each window may be resized (the content scrolls), never
// past the work area.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace ReBuzz.Studio.Platform
{
    // Inside the namespace: the engine's ReBuzz.Control namespace would hide Avalonia's Control.
    using Control = Avalonia.Controls.Control;

    internal static class MachineWindowFit
    {
        /// <summary>The room a window's frame takes around its content (title bar, borders), kept out of the work area.</summary>
        public const double FrameWidth = 16, FrameHeight = 40;

        /// <summary>
        /// The work area a window is kept inside, in device-independent pixels: its screen's, less the dock or task
        /// bar; null when the platform doesn't say (no clamp). Tests set a screen of their own.
        /// </summary>
        public static Func<Window, Size?> WorkArea { get; set; } = ScreenWorkArea;

        static Size? ScreenWorkArea(Window window)
        {
            try
            {
                var screens = window.Screens;
                var screen = screens?.ScreenFromWindow(window) ?? screens?.Primary;
                if (screen == null || screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0) return null;
                double scaling = screen.Scaling > 0 ? screen.Scaling : 1;
                return new Size(screen.WorkingArea.Width / scaling, screen.WorkingArea.Height / scaling);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Keeps <paramref name="window"/> inside the work area (now, and when it opens on its screen) and lets it be
        /// resized: smaller, its content scrolls; larger, up to the work area.
        /// </summary>
        public static void Clamp(Window window)
        {
            window.CanResize = true;
            Apply(window);
            window.Opened += (s, e) => Apply(window);
        }

        /// <summary>Sets the window's largest size from the work area.</summary>
        public static void Apply(Window window)
        {
            var area = WorkArea?.Invoke(window);
            if (area is not Size a) return;
            window.MaxWidth = Math.Max(240, a.Width - FrameWidth);
            window.MaxHeight = Math.Max(160, a.Height - FrameHeight);
        }
    }
}
