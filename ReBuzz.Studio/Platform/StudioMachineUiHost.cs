// Fork (docs/specs/studio-mvp.md §3.4): the engine's machine windows (IMachineUiHost) as Avalonia
// windows: a machine's parameter window and its own GUI, one each, kept as long as the machine,
// with their placement saved in songs. What goes inside comes from the steps that build it:
// ParameterWindowContent (S2, the generic parameter window) and GuiContent (S1: a machine's own
// GUI when it is an Avalonia control, as the Neo machines' editors are, MachineGuis.cs). Until
// they are in, a window says what will be there.
// Fork (docs/specs/mcp-server.md §12.11, U4): a machine's manifest editor (Machines/NeoBuzz.ManifestEditor),
// in a window of its own: what a double click opens on a machine with no GUI here when the per-gear
// setting says so (on by default for curated manifests), and the machine menu's "Editor...". It isn't
// saved in songs (the parameter window and GUI are, as before).
// Fork (machine keyboard, docs/specs/studio-mvp.md §3.12): each of these windows of a machine that plays notes
// has the keyboard under its content (MachineKeyboardFrame), the window grown by its height; songs keep the
// windows' sizes without it, so a GUI opens at its own size with or without it. Space plays and stops the
// song in these windows too (TransportKeys, as in the main window), so a focused keyboard never takes it.

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
            public Window Editor;
        }

        static readonly ConditionalWeakTable<IMachine, Windows> windows = new();

        /// <summary>The generic parameter window's content for a machine (S2); null: a placeholder.</summary>
        public static Func<IMachine, Control> ParameterWindowContent { get; set; } = m => new Parameters.ParameterWindowView(m);

        /// <summary>
        /// A machine's own GUI as an Avalonia control (S1: the Neo editors), or null when it has none
        /// here (WPF GUIs don't load off Windows): its parameter window opens instead.
        /// </summary>
        public static Func<IMachine, Control> GuiContent { get; set; } = MachineGuis.Create;

        /// <summary>The window a machine's GUI or parameters are in, if open (for tests).</summary>
        public static Window GuiWindowOf(IMachine machine) => windows.TryGetValue(machine, out var w) ? w.Gui : null;

        public static Window ParameterWindowOf(IMachine machine) => windows.TryGetValue(machine, out var w) ? w.Parameters : null;

        /// <summary>The window a machine's manifest editor is in, if open (for tests).</summary>
        public static Window EditorWindowOf(IMachine machine) => windows.TryGetValue(machine, out var w) ? w.Editor : null;

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
                    // U4: its manifest editor where the setting says so, else its parameter window.
                    if (ReBuzz.Agent.ManifestEditorSettings.OpensEditor(machine.DLL) && OpenEditor(machine)) return;
                    OpenParameterWindow(machine, at);
                    return;
                }
                w.Gui = Make(machine, machine.Name, content, () => w.Gui = null);
                w.Gui.CanResize = MachineGuis.Resizable(machine);
            }
            Show(w.Gui, at);
        }

        public bool OpenEditor(IMachine machine)
        {
            var w = windows.GetValue(machine, _ => new Windows());
            if (w.Editor == null)
            {
                var editor = ReBuzz.Agent.MachineFaces.Editor(machine, () => OpenParameterWindow(machine, null));
                if (editor == null) return false;
                w.Editor = Make(machine, $"{machine.Name} — editor", editor, () => w.Editor = null);
                w.Editor.CanResize = false;
            }
            Show(w.Editor, null);
            return true;
        }

        public void CloseWindows(IMachine machine)
        {
            if (!windows.TryGetValue(machine, out var w)) return;
            w.Parameters?.Close();
            w.Gui?.Close();
            w.Editor?.Close();
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
                Content = NeoBuzz.Editor.MachineKeyboardFrame.Wrap(machine, content),
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
            };
            _ = new TransportKeys(window, () => machine.Graph?.Buzz as ReBuzzCore, () => false);
            window.Closed += (s, e) => closed();
            return window;
        }

        /// <summary>A machine window's own content (its GUI, parameter window or editor), without the keyboard under it.</summary>
        public static Control ContentOf(Window window) => NeoBuzz.Editor.MachineKeyboardFrame.ContentOf(window?.Content) as Control;

        /// <summary>The keyboard under a machine window's content, or null (for tests).
        public static NeoBuzz.Editor.MachineKeyboard KeyboardOf(Window window) => NeoBuzz.Editor.MachineKeyboardFrame.Of(window?.Content);

        static void Show(Window window, WindowPlacement? at)
        {
            if (at is WindowPlacement p && p.Width > 0)
            {
                window.SizeToContent = SizeToContent.Manual;
                window.Position = new PixelPoint((int)p.Left, (int)p.Top);
                window.Width = p.Width;
                // Songs keep the GUI's height; the keyboard comes on top of it.
                window.Height = p.Height + NeoBuzz.Editor.MachineKeyboardFrame.ExtraHeightOf(window.Content);
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
            window == null ? default : new WindowPlacement(window.Position.X, window.Position.Y, window.Bounds.Width,
                Math.Max(0, window.Bounds.Height - NeoBuzz.Editor.MachineKeyboardFrame.ExtraHeightOf(window.Content)));

        static Control Note(string text) => new TextBlock { Text = text, Margin = new Thickness(24), Foreground = Brushes.Gray };
    }
}
