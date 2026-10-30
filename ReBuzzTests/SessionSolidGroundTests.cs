using BuzzGUI.Interfaces;
using ReBuzz.AppViews.Session;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using static ReBuzzTests.SessionClipTests;
using FollowSettings = ReBuzz.Core.Session.SessionDocument.FollowSettings;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: regressions for the 0.6.1 review (docs/specs/session-view.md 0.6.1), through
    /// the real engine: queued scenes against Stop All and undo, late scenes and the bar grid,
    /// beats per bar changed while playing, undo keeping performance settings, deleted patterns
    /// and stale references, the crossfader's position, songs loading, plain songs, long .bmx
    /// arrangements and autosave recovery. Scheduler cases are unit tested in
    /// ReBuzz.SessionTests/SessionHardeningTests.cs.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class SessionSolidGroundTests
    {
        const int Bar = 16;

        // ------------------------------------------------------------------ scenes

        [Test]
        public void StopAllDropsAQueuedScenesTempoAndFollowAction()
        {
            using var rig = new ClipRig(1);
            rig.Pattern(rig.Machines[0], "A", Bar);
            rig.Pattern(rig.Machines[0], "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetSceneTempo(0, 140);
            layout.SetSceneFollow(0, new FollowSettings { On = true, A = "Next", Time = "1.0.0" });
            int bpm = rig.Core.BPM;

            rig.PlayTo(1);
            layout.LaunchScene(0);                               // 16
            rig.PlayTo(2);
            rig.Session.StopAll();
            rig.PlayTo(16);
            Assert.That(rig.Core.BPM, Is.EqualTo(bpm), "its tempo went with its clips");
            rig.PlayTo(48);
            Assert.That(rig.Session.GetPlaying(rig.Track(0)), Is.Null, "and its follow action never launched scene 2");
        }

        [Test]
        public void UndoingAClipLaunchKeepsAQueuedScenesSettings()
        {
            using var rig = new ClipRig(2);
            rig.Pattern(rig.Machines[0], "A", Bar);
            var b = rig.Pattern(rig.Machines[1], "B", Bar);
            rig.Pattern(rig.Machines[1], "B2", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetSceneTempo(0, 140);

            rig.PlayTo(1);
            layout.LaunchScene(0);                               // 16, with its tempo
            layout.LaunchClip(rig.Track(1), rig.Machines[1].PatternsList[1]);   // B2 replaces B on track 2
            ((SessionCore)rig.Session).UndoLaunch();             // only that clip launch: B is queued again
            rig.PlayTo(16);
            Assert.That(rig.Core.BPM, Is.EqualTo(140), "the scene's tempo still comes");
            Assert.That(rig.Session.GetPlaying(rig.Track(1)), Is.SameAs(b));
        }

        [Test]
        public void ALateSceneRestartsTheBarGridOnItsGridPoint()
        {
            using var rig = new ClipRig(1);
            rig.Pattern(rig.Machines[0], "A", Bar);
            rig.Pattern(rig.Machines[0], "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetSceneBeatsPerBar(0, 3);
            layout.SetSceneFollow(0, new FollowSettings { On = true, A = "Next", Time = "1.0.0" });   // a 3/4 bar: 12 ticks
            var core = (SessionCore)rig.Session;

            rig.PlayTo(17);
            rig.Driver.ReadStereoSamples(rig.SamplesPerTick / 2);    // rendered: about 17.5 ticks
            rig.Core.OutputLatencySamples = 3 * rig.SamplesPerTick;  // heard: about 14.5
            layout.LaunchScene(0);                               // the bar line at 16 is rendered: in step with it, from 18
            rig.Core.OutputLatencySamples = 0;
            rig.PlayTo(18);
            Assert.That(rig.Session.GetClipStartClock(rig.Track(0)), Is.EqualTo(16));
            Assert.That(core.GridOrigin, Is.EqualTo(16), "3/4 bars count from the grid point it is heard on, not from 18");
            rig.PlayTo(27);
            Assert.That(rig.Session.GetPlaying(rig.Track(0))!.Name, Is.EqualTo("A"));
            rig.PlayTo(28);
            Assert.That(rig.Session.GetPlaying(rig.Track(0))!.Name, Is.EqualTo("B"), "its follow action comes a 3/4 bar after 16");
        }

        [Test]
        public void ChangingBeatsPerBarWhilePlayingRestartsTheGridOnTheBarPlaying()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            var core = (SessionCore)rig.Session;

            rig.PlayTo(21);                                      // bar 2 started at 16
            layout.SetBeatsPerBar(3);
            Assert.That(core.GridOrigin, Is.EqualTo(16));
            Assert.That(core.GridOriginBar, Is.EqualTo(1));
            layout.LaunchClip(rig.Track(0), a);                  // the next 3/4 bar line: 28
            rig.PlayTo(27);
            Assert.That(rig.Session.GetPlaying(rig.Track(0)), Is.Null);
            rig.PlayTo(28);
            Assert.That(rig.Session.GetPlaying(rig.Track(0)), Is.SameAs(a));
        }

        // ------------------------------------------------------------------ undo and settings

        [Test]
        public void GridUndoKeepsPerformanceSettings()
        {
            using var rig = new ClipRig(2);
            rig.Pattern(rig.Machines[0], "A", Bar);
            rig.Pattern(rig.Machines[1], "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.RenameScene(0, "Intro");
            layout.SetCrossfade(rig.Machines[0], "A");
            layout.SetCrossfaderCurve("Fast cut");
            layout.SetFollowActionsEnabled(false);
            layout.SetScenesStopOtherClips(true);

            layout.Undo();                                       // the rename
            var doc = layout.Document!;
            Assert.That(doc.Scenes[0].Name, Is.Not.EqualTo("Intro"));
            Assert.That(layout.CrossfadeOf(rig.Machines[0]), Is.EqualTo("A"));
            Assert.That(doc.CrossfaderCurve, Is.EqualTo("Fast cut"));
            Assert.That(doc.FollowActionsEnabled, Is.False);
            Assert.That(((SessionCore)rig.Session).FollowActionsEnabled, Is.False);
            Assert.That(doc.ScenesStopOtherClips, Is.True);
        }

        [Test]
        public void TheCrossfaderKeepsItsPositionWhenTheLayoutChanges()
        {
            using var rig = new ClipRig(1, hum: true);
            rig.Pattern(rig.Machines[0], "A", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetCrossfade(rig.Machines[0], "A");
            var core = (SessionCore)rig.Session;

            core.SetCrossfader(0.9);                             // as MIDI does, ahead of the document
            layout.RenameScene(0, "Intro");                      // rebuilds the launch map
            Assert.That(core.CrossfaderPosition, Is.EqualTo(0.9));
            Assert.That(rig.Machines[0].CrossfadeGain, Is.EqualTo(0.2f).Within(1e-4));
        }

        // ------------------------------------------------------------------ gone

        [Test]
        public void ADeletedPatternDoesNotComeBackThroughUndoOrAFollowAction()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var b = rig.Pattern(rig.Machines[0], "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            rig.Set(b, c => { c.Follow.On = true; c.Follow.A = "Next"; });   // B → A (Next wraps)
            var track = rig.Track(0);
            var core = (SessionCore)rig.Session;

            rig.PlayTo(1);
            layout.LaunchClip(track, a);                         // 16
            rig.PlayTo(17);
            layout.LaunchClip(track, b);                         // 32; its follow action is due at 48
            rig.PlayTo(33);

            rig.Driver.Dispatcher.Deferred = true;               // the UI hasn't caught up: the launch map still has A
            rig.Machines[0].DeletePattern(a);
            Assert.That(core.LastLaunch, Is.Null, "the history forgets launches that involve A");
            Assert.That(core.UndoLaunch(), Is.Null);
            rig.PlayTo(48);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(b), "the follow action can't launch the deleted A");
            Assert.That(a.IsDeleted, Is.True);
            rig.Driver.Dispatcher.Deferred = false;
            rig.Driver.Dispatcher.RunPending();
        }

        [Test]
        public void NothingLaunchesWhileASongLoads()
        {
            using var rig = new ClipRig(1);
            rig.Pattern(rig.Machines[0], "A", Bar);
            rig.Layout.Ensure();
            rig.Core.SessionMidi.BeginLearn(SessionTarget.Slot, 0, 0);
            rig.Core.SendMIDIInput(NoteOn(36, 100));             // learnt

            rig.Core.SongLoading = true;
            rig.Core.SendMIDIInput(NoteOn(36, 100));
            Assert.That(rig.Core.Playing, Is.False, "a pad doesn't start a song that is loading");
            Assert.That(rig.Session.GetQueued(rig.Track(0)), Is.Null);
            rig.Core.SongLoading = false;
            rig.Core.SendMIDIInput(NoteOn(36, 100));
            Assert.That(rig.Session.GetQueued(rig.Track(0)), Is.Not.Null);
        }

        // ------------------------------------------------------------------ files

        [Test]
        public void ASongOnlyLookedAtInTheSessionViewSavesNoLayout()
        {
            using var rig = new ClipRig(1);
            rig.Pattern(rig.Machines[0], "A", Bar);
            var view = new SessionView(rig.Core);
            view.Rebuild();                                      // auto-fills a layout to show
            Assert.That(rig.Layout.Document, Is.Not.Null);

            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "plain-" + Guid.NewGuid().ToString("N") + ".bmx");
            rig.Core.SaveSongCopy(path);
            rig.Core.Modified = false;
            rig.Core.OpenSongFile(path);
            Assert.That(rig.Layout.Document, Is.Null, "no Session View data in the file");

            rig.Layout.Ensure();
            rig.Layout.RenameScene(0, "Edited");                 // edited: now it is saved
            rig.Core.SaveSongCopy(path);
            rig.Core.Modified = false;
            rig.Core.OpenSongFile(path);
            Assert.That(rig.Layout.Document?.Scenes[0].Name, Is.EqualTo("Edited"));
        }

        [Test]
        public void BmxKeepsArrangementPositionsPast65535Ticks()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var track = (SequenceCore)rig.Core.SongCore.SequencesList.Single(s => s.Machine == rig.Machines[0]);
            track.SetEvent(16, new SequenceEvent(SequenceEventType.PlayPattern, a));
            track.SetEvent(70000, new SequenceEvent(SequenceEventType.PlayPattern, a));

            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "long-" + Guid.NewGuid().ToString("N") + ".bmx");
            rig.Core.SaveSongCopy(path);
            rig.Core.Modified = false;
            rig.Core.OpenSongFile(path);
            rig.Driver.AssertNoErrorsReportedToUser();
            var loaded = rig.Core.SongCore.SequencesList.Single(s => s.Machine.Name == "Tone0");
            Assert.That(loaded.Events.Keys, Is.EqualTo(new[] { 16, 70000 }));
        }

        [Test]
        public void AnAutosaveThatCannotBeOpenedIsKept()
        {
            string original = SongAutosave.Folder;
            string folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "autosave-" + Guid.NewGuid().ToString("N"));
            SongAutosave.Folder = folder;
            try
            {
                Directory.CreateDirectory(folder);
                string path = SongAutosave.PathFor(null);
                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });     // not a song
                File.WriteAllText(path + ".txt", "");
                using var rig = new ClipRig(0);
                var leftovers = SongAutosave.FindLeftovers();
                Assert.That(leftovers.Count, Is.EqualTo(1));
                Assert.That(rig.Core.Autosave.Recover(leftovers[0]), Is.False);
                Assert.That(File.Exists(path), Is.True, "kept to try again or open by hand");
            }
            finally
            {
                SongAutosave.Folder = original;
            }
        }
    }
}
