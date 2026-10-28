using ReBuzz.Core.Session;
using System.Text;
using static ReBuzz.Core.Session.SessionDocument;

namespace ReBuzz.SessionTests
{
    [TestFixture]
    public class SessionDocumentTests
    {
        static SongTrack T(string machine, params string[] patterns) => new SongTrack(machine, patterns);

        static Dictionary<int, string> Slots(SessionDocument d, int track) => d.Tracks[track].Slots.ToDictionary(k => k.Key, v => v.Value);

        // ------------------------------------------------------------------ auto-fill

        [Test]
        public void AutoFillPutsEachMachinesPatternsDownTheScenes()
        {
            var d = AutoFill(new[] { T("drums", "00", "01", "02"), T("bass", "00") });

            Assert.That(d.Tracks.Select(t => t.Machine), Is.EqualTo(new[] { "drums", "bass" }));
            Assert.That(d.GetSlot(0, 2), Is.EqualTo("02"));
            Assert.That(d.GetSlot(1, 0), Is.EqualTo("00"));
            Assert.That(d.GetSlot(1, 1), Is.Null);
            Assert.That(d.Scenes.Count, Is.EqualTo(MinScenes));
        }

        [Test]
        public void AutoFillAddsScenesForLongPatternLists()
        {
            var many = Enumerable.Range(0, 12).Select(i => i.ToString("00")).ToArray();
            var d = AutoFill(new[] { T("drums", many) });
            Assert.That(d.Scenes.Count, Is.EqualTo(12));
        }

        [Test]
        public void AutoFillOfAnEmptySongStillHasScenes()
        {
            var d = AutoFill(Array.Empty<SongTrack>());
            Assert.That(d.Tracks.Count, Is.EqualTo(0));
            Assert.That(d.Scenes.Count, Is.EqualTo(MinScenes));
        }

        // ------------------------------------------------------------------ reconcile

        [Test]
        public void ReconcileFollowsMovedSequences()
        {
            var d = AutoFill(new[] { T("drums", "a"), T("bass", "b") });
            d.Reconcile(new[] { T("bass", "b"), T("drums", "a") });

            Assert.That(d.Tracks.Select(t => t.Machine), Is.EqualTo(new[] { "bass", "drums" }));
            Assert.That(d.GetSlot(0, 0), Is.EqualTo("b"));
            Assert.That(d.Tracks.Select(t => t.Sequence), Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void ReconcileDropsRemovedSequencesAndAddsNewOnesEmpty()
        {
            var d = AutoFill(new[] { T("drums", "a"), T("bass", "b") });
            d.Reconcile(new[] { T("bass", "b"), T("lead", "x", "y") });

            Assert.That(d.Tracks.Select(t => t.Machine), Is.EqualTo(new[] { "bass", "lead" }));
            Assert.That(d.GetSlot(0, 0), Is.EqualTo("b"));
            Assert.That(d.Tracks[1].Slots, Is.Empty);
        }

        [Test]
        public void ReconcileKeepsSlotsOfARenamedMachine()
        {
            var d = AutoFill(new[] { T("drums", "a", "b"), T("bass", "c") });
            d.Reconcile(new[] { T("beats", "a", "b"), T("bass", "c") });

            Assert.That(d.Tracks[0].Machine, Is.EqualTo("beats"));
            Assert.That(Slots(d, 0), Is.EqualTo(new Dictionary<int, string> { [0] = "a", [1] = "b" }));
        }

        [Test]
        public void ReconcileDropsSlotsOfDeletedPatterns()
        {
            var d = AutoFill(new[] { T("drums", "a", "b", "c") });
            d.Reconcile(new[] { T("drums", "a", "c") });

            Assert.That(Slots(d, 0), Is.EqualTo(new Dictionary<int, string> { [0] = "a", [2] = "c" }));
        }

        [Test]
        public void ReconcileMatchesTwoSequencesOfTheSameMachineByIndex()
        {
            var d = AutoFill(new[] { T("synth", "a", "b"), T("synth", "a", "b") });
            d.SetSlot(1, 5, "b");
            d.Reconcile(new[] { T("synth", "a", "b"), T("synth", "a", "b") });

            Assert.That(d.GetSlot(1, 5), Is.EqualTo("b"));
            Assert.That(d.GetSlot(0, 5), Is.Null);
        }

        // ------------------------------------------------------------------ editing

        [Test]
        public void SetSlotGrowsScenesAndNullClears()
        {
            var d = AutoFill(new[] { T("drums", "a") });
            d.SetSlot(0, 11, "a");
            Assert.That(d.Scenes.Count, Is.EqualTo(12));
            d.SetSlot(0, 11, null);
            Assert.That(d.GetSlot(0, 11), Is.Null);
        }

        [Test]
        public void InsertSceneMovesLaterRowsDown()
        {
            var d = AutoFill(new[] { T("drums", "a", "b", "c") });
            d.Scenes[1].Name = "verse";
            d.InsertScene(1);

            Assert.That(Slots(d, 0), Is.EqualTo(new Dictionary<int, string> { [0] = "a", [2] = "b", [3] = "c" }));
            Assert.That(d.Scenes[2].Name, Is.EqualTo("verse"));
            Assert.That(d.Scenes[1].Name, Is.EqualTo(""));
        }

        [Test]
        public void RemoveSceneMovesLaterRowsUp()
        {
            var d = AutoFill(new[] { T("drums", "a", "b", "c") });
            d.Scenes[2].Name = "chorus";
            d.RemoveScene(1);

            Assert.That(Slots(d, 0), Is.EqualTo(new Dictionary<int, string> { [0] = "a", [1] = "c" }));
            Assert.That(d.Scenes[1].Name, Is.EqualTo("chorus"));
            Assert.That(d.Scenes.Count, Is.EqualTo(MinScenes), "never fewer than the minimum");
        }

        [Test]
        public void RenamePatternOnlyTouchesThatMachine()
        {
            var d = AutoFill(new[] { T("drums", "00"), T("bass", "00") });
            d.RenamePattern("drums", "00", "kick");
            Assert.That(d.GetSlot(0, 0), Is.EqualTo("kick"));
            Assert.That(d.GetSlot(1, 0), Is.EqualTo("00"));
        }

        [Test]
        public void SceneSlotsListsFilledSlotsOfARow()
        {
            var d = AutoFill(new[] { T("drums", "a", "b"), T("bass", "c"), T("lead", "d", "e") });
            Assert.That(d.SceneSlots(1).ToList(), Is.EqualTo(new[] { (0, "b"), (2, "e") }));
        }

        // ------------------------------------------------------------------ 0.2 editing

        [Test]
        public void DuplicateSceneCopiesNameColourAndSlotsBelow()
        {
            var d = AutoFill(new[] { T("drums", "a", "b"), T("bass", "c") });
            d.Scenes[0].Name = "Intro";
            d.Scenes[0].ColorIndex = 4;

            d.DuplicateScene(0);

            Assert.That(d.Scenes[1].Name, Is.EqualTo("Intro"));
            Assert.That(d.Scenes[1].ColorIndex, Is.EqualTo(4));
            Assert.That(Slots(d, 0), Is.EqualTo(new Dictionary<int, string> { [0] = "a", [1] = "a", [2] = "b" }));
            Assert.That(Slots(d, 1), Is.EqualTo(new Dictionary<int, string> { [0] = "c", [1] = "c" }));
        }

        [Test]
        public void MoveSlotMovesOrCopiesWithinTheTrack()
        {
            var d = AutoFill(new[] { T("drums", "a", "b") });

            Assert.That(d.MoveSlot(0, 0, 5, copy: false), Is.True);
            Assert.That(Slots(d, 0), Is.EqualTo(new Dictionary<int, string> { [1] = "b", [5] = "a" }));

            Assert.That(d.MoveSlot(0, 1, 5, copy: true), Is.True);
            Assert.That(Slots(d, 0), Is.EqualTo(new Dictionary<int, string> { [1] = "b", [5] = "b" }));

            Assert.That(d.MoveSlot(0, 3, 4, copy: false), Is.False, "empty source");
        }

        [Test]
        public void MoveSlotBelowTheLastSceneAddsScenes()
        {
            var d = AutoFill(new[] { T("drums", "a") });
            d.MoveSlot(0, 0, 11, copy: false);
            Assert.That(d.Scenes.Count, Is.EqualTo(12));
        }

        // ------------------------------------------------------------------ 0.4 mixer

        [Test]
        public void MixerSettingsRoundTripAndFollowRenames()
        {
            var d = AutoFill(new[] { T("drums", "a") });
            Assert.That(d.ShowMixer, Is.True, "shown by default");
            d.ShowMixer = false;
            d.Returns.Add("Reverb");
            d.Returns.Add("Delay & Co");
            d.CueMachine = "Phones";
            d.CollapsedGroups.Add("Drums group");

            d.RenameMachine("Reverb", "Hall");
            d.RenameMachine("Phones", "Cue out");
            var copy = Load(new MemoryStream(d.ToBytes()))!;

            Assert.That(copy.ShowMixer, Is.False);
            Assert.That(copy.Returns, Is.EqualTo(new[] { "Hall", "Delay & Co" }));
            Assert.That(copy.CueMachine, Is.EqualTo("Cue out"));
            Assert.That(copy.CollapsedGroups.Single(), Is.EqualTo("Drums group"));
        }

        [Test]
        public void SongsWithoutMixerDataGetDefaults()
        {
            var d = Load(new MemoryStream(Encoding.UTF8.GetBytes("<Session><Tracks/></Session>")))!;
            Assert.That(d.ShowMixer, Is.True);
            Assert.That(d.Returns, Is.Empty);
            Assert.That(d.CueMachine, Is.EqualTo(""));
        }

        // ------------------------------------------------------------------ persistence

        [Test]
        public void SaveAndLoadRoundTrip()
        {
            var d = AutoFill(new[] { T("drums", "a", "b"), T("bass & co <1>", "c") });
            d.BeatsPerBar = 3;
            d.LaunchQuantization = "TwoBars";
            d.Scenes[0].Name = "Intro";
            d.Scenes[1].ColorIndex = 7;
            d.Tracks[0].ColorIndex = 3;
            d.ClipBars = 2;
            d.SetSlot(1, 9, "c");
            d.SongMidi = new SessionMidiMap { Grid = new NoteGrid { FirstNote = 48 } };
            d.SongMidi.Bind(new MidiControl(MidiControlType.ControlChange, 0, 20), SessionTarget.StopAll, 0, 0);

            var copy = Load(new MemoryStream(d.ToBytes()))!;

            Assert.That(copy.BeatsPerBar, Is.EqualTo(3));
            Assert.That(copy.LaunchQuantization, Is.EqualTo("TwoBars"));
            Assert.That(copy.Scenes.Count, Is.EqualTo(10));
            Assert.That(copy.Scenes[0].Name, Is.EqualTo("Intro"));
            Assert.That(copy.Scenes[1].ColorIndex, Is.EqualTo(7));
            Assert.That(copy.Scenes[0].ColorIndex, Is.EqualTo(-1));
            Assert.That(copy.Tracks[0].ColorIndex, Is.EqualTo(3));
            Assert.That(copy.Tracks[1].ColorIndex, Is.EqualTo(-1));
            Assert.That(copy.ClipBars, Is.EqualTo(2));
            Assert.That(copy.SongMidi!.Grid!.FirstNote, Is.EqualTo(48));
            Assert.That(copy.SongMidi.Bindings.Single().Target, Is.EqualTo(SessionTarget.StopAll));
            Assert.That(copy.Tracks[1].Machine, Is.EqualTo("bass & co <1>"));
            Assert.That(Slots(copy, 1), Is.EqualTo(new Dictionary<int, string> { [0] = "c", [9] = "c" }));
        }

        [Test]
        public void LoadIgnoresUnknownContentAndFillsDefaults()
        {
            const string xml = """
                <Session version="7" futureThing="x">
                  <Tracks>
                    <Track machine="drums"><Slot scene="2" pattern="a" mood="happy"/><Slot pattern="lost"/></Track>
                  </Tracks>
                  <Mixer/>
                </Session>
                """;
            var d = Load(new MemoryStream(Encoding.UTF8.GetBytes(xml)))!;

            Assert.That(d.BeatsPerBar, Is.EqualTo(4));
            Assert.That(d.LaunchQuantization, Is.EqualTo("Bar"));
            Assert.That(d.ClipBars, Is.EqualTo(1));
            Assert.That(d.SongMidi, Is.Null);
            Assert.That(d.Tracks[0].Sequence, Is.EqualTo(0));
            Assert.That(Slots(d, 0), Is.EqualTo(new Dictionary<int, string> { [2] = "a" }));
            Assert.That(d.Scenes.Count, Is.EqualTo(MinScenes));
        }

        [TestCase("")]
        [TestCase("not xml")]
        [TestCase("<Other/>")]
        public void LoadReturnsNullForSomethingElse(string text)
        {
            Assert.That(Load(new MemoryStream(Encoding.UTF8.GetBytes(text))), Is.Null);
        }
    }
}
