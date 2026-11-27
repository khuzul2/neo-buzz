#nullable disable
// Tutorial Tremolo (docs/sdk/tutorial.md): the machine class. DSP in Engine/, parameters generated from Params.def,
// editor in TutorialTremoloEditor.cs. In a real machine this file is Machines/<Name>/<Name>.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.TutorialTremolo
{
    // snippet MachineClass
    [MachineDecl(Name = "Tutorial Tremolo", ShortName = "Tremolo", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class TutorialTremoloMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[TremoloEngine.MaxBlock], right = new float[TremoloEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public TutorialTremoloMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new TremoloEngine(Values);
        }

        internal TremoloEngine Engine { get; }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && input != null && HasSignal(input, n);
            if (!hasInput)
            {
                // A few quiet blocks of silence through the engine (Mix and the smoothers settle), then rest.
                if (quietBlocks > 4) return false;
                quietBlocks++;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else
            {
                quietBlocks = 0;
                Split(input, left, right, n);
            }
            if (n > TremoloEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        /// <summary>Samples of delay ReBuzz compensates: none.</summary>
        public int GetLatency() => Engine.Latency;
        // end snippet MachineClass

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Tutorial Tremolo...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Tutorial Tremolo — the machine of the SDK tutorial: a low-frequency oscillator moves the level, " +
                        "with a sine or triangle shape, a phase offset between the channels, Mix and Amount.\n\n" +
                        "Part of neo-buzz (docs/sdk/tutorial.md).", "Tutorial Tremolo"),
                },
            },
        };
    }

    // snippet MachineFactory
#if AVALONIA   // the editor: Avalonia in the studio; none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new TutorialTremoloEditor();
    }
#endif
    // end snippet MachineFactory
}
