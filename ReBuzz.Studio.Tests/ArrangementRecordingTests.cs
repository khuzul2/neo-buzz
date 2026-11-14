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
            public void RunTicks(int ticks, bool drain = true)
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
                if (drain) Dispatcher.UIThread.RunJobs();
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
                View.OpenMenu.Close();                      // as choosing an item does
                Settle();
            }

            public void Dispose()
            {
                Core.ExecuteCommand(BuzzCommand.Stop);
                Core.Arrangement?.Takes.Reset();
                if (Core.Arrangement != null) Core.Arrangement.Takes.Overdub = false;
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

        static void Fill(IPattern p, int length)
        {
            var notes = Enumerable.Range(0, length / 2).Select(i => new ClipNote(i * 2 * TB, TB, 48 + 3 * (i % 7), 100)).ToArray();
            Core.SessionLayout.EditNotes(p, null, notes);
        }

        /// <summary>A take lane holding a take of <paramref name="length"/> ticks whose notes are <paramref name="note"/> and up, one a beat.</summary>
        static ISequence MakeTake(IMachine imachine, string name, int length, int note)
        {
            var machine = (MachineCore)imachine;
            Core.SongCore.AddSequence(machine, Core.SongCore.SequencesList.Count);
            var lane = Core.SongCore.Sequences.Last();
            Core.Arrangement.SetRole(lane, LaneRole.Take);
            machine.CreatePattern(name, length);
            var take = machine.Patterns.First(p => p.Name == name);
            Core.SessionLayout.EditNotes(take, null, Enumerable.Range(0, length / 4).Select(i => new ClipNote(i * 4 * TB, 2 * TB, note + i, 90)).ToArray());
            lane.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, take));
            return lane;
        }

        // ------------------------------------------------------------------ one pass, overdub, the MIDI thread's time

        [AvaloniaTest]
        public void APassRecordedFromTheMiddleOfTheSongIsAClipWhereItBeganEndingWhereItStoppedWithTheNotesAtTheTimesTheMidiThreadHeardAndOverdubAddsToItsPattern()
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            var machine = lead.Machine;
            SongLength(64);
            Core.Looping = false;
            Core.SongCore.PlayPosition = 8;

            Assert.That(Core.Arrangement.Takes.Start(false), Is.EqualTo("Arm a track first."), "an unarmed song doesn't record");
            rig.Press("Arm Lead for recording");
            rig.Press("Record arrangement");
            Assert.That(Core.Arrangement.Takes.IsRecording, Is.True);
            rig.PlayTo(10);
            Core.SendMIDIInput(On(60));
            // The UI is busy for three ticks: the stamp was made when the message was heard, not when the UI looked.
            rig.RunTicks(3, drain: false);
            Core.SendMIDIInput(Off(60));
            rig.RunTicks(1, drain: false);
            Core.SendMIDIInput(0xB0 | (1 << 8) | (64 << 16));          // the mod wheel, at tick 14
            rig.RunTicks(3, drain: false);
            Dispatcher.UIThread.RunJobs();
            rig.PlayTo(24);
            Assert.That(Core.Arrangement.Takes.IsRecording, Is.True);
            rig.Press("Record arrangement");
            Assert.That(Core.Arrangement.Takes.IsRecording, Is.False);
            Assert.That(Core.Session.Clock, Is.EqualTo(-1).Or.LessThan(0), "stopping records stops the transport");

            var item = new ClipEditor(Core).Read(lead).Single();
            Assert.That((item.Time, item.End), Is.EqualTo((8, 24)), "a clip from where recording began to where it stopped");
            var pattern = item.Pattern;
            Assert.That(pattern.Length, Is.EqualTo(16));
            Assert.That(Notes(pattern), Is.EqualTo(new[] { new ClipNote(2 * TB, 3 * TB, 60, 99) }), "the note where it was heard, as long as it was held (the UI's clock is not read)");
            var events = pattern.Columns.Single().GetEvents(int.MinValue, int.MaxValue).OrderBy(e => e.Time).ToList();
            Assert.That(events.Select(e => e.Time), Is.EqualTo(new[] { 2 * TB, 6 * TB }), "the controller too, at its own time");
            Assert.That(machine.Patterns.Count, Is.EqualTo(1));

            rig.Undo();
            Assert.That(lead.Events, Is.Empty, "undo takes the clip");
            Assert.That(machine.Patterns.Count, Is.EqualTo(0), "and its pattern");
            rig.Redo();
            Assert.That(lead.Events.Single().Key, Is.EqualTo(8));
            Assert.That(Notes(lead.Events.Single().Value.Pattern).Single().Note, Is.EqualTo(60), "redo brings it back with its notes");

            // Overdub: recording over the clip adds to its pattern, at pattern time.
            pattern = lead.Events.Single().Value.Pattern;
            Core.SongCore.PlayPosition = 8;
            rig.Press("Overdub");
            Assert.That(Core.Arrangement.Takes.Overdub, Is.True);
            rig.Press("Record arrangement");
            Assert.That(Core.Arrangement.Takes.IsRecording, Is.True, rig.View.StatusLine.Text);
            rig.PlayTo(12);
            Core.SendMIDIInput(On(64));
            rig.PlayTo(14);
            Core.SendMIDIInput(Off(64));
            rig.PlayTo(20);
            rig.Press("Record arrangement");
            Assert.That(machine.Patterns.Count, Is.EqualTo(1), "no new pattern");
            Assert.That(lead.Events.Count, Is.EqualTo(1), "and no new clip");
            Assert.That(Notes(pattern).OrderBy(n => n.Time).ToArray(), Is.EqualTo(new[] { new ClipNote(2 * TB, 3 * TB, 60, 99), new ClipNote(4 * TB, 2 * TB, 64, 99) }), "the new note at pattern time (tick 12 of the song is tick 4 of the clip)");
            rig.Undo();
            Assert.That(Notes(pattern).Single().Note, Is.EqualTo(60), "an overdub is one undo step");
        }

        [AvaloniaTest]
        public void NotesAreStampedWithTheHeardTimeNotTheRenderedOneWhenTheOutputHasLatency()
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            SongLength(64);
            Core.Looping = false;
            int latency = Core.OutputLatencySamples;
            try
            {
                Core.OutputLatencySamples = 2 * ReBuzzCore.masterInfo.SamplesPerTick;       // what is heard is two ticks behind what is rendered
                rig.Press("Arm Lead for recording");
                rig.Press("Record arrangement");
                rig.PlayTo(12);
                Core.SendMIDIInput(On(60));
                rig.PlayTo(16);
                Core.SendMIDIInput(Off(60));
                rig.PlayTo(20);
                rig.Press("Record arrangement");
                var pattern = new ClipEditor(Core).Read(lead).Single().Pattern;
                Assert.That(Notes(pattern), Is.EqualTo(new[] { new ClipNote(10 * TB, 4 * TB, 60, 99) }), "played at 12 while 2 ticks were still to be heard: tick 10");
            }
            finally
            {
                Core.OutputLatencySamples = latency;
            }
        }

        [AvaloniaTest]
        public void RecordingOverASharedPatternSaysSoAndNothingPlayedChangesNothing()
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            SongLength(64);
            Core.Looping = false;
            rig.DoubleClick(rig.At(lead, 0, 2));
            var pattern = lead.Machine.Patterns.Single();
            Fill(pattern, 16);
            var editor = new ClipEditor(Core);
            Assert.That(editor.Place(lead, pattern, 32), Is.Null, "the same pattern again later: shared");
            var before = Render(48);
            Assert.That(before.Max(Math.Abs), Is.GreaterThan(0.01f));
            Assert.That(Core.Arrangement.ToXml(), Is.Null);

            // Nothing played: the song is as it was, with no undo step.
            bool couldUndo = Core.SongCore.ActionStack.CanUndo;
            Core.SongCore.PlayPosition = 0;
            rig.Press("Arm Lead for recording");
            rig.Press("Record arrangement");
            rig.PlayTo(6);
            rig.Press("Record arrangement");
            Assert.That(Core.Arrangement.Takes.Status, Does.Contain("Nothing"));
            Assert.That(Core.SongCore.ActionStack.CanUndo, Is.EqualTo(couldUndo), "no step for nothing");
            Assert.That(Core.Arrangement.ToXml(), Is.Null, "an old song still saves without an arrangement subsection");
            Assert.That(Render(48), Is.EqualTo(before), "and renders as it did");

            // Overdub over the shared pattern: it is written, and the status line says whose it is.
            Core.SongCore.PlayPosition = 0;
            rig.Press("Overdub");
            rig.Press("Record arrangement");
            rig.PlayTo(4);
            Core.SendMIDIInput(On(72));
            rig.PlayTo(6);
            Core.SendMIDIInput(Off(72));
            rig.PlayTo(10);
            rig.Press("Record arrangement");
            Assert.That(Notes(pattern).Any(n => n.Note == 72 && n.Time == 4 * TB), Is.True, string.Join(" ", Notes(pattern)) + " / " + Core.Arrangement.Takes.Status + " / patterns " + lead.Machine.Patterns.Count + " clips " + string.Join(",", lead.Events.Keys));
            Assert.That(Core.Arrangement.Takes.Status, Does.Contain("2 places"), "the warning: " + Core.Arrangement.Takes.Status);
        }

        // ------------------------------------------------------------------ comping

        [AvaloniaTest]
        public void CompingByShiftDragByTheMenuAndByTheKeyPutsTrimmedEventsOnTheMainLaneAndUndoPutsThemBack()
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            var machine = lead.Machine;
            SongLength(48);
            var one = MakeTake(machine, "Take A", 48, 40);
            var two = MakeTake(machine, "Take B", 48, 60);
            Settle();
            rig.Click(rig.At(one, 3));                      // a click selects a take, as any clip
            Assert.That(rig.Canvas.Selected?.Sequence, Is.SameAs(one));

            // Shift-drag over a range of the take (snapped to beats): from beat 5 to beat 9.
            rig.Drag(rig.At(two, 21, 1), rig.At(two, 33, 1), RawInputModifiers.Shift);
            var events = lead.Events;
            Assert.That(events.Count, Is.EqualTo(1));
            Assert.That((events.Keys.Single(), events.Values.Single().Offset, events.Values.Single().End), Is.EqualTo((20, 20, 32)), "the range, trimmed: the take's pattern is not copied");
            Assert.That(events.Values.Single().Pattern.Name, Is.EqualTo("Take B"));
            Assert.That(machine.Patterns.Count, Is.EqualTo(2), "no notes or patterns were made");

            // The menu comps the bar at the cursor of the selected take over what the main lane holds there.
            rig.Click(rig.At(one, 26), MouseButton.Left);
            rig.Click(rig.At(one, 26), MouseButton.Right);
            rig.Menu("Comp to Main Lane");
            Assert.That(lead.Events.Select(e => (e.Key, e.Value.Pattern.Name, e.Value.Offset, e.Value.End)), Is.EqualTo(new[] { (16, "Take A", 16, 32) }),
                "bar 2 of take A replaced the part of the first comp it overlaps; of that comp's earlier part nothing is left before bar 2");
            rig.Undo();
            Assert.That(lead.Events.Select(e => (e.Key, e.Value.Offset, e.Value.End)), Is.EqualTo(new[] { (20, 20, 32) }), "undo gives the first comp back, whole");

            // The key C does the same for the selected take.
            rig.Click(rig.At(two, 40));
            Assert.That(rig.Canvas.Selected?.Sequence, Is.SameAs(two), "selected after click");
            rig.Key(PhysicalKey.C);
            Assert.That(lead.Events.Select(e => (e.Key, e.Value.Pattern.Name)).ToArray(), Is.EqualTo(new[] { (20, "Take B"), (32, "Take B") }), "bar 3 of take B after the first comp: " + string.Join(" ", lead.Events.Select(e => $"{e.Key}:{e.Value.Pattern.Name}:{e.Value.Offset}-{e.Value.End}")) + " / " + rig.View.StatusLine.Text);
            rig.Undo();
            rig.Undo();
            Assert.That(lead.Events, Is.Empty);

            // A comp has to come from a take lane.
            Assert.That(new ClipEditor(Core).Comp(lead, 0, 16, lead), Does.Contain("take lane"));
            rig.Click(rig.At(lead, 4));
            Assert.That(rig.Canvas.CompAtCursor(), Is.False);
        }

        // ------------------------------------------------------------------ launching, and going back

        [AvaloniaTest]
        public void AnArrangementClipLaunchesInTheSessionWithItsRegionAndTheArrangementTakesTheTrackBack()
        {
            using var rig = new Rig("Lead", "Bass");
            var lead = rig.Lane("Lead");
            SongLength(64);
            Core.Looping = false;
            rig.DoubleClick(rig.At(lead, 0, 2));
            var riff = lead.Machine.Patterns.Single();
            Fill(riff, 16);
            rig.Drag(rig.At(lead, 16, -2), rig.At(lead, 48));        // the clip loops to bar 4
            var bass = rig.Lane("Bass");
            rig.DoubleClick(rig.At(bass, 0, 2));
            Fill(bass.Machine.Patterns.Single(), 16);
            rig.Drag(rig.At(bass, 16, -2), rig.At(bass, 48));
            var arranged = Render(48);
            Assert.That(arranged.Max(Math.Abs), Is.GreaterThan(0.01f));

            // Trim the left edge so the clip starts partway into its pattern: that region is what the session plays.
            rig.Drag(rig.At(lead, 0, 2), rig.At(lead, 4, 0));
            var item = rig.Canvas.ItemsOf(lead).First();
            Assert.That(item.Spec.Start, Is.EqualTo(4));
            rig.Click(rig.At(lead, 20));
            rig.Click(rig.At(lead, 20), MouseButton.Right);
            rig.Menu("Launch in Session");
            Core.SongCore.PlayPosition = 0;
            Core.Playing = true;
            rig.PlayTo(Bar);
            rig.RunTicks(2);
            var session = Core.SessionCore;
            Assert.That(session.GetPlaying(lead), Is.SameAs(riff), "the clip's pattern plays on its lane's track");
            Assert.That(session.IsArrangementOverridden(lead), Is.True, "the track is the session's now");
            Assert.That(session.IsArrangementOverridden(bass), Is.False, "other tracks play the arrangement");
            Assert.That(session.GetClipPosition(lead), Is.GreaterThanOrEqualTo(4), "from the clip's region, not the pattern's start");
            Assert.That(Core.Arrangement.Links.AnyLaunched, Is.True);
            Settle();
            Studio.Screenshot(rig.Window, "ar5-launched");

            rig.Press("Back to arrangement");
            rig.RunTicks(2);
            Assert.That(session.IsArrangementOverridden(lead), Is.False, "back to the arrangement; launched " + Core.Arrangement.Links.AnyLaunched + " / " + session.GetPlaying(lead)?.Name);
            Assert.That(Core.Arrangement.Links.AnyLaunched, Is.False);
            Assert.That(session.GetPlaying(lead), Is.Null);
            Core.Playing = false;
            Settle();

            // The song is as it was, trimmed: launching changed nothing in it.
            Assert.That(rig.Canvas.ItemsOf(lead).First().Spec.Start, Is.EqualTo(4));
            Assert.That(lead.Events.Count, Is.EqualTo(rig.Canvas.ItemsOf(lead).First().EventTimes.Count));
            var again = Render(48);
            Assert.That(again.Max(Math.Abs), Is.GreaterThan(0.01f));
            Assert.That(Render(48), Is.EqualTo(again), "and renders the same each time");
        }

        // ------------------------------------------------------------------ between the session and the arrangement

        [AvaloniaTest]
        public void ASlotGoesToTheArrangementAndAClipBackToASlotKeepingTheNotesAndGivingEachPlaceItsOwnClipId()
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            var machine = lead.Machine;
            SongLength(64);
            Core.Looping = false;
            machine.CreatePattern("Riff", 16);
            var riff = machine.Patterns.First(p => p.Name == "Riff");
            Fill(riff, 16);
            var notes = Notes(riff);
            var layout = Core.SessionLayout;
            layout.Ensure();
            layout.SetSlot(0, 0, riff);
            layout.SetClipSettings(riff, new SessionDocument.ClipSettings { Loop = true, Start = 2, LoopStart = 4, LoopEnd = 12 });

            // Session -> arrangement: the slot's menu. The clip is the same pattern, with the slot's region, and an id of its own.
            var tab = rig.Window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Session");
            tab.IsSelected = true;
            Settle();
            var sessionView = (Views.Session.SessionView)tab.Content!;
            var cell = sessionView.SlotAt(0, 0);
            Assert.That(cell.Pattern, Is.SameAs(riff));
            sessionView.ShowSlotMenu(cell);
            Assert.That(sessionView.ClickMenuItem("Send to Arrangement"), Is.True);
            var editor = new ClipEditor(Core);
            var first = editor.Read(lead).Single();
            Assert.That(first.Pattern, Is.SameAs(riff), "the same pattern: a place, not a copy of the notes");
            Assert.That(first.Time, Is.EqualTo(0));
            Assert.That(first.Spec.Id, Is.GreaterThan(0));
            Assert.That((first.Spec.Start, first.Spec.LoopStart, first.Spec.LoopEnd, first.Spec.Loop), Is.EqualTo((2, 4, 12, true)), "its region from the slot's clip settings");
            Assert.That(editor.Uses(riff), Is.EqualTo(2), "the slot and the clip");
            Assert.That(Notes(riff), Is.EqualTo(notes), "the notes are untouched");
            Assert.That(Core.Clips.TryGetLoop(first.Spec.Id, out var loop), Is.True);
            Assert.That(loop, Is.EqualTo(new ClipLoop(4, 12, true)), "the loop is the clip's, kept as §8.1 keeps it");

            // Arrangement -> session: the clip's menu gives the pattern to the first empty slot of its track.
            tab = rig.Window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Arrangement");
            tab.IsSelected = true;
            Settle();
            rig.Click(rig.At(lead, 3));
            rig.Click(rig.At(lead, 3), MouseButton.Right);
            rig.Menu("Send to Session Slot");
            Assert.That(layout.PatternAt(0, 1), Is.SameAs(riff), "scene 2 was the first empty slot");
            Assert.That(layout.PatternAt(0, 0), Is.SameAs(riff));
            rig.Undo();
            Assert.That(layout.PatternAt(0, 1), Is.Null, "one undo step takes the slot back");
            rig.Redo();
            Assert.That(layout.PatternAt(0, 1), Is.SameAs(riff));

            // ...and from that slot to the arrangement again: after the lane's last clip, a clip id of its own, the same region and notes.
            Assert.That(Core.Arrangement.Links.SlotToLane(0, 1), Is.Null);
            var clips = editor.Read(lead);
            Assert.That(clips.Count, Is.EqualTo(2));
            var second = clips[1];
            Assert.That(second.Time, Is.EqualTo(clips[0].End), "after the first clip");
            Assert.That(second.Spec.Id, Is.GreaterThan(first.Spec.Id), "ids are never reused: each place has its own");
            Assert.That(clips[0].Spec.Id, Is.EqualTo(first.Spec.Id), "the first keeps its id");
            Assert.That((second.Spec.Start, second.Spec.LoopStart, second.Spec.LoopEnd, second.Spec.Loop), Is.EqualTo((2, 4, 12, true)));
            Assert.That(second.Pattern, Is.SameAs(riff));
            Assert.That(Notes(riff), Is.EqualTo(notes), "the notes went there and back unchanged");
            Assert.That(editor.Uses(riff), Is.EqualTo(4), "two slots and two clips");
            Assert.That(Core.Arrangement.Links.SlotToLane(0, 5), Is.EqualTo("The slot is empty."));
        }

        // ------------------------------------------------------------------ old songs, and the saved takes

        [AvaloniaTest]
        public void TakeLanesSurviveASaveAndStayUnplayedAndASongWithoutAnyRendersTheSameAfterTheRecorderWasUsed([Values(".bmx", ".bmxml")] string extension)
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            var machine = lead.Machine;
            SongLength(48);
            Core.Looping = false;
            rig.DoubleClick(rig.At(lead, 0, 2));
            Fill(machine.Patterns.Single(), 16);
            rig.Drag(rig.At(lead, 16, -2), rig.At(lead, 48));
            var old = Render(48);
            Assert.That(Core.Arrangement.ToXml(), Is.Null, "an old song has no arrangement subsection");

            MakeTake(machine, "Take A", 48, 70);
            Assert.That(Core.Arrangement.ToXml(), Is.Not.Null);
            Assert.That(Render(48), Is.EqualTo(old), "a take lane is not played: the song renders as it did");

            string path = Path.Combine(Path.GetTempPath(), $"rebuzz-ar5-{Environment.ProcessId}{extension}");
            try
            {
                Core.SaveSongCopy(path);
                Core.Modified = false;
                Core.OpenSongFile(path);
                Settle();
                var lanes = Core.Song.Sequences.Where(s => s.Machine.Name == "Lead").ToList();
                Assert.That(lanes.Select(s => Core.Arrangement.RoleOf(s)), Is.EqualTo(new[] { LaneRole.Main, LaneRole.Take }), "the role comes back with the song");
                Assert.That(((SequenceCore)lanes[1]).TakeLane, Is.True, "and the engine skips the lane");
                Assert.That(lanes[1].Events.Count, Is.EqualTo(1));
                Assert.That(Render(48), Is.EqualTo(old), "the reopened song renders as it did");
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ------------------------------------------------------------------ nothing allocated

        [AvaloniaTest]
        public void RecordingAllocatesNothingOnTheAudioThreadAndTheMidiThreadsStampingAddsNothing()
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            var machine = lead.Machine;
            SongLength(32);
            Core.Looping = true;
            rig.DoubleClick(rig.At(lead, 0, 2));
            Fill(machine.Patterns.Single(), 16);
            rig.Drag(rig.At(lead, 16, -2), rig.At(lead, 32));
            MakeTake(machine, "Take A", 32, 55);
            rig.Press("Arm Lead for recording");
            Assert.That(Core.Arrangement.Takes.Start(true), Is.Null);
            int note = 0;
            using (var audio = new AudioThread(1))
            {
                // Every 20th buffer a note goes in or off (from the thread that renders, outside the measured call).
                int n = 0;
                Action perBuffer = () =>
                {
                    if (n++ % 20 != 0) return;
                    Core.SendMIDIInput(n % 40 == 1 ? On(48 + note++ % 12) : Off(48 + (note - 1) % 12));
                };
                var cost = Steady(audio, 400, perBuffer);
                AssertNoAllocation(cost, "playing the arrangement while recording takes (armed, windows open, a take lane that is skipped, notes arriving)");

                // The MIDI thread: the same message costs the same with the windows open and without.
                long Bytes()
                {
                    for (int i = 0; i < 50; i++) { Core.SendMIDIInput(On(60)); Core.SendMIDIInput(Off(60)); }
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 200; i++) { Core.SendMIDIInput(On(60)); Core.SendMIDIInput(Off(60)); }
                    return GC.GetAllocatedBytesForCurrentThread() - before;
                }
                long recording = Bytes();
                Core.SessionCore.SetArrangementWindows(null);
                long without = Bytes();
                TestContext.Out.WriteLine($"[notice] MIDI thread: {recording} bytes for 400 messages with the arrangement windows open, {without} without");
                Assert.That(recording, Is.LessThanOrEqualTo(without), "stamping a message for the arrangement allocates nothing");
            }
            Core.Arrangement.Takes.Reset();
            lock (ReBuzzCore.AudioLock) Core.Playing = false;
        }

        // ------------------------------------------------------------------ the kit

        [AvaloniaTest]
        public void TheRecordingControlsAreOnTheKitsStyleHaveNamesDrawInLightAndDarkAndLetGoWhenTheWindowCloses()
        {
            var kind = StudioTheme.Kind;
            using var restore = Studio.AfterTest(() => { StudioTheme.Kind = kind; StudioTheme.Refresh(); });
            var rig = new Rig("Lead");
            bool disposed = false;
            try
            {
                var lead = rig.Lane("Lead");
                SongLength(48);
                MakeTake(lead.Machine, "Take A", 48, 40);
                rig.Press("Arm Lead for recording");
                rig.Press("Record arrangement");
                rig.PlayTo(6);
                rig.RunTicks(1);
                Settle();
                Studio.Screenshot(rig.Window, "ar5-recording");
                rig.Press("Record arrangement");
                rig.Window.Detail.Close();

                var problems = new List<string>();
                var all = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(rig.View).OfType<Avalonia.Controls.Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0).ToList();
                foreach (var c in all)
                {
                    if (c.GetType() != typeof(Button) && c.GetType() != typeof(ToggleButton)) continue;
                    var b = (TemplatedControl)c;
                    string label = Avalonia.Automation.AutomationProperties.GetName(b);
                    if (string.IsNullOrEmpty(label)) { problems.Add("a button without a name: " + ((ContentControl)b).Content); continue; }
                    bool icon = b.Classes.Contains("icon");
                    var padding = icon ? StudioStyles.IconPadding : StudioStyles.ButtonPadding;
                    double font = icon ? StudioStyles.IconFontSize : StudioTokens.FontBody;
                    if (b.Padding != padding) problems.Add($"padding {b.Padding}: {label}");
                    if (b.FontSize != font) problems.Add($"font {b.FontSize}: {label}");
                    if (b.Bounds.Height < StudioStyles.ControlHeight - 0.5) problems.Add($"{b.Bounds.Height:0} high: {label}");
                    if (b.CornerRadius != new CornerRadius(StudioStyles.CornerRadius)) problems.Add($"radius: {label}");
                }
                Assert.That(problems, Is.Empty);
                foreach (var name in new[] { "Arm Lead for recording", "Record arrangement", "Loop recording", "Overdub", "Back to arrangement" })
                    Assert.That(all.Any(c => Avalonia.Automation.AutomationProperties.GetName(c) == name), Is.True, name);

                foreach (var theme in new[] { AppearanceTheme.Light, AppearanceTheme.Dark })
                {
                    StudioTheme.Kind = theme;
                    StudioTheme.Refresh();
                    Settle();
                    Studio.Screenshot(rig.Window, "ar5-recording-" + theme);
                }

                Assert.That(Listeners(Core.Arrangement.Takes, "Changed"), Is.GreaterThan(0), "the headers and the view listen to the recorder while shown");
                rig.Dispose();
                disposed = true;
                Assert.That(Listeners(Core.Arrangement.Takes, "Changed"), Is.EqualTo(0), "the closed window no longer listens to the recorder");
            }
            finally
            {
                if (!disposed) rig.Dispose();
            }
        }

        static int Listeners(object owner, string eventName)
        {
            var field = owner.GetType().GetField(eventName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (field?.GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
        }
    }
}
