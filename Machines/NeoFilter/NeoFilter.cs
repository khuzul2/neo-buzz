#nullable disable
// Neo Filter (docs/specs/machines.md §5): a stereo filter effect on Neo Poly's filters (the ladder and
// the state-variable filter), oversampled, with drive, an envelope follower and an LFO moving the
// cutoff; the successor of Jeskola Filter and Filter 2, FSM WahMan and Pedal Filter (docs/specs/
// mcp-server.md §12.5). DSP in Engine/, parameters generated from Params.def, editor in
// NeoFilterEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Filter
{
    [MachineDecl(Name = "Neo Filter", ShortName = "Filter", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoFilterMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[FilterEngine.MaxBlock], right = new float[FilterEngine.MaxBlock];
        int rate;

        public NeoFilterMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new FilterEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal FilterEngine Engine { get; }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            n = Math.Min(n, FilterEngine.MaxBlock);
            Engine.Tempo = BeatsPerMinute;
            Engine.Transport = Transport();
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && input != null && HasSignal(input, n);
            if (hasInput) Split(input, left, right, n);
            else
            {
                // The filter rings on (and self-oscillates for good) until it and its input have been silent a while.
                if (Engine.Idle) return false;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            Engine.Process(left, right, n);
            bool sound = Join(left, right, output, n);
            return sound || hasInput;
        }

        /// <summary>Where the song is now (valid in Work), for an LFO that follows it.</summary>
        FilterTransport Transport()
        {
            var t = new FilterTransport();
            try
            {
                var info = host?.MasterInfo;
                var buzz = host?.Machine?.Graph?.Buzz;
                if (info != null && buzz != null)
                {
                    t.Playing = buzz.Playing;
                    t.Tick = buzz.Song?.PlayPosition ?? 0;
                    t.PosInTick = info.PosInTick;
                    t.SamplesPerTick = info.SamplesPerTick;
                    t.TicksPerBeat = info.TicksPerBeat;
                }
            }
            catch
            {
                t.Playing = false;
            }
            return t;
        }

        /// <summary>No latency: the dry signal is never delayed.</summary>
        public int GetLatency() => Engine.Latency;

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Filter...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Filter — Neo Poly's filters as an effect: the ladder (low-pass 24 or 12 dB/oct) and the state-" +
                        "variable filter (low-pass, band-pass, high-pass, notch, peak; 12 or 24 dB/oct), cutoff in Hz, " +
                        "resonance as Q up to a bounded self-oscillation, oversampled drive, and an envelope follower and an " +
                        "LFO (free or synced, locked to the song) moving the cutoff. At Mix 0 % it passes the input bit for bit.\n\n" +
                        "Part of neo-buzz (docs/specs/machines.md).", "Neo Filter"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoFilterEditor();
    }
#endif
}
