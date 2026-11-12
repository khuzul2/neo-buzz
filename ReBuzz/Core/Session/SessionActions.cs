using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Pattern colours shared with the Sequence View (fork addition, docs/specs/session-view.md
    /// 0.2): an index into the theme's PatternColors, stored by the sequence editor's
    /// ViewSettings.PatternAssociations and saved in the song by it. Without a sequence editor
    /// (tests, headless use) colours are kept here instead. UI thread only.
    /// </summary>
    internal static class PatternColors
    {
        static readonly ConditionalWeakTable<IPattern, StrongBox<int>> fallback = new ConditionalWeakTable<IPattern, StrongBox<int>>();

        /// <summary>A pattern's colour changed (from the session; the Sequence View should redraw).</summary>
        public static event Action<IPattern> Changed;

        public static int Get(IPattern pattern)
        {
            if (pattern == null) return -1;
            var associations = PatternColours.Current;   // engine-split.md S7
            if (associations != null)
                return associations.TryGet(pattern, out var index) ? index : -1;
            return fallback.TryGetValue(pattern, out var box) ? box.Value : -1;
        }

        public static void Set(IPattern pattern, int index)
        {
            if (pattern == null) return;
            var associations = PatternColours.Current;
            if (associations != null)
            {
                if (index < 0)
                    associations.Remove(pattern);
                else
                    associations.Set(pattern, index);
            }
            else
            {
                fallback.AddOrUpdate(pattern, new StrongBox<int>(index));
            }
            Changed?.Invoke(pattern);
        }
    }

    /// <summary>
    /// Renames a pattern (0.6.1: holds the pattern, not its old name, and follows it if an undo
    /// brought it back as another object). Does nothing if the name it would take is taken.
    /// </summary>
    [BuzzGUI.Common.HistoryName("Rename clip")]
    internal sealed class RenamePatternAction : IAction
    {
        readonly SessionLayout layout;
        readonly IPattern pattern;
        readonly string oldName;
        readonly string newName;

        public RenamePatternAction(SessionLayout layout, IPattern pattern, string newName)
        {
            this.layout = layout;
            this.pattern = pattern;
            oldName = pattern.Name;
            this.newName = newName;
        }

        public void Do() => Rename(newName);
        public void Undo() => Rename(oldName);

        void Rename(string name)
        {
            var p = layout.Current(pattern);
            var machine = p?.Machine;
            if (machine == null || !machine.Patterns.Contains(p) || p.Name == name) return;
            if (machine.Patterns.Any(x => !ReferenceEquals(x, p) && x.Name == name)) return;
            p.Name = name;
        }
    }

    /// <summary>A pattern's colour (0.6.1: holds the pattern, not its name).</summary>
    [BuzzGUI.Common.HistoryName("Clip colour")]
    internal sealed class PatternColorAction : IAction
    {
        readonly SessionLayout layout;
        readonly IPattern pattern;
        readonly int oldIndex;
        readonly int newIndex;

        public PatternColorAction(SessionLayout layout, IPattern pattern, int newIndex)
        {
            this.layout = layout;
            this.pattern = pattern;
            oldIndex = PatternColors.Get(pattern);
            this.newIndex = newIndex;
        }

        public void Do() => PatternColors.Set(Live(), newIndex);
        public void Undo() => PatternColors.Set(Live(), oldIndex);

        IPattern Live()
        {
            var p = layout.Current(pattern);
            return p?.Machine != null && p.Machine.Patterns.Contains(p) ? p : null;
        }
    }

    /// <summary>
    /// A grid edit as before/after snapshots of the layout (0.6.1: documents that share the live
    /// settings, kept in step with the song by <see cref="SessionLayout"/>'s transforms). The
    /// edit has already been made when the action is pushed, so the first Do does nothing.
    /// </summary>
    internal sealed class LayoutEditAction : IAction, IHistoryAction
    {
        readonly SessionLayout layout;
        readonly long bytes;
        bool skipDo = true;

        public LayoutEditAction(SessionLayout layout, SessionDocument before, SessionDocument after, long bytes = 0)
        {
            this.layout = layout;
            Before = before;
            After = after;
            this.bytes = bytes;
        }

        string IHistoryAction.HistoryName => "Edit session grid";
        long IHistoryAction.HistoryBytes => bytes;
        IEnumerable<KeyValuePair<object, long>> IHistoryAction.HistoryBlobs => null;

        public SessionDocument Before { get; }
        public SessionDocument After { get; }

        public void Do()
        {
            if (skipDo) { skipDo = false; return; }
            layout.Restore(After);
        }

        public void Undo() => layout.Restore(Before);
    }
}
