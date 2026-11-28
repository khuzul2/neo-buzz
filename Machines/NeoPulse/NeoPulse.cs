#nullable disable
// Neo Pulse (docs/specs/neo-pulse.md): eight-lane Euclidean and probability rhythm generator on note wires.
// Sends notes out to any machine with a note input (drums, synths) at sample-exact offsets synced to the song
// clock; makes no audio itself. Mirrors NeoArpMachine (Machines/NeoArp/NeoArp.cs).

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Pulse
{
    [MachineDecl(Name = "Neo Pulse", ShortName = "Pulse", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 1)]
    public partial class NeoPulseMachine : NeoMachineBase, IBuzzMachine, INoteCapable
    {
        // State version 1 (SYN1, tempo sync): see PulseMigrations.
        internal override StateMigrations Migrations => PulseMigrations.Chain;

        readonly PulseEngine engine;
        INoteEmitter emitter;
        int rate;
        double beatsPerBar = 4;
        int barCheck;

        public NeoPulseMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            engine = new PulseEngine(Values);
        }

        internal PulseEngine Engine => engine;

        // Note wires (docs/specs/note-wires.md): rhythm generator (notes out only).
        bool INoteCapable.AcceptsNotes => false;
        bool INoteCapable.SendsNotes => true;

        /// <summary>The Panic command: release every active note immediately.</summary>
        public void Panic() => engine.RequestPanic();

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            Array.Clear(output, 0, n);
            if (SampleRate != rate)
            {
                rate = SampleRate;
                engine.SetSampleRate(rate);
            }
            emitter ??= host.Machine as INoteEmitter;

            var block = new PulseBlock { Samples = n, BeatsPerBar = beatsPerBar };
            ReadClock(ref block);
            engine.Process(in block);

            if (emitter != null)
            {
                var o = engine.Out;
                for (int i = 0; i < engine.OutCount; i++)
                {
                    ref var e = ref o[i];
                    if (e.Offset >= n) continue;
                    emitter.EmitNote(new NoteMessage
                    {
                        Offset = e.Offset,
                        Kind = (NoteMessageKind)(byte)e.Kind,
                        Channel = e.Channel,
                        Data1 = e.Data1,
                        Data2 = e.Data2,
                    });
                }
            }
            return false;
        }

        void ReadClock(ref PulseBlock block)
        {
            var mi = host?.MasterInfo;
            if (mi == null) return;
            int spt = Math.Max(1, mi.SamplesPerTick), tpb = Math.Max(1, mi.TicksPerBeat);
            block.SamplesPerBeat = (double)spt * tpb;
            try
            {
                var buzz = host.Machine?.Graph?.Buzz;
                if (buzz != null && buzz.Playing)
                {
                    block.Playing = true;
                    block.Beat = (buzz.Song.PlayPosition + mi.PosInTick / (double)spt) / tpb;
                }
                if (buzz != null && (barCheck++ & 63) == 0)
                {
                    int bars = (buzz as ISessionHost)?.Session?.BeatsPerBar ?? 4;
                    if (bars >= 1) beatsPerBar = bars;
                    block.BeatsPerBar = beatsPerBar;
                }
            }
            catch (Exception)
            {
                block.Playing = false;
            }
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "Panic: release every note",
                Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => Panic() },
            },
            new MenuItemVM
            {
                Text = "About Neo Pulse...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Neo Pulse — an eight-lane Euclidean and probability rhythm generator on note wires: " +
                        "Bjorklund step rings (1 to 32 steps, pulses, rotation), second Euclidean accent rings, " +
                        "per-hit chance, ratchets (1 to 4 repeats with ratchet chance), gate, clock dividers (1x to 4x), " +
                        "MIDI note and channel per lane, global rate (1/1 to 1/64, straight/dotted/triplet), swing, " +
                        "fill chance, humanize and repeatable seeding (0 = Free). Makes no audio itself.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-pulse.md).", "Neo Pulse"),
                },
            },
        };
    }

#if AVALONIA   // fork: the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoPulseEditor();
    }
#endif
}
