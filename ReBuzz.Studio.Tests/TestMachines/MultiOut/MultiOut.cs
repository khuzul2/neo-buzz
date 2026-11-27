// Fork (docs/specs/multi-output.md): the proof machine of the several-output work, for the tests and the gallery. A generator with three
// named outputs (Low, Mid, High), each a steady sine of its own pitch (110, 220 and 440 Hz at levels 6000, 4000 and 2000), so a chain wired
// to an output can be told from the others by what it hears. An output nobody listens to (null in the list) is skipped; every
// output's phase advances anyway, so what one output plays doesn't depend on which of the others are wired.
// Built into the tests' Gear/Generators as "Multi Test.NET.dll" (MultiOut.csproj); not shipped.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace ReBuzz.Studio.Tests.TestMachines
{
    [MachineDecl(Name = "Multi Test", ShortName = "Multi", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 3)]
    [NeoOutputs("Low", "Mid", "High")]
    public class MultiTestMachine : IBuzzMachine
    {
        /// <summary>Each output's pitch and level (sample units; 32768 is full scale).</summary>
        public static readonly double[] Hz = { 110, 220, 440 };
        public static readonly float[] Level = { 6000f, 4000f, 2000f };

        readonly IBuzzMachineHost host;
        readonly double[] phase = new double[3];

        public MultiTestMachine(IBuzzMachineHost host)
        {
            this.host = host;
        }

        /// <summary>A value the tests read and write; no sound depends on it.</summary>
        [ParameterDecl(Name = "Spare", MinValue = 0, MaxValue = 127, DefValue = 64, Description = "A value the tests read and write; no sound depends on it")]
        public int Spare { get; set; }

        public bool Work(IList<Sample[]> output, int n, WorkModes mode)
        {
            int rate = Math.Max(1, host.MasterInfo.SamplesPerSec);
            for (int k = 0; k < 3; k++)
            {
                var buffer = output != null && k < output.Count ? output[k] : null;
                double step = 2 * Math.PI * Hz[k] / rate;
                double p = phase[k];
                for (int i = 0; i < n; i++)
                {
                    if (buffer != null)
                    {
                        float v = Level[k] * (float)Math.Sin(p);
                        buffer[i] = new Sample(v, v);
                    }
                    p += step;
                    if (p > 2 * Math.PI) p -= 2 * Math.PI;
                }
                phase[k] = p;
            }
            return true;
        }

        public string GetChannelName(bool input, int index) => NeoOutputs.ChannelName(this, input, index);

        public void Stop()
        {
            Array.Clear(phase, 0, phase.Length);
        }
    }
}
