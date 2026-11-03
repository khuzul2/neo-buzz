// Fork (docs/specs/studio-mvp.md S7): the main window's size and place, kept in the settings
// ("Studio" section) when it closes and put back when it opens, on a screen that is still there.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using ReBuzz.Core;

namespace ReBuzz.Studio.Shell
{
    internal static class WindowBounds
    {
        const string Section = "Studio";

        /// <summary>Before the window shows: its last size, place and maximized state.</summary>
        public static void Restore(Window window, IRegistryEx registry, string name)
        {
            double width = registry.Read(name + "Width", 0.0, Section), height = registry.Read(name + "Height", 0.0, Section);
            if (width >= window.MinWidth && height >= window.MinHeight && width < 20000 && height < 20000)
            {
                window.Width = width;
                window.Height = height;
            }
            int x = registry.Read(name + "X", int.MinValue, Section), y = registry.Read(name + "Y", int.MinValue, Section);
            if (x != int.MinValue && y != int.MinValue)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = new PixelPoint(x, y);
                // A screen unplugged since: back on the main screen.
                window.Opened += (s, e) =>
                {
                    var screens = window.Screens;
                    if (screens == null || screens.All.Count == 0) return;
                    var title = new PixelRect(window.Position, new PixelSize(Math.Max(1, (int)window.Width), 40));
                    if (!screens.All.Any(sc => sc.WorkingArea.Intersects(title)))
                    {
                        var area = (screens.Primary ?? screens.All[0]).WorkingArea;
                        window.Position = new PixelPoint(area.X + Math.Max(0, (area.Width - (int)window.Width) / 2), area.Y + Math.Max(0, (area.Height - (int)window.Height) / 2));
                    }
                };
            }
            if (registry.Read(name + "Maximized", false, Section)) window.WindowState = WindowState.Maximized;
        }

        /// <summary>As it closes. A maximized window keeps the size it had before.</summary>
        public static void Save(Window window, IRegistryEx registry, string name)
        {
            bool maximized = window.WindowState == WindowState.Maximized;
            registry.Write(name + "Maximized", maximized, Section);
            if (maximized || window.WindowState == WindowState.Minimized || window.WindowState == WindowState.FullScreen) return;
            registry.Write(name + "Width", Math.Round(window.ClientSize.Width), Section);
            registry.Write(name + "Height", Math.Round(window.ClientSize.Height), Section);
            registry.Write(name + "X", window.Position.X, Section);
            registry.Write(name + "Y", window.Position.Y, Section);
        }
    }
}
