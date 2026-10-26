using ReBuzz.Core.Session;

namespace ReBuzz.SessionTests
{
    [TestFixture]
    public class SessionMidiMapTests
    {
        static int NoteOn(int channel, int note, int velocity = 100) => 0x90 | channel | (note << 8) | (velocity << 16);
        static int NoteOff(int channel, int note) => 0x80 | channel | (note << 8);
        static int Cc(int channel, int number, int value) => 0xB0 | channel | (number << 8) | (value << 16);

        static MidiControl Note(int note, int channel = 0) => new MidiControl(MidiControlType.Note, channel, note);
        static MidiControl Controller(int number, int channel = 0) => new MidiControl(MidiControlType.ControlChange, channel, number);

        // ------------------------------------------------------------------ decoding

        [Test]
        public void DecodesNotesAndControllers()
        {
            Assert.That(MidiControl.TryDecode(NoteOn(2, 60, 90), out var c, out var v, out var press), Is.True);
            Assert.That(c, Is.EqualTo(Note(60, 2)));
            Assert.That(v, Is.EqualTo(90));
            Assert.That(press, Is.True);

            Assert.That(MidiControl.TryDecode(NoteOn(0, 60, 0), out c, out _, out press), Is.True);
            Assert.That(c, Is.EqualTo(Note(60)));
            Assert.That(press, Is.False, "note on with velocity 0 is a release");

            Assert.That(MidiControl.TryDecode(NoteOff(0, 61), out c, out _, out press), Is.True);
            Assert.That(c, Is.EqualTo(Note(61)));
            Assert.That(press, Is.False);

            Assert.That(MidiControl.TryDecode(Cc(15, 7, 127), out c, out v, out press), Is.True);
            Assert.That(c, Is.EqualTo(Controller(7, 15)));
            Assert.That(v, Is.EqualTo(127));
            Assert.That(press, Is.True);
        }

        [TestCase(0xE0 | (0x40 << 16))]   // pitch bend
        [TestCase(0xF8)]                    // clock
        [TestCase(0xC0 | (5 << 8))]         // program change
        public void IgnoresOtherMessages(int data)
        {
            Assert.That(MidiControl.TryDecode(data, out _, out _, out _), Is.False);
        }

        [Test]
        public void DescribesControls()
        {
            Assert.That(Note(60).ToString(), Is.EqualTo("Note C4, ch 1"));
            Assert.That(Note(37, 9).ToString(), Is.EqualTo("Note C#2, ch 10"));
            Assert.That(Controller(20).ToString(), Is.EqualTo("CC 20, ch 1"));
        }

        // ------------------------------------------------------------------ bindings

        [Test]
        public void ResolvesBoundControls()
        {
            var map = new SessionMidiMap();
            map.Bind(Note(36), SessionTarget.Slot, 2, 3);
            map.Bind(Controller(20), SessionTarget.StopAll, 5, 5);

            Assert.That(map.TryResolve(Note(36), out var target, out var track, out var scene), Is.True);
            Assert.That((target, track, scene), Is.EqualTo((SessionTarget.Slot, 2, 3)));

            Assert.That(map.TryResolve(Controller(20), out target, out track, out scene), Is.True);
            Assert.That((target, track, scene), Is.EqualTo((SessionTarget.StopAll, 0, 0)), "positions that don't apply are dropped");

            Assert.That(map.TryResolve(Note(36, 1), out _, out _, out _), Is.False, "other channel");
            Assert.That(map.Handles(Note(37)), Is.False);
        }

        [Test]
        public void AControlDrivesOneTargetAndATargetHasOneControl()
        {
            var map = new SessionMidiMap();
            map.Bind(Note(36), SessionTarget.Slot, 0, 0);
            map.Bind(Note(36), SessionTarget.Scene, 0, 1);       // same control, new target
            Assert.That(map.Bindings.Count, Is.EqualTo(1));
            Assert.That(map.Bindings[0].Target, Is.EqualTo(SessionTarget.Scene));

            map.Bind(Note(40), SessionTarget.Scene, 0, 1);       // same target, new control
            Assert.That(map.Bindings.Count, Is.EqualTo(1));
            Assert.That(map.Bindings[0].Control, Is.EqualTo(Note(40)));
        }

        [Test]
        public void UnbindAndFindByTarget()
        {
            var map = new SessionMidiMap();
            map.Bind(Note(36), SessionTarget.TrackStop, 3, 9);

            Assert.That(map.Find(SessionTarget.TrackStop, 3, 0)?.Control, Is.EqualTo(Note(36)), "scene doesn't matter for a track stop");
            Assert.That(map.Find(SessionTarget.TrackStop, 2, 0), Is.Null);
            Assert.That(map.Unbind(SessionTarget.TrackStop, 3, 4), Is.True);
            Assert.That(map.IsEmpty, Is.True);
        }

        // ------------------------------------------------------------------ note grid

        [Test]
        public void NoteGridBottomUpMapsTheFirstNoteToTheBottomLeftPad()
        {
            var map = new SessionMidiMap { Grid = new NoteGrid { FirstNote = 36, Columns = 4, Rows = 4, BottomUp = true, OriginTrack = 1, OriginScene = 2 } };

            Assert.That(Slot(map, 36), Is.EqualTo((1, 5)));   // bottom-left pad -> last row of the block
            Assert.That(Slot(map, 39), Is.EqualTo((4, 5)));
            Assert.That(Slot(map, 40), Is.EqualTo((1, 4)));
            Assert.That(Slot(map, 51), Is.EqualTo((4, 2)));   // top-right pad -> origin row
            Assert.That(map.Handles(Note(35)), Is.False);
            Assert.That(map.Handles(Note(52)), Is.False);
        }

        [Test]
        public void NoteGridTopDownAndChannelFilter()
        {
            var map = new SessionMidiMap { Grid = new NoteGrid { Channel = 9, FirstNote = 0, Columns = 8, Rows = 2, BottomUp = false } };

            Assert.That(Slot(map, 0, 9), Is.EqualTo((0, 0)));
            Assert.That(Slot(map, 9, 9), Is.EqualTo((1, 1)));
            Assert.That(map.Handles(Note(0, 0)), Is.False, "wrong channel");
            Assert.That(map.Handles(Controller(0, 9)), Is.False, "grids map notes only");
        }

        [Test]
        public void ExplicitBindingsWinOverTheGrid()
        {
            var map = new SessionMidiMap { Grid = new NoteGrid { FirstNote = 36 } };
            map.Bind(Note(36), SessionTarget.StopAll, 0, 0);

            Assert.That(map.TryResolve(Note(36), out var target, out _, out _), Is.True);
            Assert.That(target, Is.EqualTo(SessionTarget.StopAll));
        }

        static (int, int) Slot(SessionMidiMap map, int note, int channel = 0)
        {
            Assert.That(map.TryResolve(Note(note, channel), out var target, out var track, out var scene), Is.True, $"note {note}");
            Assert.That(target, Is.EqualTo(SessionTarget.Slot));
            return (track, scene);
        }

        // ------------------------------------------------------------------ persistence

        [Test]
        public void XmlRoundTrip()
        {
            var map = new SessionMidiMap { Grid = new NoteGrid { Channel = 3, FirstNote = 48, Columns = 8, Rows = 2, BottomUp = false, OriginTrack = 1, OriginScene = 4 } };
            map.Bind(Note(60, 1), SessionTarget.Scene, 0, 2);
            map.Bind(Controller(21), SessionTarget.Quantization, 0, 0);
            map.Bind(Controller(22), SessionTarget.Transport, 0, 0);

            var copy = SessionMidiMap.Parse(map.ToXml().ToString());

            Assert.That(copy.Bindings, Is.EqualTo(map.Bindings));
            Assert.That(copy.Grid!.Channel, Is.EqualTo(3));
            Assert.That(copy.Grid.FirstNote, Is.EqualTo(48));
            Assert.That(copy.Grid.Columns, Is.EqualTo(8));
            Assert.That(copy.Grid.Rows, Is.EqualTo(2));
            Assert.That(copy.Grid.BottomUp, Is.False);
            Assert.That(copy.Grid.OriginTrack, Is.EqualTo(1));
            Assert.That(copy.Grid.OriginScene, Is.EqualTo(4));
        }

        [Test]
        public void AnyChannelGridSurvivesRoundTrip()
        {
            var map = new SessionMidiMap { Grid = new NoteGrid() };
            Assert.That(SessionMidiMap.Parse(map.ToXml().ToString()).Grid!.Channel, Is.EqualTo(-1));
        }

        [TestCase("")]
        [TestCase("garbage")]
        [TestCase("<Midi><Bind type=\"note\" channel=\"99\" number=\"1\" target=\"Slot\"/><Bind target=\"Nope\" number=\"1\"/></Midi>")]
        public void ParseSkipsWhatItDoesntUnderstand(string xml)
        {
            Assert.That(SessionMidiMap.Parse(xml).IsEmpty, Is.True);
        }

        [Test]
        public void CloneIsIndependent()
        {
            var map = new SessionMidiMap { Grid = new NoteGrid() };
            map.Bind(Note(1), SessionTarget.Slot, 0, 0);
            var copy = map.Clone();
            copy.Bind(Note(2), SessionTarget.Slot, 1, 1);
            copy.Grid!.FirstNote = 60;

            Assert.That(map.Bindings.Count, Is.EqualTo(1));
            Assert.That(map.Grid!.FirstNote, Is.EqualTo(36));
        }
    }
}
