using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.MachineTests
{
    /// <summary>The notes' queue into every Neo synth (NoteInput, docs/specs/notes.md G5): a flood of notes can never leave one stuck.</summary>
    [TestFixture]
    public class NoteQueueTests
    {
        sealed class Sink : INoteSink
        {
            public readonly HashSet<int> Held = new();
            public int Ons, Offs;
            public void NoteOn(int key, int note, float velocity) { Held.Add(note); Ons++; }
            public void NoteOff(int key) { Held.Remove(key - NoteInput.MidiKeyBase); Offs++; }
            public void ModWheel(float value) { }
            public void PitchBend(float value) { }
            public void Sustain(bool down) { }
            public void Aftertouch(float value) { }
            public void AllNotesOff(bool hard) { }
        }

        [Test]
        public void ANoteOffIsNeverDroppedWhileTheQueueTakesOnlyNoteOnsUpToItsReserve()
        {
            var input = new NoteInput(1);
            var sink = new Sink();
            // A flood of note-ons in one block (far more than the queue holds), then the offs of the first notes.
            for (int i = 0; i < 10000; i++) input.MidiNote(0, i % 128, 100);
            Assert.That(input.Dropped, Is.GreaterThan(0), "the flood did not fit and was counted");
            for (int n = 0; n < 128; n++) input.MidiNote(0, n, 0);
            input.Drain(sink);
            Assert.That(sink.Offs, Is.EqualTo(128), "every note-off reached the synth");
            Assert.That(sink.Held, Is.Empty, "no note is left held");
        }

        [Test]
        public void SixteenThousandEventsAcrossBlocksAreAllTakenAndNothingIsDroppedWhenTheyArriveInTime()
        {
            var input = new NoteInput(1);
            var sink = new Sink();
            for (int block = 0; block < 100; block++)
            {
                for (int i = 0; i < 160; i++) input.MidiNote(0, (block + i) % 128, i % 2 == 0 ? 90 : 0);
                input.Drain(sink);
            }
            Assert.That(input.Dropped, Is.EqualTo(0));
            Assert.That(sink.Ons + sink.Offs, Is.EqualTo(16000));
        }

        [Test]
        public void TheQueueHoldsMoreThanTheOldThousandAndTwentyFourEvents()
        {
            var input = new NoteInput(1);
            var sink = new Sink();
            for (int i = 0; i < 3000; i++) input.MidiNote(0, i % 128, i % 3 == 2 ? 0 : 80);
            input.Drain(sink);
            Assert.That(sink.Ons + sink.Offs, Is.EqualTo(3000), "3000 events in one block all arrive");
            Assert.That(input.Dropped, Is.EqualTo(0));
        }

        [Test]
        public void NoteWireEventsCarryTheirVelocityIntoTheQueue()
        {
            var input = new NoteInput(1);
            var seen = new List<(int Note, float Velocity)>();
            var sink = new Rec(seen);
            var events = new[] { NoteMessage.On(0, 0, 60, 30), NoteMessage.On(0, 0, 64, 127), NoteMessage.Off(10, 0, 60) };
            input.Receive(events);
            input.Drain(sink);
            Assert.That(seen[0].Velocity, Is.EqualTo(30 / 127f).Within(1e-6));
            Assert.That(seen[1].Velocity, Is.EqualTo(1f).Within(1e-6));
        }

        sealed class Rec : INoteSink
        {
            readonly List<(int, float)> seen;
            public Rec(List<(int, float)> seen) { this.seen = seen; }
            public void NoteOn(int key, int note, float velocity) => seen.Add((note, velocity));
            public void NoteOff(int key) { }
            public void ModWheel(float value) { }
            public void PitchBend(float value) { }
            public void Sustain(bool down) { }
            public void Aftertouch(float value) { }
            public void AllNotesOff(bool hard) { }
        }
    
        // ---- Neo Plug's queue of events for the hosted plug-in (PlugEventQueue, notes.md G9)

        struct Ev { public int Kind, Key; }   // 1 note-on, 2 note-off, 3 controller

        [Test]
        public void APlugInsQueueNeverDropsANoteOffOrControllerWhenFloodedWithNoteOns()
        {
            var q = new NeoBuzz.Plug.PlugEventQueue<Ev>(1024, 128);
            for (int i = 0; i < 5000; i++) q.Push(new Ev { Kind = 1, Key = i % 128 }, true);
            Assert.That(q.Count, Is.EqualTo(1024 - 128), "note-ons stop at the reserve");
            Assert.That(q.Dropped, Is.EqualTo(5000 - (1024 - 128)));
            for (int i = 0; i < 128; i++) q.Push(new Ev { Kind = i % 2 == 0 ? 2 : 3, Key = i }, false);
            Assert.That(q.Count, Is.EqualTo(1024), "the reserve takes every note-off and controller");
            var dst = new Ev[1024]; int n = 0;
            q.DrainTo(dst, ref n, 1023);
            Assert.That(n, Is.GreaterThan(0));
            int offs = 0; for (int i = 0; i < n; i++) if (dst[i].Kind == 2) offs++;
            Assert.That(offs, Is.EqualTo(64), "all 64 note-offs reached the block's events");
            Assert.That(q.Count, Is.EqualTo(0), "drained");
        }

        [Test]
        public void APlugInsQueueKeepsOrderAndNoteOnsStopShortOfTheBlocksLimit()
        {
            var q = new NeoBuzz.Plug.PlugEventQueue<Ev>(1024, 128);
            q.Push(new Ev { Kind = 1, Key = 1 }, true); q.Push(new Ev { Kind = 2, Key = 1 }, false); q.Push(new Ev { Kind = 1, Key = 2 }, true); q.Push(new Ev { Kind = 2, Key = 2 }, false);
            var dst = new Ev[1024]; int n = 0;
            q.DrainTo(dst, ref n, 1023);
            Assert.That(n, Is.EqualTo(4));
            Assert.That(new[] { dst[0].Key, dst[1].Key, dst[2].Key, dst[3].Key }, Is.EqualTo(new[] { 1, 1, 2, 2 }));
            Assert.That(new[] { dst[0].Kind, dst[1].Kind, dst[2].Kind, dst[3].Kind }, Is.EqualTo(new[] { 1, 2, 1, 2 }));
            // a block already nearly full of events: the note-on is left out, the note-off still gets in
            var small = new NeoBuzz.Plug.PlugEventQueue<Ev>(16, 4);
            small.Push(new Ev { Kind = 1, Key = 9 }, true); small.Push(new Ev { Kind = 2, Key = 9 }, false);
            var full = new Ev[8]; n = 6;
            small.DrainTo(full, ref n, 7);
            Assert.That(n, Is.EqualTo(7), "only the note-off was added");
            Assert.That(full[6].Kind, Is.EqualTo(2));
        }
    }
}
