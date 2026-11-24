// Fork (docs/specs/clips.md §10, wave CU2): the converter and the export, round-tripped on the whole corpus (tools/clip-null.py list: every
// repo song and every generated song). For each machine of a song that holds tracker data:
//   - blob -> store -> blob: the editor's data (the machine's pattern editor, or the song's built-in rows) is migrated to lanes, the lanes are put
//     in the pattern's columns (the engine's store), read back, and exported as a blob: every parameter column comes out with the events it went in
//     with (and the rows of its beats), the MIDI lanes become the Notes lane and stay out of the blob (the double-play rule), the stamp is the
//     hash of the store;
//   - store -> blob -> store: the exported blob migrated again gives the same lanes (the Notes lane aside, which is in the MIDI column only);
//   - the log: every event of the source is kept, merged into notes or dropped (nothing missing), and every dropped column has a log line with a
//     cause tag.
// Nothing here plays: migrated notes in the MIDI column would play with the editor's, which is why nothing calls the converter on open before CU3.
//
// The generated songs are built in the process (ClipCorpus); the repo songs are opened as the app opens them; when CLIP_CORPUS_DIR names the work
// directory of tools/clip-null.py (its songs/ folder) those generated files are opened too. CLIP_MIGRATION_LOG names a file for the migration log of
// every song (the change report reads it), CLIP_ROUNDTRIP_OUT one for the table below.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Headless.NUnit;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    public class ClipRoundTripTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        sealed class Stats
        {
            public int Songs, Opened, Machines, Patterns, Columns, EventsIn, Kept, Merged, Dropped, Lanes, NotesLanes, Offs;
        }

        // What the corpus tests collect, written out after each (they run one after another).
        static readonly SortedDictionary<string, List<ClipLogEntry>> Logs = new SortedDictionary<string, List<ClipLogEntry>>(StringComparer.Ordinal);
        static readonly SortedDictionary<string, string> Table = new SortedDictionary<string, string>(StringComparer.Ordinal);

        static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "tools", "clip-null.py")))
                    return dir.FullName;
            throw new InvalidOperationException("the repo's tools/clip-null.py is not above " + AppContext.BaseDirectory);
        }

        /// <summary>The repo's song files, named as clip-null.py names them.</summary>
        static SortedDictionary<string, string> RepoSongs()
        {
            string root = RepoRoot();
            var songs = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var b in new[] { "ExternalMachines", "Other" })
            {
                string dir = Path.Combine(root, b);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Walk(dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is ".bmx" or ".bmw" or ".nbl" or ".bmxml")
                        songs["repo/" + Path.GetFileName(f)] = f;
                }
            }
            return songs;
        }

        static IEnumerable<string> Walk(string dir)
        {
            foreach (var f in Directory.EnumerateFiles(dir)) yield return f;
            foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
            {
                string n = Path.GetFileName(d);
                if (n is "bin" or "obj" or ".git") continue;
                foreach (var f in Walk(d)) yield return f;
            }
        }

        /// <summary>Songs that do not open on any platform (their machines are missing), as the goldens say.</summary>
        static HashSet<string> CannotOpen()
        {
            var set = new HashSet<string>();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "eval", "clip-goldens.json")));
                foreach (var platform in doc.RootElement.GetProperty("platforms").EnumerateObject())
                    foreach (var song in platform.Value.GetProperty("songs").EnumerateObject())
                        if (song.Value.TryGetProperty("status", out var st) && st.GetString() == "open failed")
                            set.Add(song.Name);
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException) { }
            return set;
        }

        static bool Open(string path)
        {
            AudioHygieneTests.NewSong();
            Core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            try { return Core.TryOpenSongFile(path); }
            finally { Dialogs.Responder = null; }
        }

        static string Where(ClipLogEntry e) => $"{e.Machine}/{e.Clip}/{e.Lane}";

        static string Describe(IEnumerable<PatternEvent> events) => string.Join(" ", events.Take(6).Select(e => $"{e.Time}:{e.Value}" + (e.Duration != 0 ? $"+{e.Duration}" : ""))) + (events.Count() > 6 ? " ..." : "");

        static List<PatternEvent> Sorted(IEnumerable<PatternEvent> e) => e.OrderBy(x => x.Time).ThenBy(x => x.Value).ThenBy(x => x.Duration).ToList();

        static bool Same(IEnumerable<PatternEvent> a, IEnumerable<PatternEvent> b) => Sorted(a).SequenceEqual(Sorted(b));

        sealed class Plain
        {
            public string Machine;
            public int Index, Track;
            public List<PatternEvent> Events = new List<PatternEvent>();
            public List<int> BeatRows;
            public string Key => $"{Machine}|{Index}|{Track}";
        }

        /// <summary>The columns of the source blob that must come out of the export, by an independent reading of the rules (not the converter's code).</summary>
        static Dictionary<string, Plain> PlainColumns(BlobPattern p, IClipMachines machines, int length)
        {
            var plain = new Dictionary<string, Plain>();
            int beats = Math.Max(1, (Math.Max(1, length) + 3) / 4);
            foreach (var c in p.Columns)
            {
                var m = machines.Find(c.Machine);
                if (m == null || c.ParamIndex < 0) continue;
                bool global = c.ParamIndex < m.GlobalCount && c.Track == 0;
                int index = global ? c.ParamIndex : c.ParamIndex - m.GlobalCount;
                if (!global && (index < 0 || index >= m.TrackParamCount || c.Track >= m.TrackCount || c.Track < 0)) continue;
                var col = plain.TryGetValue($"{c.Machine}|{c.ParamIndex}|{c.Track}", out var e) ? e : plain[$"{c.Machine}|{c.ParamIndex}|{c.Track}"] = new Plain { Machine = c.Machine, Index = c.ParamIndex, Track = c.Track };
                bool note = m.IsNote(global ? 1 : 2, index);
                foreach (var ev in c.Events)
                {
                    if (length > 0 && ev.Time >= length * PatternEvent.TimeBase) continue;
                    col.Events.Add(new PatternEvent(ev.Time, note ? ClipMigration.FixNote(ev.Value) : ev.Value));
                }
                var rows = new List<int>();
                for (int b = 0; b < beats; b++) rows.Add(b < c.BeatRows.Count ? c.BeatRows[b] : Math.Max(1, p.RowsPerBeat));
                col.BeatRows = rows;
            }
            return plain;
        }

        /// <summary>One song of the session, all of its machines. Returns the problems found; adds to the log and the stats.</summary>
        static List<string> CheckSong(string name, Dictionary<string, byte[]> blobs, ClipLog log, Stats stats)
        {
            var problems = new List<string>();
            void Problem(string text) => problems.Add($"{name}: {text}");
            var song = Core.SongCore;
            var machines = ClipStore.Machines(song);
            foreach (var machine in song.MachinesList.ToList())
            {
                if (machine.PatternsList.Count == 0) continue;
                stats.Machines++;
                stats.Patterns += machine.PatternsList.Count;
                byte[] data = blobs != null && blobs.TryGetValue(machine.Name, out var given) ? given : blobs != null ? null : ClipStore.EditorData(machine);
                bool builtIn = machine.PatternsList.Any(p => p.BuiltInData != null);
                var lengths = machine.PatternsList.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().Length);
                int lengthOf(string n) => lengths.TryGetValue(n, out int l) ? l : 0;

                // ---- blob -> store
                var mlog = new ClipLog();
                var result = ClipStore.Migrate(machine, song, mlog, data);
                log.AddRange(mlog);
                if (!result.Conserved)
                    Problem($"{machine.Name}: events in {result.EventsIn} != kept {result.EventsKept} + merged {result.EventsMerged} + dropped {result.EventsDropped}");
                var parsed = ClipBlob.Parse(data);
                int lostInput = machine.PatternsList.Where(p => p.BuiltInData == null && p.LostInputCells != null).Sum(p => p.LostInputCells.Values.Sum());
                if (!builtIn && data != null && data.Length > 0 && parsed.Problem == null && result.EventsIn - lostInput != parsed.EventCount)
                    Problem($"{machine.Name}: the converter counted {result.EventsIn - lostInput} events in, the blob holds {parsed.EventCount}");
                stats.EventsIn += result.EventsIn; stats.Kept += result.EventsKept; stats.Merged += result.EventsMerged; stats.Dropped += result.EventsDropped;
                stats.Columns += parsed.Patterns.Sum(p => p.Columns.Count);

                var wlog = new ClipLog();
                foreach (var clip in result.Clips)
                {
                    var pattern = machine.PatternsList.FirstOrDefault(p => p.Name == clip.Name);
                    if (pattern == null)
                    {
                        wlog.Loss(ClipTags.PatternMissing, machine.Name, clip.Name, null, clip.Lanes.Sum(l => l.Events.Count), "no such pattern in the song");
                        continue;
                    }
                    ClipStore.Write(pattern, clip, song, wlog);
                    var read = ClipStore.Read(pattern);
                    foreach (var lane in clip.Lanes)
                    {
                        stats.Lanes++;
                        if (lane.Kind == ClipLaneKind.Notes) { stats.NotesLanes++; stats.Offs += lane.Events.Count(ClipEvents.IsExplicitOff); }
                        var back = read.Lanes.FirstOrDefault(l => l.Key == lane.Key);
                        if (back == null) { Problem($"{machine.Name}/{clip.Name}: the lane {lane.Key} is not in the pattern's columns after writing"); continue; }
                        if (!Same(lane.Events, back.Events)) Problem($"{machine.Name}/{clip.Name}: the lane {lane.Key} reads back as {Describe(Sorted(back.Events))}, it was {Describe(Sorted(lane.Events))}");
                        if (back.Grid != lane.Grid || back.Mute != lane.Mute) Problem($"{machine.Name}/{clip.Name}: the lane {lane.Key} lost its Grid or Mute ({back.Grid} / {lane.Grid})");
                    }
                }
                foreach (var e in wlog.Entries)
                    if (e.Kind == ClipLogKind.Loss) { log.Loss(e.Tag, e.Machine, e.Clip, e.Lane, e.Count, e.Detail); }

                // ---- the log: every dropped column has a line
                foreach (var p in parsed.Patterns)
                {
                    var plainKeys = PlainColumns(p, machines, lengthOf(p.Name));
                    var droppedLog = mlog.Entries.Where(e => e.Kind == ClipLogKind.Loss && e.Clip == p.Name).ToList();
                    foreach (var c in p.Columns)
                    {
                        bool midi = c.ParamIndex <= -128 && c.ParamIndex >= -133;
                        if (plainKeys.ContainsKey($"{c.Machine}|{c.ParamIndex}|{c.Track}") || midi) continue;
                        if (c.Events.Count == 0 && !droppedLog.Any(e => e.Lane == $"{c.ParamIndex}.{c.Track}")) continue;   // an empty column holds nothing to drop
                        if (!droppedLog.Any(e => e.Lane == $"{c.ParamIndex}.{c.Track}" && e.Machine == c.Machine))
                            Problem($"{machine.Name}/{p.Name}: the column {c.Machine} {c.ParamIndex}.{c.Track} ({c.Events.Count} events) is not in the store and not in the log");
                    }
                    foreach (var grp in p.Columns.Where(c => c.ParamIndex is -132 or -133).GroupBy(c => (c.Machine, c.Track)))
                        if (p.Columns.Any(c => c.ParamIndex == -128 && c.Machine == grp.Key.Machine && c.Track == grp.Key.Track && c.Events.Count > 0))
                            foreach (var c in grp.Where(c => c.Events.Count > 0))
                                if (!droppedLog.Any(e => e.Tag == ClipTags.CcLaneDropped && e.Lane == $"{c.ParamIndex}.{c.Track}"))
                                    Problem($"{machine.Name}/{p.Name}: the pitch wheel or CC lane {c.ParamIndex}.{c.Track} is on a track with a note lane and not in the log");
                }

                // ---- store -> blob
                var elog = new ClipLog();
                byte[] blobB = ClipStore.Export(machine, song, false, elog);
                var b = ClipBlob.Parse(blobB);
                if (b.Problem != null || b.Format != BlobFormat.Mpe1) { Problem($"{machine.Name}: the exported blob does not read ({b.Format}, {b.Problem})"); continue; }
                var expectStamp = ClipContent.Hash(machine.PatternsList.Select(ClipStore.Read));
                if (b.Stamp == null || !b.Stamp.SequenceEqual(expectStamp)) Problem($"{machine.Name}: the exported blob's stamp is not the store's hash");
                if (!b.Patterns.Select(p => p.Name).SequenceEqual(machine.PatternsList.Select(p => p.Name)))
                    Problem($"{machine.Name}: the exported blob holds patterns {string.Join(",", b.Patterns.Select(p => p.Name))}");
                foreach (var col in b.Patterns.SelectMany(p => p.Columns))
                    if (col.ParamIndex < 0) Problem($"{machine.Name}: the exported blob has an internal column {col.ParamIndex} (a MIDI lane: the double-play rule)");
                foreach (var p in parsed.Patterns)
                {
                    var bp = b.Patterns.FirstOrDefault(x => x.Name == p.Name);
                    if (bp == null) continue;
                    foreach (var want in PlainColumns(p, machines, lengthOf(p.Name)).Values)
                    {
                        var got = bp.Columns.Where(c => c.Machine == want.Machine && c.ParamIndex == want.Index && c.Track == want.Track).ToList();
                        if (want.Events.Count == 0 && got.Count == 0) continue;
                        if (got.Count != 1) { Problem($"{machine.Name}/{p.Name}: the column {want.Key} is in the exported blob {got.Count} times"); continue; }
                        if (!Same(got[0].Events, want.Events)) Problem($"{machine.Name}/{p.Name}: the column {want.Key} has {Describe(Sorted(got[0].Events))} after the round trip, it had {Describe(Sorted(want.Events))}");
                        if (!got[0].BeatRows.SequenceEqual(want.BeatRows)) Problem($"{machine.Name}/{p.Name}: the column {want.Key} has beat rows {string.Join(",", got[0].BeatRows)}, it had {string.Join(",", want.BeatRows)}");
                    }
                    // The MIDI lanes became notes and are in the MIDI column only: the log says so.
                    if (p.Columns.Any(c => c.ParamIndex == ClipMigration.MidiNote && c.Events.Count > 0) && !elog.Entries.Any(e => e.Tag == ClipTags.MidiNotInBlob && e.Clip == p.Name))
                        Problem($"{machine.Name}/{p.Name}: the Notes lane left the blob and the export does not say so");
                }

                // ---- store -> blob -> store
                var rlog = new ClipLog();
                var again = ClipMigration.FromBlob(blobB, machines, lengthOf, rlog, machine.Name);
                if (!again.Conserved) Problem($"{machine.Name}: the second migration lost count of events");
                if (rlog.Entries.Any(e => e.Kind == ClipLogKind.Loss)) Problem($"{machine.Name}: the exported blob migrates again with losses: {string.Join("; ", rlog.Entries.Where(e => e.Kind == ClipLogKind.Loss).Take(3).Select(e => e.Tag + " " + Where(e)))}");
                foreach (var clip in result.Clips)
                {
                    var c2 = again.Clips.FirstOrDefault(c => c.Name == clip.Name);
                    foreach (var lane in clip.Lanes.Where(l => l.Kind == ClipLaneKind.Parameter && !l.Auto))
                    {
                        var lane2 = c2?.Lanes.FirstOrDefault(l => l.Key == lane.Key);
                        if (lane2 == null) { if (lane.Events.Count > 0) Problem($"{machine.Name}/{clip.Name}: the lane {lane.Key} is not in the lanes migrated from the exported blob"); continue; }
                        if (!Same(lane.Events, lane2.Events)) Problem($"{machine.Name}/{clip.Name}: the lane {lane.Key} is {Describe(Sorted(lane2.Events))} after blob, store, blob, store; it was {Describe(Sorted(lane.Events))}");
                        if (lane.Grid != lane2.Grid) Problem($"{machine.Name}/{clip.Name}: the lane {lane.Key} has the grid {lane2.Grid} after the round trip, it had {lane.Grid}");
                    }
                    if (c2 != null && c2.Lanes.Any(l => l.Kind == ClipLaneKind.Notes))
                        Problem($"{machine.Name}/{clip.Name}: a Notes lane came back from the blob (the double-play rule leaves it in the MIDI column)");
                }

                // ---- PatternXP's format (rows): what each column holds after the rows' quantization, and the log says how many events moved
                var plog = new ClipLog();
                var px = ClipBlob.Parse(ClipStore.Export(machine, song, true, plog));
                if (px.Problem != null || px.Format != BlobFormat.Pxp3) { Problem($"{machine.Name}: the PatternXP blob does not read ({px.Format}, {px.Problem})"); continue; }
                int moved = 0;
                foreach (var p in parsed.Patterns)
                {
                    var pp = px.Patterns.FirstOrDefault(x => x.Name == p.Name);
                    var bp = b.Patterns.FirstOrDefault(x => x.Name == p.Name);
                    if (pp == null || bp == null) { Problem($"{machine.Name}/{p.Name}: not in the PatternXP blob"); continue; }
                    int rpb = Math.Max(1, bp.RowsPerBeat);
                    foreach (var want in bp.Columns)
                    {
                        var got = pp.Columns.FirstOrDefault(c => c.Machine == want.Machine && c.ParamIndex == want.ParamIndex && c.Track == want.Track);
                        if (got == null) { Problem($"{machine.Name}/{p.Name}: the column {want.Machine} {want.ParamIndex}.{want.Track} is not in the PatternXP blob"); continue; }
                        int rowLength = ClipBlob.PxpTime / rpb;
                        var expect = want.Events.Select(e => new PatternEvent(ClipBlob.Quantized(e.Time, rpb) / rowLength * (PatternEvent.TimeBase * 4) / rpb, e.Value)).ToList();
                        moved += want.Events.Count(e => ClipBlob.Quantized(e.Time, rpb) != e.Time);
                        if (!Same(got.Events, expect)) Problem($"{machine.Name}/{p.Name}: the column {want.ParamIndex}.{want.Track} in the PatternXP blob is {Describe(Sorted(got.Events))}, expected {Describe(Sorted(expect))}");
                    }
                }
                int loggedMoved = plog.Entries.Where(e => e.Tag == ClipTags.ExportQuantized).Sum(e => e.Count);
                if (loggedMoved != moved) Problem($"{machine.Name}: {moved} events moved to rows in the PatternXP blob, the log says {loggedMoved}");
                foreach (var e in plog.Entries.Where(e => e.Tag == ClipTags.ExportQuantized)) log.Loss(e.Tag, machine.Name, null, null, e.Count, "PatternXP's rows");
                foreach (var e in elog.Entries.Where(e => e.Kind == ClipLogKind.Loss || e.Tag == ClipTags.MidiNotInBlob || e.Tag == ClipTags.AutoNotInBlob)) log.Add(e.Tag, e.Kind, e.Machine, e.Clip, e.Lane, e.Count, e.Detail);
            }
            return problems;
        }

        static void Summarize(string title, Stats s, ClipLog log, List<string> problems)
        {
            var text = new StringBuilder();
            text.AppendLine($"== {title}: {s.Opened}/{s.Songs} songs opened, {s.Machines} machines with patterns, {s.Patterns} patterns, {s.Columns} columns of tracker data");
            text.AppendLine($"   events: in {s.EventsIn} = kept {s.Kept} + merged into notes {s.Merged} + dropped {s.Dropped}; lanes written {s.Lanes} ({s.NotesLanes} Notes lanes, {s.Offs} explicit note-offs)");
            foreach (var kv in log.ByTag())
                text.AppendLine($"   {kv.Key,-20} {kv.Value.Entries,5} entries {kv.Value.Events,8} events");
            foreach (var pr in problems)
                text.AppendLine("   PROBLEM " + pr);
            TestContext.Out.WriteLine(text.ToString());
            Table[title] = text.ToString();
            WriteOut();
        }

        static void WriteOut()
        {
            string path = Environment.GetEnvironmentVariable("CLIP_ROUNDTRIP_OUT");
            if (!string.IsNullOrEmpty(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllText(path, string.Concat(Table.Values));
            }
            path = Environment.GetEnvironmentVariable("CLIP_MIGRATION_LOG");
            if (!string.IsNullOrEmpty(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                var songs = Logs.ToDictionary(kv => kv.Key, kv => kv.Value.Select(e => new { tag = e.Tag, kind = e.Kind.ToString().ToLowerInvariant(), machine = e.Machine, clip = e.Clip, lane = e.Lane, count = e.Count, detail = e.Detail }).ToArray());
                File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1, songs }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        static List<string> Corpus(string title, IEnumerable<(string Name, Func<(bool Opened, Dictionary<string, byte[]> Blobs)> Prepare)> songs, ISet<string> mayNotOpen)
        {
            var all = new List<string>();
            var stats = new Stats();
            var total = new ClipLog();
            foreach (var (name, prepare) in songs)
            {
                stats.Songs++;
                var (opened, blobs) = prepare();
                if (!opened)
                {
                    if (mayNotOpen == null || !mayNotOpen.Contains(name)) all.Add($"{name}: does not open, and the goldens say it does");
                    Logs[name] = new List<ClipLogEntry>();
                    continue;
                }
                stats.Opened++;
                var log = new ClipLog();
                try { all.AddRange(CheckSong(name, blobs, log, stats)); }
                catch (Exception e) { all.Add($"{name}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
                total.AddRange(log);
                Logs[name] = log.Entries.ToList();
            }
            Summarize(title, stats, total, all);
            return all;
        }

        [AvaloniaTest]
        public void TheRepoSongsRoundTripAndEveryLossIsLogged()
        {
            var cannot = CannotOpen();
            var songs = RepoSongs().Select(kv => (kv.Key, (Func<(bool, Dictionary<string, byte[]>)>)(() => (Open(kv.Value), null))));
            var problems = Corpus("repo songs", songs, cannot);
            Assert.That(RepoSongs().Count, Is.GreaterThanOrEqualTo(20), "the repo's songs are found");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [AvaloniaTest]
        public void TheGeneratedSongsRoundTripAndEveryLossIsLogged()
        {
            var songs = ClipCorpus.Names.Select(n => (n, (Func<(bool, Dictionary<string, byte[]>)>)(() =>
            {
                AudioHygieneTests.NewSong();
                return (true, ClipCorpus.Build(n).Blobs);
            })));
            var problems = Corpus("generated songs", songs, null);
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [AvaloniaTest]
        public void TheSongsOfTheCorpusToolRoundTripToo()
        {
            string dir = Environment.GetEnvironmentVariable("CLIP_CORPUS_DIR");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(Path.Combine(dir, "songs")))
                Assert.Pass("CLIP_CORPUS_DIR does not name a clip-null.py work directory with songs/: only the in-process corpus ran");
            var files = Directory.GetFiles(Path.Combine(dir, "songs"), "*.bmxml").OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.That(files, Is.Not.Empty);
            var songs = files.Select(f => ("gen/" + Path.GetFileNameWithoutExtension(f), (Func<(bool, Dictionary<string, byte[]>)>)(() => (Open(f), null))));
            var problems = Corpus("clip-null.py's generated songs", songs, null);
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [AvaloniaTest]
        public void TheGeneratedCorpusHasEverySongOfTheCorpusTool()
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "clip-null.py"));
            var names = Regex.Matches(text, "@generated\\(\"(gen/[a-z0-9-]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
            Assert.That(text, Does.Contain("for _tpb in (1, 4, 8)"));
            foreach (int tpb in new[] { 1, 4, 8 }) names.Add($"gen/tpb{tpb}");
            Assert.That(ClipCorpus.Names.OrderBy(n => n).ToList(), Is.EqualTo(names.OrderBy(n => n).ToList()), "tools/clip-null.py's generated songs and ClipCorpus.Names");
        }
    }
}
