using BuzzGUI.Interfaces;
using ReBuzz.AppViews.Session;
using ReBuzz.Core.Session;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using Rig = ReBuzzTests.SessionViewTests.Rig;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: Session View 0.2 "Playable" (docs/specs/session-view.md): clip and grid
    /// edits with undo, MIDI mapping (learn, note grid, song override) and keyboard control,
    /// against a live song.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class SessionPlayableTests
    {
        const int Bar = 16;

        static int NoteOn(int note, int channel = 0) => 0x90 | channel | (note << 8) | (100 << 16);
        static int NoteOff(int note, int channel = 0) => 0x80 | channel | (note << 8);
        static int Cc(int number, int value, int channel = 0) => 0xB0 | channel | (number << 8) | (value << 16);

        static void Press(Rig rig, int data)
        {
            rig.Core.SendMIDIInput(data);
            if ((data & 0xF0) == 0x90)
                rig.Core.SendMIDIInput(NoteOff((data >> 8) & 0x7F, data & 0x0F));
        }

        // ------------------------------------------------------------------ clip edits

        [Test]
        public void CreateClipMakesAOneBarPatternInTheSlotAndUndoRemovesIt()
        {
            using var rig = new Rig();
            var layout = rig.Core.SessionLayout;
            layout.Ensure();

            var clip = layout.CreateClip(2, 1);

            Assert.That(clip, Is.Not.Null);
            Assert.That(clip!.Machine, Is.SameAs(rig.Lead));
            Assert.That(clip.Length, Is.EqualTo(rig.Core.TPB * 4));
            Assert.That(layout.PatternAt(2, 1), Is.SameAs(clip));

            layout.Undo();
            Assert.That(rig.Lead.Patterns, Is.Empty);
            Assert.That(layout.PatternAt(2, 1), Is.Null);

            layout.Redo();
            Assert.That(layout.PatternAt(2, 1)?.Name, Is.EqualTo(clip.Name));
        }

        [Test]
        public void NewClipLengthFollowsTheSetting()
        {
            using var rig = new Rig();
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            layout.SetClipBars(2);
            layout.SetBeatsPerBar(3);

            Assert.That(layout.CreateClip(0, 0)!.Length, Is.EqualTo(rig.Core.TPB * 3 * 2));
        }

        [Test]
        public void DuplicateClipCopiesIntoTheSlotBelowOrIntoANewScene()
        {
            using var rig = new Rig();
            var beat = rig.Pattern(rig.Drums, "Beat", Bar);
            var fill = rig.Pattern(rig.Drums, "Fill", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            layout.SetClipColor(beat, 5);
            int scenes = layout.SceneCount;

            var copy = layout.DuplicateClip(0, 0, out int target);

            Assert.That(target, Is.EqualTo(1));
            Assert.That(copy!.Name, Is.EqualTo("Beat 2"));
            Assert.That(copy, Is.Not.SameAs(beat), "an independent pattern");
            Assert.That(PatternColors.Get(copy), Is.EqualTo(5), "keeps the colour");
            Assert.That(layout.PatternAt(0, 1), Is.SameAs(copy));
            Assert.That(layout.PatternAt(0, 2), Is.SameAs(fill), "the slot below was taken, so a scene was inserted");
            Assert.That(layout.SceneCount, Is.EqualTo(scenes + 1));

            layout.Undo();
            Assert.That(rig.Drums.Patterns.Select(p => p.Name), Is.EqualTo(new[] { "Beat", "Fill" }));
            Assert.That(layout.PatternAt(0, 1), Is.SameAs(fill));
            Assert.That(layout.SceneCount, Is.EqualTo(scenes));
        }

        [Test]
        public void RenameAndDeleteOfAClipAreUndoable()
        {
            using var rig = new Rig();
            var beat = rig.Pattern(rig.Drums, "Beat", 2 * Bar);
            rig.Pattern(rig.Drums, "Fill", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();

            Assert.That(layout.RenameClip(beat, "Fill"), Is.False, "name taken");
            Assert.That(layout.RenameClip(beat, "  Groove "), Is.True);
            Assert.That(beat.Name, Is.EqualTo("Groove"));
            layout.Undo();
            Assert.That(beat.Name, Is.EqualTo("Beat"));
            Assert.That(layout.PatternAt(0, 0), Is.SameAs(beat));

            layout.DeleteClip(beat);
            Assert.That(rig.Drums.Patterns.Any(p => p.Name == "Beat"), Is.False);
            Assert.That(layout.PatternAt(0, 0), Is.Null);

            layout.Undo();
            var back = layout.PatternAt(0, 0);
            Assert.That(back?.Name, Is.EqualTo("Beat"));
            Assert.That(back!.Length, Is.EqualTo(2 * Bar));
        }

        [Test]
        public void GridEditsAreUndoable()
        {
            using var rig = new Rig();
            rig.Pattern(rig.Drums, "Beat", Bar);
            rig.Pattern(rig.Bass, "Line", Bar);
            var layout = rig.Core.SessionLayout;
            var doc = layout.Ensure();
            int scenes = doc.Scenes.Count;

            layout.MoveSlot(0, 0, 3, copy: false);
            layout.DuplicateScene(3);
            layout.SetTrackColor(1, 2);
            layout.SetSceneColor(0, 9);
            layout.RenameScene(0, "Intro");

            Assert.That(layout.Document!.GetSlot(0, 3), Is.EqualTo("Beat"));
            Assert.That(layout.Document.GetSlot(0, 4), Is.EqualTo("Beat"));
            Assert.That(layout.Document.Tracks[1].ColorIndex, Is.EqualTo(2));

            for (int i = 0; i < 5; i++) layout.Undo();
            Assert.That(layout.Document!.GetSlot(0, 0), Is.EqualTo("Beat"));
            Assert.That(layout.Document.GetSlot(0, 3), Is.Null);
            Assert.That(layout.Document.Scenes.Count, Is.EqualTo(scenes));
            Assert.That(layout.Document.Tracks[1].ColorIndex, Is.EqualTo(-1));
            Assert.That(layout.Document.Scenes[0].Name, Is.EqualTo(""));
            Assert.That(layout.UndoStack.CanUndo, Is.False);

            for (int i = 0; i < 5; i++) layout.Redo();
            Assert.That(layout.Document!.Scenes[0].Name, Is.EqualTo("Intro"));
            Assert.That(layout.Document.Scenes[0].ColorIndex, Is.EqualTo(9));
            Assert.That(layout.Document.GetSlot(0, 4), Is.EqualTo("Beat"));
        }

        [Test]
        public void ClipColourIsThePatternColourAndUndoable()
        {
            using var rig = new Rig();
            var beat = rig.Pattern(rig.Drums, "Beat", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();

            layout.SetClipColor(beat, 3);
            Assert.That(PatternColors.Get(beat), Is.EqualTo(3));
            layout.Undo();
            Assert.That(PatternColors.Get(beat), Is.EqualTo(-1));
        }

        [Test]
        public void NewSongStartsWithAnEmptyUndoHistory()
        {
            using var rig = new Rig();
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            layout.InsertScene(0);
            Assert.That(layout.UndoStack.CanUndo, Is.True);

            rig.Core.Modified = false;
            rig.Driver.NewFile();
            Assert.That(layout.UndoStack.CanUndo, Is.False);
        }

        // ------------------------------------------------------------------ MIDI

        [Test]
        public void MidiLearnBindsTheNextPressAndTheControlThenLaunchesTheSlot()
        {
            using var rig = new Rig();
            var beat = rig.Pattern(rig.Drums, "Beat", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            var midi = rig.Core.SessionMidi;

            midi.BeginLearn(SessionTarget.Slot, 0, 0);
            Assert.That(midi.IsLearning, Is.True);
            rig.Core.SendMIDIInput(NoteOff(40));            // a release doesn't count
            Assert.That(midi.IsLearning, Is.True);
            Press(rig, NoteOn(36, 9));
            Assert.That(midi.IsLearning, Is.False);
            Assert.That(midi.FindBinding(SessionTarget.Slot, 0, 0)?.Control, Is.EqualTo(new MidiControl(MidiControlType.Note, 9, 36)));
            Assert.That(rig.Core.Session.GetState(layout.SequenceOf(0)!, beat), Is.EqualTo(ClipState.Stopped), "learning doesn't launch");

            Press(rig, NoteOn(36, 9));
            Assert.That(rig.Core.Session.GetState(layout.SequenceOf(0)!, beat), Is.EqualTo(ClipState.Queued));
            Assert.That(midi.Consumes(NoteOn(36, 9)), Is.True, "mapped notes don't reach machines");
            Assert.That(midi.Consumes(NoteOn(37, 9)), Is.False);
        }

        [Test]
        public void MidiControlsSceneStopAllQuantizationAndTransport()
        {
            using var rig = new Rig();
            var beat = rig.Pattern(rig.Drums, "Beat", Bar);
            var line = rig.Pattern(rig.Bass, "Line", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            var midi = rig.Core.SessionMidi;
            var session = rig.Core.Session;

            midi.BeginLearn(SessionTarget.Scene, 0, 0); Press(rig, Cc(20, 127));
            midi.BeginLearn(SessionTarget.StopAll, 0, 0); Press(rig, Cc(21, 127));
            midi.BeginLearn(SessionTarget.Quantization, 0, 0); Press(rig, Cc(22, 127));
            midi.BeginLearn(SessionTarget.Transport, 0, 0); Press(rig, NoteOn(60));

            Press(rig, Cc(22, 0));
            Assert.That(session.LaunchQuantization, Is.EqualTo(LaunchQuantization.None), "CC value picks across the range");
            Press(rig, Cc(22, 127));
            Assert.That(session.LaunchQuantization, Is.EqualTo(LaunchQuantization.EightBars));

            Press(rig, Cc(20, 127));
            Assert.That(session.GetState(layout.SequenceOf(0)!, beat), Is.Not.EqualTo(ClipState.Stopped));
            Assert.That(session.GetState(layout.SequenceOf(1)!, line), Is.Not.EqualTo(ClipState.Stopped));
            rig.RunTicks(1);

            Press(rig, Cc(22, 0));                      // back to no quantization: the stop comes at the next tick
            Press(rig, Cc(21, 127));
            rig.RunTicks(2);
            Assert.That(session.GetPlaying(layout.SequenceOf(0)!), Is.Null);

            bool playing = rig.Core.PlayRequested;
            Press(rig, NoteOn(60));
            Assert.That(rig.Core.PlayRequested, Is.EqualTo(!playing));
        }

        [Test]
        public void NoteGridMapsPadsOntoSlots()
        {
            using var rig = new Rig();
            var beat = rig.Pattern(rig.Drums, "Beat", Bar);
            var hook = rig.Pattern(rig.Lead, "Hook", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            layout.MoveSlot(2, 0, 2, copy: false);     // Hook to scene 3
            var midi = rig.Core.SessionMidi;
            midi.SetGrid(new NoteGrid { FirstNote = 36, Columns = 4, Rows = 4, BottomUp = true });

            Press(rig, NoteOn(36 + 12));               // top-left pad: track 1, scene 1
            Press(rig, NoteOn(36 + 4 + 2));            // third row from the top, third pad: track 3, scene 3
            Assert.That(rig.Core.Session.GetState(layout.SequenceOf(0)!, beat), Is.EqualTo(ClipState.Queued));
            Assert.That(rig.Core.Session.GetState(layout.SequenceOf(2)!, hook), Is.EqualTo(ClipState.Queued));
            // MIDI 42, named as Preferences name notes (ux-overhaul.md §3.5): F#1 when C3 is 60, the default.
            Assert.That(midi.Describe(SessionTarget.Slot, 2, 2), Does.Contain(BuzzGUI.Common.NoteNames.Name(42)));
            Assert.That(BuzzGUI.Common.NoteNames.Name(42, BuzzGUI.Common.NoteNaming.C3), Is.EqualTo("F#1"));
        }

        [Test]
        public void MidiMovesTheNoteGridOverTheSession()
        {
            using var rig = new Rig();
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            var midi = rig.Core.SessionMidi;
            midi.SetGrid(new NoteGrid { FirstNote = 36, Columns = 2, Rows = 2 });
            midi.BeginLearn(SessionTarget.GridDown, 0, 0); Press(rig, Cc(40, 127));
            midi.BeginLearn(SessionTarget.GridRight, 0, 0); Press(rig, Cc(41, 127));
            midi.BeginLearn(SessionTarget.GridUp, 0, 0); Press(rig, Cc(42, 127));

            Press(rig, Cc(40, 127));
            Press(rig, Cc(40, 127));
            Press(rig, Cc(41, 127));
            Assert.That((midi.ActiveMap.Grid!.OriginTrack, midi.ActiveMap.Grid.OriginScene), Is.EqualTo((1, 2)));

            for (int i = 0; i < 10; i++) Press(rig, Cc(42, 127));
            for (int i = 0; i < 10; i++) Press(rig, Cc(41, 127));
            Assert.That((midi.ActiveMap.Grid!.OriginTrack, midi.ActiveMap.Grid.OriginScene), Is.EqualTo((layout.TrackCount - 1, 0)), "stays inside the session");
        }

        [Test]
        public void ASongCanCarryItsOwnMapping()
        {
            using var rig = new Rig();
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            var midi = rig.Core.SessionMidi;
            midi.BeginLearn(SessionTarget.StopAll, 0, 0); Press(rig, Cc(30, 127));
            var userMap = midi.ActiveMap;

            midi.SetUseSongMap(true);
            Assert.That(midi.UsesSongMap, Is.True);
            Assert.That(layout.Document!.SongMidi, Is.Not.Null);
            midi.Unbind(SessionTarget.StopAll, 0, 0);
            Assert.That(midi.ActiveMap.IsEmpty, Is.True, "the song's copy changed");

            midi.SetUseSongMap(false);
            Assert.That(midi.ActiveMap, Is.SameAs(userMap), "the user's mapping is untouched");
            Assert.That(layout.Document!.SongMidi, Is.Null);
        }

        [Test]
        public void ClearAllRemovesBindingsAndGrid()
        {
            using var rig = new Rig();
            var midi = rig.Core.SessionMidi;
            rig.Core.SessionLayout.Ensure();
            midi.SetGrid(new NoteGrid());
            midi.BeginLearn(SessionTarget.Transport, 0, 0); Press(rig, NoteOn(1));
            Assert.That(midi.ActiveMap.IsEmpty, Is.False);

            midi.ClearAll();
            Assert.That(midi.ActiveMap.IsEmpty, Is.True);
        }

        // ------------------------------------------------------------------ keyboard

        [Test]
        public void KeyboardDrivesTheGrid()
        {
            using var rig = new Rig();
            var beat = rig.Pattern(rig.Drums, "Beat", Bar);
            var line = rig.Pattern(rig.Bass, "Line", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            var view = new SessionView(rig.Core);
            view.Rebuild();
            var session = rig.Core.Session;

            Assert.That(view.HandleKey(Key.Down, ModifierKeys.None), Is.True);      // selects (0,0)
            Assert.That(view.HandleKey(Key.Enter, ModifierKeys.None), Is.True);
            Assert.That(session.GetState(layout.SequenceOf(0)!, beat), Is.EqualTo(ClipState.Queued));

            view.HandleKey(Key.Right, ModifierKeys.None);                            // (1,0): Line
            view.HandleKey(Key.D, ModifierKeys.Control);                             // duplicate below
            Assert.That(layout.PatternAt(1, 1)?.Name, Is.EqualTo("Line 2"));
            view.HandleKey(Key.Z, ModifierKeys.Control);
            Assert.That(layout.PatternAt(1, 1), Is.Null);

            // The selection followed the copy to (1,1), now empty again.
            view.HandleKey(Key.M, ModifierKeys.Control | ModifierKeys.Shift);        // create clip
            Assert.That(layout.PatternAt(1, 1), Is.Not.Null);
            view.HandleKey(Key.Delete, ModifierKeys.None);
            Assert.That(layout.PatternAt(1, 1), Is.Null);

            view.HandleKey(Key.D1, ModifierKeys.None);                               // scene 1
            Assert.That(session.GetState(layout.SequenceOf(1)!, line), Is.EqualTo(ClipState.Queued));

            int scenes = layout.SceneCount;
            view.HandleKey(Key.I, ModifierKeys.Control);
            Assert.That(layout.SceneCount, Is.EqualTo(scenes + 1));

            bool playing = rig.Core.PlayRequested;
            view.HandleKey(Key.Space, ModifierKeys.None);
            Assert.That(rig.Core.PlayRequested, Is.EqualTo(!playing));
            view.HandleKey(Key.Space, ModifierKeys.None);
            Assert.That(rig.Core.PlayRequested, Is.EqualTo(playing));
        }

        // ------------------------------------------------------------------ look

        [Test]
        public void RendersColoursMuteAndLearn()
        {
            using var rig = new Rig();
            var beat = rig.Pattern(rig.Drums, "Beat", Bar);
            var fill = rig.Pattern(rig.Drums, "Fill", Bar);
            var line = rig.Pattern(rig.Bass, "Line A", 2 * Bar);
            rig.Pattern(rig.Bass, "Line B", 2 * Bar);
            var hook = rig.Pattern(rig.Lead, "Hook", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            layout.SetClipColor(beat, 9);      // red
            layout.SetClipColor(fill, 10);     // orange
            layout.SetClipColor(line, 14);     // sky blue
            layout.SetClipColor(hook, 2);      // wheat (dark text)
            layout.SetTrackColor(0, 9);
            layout.SetTrackColor(1, 14);
            layout.SetSceneColor(0, 13);
            layout.RenameScene(0, "Intro");
            rig.Bass.IsMuted = true;
            rig.Core.SessionMidi.BeginLearn(SessionTarget.Scene, 0, 1);

            var view = new SessionView(rig.Core);
            view.Rebuild();
            layout.LaunchScene(0);
            rig.RunTicks(5);
            view.HandleKey(Key.Down, ModifierKeys.None);
            view.HandleKey(Key.Down, ModifierKeys.None);                             // select (0,1)
            // Three refreshes so far (build, two moves): the blinking learn outline is in its "on" phase.
            SessionViewTests.Screenshot(view, "session-view-colours");

            Assert.That(rig.Core.Session.GetState(layout.SequenceOf(0)!, beat), Is.EqualTo(ClipState.Playing));
        }
    }
}
