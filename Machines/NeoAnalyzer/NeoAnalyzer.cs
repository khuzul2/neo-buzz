#nullable disable
// Neo Analyzer (docs/specs/machines.md §5): the analyzer machine. Its output is its input, bit for
// bit; the engine (Engine/) measures what passes and publishes snapshots for the editor
// (NeoAnalyzerEditor.cs) and readings for the agent server (Readings, docs/specs/mcp-server.md
// §3.5). Parameters are generated from Params.def.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Analyzer
{
    [MachineDecl(Name = "Neo Analyzer", ShortName = "Analyzer", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoAnalyzerMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[AnalyzerEngine.MaxBlock], right = new float[AnalyzerEngine.MaxBlock];
        int rate;

        public NeoAnalyzerMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new AnalyzerEngine(Values);
        }

        internal AnalyzerEngine Engine { get; }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            bool read = (mode & WorkModes.WM_READ) != 0 && input != null;
            bool sound = false;
            const float scale = 1f / DspMath.BuzzScale;
            for (int at = 0; at < n; at += AnalyzerEngine.MaxBlock)
            {
                int m = Math.Min(AnalyzerEngine.MaxBlock, n - at);
                if (read)
                {
                    for (int i = 0; i < m; i++)
                    {
                        var x = input[at + i];
                        if (output != null) output[at + i] = x;   // the input, bit for bit
                        left[i] = x.L * scale;
                        right[i] = x.R * scale;
                        if (!sound && (MathF.Abs(x.L) > 0.03f || MathF.Abs(x.R) > 0.03f)) sound = true;
                    }
                }
                else
                {
                    if (output != null) Array.Clear(output, at, m);
                    Array.Clear(left, 0, m);
                    Array.Clear(right, 0, m);
                }
                Engine.Process(left, right, m);
            }
            return sound;
        }

        /// <summary>
        /// What the analyzer measures, for agents (docs/specs/mcp-server.md §3.5): the keys of the
        /// manifest's readings (Params.def), in LUFS, LU, dBTP, dB, seconds and ratios; −∞ when
        /// silent. Safe from any thread: a copy of the newest snapshot.
        /// </summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        /// <summary>
        /// Any thread: starts the integrated loudness, loudness range, maxima and held peaks again
        /// at the next block (as the Reset parameter does, without having to switch it Off first).
        /// </summary>
        public void ResetReadings() => Engine.RequestReset();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "Reset integrated loudness",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => ResetReadings(),
                },
            },
            new MenuItemVM
            {
                Text = "About Neo Analyzer...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Analyzer — spectrum (FFT with peak hold, third-octave bars), loudness (momentary, " +
                        "short-term and integrated LUFS, loudness range, true peak) and stereo (correlation, width, " +
                        "goniometer). Audio passes through untouched. The numbers are the analysis library's, which " +
                        "the agent server's analyze uses.\n\nPart of neo-buzz (docs/specs/machines.md).", "Neo Analyzer"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoAnalyzerEditor();
    }
#endif
}
