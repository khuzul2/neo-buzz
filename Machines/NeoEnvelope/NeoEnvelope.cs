#nullable disable
// Neo Envelope (docs/specs/neo-patch.md §3): a canvas modulator. A control machine: Work() runs once per block and writes up to 8 targets
// through the host (NeoBuzz.Machine/NeoModulator.cs). The envelope is the engine's (Engine/EnvelopeEngine.cs); parameters are generated from
// Params.def, with each control's Explain text; the editor is NeoEnvelopeEditor.cs.

using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Envelope
{
    [MachineDecl(Name = "Neo Envelope", ShortName = "Env", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 0)]
    public partial class NeoEnvelopeMachine : NeoModulatorBase, IBuzzMachine
    {
        readonly EnvelopeEngine engine;

        public NeoEnvelopeMachine(IBuzzMachineHost host) : base(host, Params.Table, "Envelope")
        {
            engine = new EnvelopeEngine(Values);
        }

        internal EnvelopeEngine Engine => engine;

        /// <summary>ReBuzz: the control machine's block.</summary>
        public void Work() => Run();

        protected override float Advance(int samples, double dt) {
            engine.SetSampleRate(SampleRate);
            return engine.Step(samples, dt, engine.Following ? ListenedLevel() : 0f);
        }

        protected override bool WantsLevel => engine.Following;

        protected override double TargetGlide => engine.Glide;

        protected override void NoteOn() { if (!engine.Following) engine.NoteOn(); }

        protected override void NoteOff() { if (!engine.Following) engine.NoteOff(); }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Envelope...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Neo Envelope \u2014 an envelope for any parameter of any machine. Note mode: an ADSR started by the notes of a chosen " +
                        "machine (and track) or by its Gate switch. Follower mode: follows the level of a chosen machine's output, with attack, " +
                        "release and a threshold. Up to 8 targets, each with its own depth, polarity, range and curve; writes are smoothed, are not " +
                        "undo steps, and the target's own value stays the base. Safe ranges are kept.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-patch.md).", "Neo LFO"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoEnvelopeEditor();
    }
#endif
}
