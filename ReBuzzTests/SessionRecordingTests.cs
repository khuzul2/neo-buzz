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
