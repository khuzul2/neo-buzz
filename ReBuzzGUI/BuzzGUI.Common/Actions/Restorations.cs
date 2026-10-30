using System;

namespace BuzzGUI.Common.Actions
{
    /// <summary>
    /// Fork (neo-buzz, docs/specs/session-view.md 0.6.2): undo actions that bring back a
    /// pattern, sequence or machine they deleted (or redo actions that make again one their
    /// undo deleted) report the old object and the new one standing for it. Views that keep
    /// their own data about objects (the Session View's slots and clip settings) follow them
    /// exactly, instead of guessing by name.
    /// </summary>
    public static class Restorations
    {
        /// <summary>(previous, restored): <paramref name="restored"/> now stands for <paramref name="previous"/>, which is gone.</summary>
        public static event Action<object, object> Restored;

        public static void Report(object previous, object restored)
        {
            if (previous == null || restored == null || ReferenceEquals(previous, restored)) return;
            Restored?.Invoke(previous, restored);
        }
    }
}
