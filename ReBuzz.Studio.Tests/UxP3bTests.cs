// Fork (docs/specs/ux-overhaul.md P3b, first step: decisions 40-44): the detail panel's Tracker | Piano roll switch, and the piano
// roll on the Tracker's own Note column: draw, move, resize, delete and velocity, one undo step a gesture on the history the
// Tracker has, the grid with Alt to bypass it, the keys, and a large pattern drawn by its visible part only.

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

            public Rig(int ticks = 16)
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Song = new SessionRig("Lead");
                Lead = Song.Machine("Lead");
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

            /// <summary>Shows the roll on the Tracker's Note column (the default here) or its MIDI lanes.</summary>
            public void ShowRoll(TrackerLane lane = TrackerLane.NoteColumn)
            {
                Page.Roll.UseLane(lane);
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

            /// <summary>Track 0's Note column in the Tracker (the editor's own store): (time, Buzz note).</summary>
            public (int Time, int Value)[] Column(string parameter = "Note", int track = 0) => Events(Pattern, parameter, track);

            /// <summary>How many steps the editor's history can undo (it undoes them all, and redoes them again).</summary>
            public int Undos
            {
                get
                {
                    var stack = Page.EditContext.ActionStack;
                    int n = 0;
                    while (stack.CanUndo && n < 1000) { stack.Undo(); n++; }
                    for (int i = 0; i < n; i++) stack.Redo();
                    return n;
                }
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

        /// <summary>The editor's events of a parameter (by name) and track: (time, value).</summary>
        static (int Time, int Value)[] Events(IPattern pattern, string parameter, int track = 0)
        {
            int found = 0;
            foreach (var c in pattern.PatternEditorMachineEvents.ToList())
            {
                if (c.Parameter.Name != parameter) continue;
                if (found++ == track)
                    return c.Events.Select(e => (e.Time, e.Value)).OrderBy(e => e.Time).ToArray();
            }
            return null;
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
        public void DrawingANoteWritesTheTrackersNoteColumnAndOneUndoTakesItBack()
        {
            using var r = new Rig();
            r.ShowRoll();
            Assert.That(r.Column(), Is.Empty);
            int undos = r.Undos;

            r.Notes.AddNote(2 * TB, 60);
            Assert.That(r.Column(), Is.EqualTo(new[] { (2 * TB, Buzz(60)), (3 * TB, BuzzNote.Off) }), "a note-on and, a row later, its off: as the Tracker writes one");
            Assert.That(r.Undos, Is.EqualTo(undos + 1), "one step on the editor's history");
            Assert.That(r.Notes.Notes.Select(n => (n.Time, n.Duration, n.Note)), Is.EqualTo(new[] { (2 * TB, TB, 60) }));

            r.ShowTracker();
            Assert.That(r.Column(), Is.EqualTo(new[] { (2 * TB, Buzz(60)), (3 * TB, BuzzNote.Off) }), "the Tracker has the same notes");

            Assert.That(r.Page.Undo(), Is.True, "the Tracker's undo");
            Assert.That(r.Column(), Is.Empty, "both events in one step");
            Assert.That(r.Page.Redo(), Is.True);
            Assert.That(r.Column(), Is.EqualTo(new[] { (2 * TB, Buzz(60)), (3 * TB, BuzzNote.Off) }));
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
            Assert.That(r.Column(), Is.EqualTo(new[] { (4 * TB, Buzz(60)), (5 * TB, BuzzNote.Off) }), "drawn");
            int undos = r.Undos;

            // Move: drag the note two rows right and two semitones up.
            var from = At(r, 4 * TB, 60);
            var to = new Point(from.X + 2 * roll.TickWidth, from.Y - 2 * roll.RowHeight);
            Drag(r, from, to);
            Assert.That(r.Column(), Is.EqualTo(new[] { (6 * TB, Buzz(62)), (7 * TB, BuzzNote.Off) }), "moved on the grid");
            Assert.That(r.Undos, Is.EqualTo(undos + 1), "one undo step for the whole drag");

            // Resize: drag the note's end two rows right.
            var end = At(r, 7 * TB, 62);
            Drag(r, new Point(end.X - 4, end.Y), new Point(end.X - 4 + 2 * roll.TickWidth, end.Y));
            Assert.That(r.Column(), Is.EqualTo(new[] { (6 * TB, Buzz(62)), (9 * TB, BuzzNote.Off) }), "three rows long now");
            Assert.That(r.Undos, Is.EqualTo(undos + 2));

            // Delete: a double click on the note.
            MachineViewTests.DoubleClick(r.Window, At(r, 7 * TB, 62));
            Pump();
            Assert.That(r.Column(), Is.Empty, "deleted with its off");
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
            Assert.That(r.Notes.Notes[0].Time, Is.EqualTo(TB), "bypassed the beat grid: on the row at 1.5 ticks");
            Assert.That(r.Column()[0].Time, Is.EqualTo(TB));
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
            Assert.That(r.Column(), Is.EqualTo(new[] { (2 * TB, Buzz(60)), (3 * TB, BuzzNote.Off) }), "a grid step right");
            Assert.That(roll.HandleKey(Key.Right, KeyModifiers.Shift), Is.True);
            Assert.That(r.Column(), Is.EqualTo(new[] { (2 * TB, Buzz(60)), (4 * TB, BuzzNote.Off) }), "Shift: a step longer");
            Assert.That(roll.HandleKey(Key.Left, KeyModifiers.Shift), Is.True);
            Assert.That(r.Column().Last().Time, Is.EqualTo(3 * TB), "and shorter");
            Assert.That(roll.HandleKey(Key.Up, KeyModifiers.None), Is.True);
            Assert.That(r.Column()[0].Value, Is.EqualTo(Buzz(61)), "a semitone up");
            Assert.That(roll.HandleKey(Key.Up, KeyModifiers.Shift), Is.True);
            Assert.That(r.Column()[0].Value, Is.EqualTo(Buzz(73)), "Shift: an octave");
            Assert.That(roll.HandleKey(Key.Down, KeyModifiers.Shift), Is.True);
            Assert.That(r.Column()[0].Value, Is.EqualTo(Buzz(61)));
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
        public void ANoteStartingWhereTheNextEndsHasNoOffAndTheLastOneRunsToThePatternsEnd()
        {
            using var r = new Rig();            // 16 ticks: 16 rows
            r.ShowRoll();
            var roll = r.Notes;
            roll.AddNote(0, 60);
            roll.AddNote(TB, 62);
            Assert.That(r.Column(), Is.EqualTo(new[] { (0, Buzz(60)), (TB, Buzz(62)), (2 * TB, BuzzNote.Off) }), "the next note-on ends the first: no off between them");
            roll.AddNote(15 * TB, 64);
            var col = r.Column();
            Assert.That(col.Last(), Is.EqualTo((15 * TB, Buzz(64))), "a note that runs to the end has no off (the Tracker's own way)");
            Assert.That(roll.Notes.Select(n => (n.Time, n.End)).Last(), Is.EqualTo((15 * TB, 16 * TB)));
        }

        [AvaloniaTest]
        public void WhatTheTrackerWritesTheRollShowsAndAnEditLeavesTheOtherNotesAlone()
        {
            using var r = new Rig();
            // Types C, E, G and a note off on track 0 from row 0, in the Tracker.
            dynamic view = r.Page.EditorControl;
            ((Control)view.TrackerGrid.Body).Focus();
            Pump();
            void Type(PhysicalKey k) { r.Window.KeyPressQwerty(k, RawInputModifiers.None); r.Window.KeyReleaseQwerty(k, RawInputModifiers.None); Pump(); }
            foreach (var k in new[] { PhysicalKey.Z, PhysicalKey.C, PhysicalKey.B, PhysicalKey.Digit1 }) Type(k);
            var typed = r.Column();
            Assert.That(typed.Length, Is.EqualTo(4), "three notes and an off");

            r.ShowRoll();
            var notes = r.Notes.Notes;
            Assert.That(notes.Count, Is.EqualTo(3), "the roll shows the three notes");
            Assert.That(notes.Select(n => n.Time), Is.EqualTo(new[] { 0, TB, 2 * TB }));
            Assert.That(notes.Select(n => n.Duration), Is.EqualTo(new[] { TB, TB, TB }), "each lasts to the next, the last to the off");
            int g = notes[2].Note;

            // Move the last note an octave: the others' events are exactly what they were.
            r.Notes.SelectAll();
            r.Notes.DeselectAll();
            var lastNote = notes[2];
            var click = At(r, lastNote.Time, lastNote.Note);
            r.Window.MouseDown(click, MouseButton.Left);
            r.Window.MouseUp(click, MouseButton.Left);
            Pump();
            r.Notes.HandleKey(Key.Up, KeyModifiers.Shift);
            var after = r.Column();
            Assert.That(after.Take(2), Is.EqualTo(typed.Take(2)), "the untouched notes' events are unchanged");
            Assert.That(after[2].Value, Is.EqualTo(typed[2].Value + 12 * 16 / 16 * 0 + (BuzzNote.FromMIDINote(g + 12) - BuzzNote.FromMIDINote(g))));
            Assert.That(after[3], Is.EqualTo(typed[3]), "the off stays");
        }

        [AvaloniaTest]
        public void AChordNeedsATrackEachAndIsRefusedWithTheReasonWhenTheMachineHasNone()
        {
            using var r = new Rig();
            r.ShowRoll();
            int tracks = Math.Max(1, r.Lead.TrackCount);
            var roll = r.Notes;
            for (int i = 0; i < tracks; i++) roll.AddNote(0, 48 + 4 * i);
            int count = roll.Notes.Count;
            int undos = r.Undos;
            roll.AddNote(0, 48 + 4 * tracks);
            Assert.That(roll.Refusal, Does.Contain("track"), "refused, with the reason");
            Assert.That(r.Roll.SummaryText, Does.Contain("track"), "the summary says so");
            Assert.That(roll.Notes.Count, Is.EqualTo(count), "nothing changed");
            Assert.That(r.Undos, Is.EqualTo(undos), "and no undo step was made");
        }

        // ------------------------------------------------------------------ the MIDI lanes

        static (int Time, int Duration, int Note)[] Lanes(Rig r) =>
            TrackerNotes.Unpack(r.Song.Core.MachineManager.ExportClipNotes(r.Lead, r.Pattern)).Select(n => (n.Time, n.Duration, n.Midi)).ToArray();

        [AvaloniaTest]
        public void TheMidiLanesOfTheTrackerAreEditedWithOneUndoStepAGesture()
        {
            using var r = new Rig();
            r.ShowRoll(TrackerLane.MidiLanes);
            Assert.That(r.Roll.Lane, Is.EqualTo(TrackerLane.MidiLanes));
            Assert.That(r.Roll.LaneBox.IsVisible, Is.True, "a machine with a Note column and MIDI lanes can show either");
            int undos = r.Undos;

            r.Notes.AddNote(2 * TB, 60);
            Assert.That(Lanes(r), Is.EqualTo(new[] { (2 * TB, TB, 60) }), "the notes the editor reads from the lanes");
            Assert.That(r.Undos, Is.EqualTo(undos + 1));
            r.Notes.SelectAll();
            r.Notes.HandleKey(Key.Up, KeyModifiers.Shift);
            Assert.That(Lanes(r), Is.EqualTo(new[] { (2 * TB, TB, 72) }), "an octave up");
            r.Notes.HandleKey(Key.Right, KeyModifiers.None);
            r.Notes.HandleKey(Key.Right, KeyModifiers.Shift);
            Assert.That(Lanes(r), Is.EqualTo(new[] { (3 * TB, 2 * TB, 72) }), "moved and lengthened");
            Assert.That(r.Undos, Is.EqualTo(undos + 4), "a step each");
            Assert.That(r.Column(), Is.Empty, "the Note column is another store");

            Assert.That(r.Page.Undo(), Is.True);
            Assert.That(Lanes(r), Is.EqualTo(new[] { (3 * TB, TB, 72) }));
            r.Page.Undo();
            r.Page.Undo();
            Assert.That(Lanes(r), Is.EqualTo(new[] { (2 * TB, TB, 60) }));
            r.Page.Undo();
            Assert.That(Lanes(r), Is.Empty, "the lanes are as they were");
            Assert.That(r.Page.Redo(), Is.True);
            Assert.That(Lanes(r), Is.EqualTo(new[] { (2 * TB, TB, 60) }), "and redo brings the note back");
            r.Roll.Poll();
            Assert.That(r.Notes.Notes.Count, Is.EqualTo(1));
        }

        [AvaloniaTest]
        public void TheRollShowsTheStoreThatHasNotesAndOffersTheOther()
        {
            using var r = new Rig();
            // Notes on the MIDI lanes only (what the Session View's "move into the Tracker" and the agent make).
            Assert.That(r.Song.Core.MachineManager.ImportClipNotes(r.Lead, r.Pattern, TrackerNotes.Pack(new[] { new TrackerNote(0, TB, 64, 100) })), Is.True);
            r.Roll.UseLane(TrackerLane.Auto);
            r.Window.ShowPianoRoll();
            Pump();
            Assert.That(r.Roll.Lane, Is.EqualTo(TrackerLane.MidiLanes), "the lanes have the notes");
            Assert.That(r.Notes.Notes.Select(n => n.Note), Is.EqualTo(new[] { 64 }));

            r.Roll.LaneBox.SelectedIndex = 0;       // the Note column, which is empty
            Pump();
            Assert.That(r.Roll.Lane, Is.EqualTo(TrackerLane.NoteColumn));
            Assert.That(r.Notes.Notes, Is.Empty);
            r.Notes.AddNote(0, 67);
            Assert.That(r.Column()[0].Value, Is.EqualTo(Buzz(67)));
            Assert.That(Lanes(r).Select(n => n.Note), Is.EqualTo(new[] { 64 }), "the lanes are not touched");
            r.Roll.UseLane(TrackerLane.Auto);
            Assert.That(r.Roll.Lane, Is.Not.Null);
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
            Assert.That(Events(other, "Note"), Is.Not.Empty);
            Assert.That(Events(r.Pattern, "Note"), Is.Empty);
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
            Assert.That(r.Page.Undo(), Is.True);
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
            string dir = Path.Combine(Path.GetTempPath(), $"rebuzz-p3b-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            try
            {
                string a = Path.Combine(dir, "a.bmx"), b = Path.Combine(dir, "b.bmx"), c = Path.Combine(dir, "c.bmx");
                r.Song.Core.SaveSongCopy(a);
                r.ShowRoll();
                r.Notes.HandleKey(Key.A, KeyModifiers.Control);     // looking, selecting: no edit
                r.ShowTracker();
                r.ShowRoll();
                r.Song.Core.SaveSongCopy(b);
                Assert.That(File.ReadAllBytes(b), Is.EqualTo(File.ReadAllBytes(a)), "the roll, looked at, changes nothing in the song");

                r.Notes.AddNote(TB, 60);
                r.Notes.AddNote(4 * TB, 67);
                var shown = r.Column();
                r.Song.Core.SaveSongCopy(c);
                r.Song.Core.Modified = false;
                r.Song.Core.OpenSongFile(c);
                Pump();
                var lead = r.Song.Core.SongCore.MachinesList.First(m => m.Name == "Lead");
                var again = lead.PatternsList.First(p => p.Name == "00");
                Assert.That(Events(again, "Note"), Is.EqualTo(shown), "the saved song holds the notes the Tracker shows");
                var roll = ReBuzz.Core.Notes.EditorTrackerNotes.ForEditor(r.Song.Core, again);
                Assert.That(roll.Notes().Select(n => (n.Time, n.Duration, n.Note)), Is.EqualTo(new[] { (TB, TB, 60), (4 * TB, TB, 67) }), "and so the roll");
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
            var source = EditorTrackerNotes.ForEditor(r.Song.Core, r.Pattern)!;
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
