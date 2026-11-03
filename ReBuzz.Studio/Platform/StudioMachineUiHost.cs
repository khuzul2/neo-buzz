// Fork (docs/specs/studio-mvp.md §3.4): the engine's machine windows (IMachineUiHost) as Avalonia
// windows: a machine's parameter window and its own GUI, one each, kept as long as the machine,
// with their placement saved in songs. What goes inside comes from the steps that build it:
// ParameterWindowContent (S2, the generic parameter window) and GuiContent (S1: a machine's own
// GUI when it is an Avalonia control, as the Neo machines' editors are, MachineGuis.cs). Until
// they are in, a window says what will be there.

using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Platform
{
    // Inside the namespace: the engine's ReBuzz.Control namespace would hide Avalonia's Control.
    using Control = Avalonia.Controls.Control;

    internal sealed class StudioMachineUiHost : IMachineUiHost
    {
        sealed class Windows
        {
            public Window Parameters;
            public Window Gui;
        }

        static readonly ConditionalWeakTable<IMachine, Windows> windows = new();

        /// <summary>The generic parameter window's content for a machine (S2); null: a placeholder.</summary>
        public static Func<IMachine, Control> ParameterWindowContent { get; set; }

        /// <summary>
        /// A machine's own GUI as an Avalonia control (S1: the Neo editors), or null when it has none
        /// here (WPF GUIs don't load off Windows): its parameter window opens instead.
        /// </summary>
        public static Func<IMachine, Control> GuiContent { get; set; } = MachineGuis.Create;

        /// <summary>The window a machine's GUI or parameters are in, if open (for tests).</summary>
        public static Window GuiWindowOf(IMachine machine) => windows.TryGetValue(machine, out var w) ? w.Gui : null;

        public static Window ParameterWindowOf(IMachine machine) => windows.TryGetValue(machine, out var w) ? w.Parameters : null;

        public void AttachMachineView(IntPtr machineViewHwnd) { }

        public void OpenParameterWindow(IMachine machine, WindowPlacement? at)
        {
            var w = windows.GetValue(machine, _ => new Windows());
            w.Parameters ??= Make(machine, $"{machine.Name} — parameters",
                ParameterWindowContent?.Invoke(machine) ?? Note("The parameter window comes in step S2."), () => w.Parameters = null);
            Show(w.Parameters, at);
        }

        public void OpenGui(IMachine machine, WindowPlacement? at)
        {
            var w = windows.GetValue(machine, _ => new Windows());
            if (w.Gui == null)
            {
                // Made once per window: an editor holds on to its machine's parameters while shown.
                var content = GuiContent?.Invoke(machine);
                if (content == null)
                {
                    OpenParameterWindow(machine, at);
                    return;
                }
                w.Gui = Make(machine, machine.Name, content, () => w.Gui = null);
                w.Gui.CanResize = MachineGuis.Resizable(machine);
            }
            Show(w.Gui, at);
        }

        public void CloseWindows(IMachine machine)
        {
            if (!windows.TryGetValue(machine, out var w)) return;
            w.Parameters?.Close();
            w.Gui?.Close();
        }

        public MachineWindowState GetWindowState(IMachine machine)
        {
            windows.TryGetValue(machine, out var w);
            return new MachineWindowState(
                w?.Parameters?.IsVisible == true, Placement(w?.Parameters),
                w?.Gui?.IsVisible == true, Placement(w?.Gui));
        }

        public void ReleaseMachine(IMachine machine)
        {
            CloseWindows(machine);
            windows.Remove(machine);
        }

        public string AskName(string title, string current, int x, int y) =>
            Dialogs.Modal(() => Dialogs.AskText(title, current));

        static Window Make(IMachine machine, string title, Control content, Action closed)
        {
            var window = new Window
            {
                Title = title,
                Content = content,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
            };
            window.Closed += (s, e) => closed();
            return window;
        }

        static void Show(Window window, WindowPlacement? at)
        {
            if (at is WindowPlacement p && p.Width > 0)
            {
                window.SizeToContent = SizeToContent.Manual;
                window.Position = new PixelPoint((int)p.Left, (int)p.Top);
                window.Width = p.Width;
                window.Height = p.Height;
            }
            var owner = Dialogs.Owner;
            if (!window.IsVisible)
            {
                if (owner != null && owner != window) window.Show(owner);
                else window.Show();
            }
            window.Activate();
        }

        static WindowPlacement Placement(Window window) =>
            window == null ? default : new WindowPlacement(window.Position.X, window.Position.Y, window.Bounds.Width, window.Bounds.Height);

        static Control Note(string text) => new TextBlock { Text = text, Margin = new Thickness(24), Foreground = Brushes.Gray };
    }
}
