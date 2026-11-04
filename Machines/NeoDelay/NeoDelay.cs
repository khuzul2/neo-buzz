#nullable disable
// Neo Delay (docs/specs/machines.md §5): a tempo-synced stereo, ping-pong and cross delay with
// filters, saturation, a little wow and ducking in its loop, which can't run away; the successor of
// Jeskola Delay, Cross Delay, NiNjA dElaY and FSM PanzerDelay (docs/specs/mcp-server.md §12.5). DSP
// in Engine/, parameters generated from Params.def, editor in NeoDelayEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Delay
{
    [MachineDecl(Name = "Neo Delay", ShortName = "Delay", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoDelayMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[DelayEngine.MaxBlock], right = new float[DelayEngine.MaxBlock];
        int rate;

        public NeoDelayMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new DelayEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal DelayEngine Engine { get; }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            n = Math.Min(n, DelayEngine.MaxBlock);
            Engine.Tempo = BeatsPerMinute;
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && input != null && HasSignal(input, n);
            if (hasInput) Split(input, left, right, n);
            else
            {
                // The echoes ring on until the lines have been silent for longer than the longest delay.
                if (Engine.Idle) return false;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            Engine.Process(left, right, n);
            bool sound = Join(left, right, output, n);
            return sound || hasInput;
        }

        /// <summary>No latency: the dry signal is never delayed.</summary>
        public int GetLatency() => Engine.Latency;

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Delay...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Delay — a stereo, ping-pong and cross delay, its time free or synced to the song " +
                        "(straight, dotted, triplet), with a low cut, a high cut and tape saturation in the loop, a little " +
                        "wow, ducking by the input, width and mix. Feedback reaches ±99 % and the line can't run away. " +
                        "At Mix 0 % it passes the input bit for bit.\n\n" +
                        "Part of neo-buzz (docs/specs/machines.md).", "Neo Delay"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoDelayEditor();
    }
#endif
}
