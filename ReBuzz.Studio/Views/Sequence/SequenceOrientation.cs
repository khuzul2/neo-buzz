// Fork (docs/specs/daw-modes.md AR6, agent-knowledge.md K3b): the tracker's orientation as a user preference. The sequence view's Vertical
// button and the Appearance setting (appearance.VerticalTracker, so an agent's settings_set and Preferences) share this one value,
// kept in the settings registry (Studio/SequenceVertical). A change made here reaches the views that are open.

using System;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Views.Sequence
{
    internal static class SequenceOrientation
    {
        public const string Key = "SequenceVertical", Section = "Studio";

        /// <summary>The remembered orientation: vertical (time runs down) or horizontal (the default).</summary>
        public static bool Vertical
        {
            get
            {
                try { return StudioHost.Current?.Registry?.Read(Key, false, Section) ?? false; }
                catch (Exception) { return false; }
            }
            set
            {
                if (Vertical == value) return;
                Remember(value);
                Changed?.Invoke(value);
            }
        }

        /// <summary>Keeps the choice for the next session without telling the views (the view that made it already shows it).</summary>
        public static void Remember(bool on)
        {
            try { StudioHost.Current?.Registry?.Write(Key, on, Section); }
            catch (Exception) { /* a registry that can't be written: the choice lasts this session */ }
        }

        /// <summary>The setting changed the orientation: open sequence views follow.</summary>
        public static event Action<bool> Changed;
    }
}
