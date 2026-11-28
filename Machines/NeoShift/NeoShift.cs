#nullable disable
// Neo Shift (docs/specs/neo-shift.md): a pitch shifter, a frequency shifter and a ring modulator. DSP in Engine/, parameters generated from
// Params.def, editor in NeoShiftEditor.cs. Its latency belongs to the Quality (Fast: half the Window; Smooth: 2047 samples), reported to the host,
// and is the same in every mode. It takes notes (a note wire, live MIDI or its own note column) that set the pitch shift or the ring carrier
// when Follow notes is on.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Shift
{
    [MachineDecl(Name = "Neo Shift", ShortName = "Shift", Author = "neo-buzz", MaxTracks = 1, InputCount = 1, OutputCount = 1)]
    public partial class NeoShiftMachine : NeoMachineBase, IBuzzMachine, INoteCapable, INoteReceiver
    {
        readonly float[] left = new float[ShiftEngine.MaxBlock], right = new float[ShiftEngine.MaxBlock];
        int rate;
        int quietBlocks;

        // Notes from the wire: what ReceiveNotes was given, kept for this block's Work (the audio thread, one after the other).
        readonly NoteMessage[] wire = new NoteMessage[256];
        int wireCount;
        readonly byte[] trackNote = new byte[16];
        readonly NoteSink sink;

        public NeoShiftMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new ShiftEngine(Values);
            Notes = new NoteInput(1);
            sink = new NoteSink(this);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal ShiftEngine Engine { get; }

        /// <summary>Notes from the track's note column and live MIDI (the wire's come through ReceiveNotes).</summary>
        internal NoteInput Notes { get; }

        bool INoteCapable.AcceptsNotes => true;
        bool INoteCapable.SendsNotes => false;

        void INoteReceiver.ReceiveNotes(ReadOnlySpan<NoteMessage> events)
        {
            for (int i = 0; i < events.Length && wireCount < wire.Length; i++)
            {
                var k = events[i].Kind;
                if (k == NoteMessageKind.NoteOn || k == NoteMessageKind.NoteOff) wire[wireCount++] = events[i];
            }
        }

        public void MidiNote(int channel, int value, int velocity) => Notes.MidiNote(channel, value, velocity);

        public void MidiControlChange(int ctrl, int channel, int value) => Notes.MidiControl(ctrl, channel, value);

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            if (n > ShiftEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && input != null && HasSignal(input, n);
            if (TransportStopped()) { Notes.Clear(); Engine.ClearNotes(); }

            if (!hasInput)
            {
                // What the paths still hold (the shifter's delay, the allpass pair, a feedback) rings out; rest once it is silent.
                if (Engine.Silent && !Engine.AnyNote && quietBlocks > 8 + Engine.Latency / Math.Max(1, n)) { wireCount = 0; Notes.Drain(sink); return false; }
                quietBlocks++;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else
            {
                quietBlocks = 0;
                Split(input, left, right, n);
            }

            QueueNotes();
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        void QueueNotes()
        {
            Notes.Drain(sink);
            for (int i = 0; i < wireCount; i++)
            {
                ref readonly var e = ref wire[i];
                if (e.Kind == NoteMessageKind.NoteOn) Engine.NoteOn(e.Data1);
                else Engine.NoteOff(e.Data1);
            }
            wireCount = 0;
        }

        /// <summary>The Quality's latency (Fast: half the Window; Smooth: 2047), the same in every mode.</summary>
        public int GetLatency() => Engine.Latency;

        /// <summary>For agents (machine_readings): latency_samples, mode, ratio, shift_hz, carrier_hz, output_peak_db.</summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Shift...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Shift - a pitch shifter (Fast: a delay-line shifter; Smooth: a phase-locked phase vocoder), a frequency shifter " +
                        "(single sideband, with a feedback for rising or falling barber-poles) and a ring modulator (sine, triangle or square carrier); " +
                        "notes can set the shift or the carrier; Mix and Amount.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-shift.md).", "Neo Shift"),
                },
            },
        };

        /// <summary>Notes from the track column and live MIDI become engine notes at the start of the block.</summary>
        sealed class NoteSink : INoteSink
        {
            readonly NeoShiftMachine owner;
            public NoteSink(NeoShiftMachine owner) { this.owner = owner; }

            public void NoteOn(int key, int note, float velocity)
            {
                if (key >= 0 && key < owner.trackNote.Length) owner.trackNote[key] = (byte)note;
                owner.Engine.NoteOn(note);
            }

            public void NoteOff(int key)
            {
                int note = key >= NoteInput.MidiKeyBase ? key - NoteInput.MidiKeyBase : key >= 0 && key < owner.trackNote.Length ? owner.trackNote[key] : -1;
                if (note >= 0) owner.Engine.NoteOff(note);
            }

            public void ModWheel(float value) { }
            public void PitchBend(float value) { }
            public void Sustain(bool down) { }
            public void Aftertouch(float value) { }
            public void AllNotesOff(bool hard) { }
        }
    }

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoShiftEditor();
    }
#endif
}
