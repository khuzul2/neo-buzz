// Fork (TM1, docs/specs/machines.md Neo Sampler Tracker mode): the piano roll of a sampler that plays a sample per note, headless. A chip on each
// note, the picker (search, a number, the keyboard, hover preview, one undo step an apply, the mixed selection, Key map), the Sample lane, Alt+wheel,
// colour by sample; a machine that doesn't play samples per note gets none of it.

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Detail;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Tests
{
    public class SamplerPickerTests
    {
        const int TB = ClipNotes.TimeBase;

        static ReBuzzCore Core => Studio.Host.Core;

        static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        /// <summary>A short mono WAV: a decaying burst, so each wave is its own.</summary>
        internal static string Wav(string dir, string name, double seconds, int seed)
        {
            string path = Path.Combine(dir, name + ".wav");
            int rate = 44100, n = (int)(seconds * rate);
            var rng = new Random(seed);
            using var f = File.Create(path);
            using var w = new BinaryWriter(f);
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Exp(-i / (0.25 * rate * seconds + 1));
                w.Write((short)((rng.NextDouble() * 2 - 1) * 14000 * env));
            }
            return path;
        }

        sealed class Rig : IDisposable
        {
            public MainWindow Window;
            public MachineCore Machine;
            public IClip Clip;
            public TrackerRoll Roll;
            public PianoRoll Notes => Roll.Roll;
            readonly string dir;

            public Rig(string gear, int mode, params string[] waves)
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Core.ExecuteCommand(BuzzCommand.Stop);
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Pump();
                dir = Path.Combine(NeoBuzz.TestSupport.TestTemp.Path("sampler-picker-" + Environment.ProcessId));
                Directory.CreateDirectory(dir);
                for (int i = 0; i < waves.Length; i++)
                    Assert.That(ReBuzz.Agent.WaveEdits.Load(Core, Wav(dir, "w" + i, 0.1 + 0.05 * i, i + 1), -1, waves[i], false, out _), Is.Null);
                Core.SongCore.CreateMachine(gear, null, "Breaks", null, null, null, -1, 0.3f, 0.3f);
                Machine = Core.SongCore.MachinesList.First(m => m.Name == "Breaks");
                if (gear == "Neo Sampler") Machine.ParameterGroups[1].Parameters.First(p => p.Name == "Mode").SetValue(0, mode);
                Machine.CreateClip("Jungle", 16);
                Clip = Machine.ClipsList.First(p => p.Name == "Jungle");
                Window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
                Window.Show();
                Pump();
                Window.Detail.ShowPage(true, true);
                ClipView.Edit(Clip);
                Pump();
                Roll = ClipView.Page.Roll;
                Roll.Poll();
                Pump();
            }

            public void Dispose()
            {
                Core.Modified = false;
                Window.Close();
                Pump();
                Core.SongCore.DeleteMachines(new[] { Machine });
                Dialogs.Responder = null;
            }

            public int Steps => Core.SongCore.ActionStack.Actions.Count();

            public void Write(params ClipNote[] notes)
            {
                Assert.That(SongNotes.For(Core, Clip).Edit(null, notes), Is.Null);
                Roll.Poll();
                Pump();
            }

            public ClipNote[] Now => SongNotes.For(Core, Clip).Notes().OrderBy(n => n.Time).ToArray();
        }

        static readonly string[] Names = { "Break · kick", "Break · snare", "Break · snare roll", "Break · hat", "Break · kick and hat" };

        static ClipNote[] Line() => new[]
        {
            new ClipNote(0, 2 * TB, 48, 100, 0, 1),
            new ClipNote(2 * TB, TB, 48, 100, 0, 2),
            new ClipNote(3 * TB, TB, 46, 100, 0, 3),
            new ClipNote(4 * TB, 2 * TB, 48, 100, 0, 5),
            new ClipNote(6 * TB, TB, 48, 100),
        };

        [AvaloniaTest]
        public void ANoteWearsItsChipTheSampleLaneAndColourComeWithTrackerModeOnly()
        {
            using (var poly = new Rig("Neo Poly", 0))
            {
                Assert.That(poly.Notes.PerNoteSamples, Is.False, "no chips on a machine that doesn't play a sample per note");
                Assert.That(poly.Roll.ColourBox.IsVisible, Is.False);
                Assert.That(poly.Roll.LowerBox.Items.Cast<object>().Select(o => o.ToString()), Does.Not.Contain("Sample"));
            }
            using (var instrument = new Rig("Neo Sampler", 0, Names))
                Assert.That(instrument.Notes.PerNoteSamples, Is.False, "Instrument mode: none either");

            using var r = new Rig("Neo Sampler", 2, Names);
            Assert.That(r.Notes.PerNoteSamples, Is.True);
            Assert.That(r.Roll.ColourBox.IsVisible, Is.True);
            Assert.That(r.Roll.LowerBox.Items.Cast<object>().Select(o => o.ToString()), Does.Contain("Sample"));
            r.Write(Line());
            Assert.That(r.Notes.Notes.Count, Is.EqualTo(5));
            var wide = r.Notes.Notes.First(n => n.Duration == 2 * TB);
            Assert.That(r.Notes.ChipRect(wide), Is.Not.Null, "a wide note has a chip");
            r.Notes.TickWidth = 3;
            Assert.That(r.Notes.ChipRect(wide), Is.Null, "zoomed out: too short for a chip, it keeps its colour");
            r.Notes.TickWidth = 24;
            Assert.That(r.Roll.SampleContext.Samples.Select(s => s.Name).Take(5), Is.EqualTo(Names));
            Assert.That(r.Roll.SampleContext.Samples.Count, Is.GreaterThan(5), "and the built-in samples after the song's");
            Assert.That(r.Roll.SampleContext.Samples.First().Seconds, Is.EqualTo(0.1).Within(0.01));

            // Changing the mode makes the roll for the other store.
            r.Machine.ParameterGroups[1].Parameters.First(p => p.Name == "Mode").SetValue(0, 0);
            r.Roll.Poll();
            Pump();
            Assert.That(r.Notes.PerNoteSamples, Is.False, "Mode back to Instrument: no chips");
            r.Machine.ParameterGroups[1].Parameters.First(p => p.Name == "Mode").SetValue(0, 2);
            r.Roll.Poll();
            Pump();
            Assert.That(r.Notes.PerNoteSamples, Is.True);
            Assert.That(r.Notes.Notes.Count, Is.EqualTo(5), "the notes in the tracks are still there");
        }

        [AvaloniaTest]
        public void ThePickerAppliesToTheSelectionInOneUndoStepAndTakesTheKeyboard()
        {
            using var r = new Rig("Neo Sampler", 2, Names);
            r.Write(Line());
            var roll = r.Notes;
            roll.SelectOnly(new[] { 1, 2 });
            int steps = r.Steps;
            Assert.That(roll.RequestPicker(), Is.True);
            Pump();
            var picker = r.Roll.Picker;
            Assert.That(picker.IsVisible, Is.True);
            Assert.That(picker.Mixed, Is.True, "snare and snare roll: two samples");
            Assert.That(picker.Summary, Does.Contain("2 notes"));
            picker.Close();

            roll.SelectOnly(new[] { 1 });
            roll.RequestPicker();
            Pump();
            Assert.That(picker.Mixed, Is.False);
            Assert.That(picker.Summary, Is.EqualTo("Applies to this note"));
            Assert.That(picker.RowNumbers[0], Is.EqualTo(0), "Key map first");
            Assert.That(picker.RowNumbers.Skip(1).Take(5), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));

            // Typing: letters search the names, a number jumps to it.
            picker.Search.Text = "snare";
            Pump();
            Assert.That(picker.RowNumbers.Take(2), Is.EqualTo(new[] { 2, 3 }), "the song's samples first");
            picker.Search.Text = "4";
            Pump();
            Assert.That(picker.Highlighted, Is.EqualTo(4), "a number jumps to it");
            picker.Search.Text = "snare";
            Pump();
            r.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            r.Window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Pump();
            Assert.That(picker.Highlighted, Is.EqualTo(3), "↓ moves");
            r.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            r.Window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Pump();
            Assert.That(picker.IsVisible, Is.False, "Enter applies and closes");
            Assert.That(r.Now[1].Sample, Is.EqualTo(3));
            Assert.That(r.Steps, Is.EqualTo(steps + 1), "one undo step");
            Core.SongCore.ActionStack.Undo();
            r.Roll.Poll();
            Assert.That(r.Now[1].Sample, Is.EqualTo(2), "undo takes it back");
            Core.SongCore.ActionStack.Redo();

            // Key map gives the note back to the sampler's zones.
            roll.SelectOnly(new[] { 0 });
            roll.RequestPicker();
            r.Roll.Picker.Apply(0);
            Pump();
            Assert.That(r.Now[0].Sample, Is.EqualTo(0));
            Assert.That(r.Now[0].Offset, Is.EqualTo(0));

            // Esc closes without a change.
            roll.SelectOnly(new[] { 4 });
            int before = r.Steps;
            roll.HandleKey(Key.S, KeyModifiers.None);
            Pump();
            Assert.That(r.Roll.Picker.IsVisible, Is.True, "S opens it for the selection");
            r.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            r.Window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Pump();
            Assert.That(r.Roll.Picker.IsVisible, Is.False);
            Assert.That(r.Steps, Is.EqualTo(before));
        }

        [AvaloniaTest]
        public void AMixedSelectionStepsEachNoteByOneAndTheWheelStepsOne()
        {
            using var r = new Rig("Neo Sampler", 2, Names);
            r.Write(Line());
            var roll = r.Notes;
            roll.SelectOnly(new[] { 0, 1, 3 });                    // samples 1, 2, 5
            roll.RequestPicker();
            Pump();
            var picker = r.Roll.Picker;
            Assert.That(picker.Mixed, Is.True);
            int steps = r.Steps;
            roll.StepSelected(1);
            Assert.That(r.Now.Select(n => n.Sample), Is.EqualTo(new[] { 2, 3, 3, 201, 0 }), "the list goes on from the song's last sample into the built-in ones");
            Assert.That(r.Steps, Is.EqualTo(steps + 1), "Each +1 is one undo step");
            picker.Close();

            // Alt+wheel on a note steps its sample (and only its, when it isn't selected).
            roll.SelectOnly(new[] { 4 });
            steps = r.Steps;
            roll.StepSample(2, 1);
            Assert.That(r.Now[2].Sample, Is.EqualTo(4));
            Assert.That(r.Now[4].Sample, Is.EqualTo(0));
            Assert.That(r.Steps, Is.EqualTo(steps + 1));
            roll.StepSample(2, -1);
            Assert.That(r.Now[2].Sample, Is.EqualTo(3));
            // A key-mapped note steps to the first sample.
            roll.StepSample(4, 1);
            Assert.That(r.Now[4].Sample, Is.EqualTo(1));
        }

        [AvaloniaTest]
        public void TheSampleLaneShowsTheChipsAndAClickOpensThePicker()
        {
            using var r = new Rig("Neo Sampler", 2, Names);
            r.Write(Line());
            Assert.That(r.Roll.Samples, Is.Not.Null, "the lane exists for a machine with samples per note");
            Assert.That(r.Roll.LowerBox.SelectedItem?.ToString(), Is.EqualTo("Sample"), "and is the lane shown");
            Pump();
            Assert.That(r.Roll.Samples.IsVisible, Is.True);
            Assert.That(r.Roll.Velocities.IsVisible, Is.False);
            var p = r.Roll.Samples.TranslatePoint(r.Roll.Samples.ChipPoint(1), r.Window);
            Assert.That(p, Is.Not.Null);
            r.Window.MouseMove(p!.Value);
            r.Window.MouseDown(p.Value, MouseButton.Left);
            r.Window.MouseUp(p.Value, MouseButton.Left);
            Pump();
            Assert.That(r.Roll.Picker.IsVisible, Is.True, "a click on a chip opens the picker");
            Assert.That(r.Notes.Selection.ToArray(), Is.EqualTo(new[] { 1 }), "for that note");
            r.Roll.Picker.Apply(4);
            Assert.That(r.Now[1].Sample, Is.EqualTo(4));
            r.Roll.LowerBox.SelectedIndex = 0;
            Pump();
            Assert.That(r.Roll.Velocities.IsVisible, Is.True, "Velocity again");
        }

        [AvaloniaTest]
        public void ANewNoteTakesTheSampleUsedLastAndColourByTheSampleCanBeSwitchedOff()
        {
            using var r = new Rig("Neo Sampler", 2, Names);
            r.Write(Line());
            r.Notes.SelectOnly(new[] { 3 });                       // sample 5
            r.Notes.RequestPicker();
            r.Roll.Picker.Apply(4);
            Pump();
            r.Notes.AddNote(10 * TB, 50);
            Assert.That(r.Now.Last().Sample, Is.EqualTo(4), "the note drawn plays the sample chosen last");
            Assert.That(r.Roll.SampleContext.ColourBySample, Is.True);
            r.Roll.ColourBox.IsChecked = false;
            Assert.That(r.Roll.SampleContext.ColourBySample, Is.False);
        }
    }
}
