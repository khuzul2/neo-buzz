// Fork (docs/specs/ux-overhaul.md P5b): drag-to-modulate and the Samples view, headless.
//  - A drag from a modulator's output handle onto a knob adds exactly one modulation and one undo step; the knob shows its ring; undo and redo
//    take it away and bring it back; letting go over nothing, Escape, and a ninth target add nothing (the agent's limits).
//  - The device view and the parameter window take the same drop; the matrix (+ Add, depth, polarity, Remove) round-trips through the same model.
//  - Samples: a WAV loaded into a slot, renamed, looped, given a root, cleared and brought back by undo; a file dragged in; the preview; slots in
//    hex; names and keyboard reach on every control of both windows.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Device;
using ReBuzz.Studio.Views.Modulation;
using ReBuzz.Studio.Views.Samples;
using ReBuzzTests.Automation;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class UxP5bTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static MachineCore Add(string machine, string name)
        {
            Core.SongCore.CreateMachine(machine, null, name, null, null, null, -1, 0.3f, 0.3f);
            return Core.SongCore.MachinesList.First(m => m.Name == name);
        }

        static void Remove(params MachineCore[] machines)
        {
            foreach (var m in machines) Core.UiHost.ReleaseMachine(m);
            Core.SongCore.DeleteMachines(machines);
        }

        static IParameter Global(IMachine m, string name) =>
            m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name == name);

        static int Steps => Core.SongCore.ActionStack.Actions.Count();

        static void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        static Window OpenEditor(MachineCore m)
        {
            Core.UiHost.OpenGui(m, null);
            var window = StudioMachineUiHost.GuiWindowOf(m);
            Assert.That(window, Is.Not.Null, $"{m.Name}: its GUI opened in a window");
            Settle();
            window!.CaptureRenderedFrame();
            return window;
        }

        static IEnumerable<T> All<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

        static Control ByName(Visual root, string type, string name) =>
            All<Control>(root).Single(c => c.GetType().Name == type && AutomationProperties.GetName(c) == name);

        /// <summary>The modulator's output handle (the kit's, one copy per machine DLL and one in the studio: found by its name).</summary>
        static Control Handle(Visual root) => All<Control>(root).Single(c => c.GetType().Name == "ModSourceHandle");

        static bool Ring(Control knob) => (bool)knob.GetType().GetProperty("ShowsModulationRing")!.GetValue(knob)!;

        static void Refresh(Control knob) => knob.GetType().GetMethod("RefreshModulation")!.Invoke(knob, null);

        static Point Centre(Control c, Window w) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w)!.Value;

        /// <summary>Presses on the source, moves, and lets go over a point of another window: the pointer stays with the source's window, as a captured drag does.</summary>
        static void Drag(Window from, Point start, Window to, Point end, bool escape = false)
        {
            from.MouseDown(start, MouseButton.Left);
            from.MouseMove(start + new Vector(14, 0));
            var screen = to.PointToScreen(end);
            var back = from.PointToClient(screen);
            from.MouseMove(back);
            if (escape)
            {
                from.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                from.MouseUp(back, MouseButton.Left);
            }
            else from.MouseUp(back, MouseButton.Left);
            Settle();
        }

        static (Window Lfo, Window Filter, MachineCore LfoMachine, MachineCore FilterMachine) TwoEditors()
        {
            var lfo = Add("Neo LFO", "P5b LFO");
            var filter = Add("Neo Filter", "P5b Filter");
            var lw = OpenEditor(lfo);
            var fw = OpenEditor(filter);
            lw.Position = new PixelPoint(0, 0);
            fw.Position = new PixelPoint(3000, 0);        // far apart on the screen: the drop finds the window by the point
            Settle();
            return (lw, fw, lfo, filter);
        }

        // ------------------------------------------------------------------ drag-to-modulate

        [AvaloniaTest]
        public void ADragFromTheHandleOntoAKnobAddsOneModulationAndOneUndoStep()
        {
            var (lw, fw, lfo, filter) = TwoEditors();
            try
            {
                var mod = ModulationBoard.Of(lfo)!;
                var handle = Handle(lw);
                var knob = ByName(fw, "ParamKnob", "Cutoff");
                Assert.That(Ring(knob), Is.False);
                Assert.That(mod.Targets, Is.Empty);
                int before = Steps;
                Drag(lw, Centre(handle, lw), fw, Centre(knob, fw));
                Assert.That(mod.Targets, Has.Count.EqualTo(1), "exactly one modulation");
                Assert.That(mod.Targets[0].Machine, Is.EqualTo("P5b Filter"));
                Assert.That(mod.Targets[0].Param, Is.EqualTo("Cutoff"));
                Assert.That(Steps - before, Is.EqualTo(1), "one undo step for the gesture");
                Refresh(knob);
                Assert.That(Ring(knob), Is.True, "the knob shows its ring");
                Assert.That(ModulationBoard.Dragging, Is.Null, "the drag is over");
                Assert.That(MainWindow.OpenWindows.LastOrDefault()?.StatusText ?? "", Does.Not.Contain("ZZZ"));

                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(mod.Targets, Is.Empty, "undo takes it away");
                Refresh(knob);
                Assert.That(Ring(knob), Is.False);
                Core.SongCore.ActionStack.Redo();
                Settle();
                Assert.That(mod.Targets, Has.Count.EqualTo(1), "redo brings it back");
            }
            finally { lw.Close(); fw.Close(); Remove(lfo, filter); }
        }

        [AvaloniaTest]
        public void LettingGoOverNothingOrPressingEscapeAddsNothing()
        {
            var (lw, fw, lfo, filter) = TwoEditors();
            try
            {
                var mod = ModulationBoard.Of(lfo)!;
                var handle = Handle(lw);
                var knob = ByName(fw, "ParamKnob", "Cutoff");
                int before = Steps;
                Drag(lw, Centre(handle, lw), fw, new Point(2, 2));
                Assert.That(mod.Targets, Is.Empty, "nothing under the pointer");
                Drag(lw, Centre(handle, lw), fw, Centre(knob, fw), escape: true);
                Assert.That(mod.Targets, Is.Empty, "Escape cancels");
                Assert.That(Steps, Is.EqualTo(before), "no undo step");
                Assert.That(ModulationBoard.Dragging, Is.Null);
            }
            finally { lw.Close(); fw.Close(); Remove(lfo, filter); }
        }

        [AvaloniaTest]
        public void ANinthTargetIsRefusedLikeTheAgentsAndAddsNoStep()
        {
            var lfo = Add("Neo LFO", "P5b LFO");
            var filter = Add("Neo Filter", "P5b Filter");
            try
            {
                var mod = ModulationBoard.Of(lfo)!;
                var targets = filter.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.Where(p => p.Type != ParameterType.Note).ToList();
                Assert.That(targets.Count, Is.GreaterThanOrEqualTo(9), "the filter has enough parameters");
                int added = 0;
                foreach (var p in targets)
                {
                    int before = Steps;
                    string refusal = ModulationEdits.Add(Core, mod, filter, p, 0);
                    if (refusal == null) { added++; Assert.That(Steps - before, Is.EqualTo(1)); }
                    else
                    {
                        Assert.That(Steps, Is.EqualTo(before), "a refusal is no undo step: " + refusal);
                        break;
                    }
                }
                Assert.That(added, Is.EqualTo(ModulationBoard.MaxTargets), "8 targets, as modulate has");
                Assert.That(mod.Targets, Has.Count.EqualTo(8));
            }
            finally { Remove(lfo, filter); }
        }

        [AvaloniaTest]
        public void TheDeviceViewTakesTheDropAndShowsTheRing()
        {
            var lfo = Add("Neo LFO", "P5b LFO");
            var filter = Add("Neo Filter", "P5b Filter");
            var host = new Window { Width = 1500, Height = 700 };
            try
            {
                var mod = ModulationBoard.Of(lfo)!;
                var source = new DeviceView(lfo);
                source.SetMode(DeviceView.DeviceMode.List, remember: false);
                var target = new DeviceView(filter);
                target.SetMode(DeviceView.DeviceMode.List, remember: false);
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,*") };
                Grid.SetRow(source, 1);
                Grid.SetRow(target, 1);
                Grid.SetColumn(target, 1);
                grid.Children.Add(source.Toolbar);          // the dock hosts the toolbar (the handle is in it)
                grid.Children.Add(source);
                grid.Children.Add(target);
                host.Content = grid;
                host.Show();
                Settle();
                var handle = Handle(source.Toolbar);
                var cutoff = target.Layout.All.First(p => p.Parameter == Global(filter, "Cutoff"));
                target.List.Reveal(cutoff);
                Settle();
                var rect = target.List.ControlBounds(cutoff);
                var drop = target.List.TranslatePoint(new Point(rect.Center.X, rect.Center.Y), host)!.Value;
                int before = Steps;
                Drag(host, Centre(handle, host), host, drop);
                Assert.That(mod.Targets.Select(t => t.Param), Is.EqualTo(new[] { "Cutoff" }), "the row took it");
                Assert.That(Steps - before, Is.EqualTo(1));
                for (int i = 0; i < 20 && !target.List.IsModulated(cutoff); i++) { System.Threading.Thread.Sleep(20); Settle(); }
                Assert.That(target.List.IsModulated(cutoff), Is.True, "the row shows the ring");
            }
            finally { host.Close(); Remove(lfo, filter); }
        }

        [AvaloniaTest]
        public void TheDevicePanelsKnobShowsTheRingToo()
        {
            var lfo = Add("Neo LFO", "P5b LFO");
            var filter = Add("Neo Filter", "P5b Filter");
            try
            {
                var mod = ModulationBoard.Of(lfo)!;
                var cutoff = Global(filter, "Cutoff");
                var param = DeviceLayout.Build(filter).All.First(p => p.Name == "Cutoff");
                var knob = new DeviceKnob(filter, param, Core, NeoBuzz.Editor.Tokens.Identity(NeoBuzz.Editor.MachineCategory.Synth));
                var host = new Window { Width = 200, Height = 140, Content = new Grid { Children = { knob } } };
                host.Show();
                Settle();
                Assert.That(knob.ShowsModulationRing, Is.False);
                Assert.That(ModulationEdits.Add(Core, mod, filter, cutoff, 0), Is.Null);
                knob.RefreshModulation();
                Assert.That(knob.ShowsModulationRing, Is.True, "a ring around the knob");
                var surface = (IModulationDropSurface)knob;
                Assert.That(surface.ModulationTargetAt(5, 5, out var machine, out var parameter, out _), Is.True);
                Assert.That(parameter, Is.SameAs(cutoff));
                Assert.That(machine, Is.SameAs(filter));
                host.Close();
            }
            finally { Remove(lfo, filter); }
        }

        [AvaloniaTest]
        public void TheMachineCanvasHasAnOutputHandleOnAModulatorAndItsDragAddsOneTarget()
        {
            var (window, view) = MachineViewTests.OpenEmpty();
            var host = new Window { Width = 760, Height = 700 };
            MachineCore? lfo = null, filter = null;
            try
            {
                lfo = Add("Neo LFO", "P5b LFO");
                filter = Add("Neo Filter", "P5b Filter");
                var device = new DeviceView(filter);
                device.SetMode(DeviceView.DeviceMode.List, remember: false);
                host.Content = device;
                host.Position = new PixelPoint(3000, 0);
                host.Show();
                Settle();
                var canvas = window.GetVisualDescendants().OfType<ReBuzz.Studio.Views.Machines.MachineCanvas>().First();
                var cutoff = device.Layout.All.First(p => p.Parameter == Global(filter, "Cutoff"));
                device.List.Reveal(cutoff);
                Settle();
                var rect = device.List.ControlBounds(cutoff);
                var drop = device.List.TranslatePoint(rect.Center, host)!.Value;
                var start = view.FromCanvas(canvas.ModHandleOf(lfo), window);
                int before = Steps;
                Drag(window, start, host, drop);
                var mod = ModulationBoard.Of(lfo)!;
                Assert.That(mod.Targets.Select(t => t.Param), Is.EqualTo(new[] { "Cutoff" }), "dragged from the machine box's handle");
                Assert.That(Steps - before, Is.EqualTo(1));
                // A press on an ordinary machine's port does not start a modulation.
                Assert.That(ModulationBoard.Dragging, Is.Null);
            }
            finally
            {
                host.Close();
                MachineViewTests.Close(window);
                if (lfo != null && filter != null) Remove(lfo, filter);
            }
        }

        // ------------------------------------------------------------------ the matrix

        [AvaloniaTest]
        public void TheMatrixAddsEditsAndRemovesThroughTheSameModelAndUndo()
        {
            var lfo = Add("Neo LFO", "P5b LFO");
            var filter = Add("Neo Filter", "P5b Filter");
            ModulationMatrixWindow? matrix = null;
            try
            {
                var mod = ModulationBoard.Of(lfo)!;
                matrix = ModulationMatrixWindow.Open(mod);
                Settle();
                Assert.That(matrix.Rows, Is.Empty);
                matrix.Choose("P5b LFO", "P5b Filter", "Cutoff");
                int before = Steps;
                Assert.That(matrix.AddNow(), Is.Null);
                Settle();
                Assert.That(Steps - before, Is.EqualTo(1), "+ Add is one undo step");
                Assert.That(matrix.Rows, Has.Count.EqualTo(1));
                Assert.That(mod.Targets[0].Param, Is.EqualTo("Cutoff"));

                before = Steps;
                matrix.Rows[0].Depth.Value = -30;
                Settle();
                Assert.That(mod.Targets[0].Depth, Is.EqualTo(-30), "the depth edit reaches the model");
                Assert.That(Steps - before, Is.EqualTo(1), "one undo step");
                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(mod.Targets[0].Depth, Is.EqualTo(50), "undo puts the depth back");
                matrix.Refill();
                Assert.That((double)matrix.Rows[0].Depth.Value!, Is.EqualTo(50));

                bool bipolar = mod.Targets[0].Bipolar;
                matrix.Rows[0].Polarity.IsChecked = !bipolar;
                Settle();
                Assert.That(mod.Targets[0].Bipolar, Is.EqualTo(!bipolar), "polarity");

                before = Steps;
                matrix.Rows[0].Remove.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle();
                Assert.That(mod.Targets, Is.Empty, "remove");
                Assert.That(Steps - before, Is.EqualTo(1));
                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(mod.Targets, Has.Count.EqualTo(1), "undo puts it back");
                Assert.That(mod.Targets[0].Param, Is.EqualTo("Cutoff"));
            }
            finally { matrix?.Close(); Remove(lfo, filter); }
        }

        [AvaloniaTest]
        public void TheMatrixSaysWhyAnAddIsRefusedAndTheAgentsSafeRangeStillHolds()
        {
            var lfo = Add("Neo LFO", "P5b LFO");
            try
            {
                var mod = ModulationBoard.Of(lfo)!;
                var master = Core.SongCore.MachinesList[0];
                var tempo = master.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name.Contains("BPM", StringComparison.OrdinalIgnoreCase));
                int before = Steps;
                string refusal = ModulationEdits.Add(Core, mod, master, tempo, 0);
                Assert.That(refusal, Is.Not.Null, "the master's tempo is refused, as modulate refuses it");
                Assert.That(Steps, Is.EqualTo(before));
                Assert.That(mod.Targets.Select(t => $"{t.Machine}.{t.Param}:{t.State}"), Is.Empty);
            }
            finally { Remove(lfo); }
        }

        // ------------------------------------------------------------------ the Samples view

        static string MakeWav(string name, double seconds = 0.5, int rate = 22050)
        {
            string dir = Path.Combine(Path.GetTempPath(), "p5b-samples-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            int frames = (int)(seconds * rate);
            var data = new float[frames * 2];
            for (int i = 0; i < frames; i++) data[2 * i] = data[2 * i + 1] = (float)(0.6 * Math.Sin(i * 2 * Math.PI * 220 / rate) * (1 - i / (double)frames));
            ReBuzz.Agent.Audio.Wav.WriteStereoFloat(path, data, frames, rate);
            return path;
        }

        static void EmptyWavetable()
        {
            for (int i = 0; i < Core.SongCore.Wavetable.Waves.Count; i++)
                if (Core.SongCore.Wavetable.Waves[i] != null) Core.SongCore.Wavetable.LoadWave(i, null, null, false);
        }

        [AvaloniaTest]
        public void ASampleLoadsRenamesLoopsClearsAndComesBackWithUndo()
        {
            EmptyWavetable();
            string wav = MakeWav("Kick Drum.wav");
            var window = SamplesWindow.Open();
            try
            {
                Settle();
                Assert.That(window.SlotList.ItemCount, Is.EqualTo(200), "Buzz's 200 slots");
                var first = (window.SlotList.ItemsSource as IEnumerable<string>)!.ToList();
                Assert.That(first[0], Is.EqualTo("01"), "slots are in hex from 01, empty ones show only that");
                Assert.That(first[199], Is.EqualTo("C8"), "the last slot is C8");

                window.Selected = 0;
                int before = Steps;
                Assert.That(window.LoadFile(wav), Is.Null);
                Settle();
                Assert.That(Steps - before, Is.EqualTo(1), "loading is one undo step");
                var wave = Core.SongCore.Wavetable.Waves[0];
                Assert.That(wave, Is.Not.Null);
                Assert.That(wave!.Name, Is.EqualTo("Kick Drum"));
                Assert.That((window.SlotList.ItemsSource as IEnumerable<string>)!.First(), Is.EqualTo("01  Kick Drum"));
                Assert.That(window.InfoText, Does.Contain("22050 Hz").And.Contain("stereo"));
                Assert.That(window.Waveform.HasSignal, Is.True, "the waveform is drawn from the samples");
                Assert.That(window.LoadButton.Content, Is.EqualTo("Replace…"));

                // Rename.
                before = Steps;
                window.NameBox.Text = "Kick";
                Assert.That(window.RenameNow(), Is.Null);
                Settle();
                Assert.That(Core.SongCore.Wavetable.Waves[0]!.Name, Is.EqualTo("Kick"));
                Assert.That(Steps - before, Is.EqualTo(1));

                // Loop.
                var layer = Core.SongCore.Wavetable.Waves[0]!.Layers[0];
                before = Steps;
                window.LoopStartBox.Value = 1000;
                window.LoopEndBox.Value = 8000;
                window.LoopBox.IsChecked = true;
                Settle();
                Assert.That(Core.SongCore.Wavetable.Waves[0]!.Flags & WaveFlags.Loop, Is.EqualTo(WaveFlags.Loop));
                layer = Core.SongCore.Wavetable.Waves[0]!.Layers[0];
                Assert.That((layer.LoopStart, layer.LoopEnd), Is.EqualTo((1000, 8000)));
                Assert.That(Steps - before, Is.GreaterThanOrEqualTo(1));
                Assert.That(WaveEdits.SetLoop(Core, 0, true, 9000, 9001), Is.Not.Null, "a loop under two samples is refused");
                Assert.That(WaveEdits.SetLoop(Core, 0, true, 100, layer.SampleCount + 5), Is.Not.Null, "a loop past the end is refused");

                // Root.
                window.RootBox.Value = 48;
                window.ApplyRoot();
                Assert.That(Pitch(Core.SongCore.Wavetable.Waves[0]!.Layers[0].RootNote), Is.EqualTo(48));

                // Clear and undo.
                before = Steps;
                window.ClearNow();
                Settle();
                Assert.That(Core.SongCore.Wavetable.Waves[0], Is.Null, "cleared");
                Assert.That(Steps - before, Is.EqualTo(1));
                Core.SongCore.ActionStack.Undo();
                Settle();
                var back = Core.SongCore.Wavetable.Waves[0];
                Assert.That(back, Is.Not.Null, "undo brings the wave back");
                Assert.That(back!.Name, Is.EqualTo("Kick"));
                Assert.That(back.Layers[0].SampleCount, Is.EqualTo(layer.SampleCount));
                Assert.That((back.Layers[0].LoopStart, back.Layers[0].LoopEnd), Is.EqualTo((1000, 8000)), "with its loop");

                // Undo the rest, down to an empty wavetable.
                for (int i = 0; i < 12 && Core.SongCore.Wavetable.Waves[0] != null; i++) Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(Core.SongCore.Wavetable.Waves[0], Is.Null, "undo of the load empties the slot");
            }
            finally { window.Close(); EmptyWavetable(); try { Directory.Delete(Path.GetDirectoryName(wav)!, true); } catch (IOException) { } }
        }

        static int Pitch(int buzzNote) => ReBuzz.Agent.Model.Pitch.FromBuzz(buzzNote);

        [AvaloniaTest]
        public void FilesDraggedInFillTheChosenSlotThenTheEmptyOnesAndNonAudioIsRefused()
        {
            EmptyWavetable();
            string a = MakeWav("A.wav"), b = MakeWav("B.wav");
            string text = Path.Combine(Path.GetDirectoryName(a)!, "notes.txt");
            File.WriteAllText(text, "not audio");
            var window = SamplesWindow.Open();
            try
            {
                Settle();
                window.Selected = 2;
                window.DropFiles(new[] { a, b });
                Settle();
                var waves = Core.SongCore.Wavetable.Waves;
                Assert.That(waves[2]?.Name, Is.EqualTo("A"), "the first goes to the chosen slot");
                Assert.That(waves[0]?.Name, Is.EqualTo("B"), "the next to the first empty one");
                int before = Steps;
                window.DropFiles(new[] { text });
                Assert.That(window.Message, Does.Contain("WAV"), "a text file says what is accepted");
                Assert.That(Steps, Is.EqualTo(before));
                string again = window.LoadFile(a);
                Assert.That(again, Is.Null, "a filled slot is replaced from the Replace button");
            }
            finally { window.Close(); EmptyWavetable(); try { Directory.Delete(Path.GetDirectoryName(a)!, true); } catch (IOException) { } }
        }

        [AvaloniaTest]
        public void ThePreviewPlaysAndStops()
        {
            EmptyWavetable();
            string wav = MakeWav("Pre.wav", 2);
            var window = SamplesWindow.Open();
            try
            {
                Settle();
                window.Selected = 0;
                Assert.That(window.LoadFile(wav), Is.Null);
                Assert.That(window.IsPlaying, Is.False);
                window.TogglePlay();
                Assert.That(window.IsPlaying, Is.True, "Play starts the preview");
                Assert.That(window.PlayButton.Content, Is.EqualTo("Stop"));
                window.TogglePlay();
                Assert.That(window.IsPlaying, Is.False);
                Assert.That(window.PlayButton.Content, Is.EqualTo("Play"));
            }
            finally { window.Close(); EmptyWavetable(); try { Directory.Delete(Path.GetDirectoryName(wav)!, true); } catch (IOException) { } }
        }

        // ------------------------------------------------------------------ keyboard and names

        [AvaloniaTest]
        public void EveryControlOfBothWindowsHasANameAndTakesTheKeyboard()
        {
            var lfo = Add("Neo LFO", "P5b LFO");
            var samples = SamplesWindow.Open();
            var matrix = ModulationMatrixWindow.Open();
            try
            {
                Settle();
                foreach (var w in new Window[] { samples, matrix })
                {
                    var problems = new List<string>();
                    foreach (var c in All<Control>(w).Where(c => c.IsEffectivelyVisible && !c.GetVisualAncestors().Any(a => a is NumericUpDown || a is ComboBox || a is ListBoxItem) && c is not RepeatButton && (c is Button || c is ToggleButton || c is CheckBox || c is ComboBox || c is NumericUpDown || c is TextBox || c is ListBox)))
                    {
                        string name = AutomationProperties.GetName(c);
                        if (string.IsNullOrWhiteSpace(name) && !(c is Button b && b.Content is string s && s.Length > 0)) problems.Add(c.GetType().Name + " has no name");
                        if (c.IsEnabled && !c.Focusable && c is not ListBox) problems.Add(c.GetType().Name + " " + name + " cannot take the keyboard");
                    }
                    Assert.That(problems, Is.Empty, w.Title + ": " + string.Join("; ", problems));
                }
            }
            finally { samples.Close(); matrix.Close(); Remove(lfo); }
        }
    }
}
