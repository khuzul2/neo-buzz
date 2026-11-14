#nullable disable
// Neo LFO (docs/specs/neo-patch.md §3): a canvas modulator. A control machine: Work() runs once per block and writes up to 8 targets
// through the host (NeoBuzz.Machine/NeoModulator.cs). The wave is the engine's (Engine/LfoEngine.cs); parameters are generated from
// Params.def, with each control's Explain text; the editor is NeoLfoEditor.cs.

using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.LfoMachine
{
    [MachineDecl(Name = "Neo LFO", ShortName = "LFO", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 0)]
    public partial class NeoLfoMachine : NeoModulatorBase, IBuzzMachine
    {
        readonly LfoEngine engine;

        public NeoLfoMachine(IBuzzMachineHost host) : base(host, Params.Table, "LFO")
        {
            engine = new LfoEngine(Values);
        }

        internal LfoEngine Engine => engine;

        /// <summary>ReBuzz: the control machine's block.</summary>
        public void Work() => Run();

        protected override float Advance(int samples, double dt) => engine.Step(dt, Beats, Playing, BeatsPerMinute);

        protected override double TargetGlide => engine.Glide;

        protected override void NoteOn() => engine.Note();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo LFO...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Neo LFO — a low-frequency oscillator for any parameter of any machine: seven shapes, a rate in Hz or locked to the " +
                        "tempo (8 bars to 1/64, straight, dotted or triplet), a phase, retrigger on the beat, the bar or a note, and smoothing. " +
                        "Up to 8 targets, each with its own depth, polarity, range and curve; writes are smoothed, are not undo steps, and the " +
                        "target's own value stays the base (removing the LFO puts it back). Safe ranges are kept.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-patch.md).", "Neo LFO"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoLfoEditor();
    }
#endif
}
