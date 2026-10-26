using AtmaFileSystem;
using AtmaFileSystem.IO;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using System.Linq;

namespace ReBuzzTests.Automation.TestMachines
{
    /// <summary>
    /// Fork addition: a stub generator whose output follows a pattern-controlled parameter
    /// (Level), so tests can compare what patterns sound like when played from the session and
    /// from the arrangement. Compiled at test time from this file, like <see cref="Synth"/>.
    /// </summary>
    [MachineDecl(Name = "Tone", ShortName = "Tone", Author = "WDE", MaxTracks = 1, InputCount = 0, OutputCount = 1)]
    public class Tone(IBuzzMachineHost host) : IBuzzMachine
    {
        private readonly IBuzzMachineHost host = host;

        public static string GetSourceCode() => AbsoluteFilePath.OfThisFile().ReadAllText();

        public static MachineDecl GetMachineDecl() => typeof(Tone).GetCustomAttributes(false).OfType<MachineDecl>().Single();

        [ParameterDecl(Name = "Level", MinValue = 0, MaxValue = 100, DefValue = 0)]
        public int Level { get; set; }

        public Sample Work() => new Sample(Level * 100, Level * 100);
    }
}
