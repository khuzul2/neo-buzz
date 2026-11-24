#nullable disable
// Fork addition (neo-buzz, docs/specs/clips.md §7.2, wave CU2): the derived blob. Lanes become what Classic and upstream read: the
// Modern Pattern Editor's data (version 1, exact times) or PatternXP's (version 3, rows), with a stamp, so that a song saved by the
// Studio still plays its tracker data there.
//
// The double-play rule: the Notes lane of a MIDI machine stays in the MIDI column only (upstream plays that column), so the blob gets no
// MIDI lanes for it; a parameter column the engine plays (the session mark) stays out too (upstream plays those from the song's own
// columns). Notes and parameters of track machines go in. Each of these, and each loss the format makes (a ramp written as steps at
// rows, an event between rows written on its row, the column mute), is a log line with its cause tag.
//
// The stamp is a hash of the store content the blob came from (ClipContent.Hash). A blob that Classic changed and saved no longer
// matches the store, and on the next open the blob wins (§7.2). Nothing calls this in the app yet (CU3 does).

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;

namespace BuzzGUI.Common
{
    public static class ClipExport
    {
        /// <summary>The blob for the clips of one pattern editor (the clips of one machine): MPE's format, or PatternXP's version 3 when <paramref name="pxp"/>.</summary>
        public static byte[] ToBlob(IEnumerable<ClipContent> clips, IClipMachines machines, bool pxp, ClipLog log)
        {
            var list = clips.ToList();
            var patterns = new List<BlobPattern>();
            foreach (var clip in list)
            {
                int beats = clip.Beats;
                var pattern = new BlobPattern { Name = clip.Name, Beats = beats, RowsPerBeat = Math.Max(1, clip.RowsPerBeat) };
                foreach (var lane in clip.Lanes)
                {
                    string where = lane.Kind == ClipLaneKind.Notes ? "notes" : $"{lane.Group}.{lane.Index}.{lane.Track}";
                    if (lane.Kind == ClipLaneKind.Notes)
                    {
                        log.Info(ClipTags.MidiNotInBlob, lane.Machine, clip.Name, where, lane.Events.Count, "the Notes lane stays in the MIDI column");
                        continue;
                    }
                    if (lane.Auto)
                    {
                        log.Info(ClipTags.AutoNotInBlob, lane.Machine, clip.Name, where, lane.Events.Count, "the engine plays it, from the song's own columns");
                        continue;
                    }
                    var machine = machines.Find(lane.Machine);
                    if (machine == null || (lane.Group == 2 && lane.Index >= machine.TrackParamCount) || (lane.Group == 1 && lane.Index >= machine.GlobalCount)
                        || (lane.Group != 1 && lane.Group != 2))
                    {
                        log.Loss(ClipTags.LaneNotInBlob, lane.Machine, clip.Name, where, lane.Events.Count, "the machine or the parameter is not known");
                        continue;
                    }
                    if (lane.Mute)
                        log.Loss(ClipTags.MuteNotInBlob, lane.Machine, clip.Name, where, lane.Events.Count, "the column's mute is not in the blob");

                    var beatRows = ClipGrid.BeatRows(lane.Grid, beats, pattern.RowsPerBeat);
                    var column = new BlobColumn
                    {
                        Machine = lane.Machine,
                        ParamIndex = lane.Group == 1 ? lane.Index : machine.GlobalCount + lane.Index,
                        Track = lane.Track,
                        BeatRows = beatRows,
                    };
                    int ramps = lane.Events.Count(e => e.Duration > 0);
                    var events = ramps > 0 ? Sampled(lane, beatRows, out _) : lane.Events.Select(e => new PatternEvent(e.Time, e.Value)).ToList();
                    if (ramps > 0)
                        log.Loss(ClipTags.RampSampled, lane.Machine, clip.Name, where, ramps, "written as steps at the rows");
                    column.Events = events.Where(e => clip.Length <= 0 || e.Time < clip.Length * PatternEvent.TimeBase).ToList();
                    pattern.Columns.Add(column);
                }
                patterns.Add(pattern);
            }

            var stamp = ClipContent.Hash(list);
            if (pxp)
            {
                var bytes = ClipBlob.WritePxp(patterns, out int moved, stamp);
                if (moved > 0)
                    log.Loss(ClipTags.ExportQuantized, null, null, null, moved, "an event between rows, written on its row");
                return bytes;
            }
            return ClipBlob.WriteMpe(patterns, stamp);
        }

        /// <summary>A lane with ramps as steps: each ramp (an event with a duration above 0, to the next event) at the start of every row it passes, on the line, rounded as the engine rounds it.</summary>
        static List<PatternEvent> Sampled(ClipLane lane, List<int> beatRows, out int added)
        {
            var sorted = lane.Events.OrderBy(e => e.Time).ToList();
            var result = new List<PatternEvent>();
            var grid = RowGrid.Beats(beatRows);
            added = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                var e = sorted[i];
                result.Add(new PatternEvent(e.Time, e.Value));
                if (e.Duration <= 0 || i + 1 >= sorted.Count) continue;
                var next = sorted[i + 1];
                if (next.Time <= e.Time) continue;
                for (int row = grid.RowAt(e.Time) + 1; ; row++)
                {
                    int t = grid.Start(row);
                    if (t >= next.Time) break;
                    long span = (long)next.Time - e.Time;
                    long rise = (long)(next.Value - e.Value) * (t - e.Time);
                    int v = e.Value + (int)(rise >= 0 ? (rise + span / 2) / span : -((-rise + span / 2) / span));
                    result.Add(new PatternEvent(t, v));
                    added++;
                }
            }
            return result;
        }
    }
}
