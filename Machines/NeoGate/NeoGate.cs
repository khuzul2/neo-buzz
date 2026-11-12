#nullable disable
// Neo Gate (docs/specs/mastering-tools.md §5.13): the successor of Pedal Gate. Two inputs: 1 is the signal, 2 the
// external sidechain (connect a machine to the second input). DSP in Engine/, parameters generated from Params.def,
// editor in NeoGateEditor.cs. Its latency is the lookahead, reported.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Gate
{
    [MachineDecl(Name = "Neo Gate", ShortName = "Gate", Author = "neo-buzz", MaxTracks = 0, InputCount = 2, OutputCount = 1)]
    public partial class NeoGateMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[GateEngine.MaxBlock], right = new float[GateEngine.MaxBlock];
        readonly float[] keyLeft = new float[GateEngine.MaxBlock], keyRight = new float[GateEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoGateMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new GateEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal GateEngine Engine { get; }

        public bool Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            var main = input != null && input.Count > 0 ? input[0] : null;
            var key = input != null && input.Count > 1 ? input[1] : null;
            var outBuf = output != null && output.Count > 0 ? output[0] : null;
            n = Math.Min(n, GateEngine.MaxBlock);

            bool hasInput = main != null && HasSignal(main, n);
            bool hasKey = key != null && HasSignal(key, n);
            if (!hasInput && !hasKey)
            {
                // Run until the lookahead has flushed and the gain has settled, then rest.
                if (quietBlocks > 8 + Engine.Latency / Math.Max(1, n)) return false;
                quietBlocks++;
            }
            else quietBlocks = 0;

            if (main != null) Split(main, left, right, n);
            else
            {
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            if (key != null) Split(key, keyLeft, keyRight, n);
            Engine.Process(left, right, key != null ? keyLeft : null, key != null ? keyRight : null, n);
            if (outBuf == null) return false;
            return Join(left, right, outBuf, n) || hasInput;
        }

        /// <summary>Samples of delay ReBuzz compensates: the lookahead.</summary>
        public int GetLatency() => Engine.Latency;

        public string GetChannelName(bool input, int index) => input ? (index == 0 ? "Input" : "Sidechain") : "Output";

        /// <summary>For agents (machine_readings): gain_db, open, gain_match_db, latency_samples.</summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Gate...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Gate — noise gate and downward expander, the successor of Pedal Gate: threshold with hysteresis, attack, hold " +
                        "and release, a Range floor, an expansion ratio, a key filter, external sidechain (input 2) and lookahead.\n\n" +
                        "Part of neo-buzz (docs/specs/mastering-tools.md).", "Neo Gate"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoGateEditor();
    }
#endif
}
