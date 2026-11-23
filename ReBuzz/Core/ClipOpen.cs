// Fork (docs/specs/clips.md §7.1, CU3): migration on open. After a song is loaded (machines, then the engine columns), every machine that has
// patterns has its clips moved into the store: the built-in rows (PATT), PatternXP's data and the Modern Pattern Editor's blob become lanes
// (ClipMigration), what does not convert is dropped with a log line, the machine's pattern editor is told to view the store, and the song is
// marked as migrated, in memory. The file is never written here. A song that already holds its lanes in the store (a .nbl of format 3, a
// .bmx or .bmxml whose derived blob carries the stamp of the store) is not migrated again: the store is the truth.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core
{
    /// <summary>What opening a song did to its clips: the log of every step, for the loss report (the panel after open) and the change report.</summary>
    internal sealed class ClipOpenReport
    {
        public ClipLog Log { get; } = new ClipLog();
        public int MachinesMigrated, MachinesInStore, ClipsMigrated, LanesWritten;
        /// <summary>The blob of a machine did not match the store (an older program changed it): the blob won.</summary>
        public List<string> StaleBlobs { get; } = new List<string>();

        /// <summary>The entries that name something dropped: the panel shows when there is one.</summary>
        public IEnumerable<ClipLogEntry> Losses => Log.Entries.Where(e => e.Kind == ClipLogKind.Loss && e.Count > 0);

        public bool HasLoss => Losses.Any();

        /// <summary>The report in plain words, by cause tag: what was dropped first, then what changed. One line a tag.</summary>
        public IReadOnlyList<(bool Loss, string Text)> Lines()
        {
            var lines = new List<(bool, string)>();
            foreach (var kind in new[] { ClipLogKind.Loss, ClipLogKind.Change })
                foreach (var g in Log.Entries.Where(e => e.Kind == kind && e.Count > 0).GroupBy(e => e.Tag).OrderBy(g => g.Key, StringComparer.Ordinal))
                    lines.Add((kind == ClipLogKind.Loss, ClipWords.Say(g.Key, g.Sum(e => e.Count))));
            foreach (var m in StaleBlobs)
                lines.Add((false, $"{m}: its pattern data was changed by an older program, so it was read again from that data."));
            return lines;
        }
    }

    internal static class ClipOpen
    {
        /// <summary>The raised event: a song was opened and its clips migrated. The Studio shows the report when it has a loss.</summary>
        public static event Action<ClipOpenReport> Opened;

        /// <summary>
        /// Moves the clips of every machine into the store. <paramref name="lanesInStore"/>: the file says its lanes are in the store (a .nbl of
        /// format 3), so no machine is migrated, only attached. Runs on the thread that opened the song, with the song stopped.
        /// </summary>
        public static ClipOpenReport Run(ReBuzzCore buzz, bool lanesInStore)
        {
            var song = buzz.SongCore;
            var report = new ClipOpenReport();
            foreach (var machine in song.MachinesList.ToList())
            {
                if (machine.PatternsList.Count == 0) continue;
                try
                {
                    Migrate(buzz, song, machine, lanesInStore, report);
                }
                catch (Exception e)
                {
                    // A song that cannot be migrated opens as it was read: the editor keeps its own data (and plays it only in Classic).
                    buzz.DCWriteLine($"Clips of {machine.Name} could not be moved to the store: {e.Message}", DCLogLevel.Warning);
                }
            }
            song.ClipOpen = report;
            Opened?.Invoke(report);
            return report;
        }

        static void Migrate(ReBuzzCore buzz, SongCore song, MachineCore machine, bool lanesInStore, ClipOpenReport report)
        {
            byte[] data = ClipStore.EditorData(machine);
            bool current = lanesInStore || (data != null && data.Length > 0 && ClipStore.BlobIsCurrent(machine, data));
            bool stale = !lanesInStore && data != null && data.Length > 0 && ClipBlob.StampOf(data) != null && !current;
            if (current)
                report.MachinesInStore++;
            else
            {
                var log = new ClipLog();
                var result = ClipStore.MigrateForOpen(machine, song, log, data);
                int lanes = 0;
                foreach (var clip in result.Clips)
                {
                    var pattern = machine.PatternsList.FirstOrDefault(p => p.Name == clip.Name);
                    if (pattern == null)
                    {
                        log.Loss(ClipTags.PatternMissing, machine.Name, clip.Name, null, clip.Lanes.Sum(l => l.Events.Count), "no such pattern in the song");
                        continue;
                    }
                    lanes += ClipStore.Write(pattern, clip, song, log, mergeNotes: true);
                    report.ClipsMigrated++;
                }
                report.LanesWritten += lanes;
                if (result.EventsIn > 0 || lanes > 0) { report.MachinesMigrated++; song.ClipsMigrated = true; }
                report.Log.AddRange(log);
                if (stale) report.StaleBlobs.Add(machine.Name);
            }
            if (buzz.MachineManager.AttachClipStore(machine))
                machine.ClipsAttached = true;
            if (machine.EditorMachine != null) machine.EditorMachine.LoadedEditorData = null;
        }
    }
}
