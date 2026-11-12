#nullable disable
// Neo Imager (docs/specs/mastering-tools.md §5.7): the stereo imager. DSP in Engine/, parameters generated from
// Params.def, editor in NeoImagerEditor.cs. No latency; machine_readings gets the per-band width and correlation.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Imager
{
    [MachineDecl(Name = "Neo Imager", ShortName = "Imager", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoImagerMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[ImagerEngine.MaxBlock], right = new float[ImagerEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoImagerMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new ImagerEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal ImagerEngine Engine { get; }

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
                // The bands ring for a moment after the input stops, then the machine rests.
                if (quietBlocks > 8) return false;
                quietBlocks++;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else
            {
                quietBlocks = 0;
                Split(input, left, right, n);
            }
            if (n > ImagerEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        public int GetLatency() => 0;

        /// <summary>For agents (machine_readings): per band width_db_N and correlation_N over the last 3 s, gain_match_db, latency_samples.</summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Imager...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Imager — four bands, the width of each from 0 to 200 %, Stereoize (a decorrelated copy of the " +
                        "mid added to the side, mono-safe) and Recover sides (what narrowing takes away returns to the mid " +
                        "without combing). No latency.\n\n" +
                        "Part of neo-buzz (docs/specs/mastering-tools.md).", "Neo Imager"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoImagerEditor();
    }
#endif
}
