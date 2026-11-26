#nullable disable
// Neo Chord (docs/specs/neo-chord.md): notes in, chords out. The first shipped machine that both takes and sends notes (docs/specs/note-wires.md):
// a note insert before an instrument, or a stage on a free note wire. It has no audio. The engine is Engine/ChordEngine.cs; parameters are generated from
// Params.def; the editor is NeoChordEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Chord
{
    [MachineDecl(Name = "Neo Chord", ShortName = "Chord", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 1)]
    public partial class NeoChordMachine : NeoMachineBase, IBuzzMachine, INoteCapable, INoteReceiver
    {
        readonly ChordEngine engine;
        INoteEmitter emitter;
        int rate;
        volatile bool stopRequested;

        // Events the old way in (MidiNote, MidiControlChange: a machine that is not on a note wire is played by MIDI): kept until the next block.
        readonly object gate = new object();
        readonly ChordEvent[] legacy = new ChordEvent[256];
        int legacyCount;

        public NeoChordMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            engine = new ChordEngine(Values);
        }

        internal ChordEngine Engine => engine;

        // Note wires: it takes notes and sends notes.
        bool INoteCapable.AcceptsNotes => true;
        bool INoteCapable.SendsNotes => true;

        /// <summary>Audio thread, just before Work: what the wires carried this block, at their offsets.</summary>
        void INoteReceiver.ReceiveNotes(ReadOnlySpan<NoteMessage> events)
        {
            for (int i = 0; i < events.Length; i++)
            {
                ref readonly var e = ref events[i];
                engine.Input(new ChordEvent { Offset = e.Offset, Kind = (byte)e.Kind, Channel = e.Channel, Data1 = e.Data1, Data2 = e.Data2 });
            }
        }

        /// <summary>ReBuzz: a MIDI note for this machine (velocity 0 releases).</summary>
        public void MidiNote(int channel, int value, int velocity)
        {
            if (value < 0 || value > 127) return;
            Push(velocity > 0 ? ChordEvent.NoteOn(0, channel, value, velocity) : ChordEvent.NoteOff(0, channel, value));
        }

        /// <summary>ReBuzz: a MIDI controller, pitch bend (0xE0 or 0xFF) or channel pressure (0xD0 or 0xFC): passed on.</summary>
        public void MidiControlChange(int ctrl, int channel, int value)
        {
            byte kind = ctrl == 0xE0 || ctrl == 0xFF ? (byte)3 : ctrl == 0xD0 || ctrl == 0xFC ? (byte)4 : (byte)2;
            Push(new ChordEvent { Offset = 0, Kind = kind, Channel = (byte)(channel & 15), Data1 = (byte)(kind == 2 ? ctrl & 127 : 0), Data2 = value });
        }

        void Push(in ChordEvent e)
        {
            lock (gate)
                if (legacyCount < legacy.Length) legacy[legacyCount++] = e;
        }

        /// <summary>ReBuzz: stop. Every chord note ends at the next block.</summary>
        public void Stop() => stopRequested = true;

        /// <summary>Offline render: starts from nothing, as a new machine does.</summary>
        public void ResetForRender()
        {
            stopRequested = false;
            lock (gate) legacyCount = 0;
            engine.Reset();
            ResetTransport();
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            emitter ??= host.Machine as INoteEmitter;
            if (SampleRate != rate)
            {
                rate = SampleRate;
                engine.SetSampleRate(rate);
            }
            bool edge = TransportStopped();
            bool stop = stopRequested;
            stopRequested = false;
            if (edge || stop) engine.Panic();
            lock (gate)
            {
                for (int i = 0; i < legacyCount; i++) engine.Input(legacy[i]);
                legacyCount = 0;
            }
            var events = engine.Process(n);
            if (emitter != null)
                for (int i = 0; i < events.Length; i++)
                {
                    ref readonly var e = ref events[i];
                    emitter.EmitNote(new NoteMessage { Offset = e.Offset, Kind = (NoteMessageKind)e.Kind, Channel = e.Channel, Data1 = e.Data1, Data2 = e.Data2 });
                }
            Array.Clear(output, 0, n);
            return false;
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Chord...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Chord — notes in, chords out. Put it before an instrument (a note insert) or on a note wire: every note becomes a chord " +
                        "(20 shapes and a Custom chord, or a chord built in a key and scale), with inversion, spread, octave shift, an added bass note, " +
                        "velocity shaping and a strum (up, down, alternating, random) with a seeded humanize. Every note it sends ends when its source note " +
                        "ends. It makes no sound itself.\n\nPart of neo-buzz (docs/specs/neo-chord.md).", "Neo Chord"),
                },
            },
        };
    }

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoChordEditor();
    }
#endif
}
