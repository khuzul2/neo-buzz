// Fork (docs/specs/studio-mvp.md S5): the Session View on Avalonia, driven headless through its
// controls with simulated mouse and keys, following the scenarios of the WPF view's tests
// (ReBuzzTests: SessionViewTests, SessionPlayableTests, SessionClipTests, SessionMixerTests,
// SessionRecordingTests, SessionPerformanceTests, PianoRollTests). Each saves screenshots.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Session;
using static ReBuzz.Studio.Tests.SessionRig;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class SessionViewTests
    {
        static ClipState StateOf(SessionRig rig, int track, IPattern clip) => rig.Session.GetState(rig.Layout.SequenceOf(track), clip);

        // ------------------------------------------------------------------ the tab

        [AvaloniaTest]
        public void TheSessionTabIsTheSessionView()
        {
            using var rig = new SessionRig("Drums", "Bass");
            rig.Pattern(rig.Machine("Drums"), "Beat", Bar);
            rig.Pattern(rig.Machine("Bass"), "Line", 2 * Bar);
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            Quiet(window);
            window.Show();
            try
            {
                var tab = window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Session");
                window.Tabs.SelectedItem = tab;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.That(tab.Content, Is.InstanceOf<SessionView>());
                var view = (SessionView)tab.Content;
                Assert.That(view.Slots.Where(s => s.Pattern != null).Select(s => s.Pattern.Name), Is.EquivalentTo(new[] { "Beat", "Line" }),
                    "a song without a layout fills the grid from its patterns");
                Assert.That(view.Headers.Select(h => h.Machine?.Name), Is.EqualTo(new[] { "Drums", "Bass" }));
                Studio.Screenshot(window, "session-tab");
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ launching (SessionViewTests)

        [AvaloniaTest]
        public void LaunchingASceneShowsItQueuedThenPlaying()
        {
            using var rig = new SessionRig("Drums", "Bass", "Lead");
            var beat = rig.Pattern(rig.Machine("Drums"), "Beat", Bar, (0, 36), (4, 38), (8, 36), (12, 38));
            var fill = rig.Pattern(rig.Machine("Drums"), "Fill", Bar, (0, 36), (2, 36), (4, 38));
            rig.Pattern(rig.Machine("Drums"), "Break", 2 * Bar);
            var line = rig.Pattern(rig.Machine("Bass"), "Line A", 2 * Bar, (0, 40), (8, 43));
            var lineB = rig.Pattern(rig.Machine("Bass"), "Line B", 2 * Bar, (0, 45));
            var hook = rig.Pattern(rig.Machine("Lead"), "Hook", Bar, (0, 72));
            rig.Pattern(rig.Machine("Lead"), "Answer", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.RenameScene(0, "Intro");
            layout.RenameScene(1, "Verse");
            layout.RenameScene(2, "Drop");
            layout.SetClipColor(fill, 10);          // orange
            layout.SetClipColor(lineB, 14);         // sky blue
            layout.SetTrackColor(0, 9);
            layout.SetSceneColor(0, 13);
            layout.SetSceneTempo(2, 140);

            var view = rig.Show();
            Assert.That(view.ClockText, Is.EqualTo("–.–.–"), "stopped");
            rig.Screenshot("session-grid");

            rig.Click(view.Scenes[0]);                                 // the scene's button
            Assert.That(StateOf(rig, 0, beat), Is.EqualTo(ClipState.Queued));
            Assert.That(StateOf(rig, 1, line), Is.EqualTo(ClipState.Queued));
            Assert.That(StateOf(rig, 2, hook), Is.EqualTo(ClipState.Queued));
            Assert.That(view.Selected, Is.EqualTo((0, 0)), "select on launch: the scene's row");

            rig.RunTicks(6);
            view.RefreshState();
            Assert.That(StateOf(rig, 0, beat), Is.EqualTo(ClipState.Playing));
            Assert.That(StateOf(rig, 1, line), Is.EqualTo(ClipState.Playing));
            Assert.That(view.ClockText, Does.StartWith("1.2."), "the session clock: bar.beat.tick");
            Assert.That(view.SlotAt(0, 0).Background, Is.SameAs(SessionPalette.Playing).Or.SameAs(SessionPalette.Lighten(SessionPalette.Playing, 0.1)),
                "an uncoloured clip turns green while it plays");

            // Line B waits for the next bar on the bass track: its ▶ launches it.
            rig.Click(view.SlotAt(1, 1).LaunchArea);
            Assert.That(StateOf(rig, 1, lineB), Is.EqualTo(ClipState.Queued));
            Assert.That(StateOf(rig, 1, line), Is.EqualTo(ClipState.Playing), "until the bar line");
            rig.Screenshot("session-grid-playing");

            rig.RunTicks(Bar);
            Assert.That(StateOf(rig, 1, lineB), Is.EqualTo(ClipState.Playing));

            // The track's stop button, then Stop all.
            rig.Click(view.SlotAt(1, 3), fx: 0.5);                    // an empty slot's ■
            rig.RunTicks(Bar);
            Assert.That(rig.Session.GetPlaying(rig.Layout.SequenceOf(1)), Is.Null, "the empty slot's ■ stopped the track");
            rig.Click(view.StopAllButton);
            rig.RunTicks(Bar);
            Assert.That(rig.Session.GetPlaying(rig.Layout.SequenceOf(0)), Is.Null);
        }

        // ------------------------------------------------------------------ clips and notes (SessionPlayableTests, PianoRollTests)

        [AvaloniaTest]
        public void MakeAClipAndWriteNotesInThePianoRoll()
        {
            using var rig = new SessionRig("Keys");
            rig.Layout.Ensure();
            var view = rig.Show();

            rig.DoubleClick(view.SlotAt(0, 0), fx: 0.85);              // an empty slot (not its ■): a new clip
            var clip = rig.Layout.PatternAt(0, 0);
            Assert.That(clip, Is.Not.Null, "double-click creates a clip");
            Assert.That(clip.Length, Is.EqualTo(Bar), "one bar (⚙ New clip length)");
            Assert.That(view.Selected, Is.EqualTo((0, 0)));
            Assert.That(view.ClipPanelView.TitleText, Is.EqualTo(clip.Name), "the clip panel shows it");

            // Ctrl+double-click (Command on macOS): its notes in the piano roll.
            rig.DoubleClick(view.SlotAt(0, 0), RawInputModifiers.Control);
            var piano = PianoRollWindow.OpenWindows.Single();
            Quiet(piano);
            var roll = piano.Roll;
            Assert.That(roll.Pattern, Is.SameAs(clip));
            Assert.That(piano.SummaryText, Does.Contain("No notes yet"));

            void At(int time, int note, Action<Avalonia.Point> act)
            {
                rig.Settle();
                piano.UpdateLayout();
                var p = roll.TranslatePoint(roll.PointOf(time, note), piano);
                Assert.That(p, Is.Not.Null);
                act(p!.Value);
            }
            void DoubleClickNote(int time, int note) => At(time, note, p => MachineViewTests.DoubleClick(piano, p));

            DoubleClickNote(0, 60);                                     // double-click draws a note, a grid step long
            DoubleClickNote(4 * TB, 64);
            Assert.That(rig.Layout.NotesOf(clip), Is.EqualTo(new[] { new ClipNote(0, TB, 60, 100), new ClipNote(4 * TB, TB, 64, 100) }));
            Assert.That(clip.Columns.Single().Type, Is.EqualTo(PatternColumnType.MIDI), "drawing gave the clip an engine MIDI column");

            // Drag the second note two ticks right and three semitones up.
            At(4 * TB, 64, p =>
            {
                piano.MouseDown(p, MouseButton.Left);
                var to = new Avalonia.Point(p.X + 2 * roll.TickWidth, p.Y - 3 * roll.RowHeight);
                piano.MouseMove(new Avalonia.Point((p.X + to.X) / 2, (p.Y + to.Y) / 2), RawInputModifiers.LeftMouseButton);
                piano.MouseMove(to, RawInputModifiers.LeftMouseButton);
                piano.MouseUp(to, MouseButton.Left);
            });
            Assert.That(rig.Layout.NotesOf(clip).Last(), Is.EqualTo(new ClipNote(6 * TB, TB, 67, 100)));

            // Keys: Up moves the selection a semitone; Ctrl+Z (Command on macOS) undoes it here.
            rig.Key(PhysicalKey.ArrowUp, top: piano);
            Assert.That(rig.Layout.NotesOf(clip).Last().Note, Is.EqualTo(68));
            rig.Key(PhysicalKey.Z, RawInputModifiers.Control, piano);
            Assert.That(rig.Layout.NotesOf(clip).Last().Note, Is.EqualTo(67), "each edit is one undo step of the session");
            Assert.That(piano.SummaryText, Is.EqualTo("2 notes"), "the undone move's note isn't selected any more");
            rig.Screenshot("session-piano-roll", piano);

            // The clip panel counts them, and the engine plays the clip.
            view.RefreshState();
            Assert.That(view.ClipPanelView.InfoText, Does.Contain("2 recorded notes"));
            rig.Click(view.SlotAt(0, 0).LaunchArea);
            rig.RunTicks(3);
            Assert.That(StateOf(rig, 0, clip), Is.EqualTo(ClipState.Playing));
            piano.Poll();
            rig.Screenshot("session-piano-roll-playing", piano);
        }

        // ------------------------------------------------------------------ recording (SessionRecordingTests)

        [AvaloniaTest]
        public void ArmATrackAndRecordATakeWithTheTransportRunning()
        {
            using var rig = new SessionRig("Keys");
            var layout = rig.Layout;
            layout.Ensure();
            var view = rig.Show();

            var settings = view.SettingsButton;
            rig.Click(settings);
            rig.Menu("Record length/1 bar");                            // ⚙ › Record length › 1 bar
            Assert.That(layout.Document.RecordBars, Is.EqualTo(1));

            rig.Key(PhysicalKey.Space);                                 // the transport runs
            Assert.That(rig.Core.PlayRequested, Is.True);
            rig.RunTicks(3);

            rig.Click(view.Headers[0].Arm);                             // the header's ●
            Assert.That(layout.IsArmed(0), Is.True);
            view.RefreshState();
            var empty = view.SlotAt(0, 0);
            Assert.That(empty.RecordButton.IsVisible, Is.True, "an armed track's empty slots record");
            rig.Click(empty.RecordButton);
            var take = layout.PatternAt(0, 0);
            var track = layout.SequenceOf(0);
            Assert.That(take, Is.Not.Null, "a take in the slot");
            Assert.That(take.Length, Is.EqualTo(Bar));
            Assert.That(layout.IsRecordingInto(track, take), Is.True);
            Assert.That(StateOf(rig, 0, take), Is.EqualTo(ClipState.Queued), "it waits for the bar line");

            rig.PlayTo(Bar + 2);                                       // the take started at bar 2
            rig.Core.SendMIDIInput(NoteOn(60, 99));
            rig.PlayTo(Bar + 6);
            rig.Core.SendMIDIInput(NoteOff(60));
            rig.RunTicks(1);
            view.RefreshState();
            Assert.That(view.SlotAt(0, 0).Glyph, Is.EqualTo("●"), "a take recording shows ●");
            Assert.That(view.ClipPanelView.StatusText, Does.Contain("recording"));
            rig.Screenshot("session-recording");

            rig.PlayTo(2 * Bar + 2);
            view.RefreshState();                                        // polls the takes
            Assert.That(layout.IsRecording(track), Is.False, "its first pass is over");
            Assert.That(layout.NotesOf(take), Is.EqualTo(new[] { new ClipNote(2 * TB, 4 * TB, 60, 99) }),
                "the note where it was played in the take");
            Assert.That(StateOf(rig, 0, take), Is.EqualTo(ClipState.Playing), "and it loops on");
            Assert.That(view.SlotAt(0, 0).Glyph, Is.EqualTo("▶"));

            // The take is one undo step.
            string name = take.Name;
            rig.Click(view.UndoButton);
            Assert.That(rig.Machine("Keys").Patterns.Any(p => p.Name == name), Is.False, "undo takes the whole take");
            rig.Click(view.RedoButton);
            Assert.That(layout.NotesOf(layout.PatternAt(0, 0)).Single().Note, Is.EqualTo(60), "redo brings it back with what it recorded");
        }

        // ------------------------------------------------------------------ mixing (SessionMixerTests)

        [AvaloniaTest]
        public void MixWithTheFadersAndSendToAReturn()
        {
            using var rig = new SessionRig("Drums", "Lead");
            var drums = rig.Machine("Drums");
            var lead = rig.Machine("Lead");
            var verb = rig.Add("Neo Verb", "Verb", track: false);
            rig.Pattern(drums, "Beat", Bar, (0, 48), (4, 50), (8, 48), (12, 50));
            rig.Pattern(lead, "Hook", Bar, (0, 72), (2, 76), (4, 79), (8, 84));
            var layout = rig.Layout;
            layout.Ensure();
            layout.RenameScene(0, "Intro");
            var view = rig.Show(1100, 720);

            rig.Click(view.SettingsButton);
            rig.Menu("Return tracks/Verb");                             // ⚙ › Return tracks › Verb
            Assert.That(layout.Document.Returns, Is.EqualTo(new[] { "Verb" }));
            Assert.That(view.DescribeColumns(), Is.EqualTo("Drums Lead A:Verb"));

            // Edits rebuild the view: the strips are fetched again each time.
            SessionView.MixerStrip Strip(IMachine m) => view.MixerStrips.Single(s => s.Kind == SessionView.StripKind.Track && s.Machine == m);
            var toMaster = lead.Outputs.Single(o => o.Destination == rig.Master);
            Assert.That(Strip(lead).DbText, Is.EqualTo("0.0 dB"));

            // The fader is the connection's amp: drag it down.
            rig.Drag(Strip(lead).Fader, 0, 30);
            Assert.That(toMaster.Amp, Is.LessThan(0x4000));
            Assert.That(toMaster.Amp, Is.EqualTo(SessionMixer.AmpOf(Strip(lead).Fader.Value)).Within(1));
            Assert.That(Strip(lead).DbText, Does.StartWith("-"));
            rig.DoubleClick(Strip(lead).Fader);
            Assert.That(toMaster.Amp, Is.EqualTo(0x4000), "double-click: 0 dB");

            // Pan, and a change in the Machine View shows on the strip.
            rig.Drag(Strip(lead).PanBar, 20, 0);
            Assert.That(toMaster.Pan, Is.GreaterThan(0x4000));
            view.RefreshState();            // the strip's text follows on the view's 50 ms timer
            Assert.That(Strip(lead).PanBar.ValueText, Does.EndWith("R"));
            toMaster.Amp = 0x2000;
            view.RefreshState();
            Assert.That(Strip(lead).DbText, Is.EqualTo("-6.0 dB"));

            // A send raised from off connects the track to the return.
            Assert.That(SessionMixer.GetSend(lead, verb), Is.EqualTo(0));
            rig.Drag(Strip(lead).Sends.Single(), 40, 0);
            int send = SessionMixer.GetSend(lead, verb);
            Assert.That(send, Is.GreaterThan(0), "a connection Lead → Verb");
            view.RefreshState();
            Assert.That(Strip(lead).Sends.Single().ValueText, Is.EqualTo(SessionMixer.FormatAmp(send)));
            rig.Drag(Strip(drums).Sends.Single(), 25, 0);
            Assert.That(SessionMixer.GetSend(drums, verb), Is.GreaterThan(0));

            // Mute, and crossfader side A.
            rig.Click(Strip(lead).Mute);
            Assert.That(lead.IsMuted, Is.True);
            rig.Click(Strip(lead).Mute);
            Assert.That(lead.IsMuted, Is.False);
            rig.Click(Strip(lead).SideA);
            Assert.That(layout.CrossfadeOf(lead), Is.EqualTo("A"));

            // The master fader is the master volume.
            double volume = rig.Core.MasterVolume;
            rig.Drag(view.MixerStrips.Single(s => s.Kind == SessionView.StripKind.Master).Fader, 0, 20);
            Assert.That(rig.Core.MasterVolume, Is.LessThan(volume));

            // Meters: the strips' post-fader peaks while the scene plays.
            Assert.That(view.IsMetering, Is.True, "the mixer is on screen");
            rig.Click(view.Scenes[0]);
            rig.RunTicks(6);
            view.RefreshState();
            view.RefreshMeters();
            Assert.That(Strip(drums).Meter.Left, Is.GreaterThan(0), "Drums' meter shows its level");
            Assert.That(Strip(lead).Meter.Left, Is.GreaterThan(0));
            rig.Screenshot("session-mixer");

            // The sends' connections are Machine View undo steps (the song's history).
            rig.Core.SongCore.ActionStack.Undo();
            rig.Core.SongCore.ActionStack.Undo();
            rig.Settle();
            view.RefreshState();
            Assert.That(SessionMixer.GetSend(lead, verb), Is.EqualTo(0), "undo takes the send's connection away");
            Assert.That(Strip(lead).Sends.Single().ValueText, Is.EqualTo("off"));
            rig.Click(view.MixerButton);
            Assert.That(layout.Document.ShowMixer, Is.False, "Mixer hides it");
            Assert.That(view.IsMetering, Is.False);
        }

        [AvaloniaTest]
        public void GroupsGetAColumnThatLaunchesTheirClipsAndFolds()
        {
            // As ReBuzzTests' MixRig: Drums and Bass through a bus in the group Rhythm, Lead to the master.
            using var rig = new SessionRig();
            var bus = rig.Add("Neo Utility", "Bus", track: false);
            var drums = rig.Add("Neo Poly", "Drums", to: bus);
            var bass = rig.Add("Neo Poly", "Bass", to: bus);
            var lead = rig.Add("Neo Poly", "Lead");
            var phones = rig.Add("Neo EQ", "Phones", track: false, connect: false);
            var song = rig.Core.SongCore;
            song.CreateMachineGroup("Rhythm", 0, 0);
            var group = song.MachineGroups.Single();
            foreach (var m in new IMachine[] { drums, bass, bus })
                song.AddMachineToGroup(m, group);
            group.MainOutputMachine = bus;
            var beat = rig.Pattern(drums, "Beat", Bar, (0, 36));
            var line = rig.Pattern(bass, "Line", Bar, (0, 40));
            var hook = rig.Pattern(lead, "Hook", Bar, (0, 72));
            var layout = rig.Layout;
            layout.Ensure();
            var view = rig.Show(1100, 680);

            Assert.That(view.DescribeColumns(), Is.EqualTo("[Rhythm] Drums Bass Lead"));
            Assert.That(view.MixerStrips.Single(s => s.Kind == SessionView.StripKind.Group).Machine, Is.SameAs(bus), "the group's fader is its output machine's");

            rig.Click(view.GroupSlots.Single(c => c.Scene == 0));         // a group slot launches its members' clips
            rig.RunTicks(2);
            Assert.That(StateOf(rig, 1, line), Is.EqualTo(ClipState.Playing));
            Assert.That(StateOf(rig, 0, beat), Is.EqualTo(ClipState.Playing));
            Assert.That(rig.Session.GetPlaying(layout.SequenceOf(2)), Is.Null, "Lead is not in the group");

            // Cue: ⚙ › Cue output, then a strip's C connects its machine to it.
            rig.Click(view.SettingsButton);
            rig.Menu("Cue output/Phones");
            var leadStrip = view.MixerStrips.Single(s => s.Kind == SessionView.StripKind.Track && s.Machine == lead);
            rig.Click(leadStrip.Cue);
            Assert.That(SessionMixer.IsCued(lead, phones), Is.True);
            rig.Screenshot("session-groups");

            // The group's header folds it; the keyboard skips folded tracks.
            rig.Click(view.FindGroupHeader("Rhythm"));
            Assert.That(layout.Document.CollapsedGroups, Does.Contain("Rhythm"));
            Assert.That(view.DescribeColumns(), Is.EqualTo("[Rhythm ▸] Lead"));
            rig.Key(PhysicalKey.ArrowDown);
            rig.Key(PhysicalKey.Enter);
            Assert.That(StateOf(rig, 2, hook), Is.EqualTo(ClipState.Queued), "the first shown track is Lead");
        }

        // ------------------------------------------------------------------ undo, and keys in the main window

        [AvaloniaTest]
        public void UndoInTheViewTakesBackGridAndClipEdits()
        {
            using var rig = new SessionRig("Drums", "Bass");
            var beat = rig.Pattern(rig.Machine("Drums"), "Beat", Bar);
            var line = rig.Pattern(rig.Machine("Bass"), "Line", Bar);
            var layout = rig.Layout;
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            Quiet(window);
            window.Show();
            try
            {
                var tab = window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Session");
                window.Tabs.SelectedItem = tab;
                rig.Settle();
                var view = (SessionView)tab.Content;
                window.UpdateLayout();
                int songSteps = rig.Core.SongCore.ActionStack.Actions.Count();

                rig.Click(view.SlotAt(1, 0), fx: 0.6);                   // selects (not ▶)
                Assert.That(view.Selected, Is.EqualTo((1, 0)));
                rig.Key(PhysicalKey.D, RawInputModifiers.Control, window);
                Assert.That(layout.PatternAt(1, 1)?.Name, Is.EqualTo("Line 2"), "Ctrl+D duplicates into the slot below");
                rig.Key(PhysicalKey.Z, RawInputModifiers.Control, window);
                Assert.That(layout.PatternAt(1, 1), Is.Null, "Ctrl+Z in the view: the session's undo");
                Assert.That(rig.Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(songSteps), "not the song's (the main window's Edit › Undo)");
                rig.Key(PhysicalKey.Y, RawInputModifiers.Control, window);
                Assert.That(layout.PatternAt(1, 1)?.Name, Is.EqualTo("Line 2"), "Ctrl+Y redoes");

                // A clip panel edit, undone and redone with ↶ ↷.
                rig.Click(view.SlotAt(0, 0), fx: 0.6);
                Assert.That(view.ClipPanelView.Pattern, Is.SameAs(beat));
                rig.Choose(view.ClipPanelView.ModeBox, 1);            // Gate
                Assert.That(layout.ClipOf(beat).Mode, Is.EqualTo("Gate"));
                rig.Click(view.UndoButton);
                Assert.That(layout.ClipOf(beat).Mode, Is.EqualTo("Trigger"));
                rig.Click(view.RedoButton);
                Assert.That(layout.ClipOf(beat).Mode, Is.EqualTo("Gate"));

                // Moving a clip by dragging it down its track (Ctrl copies), then undoing.
                rig.Drag(view.SlotAt(1, 0), 0, 2 * 28, fx: 0.6);
                Assert.That(layout.PatternAt(1, 2), Is.SameAs(line), "dragged two scenes down");
                Assert.That(layout.PatternAt(1, 0), Is.Null);
                rig.Key(PhysicalKey.Z, RawInputModifiers.Control, window);
                Assert.That(layout.PatternAt(1, 0), Is.SameAs(line));
                Assert.That(layout.PatternAt(1, 2), Is.Null);
                Studio.Screenshot(window, "session-undo");
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ keys (SessionPlayableTests.KeyboardDrivesTheGrid)

        [AvaloniaTest]
        public void TheKeyboardDrivesTheGrid()
        {
            using var rig = new SessionRig("Drums", "Bass");
            var beat = rig.Pattern(rig.Machine("Drums"), "Beat", Bar);
            var line = rig.Pattern(rig.Machine("Bass"), "Line", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            var view = rig.Show();

            rig.Key(PhysicalKey.ArrowDown);                              // selects (0,0)
            Assert.That(view.Selected, Is.EqualTo((0, 0)));
            rig.Key(PhysicalKey.Enter);
            Assert.That(StateOf(rig, 0, beat), Is.EqualTo(ClipState.Queued));

            rig.Key(PhysicalKey.ArrowRight);                             // (1,0): Line
            rig.Key(PhysicalKey.D, RawInputModifiers.Control);           // duplicate below
            Assert.That(layout.PatternAt(1, 1)?.Name, Is.EqualTo("Line 2"));
            rig.Key(PhysicalKey.Z, RawInputModifiers.Control);
            Assert.That(layout.PatternAt(1, 1), Is.Null);

            // The selection followed the copy to (1,1), now empty again.
            rig.Key(PhysicalKey.M, RawInputModifiers.Control | RawInputModifiers.Shift);   // create clip
            Assert.That(layout.PatternAt(1, 1), Is.Not.Null);
            rig.Key(PhysicalKey.Delete);
            Assert.That(layout.PatternAt(1, 1), Is.Null);

            rig.Key(PhysicalKey.Digit1);                                  // scene 1
            Assert.That(StateOf(rig, 1, line), Is.EqualTo(ClipState.Queued));

            int scenes = layout.SceneCount;
            rig.Key(PhysicalKey.I, RawInputModifiers.Control);
            Assert.That(layout.SceneCount, Is.EqualTo(scenes + 1));

            bool playing = rig.Core.PlayRequested;
            rig.Key(PhysicalKey.Space);
            Assert.That(rig.Core.PlayRequested, Is.EqualTo(!playing));
            rig.Key(PhysicalKey.Space);
            Assert.That(rig.Core.PlayRequested, Is.EqualTo(playing));

            rig.Key(PhysicalKey.M, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.That(layout.Document.ShowMixer, Is.False, "Ctrl+Alt+M hides the mixer");
            rig.Key(PhysicalKey.M, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.That(layout.Document.ShowMixer, Is.True);

            // F2 renames the clip (the prompt answered here).
            rig.Key(PhysicalKey.ArrowUp);                                // (1,0): Line
            Dialogs.TextResponder = (title, current) => "Bassline";
            rig.Key(PhysicalKey.F2);
            Assert.That(line.Name, Is.EqualTo("Bassline"));
        }

        // ------------------------------------------------------------------ the clip panel (SessionClipTests, SessionPerformanceTests)

        [AvaloniaTest]
        public void TheClipPanelEditsClipAndSceneProperties()
        {
            using var rig = new SessionRig("Drums", "Bass");
            var beat = rig.Pattern(rig.Machine("Drums"), "Beat", Bar, (0, 36));
            rig.Pattern(rig.Machine("Drums"), "Fill", Bar);
            rig.Pattern(rig.Machine("Bass"), "Line", 2 * Bar);
            var layout = rig.Layout;
            layout.Ensure();
            var view = rig.Show(1180, 760);
            var panel = view.ClipPanelView;
            Assert.That(panel.IsVisible, Is.True, "shown by default");

            rig.Click(view.SlotAt(0, 0), fx: 0.6);
            Assert.That(panel.TitleText, Is.EqualTo("Beat"));
            Assert.That(panel.InfoText, Does.Contain("1 bar (16 ticks)"));

            rig.Choose(panel.ModeBox, 1);                              // Gate
            rig.Choose(panel.QuantizeBox, 4);                          // 1 beat
            rig.Click(panel.LegatoBox);
            Assert.That(layout.ClipOf(beat).Mode, Is.EqualTo("Gate"));
            Assert.That(layout.ClipOf(beat).Quantization, Is.EqualTo(nameof(LaunchQuantization.Beat)));
            Assert.That(layout.ClipOf(beat).Legato, Is.True);

            void Type(TextBox box, string text)
            {
                box.Focus();
                box.Text = text;
                rig.Key(PhysicalKey.Enter);
            }
            Type(panel.StartBox, "4");
            Type(panel.LoopEndBox, "12");
            Assert.That(layout.ClipOf(beat).Start, Is.EqualTo(4));
            Assert.That(layout.ClipOf(beat).LoopEnd, Is.EqualTo(12));
            Assert.That(panel.RegionText, Is.EqualTo("Plays from 4, loops 0–12 of 16 ticks."));

            rig.Drag(panel.VelocityBar, 90, 0);                          // committed when the drag ends
            Assert.That(layout.ClipOf(beat).Velocity, Is.GreaterThan(0));

            // Follow action: on, Next, after the end.
            rig.Click(panel.ClipFollow.OnBox);
            Assert.That(layout.ClipOf(beat).Follow.On, Is.True);
            rig.Choose(panel.ClipFollow.BBox, 8);                      // Other
            Assert.That(layout.ClipOf(beat).Follow.B, Is.EqualTo("Other"));

            // The scene row's tempo and follow action.
            Type(panel.SceneTempoBox, "140");
            Assert.That(layout.Document.Scenes[0].Tempo, Is.EqualTo(140));
            rig.Click(panel.SceneFollow.OnBox);
            Assert.That(layout.Document.Scenes[0].Follow.On, Is.True);

            Assert.That(SessionView.SlotCell.Flags(layout.ClipOf(beat)), Is.EqualTo("G L ◆ v q →"), "the slot shows the clip's settings in short");
            rig.RunTicks(0);
            rig.Screenshot("session-clip-panel");

            // Launching a Gate clip: the press launches, the release stops it.
            rig.Click(view.SlotAt(0, 0).LaunchArea);
            rig.RunTicks(2);
            Assert.That(rig.Session.GetPlaying(layout.SequenceOf(0)), Is.Null.Or.SameAs(beat));
            rig.Click(view.SlotAt(1, 3), fx: 0.6);                        // an empty slot
            Assert.That(panel.TitleText, Is.EqualTo("Clip"));
        }

        // ------------------------------------------------------------------ follow actions and tempo (SessionPerformanceTests)

        [AvaloniaTest]
        public void FollowActionsAndTheTempoControls()
        {
            using var rig = new SessionRig("Drums");
            var a = rig.Pattern(rig.Machine("Drums"), "A", 4, (0, 36));
            var b = rig.Pattern(rig.Machine("Drums"), "B", 4, (0, 38));
            var layout = rig.Layout;
            layout.Ensure();
            var view = rig.Show();
            var panel = view.ClipPanelView;

            // Launch quantization and beats per bar from the top bar.
            rig.Choose(view.QuantizeBox, 3);                           // 1 beat
            Assert.That(rig.Session.LaunchQuantization, Is.EqualTo(LaunchQuantization.Beat));
            rig.Choose(view.BeatsBox, 2);                              // 3
            Assert.That(rig.Session.BeatsPerBar, Is.EqualTo(3));
            rig.Choose(view.BeatsBox, 3);

            // A follows on to B at its end (Next, linked).
            rig.Click(view.SlotAt(0, 0), fx: 0.6);
            rig.Click(panel.ClipFollow.OnBox);
            Assert.That(layout.ClipOf(a).Follow.On, Is.True);
            Assert.That(view.FollowButton.Lit, Is.Not.Null, "follow actions are on for the song");
            rig.Click(view.SlotAt(0, 0).LaunchArea);
            rig.RunTicks(2);
            Assert.That(StateOf(rig, 0, a), Is.EqualTo(ClipState.Playing));
            rig.RunTicks(4);
            Assert.That(StateOf(rig, 0, b), Is.EqualTo(ClipState.Playing), "A's follow action launched B");
            view.RefreshState();
            Assert.That(view.Selected, Is.EqualTo((0, 0)));

            // Follow off for the song.
            rig.Click(view.FollowButton);
            Assert.That(layout.Document.FollowActionsEnabled, Is.False);
            Assert.That(view.FollowButton.Lit, Is.Null);

            // Undo the last launch: A comes back.
            rig.Click(view.SlotAt(0, 0).LaunchArea);
            rig.RunTicks(4);                                              // on the next beat
            Assert.That(StateOf(rig, 0, a), Is.EqualTo(ClipState.Playing));
            view.RefreshState();
            rig.Click(view.UndoLaunchButton);
            rig.RunTicks(4);
            Assert.That(StateOf(rig, 0, b), Is.EqualTo(ClipState.Playing), "↶ Launch gave the track back B");

            // Nudge while held; the tempo shown.
            var nudge = view.NudgeUpButton;
            var top = TopLevel.GetTopLevel(nudge)!;
            var p = rig.PointIn(nudge);
            top.MouseDown(p, MouseButton.Left);
            Assert.That(rig.Core.TempoNudge, Is.GreaterThan(1f), "faster while held");
            top.MouseUp(p, MouseButton.Left);
            Assert.That(rig.Core.TempoNudge, Is.EqualTo(1f));
            view.RefreshState();
            Assert.That(view.TempoText, Is.EqualTo($"{rig.Core.BPM} BPM"));
            rig.Screenshot("session-performance");
        }

        // ------------------------------------------------------------------ menus

        [AvaloniaTest]
        public void SlotAndSceneMenusEditTheGrid()
        {
            using var rig = new SessionRig("Drums");
            var beat = rig.Pattern(rig.Machine("Drums"), "Beat", Bar);
            var fill = rig.Pattern(rig.Machine("Drums"), "Fill", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            var view = rig.Show();

            rig.Click(view.SlotAt(0, 0), MouseButton.Right, fx: 0.6);
            rig.Menu("Colour/Red");
            Assert.That(PatternColors.Get(beat), Is.EqualTo(9));
            rig.Click(view.SlotAt(0, 2), MouseButton.Right, fx: 0.6);
            rig.Menu("Assign pattern/Fill");
            Assert.That(layout.PatternAt(0, 2), Is.SameAs(fill));

            Dialogs.TextResponder = (title, current) => title == "Scene tempo" ? "128" : "Chorus";
            rig.Click(view.Scenes[1], MouseButton.Right);
            rig.Menu("Rename…");
            rig.Click(view.Scenes[1], MouseButton.Right);
            rig.Menu("Tempo…");
            Assert.That(layout.Document.Scenes[1].Name, Is.EqualTo("Chorus"));
            Assert.That(layout.Document.Scenes[1].Tempo, Is.EqualTo(128));

            // Insert into arrangement: the scene's clips written into the sequences.
            ArrangementInsertDialog.Responder = (scene, bar, bars) => new ArrangementInsertDialog.Answer(1, 2, false);
            rig.Click(view.Scenes[0], MouseButton.Right);
            rig.Menu("Insert into arrangement…");
            Assert.That(layout.SequenceOf(0).Events.Values.Select(e => e.Pattern), Does.Contain(beat), "the arrangement plays Beat");

            int scenes = layout.SceneCount;
            rig.Click(view.Scenes[0], MouseButton.Right);
            rig.Menu("Duplicate scene");
            Assert.That(layout.SceneCount, Is.EqualTo(scenes + 1));
            Assert.That(layout.PatternAt(0, 1), Is.SameAs(beat), "the copy is below");
            // MIDI learn from a slot's menu: the status line says what it waits for, Esc cancels.
            rig.Click(view.SlotAt(0, 1), MouseButton.Right, fx: 0.6);
            rig.Menu("MIDI learn…");
            view.RefreshState();
            Assert.That(view.StatusLine, Does.StartWith("MIDI learn: press a pad, key or knob for track 1, scene 2"));
            rig.Screenshot("session-menus");
            rig.Key(PhysicalKey.Escape);
            Assert.That(rig.Core.SessionMidi.IsLearning, Is.False);
            view.RefreshState();
            Assert.That(view.StatusLine, Is.Empty);
        }
    }
}
