using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Common.Settings;
using BuzzGUI.Interfaces;
using Microsoft.Win32;
using ReBuzz.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;

namespace ReBuzz.Common
{
    /// <summary>
    /// The WPF half of the former Utils class: themes and XAML, theme colours, message boxes,
    /// DPI and window placement. Engine code should use <see cref="Utils"/>; its remaining calls
    /// here go with seams S1, S3, S4 and S6 (docs/specs/engine-split.md).
    /// </summary>
    public static class UiUtils
    {
        public static ResourceDictionary GetBuzzThemeResources(string theme, string buzzPath)
        {
            ResourceDictionary res = new ResourceDictionary();

            try
            {
                string selectedTheme = Global.Buzz.SelectedTheme == "<default>" ? "Default" : Global.Buzz.SelectedTheme;
                string skinPath = buzzPath + "\\Themes\\" + selectedTheme + "\\" + theme;

                res = XamlReaderEx.LoadHack(skinPath) as ResourceDictionary;
            }
            catch (Exception)
            {
                string skinPath = buzzPath + "\\Themes\\Default\\" + theme;
                res = XamlReaderEx.LoadHack(skinPath) as ResourceDictionary;
            }

            return res;
        }

        public static T GetUserControlXAML<T>(string xaml, string buzzPath)
        {
            T rootObject;

            string selectedTheme = Global.Buzz.SelectedTheme == "<default>" ? "Default" : Global.Buzz.SelectedTheme;
            string skinPath = buzzPath + "\\Themes\\" + selectedTheme + "\\" + xaml;

            try
            {
                rootObject = (T)XamlReaderEx.LoadHack(skinPath);

            }
            catch (Exception e)
            {
                Global.Buzz.DCWriteLine("XAML Error in " + skinPath + ":\n\n" + e.Message + "\n\nInner Exception: " + e.InnerException + "\n", DCLogLevel.Error);
                skinPath = buzzPath + "\\Themes\\Default\\" + xaml;
                StreamReader mysr = new StreamReader(skinPath);
                rootObject = (T)XamlReaderEx.LoadHack(skinPath);
            }

            return rootObject;
        }

        public class ColIndex : IIndex<string, Color>
        {
            public Dictionary<string, Color> Colors { get; }
            public ColIndex() { Colors = new Dictionary<string, Color>(); }

            public void Add(string key, Color val)
            {
                Colors[key] = val;
            }
            public Color this[string index] => Colors.ContainsKey(index) ? Colors[index] : System.Windows.Media.Colors.Black;
        }

        public static IIndex<string, Color> GetThemeColors(string buzzPath)
        {
            ColIndex colIndex = new ColIndex();

            // Add defaults?
            InitThemeColors(colIndex);

            string selectedTheme = Global.Buzz.SelectedTheme == "<default>" ? "Default" : Global.Buzz.SelectedTheme + ".col";

            if (selectedTheme != "Default")
            {
                string skinPath = buzzPath + "\\Themes\\" + selectedTheme;

                string[] readText = File.ReadAllLines(skinPath);
                string[] delimiterChars = {
                            "\t",
                            "  "
                          };
                foreach (string s in readText)
                {
                    try
                    {
                        if (!s.Trim().StartsWith("#") && s.Trim().Length > 0)
                        {
                            string[] items = s.Trim().Split(delimiterChars, StringSplitOptions.RemoveEmptyEntries);
                            string key = items[0].Trim();
                            Color value = (Color)ColorConverter.ConvertFromString("#ff" + items.Last().Trim());
                            colIndex.Add(key, value);
                        }
                    }
                    catch { }
                }
            }

            return colIndex;
        }

        static readonly (string Name, uint Color)[] defaultColors =
        [
            ("DC BG", 0xFF000000),
            ("DC Text", 0xFFC0C0C0),
            ("IV BG", 0xFF000000),
            ("IV Text", 0xFF000000),
            ("MV Amp BG", 0xFFFFFFFF),
            ("MV Amp Handle", 0xFF000000),
            ("MV Arrow", 0xFFFFFFFF),
            ("MV Background", 0xFFDAD6C9),
            ("MV Control", 0xFFADADAD),
            ("MV Effect", 0xFFC7ADA9),
            ("MV Effect LED Border", 0xFF000000),
            ("MV Effect LED Off", 0xFF641E1E),
            ("MV Effect LED On", 0xFFFF6464),
            ("MV Effect Mute", 0xFF9F8A87),
            ("MV Effect Pan BG", 0xFF92655F),
            ("MV Generator", 0xFFA9AEC7),
            ("MV Generator LED Border", 0xFF000000),
            ("MV Generator LED Off", 0xFF28288C),
            ("MV Generator LED On", 0xFF6464FF),
            ("MV Generator Mute", 0xFF878B9F),
            ("MV Generator Pan BG", 0xFF5F6792),
            ("MV Line", 0xFF000000),
            ("MV Machine Border", 0xFF000000),
            ("MV Machine Select 1", 0xFFFFFFFF),
            ("MV Machine Select 2", 0xFF000000),
            ("MV Machine Text", 0xFF000000),
            ("MV Master", 0xFFC6BEAA),
            ("MV Master LED Border", 0xFF000000),
            ("MV Master LED Off", 0xFF595922),
            ("MV Master LED On", 0xFFE8E8C1),
            ("MV Pan Handle", 0xFF000000),
            ("PE BG", 0xFFDAD6C9),
            ("PE BG Dark", 0xFFBDB59F),
            ("PE BG Very Dark", 0xFF9F9373),
            ("PE Sel BG", 0xFFF7F7F4),
            ("PE Text", 0xFF303021),
            ("SA Amp BG", 0xFF000000),
            ("SA Amp Line", 0xFF00C800),
            ("SA Freq BG", 0xFF000000),
            ("SA Freq Line", 0xFF00C800),
            ("SE BG", 0xFFDAD6C9),
            ("SE BG Dark", 0xFFBDB59F),
            ("SE BG Very Dark", 0xFF9F9373),
            ("SE Break Box", 0xFFE0B06D),
            ("SE Line", 0xFF000000),
            ("SE Mute Box", 0xFFDD816C),
            ("SE Pattern Box", 0xFFC6BEA9),
            ("SE Sel BG", 0xFFF7F7F4),
            ("SE Song Position", 0xFFFFFF00),
            ("SE Text", 0xFF303021),
            ("SE Off Box", 0xFF606060),
            ("black", 0xFF000000)
        ];

        static void InitThemeColors(ColIndex colIndex)
        {
            foreach (var col in defaultColors)
            {
                byte[] bytes = BitConverter.GetBytes(col.Color);
                colIndex.Add(col.Name, Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]));
            }
        }

        static Window mainWindow = null;

        public static void InitUtils(Window appWindow)
        {
            UiUtils.mainWindow = appWindow;
        }

        public static MessageBoxResult MessageBox(string message, string caption = null, MessageBoxButton messageBoxButton = MessageBoxButton.OK)
        {
            // Fork (agent server): no dialogs during an agent call or in headless mode.
            if (ReBuzz.Agent.NonModal.TryCapture(message, caption, messageBoxButton, out var captured)) return captured;

            var handle = new WindowInteropHelper(Application.Current.MainWindow).Handle;

            UiUtils.mainWindow.Topmost = true;
            MessageBoxResult result = System.Windows.MessageBox.Show(message, caption, messageBoxButton);
            UiUtils.mainWindow.Topmost = false;

            return result;
        }

        public static void AllowUIToUpdate()
        {
            DispatcherFrame frame = new DispatcherFrame();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, new DispatcherOperationCallback(delegate (object parameter)
            {
                frame.Continue = false;
                return null;

            }), null);
            Dispatcher.PushFrame(frame);
        }

        internal static void UpdateDpi(FrameworkElement frameworkElement)
        {
            if (Global.GeneralSettings.DpiScaling != DpiScalingType.System)
            {
                double scale = (double)Global.GeneralSettings.DpiScaling / 100.0;
                UiUtils.SetWindowDpiScaling(frameworkElement, scale);
            }
            else
            {
                frameworkElement.LayoutTransform = null;
            }
        }

        internal static void SetWindowDpiScaling(FrameworkElement fwElement, double scale)
        {
            var source = PresentationSource.FromVisual(fwElement);
            if (source != null)
            {
                var matrix = source.CompositionTarget.TransformToDevice;
                double dpiX = matrix.M11;
                double dpiY = matrix.M22;
                fwElement.LayoutTransform = new ScaleTransform(scale / dpiX, scale / dpiY);
            }
        }

        private static readonly Action EmptyDelegate = delegate { };
        // Forces refreshin view
        public static void Refresh(this UIElement uiElement)
        {
            uiElement.Dispatcher.Invoke(DispatcherPriority.Render, EmptyDelegate);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("Shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

        private const uint MONITOR_DEFAULTTONULL = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        private static bool IsWindowVisibleOnAnyMonitor(double left, double top, double width, double height)
        {
            RECT rect = new RECT
            {
                Left = (int)left,
                Top = (int)top,
                Right = (int)(left + width),
                Bottom = (int)(top + height)
            };

            IntPtr monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL);
            return monitor != IntPtr.Zero;
        }

        internal static void SaveWindowStateToRegistry(Window window, IRegistryEx registry, string registryKey)
        {
            if (window == null)
                return;

            window.Dispatcher.Invoke(() =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY);

                Rect bounds = window.RestoreBounds;

                if (!(double.IsNormal(bounds.Width) && double.IsNormal(bounds.Height) &&
                      double.IsNormal(bounds.Top) && double.IsNormal(bounds.Left)))
                    return;

                string regValue = string.Join("|",
                    window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState,
                    bounds.Width * dpiX / 96.0,
                    bounds.Height * dpiY / 96.0,
                    bounds.Left * dpiX / 96.0,
                    bounds.Top * dpiY / 96.0,
                    dpiX,
                    dpiY
                );

                registry.Write(registryKey, regValue, "Settings");
            });
        }

        internal static void RestoreWindowStateFromRegistry(Window window, IRegistryEx registry, string registryKey)
        {
            window.Dispatcher.Invoke(() =>
            {
                string? regValue = registry.Read(registryKey, "", "Settings");
                if (string.IsNullOrWhiteSpace(regValue))
                    return;

                string[] parts = regValue.Split('|');
                if (parts.Length < 7)
                    return;

                string stateStr = parts[0];
                double widthPx = double.Parse(parts[1]);
                double heightPx = double.Parse(parts[2]);
                double leftPx = double.Parse(parts[3]);
                double topPx = double.Parse(parts[4]);
                double savedDpiX = double.Parse(parts[5]);
                double savedDpiY = double.Parse(parts[6]);

                double width = widthPx * 96.0 / savedDpiX;
                double height = heightPx * 96.0 / savedDpiY;
                double left = leftPx * 96.0 / savedDpiX;
                double top = topPx * 96.0 / savedDpiY;

                if (!double.IsNormal(width) || !double.IsNormal(height) ||
                    !double.IsNormal(left) || !double.IsNormal(top))
                    return;

                window.Width = width;
                window.Height = height;
                window.Left = left;
                window.Top = top;

                // If window is off-screen (monitor unplugged), move it to primary monitor center
                if (!IsWindowVisibleOnAnyMonitor(left, top, width, height))
                {
                    var primary = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                    var dpi = VisualTreeHelper.GetDpi(window);

                    double primaryLeftDip = primary.Left / dpi.DpiScaleX;
                    double primaryTopDip = primary.Top / dpi.DpiScaleY;
                    double primaryWidthDip = primary.Width / dpi.DpiScaleX;
                    double primaryHeightDip = primary.Height / dpi.DpiScaleY;

                    window.Left = primaryLeftDip + (primaryWidthDip - window.Width) / 2;
                    window.Top = primaryTopDip + (primaryHeightDip - window.Height) / 2;
                }

                // Reset state after positioning
                if (Enum.TryParse(stateStr, out WindowState restoredState))
                    window.WindowState = restoredState;
                else
                    window.WindowState = WindowState.Normal;
            });
        }
    }
}
