// Fork (docs/specs/ux-overhaul.md P3c): the pattern editor, notes first. Written after the helper the older editor tests share.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    /// <summary>Moving the pattern editor's cursor by column name (the old tests' Tab and arrow chains no longer reach the same columns).</summary>
    internal static class EditorNav
    {
        /// <summary>
        /// Puts the cursor on a column by its name, at row 0 and its first digit, showing it first when + Automation hides it
        /// (the nth column of that name, in the order the sets are shown).
        /// </summary>
        public static void GoTo(object editorView, string label, Action pump, int nth = 0)
        {
            dynamic view = editorView;
            view.ShowAutomation(label);
            pump();
            dynamic vm = view.Editor.SelectedMachine.SelectedPattern;
            int found = 0;
            for (int i = 0; i < ((System.Collections.ICollection)vm.ColumnSets).Count; i++)
                for (int j = 0; j < ((System.Collections.ICollection)vm.ColumnSets[i].Columns).Count; j++)
                    if ((string)vm.ColumnSets[i].Columns[j].Label == label && found++ == nth)
                    {
                        dynamic d = vm.CursorPosition;
                        view.Editor.Control.MoveCursor(d.SetColumnSet(i).SetColumn(j).SetBeat(0).SetRowInBeat(0).SetIndex(0));
                        pump();
                        return;
                    }
            NUnit.Framework.Assert.Fail("no column " + label);
        }
    }

    public class UxP3cTests
    {
        const int Row = PatternEvent.TimeBase;

        sealed class Setup
        {
            public MainWindow Window;
            public MachineCore Machine;
            public IPattern Pattern;
            public dynamic View;
            public dynamic Grid => View.TrackerGrid;
            public dynamic Vm => View.Editor.SelectedMachine.SelectedPattern;
        }

        static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        static Setup Open(string type, string name, int ticks = 16, bool hex = false)
        {
            ValueDisplay.Base = hex ? ValueBase.Hex : ValueBase.Decimal;
            var core = Studio.Host.Core;
            core.SongCore.CreateMachine(type, null, name, null, null, null, -1, 0.3f, 0.3f);
            var machine = core.SongCore.MachinesList.First(m => m.Name == name);
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
            Studio.Host.Core.Modified = false;
            s.Window.Close();
            Pump();
            Studio.Host.Core.SongCore.DeleteMachines(new[] { s.Machine });
        }

        static void Key(Setup s, PhysicalKey key, RawInputModifiers m = RawInputModifiers.None)
        {
            s.Window.KeyPressQwerty(key, m);
            s.Window.KeyReleaseQwerty(key, m);
            Pump();
        }

        static void GoTo(Setup s, string label) => EditorNav.GoTo((object)s.View, label, Pump);

        static string[] Labels(Setup s, int set) =>
            ((System.Collections.IEnumerable)s.Vm.ColumnSets[set].Columns).Cast<dynamic>().Select(c => (string)c.Label).ToArray();

        static string[] AllLabels(Setup s) =>
            ((System.Collections.IEnumerable)s.Vm.ColumnSets).Cast<dynamic>().SelectMany(cs => ((System.Collections.IEnumerable)cs.Columns).Cast<dynamic>().Select(c => (string)c.Label)).Cast<string>().ToArray();

        /// <summary>The texts the first column of a label shows on its filled rows.</summary>
        static string[] Texts(Setup s, string label)
        {
            int beats = s.Vm.BeatCount;
            foreach (dynamic cs in (System.Collections.IEnumerable)s.Vm.ColumnSets)
                foreach (dynamic column in (System.Collections.IEnumerable)cs.Columns)
                {
                    if ((string)column.Label != label) continue;
                    var values = new System.Collections.Generic.List<string>();
                    for (int b = 0; b < beats; b++)
                    {
                        object beat = column.FetchBeat(b);
                        var rows = (System.Collections.IEnumerable)beat.GetType().GetInterfaces().First(i => i.Name == "IBeat").GetProperty("Rows")!.GetValue(beat)!;
                        foreach (object row in rows)
                        {
                            string v = (string)row.GetType().GetField("ValueString")!.GetValue(row)!;
                            if (!v.All(c => c == '.')) values.Add(v);
                        }
                    }
                    return values.ToArray();
                }
            return null;
        }

        static (int Time, int Value)[] Events(IPattern pattern, string parameter) =>
            pattern.PatternEditorMachineEvents.First(c => c.Parameter.Name == parameter).Events.Select(e => (e.Time, e.Value)).OrderBy(e => e.Time).ToArray();

        [AvaloniaTest]
        public void NoteAndVelocityColumnsComeFirstAndTheGlobalsAfterThem()
        {
            var s = Open("Neo Poly", "P3c Order");
            try
            {
                Assert.That(Labels(s, 0).Take(2), Is.EqualTo(new[] { "Note", "Velocity" }), "track 0: the note, then its velocity");
                Assert.That((string)s.Vm.ColumnSets[0].Label, Does.Contain("Track"), "the first set is a track's");
                Assert.That(Labels(s, 0).Length, Is.EqualTo(2), "nothing else while the pattern holds nothing");
                var cursor = s.Grid.Cursor;
                Assert.That(((int)cursor.ColumnSet, (int)cursor.Column), Is.EqualTo((0, 0)), "the cursor starts on the Note column");

                s.View.ShowAllAutomation();
                Pump();
                int last = ((System.Collections.ICollection)s.Vm.ColumnSets).Count - 1;
                Assert.That((string)s.Vm.ColumnSets[last].Label, Does.Contain("Global"), "the globals come after the tracks");
                Assert.That(Labels(s, 0).Take(2), Is.EqualTo(new[] { "Note", "Velocity" }));
            }
            finally { Close(s); }
        }

        [AvaloniaTest]
        public void EmptyParameterColumnsWaitBehindAutomationAndColumnsWithDataAlwaysShow()
        {
            var s = Open("Neo Poly", "P3c Auto");
            try
            {
                Assert.That(AllLabels(s), Does.Not.Contain("Uni detune"), "an empty parameter has no column");
                Assert.That((System.Collections.Generic.IEnumerable<string>)s.View.AutomationChoices, Does.Contain("Uni detune"), "+ Automation offers it");
                Assert.That((bool)s.View.AutomationButton.IsEnabled, Is.True);
                Assert.That((string)s.View.AutomationButton.Content, Is.EqualTo("+ Automation"));

                // Typing data into it (revealed first): it stays shown, and the cursor stays on the column it was on.
                Assert.That((bool)s.View.ShowAutomation("Uni detune"), Is.True);
                Pump();
                Assert.That(AllLabels(s), Does.Contain("Uni detune"));
                Assert.That((System.Collections.Generic.IEnumerable<string>)s.View.AutomationChoices, Does.Not.Contain("Uni detune"));

                // Data in it, saved and opened again (a new editor, nothing asked for): it shows, the others still wait.
                GoTo(s, "Uni detune");
                Key(s, PhysicalKey.Digit3);
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rebuzz-studio-p3c-" + Environment.ProcessId);
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "auto.bmxml");
                try
                {
                    SongFiles.Picker = save => path;
                    Dialogs.Responder = (m, c, b) => UserAnswer.No;
                    Assert.That(SongFiles.SaveAs().Result, Is.True);
                    Assert.That(SongFiles.Open(path).Result, Is.True);
                    Pump();
                    s.Machine = Studio.Host.Core.SongCore.MachinesList.First(m => m.Name == "P3c Auto");
                    s.Pattern = s.Machine.Patterns.First();
                    PatternView.Edit(s.Pattern);
                    Pump();
                    s.View = PatternView.Page.EditorControl;
                    Assert.That(AllLabels(s), Does.Contain("Uni detune"), "a column with data shows");
                    Assert.That(AllLabels(s), Does.Not.Contain("Glide"), "an empty one doesn't");
                }
                finally
                {
                    SongFiles.Picker = null;
                    Dialogs.Responder = null;
                }
            }
            finally { Close(s); }
        }

        [AvaloniaTest]
        public void TheHeadersAreHorizontalOnTwoLinesAtElevenPoints()
        {
            var s = Open("Neo Poly", "P3c Headers");
            try
            {
                s.View.ShowAllAutomation();
                Pump();
                var grid = s.Grid;
                int set = ((System.Collections.ICollection)s.Vm.ColumnSets).Count - 1;
                int count = ((System.Collections.ICollection)s.Vm.ColumnSets[set].Columns).Count;
                int two = 0;
                for (int c = 0; c < count; c++)
                {
                    var r = (Rect)grid.ColumnLabelBounds(set, c);
                    double cw = (double)grid.ColumnWidth(set, c);
                    Assert.That(r.Width, Is.LessThanOrEqualTo(cw), Labels(s, set)[c] + ": inside its column");
                    Assert.That(r.Height, Is.LessThanOrEqualTo(32.0), "two lines of 11 pt at most (the header is 32 pt)");
                    Assert.That(r.Height, Is.GreaterThan(11.0));
                    if (r.Height > 20) two++;
                }
                Assert.That(two, Is.GreaterThan(0), "names with a space take two lines");
                var colours = ((Avalonia.Media.Color Label, Avalonia.Media.Color Background, Avalonia.Media.Color BeatRule, Avalonia.Media.Color BarRule))ReBuzz.Studio.Tests.UxP3cTests.Colours(s);
                Assert.That(StudioTokens.Contrast(colours.Label, colours.Background), Is.GreaterThanOrEqualTo(4.5), "header text");
                Studio.Screenshot(s.Window, "pattern-editor-headers-p3c");
            }
            finally { Close(s); }
        }

        static object Colours(Setup s) => ((object)s.Grid).GetType().GetProperty("ThemeColours")!.GetValue(null)!;

        static uint[] Frame(Setup s, out int width)
        {
            var frame = (WriteableBitmap)s.Window.CaptureRenderedFrame();
            width = frame.PixelSize.Width;
            using var fb = frame.Lock();
            var data = new byte[fb.RowBytes * fb.Size.Height];
            System.Runtime.InteropServices.Marshal.Copy(fb.Address, data, 0, data.Length);
            var px = new uint[width * fb.Size.Height];
            for (int y = 0; y < fb.Size.Height; y++)
                for (int x = 0; x < width; x++)
                    px[y * width + x] = BitConverter.ToUInt32(data, y * fb.RowBytes + x * 4);
            return px;
        }

        static Avalonia.Media.Color At(uint[] px, int width, int x, int y)
        {
            uint v = px[y * width + x];
            return Avalonia.Media.Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);       // BGRA
        }

        [AvaloniaTest]
        public void BeatAndBarRulesAreAtLeastThreeToOneAgainstTheGrid()
        {
            var s = Open("Neo Poly", "P3c Rules", ticks: 32);
            try
            {
                var grid = s.Grid;
                grid.ScrollBeatsTo(0.0);
                Pump();
                var body = (Control)grid.Body;
                double x0 = (double)grid.ColumnX(0, 0) + (double)grid.SidewaysOffset + 1;
                var px = Frame(s, out int w);
                Avalonia.Media.Color Rule(int beat, double dy, int below)
                {
                    var top = body.TranslatePoint(new Point(x0, (double)grid.BeatTop(beat)), s.Window)!.Value;
                    return At(px, w, (int)top.X, (int)top.Y + (int)dy);
                }
                int bar = (int)Math.Round((double)grid.BarBeats);
                var back = Rule(1, 6, 0);
                var beatRule = Rule(1, 0, 0);
                Assert.That(StudioTokens.Contrast(beatRule, back), Is.GreaterThanOrEqualTo(3.0), "a beat rule");
                var barRule = Rule(bar, 0, 0);
                var barBack = Rule(bar, 6, 0);
                Assert.That(StudioTokens.Contrast(barRule, barBack), Is.GreaterThanOrEqualTo(3.0), "a bar rule");
                Assert.That(StudioTokens.Contrast(barRule, barBack), Is.GreaterThan(StudioTokens.Contrast(beatRule, back)), "the bar's is the stronger");
                Studio.Screenshot(s.Window, "pattern-editor-rules");
            }
            finally { Close(s); }
        }

        [AvaloniaTest]
        public void ADrumMachineNamesItsNotesAndOthersShowPitches()
        {
            var s = Open("Neo Drums", "P3c Drums");
            try
            {
                s.Machine.BaseOctave = 3;
                Key(s, PhysicalKey.Z);      // MIDI 36: the kick
                Key(s, PhysicalKey.X);      // 38: the snare
                Pump();
                Assert.That(Texts(s, "Note"), Is.EqualTo(new[] { "BD", "SD" }), "the machine's names, three characters at most");
                Assert.That(Events(s.Pattern, "Note").Select(e => BuzzNote.ToMIDINote(e.Value)), Is.EqualTo(new[] { 36, 38 }), "the pattern holds the pitches");
            }
            finally { Close(s); }

            var p = Open("Neo Poly", "P3c Poly");
            try
            {
                Key(p, PhysicalKey.Z);
                Assert.That(Texts(p, "Note").Single(), Does.Match("^[A-G][-#]\\d$"), "a pitched machine shows pitches");
            }
            finally { Close(p); }
        }

        [AvaloniaTest]
        public void ValuesAreDecimalByDefaultAndHexOnRequestAndTypedAccordingly()
        {
            var s = Open("Neo Poly", "P3c Values");
            try
            {
                GoTo(s, "Uni detune");               // 0-1000: four decimal digits
                Key(s, PhysicalKey.Digit1);          // the first digit: 1000; the cursor goes down a row
                Key(s, PhysicalKey.Digit5);          // 5000 is past the largest: 1000
                Key(s, PhysicalKey.ArrowRight);
                Key(s, PhysicalKey.A);               // not a decimal digit: ignored
                Key(s, PhysicalKey.Digit7);          // the second digit of row 2: 0700
                Assert.That(Events(s.Pattern, "Uni detune"), Is.EqualTo(new[] { (0, 1000), (Row, 1000), (2 * Row, 700) }));
                Assert.That(Texts(s, "Uni detune"), Is.EqualTo(new[] { "1000", "1000", "0700" }), "shown in decimal");

                s.View.HexToggle.IsChecked = true;
                Pump(); Pump();
                Assert.That(ValueDisplay.IsHex, Is.True, "the toggle is the setting");
                Assert.That(Texts(s, "Uni detune"), Is.EqualTo(new[] { "03E8", "03E8", "02BC" }), "the same numbers in hex");
                GoTo(s, "Uni detune");
                for (int i = 0; i < 3; i++) Key(s, PhysicalKey.ArrowDown);
                Key(s, PhysicalKey.A);               // hex digits typed as before
                Assert.That(Events(s.Pattern, "Uni detune").Last(), Is.EqualTo((3 * Row, 0xA000 > 1000 ? 1000 : 0xA000)), "typed in hex, clamped to the largest");
                s.View.HexToggle.IsChecked = false;
                Pump(); Pump();
                Assert.That(ValueDisplay.IsHex, Is.False);
            }
            finally { Close(s); }
        }

        [AvaloniaTest]
        public void TheCursorRowStaysCentredWhileMoving()
        {
            var s = Open("Neo Poly", "P3c Centre", ticks: 256);
            try
            {
                var grid = s.Grid;
                for (int i = 0; i < 4; i++) Pump();
                double line = 16;
                Key(s, PhysicalKey.ArrowDown);
                Assert.That((double)grid.CursorY, Is.EqualTo((double)grid.CenterY).Within(line), "row 1: scrollY " + grid.ScrollY + " pad " + grid.Pad + " raw " + grid.RawY);
                Key(s, PhysicalKey.ArrowUp);
                Assert.That((double)grid.CursorY, Is.EqualTo((double)grid.CenterY).Within(line), "row 0 is in the middle: scrollY " + grid.ScrollY + " view " + grid.ViewHeight + " pad " + grid.Pad + " raw " + grid.RawY + " centre " + s.View.CentreToggle.IsChecked);
                for (int i = 0; i < 20; i++) Key(s, PhysicalKey.ArrowDown);
                Assert.That((double)grid.CursorY, Is.EqualTo((double)grid.CenterY).Within(line), "and row 20");
                Key(s, PhysicalKey.End);
                Key(s, PhysicalKey.PageDown);
                Pump();
                Assert.That((double)grid.CursorY, Is.EqualTo((double)grid.CenterY).Within(line), "after a page down");

                s.View.CentreToggle.IsChecked = false;
                Pump(); Pump();
                grid.ScrollBeatsTo(0.0);
                Key(s, PhysicalKey.Home);
                Key(s, PhysicalKey.PageUp);
                Pump();
                double before = grid.ScrollY;
                for (int i = 0; i < 3; i++) Key(s, PhysicalKey.ArrowDown);
                Assert.That((double)grid.ScrollY, Is.EqualTo(before), "with Centre off a cursor in view doesn't scroll it");
            }
            finally
            {
                s.View.CentreToggle.IsChecked = true;
                Pump();
                Close(s);
            }
        }

        [AvaloniaTest]
        public void OnlyViewingAPatternLeavesItsSavedDataByteEqual()
        {
            var s = Open("Neo Poly", "P3c Save", hex: true);
            try
            {
                Key(s, PhysicalKey.Z); Key(s, PhysicalKey.C); Key(s, PhysicalKey.B);
                var core = Studio.Host.Core;
                byte[] before = core.MachineManager.GetMachineData(s.Machine.EditorMachine);
                var notes = Events(s.Pattern, "Note");
                // Looking: every column, the other base, scrolling, the cursor about, the roll's page.
                s.View.ShowAllAutomation();
                s.View.HexToggle.IsChecked = false;
                s.View.CentreToggle.IsChecked = false;
                s.View.CentreToggle.IsChecked = true;
                Pump();
                GoTo(s, "Uni detune");
                s.Grid.ScrollBeatsTo(40.0);
                s.Grid.ScrollSideways(30.0);
                foreach (var k in new[] { PhysicalKey.ArrowDown, PhysicalKey.ArrowRight, PhysicalKey.PageDown, PhysicalKey.Home, PhysicalKey.End })
                    Key(s, k);
                Pump();
                Assert.That(core.MachineManager.GetMachineData(s.Machine.EditorMachine), Is.EqualTo(before), "the editor's data, byte for byte");
                Assert.That(Events(s.Pattern, "Note"), Is.EqualTo(notes));
                Assert.That(s.Pattern.PatternEditorMachineEvents.Where(c => c.Parameter.Name != "Note").Sum(c => c.Events.Count()), Is.EqualTo(0), "no event was made by looking");
            }
            finally { Close(s); }
        }
    }
}
