// Fork (UI gallery review, then the user's "machine windows are not resizable"): a machine window (a machine's GUI,
// its manifest editor) is a fixed size: CanResize = false, sized to its content, so a
// resize never leaves the UI unresponsive. It is never larger than the screen's work area, though: Neo FM6's, Neo
// Poly's and Neo Wave's editors are 942-1000 pt tall, and on a laptop their bottoms were out of reach. A GUI of a
// fixed layout (the Neo editors, the manifest editor) sits in a scroll viewer (NeoBuzz.Editor.MachineKeyboardFrame,
// above the machine keyboard where the machine has one), so where the window is clamped to the work area it keeps
// its layout and scrolls inside it. The clamp is checked again when the window opens, moves to another screen or
// the UI scale changes. A plug-in's editor window follows the plug-in's own can-resize flag (PluginEditor); the
// generic parameter window and the device dock's floating window stay resizable (Clamp) and scroll their rows.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

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
        /// A machine's window: a fixed size (not resizable, no zoom or maximise), sized to its content and kept inside
        /// the work area, the content scrolling where it is larger.
        /// </summary>
        public static void Fix(Window window)
        {
            window.CanResize = false;
            window.CanMaximize = false;
            window.SizeToContent = SizeToContent.WidthAndHeight;
            Watch(window);
        }

        /// <summary>
        /// Keeps <paramref name="window"/> inside the work area (now, and when it opens, moves to another screen or its
        /// scale changes) and leaves it resizable: smaller, its content scrolls; larger, up to the work area.
        /// </summary>
        public static void Clamp(Window window)
        {
            window.CanResize = true;
            Watch(window);
        }

        static void Watch(Window window)
        {
            Apply(window);
            window.Opened += (s, e) => Apply(window);
            window.PositionChanged += (s, e) => Apply(window);
            window.ScalingChanged += (s, e) => Apply(window);
        }

        /// <summary>
        /// Sets the window's largest size from the work area. A fixed window is sized to its content again: where the
        /// content fits it sizes to it (so a larger screen gives it back its whole size); where it doesn't, it takes
        /// the work area and the content scrolls inside. A maximum set on a window that is already sized doesn't shrink
        /// it, so the size is set here, not left to the next layout.
        /// </summary>
        public static void Apply(Window window)
        {
            var area = WorkArea?.Invoke(window);
            if (area is not Size a) return;
            window.MaxWidth = Math.Max(240, a.Width - FrameWidth);
            window.MaxHeight = Math.Max(160, a.Height - FrameHeight);
            if (window.CanResize || window.Content is not Control content) return;
            content.Measure(Size.Infinity);
            var want = content.DesiredSize;
            if (want.Width <= window.MaxWidth && want.Height <= window.MaxHeight)
            {
                // A size set when it was clamped would hold it small: drop it, and size to the content again.
                window.ClearValue(Layoutable.WidthProperty);
                window.ClearValue(Layoutable.HeightProperty);
                window.SizeToContent = SizeToContent.WidthAndHeight;
                return;
            }
            window.SizeToContent = SizeToContent.Manual;
            window.Width = Math.Min(want.Width, window.MaxWidth);
            window.Height = Math.Min(want.Height, window.MaxHeight);
        }
    }
}
