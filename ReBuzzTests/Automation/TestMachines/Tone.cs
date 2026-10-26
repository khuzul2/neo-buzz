using AtmaFileSystem;
using AtmaFileSystem.IO;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using System.Linq;

namespace ReBuzzTests.Automation.TestMachines
{
    /// <summary>
    /// Fork addition: a stub generator whose output says which of its patterns is playing and
    /// where: (pattern index + 1) * 1000 + play position (10 per tick, with the fraction within
    /// the tick), or 0 when none plays. Tests use it to compare playback from the session and
    /// from the arrangement sample by sample. (Parameter columns can't be used for that: in
    /// ReBuzz the pattern editor machine plays them, and the tests' editor is a stub.)
    /// Compiled at test time from this file, like <see cref="Synth"/>.
    /// </summary>
    [MachineDecl(Name = "Tone", ShortName = "Tone", Author = "WDE", MaxTracks = 1, InputCount = 0, OutputCount = 1)]
    public class Tone(IBuzzMachineHost host) : IBuzzMachine
    {
        private readonly IBuzzMachineHost host = host;

        public static string GetSourceCode() => AbsoluteFilePath.OfThisFile().ReadAllText();

        public static MachineDecl GetMachineDecl() => typeof(Tone).GetCustomAttributes(false).OfType<MachineDecl>().Single();

        [ParameterDecl(ValueDescriptions = ["no", "yes"])]
        public bool Bypass { get; set; }

        public Sample Work()
        {
            var patterns = host.Machine.Patterns;
            for (int i = 0; i < patterns.Count; i++)
            {
                int position = patterns[i].PlayPosition;
                if (position != int.MinValue)
                {
                    float v = (i + 1) * 1000 + position / 24f;
                    return new Sample(v, v);
                }
            }
            return new Sample(0, 0);
        }
    }
}
