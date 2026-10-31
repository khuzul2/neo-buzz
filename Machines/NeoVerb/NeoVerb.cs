#nullable disable
// Neo Verb (docs/specs/machines.md §5): the reverb machine. DSP in Engine/, parameters generated
// from Params.def, editor in NeoVerbEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Verb
{
    [MachineDecl(Name = "Neo Verb", ShortName = "Verb", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoVerbMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[VerbEngine.MaxBlock], right = new float[VerbEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoVerbMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new VerbEngine(Values);
        }

        internal VerbEngine Engine { get; }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            n = Math.Min(n, VerbEngine.MaxBlock);
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && HasSignal(input, n);
            if (hasInput) Split(input, left, right, n);
            else
            {
                if (quietBlocks > 16) return false;   // the tail has died away
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            Engine.Process(left, right, n);
            bool sound = Join(left, right, output, n);
            quietBlocks = sound || hasInput ? 0 : quietBlocks + 1;
            return sound || hasInput;
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Verb...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => System.Windows.MessageBox.Show(
                        "Neo Verb — algorithmic reverb: hall, room, chamber and plate on a 16-line feedback " +
                        "delay network with modulation, exact decay per band, freeze and shimmer.\n\n" +
                        "Part of neo-buzz (docs/specs/machines.md).", "Neo Verb"),
                },
            },
        };
    }

    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoVerbEditor();
    }
}
