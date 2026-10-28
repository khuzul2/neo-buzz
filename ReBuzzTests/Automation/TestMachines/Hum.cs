using AtmaFileSystem;
using AtmaFileSystem.IO;
using Buzz.MachineInterface;
using System.Linq;

namespace ReBuzzTests.Automation.TestMachines
{
    /// <summary>
    /// Fork addition (0.4.1): a stub generator that outputs a constant level and does nothing
    /// else: no host calls, no allocation, no console output. Engine tests use it to measure
    /// what the engine itself costs (allocations and time per buffer).
    /// Compiled at test time from this file, like <see cref="Synth"/>.
    /// </summary>
    [MachineDecl(Name = "Hum", ShortName = "Hum", Author = "WDE", MaxTracks = 1, InputCount = 0, OutputCount = 1)]
    public class Hum(IBuzzMachineHost host) : IBuzzMachine
    {
        private readonly IBuzzMachineHost host = host;

        public static string GetSourceCode() => AbsoluteFilePath.OfThisFile().ReadAllText();

        public static MachineDecl GetMachineDecl() => typeof(Hum).GetCustomAttributes(false).OfType<MachineDecl>().Single();

        [ParameterDecl(ValueDescriptions = ["no", "yes"])]
        public bool Bypass { get; set; }

        public Sample Work() => new Sample(1000, 1000);
    }
}
