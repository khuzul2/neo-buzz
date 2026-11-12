#nullable disable
// Neo Tape (docs/specs/mastering-tools.md §5.11): the tape machine. DSP in Engine/, parameters generated from
// Params.def, editor in NeoTapeEditor.cs. Its latency is the oversampling's (High) plus the modulated read's (with
// Wow or Flutter), reported; machine_readings gets the added level, the gain-match reading and the latency.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Tape
{
    [MachineDecl(Name = "Neo Tape", ShortName = "Tape", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoTapeMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[TapeEngine.MaxBlock], right = new float[TapeEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoTapeMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new TapeEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal TapeEngine Engine { get; }

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
                // The delayed audio rings out for the latency after the input stops, then the machine rests (unless the hiss is on).
                if (!Engine.HissOn && quietBlocks > 8 + Engine.Latency / Math.Max(1, n)) return false;
                quietBlocks++;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else
            {
                quietBlocks = 0;
                Split(input, left, right, n);
            }
            if (n > TapeEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        public int GetLatency() => Engine.Latency;

        /// <summary>For agents (machine_readings): added_db, gain_match_db, latency_samples.</summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Tape...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Tape — record pre-emphasis, tape saturation with bias (oversampled, anti-aliased), the playback de-emphasis, " +
                        "head bump and high-frequency loss by speed, an even-harmonic electronics stage, hiss, wow and flutter.\n\n" +
                        "Part of neo-buzz (docs/specs/mastering-tools.md).", "Neo Tape"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoTapeEditor();
    }
#endif
}
