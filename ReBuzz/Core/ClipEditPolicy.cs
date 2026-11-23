// Fork (docs/specs/daw-modes.md EV1): the one question every write to a clip's content asks first: may this machine's clips be edited?
// Today every machine's may. CU3 will make the clips of old machines (classic Buzz machines that take no real notes) view only, by a
// manifest flag, and fills in the rule here. The envelope lane's clicks, drags, paste, delete and ramp/step, and the agent's
// clip_envelope, all reach it through ClipEnvelopeEditor.Edit.

using System;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core
{
    internal static class ClipEditPolicy
    {
        /// <summary>Tests: a rule that returns the reason to refuse a machine, or null to allow it.</summary>
        internal static Func<IMachine, string> Rule { get; set; }

        public static bool CanEdit(IMachine machine, out string reason)
        {
            reason = Rule?.Invoke(machine);
            return reason == null;
        }
    }
}
