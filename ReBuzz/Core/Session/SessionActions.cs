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
    internal static class ClipColors
    {
        static readonly ConditionalWeakTable<IClip, StrongBox<int>> fallback = new ConditionalWeakTable<IClip, StrongBox<int>>();

        /// <summary>A pattern's colour changed (from the session; the Sequence View should redraw).</summary>
        public static event Action<IClip> Changed;

        public static int Get(IClip clip)
        {
            if (clip == null) return -1;
            var associations = ClipColours.Current;   // engine-split.md S7
            if (associations != null)
                return associations.TryGet(clip, out var index) ? index : -1;
            return fallback.TryGetValue(clip, out var box) ? box.Value : -1;
        }

        public static void Set(IClip clip, int index)
        {
            if (clip == null) return;
            var associations = ClipColours.Current;
            if (associations != null)
            {
                if (index < 0)
                    associations.Remove(clip);
                else
                    associations.Set(clip, index);
            }
            else
            {
                fallback.AddOrUpdate(clip, new StrongBox<int>(index));
            }
            Changed?.Invoke(clip);
        }
    }

    /// <summary>
    /// Renames a pattern (0.6.1: holds the pattern, not its old name, and follows it if an undo
    /// brought it back as another object). Does nothing if the name it would take is taken.
    /// </summary>
    [BuzzGUI.Common.HistoryName("Rename clip")]
    internal sealed class RenameClipAction : IAction
    {
        readonly SessionLayout layout;
        readonly IClip clip;
        readonly string oldName;
        readonly string newName;

        public RenameClipAction(SessionLayout layout, IClip clip, string newName)
        {
            this.layout = layout;
            this.clip = clip;
            oldName = clip.Name;
            this.newName = newName;
        }

        public void Do() => Rename(newName);
        public void Undo() => Rename(oldName);

        void Rename(string name)
        {
            var p = layout.Current(clip);
            var machine = p?.Machine;
            if (machine == null || !machine.Clips.Contains(p) || p.Name == name) return;
            if (machine.Clips.Any(x => !ReferenceEquals(x, p) && x.Name == name)) return;
            p.Name = name;
        }
    }

    /// <summary>A pattern's colour (0.6.1: holds the pattern, not its name).</summary>
    [BuzzGUI.Common.HistoryName("Clip colour")]
    internal sealed class ClipColorAction : IAction
    {
        readonly SessionLayout layout;
        readonly IClip clip;
        readonly int oldIndex;
        readonly int newIndex;

        public ClipColorAction(SessionLayout layout, IClip clip, int newIndex)
        {
            this.layout = layout;
            this.clip = clip;
            oldIndex = ClipColors.Get(clip);
            this.newIndex = newIndex;
        }

        public void Do() => ClipColors.Set(Live(), newIndex);
        public void Undo() => ClipColors.Set(Live(), oldIndex);

        IClip Live()
        {
            var p = layout.Current(clip);
            return p?.Machine != null && p.Machine.Clips.Contains(p) ? p : null;
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
