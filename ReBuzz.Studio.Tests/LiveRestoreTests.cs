// Fork (docs/specs/history-snapshots.md HS3): a snapshot restored while the song plays. The harness drives the engine's ticks as
// the audio thread does (SessionRig.RunTicks), so a boundary lands where the Session launch clock says; what is asserted is what
// the song does before, at and after it: nothing before, glides and swaps at it, the old machines gone and the new ones named after.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Headless.NUnit;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Core.SongHistory;

namespace ReBuzz.Studio.Tests
{
    public class LiveRestoreTests
    {
        static ParameterCore Continuous(MachineCore m) =>
            m.ParameterGroupsList[1].ParametersList.OfType<ParameterCore>().First(p => p.Flags.HasFlag(ParameterFlags.State) && p.Type == ParameterType.Word && p.MaxValue - p.MinValue > 100 && p.Name != "BPM");

        /// <summary>Plays to the middle of a bar (so the next bar is a boundary ahead).</summary>
        static void PlayIntoBar(SessionRig rig)
        {
            rig.Core.Playing = true;
            rig.RunTicks(2);
            Assert.That(rig.Core.PlayRequested);
        }

        static void Finish(SessionRig rig, int maxTicks = 200)
        {
            for (int i = 0; i < maxTicks && rig.Core.Snapshots.LiveActive; i++)
            {
                rig.RunTicks(1);
                rig.Core.Snapshots.Pump();
                System.Threading.Thread.Sleep(2);
            }
            Assert.That(rig.Core.Snapshots.LiveActive, Is.False, "the restore finished");
        }

        [AvaloniaTest]
        public void ParametersGlideToTheSnapshotAtTheNextBarAndNothingMovesBefore()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = SnapshotTests.Song("Lead", "Pad");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var p = Continuous(rig.Machine("Lead"));
            int original = p.GetValue(0);
            var snap = core.Snapshots.Capture("a");
            core.Snapshots.Flush();
            int changed = original < (p.MinValue + p.MaxValue) / 2 ? p.MaxValue - 1 : p.MinValue + 1;
            p.SetValue(0, changed);
            PlayIntoBar(rig);
            using var restore = Studio.AfterTest(() => SnapshotTests.Silence(core));

            var r = core.Snapshots.Restore(snap);
            Assert.That(r.Mode, Is.EqualTo("live"));
            Assert.That(r.Waiting, Is.True, "it waits for the boundary");
            Assert.That(r.Quantize, Is.EqualTo("bar"), "the default while playing");
            var plan = core.ActiveRestore!;
            Assert.That(plan.At % core.SessionCore.GetQuantizationTicks(LaunchQuantization.Bar), Is.EqualTo(0), "on the bar grid");
            Assert.That(p.GetValue(0), Is.EqualTo(changed), "nothing moved yet");

            var seen = new List<int>();
            for (int i = 0; i < 400 && core.Snapshots.LiveActive; i++)
            {
                rig.RunTicks(1);
                seen.Add(p.GetValue(0));
                if (core.Session.Clock < plan.At) Assert.That(p.GetValue(0), Is.EqualTo(changed), "still the old value before the boundary (tick " + core.Session.Clock + " of " + plan.At + ")");
                core.Snapshots.Pump();
            }
            Finish(rig);
            Assert.That(p.GetValue(0), Is.EqualTo(original), "the snapshot's value");
            var steps = seen.Where((v, i) => i == 0 || v != seen[i - 1]).ToList();
            TestContext.Out.WriteLine("glide values at each tick: " + string.Join(" ", steps));
            Assert.That(steps.Count, Is.GreaterThanOrEqualTo(3), "it moved in steps, not in one jump");
            Assert.That(steps.Zip(steps.Skip(1), (a, b) => original > changed ? b >= a : b <= a).All(x => x), "monotonic");
            Assert.That(core.Snapshots.Diff(snap, null), Is.Empty);
        }

        /// <summary>
        /// The audio thread's part of a restore (swapping event lists, setting values, flags and wire levels, the glide) allocates nothing: the bytes
        /// allocated by ticks across the boundary equal those of the same ticks without a restore. (Here the test thread is the audio thread.)
        /// </summary>
        [AvaloniaTest]
        public void TheBoundaryAndTheGlideAllocateNothingOnTheAudioThread()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = SnapshotTests.Song("Lead", "Pad");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var lead = rig.Machine("Lead");
            var pad = rig.Machine("Pad");
            var snap = core.Snapshots.Capture("a");
            core.Snapshots.Flush();
            // Everything an in-place restore touches: a gliding parameter, a stepping one, a mute, a wire level, pattern events, track events.
            var cont = Continuous(lead);
            var stepper = lead.ParameterGroupsList[1].ParametersList.OfType<ParameterCore>().First(p => p.Flags.HasFlag(ParameterFlags.State) && p.Type == ParameterType.Byte && p.MaxValue - p.MinValue <= 32 && p != cont);
            cont.SetValue(0, cont.MaxValue - 5);
            stepper.SetValue(0, stepper.GetValue(0) == stepper.MaxValue ? stepper.MinValue : stepper.MaxValue);
            pad.IsMuted = true;
            lead.AllOutputs.First().Amp = 0x2000;
            var pattern = lead.PatternsList.Single(x => x.Name == "P0");
            rig.Layout.EditNotes(pattern, null, new[] { new ClipNote(1 * SessionRig.TB, SessionRig.TB, 70, 100) });
            core.SongCore.SequencesList.First(s => s.Machine == lead).SetEvent(32, new SequenceEvent(SequenceEventType.PlayPattern, pattern));
            PlayIntoBar(rig);
            using var restore = Studio.AfterTest(() => SnapshotTests.Silence(core));
            for (int i = 0; i < 40; i++) rig.RunTicks(1, runJobs: false);        // warm up: first runs of everything
            long Ticks(int n)
            {
                long a = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < n; i++) rig.RunTicks(1, runJobs: false);
                return GC.GetAllocatedBytesForCurrentThread() - a;
            }
            long baseline = Ticks(24);
            var r = core.Snapshots.Restore(snap, RestoreQuantize.Beat);
            Assert.That(r.Mode, Is.EqualTo("live"), r.Reason);
            Assert.That(core.ActiveRestore!.Params.Length, Is.GreaterThanOrEqualTo(2));
            Assert.That(core.ActiveRestore.Columns.Length + core.ActiveRestore.Sequences.Length, Is.GreaterThanOrEqualTo(1));
            long during = Ticks(24);                                              // the boundary and the glide are inside these ticks
            Assert.That(core.ActiveRestore == null || core.ActiveRestore.State >= LiveRestorePlan.GlideDone, "the restore applied inside the measured ticks");
            TestContext.Out.WriteLine($"audio-thread allocation over 24 ticks: {baseline} bytes without a restore, {during} bytes across one");
            Assert.That(during, Is.LessThanOrEqualTo(baseline), "a restore adds no audio-thread allocation");
            Finish(rig);
        }

        [AvaloniaTest]
        public void NoneAppliesOnTheNextTickAndBeatAtTheBeatGrid()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = SnapshotTests.Song("Lead");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var p = Continuous(rig.Machine("Lead"));
            var snap = core.Snapshots.Capture("a");
            core.Snapshots.Flush();
            p.SetValue(0, p.MaxValue - 3);
            PlayIntoBar(rig);
            using var restore = Studio.AfterTest(() => SnapshotTests.Silence(core));
            var r = core.Snapshots.Restore(snap, RestoreQuantize.Now);
            long clock = core.Session.Clock;
            Assert.That(core.ActiveRestore!.At, Is.EqualTo(clock + 1), "the next tick");
            Finish(rig);
            r = core.Snapshots.Restore(core.Snapshots.Capture("b"), RestoreQuantize.Beat);
            Assert.That(r.Waiting, Is.False, "nothing differs");
            p.SetValue(0, p.MaxValue - 9);
            var snapB = core.Snapshots.Capture("c");
            p.SetValue(0, p.MaxValue - 5);
            r = core.Snapshots.Restore(snapB, RestoreQuantize.Beat);
            Assert.That(core.ActiveRestore!.At % core.SessionCore.GetQuantizationTicks(LaunchQuantization.Beat), Is.EqualTo(0));
            Finish(rig);
        }

        [AvaloniaTest]
        public void PatternsAndTracksChangeTogetherAtTheBoundary()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = SnapshotTests.Song("Lead", "Pad");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var lead = rig.Machine("Lead");
            var snap = core.Snapshots.Capture("a");
            core.Snapshots.Flush();
            // Edit a pattern's notes and the arrangement after the snapshot.
            var pattern = lead.PatternsList.Single(x => x.Name == "P0");
            rig.Layout.EditNotes(pattern, null, new[] { new ClipNote(1 * SessionRig.TB, SessionRig.TB, 70, 100) });
            var seq = core.SongCore.SequencesList.First(s => s.Machine == lead);
            seq.SetEvent(32, new SequenceEvent(SequenceEventType.PlayPattern, pattern));
            string Notes() => string.Join(";", pattern.ColumnsList.OfType<PatternColumnCore>().SelectMany(c => c.GetEvents(0, 100000)).Select(e => e.Time + "/" + e.Value));
            string notesNow = Notes();
            int eventsNow = seq.EventsList.Count;
            PlayIntoBar(rig);
            using var restore = Studio.AfterTest(() => SnapshotTests.Silence(core));
            var r = core.Snapshots.Restore(snap);
            Assert.That(r.Mode, Is.EqualTo("live"));
            Assert.That(Notes(), Is.EqualTo(notesNow), "the pattern isn't touched before the boundary");
            Assert.That(seq.EventsList.Count, Is.EqualTo(eventsNow));
            long at = core.ActiveRestore!.At;
            while (core.Session.Clock < at - 1) rig.RunTicks(1);
            Assert.That(Notes(), Is.EqualTo(notesNow), "still before the boundary tick");
            rig.RunTicks(1);
            Assert.That(Notes(), Is.Not.EqualTo(notesNow), "the pattern changed at the boundary");
            Assert.That(seq.EventsList.Count, Is.EqualTo(eventsNow - 1), "and the track with it");
            Finish(rig);
            Assert.That(core.Snapshots.Diff(snap, null), Is.Empty);
        }

        [AvaloniaTest]
        public void StructureChangesAreBuiltBesideAndSwappedWithAFade()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = SnapshotTests.Song("Lead");
            rig.Add("Neo Poly", "Pad", track: false);                            // machines without patterns have no pattern editor: they can be rebuilt live
            rig.Add("Neo Poly", "Aux", track: false);
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var snap = core.Snapshots.Capture("a");
            core.Snapshots.Flush();
            rig.Add("Neo Poly", "Extra", track: false);                          // added since: goes
            core.SongCore.DeleteMachines(new[] { (IMachine)rig.Machine("Pad") });  // deleted since: comes back
            var oldAux = rig.Machine("Aux");
            oldAux.TrackCount = 3;                                               // its shape changed: it is rebuilt
            PlayIntoBar(rig);
            using var restore = Studio.AfterTest(() => SnapshotTests.Silence(core));
            var r = core.Snapshots.Restore(snap);
            Assert.That(r.Mode, Is.EqualTo("live"), r.Reason);
            var plan = core.ActiveRestore!;
            Assert.That(plan.Replacements.Count, Is.EqualTo(1));
            Assert.That(plan.Removed.Count, Is.EqualTo(1));
            Assert.That(plan.Added.Count, Is.EqualTo(1));
            Assert.That(core.SongCore.Machines.Select(m => m.Name), Is.EquivalentTo(new[] { "Master", "Lead", "Aux", "Extra" }), "the new machines are built hidden: the song looks as it did");
            var fresh = plan.Replacements[0].New;
            Assert.That(fresh.Hidden, Is.True);
            Assert.That(fresh.AllOutputs.OfType<MachineConnectionCore>().All(c => c.Amp == 0), "its outputs are silent until the boundary");
            Assert.That(plan.Wires.Count(w => w.Amp == 0x4000), Is.GreaterThanOrEqualTo(2), "and ramp up at the boundary");
            Finish(rig);
            Assert.That(core.SongCore.Machines.Select(m => m.Name), Is.EquivalentTo(new[] { "Master", "Lead", "Aux", "Pad" }));
            var nowAux = rig.Machine("Aux");
            Assert.That(nowAux, Is.Not.SameAs(oldAux), "the machine was swapped");
            Assert.That(nowAux.Hidden, Is.False);
            Assert.That(nowAux.TrackCount, Is.EqualTo(oldAux.TrackCount - 2));
            foreach (var name in new[] { "Aux", "Pad" })
            {
                var m = rig.Machine(name);
                Assert.That(m.Hidden, Is.False);
                Assert.That(m.AllOutputs.OfType<MachineConnectionCore>().Single().Amp, Is.EqualTo(0x4000), name + " plays on the master");
            }
            Assert.That(core.SongCore.MachinesList.Count(m => m.Name.Contains("restore")), Is.EqualTo(0));
            Assert.That(core.Snapshots.Diff(snap, null), Is.Empty);
        }

        [AvaloniaTest]
        public void APatternEditorsDataThatDiffersCantBeSwappedWhilePlayingSoTheRestoreWaits()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = SnapshotTests.Song("Lead");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var snap = core.Snapshots.Capture("a");
            core.Snapshots.Flush();
            rig.Machine("Lead").PatternsList.Single(x => x.Name == "P0").Length = 32;
            PlayIntoBar(rig);
            using var restore = Studio.AfterTest(() => SnapshotTests.Silence(core));
            var r = core.Snapshots.Restore(snap);
            Assert.That(r.Mode, Is.EqualTo("deferred"));
            Assert.That(r.Reason, Does.Contain("pattern editor"));
            Assert.That(r.Text, Does.StartWith("The restore waits until the transport stops"));
            Assert.That(rig.Machine("Lead").PatternsList.Single(x => x.Name == "P0").Length, Is.EqualTo(32), "unchanged while it plays");
            SnapshotTests.Silence(core);
            core.Snapshots.Pump();
            Assert.That(rig.Machine("Lead").PatternsList.Single(x => x.Name == "P0").Length, Is.EqualTo(16));
        }

        [AvaloniaTest]
        public void WhatCantBePreparedWhilePlayingWaitsForTheStopAndSaysSo()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = SnapshotTests.Song("Lead");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var snap = core.Snapshots.Capture("a");
            core.Snapshots.Flush();
            string wav = SnapshotTests.Wav(SnapshotTests.Temp("wave") + ".wav", 1);
            try
            {
                core.SongCore.WavetableCore.LoadWave(1, wav, "noise", false);
                rig.Add("Neo Poly", "Extra");
                PlayIntoBar(rig);
                using var restore = Studio.AfterTest(() => SnapshotTests.Silence(core));
                var r = core.Snapshots.Restore(snap);
                Assert.That(r.Mode, Is.EqualTo("deferred"));
                Assert.That(r.Reason, Does.Contain("waves"));
                Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Extra"), "unchanged while it plays");
                Assert.That(core.Snapshots.Deferred, Is.Not.Null);
                rig.RunTicks(2);
                core.Snapshots.Pump();
                Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Extra"), "still unchanged while it plays");
                SnapshotTests.Silence(core);
                core.Snapshots.Pump();
                Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Extra"), Is.False, "applied at the stop");
                Assert.That(core.Snapshots.Deferred, Is.Null);
                Assert.That(core.SongCore.ActionStack.Steps.Last().Name, Does.Contain("waited for stop"));
            }
            finally { try { System.IO.File.Delete(wav); } catch (System.IO.IOException) { } }
        }

        [AvaloniaTest]
        public void ARestoreIsOneStepOfTheHistoryAndUndoGoesBackWhetherStoppedOrPlaying()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = SnapshotTests.Song("Lead");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var p = Continuous(rig.Machine("Lead"));
            int original = p.GetValue(0);
            var snap = core.Snapshots.Capture("a");
            core.Snapshots.Flush();
            p.SetValue(0, p.MaxValue - 7);
            core.SongCore.ActionStack.Clear();
            int changed = p.GetValue(0);

            core.Snapshots.Restore(snap);
            var stack = core.SongCore.ActionStack;
            Assert.That(stack.Steps.Count, Is.EqualTo(1));
            Assert.That(stack.Steps[0].Name, Does.StartWith("Restore \"a\""));
            Assert.That(Continuous(rig.Machine("Lead")).GetValue(0), Is.EqualTo(original));
            stack.Undo();
            Assert.That(Continuous(rig.Machine("Lead")).GetValue(0), Is.EqualTo(changed), "undo puts the song back as it was before the restore");
            Assert.That(core.SongCore.ActionStack, Is.SameAs(stack), "the history is the same one");
            stack.Redo();
            Assert.That(Continuous(rig.Machine("Lead")).GetValue(0), Is.EqualTo(original));
            stack.Undo();

            PlayIntoBar(rig);
            using var restore = Studio.AfterTest(() => SnapshotTests.Silence(core));
            core.Snapshots.Restore(snap);
            Finish(rig);
            Assert.That(Continuous(rig.Machine("Lead")).GetValue(0), Is.EqualTo(original));
            Assert.That(stack.Steps.Count(s => s.Name.StartsWith("Restore")), Is.EqualTo(1), "one step, however it was applied");
            stack.Undo();
            Finish(rig);
            Assert.That(Continuous(rig.Machine("Lead")).GetValue(0), Is.EqualTo(changed), "undo of a live restore is a live restore back");
        }
    }
}
