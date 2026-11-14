// Fork (docs/specs/daw-modes.md AR2): the piano roll everywhere, headless. A clip opened in the Arrangement mode shows its notes in a
// roll under the timeline, drawn with the mouse; the Pattern tab's roll shows and edits the same notes; the edits are steps of the
// song's history; velocity and controller lanes, key and scale, fold, step input and the ghosts of other clips; and the exit test:
// a bass line drawn in the arrangement's roll plays in its session clip, as the same notes written to a clip by the session do.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Detail;
using ReBuzz.Studio.Views.Sequence;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Tests
{
    public class PianoRollEverywhereTests
    {
        const int TB = ClipNotes.TimeBase;

        static ReBuzzCore Core => Studio.Host.Core;

        static RawInputModifiers Cmd => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        static void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        sealed class Rig : IDisposable
        {
            public MainWindow Window { get; }
            public SequenceView View { get; }
            public ArrangementRollPanel Panel => View.RollPanel;
            public TrackerRoll Roll => Panel.Roll;
            public PianoRoll Notes => Roll.Roll;
            readonly bool looping;

            public Rig(params (string Gear, string Name)[] machines)
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                looping = Core.Looping;
                Core.ExecuteCommand(BuzzCommand.Stop);
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
                foreach (var (gear, name) in machines) Machine(gear, name);
                Window = new MainWindow(Studio.Host) { Width = 1280, Height = 900 };
                ToolTip.SetServiceEnabled(Window, false);
                Window.Show();
                var tab = Window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Arrangement");
                tab.IsSelected = true;
                Settle();
                View = (SequenceView)tab.Content!;
                Click(Of(View.ArrangementModeButton));
                Assert.That(View.IsArrangementMode, Is.True);
                Settle();
            }

            public static MachineCore Machine(string gear, string name)
            {
                var song = Core.SongCore;
                song.CreateMachine(gear, null, name, null, null, null, -1, -0.3f + 0.2f * song.MachinesList.Count, 0.3f);
                var machine = song.MachinesList.Last(m => m.Name == name);
                var master = song.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
                song.ConnectMachines(machine, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                song.AddSequence(machine, song.SequencesList.Count);
                if (machine.ParameterGroupsList[1].ParametersList.FirstOrDefault(q => q.Name == "Drift") is { } drift) drift.SetValue(0, 0);      // random per voice: off, so renders compare
                return machine;
            }

            public ISequence Lane(string machine, int index = 0) => Core.Song.Sequences.Where(s => s.Machine.Name == machine).ElementAt(index);

            public Point Of(Avalonia.Controls.Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), Window)!.Value;

            public Point At(ISequence lane, int tick, double dx = 0, double fy = 0.55)
            {
                Settle();
                var row = View.Arrangement.RowOf(lane);
                Assert.That(row, Is.Not.Null);
                var p = View.Arrangement.TranslatePoint(new Point(View.Arrangement.Geometry.XOf(tick) + dx, View.Arrangement.YOf(row) + fy * row.Height), Window);
                Assert.That(p, Is.Not.Null);
                return p!.Value;
            }

            public void Click(Point p, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None)
            {
                Window.MouseMove(p, mods);
                Window.MouseDown(p, button, mods);
                Window.MouseUp(p, button, mods);
                Settle();
            }

            public void DoubleClick(Point p)
            {
                MachineViewTests.DoubleClick(Window, p);
                Settle();
            }

            public void Drag(Point from, Point to)
            {
                Window.MouseMove(from);
                Window.MouseDown(from, MouseButton.Left);
                for (int i = 1; i <= 4; i++)
                    Window.MouseMove(from + (to - from) * (i / 4.0), RawInputModifiers.LeftMouseButton);
                Window.MouseUp(to, MouseButton.Left);
                Settle();
            }

            public void Key(PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None)
            {
                Window.KeyPressQwerty(key, mods);
                Window.KeyReleaseQwerty(key, mods);
                Settle();
            }

            /// <summary>A clip drawn on a lane at a tick (a double click on empty space), then opened (a double click on it): the roll shows its pattern.</summary>
            public IPattern DrawAndOpen(ISequence lane, int tick)
            {
                DoubleClick(At(lane, tick, 2));
                var clip = View.Arrangement.ItemsOf(lane).First(i => i.Time == tick);
                DoubleClick(At(lane, tick, 6));
                Assert.That(Panel.IsVisible, Is.True, "a double click on a clip opens its piano roll");
                Assert.That(Panel.Pattern, Is.SameAs(clip.Pattern));
                Settle();
                return clip.Pattern;
            }

            /// <summary>The point of a cell of the arrangement's roll in window pixels, with the roll scrolled so that it shows.</summary>
            public Point Cell(int time, int note)
            {
                var r = Notes;
                Roll.Scroll.Offset = new Vector(Roll.Scroll.Offset.X, Math.Max(0, r.Y(note) - 40));
                Settle();
                var p = r.TranslatePoint(r.PointOf(time, note), Window);
                Assert.That(p, Is.Not.Null);
                return p!.Value;
            }

            public void DrawNote(int tick, int note) => DoubleClick(Cell(tick * TB, note));

            public void Dispose()
            {
                Core.ExecuteCommand(BuzzCommand.Stop);
                Window.Close();
                Settle();
                Dialogs.Responder = null;
                Core.Looping = looping;
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
            }
        }

        static ClipNote N(int tick, int note, int ticks = 1, int velocity = 100) => new ClipNote(tick * TB, ticks * TB, note, velocity);

        static int Undos => Core.SongCore.ActionStack.Actions.Count();

        static int Listeners(INoteSource source)
        {
            for (var t = source.GetType(); t != null; t = t.BaseType)
                if (t.GetField("Changed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)?.GetValue(source) is Delegate d)
                    return d.GetInvocationList().Length;
            return 0;
        }

        // ------------------------------------------------------------------ rendering

        static void ResetMachines()
        {
            foreach (var m in Core.SongCore.MachinesList)
            {
                Core.MachineManager.Stop(m);
                var target = m.ManagedMachine;
                target?.GetType().GetMethod("ResetForRender", Type.EmptyTypes)?.Invoke(target, null);
            }
            var sub = ReBuzzCore.subTickInfo;
            sub.PosInSubTick = 0; sub.CurrentSubTick = 0; sub.SubTickReminderCounter = 0;
            Core.Looping = false;
            Core.SongCore.PlayPosition = 0;
            ReBuzzCore.masterInfo.PosInTick = 0;
        }

        /// <summary>Plays a pattern as a session clip from the start and returns the audio.</summary>
        static float[] RenderClip(ISequence lane, IPattern pattern, int ticks)
        {
            Core.SessionCore.Stop(lane, immediate: true);
            ResetMachines();
            int blocks = (int)Math.Ceiling(ticks * (double)ReBuzzCore.masterInfo.SamplesPerTick / 256) + 200;
            var work = new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
            var all = new float[blocks * 512];
            var buffer = new float[512];
            Core.Playing = true;
            Core.Session.Launch(lane, pattern);
            lock (ReBuzzCore.AudioLock)
            {
                for (int i = 0; i < blocks; i++)
                {
                    work.MainAudioFillBuffer(buffer, 0, 512);
                    Array.Copy(buffer, 0, all, i * 512, 512);
                }
            }
            Core.Playing = false;
            Dispatcher.UIThread.RunJobs();
            Core.SessionCore.Stop(lane, immediate: true);
            return all;
        }

        static float Peak(float[] audio) => audio.Length == 0 ? 0 : audio.Max(Math.Abs);

        // ------------------------------------------------------------------ the exit test

        [AvaloniaTest]
        public void ABassLineDrawnInTheArrangementsPianoRollPlaysInItsSessionClip()
        {
            using var rig = new Rig(("Neo Poly", "Bass"));
            var lane = rig.Lane("Bass");
            var pattern = rig.DrawAndOpen(lane, 0);
            var machine = (MachineCore)pattern.Machine;
            Assert.That(rig.Roll.IsSongLane, Is.True, "the arrangement's roll is on the song's notes");
            Assert.That(rig.Roll.Message, Is.Null);
            int undos = Undos;

            // A bass line: C2 C2 G2 F2 on four beats, drawn by double clicks on the roll.
            var line = new[] { (0, 36), (2, 36), (4, 43), (6, 41) };
            foreach (var (tick, note) in line) rig.DrawNote(tick, note);
            Assert.That(rig.Notes.Notes.Select(n => (n.Time / TB, n.Note)), Is.EqualTo(line), "four notes");
            Assert.That(Undos, Is.EqualTo(undos + 4), "each on the song's history");

            // They are the pattern's MIDI column: what the session clip (and the sequence) plays.
            var session = Core.SessionLayout.NotesOf(pattern);
            Assert.That(session, Is.EqualTo(rig.Notes.Notes.ToList()), "the Session View's clip has the notes");
            Assert.That(new MidiColumnNotes(Core, pattern).Notes(), Is.EqualTo(session));

            // The same four notes written to another clip by the session itself.
            machine.CreatePattern("Reference", pattern.Length);
            var reference = machine.PatternsList.Single(p => p.Name == "Reference");
            Core.SessionLayout.EditNotes(reference, null, session.ToArray());
            machine.CreatePattern("Other", pattern.Length);
            var other = machine.PatternsList.Single(p => p.Name == "Other");
            Core.SessionLayout.EditNotes(other, null, new[] { N(0, 48), N(2, 50) });

            var drawn = RenderClip(lane, pattern, 48);
            var written = RenderClip(lane, reference, 48);
            var different = RenderClip(lane, other, 48);
            Assert.That(Peak(drawn), Is.GreaterThan(0.001f), "the clip sounds");
            Assert.That(drawn, Is.EqualTo(written), "it plays as the same notes written by the session do");
            Assert.That(drawn, Is.Not.EqualTo(different), "and other notes sound otherwise");
            rig.Notes.Focus();
            Studio.Screenshot(rig.Window, "ar2-bass-line-drawn");
        }

        [AvaloniaTest]
        public void AMachineThatTakesNotesInItsTracksGetsThemInItsNoteColumns()
        {
            using var rig = new Rig(("Keys Test", "Acid"));
            var lane = rig.Lane("Acid");
            var pattern = rig.DrawAndOpen(lane, 0);
            Assert.That(rig.Roll.IsSongLane, Is.True);
            Assert.That(rig.Notes.Source, Is.InstanceOf<TrackerColumnNotes>(), "a machine that takes notes in its Note track parameter only");
            rig.DrawNote(0, 36);
            rig.DrawNote(4, 43);
            Assert.That(rig.Notes.Notes.Select(n => n.Note), Is.EqualTo(new[] { 36, 43 }));
            var column = pattern.Columns.First(c => c.Parameter?.Type == ParameterType.Note && c.Track == 0);
            Assert.That(column.GetEvents(0, int.MaxValue).Select(e => e.Value).Where(v => v != BuzzNote.Off), Is.EqualTo(new[] { BuzzNote.FromMIDINote(36), BuzzNote.FromMIDINote(43) }), "Buzz notes on track 0");
            Assert.That(new MidiColumnNotes(Core, pattern).Notes(), Is.Empty, "nothing in the MIDI column, which this machine would not play");
            Assert.That(rig.Roll.LowerBox.Items.Count, Is.EqualTo(1), "no controllers for a machine that takes no MIDI");
            var audio = RenderClip(lane, pattern, 48);
            Assert.That(Peak(audio), Is.GreaterThan(0.001f), "and it sounds");
        }

        // ------------------------------------------------------------------ the Pattern tab

        [AvaloniaTest]
        public void ThePatternTabsRollEditsTheSameNotesAndUndoIsTheSongs()
        {
            using var rig = new Rig(("Neo Poly", "Lead"));
            var lane = rig.Lane("Lead");
            var pattern = rig.DrawAndOpen(lane, 0);
            rig.DrawNote(0, 60);
            rig.DrawNote(2, 64);
            Assert.That(rig.Notes.Notes.Count, Is.EqualTo(2));

            // The Pattern tab's toggle between the Tracker and the roll: it shows the notes the arrangement's roll drew.
            Core.SetPatternEditorPattern(pattern);
            rig.Window.ShowPianoRoll();
            Settle();
            var page = PatternView.Page;
            Assert.That(page, Is.Not.Null);
            page.Edit(pattern);
            Settle();
            Assert.That(page.IsRollMode, Is.True);
            var tabRoll = page.Roll;
            Assert.That(tabRoll.Lane, Is.EqualTo(TrackerLane.SongNotes), "the Tracker holds no notes and the song does");
            Assert.That(tabRoll.Notes().Select(n => n.Note), Is.EqualTo(new[] { 60, 64 }), "the same notes");

            // An edit there is a step of the song's history and shows in the arrangement's roll.
            int undos = Undos;
            tabRoll.Roll.AddNote(4 * TB, 67);
            Assert.That(Undos, Is.EqualTo(undos + 1));
            Assert.That(new MidiColumnNotes(Core, pattern).Notes().Select(n => n.Note), Is.EqualTo(new[] { 60, 64, 67 }));
            rig.Roll.Poll();
            Assert.That(rig.Notes.Notes.Select(n => n.Note), Is.EqualTo(new[] { 60, 64, 67 }), "the arrangement's roll follows");

            // Undo and redo, from the keys, are the song's: the tab's roll and the arrangement's both follow.
            rig.Key(PhysicalKey.Z, Cmd);
            Assert.That(new MidiColumnNotes(Core, pattern).Notes().Select(n => n.Note), Is.EqualTo(new[] { 60, 64 }));
            tabRoll.Poll();
            Assert.That(tabRoll.Notes().Count, Is.EqualTo(2));
            if (OperatingSystem.IsMacOS()) rig.Key(PhysicalKey.Z, Cmd | RawInputModifiers.Shift);
            else rig.Key(PhysicalKey.Y, Cmd);
            Assert.That(new MidiColumnNotes(Core, pattern).Notes().Select(n => n.Note), Is.EqualTo(new[] { 60, 64, 67 }), "redo");
        }

        [AvaloniaTest]
        public void UndoAndRedoOfDrawnNotesAreOnTheSongsHistoryAndTheKeysInTheRollAreTheRollsNotTheClips()
        {
            using var rig = new Rig(("Neo Poly", "Lead"));
            var lane = rig.Lane("Lead");
            var pattern = rig.DrawAndOpen(lane, 0);
            rig.DrawNote(0, 60);
            rig.DrawNote(1, 62);
            Assert.That(rig.Notes.Notes.Count, Is.EqualTo(2));

            // The keys typed in the roll are the roll's, not the clips' under it: S would split a clip, Delete would delete it.
            string clips = string.Join(" ", rig.View.Arrangement.ItemsOf(lane).Select(i => $"{i.Time}-{i.End}"));
            rig.Notes.Focus();
            rig.Key(PhysicalKey.S);
            rig.Key(PhysicalKey.Delete);
            Assert.That(string.Join(" ", rig.View.Arrangement.ItemsOf(lane).Select(i => $"{i.Time}-{i.End}")), Is.EqualTo(clips), "the clips are as they were");
            Assert.That(rig.Notes.Notes.Count, Is.EqualTo(1), "Delete took the selected note (the one just drawn)");
            rig.Key(PhysicalKey.Z, Cmd);
            rig.Roll.Poll();
            Assert.That(rig.Notes.Notes.Count, Is.EqualTo(2), "and undo brings it back");
            rig.Key(PhysicalKey.Z, Cmd);
            rig.Key(PhysicalKey.Z, Cmd);
            rig.Roll.Poll();
            Assert.That(rig.Notes.Notes, Is.Empty);
            if (OperatingSystem.IsMacOS()) rig.Key(PhysicalKey.Z, Cmd | RawInputModifiers.Shift);
            else rig.Key(PhysicalKey.Y, Cmd);
            rig.Roll.Poll();
            Assert.That(rig.Notes.Notes.Select(n => n.Note), Is.EqualTo(new[] { 60 }), "redo brings the first back");
            Assert.That(Core.SessionLayout.NotesOf(pattern).Select(n => n.Note), Is.EqualTo(new[] { 60 }), "and the session's clip has it");
        }

        // ------------------------------------------------------------------ velocity and controller lanes

        [AvaloniaTest]
        public void TheVelocityLaneSetsAVelocityAndAControllerLaneDrawsAndEditsACcOnTheSongsHistory()
        {
            using var rig = new Rig(("Neo Poly", "Lead"));
            var pattern = rig.DrawAndOpen(rig.Lane("Lead"), 0);
            rig.DrawNote(0, 60);
            var velocity = rig.Roll.Velocities;
            Assert.That(velocity, Is.Not.Null);
            var note = rig.Notes.Notes[0];
            int undos = Undos;
            rig.Drag(velocity.TranslatePoint(velocity.BarPoint(note), rig.Window)!.Value, velocity.TranslatePoint(new Point(velocity.BarPoint(note).X, velocity.YOfVelocity(40)), rig.Window)!.Value);
            Assert.That(rig.Notes.Notes[0].Velocity, Is.InRange(38, 42), "the velocity lane's drag");
            Assert.That(Undos, Is.EqualTo(undos + 1));

            // Controller lanes: the mod wheel, offered always for a machine that takes MIDI.
            var items = rig.Roll.LowerBox.Items.Cast<string>().ToList();
            Assert.That(items, Does.Contain(ControllerPoint.NameOf(1)));
            Assert.That(items, Does.Contain("Pitch bend"));
            rig.Roll.LowerBox.SelectedIndex = items.IndexOf(ControllerPoint.NameOf(1));
            Settle();
            var lane = rig.Roll.Controller;
            Assert.That(lane, Is.Not.Null, "the lane replaces the velocity lane");
            Assert.That(velocity.IsVisible, Is.False);
            var store = new MidiControllers(Core, pattern);
            undos = Undos;
            rig.Click(lane.TranslatePoint(lane.At(2 * TB, 100), rig.Window)!.Value);
            Assert.That(store.Points(), Is.EqualTo(new[] { new ControllerPoint(2 * TB, 1, 100) }), "a click adds a point");
            Assert.That(Undos, Is.EqualTo(undos + 1));
            Assert.That(new MidiColumnNotes(Core, pattern).Notes().Count, Is.EqualTo(1), "the notes are not touched");
            var point = lane.Points[0];
            rig.Drag(lane.TranslatePoint(lane.PointOf(point), rig.Window)!.Value, lane.TranslatePoint(new Point(lane.PointOf(point).X, lane.PointOf(point with { Value = 30 }).Y), rig.Window)!.Value);
            Assert.That(store.Points().Single().Value, Is.InRange(28, 32), "a drag moves its value");
            Assert.That(store.Points().Single().Time, Is.EqualTo(2 * TB));
            rig.Key(PhysicalKey.Z, Cmd);
            Assert.That(store.Points().Single().Value, Is.EqualTo(100), "undone");
            rig.Roll.Poll();
            rig.DoubleClick(lane.TranslatePoint(lane.PointOf(store.Points().Single()), rig.Window)!.Value);
            Assert.That(store.Points(), Is.Empty, "a double click deletes it");

            // The pitch bend: 14 bits, centred at 8192.
            rig.Roll.LowerBox.SelectedIndex = items.IndexOf("Pitch bend");
            Settle();
            var bend = rig.Roll.Controller;
            Assert.That(bend.Controller, Is.EqualTo(ControllerPoint.PitchBend));
            Assert.That(store.Edit(null, new[] { new ControllerPoint(0, ControllerPoint.PitchBend, 12000) }), Is.Null);
            Assert.That(store.Points().Single().Value, Is.EqualTo(12000));
            Assert.That(new MidiColumnNotes(Core, pattern).Notes().Count, Is.EqualTo(1));
            rig.Roll.Poll();
            Assert.That(bend.Points.Single().Value, Is.EqualTo(12000), "the lane follows the column");
        }

        [Test]
        public void ControllersAreMidiEventsAndPitchBendHoldsFourteenBits()
        {
            foreach (var p in new[] { new ControllerPoint(10, 7, 99, 2), new ControllerPoint(0, 74, 0), new ControllerPoint(5, ControllerPoint.PitchBend, 16383, 1), new ControllerPoint(5, ControllerPoint.PitchBend, 8192) })
            {
                Assert.That(ControllerPoint.TryFrom(p.ToEvent(), out var back), Is.True);
                Assert.That(back, Is.EqualTo(p));
            }
            Assert.That(ControllerPoint.TryFrom(new ClipNote(0, TB, 60, 100).ToEvent(), out _), Is.False, "a note-on is no controller");
            Assert.That(ClipNote.TryFrom(new ControllerPoint(0, 1, 64).ToEvent(), out _), Is.False, "and a controller is no note");
        }

        // ------------------------------------------------------------------ scale and fold

        [Test]
        public void AScaleSnapsAndFoldsToTheRowsItKeeps()
        {
            var cMajor = new NoteScale(0, ReBuzz.Agent.Model.Scale.Major);
            Assert.That(Enumerable.Range(60, 12).Where(cMajor.Contains), Is.EqualTo(new[] { 60, 62, 64, 65, 67, 69, 71 }));
            Assert.That(cMajor.IsRoot(72) && !cMajor.IsRoot(71), Is.True);
            Assert.That(new[] { 61, 63, 66 }.Select(cMajor.Snap), Is.EqualTo(new[] { 60, 62, 65 }), "the lower of two equally near");
            Assert.That(cMajor.Snap(64), Is.EqualTo(64));
            var aMinor = new NoteScale(9, ReBuzz.Agent.Model.Scale.Minor);
            Assert.That(aMinor.Contains(60) && aMinor.Contains(69) && !aMinor.Contains(61), Is.True, "A minor has C and A");
            Assert.That(NoteScale.Rows(FoldMode.Used, new[] { 48, 60, 48, 55 }, null), Is.EqualTo(new[] { 60, 55, 48 }), "highest first");
            Assert.That(NoteScale.Rows(FoldMode.Used, Array.Empty<int>(), null), Is.Null, "nothing used: all rows");
            var scaleRows = NoteScale.Rows(FoldMode.Scale, new[] { 61 }, cMajor);
            Assert.That(scaleRows, Does.Contain(61).And.Contain(60).And.Not.Contain(63), "the scale's pitches and those used");
            Assert.That(scaleRows, Is.Ordered.Descending);
            Assert.That(NoteScale.Rows(FoldMode.Off, new[] { 60 }, cMajor), Is.Null);
        }

        [AvaloniaTest]
        public void TheRollHighlightsFoldsAndSnapsToAScale()
        {
            using var rig = new Rig(("Neo Poly", "Lead"));
            rig.DrawAndOpen(rig.Lane("Lead"), 0);
            var roll = rig.Roll;
            Core.SessionLayout.EditNotes(rig.Notes.Pattern, null, new[] { N(0, 48), N(2, 55), N(4, 61) });
            roll.Poll();
            Assert.That(rig.Notes.Notes.Count, Is.EqualTo(3));
            Assert.That(rig.Notes.Rows, Is.Null, "all rows to begin with");

            roll.FoldBox.SelectedIndex = 1;
            Settle();
            Assert.That(rig.Notes.Rows, Is.EqualTo(new[] { 61, 55, 48 }), "folded to the notes used");
            // A cell of the folded roll is a used pitch: a double click on an empty cell of the 55's row adds a note there.
            rig.DrawNote(6, 55);
            Assert.That(rig.Notes.Notes.Select(n => n.Note), Is.EqualTo(new[] { 48, 55, 61, 55 }), "the new note is on the row it was drawn on");
            Assert.That(rig.Notes.Rows, Is.EqualTo(new[] { 61, 55, 48 }));

            roll.KeyBox.SelectedIndex = 1;      // C
            roll.ScaleBox.SelectedIndex = 0;    // major
            roll.FoldBox.SelectedIndex = 2;
            Settle();
            Assert.That(rig.Notes.Scale, Is.Not.Null);
            Assert.That(rig.Notes.Rows, Does.Contain(61), "a pitch used stays even if it is off the scale");
            Assert.That(rig.Notes.Rows, Does.Not.Contain(63));
            Assert.That(rig.Notes.Rows.Count, Is.InRange(75, 82), "the scale's pitches over ten octaves, and the one used");

            roll.FoldBox.SelectedIndex = 0;
            roll.SnapBox.IsChecked = true;
            Settle();
            Assert.That(rig.Notes.SnapToScale, Is.True);
            int before = rig.Notes.Notes.Count;
            rig.Notes.AddNote(8 * TB, 61);
            Assert.That(rig.Notes.Notes.Count, Is.EqualTo(before + 1));
            Assert.That(rig.Notes.Notes.Last(n => n.Time == 8 * TB).Note, Is.EqualTo(60), "C# snaps to C in C major");
            roll.SnapBox.IsChecked = false;
            roll.KeyBox.SelectedIndex = 0;
            Settle();
            Assert.That(rig.Notes.Scale, Is.Null);
        }

        // ------------------------------------------------------------------ step input

        [AvaloniaTest]
        public void StepInputWritesTheKeysAtTheCursorAndStepsOnByTheGridAndHeldKeysMakeAChord()
        {
            using var rig = new Rig(("Neo Poly", "Lead"));
            var pattern = rig.DrawAndOpen(rig.Lane("Lead"), 0);
            var roll = rig.Roll;
            roll.StepBox.IsChecked = true;
            Settle();
            Assert.That(rig.Notes.StepInput, Is.True);
            rig.Notes.StepOctave = 4;
            rig.Notes.StepTime = 0;
            int undos = Undos;

            // Z is the octave's C; a key down and up writes a note and the cursor steps by the grid (a row: a tick here).
            rig.Key(PhysicalKey.Z);
            rig.Key(PhysicalKey.X);
            Assert.That(rig.Notes.Notes.Select(n => (n.Time / TB, n.Note, n.Duration / TB)), Is.EqualTo(new[] { (0, 48, 1), (1, 50, 1) }), "C then D, a step each");
            Assert.That(rig.Notes.StepTime, Is.EqualTo(2 * TB));
            Assert.That(Undos, Is.EqualTo(undos + 2));

            // Right rests, Left steps back (Space is the transport's).
            rig.Key(PhysicalKey.ArrowRight);
            Assert.That(rig.Notes.StepTime, Is.EqualTo(3 * TB));
            rig.Key(PhysicalKey.ArrowLeft);
            Assert.That(rig.Notes.StepTime, Is.EqualTo(2 * TB));
            Assert.That(rig.Notes.Notes.Count, Is.EqualTo(2), "the arrows move the cursor, not the notes, in step input");

            // Held keys are a chord: Z and C (E) pressed together at the cursor, released, and then it steps once.
            rig.Window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.None);
            rig.Window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.None);
            Settle();
            Assert.That(rig.Notes.StepTime, Is.EqualTo(2 * TB), "the cursor waits while keys are held");
            rig.Window.KeyReleaseQwerty(PhysicalKey.Z, RawInputModifiers.None);
            rig.Window.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.None);
            Settle();
            Assert.That(rig.Notes.Notes.Where(n => n.Time == 2 * TB).Select(n => n.Note).OrderBy(n => n), Is.EqualTo(new[] { 48, 52 }), "a chord");
            Assert.That(rig.Notes.StepTime, Is.EqualTo(3 * TB));

            // The octave keys, then off: the keys are the roll's again.
            rig.Window.KeyTextInput("*");
            Settle();
            Assert.That(rig.Notes.StepOctave, Is.EqualTo(5));
            roll.StepBox.IsChecked = false;
            Settle();
            Assert.That(rig.Notes.StepInput, Is.False);
            Assert.That(Core.SessionLayout.NotesOf(pattern).Count, Is.EqualTo(4));
        }

        // ------------------------------------------------------------------ ghosts

        [AvaloniaTest]
        public void TheNotesOfOtherClipsAreGhostsInTheRollAndNeverEdited()
        {
            using var rig = new Rig(("Neo Poly", "Lead"), ("Neo Poly", "Bass"));
            var lead = rig.Lane("Lead");
            var bass = rig.Lane("Bass");
            rig.DoubleClick(rig.At(lead, 0, 2));
            var leadPattern = lead.Events.Values.First().Pattern;
            Core.SessionLayout.EditNotes(leadPattern, null, new[] { N(0, 72), N(4, 76) });
            var bassPattern = rig.DrawAndOpen(bass, 0);
            Core.SessionLayout.EditNotes(bassPattern, null, new[] { N(0, 36) });
            var roll = rig.Roll;
            roll.Poll();

            Assert.That(roll.GhostBox.SelectedIndex, Is.EqualTo(1), "this track's, by default");
            Assert.That(roll.Ghosts, Is.Empty, "no other clip on Bass's lane");
            roll.GhostBox.SelectedIndex = 2;
            Settle();
            Assert.That(roll.Ghosts.Select(g => (g.Time / TB, g.Note)), Is.EqualTo(new[] { (0, 72), (4, 76) }), "Lead's notes, where they lie in Bass's clip");
            Assert.That(rig.Notes.Notes.Select(n => n.Note), Is.EqualTo(new[] { 36 }), "they are not Bass's notes");
            int undos = Undos;
            rig.Notes.SelectAll();
            rig.Notes.DeleteSelected();
            Assert.That(new MidiColumnNotes(Core, leadPattern).Notes().Count, Is.EqualTo(2), "editing leaves them alone");
            Assert.That(Undos, Is.EqualTo(undos + 1));

            // A clip on a second lane of the same machine is "this track's".
            Core.SongCore.AddSequence(bass.Machine, Core.SongCore.SequencesList.Count);
            Settle();
            var lane2 = rig.Lane("Bass", 1);
            rig.DoubleClick(rig.At(lane2, 0, 2));
            var secondPattern = lane2.Events.Values.First().Pattern;
            Core.SessionLayout.EditNotes(secondPattern, null, new[] { N(2, 40) });
            Assert.That(rig.Panel.Pattern, Is.SameAs(secondPattern), "the open roll followed the clip just drawn");
            rig.Click(rig.At(bass, 4));
            Assert.That(rig.Panel.Pattern, Is.SameAs(bassPattern));
            roll.GhostBox.SelectedIndex = 1;
            Settle();
            Assert.That(string.Join(",", roll.Ghosts.Select(g => $"{g.Time / TB}:{g.Note}")), Is.EqualTo("2:40"), "this track's: the other lane's clip, not Lead's");
            roll.GhostBox.SelectedIndex = 0;
            Settle();
            Assert.That(roll.Ghosts, Is.Empty, "off: none");
        }

        // ------------------------------------------------------------------ the host

        [AvaloniaTest]
        public void TheRollPanelFollowsTheSelectedClipClosesAndLetsGo()
        {
            using var rig = new Rig(("Neo Poly", "Lead"));
            var lane = rig.Lane("Lead");
            var first = rig.DrawAndOpen(lane, 0);
            rig.DoubleClick(rig.At(lane, 32, 2));      // a second clip, on empty space
            rig.Click(rig.At(lane, 4));                // select the first again
            rig.DoubleClick(rig.At(lane, 34, 6));
            var second = rig.Panel.Pattern;
            Assert.That(second, Is.Not.SameAs(first), "a double click on the second clip shows it");
            Assert.That(rig.Panel.TitleText, Does.Contain(second.Name));
            rig.Click(rig.At(lane, 4));
            Assert.That(rig.Panel.Pattern, Is.SameAs(first), "the open roll follows the selected clip");

            // The Pattern tab button of the panel asks for the pattern editor.
            IPattern asked = null;
            Action<IPattern> handler = p => asked = p;
            SequenceTab.PatternChosen += handler;
            try
            {
                rig.Click(rig.Of(rig.Panel.EditorButton));
                Assert.That(asked, Is.SameAs(first));
            }
            finally
            {
                SequenceTab.PatternChosen -= handler;
            }

            // Closing it lets go: the roll is dropped and nothing is left listening to the source.
            var source = rig.Notes.Source;
            Assert.That(Listeners(source), Is.GreaterThan(0), "the open roll listens to its source");
            rig.Click(rig.Of(rig.Panel.CloseButton));
            Assert.That(rig.Panel.IsVisible, Is.False);
            Assert.That(rig.Panel.Pattern, Is.Null);
            Assert.That(rig.Roll.Roll, Is.Null);
            Assert.That(Listeners(source), Is.EqualTo(0), "the closed roll no longer listens to the song's notes");

            // Leaving the Arrangement mode closes it too.
            rig.DoubleClick(rig.At(lane, 2, 6));
            Assert.That(rig.Panel.IsVisible, Is.True);
            rig.Click(rig.Of(rig.View.TrackerModeButton));
            Assert.That(rig.Panel.IsVisible, Is.False, "the tracker mode has no roll");
        }

        [AvaloniaTest]
        public void EveryControlOfThePanelIsOnTheKitsStyleAndHasAName()
        {
            using var rig = new Rig(("Neo Poly", "Lead"));
            rig.DrawAndOpen(rig.Lane("Lead"), 0);
            var problems = new List<string>();
            var all = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(rig.Panel).OfType<Avalonia.Controls.Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0).ToList();
            foreach (var c in all)
            {
                if (c.GetType() == typeof(Button) || c.GetType() == typeof(ToggleButton))
                {
                    var b = (TemplatedControl)c;
                    bool icon = b.Classes.Contains("icon");
                    var padding = icon ? StudioStyles.IconPadding : StudioStyles.ButtonPadding;
                    double font = icon ? StudioStyles.IconFontSize : StudioTokens.FontBody;
                    if (b.Padding != padding) problems.Add($"padding {b.Padding}: {((ContentControl)b).Content}");
                    if (b.FontSize != font) problems.Add($"font {b.FontSize}: {((ContentControl)b).Content}");
                    if (b.Bounds.Height < StudioStyles.ControlHeight - 0.5) problems.Add($"{b.Bounds.Height:0} high: {((ContentControl)b).Content}");
                    if (icon && string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(b))) problems.Add("an icon button without a name");
                }
                if (c is ComboBox box && string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(box))) problems.Add("a combo box without a name");
            }
            Assert.That(problems, Is.Empty);
            foreach (var name in new[] { "Close piano roll", "Edit in the Pattern tab", "Step input", "Snap to scale", "Key", "Scale", "Fold", "Ghosts", "Lane under the roll" })
                Assert.That(all.Any(c => Avalonia.Automation.AutomationProperties.GetName(c) == name), Is.True, name);
        }

        [AvaloniaTest]
        public void ThePanelDrawsInLightAndDark()
        {
            using var rig = new Rig(("Neo Poly", "Lead"));
            rig.DrawAndOpen(rig.Lane("Lead"), 0);
            rig.DrawNote(0, 60);
            var kind = StudioTheme.Kind;
            try
            {
                foreach (var theme in new[] { AppearanceTheme.Light, AppearanceTheme.Dark })
                {
                    StudioTheme.Kind = theme;
                    StudioTheme.Refresh();
                    Settle();
                    Studio.Screenshot(rig.Window, "ar2-roll-" + theme);
                }
            }
            finally
            {
                StudioTheme.Kind = kind;
                StudioTheme.Refresh();
                Settle();
            }
        }

        // ------------------------------------------------------------------ old songs

        [AvaloniaTest]
        public void AnOldSongRendersTheSameAfterTheRollsWereOpenedAndClosedWithoutAnEdit()
        {
            using var rig = new Rig(("Neo Poly", "Lead"));
            var lane = rig.Lane("Lead");
            rig.DoubleClick(rig.At(lane, 0, 2));
            var pattern = lane.Events.Values.First().Pattern;
            Core.SessionLayout.EditNotes(pattern, null, new[] { N(0, 60), N(2, 64), N(4, 67) });
            Core.SongCore.SongEnd = 32;
            string before = Snapshot(pattern);
            var first = RenderClip(lane, pattern, 48);
            Assert.That(Peak(first), Is.GreaterThan(0.001f));

            rig.DoubleClick(rig.At(lane, 0, 6));          // the arrangement's roll
            rig.Roll.GhostBox.SelectedIndex = 2;
            rig.Roll.LowerBox.SelectedIndex = 1;
            Settle();
            rig.Roll.StepBox.IsChecked = true;
            rig.Roll.StepBox.IsChecked = false;
            rig.Window.ShowPianoRoll();                    // and the Pattern tab's
            PatternView.Page!.Edit(pattern);
            Settle();
            rig.Click(rig.Of(rig.Panel.CloseButton));
            Assert.That(Snapshot(pattern), Is.EqualTo(before), "looking at the notes changes nothing of the pattern");
            var second = RenderClip(lane, pattern, 48);
            Assert.That(second, Is.EqualTo(first), "and the render is the same bits");
        }

        static string Snapshot(IPattern pattern) =>
            string.Join(";", pattern.Columns.Select(c => c.Parameter?.Name + "/" + c.Track + ":" + string.Join(",", c.GetEvents(int.MinValue, int.MaxValue).Select(e => $"{e.Time}.{e.Value}.{e.Duration}"))));
    }
}
