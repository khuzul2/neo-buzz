#nullable disable
// Neo Exciter (docs/specs/mastering-tools.md §5.8): the harmonic exciter. DSP in Engine/, parameters generated from
// Params.def, editor in NeoExciterEditor.cs. Its latency is the oversampling's, constant; machine_readings gets the added level and the gain-match reading.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Exciter
{
    [MachineDecl(Name = "Neo Exciter", ShortName = "Exciter", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoExciterMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[ExciterEngine.MaxBlock], right = new float[ExciterEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoExciterMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new ExciterEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal ExciterEngine Engine { get; }

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
                // The delayed audio and the filters ring out for a moment after the input stops, then the machine rests.
                if (quietBlocks > 8 + ExciterEngine.Latency / Math.Max(1, n)) return false;
                quietBlocks++;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else
            {
                quietBlocks = 0;
                Split(input, left, right, n);
            }
            if (n > ExciterEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        public int GetLatency() => ExciterEngine.Latency;

        /// <summary>For agents (machine_readings): added_db over the last 3 s, gain_match_db, latency_samples.</summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Exciter...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Exciter — up to four bands, each through an oversampled, anti-aliased shaper (tape, tube, transistor, diode " +
                        "or warm); only the harmonics it makes are added back, by an Amount per band. The audio itself is " +
                        "never filtered.\n\n" +
                        "Part of neo-buzz (docs/specs/mastering-tools.md).", "Neo Exciter"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoExciterEditor();
    }
#endif
}
