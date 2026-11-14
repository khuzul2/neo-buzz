// Fork (docs/specs/daw-modes.md AR3, seam S7): the tracker sees the notes, headless. The Modern Pattern Editor draws the pattern's
// engine MIDI column as lanes of its own ("Notes / Voice n": Note, Vel, Dly, Cut): a take written to the column shows at once,
// a typed row changes only that note (a step of the song's history), and the editor's own data and the song's bytes are as they were.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class TrackerNotesViewTests
    {
        const int TB = PatternEvent.TimeBase;
        const int Row = TB;                    // a row at 4 rows a beat

        sealed class Setup
        {
            public MainWindow Window;
            public MachineCore Machine;
            public IPattern Pattern;
            public dynamic View;
            public dynamic Vm => View.Editor.SelectedMachine.SelectedPattern;
        }

        static ReBuzzCore Core => Studio.Host.Core;

        static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        static RawInputModifiers Cmd => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        static Setup Open(string name, int ticks = 16)
        {
            ValueDisplay.Base = ValueBase.Hex;      // the keys below type hex digits
            Core.SongCore.CreateMachine("Neo Poly", null, name, null, null, null, -1, 0.3f, 0.3f);
            var machine = Core.SongCore.MachinesList.First(m => m.Name == name);
            machine.CreatePattern("00", ticks);
            var pattern = machine.Patterns.First(p => p.Name == "00");
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Pump();
            window.Detail.Show(true);
            PatternView.Edit(pattern);
            Pump();
            dynamic view = PatternView.Page.EditorControl;
            Assert.That((object)view, Is.Not.Null);
            ((Control)view.TrackerGrid.Body).Focus();
            Pump();
            return new Setup { Window = window, Machine = machine, Pattern = pattern, View = view };
        }

        static void Close(Setup s)
        {
            ValueDisplay.Base = ValueDisplay.Default;
            Core.Modified = false;
            s.Window.Close();
            Pump();
            Core.SongCore.DeleteMachines(new[] { s.Machine });
        }

        static void Key(Setup s, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            s.Window.KeyPressQwerty(key, modifiers);
            s.Window.KeyReleaseQwerty(key, modifiers);
            Pump();
        }

        /// <summary>Writes events to the pattern's MIDI column as a session take does when it ends.</summary>
        static void Take(IPattern pattern, params LaneEvent[] events) =>
            SessionLayout.EnsureMidiColumn(pattern).SetEvents(events.Select(e => new PatternEvent(e.Time, e.Value, e.Duration)).ToArray(), true);

        static LaneEvent Note(int time, int duration, int midi, int velocity = 100, int channel = 0) =>
            new LaneEvent(time, 0x90 | channel | (midi << 8) | (velocity << 16), duration);

        static List<LaneEvent> Column(IPattern pattern) =>
            (SessionLayout.EnsureMidiColumn(pattern)?.GetEvents(int.MinValue, int.MaxValue) ?? Enumerable.Empty<PatternEvent>())
                .Select(e => new LaneEvent(e.Time, e.Value, e.Duration)).OrderBy(e => e.Time).ThenBy(e => e.Value).ToList();

        /// <summary>The cursor on a lane of voice 0 (0 Note, 1 Vel, 2 Dly, 3 Cut) in a row, at the first digit.</summary>
        static void At(Setup s, int column, int row, int set = 0, int index = 0)
        {
            dynamic d = s.Vm.CursorPosition;
            s.View.Editor.Control.MoveCursor(d.SetColumnSet(set).SetColumn(column).SetBeat(row / 4).SetRowInBeat(row % 4).SetIndex(index));
            Pump();
        }

        static string Text(Setup s, int column, int row, int set = 0)
        {
            // The editor's types are in a load context of their own: through the interface the column's beat implements.
            object beat = s.Vm.ColumnSets[set].Columns[column].FetchBeat(row / 4);
            var rows = (System.Collections.IList)beat.GetType().GetInterfaces().First(i => i.Name == "IBeat").GetProperty("Rows").GetValue(beat);
            var r = rows[row % 4];
            return (string)r.GetType().GetField("ValueString").GetValue(r);
        }

        /// <summary>Where the tracker puts a note of a take: its voice and row (the lanes' own layout).</summary>
        static (int Voice, int Row) Where(IEnumerable<LaneEvent> events, LaneEvent note)
        {
            var list = events.ToList();
            var lanes = new NoteLanes(list, RowGrid.Beats(new[] { 4 }), 16 * TB);
            var cell = lanes.Cells.First(c => !c.IsOff && list[c.Source] == note);
            return (cell.Voice, cell.Row);
        }

        static LaneEvent[] ATake() => new[]
        {
            Note(0, Row, 60),                                   // on the grid
            Note(2 * Row + 61, 2 * Row - 7, 64, 90, 2),         // off the grid, on channel 3
            Note(4 * Row, 4 * Row, 67, 33),
            Note(5 * Row + 100, Row / 2, 71, 127),
            Note(9 * Row, 5 * Row, 62, 64),
            Note(2 * Row, 6 * Row, 48, 80),                     // under the second: a second voice
        };

        [AvaloniaTest]
        public void ATakeRecordedInTheSessionShowsInTheTrackerAtOnce()
        {
            var s = Open("AR3 Take");
            try
            {
                Assert.That((bool)s.Vm.HasNoteLanes, Is.False, "an empty MIDI column shows no lanes");
                int sets = s.Vm.ColumnSets.Count;
                Take(s.Pattern, ATake());
                Pump();
                Assert.That((bool)s.Vm.HasNoteLanes, Is.True, "the take is in the tracker without asking");
                Assert.That((string)s.Vm.ColumnSets[0].Label, Does.StartWith("Notes"));
                Assert.That(((System.Collections.ICollection)s.Vm.ColumnSets[0].Columns).Count, Is.EqualTo(4));
                var take = ATake();
                int voices = new NoteLanes(take, RowGrid.Beats(new[] { 4 }), 16 * TB).Voices;
                Assert.That(voices, Is.EqualTo(3), "the overlapping notes need three voices");
                Assert.That(s.Vm.ColumnSets.Count, Is.EqualTo(sets + voices + 1), "a set for each voice and a free one");
                var (v0, r0) = Where(take, take[0]);
                Assert.That(Text(s, 0, r0, set: v0), Is.EqualTo(NoteNames.TryTracker(BuzzNote.FromMIDINote(60))));
                var (v1, r1) = Where(take, take[1]);
                Assert.That(Text(s, 0, r1, set: v1), Is.EqualTo(NoteNames.TryTracker(BuzzNote.FromMIDINote(64))), "the note under another is on its own voice");
                Assert.That(Text(s, 1, r1, set: v1), Is.EqualTo(90.ToString("X2")), "velocity");
                Assert.That(Text(s, 2, r1, set: v1), Is.EqualTo(((int)Math.Round(61 * 95.0 / 240)).ToString("X2")), "the delay of the off-grid note");
                Studio.Screenshot(s.Window, "tracker-notes-lanes");
                // More notes arriving (another take, the piano roll, the agent) show too.
                Take(s.Pattern, Note(14 * Row, Row, 72));
                Pump();
                var more = take.Append(Note(14 * Row, Row, 72)).ToArray();
                var (v2, r2) = Where(more, more[^1]);
                Assert.That(Text(s, 0, r2, set: v2), Is.EqualTo(NoteNames.TryTracker(BuzzNote.FromMIDINote(72))));
            }
            finally { Close(s); }
        }

        [AvaloniaTest]
        public void EditingARowChangesOnlyThatNoteAndUndoPutsItBack()
        {
            var s = Open("AR3 Edit");
            try
            {
                Take(s.Pattern, ATake());
                Pump();
                var before = Column(s.Pattern);
                int undos = Core.SongCore.ActionStack.Actions.Count();

                // A key on the Note column of the third note: another pitch for it.
                var third = ATake()[2];
                var (tv, tr) = Where(before, third);
                At(s, 0, tr, set: tv);
                Key(s, PhysicalKey.Z);
                var after = Column(s.Pattern);
                Assert.That(Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(undos + 1), "one step of the song's history");
                var gone = before.Except(after).ToList();
                var came = after.Except(before).ToList();
                Assert.That(gone, Is.EqualTo(new[] { third }));
                Assert.That(came.Count, Is.EqualTo(1));
                Assert.That((came[0].Time, came[0].Duration, came[0].Velocity, came[0].Channel), Is.EqualTo((third.Time, third.Duration, 33, 0)), "time, length and velocity kept");
                Assert.That(came[0].Midi, Is.Not.EqualTo(67));
                Assert.That(after.Count, Is.EqualTo(before.Count));
                Assert.That(after.Intersect(before).Count(), Is.EqualTo(before.Count - 1), "every other note is exactly as it was");

                // A velocity typed in the Vel column of the off-grid note: that note's velocity only.
                var offGrid = ATake()[1];
                var (ov, orow) = Where(after, offGrid);
                At(s, 1, orow, set: ov);
                Key(s, PhysicalKey.Digit3);                     // the high digit; the cursor steps down a row
                At(s, 1, orow, set: ov, index: 1);
                Key(s, PhysicalKey.Digit3);                     // the low one
                var typed = Column(s.Pattern);
                Assert.That(typed.Except(after).Count(), Is.EqualTo(1));
                var changed = typed.Except(after).Single();
                Assert.That((changed.Time, changed.Duration, changed.Midi, changed.Channel), Is.EqualTo((offGrid.Time, offGrid.Duration, 64, 2)));
                Assert.That(changed.Velocity, Is.EqualTo(0x33));

                // The song's Undo (Ctrl+Z) takes them back, one by one.
                for (int i = 0; i < 3; i++) Key(s, PhysicalKey.Z, Cmd);       // the pitch, and the two digits
                Assert.That(Column(s.Pattern), Is.EqualTo(before), "undone to the very events of the take");
                for (int i = 0; i < 3; i++)
                    if (OperatingSystem.IsMacOS()) Key(s, PhysicalKey.Z, Cmd | RawInputModifiers.Shift);
                    else Key(s, PhysicalKey.Y, Cmd);
                Assert.That(Column(s.Pattern), Is.EqualTo(typed), "and redone");
            }
            finally { Close(s); }
        }

        [AvaloniaTest]
        public void ANoteTypedInAnEmptyCellAndAnOffAreNotesOfTheColumn()
        {
            var s = Open("AR3 Type");
            try
            {
                s.Vm.ShowNoteLanes = true;
                Pump();
                Assert.That((bool)s.Vm.HasNoteLanes, Is.True, "the Notes button shows the lanes of an empty column");
                At(s, 0, 2);
                Key(s, PhysicalKey.Z);
                var column = Column(s.Pattern);
                Assert.That(column.Count, Is.EqualTo(1));
                Assert.That((column[0].Time, column[0].Duration), Is.EqualTo((2 * Row, 16 * TB - 2 * Row)), "from the row to the end of the pattern");
                // An off two rows on ends it.
                At(s, 0, 5);
                Key(s, PhysicalKey.Digit1);
                column = Column(s.Pattern);
                Assert.That(column.Count, Is.EqualTo(1));
                Assert.That((column[0].Time, column[0].Duration), Is.EqualTo((2 * Row, 3 * Row)));
            }
            finally { Close(s); }
        }

        [AvaloniaTest]
        public void TheEditorsDataAndTheSongsFileAreNotChangedByTheLanes()
        {
            var s = Open("AR3 Bytes");
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rebuzz-studio-ar3-" + Environment.ProcessId);
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                // The editor's own data: its Note column typed, and a MIDI lane's note, as an old song has them.
                Key(s, PhysicalKey.Tab);
                Key(s, PhysicalKey.Z);
                var editorBefore = Core.MachineManager.GetMachineData(s.Machine.EditorMachine);
                var editorEvents = s.Pattern.PatternEditorMachineEvents.Select(c => (c.Parameter.Name, Events: c.Events.Select(e => (e.Time, e.Value, e.Duration)).OrderBy(e => e).ToArray())).ToList();

                Take(s.Pattern, ATake());
                Pump();
                var (bv, br) = Where(ATake(), ATake()[2]);
                At(s, 0, br, set: bv);
                Key(s, PhysicalKey.X);                  // an edit in the lanes
                Assert.That(Core.MachineManager.GetMachineData(s.Machine.EditorMachine), Is.EqualTo(editorBefore), "the lanes aren't in the editor's data");
                Assert.That(s.Pattern.PatternEditorMachineEvents.Select(c => (c.Parameter.Name, Events: c.Events.Select(e => (e.Time, e.Value, e.Duration)).OrderBy(e => e).ToArray())).ToList(), Is.EqualTo(editorEvents));

                var notes = Column(s.Pattern);
                SongFiles.Picker = save => null;
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                foreach (var ext in new[] { ".bmxml", ".bmx" })
                {
                    string path = System.IO.Path.Combine(dir, "lanes" + ext);
                    SongFiles.Picker = save => path;
                    Assert.That(SongFiles.SaveAs().Result, Is.True);
                    Assert.That(SongFiles.Open(path).Result, Is.True);
                    Pump();
                    var machine = Core.SongCore.MachinesList.First(m => m.Name == "AR3 Bytes");
                    s.Machine = machine;
                    s.Pattern = machine.Patterns.First();
                    Assert.That(Core.MachineManager.GetMachineData(machine.EditorMachine), Is.EqualTo(editorBefore), ext + ": the editor's data, byte for byte");
                    Assert.That(Column(s.Pattern), Is.EqualTo(notes), ext + ": the MIDI column's notes, exactly");
                }
                PatternView.Edit(s.Pattern);
                Pump();
                dynamic view = PatternView.Page.EditorControl;
                Assert.That((bool)view.Editor.SelectedMachine.SelectedPattern.HasNoteLanes, Is.True, "reopened, the lanes are there again");
            }
            finally
            {
                SongFiles.Picker = null;
                Dialogs.Responder = null;
                Close(s);
                SongFiles.New().Wait();
            }
        }

        [AvaloniaTest]
        public void TheLanesFollowTheKitAndTheOtherCommandsSayTheyAreNotThereYet()
        {
            var s = Open("AR3 Kit");
            try
            {
                Take(s.Pattern, ATake());
                Pump();
                Assert.That(s.View.NotesToggle.IsChecked, Is.True);
                Assert.That(Avalonia.Automation.AutomationProperties.GetName((Control)s.View.NotesToggle), Is.Not.Empty);
                // Transpose (Shift + and Ctrl+Shift +) moves the note under the cursor; the rest of the column stays.
                var before = Column(s.Pattern);
                var (cv, cr) = Where(before, ATake()[2]);
                At(s, 0, cr, set: cv);
                Key(s, PhysicalKey.NumPadAdd, RawInputModifiers.Shift);
                var after = Column(s.Pattern);
                Assert.That(before.Except(after).Single().Midi, Is.EqualTo(67));
                Assert.That(after.Except(before).Single().Midi, Is.EqualTo(68));
                // Insert/Delete rows is the editor's own columns' command: not in the lanes, and nothing changes.
                Key(s, PhysicalKey.Insert);
                Assert.That(Column(s.Pattern), Is.EqualTo(after));
            }
            finally { Close(s); }
        }

        [AvaloniaTest]
        public void AThousandNotesThroughKeyboardEditsKeepTheUntouchedOnesExact()
        {
            // 1,000 notes in a 64-beat pattern (the pattern's own length), seeded, off the grid and overlapping, with controllers beside.
            var s = Open("AR3 Random", ticks: 256);
            try
            {
                var rng = new Random(20261114);
                int length = 256 * TB;
                var original = new List<LaneEvent>();
                for (int i = 0; i < 1000; i++)
                    original.Add(Note(rng.Next(0, length - 1), rng.Next(1, 3 * 4 * TB), rng.Next(24, 108), rng.Next(1, 128), rng.Next(0, 3)));
                for (int i = 0; i < 12; i++) original.Add(new LaneEvent(rng.Next(0, length - 1), 0xB0 | (rng.Next(0, 120) << 8) | (rng.Next(0, 128) << 16), 0));
                Take(s.Pattern, original.ToArray());
                Pump();
                Assert.That((bool)s.Vm.HasNoteLanes, Is.True);
                Assert.That(Column(s.Pattern).Count, Is.EqualTo(1012));

                var noteKeys = new[] { PhysicalKey.Z, PhysicalKey.S, PhysicalKey.X, PhysicalKey.D, PhysicalKey.C, PhysicalKey.V, PhysicalKey.G, PhysicalKey.B };
                var digitKeys = new[] { PhysicalKey.Digit0, PhysicalKey.Digit1, PhysicalKey.Digit2, PhysicalKey.Digit3, PhysicalKey.Digit4, PhysicalKey.Digit5 };
                var touched = new List<LaneEvent>();
                int edits = 0;
                for (int n = 0; n < 45; n++)
                {
                    var now = Column(s.Pattern);
                    var cells = new NoteLanes(now, RowGrid.Beats(new[] { 4 }), length).Cells.Where(c => !c.IsOff).ToList();
                    var c = cells[rng.Next(cells.Count)];
                    int kind = rng.Next(4);
                    At(s, kind, c.Row, set: c.Voice);
                    switch (kind)
                    {
                        case 0: Key(s, rng.Next(6) == 0 ? PhysicalKey.Period : noteKeys[rng.Next(noteKeys.Length)]); break;
                        default: Key(s, digitKeys[rng.Next(digitKeys.Length)]); break;
                    }
                    var after = Column(s.Pattern);
                    var gone = now.Except(after).ToList();
                    var came = after.Except(now).ToList();
                    Assert.That(gone.Count, Is.LessThanOrEqualTo(1), $"edit {n}: at most the cell's note goes");
                    Assert.That(came.Count, Is.LessThanOrEqualTo(1), $"edit {n}: and at most one comes");
                    Assert.That(after.Count, Is.EqualTo(now.Count - gone.Count + came.Count));
                    if (gone.Count > 0) { touched.AddRange(gone); edits++; }
                }
                Assert.That(edits, Is.GreaterThan(20), "the run did edit");
                var final = Column(s.Pattern);
                foreach (var e in original.Where(e => !touched.Contains(e)))
                    Assert.That(final, Does.Contain(e), "an untouched note is exactly as it was: " + e);
                Assert.That(final.Where(e => !e.IsNote).ToList(), Is.EqualTo(original.Where(e => !e.IsNote).OrderBy(e => e.Time).ThenBy(e => e.Value).ToList()), "the controllers too");
            }
            finally { Close(s); }
        }

        [AvaloniaTest]
        public void TheNotesButtonAndTheLanesFollowTheKitInLightAndDark()
        {
            var s = Open("AR3 Kit2");
            var kind = StudioTheme.Kind;
            try
            {
                Take(s.Pattern, ATake());
                Pump();
                var all = ((Control)s.View).GetVisualDescendants().OfType<Control>().ToList();
                var notes = (ToggleButton)s.View.NotesToggle;
                Assert.That(Avalonia.Automation.AutomationProperties.GetName(notes), Does.Contain("Notes"));
                Assert.That(notes.Padding, Is.EqualTo(StudioStyles.ButtonPadding), "the kit's button style, as the other toggles");
                Assert.That(notes.FontSize, Is.EqualTo(StudioTokens.FontBody));
                var hex = (ToggleButton)s.View.HexToggle;
                Assert.That((notes.Padding, notes.FontSize, notes.Bounds.Height), Is.EqualTo((hex.Padding, hex.FontSize, hex.Bounds.Height)), "it is a toggle like its neighbours");
                foreach (var theme in new[] { AppearanceTheme.Light, AppearanceTheme.Dark })
                {
                    StudioTheme.Kind = theme;
                    StudioTheme.Refresh();
                    Pump();
                    Studio.Screenshot(s.Window, "ar3-lanes-" + theme);
                }
            }
            finally
            {
                StudioTheme.Kind = kind;
                StudioTheme.Refresh();
                Pump();
                Close(s);
            }
        }

        [AvaloniaTest]
        public void LeavingAPatternLetsGoOfItsColumn()
        {
            // The lanes follow the pattern's MIDI column; a pattern the editor no longer shows has no handler left on it.
            var s = Open("AR3 Leak");
            try
            {
                Take(s.Pattern, ATake());
                Pump();
                var column = SessionLayout.EnsureMidiColumn(s.Pattern);
                int Handlers() => ((Delegate)typeof(PatternColumnCore).GetField("EventsChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(column))?.GetInvocationList().Length ?? 0;
                int shown = Handlers();
                Assert.That(shown, Is.GreaterThan(0), "the lanes follow it");
                Core.Modified = false;
                s.Window.Close();
                Pump();
                Core.SongCore.DeleteMachines(new[] { s.Machine });
                Pump();
                Assert.That(Handlers(), Is.LessThan(shown), "no handler of the editor is left on a deleted machine's column");
            }
            finally
            {
                ValueDisplay.Base = ValueDisplay.Default;
            }
        }
    }
}
