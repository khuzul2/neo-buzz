// Fork (docs/specs/native-format.md, NF2): "Export for Buzz/ReBuzz (.bmx)". The names read in Latin-1; take lanes are written
// empty in the core sequence data and kept in the "ExportedLanes" subsection, so NeoBuzz Live reopens the takes exactly; the
// loss report lists what upstream can't hold; a simulated upstream read (the fork-only subsections dropped) renders as the song
// does minus those losses; the open song is untouched; the studio's flow and the agent's song_export.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.FileOps;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Commands;
using ReBuzzTests.Automation;

namespace ReBuzz.Studio.Tests
{
    public class BuzzExportTests
    {
        const string LeadName = "Léad → ś";
        const string StressName = "Stress → ü";
        const string PatternName = "Pad ← ★";
        const int TB = PatternEvent.TimeBase;

        static string Work(string name)
        {
            string dir = TestTemp.Path($"buzz-export-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        static void Reopen(ReBuzzCore core, string path)
        {
            core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Assert.That(core.TryOpenSongFile(path), Is.True, "opens " + Path.GetFileName(path));
            Dialogs.Responder = null;
        }

        // ------------------------------------------------------------------ names

        [Test]
        public void NamesReadInLatin1WhereverTheyCan()
        {
            Assert.That(Latin1Names.Transliterate("Vel → amp"), Is.EqualTo("Vel -> amp"));
            Assert.That(Latin1Names.Transliterate("Bass ← Lead ↔ Pad"), Is.EqualTo("Bass <- Lead <-> Pad"));
            Assert.That(Latin1Names.Transliterate("Café Müller ß µs ×2"), Is.EqualTo("Café Müller ß µs ×2"), "what Latin-1 has stays");
            Assert.That(Latin1Names.Transliterate("Łódź śmiech"), Is.EqualTo("Lódz smiech"), "letters with a mark Latin-1 hasn't");
            Assert.That(Latin1Names.Transliterate("“quoted” – dash… €5 ™"), Is.EqualTo("\"quoted\" - dash... EUR5 (TM)"));
            Assert.That(Latin1Names.Transliterate("ﬁne"), Is.EqualTo("fine"), "compatibility forms");
            Assert.That(Latin1Names.Transliterate("音楽 ☃ 😀"), Is.EqualTo("?? ? ?"), "a question mark only as a last resort, one a character");
            Assert.That(Latin1Names.Transliterate("Plain"), Is.EqualTo("Plain"));
            Assert.That(Latin1Names.Transliterate(null), Is.Null);
            foreach (char c in Latin1Names.Transliterate("Δ→ś≈♯Ω…€∞"))
                Assert.That((int)c, Is.LessThanOrEqualTo(0xFF));
        }

        [Test]
        public void TheReportIsTypedSortedAndStable()
        {
            var a = new ExportReport(new[] { new ExportLoss("Takes", "b", "x"), new ExportLoss("Clip trims", "z", "y"), new ExportLoss("Takes", "a", "x"), new ExportLoss("Takes", "a", "x") });
            Assert.That(a.Losses.Select(l => l.Category + "/" + l.Item), Is.EqualTo(new[] { "Clip trims/z", "Takes/a", "Takes/b" }), "sorted, no duplicates");
            Assert.That(a.ToText(), Is.EqualTo("Clip trims | z | y\nTakes | a | x\nTakes | b | x"));
            var b = new ExportReport(a.Losses.Reverse());
            Assert.That(b.ToText(), Is.EqualTo(a.ToText()), "the same text whatever the order they were found in");
            Assert.That(ExportReport.Empty.IsEmpty, Is.True);
            Assert.That(ExportReport.Empty.ToText(), Is.Empty);
        }

        [Test]
        public void ATargetIsABmxThatIsNotTheOpenSongsFile()
        {
            string song = Path.GetFullPath(Work("own.bmx"));
            Assert.That(BuzzExport.TargetProblem(song, Work("other.bmx")), Is.Null);
            Assert.That(BuzzExport.TargetProblem(song, Work("other.bmw")), Is.Null);
            Assert.That(BuzzExport.TargetProblem(song, Work("x.nbl")), Does.Contain(".nbl"));
            Assert.That(BuzzExport.TargetProblem(song, Work("x.bmxml")), Does.Contain(".bmx"));
            Assert.That(BuzzExport.TargetProblem(song, song), Does.Contain("own file"));
            Assert.That(BuzzExport.TargetProblem(null, Work("fresh.bmx")), Is.Null);
            Assert.That(BuzzExport.TargetProblem(song, ""), Is.Not.Null);
        }

        // ------------------------------------------------------------------ the song with everything

        /// <summary>The sequences of a .bmx: per sequence its machine index and its events (position, value).</summary>
        static List<(int Machine, List<(int Pos, int Value)> Events)> ReadSequences(byte[] bmx)
        {
            var sections = Sections(bmx);
            var d = sections.First(s => s.Magic == (uint)BMXFile.SectionType.SEQU).Data;
            var r = new BinaryReader(new MemoryStream(d));
            r.ReadInt32(); r.ReadInt32(); r.ReadInt32();
            int n = r.ReadUInt16();
            var list = new List<(int, List<(int, int)>)>();
            for (int i = 0; i < n; i++)
            {
                int machine = r.ReadUInt16();
                uint events = r.ReadUInt32();
                var e = new List<(int, int)>();
                if (events > 0)
                {
                    byte ps = r.ReadByte(), es = r.ReadByte();
                    for (int j = 0; j < events; j++)
                    {
                        int pos = ps == 4 ? (int)r.ReadUInt32() : r.ReadUInt16();
                        int val = es == 4 ? (int)r.ReadUInt32() : r.ReadUInt16();
                        e.Add((pos, val));
                    }
                }
                list.Add((machine, e));
            }
            return list;
        }

        static List<(uint Magic, byte[] Data)> Sections(byte[] bmx)
        {
            var r = new BinaryReader(new MemoryStream(bmx));
            r.ReadUInt32();
            int count = (int)r.ReadUInt32();
            var entries = Enumerable.Range(0, count).Select(_ => (Magic: r.ReadUInt32(), Offset: r.ReadUInt32(), Size: r.ReadUInt32())).ToList();
            return entries.Select(e => (e.Magic, bmx.AsSpan((int)e.Offset, (int)e.Size).ToArray())).ToList();
        }

        static List<(string Name, byte[] Data)> SubSections(byte[] bmx) =>
            BMXFile.ParseSubSections(Sections(bmx).First(s => s.Magic == (uint)BMXFile.SectionType.IUGB).Data);

        /// <summary>What upstream ReBuzz reads: the same file without the subsections it doesn't know.</summary>
        static byte[] AsUpstreamReads(byte[] bmx)
        {
            var keep = new[] { "SequenceEditor", "ModernSequenceEditor", "ModernSequenceEditorHorizontal" };
            var parts = Sections(bmx).Select(s => s.Magic == (uint)BMXFile.SectionType.IUGB
                ? (s.Magic, BMXFile.BuildSubSections(BMXFile.ParseSubSections(s.Data).Where(x => keep.Contains(x.Name))))
                : s).ToList();
            return BMXFile.Assemble(parts);
        }

        sealed class Scene
        {
            public SessionRig Rig;
            public ReBuzzCore Core;
            public MachineCore Lead, Stress;
            public PatternCore Pad, Q;
            public SequenceCore Main, Take, Automation;
            public IParameter Swept;
            public int StressBindings, MidiPast = 450;
        }

        static Scene Build()
        {
            var rig = SnapshotTests.Song("Lead");
            var s = new Scene { Rig = rig, Core = rig.Core };
            var song = s.Core.SongCore;
            s.Lead = rig.Machine("Lead");
            s.Lead.Name = LeadName;
            s.Pad = s.Lead.PatternsList.First();
            s.Pad.Name = PatternName;
            s.Q = rig.Pattern(s.Lead, "Q", 16, (0, 52), (8, 55));
            s.Main = song.SequencesList.First(q => q.Machine == s.Lead);
            // The first clip is trimmed (rows 4 to 12), at half volume, with an id; one event sits past 65,535 ticks.
            s.Main.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, s.Pad, s.Pad.Length) { Offset = 4, End = 12, Gain = 0.5, Clip = 7 });
            s.Main.SetEvent(100_000, new SequenceEvent(SequenceEventType.PlayPattern, s.Pad, s.Pad.Length));
            // A take lane (the passes of a loop recording) and an automation lane.
            song.AddSequence(s.Lead, song.SequencesList.Count);
            s.Take = song.SequencesList.Last();
            s.Core.Arrangement.SetRole(s.Take, LaneRole.Take);
            s.Take.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, s.Q, s.Q.Length) { Offset = 2, Clip = 9 });
            s.Take.SetEvent(16, new SequenceEvent(SequenceEventType.PlayPattern, s.Q, s.Q.Length) { Gain = 0.25 });
            s.Take.SetEvent(32, new SequenceEvent(SequenceEventType.Break));
            song.AddSequence(s.Lead, song.SequencesList.Count);
            s.Automation = song.SequencesList.Last();
            s.Core.Arrangement.SetRole(s.Automation, LaneRole.Automation);
            s.Automation.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, s.Q, s.Q.Length));
            // Engine automation with a ramp, in the pad pattern.
            s.Swept = s.Lead.ParameterGroups[1].Parameters.First(x => x.Flags.HasFlag(ParameterFlags.State) && x.MaxValue - x.MinValue >= 64);
            var column = SessionLayout.ColumnFor(s.Pad, new ColumnKey(1, s.Swept.IndexInGroup, 0), create: true);
            column.SetEvents(new[] { new PatternEvent(0, s.Swept.MinValue, 8 * TB), new PatternEvent(8 * TB, s.Swept.MinValue + 64, 0) }, true);
            // A machine with 704 parameters, every one bound to a MIDI controller.
            s.Stress = rig.Add("Dye Stress", StressName, track: false);
            var globals = s.Stress.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters;
            for (int i = 0; i < globals.Count; i++) globals[i].BindToMIDIController(0, i / 128, i % 128);
            s.StressBindings = globals.Count;
            s.Core.SongCore.LoopEnd = 48;
            s.Core.SongCore.SongEnd = 48;
            return s;
        }

        static string Lane(Scene s, SequenceCore lane) => $"Lane {s.Core.SongCore.SequencesList.IndexOf(lane) + 1} ({LeadName})";

        static IEnumerable<(string Category, string Item)> Expected(Scene s) => new[]
        {
            ("Automation ramps", $"{LeadName} / {PatternName}: {LeadName}.{s.Swept.Name}, 2 events"),
            ("Clip gain", $"{Lane(s, s.Main)}: 1 clips"),
            ("Clip ids", $"{Lane(s, s.Main)}: 1 events"),
            ("Clip trims", $"{Lane(s, s.Main)}: 1 clips"),
            ("Lane roles", $"{Lane(s, s.Automation)}: automation lane"),
            ("MIDI bindings", $"{s.MidiPast} bindings past index 255"),
            ("Machines", $"{LeadName} (Neo Poly)"),
            ("Machines", $"{StressName} (Dye Stress)"),
            ("Names", $"Machine \"{LeadName}\""),
            ("Names", $"Machine \"{StressName}\""),
            ("Names", $"Pattern \"{PatternName}\" of {LeadName}"),
            ("NeoBuzz Live data", "arrangement lane roles, folded tracks and parameter lanes"),
            ("NeoBuzz Live data", "clip trims, gain, ids and loops"),
            ("Sequence length", $"{Lane(s, s.Main)}: 1 events past tick 65535"),
            ("Takes", $"{Lane(s, s.Take)}: 3 events"),
        }.OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2, StringComparer.Ordinal);

        [AvaloniaTest]
        public void ATakeLaneIsWrittenEmptyAndComesBackWithTheTakesFromOurSubsection()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var restore = Studio.AfterTest(() => Dialogs.Responder = null);
            var s = Build();
            using var rig = s.Rig;
            var core = s.Core;
            var song = core.SongCore;
            string x = Work("takes.bmx");

            var report = core.ExportForBuzz(x);
            var bytes = File.ReadAllBytes(x);
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(x), ".takes.bmx.tmp-*"), Is.Empty, "no temporary file left");

            // The core data: take lane empty, the late event out, the automation lane and the trimmed clip as upstream holds them.
            var sequences = ReadSequences(bytes);
            Assert.That(sequences[song.SequencesList.IndexOf(s.Take)].Events, Is.Empty, "the take lane is written empty");
            Assert.That(sequences[song.SequencesList.IndexOf(s.Main)].Events.Select(e => e.Pos), Is.EqualTo(new[] { 0 }), "the event past 65,535 ticks is left out");
            Assert.That(sequences[song.SequencesList.IndexOf(s.Automation)].Events.Count, Is.EqualTo(1));

            // Our subsection holds what was left out.
            var lanes = XElement.Parse(Encoding.UTF8.GetString(SubSections(bytes).Single(n => n.Name == "ExportedLanes").Data));
            var take = lanes.Elements("Lane").Single(l => (int)l.Attribute("index") == song.SequencesList.IndexOf(s.Take));
            Assert.That(take.Elements("E").Select(e => ((int)e.Attribute("time"), (string)e.Attribute("kind"))), Is.EqualTo(new[] { (0, "playpattern"), (16, "playpattern"), (32, "break") }));
            var main = lanes.Elements("Lane").Single(l => (int)l.Attribute("index") == song.SequencesList.IndexOf(s.Main));
            Assert.That(main.Elements("E").Select(e => (int)e.Attribute("time")), Is.EqualTo(new[] { 100_000 }));

            // Names are Latin-1 in every core section: "→" is "->", the accent in "Léad" stays one byte, "ś" is "s".
            var core1 = Sections(bytes).Where(q => q.Magic != (uint)BMXFile.SectionType.IUGB).ToList();
            Assert.That(core1.Any(q => q.Data.AsSpan().IndexOf(Encoding.Latin1.GetBytes("Léad -> s\0")) >= 0), Is.True, "the machine's name");
            Assert.That(core1.Any(q => q.Data.AsSpan().IndexOf(Encoding.Latin1.GetBytes("Pad <- *\0")) >= 0), Is.True, "the pattern's name");
            Assert.That(core1.Any(q => q.Data.AsSpan().IndexOf(new byte[] { 0xE2, 0x86, 0x92 }) >= 0), Is.False, "no UTF-8 arrow");
            Assert.That(core1.Where(q => q.Magic == (uint)BMXFile.SectionType.PATT).Any(q => Encoding.Latin1.GetString(q.Data).Contains("Pad ?")), Is.False, "no question mark where a better text exists");

            // MIDI keeps the bindings that fit in a byte; the report counts the rest (MID2 has them all).
            int fitting = 0;
            var midi = new MemoryStream(Sections(bytes).First(q => q.Magic == (uint)BMXFile.SectionType.MIDI).Data);
            while (midi.Position < midi.Length && BMXFile.ReadString(midi) is { Length: > 0 }) { midi.Position += 5; fitting++; }
            Assert.That(report.Losses.Single(l => l.Category == "MIDI bindings").Item, Is.EqualTo($"{s.StressBindings - fitting} bindings past index 255"));

            // The report: exactly what upstream can't hold.
            Assert.That(report.Losses.Select(l => (l.Category, l.Item)), Is.EqualTo(Expected(s)));
            Assert.That(report.Losses.All(l => l.Upstream.Length > 10), Is.True);
            Assert.That(core.PreviewBuzzExport().ToText(), Is.EqualTo(report.ToText()), "the preview says what the export wrote");

            // NeoBuzz Live opens it and has the takes back, and the trims, ids, gain, late event and all 704 bindings.
            Reopen(core, x);
            var lead = song.MachinesList.Single(m => m.Name == "Léad -> s");
            var back = core.SongCore.SequencesList.Where(q => q.Machine == lead).ToList();
            Assert.That(back.Count, Is.EqualTo(3));
            Assert.That(core.Arrangement.RoleOf(back[1]), Is.EqualTo(LaneRole.Take));
            Assert.That(back[1].TakeLane, Is.True, "the engine skips it again");
            Assert.That(core.Arrangement.RoleOf(back[2]), Is.EqualTo(LaneRole.Automation));
            var q0 = lead.PatternsList.First(p => p.Name == "Q");
            Assert.That(back[1].EventsList.Keys, Is.EqualTo(new[] { 0, 16, 32 }));
            Assert.That(back[1].EventsList[0].Pattern, Is.SameAs(q0));
            Assert.That((back[1].EventsList[0].Offset, back[1].EventsList[0].Clip), Is.EqualTo((2, 9)));
            Assert.That(back[1].EventsList[16].Gain, Is.EqualTo(0.25));
            Assert.That(back[1].EventsList[32].Type, Is.EqualTo(SequenceEventType.Break));
            Assert.That(back[0].EventsList.Keys, Is.EqualTo(new[] { 0, 100_000 }));
            var e0 = back[0].EventsList[0];
            Assert.That((e0.Offset, e0.End, e0.Gain, e0.Clip), Is.EqualTo((4, 12, 0.5, 7)));
            Assert.That(core.MidiControllerAssignments.ContollerBindings.Count, Is.EqualTo(s.StressBindings), "MID2 brings every binding back");
            var pad = lead.PatternsList.First(p => p.Name == "Pad <- *");
            var column = pad.ColumnsList.OfType<PatternColumnCore>().Single(c => c.EnginePlays);
            Assert.That(column.GetEvents(0, int.MaxValue).Select(e => (e.Time, e.Duration > 0)), Is.EqualTo(new[] { (0, true), (8 * TB, false) }), "automation and its ramp");
            Assert.That(core.PreviewBuzzExport().Losses.Any(l => l.Category == "Takes"), Is.True, "and it exports again the same way");
        }

        [AvaloniaTest]
        public void AnUpstreamReadRendersTheSongMinusWhatTheReportListed()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var restore = Studio.AfterTest(() => Dialogs.Responder = null);
            var s = Build();
            using var rig = s.Rig;
            var core = s.Core;
            const int buffers = 600;
            string a = Work("render-a.nbl"), x = Work("render.bmx"), u = Work("render-upstream.bmx");
            core.SaveSongCopy(a);
            core.ExportForBuzz(x);
            File.WriteAllBytes(u, AsUpstreamReads(File.ReadAllBytes(x)));

            // Each render starts from a freshly opened song: a machine's tail from the render before would colour the next.
            Reopen(core, a);
            var original = SnapshotTests.Render(core, buffers);
            Assert.That(original.Any(v => v != 0), "the song makes sound");

            // The song minus the losses: no trims, gain, clip ids or automation (the take lane never played, the late event is out of range).
            Reopen(core, a);
            var lead = core.SongCore.MachinesList.Single(m => m.Name == LeadName);
            foreach (var lane in core.SongCore.SequencesList.Where(q => q.Machine == lead))
            {
                foreach (var e in lane.EventsList.Values) { e.Offset = 0; e.End = 0; e.Gain = 1; e.Clip = 0; }
                lane.RefreshSpans();
            }
            foreach (var c in lead.PatternsList.SelectMany(p => p.ColumnsList.OfType<PatternColumnCore>()).Where(c => c.EnginePlays))
                c.SetEvents(Array.Empty<PatternEvent>(), true);
            var minus = SnapshotTests.Render(core, buffers);

            Reopen(core, u);        // the fork-only subsections ignored: no take role, so the engine would play a take lane if it had events
            Assert.That(core.SongCore.SequencesList.Any(q => q.TakeLane), Is.False);
            Assert.That(core.SongCore.SequencesList.Where(q => q.Machine.Name == "Léad -> s").Select(q => q.EventsList.Count), Is.EqualTo(new[] { 1, 0, 1 }), "the take lane has no events upstream");
            var upstream = SnapshotTests.Render(core, buffers);

            double lost = Db0(original, upstream), kept = Db0(minus, upstream);
            TestContext.Out.WriteLine($"upstream read against the song: {lost:F1} dB (trims and gain are lost); against the song minus the losses: {kept:F1} dB");
            Assert.That(lost, Is.GreaterThan(-30), "the trims and gain are really lost upstream");
            Assert.That(kept, Is.LessThan(-50), "otherwise it renders the same");
        }

        [AvaloniaTest]
        public void ExportingChangesNothingInTheOpenSong()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; Studio.Host.Core.Modified = false; });
            var s = Build();
            using var rig = s.Rig;
            var core = s.Core;
            string own = Work("open-song.nbl");
            core.SongCore.SongName = own;
            core.MarkerStore.Set("Drop", 16);
            core.SetModifiedFlag();
            var sections = core.CaptureSongSections();
            int steps = core.SongCore.ActionStack.Steps.Count();
            int snapshots = core.Snapshots.List().Count;
            var roles = core.SongCore.SequencesList.Select(q => core.Arrangement.RoleOf(q)).ToList();
            var events = core.SongCore.SequencesList.Select(q => q.EventsList.Count).ToList();

            core.ExportForBuzz(Work("nothing.bmx"));

            Assert.That(core.SongCore.SongName, Is.EqualTo(own), "its name");
            Assert.That(File.Exists(own), Is.False, "its file was not written");
            Assert.That(core.Modified, Is.True, "its dirty flag");
            Assert.That(core.SongCore.ActionStack.Steps.Count(), Is.EqualTo(steps), "its undo");
            Assert.That(core.Snapshots.List().Count, Is.EqualTo(snapshots), "its history");
            Assert.That(core.LegacyOrigin, Is.Null);
            Assert.That(core.SongCore.SequencesList.Select(q => core.Arrangement.RoleOf(q)), Is.EqualTo(roles));
            Assert.That(core.SongCore.SequencesList.Select(q => q.EventsList.Count), Is.EqualTo(events), "the take lane still has its events");
            Assert.That(s.Take.TakeLane, Is.True);
            var after = core.CaptureSongSections();
            for (int i = 0; i < sections.Count; i++)
                Assert.That(after[i].Data, Is.EqualTo(sections[i].Data), $"section {sections[i].Magic:X8} is the same");
            Assert.That(BMXFile.Assemble(core.CaptureSongSections()).Length, Is.EqualTo(BMXFile.Assemble(sections).Length), "and a normal save still writes the take lane whole");
            Assert.That(ReadSequences(BMXFile.Assemble(sections))[core.SongCore.SequencesList.IndexOf(s.Take)].Events.Count, Is.EqualTo(3));
        }

        // ------------------------------------------------------------------ the studio

        [AvaloniaTest]
        public void TheMenuEntryAsksWhereShowsTheReportAndWritesOnlyOnExport()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var restore = Studio.AfterTest(() =>
            {
                BuzzExportFlow.Picker = null; BuzzExportFlow.Confirmer = null; Dialogs.Responder = null; Studio.Host.Core.Modified = false;
            });
            var s = Build();
            using var rig = s.Rig;
            var core = s.Core;
            Assert.That(StudioKeymap.Get("file.exportbuzz").Title, Does.Contain("Export for"));
            string target = Work("flow.bmx");
            if (File.Exists(target)) File.Delete(target);
            string suggested = null;
            ExportReport shown = null;
            BuzzExportFlow.Picker = name => { suggested = name; return target; };
            BuzzExportFlow.Confirmer = r => { shown = r; return false; };
            core.SongCore.SongName = Work("My Song.nbl");
            Assert.That(BuzzExportFlow.Run().GetAwaiter().GetResult(), Is.Null, "Cancel writes nothing");
            Assert.That(suggested, Is.EqualTo("My Song.bmx"), "the song's name, as a .bmx");
            Assert.That(shown.Losses.Count, Is.GreaterThan(5));
            Assert.That(File.Exists(target), Is.False);

            BuzzExportFlow.Confirmer = r => true;
            Assert.That(BuzzExportFlow.Run().GetAwaiter().GetResult(), Is.EqualTo(target));
            Assert.That(File.Exists(target), Is.True);

            // A name with no extension gets .bmx; a .nbl or the song's own file is refused with a word.
            string said = null;
            Dialogs.Responder = (m, c, b) => { said = m; return UserAnswer.Ok; };
            BuzzExportFlow.Picker = _ => Work("plain");
            Assert.That(BuzzExportFlow.Run().GetAwaiter().GetResult(), Is.EqualTo(Work("plain") + ".bmx"));
            BuzzExportFlow.Picker = _ => Work("native.nbl");
            Assert.That(BuzzExportFlow.Run().GetAwaiter().GetResult(), Is.Null);
            Assert.That(said, Does.Contain(".nbl"));
            core.SongCore.SongName = Work("own.bmx");
            BuzzExportFlow.Picker = _ => Work("own.bmx");
            Assert.That(BuzzExportFlow.Run().GetAwaiter().GetResult(), Is.Null);
            Assert.That(said, Does.Contain("own file"));
            Assert.That(File.Exists(Work("own.bmx")), Is.False);
        }

        [AvaloniaTest]
        public void ASongThatLosesNothingIsExportedWithoutAskingAndTheReportDialogListsTheLosses()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var restore = Studio.AfterTest(() =>
            {
                BuzzExportFlow.Picker = null; BuzzExportFlow.Confirmer = null; Dialogs.Responder = null; Studio.Host.Core.Modified = false;
            });
            var core = Studio.Host.Core;
            core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            SongFiles.New().Wait();
            Dispatcher.UIThread.RunJobs();
            Assert.That(core.PreviewBuzzExport().IsEmpty, Is.True, core.PreviewBuzzExport().ToText());
            string target = Work("empty.bmx");
            bool asked = false;
            BuzzExportFlow.Picker = _ => target;
            BuzzExportFlow.Confirmer = r => { asked = true; return true; };
            Assert.That(BuzzExportFlow.Run().GetAwaiter().GetResult(), Is.EqualTo(target));
            Assert.That(asked, Is.False, "nothing lost: it just exports");
            Assert.That(File.Exists(target), Is.True);

            var report = new ExportReport(new[] { new ExportLoss("Takes", "Lane 2 (Lead): 3 events", "Written empty."), new ExportLoss("Machines", "Poly (Neo Poly)", "Missing upstream.") });
            bool exported = false;
            var window = BuzzExportFlow.Build(report, () => exported = true);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                var texts = Descendants<TextBlock>(window).Select(t => t.Text).ToList();
                Assert.That(texts, Does.Contain("Takes").And.Contain("Machines"));
                Assert.That(texts, Does.Contain("Lane 2 (Lead): 3 events: Written empty."));
                var buttons = Descendants<Button>(window).ToList();
                Assert.That(buttons.Select(b => (string)b.Content), Is.EqualTo(OperatingSystem.IsMacOS() ? new[] { "Cancel", "Export" } : new[] { "Export", "Cancel" }));
                Assert.That(buttons.Single(b => (string)b.Content == "Export").IsDefault, Is.True, "Return exports");
                Assert.That(buttons.Single(b => (string)b.Content == "Cancel").IsCancel, Is.True, "Escape cancels");
                Assert.That(window.Title, Is.EqualTo("Export for Buzz/ReBuzz"));
                Studio.Screenshot(window, "export-for-buzz");
            }
            finally { window.Close(); }
            Assert.That(exported, Is.False, "closing is not exporting");
        }

        static double Db0(float[] a, float[] b)
        {
            double err = 0, sig = 0;
            for (int i = 0; i < a.Length; i++) { err += Math.Pow(a[i] - b[i], 2); sig += Math.Pow(a[i], 2); }
            return 10 * Math.Log10(Math.Max(1e-20, err) / Math.Max(1e-20, sig));
        }

        static IEnumerable<T> Descendants<T>(Avalonia.Visual root) where T : class
        {
            foreach (var v in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root))
                if (v is T t) yield return t;
        }

        // ------------------------------------------------------------------ the agent

        static JsonObject Tool(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            return task.GetAwaiter().GetResult()["result"]!.AsObject();
        }

        static bool IsError(JsonObject r) => r["isError"]?.GetValue<bool>() == true;
        static string Text(JsonObject r) => r["content"]![0]!["text"]!.GetValue<string>();

        [AvaloniaTest]
        public void TheAgentExportsAndGetsTheReportRefusesTheOpenSongAndANblAndChangesNothing()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; Studio.Host.Core.Modified = false; });
            var s = Build();
            using var rig = s.Rig;
            var core = s.Core;
            string own = Work("agent-own.bmx");
            Tool("song_save", new JsonObject { ["path"] = own, ["format"] = "bmx", ["overwrite"] = true });
            Assert.That(core.SongCore.SongName, Is.EqualTo(Path.GetFullPath(own)).IgnoreCase);
            byte[] ownBytes = File.ReadAllBytes(own);
            core.MarkerStore.Set("agent", 4);
            int steps = core.SongCore.ActionStack.Steps.Count();
            bool modified = core.Modified;
            string target = Work("agent-for-buzz.bmx");
            if (File.Exists(target)) File.Delete(target);

            var ok = Tool("song_export", new JsonObject { ["path"] = target });
            Assert.That(IsError(ok), Is.False, ok.ToJsonString());
            var data = ok["structuredContent"]!.AsObject();
            Assert.That(data["lossless"]!.GetValue<bool>(), Is.False);
            Assert.That(data["file"]!.GetValue<string>(), Is.EqualTo(Path.GetFullPath(target)).IgnoreCase);
            Assert.That(data["bytes"]!.GetValue<long>(), Is.EqualTo(new FileInfo(target).Length));
            var losses = data["losses"]!.AsArray().Select(l => (l!["category"]!.GetValue<string>(), l["item"]!.GetValue<string>())).ToList();
            Assert.That(losses, Is.EqualTo(Expected(s).Concat(new[] { ("NeoBuzz Live data", "song markers"), ("History", "1 snapshots") }).OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2, StringComparer.Ordinal)));
            Assert.That(ReadSequences(File.ReadAllBytes(target))[core.SongCore.SequencesList.IndexOf(s.Take)].Events, Is.Empty);

            Assert.That(core.SongCore.ActionStack.Steps.Count(), Is.EqualTo(steps), "no undo step");
            Assert.That(core.Modified, Is.EqualTo(modified), "no dirty flag change");
            Assert.That(core.SongCore.SongName, Is.EqualTo(Path.GetFullPath(own)).IgnoreCase, "the song keeps its file");

            var again = Tool("song_export", new JsonObject { ["path"] = target });
            Assert.That(IsError(again), Is.True, "an existing file needs overwrite");
            Assert.That(Text(again), Does.Contain("overwrite"));
            Assert.That(IsError(Tool("song_export", new JsonObject { ["path"] = target, ["overwrite"] = true })), Is.False);

            var self = Tool("song_export", new JsonObject { ["path"] = own, ["overwrite"] = true });
            Assert.That(IsError(self), Is.True);
            Assert.That(Text(self), Does.Contain("own file"));
            Assert.That(File.ReadAllBytes(own), Is.EqualTo(ownBytes), "the song's file is untouched");
            var nbl = Tool("song_export", new JsonObject { ["path"] = Work("agent-x.nbl") });
            Assert.That(IsError(nbl), Is.True);
            Assert.That(File.Exists(Work("agent-x.nbl")), Is.False);
            var xml = Tool("song_export", new JsonObject { ["path"] = Work("agent-x.bmxml") });
            Assert.That(IsError(xml), Is.True, "a .bmx only");
            var bare = Tool("song_export", new JsonObject { ["path"] = Work("agent-bare"), ["overwrite"] = true });
            Assert.That(IsError(bare), Is.False, bare.ToJsonString());
            Assert.That(File.Exists(Work("agent-bare.bmx")), Is.True, "the extension is added");
            Assert.That(core.SongCore.ActionStack.Steps.Count(), Is.EqualTo(steps), "still no undo step");
            Assert.That(Tool("song_save", new JsonObject { ["format"] = "bmx" }) is var saved && !IsError(saved), Is.True, "song_save format=bmx stays a full save");
            Assert.That(ReadSequences(File.ReadAllBytes(own))[core.SongCore.SequencesList.IndexOf(s.Take)].Events.Count, Is.EqualTo(3), "whole, with the take lane's events");
        }
    }
}
