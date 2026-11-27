// Fork (docs/specs/midi-mapping.md, MM2): the controller profiles as data: the two built-in files against the makers' published protocols
// (restated here, number by number, from the documents cited in the spec), how Auto picks a profile for a port, and what a bad file says.

using ReBuzz.Midi.Mapping;

namespace ReBuzz.SessionTests
{
    [TestFixture]
    public class ProfileTests
    {
        static string Text(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Profiles", file));

        static ControllerProfile Grid => ControllerProfile.Parse(Text("clip-grid-8x5.json"));
        static ControllerProfile Keys => ControllerProfile.Parse(Text("keys-knobs-pads-25.json"));

        static ProfileCatalog Catalog => new ProfileCatalog(new[] { Text("clip-grid-8x5.json"), Text("keys-knobs-pads-25.json") });

        [Test]
        public void TheBuiltInProfilesAreGoodFilesWithDescriptiveNames()
        {
            foreach (var p in new[] { Grid, Keys })
            {
                Assert.That(p.Problems, Is.Empty, p.Id);
                Assert.That(p.Id.Length, Is.GreaterThan(0));
                Assert.That(p.Name + p.Description, Does.Not.Match("(?i)akai|apc|mpk|novation|ableton"), "no brands or model names in what people read");
            }
        }

        [Test]
        public void AutoMatchesEachProfileToItsPortNameAndOnlyThat()
        {
            var catalog = Catalog;
            Assert.That(catalog.Auto("APC40 mkII")?.Id, Is.EqualTo("clip-grid-8x5"));
            Assert.That(catalog.Auto("APC40 mkII #2")?.Id, Is.EqualTo("clip-grid-8x5"), "a second one of the same name");
            Assert.That(catalog.Auto("apc40 mkii")?.Id, Is.EqualTo("clip-grid-8x5"), "case does not matter");
            Assert.That(catalog.Auto("MPK225")?.Id, Is.EqualTo("keys-knobs-pads-25"));
            Assert.That(catalog.Auto("MPK225 Port A")?.Id, Is.EqualTo("keys-knobs-pads-25"));
            Assert.That(catalog.Auto("Faders"), Is.Null);
            Assert.That(catalog.Auto("APC mini mk2"), Is.Null, "another controller of the same family is not this one");
            Assert.That(catalog.Auto(""), Is.Null);
            Assert.That(catalog.Find("keys-knobs-pads-25"), Is.Not.Null);
            Assert.That(catalog.Find("nothing"), Is.Null);
        }

        [Test]
        public void TheClipGridProfileSendsTheMakersModeSwitchAndUsesTheirNumbers()
        {
            var p = Grid;
            // Introduction message, Communications Protocol v1.2 p.12: F0 47 <device id> 29 60 00 04 <mode> <version high, low, bugfix> F7; mode 0x41 is Ableton Live mode, 0x40 Generic.
            Assert.That(p.Enter.Count, Is.EqualTo(1));
            var enter = p.Enter[0];
            Assert.That(enter.Length, Is.EqualTo(12));
            Assert.That((enter[0], enter[1], enter[3], enter[4], enter[5], enter[6], enter[7], enter[^1]), Is.EqualTo(((byte)0xF0, (byte)0x47, (byte)0x29, (byte)0x60, (byte)0x00, (byte)0x04, (byte)0x41, (byte)0xF7)));
            Assert.That(p.Exit[0][7], Is.EqualTo(0x40), "back to Generic mode");

            ProfileControl One(string action, int index = 0) => p.Controls.Single(c => c.Action == action && c.Index == index);
            for (int i = 1; i <= 8; i++)
            {
                Assert.That((One("volume", i).Kind, One("volume", i).Channel, One("volume", i).Number), Is.EqualTo((MapKind.ControlChange, i - 1, 7)), "track fader: CC 0x07, channel = track (p.34)");
                Assert.That((One("knob", i).Kind, One("knob", i).Number), Is.EqualTo((MapKind.ControlChange, 0x10 + i - 1)), "device knobs: CC 0x10 to 0x17 (p.34)");
                Assert.That(One("knob", i).Led.TypeNumber, Is.EqualTo(0x18 + i - 1), "their ring type: CC 0x18 to 0x1F (p.24)");
                Assert.That(One("trackknob", i).Number, Is.EqualTo(0x30 + i - 1), "track knobs: CC 0x30 to 0x37 (p.35)");
                Assert.That(One("trackknob", i).Led.TypeNumber, Is.EqualTo(0x38 + i - 1), "their ring type: CC 0x38 to 0x3F (p.25)");
                foreach (var (action, note) in new[] { ("arm", 0x30), ("solo", 0x31), ("mute", 0x32), ("select", 0x33), ("clipstop", 0x34) })
                    Assert.That((One(action, i).Kind, One(action, i).Channel, One(action, i).Number), Is.EqualTo((MapKind.Note, i - 1, note)), action + ": a note on the track's channel (p.30)");
            }
            Assert.That(One("master").Number, Is.EqualTo(0x0E), "master fader: CC 0x0E");
            Assert.That(p.Controls.Where(c => c.Action == "scene").Select(c => c.Number), Is.EqualTo(new[] { 0x52, 0x53, 0x54, 0x55, 0x56 }), "scene launch: notes 0x52 to 0x56");
            Assert.That((One("play").Number, One("stop").Number, One("record").Number), Is.EqualTo((0x5B, 0x5C, 0x5D)));
            Assert.That((One("sceneup").Number, One("scenedown").Number, One("bankright").Number, One("bankleft").Number), Is.EqualTo((0x5E, 0x5F, 0x60, 0x61)), "up, down, right, left");
            Assert.That((One("bankleft8").Number, One("bankright8").Number), Is.EqualTo((0x3C, 0x3D)), "bank left and right");
            Assert.That((One("knobmode.pan").Number, One("knobmode.send").Number), Is.EqualTo((0x57, 0x58)), "pan and sends");
            Assert.That(One("stopall").Number, Is.EqualTo(0x51));
            Assert.That(p.Controls.All(c => c.Kind == MapKind.Note || c.Action is "volume" or "master" or "knob" or "trackknob"));

            // The 5 x 8 grid: notes 0x00 to 0x27, the bottom row first (p.13).
            var g = p.Grid;
            Assert.That((g.Cols, g.Rows, g.Kind), Is.EqualTo((8, 5, MapKind.Note)));
            Assert.That((g.NumberOf(0, 4), g.NumberOf(7, 4), g.NumberOf(0, 0), g.NumberOf(7, 0)), Is.EqualTo((0, 7, 32, 39)));
            Assert.That(Enumerable.Range(0, 5).SelectMany(r => Enumerable.Range(0, 8).Select(c => g.NumberOf(c, r))).Distinct().Count(), Is.EqualTo(40));
            // LEDs: the clip pads' colour is the velocity, the channel is its behaviour: 0 steady, 6 to 10 pulsing, 11 to 15 blinking (p.18).
            Assert.That(g.Look("playing").Channel, Is.InRange(6, 10), "pulses");
            Assert.That(g.Look("queued").Channel, Is.InRange(11, 15), "blinks");
            Assert.That(g.Look("stopped").Channel, Is.EqualTo(0));
            // Every clip state has its look: the eight states the lights know.
            foreach (var state in new[] { "empty", "armedEmpty", "stopped", "queued", "playing", "stopping", "recording", "recordQueued" })
                Assert.That(g.States.ContainsKey(state), Is.True, state);
            Assert.That(new[] { "empty", "armedEmpty", "stopped", "queued", "playing", "stopping", "recording", "recordQueued" }.Select(x => (g.Look(x).Velocity, g.Look(x).Channel, g.Look(x).BaseVelocity)).Distinct().Count(), Is.EqualTo(8), "each state looks different");
            Assert.That(One("trackknob", 1).Led.TypeValue, Is.EqualTo(2), "ring type 2 is a volume bar");
            Assert.That(One("trackknob", 1).Led.TypePanValue, Is.EqualTo(3), "ring type 3 is a pan");
        }

        [Test]
        public void TheKeyboardProfileTakesItsTransportFromTheMakersControllerNumbers()
        {
            var p = Keys;
            Assert.That(p.Role, Is.EqualTo(DeviceRole.Both));
            Assert.That(p.Controls.Where(c => c.Action == "stop").Select(c => c.Number).Distinct(), Is.EqualTo(new[] { 117 }));
            Assert.That(p.Controls.Where(c => c.Action == "play").Select(c => c.Number).Distinct(), Is.EqualTo(new[] { 118 }));
            Assert.That(p.Controls.Where(c => c.Action == "record").Select(c => c.Number).Distinct(), Is.EqualTo(new[] { 119 }));
            Assert.That(p.Controls.Count(c => c.Action == "play"), Is.EqualTo(16), "on every channel");
            Assert.That(p.Controls.Any(c => c.Action == "knob"), Is.False, "its knobs are taught: their numbers are a preset's, not the maker's published ones");
        }

        [Test]
        public void ABadProfileSaysWhatIsWrongAndNeverThrows()
        {
            Assert.That(ControllerProfile.Parse("{ nope").Problems, Is.Not.Empty);
            Assert.That(ControllerProfile.Parse("[]").Problems, Is.Not.Empty);
            var p = ControllerProfile.Parse("""{ "id": "x", "name": "X", "controls": [ { "id": "a", "action": "play", "kind": "cc", "number": 5 }, { "id": "b", "action": "stop", "kind": "cc", "number": 5 }, { "id": "c", "action": "record", "kind": "cc", "number": 200 } ], "enter": ["F0 99 F7"] }""");
            Assert.That(p.Problems.Count, Is.EqualTo(3), string.Join("; ", p.Problems));
            Assert.That(p.Problems.Any(x => x.Contains("same control")), Is.True);
            Assert.That(p.Problems.Any(x => x.Contains("out of range")), Is.True);
            Assert.That(p.Problems.Any(x => x.Contains("system exclusive")), Is.True);
            Assert.That(new ProfileCatalog(new[] { """{ "id": "x", "name": "X", "match": ["Port"], "controls": [ { "id": "a", "action": "play", "kind": "cc", "number": 5 }, { "id": "b", "action": "stop", "kind": "cc", "number": 5 } ] }""" }).Auto("Port"),
                Is.Null, "a profile with problems is not picked by Auto");
        }

        [Test]
        public void ADeviceSettingSavesOnlyWhatIsNotDefault()
        {
            var c = new ComputerMap();
            c.Devices.Add(new DeviceSetting { Device = "Plain" });
            c.Devices.Add(new DeviceSetting { Device = "Set", Role = DeviceRole.Keys, Profile = "none", Output = "Set out", KnobGroup = 12, Takeover = Takeover.Scale });
            var back = ComputerMap.Parse(c.ToXml().ToString());
            Assert.That(back.SettingOf("Plain"), Is.Null, "defaults are not stored");
            var s = back.SettingOf("set")!;
            Assert.That((s.Role, s.Profile, s.Output, s.KnobGroup, s.Takeover), Is.EqualTo(((DeviceRole?)DeviceRole.Keys, "none", "Set out", 12, Takeover.Scale)));
        }
    }
}
