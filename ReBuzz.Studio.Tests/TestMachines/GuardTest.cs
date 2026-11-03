// Fork (docs/specs/machine-safety.md §5): a machine that misbehaves on purpose, for the guard's and
// the safe master's tests. Mode Sine plays 441 Hz at Level dBFS (Level − 60: 72 is +12 dBFS); Runaway
// is a feedback loop at gain 1.01 that grows until the guard trips (about seven blocks); NaN plays
// the sine, then writes NaN from block Blocks on. Stop starts it over, as Buzz's Stop does.
// Built into the tests' Gear/Generators as "Guard Test.NET.dll" (GuardTest.csproj); not shipped.

using System;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace ReBuzz.Studio.Tests.TestMachines
{
    [MachineDecl(Name = "Guard Test", ShortName = "Guard", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 1)]
    public class GuardTestMachine : IBuzzMachine
    {
        readonly IBuzzMachineHost host;
        double phase;
        float loop = 1f;
        int blocks;

        public GuardTestMachine(IBuzzMachineHost host)
        {
            this.host = host;
        }

        [ParameterDecl(Name = "Mode", ValueDescriptions = new[] { "Sine", "Runaway", "NaN" }, DefValue = 0)]
        public int Mode { get; set; }

        [ParameterDecl(Name = "Level", Description = "Sine level: dBFS + 60", MinValue = 0, MaxValue = 96, DefValue = 54)]
        public int Level { get; set; }

        [ParameterDecl(Name = "Blocks", Description = "NaN: the block it starts in", MinValue = 1, MaxValue = 1000, DefValue = 10)]
        public int Blocks { get; set; }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            blocks++;
            int rate = Math.Max(1, host.MasterInfo.SamplesPerSec);
            if (Mode == 1)
            {
                // y[n] = 1.01 · y[n−1], from an impulse at full scale: diverges.
                for (int i = 0; i < n; i++)
                {
                    loop *= 1.01f;
                    output[i] = new Sample(32768f * loop, 32768f * loop);
                }
                return true;
            }
            if (Mode == 2 && blocks >= Blocks)
            {
                for (int i = 0; i < n; i++) output[i] = new Sample(float.NaN, float.NaN);
                return true;
            }
            float amp = 32768f * (float)Math.Pow(10, (Level - 60) / 20.0);
            for (int i = 0; i < n; i++)
            {
                float v = amp * (float)Math.Sin(phase);
                phase += 2 * Math.PI * 441.0 / rate;
                if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                output[i] = new Sample(v, v);
            }
            return true;
        }

        public void Stop()
        {
            loop = 1f;
            blocks = 0;
            phase = 0;
        }
    }
}
