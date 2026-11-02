#nullable disable
// Neo EQ (docs/specs/machines.md §5): the eight-band parametric equaliser machine. DSP in Engine/,
// parameters generated from Params.def, editor in NeoEqEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Eq
{
    [MachineDecl(Name = "Neo EQ", ShortName = "EQ", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoEqMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[EqEngine.MaxBlock], right = new float[EqEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoEqMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new EqEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal EqEngine Engine { get; }

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
                // Run until resonant bands have rung out (a Q 30 bell at 20 Hz takes a while), then rest.
                if (quietBlocks > 2 * rate / Math.Max(1, n)) return false;
                quietBlocks++;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else
            {
                quietBlocks = 0;
                Split(input, left, right, n);
            }
            if (n > EqEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        /// <summary>Minimum phase: no latency.</summary>
        public int GetLatency() => Engine.Latency;

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo EQ...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo EQ — eight-band parametric equaliser: bell, shelves, cuts at 12/24/48 dB/oct and notch, " +
                        "minimum phase, zero latency. Drag a band's handle for frequency and gain, the wheel for Q, " +
                        "double-click to switch it.\n\nPart of neo-buzz (docs/specs/machines.md).", "Neo EQ"),
                },
            },
        };
    }

#if WINDOWS   // fork (mac-engine.md M2): the editor is WPF; portable builds have none
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoEqEditor();
    }
#endif
}
