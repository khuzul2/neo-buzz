#nullable disable
// Neo Step (docs/specs/neo-patch.md §3): a canvas modulator. A control machine: Work() runs once per block and writes up to 8 targets
// through the host (NeoBuzz.Machine/NeoModulator.cs). The pattern is the engine's (Engine/StepEngine.cs); parameters are generated from
// Params.def, with each control's Explain text; the editor is NeoStepEditor.cs.

using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.StepMachine
{
    [MachineDecl(Name = "Neo Step", ShortName = "Step", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 0)]
    public partial class NeoStepMachine : NeoModulatorBase, IBuzzMachine
    {
        readonly StepEngine engine;

        public NeoStepMachine(IBuzzMachineHost host) : base(host, Params.Table, "Step")
        {
            engine = new StepEngine(Values);
        }

        internal StepEngine Engine => engine;

        /// <summary>ReBuzz: the control machine's block.</summary>
        public void Work() => Run();

        protected override float Advance(int samples, double dt) => engine.Step(dt, Beats, Playing, BeatsPerMinute);

        protected override double TargetGlide => engine.WriteGlide;

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Step...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Neo Step \u2014 a step sequencer for values: 1 to 32 steps of 0 to 100 %, a synced rate with swing, a glide between " +
                        "steps and a random amount. Up to 8 targets, each with its own depth, polarity, range and curve; writes are smoothed, are " +
                        "not undo steps, and the target's own value stays the base. Safe ranges are kept.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-patch.md).", "Neo LFO"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoStepEditor();
    }
#endif
}
