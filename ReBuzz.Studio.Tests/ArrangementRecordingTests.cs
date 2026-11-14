// Fork (docs/specs/daw-modes.md AR5): recording and links, headless. An armed track records MIDI into the arrangement
// (a scripted MIDI source, timed by the MIDI thread's stamps); loop recording makes a take of each pass on a take lane; a
// bar of each take is comped onto the main lane (the exit test: the render equals the same notes written plain); a clip
// launches in the Session View and the arrangement takes the track back; clips go between slots and lanes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Sequence;
using static ReBuzz.Studio.Tests.AudioHygieneTests;

namespace ReBuzz.Studio.Tests
{
    public class ArrangementRecordingTests
    {
        const int TB = PatternEvent.TimeBase;
        const int Bar = 16;

        static ReBuzzCore Core => Studio.Host.Core;

        static RawInputModifiers Cmd => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        static void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        static int On(int note, int velocity = 99) => SessionRig.NoteOn(note, velocity);

        static int Off(int note) => SessionRig.NoteOff(note);

        sealed class Rig : IDisposable
        {
            public MainWindow Window { get; }
            public SequenceView View { get; }
            public ArrangementCanvas Canvas => View.Arrangement;
            readonly WorkManager work;
            readonly float[] buffer = new float[512];
            readonly bool looping;
            readonly bool subTicks;

            public Rig(params string[] machines)
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                SessionCore.Timestamp = () => 1_000_000;      // time stands still between renders: heard time is the rendered position
                looping = Core.Looping;
                subTicks = Core.EngineSettings.SubTickTiming;
                Core.EngineSettings.SubTickTiming = false;
                Core.ExecuteCommand(BuzzCommand.Stop);
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
                foreach (var name in machines) Machine(name);
                Window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
                ToolTip.SetServiceEnabled(Window, false);
                Window.Show();
                var tab = Window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Arrangement");
                tab.IsSelected = true;
                Settle();
                View = (SequenceView)tab.Content!;
                Click(Of(View.ArrangementModeButton));
                Assert.That(View.IsArrangementMode, Is.True);
                Settle();
                work = new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
            }

            public static MachineCore Machine(string name)
            {
                var song = Core.SongCore;
                song.CreateMachine("Neo Poly", null, name, null, null, null, -1, -0.3f + 0.2f * song.MachinesList.Count, 0.3f);
                var machine = song.MachinesList.Last(m => m.Name == name);
                var master = song.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
                song.ConnectMachines(machine, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                song.AddSequence(machine, song.SequencesList.Count);
                machine.ParameterGroupsList[1].ParametersList.First(q => q.Name == "Drift").SetValue(0, 0);      // random per voice: off, so renders compare
                return machine;
            }

            public ISequence Lane(string machine, int index = 0) => Core.Song.Sequences.Where(s => s.Machine.Name == machine).ElementAt(index);

            public List<ISequence> TakeLanes(string machine) => Core.Song.Sequences.Where(s => s.Machine.Name == machine && Core.Arrangement.RoleOf(s) == LaneRole.Take).ToList();

            public Point Of(Avalonia.Controls.Control c)
            {
                Settle();
                return c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), Window)!.Value;
            }

            public Point At(ISequence lane, int tick, double dx = 0, double fy = 0.55)
            {
                Settle();
                var row = Canvas.RowOf(lane);
                Assert.That(row, Is.Not.Null, "the lane has a row");
                var p = Canvas.TranslatePoint(new Point(Canvas.Geometry.XOf(tick) + dx, Canvas.YOf(row) + fy * row.Height), Window);
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

            public void Drag(Point from, Point to, RawInputModifiers mods = RawInputModifiers.None)
            {
                Window.MouseMove(from, mods);
                Window.MouseDown(from, MouseButton.Left, mods);
                for (int i = 1; i <= 4; i++)
                    Window.MouseMove(from + (to - from) * (i / 4.0), mods | RawInputModifiers.LeftMouseButton);
                Window.MouseUp(to, MouseButton.Left, mods);
                Settle();
            }

            public void Key(PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None)
            {
                Window.KeyPressQwerty(key, mods);
                Window.KeyReleaseQwerty(key, mods);
                Settle();
            }

            public void Undo() => Key(PhysicalKey.Z, Cmd);

            public void Redo()
            {
                if (OperatingSystem.IsMacOS()) Key(PhysicalKey.Z, Cmd | RawInputModifiers.Shift);
                else Key(PhysicalKey.Y, Cmd);
            }

            public T Button<T>(string name) where T : Avalonia.Controls.Control
            {
                Settle();
                var found = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(Window).OfType<T>().FirstOrDefault(c => Avalonia.Automation.AutomationProperties.GetName(c) == name && c.IsEffectivelyVisible);
                Assert.That(found, Is.Not.Null, "a control named " + name);
                return found!;
            }

            public void Press(string name) => Click(Of(Button<Avalonia.Controls.Control>(name)));

            /// <summary>Renders whole ticks as the audio thread would, transport as it is.</summary>
            public void RunTicks(int ticks)
            {
                for (int i = 0; i < ticks; i++)
                {
                    var mi = ReBuzzCore.masterInfo;
                    int frames = Math.Max(1, mi.SamplesPerTick - mi.PosInTick);
                    while (frames > 0)
                    {
                        int n = Math.Min(frames, buffer.Length / 2);
                        work.MainAudioFillBuffer(buffer, 0, n * 2);
                        frames -= n;
                    }
                }
                Dispatcher.UIThread.RunJobs();
            }

            /// <summary>Renders until the session clock is at the start of tick <paramref name="clock"/>.</summary>
            public void PlayTo(long clock)
            {
                int guard = 0;
                while (Core.Session.Clock < clock && guard++ < 100000) RunTicks(1);
                Assert.That(Core.Session.Clock, Is.EqualTo(clock));
            }

            public void Menu(string header)
            {
                var item = View.OpenMenu.Items.OfType<MenuItem>().First(i => (string)i.Header! == header);
                Assert.That(item.IsEnabled, Is.True, header + " is enabled");
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Settle();
            }

            public void Dispose()
            {
                Core.ExecuteCommand(BuzzCommand.Stop);
                Core.Arrangement?.Takes.Reset();
                Window.Close();
                Settle();
                SessionCore.Timestamp = System.Diagnostics.Stopwatch.GetTimestamp;
                Dialogs.Responder = null;
                Core.Looping = looping;
                Core.EngineSettings.SubTickTiming = subTicks;
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
            }
        }

        static float[] Render(int ticks)
        {
            foreach (var m in Core.SongCore.MachinesList)
            {
                Core.MachineManager.Stop(m);
                m.ManagedMachine?.GetType().GetMethod("ResetForRender", Type.EmptyTypes)?.Invoke(m.ManagedMachine, null);
            }
            var sub = ReBuzzCore.subTickInfo;
            sub.PosInSubTick = 0; sub.CurrentSubTick = 0; sub.SubTickReminderCounter = 0;
            Core.Looping = false;
            Core.SongCore.PlayPosition = 0;
            ReBuzzCore.masterInfo.PosInTick = 0;
            int blocks = (int)Math.Ceiling(ticks * (double)ReBuzzCore.masterInfo.SamplesPerTick / 256) + 200;
            var work = new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
            var all = new float[blocks * 512];
            var buffer = new float[512];
            Core.Playing = true;
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
            return all;
        }

        static void SongLength(int ticks)
        {
            Core.SongCore.SongEnd = ticks;
            Core.SongCore.LoopEnd = ticks;
        }

        static ClipNote[] Notes(IPattern p) => Core.SessionLayout.NotesOf(p).ToArray();

        /// <summary>The notes a take of pass <paramref name="pass"/> holds: two a bar, in pass-dependent pitches.</summary>
        static IEnumerable<(int Tick, int Note)> Played(int pass, int bars) =>
            Enumerable.Range(0, bars).SelectMany(b => new[] { (b * Bar, 40 + 12 * pass + 2 * b), (b * Bar + 8, 47 + 12 * pass + 2 * b) });

        // ------------------------------------------------------------------ the exit test

        [AvaloniaTest]
        public void ThreeLoopPassesRecordedAsTakesAndABarCompedFromEachRenderBitForBitAsTheSameNotesWrittenPlain()
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            var machine = lead.Machine;
            SongLength(3 * Bar);
            Core.SongCore.PlayPosition = 0;

            rig.Press("Arm Lead for recording");
            Assert.That(Core.Arrangement.Takes.IsArmed(machine), Is.True, "the header's R arms the track");
            rig.Press("Loop recording");
            rig.Press("Record arrangement");
            Assert.That(Core.Arrangement.Takes.IsRecording, Is.True, "Record starts: " + rig.View.StatusLine.Text + " / " + Core.Arrangement.Takes.Status);
            Assert.That(Core.Arrangement.Takes.IsLoopRecording, Is.True);

            // A scripted MIDI source plays three passes of the three-bar loop: two notes a bar, other pitches in each pass. Each message
            // is stamped on the MIDI thread's side with where the song was when it was heard.
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (var (tick, note) in Played(pass, 3))
                {
                    rig.PlayTo(pass * 3 * Bar + tick);
                    Core.SendMIDIInput(On(note));
                    rig.PlayTo(pass * 3 * Bar + tick + 4);
                    Core.SendMIDIInput(Off(note));
                }
            }
            rig.PlayTo(9 * Bar);
            rig.Press("Record arrangement");
            Assert.That(Core.Arrangement.Takes.IsRecording, Is.False);
            Assert.That(Core.Arrangement.Takes.Status, Is.Null.Or.Empty, "no complaint");
            Settle();

            var takes = rig.TakeLanes("Lead");
            Assert.That(takes.Count, Is.EqualTo(3), "each pass is a take on a take lane of its own");
            Assert.That(rig.Lane("Lead").Events, Is.Empty, "the main lane holds nothing until it is comped");
            Assert.That(Core.Song.Sequences.Where(s => s.Machine == machine).Count(), Is.EqualTo(4));
            var patterns = new List<IPattern>();
            for (int pass = 0; pass < 3; pass++)
            {
                var clip = takes[pass].Events.Single();
                Assert.That(clip.Key, Is.EqualTo(0), "a take is placed where the loop starts");
                Assert.That(clip.Value.Pattern.Length, Is.EqualTo(3 * Bar), "and is the loop's length");
                patterns.Add(clip.Value.Pattern);
                var want = Played(pass, 3).Select(n => new ClipNote(n.Tick * TB, 4 * TB, n.Note, 99)).OrderBy(n => n.Time).ToArray();
                Assert.That(Notes(clip.Value.Pattern).OrderBy(n => n.Time).ToArray(), Is.EqualTo(want), $"take {pass + 1}: each note where it was played, as long as it was held");
            }
            Assert.That(Core.Arrangement.ToXml(), Is.Not.Null, "the lanes' roles are saved with the song");

            // Take lanes are never played: the song is silent until a bar is comped.
            var silent = Render(3 * Bar);
            Assert.That(silent.Max(Math.Abs), Is.LessThan(1e-6f), "takes are heard only through the main lane");

            // One undo step took them all in; one brings them back.
            rig.Undo();
            Assert.That(rig.TakeLanes("Lead"), Is.Empty, "undo takes the takes and their lanes away in one step");
            Assert.That(machine.Patterns.Count, Is.EqualTo(0));
            rig.Redo();
            takes = rig.TakeLanes("Lead");
            Assert.That(takes.Count, Is.EqualTo(3), "redo brings them back");
            patterns = takes.Select(t => t.Events.Single().Value.Pattern).ToList();       // made again by the redo
            Assert.That(Notes(takes[1].Events.Single().Value.Pattern).Length, Is.EqualTo(6));
            Assert.That(Render(3 * Bar).Max(Math.Abs), Is.LessThan(1e-6f), "and they are still not played");

            // Comp one bar from each take with Shift and a click on the take's bar.
            for (int pass = 0; pass < 3; pass++)
                rig.Click(rig.At(takes[pass], pass * Bar + 5), mods: RawInputModifiers.Shift);
            var main = rig.Lane("Lead");
            Assert.That(main.Events.Count, Is.EqualTo(3), "a trimmed event for each bar; no notes were copied");
            Assert.That(main.Events.Select(e => (e.Key, e.Value.Offset, e.Value.End)), Is.EqualTo(new[] { (0, 0, Bar), (Bar, Bar, 2 * Bar), (2 * Bar, 2 * Bar, 0) }));
            Assert.That(main.Events.Values.Select(e => e.Pattern).ToList(), Is.EqualTo(patterns), "bar 1 from take 1, bar 2 from take 2, bar 3 from take 3");
            Studio.Screenshot(rig.Window, "ar5-takes-comp");

            var comped = Render(3 * Bar);
            Assert.That(comped.Max(Math.Abs), Is.GreaterThan(0.01f), "the comp makes sound");

            // The same notes written plain: one three-bar pattern, no takes, no comp.
            foreach (var t in main.Events.Keys.ToList()) main.SetEvent(t, null);
            machine.CreatePattern("Plain", 3 * Bar);
            var plain = machine.Patterns.First(p => p.Name == "Plain");
            var column = SessionLayout.EnsureMidiColumn(plain);
            column.SetEvents(Enumerable.Range(0, 3).SelectMany(b => Played(b, 3).Where(n => n.Tick / Bar == b)).Select(n => new PatternEvent(n.Tick * TB, On(n.Note), 4 * TB)), true);
            main.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, plain));
            var written = Render(3 * Bar);
            Assert.That(written, Is.EqualTo(comped), "the comp renders bit for bit as the same notes written plain");

            // And not as a comp of other bars.
            main.SetEvent(0, null);
            var other = takes[0].Events.Single().Value.Pattern;
            main.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, other));
            Assert.That(Render(3 * Bar), Is.Not.EqualTo(comped), "it is the comp that is heard, not one take");
        }
    }
}
