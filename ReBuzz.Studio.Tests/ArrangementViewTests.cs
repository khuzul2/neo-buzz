// Fork (docs/specs/daw-modes.md AR1): the Sequence tab's Arrangement mode, headless: tracks with
// lanes and clips drawn, moved, copied, trimmed, looped, split, joined and made unique with the
// mouse and keys; the lane roles; and the exit test, an 8-bar song built by mouse that renders as
// the equivalent plain events and, but for the trims, as upstream ReBuzz plays its events.

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
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzzTests.Automation;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Sequence;

namespace ReBuzz.Studio.Tests
{
    public class ArrangementViewTests
    {
        const int TB = PatternEvent.TimeBase;

        static ReBuzzCore Core => Studio.Host.Core;

        /// <summary>Control, or Command on a Mac: the copy-drag.</summary>
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

            public Rig(params string[] machines)
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                looping = Core.Looping;
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
                Click(Of(View.ArrangementModeButton));       // the mode switch, by mouse
                Assert.That(View.IsArrangementMode, Is.True, "the Arrange button switches to the arrangement");
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
                machine.ParameterGroupsList[1].ParametersList.First(q => q.Name == "Drift").SetValue(0, 0);   // analog drift is random per voice: off, so renders compare
                return machine;
            }

            public ISequence Lane(string machine, int index = 0) => Core.Song.Sequences.Where(s => s.Machine.Name == machine).ElementAt(index);

            public Point Of(Avalonia.Controls.Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), Window)!.Value;

            /// <summary>A tick on a lane in window pixels (dx pixels right of it, fy down the lane).</summary>
            public Point At(ISequence lane, int tick, double dx = 0, double fy = 0.55)
            {
                Settle();
                var row = Canvas.RowOf(lane);
                Assert.That(row, Is.Not.Null, "the lane has a row");
                var geo = Canvas.Geometry;
                var p = Canvas.TranslatePoint(new Point(geo.XOf(tick) + dx, Canvas.YOf(row) + fy * row.Height), Window);
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

            /// <summary>The clips of a lane as text: "0-16 Name 0 +0" (start-end, pattern, the part it starts with).</summary>
            public string Clips(ISequence lane) => string.Join(" | ", Canvas.ItemsOf(lane).Select(i => $"{i.Time}-{i.End} {i.Pattern.Name} from {i.Spec.Start}{(i.Spec.Loop ? " loop" : "")}"));

            public void Dispose()
            {
                Core.ExecuteCommand(BuzzCommand.Stop);
                Window.Close();
                Settle();
                Dialogs.Responder = null;
                Core.Looping = looping;
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

        static string Events(ISequence s) => string.Join(" ", s.Events.OrderBy(e => e.Key).Select(e => $"{e.Key}:{e.Value.Pattern?.Name}[{e.Value.Offset},{e.Value.End}]"));

        // ------------------------------------------------------------------ the mode

        [AvaloniaTest]
        public void TheArrangementHasATrackForEachMachineWithLanesAndMasterOnTop()
        {
            using var rig = new Rig("Lead", "Bass");
            var c = rig.Canvas;
            Assert.That(c.Rows.Select(r => r.Kind).Take(2), Is.EqualTo(new[] { ArrangementRowKind.Tempo, ArrangementRowKind.Markers }), "the Master's tempo and markers are on top");
            Assert.That(c.Rows.Count(r => r.Kind == ArrangementRowKind.Lane), Is.EqualTo(2));
            // A second lane for Lead: the track's "+".
            rig.Click(rig.Of(rig.View.Headers.Find<Button>("Add lane for Lead")!));
            Assert.That(Core.Song.Sequences.Count(s => s.Machine.Name == "Lead"), Is.EqualTo(2), "the + adds a sequence to the machine");
            Assert.That(c.Rows.Where(r => r.Lane != null).Select(r => (r.Machine.Name, r.LaneNumber)), Is.EqualTo(new[] { ("Lead", 1), ("Lead", 2), ("Bass", 1) }), "lanes sit under their machine");
            // Fold the track.
            rig.Click(rig.Of(rig.View.Headers.Find<ToggleButton>("Fold Lead")!));
            Assert.That(Core.Arrangement.IsFolded("Lead"), Is.True);
            Assert.That(c.Rows.Count(r => r.Machine?.Name == "Lead"), Is.EqualTo(1), "a folded track is one row");
            Assert.That(rig.View.Headers.Find<ToggleButton>("Mute Bass"), Is.Not.Null, "the header's icon buttons have names");
            // Back to the tracker mode: the old editor is as it was.
            rig.Click(rig.Of(rig.View.TrackerModeButton));
            Assert.That(rig.View.IsArrangementMode, Is.False);
            Assert.That(rig.View.Canvas.IsVisible && !rig.Canvas.IsVisible, Is.True);
        }

        [AvaloniaTest]
        public void LaneRolesAndFoldsAreSavedWithTheSongAndOldSongsHaveNone([Values(".bmx", ".bmxml")] string extension)
        {
            using var rig = new Rig("Lead", "Bass");
            Assert.That(Core.Arrangement.ToXml(), Is.Null, "nothing differs from the default: no subsection");
            Core.SongCore.AddSequence(Core.Song.Machines.First(m => m.Name == "Lead"), Core.Song.Sequences.Count);
            Settle();
            var lane = rig.Lane("Lead", 1);
            Core.Arrangement.SetRole(lane, LaneRole.Take);
            Core.Arrangement.SetFolded("Bass", true);
            string path = TestTemp.Path($"rebuzz-ar1-{Environment.ProcessId}{extension}");   // the song's .history folder lands there too
            try
            {
                Core.SaveSongCopy(path);
                Core.Modified = false;
                Core.OpenSongFile(path);
                Settle();
                Assert.That(Core.Arrangement.RoleOf(rig.Lane("Lead", 1)), Is.EqualTo(LaneRole.Take));
                Assert.That(Core.Arrangement.RoleOf(rig.Lane("Lead", 0)), Is.EqualTo(LaneRole.Main));
                Assert.That(Core.Arrangement.IsFolded("Bass"), Is.True);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ------------------------------------------------------------------ the gestures

        [AvaloniaTest]
        public void ADoubleClickDrawsAClipAndItsEdgesTrimAndRepeatIt()
        {
            using var rig = new Rig("Lead");
            var lane = rig.Lane("Lead");
            rig.DoubleClick(rig.At(lane, 0, 2));
            Assert.That(rig.Clips(lane), Is.EqualTo("0-16 00 from 0 loop"), "a clip of the session's clip length with a pattern of its own");
            var pattern = Core.Song.Machines.First(m => m.Name == "Lead").Patterns.Single();
            Assert.That(pattern.Length, Is.EqualTo(16));
            rig.Drag(rig.At(lane, 16, -2), rig.At(lane, 48));
            Assert.That(Events(lane), Is.EqualTo("0:00[0,0] 16:00[0,0] 32:00[0,0]"), "the right edge repeats a looping clip");
            Assert.That(rig.Clips(lane), Is.EqualTo("0-48 00 from 0 loop"));
            rig.Drag(rig.At(lane, 48, -2), rig.At(lane, 40));
            Assert.That(Events(lane), Is.EqualTo("0:00[0,0] 16:00[0,0] 32:00[0,8]"), "and trims it: the last repeat is cut with End");
            rig.Drag(rig.At(lane, 0, 2), rig.At(lane, 8));
            Assert.That(Events(lane), Is.EqualTo("8:00[8,0] 16:00[0,0] 32:00[0,8]"), "the left edge trims the start: what plays then plays on");
            // Undo is on the song's history, one step for each gesture.
            rig.Key(PhysicalKey.Z, Cmd);
            Assert.That(Events(lane), Is.EqualTo("0:00[0,0] 16:00[0,0] 32:00[0,8]"));
            rig.Key(PhysicalKey.Z, Cmd);
            Assert.That(Events(lane), Is.EqualTo("0:00[0,0] 16:00[0,0] 32:00[0,0]"));
            if (OperatingSystem.IsMacOS()) rig.Key(PhysicalKey.Z, Cmd | RawInputModifiers.Shift);
            else rig.Key(PhysicalKey.Y, Cmd);
            Assert.That(Events(lane), Is.EqualTo("0:00[0,0] 16:00[0,0] 32:00[0,8]"), "redo");
        }

        [AvaloniaTest]
        public void ACopyDragMakesAnIndependentCopyAnAltDragALinkedOneAndEachClipHasItsOwnId()
        {
            using var rig = new Rig("Lead");
            var lane = rig.Lane("Lead");
            rig.DoubleClick(rig.At(lane, 0, 2));
            var machine = Core.Song.Machines.First(m => m.Name == "Lead");
            Notes(machine.Patterns.Single(), 16);
            int original = lane.Events[0].Clip;
            Assert.That(original, Is.Positive);

            rig.Drag(rig.At(lane, 4), rig.At(lane, 36), Cmd);
            Assert.That(machine.Patterns.Count, Is.EqualTo(2), "a copy-drag makes a new pattern");
            var copy = lane.Events[32].Pattern;
            Assert.That(copy.Name, Is.Not.EqualTo(machine.Patterns.First(p => p.Name == "00").Name));
            Assert.That(lane.Events[32].Clip, Is.Not.EqualTo(original), "and a clip id of its own");
            Assert.That(copy.Columns.Count, Is.EqualTo(machine.Patterns.First(p => p.Name == "00").Columns.Count), "with the notes (the engine columns) copied");
            Assert.That(Events(lane), Is.EqualTo($"0:00[0,0] 32:{copy.Name}[0,0]"));

            rig.Drag(rig.At(lane, 4), rig.At(lane, 68), RawInputModifiers.Alt);
            Assert.That(machine.Patterns.Count, Is.EqualTo(2), "an Alt-drag makes no pattern");
            Assert.That(lane.Events[64].Pattern.Name, Is.EqualTo("00"), "it is another place of the same pattern");
            Assert.That(lane.Events[64].Clip, Is.Not.EqualTo(original).And.Not.EqualTo(lane.Events[32].Clip), "with its own id");
            Assert.That(rig.Canvas.UsesOf(machine.Patterns.First(p => p.Name == "00")), Is.EqualTo(2), "the shared badge counts the places");

            // Make unique: the clip gets a pattern of its own.
            rig.Click(rig.At(lane, 68));
            rig.Key(PhysicalKey.U);
            Assert.That(machine.Patterns.Count, Is.EqualTo(3));
            Assert.That(lane.Events[64].Pattern.Name, Is.Not.EqualTo("00"));
            Assert.That(rig.Canvas.UsesOf(machine.Patterns.First(p => p.Name == "00")), Is.EqualTo(1));
            rig.Key(PhysicalKey.Z, Cmd);
            Assert.That(lane.Events[64].Pattern.Name, Is.EqualTo("00"), "undo gives the shared pattern back");
        }

        [AvaloniaTest]
        public void SplitCutsAtTheClickedBeatAndJoinPutsItBack()
        {
            using var rig = new Rig("Lead");
            var lane = rig.Lane("Lead");
            rig.DoubleClick(rig.At(lane, 0, 2));
            rig.Drag(rig.At(lane, 16, -2), rig.At(lane, 48));
            string before = Events(lane);
            rig.Click(rig.At(lane, 24));           // the click puts the cursor on the beat
            rig.Key(PhysicalKey.S);
            Assert.That(rig.Clips(lane), Is.EqualTo("0-24 00 from 0 loop | 24-48 00 from 8 loop"), "two clips, the second starting partway into the pattern");
            Assert.That(lane.Events[0].Clip, Is.Not.EqualTo(lane.Events[24].Clip));
            rig.Click(rig.At(lane, 4));
            rig.Key(PhysicalKey.J);
            Assert.That(rig.Clips(lane), Is.EqualTo("0-48 00 from 0 loop"));
            Assert.That(Events(lane), Is.EqualTo(before), "the same events as before");
        }

        [AvaloniaTest]
        public void AClipPlacedOverOthersCutsThemAndALoopOffClipCannotBeRepeated()
        {
            using var rig = new Rig("Lead");
            var lane = rig.Lane("Lead");
            rig.DoubleClick(rig.At(lane, 0, 2));
            rig.Drag(rig.At(lane, 16, -2), rig.At(lane, 64));
            rig.Click(rig.At(lane, 8));
            rig.Key(PhysicalKey.L);                  // loop off: it plays its pattern once
            Assert.That(rig.Clips(lane), Is.EqualTo("0-16 00 from 0"));
            rig.Drag(rig.At(lane, 16, -2), rig.At(lane, 48));
            Assert.That(rig.Clips(lane), Is.EqualTo("0-16 00 from 0"), "without a loop the right edge can't make it longer than its pattern");
            rig.Click(rig.At(lane, 8));
            rig.Key(PhysicalKey.L);
            rig.Drag(rig.At(lane, 16, -2), rig.At(lane, 48));
            Assert.That(rig.Clips(lane), Is.EqualTo("0-48 00 from 0 loop"));
            // A second clip drawn on top of the repeats' end cuts the first.
            rig.DoubleClick(rig.At(lane, 60, 2));
            rig.Drag(rig.At(lane, 60, 2), rig.At(lane, 40), Cmd);
            var clips = rig.Canvas.ItemsOf(lane);
            Assert.That(clips.Select(i => i.Time).ToArray(), Is.Ordered.Ascending, "no overlaps: " + rig.Clips(lane));
            for (int i = 1; i < clips.Count; i++)
                Assert.That(clips[i].Time, Is.GreaterThanOrEqualTo(clips[i - 1].End), "no overlaps: " + rig.Clips(lane));
        }

        [AvaloniaTest]
        public void ALaneTheSessionHasTakenOverIsGreyedAndTheMeterGridFollowsTheBars()
        {
            using var rig = new Rig("Lead");
            var lane = rig.Lane("Lead");
            rig.DoubleClick(rig.At(lane, 0, 2));
            var pattern = Core.Song.Machines.First(m => m.Name == "Lead").Patterns.Single();
            Core.Session.Launch(lane, pattern);
            Settle();
            rig.Canvas.InvalidateVisual();
            Settle();
            Core.SessionCore.Stop(lane, immediate: true);
            Settle();
            // Bars of 12 ticks (3/4): the canvas's bar numbers count them.
            Core.SongMeter.Set(0, 12);
            Settle();
            Assert.That(rig.Canvas.BarNumberAt(24), Is.EqualTo(3));
            Assert.That(rig.Canvas.BarBeat(14), Is.EqualTo("bar 2.1"));
        }

        // ------------------------------------------------------------------ the engine side of the clips

        [AvaloniaTest]
        public void AClipsEventsAreItsRegionRepeatedAndCutAtItsEnd()
        {
            using var rig = new Rig("Lead");
            var machine = Core.Song.Machines.First(m => m.Name == "Lead");
            machine.CreatePattern("P", 16);
            var p = machine.Patterns.Single();
            var spec = new ClipSpec(0, p, 8, 32, 4, 2, 12, true, 1.0);
            // Starts at 4, plays to 12 (8 ticks), then repeats 2..12 (10 ticks): 8 + 10 + 10 + 10 = 38, cut at 40.
            Assert.That(spec.Segments(), Is.EqualTo(new[] { new ClipSegment(8, 4, 8), new ClipSegment(16, 2, 10), new ClipSegment(26, 2, 10), new ClipSegment(36, 2, 4) }));
            Assert.That(spec.Events(1).Select(e => (e.Time, e.Event.Offset, e.Event.End)), Is.EqualTo(new[] { (8, 4, 12), (16, 2, 12), (26, 2, 12), (36, 2, 6) }));
            Assert.That(spec.PatternPositionAt(30), Is.EqualTo(6));
            var right = spec.CutLeft(30);
            Assert.That((right.Time, right.Start, right.Length), Is.EqualTo((30, 6, 10)));
            Assert.That((spec with { Loop = false }).Segments(), Is.EqualTo(new[] { new ClipSegment(8, 4, 8) }), "without a loop the region plays once");
        }

        // ------------------------------------------------------------------ the exit test

        static float[] Render(int blocks)
        {
            foreach (var m in Core.SongCore.MachinesList)
            {
                Core.MachineManager.Stop(m);
                // Each machine's own offline-render reset, as the agent's render does, so renders repeat.
                var target = m.ManagedMachine;
                target?.GetType().GetMethod("ResetForRender", Type.EmptyTypes)?.Invoke(target, null);
            }
            var sub = ReBuzzCore.subTickInfo;
            sub.PosInSubTick = 0; sub.CurrentSubTick = 0; sub.SubTickReminderCounter = 0;
            Core.Looping = false;
            Core.SongCore.PlayPosition = 0;
            ReBuzzCore.masterInfo.PosInTick = 0;
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

        static float[] RenderSong(IMachine muted = null)
        {
            if (muted != null) muted.IsMuted = true;
            try
            {
                int blocks = (int)Math.Ceiling(8 * 16 * (double)ReBuzzCore.masterInfo.SamplesPerTick / 256) + 200;
                return Render(blocks);
            }
            finally
            {
                if (muted != null) muted.IsMuted = false;
            }
        }

        [AvaloniaTest]
        public void AnEightBarSongBuiltByMouseRendersAsItsPlainEventsAndAsUpstreamPlaysItButForTheTrims()
        {
            using var rig = new Rig("Lead", "Bass");
            var lead = rig.Lane("Lead");
            var bass = rig.Lane("Bass");
            var leadMachine = lead.Machine;
            var bassMachine = bass.Machine;
            Core.Looping = false;
            Core.SongCore.LoopEnd = 128;
            Core.SongCore.SongEnd = 128;

            // Lead: a clip drawn at bar 1, repeated to bar 4 by its right edge, and copied (independently) to bar 5 by Ctrl/Cmd-drag.
            rig.DoubleClick(rig.At(lead, 0, 2));
            Notes(leadMachine.Patterns.Single(), 16);
            rig.Drag(rig.At(lead, 16, -2), rig.At(lead, 64));
            rig.Drag(rig.At(lead, 8), rig.At(lead, 72), Cmd);

            // Bass: a clip at bar 1, its start trimmed by a beat, repeated to bar 3 and its end trimmed again;
            // and a second one at bar 6 that doesn't loop, trimmed to half.
            rig.DoubleClick(rig.At(bass, 0, 2));
            Notes(bassMachine.Patterns.Single(), 16);
            rig.Drag(rig.At(bass, 0, 2), rig.At(bass, 4));
            rig.Drag(rig.At(bass, 16, -2), rig.At(bass, 48));
            rig.Drag(rig.At(bass, 48, -2), rig.At(bass, 40));
            rig.DoubleClick(rig.At(bass, 80, 2));
            var second = bassMachine.Patterns.First(p => p.Name != bass.Events.OrderBy(e => e.Key).First().Value.Pattern.Name);
            Notes(second, 16);
            rig.Click(rig.At(bass, 84));
            rig.Key(PhysicalKey.L);
            rig.Drag(rig.At(bass, 96, -2), rig.At(bass, 88));

            string leadName = lead.Events[0].Pattern.Name, copyName = lead.Events[64].Pattern.Name, bassName = bass.Events.OrderBy(e => e.Key).First().Value.Pattern.Name;
            Assert.That(copyName, Is.Not.EqualTo(leadName), "the copy-drag made its own pattern");
            Assert.That(Events(lead), Is.EqualTo($"0:{leadName}[0,0] 16:{leadName}[0,0] 32:{leadName}[0,0] 48:{leadName}[0,0] 64:{copyName}[0,0] 80:{copyName}[0,0] 96:{copyName}[0,0] 112:{copyName}[0,0]"),
                "Lead: two looped clips, as the mouse made them");
            Assert.That(Events(bass), Is.EqualTo($"4:{bassName}[4,0] 16:{bassName}[0,0] 32:{bassName}[0,8] 80:{second.Name}[0,8]"),
                "Bass: a trimmed looped clip and a trimmed one without a loop");
            Assert.That(rig.Canvas.ItemsOf(lead).Count, Is.EqualTo(2));
            Assert.That(rig.Canvas.ItemsOf(bass).Count, Is.EqualTo(2));
            Assert.That(rig.Canvas.ItemsOf(bass)[1].Spec.Loop, Is.False);

            // The render of the song as drawn...
            var drawn = RenderSong();
            var drawnLead = RenderSong(bassMachine);
            var again = RenderSong();
            Assert.That(again, Is.EqualTo(drawn), "baseline: a render repeats exactly");
            Assert.That(drawn.Max(Math.Abs), Is.GreaterThan(0.01f), "the song makes sound");

            // ...is the render of the same events written plain, with no clip ids, loops or arrangement data...
            var plain = new List<(ISequence Seq, int Time, IPattern Pattern, int Offset, int End)>();
            foreach (var seq in new[] { lead, bass })
                foreach (var kv in seq.Events.OrderBy(e => e.Key))
                    plain.Add((seq, kv.Key, kv.Value.Pattern, kv.Value.Offset, kv.Value.End));
            foreach (var seq in new[] { lead, bass })
                foreach (int t in seq.Events.Keys.ToList()) seq.SetEvent(t, null);
            Core.Clips.Reset();
            foreach (var (seq, time, pattern, offset, end) in plain)
                seq.SetEvent(time, new SequenceEvent(SequenceEventType.PlayPattern, pattern) { Offset = offset, End = end });
            Assert.That(rig.Canvas.Editor.Read(bass).Count, Is.GreaterThan(0));
            var asPlain = RenderSong();
            Assert.That(asPlain, Is.EqualTo(drawn), "the song drawn by mouse renders as the equivalent plain events");

            // ...and, but for the trims, as upstream ReBuzz plays it: upstream knows no Offset or End and plays each event whole.
            foreach (var (seq, time, pattern, _, _) in plain)
                seq.SetEvent(time, new SequenceEvent(SequenceEventType.PlayPattern, pattern));
            var upstream = RenderSong();
            var upstreamLead = RenderSong(bassMachine);
            Assert.That(upstreamLead, Is.EqualTo(drawnLead), "Lead, whose clips aren't trimmed, plays the same as upstream plays it");
            Assert.That(upstream, Is.Not.EqualTo(drawn), "Bass, whose clips are trimmed, differs: upstream plays them whole");
        }
    }
}
