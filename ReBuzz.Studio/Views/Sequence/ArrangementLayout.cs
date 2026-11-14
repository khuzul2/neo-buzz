// Fork (docs/specs/daw-modes.md AR1, §4.1): the arrangement's rows. The Master's tempo row and
// markers row on top, then a track for each machine with a sequence, its lanes (its sequences)
// one under the other; a folded track is one short row. Heights are in pixels, tops from the top
// of the tracks area.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.Studio.Views.Sequence
{
    internal enum ArrangementRowKind { Tempo, Markers, Lane, Parameter }

    internal sealed class ArrangementRow
    {
        public ArrangementRowKind Kind { get; init; }
        public IMachine Machine { get; init; }
        /// <summary>The lane's sequence (null for the Master's rows and for a folded track's row).</summary>
        public ISequence Lane { get; init; }
        /// <summary>The track's lanes (a folded track's row shows all of them).</summary>
        public IReadOnlyList<ISequence> Lanes { get; init; } = Array.Empty<ISequence>();
        /// <summary>1-based place among the machine's lanes.</summary>
        public int LaneNumber { get; init; }
        public bool Folded { get; init; }
        public double Top { get; set; }
        public double Height { get; init; }
        public double Bottom => Top + Height;
        public bool IsFirstLane => LaneNumber == 1;
        /// <summary>For a parameter row (AR4): the state parameter whose automation it shows.</summary>
        public AutoParam Param { get; init; }
    }

    internal static class ArrangementLayout
    {
        public const double TempoHeight = 44, MarkersHeight = 22, LaneHeight = 64, FoldedHeight = 28, ParameterHeight = 72;

        /// <summary>The rows for the song's sequences, grouped by machine in the order of each machine's first sequence (the Master's first).</summary>
        public static List<ArrangementRow> Build(ISong song, ArrangementStore store)
        {
            var rows = new List<ArrangementRow>
            {
                new ArrangementRow { Kind = ArrangementRowKind.Tempo, Height = TempoHeight },
                new ArrangementRow { Kind = ArrangementRowKind.Markers, Height = MarkersHeight },
            };
            var groups = song.Sequences.Where(s => s.Machine != null).GroupBy(s => s.Machine).ToList();
            groups = groups.Where(g => IsMaster(g.Key)).Concat(groups.Where(g => !IsMaster(g.Key))).ToList();
            foreach (var g in groups)
            {
                var lanes = g.ToList();
                bool folded = store?.IsFolded(g.Key.Name) == true;
                if (folded)
                {
                    rows.Add(new ArrangementRow { Kind = ArrangementRowKind.Lane, Machine = g.Key, Lanes = lanes, LaneNumber = 1, Folded = true, Height = FoldedHeight });
                    continue;
                }
                for (int i = 0; i < lanes.Count; i++)
                    rows.Add(new ArrangementRow { Kind = ArrangementRowKind.Lane, Machine = g.Key, Lane = lanes[i], Lanes = lanes, LaneNumber = i + 1, Height = LaneHeight });
                // AR4: the parameters chosen for this track, under its lanes.
                if (store != null)
                    foreach (var (machine, key) in store.ParameterLanes)
                        if (machine == g.Key.Name && AutomationParams.Find(g.Key, key) is { } param)
                            rows.Add(new ArrangementRow { Kind = ArrangementRowKind.Parameter, Machine = g.Key, Lanes = lanes, Param = param, Height = ParameterHeight });
            }
            double top = 0;
            foreach (var r in rows)
            {
                r.Top = top;
                top += r.Height;
            }
            return rows;
        }

        static bool IsMaster(IMachine m) => m.DLL?.Info.Type == MachineType.Master;

        public static double TotalHeight(IReadOnlyList<ArrangementRow> rows) => rows.Count == 0 ? 0 : rows[^1].Bottom;
    }

    /// <summary>The tempo changes the Master's patterns make, for the tempo row: where (song ticks) and the BPM from there on.</summary>
    internal static class TempoLane
    {
        public static List<(int Tick, int Bpm)> Changes(ISong song)
        {
            var list = new List<(int, int)>();
            try
            {
                foreach (var seq in song.Sequences.Where(s => s.Machine?.DLL?.Info.Type == MachineType.Master))
                    foreach (var (time, e) in seq.Events)
                    {
                        if (e.Type != SequenceEventType.PlayPattern || e.Pattern == null) continue;
                        int from = e.PlayedOffset, to = e.PlayedOffset + e.PlayedLength;
                        foreach (var (tick, bpm) in BpmEvents(e.Pattern))
                            if (tick >= from && tick < to) list.Add((time + tick - from, bpm));
                    }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NullReferenceException || ex is KeyNotFoundException)
            {
                // A pattern that can't be read has no tempo to show.
            }
            return list.OrderBy(x => x.Item1).ToList();
        }

        static IEnumerable<(int Tick, int Bpm)> BpmEvents(IPattern pattern)
        {
            foreach (var column in pattern.Columns.Where(c => c.Type == PatternColumnType.Parameter && c.Parameter?.Name == "BPM"))
                foreach (var e in column.GetEvents(0, pattern.Length * PatternEvent.TimeBase))
                    if (e.Value != column.Parameter.NoValue) yield return (e.Time / PatternEvent.TimeBase, e.Value);
            foreach (var column in pattern.PatternEditorMachineEvents ?? Enumerable.Empty<IPatternEditorColumn>())
                if (column.Parameter?.Name == "BPM")
                    foreach (var e in column.Events)
                        if (e.Value != column.Parameter.NoValue) yield return (e.Time / PatternEvent.TimeBase, e.Value);
        }
    }
}
