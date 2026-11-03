#nullable disable
// Neo Drive (docs/specs/machines.md §5): the saturator machine. DSP in Engine/, parameters
// generated from Params.def, editor in NeoDriveEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Drive
{
    [MachineDecl(Name = "Neo Drive", ShortName = "Drive", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoDriveMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[DriveEngine.MaxBlock], right = new float[DriveEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoDriveMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new DriveEngine(Values);
        }

        internal DriveEngine Engine { get; }

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
                // Run until the latency and filter tails have flushed, then rest.
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
            for (int pos = 0; pos < n; pos += DriveEngine.MaxBlock)
            {
                int m = Math.Min(DriveEngine.MaxBlock, n - pos);
                if (pos > 0) throw new InvalidOperationException("block larger than 256");
                Engine.Process(left, right, m);
            }
            return Join(left, right, output, n) || hasInput;
        }

        /// <summary>Samples of delay ReBuzz compensates (constant at every quality).</summary>
        public int GetLatency() => DriveEngine.Latency;

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Drive...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Drive — saturator: tape, tube, transistor, diode and fold, oversampled with " +
                        "anti-derivative anti-aliasing.\n\nPart of neo-buzz (docs/specs/machines.md).", "Neo Drive"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoDriveEditor();
    }
#endif
}
