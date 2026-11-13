#nullable disable
// Neo Macro (docs/specs/neo-patch.md §3): a canvas modulator. A control machine: Work() runs once per block and writes up to 8 targets
// through the host (NeoBuzz.Machine/NeoModulator.cs). Parameters are generated from
// Params.def, with each control's Explain text; the editor is NeoMacroEditor.cs.

using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Macro
{
    [MachineDecl(Name = "Neo Macro", ShortName = "Macro", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 0)]
    public partial class NeoMacroMachine : NeoModulatorBase, IBuzzMachine
    {
        public NeoMacroMachine(IBuzzMachineHost host) : base(host, Params.Table, "Macro")
        {
        }

        /// <summary>ReBuzz: the control machine's block.</summary>
        public void Work() => Run();

        protected override float Advance(int samples, double dt) => Values.F(P.Value);

        protected override double TargetGlide => Values.Value(P.Glide);

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Macro...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Neo Macro \u2014 one knob that moves up to 8 parameters of other machines at once, each with its own depth, polarity, " +
                        "range and curve. The Value is an ordinary parameter: patterns, the agent and a bound MIDI controller drive it. Writes are " +
                        "smoothed, are not undo steps, and the targets' own values stay the base. Safe ranges are kept.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-patch.md).", "Neo LFO"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoMacroEditor();
    }
#endif
}
