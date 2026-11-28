// Fork (docs/specs/instrument-input.md): a steady white-noise source for the tests of audio into an instrument. Its noise starts from its Seed
// (a parameter) and runs on from there, so two renders of the same song are the same samples, and two sources with different seeds differ.
// Built into the tests' Gear/Generators as "Noise Test.NET.dll" (NoiseTest.csproj); not shipped.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace ReBuzz.Studio.Tests.TestMachines
{
    [MachineDecl(Name = "Noise Test", ShortName = "Noise", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 1)]
    public class NoiseTestMachine : IBuzzMachine
    {
        /// <summary>The noise's peak, in sample units (32768 is full scale).</summary>
        public const float Level = 8000f;

        readonly IBuzzMachineHost host;
        uint state = 1;
        int seeded = -1;

        public NoiseTestMachine(IBuzzMachineHost host)
        {
            this.host = host;
        }

        [ParameterDecl(Name = "Seed", MinValue = 1, MaxValue = 127, DefValue = 1, Description = "Where the noise starts: another seed, other samples")]
        public int Seed { get; set; } = 1;

        uint Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        public bool Work(IList<Sample[]> output, int n, WorkModes mode)
        {
            if (seeded != Seed)
            {
                seeded = Seed;
                state = 2463534242u ^ (uint)(Seed * 2654435761u);
                if (state == 0) state = 1;
            }
            var o = output != null && output.Count > 0 ? output[0] : null;
            for (int k = 0; k < n; k++)
            {
                float l = Level * ((Next() >> 8) / 8388608f - 1f);
                float r = Level * ((Next() >> 8) / 8388608f - 1f);
                if (o != null) o[k] = new Sample(l, r);
            }
            return true;
        }

        public string GetChannelName(bool input, int index) => input ? "Input" : "Output";

        public void Stop()
        {
            seeded = -1;
        }
    }
}
