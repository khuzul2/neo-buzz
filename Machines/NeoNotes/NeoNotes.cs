#nullable disable
// Neo Notes (docs/specs/neo-notes.md): notes in, changed notes out. A note insert before an instrument, or a stage on a free note wire (docs/specs/note-wires.md).
// It has no audio. The engine is Engine/NotesEngine.cs; parameters are generated from Params.def; the editor is NeoNotesEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Notes
{
    [MachineDecl(Name = "Neo Notes", ShortName = "Notes", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 1)]
    public partial class NeoNotesMachine : NeoMachineBase, IBuzzMachine, INoteCapable, INoteReceiver
    {
        readonly NotesEngine engine;
        INoteEmitter emitter;
        int rate;
        volatile bool stopRequested;

        // Events the old way in (MidiNote, MidiControlChange: a machine that is not on a note wire is played by MIDI): kept until the next block.
        readonly object gate = new object();
        readonly NotesEvent[] legacy = new NotesEvent[256];
        int legacyCount;

        public NeoNotesMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            engine = new NotesEngine(Values);
        }

        internal NotesEngine Engine => engine;

        // Note wires: it takes notes and sends notes.
        bool INoteCapable.AcceptsNotes => true;
        bool INoteCapable.SendsNotes => true;

        /// <summary>Audio thread, just before Work: what the wires carried this block, at their offsets.</summary>
        void INoteReceiver.ReceiveNotes(ReadOnlySpan<NoteMessage> events)
        {
            for (int i = 0; i < events.Length; i++)
            {
                ref readonly var e = ref events[i];
                engine.Input(new NotesEvent { Offset = e.Offset, Kind = (byte)e.Kind, Channel = e.Channel, Data1 = e.Data1, Data2 = e.Data2 });
            }
        }

        /// <summary>ReBuzz: a MIDI note for this machine (velocity 0 releases).</summary>
        public void MidiNote(int channel, int value, int velocity)
        {
            if (value < 0 || value > 127) return;
            Push(velocity > 0 ? NotesEvent.NoteOn(0, channel, value, velocity) : NotesEvent.NoteOff(0, channel, value));
        }

        /// <summary>ReBuzz: a MIDI controller, pitch bend (0xE0 or 0xFF) or channel pressure (0xD0 or 0xFC): passed on.</summary>
        public void MidiControlChange(int ctrl, int channel, int value)
        {
            byte kind = ctrl == 0xE0 || ctrl == 0xFF ? (byte)3 : ctrl == 0xD0 || ctrl == 0xFC ? (byte)4 : (byte)2;
            Push(new NotesEvent { Offset = 0, Kind = kind, Channel = (byte)(channel & 15), Data1 = (byte)(kind == 2 ? ctrl & 127 : 0), Data2 = value });
        }

        void Push(in NotesEvent e)
        {
            lock (gate)
            {
                // The note queue's reserve (notes.md G5): a note off always finds room.
                if (legacyCount < legacy.Length - (e.Kind == NotesEvent.Off ? 0 : 32)) legacy[legacyCount++] = e;
            }
        }

        /// <summary>ReBuzz: stop. Every note ends at the next block.</summary>
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
            var mi = host?.MasterInfo;
            if (mi != null) engine.SamplesPerBeat = (double)Math.Max(1, mi.SamplesPerTick) * Math.Max(1, mi.TicksPerBeat);
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
                Text = "Panic: release every note",
                Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => Stop() },
            },
            new MenuItemVM
            {
                Text = "About Neo Notes...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Notes — notes in, changed notes out. Put it before an instrument (a note insert) or on a note wire. Seven sections, each with its own switch: " +
                        "Transpose, Scale (a key and ten scales; move, drop or filter the notes outside it), Spread (notes moved by whole octaves), Velocity (curve, range, fixed, random), " +
                        "Length (as played, fixed or clamped), Echo (up to 16 repeats at the song's tempo or in milliseconds, each quieter, shifted and shorter) and Humanize (a random delay and velocity, seeded). " +
                        "Every note it sends ends. It makes no sound itself.\n\nPart of neo-buzz (docs/specs/neo-notes.md).", "Neo Notes"),
                },
            },
        };
    }

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoNotesEditor();
    }
#endif
}
