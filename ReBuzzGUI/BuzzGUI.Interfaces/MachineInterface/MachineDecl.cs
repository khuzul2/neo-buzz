using System;

namespace Buzz.MachineInterface
{
    [Flags]
    public enum WorkModes { WM_NOIO = 0, WM_READ = 1, WM_WRITE = 2, WM_READWRITE = 3 };

    [AttributeUsage(AttributeTargets.Class)]
    public class MachineDecl : Attribute
    {
        public string Name { get; set; }
        public string ShortName { get; set; }
        public string Author { get; set; }
        public int InputCount { get; set; }
        public int OutputCount { get; set; }
        public int MaxTracks { get; set; }

        /// <summary>
        /// Fork (docs/specs/instrument-input.md): a generator that takes audio. Declare it with <c>Generator = true</c> and <c>InputCount</c> above 0 and
        /// a <c>Work(IList&lt;Sample[]&gt; output, IList&lt;Sample[]&gt; input, int n, WorkModes mode)</c>: the engine sums the wires into each input as it does
        /// for an effect. Without it a machine with that Work is an effect; a generator without it takes no wire.
        /// </summary>
        public bool Generator { get; set; }
    }
}
