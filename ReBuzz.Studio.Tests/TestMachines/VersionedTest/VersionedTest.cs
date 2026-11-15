// Fork (docs/specs/machines.md §3.4): a Neo effect whose layout went through three versions, for the versioned state tests.
//
//   v0 (the layout songs had): Level 0…100, Freq 0…1000, Mode 0…2, Old pan 0…10;  data <Level>, <Pan>
//   v1 (reorder):              Freq, Level, Mode, Old pan
//   v2 (rename and rescale):   Cutoff (was Freq), Gain (was Level, now 0…1000), Mode, Old pan;  data <Gain>, <Pan>
//   v3 (removal, current):     Cutoff, Gain, Mode;  data <Gain>
//
// It passes its input through at Gain (0…1) and keeps Cutoff and Mode for the tests to read; its data is a Gain element.
// Built into the tests' Gear/Effects as "Versioned Test.NET.dll" (VersionedTest.csproj).

using System;
using System.Text;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace ReBuzz.Studio.Tests.TestMachines
{
    public class VersionedTestState
    {
        public int Gain { get; set; } = 1;
    }

    [MachineDecl(Name = "Versioned Test", ShortName = "VTest", Author = "neo-buzz", MaxTracks = 0)]
    public class VersionedTestMachine : NeoMachineBase, IBuzzMachine
    {
        static ParamDef D(int i, string id, string name, int min, int max, int def) => new ParamDef(i, id, name, min, max, def, MapKind.Linear, a: 0, b: 1);

        internal static readonly ParamDef[] Table =
        {
            D(0, "cutoff", "Cutoff", 0, 1000, 500),
            D(1, "gain", "Gain", 0, 1000, 500),
            D(2, "mode", "Mode", 0, 2, 0),
        };

        static readonly StateMigrations Chain = new StateMigrations(Table)
            .Step(new[] { "level", "freq", "mode", "old_pan" }, s => { })
            .Step(new[] { "freq", "level", "mode", "old_pan" }, s => s
                .Rename("freq", "cutoff")
                .Rename("level", "gain")
                .Rescale("gain", 0, 100, 0, 1000)
                .Data(d => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(d).Replace("<Level>", "<Gain>").Replace("</Level>", "</Gain>"))))
            .Step(new[] { "cutoff", "gain", "mode", "old_pan" }, s => s
                .Remove("old_pan")
                .Data(d => Encoding.UTF8.GetBytes(System.Text.RegularExpressions.Regex.Replace(Encoding.UTF8.GetString(d), "<Pan>[^<]*</Pan>", ""))));

        internal override StateMigrations Migrations => Chain;

        public VersionedTestMachine(IBuzzMachineHost host) : base(host, Table)
        {
        }

        [ParameterDecl(Name = "Cutoff", MinValue = 0, MaxValue = 1000, DefValue = 500)]
        public int Cutoff { get => Values[0]; set => Values.Set(0, value); }

        [ParameterDecl(Name = "Gain", MinValue = 0, MaxValue = 1000, DefValue = 500)]
        public int Gain { get => Values[1]; set => Values.Set(1, value); }

        [ParameterDecl(Name = "Mode", MinValue = 0, MaxValue = 2, DefValue = 0)]
        public int Mode { get => Values[2]; set => Values.Set(2, value); }

        public VersionedTestState MachineState { get; set; } = new VersionedTestState();

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            float g = Gain / 1000f;
            for (int i = 0; i < n; i++) output[i] = new Sample(input[i].L * g, input[i].R * g);
            return true;
        }
    }
}
