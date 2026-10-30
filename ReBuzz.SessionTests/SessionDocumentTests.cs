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

        // ------------------------------------------------------------------ 0.5 clip and scene properties

        [Test]
        public void ClipSettingsDefaultUntilSetAndRoundTrip()
        {
            var d = AutoFill(new[] { T("drums", "a", "b"), T("bass", "a") });
            Assert.That(d.GetClip("drums", "a").IsDefault, Is.True);

            var c = d.GetClip("drums", "a");
            c.Mode = "Gate";
            c.Quantization = "Sixteenth";
            c.Legato = true;
            c.Loop = false;
            c.Start = 4;
            c.LoopStart = 8;
            c.LoopEnd = 24;
            c.Velocity = 60;
            d.SetClip("drums", "a", c);
            c.Start = 99;                                         // stored as a copy
            Assert.That(d.GetClip("bass", "a").IsDefault, Is.True, "per machine");

            var copy = Load(new MemoryStream(d.ToBytes()))!;
            var back = copy.GetClip("drums", "a");
            Assert.That((back.Mode, back.Quantization, back.Legato, back.Loop), Is.EqualTo(("Gate", "Sixteenth", true, false)));
            Assert.That((back.Start, back.LoopStart, back.LoopEnd, back.Velocity), Is.EqualTo((4, 8, 24, 60)));

            copy.SetClip("drums", "a", new ClipSettings());
            Assert.That(copy.Clips, Is.Empty, "default settings are not stored");
        }

        [Test]
        public void ClipSettingsFollowRenamesAndDeletedPatterns()
        {
            var d = AutoFill(new[] { T("drums", "a", "b") });
            d.SetClip("drums", "a", new ClipSettings { Legato = true });
            d.SetClip("drums", "b", new ClipSettings { Velocity = 10 });
            d.SetClip("gone", "x", new ClipSettings { Velocity = 20 });

            d.RenamePattern("drums", "a", "intro");
            d.RenameMachine("drums", "beats");
            Assert.That(d.GetClip("beats", "intro").Legato, Is.True);
            Assert.That(d.GetClip("drums", "a").IsDefault, Is.True);

            d.Reconcile(new[] { T("beats", "intro") });           // "b" deleted
            Assert.That(d.GetClip("beats", "b").IsDefault, Is.True);
            Assert.That(d.GetClip("beats", "intro").Legato, Is.True);
            Assert.That(d.GetClip("gone", "x").Velocity, Is.EqualTo(20), "a machine without a track keeps its settings");
        }

        [Test]
        public void SceneTempoAndBeatsRoundTripAndDuplicate()
        {
            var d = AutoFill(new[] { T("drums", "a") });
            d.Scenes[0].Tempo = 140;
            d.Scenes[0].BeatsPerBar = 3;
            d.DuplicateScene(0);
            Assert.That((d.Scenes[1].Tempo, d.Scenes[1].BeatsPerBar), Is.EqualTo((140, 3)));

            var copy = Load(new MemoryStream(d.ToBytes()))!;
            Assert.That((copy.Scenes[0].Tempo, copy.Scenes[0].BeatsPerBar), Is.EqualTo((140, 3)));
            Assert.That((copy.Scenes[2].Tempo, copy.Scenes[2].BeatsPerBar), Is.EqualTo((0, 0)));
        }

        [Test]
        public void LoadClampsClipAndSceneValues()
        {
            const string xml = """
                <Session>
                  <Scenes><Scene tempo="9999" beatsPerBar="-3"/></Scenes>
                  <Tracks/>
                  <Clips><Clip machine="m" pattern="p" start="-5" velocity="400"/><Clip pattern="no machine"/></Clips>
                </Session>
                """;
            var d = Load(new MemoryStream(Encoding.UTF8.GetBytes(xml)))!;
            Assert.That((d.Scenes[0].Tempo, d.Scenes[0].BeatsPerBar), Is.EqualTo((500, 0)));
            Assert.That(d.Clips.Count, Is.EqualTo(1));
            Assert.That((d.GetClip("m", "p").Start, d.GetClip("m", "p").Velocity), Is.EqualTo((0, 100)));
        }

        // ------------------------------------------------------------------ 0.6 performance

        [Test]
        public void FollowActionsCrossfaderAndOptionsRoundTrip()
        {
            var d = AutoFill(new[] { T("drums", "a", "b"), T("bass", "c") });
            var c = d.GetClip("drums", "a");
            c.Follow.On = true;
            c.Follow.A = "Other";
            c.Follow.B = "Jump";
            c.Follow.ChanceA = 70;
            c.Follow.Linked = false;
            c.Follow.Multiplier = 3;
            c.Follow.Time = "2.1.0";
            c.Follow.JumpB = 4;
            d.SetClip("drums", "a", c);
            d.Scenes[1].Follow.On = true;
            d.Scenes[1].Follow.Time = "8";
            d.FollowActionsEnabled = false;
            d.ScenesStopOtherClips = true;
            d.Crossfade["drums"] = "A";
            d.Crossfade["bass"] = "B";
            d.CrossfaderPosition = 0.25;
            d.CrossfaderCurve = "Constant power";
            d.RenameMachine("bass", "sub");

            var copy = Load(new MemoryStream(d.ToBytes()))!;
            var f = copy.GetClip("drums", "a").Follow;
            Assert.That((f.On, f.A, f.B, f.ChanceA, f.Linked, f.Multiplier, f.Time, f.JumpA, f.JumpB),
                Is.EqualTo((true, "Other", "Jump", 70, false, 3, "2.1.0", -1, 4)));
            Assert.That((copy.Scenes[1].Follow.On, copy.Scenes[1].Follow.Time), Is.EqualTo((true, "8")));
            Assert.That(copy.Scenes[0].Follow.IsDefault, Is.True);
            Assert.That((copy.FollowActionsEnabled, copy.ScenesStopOtherClips), Is.EqualTo((false, true)));
            Assert.That(copy.Crossfade, Is.EqualTo(new Dictionary<string, string> { ["drums"] = "A", ["sub"] = "B" }));
            Assert.That((copy.CrossfaderPosition, copy.CrossfaderCurve), Is.EqualTo((0.25, "Constant power")));

            var clone = copy.GetClip("drums", "a");
            clone.Follow.ChanceA = 1;
            Assert.That(copy.GetClip("drums", "a").Follow.ChanceA, Is.EqualTo(70), "settings are copied, follow actions too");
        }

        [Test]
        public void FollowTimesAreBarsBeatsTicks()
        {
            Assert.That(FollowSettings.TicksOf("1.0.0", 4, 4), Is.EqualTo(16));
            Assert.That(FollowSettings.TicksOf("2.1.3", 3, 4), Is.EqualTo(2 * 12 + 4 + 3));
            Assert.That(FollowSettings.TicksOf("8", 4, 4), Is.EqualTo(128));
            Assert.That(FollowSettings.TicksOf("0.0.0", 4, 4), Is.Null);
            Assert.That(FollowSettings.TicksOf("1.x", 4, 4), Is.Null);
            Assert.That(FollowSettings.TicksOf("-1", 4, 4), Is.Null);
        }

        [Test]
        public void TracksFollowSequencesInsertedRemovedAndSwapped()
        {
            var d = AutoFill(new[] { T("synth", "a"), T("synth", "b"), T("drums", "c") });
            d.RemoveTrack(0);                                    // the first of the two synth sequences
            Assert.That(d.Tracks.Select(t => (t.Sequence, t.Machine, t.Slots[0])), Is.EqualTo(new[] { (0, "synth", "b"), (1, "drums", "c") }));
            d.InsertTrack(1, "bass");
            Assert.That(d.Tracks.Select(t => t.Machine), Is.EqualTo(new[] { "synth", "bass", "drums" }));
            Assert.That(d.Tracks[1].Slots, Is.Empty);
            d.SwapTracks(0, 2);
            Assert.That(d.Tracks.Select(t => (t.Sequence, t.Machine)), Is.EqualTo(new[] { (0, "drums"), (1, "bass"), (2, "synth") }));
        }

        [Test]
        public void RetiredNamesStayInCopiesButNeverInFiles()
        {
            var d = AutoFill(new[] { T("drums", "a", "b") });
            d.SetClip("drums", "a", new SessionDocument.ClipSettings { Mode = "Gate" });
            d.Crossfade["drums"] = "A";
            string gone = SessionDocument.Retire("a", 7);
            d.RenamePattern("drums", "a", gone);
            Assert.That(SessionDocument.IsRetired(gone), Is.True);
            Assert.That(SessionDocument.IsRetired("a"), Is.False);

            var copy = d.CloneLayout();
            Assert.That(copy.Tracks[0].Slots[0], Is.EqualTo(gone), "a snapshot keeps the retired name, to bring it back");
            Assert.That(copy.GetClip("drums", gone).Mode, Is.EqualTo("Gate"));
            Assert.That(ReferenceEquals(copy.Settings, d.Settings), Is.True, "snapshots share the settings");

            var file = new MemoryStream();
            d.Save(file);
            file.Position = 0;
            var loaded = SessionDocument.Load(file)!;
            Assert.That(loaded.Tracks[0].Slots.Values, Is.EqualTo(new[] { "b" }), "a file has no retired names");
            Assert.That(loaded.Clips.Count, Is.Zero);

            d.RenameMachine("drums", SessionDocument.Retire("drums", 8));
            file = new MemoryStream();
            d.Save(file);
            file.Position = 0;
            loaded = SessionDocument.Load(file)!;
            Assert.That(loaded.Crossfade.Count, Is.Zero, "nor retired machines");
        }

        [Test]
        public void ARetiredPatternKeepsItsPlaceButLooksEmpty()
        {
            var d = AutoFill(new[] { T("drums", "a", "b") });
            d.SetClip("drums", "a", new SessionDocument.ClipSettings { Mode = "Gate" });
            string gone = SessionDocument.Retire("a", 3);
            d.RenamePattern("drums", "a", gone);

            Assert.That(d.GetSlot(0, 0), Is.Null, "an empty slot to everyone");
            Assert.That(d.SceneSlots(0).Any(), Is.False);
            Assert.That(d.MoveSlot(0, 0, 5, copy: false), Is.False, "nothing to move");
            d.Reconcile(new[] { T("drums", "b") });             // "a" is gone from the song
            Assert.That(d.Tracks[0].Slots[0], Is.EqualTo(gone), "its place is kept for an undo");
            Assert.That(d.GetClip("drums", gone).Mode, Is.EqualTo("Gate"));

            d.InsertScene(0);                                    // edits move it like any slot
            d.RenamePattern("drums", gone, "a2");                // brought back under another name
            Assert.That(d.GetSlot(0, 1), Is.EqualTo("a2"));
            Assert.That(d.GetClip("drums", "a2").Mode, Is.EqualTo("Gate"));

            d.RenamePattern("drums", "a2", gone);
            d.PurgeRetired(gone);                                // forgotten for good
            Assert.That(d.Tracks[0].Slots.ContainsKey(1), Is.False);
            Assert.That(d.Clips.Count, Is.Zero);
        }

        [Test]
        public void ARemovedTrackComesBackOnlyIntoAnEmptyTrackOfItsMachine()
        {
            var d = AutoFill(new[] { T("synth", "a"), T("drums", "c") });
            d.Tracks[0].ColorIndex = 4;
            var removed = d.RemoveTrack(0)!.Clone();
            d.InsertTrack(0, "synth");
            Assert.That(d.PutTrack(0, removed), Is.True);
            Assert.That((d.Tracks[0].Slots[0], d.Tracks[0].ColorIndex, d.Tracks[0].Sequence), Is.EqualTo(("a", 4, 0)));
            Assert.That(d.PutTrack(1, removed), Is.False, "not into another machine's track");
            d.InsertTrack(0, "synth");
            d.Tracks[0].Slots[2] = "x";
            Assert.That(d.PutTrack(0, removed), Is.False, "nor over one with clips");
        }

        [Test]
        public void JumpTargetsFollowScenesInsertedAndRemoved()
        {
            var d = AutoFill(new[] { T("drums", "a", "b", "c", "d") });
            d.Scenes[0].Follow = new FollowSettings { On = true, A = "Jump", JumpA = 2, B = "Jump", JumpB = 3 };
            var clip = new SessionDocument.ClipSettings();
            clip.Follow = new FollowSettings { On = true, A = "Jump", JumpA = 1 };
            d.Clips[("drums", "a")] = clip;

            d.InsertScene(1);                                    // rows 1+ move down
            Assert.That((d.Scenes[0].Follow.JumpA, d.Scenes[0].Follow.JumpB, clip.Follow.JumpA), Is.EqualTo((3, 4, 2)));
            d.RemoveScene(3);                                    // the target of A goes; later rows move up
            Assert.That((d.Scenes[0].Follow.JumpA, d.Scenes[0].Follow.JumpB, clip.Follow.JumpA), Is.EqualTo((-1, 3, 2)));
            d.DuplicateScene(0);                                 // a copy below: rows 1+ move down, the copy jumps as the original
            Assert.That((d.Scenes[0].Follow.JumpB, d.Scenes[1].Follow.JumpB, clip.Follow.JumpA), Is.EqualTo((4, 4, 3)));
        }

        [Test]
        public void DamagedLayoutsLoadWithoutHarm()
        {
            string xml = "<Session version=\"1\"><Tracks><Track sequence=\"0\" machine=\"m\">" +
                         "<Slot scene=\"99999999\" pattern=\"a\" /><Slot scene=\"2\" pattern=\"b\" /></Track></Tracks>" +
                         "<Mixer><Crossfader position=\"NaN\" /></Mixer></Session>";
            var d = SessionDocument.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)));
            Assert.That(d != null, Is.True);
            Assert.That(d!.Scenes.Count, Is.LessThan(SessionDocument.MaxScenes + 1));
            Assert.That(d.Tracks[0].Slots.Keys, Is.EqualTo(new[] { 2 }));
            Assert.That(d.CrossfaderPosition, Is.EqualTo(0.5));
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
