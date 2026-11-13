// Fork (docs/specs/history-snapshots.md HS1): the song's one undo history. The stack itself (steps, names, sources,
// coalescing, the memory cap, shared blobs, jumping), then on the shared engine: the Session View's edits and the other
// views' edits in one order, tempo and loop, MIDI controller moves, pattern renames and machine state (a Dye Essence
// wavetable) as steps.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Headless.NUnit;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Actions;

namespace ReBuzz.Studio.Tests
{
    public class HistoryTests
    {
        // ------------------------------------------------------------------ the stack

        sealed class Counter : IAction
        {
            readonly int[] box;
            readonly int by;
            public Counter(int[] box, int by = 1) { this.box = box; this.by = by; }
            public void Do() => box[0] += by;
            public void Undo() => box[0] -= by;
        }

        sealed class Blobby : IAction, IHistoryAction
        {
            readonly object[] blobs;
            readonly long size;
            public Blobby(long size, params object[] blobs) { this.size = size; this.blobs = blobs; }
            public void Do() { }
            public void Undo() { }
            public string HistoryName => null;
            public long HistoryBytes => 100;
            public IEnumerable<KeyValuePair<object, long>> HistoryBlobs => blobs.Select(b => new KeyValuePair<object, long>(b, size));
        }

        sealed class Sweep : IAction, ICoalescingAction
        {
            readonly int[] box;
            readonly int before;
            int after;
            public Sweep(int[] box, int before, int after) { this.box = box; this.before = before; this.after = after; }
            public int After => after;
            public void Do() => box[0] = after;
            public void Undo() => box[0] = before;
            public bool TryMerge(IAction later)
            {
                if (later is not Sweep s) return false;
                after = s.after;
                return true;
            }
        }

        sealed class Probe : IAction
        {
            readonly Action onDo;
            public Probe(Action onDo) { this.onDo = onDo; }
            public void Do() => onDo();
            public void Undo() { }
        }

        [HistoryName("Named by attribute")]
        sealed class Attributed : IAction
        {
            public void Do() { }
            public void Undo() { }
        }

        sealed class PlainThing : IAction
        {
            public void Do() { }
            public void Undo() { }
        }

        [AvaloniaTest]
        public void StepsHaveNamesSourcesAndTimes()
        {
            var stack = new ManagedActionStack();
            var box = new int[1];
            stack.Do(new Counter(box));
            stack.Do(new Attributed());
            using (stack.BeginSource(HistorySource.Agent, "Agent: tweak"))
            {
                stack.BeginActionGroup();
                stack.Do(new Counter(box));
                stack.Do(new Counter(box));
                stack.EndActionGroup();
            }
            stack.Do(new PlainThing());

            var steps = stack.Steps;
            Assert.That(steps.Select(s => s.Name), Is.EqualTo(new[] { "Counter", "Named by attribute", "Agent: tweak", "Plain thing" }));
            Assert.That(steps.Select(s => s.Source), Is.EqualTo(new[] { HistorySource.Manual, HistorySource.Manual, HistorySource.Agent, HistorySource.Manual }));
            Assert.That(steps[2].Actions, Is.EqualTo(2));
            Assert.That(steps.All(s => s.Done), Is.True);
            Assert.That(steps.Select(s => s.Id).Distinct().Count(), Is.EqualTo(4));
            Assert.That((DateTime.Now - steps[0].Time).TotalMinutes, Is.LessThan(1));

            stack.Undo();
            Assert.That(stack.Steps.Select(s => s.Done), Is.EqualTo(new[] { true, true, true, false }));
            Assert.That(stack.CurrentStepId, Is.EqualTo(steps[2].Id));
        }

        [AvaloniaTest]
        public void JumpGoesToAStepWhicheverWayItIs()
        {
            var stack = new ManagedActionStack();
            var box = new int[1];
            for (int i = 0; i < 5; i++) stack.Do(new Counter(box, 10));
            var ids = stack.Steps.Select(s => s.Id).ToArray();

            Assert.That(stack.JumpTo(ids[1]), Is.True);
            Assert.That(box[0], Is.EqualTo(20));
            Assert.That(stack.Steps.Count(s => s.Done), Is.EqualTo(2));
            Assert.That(stack.CanRedo, Is.True);

            Assert.That(stack.JumpTo(ids[4]), Is.True);
            Assert.That(box[0], Is.EqualTo(50));
            Assert.That(stack.JumpTo(0), Is.True, "before the oldest step");
            Assert.That(box[0], Is.EqualTo(0));
            Assert.That(stack.CanUndo, Is.False);
            Assert.That(stack.JumpTo(12345), Is.False, "no such step");
            Assert.That(box[0], Is.EqualTo(0));
        }

        [AvaloniaTest]
        public void ARecordedEditIsNotDoneAgainButUndoAndRedoWork()
        {
            var stack = new ManagedActionStack();
            var box = new int[] { 5 };
            stack.Record(new Counter(box, 5));                  // already made: box was 5 after it
            Assert.That(box[0], Is.EqualTo(5));
            stack.Undo();
            Assert.That(box[0], Is.EqualTo(0));
            stack.Redo();
            Assert.That(box[0], Is.EqualTo(5));
        }

        [AvaloniaTest]
        public void CoalescingMakesASweepOneStepWithinTheIdleGap()
        {
            long now = 0;
            var stack = new ManagedActionStack { Clock = () => now };
            var box = new int[] { 0 };
            var gap = TimeSpan.FromMilliseconds(500);
            for (int v = 1; v <= 50; v++)
            {
                box[0] = v;
                now += 100;                                     // 5 seconds in all, never idle for 500 ms
                stack.RecordCoalesced(new Sweep(box, v - 1, v), "knob", gap);
            }
            Assert.That(stack.Steps.Count, Is.EqualTo(1), "one gesture");
            Assert.That(stack.UsedBytes, Is.LessThan(1000));
            stack.Undo();
            Assert.That(box[0], Is.EqualTo(0), "back to where the sweep began");
            stack.Redo();
            Assert.That(box[0], Is.EqualTo(50));

            now += 600;                                         // idle: a new gesture
            box[0] = 51;
            stack.RecordCoalesced(new Sweep(box, 50, 51), "knob", gap);
            Assert.That(stack.Steps.Count, Is.EqualTo(2));

            box[0] = 7;
            now += 10;
            stack.RecordCoalesced(new Sweep(box, 51, 7), "other knob", gap);
            Assert.That(stack.Steps.Count, Is.EqualTo(3), "another key is another step");

            stack.Undo();                                       // an undo ends the run: the next move is a new step
            stack.Redo();
            now += 10;
            stack.RecordCoalesced(new Sweep(box, 7, 8), "other knob", gap);
            Assert.That(stack.Steps.Count, Is.EqualTo(3), "nothing was done since, so it still merges");
            stack.Do(new Counter(box));
            now += 10;
            stack.RecordCoalesced(new Sweep(box, 9, 10), "other knob", gap);
            Assert.That(stack.Steps.Count, Is.EqualTo(5), "a step in between ends it");
        }

        [AvaloniaTest]
        public void TheMemoryCapDropsTheOldestWholeStepsAndKeepsTheNewest()
        {
            var stack = new ManagedActionStack { MaxBytes = 10_000 };
            var box = new int[1];
            for (int i = 0; i < 100; i++) stack.Do(new Counter(box));     // 256 bytes each
            Assert.That(stack.UsedBytes, Is.LessThanOrEqualTo(10_000));
            Assert.That(stack.Steps.Count, Is.EqualTo(10_000 / ManagedActionStack.DefaultActionBytes));
            Assert.That(stack.DroppedSteps, Is.EqualTo(100 - stack.Steps.Count));
            int kept = stack.Steps.Count;
            for (int i = 0; i < kept; i++) stack.Undo();
            Assert.That(box[0], Is.EqualTo(100 - kept), "the dropped steps stay done");
            Assert.That(stack.CanUndo, Is.False);

            // A group is dropped whole.
            stack = new ManagedActionStack { MaxBytes = 1_000 };
            stack.BeginActionGroup();
            for (int i = 0; i < 3; i++) stack.Do(new Counter(box));
            stack.EndActionGroup();
            stack.Do(new Counter(box));
            stack.Do(new Counter(box));
            Assert.That(stack.Steps.Select(s => s.Actions), Is.EqualTo(new[] { 1, 1 }), "the group of three went together");

            // The newest step stays even when it is over the cap on its own.
            stack = new ManagedActionStack { MaxBytes = 500 };
            stack.Do(new Blobby(100_000, new object()));
            Assert.That(stack.Steps.Count, Is.EqualTo(1));
            Assert.That(stack.CanUndo, Is.True);
        }

        [AvaloniaTest]
        public void ABlobTwoStepsShareCountsOnceAndGoesWithTheLastStepThatHoldsIt()
        {
            var stack = new ManagedActionStack();
            var a = new string('a', 10);
            var b = new string('b', 10);
            stack.Do(new Blobby(1000, a));
            long one = stack.UsedBytes;
            stack.Do(new Blobby(1000, a, b));
            Assert.That(stack.UsedBytes, Is.EqualTo(one + 100 + 1000), "a is counted once, b is new");
            stack.Do(new Blobby(1000, b));
            Assert.That(stack.UsedBytes, Is.EqualTo(one + 200 + 1000));

            stack = new ManagedActionStack { MaxBytes = 2_300 };
            stack.Do(new Blobby(1000, a));
            stack.Do(new Blobby(1000, a));
            stack.Do(new Blobby(1000, a));                      // 300 + 1000: all three fit, a is held once
            Assert.That(stack.Steps.Count, Is.EqualTo(3));
            stack.Do(new Blobby(1000, b));                      // 400 + 2000: over; the oldest go
            Assert.That(stack.UsedBytes, Is.LessThanOrEqualTo(2_300));
            Assert.That(stack.Steps.Count, Is.EqualTo(3));
        }

        [AvaloniaTest]
        public void ApplyingIsTrueWhileAnActionRunsAndEventsReportEachGroup()
        {
            var stack = new ManagedActionStack();
            bool seen = false;
            stack.Do(new Probe(() => seen = stack.Applying));
            Assert.That(seen, Is.True);
            Assert.That(stack.Applying, Is.False);

            var kinds = new List<HistoryChangeKind>();
            int changed = 0;
            stack.Applied += c => kinds.Add(c.Kind);
            stack.Changed += () => changed++;
            var box = new int[1];
            stack.Do(new Counter(box));
            stack.Undo();
            stack.Redo();
            Assert.That(kinds, Is.EqualTo(new[] { HistoryChangeKind.Do, HistoryChangeKind.Undo, HistoryChangeKind.Redo }));
            Assert.That(changed, Is.EqualTo(3));
        }

        // ------------------------------------------------------------------ one history for every view

        static ReBuzzCore Core => Studio.Host.Core;

        [AvaloniaTest]
        public void SessionEditsAndOtherViewsEditsAreOneOrderedHistory()
        {
            using var rig = new SessionRig("Lead");
            var layout = rig.Layout;
            var song = rig.Core.SongCore;
            var stack = song.ActionStack;
            var lead = rig.Machine("Lead");
            layout.Ensure();
            stack.Clear();
            int bpm = rig.Core.BPM;

            layout.RenameScene(0, "Intro");                             // the Session View
            song.RenameMachineUndoable(lead, "Bass");                   // the Machine View
            TransportEdits.SetTempo(rig.Core, bpm + 10);                // the transport bar
            Assert.That(ReferenceEquals(layout.UndoStack, stack), "the session's history is the song's");
            Assert.That(stack.Steps.Count, Is.EqualTo(3));
            Assert.That(stack.Steps.Select(s => s.Name), Is.EqualTo(new[] { "Edit session grid", "Rename machine", "Set tempo" }));

            layout.Undo();                                              // the Session View's button takes the tempo first
            Assert.That(rig.Core.BPM, Is.EqualTo(bpm));
            Assert.That(song.MachinesList.Any(m => m.Name == "Bass"), Is.True);
            song.Undo();                                                // the Machine View's takes the rename
            Assert.That(song.MachinesList.Any(m => m.Name == "Lead"), Is.True);
            Assert.That(layout.Document!.Scenes[0].Name, Is.EqualTo("Intro"));
            song.Undo();                                                // and the song's undo reaches the Session View's edit
            Assert.That(layout.Document!.Scenes[0].Name, Is.Not.EqualTo("Intro"));
            Assert.That(layout.UndoStack.CanUndo, Is.False);

            song.Redo();
            layout.Redo();
            song.Redo();
            Assert.That(layout.Document!.Scenes[0].Name, Is.EqualTo("Intro"));
            Assert.That(song.MachinesList.Any(m => m.Name == "Bass"), Is.True);
            Assert.That(rig.Core.BPM, Is.EqualTo(bpm + 10));
        }

        [AvaloniaTest]
        public void AnUndoInAnyViewRefreshesTheSessionGridAndJumpGoesAcrossViews()
        {
            using var rig = new SessionRig("Lead");
            var layout = rig.Layout;
            var song = rig.Core.SongCore;
            var stack = song.ActionStack;
            layout.Ensure();
            stack.Clear();
            int refreshed = 0;
            layout.Changed += () => refreshed++;

            layout.RenameScene(0, "One");
            song.RenameMachineUndoable(rig.Machine("Lead"), "Two");
            layout.RenameScene(0, "Three");
            var ids = stack.Steps.Select(s => s.Id).ToArray();

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            refreshed = 0;
            song.Undo();                                                // the Machine View's undo, of a Session View step
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.That(refreshed, Is.GreaterThan(0), "the grid follows an undo made elsewhere");

            Assert.That(stack.JumpTo(0), Is.True);
            Assert.That(song.MachinesList.Any(m => m.Name == "Lead"), Is.True);
            Assert.That(layout.Document!.Scenes[0].Name, Is.Not.EqualTo("One"));
            Assert.That(stack.JumpTo(ids[1]), Is.True);
            Assert.That(song.MachinesList.Any(m => m.Name == "Two"), Is.True);
            Assert.That(layout.Document!.Scenes[0].Name, Is.EqualTo("One"));
            Assert.That(stack.JumpTo(ids[2]), Is.True);
            Assert.That(layout.Document!.Scenes[0].Name, Is.EqualTo("Three"));
        }

        [AvaloniaTest]
        public void ANewSongStartsWithAnEmptyHistoryAndTheCapIsTheSetting()
        {
            using var rig = new SessionRig("Lead");
            rig.Layout.Ensure();
            rig.Layout.InsertScene(0);
            Assert.That(rig.Core.SongCore.ActionStack.CanUndo, Is.True);
            Assert.That(rig.Core.SongCore.ActionStack.MaxBytes, Is.EqualTo(HistorySettings.MaxBytes));
            Assert.That(HistorySettings.MaxBytes, Is.EqualTo(256L * 1024 * 1024), "the default is 256 MB");

            rig.Core.Modified = false;
            rig.Core.ExecuteCommand(BuzzCommand.NewFile);
            Assert.That(rig.Layout.UndoStack.CanUndo, Is.False);
            Assert.That(rig.Layout.UndoStack.Steps, Is.Empty);
        }

        // ------------------------------------------------------------------ the gaps

        [AvaloniaTest]
        public void TempoAndLoopAreStepsAndATempoRunIsOne()
        {
            using var rig = new SessionRig("Lead");
            var stack = rig.Core.SongCore.ActionStack;
            long now = 0;
            stack.Clock = () => now;
            stack.Clear();
            int bpm = rig.Core.BPM;
            Assert.That(rig.Core.Looping, Is.True);

            foreach (int b in new[] { bpm + 1, bpm + 2, bpm + 3 })
            {
                TransportEdits.SetTempo(rig.Core, b);
                now += 100;
            }
            Assert.That(stack.Steps.Count, Is.EqualTo(1), "typing or stepping the tempo is one gesture");
            now += 700;
            TransportEdits.SetTempo(rig.Core, bpm + 20);
            Assert.That(stack.Steps.Count, Is.EqualTo(2));

            TransportEdits.SetTempo(rig.Core, 5);                       // refused: out of range
            Assert.That(stack.Steps.Count, Is.EqualTo(2));
            Assert.That(rig.Core.BPM, Is.EqualTo(bpm + 20));

            stack.Undo();
            Assert.That(rig.Core.BPM, Is.EqualTo(bpm + 3));
            stack.Undo();
            Assert.That(rig.Core.BPM, Is.EqualTo(bpm));
            stack.Redo();
            Assert.That(rig.Core.BPM, Is.EqualTo(bpm + 3));

            stack.Clear();
            TransportEdits.SetLooping(rig.Core, false);
            Assert.That(rig.Core.Looping, Is.False);
            Assert.That(stack.Steps.Single().Name, Is.EqualTo("Switch loop"));
            stack.Undo();
            Assert.That(rig.Core.Looping, Is.True);
            stack.Redo();
            Assert.That(rig.Core.Looping, Is.False);
            TransportEdits.SetLooping(rig.Core, false);
            Assert.That(stack.Steps.Count, Is.EqualTo(1), "no change, no step");
        }

        static ParameterCore Wide(MachineCore m) =>
            m.ParameterGroupsList[1].ParametersList.First(p => p.MaxValue - p.MinValue >= 250 && p.Type != ParameterType.Note);

        static int Cc(int controller, int value) => 0xB0 | (controller << 8) | (value << 16);

        [AvaloniaTest]
        public void ControllerMovesAreOneStepWhileStoppedAndNoneWhilePlaying()
        {
            using var rig = new SessionRig("Lead");
            var core = rig.Core;
            var stack = core.SongCore.ActionStack;
            long now = 0;
            stack.Clock = () => now;
            var lead = rig.Machine("Lead");
            var p = Wide(lead);
            var midi = core.MidiControllerAssignments;
            bool soft = Global.MIDISettings.ParameterSoftTakeover;
            try
            {
                Global.MIDISettings.ParameterSoftTakeover = false;
                midi.BindParameter(p, 0, 0, 21);
                stack.Clear();
                p.SetValue(0, p.MinValue);
                int start = p.GetValue(0);

                Assert.That(core.Playing, Is.False);
                foreach (int v in new[] { 10, 30, 60, 90, 120 })
                {
                    midi.SendMidi(Cc(21, v));
                    now += 50;
                }
                int expected = (int)((p.MaxValue - p.MinValue) * 120 / 127f + p.MinValue);
                Assert.That(p.GetValue(0), Is.EqualTo(expected));
                Assert.That(stack.Steps.Count, Is.EqualTo(1), "a sweep is one step");
                Assert.That(stack.Steps[0].Source, Is.EqualTo(HistorySource.Midi));
                Assert.That(stack.Steps[0].Name, Does.StartWith("MIDI: Lead"));
                Assert.That(stack.UsedBytes, Is.LessThan(1000));

                now += 800;                                             // idle: the next sweep is a step of its own
                midi.SendMidi(Cc(21, 5));
                Assert.That(stack.Steps.Count, Is.EqualTo(2));
                stack.Undo();
                Assert.That(p.GetValue(0), Is.EqualTo(expected));
                stack.Undo();
                Assert.That(p.GetValue(0), Is.EqualTo(start), "back to where the first sweep began");
                Assert.That(stack.CanUndo, Is.False);
                stack.Redo();
                stack.Redo();
                int last = (int)((p.MaxValue - p.MinValue) * 5 / 127f + p.MinValue);
                Assert.That(p.GetValue(0), Is.EqualTo(last));

                stack.Clear();
                core.Playing = true;
                rig.RunTicks(2);
                try
                {
                    Assert.That(core.Playing, Is.True);
                    midi.SendMidi(Cc(21, 77));
                    midi.SendMidi(Cc(21, 78));
                }
                finally
                {
                    core.Playing = false;
                }
                Assert.That(p.GetValue(0), Is.Not.EqualTo(last), "the controller still moves it");
                Assert.That(stack.Steps, Is.Empty, "performance isn't recorded");
            }
            finally
            {
                Global.MIDISettings.ParameterSoftTakeover = soft;
                core.Playing = false;
                midi.UnbindAllMIDIControllers(lead);
            }
        }

        [AvaloniaTest]
        public void APatternRenameFromAnEditorIsAStepAndAnUndoIsNotRecordedAgain()
        {
            using var rig = new SessionRig("Lead");
            var song = rig.Core.SongCore;
            var stack = song.ActionStack;
            var lead = rig.Machine("Lead");
            var riff = rig.Pattern(lead, "Riff", 8);
            stack.Clear();

            lead.RenamePattern(riff, "Hook");
            Assert.That(riff.Name, Is.EqualTo("Hook"));
            Assert.That(stack.Steps.Select(s => s.Name), Is.EqualTo(new[] { "Rename pattern" }));
            lead.RenamePattern(riff, "Hook");                          // the same name: taken, nothing happens
            Assert.That(stack.Steps.Count, Is.EqualTo(1));

            song.Undo();
            Assert.That(riff.Name, Is.EqualTo("Riff"));
            Assert.That(stack.Steps.Count, Is.EqualTo(1), "undo didn't record a step");
            Assert.That(stack.CanRedo, Is.True);
            song.Redo();
            Assert.That(riff.Name, Is.EqualTo("Hook"));
            Assert.That(stack.CanRedo, Is.False);
        }

        // A one-cycle WAV: 16-bit mono.
        static string Wav()
        {
            string path = Path.Combine(Path.GetTempPath(), "history-" + Guid.NewGuid().ToString("N") + ".wav");
            const int n = 2048;
            using var s = new MemoryStream();
            using var w = new BinaryWriter(s);
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + n * 2);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(44100); w.Write(88200); w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(n * 2);
            for (int i = 0; i < n; i++) w.Write((short)(Math.Sin(i * 2 * Math.PI / n) * 12000));
            w.Flush();
            File.WriteAllBytes(path, s.ToArray());
            return path;
        }

        static object Get(object o, string property) =>
            o.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(o)!;

        [AvaloniaTest]
        public void AMachinesStateLoadIsAStepWithItsParametersAndBlobsAreCountedOnce()
        {
            var song = Core.SongCore;
            song.CreateMachine("Dye Essence", null, "HistDye", null, null, null, -1, 0.2f, 0.4f);
            var machine = song.MachinesList.First(m => m.Name == "HistDye");
            string wav = Wav();
            var stack = song.ActionStack;
            try
            {
                var dye = machine.ManagedMachine!;
                var table = Array.Empty<ParamDef>();
                var ctx = new EditorContext(machine, table, default)
                {
                    Edits = new ParamEdits(machine, table, a => a()),
                };
                stack.Clear();
                var load = dye.GetType().GetMethod("LoadUserTable", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
                var state = dye.GetType().GetProperty("MachineState")!;
                void Load(int slot) => StateEdit.Run<object, object>(ctx, "Load wavetable", dye,
                    m => state.GetValue(m)!, (m, s) => state.SetValue(m, s), () => load.Invoke(dye, new object[] { slot, wav }));

                Assert.That(Get(Get(dye, "MachineState"), "TableAName"), Is.EqualTo(""));
                Load(0);
                Assert.That(Get(Get(dye, "MachineState"), "TableAName"), Is.Not.EqualTo(""));
                Assert.That(stack.Steps.Select(s => s.Name), Is.EqualTo(new[] { "Load wavetable" }));
                int blob = ((string)Get(Get(dye, "MachineState"), "TableAData")).Length;
                Assert.That(blob, Is.GreaterThan(4096));
                long one = stack.UsedBytes;
                Assert.That(one, Is.GreaterThanOrEqualTo(2L * blob));

                Load(1);                                                // slot A's data is shared by both steps
                Assert.That(stack.Steps.Count, Is.EqualTo(2));
                // Strings are 2 bytes a char. Slot A's blob is in three of the four states the steps hold, B's in one.
                Assert.That(stack.UsedBytes, Is.LessThan(2L * blob * 2 + 2048), "A's blob is counted once and B's once: not once for each state that holds it");

                song.Undo();
                Assert.That(Get(Get(dye, "MachineState"), "TableBName"), Is.EqualTo(""));
                Assert.That(Get(Get(dye, "MachineState"), "TableAName"), Is.Not.EqualTo(""));
                song.Undo();
                Assert.That(Get(Get(dye, "MachineState"), "TableAName"), Is.EqualTo(""));
                song.Redo();
                song.Redo();
                Assert.That(Get(Get(dye, "MachineState"), "TableBName"), Is.Not.EqualTo(""));
            }
            finally
            {
                File.Delete(wav);
                Core.UiHost.ReleaseMachine(machine);
                song.DeleteMachines(new[] { machine });
            }
        }
    }
}
