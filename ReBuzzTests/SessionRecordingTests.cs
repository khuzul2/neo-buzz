using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using System;
using System.Linq;
using System.Threading;
using static ReBuzzTests.HardeningTests063;
using static ReBuzzTests.SessionClipTests;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: session recording (docs/specs/session-view.md 0.7). Armed tracks take the
    /// keyboard; a take records what is played where it was heard, into the clip's engine MIDI
    /// column, and plays it back from the next pass; overdub, count-in, the metronome, takes
    /// recorded until stopped, undo, and songs keeping takes.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class SessionRecordingTests
    {
        const int Bar = 16;
        const int TB = PatternEvent.TimeBase;

        static (int Time, int Value, int Duration)[] EventsOf(IPattern p) =>
            p.Columns.Where(c => c.Type == PatternColumnType.MIDI).SelectMany(c => c.GetEvents(0, int.MaxValue))
             .OrderBy(e => e.Time).Select(e => (e.Time, e.Value, e.Duration)).ToArray();

        /// <summary>Stamps from now on see no time passing since the last render: heard = rendered − latency.</summary>
        static void FreezeClock() => SessionCore.Timestamp = () => 1_000_000;

        static long TimeAt(long clock, int posInTick, int samplesPerTick) =>
            (long)Math.Floor((clock + (double)posInTick / samplesPerTick) * TB);

        // ------------------------------------------------------------------ arm

        [Test]
        public void AnArmedTrackPlaysTheKeyboardAndAReleaseGoesWhereItsPressWent()
        {
            using var rig = new KeysRig();
            rig.Layout.Ensure();
            rig.Layout.SetArmed(0, true);
            Assert.That(rig.Layout.IsArmed(0), Is.True);

            rig.Core.SendMIDIInput(NoteOn(60, 100));
            Assert.That(rig.NoteOns.Select(n => n.Note), Is.EqualTo(new[] { 60 }), "the armed track's machine plays it");
            rig.Layout.SetArmed(0, false);
            rig.Core.SendMIDIInput(NoteOff(60));
            Assert.That(rig.NoteOffs, Is.EqualTo(new[] { 60 }), "released where it was pressed, though disarmed since");
        }

        // ------------------------------------------------------------------ takes

        [TestCase(0)]
        [TestCase(1)]
        public void ATakeRecordsNotesWhereTheyWereHeardAndPlaysThemFromItsNextPass(int latencyTicks)
        {
            using var rig = new KeysRig();
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetRecordBars(1);
            layout.SetArmed(0, true);
            FreezeClock();

            var take = (PatternCore)layout.RecordTake(0, 0)!;
            Assert.That(take, Is.Not.Null);
            Assert.That(layout.PatternAt(0, 0), Is.SameAs(take));
            Assert.That(take.Length, Is.EqualTo(Bar));
            var track = layout.SequenceOf(0)!;
            Assert.That(layout.IsRecordingInto(track, take), Is.True);

            rig.PlayTo(3, 1234);
            int spt = rig.SamplesPerTick;
            rig.Core.OutputLatencySamples = latencyTicks * spt;
            long start = rig.Session.GetClipStartClock(track);
            Assert.That(start, Is.EqualTo(0));
            rig.Core.SendMIDIInput(NoteOn(60, 99));
            long on = TimeAt(3 - latencyTicks, 1234, spt);
            rig.PlayTo(6, 777);
            rig.Core.SendMIDIInput(NoteOff(60));
            long off = TimeAt(6 - latencyTicks, 777, spt);

            Assert.That(EventsOf(take), Is.EqualTo(new[] { ((int)on, NoteOn(60, 99), (int)(off - on)) }),
                "the note where it was heard, within one 1/240 tick (well within a sub-tick)");

            rig.NoteOns.Clear();
            rig.PlayTo(Bar + 8);
            layout.PollTakes();
            Assert.That(layout.IsRecording(track), Is.False, "the first pass is over");
            var replay = rig.NoteOns.Single();
            Assert.That(replay.Note, Is.EqualTo(60), "the next pass plays it");
            int from = (int)(replay.Clock - Bar) * TB + replay.PosInTick * TB / spt;
            Assert.That(on - from, Is.InRange(0, 256 * TB / spt), "in the block of its position: seamless");
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(take), "and loops on");
        }

        [Test]
        public void ATakeIsOneUndoStepAndRedoBringsItBackWithItsNotes()
        {
            using var rig = new KeysRig();
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetRecordBars(1);
            layout.SetArmed(0, true);
            FreezeClock();
            var take = layout.RecordTake(0, 0)!;
            string name = take.Name;
            rig.PlayTo(2);
            rig.Core.SendMIDIInput(NoteOn(64, 100));
            rig.PlayTo(3);
            rig.Core.SendMIDIInput(NoteOff(64));
            rig.PlayTo(Bar + 1);
            layout.PollTakes();
            var recorded = EventsOf(take);
            Assert.That(recorded.Length, Is.EqualTo(1));

            layout.Undo();
            Assert.That(rig.Keys.Patterns.Any(p => p.Name == name), Is.False, "undo takes the whole take");
            Assert.That(layout.PatternAt(0, 0), Is.Null);
            layout.Redo();
            var again = layout.PatternAt(0, 0)!;
            Assert.That(again.Name, Is.EqualTo(name));
            Assert.That(EventsOf(again), Is.EqualTo(recorded), "with what it recorded");
        }

        [Test]
        public void ATakeRecordedUntilStoppedGetsTheBarsItRecorded()
        {
            using var rig = new KeysRig();
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetRecordBars(0);
            layout.SetArmed(0, true);
            FreezeClock();
            var take = layout.RecordTake(0, 0)!;
            Assert.That(take.Length, Is.EqualTo(SessionDocument.MaxRecordBars * Bar), "until stopped: room for the longest take");
            rig.PlayTo(Bar + 6);
            layout.LaunchClip(layout.SequenceOf(0)!, take);       // launching the take ends its recording
            Assert.That(layout.IsRecording(layout.SequenceOf(0)!), Is.False);
            Assert.That(take.Length, Is.EqualTo(2 * Bar), "1 bar and 6 ticks: two bars");
            rig.PlayTo(2 * Bar + 1);
            Assert.That(rig.Session.GetClipPosition(layout.SequenceOf(0)!), Is.EqualTo(1), "it loops in step");
        }

        [Test]
        public void FromAStoppedTransportTheMetronomeCountsIn()
        {
            using var rig = new KeysRig();
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetRecordBars(1);
            layout.SetCountInBars(1);
            layout.SetArmed(0, true);
            var take = layout.RecordTake(0, 0)!;
            var track = layout.SequenceOf(0)!;

            rig.PlayTo(0);
            int spt = rig.SamplesPerTick;
            // Keys and Tone are silent: what sounds is the click, on the beats of the count-in only.
            for (int tick = 0; tick < Bar + 8; tick++)
            {
                var block = rig.Render(spt);
                double level = block.Take(400).Max(v => Math.Abs(v));
                bool beat = tick % 4 == 0 && tick < Bar;
                Assert.That(level > 0.05, Is.EqualTo(beat), $"tick {tick}");
            }
            Assert.That(rig.Session.GetClipStartClock(track), Is.EqualTo(Bar), "the take starts after one bar");
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(take));
        }

        [Test]
        public void TheMetronomeClicksOnEveryBeatWhileOnAndLouderOnTheBarLine()
        {
            using var rig = new KeysRig();
            rig.Layout.Ensure();
            rig.Layout.SetMetronome(true);
            rig.PlayTo(0);
            int spt = rig.SamplesPerTick;
            var levels = Enumerable.Range(0, 8).Select(_ => (double)rig.Render(spt).Take(400).Max(v => Math.Abs(v))).ToArray();
            Assert.That(levels.Select(l => l > 0.05), Is.EqualTo(new[] { true, false, false, false, true, false, false, false }));
            Assert.That(levels[0] > levels[4] * 1.2, Is.True, $"the bar line louder than the beat ({levels[0]:F3} vs {levels[4]:F3})");
            rig.Layout.SetMetronome(false);
            Assert.That(rig.Render(4 * spt).Max(v => Math.Abs(v)), Is.LessThan(0.05), "off");
        }

        // ------------------------------------------------------------------ overdub

        [Test]
        public void AnOverdubPassIsOneUndoStepThatTakesBackOnlyWhatItAdded()
        {
            using var rig = new KeysRig();
            var clip = rig.Clip("A", Bar, (0, 48));
            var layout = rig.Layout;
            layout.Ensure();
            var track = layout.SequenceOf(0)!;
            FreezeClock();
            rig.Session.Launch(track, clip);
            rig.PlayTo(1);
            layout.SetArmed(0, true);
            layout.SetSessionRecord(true);
            Assert.That(layout.IsRecordingInto(track, clip), Is.True);

            rig.PlayTo(4);
            rig.Core.SendMIDIInput(NoteOn(67, 90));
            rig.PlayTo(5);
            rig.Core.SendMIDIInput(NoteOff(67));
            Assert.That(EventsOf(clip).Select(e => (e.Value >> 8) & 0x7F), Is.EqualTo(new[] { 48, 67 }));
            layout.SetSessionRecord(false);
            Assert.That(layout.IsRecording(track), Is.False);

            layout.Undo();
            Assert.That(EventsOf(clip).Select(e => (e.Value >> 8) & 0x7F), Is.EqualTo(new[] { 48 }), "the pass only");
            layout.Redo();
            Assert.That(EventsOf(clip).Select(e => (e.Value >> 8) & 0x7F), Is.EqualTo(new[] { 48, 67 }));
        }

        [Test]
        public void SessionRecordStartsATakeOnAnArmedTrackPlayingNothing()
        {
            using var rig = new KeysRig();
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetRecordBars(2);
            layout.SetArmed(0, true);
            layout.SetSessionRecord(true, scene: 0);
            var take = layout.PatternAt(0, 0);
            Assert.That(take, Is.Not.Null);
            Assert.That(take!.Length, Is.EqualTo(2 * Bar));
            Assert.That(layout.IsRecordingInto(layout.SequenceOf(0)!, take), Is.True);
            layout.SetSessionRecord(false);
            Assert.That(layout.IsRecording(layout.SequenceOf(0)!), Is.False);
        }

        [Test]
        public void ClearingRecordedNotesIsUndoable()
        {
            using var rig = new KeysRig();
            var clip = rig.Clip("A", Bar, (0, 48), (TB, 50));
            var layout = rig.Layout;
            layout.Ensure();
            Assert.That(layout.RecordedNoteCount(clip), Is.EqualTo(2));
            layout.ClearRecordedNotes(clip);
            Assert.That(layout.RecordedNoteCount(clip), Is.EqualTo(0));
            layout.Undo();
            Assert.That(layout.RecordedNoteCount(clip), Is.EqualTo(2));
        }

        // ------------------------------------------------------------------ Capture MIDI and automation

        [Test]
        public void CaptureTurnsWhatWasJustPlayedIntoAClipThatPlaysOnInPhase()
        {
            using var rig = new KeysRig();
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetArmed(0, true);
            FreezeClock();
            rig.PlayTo(18);
            rig.Core.SendMIDIInput(NoteOn(60, 100));             // not recording: just playing
            rig.PlayTo(19);
            rig.Core.SendMIDIInput(NoteOff(60));
            rig.PlayTo(24);
            rig.Core.SendMIDIInput(NoteOn(62, 100));
            rig.PlayTo(25);
            rig.Core.SendMIDIInput(NoteOff(62));
            rig.PlayTo(30);

            var made = layout.CaptureMidi(0);
            Assert.That(made.Count, Is.EqualTo(1));
            var clip = made[0];
            Assert.That(layout.PatternAt(0, 0), Is.SameAs(clip));
            Assert.That(clip.Length, Is.EqualTo(Bar), "from the bar line before the first note: one bar");
            Assert.That(EventsOf(clip), Is.EqualTo(new[] { (2 * TB, NoteOn(60, 100), TB), (8 * TB, NoteOn(62, 100), TB) }));

            var track = layout.SequenceOf(0)!;
            rig.PlayTo(32);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(clip));
            Assert.That(rig.Session.GetClipPosition(track), Is.EqualTo(0), "in phase with what was played: bar 3 is its start again");
            Assert.That(layout.CaptureMidi(0), Is.Empty, "taken out: nothing new to capture");

            layout.Undo();
            Assert.That(layout.PatternAt(0, 0), Is.Null, "one undo step");
        }

        [Test]
        public void AParameterMovedWhileATakeRecordsIsAutomationTheEnginePlaysBack()
        {
            using var rig = new KeysRig();
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetRecordBars(1);
            layout.SetArmed(0, true);
            var bypass = rig.Keys.ParameterGroups[1].Parameters[0];
            FreezeClock();
            var take = layout.RecordTake(0, 0)!;

            rig.PlayTo(2, 500);
            bypass.SetValue(0, 1);                               // moved by hand
            Assert.That(layout.RecordedAutomationCount(take), Is.EqualTo(1));
            var column = take.Columns.Single(c => c.Type == PatternColumnType.Parameter);
            Assert.That(column.Parameter, Is.SameAs(bypass));
            var point = column.GetEvents(0, int.MaxValue).Single();
            Assert.That(point.Time, Is.EqualTo((int)TimeAt(2, 500, rig.SamplesPerTick)));
            Assert.That(point.Value, Is.EqualTo(1));

            rig.PlayTo(Bar - 2);
            bypass.SetValue(1 << 16, 0);                         // back, without recording
            rig.PlayTo(Bar + 4);
            Assert.That(bypass.GetValue(0), Is.EqualTo(1), "the next pass plays it");
        }

        [Test]
        public void AutomationPlaysBackDuringAnOverdubWithoutBeingRecordedAgain()
        {
            using var rig = new KeysRig();
            var clip = rig.Clip("A", Bar);
            var bypass = rig.Keys.ParameterGroups[1].Parameters[0];
            var automation = SessionLayout.ColumnFor(clip, new ColumnKey(1, 0, 0), create: true)!;
            automation.SetEvents(new[] { new PatternEvent(2 * TB, 1) }, true);
            var layout = rig.Layout;
            layout.Ensure();
            var track = layout.SequenceOf(0)!;
            rig.Session.Launch(track, clip);
            rig.PlayTo(1);
            layout.SetArmed(0, true);
            layout.SetSessionRecord(true);
            Assert.That(layout.IsRecordingInto(track, clip), Is.True, "overdubbing");

            rig.PlayTo(4);
            Assert.That(bypass.GetValue(0), Is.EqualTo(1), "played back");
            layout.SetSessionRecord(false);
            Assert.That(layout.RecordedAutomationCount(clip), Is.EqualTo(1),
                "not recorded again (playback sets it with the don't-record flag, from the audio thread)");
            Assert.That(layout.UndoStack.CanUndo, Is.False, "an overdub pass that recorded nothing is no undo step");
        }

        [Test]
        public void ATakeRecordedUntilStoppedEndsWhereTheTransportStopped()
        {
            using var rig = new KeysRig();
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetRecordBars(0);
            layout.SetArmed(0, true);
            FreezeClock();
            var take = layout.RecordTake(0, 0)!;
            rig.PlayTo(Bar + 4);
            rig.Core.SendMIDIInput(NoteOn(60, 100));             // held when the transport stops
            rig.PlayTo(Bar + 6);
            layout.PollTakes();                                  // the view's timer
            rig.Core.Playing = false;
            layout.PollTakes();
            Assert.That(layout.IsRecording(layout.SequenceOf(0)!), Is.False);
            Assert.That(take.Length, Is.EqualTo(2 * Bar), "1 bar and 6 ticks: two bars (not one, nor the longest)");
            Assert.That(EventsOf(take).Single().Duration, Is.EqualTo(2 * TB), "the held note ends where it stopped");
        }

        [Test]
        public void ATakeRecordedUntilStoppedAndStoppedOnTheBarLineHasTheBarsBeforeIt()
        {
            using var rig = new KeysRig();
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetRecordBars(0);
            layout.SetArmed(0, true);
            var take = layout.RecordTake(0, 0)!;
            var track = layout.SequenceOf(0)!;
            rig.PlayTo(Bar + 6);
            layout.PollTakes();                                  // the view's timer
            rig.Session.StopAll();                               // on the next bar line: 32
            rig.PlayTo(2 * Bar + 3);
            Assert.That(rig.Session.GetPlaying(track), Is.Null);
            layout.PollTakes();
            Assert.That(take.Length, Is.EqualTo(2 * Bar), "stopped on tick 32: two bars, not three");
        }

        // ------------------------------------------------------------------ songs

        [TestCase(".bmx")]
        [TestCase(".bmxml")]
        public void ASongKeepsItsTakes(string extension)
        {
            using var rig = new KeysRig();
            rig.Clip("take", Bar, (TB * 3 + 17, 60), (TB * 9, 62));
            rig.Layout.Ensure();
            rig.Layout.SetRecordBars(4);
            rig.Layout.SetMetronome(true);

            string path = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "takes-" + Guid.NewGuid().ToString("N") + extension);
            rig.Core.SaveSongCopy(path);
            rig.Core.Modified = false;
            rig.Core.OpenSongFile(path);
            rig.Driver.AssertNoErrorsReportedToUser();

            var keys = rig.Core.SongCore.MachinesList.Single(m => m.Name == "Keys");
            var take = keys.PatternsList.Single(p => p.Name == "take");
            Assert.That(EventsOf(take).Select(e => e.Time), Is.EqualTo(new[] { TB * 3 + 17, TB * 9 }));
            Assert.That(take.Columns.Single(c => c.Type == PatternColumnType.MIDI).Machine, Is.SameAs(keys));
            Assert.That(rig.Layout.Document!.RecordBars, Is.EqualTo(4));
            Assert.That(rig.Layout.Document!.Metronome, Is.True);
        }
    }
}
