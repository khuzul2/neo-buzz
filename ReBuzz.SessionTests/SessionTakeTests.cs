using ReBuzz.Core.Session;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// Session recording's pure parts (docs/specs/session-view.md 0.7): what the listener hears
    /// when, stamped MIDI into clip events, where a track's clip was at a heard moment, a take's
    /// count-in, and the metronome click.
    /// </summary>
    [TestFixture]
    public class SessionTakeTests
    {
        const int TB = TakeTime.TimeBase;

        static int On(int note, int velocity, int channel = 0) => 0x90 | channel | (note << 8) | (velocity << 16);
        static int Off(int note, int channel = 0) => 0x80 | channel | (note << 8);
        static int Cc(int number, int value) => 0xB0 | (number << 8) | (value << 16);

        // ------------------------------------------------------------------ heard time

        [Test]
        public void TheHeardPositionIsTheRenderLessTheLatencyStillToPlay()
        {
            // Rendered to 10.5 ticks of 1000 samples; 3000 samples of latency; 1000 played since.
            Assert.That(Math.Abs(TakeTime.Heard(10.5, 1000, 3000, 1000) - 8.5) < 1e-9, Is.True);
            Assert.That(TakeTime.Heard(10.5, 1000, 0, 0), Is.EqualTo(10.5), "no latency: what is rendered");
            Assert.That(TakeTime.Heard(10.5, 1000, 3000, 5000), Is.EqualTo(10.5), "never later than the render");
            Assert.That(TakeTime.Heard(1.0, 1000, 3000, 0), Is.EqualTo(0), "never before the start");
            Assert.That(TakeTime.Heard(-1, 1000, 0, 0), Is.EqualTo(-1), "nothing rendered");
        }

        [Test]
        public void At120BpmAStampLandsWithinOneSubTickOfWhenItWasHeard()
        {
            // 120 BPM, 4 ticks per beat, 44.1 kHz: 5512.5 samples per tick (5512 rendered), and
            // ReBuzz's default sub-tick resolution gives sub-ticks of at least a few hundred samples.
            const double samplesPerTick = 5512;
            const int latency = 512;
            const double subTick = 260;      // samples, the smallest sub-tick (SubTickSize)
            var random = new Random(7);
            for (int i = 0; i < 10000; i++)
            {
                double rendered = 4 + random.NextDouble() * 60;                  // end of the last callback
                double since = random.NextDouble() * latency;                    // samples played out since
                double truth = rendered - (latency - since) / samplesPerTick;     // what the listener hears now
                long time = (long)Math.Floor(TakeTime.Heard(rendered, samplesPerTick, latency, since) * TB);
                double errorSamples = Math.Abs(time / (double)TB - truth) * samplesPerTick;
                Assert.That(errorSamples, Is.LessThan(subTick), $"stamp {i}");
            }
        }

        // ------------------------------------------------------------------ stamped MIDI into events

        [Test]
        public void ANoteIsOneEventFromItsPressToItsRelease()
        {
            var w = new TakeWriter();
            var events = new List<TakeEvent>();
            w.Add(new TakeStamp(On(60, 100), 1000, 120, 16 * TB), events);
            Assert.That(events, Is.Empty, "written when released");
            Assert.That(w.Holding, Is.EqualTo(1));
            w.Add(new TakeStamp(Off(60), 1360, 480, 16 * TB), events);
            Assert.That(events, Is.EqualTo(new[] { new TakeEvent(120, On(60, 100), 360) }));
            Assert.That(w.Holding, Is.EqualTo(0));
        }

        [Test]
        public void ANoteOnWithVelocityZeroReleasesAndChannelsAreApart()
        {
            var w = new TakeWriter();
            var events = new List<TakeEvent>();
            w.Add(new TakeStamp(On(60, 90, channel: 1), 0, 0, 4 * TB), events);
            w.Add(new TakeStamp(On(60, 80), 10, 10, 4 * TB), events);
            w.Add(new TakeStamp(On(60, 0), 50, 50, 4 * TB), events);           // channel 0's release
            Assert.That(events, Is.EqualTo(new[] { new TakeEvent(10, On(60, 80), 40) }));
            w.Add(new TakeStamp(Off(60, channel: 1), 100, 100, 4 * TB), events);
            Assert.That(events[1], Is.EqualTo(new TakeEvent(0, On(60, 90, channel: 1), 100)));
        }

        [Test]
        public void ANoteHeldPastItsClipsEndIsCutThereAndAWrapDoesntMatter()
        {
            var w = new TakeWriter();
            var events = new List<TakeEvent>();
            // Pressed 20 units before the end of a 4-tick loop, released after the wrap (clock time counts).
            w.Add(new TakeStamp(On(64, 100), 1000, 4 * TB - 20, 4 * TB), events);
            w.Add(new TakeStamp(Off(64), 1100, 80, 4 * TB), events);
            Assert.That(events.Single().Duration, Is.EqualTo(20));
        }

        [Test]
        public void PressingAHeldNoteAgainEndsTheFirstOne()
        {
            var w = new TakeWriter();
            var events = new List<TakeEvent>();
            w.Add(new TakeStamp(On(60, 100), 0, 0, 16 * TB), events);
            w.Add(new TakeStamp(On(60, 70), 240, 240, 16 * TB), events);
            w.Add(new TakeStamp(Off(60), 300, 300, 16 * TB), events);
            Assert.That(events, Is.EqualTo(new[] { new TakeEvent(0, On(60, 100), 240), new TakeEvent(240, On(60, 70), 60) }));
        }

        [Test]
        public void ControllersAreEventsOfNoLengthAndStrayReleasesNothing()
        {
            var w = new TakeWriter();
            var events = new List<TakeEvent>();
            w.Add(new TakeStamp(Cc(1, 64), 0, 30, 16 * TB), events);
            w.Add(new TakeStamp(0xE0 | (0 << 8) | (80 << 16), 5, 35, 16 * TB), events);   // pitch bend
            w.Add(new TakeStamp(Off(61), 9, 39, 16 * TB), events);                        // never pressed
            w.Add(new TakeStamp(0xC0 | (5 << 8), 9, 39, 16 * TB), events);                  // program change: not recorded
            Assert.That(events, Is.EqualTo(new[] { new TakeEvent(30, Cc(1, 64), 0), new TakeEvent(35, 0xE0 | (80 << 16), 0) }));
        }

        [Test]
        public void StoppingEndsHeldNotesAndAVeryShortNoteLastsAtLeastOneUnit()
        {
            var w = new TakeWriter();
            var events = new List<TakeEvent>();
            w.Add(new TakeStamp(On(60, 100), 100, 100, 16 * TB), events);
            w.Add(new TakeStamp(On(62, 100), 200, 200, 16 * TB), events);
            w.Add(new TakeStamp(Off(62), 200, 200, 16 * TB), events);
            Assert.That(events.Single().Duration, Is.EqualTo(1));
            w.CloseAll(700, events);
            Assert.That(events[1], Is.EqualTo(new TakeEvent(100, On(60, 100), 600)));
            Assert.That(w.Holding, Is.EqualTo(0));
            w.Add(new TakeStamp(On(65, 1), 0, 0, 16 * TB), events);
            w.Drop();
            Assert.That(w.Holding, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ the scheduler's side

        [Test]
        public void WhereATracksClipWasAtAHeardTick()
        {
            var s = new SessionScheduler<Track, Clip>(c => c.Length);
            var keys = new Track("keys");
            var take = new Clip("take", 8);
            s.Start(0);
            s.LaunchAt(keys, take, 4, true);
            Assert.That(s.TryGetPlayingAt(keys, 2, out _, out _, out _, out _), Is.False, "before anything plays");
            for (int i = 0; i < 4; i++) s.Advance();            // clock 4: it starts
            Assert.That(s.TryGetPlayingAt(keys, 3, out _, out _, out _, out _), Is.False, "a moment before it started");
            for (int i = 0; i < 9; i++) s.Advance();            // clock 13
            Assert.That(s.TryGetPlayingAt(keys, 13, out var clip, out int position, out int end, out long start), Is.True);
            Assert.That(clip, Is.SameAs(take));
            Assert.That((position, end, start), Is.EqualTo((1, 8, 4L)), "looped once: 9 ticks in is position 1");
            Assert.That(s.TryGetPlayingAt(keys, 11, out _, out position, out _, out _), Is.True);
            Assert.That(position, Is.EqualTo(7), "a little earlier, as heard");
        }

        [Test]
        public void ATakeQueuedWhileStoppedStartsAfterItsCountIn()
        {
            var s = new SessionScheduler<Track, Clip>(c => c.Length);
            var keys = new Track("keys");
            var bass = new Track("bass");
            var take = new Clip("take", 16);
            var line = new Clip("line", 16);
            s.LaunchAt(bass, line, 0, true);                    // stopped: "at the start"
            s.LaunchAt(keys, take, 16, true);                   // one bar of count-in
            s.Start(32);                                        // the song starts at bar 3
            Assert.That(s.GetPlaying(bass), Is.SameAs(line));
            Assert.That(s.GetPlaying(keys), Is.Null);
            for (int i = 0; i < 15; i++) s.Advance();
            Assert.That(s.GetPlaying(keys), Is.Null, "counting in");
            s.Advance();                                        // clock 48
            Assert.That(s.GetPlaying(keys), Is.SameAs(take));
            Assert.That(s.GetStartClock(keys), Is.EqualTo(48));
        }

        // ------------------------------------------------------------------ the metronome

        [Test]
        public void TheClickIsShortDecaysAndIsHigherOnTheDownbeat()
        {
            var click = new MetronomeClick();
            var buffer = new float[2 * 4096];
            click.MixInto(buffer, 0, 4096);
            Assert.That(buffer.All(v => v == 0), Is.True, "silent until triggered");

            click.Trigger(accent: false, 44100);
            int length = click.Remaining;
            Assert.That(length, Is.EqualTo((int)(MetronomeClick.Length * 44100)));
            click.MixInto(buffer, 0, 256);
            click.MixInto(buffer, 512, 4096 - 256);             // in two blocks: continuous
            Assert.That(click.Remaining, Is.EqualTo(0));
            Assert.That(buffer[0], Is.EqualTo(buffer[1]), "the same on both sides");
            double early = buffer.Take(200).Max(Math.Abs);
            double late = buffer.Skip(2 * (length - 100)).Take(200).Max(Math.Abs);
            Assert.That(early, Is.InRange(1000, MetronomeClick.Level));
            Assert.That(late, Is.LessThan(early / 20), "decayed");
            Assert.That(buffer.Skip(2 * length).All(v => v == 0), Is.True, "nothing after it");

            var accent = new float[2 * 4096];
            click.Trigger(accent: true, 44100);
            click.MixInto(accent, 0, 4096);
            Assert.That(accent.Take(200).Max(Math.Abs) > early, Is.True, "louder on the bar line");
            int Crossings(float[] b) => Enumerable.Range(1, 400).Count(i => Math.Sign(b[2 * i]) != Math.Sign(b[2 * i - 2]));
            Assert.That(Crossings(accent) > Crossings(buffer), Is.True, "and higher");
        }

        // ------------------------------------------------------------------ settings

        [Test]
        public void RecordSettingsAreSavedOnlyWhenSet()
        {
            var doc = new SessionDocument();
            Assert.That(doc.ToXml().Root!.Element("Record"), Is.Null);
            doc.RecordBars = 4;
            doc.CountInBars = 1;
            doc.Metronome = true;
            var back = SessionDocument.Load(new MemoryStream(doc.ToBytes()))!;
            Assert.That((back.RecordBars, back.CountInBars, back.Metronome), Is.EqualTo((4, 1, true)));
            var undoCopy = back.CloneLayout();
            Assert.That(undoCopy.Metronome, Is.True, "settings are shared, not snapshotted");

            var x = doc.ToXml();
            x.Root!.Element("Record")!.SetAttributeValue("bars", "9999");
            x.Root!.Element("Record")!.SetAttributeValue("countIn", "-3");
            var stream = new MemoryStream();
            x.Save(stream);
            stream.Position = 0;
            var clamped = SessionDocument.Load(stream)!;
            Assert.That((clamped.RecordBars, clamped.CountInBars), Is.EqualTo((SessionDocument.MaxRecordBars, 0)));
        }
    }
}
