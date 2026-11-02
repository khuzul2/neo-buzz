using System;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core
{
    /// <summary>
    /// Machine windows (engine-split.md S3): the parameter window, a machine's own windowed GUI
    /// and the rename prompt. The engine asks; the WPF app (WpfMachineUiHost) shows them. With no
    /// host the engine only remembers which windows a song wants open, so saving keeps them.
    /// </summary>
    public interface IMachineUiHost
    {
        /// <summary>The machine view's window exists (engine start-up, step 2).</summary>
        void AttachMachineView(IntPtr machineViewHwnd);

        /// <summary>Opens (or shows again) the machine's parameter window, at <paramref name="at"/> if given.</summary>
        void OpenParameterWindow(IMachine machine, WindowPlacement? at);

        /// <summary>Opens (or shows again) the machine's windowed GUI, at <paramref name="at"/> if given.</summary>
        void OpenGui(IMachine machine, WindowPlacement? at);

        void CloseWindows(IMachine machine);

        /// <summary>Which of the machine's windows are open, and where (saved in BMX and BMXML songs).</summary>
        MachineWindowState GetWindowState(IMachine machine);

        /// <summary>The machine is being deleted: its windows let go of it.</summary>
        void ReleaseMachine(IMachine machine);

        /// <summary>Asks for a name near screen point (x, y); null if the user cancels.</summary>
        string AskName(string title, string current, int x, int y);
    }

    /// <summary>A window's position and size, in WPF units (as songs save them).</summary>
    public readonly record struct WindowPlacement(double Left, double Top, double Width, double Height);

    /// <summary>A machine's windows: which are open, and where.</summary>
    public readonly record struct MachineWindowState(
        bool ParameterWindowVisible, WindowPlacement ParameterWindow,
        bool GuiWindowVisible, WindowPlacement GuiWindow);
}
