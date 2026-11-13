#nullable disable
// Neo Multiband (docs/specs/machines.md §5): a multiband compressor and upward expander for mastering
// and buses (docs/specs/mcp-server.md §8.2), on linear-phase crossovers (latency reported to ReBuzz)
// or zero-latency minimum-phase ones (M6b: and a Dynamic phase, lookahead, upward compression and an external
// sidechain on the second input; docs/specs/mastering-tools.md §5.14). DSP in Engine/, parameters generated from Params.def, editor in
// NeoMultibandEditor.cs; readings for agents (machine_readings).

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Multiband
{
    [MachineDecl(Name = "Neo Multiband", ShortName = "Multiband", Author = "neo-buzz", MaxTracks = 0, InputCount = 2, OutputCount = 1)]
    public partial class NeoMultibandMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[MultibandEngine.MaxBlock], right = new float[MultibandEngine.MaxBlock];
        readonly float[] keyLeft = new float[MultibandEngine.MaxBlock], keyRight = new float[MultibandEngine.MaxBlock];
        int rate;

        public NeoMultibandMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new MultibandEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal MultibandEngine Engine { get; }

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
            n = Math.Min(n, MultibandEngine.MaxBlock);
            // The host calls a two-input machine with WM_WRITE alone: an input is there when it has sound.
            bool hasInput = main != null && HasSignal(main, n);
            bool hasKey = key != null && HasSignal(key, n);
            bool active;
            if (hasInput) Split(main, left, right, n);
            else
            {
                // The sound in flight (the latency's worth in linear phase) and the gains' release play out, then rest.
                if (Engine.Idle && !hasKey) active = false;
                else
                {
                    Array.Clear(left, 0, n);
                    Array.Clear(right, 0, n);
                    active = true;
                }
                if (!active) return Rest(main, outBuf, n);
            }
            if (key != null) Split(key, keyLeft, keyRight, n);
            Engine.Process(left, right, key != null ? keyLeft : null, key != null ? keyRight : null, n);
            if (outBuf == null) return false;
            active = Join(left, right, outBuf, n) || hasInput;
            // As the host did for v1's one-input Work returning false: the input passes as it is. The two-input host keeps
            // whatever the buffer holds, so the machine copies it: v1's renders stay bit for bit.
            if (!active) return Rest(main, outBuf, n);
            return true;
        }

        static bool Rest(Sample[] main, Sample[] outBuf, int n)
        {
            if (outBuf == null) return false;
            if (main != null) Array.Copy(main, outBuf, n);
            else Array.Clear(outBuf, 0, n);
            return false;
        }

        public string GetChannelName(bool input, int index) => input ? (index == 0 ? "Input" : "Sidechain") : "Output";

        /// <summary>Samples ReBuzz compensates: about 96 ms in linear phase (4607 at 44.1 and 48 kHz), none in minimum phase.</summary>
        public int GetLatency() => Engine.Latency;

        /// <summary>
        /// What the bands do, for agents (docs/specs/mcp-server.md §3.5): per band its gain change now and its largest over the
        /// last 3 s (dB; positive cuts, negative boosts), its peak level over the last 3 s (dBFS), the band count and the
        /// latency. Safe from any thread: a copy of the newest snapshot.
        /// </summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Multiband...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Multiband — a multiband compressor and upward expander for mastering and buses: 3 or 4 bands " +
                        "split by linear-phase crossovers that sum back to the input exactly (about 96 ms of latency, which " +
                        "NeoBuzz Live compensates) or zero-latency minimum-phase Linkwitz–Riley ones with the same band shapes; per band " +
                        "threshold, ratio, attack, release, knee, range, makeup, solo and bypass; upward compression and an external " +
                        "sidechain (input 2) per band; peak or RMS detection in stereo or mid/side; lookahead; a Dynamic phase " +
                        "(no latency, no static phase shift); mix and output. With every band at 1:1 it passes the input, delayed, bit for bit.\n\n" +
                        "Part of neo-buzz (docs/specs/machines.md).", "Neo Multiband"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoMultibandEditor();
    }
#endif
}
