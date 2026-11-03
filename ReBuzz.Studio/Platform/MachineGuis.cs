// Fork (docs/specs/studio-mvp.md S1, §3.4): a machine's own GUI in the studio: what its GUI
// factory makes, when that is an Avalonia control (the Neo machines' studio flavour, with their
// editors on NeoBuzz.Editor.Avalonia). Other machines have none here (their GUIs are WPF): they
// get their parameter window. StudioMachineUiHost.GuiContent calls this.

using System;
using BuzzGUI.Interfaces;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Platform
{
    // Inside the namespace: the engine's ReBuzz.Control namespace would hide Avalonia's Control.
    using Control = Avalonia.Controls.Control;

    internal static class MachineGuis
    {
        /// <summary>The machine's GUI, ready to show, or null when it has none here.</summary>
        public static Control Create(IMachine machine)
        {
            IMachineGUIFactory factory;
            try
            {
                factory = machine?.DLL?.GUIFactory;
            }
            catch (Exception e)
            {
                StudioHost.Current?.Log($"{machine?.Name}: no GUI ({e.Message})");
                return null;
            }
            if (factory == null) return null;
            try
            {
                var gui = factory.CreateGUI(new GuiHost(machine));
                if (gui is not Control control) return null;
                gui.Machine = machine;
                return control;
            }
            catch (Exception e)
            {
                // A WPF GUI can't load here (no WPF): the parameter window stands in.
                StudioHost.Current?.Log($"{machine.Name}: its GUI doesn't run here ({e.GetType().Name}: {e.Message})");
                return null;
            }
        }

        /// <summary>Whether the machine's GUI window may be resized, as its factory declares.</summary>
        public static bool Resizable(IMachine machine) => machine?.DLL?.GUIFactoryDecl?.IsGUIResizable ?? true;

        /// <summary>What a GUI may ask of its window: its actions go on the song's undo history.</summary>
        sealed class GuiHost : IMachineGUIHost
        {
            readonly IMachine machine;

            public GuiHost(IMachine machine)
            {
                this.machine = machine;
            }

            public void DoAction(IAction a) => machine.Graph?.Do(a);
        }
    }
}
