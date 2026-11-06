// Fork (UI gallery review): a machine window (a managed machine's GUI, its manifest editor, its parameter window) never
// larger than the screen's work area. The parameter window was 410 × 1100 px and Neo FM6's, Neo Poly's and Neo
// Wave's editors 946–1006 px tall and fixed: on a 1080 px screen (less the task bar) their bottoms were out of reach.
// A GUI of a fixed layout (the Neo editors, the manifest editor) sits in a scroll viewer
// (NeoBuzz.Editor.MachineKeyboardFrame, above the machine keyboard where the machine has one), so where the window
// is clamped it keeps its layout and scrolls, and the window may be resized; the parameter window scrolls its rows
// itself. The studio's flavour is ReBuzz.Studio/Platform/MachineWindowFit.cs.

using System;
using System.Windows;
using System.Windows.Controls;

namespace ReBuzz.AppViews
{
    internal static class MachineWindowFit
    {
        /// <summary>The work area a machine window is kept inside (device-independent pixels): the screen's, less the task bar.</summary>
        public static Func<Rect> WorkArea { get; set; } = () => SystemParameters.WorkArea;

        /// <summary>Keeps <paramref name="window"/> (its frame included) inside the work area.</summary>
        public static void Clamp(Window window)
        {
            Rect area;
            try
            {
                area = WorkArea();
            }
            catch (Exception)
            {
                return;
            }
            if (area.Width <= 0 || area.Height <= 0) return;
            window.MaxWidth = Math.Max(240, area.Width);
            window.MaxHeight = Math.Max(160, area.Height);
        }
    }
}
