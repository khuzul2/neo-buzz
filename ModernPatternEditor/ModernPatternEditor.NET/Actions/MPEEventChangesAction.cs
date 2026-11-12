using BuzzGUI.Common.Actions;
using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor.Actions
{
    /// <summary>
    /// Fork (ux-overhaul.md P3b): events removed from and added to several track parameter columns as one undo step
    /// (a piano roll gesture). Columns are found by parameter index and track, kept as indexes like the other actions.
    /// </summary>
    public class MPEEventChangesAction : PatternAction
    {
        sealed class Change
        {
            public int Column;
            public PatternEvent[] Removed, Added;
        }

        readonly MPEPattern mpePattern;
        readonly List<Change> changes;

        MPEEventChangesAction(IPattern pattern, MPEPattern mpePattern, List<Change> changes) : base(pattern)
        {
            this.mpePattern = mpePattern;
            this.changes = changes;
        }

        internal static MPEPatternColumn Find(MPEPattern pattern, int indexInGroup, int track) =>
            pattern.MPEPatternColumns.FirstOrDefault(c => c.Parameter != null && c.Parameter is not MPEInternalParameter
                && c.GroupType == ParameterGroupType.Track && c.Parameter.IndexInGroup == indexInGroup && c.ParamTrack == track);

        /// <summary>The action for records (parameter index, track, add, time, value), or null when a column is missing.</summary>
        internal static MPEEventChangesAction Plan(IPattern pattern, MPEPattern mpePattern, int[] records)
        {
            var byColumn = new Dictionary<int, Change>();
            for (int i = 0; i < records.Length; i += 5)
            {
                var column = Find(mpePattern, records[i], records[i + 1]);
                if (column == null) return null;
                int index = mpePattern.MPEPatternColumns.IndexOf(column);
                if (!byColumn.TryGetValue(index, out var c))
                    byColumn[index] = c = new Change { Column = index };
                var e = new PatternEvent(records[i + 3], records[i + 4]);
                if (records[i + 2] == 1) c.Added = (c.Added ?? new PatternEvent[0]).Append(e).ToArray();
                else c.Removed = (c.Removed ?? new PatternEvent[0]).Append(e).ToArray();
            }
            return byColumn.Count == 0 ? null : new MPEEventChangesAction(pattern, mpePattern, byColumn.Values.ToList());
        }

        protected override void DoAction()
        {
            foreach (var c in changes)
            {
                var column = mpePattern.MPEPatternColumns[c.Column];
                if (c.Removed != null) column.SetEvents(c.Removed.ToArray(), false);
                if (c.Added != null) column.SetEvents(c.Added.ToArray(), true);
            }
            Done();
        }

        protected override void UndoAction()
        {
            for (int i = changes.Count - 1; i >= 0; i--)
            {
                var c = changes[i];
                var column = mpePattern.MPEPatternColumns[c.Column];
                if (c.Added != null) column.SetEvents(c.Added.ToArray(), false);
                if (c.Removed != null) column.SetEvents(c.Removed.ToArray(), true);
            }
            Done();
        }

        void Done()
        {
            var pattern = mpePattern.Pattern;
            if (pattern != null) mpePattern.Editor.PatternChanged(pattern);
            mpePattern.Editor.SetModifiedFlag();
        }
    }
}
