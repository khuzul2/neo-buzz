#nullable disable
// Neo De-esser (docs/specs/mastering-tools.md §5.12): the de-esser. DSP in Engine/, parameters generated from
// Params.def, editor in NeoDeEsserEditor.cs. Its latency is the lookahead (plus 255 in Split linear), reported;
// machine_readings gets the reduction, the gain-match reading and the latency.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.DeEsser
{
    [MachineDecl(Name = "Neo De-esser", ShortName = "DeEsser", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoDeEsserMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[DeEsserEngine.MaxBlock], right = new float[DeEsserEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoDeEsserMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new DeEsserEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal DeEsserEngine Engine { get; }

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
            if (n > DeEsserEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        public int GetLatency() => Engine.Latency;

        /// <summary>For agents (machine_readings): reduction_db, gain_match_db, latency_samples.</summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo De-esser...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo De-esser — a band-pass detector on the sibilance band, relative to the whole signal (vocals) or absolute; " +
                        "the reduction on a split band (minimum or linear phase) or as a dynamic shelf or bell, with lookahead, Listen " +
                        "and Delta.\n\n" +
                        "Part of neo-buzz (docs/specs/mastering-tools.md).", "Neo De-esser"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoDeEsserEditor();
    }
#endif
}
