#nullable disable
// Neo Random (docs/specs/neo-random.md): a canvas modulator that moves up to 8 parameters of other machines
// with random steps, sample-and-hold, 1/f-like drift or chaotic Lorenz trajectories. Reuses NeoModulatorBase
// (NeoBuzz.Machine/NeoModulator.cs) and ModulatorEditor (NeoModulator/ModulatorEditor.cs).

using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.RandomMachine
{
    [MachineDecl(Name = "Neo Random", ShortName = "Rnd", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 0)]
    public partial class NeoRandomMachine : NeoModulatorBase, IBuzzMachine
    {
        readonly RandomEngine engine;

        public NeoRandomMachine(IBuzzMachineHost host) : base(host, Params.Table, "RND")
        {
            engine = new RandomEngine(Values);
        }

        internal RandomEngine Engine => engine;

        /// <summary>ReBuzz: the control machine's block.</summary>
        public void Work() => Run();

        protected override float Advance(int samples, double dt) => engine.Step(dt, Beats, Playing, BeatsPerMinute);

        protected override double TargetGlide => engine.Glide;

        protected override void NoteOn() => engine.Note();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Random...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Neo Random — a stochastic and chaotic control modulator for any parameter of any machine: Random steps or curves, " +
                        "Sample and hold with probability, bounded 1/f-like Drift, and Lorenz Chaos. Free rate in Hz or tempo-synced " +
                        "(8 bars to 1/64, straight, dotted or triplet), smoothing, depth, signed bias, unipolar/bipolar range, quantisation " +
                        "(1 to 32 levels), reproducible seeds (0 = fresh each run) and beat, bar or note retriggering. Up to 8 targets " +
                        "with individual depth, polarity, range and curve; writes are smoothed, are not undo steps, and keep safe ranges.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-random.md).", "Neo Random"),
                },
            },
        };
    }

#if AVALONIA   // fork: the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoRandomEditor();
    }
#endif
}
