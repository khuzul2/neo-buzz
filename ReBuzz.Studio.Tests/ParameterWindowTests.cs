// Fork (docs/specs/studio-mvp.md S2): the generic parameter window, headless: a Neo machine's and
// every Pedal machine's (screenshots), a slider drag and its undo, track parameters, presets, the
// value editor, the right-click menu (MIDI bind, copy and paste of the values) and edit mode.

using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Parameters;
using ReBuzz.Studio.Platform;

namespace ReBuzz.Studio.Tests
{
    public class ParameterWindowTests
    {
        /// <summary>The 19 Pedal machines (ExternalMachines/source/thepedal), built for net10.0.</summary>
        public static readonly string[] Pedal =
        {
            "Pedal Chord", "Pedal Chorus", "Pedal Comp", "Pedal Drum Matrix", "Pedal EQ", "Pedal Filter", "Pedal FM",
            "Pedal Gain", "Pedal Gain Multi", "Pedal Gate", "Pedal Juno106", "Pedal LFO", "Pedal M1", "Pedal MComp",
            "Pedal Peer Control", "Pedal Plate", "Pedal SH101", "Pedal Shaper", "Pedal Tracker",
        };

        static ReBuzzCore Core => Studio.Host.Core;

        static MachineCore Add(string gear, string name)
        {
            Core.SongCore.CreateMachine(gear, null, name, null, null, null, -1, 0.3f, 0.3f);
            var machine = Core.SongCore.MachinesList.FirstOrDefault(m => m.Name == name);
            Assert.That(machine, Is.Not.Null, $"{gear} loads");
            return machine!;
        }

        static (Window Window, ParameterWindowView View) Open(MachineCore machine)
        {
            // The parameter window itself: a Neo machine's GUI is its editor (S1); a Pedal machine,
            // with no GUI here, would open this window from OpenGui too.
            Core.UiHost.OpenParameterWindow(machine, null);
            var window = StudioMachineUiHost.ParameterWindowOf(machine);
            Assert.That(window, Is.Not.Null);
            Assert.That(window!.Content, Is.InstanceOf<ParameterWindowView>());
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return (window, (ParameterWindowView)window.Content!);
        }

        static void Remove(MachineCore machine)
        {
            Core.UiHost.ReleaseMachine(machine);
            Core.SongCore.DeleteMachines(new[] { machine });
            Dispatcher.UIThread.RunJobs();
        }

        static ParameterRow Row(ParameterWindowView view, string name) => view.Rows.Single(r => r.ParameterName == name);

        /// <summary>Every state parameter of every group and track, named and described as the machine does.</summary>
        static void AssertShowsItsParameters(MachineCore machine, ParameterWindowView view)
        {
            var expected = machine.AllParametersAndTracks().Where(x => x.Item1.Flags.HasFlag(ParameterFlags.State)).ToList();
            Assert.That(view.Rows.Select(r => (r.Parameter, r.Track)), Is.EqualTo(expected.Select(x => (x.Item1, x.Item2))), $"{machine.Name}: the rows");
            foreach (var r in view.Rows)
            {
                Assert.That(r.ValueText, Is.EqualTo(r.Parameter.DescribeValue(r.Parameter.GetValue(r.Track))), $"{r.ParameterName}: the machine's description");
                // The slider keeps to the range (as WPF's coerces); some machines start outside it.
                Assert.That(r.Slider.Value, Is.EqualTo(System.Math.Clamp(r.Parameter.GetValue(r.Track), r.Parameter.MinValue, r.Parameter.MaxValue)));
                Assert.That(r.Slider.Minimum, Is.EqualTo(r.Parameter.MinValue));
                Assert.That(r.Slider.Maximum, Is.EqualTo(r.Parameter.MaxValue));
            }
        }

        [AvaloniaTest]
        public void ANeoMachineShowsItsParameters()
        {
            var machine = Add("Neo Poly", "Param Poly");
            var (window, view) = Open(machine);
            Assert.That(window.Title, Is.EqualTo("Param Poly - Neo Poly"), "the WPF window's title");
            AssertShowsItsParameters(machine, view);
            Assert.That(view.Rows.Count, Is.GreaterThan(50));
            Assert.That(view.TrackChoiceShown, Is.False, "no track parameters");
            Studio.Screenshot(window, "parameters-neo-poly");
            Remove(machine);
        }

        [AvaloniaTest]
        public void EveryPedalMachineLoadsAndShowsItsParameters()
        {
            Assert.That(Pedal.Where(n => !Core.MachineDLLs.ContainsKey(n)), Is.Empty, "the portable Pedal machines are installed");
            foreach (var gear in Pedal)
            {
                var machine = Add(gear, "Param " + gear);
                Assert.That(machine.DLL.IsManaged, Is.True);
                Assert.That(machine.DLL.GUIFactory, Is.Null, $"{gear}: no WPF GUI here");
                var (window, view) = Open(machine);
                AssertShowsItsParameters(machine, view);
                Assert.That(view.Rows, Is.Not.Empty, gear);
                Studio.Screenshot(window, "parameters-" + gear.Replace("Pedal ", "pedal-").Replace(" ", "-").ToLowerInvariant());
                Remove(machine);
            }
        }

        [AvaloniaTest]
        public void ASliderDragChangesTheValueAndUndoRestoresIt()
        {
            var machine = Add("Pedal Juno106", "Drag Juno");
            var (window, view) = Open(machine);
            var row = Row(view, "VCF Freq");
            int before = row.Parameter.GetValue(0);
            string text = row.ValueText;

            // Press on the thumb, drag 60 pixels left, let go: one undo step.
            var thumb = row.Slider.ThumbBounds.Center;
            var start = row.Slider.TranslatePoint(thumb, window)!.Value;
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(start - new Point(30, 0), RawInputModifiers.LeftMouseButton);
            window.MouseMove(start - new Point(60, 0), RawInputModifiers.LeftMouseButton);
            window.MouseUp(start - new Point(60, 0), MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            int after = row.Parameter.GetValue(0);
            Assert.That(after, Is.LessThan(before - 20), "the drag lowered the value");
            Assert.That(row.Slider.Value, Is.EqualTo(after));
            Assert.That(row.ValueText, Is.EqualTo(row.Parameter.DescribeValue(after)).And.Not.EqualTo(text));
            Studio.Screenshot(window, "parameters-drag");

            Assert.That(view.UndoButton.IsEnabled, Is.True);
            view.UndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(row.Parameter.GetValue(0), Is.EqualTo(before), "undo restores it");
            Assert.That(row.Slider.Value, Is.EqualTo(before));
            Assert.That(row.ValueText, Is.EqualTo(text));
            Core.SongCore.ActionStack.Redo();       // the song's stack: Edit → Redo too
            view.Refresh();
            Assert.That(row.Parameter.GetValue(0), Is.EqualTo(after), "redo");

            // The wheel steps by one, and steps that follow each other are one undo step.
            Core.SongCore.ActionStack.Undo();
            view.Refresh();
            var at = row.Slider.TranslatePoint(new Point(5, 5), window)!.Value;
            window.MouseWheel(at, new Vector(0, 1));
            window.MouseWheel(at, new Vector(0, 1));
            window.MouseWheel(at, new Vector(0, 1));
            Assert.That(row.Parameter.GetValue(0), Is.EqualTo(before + 3));
            view.Undo();
            Assert.That(row.Parameter.GetValue(0), Is.EqualTo(before), "three wheel steps, one undo");
            Remove(machine);
        }

        [AvaloniaTest]
        public void ADigitOpensTheValueEditor()
        {
            var machine = Add("Pedal Juno106", "Digit Juno");
            var (window, view) = Open(machine);
            var row = Row(view, "Env A");
            int before = row.Parameter.GetValue(0);
            try
            {
                int? asked = null;
                ParameterDialogs.ValueResponder = (start, min, max) =>
                {
                    asked = start;
                    return 77;
                };
                row.Slider.Focus();
                window.KeyPressQwerty(PhysicalKey.Digit7, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                Assert.That(asked, Is.EqualTo(7), "the editor starts from the digit typed");
                Assert.That(row.Parameter.GetValue(0), Is.EqualTo(77));
                window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                Assert.That(row.Parameter.GetValue(0), Is.EqualTo(78), "the arrow keys step by one");
                view.Undo();
                view.Undo();
                Assert.That(row.Parameter.GetValue(0), Is.EqualTo(before));
            }
            finally
            {
                ParameterDialogs.ValueResponder = null;
                Remove(machine);
            }
        }

        [AvaloniaTest]
        public void TrackParametersOfAMultiTrackMachine()
        {
            var machine = Add("Pedal Gain Multi", "Tracks");
            var (window, view) = Open(machine);
            int tracks = machine.TrackCount;
            Assert.That(tracks, Is.GreaterThan(1));
            for (int t = 0; t < tracks; t++)
                Assert.That(view.Rows.Any(r => r.ParameterName == $"{t}-Volume" && r.Track == t), $"track {t}'s Volume");
            Assert.That(view.TrackChoiceShown, Is.True);
            Assert.That(view.SelectedTrack, Is.EqualTo(-1), "every track, as the WPF window lists them");

            // One track alone: the globals and that track's rows.
            view.SelectedTrack = 2;
            Assert.That(view.ShownRows.Where(r => r.Parameter.Group.Type == ParameterGroupType.Track).Select(r => r.Track).Distinct(), Is.EqualTo(new[] { 2 }));
            Assert.That(view.ShownRows.Any(r => r.Parameter.Group.Type == ParameterGroupType.Global), Is.True);
            Studio.Screenshot(window, "parameters-track-2");

            var volume2 = Row(view, "2-Volume");
            var volume1 = Row(view, "1-Volume");
            int one = volume1.Parameter.GetValue(1), two = volume2.Parameter.GetValue(2);
            volume2.Slider.Commit(volume2.Slider.Minimum);
            Assert.That(volume2.Parameter.GetValue(2), Is.EqualTo(volume2.Slider.Minimum).And.Not.EqualTo(two), "track 2 changed");
            Assert.That(volume1.Parameter.GetValue(1), Is.EqualTo(one), "track 1 didn't");
            view.Undo();
            Assert.That(volume2.Parameter.GetValue(2), Is.EqualTo(two));

            // Add Track (an undo step): a row set more, and the choice keeps track 2.
            view.SetTrackCount(tracks + 1);
            Dispatcher.UIThread.RunJobs();
            Assert.That(machine.TrackCount, Is.EqualTo(tracks + 1));
            Assert.That(view.Rows.Any(r => r.ParameterName == $"{tracks}-Volume"), $"track {tracks}'s rows");
            Assert.That(view.SelectedTrack, Is.EqualTo(2));
            view.Undo();
            Dispatcher.UIThread.RunJobs();
            Assert.That(machine.TrackCount, Is.EqualTo(tracks), "undo removes it");
            Assert.That(view.Rows.Any(r => r.ParameterName == $"{tracks}-Volume"), Is.False);
            Remove(machine);
        }

        [AvaloniaTest]
        public void APresetLoads()
        {
            var machine = Add("Pedal Juno106", "Preset Juno");
            var (window, view) = Open(machine);
            var names = machine.GetPresetNames().ToList();
            Assert.That(names, Is.Not.Empty, "Pedal Juno106.prs.xml, beside it in Gear");
            Assert.That(view.PresetBox.Items.Cast<string>(), Is.EqualTo(new[] { "<default>" }.Concat(names)));
            var before = view.Rows.Select(r => r.Parameter.GetValue(r.Track)).ToList();

            // Chosen as a user does, in the box.
            string name = names[names.Count / 2];
            view.PresetBox.SelectedItem = name;
            var preset = machine.GetPreset(name);
            foreach (var r in view.Rows)
            {
                Assert.That(r.Parameter.GetValue(r.Track), Is.EqualTo(preset.GetValueOrDefault(r.Parameter, r.Track)), $"{name}: {r.ParameterName}");
                Assert.That(r.Slider.Value, Is.EqualTo(r.Parameter.GetValue(r.Track)));
            }
            Assert.That(view.Rows.Select(r => r.Parameter.GetValue(r.Track)), Is.Not.EqualTo(before));
            Studio.Screenshot(window, "parameters-preset");
            view.Undo();
            Assert.That(view.Rows.Select(r => r.Parameter.GetValue(r.Track)), Is.EqualTo(before), "undo: the values before it");
            Remove(machine);
        }

        [AvaloniaTest]
        public void APresetSavesAndDeletesInThePresetsWindow()
        {
            var machine = Add("Pedal Gain", "Preset Gain");
            var (window, view) = Open(machine);
            var row = view.Rows.First();
            row.Slider.Commit(row.Slider.Minimum + 1);
            var presets = view.OpenPresets();
            presets.PresetName = "S2 test";
            presets.Comment = "from the studio's tests";
            presets.Add();
            Assert.That(machine.GetPresetNames(), Does.Contain("S2 test"));
            Assert.That(machine.GetPreset("S2 test").Comment, Is.EqualTo("from the studio's tests"));
            view.Refresh();
            Dispatcher.UIThread.RunJobs();
            Assert.That(view.PresetBox.Items.Cast<string>(), Does.Contain("S2 test"), "the window lists it");

            presets = view.OpenPresets();
            presets.Select("S2 test");
            presets.Delete();
            presets.Close();
            Assert.That(machine.GetPresetNames(), Does.Not.Contain("S2 test"));
            Remove(machine);
        }

        [AvaloniaTest]
        public void TheMenuBindsAMidiControllerAndCopiesAndPastesTheValues()
        {
            var machine = Add("Pedal Juno106", "Menu Juno");
            var (window, view) = Open(machine);
            var row = Row(view, "VCF Reso");
            try
            {
                // Bind… (MIDI learn), from the row's right-click menu.
                ParameterDialogs.BindResponder = () => (3, 74);
                row.ContextMenu!.Open(row);
                Dispatcher.UIThread.RunJobs();
                var items = row.ContextMenu.Items.OfType<MenuItem>().ToList();
                Assert.That(items.Select(i => i.Header as string), Does.Contain("_Bind to MIDI Controller").And.Contain("_Paste Preset").And.Contain("A_dd Track"));
                var bind = items.First(i => (string)i.Header! == "_Bind to MIDI Controller").Items.OfType<MenuItem>().First();
                Assert.That(bind.Header, Is.EqualTo("Bind..."));
                bind.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.That(row.Parameter.GetMIDIBinding(0), Is.EqualTo((2, 74)), "channel 3 (from 0: 2), controller 74");
                Studio.Screenshot(window, "parameters-menu");
                row.ContextMenu.Close();
                machine.UnbindAllMIDIControllers();

                // Copy the values, change one, paste them back: one undo step.
                int before = row.Parameter.GetValue(0);
                view.CopyPreset(xml: true).Wait();
                row.Slider.Commit(row.Slider.Maximum);
                Assert.That(row.Parameter.GetValue(0), Is.Not.EqualTo(before));
                view.PastePreset().Wait();
                Assert.That(row.Parameter.GetValue(0), Is.EqualTo(before), "pasted");
                view.Undo();
                Assert.That(row.Parameter.GetValue(0), Is.EqualTo(row.Slider.Maximum), "undo the paste");
            }
            finally
            {
                ParameterDialogs.BindResponder = null;
                Remove(machine);
            }
        }

        [AvaloniaTest]
        public void EditModeHidesParametersLocksAndMorphs()
        {
            string file = Path.Combine(Path.GetTempPath(), "rebuzz-studio-hidden-" + System.Environment.ProcessId + ".xml");
            ParameterVisibility.FilePath = file;
            ParameterVisibility.Reset();
            var machine = Add("Pedal SH101", "Edit SH");
            var (window, view) = Open(machine);
            try
            {
                view.EditMode = true;
                window.UpdateLayout();
                Studio.Screenshot(window, "parameters-edit-mode");

                // Untick a row's box: it hides when edit mode ends, and stays hidden (the file).
                var hidden = Row(view, "Cutoff");
                hidden.IsShown = false;
                view.EditMode = false;
                Assert.That(hidden.IsVisible, Is.False);
                Assert.That(File.ReadAllText(file), Does.Contain("Cutoff"));
                ParameterVisibility.Reset();
                Assert.That(ParameterVisibility.IsVisible(hidden.Parameter), Is.False);

                // Morph from <default> to <default> leaves locked rows alone and is one undo step.
                view.EditMode = true;
                var locked = Row(view, "Resonance");
                locked.IsLocked = true;
                int lockedBefore = locked.Parameter.GetValue(0);
                view.Function = ParameterWindowView.EditFunction.RandomizeAbsolute;
                var all = view.Rows.Select(r => r.Parameter.GetValue(r.Track)).ToList();
                view.ApplyEditFunction(updatable: false);
                Assert.That(locked.Parameter.GetValue(0), Is.EqualTo(lockedBefore), "a locked row stays");
                view.Undo();
                Assert.That(view.Rows.Select(r => r.Parameter.GetValue(r.Track)), Is.EqualTo(all), "undo the randomize");

                hidden.IsShown = true;
                view.EditMode = false;
                Assert.That(hidden.IsVisible, Is.True);
            }
            finally
            {
                ParameterVisibility.FilePath = null;
                ParameterVisibility.Reset();
                Remove(machine);
            }
        }

        [Test]
        public void TheSliderGeometryIsTheWpfOnes()
        {
            // 128 values in 200 pixels: the thumb is its minimum, 12, and travels 188.
            Assert.That(ParameterSliderGeometry.Thumb(0, 0, 127, 200, 12), Is.EqualTo((0, 12)));
            Assert.That(ParameterSliderGeometry.Thumb(127, 0, 127, 200, 12), Is.EqualTo((188, 12)));
            Assert.That(ParameterSliderGeometry.Thumb(64, 0, 127, 200, 12), Is.EqualTo((64 * 188 / 127, 12)));
            // A switch (0–1) in 200 pixels: two halves.
            Assert.That(ParameterSliderGeometry.Thumb(1, 0, 1, 200, 12), Is.EqualTo((100, 100)));
            Assert.That(ParameterSliderGeometry.DragValue(150, 50, 0, 1, 200, 100, 12), Is.EqualTo(1));
            Assert.That(ParameterSliderGeometry.DragValue(40, 6, 0, 127, 200, 12, 12), Is.EqualTo((int)System.Math.Round(34 * 127 / 188.0)));
            Assert.That(ParameterSliderGeometry.PageStep(0, 127), Is.EqualTo(22));
        }
    }
}
