#nullable disable
// Fork addition (neo-buzz, docs/specs/mcp-server.md §12.11, U4): how ReBuzz.exe's machine view
// (BuzzGUI.MachineView, which knows only BuzzGUI's interfaces) offers a machine's manifest editor:
// "Editor..." opens it, and "Double-click Opens Editor" is its per-gear setting (on by default for
// curated manifests). ReBuzz sets these at start; the studio calls the same code directly.

using BuzzGUI.Interfaces;
using System;

namespace BuzzGUI.Common
{
    public static class MachineEditors
    {
        /// <summary>Set by ReBuzz at start: whether the machine has a manifest to make an editor from.</summary>
        public static Func<IMachine, bool> Available { get; set; }

        /// <summary>Set by ReBuzz at start: opens the machine's manifest editor.</summary>
        public static Action<IMachine> Open { get; set; }

        /// <summary>Set by ReBuzz at start: whether a double click opens the editor rather than the parameter list (per gear).</summary>
        public static Func<IMachine, bool> OnDoubleClick { get; set; }

        /// <summary>Set by ReBuzz at start: sets that, for the machine's gear.</summary>
        public static Action<IMachine, bool> SetOnDoubleClick { get; set; }
    }
}
