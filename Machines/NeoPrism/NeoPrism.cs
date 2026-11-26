#nullable disable
// Neo Prism (docs/specs/neo-prism.md): spectral pitch, scramble, freeze, blur and spectral delay on a short-time spectrum. DSP in Engine/,
// parameters generated from Params.def, editor in NeoPrismEditor.cs. Its latency is the frame (size minus one), reported to the host; the
// engine's FFT size can change while it plays (the new size takes over with a crossfade). It takes notes (a note wire, live MIDI or its own
// note column) that freeze the spectrum while a key is down, when the Freeze trigger says Note.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Prism
{
    [MachineDecl(Name = "Neo Prism", ShortName = "Prism", Author = "neo-buzz", MaxTracks = 1, InputCount = 1, OutputCount = 1)]
    public partial class NeoPrismMachine : NeoMachineBase, IBuzzMachine, INoteCapable, INoteReceiver
    {
        readonly float[] left = new float[PrismEngine.MaxBlock], right = new float[PrismEngine.MaxBlock];
        int rate;
        int quietBlocks;

        // Notes from the wire: what ReceiveNotes was given, kept for this block's Work (the audio thread, one after the other).
        readonly NoteMessage[] wire = new NoteMessage[256];
        int wireCount;
        readonly byte[] trackNote = new byte[16];
        readonly NoteSink sink;

        public NeoPrismMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new PrismEngine(Values);
            Notes = new NoteInput(1);
            sink = new NoteSink(this);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal PrismEngine Engine { get; }

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
            if (n > PrismEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && input != null && HasSignal(input, n);
            if (TransportStopped()) { Notes.Clear(); Engine.ClearNotes(); }

            if (!hasInput)
            {
                // What the spectrum still holds (the frame in flight, a freeze, a blur or a delay's repeats) rings out; rest once it is silent.
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
            Notes.Drain(sink);
            for (int i = 0; i < wireCount; i++)
            {
                ref readonly var e = ref wire[i];
                if (e.Kind == NoteMessageKind.NoteOn) Engine.NoteOn(e.Data1);
                else Engine.NoteOff(e.Data1);
            }
            wireCount = 0;
        }

        /// <summary>The frame size minus one, of the FFT size running now.</summary>
        public int GetLatency() => Engine.Latency;

        /// <summary>For agents (machine_readings): latency_samples, fft_size, output_peak_db, sections_on (bits: pitch, scramble, freeze, blur, delay).</summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Prism...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Prism — a spectral pitch shift (phase-locked, with formants kept if you like), a bin scramble, a freeze, a blur and a spectral " +
                        "delay that runs slower toward the highs, on a short-time spectrum from 1024 to 8192 points; Mix and Amount; the latency is one frame.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-prism.md).", "Neo Prism"),
                },
            },
        };

        /// <summary>Notes from the track column and live MIDI become engine notes at the start of the block.</summary>
        sealed class NoteSink : INoteSink
        {
            readonly NeoPrismMachine owner;
            public NoteSink(NeoPrismMachine owner) { this.owner = owner; }

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
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoPrismEditor();
    }
#endif
}
