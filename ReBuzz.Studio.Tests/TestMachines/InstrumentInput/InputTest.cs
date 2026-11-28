// Fork (docs/specs/instrument-input.md): the proof machine of audio into an instrument, for the tests and the gallery. A generator with one audio
// input: it plays its input times a gate that is open while a note is held, plus a quiet sine at the note's pitch while it is held, so a test
// can tell the input from the voice. Nothing held: silence, whatever the input is. Built into the tests' Gear/Generators as "Input Test.NET.dll"
// (InputTest.csproj); not shipped.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace ReBuzz.Studio.Tests.TestMachines
{
    [MachineDecl(Name = "Input Test", ShortName = "InTest", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1, Generator = true)]
    public class InputTestMachine : IBuzzMachine
    {
        /// <summary>The voice's level, in sample units (32768 is full scale): quiet against the noise a test feeds.</summary>
        public const float VoiceLevel = 64f;

        /// <summary>The pitch of a MIDI note, as the voice plays it.</summary>
        public static double HzOf(int note) => 440.0 * Math.Pow(2, (note - 69) / 12.0);

        readonly IBuzzMachineHost host;
        volatile bool held;
        volatile int note = 69;
        double phase;
        bool wasHeld;

        public InputTestMachine(IBuzzMachineHost host)
        {
            this.host = host;
        }

        /// <summary>A value the tests read and write; no sound depends on it.</summary>
        [ParameterDecl(Name = "Spare", MinValue = 0, MaxValue = 127, DefValue = 64, Description = "A value the tests read and write; no sound depends on it")]
        public int Spare { get; set; }

        public void MidiNote(int channel, int value, int velocity)
        {
            if (velocity > 0) { note = value; held = true; }
            else if (value == note) held = false;
        }

        public bool Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)
        {
            var o = output != null && output.Count > 0 ? output[0] : null;
            var i0 = input != null && input.Count > 0 ? input[0] : null;
            bool gate = held;
            if (gate && !wasHeld) phase = 0;
            wasHeld = gate;
            int rate = Math.Max(1, host.MasterInfo.SamplesPerSec);
            double step = 2 * Math.PI * HzOf(note) / rate;
            for (int k = 0; k < n; k++)
            {
                float l = 0, r = 0;
                if (gate)
                {
                    float voice = VoiceLevel * (float)Math.Sin(phase);
                    l = (i0 != null ? i0[k].L : 0f) + voice;
                    r = (i0 != null ? i0[k].R : 0f) + voice;
                    phase += step;
                    if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                }
                if (o != null) o[k] = new Sample(l, r);
            }
            return true;
        }

        public string GetChannelName(bool input, int index) => input ? "Input" : "Output";

        public void Stop()
        {
            held = false;
            wasHeld = false;
            phase = 0;
        }
    }
}
