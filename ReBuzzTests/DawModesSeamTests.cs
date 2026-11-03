using BuzzGUI.Interfaces;
using ReBuzz.AppViews;
using ReBuzz.AppViews.Session;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzzTests.Automation;
using ReBuzzTests.Automation.TestMachinesControllers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using static ReBuzzTests.HardeningTests063;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: the engine seams of docs/specs/daw-modes.md (AR0) where only the Windows
    /// build reaches: allocation-free ramps in a release build, ramps on subticks, the WPF
    /// sequence editors' bars mirroring the engine's song meter, the engine keeping the bars
    /// where no sequence editor saves them (as rebuzz-headless), and the WPF piano roll on its
    /// note source. ReBuzz.Studio.Tests/DawModesSeamTests.cs covers the rest on Linux.
    /// </summary>
    public class DawModesSeamTests
    {
        const int Bar = 16;
        const int TB = PatternEvent.TimeBase;

        sealed class HumRig : IDisposable
        {
            public Driver Driver { get; } = new Driver();
            public ReBuzzCore Core => Driver.Core;
            public SongCore Song => Core.SongCore;
            public List<MachineCore> Machines { get; } = new List<MachineCore>();

            public HumRig(int count)
            {
                var controllers = Enumerable.Range(0, count).Select(i => HumController.NewInstance("Hum" + i)).ToList();
                Driver.Gear.AddDynamicGenerator(HumController.Info);
                Driver.Start();
                foreach (var c in controllers)
                {
                    Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(c);
                    Machines.Add(Driver.MachineFor(c));
                }
                foreach (var m in Machines)
                    if (!Song.SequencesList.Any(s => s.Machine == m))
                        Song.AddSequence(m, Song.SequencesList.Count);
                Core.Looping = true;
            }

            public PatternCore Pattern(MachineCore machine, string name, int length)
            {
                machine.CreatePattern(name, length);
                return machine.PatternsList.Single(p => p.Name == name);
            }

            public void Dispose()
            {
                Core.Playing = false;
                Driver.Dispose();
            }
        }

        /// <summary>An engine automation column of a pattern's machine's first global parameter, with ramps up and down.</summary>
        static IPatternColumn Ramps(PatternCore p, IParameter parameter, int ticks)
        {
            var column = SessionLayout.ColumnFor(p, new ColumnKey(1, parameter.IndexInGroup, 0), create: true);
            var events = new List<PatternEvent>();
            for (int t = 0; t < ticks; t += 4)
                events.Add(new PatternEvent(t * TB, (t / 4) % 2 == 0 ? parameter.MinValue : parameter.MaxValue, 4 * TB));
            column.SetEvents(events, true);
            return column;
        }

        // ------------------------------------------------------------------ S3: ramps

        [Test]
        public void PlayingRampsAllocatesNothing()
        {
            using var rig = new HumRig(4);
            for (int i = 0; i < 4; i++)
            {
                var m = rig.Machines[i];
                var p = rig.Pattern(m, "P", Bar);
                var q = rig.Pattern(m, "Q", 2 * Bar);
                var bypass = m.ParameterGroups[1].Parameters[0];
                Ramps(p, bypass, Bar);
                Ramps(q, bypass, 2 * Bar);
                Assert.That(p.Columns.OfType<PatternColumnCore>().Single(c => c.EnginePlays).HasRamps, Is.True);
                rig.Song.SequencesList[i].SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, p));
                rig.Song.SequencesList[i].SetEvent(Bar, new SequenceEvent(SequenceEventType.PlayPattern, q));
            }
            rig.Song.LoopEnd = 4 * Bar;
            var reader = rig.Driver.CreateAudioReader(256);
            rig.Core.Playing = true;
            for (int i = 0; i < 200; i++)
                reader.Read(256);                                    // warm up
            for (int stretch = 0; stretch < 200; stretch++)
            {
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++)
                    reader.Read(256);
                if (GC.GetAllocatedBytesForCurrentThread() == start)
                    break;
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 400; i++)
                reader.Read(256);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            TestContext.Out.WriteLine($"[notice] ramps: {bytes} bytes in 400 buffers of 256 frames");
            Assert.That(bytes, Is.EqualTo(0), "ramps must not allocate on the audio thread");
        }

        [Test]
        public void RampsMoveOnEverySubtick()
        {
            using var rig = new HumRig(0);
            var master = rig.Song.MachinesList[0];
            var p = rig.Pattern(master, "Fade", Bar);
            var volume = master.ParameterGroups[1].Parameters[0];
            var column = SessionLayout.ColumnFor(p, new ColumnKey(1, volume.IndexInGroup, 0), create: true);
            column.SetEvents(new[] { new PatternEvent(0, 0, 8 * TB), new PatternEvent(8 * TB, 0x4000, 0) }, true);
            if (!rig.Song.SequencesList.Any(s => s.Machine == master)) rig.Song.AddSequence(master, 0);
            rig.Song.SequencesList.First(s => s.Machine == master).SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, p));
            rig.Song.LoopEnd = Bar;

            var mi = ReBuzzCore.masterInfo;
            rig.Driver.ReadStereoSamples(mi.SamplesPerTick - mi.PosInTick);
            rig.Core.Playing = true;
            var values = new List<int>();
            for (int guard = 0; rig.Song.PlayPosition < 8 && guard < 10000; guard++)       // the way up, by subticks
            {
                rig.Driver.ReadStereoSamples(Math.Max(1, ReBuzzCore.subTickInfo.SamplesPerSubTick - ReBuzzCore.subTickInfo.PosInSubTick));
                values.Add(volume.GetValue(0));
            }
            rig.Core.Playing = false;
            var during = values.Where(v => v > 0 && v < 0x4000).Distinct().Count();
            TestContext.Out.WriteLine("[notice] master volume by subtick: " + string.Join(" ", values));
            Assert.That(during, Is.GreaterThan(8), "more than a value a tick on the way up");
            for (int i = 1; i < values.Count; i++)
                Assert.That(values[i], Is.GreaterThanOrEqualTo(values[i - 1]), "it only rises");
        }

        // ------------------------------------------------------------------ S1: the meter

        [Test]
        public void TheWpfSequenceEditorsBarsMirrorTheEnginesSongMeter()
        {
            var meter = new SongMeter();
            var mirror = new SequenceEditorMeter(meter);
            var classic = new BuzzGUI.SequenceEditor.TimeSignatureList();
            meter.Set(0, 12);
            mirror.Watch(classic);
            Assert.That(classic.GetBarLengthAt(0), Is.EqualTo(12), "a list it starts to follow takes the engine's bars");

            classic.Set(48, 8);                                       // an edit in the WPF editor
            Assert.That(meter.Entries, Is.EqualTo(new Dictionary<int, int> { [0] = 12, [48] = 8 }), "reaches the engine");

            meter.Reset();                                            // a new song, a song opened, the agent
            Assert.That(classic.GetBarLengthAt(100), Is.EqualTo(16), "reaches the editor");

            var replaced = new BuzzGUI.SequenceEditor.TimeSignatureList();   // the editor read a new list (a song opened)
            replaced.Set(0, 8);
            mirror.Watch(replaced);
            Assert.That(replaced.GetBarLengthAt(0), Is.EqualTo(16), "the engine's bars are the song's");
            classic.Set(0, 4);
            Assert.That(meter.Entries[0], Is.EqualTo(16), "a list no longer followed changes nothing");
        }

        [TestCase(".bmx")]
        [TestCase(".bmxml")]
        [Apartment(ApartmentState.STA)]      // the .bmxml loader creates UI objects
        public void WithoutASequenceEditorTheEngineKeepsTheBarsInTheSong(string extension)
        {
            using var rig = new HumRig(1);
            rig.Core.SongMeter.Set(0, 12);
            rig.Core.SongMeter.Set(36, 16);
            var bars = rig.Core.SongMeter.Entries.ToDictionary(e => e.Key, e => e.Value);
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "meter-" + Guid.NewGuid().ToString("N") + extension);
            rig.Core.SaveSongCopy(path);
            rig.Core.Modified = false;
            rig.Core.OpenSongFile(path);
            rig.Driver.AssertNoErrorsReportedToUser();
            Assert.That(rig.Core.SongMeter.Entries, Is.EqualTo(bars), "the classic editor's subsection, written by the engine");
            Assert.That(rig.Core.Session.BeatsPerBar, Is.EqualTo(3), "the session's bar follows the song's first bar");
        }

        // ------------------------------------------------------------------ S4: the WPF piano roll

        [Test]
        [Apartment(ApartmentState.STA)]
        public void TheWpfPianoRollGoesThroughANoteSource()
        {
            using var rig = new KeysRig();
            var clip = rig.Clip("A", Bar, (0, 60));
            var layout = rig.Layout;
            layout.Ensure();
            var roll = new PianoRoll(rig.Core, layout, layout.SequenceOf(0)!, clip) { Grid = TB };
            Assert.That(roll.Source, Is.InstanceOf<SessionClipNotes>());

            var song = new PianoRoll(rig.Core, new MidiColumnNotes(rig.Core, clip), null!) { Grid = TB };
            song.AddNote(4 * TB, 67);
            Assert.That(layout.NotesOf(clip).Select(n => n.Note), Is.EqualTo(new[] { 60, 67 }), "a roll on the song's source edits the same column");
            rig.Core.SongCore.ActionStack.Undo();
            Assert.That(layout.NotesOf(clip).Select(n => n.Note), Is.EqualTo(new[] { 60 }), "on the song's history");
        }
    }
}
