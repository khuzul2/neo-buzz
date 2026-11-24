// Fork (docs/specs/midi-mapping.md, MM1): the mapping model and its maths on their own: sources, the saved forms, range and invert, the three
// takeovers, relative encoders and how an encoder is told from a knob. Two named devices stand in for two controllers (no model names in logic).

using ReBuzz.Midi.Mapping;

namespace ReBuzz.SessionTests
{
    [TestFixture]
    public class MapModelTests
    {
        const string KeysA = "Keys A";
        const string PadsB = "Pads B";

        static int Cc(int channel, int number, int value) => 0xB0 | channel | (number << 8) | (value << 16);
        static int NoteOn(int channel, int note, int velocity = 100) => 0x90 | channel | (note << 8) | (velocity << 16);

        // ------------------------------------------------------------------ sources

        [Test]
        public void DecodesTheKindsOfControl()
        {
            Assert.That(MapSource.TryDecode(Cc(2, 21, 99), out var kind, out int ch, out int number, out int value, out int max), Is.True);
            Assert.That((kind, ch, number, value, max), Is.EqualTo((MapKind.ControlChange, 2, 21, 99, 127)));
            Assert.That(MapSource.TryDecode(NoteOn(0, 36, 80), out kind, out _, out number, out value, out _), Is.True);
            Assert.That((kind, number, value), Is.EqualTo((MapKind.Note, 36, 80)));
            Assert.That(MapSource.TryDecode(0x80 | (36 << 8), out kind, out _, out _, out value, out _), Is.True);
            Assert.That((kind, value), Is.EqualTo((MapKind.Note, 0)), "a note off is a note at 0");
            Assert.That(MapSource.TryDecode(0xE0 | (127 << 8) | (127 << 16), out kind, out _, out number, out value, out max), Is.True);
            Assert.That((kind, number, value, max), Is.EqualTo((MapKind.PitchBend, 0, 16383, 16383)));
            Assert.That(MapSource.TryDecode(0xC0 | 3 | (7 << 8), out kind, out ch, out number, out _, out _), Is.True);
            Assert.That((kind, ch, number), Is.EqualTo((MapKind.ProgramChange, 3, 7)));
            Assert.That(MapSource.TryDecode(0xD0 | (50 << 8), out _, out _, out _, out _, out _), Is.False, "channel pressure is not a control");
            Assert.That(MapSource.TryDecode(0xF8, out _, out _, out _, out _, out _), Is.False, "clock is not a control");
        }

        [Test]
        public void NamesAControlAndItsDevice()
        {
            Assert.That(new MapSource(KeysA, MapKind.ControlChange, 0, 21).Short(), Is.EqualTo("CC 21 · Keys A"));
            Assert.That(new MapSource("", MapKind.Note, 9, 36).Short(), Is.EqualTo("Note 36 ch10"));
            Assert.That(new MapSource(PadsB, MapKind.PitchBend, 0, 0).ShortControl, Is.EqualTo("Bend"));
            Assert.That(new MapSource("", MapKind.ControlChange, 0, 1).AnyDevice, Is.True);
        }

        // ------------------------------------------------------------------ saved forms

        static MapDrive Cutoff(double min = 0, double max = 1, bool invert = false, Takeover t = Takeover.Pickup) =>
            new MapDrive { Target = MapTarget.Parameter("Synth", "Cutoff"), Min = min, Max = max, Invert = invert, Takeover = t };

        [Test]
        public void ASongMapKeepsPagesSlotsAndEntriesThroughItsSavedForm()
        {
            var map = new SongMap();
            var bass = map.Ensure("track:Bass");
            bass.Pages[0].Slots[0] = Cutoff(0.1, 0.8, true, Takeover.Scale);
            bass.Pages[0].Slots[3] = new MapDrive { Target = MapTarget.Parameter("Drive", "T:Amount", 2) };
            bass.Pages[0].Direct.Add(new MapEntry { Source = new MapSource(PadsB, MapKind.Note, 9, 40), Target = MapTarget.Command(MapCommand.SelectTrack, 3) });
            bass.Pages.Add(new TrackPage { Name = "Mix" });
            bass.Current = 1;
            map.Global = new List<MapEntry> { new MapEntry { Source = new MapSource("", MapKind.ControlChange, 0, 7), Encoding = KnobEncoding.Offset64, Target = MapTarget.Parameter("Master", "Vol") } };

            var again = SongMap.FromXml(System.Xml.Linq.XElement.Parse(map.ToXml().ToString()), out bool newer);
            Assert.That(newer, Is.False);
            Assert.That(again.ToXml().ToString(), Is.EqualTo(map.ToXml().ToString()));
            var slot = again.Find("track:Bass")!.Pages[0].Slots[0];
            Assert.That((slot.Min, slot.Max, slot.Invert, slot.Takeover), Is.EqualTo((0.1, 0.8, true, Takeover.Scale)));
            Assert.That(again.Find("track:Bass")!.Pages[0].Slots[3].Target!.IsTrackParam, Is.True);
            Assert.That(again.Find("track:Bass")!.Current, Is.EqualTo(1));
            Assert.That(again.Global![0].Encoding, Is.EqualTo(KnobEncoding.Offset64));
            Assert.That(again.Find("track:Bass")!.Pages[0].Direct[0].Target!.Cmd, Is.EqualTo(MapCommand.SelectTrack));
        }

        [Test]
        public void ASongMapFromANewerBuildIsLeftUnread()
        {
            var root = System.Xml.Linq.XElement.Parse("<MidiMap version=\"99\"><Track id=\"track:X\"/></MidiMap>");
            var map = SongMap.FromXml(root, out bool newer);
            Assert.That(newer, Is.True);
            Assert.That(map.IsEmpty, Is.True);
        }

        [Test]
        public void EntriesThatCannotBeUnderstoodAreSkipped()
        {
            var root = System.Xml.Linq.XElement.Parse(
                "<MidiMap version=\"1\"><Global>" +
                "<Map device=\"\" kind=\"cc\" channel=\"1\" number=\"7\" machine=\"M\" param=\"P\"/>" +
                "<Map device=\"\" kind=\"cc\" channel=\"99\" number=\"7\" machine=\"M\" param=\"P\"/>" +
                "<Map device=\"\" kind=\"wheel\" channel=\"1\" number=\"7\" machine=\"M\" param=\"P\"/>" +
                "<Map device=\"\" kind=\"cc\" channel=\"1\" number=\"8\"/>" +
                "</Global></MidiMap>");
            Assert.That(SongMap.FromXml(root, out _).Global!.Count, Is.EqualTo(1));
        }

        [Test]
        public void TheComputersMapKeepsGlobalEntriesKnobGroupsAndRoles()
        {
            var c = new ComputerMap();
            c.Global.Add(new MapEntry { Source = new MapSource(PadsB, MapKind.Note, 0, 36), Target = MapTarget.Command(MapCommand.Play) });
            var g = new KnobGroup { Device = KeysA };
            for (int i = 0; i < 8; i++) { g.Knobs.Add(new MapSource(KeysA, MapKind.ControlChange, 0, 70 + i)); g.Encodings.Add(i == 7 ? KnobEncoding.TwosComplement : KnobEncoding.Absolute); }
            c.Groups.Add(g);
            c.NoKeys.Add(PadsB);
            var again = ComputerMap.Parse(c.ToXml().ToString());
            Assert.That(again.ToXml().ToString(), Is.EqualTo(c.ToXml().ToString()));
            Assert.That(again.GroupOf("keys a")!.Count, Is.EqualTo(8), "a device is found without regard to case");
            Assert.That(again.GroupOf(KeysA)!.IndexOf(new MapSource("", MapKind.ControlChange, 0, 73)), Is.EqualTo(3));
            Assert.That(again.GroupOf(KeysA)!.Encodings[7], Is.EqualTo(KnobEncoding.TwosComplement));
            Assert.That(again.NoKeys.Contains("pads b"), Is.True);
            Assert.That(ComputerMap.Parse("not xml").Global, Is.Empty);
        }

        // ------------------------------------------------------------------ range, invert

        [Test]
        public void RangeAndInvertShapeTheValue()
        {
            Assert.That(MapMath.ToValue(0, 0, 100, 0, 1, false), Is.EqualTo(0));
            Assert.That(MapMath.ToValue(1, 0, 100, 0, 1, false), Is.EqualTo(100));
            Assert.That(MapMath.ToValue(0.5, 0, 100, 0.2, 0.6, false), Is.EqualTo(40), "half way through 20 to 60 %");
            Assert.That(MapMath.ToValue(0, 0, 100, 0.2, 0.6, false), Is.EqualTo(20));
            Assert.That(MapMath.ToValue(1, 0, 100, 0.2, 0.6, false), Is.EqualTo(60));
            Assert.That(MapMath.ToValue(0, 0, 100, 0, 1, true), Is.EqualTo(100), "inverted: the control's low end is the parameter's top");
            Assert.That(MapMath.ToValue(0.25, 10, 20, 0, 1, true), Is.EqualTo(18));
            Assert.That(MapMath.ToValue(0.5, 0, 1, 0, 1, false), Is.EqualTo(1), "a switch: half and over is on");
            // and back
            Assert.That(MapMath.ToPosition(40, 0, 100, 0.2, 0.6, false), Is.EqualTo(0.5).Within(1e-9));
            Assert.That(MapMath.ToPosition(100, 0, 100, 0.2, 0.6, false), Is.EqualTo(1), "a value above the range is the range's top");
            Assert.That(MapMath.ToPosition(80, 0, 100, 0, 1, true), Is.EqualTo(0.2).Within(1e-9));
        }

        // ------------------------------------------------------------------ takeover

        [Test]
        public void JumpGoesStraightToTheControl()
        {
            var s = new TakeoverState();
            Assert.That(MapMath.Absolute(ref s, Takeover.Jump, 0.9, 0.1, false, out double t), Is.True);
            Assert.That(t, Is.EqualTo(0.9));
        }

        [Test]
        public void PickupWaitsUntilTheControlPassesTheParameter()
        {
            var s = new TakeoverState();
            double param = 0.5;
            Assert.That(MapMath.Absolute(ref s, Takeover.Pickup, 0.1, param, false, out _), Is.False, "far below: nothing moves");
            Assert.That(MapMath.Absolute(ref s, Takeover.Pickup, 0.3, param, false, out _), Is.False);
            Assert.That(MapMath.Absolute(ref s, Takeover.Pickup, 0.7, param, false, out double t), Is.True, "crossed 0.5 between messages: picked up");
            Assert.That(t, Is.EqualTo(0.7));
            Assert.That(MapMath.Absolute(ref s, Takeover.Pickup, 0.2, 0.7, false, out t), Is.True, "after the pickup it follows, down too");
            Assert.That(t, Is.EqualTo(0.2));
            s.Reset();
            Assert.That(MapMath.Absolute(ref s, Takeover.Pickup, 0.499, 0.5, false, out _), Is.True, "close enough on the first message takes it");
        }

        [Test]
        public void ScaleStartsWhereTheParameterIsAndMeetsAtTheEnds()
        {
            var s = new TakeoverState();
            double param = 0.8;
            Assert.That(MapMath.Absolute(ref s, Takeover.Scale, 0.2, param, false, out double t), Is.False, "the first move only notes where both are");
            param = t;
            Assert.That(MapMath.Absolute(ref s, Takeover.Scale, 0.2, param, false, out t), Is.True);
            Assert.That(t, Is.EqualTo(0.8).Within(1e-9), "no jump");
            param = t;
            Assert.That(MapMath.Absolute(ref s, Takeover.Scale, 1.0, param, false, out t), Is.True);
            Assert.That(t, Is.EqualTo(1.0).Within(1e-9), "the control's top is the parameter's top");
            param = t;
            Assert.That(MapMath.Absolute(ref s, Takeover.Scale, 0.0, param, false, out t), Is.True);
            Assert.That(t, Is.EqualTo(0.0).Within(1e-9), "and its bottom the parameter's bottom");
            param = t;
            Assert.That(MapMath.Absolute(ref s, Takeover.Scale, 0.1, param, false, out t), Is.True);
            Assert.That(t, Is.EqualTo(0.4).Within(1e-9), "below the start, in proportion");
        }

        [Test]
        public void ScaleStartsAgainWhenTheParameterMovedElsewhere()
        {
            var s = new TakeoverState();
            MapMath.Absolute(ref s, Takeover.Scale, 0.5, 0.5, false, out _);
            MapMath.Absolute(ref s, Takeover.Scale, 0.6, 0.5, false, out double t);
            Assert.That(t, Is.GreaterThan(0.5));
            Assert.That(MapMath.Absolute(ref s, Takeover.Scale, 0.7, 0.1, false, out _), Is.False, "someone set the parameter to 0.1: the scale begins again from there");
        }

        [Test]
        public void ASwitchIgnoresTakeover()
        {
            var s = new TakeoverState();
            Assert.That(MapMath.Absolute(ref s, Takeover.Pickup, 1.0, 0.0, true, out double t), Is.True);
            Assert.That(t, Is.EqualTo(1.0));
            Assert.That(MapMath.IsDiscrete(0, 1), Is.True);
            Assert.That(MapMath.IsDiscrete(0, 2), Is.False);
        }

        // ------------------------------------------------------------------ relative encoders

        [Test]
        public void RelativeEncodersTurnIntoSteps()
        {
            Assert.That(MapMath.Step(KnobEncoding.Offset64, 66), Is.EqualTo(2));
            Assert.That(MapMath.Step(KnobEncoding.Offset64, 62), Is.EqualTo(-2));
            Assert.That(MapMath.Step(KnobEncoding.TwosComplement, 1), Is.EqualTo(1));
            Assert.That(MapMath.Step(KnobEncoding.TwosComplement, 127), Is.EqualTo(-1));
            Assert.That(MapMath.Step(KnobEncoding.TwosComplement, 120), Is.EqualTo(-8));
            Assert.That(MapMath.Step(KnobEncoding.Absolute, 50), Is.EqualTo(0));
        }

        [Test]
        public void ARelativeControlMovesAWideParameterInFractionsAndANarrowOneInWholeValues()
        {
            var s = new TakeoverState();
            int moved = 0;
            for (int i = 0; i < 127; i++) moved += MapMath.Relative(ref s, KnobEncoding.TwosComplement, 1, 0, 1000, 0, 1, false);
            Assert.That(moved, Is.InRange(998, 1000), "127 steps cross a wide parameter, with the remainder carried");
            s.Reset();
            Assert.That(MapMath.Relative(ref s, KnobEncoding.TwosComplement, 1, 0, 3, 0, 1, false), Is.EqualTo(1), "a short list moves a value a step");
            Assert.That(MapMath.Relative(ref s, KnobEncoding.TwosComplement, 127, 0, 3, 0, 1, false), Is.EqualTo(-1));
            Assert.That(MapMath.Relative(ref s, KnobEncoding.Offset64, 65, 0, 3, 0, 1, true), Is.EqualTo(-1), "inverted");
            Assert.That(MapMath.Relative(ref s, KnobEncoding.Offset64, 64, 0, 3, 0, 1, false), Is.EqualTo(0));
        }

        [Test]
        public void AnEncoderIsToldFromAKnobByWhatItSends()
        {
            var d = new EncoderDetector();
            foreach (int v in new[] { 1, 1, 2, 1 }) d.Observe(v);
            Assert.That(d.Encoding, Is.EqualTo(KnobEncoding.TwosComplement));
            d = new EncoderDetector();
            foreach (int v in new[] { 127, 126, 127 }) d.Observe(v);
            Assert.That(d.Encoding, Is.EqualTo(KnobEncoding.TwosComplement), "steps down");
            d = new EncoderDetector();
            foreach (int v in new[] { 65, 66, 65, 63 }) d.Observe(v);
            Assert.That(d.Encoding, Is.EqualTo(KnobEncoding.Offset64));
            d = new EncoderDetector();
            foreach (int v in new[] { 0, 3, 17, 40, 90, 127 }) d.Observe(v);
            Assert.That(d.Encoding, Is.EqualTo(KnobEncoding.Absolute), "a knob swept through the range");
            d = new EncoderDetector();
            foreach (int v in new[] { 1, 2, 5, 31 }) d.Observe(v);
            Assert.That(d.Encoding, Is.EqualTo(KnobEncoding.Absolute), "a knob that started at the bottom is not an encoder");
            Assert.That(MapMath.Guess(1), Is.EqualTo(KnobEncoding.TwosComplement));
            Assert.That(MapMath.Guess(65), Is.EqualTo(KnobEncoding.Offset64));
            Assert.That(MapMath.Guess(90), Is.EqualTo(KnobEncoding.Absolute));
        }
    }
}
