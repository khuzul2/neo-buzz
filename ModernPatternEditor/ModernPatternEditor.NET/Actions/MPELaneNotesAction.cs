using BuzzGUI.Common;
using BuzzGUI.Common.Actions;
using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor.Actions
{
    /// <summary>
    /// Fork (ux-overhaul.md P3b): a pattern's MIDI note lanes (Note, Velocity, Note Delay, Note Cut of each track) made again from
    /// planned rows (PatternEditorUtils.PlanClipNotes), as one undo step; the lanes before and after are kept, so an undo and a redo
    /// put them back exactly.
    /// </summary>
    public class MPELaneNotesAction : PatternAction
    {
        readonly MPEPattern mpePattern;
        readonly List<TrackerRow> rows;
        readonly int tracks;
        Dictionary<MPEPatternColumn, PatternEvent[]> before, after;

        public MPELaneNotesAction(IPattern pattern, MPEPattern mpePattern, List<TrackerRow> rows, int tracks) : base(pattern)
        {
            this.mpePattern = mpePattern;
            this.rows = rows;
            this.tracks = tracks;
        }

        Dictionary<MPEPatternColumn, PatternEvent[]> Snapshot() =>
            PatternEditorUtils.LaneColumns(mpePattern).ToDictionary(c => c, c => c.GetEvents(0, int.MaxValue).ToArray());

        void Restore(Dictionary<MPEPatternColumn, PatternEvent[]> state)
        {
            lock (mpePattern.Editor.syncLock)
            {
                foreach (var c in PatternEditorUtils.LaneColumns(mpePattern))
                {
                    c.Clear();
                    if (state.TryGetValue(c, out var events) && events.Length > 0)
                        c.SetEvents(events.ToArray(), true, false);
                }
            }
        }

        protected override void DoAction()
        {
            if (after == null)
            {
                lock (mpePattern.Editor.syncLock)
                {
                    before = Snapshot();
                    PatternEditorUtils.WriteClipNotes(mpePattern, rows, tracks);
                    after = Snapshot();
                }
            }
            else
            {
                Restore(after);
            }
            Done();
        }

        protected override void UndoAction()
        {
            Restore(before);
            Done();
        }

        void Done()
        {
            var pattern = mpePattern.Pattern;
            if (pattern != null)
            {
                mpePattern.Editor.PatternChanged(pattern);
                mpePattern.Editor.RefreshPatternView(pattern);
            }
            mpePattern.Editor.SetModifiedFlag();
        }
    }
}
