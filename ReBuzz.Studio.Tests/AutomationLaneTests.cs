// Fork (docs/specs/daw-modes.md AR4): automation lanes in the arrangement, headless. A lane for a state parameter
// is added from a track's parameter list; clip automation, automation clips and the tracker's columns are stitched
// along the timeline; curves are drawn, moved and ramped with the mouse; overlaps are marked; knob moves are
// recorded into the armed lane. The exit test: a 16-bar filter sweep drawn over a looped one-bar clip renders
// bit for bit as the same values written as plain events.

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

namespace ReBuzz.Studio.Tests
{
    public class AutomationLaneTests
    {
        const int TB = PatternEvent.TimeBase;
        const string Cutoff = "F1 cutoff";

        static ReBuzzCore Core => Studio.Host.Core;

        static RawInputModifiers Cmd => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        static void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        sealed class Rig : IDisposable
        {
            public MainWindow Window { get; }
            public SequenceView View { get; }
            public ArrangementCanvas Canvas => View.Arrangement;
            readonly bool looping;
            readonly bool subTicks;

            public Rig(params string[] machines)
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                looping = Core.Looping;
                subTicks = Core.EngineSettings.SubTickTiming;
                Core.EngineSettings.SubTickTiming = false;      // a ramp is set on each tick; plain steps are one a tick
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

            public ArrangementRow ParamRow(string machine, string parameter)
            {
                Settle();
                var row = Canvas.Rows.FirstOrDefault(r => r.Kind == ArrangementRowKind.Parameter && r.Machine.Name == machine && r.Param.Parameter.Name == parameter);
                Assert.That(row, Is.Not.Null, $"a lane for {machine} {parameter}");
                return row!;
            }

            /// <summary>A tick (fractions allowed) and a value (0 lowest, 1 highest) on a parameter lane, in window pixels.</summary>
            public Point AtValue(ArrangementRow row, double tick, double fraction)
            {
                Settle();
                row = Canvas.Rows.First(r => r.Kind == ArrangementRowKind.Parameter && r.Machine == row.Machine && r.Param.Key == row.Param.Key);      // the rows are made again with every change
                var a = Canvas.ParameterArea(row);
                var p = Canvas.TranslatePoint(new Point(Canvas.Geometry.XOf(0) + tick * Canvas.Geometry.TickWidth, a.Y + (1 - fraction) * a.Height), Window);
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

            /// <summary>The "A" button of a track and the list it offers: picks a parameter by name.</summary>
            public void AddLane(string machine, string parameter)
            {
                Settle();
                Click(Of(View.Headers.Find<Button>($"Add automation lane for {machine}")!));
                Assert.That(View.Headers.LastPicker, Is.Not.Null, "the button offers the parameter list");
                Assert.That(View.Headers.LastPicker.Choose(parameter), Is.True, parameter + " is in the list");
                Settle();
            }

            public void Undo() => Key(PhysicalKey.Z, Cmd);

            public void Redo()
            {
                if (OperatingSystem.IsMacOS()) Key(PhysicalKey.Z, Cmd | RawInputModifiers.Shift);
                else Key(PhysicalKey.Y, Cmd);
            }

            public AutoLane Read(string machine, string parameter)
            {
                Settle();
                var row = ParamRow(machine, parameter);
                return AutomationReader.Read(Core, row.Machine, row.Param.Key);
            }

            public void Dispose()
            {
                Core.ExecuteCommand(BuzzCommand.Stop);
                Window.Close();
                Settle();
                Dialogs.Responder = null;
                Core.Looping = looping;
                Core.EngineSettings.SubTickTiming = subTicks;
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
            }
        }

        static void Notes(IPattern p, int length)
        {
            var notes = Enumerable.Range(0, length / 2).Select(i => new ClipNote(i * 2 * TB, TB, 48 + 3 * (i % 7), 100)).ToArray();
            Core.SessionLayout.EditNotes(p, null, notes);
        }

        static string Pts(AutoSegment s) => string.Join(" ", s.Points.Select(p => $"{p.Time / TB}:{p.Value}{(p.Ramp ? "/" : "-")}"));

        // ------------------------------------------------------------------ rendering

        static float[] Render(int ticks, IParameter trace = null, int[] traced = null)
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
                    // The parameter's value late in each tick: where a tick's steps have all been sent.
                    if (trace != null && Core.SongCore.PlayPosition < traced.Length && ReBuzzCore.masterInfo.PosInTick >= 64) traced[Core.SongCore.PlayPosition] = trace.GetValue(0);
                }
            }
            Core.Playing = false;
            Dispatcher.UIThread.RunJobs();
            return all;
        }

        /// <summary>Starts the transport the way the audio thread does (buffers until <c>Playing</c> is true); <see cref="AdvanceTo"/> plays on.</summary>
        static WorkManager StartTransport()
        {
            var work = new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
            Core.SongCore.PlayPosition = 0;
            ReBuzzCore.masterInfo.PosInTick = 0;
            Core.Playing = true;
            var buffer = new float[512];
            for (int i = 0; i < 40 && !Core.Playing; i++)
                lock (ReBuzzCore.AudioLock) work.MainAudioFillBuffer(buffer, 0, 512);
            Assert.That(Core.Playing, Is.True, "the transport runs");
            return work;
        }

        /// <summary>Plays buffers until the play position reaches a tick (a position set while playing waits for the next tick).</summary>
        static void AdvanceTo(WorkManager work, int tick)
        {
            var buffer = new float[512];
            for (int i = 0; i < 5000 && Core.Song.PlayPosition < tick; i++)
                lock (ReBuzzCore.AudioLock) work.MainAudioFillBuffer(buffer, 0, 512);
            Assert.That(Core.Song.PlayPosition, Is.EqualTo(tick));
        }

        // ------------------------------------------------------------------ lanes

        [AvaloniaTest]
        public void ALaneForAParameterIsAddedFromTheTracksListShowsUnderTheTrackAndIsSavedWithTheSong([Values(".bmx", ".bmxml")] string extension)
        {
            using var rig = new Rig("Lead", "Bass");
            Assert.That(Core.Arrangement.ToXml(), Is.Null, "no lanes, nothing differs from the default: no subsection");
            rig.AddLane("Lead", Cutoff);
            var row = rig.ParamRow("Lead", Cutoff);
            var rows = rig.Canvas.Rows.Where(r => r.Machine?.Name is "Lead" or "Bass").Select(r => r.Kind == ArrangementRowKind.Parameter ? "P:" + r.Param.Name : r.Machine.Name).ToList();
            Assert.That(rows, Is.EqualTo(new[] { "Lead", "P:" + Cutoff, "Bass" }), "the lane sits under its track's lanes");
            Assert.That(rig.View.Headers.Find<ToggleButton>($"Record {Cutoff} of Lead"), Is.Not.Null, "the lane's header has a record switch with a name");
            Assert.That(rig.View.Headers.Find<Button>($"Remove lane {Cutoff} of Lead"), Is.Not.Null);
            Assert.That(rig.View.Headers.LastPicker.Parameters.Any(p => p.Name == "Voice mode"), Is.True, "the list holds the machine's state parameters");
            Assert.That(rig.View.Headers.LastPicker.Parameters.Any(p => p.Name == "Note"), Is.False, "not the stateless ones");
            Assert.That(Core.Arrangement.ToXml(), Is.Not.Null);

            string path = Path.Combine(Path.GetTempPath(), $"rebuzz-ar4-{Environment.ProcessId}{extension}");
            try
            {
                Core.SaveSongCopy(path);
                Core.Modified = false;
                Core.OpenSongFile(path);
                Settle();
                Assert.That(Core.Arrangement.ParameterLanes.Select(l => l.Machine), Is.EqualTo(new[] { "Lead" }), "the lane comes back with the song");
                Assert.That(rig.Canvas.Rows.Count(r => r.Kind == ArrangementRowKind.Parameter), Is.EqualTo(1));
            }
            finally
            {
                File.Delete(path);
            }

            // The "×" takes the lane away.
            rig.Click(rig.Of(rig.View.Headers.Find<Button>($"Remove lane {Cutoff} of Lead")!));
            Assert.That(rig.Canvas.Rows.Any(r => r.Kind == ArrangementRowKind.Parameter), Is.False);
            Assert.That(Core.Arrangement.ToXml(), Is.Null, "back to the default");
        }

        // ------------------------------------------------------------------ the exit test

        [AvaloniaTest]
        public void ASixteenBarSweepDrawnOverALoopedOneBarClipRendersBitForBitAsTheSameValuesWrittenAsPlainEvents()
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            var machine = lead.Machine;
            Core.Looping = false;

            // A one-bar clip with notes, repeated to bar 16 by its right edge.
            rig.DoubleClick(rig.At(lead, 0, 2));
            Notes(machine.Patterns.Single(), 16);
            rig.Drag(rig.At(lead, 16, -2), rig.At(lead, 256));
            Core.SongCore.LoopEnd = 256;
            Core.SongCore.SongEnd = 256;
            Assert.That(lead.Events.Count, Is.EqualTo(16), "sixteen repeats of the bar");
            var notesName = machine.Patterns.Single().Name;

            // The sweep: a lane for the filter cutoff, a drag over 16 bars (an automation clip is drawn), a point added in the middle.
            rig.AddLane("Lead", Cutoff);
            var row = rig.ParamRow("Lead", Cutoff);
            rig.Drag(rig.AtValue(row, 1, 0.15), rig.AtValue(row, 256, 0.9));
            var autoLane = Core.Song.Sequences.Single(s => s.Machine == machine && Core.Arrangement.RoleOf(s) == LaneRole.Automation);
            Assert.That(autoLane.Events.Count, Is.EqualTo(1), "one automation clip, on a new automation lane");
            Assert.That(autoLane.Events.Keys.Single(), Is.EqualTo(0));
            Assert.That(autoLane.Events[0].Span, Is.EqualTo(256), "it spans the 16 bars");
            rig.Click(rig.AtValue(row, 128, 0.3));

            var lane = rig.Read("Lead", Cutoff);
            var seg = lane.Segments.Single(s => s.Source == AutoSource.AutomationClip);
            Assert.That(lane.Segments.Count, Is.EqualTo(1));
            Assert.That(seg.Points.Count, Is.EqualTo(3), "start, the added point, end: " + Pts(seg));
            Assert.That(seg.Points.Select(p => p.Ramp), Is.EqualTo(new[] { true, true, false }), "ramps to the next point");
            Assert.That(seg.Points[0].Time, Is.EqualTo(0));
            Assert.That(seg.Points[1].Time, Is.EqualTo(128 * TB));
            Assert.That(seg.Points[2].Time, Is.EqualTo(256 * TB - 1), "the last point is the pattern's last unit");
            Assert.That(seg.Points[0].Value, Is.InRange(130, 170), "drawn at about 15 %");
            Assert.That(seg.Points[1].Value, Is.InRange(270, 330));
            Assert.That(seg.Points[2].Value, Is.InRange(860, 940));
            Assert.That(lane.Overlaps, Is.Empty, "one writer: nothing overlaps");

            // The render as drawn (the engine ramps the points)...
            Core.SongCore.PlayPosition = 0;
            var drawn = Render(256);
            var again = Render(256);
            Assert.That(again, Is.EqualTo(drawn), "baseline: a render repeats exactly");
            Assert.That(drawn.Max(Math.Abs), Is.GreaterThan(0.01f), "the song makes sound");
            var cutoff = machine.ParameterGroups[1].Parameters.First(p => p.Name == Cutoff);
            Assert.That(cutoff.GetValue(0), Is.EqualTo(seg.Points[2].Value), "the sweep ends where it was drawn to end");

            // ...is, bit for bit, the render of the same points written as plain engine events: one 16-bar pattern with a ramp event
            // for each point, no clip id, no automation lane role, no arrangement data...
            string clipName = autoLane.Events[0].Pattern.Name;
            var key = rig.ParamRow("Lead", Cutoff).Param.Key;
            var drawnEvents = SessionLayout.ColumnFor(autoLane.Events[0].Pattern, key, create: false).GetEvents(int.MinValue, int.MaxValue).OrderBy(e => e.Time).ToArray();
            machine.CreatePattern("Plain", 256);
            var plain = machine.Patterns.First(p => p.Name == "Plain");
            SessionLayout.ColumnFor(plain, key, create: true).SetEvents(drawnEvents.Select(e => new PatternEvent(e.Time, e.Value, e.Duration)), true);
            autoLane.SetEvent(0, null);
            Core.Clips.Reset();
            Core.Arrangement.SetRole(autoLane, LaneRole.Main);
            autoLane.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, plain));
            var rampedValues = new int[256];
            var asPlain = Render(256, cutoff, rampedValues);
            Assert.That(asPlain, Is.EqualTo(drawn), "the sweep as drawn renders bit for bit as the same points written as plain events");

            // ...and the same values stepped once a tick (what the ramp sets at each tick start) are the same sound to within the
            // engine's block geometry: a step on a tick's first sample is sent with the next block when the tick's first block is
            // under 22 samples long (events fall in a block's window of whole 1/240 ticks), a few samples late.
            var values = Enumerable.Range(0, 256).Select(t => AutomationMath.ValueAt(seg.Points, t * TB) ?? seg.Points[0].Value).ToArray();
            Assert.That(values[0], Is.EqualTo(seg.Points[0].Value));
            Assert.That(values[128], Is.EqualTo(seg.Points[1].Value));
            Assert.That(values[64], Is.EqualTo((seg.Points[0].Value + seg.Points[1].Value) / 2).Within(1), "a ramp is the straight line between its points");
            machine.CreatePattern("Steps", 256);
            var steps = machine.Patterns.First(p => p.Name == "Steps");
            SessionLayout.ColumnFor(steps, key, create: true).SetEvents(values.Select((v, t) => new PatternEvent(t * TB, v)).Append(new PatternEvent(seg.Points[2].Time, seg.Points[2].Value)), true);      // a step each tick, and the last point
            autoLane.SetEvent(0, null);
            autoLane.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, steps));
            var steppedValues = new int[256];
            var stepped = Render(256, cutoff, steppedValues);
            Assert.That(steppedValues, Is.EqualTo(rampedValues), "the parameter takes the same value in every tick");
            Assert.That(rampedValues[128], Is.EqualTo(seg.Points[1].Value));
            double peak = drawn.Max(Math.Abs), worst = 0;
            int differing = 0;
            for (int i = 0; i < drawn.Length; i++)
            {
                double d = Math.Abs(stepped[i] - drawn[i]);
                if (d > 0) differing++;
                worst = Math.Max(worst, d);
            }
            TestContext.Out.WriteLine($"stepped vs ramped: {differing} of {drawn.Length} samples differ, largest difference {worst:0.000000} of a peak {peak:0.000}");
            Assert.That(worst, Is.LessThan(0.06 * peak), $"stepped once a tick is the same sound within the block geometry ({differing} samples differ, the worst by {worst:0.000000} of a peak of {peak:0.000})");

            // ...and not as the song without the sweep.
            autoLane.SetEvent(0, null);
            var without = Render(256);
            Assert.That(without, Is.Not.EqualTo(drawn), "the sweep is heard");
            Assert.That(clipName, Is.Not.EqualTo(notesName));
        }

        // ------------------------------------------------------------------ the mouse, undo

        [AvaloniaTest]
        public void PointsAreAddedMovedMadeStepsAndDeletedWithTheMouseAndKeysAndEveryEditIsOnTheSongsHistory()
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            rig.AddLane("Lead", Cutoff);
            var row = rig.ParamRow("Lead", Cutoff);

            // A click on an empty lane draws a one-bar clip with a point; undo takes the clip and its lane away, redo brings them back.
            int sequences = Core.Song.Sequences.Count;
            rig.Click(rig.AtValue(row, 20, 0.5));
            Assert.That(Core.Song.Sequences.Count, Is.EqualTo(sequences + 1), "an automation lane was added");
            var seg = rig.Read("Lead", Cutoff).Segments.Single();
            Assert.That(Pts(seg), Does.StartWith("0:"), "a point at the clip's start");
            Assert.That(seg.Start, Is.EqualTo(20 * TB), "a clip from the beat of the click: " + seg.Start);
            Assert.That(seg.End - seg.Start, Is.EqualTo(16 * TB), "one bar");
            rig.Undo();
            Assert.That(Core.Song.Sequences.Count, Is.EqualTo(sequences), "undo takes the lane away too, in one step");
            Assert.That(rig.Read("Lead", Cutoff).Segments, Is.Empty);
            rig.Redo();
            Assert.That(Core.Song.Sequences.Count, Is.EqualTo(sequences + 1));
            Assert.That(Core.Arrangement.RoleOf(Core.Song.Sequences.Last()), Is.EqualTo(LaneRole.Automation), "redo restores the lane's role");
            Assert.That(rig.Read("Lead", Cutoff).Segments.Single().Points.Count, Is.EqualTo(1));

            // Add a point by click, move it by a drag, make its line a step with R, delete it with Delete; each one step.
            rig.Click(rig.AtValue(row, 28, 0.8));
            var all = rig.Read("Lead", Cutoff).Segments;
            Assert.That(all.Count, Is.EqualTo(1), string.Join(" | ", all.Select(x => $"{x.Source} {x.Start / TB}-{x.End / TB} {Pts(x)}")) + " status: " + rig.Canvas.Status);
            seg = all.Single();
            Assert.That(seg.Points.Count, Is.EqualTo(2), Pts(seg));
            int v1 = seg.Points[1].Value;
            Assert.That(seg.Points[1].Time, Is.EqualTo(8 * TB), "at the beat of the click, in the clip's time");
            rig.Drag(rig.AtValue(row, 28, 0.8), rig.AtValue(row, 32, 0.2));
            seg = rig.Read("Lead", Cutoff).Segments.Single();
            Assert.That(seg.Points.Count, Is.EqualTo(2), "moved, not added: " + Pts(seg));
            Assert.That(seg.Points[1].Time, Is.EqualTo(12 * TB));
            Assert.That(seg.Points[1].Value, Is.LessThan(v1 - 300), "dragged down");
            rig.Undo();
            seg = rig.Read("Lead", Cutoff).Segments.Single();
            Assert.That((seg.Points[1].Time, seg.Points[1].Value), Is.EqualTo((8 * TB, v1)), "undo puts it back");
            rig.Redo();
            Assert.That(rig.Read("Lead", Cutoff).Segments.Single().Points[1].Time, Is.EqualTo(12 * TB), "and redo moves it again");

            rig.Click(rig.AtValue(row, 20, 0.5));            // select the first point
            Assert.That(rig.Canvas.SelectedPoint, Is.Not.Null, "a click on a point selects it");
            Assert.That(rig.Read("Lead", Cutoff).Segments.Single().Points[0].Ramp, Is.False, "a clip drawn with one point holds it");
            rig.Key(PhysicalKey.R);
            Assert.That(rig.Read("Lead", Cutoff).Segments.Single().Points[0].Ramp, Is.True, "R: the line after it goes straight to the next point");
            rig.Key(PhysicalKey.R);
            Assert.That(rig.Read("Lead", Cutoff).Segments.Single().Points[0].Ramp, Is.False, "R again: a step");

            rig.DoubleClick(rig.AtValue(row, 32, 0.2));
            Assert.That(rig.Read("Lead", Cutoff).Segments.Single().Points.Count, Is.EqualTo(1), "a double click on a point deletes it");
            rig.Undo();
            Assert.That(rig.Read("Lead", Cutoff).Segments.Single().Points.Count, Is.EqualTo(2));
            rig.Click(rig.AtValue(row, 32, 0.2));
            rig.Key(PhysicalKey.Delete);
            Assert.That(rig.Read("Lead", Cutoff).Segments.Single().Points.Count, Is.EqualTo(1), "Delete deletes the selected point");
        }

        [AvaloniaTest]
        public void ADragOnAnEmptyPlaceDrawsAClipFromBeatToBeatAndALoopedClipsColumnShowsOnEveryRepeat()
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            var machine = lead.Machine;
            rig.AddLane("Lead", Cutoff);
            var row = rig.ParamRow("Lead", Cutoff);

            // Dragged right to left: the value at the left end is where the drag ended.
            rig.Drag(rig.AtValue(row, 48, 0.9), rig.AtValue(row, 16, 0.1));
            var seg = rig.Read("Lead", Cutoff).Segments.Single();
            Assert.That((seg.Start, seg.End), Is.EqualTo((16 * TB, 48 * TB)));
            Assert.That(seg.Points[0].Value, Is.LessThan(seg.Points[1].Value), "low on the left, high on the right");

            // A clip's own automation column, on its pattern: it shows on each repeat of a looped clip, stitched along the timeline.
            rig.DoubleClick(rig.At(lead, 64, 2));
            var pattern = machine.Patterns.Last();
            var key = rig.ParamRow("Lead", Cutoff).Param.Key;
            SessionLayout.ColumnFor(pattern, key, create: true).SetEvents(new[] { new PatternEvent(0, 100), new PatternEvent(8 * TB, 700) }, true);
            rig.Drag(rig.At(lead, 80, -2), rig.At(lead, 128));
            var lane = rig.Read("Lead", Cutoff);
            var repeats = lane.Segments.Where(s => s.Source == AutoSource.ClipColumn).ToList();
            Assert.That(repeats.Select(s => s.Start / TB), Is.EqualTo(new[] { 64, 80, 96, 112 }), "a placement for each repeat");
            Assert.That(repeats.All(s => s.Editable), Is.True);
            Assert.That(repeats[2].ValueAt(100 * TB), Is.EqualTo(100));
            Assert.That(repeats[2].ValueAt(105 * TB), Is.EqualTo(700), "the step at the pattern's tick 8");
            var first = rig.Canvas.ItemsOf(lead)[0];
            Assert.That(first.Pattern, Is.SameAs(pattern));
            rig.Canvas.Changed();
            Studio.Screenshot(rig.Window, "ar4-automation-lanes");
        }

        // ------------------------------------------------------------------ tracker columns and overlaps

        [AvaloniaTest]
        public void TrackerColumnsShowAsStepsOnTheLaneAndOverlapsAreMarked()
        {
            ValueDisplay.Base = ValueBase.Hex;
            using var restore = Studio.AfterTest(() => ValueDisplay.Base = ValueDisplay.Default);
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            var machine = lead.Machine;
            machine.CreatePattern("00", 16);
            var pattern = machine.Patterns.First(p => p.Name == "00");

            // The tracker's data: Voice mode 2 on row 4 and 1 on row 8, typed in the Pattern tab.
            rig.Window.Detail.Show(true);
            Settle();
            PatternView.Edit(pattern);
            Settle();
            dynamic view = PatternView.Page.EditorControl;
            ((Avalonia.Controls.Control)view.TrackerGrid.Body).Focus();
            Settle();
            foreach (var (rowNumber, digit) in new[] { (4, PhysicalKey.Digit2), (8, PhysicalKey.Digit1) })
            {
                EditorNav.GoTo((object)view, "Voice mode", Settle);
                for (int i = 0; i < rowNumber; i++) rig.Key(PhysicalKey.ArrowDown);
                rig.Key(PhysicalKey.ArrowRight);
                rig.Key(digit);
            }
            rig.Window.Detail.Close();
            Settle();
            Assert.That(pattern.PatternEditorMachineEvents.First(c => c.Parameter.Name == "Voice mode").Events.Select(e => (e.Time / TB, e.Value)), Is.EqualTo(new[] { (4, 2), (8, 1) }), "the tracker has its steps");

            Assert.That(rig.Canvas.Editor.Place(lead, pattern, 0), Is.Null);
            rig.Canvas.Changed();
            rig.AddLane("Lead", "Voice mode");
            var lane = rig.Read("Lead", "Voice mode");
            var steps = lane.Segments.Single(s => s.Source == AutoSource.Tracker);
            Assert.That(steps.Points.Select(p => (p.Time / TB, p.Value, p.Ramp)), Is.EqualTo(new[] { (4, 2, false), (8, 1, false) }), "the tracker's columns are steps on the lane");
            Assert.That(steps.Editable, Is.False, "they are the tracker's to edit");
            Assert.That(steps.ValueAt(6 * TB), Is.EqualTo(2), "a step holds until the next");
            Assert.That(lane.Overlaps, Is.Empty, "one writer");

            // The tracker's steps are read only on the lane: a press on one says so and changes nothing.
            var row = rig.ParamRow("Lead", "Voice mode");
            var before = Pts(rig.Read("Lead", "Voice mode").Segments.Single());
            rig.Click(rig.AtValue(row, 4, 1.0));
            Assert.That(rig.Canvas.Status, Does.Contain("tracker"));
            Assert.That(Pts(rig.Read("Lead", "Voice mode").Segments.Single()), Is.EqualTo(before));

            // An automation clip over the same ticks (a ramp across them) writes the parameter too: the overlap is found where both write.
            rig.Drag(rig.AtValue(row, 1, 0.0), rig.AtValue(row, 32, 1.0));
            lane = rig.Read("Lead", "Voice mode");
            Assert.That(lane.Segments.Select(s => s.Source).OrderBy(s => s), Is.EqualTo(new[] { AutoSource.AutomationClip, AutoSource.Tracker }.OrderBy(s => s)));
            Assert.That(lane.Overlaps, Is.Not.Empty, "two writers in one place are marked");
            var o = lane.Overlaps[0];
            Assert.That(o.From, Is.GreaterThanOrEqualTo(4 * TB), "from the tracker's first step");
            Assert.That(lane.Overlaps.Any(x => x.From <= 4 * TB && x.To > 4 * TB), Is.True, "over the first step");
            Assert.That(lane.Overlaps.Any(x => x.From <= 8 * TB && x.To > 8 * TB), Is.True, "over the second");
            Assert.That(lane.Overlaps.All(x => x.To <= 16 * TB), Is.True, "not past the tracker's pattern");
        }

        [AvaloniaTest]
        public void TwoAutomationClipsOnTwoLanesOverlapWhereBothWrite()
        {
            using var rig = new Rig("Lead");
            var machine = rig.Lane("Lead").Machine;
            rig.AddLane("Lead", Cutoff);
            var row = rig.ParamRow("Lead", Cutoff);
            rig.Drag(rig.AtValue(row, 1, 0.1), rig.AtValue(row, 32, 0.9));
            Assert.That(rig.Read("Lead", Cutoff).Overlaps, Is.Empty);
            Assert.That(rig.Canvas.Automation.DrawClip(machine, row.Param.Key, 16, 32, 100, 900, out _), Is.Null);
            Settle();
            var lane = rig.Read("Lead", Cutoff);
            Assert.That(lane.Segments.Count(s => s.Source == AutoSource.AutomationClip), Is.EqualTo(2));
            Assert.That(Core.Song.Sequences.Count(s => s.Machine == machine && Core.Arrangement.RoleOf(s) == LaneRole.Automation), Is.EqualTo(2), "the second clip needed a lane of its own");
            Assert.That(lane.Overlaps.Count, Is.EqualTo(1), "both ramp over ticks 16 to 32");
            Assert.That((lane.Overlaps[0].From, lane.Overlaps[0].To), Is.EqualTo((16 * TB, 32 * TB)));
            Studio.Screenshot(rig.Window, "ar4-automation-overlap");
        }

        // ------------------------------------------------------------------ recording

        [AvaloniaTest]
        public void KnobMovesAreRecordedIntoTheArmedLaneWhileTheSongPlaysAsOneStepOfTheHistory()
        {
            using var rig = new Rig("Lead");
            var machine = rig.Lane("Lead").Machine;
            Core.SongCore.SongEnd = 64;
            rig.AddLane("Lead", Cutoff);
            var cutoff = machine.ParameterGroups[1].Parameters.First(p => p.Name == Cutoff);
            var recorder = Core.Arrangement.Recorder;

            // Not armed: nothing is written.
            var work = StartTransport();
            try
            {
                AdvanceTo(work, 4);
                cutoff.SetValue(0, 300);
            }
            finally { Core.Playing = false; Settle(); }
            Assert.That(rig.Read("Lead", Cutoff).Segments, Is.Empty, "a lane that isn't armed takes nothing");

            rig.Click(rig.Of(rig.View.Headers.Find<ToggleButton>($"Record {Cutoff} of Lead")!));
            Assert.That(recorder.IsArmed("Lead", rig.ParamRow("Lead", Cutoff).Param.Key), Is.True, "the switch arms the lane");
            work = StartTransport();
            try
            {
                foreach (var (tick, value) in new[] { (4, 100), (8, 400), (12, 800) })
                {
                    AdvanceTo(work, tick);
                    cutoff.SetValue(0, value);
                }
                // A move from the engine itself (the don't-record flag) is not a performance.
                AdvanceTo(work, 14);
                cutoff.SetValue(0 | (1 << 16), 50);
            }
            finally
            {
                Core.Playing = false;
                Settle();
            }
            var lane = rig.Read("Lead", Cutoff);
            Assert.That(lane.Segments, Is.Not.Empty, $"armed {recorder.Any}; sequences {Core.Song.Sequences.Count}; status {rig.Canvas.Status}; patterns {Core.Song.Machines.First(m => m.Name == "Lead").Patterns.Count}");
            var seg = lane.Segments.Single();
            Assert.That(seg.Source, Is.EqualTo(AutoSource.AutomationClip), "a clip was drawn for the pass");
            Assert.That(seg.Points.Select(p => (p.Time / TB, p.Value, p.Ramp)), Is.EqualTo(new[] { (4, 100, true), (8, 400, true), (12, 800, true) }), "a point for each move, ramps between: " + Pts(seg));
            Assert.That(seg.Start / TB, Is.EqualTo(0), "from the bar the first move was in");
            Assert.That(recorder.Any, Is.True);

            // The whole pass is one step.
            rig.Undo();
            Assert.That(rig.Read("Lead", Cutoff).Segments, Is.Empty, "one undo takes the whole pass away");
            rig.Redo();
            Assert.That(rig.Read("Lead", Cutoff).Segments.Single().Points.Count, Is.EqualTo(3));

            // Disarming stops it.
            rig.Click(rig.Of(rig.View.Headers.Find<ToggleButton>($"Record {Cutoff} of Lead")!));
            Assert.That(recorder.Any, Is.False);
            Core.SongCore.LoopEnd = 64;
            Core.SongCore.SongEnd = 64;
            work = StartTransport();
            try
            {
                AdvanceTo(work, 20);
                cutoff.SetValue(0, 999);
            }
            finally { Core.Playing = false; Settle(); }
            Assert.That(rig.Read("Lead", Cutoff).Segments.Single().Points.Count, Is.EqualTo(3), "a disarmed lane takes nothing");
        }

        // ------------------------------------------------------------------ old songs

        [AvaloniaTest]
        public void AnOldSongWithoutAutomationLanesLoadsAndRendersTheSameAfterTheViewShowedAndDroppedALane()
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            rig.DoubleClick(rig.At(lead, 0, 2));
            Notes(lead.Machine.Patterns.Single(), 16);
            rig.Drag(rig.At(lead, 16, -2), rig.At(lead, 64));
            Core.SongCore.SongEnd = 64;
            Core.SongCore.LoopEnd = 64;
            Assert.That(Core.Arrangement.ToXml(), Is.Null, "an old song has no arrangement subsection");
            var before = Render(64);
            Assert.That(before.Max(Math.Abs), Is.GreaterThan(0.01f));

            // A lane added, looked at and taken away again leaves the song as it was; adding one changes no sound by itself.
            rig.AddLane("Lead", Cutoff);
            Assert.That(Render(64), Is.EqualTo(before), "a lane that holds nothing is only a view");
            rig.Click(rig.Of(rig.View.Headers.Find<Button>($"Remove lane {Cutoff} of Lead")!));
            Assert.That(Core.Arrangement.ToXml(), Is.Null);
            Assert.That(Render(64), Is.EqualTo(before));
            Assert.That(Core.Song.Sequences.Count(s => s.Machine == lead.Machine), Is.EqualTo(1), "no lane of ours was added to the song");
        }

        // ------------------------------------------------------------------ the kit

        [AvaloniaTest]
        public void TheLanesAndTheirHeadersAreOnTheKitsStyleHaveNamesDrawInLightAndDarkAndLetGoWhenTheWindowCloses()
        {
            var kind = StudioTheme.Kind;
            using var restore = Studio.AfterTest(() => { StudioTheme.Kind = kind; StudioTheme.Refresh(); });
            var rig = new Rig("Lead");
            bool disposed = false;
            try
            {
                var lead = rig.Lane("Lead");
                rig.AddLane("Lead", Cutoff);
                var row = rig.ParamRow("Lead", Cutoff);
                rig.Drag(rig.AtValue(row, 1, 0.15), rig.AtValue(row, 128, 0.9));
                rig.Click(rig.AtValue(row, 64, 0.2));
                rig.Drag(rig.AtValue(row, 128, 0.9), rig.AtValue(row, 160, 0.4));
                rig.Window.Detail.Close();

                var problems = new List<string>();
                var all = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(rig.View.Headers).OfType<Avalonia.Controls.Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0).ToList();
                foreach (var c in all)
                {
                    if (c.GetType() != typeof(Button) && c.GetType() != typeof(ToggleButton)) continue;
                    var b = (TemplatedControl)c;
                    bool icon = b.Classes.Contains("icon");
                    var padding = icon ? StudioStyles.IconPadding : StudioStyles.ButtonPadding;
                    double font = icon ? StudioStyles.IconFontSize : StudioTokens.FontBody;
                    if (b.Padding != padding) problems.Add($"padding {b.Padding}: {((ContentControl)b).Content}");
                    if (b.FontSize != font) problems.Add($"font {b.FontSize}: {((ContentControl)b).Content}");
                    if (b.Bounds.Height < StudioStyles.ControlHeight - 0.5) problems.Add($"{b.Bounds.Height:0} high: {((ContentControl)b).Content}");
                    if (b.CornerRadius != new CornerRadius(StudioStyles.CornerRadius)) problems.Add($"radius: {((ContentControl)b).Content}");
                    if (string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(b))) problems.Add("a button without a name: " + ((ContentControl)b).Content);
                }
                Assert.That(problems, Is.Empty);
                foreach (var name in new[] { "Add automation lane for Lead", $"Record {Cutoff} of Lead", $"Remove lane {Cutoff} of Lead", "Add lane for Lead", "Mute Lead", "Solo Lead" })
                    Assert.That(all.Any(c => Avalonia.Automation.AutomationProperties.GetName(c) == name), Is.True, name);

                foreach (var theme in new[] { AppearanceTheme.Light, AppearanceTheme.Dark })
                {
                    StudioTheme.Kind = theme;
                    StudioTheme.Refresh();
                    Settle();
                    Studio.Screenshot(rig.Window, "ar4-automation-" + theme);
                }

                // Nothing is left listening when the window is gone.
                Assert.That(Listeners(Core.Arrangement.Recorder, "Changed"), Is.GreaterThan(0), "the headers listen to the recorder while shown");
                rig.Dispose();
                disposed = true;
                Assert.That(Listeners(Core.Arrangement.Recorder, "Changed"), Is.EqualTo(0), "the closed window no longer listens to the recorder");
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
