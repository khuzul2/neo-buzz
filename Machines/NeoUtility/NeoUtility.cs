#nullable disable
// Neo Utility (docs/specs/machines.md §5): gain, pan or balance, width, bass mono, polarity, swap
// and DC removal. DSP in Engine/, parameters generated from Params.def, editor in
// NeoUtilityEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Utility
{
    [MachineDecl(Name = "Neo Utility", ShortName = "Utility", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoUtilityMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[UtilityEngine.MaxBlock], right = new float[UtilityEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoUtilityMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new UtilityEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal UtilityEngine Engine { get; }

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
                // Run until the DC and bass mono filters have settled, then rest.
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
            if (n > UtilityEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        /// <summary>No latency: every stage works sample by sample.</summary>
        public int GetLatency() => Engine.Latency;

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Utility...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => System.Windows.MessageBox.Show(
                        "Neo Utility — gain in 0.1 dB steps, balance or mono pan, width, bass mono, polarity, " +
                        "channel swap and DC removal, each exact. At its defaults it passes the input bit for bit.\n\n" +
                        "Part of neo-buzz (docs/specs/machines.md).", "Neo Utility"),
                },
            },
        };
    }

    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoUtilityEditor();
    }
}
