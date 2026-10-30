using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzzTests.Automation.TestMachinesControllers;
using System.Linq;
using System.Threading;
using static ReBuzzTests.SessionClipTests;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: identity (docs/specs/session-view.md 0.6.1 B). The layout follows the song
    /// exactly, in its undo snapshots too: sequences by index, renames, deleted patterns and
    /// machines whose names are retired (nothing is inherited by a newcomer with the same
    /// name) and brought back by an undo in any view; the session's own pattern actions hold
    /// objects; recording undo only takes back what it wrote.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class SessionIdentityTests
    {
        const int Bar = 16;

        [Test]
        public void RemovingOneOfAMachinesTwoSequencesKeepsTheOtherTrack()
        {
            using var rig = new ClipRig(1);
            var m = rig.Machines[0];
            rig.Pattern(m, "A", Bar);
            rig.Pattern(m, "B", Bar);
            var song = rig.Core.SongCore;
            song.AddSequence(m, 1);                              // a second track of the same machine
            var layout = rig.Layout;
            layout.Ensure();
            layout.ClearSlot(0, 1);                              // track 1: A only; track 2: B only
            layout.ClearSlot(1, 0);

            song.RemoveSequence(song.SequencesList[0]);
            var doc = layout.Document!;
            Assert.That(doc.Tracks.Count, Is.EqualTo(1));
            Assert.That(doc.Tracks[0].Slots.Values, Is.EqualTo(new[] { "B" }), "the removed sequence's track went, not the other");

            layout.Undo();                                       // an earlier grid edit: the snapshot has one track less as well
            Assert.That(layout.Document!.Tracks.Count, Is.EqualTo(1));
            Assert.That(layout.Document!.Tracks[0].Slots.Values, Does.Contain("B"));
        }

        [Test]
        public void UndoAfterAMachineRenameInTheMachineViewKeepsItsClips()
        {
            using var rig = new ClipRig(1);
            var m = rig.Machines[0];
            var a = rig.Pattern(m, "A", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            rig.Set(a, c => c.Mode = "Gate");
            layout.RenameScene(0, "Intro");

            m.Name = "Lead";                                     // outside the session
            layout.Undo();                                       // the scene rename
            Assert.That(layout.Document!.Scenes[0].Name, Is.Not.EqualTo("Intro"));
            Assert.That(layout.ClipOf(a).Mode, Is.EqualTo("Gate"), "the snapshot followed the rename");
            Assert.That(layout.PatternAt(0, 0), Is.SameAs(a));
            layout.Undo();                                       // the clip settings
            Assert.That(layout.ClipOf(a).Mode, Is.EqualTo("Trigger"));
        }

        [Test]
        public void ANewMachineCalledLikeADeletedOneInheritsNothing()
        {
            using var rig = new ClipRig(1);
            var m = rig.Machines[0];
            var a = rig.Pattern(m, "00", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            rig.Set(a, c => c.Mode = "Gate");
            layout.SetCrossfade(m, "A");
            string name = m.Name;

            rig.Core.RemoveMachine(m);
            rig.Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(ToneController.NewInstance(name));
            var newcomer = rig.Core.SongCore.MachinesList.Single(x => x.Name == name);
            var song = rig.Core.SongCore;
            if (!song.SequencesList.Any(s => s.Machine == newcomer))
                song.AddSequence(newcomer, song.SequencesList.Count);
            newcomer.CreatePattern("00", Bar);
            var p = newcomer.PatternsList.Single(x => x.Name == "00");

            Assert.That(layout.CrossfadeOf(newcomer), Is.Null, "no crossfader side");
            Assert.That(layout.ClipOf(p).Mode, Is.EqualTo("Trigger"), "no clip settings");
            Assert.That(newcomer.CrossfadeGain, Is.EqualTo(1f));
        }

        [Test]
        public void SessionUndoNeverTouchesAnotherEditorsPatternOfTheSameName()
        {
            using var rig = new ClipRig(1);
            var m = rig.Machines[0];
            var layout = rig.Layout;
            layout.Ensure();
            var made = layout.CreateClip(0, 0)!;
            string name = made.Name;
            layout.Undo();                                       // the clip goes
            Assert.That(m.PatternsList.Any(x => x.Name == name), Is.False);

            m.CreatePattern(name, 8);                            // another editor makes one with that name
            var theirs = m.PatternsList.Single(x => x.Name == name);
            layout.Redo();                                       // the session's comes back under another name
            Assert.That(m.PatternsList.Count, Is.EqualTo(2));
            Assert.That(theirs.Length, Is.EqualTo(8), "theirs is untouched");
            var ours = layout.PatternAt(0, 0)!;
            Assert.That(ours, Is.Not.SameAs(theirs));
            Assert.That(ours.Name, Is.Not.EqualTo(name));

            layout.Undo();                                       // deletes ours only
            Assert.That(m.PatternsList.Single(), Is.SameAs(theirs));

            // Deleting from the session and undoing while the name is taken: the copy comes back renamed.
            layout.DeleteClip(theirs);
            m.CreatePattern(name, 4);
            var third = m.PatternsList.Single(x => x.Name == name);
            layout.Undo();
            Assert.That(third.Length, Is.EqualTo(4), "not overwritten by the undo");
            Assert.That(m.PatternsList.Count, Is.EqualTo(2));
        }

        [Test]
        public void APatternDeletedInAnotherViewLeavesNothingToANewClipOfTheSameName()
        {
            using var rig = new ClipRig(1);
            var m = rig.Machines[0];
            var layout = rig.Layout;
            layout.Ensure();
            var first = layout.CreateClip(0, 0)!;
            string name = first.Name;
            rig.Set(first, c => c.Mode = "Gate");

            rig.Driver.Dispatcher.Deferred = true;               // as the real dispatcher
            new DeletePatternAction(first).Do();                 // the Pattern View's delete
            rig.Driver.Dispatcher.RunPending();
            var second = layout.CreateClip(0, 3)!;               // a new empty clip gets the freed name
            rig.Driver.Dispatcher.RunPending();
            rig.Driver.Dispatcher.Deferred = false;

            Assert.That(second.Name, Is.EqualTo(name));
            Assert.That(layout.PatternAt(0, 0), Is.Null, "it doesn't take the old clip's slot");
            Assert.That(layout.PatternAt(0, 3), Is.SameAs(second));
            Assert.That(layout.ClipOf(second).Mode, Is.EqualTo("Trigger"), "nor its settings");
        }

        [Test]
        public void TheSessionsUndoBringsBackItsOwnDeletedClipEvenIfANewOneTookTheName()
        {
            using var rig = new ClipRig(1);
            var m = rig.Machines[0];
            var layout = rig.Layout;
            layout.Ensure();
            var clip = layout.CreateClip(0, 1)!;
            string name = clip.Name;
            rig.Set(clip, c => c.Mode = "Gate");

            layout.DeleteClip(clip);
            var newcomer = layout.CreateClip(0, 4)!;             // takes the freed name
            Assert.That(newcomer.Name, Is.EqualTo(name));
            Assert.That(layout.ClipOf(newcomer).Mode, Is.EqualTo("Trigger"));
            layout.Undo();                                       // the new clip
            layout.Undo();                                       // the delete
            var back = layout.PatternAt(0, 1)!;
            Assert.That(back, Is.Not.Null, "the slot came back");
            Assert.That(back.Name, Is.EqualTo(name));
            Assert.That(layout.ClipOf(back).Mode, Is.EqualTo("Gate"), "and its settings");
            Assert.That(layout.PatternAt(0, 4), Is.Null);
        }

        [Test]
        public void ANewSequenceForAMachineStartsWithAnEmptyTrack()
        {
            using var rig = new ClipRig(2);
            rig.Pattern(rig.Machines[0], "A", Bar);
            var b = rig.Pattern(rig.Machines[1], "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetTrackColor(1, 5);
            var song = rig.Core.SongCore;
            song.RemoveSequence(song.SequencesList[1]);
            Assert.That(layout.Document!.Tracks.Count, Is.EqualTo(1));

            song.AddSequence(rig.Machines[1], 1);
            Assert.That(layout.Document!.Tracks.Count, Is.EqualTo(2));
            Assert.That(layout.Document!.Tracks[1].Slots, Is.Empty, "nothing carried over from the removed track");
            Assert.That(layout.Document!.Tracks[1].ColorIndex, Is.EqualTo(-1));
            Assert.That(layout.Document!.Tracks[0].Slots.Values, Is.EqualTo(new[] { "A" }), "the other track is untouched");
            Assert.That(b.Machine, Is.SameAs(rig.Machines[1]));
        }

        [Test]
        public void UndoingADeleteAfterItsMachineWasDeletedMakesNoGhostPattern()
        {
            using var rig = new ClipRig(1);
            var m = rig.Machines[0];
            var a = rig.Pattern(m, "A", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.DeleteClip(a);
            rig.Core.RemoveMachine(m);
            layout.Undo();                                       // the clip's machine is gone
            Assert.That(m.PatternsList, Is.Empty);
        }

        // ------------------------------------------------------------------ restorations (0.6.2)

        [Test]
        public void UndoingAPatternDeleteInAnotherViewBringsItsSlotAndSettingsBackWhereTheyAre()
        {
            using var rig = new ClipRig(1);
            var m = rig.Machines[0];
            rig.Pattern(m, "A", Bar);
            var b = rig.Pattern(m, "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            rig.Set(b, c => c.Mode = "Gate");
            layout.MoveSlot(0, 1, 3, copy: false);               // B in scene 4

            var delete = new DeletePatternAction(b);             // the Pattern View's delete
            delete.Do();
            Assert.That(layout.PatternAt(0, 3), Is.Null);
            layout.InsertScene(0);                               // the grid changes meanwhile: B's place moves to scene 5
            delete.Undo();                                       // the Pattern View's undo
            var back = m.PatternsList.Single(x => x.Name == "B");
            Assert.That(back, Is.Not.SameAs(b));
            Assert.That(layout.PatternAt(0, 4), Is.SameAs(back), "its slot came back, where it is now");
            Assert.That(layout.ClipOf(back).Mode, Is.EqualTo("Gate"), "and its settings");
            layout.Undo();                                       // the scene insert: B back in scene 4
            Assert.That(layout.PatternAt(0, 3), Is.SameAs(back), "undo history followed it too");
        }

        [Test]
        public void UndoingASequenceDeleteBringsItsTrackBackInTheUndoHistoryToo()
        {
            using var rig = new ClipRig(2);
            rig.Pattern(rig.Machines[0], "A", Bar);
            var b = rig.Pattern(rig.Machines[1], "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetTrackColor(1, 5);
            layout.MoveSlot(1, 0, 2, copy: false);               // B in scene 3 of track 2
            var song = rig.Core.SongCore;

            var delete = new DeleteSequenceAction(song.SequencesList[1]);   // the Sequence View's delete
            delete.Do();
            Assert.That(layout.Document!.Tracks.Count, Is.EqualTo(1));
            delete.Undo();
            Assert.That(layout.Document!.Tracks.Count, Is.EqualTo(2));
            Assert.That(layout.PatternAt(1, 2), Is.SameAs(b), "its track came back");
            Assert.That(layout.Document!.Tracks[1].ColorIndex, Is.EqualTo(5));

            layout.Undo();                                       // the move: B back in scene 1
            Assert.That(layout.PatternAt(1, 0), Is.SameAs(b), "the undo history has the track too");
            Assert.That(layout.PatternAt(1, 2), Is.Null);
        }

        [Test]
        public void UndoingAMachineDeleteBringsBackItsTracksClipsAndSettings()
        {
            using var rig = new ClipRig(2);
            var m = rig.Machines[1];
            rig.Pattern(rig.Machines[0], "A", Bar);
            var b = rig.Pattern(m, "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            rig.Set(b, c => c.Mode = "Gate");
            layout.SetCrossfade(m, "B");
            string name = m.Name;

            rig.Core.SongCore.DeleteMachines(new[] { m });       // the Machine View's delete
            Assert.That(layout.Document!.Tracks.Count, Is.EqualTo(1));
            rig.Core.SongCore.Undo();                            // and its undo
            var back = rig.Core.SongCore.MachinesList.Single(x => x.Name == name);
            Assert.That(back, Is.Not.SameAs(m));
            var pattern = back.PatternsList.Single(x => x.Name == "B");
            Assert.That(layout.Document!.Tracks.Count, Is.EqualTo(2));
            Assert.That(layout.PatternAt(1, 0), Is.SameAs(pattern), "its track and clip came back");
            Assert.That(layout.ClipOf(pattern).Mode, Is.EqualTo("Gate"));
            Assert.That(layout.CrossfadeOf(back), Is.EqualTo("B"));
        }

        [Test]
        public void RecordingUndoKeepsEditsMadeDuringThePass()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var other = rig.Pattern(rig.Machines[0], "Other", Bar);
            rig.Layout.Ensure();
            var track = (SequenceCore)rig.Track(0);
            rig.Core.Looping = false;

            rig.Align();
            rig.Core.Recording = true;
            rig.Session.Launch(track, a);
            rig.PlayTo(8);
            track.SetEvent(64, new SequenceEvent(SequenceEventType.PlayPattern, other));   // a Sequence View edit during the pass
            rig.PlayTo(20);
            rig.Core.Recording = false;
            rig.Ticks(1);
            rig.Core.Playing = false;

            rig.Layout.Undo();                                   // the recording pass
            Assert.That(track.Events.ContainsKey(0), Is.False, "what the pass wrote is taken back");
            Assert.That(track.Events.ContainsKey(64), Is.True, "the edit made during the pass stays");
        }

        [Test]
        public void RecordingUndoKeepsLaterArrangementEdits()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var other = rig.Pattern(rig.Machines[0], "Other", Bar);
            rig.Layout.Ensure();
            var track = (SequenceCore)rig.Track(0);
            rig.Core.Looping = false;

            rig.Align();
            rig.Core.Recording = true;
            rig.Session.Launch(track, a);
            rig.PlayTo(20);
            rig.Core.Recording = false;
            rig.Ticks(1);
            rig.Core.Playing = false;
            Assert.That(track.Events.ContainsKey(0), Is.True);

            track.SetEvent(64, new SequenceEvent(SequenceEventType.PlayPattern, other));   // a Sequence View edit, later
            rig.Layout.Undo();                                   // the recording pass
            Assert.That(track.Events.ContainsKey(0), Is.False, "the recording is taken back");
            Assert.That(track.Events.ContainsKey(64), Is.True, "the later edit stays");
            rig.Layout.Redo();
            Assert.That(track.Events.ContainsKey(0), Is.True);
            Assert.That(track.Events.ContainsKey(64), Is.True);
        }
    }
}
