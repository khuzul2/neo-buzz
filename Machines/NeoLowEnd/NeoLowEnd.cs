#nullable disable
// Neo Low End (docs/specs/mastering-tools.md §5.5): the low-end shaper. DSP in Engine/, parameters generated from
// Params.def, editor in NeoLowEndEditor.cs. Its latency is 0 in minimum phase and the FIR's in linear phase, reported
// to NeoBuzz Live; machine_readings gets the low band's crest in and out, the balance and the gain-match reading.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.LowEnd
{
    [MachineDecl(Name = "Neo Low End", ShortName = "LowEnd", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoLowEndMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[LowEndEngine.MaxBlock], right = new float[LowEndEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoLowEndMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new LowEndEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal LowEndEngine Engine { get; }

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
                // Run until the delayed audio has come out, then rest.
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
            if (n > LowEndEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        /// <summary>Samples ReBuzz compensates: 0 in minimum phase, the linear-phase FIR's in linear.</summary>
        public int GetLatency() => Engine.Latency;

        /// <summary>
        /// For agents (machine_readings, docs/specs/mcp-server.md §3.5), over the last 3 s: gain_match_db (short-term loudness
        /// out − in), low_crest_in_db and low_crest_out_db (the low band's peak over RMS), balance_db, balance_gain_db,
        /// peak_clip_db and latency_samples. Safe from any thread.
        /// </summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Low End...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Low End — the band below a crossover (20–300 Hz, Linkwitz–Riley 24, minimum or linear " +
                        "phase) shaped on its own: Punch and Sustain, Contrast, Balance against the mids, Peak " +
                        "control and mono below a frequency. Everything above the crossover is left alone.\n\n" +
                        "Part of neo-buzz (docs/specs/mastering-tools.md).", "Neo Low End"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoLowEndEditor();
    }
#endif
}
