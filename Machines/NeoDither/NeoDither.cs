#nullable disable
// Neo Dither (docs/specs/mastering-tools.md §5.9): the word-length reducer. DSP in Engine/, parameters generated from
// Params.def, editor in NeoDitherEditor.cs. No latency.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Dither
{
    [MachineDecl(Name = "Neo Dither", ShortName = "Dither", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoDitherMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[DitherEngine.MaxBlock], right = new float[DitherEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoDitherMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new DitherEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal DitherEngine Engine { get; }

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
                // Silence in: the dither goes on (a track's tail ends in it) until the engine blanks itself, then the machine rests.
                if (Engine.Blanked) return false;
                quietBlocks++;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else
            {
                quietBlocks = 0;
                Split(input, left, right, n);
            }
            if (n > DitherEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        public int GetLatency() => 0;

        /// <summary>For agents (machine_readings): latency_samples (0).</summary>
        public IReadOnlyDictionary<string, double> Readings => new Dictionary<string, double> { ["latency_samples"] = 0 };

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Dither...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Dither — rounds the audio to a 16, 20 or 24 bit grid with TPDF dither and optional noise shaping; " +
                        "exactly on the grid, silent after 0.7 s of digital silence. Put it last, after the limiter.\n\n" +
                        "Part of neo-buzz (docs/specs/mastering-tools.md).", "Neo Dither"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoDitherEditor();
    }
#endif
}
