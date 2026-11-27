#nullable disable
// Neo machines' Buzz glue (docs/specs/multi-output.md): the names of a machine's outputs. A machine with more than one output says
// how many in its [MachineDecl] (OutputCount) and names them with [NeoOutputs], and answers ReBuzz's GetChannelName with
// NeoOutputs.ChannelName. The studio shows one port per output, named; the mixer gives each wired extra output a strip;
// the agent's connect takes the name in from_output. Output 1 is the main one: a plain connect uses it, and so do songs
// saved before the machine had more than one.

using System;
using System.Collections.Generic;

namespace NeoBuzz.Machine
{
    /// <summary>
    /// Names a machine's outputs, in order: <c>[NeoOutputs("Low", "Mid", "High")]</c> on the machine class, with
    /// <c>OutputCount = 3</c> in its <c>[MachineDecl]</c>. Names are plain words for what comes out (no brands), unique, and short
    /// enough for a port's tooltip and a mixer strip.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class NeoOutputsAttribute : Attribute
    {
        public NeoOutputsAttribute(params string[] names) => Names = names ?? Array.Empty<string>();

        public IReadOnlyList<string> Names { get; }
    }

    public static class NeoOutputs
    {
        /// <summary>The names a machine type declares with <see cref="NeoOutputsAttribute"/> (empty when it declares none).</summary>
        public static IReadOnlyList<string> NamesOf(Type machine)
        {
            var attribute = (NeoOutputsAttribute)Attribute.GetCustomAttribute(machine, typeof(NeoOutputsAttribute), true);
            return attribute?.Names ?? Array.Empty<string>();
        }

        /// <summary>
        /// The answer to ReBuzz's <c>GetChannelName(bool input, int index)</c> for a machine with named outputs: its declared name for an
        /// output, <paramref name="inputName"/> for the first input and "Input n" after it, and "Output n" (from 1) for an output with no name.
        /// </summary>
        public static string ChannelName(object machine, bool input, int index, string inputName = "Input")
        {
            if (input) return index == 0 ? inputName : "Input " + (index + 1);
            var names = NamesOf(machine.GetType());
            return index >= 0 && index < names.Count && !string.IsNullOrWhiteSpace(names[index]) ? names[index] : "Output " + (index + 1);
        }
    }
}
