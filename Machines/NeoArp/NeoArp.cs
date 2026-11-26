#nullable disable
// Neo Arp (docs/specs/neo-arp.md): an arpeggiator on note wires. It takes notes (a note insert before an instrument, or a free note wire) and sends
// them on one at a time, in the same block at the sample they fall on. No audio. The logic is the engine's (Engine/ArpEngine.cs); parameters are
// generated from Params.def; the editor is NeoArpEditor.cs. The song's tempo and position come from the host's master info, as for the canvas
// modulators (NeoBuzz.Machine/NeoModulator.cs): a block never crosses a tick, so the tempo is constant inside it.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Arp
{
    [MachineDecl(Name = "Neo Arp", ShortName = "Arp", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 1)]
    public partial class NeoArpMachine : NeoMachineBase, IBuzzMachine, INoteCapable, INoteReceiver
    {
        const int InputCapacity = 4096;

        readonly ArpEngine engine;
        INoteEmitter emitter;
        readonly ArpEvent[] input = new ArpEvent[InputCapacity];
        int inputCount;
        // Notes that came the old way (MidiNote, from a thread that is not the audio thread): taken at the start of the next block.
        readonly ArpEvent[] late = new ArpEvent[InputCapacity];
        int lateCount;
        readonly object lateGate = new object();
        int rate;
        double beatsPerBar = 4;
        int barCheck;

        public NeoArpMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            engine = new ArpEngine(Values);
        }

        internal ArpEngine Engine => engine;

        // Note wires (docs/specs/note-wires.md): it takes notes and sends notes, so it can be a note insert.
        bool INoteCapable.AcceptsNotes => true;
        bool INoteCapable.SendsNotes => true;

        void INoteReceiver.ReceiveNotes(ReadOnlySpan<NoteMessage> events)
        {
            // Audio thread, just before Work: the events are kept for it.
            for (int i = 0; i < events.Length && inputCount < InputCapacity; i++)
            {
                var e = events[i];
                input[inputCount++] = new ArpEvent { Offset = e.Offset, Kind = (ArpKind)(byte)e.Kind, Channel = e.Channel, Data1 = e.Data1, Data2 = e.Data2 };
            }
        }

        /// <summary>The old way in (a machine with no note wires reaches it through MidiNote): at the start of the next block.</summary>
        public void MidiNote(int channel, int value, int velocity)
        {
            var e = velocity > 0 ? ArpEvent.On(0, channel, value, velocity) : ArpEvent.Off(0, channel, value);
            lock (lateGate)
                if (lateCount < InputCapacity) late[lateCount++] = e;
        }

        public void MidiControlChange(int ctrl, int channel, int value)
        {
            var e = new ArpEvent { Offset = 0, Kind = ArpKind.Controller, Channel = (byte)(channel & 15), Data1 = (byte)(ctrl & 127), Data2 = value };
            lock (lateGate)
                if (lateCount < InputCapacity) late[lateCount++] = e;
        }

        /// <summary>The Panic command: every note off, every held note forgotten.</summary>
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

            // The notes that came by MidiNote go first (offset 0), then the wire's, which are in time order.
            if (lateCount > 0)
            {
                lock (lateGate)
                {
                    int free = InputCapacity - inputCount;
                    int take = Math.Min(free, lateCount);
                    if (take > 0)
                    {
                        Array.Copy(input, 0, input, take, Math.Min(inputCount, InputCapacity - take));
                        Array.Copy(late, 0, input, 0, take);
                        inputCount = Math.Min(InputCapacity, inputCount + take);
                    }
                    lateCount = 0;
                }
            }

            var block = new ArpBlock { Samples = n, BeatsPerBar = beatsPerBar };
            ReadClock(ref block);
            engine.Process(in block, new ReadOnlySpan<ArpEvent>(input, 0, inputCount));
            inputCount = 0;

            if (emitter != null)
            {
                var o = engine.Out;
                for (int i = 0; i < engine.OutCount; i++)
                {
                    ref var e = ref o[i];
                    if (e.Offset >= n) continue;
                    emitter.EmitNote(new NoteMessage { Offset = e.Offset, Kind = (NoteMessageKind)(byte)e.Kind, Channel = e.Channel, Data1 = e.Data1, Data2 = e.Data2 });
                }
            }
            return false;
        }

        /// <summary>The transport and the song position at the start of this block, from the host.</summary>
        void ReadClock(ref ArpBlock block)
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
                Text = "About Neo Arp...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Neo Arp — an arpeggiator on note wires: twelve patterns, a 16-step row with ties, accents and a transpose per step, a rate from 1/1 to 1/64 " +
                        "(straight, dotted, triplet) or in milliseconds, swing, gate, octaves, repeats, velocity, humanize and a latch. Steps land on the exact sample, " +
                        "follow the tempo and the song position, and every note on gets its note off. Put it before a synth as a note insert, or on a note wire.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-arp.md).", "Neo Arp"),
                },
            },
        };
    }

#if AVALONIA   // fork: the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoArpEditor();
    }
#endif
}
