using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzzTests.Automation;
using ReBuzzTests.Automation.TestMachinesControllers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static ReBuzzTests.SessionClipTests;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/session-view.md 0.6.3): the paths session recording (0.7) builds
    /// on. Engine MIDI columns play each event once, at its position; deleting machines keeps
    /// and brings back every pattern's engine columns and every track; undo in another view
    /// never makes a second pattern of one name; a MIDI release does what its press did;
    /// removed tracks kept for an undo follow renames and identity.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class HardeningTests063
    {
        const int Bar = 16;
        const int TimeBase = PatternEvent.TimeBase;

        /// <summary>One Keys machine (reports the MIDI it receives) with a track, and a Tone.</summary>
        sealed class KeysRig : IDisposable
        {
            public Driver Driver { get; } = new Driver();
            public ReBuzzCore Core => Driver.Core;
            public MachineCore Keys { get; }
            public MachineCore Tone { get; }
            /// <summary>Note-ons received: (session clock, position in the tick in samples, note).</summary>
            public List<(long Clock, int PosInTick, int Note)> NoteOns { get; } = new List<(long, int, int)>();

            public KeysRig()
            {
                var keys = KeysController.NewInstance("Keys");
                var tone = ToneController.NewInstance("Tone0");
                Driver.Gear.AddDynamicGenerator(KeysController.Info);
                Driver.Gear.AddDynamicGenerator(ToneController.Info);
                Driver.Start();
                Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(keys);
                Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(tone);
                Keys = Driver.MachineFor(keys);
                Tone = Driver.MachineFor(tone);
                Driver.MachineGraph.ExecuteMachineCommand(keys.Listen((kind, channel, data1, data2) =>
                {
                    if (kind == 0 && data2 > 0)
                        NoteOns.Add((Core.Session.Clock, ReBuzzCore.masterInfo.PosInTick, data1));
                }));
                var song = Core.SongCore;
                foreach (var m in new[] { Keys, Tone })
                    if (!song.SequencesList.Any(s => s.Machine == m))
                        song.AddSequence(m, song.SequencesList.Count);
                Core.Looping = true;
            }

            /// <summary>A pattern of the Keys machine with an engine MIDI column holding these notes.</summary>
            public PatternCore Clip(string name, int length, params (int Time, int Note)[] notes)
            {
                Keys.CreatePattern(name, length);
                var p = Keys.PatternsList.Single(x => x.Name == name);
                p.InsertColumn(0, (IMachine)null);
                p.Columns[0].SetEvents(notes.Select(n => new PatternEvent(n.Time, NoteOn(n.Note, 100), TimeBase / 2)).ToArray(), true);
                return p;
            }

            public void Dispose()
            {
                Core.Playing = false;
                Driver.Dispose();
            }
        }

        // ------------------------------------------------------------------ engine MIDI columns

        [Test]
        public void MidiColumnEventsPlayOnceEachInTheBlockOfTheirPosition()
        {
            using var rig = new KeysRig();
            // Late in a tick, early in one, and mid-tick: upstream sent the late ones up to a dozen times.
            var notes = new[] { (1 * TimeBase + 230, 60), (3 * TimeBase + 120, 62), (5 * TimeBase + 5, 64) };
            var clip = rig.Clip("A", 8, notes);
            rig.Core.SessionLayout.Ensure();
            var track = rig.Core.SessionLayout.SequenceOf(0)!;
            Assert.That(track.Machine, Is.SameAs(rig.Keys));

            var reader = rig.Driver.CreateAudioReader(256);
            rig.Core.Session.Launch(track, clip);                // on the first tick
            rig.Core.Playing = true;
            int spt = ReBuzzCore.masterInfo.SamplesPerTick;
            int guard = 0;
            while (rig.Core.Session.Clock < 16 && guard++ < 100000)
                reader.Read(256);

            Assert.That(rig.NoteOns.Select(n => n.Note), Is.EqualTo(new[] { 60, 62, 64, 60, 62, 64 }), "each note once per pass, in order");
            long start = rig.Core.Session.GetClipStartClock(track);
            foreach (var (clock, posInTick, note) in rig.NoteOns)
            {
                int time = notes.Single(n => n.Item2 == note).Item1;
                long tick = (clock - start) % 8;
                int from = (int)tick * TimeBase + posInTick * TimeBase / spt;
                Assert.That(time, Is.GreaterThanOrEqualTo(from), $"note {note} not before its block");
                Assert.That(time - from, Is.LessThan(256 * TimeBase / spt + 1), $"note {note} in the block of its position");
            }
        }

        [Test]
        public void DeletingAMachineKeepsTheEngineColumnsOfOtherMachinesPatterns()
        {
            using var rig = new KeysRig();
            var clip = rig.Clip("A", 8, (0, 60), (TimeBase * 4, 62));

            rig.Core.SongCore.DeleteMachines(new IMachine[] { rig.Tone });
            Assert.That(clip.Columns.Count, Is.EqualTo(1), "upstream deleted every pattern's columns of its own machine");
            Assert.That(clip.Columns[0].GetEvents(0, int.MaxValue).Count(), Is.EqualTo(2));
        }

        [Test]
        public void UndoingAMachineDeleteBringsBackItsPatternsEngineColumns()
        {
            using var rig = new KeysRig();
            rig.Clip("A", 8, (0, 60), (TimeBase * 4, 62));
            string name = rig.Keys.Name;

            rig.Core.SongCore.DeleteMachines(new IMachine[] { rig.Keys });
            rig.Core.SongCore.Undo();
            var back = rig.Core.SongCore.MachinesList.Single(m => m.Name == name);
            var again = back.PatternsList.Single(p => p.Name == "A");
            Assert.That(again.Columns.Count, Is.EqualTo(1));
            Assert.That(again.Columns[0].Type, Is.EqualTo(PatternColumnType.MIDI));
            Assert.That(again.Columns[0].Machine, Is.SameAs(back), "the column plays the machine made again");
            Assert.That(again.Columns[0].GetEvents(0, int.MaxValue).Select(e => MidiNote(e.Value)), Is.EqualTo(new[] { 60, 62 }));
        }

        static int MidiNote(int data) => (data >> 8) & 0x7F;

        static int[] NotesOf(IPattern p) =>
            p.Columns.Where(c => c.Type == PatternColumnType.MIDI).SelectMany(c => c.GetEvents(0, int.MaxValue)).Select(e => MidiNote(e.Value)).ToArray();

        [Test]
        public void TheSessionsCopiesAndUndosKeepEngineColumns()
        {
            using var rig = new KeysRig();
            var clip = rig.Clip("A", 8, (0, 60), (TimeBase * 2, 62));
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            Assert.That(layout.PatternAt(0, 0), Is.SameAs(clip));

            var copy = layout.DuplicateClip(0, 0, out _)!;
            Assert.That(NotesOf(copy), Is.EqualTo(new[] { 60, 62 }), "a duplicate has the notes");

            layout.DeleteClip(clip);
            layout.Undo();
            var back = layout.PatternAt(0, 0)!;
            Assert.That(back, Is.Not.Null);
            Assert.That(NotesOf(back), Is.EqualTo(new[] { 60, 62 }), "undoing a delete brings them back");

            layout.Undo();                                       // the duplicate
            layout.Redo();
            Assert.That(NotesOf(layout.PatternAt(0, 1)!), Is.EqualTo(new[] { 60, 62 }), "and redoing a duplicate");
        }

        [Test]
        public void UndoingTheDeleteOfSeveralMachinesBringsBackEveryTrack()
        {
            using var rig = new ClipRig(3);
            var b = rig.Pattern(rig.Machines[1], "B", Bar);
            var c = rig.Pattern(rig.Machines[2], "C", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetTrackColor(2, 7);
            string nameB = rig.Machines[1].Name, nameC = rig.Machines[2].Name;

            rig.Core.SongCore.DeleteMachines(new IMachine[] { rig.Machines[1], rig.Machines[2] });
            Assert.That(layout.Document!.Tracks.Count, Is.EqualTo(1));
            rig.Core.SongCore.Undo();

            var backB = rig.Core.SongCore.MachinesList.Single(m => m.Name == nameB);
            var backC = rig.Core.SongCore.MachinesList.Single(m => m.Name == nameC);
            Assert.That(layout.Document!.Tracks.Count, Is.EqualTo(3));
            Assert.That(layout.PatternAt(1, 0), Is.SameAs(backB.PatternsList.Single(p => p.Name == "B")));
            Assert.That(layout.PatternAt(2, 0), Is.SameAs(backC.PatternsList.Single(p => p.Name == "C")), "the later machine's track too");
            Assert.That(layout.Document!.Tracks[2].ColorIndex, Is.EqualTo(7));
        }

        // ------------------------------------------------------------------ names

        [Test]
        public void UndoingAPatternDeleteInAnotherViewNeverMakesASecondPatternOfOneName()
        {
            using var rig = new ClipRig(1);
            var m = rig.Machines[0];
            var layout = rig.Layout;
            layout.Ensure();
            var first = layout.CreateClip(0, 0)!;
            string name = first.Name;
            rig.Set(first, c => c.Mode = "Gate");

            var delete = new DeletePatternAction(first);         // the Pattern View's delete
            delete.Do();
            var second = layout.CreateClip(0, 3)!;               // takes the freed name
            Assert.That(second.Name, Is.EqualTo(name));

            delete.Undo();
            Assert.That(m.Patterns.Count(p => p.Name == name), Is.EqualTo(1), "one pattern per name");
            Assert.That(m.Patterns.Single(p => p.Name == name), Is.SameAs(second));
            var again = layout.PatternAt(0, 0)!;
            Assert.That(again, Is.Not.Null.And.Not.SameAs(second), "the old clip is back in its own slot, under a free name");
            Assert.That(layout.ClipOf(again).Mode, Is.EqualTo("Gate"));
            Assert.That(layout.PatternAt(0, 3), Is.SameAs(second));
            Assert.That(layout.ClipOf(second).Mode, Is.EqualTo("Trigger"));

            delete.Do();                                         // redo deletes the one made again
            Assert.That(m.Patterns.Contains(second), Is.True);
            Assert.That(m.Patterns.Contains(again), Is.False);
        }

        // ------------------------------------------------------------------ MIDI releases

        [Test]
        public void ANoteLearntWhileHeldIsReleasedWhereItsPressWent()
        {
            using var rig = new ClipRig(1);
            rig.Pattern(rig.Machines[0], "A", Bar);
            rig.Layout.Ensure();
            var midi = rig.Core.SessionMidi;

            midi.BeginLearn(SessionTarget.Slot, 0, 0);
            Assert.That(midi.OnMidiThread(NoteOn(36, 100)), Is.False, "not mapped yet: it plays the instrument");
            Assert.That(midi.IsLearning, Is.False, "learnt on the press");
            Assert.That(midi.OnMidiThread(NoteOff(36)), Is.False, "so its release goes there too (no hung note)");
            Assert.That(midi.OnMidiThread(NoteOn(36, 100)), Is.True, "mapped from now on");
            Assert.That(midi.OnMidiThread(NoteOff(36)), Is.True);
        }

        [Test]
        public void AGatePadReleasesTheClipItLaunchedEvenIfItsSlotChangedMeanwhile()
        {
            using var rig = new ClipRig(1);
            var m = rig.Machines[0];
            var a = rig.Pattern(m, "A", Bar);
            var b = rig.Pattern(m, "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            var track = rig.Track(0);
            Assert.That(layout.PatternAt(0, 0), Is.SameAs(a));
            rig.Set(a, c => { c.Mode = "Gate"; c.Quantization = "Beat"; });
            rig.Core.SessionMidi.BeginLearn(SessionTarget.Slot, 0, 0);
            rig.Core.SendMIDIInput(NoteOn(36, 100));
            rig.Core.SendMIDIInput(NoteOff(36));

            rig.PlayTo(1);
            rig.Core.SendMIDIInput(NoteOn(36, 100));             // A at 4
            rig.PlayTo(4);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));
            layout.SetSlot(0, 0, b);                             // the slot holds B now
            rig.PlayTo(5);
            rig.Core.SendMIDIInput(NoteOff(36));                 // still A's release: it stops at 8
            rig.PlayTo(8);
            Assert.That(rig.Session.GetPlaying(track), Is.Null);
        }

        // ------------------------------------------------------------------ removed tracks

        [Test]
        public void ARemovedTrackComesBackWithPatternsRenamedMeanwhile()
        {
            using var rig = new ClipRig(2);
            var b = rig.Pattern(rig.Machines[1], "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            var song = rig.Core.SongCore;

            var delete = new DeleteSequenceAction(song.SequencesList[1]);
            delete.Do();
            rig.Machines[1].RenamePattern(b, "B2");              // renamed while its track is gone
            delete.Undo();
            Assert.That(layout.PatternAt(1, 0), Is.SameAs(b), "its slot follows the rename");
        }

        [Test]
        public void ARemovedTrackStaysInUndoSnapshotsMadeAfterItsRemoval()
        {
            using var rig = new ClipRig(2);
            var b = rig.Pattern(rig.Machines[1], "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            var song = rig.Core.SongCore;

            var delete = new DeleteSequenceAction(song.SequencesList[1]);
            delete.Do();
            layout.RenameScene(0, "Verse");                      // a session edit while it is gone
            delete.Undo();
            Assert.That(layout.PatternAt(1, 0), Is.SameAs(b));
            layout.Undo();                                       // the scene rename
            Assert.That(layout.Document!.Scenes[0].Name, Is.EqualTo(""));
            Assert.That(layout.PatternAt(1, 0), Is.SameAs(b), "undoing the later edit keeps the track's clips");
            layout.Redo();
            Assert.That(layout.PatternAt(1, 0), Is.SameAs(b));
        }

        [Test]
        public void ANewMachineWithAnOldNameInheritsNoRemovedTrack()
        {
            using var rig = new ClipRig(2);
            var old = rig.Machines[1];
            rig.Pattern(old, "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            var song = rig.Core.SongCore;
            string name = old.Name;

            var delete = new DeleteSequenceAction(song.SequencesList[1]);
            delete.Do();
            song.DeleteMachines(new IMachine[] { old });
            var newcomer = ToneController.NewInstance(name);
            rig.Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(newcomer);
            var fresh = rig.Driver.MachineFor(newcomer);
            Assert.That(fresh.Name, Is.EqualTo(name));
            fresh.CreatePattern("B", Bar);

            delete.Undo();                                       // its machine is found by name: the newcomer
            int t = layout.Document!.Tracks.FindIndex(x => x.Machine == name);
            Assert.That(t, Is.GreaterThanOrEqualTo(0));
            Assert.That(layout.Document!.Tracks[t].Slots, Is.Empty, "the old track's slots stay with the old machine");
        }
    }
}
