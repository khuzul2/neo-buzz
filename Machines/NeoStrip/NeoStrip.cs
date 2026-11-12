#nullable disable
// Neo Strip (docs/specs/mastering-tools.md §5.10): gate, EQ, compressor, drive, clipper and limiter in one machine. DSP in Engine/,
// parameters generated from Params.def, editor in NeoStripEditor.cs. Latency: the drive, clipper and limiter's, whichever stages are on.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Strip
{
    [MachineDecl(Name = "Neo Strip", ShortName = "Strip", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoStripMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[StripEngine.MaxBlock], right = new float[StripEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoStripMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new StripEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal StripEngine Engine { get; }

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
                // Run until the latency and the tails (compressor, limiter, filters) have flushed, then rest.
                if (quietBlocks > (Engine.Latency + 2 * rate) / Math.Max(1, n)) return false;
                quietBlocks++;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else
            {
                quietBlocks = 0;
                Split(input, left, right, n);
            }
            if (n > StripEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        /// <summary>The drive's, clipper's and limiter's, the same whichever stages are on.</summary>
        public int GetLatency() => Engine.Latency;

        /// <summary>For agents (machine_readings): the gain match, the gate's and compressor's gain now, the limiter's reduction, the latency.</summary>
        public IReadOnlyDictionary<string, double> Readings
        {
            get
            {
                var d = new Dictionary<string, double>();
                double m = Engine.GainMatchDb;
                if (double.IsFinite(m)) d["gain_match_db"] = Math.Round(m, 2);
                d["gate_db"] = Math.Round(Engine.GateDb, 2);
                d["comp_gr_db"] = Math.Round(Engine.CompReductionDb, 2);
                d["limiter_gr_db"] = Math.Round(Engine.GainReductionDb, 2);
                d["latency_samples"] = Engine.Latency;
                return d;
            }
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Strip...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Strip — a channel strip: gate, EQ (low cut, low shelf, two bells, high shelf, high cut), compressor, " +
                        "drive, clipper and true-peak limiter, each the engine of the Neo machine of that name. A stage that is off " +
                        "passes the audio untouched; the delay is the same whichever are on.\n\n" +
                        "Part of neo-buzz (docs/specs/mastering-tools.md).", "Neo Strip"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoStripEditor();
    }
#endif
}
