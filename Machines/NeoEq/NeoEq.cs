#nullable disable
// Neo EQ (docs/specs/machines.md §5): the eight-band parametric equaliser machine, with a 31-band graphic
// mode (machine-safety.md §4). DSP in Engine/,
// parameters generated from Params.def, editor in NeoEqEditor.cs.
// v2 (docs/specs/mastering-tools.md §5.3): two inputs, 1 the signal, 2 the sidechain a dynamic band's detector can
// hear (connect a machine to the second input); the readings (gain match, the dynamic bands' changes).

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Eq
{
    [MachineDecl(Name = "Neo EQ", ShortName = "EQ", Author = "neo-buzz", MaxTracks = 0, InputCount = 2, OutputCount = 1)]
    public partial class NeoEqMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[EqEngine.MaxBlock], right = new float[EqEngine.MaxBlock];
        readonly float[] keyLeft = new float[EqEngine.MaxBlock], keyRight = new float[EqEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoEqMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new EqEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal EqEngine Engine { get; }

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
            // The host calls a two-input machine with WM_WRITE alone (MachineWorkInstance): an input is there when it has sound.
            bool hasInput = main != null && HasSignal(main, n);
            if (!hasInput)
            {
                // Run until resonant bands have rung out (a Q 30 bell at 20 Hz takes a while), then rest.
                if (quietBlocks > (2 * rate + Engine.Latency) / Math.Max(1, n)) return false;
                quietBlocks++;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else
            {
                quietBlocks = 0;
                Split(main, left, right, n);
            }
            if (n > EqEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            if (key != null) Split(key, keyLeft, keyRight, n);
            Engine.Process(left, right, key != null ? keyLeft : null, key != null ? keyRight : null, n);
            if (outBuf == null) return false;
            bool active = Join(left, right, outBuf, n) || hasInput;
            // As the host did for v1's one-input Work returning false (quiet in, under −120 dB out): the input passes as it
            // is. The two-input host keeps whatever the buffer holds, so the machine copies it: v1's renders stay bit for bit.
            if (!active)
            {
                if (main != null) Array.Copy(main, outBuf, n);
                else Array.Clear(outBuf, 0, n);
            }
            return active;
        }

        /// <summary>Minimum phase: no latency.</summary>
        public int GetLatency() => Engine.Latency;

        public string GetChannelName(bool input, int index) => input ? (index == 0 ? "Input" : "Sidechain") : "Output";

        /// <summary>What the EQ measures (machine_readings): the gain match, each band's dynamic change.</summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

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
                        "double-click to switch it.\n\nMode Graphic: a 31-band graphic equaliser at the ISO third-octave " +
                        "centres, constant-Q, whose response passes through its sliders (±18 dB). Drag a slider, or draw " +
                        "across them.\n\nv2: each band on Stereo, Left, Right, Mid or Side; proportional-Q bells, Baxandall " +
                        "and Vintage shelves, all-pass bands; dynamic bands that move their gain down or up above a " +
                        "threshold, hearing their own band or the sidechain (input 2); Delta and Match.\n\nPart of " +
                        "neo-buzz (docs/specs/machines.md).", "Neo EQ"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoEqEditor();
    }
#endif
}
