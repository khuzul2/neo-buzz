#nullable disable
// Neo Chop (docs/specs/neo-chop.md): stutter, repeat, retrigger, tape stop and start, a step gate and an auto-pan, in time with the song.
// DSP in Engine/, parameters generated from Params.def, editor in NeoChopEditor.cs. It takes notes (from a note wire, live MIDI or its own
// note column on a track) that start the cutting modes whose trigger says Note or Both.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Chop
{
    [MachineDecl(Name = "Neo Chop", ShortName = "Chop", Author = "neo-buzz", MaxTracks = 1, InputCount = 1, OutputCount = 1)]
    public partial class NeoChopMachine : NeoMachineBase, IBuzzMachine, INoteCapable, INoteReceiver
    {
        readonly float[] left = new float[ChopEngine.MaxBlock], right = new float[ChopEngine.MaxBlock];
        int rate;

        // Notes from the wire: what ReceiveNotes was given, kept for this block's Work (the audio thread, one after the other).
        readonly NoteMessage[] wire = new NoteMessage[256];
        int wireCount;
        readonly byte[] trackNote = new byte[16];
        readonly NoteSink sink;

        public NeoChopMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new ChopEngine(Values);
            Notes = new NoteInput(1);
            sink = new NoteSink(this);
        }

        internal ChopEngine Engine { get; }

        /// <summary>Notes from the track's note column and live MIDI (the wire's come through ReceiveNotes with their offsets).</summary>
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
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && HasSignal(input, n);
            if (TransportStopped()) Notes.Clear();

            if (!hasInput)
            {
                // Rest once the ring holds only silence and nothing is sounding (a stutter can go on repeating a slice after the input stops).
                if (!Engine.AnyVoice && !Engine.Shaping && Engine.SilentSamples >= Engine.RingLength)
                {
                    wireCount = 0;
                    Notes.Drain(sink);
                    Engine.ClearNotes();
                    Engine.Skipped();
                    return false;
                }
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else Split(input, left, right, n);

            Transport(n);
            QueueNotes();
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        /// <summary>Where the song is now (valid in Work): its position in beats and the samples a beat takes.</summary>
        void Transport(int n)
        {
            bool playing = false;
            double beat = 0, spb = SampleRate * 60.0 / BeatsPerMinute;
            try
            {
                var info = host?.MasterInfo;
                var buzz = host?.Machine?.Graph?.Buzz;
                if (info != null && buzz != null)
                {
                    int spt = info.SamplesPerTick, tpb = info.TicksPerBeat;
                    if (spt > 0 && tpb > 0)
                    {
                        spb = (double)spt * tpb;
                        playing = buzz.Playing;
                        int tick = buzz.Song?.PlayPosition ?? 0;
                        beat = (tick + (double)info.PosInTick / spt) / tpb;
                    }
                }
            }
            catch
            {
                playing = false;
            }
            Engine.SetTransport(playing, beat, spb);
        }

        void QueueNotes()
        {
            // Track notes and live MIDI first (they have no offset), then the wire's, which have.
            Notes.Drain(sink);
            for (int i = 0; i < wireCount; i++)
            {
                ref readonly var e = ref wire[i];
                Engine.QueueNote(Math.Min(e.Offset, ChopEngine.MaxBlock - 1), e.Kind == NoteMessageKind.NoteOn, e.Data1);
            }
            wireCount = 0;
        }

        /// <summary>No latency: the dry signal is never delayed.</summary>
        public int GetLatency() => ChopEngine.Latency;

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Chop...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Chop — a stutter, a bar-end repeat, a retrigger, a tape stop and start, a 16-step gate and an auto-pan, " +
                        "in time with the song and started by the beat grid, by notes or both; every cut is crossfaded; zero latency.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-chop.md).", "Neo Chop"),
                },
            },
        };

        /// <summary>Notes from the track column and live MIDI become engine notes at the start of the block.</summary>
        sealed class NoteSink : INoteSink
        {
            readonly NeoChopMachine owner;
            public NoteSink(NeoChopMachine owner) { this.owner = owner; }

            public void NoteOn(int key, int note, float velocity)
            {
                if (key >= 0 && key < owner.trackNote.Length) owner.trackNote[key] = (byte)note;
                owner.Engine.QueueNote(0, true, note);
            }

            public void NoteOff(int key)
            {
                int note = key >= NoteInput.MidiKeyBase ? key - NoteInput.MidiKeyBase : key >= 0 && key < owner.trackNote.Length ? owner.trackNote[key] : -1;
                if (note >= 0) owner.Engine.QueueNote(0, false, note);
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
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoChopEditor();
    }
#endif
}
