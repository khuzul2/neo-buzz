using System.Diagnostics;
using ReBuzz.Platform;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// The CoreMIDI binding for the Mac engine (docs/specs/mac-engine.md M6). The parser runs
    /// everywhere; the loopback runs on macOS CI, through virtual ports this process makes, since
    /// runners have no MIDI devices.
    /// </summary>
    [TestFixture]
    public class CoreMidiTests
    {
        [Test]
        public void TheParserTurnsBytesIntoShortMessages()
        {
            var parser = new MidiParser();
            var got = new List<int>();
            byte[] stream =
            {
                0x90, 60, 100, 62, 101,      // a note on, and one in running status
                0xF8,                        // clock
                0xC0, 5, 6,                  // program changes (one data byte), running
                0xF0, 1, 2, 0xF8, 3, 0xF7,   // SysEx (skipped) with a clock inside it
                0x80, 60, 0,
                0xF2, 1, 2, 3,               // song position; system common doesn't run: 3 is stray
                0xF6,                        // tune request
                0xB0, 7, 0xFE, 100,          // a CC with active sensing inside it
            };
            foreach (byte b in stream)
            {
                int m = parser.Feed(b);
                if (m != 0) got.Add(m);
            }
            Assert.That(got, Is.EqualTo(new[] { 0x643C90, 0x653E90, 0xF8, 0x05C0, 0x06C0, 0xF8, 0x003C80, 0x0201F2, 0xF6, 0xFE, 0x6407B0 }));
            Assert.That(MidiParser.Length(0x90), Is.EqualTo(3));
            Assert.That(MidiParser.Length(0xD0), Is.EqualTo(2));
            Assert.That(MidiParser.Length(0xF8), Is.EqualTo(1));
            Assert.That(MidiParser.Length(0xF0), Is.EqualTo(0));
        }

        /// <summary>Messages a receiver got, in a buffer allocated up front (the receive thread mustn't allocate).</summary>
        sealed class Inbox
        {
            readonly int[] messages = new int[4096];
            int count, thread;
            public long AllocatedFrom = -1, AllocatedTo = -1;

            public void Add(int message)
            {
                int n = Interlocked.Increment(ref count);
                if (n <= messages.Length) messages[n - 1] = message;
                // From the 2nd message on, on the thread that brought it (CoreMIDI's receive thread).
                if (n == 2)
                {
                    thread = Environment.CurrentManagedThreadId;
                    AllocatedFrom = AllocatedTo = GC.GetAllocatedBytesForCurrentThread();
                }
                else if (n > 2 && Environment.CurrentManagedThreadId == thread)
                    AllocatedTo = GC.GetAllocatedBytesForCurrentThread();
            }

            public int Count => Volatile.Read(ref count);
            public int[] All => messages.Take(Math.Min(Count, messages.Length)).ToArray();

            public bool WaitFor(int n, int ms = 3000)
            {
                var sw = Stopwatch.StartNew();
                while (Count < n && sw.ElapsedMilliseconds < ms) Thread.Sleep(5);
                return Count >= n;
            }
        }

        static void OnlyOnMac()
        {
            if (!OperatingSystem.IsMacOS()) Assert.Ignore("CoreMIDI is macOS's");
            Assert.That(CoreMidi.Available, Is.True, "CoreMIDI is a system framework: always there on macOS");
        }

        [Test]
        public void VirtualPortsLoopBackShortMessagesOnly()
        {
            OnlyOnMac();
            var toUs = new Inbox();
            using var destination = new CoreMidi.VirtualDestination("ReBuzz Test In", toUs.Add);
            using var source = new CoreMidi.VirtualSource("ReBuzz Test Out");

            // Listed, with their names, and known as this process's.
            var sources = CoreMidi.Sources();
            var destinations = CoreMidi.Destinations();
            TestContext.Out.WriteLine("sources: " + string.Join(", ", sources) + "\ndestinations: " + string.Join(", ", destinations));
            Assert.That(sources.Any(e => e.Ref == source.Ref && e.Name == "ReBuzz Test Out" && e.Own), Is.True, "our virtual source is listed");
            Assert.That(destinations.Any(e => e.Ref == destination.Ref && e.Name == "ReBuzz Test In" && e.Own), Is.True, "our virtual destination is listed");

            // An output port to the virtual destination: a note on, a long SysEx (skipped), a clock, a note off.
            using (var output = new CoreMidi.Output(destination.Ref))
            {
                output.Send(0x643C90);
                Assert.That(toUs.WaitFor(1), Is.True, "the note on arrived");
                var sysex = new byte[2000];
                sysex[0] = 0xF0;
                sysex[^1] = 0xF7;
                output.Send(sysex);                                  // longer than the buffer allocated up front
                output.Send(0xF8);
                output.Send(new byte[] { 0x80, 60, 0, 62, 0 });     // two note offs, running status
                Assert.That(toUs.WaitFor(4), Is.True, $"all arrived ({toUs.Count})");
            }
            Assert.That(toUs.All, Is.EqualTo(new[] { 0x643C90, 0xF8, 0x003C80, 0x003E80 }));

            // An input port on the virtual source.
            var fromSource = new Inbox();
            using (var input = new CoreMidi.Input(source.Ref, fromSource.Add))
            {
                Thread.Sleep(100);                                  // the connection settles
                source.Send(0x7F3090);
                source.Send(0x4000E0);                              // pitch bend, centred
                Assert.That(fromSource.WaitFor(2), Is.True, $"the virtual source's messages arrived ({fromSource.Count})");
                Assert.That(fromSource.All.Take(2), Is.EqualTo(new[] { 0x7F3090, 0x4000E0 }));

                // Receiving allocates nothing once the receive thread runs (from the 2nd message on).
                for (int i = 0; i < 200; i++) source.Send(0x400090 | (i % 128) << 8);
                Assert.That(fromSource.WaitFor(202), Is.True, $"200 more arrived ({fromSource.Count})");
                Assert.That(fromSource.AllocatedTo - fromSource.AllocatedFrom, Is.EqualTo(0), "bytes allocated on CoreMIDI's thread while receiving");
            }

            // Sending allocates nothing either.
            using (var output = new CoreMidi.Output(destination.Ref))
            {
                output.Send(0x903C90);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++) output.Send(0x903C90);
                Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0), "bytes allocated by 100 sends");
            }
        }

        /// <summary>
        /// Hot-plug: a virtual source coming and going is notified, and listed while it exists.
        /// tools/headless-midi.py checks the same across processes.
        /// </summary>
        [Test]
        public void ANewVirtualSourceIsNotifiedAndListedThenGone()
        {
            OnlyOnMac();
            _ = CoreMidi.Client;
            int changes = 0;
            void Changed() => Interlocked.Increment(ref changes);
            CoreMidi.SetupChanged += Changed;
            try
            {
                Thread.Sleep(200);
                Interlocked.Exchange(ref changes, 0);
                uint made;
                using (var plugged = new CoreMidi.VirtualSource("ReBuzz Test Plug"))
                {
                    made = plugged.Ref;
                    Assert.That(CoreMidi.Sources().Any(e => e.Ref == made && e.Name == "ReBuzz Test Plug" && e.Own), Is.True, "listed");
                    Assert.That(Wait(() => Volatile.Read(ref changes) > 0), Is.True, "CoreMIDI notified the client");
                    Interlocked.Exchange(ref changes, 0);
                }
                Assert.That(Wait(() => Volatile.Read(ref changes) > 0), Is.True, "and when it went");
                Assert.That(CoreMidi.Sources().Any(e => e.Ref == made), Is.False, "gone once disposed");
            }
            finally
            {
                CoreMidi.SetupChanged -= Changed;
            }
        }

        static bool Wait(Func<bool> condition, int ms = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < ms) Thread.Sleep(10);
            return condition();
        }
    }
}
