#nullable disable
// Neo Lo-Fi (docs/specs/neo-lofi.md): the lo-fi machine. DSP in Engine/, parameters
// generated from Params.def, editor in NeoLoFiEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.LoFi
{
    [MachineDecl(Name = "Neo Lo-Fi", ShortName = "LoFi", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoLoFiMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[LoFiEngine.MaxBlock], right = new float[LoFiEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoLoFiMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new LoFiEngine(Values);
        }

        internal LoFiEngine Engine { get; }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && HasSignal(input, n);
            if (!hasInput)
            {
                // Run until the motion's delay (up to 150 ms at 200 %) and the filters have flushed, then rest (unless hiss or crackle is on).
                if (!Engine.MakesSound && quietBlocks > 4 + (int)(0.16 * rate / Math.Max(1, n))) return false;
                quietBlocks++;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else
            {
                quietBlocks = 0;
                Split(input, left, right, n);
            }
            for (int pos = 0; pos < n; pos += LoFiEngine.MaxBlock)
            {
                int m = Math.Min(LoFiEngine.MaxBlock, n - pos);
                if (pos > 0) throw new InvalidOperationException("block larger than 256");
                Engine.Process(left, right, m);
            }
            return Join(left, right, output, n) || hasInput;
        }

        /// <summary>Samples of delay ReBuzz compensates: none, at every setting.</summary>
        public int GetLatency() => LoFiEngine.Latency;

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Lo-Fi...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Lo-Fi — bit and sample-rate reduction (with an optional anti-alias filter), hiss, vinyl crackle, " +
                        "light wow, flutter and drift, a tone control; zero latency.\n\nPart of neo-buzz (docs/specs/neo-lofi.md).", "Neo Lo-Fi"),
                },
            },
        };
    }

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoLoFiEditor();
    }
#endif
}
