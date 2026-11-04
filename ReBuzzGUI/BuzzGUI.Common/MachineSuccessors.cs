#nullable disable
// Fork addition (neo-buzz, docs/specs/mcp-server.md §12.5, U5): how ReBuzz.exe's machine view
// (BuzzGUI.MachineView, which knows only BuzzGUI's interfaces) offers "Replace with <successor>..."
// on a classic machine that has a Neo successor, and runs machine_replace's code for it. ReBuzz sets
// both at start; the studio calls that code directly.

using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;

namespace BuzzGUI.Common
{
    public static class MachineSuccessors
    {
        /// <summary>Set by ReBuzz at start: the successors a machine can be replaced with (gear names), or none.</summary>
        public static Func<IMachine, IReadOnlyList<string>> For { get; set; }

        /// <summary>Set by ReBuzz at start: replaces the machine with that successor, one undo step, and shows the comparison.</summary>
        public static Action<IMachine, string> Replace { get; set; }
    }
}
