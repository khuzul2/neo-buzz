#nullable disable
// Neo Ensemble (docs/specs/machines.md §5): chorus, flanger and phaser in one, its feedback bounded
// so it can't run away (docs/specs/machine-safety.md §4). DSP in Engine/, parameters generated from
// Params.def, editor in NeoEnsembleEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Ensemble
{
    [MachineDecl(Name = "Neo Ensemble", ShortName = "Ensemble", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoEnsembleMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[EnsembleEngine.MaxBlock], right = new float[EnsembleEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoEnsembleMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new EnsembleEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal EnsembleEngine Engine { get; }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            n = Math.Min(n, EnsembleEngine.MaxBlock);
            Engine.Tempo = BeatsPerMinute;
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && input != null && HasSignal(input, n);
            if (hasInput) Split(input, left, right, n);
            else
            {
                if (quietBlocks > 16) return false;   // the feedback's tail has died away
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            Engine.Process(left, right, n);
            bool sound = Join(left, right, output, n);
            quietBlocks = sound || hasInput ? 0 : quietBlocks + 1;
            return sound || hasInput;
        }

        /// <summary>No latency: the dry signal is never delayed.</summary>
        public int GetLatency() => Engine.Latency;

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Ensemble...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Ensemble — chorus (1–4 voices), flanger and phaser (2–12 stages) in one, with tempo sync, " +
                        "stereo spread and a tone control. Feedback reaches ±99 % through a saturating loop whose gain " +
                        "stays below 1 at every frequency, so it can't run away. At Mix 0 % it passes the input bit for bit.\n\n" +
                        "Part of neo-buzz (docs/specs/machines.md).", "Neo Ensemble"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoEnsembleEditor();
    }
#endif
}
