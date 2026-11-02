#nullable disable
// Neo Limiter (docs/specs/machines.md §5): the true-peak lookahead limiter machine. DSP in
// Engine/, parameters generated from Params.def, editor in NeoLimiterEditor.cs. Its latency (the
// lookahead and the true-peak interpolator) is reported to ReBuzz and follows the Lookahead.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Limiter
{
    [MachineDecl(Name = "Neo Limiter", ShortName = "Limiter", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoLimiterMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[LimiterEngine.MaxBlock], right = new float[LimiterEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoLimiterMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new LimiterEngine(Values);
            // At the host's rate from the start: the latency ReBuzz asks for depends on it.
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal LimiterEngine Engine { get; }

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
                // Run until the delayed audio has come out and the gain has recovered, then rest.
                if (quietBlocks > 8 + Engine.Latency / Math.Max(1, n)) return false;
                quietBlocks++;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else
            {
                quietBlocks = 0;
                Split(input, left, right, n);
            }
            if (n > LimiterEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        /// <summary>Samples of delay ReBuzz compensates: the lookahead and the true-peak interpolator.</summary>
        public int GetLatency() => Engine.Latency;

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Limiter...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Limiter — true-peak lookahead limiter: the output's 4× oversampled peak (as BS.1770 " +
                        "measures it) stays under the ceiling. Auto or set release, stereo link.\n\n" +
                        "Part of neo-buzz (docs/specs/machines.md).", "Neo Limiter"),
                },
            },
        };
    }

#if WINDOWS   // fork (mac-engine.md M2): the editor is WPF; portable builds have none
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoLimiterEditor();
    }
#endif
}
