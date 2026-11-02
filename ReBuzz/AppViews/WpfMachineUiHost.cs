using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using BuzzGUI.ParameterWindow;
using ReBuzz.Common;
using ReBuzz.Core;
using ReBuzz.ManagedMachine;
using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace ReBuzz.AppViews
{
    /// <summary>
    /// Fork (docs/specs/engine-split.md S3): the WPF side of IMachineUiHost. The parameter window,
    /// the windowed machine GUI and the rename prompt, moved here unchanged from MachineCore and
    /// MachineGroupCore. A machine's windows live as long as the machine (a weak table, as when they
    /// were MachineCore fields).
    /// </summary>
    internal sealed class WpfMachineUiHost : IMachineUiHost
    {
        sealed class MachineWindows
        {
            public ParameterWindow Parameters;
            public MachineGUIHostWindow Gui;
        }

        static readonly ConditionalWeakTable<MachineCore, MachineWindows> windows = new();

        static MachineWindows Find(IMachine machine) =>
            machine is MachineCore mc && windows.TryGetValue(mc, out var w) ? w : null;

        /// <summary>IMachine.ParameterWindow: null until the window has been opened.</summary>
        internal static Window ParameterWindowOf(IMachine machine) => Find(machine)?.Parameters;

        /// <summary>The machine's windowed GUI, null until opened.</summary>
        internal static MachineGUIHostWindow GuiWindowOf(IMachine machine) => Find(machine)?.Gui;

        public void AttachMachineView(IntPtr machineViewHwnd)
        {
            Global.MachineViewHwndSource = HwndSource.FromHwnd(machineViewHwnd);
        }

        public void OpenParameterWindow(IMachine machine, WindowPlacement? at)
        {
            var mc = (MachineCore)machine;
            var w = windows.GetValue(mc, _ => new MachineWindows());
            var parameterWindow = w.Parameters;
            if (parameterWindow == null)
            {
                ParameterWindowVM pWindowVM = new ParameterWindowVM();
                pWindowVM.Machine = mc;

                parameterWindow = UiUtils.GetUserControlXAML<ParameterWindow>("ParameterWindow.xaml", mc.BuzzPath);
                w.Parameters = parameterWindow;
                Window window = (Window)HwndSource.FromHwnd(mc.Graph.Buzz.MachineViewHWND).RootVisual;
                parameterWindow.Owner = window;

                parameterWindow.DataContext = pWindowVM;
                parameterWindow.Closing += (s, e) =>
                {
                    if (w.Parameters != null)
                    {
                        w.Parameters.Hide();
                    }
                    e.Cancel = true;
                };
            }

            if (at is WindowPlacement p)
            {
                var rect = new Rect(p.Left, p.Top, p.Width, p.Height);
                if (rect.Width != 0)
                {
                    parameterWindow.Left = rect.Left;
                    parameterWindow.Top = rect.Top;
                    parameterWindow.Width = rect.Width;
                    parameterWindow.Height = rect.Height;
                }
            }

            parameterWindow.Show();
            parameterWindow.InvalidateMeasure();
            parameterWindow.UpdateLayout();
        }

        public void OpenGui(IMachine machine, WindowPlacement? at)
        {
            var mc = (MachineCore)machine;
            var DLL = mc.DLL;
            var w = windows.GetValue(mc, _ => new MachineWindows());
            var machineGUIWindow = w.Gui;

            if (machineGUIWindow == null)
            {
                machineGUIWindow = new MachineGUIHostWindow();
                w.Gui = machineGUIWindow;

                if (DLL.GUIFactoryDecl.Width > 0 && DLL.GUIFactoryDecl.Height > 0)
                {
                    machineGUIWindow.SizeToContent = SizeToContent.Manual;
                    machineGUIWindow.Width = DLL.GUIFactoryDecl.Width;
                    machineGUIWindow.Height = DLL.GUIFactoryDecl.Height;
                }
                else
                {
                    machineGUIWindow.SizeToContent = SizeToContent.WidthAndHeight;
                }

                var gui = DLL.GUIFactory.CreateGUI(machineGUIWindow);
                mc.gui = gui;
                gui.Machine = mc;
                var guiUIElement = gui as UserControl;

                machineGUIWindow.Title = mc.Name;
                machineGUIWindow.ResizeMode = DLL.GUIFactoryDecl.IsGUIResizable ? ResizeMode.CanResize : ResizeMode.NoResize;

                machineGUIWindow.Closing += (s, e) =>
                {
                    machineGUIWindow.Hide();
                    e.Cancel = true;
                };

                var interop = new WindowInteropHelper(machineGUIWindow);
                interop.Owner = mc.Graph.Buzz.MachineViewHWND;

                machineGUIWindow.Loaded += (s, e) =>
                {
                    if (DLL.GUIFactoryDecl.UseThemeStyles)
                    {
                        var r = UiUtils.GetUserControlXAML<Window>("ParameterWindow.xaml", mc.BuzzPath);
                        machineGUIWindow.Resources.MergedDictionaries.Add(r.Resources);
                    }
                };

                try
                {
                    if (!DLL.IsManaged)
                    {

                        // Add mouse wheel zoom
                        Viewbox vb = new Viewbox();
                        vb.Stretch = System.Windows.Media.Stretch.Fill;
                        vb.Child = guiUIElement;
                        machineGUIWindow.Content = vb;

                        double scale = 1.0;

                        guiUIElement.SizeChanged += (sender, e) =>
                        {
                            vb.Width = guiUIElement.ActualWidth * scale;
                            vb.Height = guiUIElement.ActualHeight * scale;

                            machineGUIWindow.Width = vb.Width;
                            machineGUIWindow.Height = vb.Height;
                        };

                        machineGUIWindow.SizeChanged += (sender, e) =>
                        {
                            machineGUIWindow.Width = vb.Width + SystemParameters.VerticalScrollBarWidth;
                            machineGUIWindow.Height = vb.Height + SystemParameters.HorizontalScrollBarHeight + SystemParameters.ResizeFrameHorizontalBorderHeight;
                        };

                        machineGUIWindow.PreviewMouseWheel += (sender, e) =>
                        {
                            if (Keyboard.Modifiers == ModifierKeys.Control)
                            {
                                scale += e.Delta / 600.0;
                                if (scale < 0.5)
                                    scale = 0.5;

                                vb.Width = guiUIElement.ActualWidth * scale;
                                vb.Height = guiUIElement.ActualHeight * scale;

                                machineGUIWindow.Width = vb.Width;
                                machineGUIWindow.Height = vb.Height;

                                e.Handled = true;
                            }
                        };
                    }
                    else
                    {
                        machineGUIWindow.Content = guiUIElement;
                    }
                }
                catch (Exception ex)
                {
                    (mc.Graph.Buzz as ReBuzzCore)?.UserMessages.Error(ex.ToString(), null, ex);
                }
            }

            if (at is WindowPlacement p)
            {
                var rect = new Rect(p.Left, p.Top, p.Width, p.Height);
                if (rect.Width != 0)
                {
                    machineGUIWindow.Left = rect.Left;
                    machineGUIWindow.Top = rect.Top;
                    machineGUIWindow.Width = rect.Width;
                    machineGUIWindow.Height = rect.Height;
                }
            }

            try
            {
                if (machineGUIWindow != null)
                    machineGUIWindow.Show();
            }
            catch (Exception ex)
            {
                (mc.Graph.Buzz as ReBuzzCore)?.UserMessages.Error(ex.ToString(), null, ex);
            }
        }

        public void CloseWindows(IMachine machine)
        {
            var w = Find(machine);
            if (w?.Parameters != null)
            {
                w.Parameters.Close();
            }

            if (w?.Gui != null)
            {
                w.Gui.Close();
            }
        }

        public MachineWindowState GetWindowState(IMachine machine)
        {
            var pw = Find(machine)?.Parameters;
            var mGUI = Find(machine)?.Gui;

            bool parameterWindowVisible = pw != null && pw.Visibility == Visibility.Visible;
            bool guiWindowVisible = mGUI != null && mGUI.Visibility == Visibility.Visible;

            return new MachineWindowState(
                parameterWindowVisible, parameterWindowVisible ? new WindowPlacement(pw.Left, pw.Top, pw.ActualWidth, pw.ActualHeight) : default,
                guiWindowVisible, guiWindowVisible ? new WindowPlacement(mGUI.Left, mGUI.Top, mGUI.ActualWidth, mGUI.ActualHeight) : default);
        }

        public void ReleaseMachine(IMachine machine)
        {
            var pw = Find(machine)?.Parameters;
            if (pw != null)
            {
                var dc = pw.DataContext as ParameterWindowVM;
                if (dc != null)
                    dc.Machine = null;
            }
        }

        public string AskName(string title, string current, int x, int y)
        {
            var renameWindow = new RenameMachineWindow(title, current, false);
            renameWindow.SetStartUpLocation(x, y);
            return renameWindow.ShowDialog() == true ? renameWindow.tbName.Text : null;
        }
    }
}
