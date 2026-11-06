// Fork (machine keyboard, docs/specs/studio-mvp.md §3.12): a synth that takes notes only in its Note track parameter,
// as the native machines and (today) the Pedal synths do, for the keyboard's tests whatever the Pedal synths take later:
// a sine on each track at its note's pitch (Buzz's C-5 is MIDI 60; A4 is 440 Hz), at the level the Velocity beside it
// gives (velocity / 127 of -12 dBFS), until an off. No MidiNote: the keyboard plays its tracks. The setters keep what
// they get and Work applies it, so the order a tick sends them in doesn't matter.
// Built into the tests' Gear/Generators as "Keys Test.NET.dll" (KeysTest.csproj); not shipped.

using System;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace ReBuzz.Studio.Tests.TestMachines
{
    [MachineDecl(Name = "Keys Test", ShortName = "Keys", Author = "neo-buzz", MaxTracks = Tracks, InputCount = 0, OutputCount = 1)]
    public class KeysTestMachine : IBuzzMachine
    {
        const int Tracks = 8;
        readonly IBuzzMachineHost host;
        readonly double[] hz = new double[Tracks], phase = new double[Tracks], gain = new double[Tracks];
        readonly int[] velocity = new int[Tracks];
        readonly bool[] started = new bool[Tracks];

        public KeysTestMachine(IBuzzMachineHost host)
        {
            this.host = host;
            for (int t = 0; t < Tracks; t++) velocity[t] = 100;
        }

        [ParameterDecl(Name = "Note", IsStateless = true, Description = "A note: a sine at its pitch until an off")]
        public void SetNote(Note value, int track)
        {
            if ((uint)track >= Tracks || value.Value == 0) return;
            if (value.Value == Note.Off)
            {
                hz[track] = 0;
                return;
            }
            hz[track] = 440 * Math.Pow(2, (value.ToMIDINote() - 69) / 12.0);
            started[track] = true;
        }

        [ParameterDecl(Name = "Velocity", MinValue = 1, MaxValue = 127, DefValue = 100, IsStateless = true, Description = "The next note's level: velocity / 127")]
        public void SetVelocity(int value, int track)
        {
            if ((uint)track < Tracks) velocity[track] = value;
        }

        /// <summary>Tests: the velocity each track's note started with (0 for none playing).</summary>
        public int[] Playing
        {
            get
            {
                var v = new int[Tracks];
                for (int t = 0; t < Tracks; t++) v[t] = hz[t] > 0 ? (int)Math.Round(gain[t] * 127) : 0;
                return v;
            }
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            int rate = Math.Max(1, host.MasterInfo.SamplesPerSec);
            bool any = false;
            for (int t = 0; t < Tracks; t++)
            {
                if (started[t])
                {
                    gain[t] = velocity[t] / 127.0;
                    started[t] = false;
                }
                any |= hz[t] > 0;
            }
            for (int i = 0; i < n; i++)
            {
                double v = 0;
                for (int t = 0; t < Tracks; t++)
                {
                    if (hz[t] <= 0) continue;
                    v += gain[t] * Math.Sin(phase[t]);
                    phase[t] += 2 * Math.PI * hz[t] / rate;
                    if (phase[t] > 2 * Math.PI) phase[t] -= 2 * Math.PI;
                }
                float s = (float)(v * 0.25 * 32768);
                output[i] = new Sample(s, s);
            }
            return any;
        }

        public void Stop()
        {
            Array.Clear(hz);
        }
    }
}
