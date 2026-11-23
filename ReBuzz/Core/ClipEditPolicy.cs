// Fork (docs/specs/clips.md §5.7, CU3; daw-modes.md EV1): the one question every write to a clip's content asks first: may this machine's
// clips be edited? Old machines (classic Buzz machines: no `clipEdit: true` in their manifest) are view only in the Studio: they play, and
// the tracker, the rolls, the envelope lanes and the agent show their clips and refuse every edit, with one line saying why. Cut, copy
// and delete of a whole clip stay allowed (they do not edit the machine's content). A machine whose manifest later says `clipEdit: true`
// unlocks its existing clips: the flag is data, LM changes a machine's status by editing a manifest.
//
// Writable: a machine whose manifest says clipEdit (the Neo machines, Neo Plug, the machines of ours in Machines/Manifests), the Master.
// Where the engine does not play every lane (Classic's WPF app, the engine tests), nothing is refused: Classic edits everything.

using System;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;

namespace ReBuzz.Core
{
    internal static class ClipEditPolicy
    {
        /// <summary>Tests: a rule that returns the reason to refuse a machine, or null to allow it. Replaces the manifest rule while set.</summary>
        internal static Func<IMachine, string> Rule { get; set; }

        /// <summary>The line the UI shows when a write is refused.</summary>
        public const string UiReason = "Convert this machine to edit its clips";

        /// <summary>The agent's reason, naming the machine.</summary>
        public static string AgentReason(IMachine machine) =>
            $"\"{machine?.Name}\" is an old machine: its clips are view only. Convert it to a Neo machine to edit its clips.";

        public static bool CanEdit(IMachine machine, out string reason)
        {
            reason = Rule != null ? Rule(machine) : Default(machine);
            return reason == null;
        }

        /// <summary>Whether the clips of the machine may be edited (the answer without the reason).</summary>
        public static bool CanEdit(IMachine machine) => CanEdit(machine, out _);

        /// <summary>A switch for the tools that build songs with the agent (CI's generators and checks write notes into classic machines): NEOBUZZ_CLIP_EDIT_ALL=1 allows every machine. Tests of the rule set it false.</summary>
        internal static bool AllowAll { get; set; } = Environment.GetEnvironmentVariable("NEOBUZZ_CLIP_EDIT_ALL") == "1";

        static string Default(IMachine machine)
        {
            // Clean break (user decision C, 2026-11-23): old machines are removed in a later wave (CB1), so there is no view-only mode: every machine's clips may be edited.
            // The manifest flag and IsWritable stay as data for CB1 to delete.
            return null;
        }

        /// <summary>The agent's refusal for a write to the machine's clips, or null when it may write.</summary>
        public static string AgentRefusal(IMachine machine) => CanEdit(machine, out var reason) ? null : (reason == UiReason ? AgentReason(machine) : reason);

        /// <summary>The definition of §5.7: a manifest that says clipEdit, or the Master. Anything else is old.</summary>
        public static bool IsWritable(IMachine machine)
        {
            if (machine == null) return true;
            try
            {
                if (machine.DLL?.Info?.Type == MachineType.Master) return true;
                return SongModel.Manifest(machine)?.ClipEdit == true;
            }
            catch (Exception)
            {
                return false;       // a manifest that cannot be read is no manifest
            }
        }
    }
}
