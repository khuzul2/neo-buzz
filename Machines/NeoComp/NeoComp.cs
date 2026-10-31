#nullable disable
// Neo Comp (docs/specs/machines.md §5): the compressor machine. Two inputs: 1 is the signal,
// 2 the external sidechain (connect a machine to the second input). DSP in Engine/, parameters
// generated from Params.def, editor in NeoCompEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Comp
{
    [MachineDecl(Name = "Neo Comp", ShortName = "Comp", Author = "neo-buzz", MaxTracks = 0, InputCount = 2, OutputCount = 1)]
    public partial class NeoCompMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[CompEngine.MaxBlock], right = new float[CompEngine.MaxBlock];
        readonly float[] keyLeft = new float[CompEngine.MaxBlock], keyRight = new float[CompEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoCompMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new CompEngine(Values);
        }

        internal CompEngine Engine { get; }

        public bool Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            var main = input != null && input.Count > 0 ? input[0] : null;
            var key = input != null && input.Count > 1 ? input[1] : null;
            var outBuf = output != null && output.Count > 0 ? output[0] : null;
            n = Math.Min(n, CompEngine.MaxBlock);

            bool hasInput = main != null && HasSignal(main, n);
            bool hasKey = key != null && HasSignal(key, n);
            if (!hasInput && !hasKey)
            {
                // Run until the lookahead has flushed and the gain has recovered, then rest.
                if (quietBlocks > 8) return false;
                quietBlocks++;
            }
            else quietBlocks = 0;

            if (main != null) Split(main, left, right, n);
            else
            {
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            if (key != null) Split(key, keyLeft, keyRight, n);
            Engine.Process(left, right, key != null ? keyLeft : null, key != null ? keyRight : null, n);
            if (outBuf == null) return false;
            return Join(left, right, outBuf, n) || hasInput;
        }

        /// <summary>Samples of delay ReBuzz compensates: the lookahead.</summary>
        public int GetLatency() => Engine.Latency;

        public string GetChannelName(bool input, int index) => input ? (index == 0 ? "Input" : "Sidechain") : "Output";

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Comp...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => System.Windows.MessageBox.Show(
                        "Neo Comp — compressor: clean VCA, glue bus, FET and opto, with soft knee, lookahead, " +
                        "sidechain low cut, external sidechain (input 2) and parallel mix.\n\n" +
                        "Part of neo-buzz (docs/specs/machines.md).", "Neo Comp"),
                },
            },
        };
    }

    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoCompEditor();
    }
}
