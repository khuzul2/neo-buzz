#nullable disable
// Neo Clipper (docs/specs/mastering-tools.md §5.1): the clipper machine. DSP in Engine/, parameters
// generated from Params.def, editor in NeoClipperEditor.cs. Its latency is the same at every setting and
// is reported to NeoBuzz Live; machine_readings gets the gain-match reading and how hard it clips.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Clipper
{
    [MachineDecl(Name = "Neo Clipper", ShortName = "Clipper", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoClipperMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[ClipperEngine.MaxBlock], right = new float[ClipperEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoClipperMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new ClipperEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal ClipperEngine Engine { get; }

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
            if (n > ClipperEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        /// <summary>Samples ReBuzz compensates: the same at every setting.</summary>
        public int GetLatency() => Engine.Latency;

        /// <summary>
        /// For agents (machine_readings, docs/specs/mcp-server.md §3.5), over the last 3 s: gain_match_db (short-term
        /// loudness out − in), clip_db (how far over the ceiling), clipped_percent and latency_samples. Safe from any thread.
        /// </summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Clipper...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Clipper — hard, soft, tanh or knee clipping into a ceiling, oversampled with " +
                        "antiderivative anti-aliasing, in up to three bands, left/right or mid/side. No sample " +
                        "leaves above the ceiling at Mix 100 %.\n\n" +
                        "Part of neo-buzz (docs/specs/mastering-tools.md).", "Neo Clipper"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoClipperEditor();
    }
#endif
}
