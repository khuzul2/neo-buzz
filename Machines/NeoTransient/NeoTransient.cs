#nullable disable
// Neo Transient (docs/specs/mastering-tools.md §5.4): the transient shaper. DSP in Engine/, parameters generated
// from Params.def, editor in NeoTransientEditor.cs. Its latency is the lookahead and is reported to NeoBuzz Live;
// machine_readings gets the gain-match reading and the gains it applies.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Transient
{
    [MachineDecl(Name = "Neo Transient", ShortName = "Transient", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoTransientMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[TransientEngine.MaxBlock], right = new float[TransientEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoTransientMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new TransientEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal TransientEngine Engine { get; }

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
            if (n > TransientEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        /// <summary>Samples ReBuzz compensates: the lookahead.</summary>
        public int GetLatency() => Engine.Latency;

        /// <summary>
        /// For agents (machine_readings, docs/specs/mcp-server.md §3.5), over the last 3 s: gain_match_db (short-term
        /// loudness out − in), gain_max_db, gain_min_db, transient_percent and latency_samples. Safe from any thread.
        /// </summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Transient...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Transient — Attack and Sustain gains on the onsets and bodies of what comes in, " +
                        "found by two followers of its level, with a lookahead; or Impact mode: four bands, each " +
                        "with its microdynamics moved by one knob. Left/right or mid/side.\n\n" +
                        "Part of neo-buzz (docs/specs/mastering-tools.md).", "Neo Transient"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoTransientEditor();
    }
#endif
}
