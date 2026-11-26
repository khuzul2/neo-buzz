#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 1.0): how the Neo Audio machine (once the Session Clip Player)
// (Machines/SessionClipPlayer, the audio tracks' machine) reaches ReBuzz. The machine is a
// separate DLL, as every managed machine is (they are loaded with Assembly.LoadFile); its Work
// hands the block to ReBuzz here, which plays the audio clips of its clips and records.

using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using System;

namespace BuzzGUI.Common
{
    public static class SessionClipHost
    {
        /// <summary>The machine's name (its MachineDecl): how ReBuzz knows an audio track.</summary>
        public const string MachineName = "Neo Audio";

        /// <summary>What the machine was called before it became Neo Audio: songs that name it load as Neo Audio (MachineAliases).</summary>
        public const string LegacyMachineName = "Session Clip Player";

        /// <summary>
        /// Plays and records a block: <paramref name="state"/> is what <see cref="CreateState"/>
        /// made for this machine, <paramref name="input"/> holds sound only when
        /// <paramref name="mode"/> has WM_READ, <paramref name="monitor"/> is its Monitor
        /// parameter (0 auto, 1 in, 2 off). Returns whether the output holds sound (all
        /// <paramref name="n"/> samples written). Audio thread.
        /// </summary>
        public delegate bool RenderHandler(IMachine machine, object state, Sample[] output, Sample[] input, int n, WorkModes mode, int monitor);

        /// <summary>Set by ReBuzz at start.</summary>
        public static RenderHandler Render { get; set; }

        /// <summary>Set by ReBuzz at start: a machine's own state, made when it is created (not on the audio thread).</summary>
        public static Func<object> CreateState { get; set; }
    }
}
