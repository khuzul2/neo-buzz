// Fork (docs/specs/ux-overhaul.md P3b, first step: decisions 40-44; clips.md §8, CU4): the detail panel's Tracker | Piano roll switch, and
// the piano roll on the clip's notes (SongNotes, the one layer over the store): draw, move, resize, delete and velocity, one undo step
// a gesture on the song's history, the grid with Alt to bypass it, the keys, and a large pattern drawn by its visible part only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Detail;
using ReBuzz.Studio.Views.Session;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class UxP3bTests
    {
        const int TB = PatternEvent.TimeBase;           // a tick; a row at four rows a beat

        internal sealed class Rig : IDisposable
        {
            public SessionRig Song { get; }
            public MainWindow Window { get; }
            public MachineCore Lead;
            public PatternCore Pattern;
            public DetailPanel Detail => Window.Detail;
            public PatternPage Page => PatternView.Page;
            public TrackerRoll Roll => Page.Roll;
            public PianoRoll Notes => Roll.Roll;

            public Rig(int ticks = 16, string gear = "Neo Poly")
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Song = gear == "Neo Poly" ? new SessionRig("Lead") : new SessionRig();
                Lead = gear == "Neo Poly" ? Song.Machine("Lead") : Song.Add(gear, "Lead");
                Lead.CreatePattern("00", ticks);
                Pattern = Lead.PatternsList.Single(p => p.Name == "00");
                StudioSelection.For(Song.Core).Clear(this);
                Window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
                Window.Show();
                Pump();
                Detail.Show(true);              // over the whole window: room to click the notes
                PatternView.Edit(Pattern);
                Pump();
                Assert.That(Page, Is.Not.Null);
                Assert.That(Page.Pattern, Is.EqualTo(Pattern));
            }

            /// <summary>Shows the roll.</summary>
            public void ShowRoll()
            {
                if (!Page.IsRollMode) Song.Click(Detail.RollButton);
                Pump();
                Assert.That(Page.IsRollMode, Is.True);
            }

            public void ShowTracker()
            {
                Song.Click(Detail.TrackerButton);
                Pump();
                Assert.That(Page.IsRollMode, Is.False);
            }

            /// <summary>The clip's notes as SongNotes reads them: (time, length, MIDI note).</summary>
            public (int Time, int Duration, int Note)[] Stored(string route = null) => StoredOf(Song.Core, Pattern, route);

            /// <summary>How many steps the song's history holds (an edit made now is one more).</summary>
            public int Undos => Song.Core.SongCore.ActionStack.Steps.Count;

            /// <summary>The shell's Undo and Redo: the roll's notes are the song's, so the song's history takes them.</summary>
            public bool Undo()
            {
                if (Page.Undo()) return true;
                var stack = Song.Core.SongCore.ActionStack;
                if (!stack.CanUndo) return false;
                stack.Undo();
                Roll.Poll();
                return true;
            }

            public bool Redo()
            {
                if (Page.Redo()) return true;
                var stack = Song.Core.SongCore.ActionStack;
                if (!stack.CanRedo) return false;
                stack.Redo();
                Roll.Poll();
                return true;
            }

            public void Dispose()
            {
                Studio.Host.Core.Modified = false;
                Window.Close();
                Pump();
                Song.Dispose();
                Studio.ResetPanels();
            }
        }

        internal static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        internal static (int Time, int Duration, int Note)[] StoredOf(ReBuzzCore core, IPattern pattern, string route = null) =>
            SongNotes.For(core, pattern, route).Notes().Select(n => (n.Time, n.Duration, n.Note)).ToArray();

        /// <summary>The events of every lane of a parameter and track in the clip, in time order: (time, value).</summary>
        static (int Time, int Value)[] Events(IPattern pattern, string parameter, int track = 0)
        {
            var key = pattern.Machine.ParameterGroups[2].Parameters.First(q => q.Name == parameter);
            return StoreLanes.Events(pattern, new ColumnKey(2, key.IndexInGroup, track)).Select(e => (e.Time, e.Value)).ToArray();
        }

        internal static int Buzz(int midi) => BuzzNote.FromMIDINote(midi);

        /// <summary>A point of the roll's grid for a time and a note, in the window (the roll scrolled to show it).</summary>
        internal static Point At(Rig r, int time, int note)
        {
            var roll = r.Notes;
            var p = roll.PointOf(time, note);
            r.Roll.Scroll.Offset = new Vector(Math.Max(0, p.X - 200), Math.Max(0, p.Y - 100));
            Pump();
            r.Window.UpdateLayout();
            var q = roll.TranslatePoint(roll.PointOf(time, note), r.Window);
            Assert.That(q, Is.Not.Null);
            return q!.Value;
        }

        internal static void Drag(Rig r, Point from, Point to, RawInputModifiers mods = RawInputModifiers.None)
        {
            r.Window.MouseMove(from, mods);
            r.Window.MouseDown(from, MouseButton.Left, mods);
            for (int i = 1; i <= 4; i++)
                r.Window.MouseMove(new Point(from.X + (to.X - from.X) * i / 4, from.Y + (to.Y - from.Y) * i / 4), mods | RawInputModifiers.LeftMouseButton);
            r.Window.MouseUp(to, MouseButton.Left, mods);
            Pump();
        }

        // ------------------------------------------------------------------ the switch

        [AvaloniaTest]
        public void TheDetailPanelSwitchesBetweenTheTrackerAndThePianoRollOnOnePattern()
        {
            using var r = new Rig();
            Assert.That(r.Page.IsRollMode, Is.False, "the Tracker first");
            Assert.That(r.Detail.TrackerButton.IsVisible && r.Detail.RollButton.IsVisible, Is.True, "both buttons in the bar");
            var grid = r.Page.EditorControl;
            Assert.That(grid, Is.Not.Null);

            r.ShowRoll();
            Assert.That(r.Detail.IsPianoRoll && r.Detail.IsOpen, Is.True);
            Assert.That(grid.IsEffectivelyVisible, Is.False, "the Tracker's grid gives way");
            Assert.That(r.Roll.IsVisible && r.Notes != null, Is.True);
            Assert.That(r.Roll.Pattern, Is.EqualTo(r.Pattern), "the same pattern");
            Assert.That(r.Page.Pattern, Is.EqualTo(r.Pattern));

            r.ShowTracker();
            Assert.That(grid.IsEffectivelyVisible && !r.Roll.IsVisible, Is.True, "and back");
            Assert.That(r.Page.Pattern, Is.EqualTo(r.Pattern), "still the same pattern");

            // The menu's command and the page remembered in the settings.
            r.Window.ShowPianoRoll();
            Pump();
            Assert.That(r.Detail.IsPianoRoll && r.Page.IsRollMode, Is.True);
            Assert.That(Studio.Host.Registry.Read("DetailPianoRoll", false, "Studio"), Is.True, "remembered");
            r.Window.ShowView("Pattern");
            Pump();
            Assert.That(r.Detail.IsPianoRoll, Is.False, "the Pattern command is the Tracker");
            Assert.That(Studio.Host.Registry.Read("DetailPianoRoll", true, "Studio"), Is.False);

            // Clicking the page that is shown hides the panel; the other page's button shows that page.
            r.ShowRoll();
            Pump();
            r.Song.Click(r.Detail.RollButton);
            Assert.That(r.Detail.IsOpen, Is.False);
        }

        [AvaloniaTest]
        public void AMachineWithoutANoteColumnSaysSoInsteadOfDrawingNothing()
        {
            using var r = new Rig();
            var fx = r.Song.Add("Neo Delay", "Echo");
            fx.CreatePattern("00", 16);
            var p = fx.PatternsList.Single();
            PatternView.Edit(p);
            Pump();
            r.ShowRoll();
            Assert.That(r.Roll.Notes(), Is.Null);
            Assert.That(r.Roll.Message, Does.Contain("no Note column"));
            r.Song.Dispose();
        }

        // ------------------------------------------------------------------ editing, one undo step a gesture

        [AvaloniaTest]
        public void DrawingANoteWritesTheClipsNotesAndOneUndoTakesItBack()
        {
            using var r = new Rig();
            r.ShowRoll();
            Assert.That(r.Stored(), Is.Empty);
            int undos = r.Undos;

            r.Notes.AddNote(2 * TB, 60);
            Assert.That(r.Stored(), Is.EqualTo(new[] { (2 * TB, TB, 60) }), "a note a row long");
            Assert.That(r.Undos, Is.EqualTo(undos + 1), "one step on the song's history");
            Assert.That(r.Notes.Notes.Select(n => (n.Time, n.Duration, n.Note)), Is.EqualTo(new[] { (2 * TB, TB, 60) }));

            r.ShowTracker();
            Assert.That(r.Stored(), Is.EqualTo(new[] { (2 * TB, TB, 60) }), "the Tracker's view of the clip holds the same notes");

            Assert.That(r.Undo(), Is.True, "the Tracker's undo");
            Assert.That(r.Stored(), Is.Empty);
            Assert.That(r.Redo(), Is.True);
            Assert.That(r.Stored(), Is.EqualTo(new[] { (2 * TB, TB, 60) }));
        }

        [AvaloniaTest]
        public void TheMouseDrawsMovesResizesAndDeletesWithOneUndoStepEach()
        {
            using var r = new Rig();
            r.ShowRoll();
            var roll = r.Notes;
            Assert.That(roll.Grid, Is.EqualTo(TB), "a row, the Tracker's");

            // Draw: a double click on empty space.
            MachineViewTests.DoubleClick(r.Window, At(r, 4 * TB, 60));
            Pump();
            Assert.That(r.Stored(), Is.EqualTo(new[] { (4 * TB, TB, 60) }), "drawn");
            int undos = r.Undos;

            // Move: drag the note two rows right and two semitones up.
            var from = At(r, 4 * TB, 60);
            var to = new Point(from.X + 2 * roll.TickWidth, from.Y - 2 * roll.RowHeight);
            Drag(r, from, to);
            Assert.That(r.Stored(), Is.EqualTo(new[] { (6 * TB, TB, 62) }), "moved on the grid");
            Assert.That(r.Undos, Is.EqualTo(undos + 1), "one undo step for the whole drag");

            // Resize: drag the note's end two rows right.
            var end = At(r, 7 * TB, 62);
            Drag(r, new Point(end.X - 4, end.Y), new Point(end.X - 4 + 2 * roll.TickWidth, end.Y));
            Assert.That(r.Stored(), Is.EqualTo(new[] { (6 * TB, 3 * TB, 62) }), "three rows long now");
            Assert.That(r.Undos, Is.EqualTo(undos + 2));

            // Delete: a double click on the note.
            MachineViewTests.DoubleClick(r.Window, At(r, 7 * TB, 62));
            Pump();
            Assert.That(r.Stored(), Is.Empty, "deleted");
            Assert.That(r.Undos, Is.EqualTo(undos + 3));
        }

        [AvaloniaTest]
        public void SnapsToTheGridAndAltBypassesIt()
        {
            using var r = new Rig();
            r.ShowRoll();
            var roll = r.Notes;
            roll.AddNote(0, 60);
            r.Roll.GridBox.SelectedIndex = 1;       // beat
            Pump();
            Assert.That(roll.Grid, Is.EqualTo(4 * TB));

            var from = At(r, 0, 60);
            var to = new Point(from.X + 1.5 * roll.TickWidth, from.Y);
            Drag(r, from, to);
            Assert.That(r.Notes.Notes[0].Time, Is.EqualTo(0), "a tick and a half is nothing on a beat grid");

            // Alt: off the grid, though the tracker's rows still take the note on a row.
            Drag(r, from, to, RawInputModifiers.Alt);
            Assert.That(r.Notes.Notes[0].Time, Is.EqualTo(TB + TB / 2), "bypassed the grid: the clip keeps the time as dragged (1.5 ticks)");
            Assert.That(r.Stored()[0].Time, Is.EqualTo(TB + TB / 2));
        }

        [AvaloniaTest]
        public void TheKeysMoveResizeTransposeAndChangeVelocityOfTheSelection()
        {
            using var r = new Rig();
            r.ShowRoll();
            var roll = r.Notes;
            roll.AddNote(TB, 60);
            roll.SelectAll();
            int undos = r.Undos;

            Assert.That(roll.HandleKey(Key.Right, KeyModifiers.None), Is.True);
            Assert.That(r.Stored(), Is.EqualTo(new[] { (2 * TB, TB, 60) }), "a grid step right");
            Assert.That(roll.HandleKey(Key.Right, KeyModifiers.Shift), Is.True);
            Assert.That(r.Stored(), Is.EqualTo(new[] { (2 * TB, 2 * TB, 60) }), "Shift: a step longer");
            Assert.That(roll.HandleKey(Key.Left, KeyModifiers.Shift), Is.True);
            Assert.That(r.Stored()[0].Duration, Is.EqualTo(TB), "and shorter");
            Assert.That(roll.HandleKey(Key.Up, KeyModifiers.None), Is.True);
            Assert.That(r.Stored()[0].Note, Is.EqualTo(61), "a semitone up");
            Assert.That(roll.HandleKey(Key.Up, KeyModifiers.Shift), Is.True);
            Assert.That(r.Stored()[0].Note, Is.EqualTo(73), "Shift: an octave");
            Assert.That(roll.HandleKey(Key.Down, KeyModifiers.Shift), Is.True);
            Assert.That(r.Stored()[0].Note, Is.EqualTo(61));
            Assert.That(r.Undos, Is.EqualTo(undos + 6), "one undo step a key");

            // ⌘ on a Mac, Ctrl elsewhere (the roll normalises it): the same key, platform-correct.
            var mod = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            roll.Focus();
            r.Window.KeyPressQwerty(PhysicalKey.ArrowUp, mod);
            r.Window.KeyReleaseQwerty(PhysicalKey.ArrowUp, mod);
            Pump();
            Assert.That(roll.Notes[0].Velocity, Is.Not.EqualTo(100), "the platform's command key and Up change the velocity");
            Assert.That(roll.Notes[0].Note, Is.EqualTo(61), "and don't transpose");
        }

        [AvaloniaTest]
        public void NotesSideBySideKeepTheirOwnLengthsAndTheLastOneRunsToThePatternsEnd()
        {
            using var r = new Rig();            // 16 ticks: 16 rows
            r.ShowRoll();
            var roll = r.Notes;
            roll.AddNote(0, 60);
            roll.AddNote(TB, 62);
            Assert.That(r.Stored(), Is.EqualTo(new[] { (0, TB, 60), (TB, TB, 62) }), "the second starts where the first ends");
            roll.AddNote(15 * TB, 64);
            Assert.That(roll.Notes.Select(n => (n.Time, n.End)).Last(), Is.EqualTo((15 * TB, 16 * TB)), "the last runs to the end");
        }

        [AvaloniaTest]
        public void WhatTheTrackerWritesInANoteColumnTheOneLayerReadsOnTheTracksRoute()
        {
            using var r = new Rig();
            // Types C, E, G and a note off on track 0 from row 0, in the Tracker: the unmarked lane of the Note parameter.
            dynamic view = r.Page.EditorControl;
            ((Control)view.TrackerGrid.Body).Focus();
            Pump();
            void Type(PhysicalKey k) { r.Window.KeyPressQwerty(k, RawInputModifiers.None); r.Window.KeyReleaseQwerty(k, RawInputModifiers.None); Pump(); }
            foreach (var k in new[] { PhysicalKey.Z, PhysicalKey.C, PhysicalKey.B, PhysicalKey.Digit1 }) Type(k);
            var typed = Events(r.Pattern, "Note");
            Assert.That(typed.Length, Is.EqualTo(4), "three notes and an off, in the lane the Tracker made");

            // SongNotes on the tracks route reads that lane (and any other lane of the Note parameter): the agent and the rolls see what was typed.
            var notes = r.Stored("tracks");
            Assert.That(notes.Length, Is.EqualTo(3));
            Assert.That(notes.Select(n => n.Time), Is.EqualTo(new[] { 0, TB, 2 * TB }));
            Assert.That(notes.Select(n => n.Duration), Is.EqualTo(new[] { TB, TB, TB }), "each lasts to the next, the last to the off");

            // Move the last note an octave through the layer: the others' events are exactly what they were.
            var source = SongNotes.For(r.Song.Core, r.Pattern, "tracks")!;
            var all = source.Notes();
            Assert.That(source.Edit(new[] { all[2] }, new[] { all[2] with { Note = all[2].Note + 12 } }), Is.Null);
            var after = Events(r.Pattern, "Note");
            Assert.That(after.Take(2), Is.EqualTo(typed.Take(2)), "the untouched notes' events are unchanged");
            Assert.That(after[2].Value, Is.EqualTo(typed[2].Value + (BuzzNote.FromMIDINote(all[2].Note + 12) - BuzzNote.FromMIDINote(all[2].Note))));
            Assert.That(after[3], Is.EqualTo(typed[3]), "the off stays");
        }

        [AvaloniaTest]
        public void AChordNeedsATrackEachAndIsRefusedWithTheReasonWhenTheMachineHasNone()
        {
            using var r = new Rig();
            var source = SongNotes.For(r.Song.Core, r.Pattern, "tracks")!;
            int tracks = Math.Max(1, r.Lead.TrackCount);
            var chord = Enumerable.Range(0, tracks).Select(i => new ClipNote(0, TB, 48 + 4 * i, 100)).ToArray();
            Assert.That(source.Edit(null, chord), Is.Null);
            int undos = r.Undos;
            string why = source.Edit(null, new[] { new ClipNote(0, TB, 48 + 4 * tracks, 100) });
            Assert.That(why, Does.Contain("track"), "refused, with the reason");
            Assert.That(source.Notes().Count, Is.EqualTo(tracks), "nothing changed");
            Assert.That(r.Undos, Is.EqualTo(undos), "and no undo step was made");
        }

        // ------------------------------------------------------------------ one layer, one undo step an edit

        [AvaloniaTest]
        public void EachGestureOfTheRollIsOneUndoStepOfTheSongAndUndoAndRedoShowInTheRoll()
        {
            using var r = new Rig();
            r.ShowRoll();
            int undos = r.Undos;

            r.Notes.AddNote(2 * TB, 60);
            Assert.That(r.Stored(), Is.EqualTo(new[] { (2 * TB, TB, 60) }));
            Assert.That(r.Undos, Is.EqualTo(undos + 1));
            r.Notes.SelectAll();
            r.Notes.HandleKey(Key.Up, KeyModifiers.Shift);
            Assert.That(r.Stored(), Is.EqualTo(new[] { (2 * TB, TB, 72) }), "an octave up");
            r.Notes.HandleKey(Key.Right, KeyModifiers.None);
            r.Notes.HandleKey(Key.Right, KeyModifiers.Shift);
            Assert.That(r.Stored(), Is.EqualTo(new[] { (3 * TB, 2 * TB, 72) }), "moved and lengthened");
            Assert.That(r.Undos, Is.EqualTo(undos + 4), "a step each");

            Assert.That(r.Undo(), Is.True);
            Assert.That(r.Stored(), Is.EqualTo(new[] { (3 * TB, TB, 72) }));
            r.Undo();
            r.Undo();
            Assert.That(r.Stored(), Is.EqualTo(new[] { (2 * TB, TB, 60) }));
            r.Undo();
            Assert.That(r.Stored(), Is.Empty, "the clip is as it was");
            Assert.That(r.Redo(), Is.True);
            Assert.That(r.Stored(), Is.EqualTo(new[] { (2 * TB, TB, 60) }), "and redo brings the note back");
            r.Roll.Poll();
            Assert.That(r.Notes.Notes.Count, Is.EqualTo(1));
        }

        [AvaloniaTest]
        public void NotesWrittenThroughTheLayerShowInTheRollAndTheTracksRouteSeesEveryLaneOfTheNoteParameter()
        {
            using var r = new Rig();
            // Notes written by another reader and writer of the layer (what the agent and the Session make) show in the roll.
            var midi = SongNotes.For(r.Song.Core, r.Pattern)!;
            Assert.That(midi.Edit(null, new[] { new ClipNote(0, TB, 64, 100) }), Is.Null);
            r.Window.ShowPianoRoll();
            Pump();
            Assert.That(r.Notes.Notes.Select(n => n.Note), Is.EqualTo(new[] { 64 }));

            // The tracks route writes the Note parameter's lane and does not touch the MIDI column.
            var tracks = SongNotes.For(r.Song.Core, r.Pattern, "tracks")!;
            Assert.That(tracks.Notes(), Is.Empty);
            Assert.That(tracks.Edit(null, new[] { new ClipNote(0, TB, 67, 100) }), Is.Null);
            Assert.That(Events(r.Pattern, "Note")[0].Value, Is.EqualTo(Buzz(67)));
            Assert.That(r.Stored().Select(n => n.Note), Is.EqualTo(new[] { 64 }), "the MIDI column is not touched");
        }

        // ------------------------------------------------------------------ one clip, two views

        [AvaloniaTest]
        public void TheRollFollowsTheStudiosSelectionAndTheTrackersChoosers()
        {
            using var r = new Rig();
            r.Lead.CreatePattern("01", 8);
            var other = r.Lead.PatternsList.Single(p => p.Name == "01");
            r.ShowRoll();
            StudioSelection.For(r.Song.Core).Set(r.Lead, other, this);
            Pump();
            Assert.That(r.Roll.Pattern, Is.EqualTo(other), "a selection made elsewhere shows in the roll");
            Assert.That(r.Page.Pattern, Is.EqualTo(other), "and in the Tracker's page");
            r.Notes.AddNote(0, 60);
            r.ShowTracker();
            Assert.That(StoredOf(r.Song.Core, other), Is.Not.Empty);
            Assert.That(r.Stored(), Is.Empty);
            PatternView.Edit(r.Pattern);
            Pump();
            Assert.That(StudioSelection.For(r.Song.Core).Pattern, Is.EqualTo(r.Pattern), "the page publishes what the user picks");
            r.ShowRoll();
            Assert.That(r.Roll.Pattern, Is.EqualTo(r.Pattern));
            Assert.That(r.Notes.Notes, Is.Empty);
        }

        [AvaloniaTest]
        public void AnUndoInTheTrackerOrFromTheMenuShowsInTheRoll()
        {
            using var r = new Rig();
            r.ShowRoll();
            r.Notes.AddNote(0, 60);
            Assert.That(r.Notes.Notes.Count, Is.EqualTo(1));
            Assert.That(r.Undo(), Is.True);
            Assert.That(r.Notes.Notes, Is.Empty, "undone in the roll too");
            r.Notes.Focus();
            r.Window.KeyPressQwerty(PhysicalKey.Z, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta | RawInputModifiers.Shift : RawInputModifiers.Control | RawInputModifiers.Shift);
            r.Window.KeyReleaseQwerty(PhysicalKey.Z, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta | RawInputModifiers.Shift : RawInputModifiers.Control | RawInputModifiers.Shift);
            Pump();
            r.Roll.Poll();
            Assert.That(r.Notes.Notes.Count, Is.EqualTo(1), "the redo key brings it back");
        }

        // ------------------------------------------------------------------ the song

        [AvaloniaTest]
        public void NothingEditedSavesTheSameBytesAndAnEditSavesWhatTheTrackerShows()
        {
            using var r = new Rig();
            string dir = TestTemp.Path($"rebuzz-p3b-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            try
            {
                string a = Path.Combine(dir, "a.nbl"), b = Path.Combine(dir, "b.nbl"), c = Path.Combine(dir, "c.nbl");
                r.Song.Core.SaveSongCopy(a);
                r.ShowRoll();
                r.Notes.HandleKey(Key.A, KeyModifiers.Control);     // looking, selecting: no edit
                r.ShowTracker();
                r.ShowRoll();
                r.Song.Core.SaveSongCopy(b);
                Assert.That(File.ReadAllBytes(b), Is.EqualTo(File.ReadAllBytes(a)), "the roll, looked at, changes nothing in the song");

                r.Notes.AddNote(TB, 60);
                r.Notes.AddNote(4 * TB, 67);
                var shown = r.Stored();
                r.Song.Core.SaveSongCopy(c);
                r.Song.Core.Modified = false;
                r.Song.Core.OpenSongFile(c);
                Pump();
                var lead = r.Song.Core.SongCore.MachinesList.First(m => m.Name == "Lead");
                var again = lead.PatternsList.First(p => p.Name == "00");
                Assert.That(StoredOf(r.Song.Core, again), Is.EqualTo(shown), "the saved song holds the notes the roll shows");
                Assert.That(shown, Is.EqualTo(new[] { (TB, TB, 60), (4 * TB, TB, 67) }), "and the layer reads them back from the reopened song");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ------------------------------------------------------------------ large patterns, names, contrast

        [AvaloniaTest]
        public void OnlyTheVisiblePartOfALargePatternIsDrawn()
        {
            using var r = new Rig(ticks: 1024);
            r.ShowRoll();
            var roll = r.Notes;
            var source = SongNotes.For(r.Song.Core, r.Pattern)!;
            var many = Enumerable.Range(0, 400).Select(i => new ClipNote(i * 2 * TB, TB, 36 + i % 48, 100)).ToArray();
            Assert.That(source.Edit(null, many), Is.Null);
            r.Roll.Poll();
            Assert.That(roll.Notes.Count, Is.EqualTo(400));
            r.Roll.Scroll.Offset = new Vector(0, roll.FocusOffset());
            Pump();
            int shown = roll.NotesInView();
            Assert.That(shown, Is.GreaterThan(0).And.LessThan(120), "a screenful of 400 notes");
            r.Roll.Scroll.Offset = new Vector(roll.X(600 * TB) - 100, roll.FocusOffset());
            Pump();
            Assert.That(roll.Viewport.X, Is.GreaterThan(1000), "the viewport follows the scrolling");
            Assert.That(roll.NotesInView(), Is.LessThan(120));
            Assert.That(roll.Notes.Count, Is.EqualTo(400), "all of them still there");
        }

        [AvaloniaTest]
        public void NotesAreNamedAsTheSettingSays()
        {
            using var r = new Rig();
            var naming = NoteNames.Naming;
            try
            {
                r.ShowRoll();
                r.Notes.AddNote(0, 60);
                r.Notes.SelectAll();
                Assert.That(r.Roll.SummaryText, Does.Contain("C3"), "middle C is C3 by default");
                NoteNames.Naming = NoteNaming.Scientific;
                Pump();
                Assert.That(r.Roll.SummaryText, Does.Contain("C4"), "and C4 when Preferences say so");
            }
            finally
            {
                NoteNames.Naming = naming;
            }
        }

        [AvaloniaTest]
        public void TheRollsTextMeetsTheContrastFloorOnTheTokens()
        {
            using var r = new Rig();
            r.ShowRoll();
            Assert.That(StudioTokens.Contrast(StudioTokens.TextSecondary, StudioTokens.SurfaceView), Is.GreaterThanOrEqualTo(4.5), "the summary");
            Assert.That(StudioTokens.Contrast(StudioTokens.TextPrimary, StudioTokens.SurfacePanel), Is.GreaterThanOrEqualTo(4.5), "the page buttons, the shown one");
            Assert.That(StudioTokens.Contrast(StudioTokens.TextSecondary, StudioTokens.SurfacePanel), Is.GreaterThanOrEqualTo(4.5), "the page buttons, the other");
            Assert.That(StudioTokens.Contrast(StudioTokens.TextPrimary, StudioTokens.SelectionFill), Is.GreaterThanOrEqualTo(4.5), "the page button lit");
        }
    }
}
