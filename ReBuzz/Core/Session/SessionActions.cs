using BuzzGUI.Interfaces;
using System;
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
            var associations = BuzzGUI.SequenceEditor.SequenceEditor.ViewSettings?.PatternAssociations;
            if (associations != null)
                return associations.TryGetValue(pattern, out var ex) ? ex.ColorIndex : -1;
            return fallback.TryGetValue(pattern, out var box) ? box.Value : -1;
        }

        public static void Set(IPattern pattern, int index)
        {
            if (pattern == null) return;
            var associations = BuzzGUI.SequenceEditor.SequenceEditor.ViewSettings?.PatternAssociations;
            if (associations != null)
            {
                if (index < 0)
                    associations.Remove(pattern);
                else if (associations.TryGetValue(pattern, out var ex))
                    ex.ColorIndex = index;
                else
                    associations[pattern] = new BuzzGUI.SequenceEditor.PatternEx { ColorIndex = index };
            }
            else
            {
                fallback.AddOrUpdate(pattern, new StrongBox<int>(index));
            }
            Changed?.Invoke(pattern);
        }
    }

    /// <summary>Base for session actions that find a pattern by machine and pattern name when run.</summary>
    internal abstract class PatternRefAction : IAction
    {
        readonly ISong song;
        readonly string machine;

        protected PatternRefAction(IPattern pattern)
        {
            song = pattern.Machine.Graph.Buzz.Song;
            machine = pattern.Machine.Name;
        }

        protected IPattern Find(string name) =>
            song.Machines.FirstOrDefault(m => m.Name == machine)?.Patterns.FirstOrDefault(p => p.Name == name);

        public abstract void Do();
        public abstract void Undo();
    }

    internal sealed class RenamePatternAction : PatternRefAction
    {
        readonly string oldName;
        readonly string newName;

        public RenamePatternAction(IPattern pattern, string newName) : base(pattern)
        {
            oldName = pattern.Name;
            this.newName = newName;
        }

        public override void Do() { if (Find(oldName) is IPattern p) p.Name = newName; }
        public override void Undo() { if (Find(newName) is IPattern p) p.Name = oldName; }
    }

    internal sealed class PatternColorAction : PatternRefAction
    {
        readonly string name;
        readonly int oldIndex;
        readonly int newIndex;

        public PatternColorAction(IPattern pattern, int newIndex) : base(pattern)
        {
            name = pattern.Name;
            oldIndex = PatternColors.Get(pattern);
            this.newIndex = newIndex;
        }

        public override void Do() => PatternColors.Set(Find(name), newIndex);
        public override void Undo() => PatternColors.Set(Find(name), oldIndex);
    }

    /// <summary>
    /// A grid edit as before/after snapshots of the layout. The edit has already been made when
    /// the action is pushed, so the first Do does nothing.
    /// </summary>
    internal sealed class LayoutEditAction : IAction
    {
        readonly SessionLayout layout;
        readonly byte[] before;
        readonly byte[] after;
        bool skipDo = true;

        public LayoutEditAction(SessionLayout layout, byte[] before, byte[] after)
        {
            this.layout = layout;
            this.before = before;
            this.after = after;
        }

        public void Do()
        {
            if (skipDo) { skipDo = false; return; }
            layout.Restore(after);
        }

        public void Undo() => layout.Restore(before);
    }
}
