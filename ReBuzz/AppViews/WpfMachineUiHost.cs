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
    /// Fork (docs/specs/mcp-server.md §12.11, U4): a machine's manifest editor (Machines/NeoBuzz.ManifestEditor) in a
    /// window of its own, what a double click opens on a machine without a GUI when the per-gear setting says so,
    /// and the machine menu's "Editor...". Not saved in songs.
    /// Fork (machine keyboard, docs/specs/studio-mvp.md §3.12): each of these windows of a machine that plays notes has the
    /// keyboard under its content (NeoBuzz.Editor.MachineKeyboardFrame), the window grown by its height; songs keep the
    /// windows' sizes without it, so a GUI opens at its own size with or without it. Space plays and stops the song in these
    /// windows (TransportKeys, as in the main window), so a focused keyboard never takes it.
    /// </summary>
    internal sealed class WpfMachineUiHost : IMachineUiHost
    {
        sealed class MachineWindows
        {
            public ParameterWindow Parameters;
            public MachineGUIHostWindow Gui;
            public Window Editor;
        }

        static readonly ConditionalWeakTable<MachineCore, MachineWindows> windows = new();

        static MachineWindows Find(IMachine machine) =>
            machine is MachineCore mc && windows.TryGetValue(mc, out var w) ? w : null;

        /// <summary>The height a machine window's keyboard takes (0 without one).</summary>
        static double KeyboardHeight(Window w) => NeoBuzz.Editor.MachineKeyboardFrame.ExtraHeightOf(w?.Content);

        /// <summary>The keyboard under a machine window's content, or null (tests, --keyboard-shots).</summary>
        internal static NeoBuzz.Editor.MachineKeyboard KeyboardOf(Window w) => NeoBuzz.Editor.MachineKeyboardFrame.Of(w?.Content);

        /// <summary>A machine window's own content (its GUI, parameters or editor), without the keyboard under it.</summary>
        internal static object ContentOf(Window w) => NeoBuzz.Editor.MachineKeyboardFrame.ContentOf(w?.Content);

        /// <summary>Space plays and stops the song in a machine window too, before its focused control (the keyboard's keys, a button).</summary>
        static void TakeTransportKeys(Window window, MachineCore mc)
        {
            window.PreviewKeyDown += (s, e) =>
            {
                if (e.Handled || mc.Graph?.Buzz is not ReBuzzCore core) return;
                switch (TransportKeys.For(e.Key, Keyboard.Modifiers, e.IsRepeat, false, e.OriginalSource as DependencyObject))
                {
                    case TransportKeys.Command.TogglePlaying:
                        TransportCommands.TogglePlaying(core);
                        e.Handled = true;
                        break;
                    case TransportKeys.Command.Repeat:
                        e.Handled = true;
                        break;
                }
            };
        }

        /// <summary>IMachine.ParameterWindow: null until the window has been opened.</summary>
        internal static Window ParameterWindowOf(IMachine machine) => Find(machine)?.Parameters;

        /// <summary>The machine's windowed GUI, null until opened.</summary>
        internal static MachineGUIHostWindow GuiWindowOf(IMachine machine) => Find(machine)?.Gui;

        /// <summary>The machine's manifest editor window, null until opened.</summary>
        internal static Window EditorWindowOf(IMachine machine) => Find(machine)?.Editor;

        public bool OpenEditor(IMachine machine)
        {
            if (machine is not MachineCore mc) return false;
            var w = windows.GetValue(mc, _ => new MachineWindows());
            if (w.Editor == null)
            {
                var editor = Agent.MachineFaces.Editor(mc, () => OpenParameterWindow(mc, null));
                if (editor == null) return false;
                var window = new Window
                {
                    Title = mc.Name + " \u2014 editor",
                    Content = NeoBuzz.Editor.MachineKeyboardFrame.Wrap(mc, editor),
                    SizeToContent = SizeToContent.WidthAndHeight,
                    ResizeMode = ResizeMode.CanMinimize,
                    ShowInTaskbar = false,
                    Background = editor.Background,
                };
                TakeTransportKeys(window, mc);
                // Kept for the machine's life, as its other windows: closing hides it.
                window.Closing += (s, e) =>
                {
                    window.Hide();
                    e.Cancel = true;
                };
                // Owned by the main window, as the parameter window is (none when ReBuzz runs headless).
                if (mc.Graph?.Buzz?.MachineViewHWND is IntPtr hwnd && hwnd != IntPtr.Zero && HwndSource.FromHwnd(hwnd)?.RootVisual is Window owner)
                    window.Owner = owner;
                w.Editor = window;
            }
            w.Editor.Show();
            w.Editor.Activate();
            return true;
        }

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
                // The keyboard under the parameter list (the theme's content), for a machine that plays notes.
                if (parameterWindow.Content is UIElement themed)
                {
                    parameterWindow.Content = null;
                    parameterWindow.Content = NeoBuzz.Editor.MachineKeyboardFrame.Wrap(mc, themed);
                }
                TakeTransportKeys(parameterWindow, mc);
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
                    parameterWindow.Height = rect.Height + KeyboardHeight(parameterWindow);   // songs keep the list's height
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
                TakeTransportKeys(machineGUIWindow, mc);

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
                        // Fork (machine keyboard): the keyboard under the GUI, the window as tall as both.
                        machineGUIWindow.Content = NeoBuzz.Editor.MachineKeyboardFrame.Wrap(mc, vb);

                        double scale = 1.0;

                        guiUIElement.SizeChanged += (sender, e) =>
                        {
                            vb.Width = guiUIElement.ActualWidth * scale;
                            vb.Height = guiUIElement.ActualHeight * scale;

                            machineGUIWindow.Width = vb.Width;
                            machineGUIWindow.Height = vb.Height + KeyboardHeight(machineGUIWindow);
                        };

                        machineGUIWindow.SizeChanged += (sender, e) =>
                        {
                            machineGUIWindow.Width = vb.Width + SystemParameters.VerticalScrollBarWidth;
                            machineGUIWindow.Height = vb.Height + KeyboardHeight(machineGUIWindow) + SystemParameters.HorizontalScrollBarHeight + SystemParameters.ResizeFrameHorizontalBorderHeight;
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
                                machineGUIWindow.Height = vb.Height + KeyboardHeight(machineGUIWindow);

                                e.Handled = true;
                            }
                        };
                    }
                    else
                    {
                        machineGUIWindow.Content = NeoBuzz.Editor.MachineKeyboardFrame.Wrap(mc, guiUIElement);
                        // A GUI of a fixed size: the window is that and the keyboard.
                        if (machineGUIWindow.SizeToContent == SizeToContent.Manual) machineGUIWindow.Height += KeyboardHeight(machineGUIWindow);
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
                    machineGUIWindow.Height = rect.Height + KeyboardHeight(machineGUIWindow);   // songs keep the GUI's height
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

            // Its Closing only hides it.
            w?.Editor?.Hide();
        }

        public MachineWindowState GetWindowState(IMachine machine)
        {
            var pw = Find(machine)?.Parameters;
            var mGUI = Find(machine)?.Gui;

            bool parameterWindowVisible = pw != null && pw.Visibility == Visibility.Visible;
            bool guiWindowVisible = mGUI != null && mGUI.Visibility == Visibility.Visible;

            // Songs keep a window's size without its keyboard (the GUI's own), so it opens right with or without one.
            return new MachineWindowState(
                parameterWindowVisible, parameterWindowVisible ? new WindowPlacement(pw.Left, pw.Top, pw.ActualWidth, Math.Max(0, pw.ActualHeight - KeyboardHeight(pw))) : default,
                guiWindowVisible, guiWindowVisible ? new WindowPlacement(mGUI.Left, mGUI.Top, mGUI.ActualWidth, Math.Max(0, mGUI.ActualHeight - KeyboardHeight(mGUI))) : default);
        }

        public void ReleaseMachine(IMachine machine)
        {
            // The manifest editor lets go of the machine's parameters.
            if (Find(machine)?.Editor is Window editorWindow)
            {
                editorWindow.Hide();
                if (ContentOf(editorWindow) is NeoBuzz.Editor.ManifestEditor editor) editor.Machine = null;
            }
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
