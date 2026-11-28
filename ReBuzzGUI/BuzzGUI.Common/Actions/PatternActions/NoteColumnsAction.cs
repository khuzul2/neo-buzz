// Fork addition (neo-buzz, docs/specs/notes.md section 9, CU6b): the number of note+velocity column pairs of a clip, as one undo step of the
// song's history. It is view state kept with the clip's Notes lane (the MIDI column's metadata, NoteColumns.Key), so it is saved with the clip
// and a copy of the clip has it. The clip is found by its machine's and its own name each time, so undo and redo work after the machine is
// deleted and made again. The tracker and the agent both make it.

using BuzzGUI.Interfaces;
using System;
using System.Linq;

namespace BuzzGUI.Common.Actions.ClipActions
{
    [HistoryName("Set note columns")]
    public class NoteColumnsAction : MachineAction
    {
        readonly int? before, after;
        readonly string clipName;

        /// <summary>The clip, found again by name (ClipAction's own way: the portable engine does not compile ClipAction, and the editor's shim is not seen from the agent).</summary>
        IClip Clip
        {
            get
            {
                var p = Machine.Clips.FirstOrDefault(x => x.Name == clipName);
                if (p == null) throw new ActionException(this);
                return p;
            }
        }

        /// <summary>Raised after the count of a clip changed (done, undone or redone, on whatever thread did it): the views of that clip lay out again.</summary>
        public static event Action<IClip> Changed;

        public NoteColumnsAction(IClip clip, int? before, int after) : base(clip.Machine)
        {
            clipName = clip.Name;
            this.before = before;
            this.after = after;
        }

        /// <summary>The clip's Notes lane (the MIDI column of its own machine), or null.</summary>
        public static IClipColumn FindColumn(IClip clip) =>
            clip?.Columns.FirstOrDefault(c => c.Type == ClipColumnType.MIDI && (c.Parameter == null || c.Parameter.IndexInGroup < 0) && ReferenceEquals(c.Machine, clip.Machine));

        /// <summary>The count a clip stores now (null: none).</summary>
        public static int? StoredOf(IClip clip) => NoteColumns.Stored(FindColumn(clip)?.Metadata);

        protected override void DoAction() => Set(after);

        protected override void UndoAction() => Set(before);

        void Set(int? count)
        {
            var p = Clip;
            var column = FindColumn(p);
            if (column == null && count != null)
            {
                p.InsertColumn(p.Columns.Count, (IMachine)null);
                column = FindColumn(p);
            }
            if (column == null) return;
            NoteColumns.Store(column.Metadata, count);
            Global.Buzz?.SetModifiedFlag();
            Changed?.Invoke(p);
        }
    }
}
