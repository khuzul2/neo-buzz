// Fork (docs/specs/ux-overhaul.md P3b, daw-modes.md S7): the notes of the Tracker itself. The Modern Pattern Editor keeps
// its own Note columns, a track each, and a note lasts from its note-on to the next note or note-off on its track. The
// piano roll in the detail panel shows those columns and edits them with the tracker's rules (TrackerColumnNotes: a note
// a track at a time, a moved note keeps its track, velocity to the machine's velocity column), but on the editor's own
// store: the events are changed by the editor, as one action on its undo stack, so what the piano roll does the tracker
// shows at once and undoes with the same keys. Notes lie on the column's rows: a start or an end between two rows goes
// to the row before; a note that runs to the pattern's end has no off (the tracker's own way: it goes on into the next pattern).

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Model;
using ReBuzz.Core.Session;

namespace ReBuzz.Core.Notes
{
    internal sealed class EditorTrackerNotes : TrackerColumnNotes
    {
        readonly MachineCore machine;
        readonly PatternCore patternCore;

        EditorTrackerNotes(ReBuzzCore buzz, PatternCore pattern, IParameter note, IParameter velocity) : base(buzz, pattern, note, velocity)
        {
            patternCore = pattern;
            machine = (MachineCore)pattern.Machine;
        }

        /// <summary>The tracker's notes of a pattern, or null: the machine has no Note track parameter or its editor can't take note edits.</summary>
        public static EditorTrackerNotes ForEditor(ReBuzzCore buzz, IPattern pattern)
        {
            if (pattern is not PatternCore pc || pc.Machine is not MachineCore m || !buzz.MachineManager.HasTrackerEditor(m)) return null;
            var (note, velocity) = Parameters(pattern);
            return note == null ? null : new EditorTrackerNotes(buzz, pc, note, velocity);
        }

        /// <summary>The Note parameter's name, for messages.</summary>
        public string NoteColumnName => noteParameter.Name;

        bool IsParameter(IParameter column, IParameter wanted) =>
            column != null && (ReferenceEquals(column, wanted)
                || (column.Group?.Type == ParameterGroupType.Track && column.IndexInGroup == wanted.IndexInGroup && column.Name == wanted.Name));

        // The editor hands over every column's events at each call: one call a read or an edit.
        List<IPatternEditorColumn> snapshot;

        List<IPatternEditorColumn> Columns() => snapshot ?? patternCore.PatternEditorMachineEvents.ToList();

        /// <summary>The editor's columns of one track parameter, in track order.</summary>
        List<IPatternEditorColumn> ColumnsOf(IParameter parameter) =>
            Columns().Where(c => IsParameter(c.Parameter, parameter)).ToList();

        T Snapshot<T>(Func<T> run)
        {
            if (snapshot != null) return run();
            snapshot = patternCore.PatternEditorMachineEvents.ToList();
            try { return run(); }
            finally { snapshot = null; }
        }

        public override List<ClipNote> Notes() => IsLive ? Snapshot(() => base.Notes()) : new List<ClipNote>();

        protected override string Apply(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added) => Snapshot(() => base.Apply(removed, added));

        protected override List<PatternEvent> Events(ColumnKey key)
        {
            var parameter = key.Index == noteParameter.IndexInGroup ? noteParameter : velocityParameter;
            if (parameter == null) return new List<PatternEvent>();
            var columns = ColumnsOf(parameter);
            return key.Track < columns.Count ? columns[key.Track].Events.OrderBy(e => e.Time).ToList() : new List<PatternEvent>();
        }

        protected override int TracksWithColumns() => Math.Max(Tracks, ColumnsOf(noteParameter).Count);

        RowGrid Grid()
        {
            var rows = buzz.MachineManager.GetTrackerBeatRows(machine, patternCore, noteParameter.IndexInGroup, 0);
            return RowGrid.Beats(rows ?? Array.Empty<int>());
        }

        /// <summary>The row length at a time, for the piano roll's grid (null when the editor has no such column).</summary>
        public int? RowTimeAt(int time)
        {
            var rows = buzz.MachineManager.GetTrackerBeatRows(machine, patternCore, noteParameter.IndexInGroup, 0);
            if (rows == null) return null;
            var grid = RowGrid.Beats(rows);
            return grid.Length(grid.RowAt(time));
        }

        protected override ClipNote Fit(ClipNote n)
        {
            var grid = Grid();
            int time = Math.Min(grid.Start(grid.RowAt(n.Time)), Math.Max(0, PatternTime - 1));
            time = grid.Start(grid.RowAt(time));
            int end = Math.Min(n.Time + Math.Max(1, n.Duration), PatternTime);
            int endRow = end >= PatternTime ? PatternTime : grid.Start(grid.RowAt(end));
            if (endRow <= time) endRow = Math.Min(PatternTime, grid.Start(grid.RowAt(time) + 1));
            return n with { Time = time, Duration = endRow - time };
        }

        protected override string Commit(List<ColumnEventsAction.Change> changes)
        {
            var records = new List<int>();
            int last = PatternTime - PatternEvent.TimeBase;
            // Notes whose note-on is new: their velocity is written whatever it is; for the others (a velocity the tracker
            // leaves to the machine's default, read as the default) an unchanged default isn't written back.
            var newOns = changes.Where(c => c.Column.Index == noteParameter.IndexInGroup)
                .SelectMany(c => c.Added.Where(e => e.Value != TrackNotes.Off).Select(e => (c.Column.Track, e.Time))).ToHashSet();
            int? defaultRaw = velocityParameter is { } v && v.MaxValue > v.MinValue
                ? v.MinValue + (int)Math.Round((v.MaxValue - v.MinValue) * NoteList.DefaultVelocity / 127.0) : null;
            foreach (var c in changes)
            {
                foreach (var e in c.Removed) AddRecord(records, c.Column, 0, e);
                bool isNote = c.Column.Index == noteParameter.IndexInGroup;
                var old = !isNote && c.Column.Index == velocityParameter?.IndexInGroup ? Events(c.Column) : null;
                foreach (var e in c.Added)
                {
                    // The off the engine writes on a note's last row (its own columns need it) isn't one the tracker needs: such a note runs on.
                    if (isNote && e.Value == TrackNotes.Off && e.Time == last) continue;
                    if (old != null && e.Value == defaultRaw && !newOns.Contains((c.Column.Track, e.Time)) && !old.Any(o => o.Time == e.Time)) continue;
                    AddRecord(records, c.Column, 1, e);
                }
            }
            if (records.Count == 0) return null;
            if (!buzz.MachineManager.EditTrackerEvents(machine, patternCore, records.ToArray()))
                return $"{pattern.Machine.Name}'s Tracker has no {noteParameter.Name} column for these tracks.";
            return null;
        }

        static void AddRecord(List<int> records, ColumnKey key, int add, PatternEvent e)
        {
            records.Add(key.Index);
            records.Add(key.Track);
            records.Add(add);
            records.Add(e.Time);
            records.Add(e.Value);
        }

        protected override void DoUndo() => buzz.MachineManager.EditorHistory(machine, redo: false);

        protected override void DoRedo() => buzz.MachineManager.EditorHistory(machine, redo: true);
    }
}
