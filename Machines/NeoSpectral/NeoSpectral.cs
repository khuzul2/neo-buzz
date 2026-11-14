#nullable disable
// Neo Spectral (docs/specs/mastering-tools.md §5.16): the resonance suppressor. DSP in Engine/, parameters generated from
// Params.def, editor in NeoSpectralEditor.cs. Its latency is the frame (2047 samples at 44.1 and 48 kHz), reported;
// machine_readings gets the deepest and the mean cut, the gain-match reading and the latency.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Spectral
{
    [MachineDecl(Name = "Neo Spectral", ShortName = "Spectral", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoSpectralMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[SpectralEngine.MaxBlock], right = new float[SpectralEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoSpectralMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new SpectralEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal SpectralEngine Engine { get; }

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
                // The delayed audio rings out for the latency after the input stops, then the machine rests.
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
            if (n > SpectralEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        public int GetLatency() => Engine.Latency;

        /// <summary>For agents (machine_readings): reduction_db, mean_reduction_db, gain_match_db, latency_samples.</summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Spectral...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Spectral — a dynamic resonance suppressor on a short-time spectrum: each frequency against the spectrum around it " +
                        "(Suppress), a pink slope (Balance) or its own long-term average (Stabilize); a cut for as long as it stands out, " +
                        "with Lift, Region, Mix and Delta. Latency one frame (43 ms).\n\n" +
                        "Part of neo-buzz (docs/specs/mastering-tools.md).", "Neo Spectral"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoSpectralEditor();
    }
#endif
}
