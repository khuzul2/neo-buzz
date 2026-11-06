// Fork (docs/specs/studio-mvp.md S6): the Modern Pattern Editor in the studio's Pattern tab, headless:
// keyboard entry of notes and automation, the editor's data in the engine, edit commands (transpose,
// interpolate, copy and paste), undo, playing the pattern through the engine, and screenshots.

using System;
using System.Linq;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class PatternEditorTests
    {
        const int Row = PatternEvent.TimeBase;      // a row at 4 rows a beat

        sealed class Setup
        {
            public MainWindow Window;
            public MachineCore Machine;
            public IPattern Pattern;
            public Control EditorView;
        }

        static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        /// <summary>A Neo Poly with an empty 4-beat pattern, shown in the Pattern tab with the keyboard on its grid.</summary>
        static Setup Open(string name)
        {
            var core = Studio.Host.Core;
            core.SongCore.CreateMachine("Neo Poly", null, name, null, null, null, -1, 0.3f, 0.3f);
            var machine = core.SongCore.MachinesList.First(m => m.Name == name);
            machine.CreatePattern("00", 16);
            var pattern = machine.Patterns.First(p => p.Name == "00");

            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Pump();
            PatternView.Edit(pattern);
            Pump();
            var page = PatternView.Page;
            Assert.That(page, Is.Not.Null, "the Pattern tab is there");
            Assert.That(page.Pattern, Is.EqualTo(pattern), "it shows the pattern");
            var view = page.EditorControl;
            Assert.That(view, Is.Not.Null, "the Modern Pattern Editor's view is in the tab");
            dynamic dview = view;
            ((Control)dview.TrackerGrid.Body).Focus();
            Pump();
            return new Setup { Window = window, Machine = machine, Pattern = pattern, EditorView = view };
        }

        static void Close(Setup s)
        {
            Studio.Host.Core.Modified = false;      // else closing asks to save, and the window stays open
            s.Window.Close();
            Pump();
            Studio.Host.Core.SongCore.DeleteMachines(new[] { s.Machine });
        }

        static void Key(Setup s, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            s.Window.KeyPressQwerty(key, modifiers);
            s.Window.KeyReleaseQwerty(key, modifiers);
            Pump();
        }

        static void Keys(Setup s, params PhysicalKey[] keys)
        {
            foreach (var k in keys) Key(s, k);
        }

        /// <summary>The editor's events for a parameter (by name) and track: (time, value).</summary>
        static (int Time, int Value)[] Events(IPattern pattern, string parameter, int track = 0)
        {
            var columns = pattern.PatternEditorMachineEvents.ToList();
            var machine = pattern.Machine;
            int found = 0;
            foreach (var c in columns)
            {
                if (c.Parameter.Name != parameter) continue;
                if (found++ == track)
                    return c.Events.Select(e => (e.Time, e.Value)).OrderBy(e => e.Time).ToArray();
            }
            return null;
        }

        static dynamic Grid(Setup s) => ((dynamic)s.EditorView).TrackerGrid;

        static Control Body(Setup s) => (Control)Grid(s).Body;

        /// <summary>Types C, E, G and a note off on track 0's Note column, from row 0.</summary>
        static void TypeChord(Setup s)
        {
            Key(s, PhysicalKey.Tab);
            Keys(s, PhysicalKey.Z, PhysicalKey.C, PhysicalKey.B, PhysicalKey.Digit1);
        }

        [AvaloniaTest]
        public void NotesAndAutomationTypedOnTheKeyboardAreTheEditorsData()
        {
            var s = Open("PE Keys");
            try
            {
                Studio.Screenshot(s.Window, "pattern-editor-empty");

                // Tab: from the globals to track 0, its Note column. Z, C, B: C, E, G at the base octave.
                TypeChord(s);
                Studio.Screenshot(s.Window, "pattern-editor-notes");
                var notes = Events(s.Pattern, "Note");
                Assert.That(notes, Is.EqualTo(new[] { (0, BuzzNote.Parse("C-4")), (Row, BuzzNote.Parse("E-4")), (2 * Row, BuzzNote.Parse("G-4")), (3 * Row, BuzzNote.Off) }),
                    "notes at the base octave (4), then 1 for a note off, a row each");

                // Shift+Tab back to the globals' first column (Voice mode): row 4, the low digit, 2.
                Key(s, PhysicalKey.Tab, RawInputModifiers.Shift);
                Key(s, PhysicalKey.Home);
                Keys(s, PhysicalKey.ArrowDown, PhysicalKey.ArrowDown, PhysicalKey.ArrowDown, PhysicalKey.ArrowDown, PhysicalKey.ArrowRight, PhysicalKey.Digit2);
                Assert.That(Events(s.Pattern, "Voice mode"), Is.EqualTo(new[] { (4 * Row, 2) }), "automation of a global parameter");

                // A word: Uni detune (the fourth column), its second digit: 3 → 0300. (Home keeps the
                // digit, as in WPF: from Voice mode's second digit, six to the right.)
                Key(s, PhysicalKey.Home);
                Keys(s, PhysicalKey.ArrowRight, PhysicalKey.ArrowRight, PhysicalKey.ArrowRight, PhysicalKey.ArrowRight, PhysicalKey.ArrowRight, PhysicalKey.ArrowRight);
                Key(s, PhysicalKey.Digit3);
                Assert.That(Events(s.Pattern, "Uni detune"), Is.EqualTo(new[] { (0, 0x300) }), "a word's second digit");
                Studio.Screenshot(s.Window, "pattern-editor-typed");

                // The editor's data (what songs save) has them too.
                var data = s.Machine.EditorMachine != null ? Studio.Host.Core.MachineManager.GetMachineData(s.Machine.EditorMachine) : null;
                Assert.That(data, Is.Not.Null.And.Length.GreaterThan(10));
                Assert.That(data[0], Is.EqualTo(255), "the Modern Pattern Editor's own format, as on Windows");

                // Period clears a value; Delete deletes the row (the rows below move up).
                Key(s, PhysicalKey.Tab);
                Key(s, PhysicalKey.PageUp);
                Key(s, PhysicalKey.Period);
                Assert.That(Events(s.Pattern, "Note").Select(e => e.Time), Is.EqualTo(new[] { Row, 2 * Row, 3 * Row }), "period clears C-4");
                Key(s, PhysicalKey.ArrowUp);
                Key(s, PhysicalKey.Delete);
                Assert.That(Events(s.Pattern, "Note").Select(e => e.Time), Is.EqualTo(new[] { 0, Row, 2 * Row }), "delete moves the rows up");
            }
            finally
            {
                Close(s);
            }
        }

        [AvaloniaTest]
        public void TransposeInterpolateCopyPasteAndUndo()
        {
            var s = Open("PE Edits");
            var core = Studio.Host.Core;
            int songActions = core.SongCore.ActionStack.Actions.Count();
            try
            {
                TypeChord(s);
                // Select the chord (rows 0–2): back to row 0, Shift+Down twice. Shift+Add: up a semitone.
                Keys(s, PhysicalKey.ArrowUp, PhysicalKey.ArrowUp, PhysicalKey.ArrowUp, PhysicalKey.ArrowUp);
                Key(s, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
                Key(s, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
                Key(s, PhysicalKey.NumPadAdd, RawInputModifiers.Shift);
                Assert.That(Events(s.Pattern, "Note").Select(e => BuzzNote.TryToString(e.Value)), Is.EqualTo(new[] { "C#4", "F-4", "G#4", "off" }), "transposed a semitone");
                Key(s, PhysicalKey.NumPadAdd, RawInputModifiers.Control | RawInputModifiers.Shift);
                Assert.That(Events(s.Pattern, "Note").Select(e => BuzzNote.TryToString(e.Value)), Is.EqualTo(new[] { "C#5", "F-5", "G#5", "off" }), "and an octave");

                // Copy the selection (the Edit menu's Copy, through the page), paste at row 8 (Ctrl+V).
                Assert.That(((IStudioEditTarget)PatternView.Page).Copy(), Is.True, "Copy goes to the editor");
                Key(s, PhysicalKey.ArrowUp, RawInputModifiers.None);
                Keys(s, PhysicalKey.ArrowUp, PhysicalKey.ArrowUp);
                for (int i = 0; i < 8; i++) Key(s, PhysicalKey.ArrowDown);
                Key(s, PhysicalKey.V, RawInputModifiers.Control);
                Studio.Screenshot(s.Window, "pattern-editor-pasted");
                Assert.That(Events(s.Pattern, "Note").Select(e => (e.Time, BuzzNote.TryToString(e.Value))),
                    Is.EqualTo(new[] { (0, "C#5"), (Row, "F-5"), (2 * Row, "G#5"), (3 * Row, "off"), (8 * Row, "C#5"), (9 * Row, "F-5"), (10 * Row, "G#5") }), "pasted at row 8");

                // Interpolate: Uni detune 0000 at row 0 and 0300 at row 8, select them, Ctrl+I.
                Key(s, PhysicalKey.Tab, RawInputModifiers.Shift);
                Key(s, PhysicalKey.Home);
                Keys(s, PhysicalKey.ArrowRight, PhysicalKey.ArrowRight, PhysicalKey.ArrowRight, PhysicalKey.ArrowRight, PhysicalKey.ArrowRight, PhysicalKey.ArrowRight);
                Key(s, PhysicalKey.PageUp);
                Key(s, PhysicalKey.Digit0);
                for (int i = 0; i < 7; i++) Key(s, PhysicalKey.ArrowDown);
                Key(s, PhysicalKey.ArrowRight);
                Key(s, PhysicalKey.Digit3);
                Assert.That(Events(s.Pattern, "Uni detune"), Is.EqualTo(new[] { (0, 0), (8 * Row, 0x300) }));
                Key(s, PhysicalKey.PageUp);
                for (int i = 0; i < 8; i++) Key(s, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
                Key(s, PhysicalKey.I, RawInputModifiers.Control);
                var ramp = Events(s.Pattern, "Uni detune");
                Assert.That(ramp.Select(e => e.Time), Is.EqualTo(Enumerable.Range(0, 9).Select(i => i * Row)), "a value on every row between");
                Assert.That(ramp.Select(e => e.Value), Is.Ordered.Ascending);
                Assert.That(ramp[4].Value, Is.EqualTo((int)(769 * 0.5)), "linear, as the WPF editor computes it");
                Studio.Screenshot(s.Window, "pattern-editor-interpolated");

                // Undo (the editor's own stack, as on Windows) takes the interpolation back, then the second digit.
                Key(s, PhysicalKey.Z, RawInputModifiers.Control);
                Assert.That(Events(s.Pattern, "Uni detune"), Is.EqualTo(new[] { (0, 0), (8 * Row, 0x300) }), "undo");
                Key(s, PhysicalKey.Z, RawInputModifiers.Control);
                Assert.That(Events(s.Pattern, "Uni detune"), Is.EqualTo(new[] { (0, 0) }), "undo again");
                Key(s, PhysicalKey.Y, RawInputModifiers.Control);
                Assert.That(Events(s.Pattern, "Uni detune"), Is.EqualTo(new[] { (0, 0), (8 * Row, 0x300) }), "redo");
                // The page's hooks for the shell's Edit menu reach the same stack.
                var page = PatternView.Page;
                Assert.That(page.CanUndo, Is.True);
                page.Undo();
                Assert.That(Events(s.Pattern, "Uni detune"), Is.EqualTo(new[] { (0, 0) }), "the page's undo");
                page.Redo();
                Assert.That(Events(s.Pattern, "Uni detune"), Is.EqualTo(new[] { (0, 0), (8 * Row, 0x300) }), "the page's redo");
                Assert.That(core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(songActions), "the song's own undo stack isn't touched");
            }
            finally
            {
                Close(s);
            }
        }

        [AvaloniaTest]
        public void ThePatternPlaysThroughTheEngineAndTheCursorFollows()
        {
            var s = Open("PE Play");
            var core = Studio.Host.Core;
            var song = core.SongCore;
            ISequence seq = null;
            try
            {
                TypeChord(s);
                Key(s, PhysicalKey.Tab, RawInputModifiers.Shift);
                Key(s, PhysicalKey.Home);
                Keys(s, PhysicalKey.ArrowDown, PhysicalKey.ArrowDown, PhysicalKey.ArrowRight, PhysicalKey.Digit1);
                Assert.That(Events(s.Pattern, "Voice mode"), Is.EqualTo(new[] { (2 * Row, 1) }));
                var voiceMode = s.Machine.ParameterGroups[1].Parameters.First(p => p.Name == "Voice mode");
                Assert.That(voiceMode.GetValue(0), Is.EqualTo(0));

                var master = song.Machines.First(m => m.DLL.Info.Type == MachineType.Master);
                if (!s.Machine.Outputs.Any(c => c.Destination == master))
                    song.ConnectMachines(s.Machine, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                song.AddSequence(s.Machine, song.Sequences.Count);
                seq = song.Sequences.Last();
                seq.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, s.Pattern));

                var (peak, played) = Render(core, untilTick: 8);
                Assert.That(played, Is.GreaterThanOrEqualTo(8));
                // The play cursor: the pattern plays at tick 8 now (row 8).
                Grid(s).TimerUpdate();
                Pump();
                Studio.Screenshot(s.Window, "pattern-editor-playing");
                var (peak2, _) = Render(core, untilTick: 16, continuing: true);
                Assert.That(Math.Max(peak, peak2), Is.GreaterThan(0.01f), "the notes sound (Neo Poly through the Master)");
                Assert.That(voiceMode.GetValue(0), Is.EqualTo(1), "the automation reached the machine");
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) core.Playing = false;
                if (seq != null) song.RemoveSequence(seq);
                Close(s);
            }
        }

        /// <summary>
        /// Renders the song (the agent's offline render: a WorkManager on this thread, under the
        /// audio lock) from tick 0 until the song reaches a tick; returns the peak and the tick reached.
        /// </summary>
        static (float Peak, int Tick) Render(ReBuzzCore core, int untilTick, bool continuing = false)
        {
            var song = core.SongCore;
            var buffer = new float[512];
            float peak = 0;
            lock (ReBuzzCore.AudioLock)
            {
                var wm = new WorkManager(core, new WorkThreadEngine(1), 0, core.EngineSettings);
                if (!continuing)
                {
                    core.Playing = false;
                    core.Looping = false;
                    song.LoopStart = 0;
                    song.LoopEnd = 16;
                    song.SongEnd = 16;
                    song.PlayPosition = 0;
                    ReBuzzCore.masterInfo.PosInTick = 0;
                    var sub = ReBuzzCore.subTickInfo;
                    sub.PosInSubTick = 0;
                    sub.CurrentSubTick = 0;
                    sub.SubTickReminderCounter = 0;
                    core.Playing = true;
                    var info = ReBuzzCore.masterInfo;
                    for (int guard = 0; !core.Playing && guard < 100000; guard++)
                    {
                        int pos = info.PosInTick;
                        int n = pos == 0 ? 1 : Math.Clamp(info.SamplesPerTick - pos, 1, 256);
                        wm.MainAudioFillBuffer(buffer, 0, n * 2);
                    }
                }
                Assert.That(core.Playing, Is.True, "playing");
                for (int blocks = 0; blocks < 20000 && core.Playing && song.PlayPosition < untilTick; blocks++)
                {
                    wm.MainAudioFillBuffer(buffer, 0, 512);
                    foreach (var x in buffer) peak = Math.Max(peak, Math.Abs(x));
                }
            }
            return (peak, song.PlayPosition);
        }

        [AvaloniaTest]
        public void TheChoosersAndTheApiPickWhatTheTabEdits()
        {
            var s = Open("PE Choose");
            var core = Studio.Host.Core;
            try
            {
                var page = PatternView.Page;
                // A second pattern, made by the editor's Ctrl+Return (as on Windows), is shown.
                Key(s, PhysicalKey.Enter, RawInputModifiers.Control);
                var second = s.Machine.Patterns.FirstOrDefault(p => p.Name == "01");
                Assert.That(second, Is.Not.Null, "Ctrl+Return made pattern 01");
                Assert.That(page.Pattern, Is.EqualTo(second), "and the tab shows it");

                // The API other views use: another tab in front, then Edit(pattern) brings the Pattern tab back.
                var tabs = s.Window.Tabs;
                var patternTab = tabs.Items.OfType<TabItem>().First(t => t.Header as string == "Pattern");
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => t != patternTab);
                Pump();
                PatternView.Edit(s.Pattern);
                Pump();
                Assert.That(page.Pattern, Is.EqualTo(s.Pattern));
                Assert.That(tabs.SelectedItem, Is.EqualTo(patternTab), "the Pattern tab is in front");

                // As the Session View opens a double-clicked clip: the engine's pattern editor pattern,
                // then ActiveView. The tab follows both.
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => t != patternTab);
                Pump();
                core.SetPatternEditorPattern(second);
                core.ActiveView = BuzzView.PatternView;
                Pump();
                Assert.That(page.Pattern, Is.EqualTo(second), "SetPatternEditorPattern picks the pattern");
                Assert.That(tabs.SelectedItem, Is.EqualTo(patternTab), "ActiveView = PatternView brings the tab to the front");
                PatternView.Edit(s.Pattern);
                Pump();
                Assert.That(core.PatternEditorPattern, Is.EqualTo(s.Pattern), "the engine's pattern editor pattern");

                // Another machine, without patterns: the tab says so and offers New pattern.
                core.SongCore.CreateMachine("Neo Wave", null, "PE Empty", null, null, null, -1, 0.5f, 0.5f);
                var other = core.SongCore.MachinesList.First(m => m.Name == "PE Empty");
                PatternView.Edit(other);
                Pump();
                Assert.That(page.Machine, Is.EqualTo(other));
                Assert.That(page.EditorControl, Is.Null, "no editor yet: the machine has no patterns");
                Studio.Screenshot(s.Window, "pattern-editor-no-patterns");
                var button = page.GetLogicalDescendants().OfType<Button>().First(b => b.Content as string == "New pattern");
                button.Command?.Execute(null);
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Pump();
                Assert.That(other.Patterns.Count, Is.EqualTo(1), "New pattern made one");
                Assert.That(page.EditorControl, Is.Not.Null, "and its editor is shown");
                Assert.That(page.Pattern, Is.EqualTo(other.Patterns[0]));
                // The shell's Edit menu: the new editor has nothing to undo, so the song's history takes Undo.
                IStudioEditTarget target = page;
                Assert.That(target.Undo(), Is.False, "nothing to undo in this editor");
                Assert.That(target.Copy(), Is.False, "nothing selected to copy");
                core.SongCore.DeleteMachines(new[] { (MachineCore)other });
                Pump();
            }
            finally
            {
                Close(s);
            }
        }

        [AvaloniaTest]
        public void TheHeadersStayOverTheirColumnsInView()
        {
            // Gallery mac-051 cut "Closed hat tune" at the left, mac-052 the set label "Pad / Track" at the right.
            var s = Open("PE Headers");
            try
            {
                var grid = Grid(s);
                Pump();
                double view = (double)grid.HeaderWidth;
                int wide = -1;
                for (int i = 0; i < (int)grid.ColumnSetCount && wide < 0; i++)
                {
                    var span = ((double Left, double Right))grid.SetSpan(i);
                    if (span.Right - span.Left > view + 300) wide = i;
                }
                Assert.That(wide, Is.GreaterThanOrEqualTo(0), "Neo Poly's parameters make a set wider than the view");

                // Scrolled into the middle of that set: its label is over the part in view.
                var start = ((double Left, double Right))grid.SetSpan(wide);
                grid.ScrollSideways(start.Left + 150);
                Pump();
                var label = (Rect)grid.SetLabelBounds(wide);
                Assert.That(label.Left, Is.GreaterThanOrEqualTo(0), "the set label starts in view");
                Assert.That(label.Right, Is.LessThanOrEqualTo(view), "and ends in view");
                Assert.That(label.Center.X, Is.EqualTo(view / 2).Within(view / 4), "centred over the part in view");

                // A column half scrolled out at the left: its name moves into the part in view.
                int column = 3;
                double cw = (double)grid.ColumnWidth(wide, column);
                double contentX = (double)grid.ColumnX(wide, column) + (double)grid.SidewaysOffset;
                grid.ScrollSideways(contentX + cw * 0.4);                 // its left 40% out of view
                Pump();
                double cx = (double)grid.ColumnX(wide, column);
                Assert.That(cx, Is.LessThan(0), "the column starts out of view");
                var name = (Rect)grid.ColumnLabelBounds(wide, column);
                Assert.That(name.Left, Is.GreaterThanOrEqualTo(0), "its name is in view");
                Assert.That(name.Right, Is.LessThanOrEqualTo(cx + cw + 0.01), "inside its column");
                Studio.Screenshot(s.Window, "pattern-editor-headers-scrolled");
            }
            finally
            {
                Close(s);
            }
        }

        [AvaloniaTest]
        public void TheMouseMovesTheCursorAndSelects()
        {
            var s = Open("PE Mouse");
            try
            {
                var grid = Grid(s);
                var body = Body(s);
                // Track 0's Note column is the second column set's first column: row 5.
                Point at = grid.FieldCenter(1, 0, 1, 1);
                grid.MakeVisible(new Rect(at.X - 200, at.Y, 400, 1));      // well inside the view, clear of the scroll bar
                Pump();
                var p = body.TranslatePoint(at, s.Window).Value;
                s.Window.MouseDown(p, MouseButton.Left);
                s.Window.MouseUp(p, MouseButton.Left);
                Pump();
                dynamic cursor = grid.Cursor;
                Assert.That(((int)cursor.ColumnSet, (int)cursor.Column, (int)cursor.Beat, (int)cursor.RowInBeat), Is.EqualTo((1, 0, 1, 1)), "a click puts the cursor there");

                // Drag down three rows: a selection.
                Point qAt = grid.FieldCenter(1, 1, 2, 0);
                var q = body.TranslatePoint(qAt, s.Window).Value;
                s.Window.MouseDown(p, MouseButton.Left);
                s.Window.MouseMove(q);
                s.Window.MouseUp(q, MouseButton.Left);
                Pump();
                var selection = ((dynamic)s.EditorView).Editor.Control.CurrentPattern.Selection;
                Assert.That((bool)selection.Active, Is.True, "dragging selects");
                Studio.Screenshot(s.Window, "pattern-editor-selection");
            }
            finally
            {
                Close(s);
            }
        }

        [AvaloniaTest]
        public void PatternPropertiesAddMidiLanesAndRowsPerBeat()
        {
            var s = Open("PE Props");
            try
            {
                dynamic editor = ((dynamic)s.EditorView).Editor;
                var lanes = ((System.Collections.Generic.IEnumerable<IParameter>)editor.ColumnChoices(s.Machine)).ToList();
                var midiNote = lanes.First(p => p.Name == "MIDI Note");
                // Two of the MIDI lanes (the editor's own, listed first), the track's columns and Volume.
                var chosen = lanes.Where(p => p.Name.StartsWith("MIDI ") ? p.Name == "MIDI Note" || p.Name == "MIDI Velocity"
                    : p.Group.Type == ParameterGroupType.Track || p.Name == "Volume").ToList();
                editor.ApplyPatternProperties(8, 8, "verse", chosen);
                Pump();
                Assert.That(s.Pattern.Name, Is.EqualTo("verse"), "renamed");
                Assert.That(s.Pattern.Length, Is.EqualTo(32), "8 beats");
                // Notes typed in the MIDI Note lane, at 8 rows a beat.
                Key(s, PhysicalKey.Tab);
                Keys(s, PhysicalKey.Z, PhysicalKey.X);
                var events = s.Pattern.PatternEditorMachineEvents.First(c => c.Parameter.Name == "MIDI Note").Events.Select(e => (e.Time, e.Value)).ToArray();
                Assert.That(events, Is.EqualTo(new[] { (0, BuzzNote.Parse("C-4")), (PatternEvent.TimeBase / 2, BuzzNote.Parse("D-4")) }), "half a tick a row");
                var columns = s.Pattern.PatternEditorMachineEvents.Select(c => c.Parameter.Name).ToList();
                Assert.That(columns, Does.Contain("MIDI Note").And.Contain("MIDI Velocity").And.Contain("Note"), "the MIDI lanes beside the track's own columns");
                Assert.That(columns, Does.Not.Contain("MIDI Pitch Wheel"), "only the lanes chosen");
                Studio.Screenshot(s.Window, "pattern-editor-midi-lanes");
            }
            finally
            {
                Close(s);
            }
        }

        [AvaloniaTest]
        public void ASongsEditorDataSavesAndOpensUnchanged()
        {
            var s = Open("PE Song");
            var core = Studio.Host.Core;
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rebuzz-studio-pe-" + System.Environment.ProcessId);
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                TypeChord(s);
                byte[] before = core.MachineManager.GetMachineData(s.Machine.EditorMachine);
                SongFiles.Picker = save => null;
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                foreach (var ext in new[] { ".bmxml", ".bmx" })
                {
                    string path = System.IO.Path.Combine(dir, "editor data" + ext);
                    SongFiles.Picker = save => path;
                    Assert.That(SongFiles.SaveAs().Result, Is.True);
                    Assert.That(SongFiles.Open(path).Result, Is.True);
                    Pump();
                    var machine = core.SongCore.MachinesList.First(m => m.Name == "PE Song");
                    Assert.That(machine.EditorMachine, Is.Not.Null, ext + ": the machine has its editor again");
                    Assert.That(core.MachineManager.GetMachineData(machine.EditorMachine), Is.EqualTo(before), ext + ": the editor's data, byte for byte");
                    Assert.That(Events(machine.Patterns.First(), "Note").Length, Is.EqualTo(4), ext + ": the notes");
                    s.Machine = machine;
                    s.Pattern = machine.Patterns.First();
                }
                PatternView.Edit(s.Pattern);
                Pump();
                Studio.Screenshot(s.Window, "pattern-editor-reopened");
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
        public void TheGridDrawsQuickly()
        {
            var s = Open("PE Speed");
            try
            {
                var window = s.Window;
                var body = Body(s);
                var times = new System.Collections.Generic.List<double>();
                for (int i = 0; i < 10; i++)
                {
                    body.InvalidateVisual();
                    window.CaptureRenderedFrame();
                    times.Add(((TimeSpan)Grid(s).LastRenderTime).TotalMilliseconds);
                }
                times.Sort();
                TestContext.Out.WriteLine($"Neo Poly's grid ({s.Machine.ParameterGroups[1].Parameters.Count} globals): median {times[5]:F1} ms to draw the beats in view");
                Assert.That(times[5], Is.LessThan(100), "a frame of beats draws in well under 100 ms");
            }
            finally
            {
                Close(s);
            }
        }

        [AvaloniaTest]
        public void TheToolbarsNumberBoxesShowTheirLargestValueWhole()
        {
            // Fork (ui-gallery.md, bug 6): Beats was a fixed 96-point box whose spinner left room for one digit and a half,
            // so a 32-beat pattern showed "3:" (mac-053). Each box is as wide as its largest value in its own font.
            var s = Open("PE Boxes");
            try
            {
                s.Window.UpdateLayout();
                var boxes = s.EditorView.GetVisualDescendants().OfType<NumericUpDown>().Where(b => b.IsEffectivelyVisible).ToList();      // the keyboard mapping's are hidden
                Assert.That(boxes.Select(b => ToolTip.GetTip(b) as string), Does.Contain("Beats").And.Contain("Tracks"));
                foreach (var box in boxes)
                {
                    var presenter = box.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().First();
                    var typeface = new Avalonia.Media.Typeface(presenter.FontFamily, presenter.FontStyle, presenter.FontWeight);
                    string largest = ((int)box.Maximum).ToString();
                    double needed = new Avalonia.Media.TextFormatting.TextLayout(largest, typeface, presenter.FontSize, null).WidthIncludingTrailingWhitespace;
                    Assert.That(presenter.Bounds.Width, Is.GreaterThanOrEqualTo(needed),
                        $"{ToolTip.GetTip(box)}: room for \"{largest}\" ({needed:F1} pt) in its text ({presenter.Bounds.Width:F1} pt)");
                }
                var beats = boxes.First(b => ToolTip.GetTip(b) as string == "Beats");
                beats.Value = 32;               // a 32-beat pattern, as the gallery's Arp
                Pump();
                Studio.Screenshot(s.Window, "pattern-editor-beats-32");
            }
            finally
            {
                Close(s);
            }
        }

        [AvaloniaTest]
        public void PatternPropertiesListsAndDrawsTheColumns()
        {
            // Fork (ui-gallery.md, bug 6): the gallery's Pattern Properties (mac-056) showed an empty column list, and so
            // does the app: skipping the hidden pattern editors' names (U+0001 first) by the culture's StartsWith, which
            // ignores control characters, skipped every machine on macOS and Linux.
            var s = Open("PE Props List");
            Window dialog = null;
            int boxes = 0, drawn = 0;
            string notes = null;
            using var handler = Window.WindowOpenedEvent.AddClassHandler<Window>((w, e) =>
            {
                if (w is MainWindow) return;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        Pump();
                        dialog = w;
                        var checks = w.GetVisualDescendants().OfType<CheckBox>().ToList();
                        boxes = checks.Count;
                        drawn = checks.Count(c => c.IsEffectivelyVisible && c.Bounds.Height > 0 && c.TranslatePoint(new Point(0, 0), w) is { } at && at.Y >= 0 && at.Y < w.Bounds.Height);
                        notes = string.Join(", ", checks.Take(6).Select(c => $"{c.Content} {c.Bounds}"));
                        Studio.Screenshot(w, "pattern-properties-dialog");
                    }
                    finally
                    {
                        w.Close();
                    }
                }, DispatcherPriority.Background);
            }, Avalonia.Interactivity.RoutingStrategies.Direct, handledEventsToo: true);
            try
            {
                Key(s, PhysicalKey.Backspace, RawInputModifiers.Control);
                for (int i = 0; i < 20 && dialog == null; i++) Pump();
                Assert.That(dialog, Is.Not.Null, "Ctrl+Backspace opened Pattern Properties");
                TestContext.Out.WriteLine($"{boxes} boxes, {drawn} in view: {notes}");
                Assert.That(boxes, Is.GreaterThan(10), "a box per column the pattern can have");
                Assert.That(drawn, Is.GreaterThan(3), "the first of them in view, laid out");
            }
            finally
            {
                Close(s);
            }
        }

        /// <summary>
        /// The labels of the shown pattern's columns and, for each, the values its beats show (the grid's model), as
        /// "label: values" with empty rows left out.
        /// </summary>
        static System.Collections.Generic.Dictionary<string, string[]> Shown(dynamic patternVM)
        {
            var shown = new System.Collections.Generic.Dictionary<string, string[]>();
            int beats = patternVM.BeatCount;
            int set = 0;
            foreach (dynamic cs in (System.Collections.IEnumerable)patternVM.ColumnSets)
            {
                foreach (dynamic column in (System.Collections.IEnumerable)cs.Columns)
                {
                    var values = new System.Collections.Generic.List<string>();
                    for (int b = 0; b < beats; b++)
                    {
                        object beat = column.FetchBeat(b);       // a private class: its interface's rows, by reflection
                        var rows = (System.Collections.IEnumerable)beat.GetType().GetInterfaces().First(i => i.Name == "IBeat").GetProperty("Rows")!.GetValue(beat)!;
                        foreach (object row in rows)
                        {
                            string v = (string)row.GetType().GetField("ValueString")!.GetValue(row)!;
                            if (!v.All(c => c == '.')) values.Add(v);
                        }
                    }
                    shown[$"{set}/{(string)column.Label}"] = values.ToArray();
                }
                set++;
            }
            return shown;
        }

        [AvaloniaTest]
        public void NotesMovedOntoTheLanesOfAPatternNeverShownShowWhenItIs()
        {
            // Fork (ui-gallery.md, bug 5): the editor makes a view of each pattern as it's created (and shows the newest),
            // with the columns the pattern had then. Notes moved onto the MIDI note lanes of another pattern, before it
            // was ever shown, made its lanes, but its view kept its old columns: the Pattern View showed no lanes and no
            // notes until the song was saved and opened again. Both apps share this code (PatternEditor.Core.cs).
            var core = Studio.Host.Core;
            core.SongCore.CreateMachine("Neo Poly", null, "PE Unseen", null, null, null, 2, 0.3f, 0.3f);
            var machine = core.SongCore.MachinesList.First(m => m.Name == "PE Unseen");
            MainWindow window = null;
            try
            {
                machine.CreatePattern("A", 16);
                machine.CreatePattern("B", 16);                 // the newest: the one the editor shows
                var a = machine.Patterns.First(p => p.Name == "A");
                core.SongCore.AddSequence(machine, core.SongCore.SequencesList.Count);
                var track = core.SongCore.SequencesList.First(q => q.Machine == machine);
                var layout = core.SessionLayout;
                layout.Ensure();
                const int TB = PatternEvent.TimeBase;
                var notes = new[] { new ReBuzz.Core.Session.ClipNote(0, TB, 60, 100), new ReBuzz.Core.Session.ClipNote(4 * TB, TB, 64, 100),
                                    new ReBuzz.Core.Session.ClipNote(8 * TB, TB, 67, 100), new ReBuzz.Core.Session.ClipNote(12 * TB, 2 * TB, 72, 100) };
                layout.EditNotes(a, null, notes);
                Assert.That(layout.MoveNotesToPatternView(track, a), Is.Null, "the notes moved onto A's note lanes");
                Assert.That(layout.PatternViewNotesOf(a).Count, Is.EqualTo(4), "the lanes have them");

                window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
                window.Show();
                Pump();
                PatternView.Edit(a);
                Pump();
                Assert.That(PatternView.Page.Pattern, Is.EqualTo(a), "the Pattern View shows A");
                dynamic editor = ((dynamic)PatternView.Page.EditorControl).Editor;
                dynamic vm = editor.SelectedMachine.SelectedPattern;
                Assert.That((IPattern)vm.Pattern, Is.EqualTo(a));
                System.Collections.Generic.Dictionary<string, string[]> shown = Shown(vm);
                var lane = shown.Where(kv => kv.Key.EndsWith("/MIDI Note")).Select(kv => kv.Value).FirstOrDefault();
                Assert.That(lane, Is.Not.Null, "A's view has its MIDI Note lane (columns: " + string.Join(", ", shown.Keys) + ")");
                Assert.That(shown.Where(kv => kv.Key.EndsWith("/MIDI Note")).SelectMany(kv => kv.Value).Where(v => v != "off"), Is.EqualTo(new[] { "C-5", "E-5", "G-5", "C-6" }),
                    "and the notes on it (MIDI 60 is the editor's C-5; each ends in an off), as the song opened again would show them");
                Studio.Screenshot(window, "pattern-editor-unseen-lanes");
            }
            finally
            {
                core.Modified = false;
                window?.Close();
                Pump();
                core.SongCore.DeleteMachines(new[] { machine });
            }
        }
    }
}
