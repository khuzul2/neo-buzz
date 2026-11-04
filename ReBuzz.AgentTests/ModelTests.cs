using System.Globalization;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using V = ReBuzz.Agent.Model.ValueResolver;

namespace ReBuzz.AgentTests
{
    /// <summary>Units, pitches and positions as agents write them (docs/specs/mcp-server.md §3.3).</summary>
    [TestFixture]
    public class ModelTests
    {
        [Test]
        public void PitchesByNumberAndScientificName()
        {
            Assert.That(Pitch.Parse("C4"), Is.EqualTo(60));
            Assert.That(Pitch.Parse("A4"), Is.EqualTo(69));
            Assert.That(Pitch.Parse("F#3"), Is.EqualTo(54));
            Assert.That(Pitch.Parse("Bb2"), Is.EqualTo(46));
            Assert.That(Pitch.Parse("e♭5"), Is.EqualTo(75));
            Assert.That(Pitch.Parse("C-1"), Is.EqualTo(0), "C-1 is MIDI 0 in scientific naming");
            Assert.That(Pitch.Parse("64"), Is.EqualTo(64));
            Assert.That(Pitch.Name(61), Is.EqualTo("C#4"));
            Assert.That(Pitch.Name(0), Is.EqualTo("C-1"));
            var buzzStyle = Assert.Throws<ToolError>(() => Pitch.Parse("C-5"));
            Assert.That(buzzStyle!.Message, Does.Contain("Buzz C-5 = C4 = 60"));
            Assert.Throws<ToolError>(() => Pitch.Parse("H2"));
            Assert.Throws<ToolError>(() => Pitch.Parse("130"));
            Assert.That(Pitch.Hz(69), Is.EqualTo(440));
        }

        [Test]
        public void BuzzNoteValuesMapToMidi()
        {
            int c5 = (5 << 4) | 1;   // Buzz shows C-5
            Assert.That(Pitch.FromBuzz(c5), Is.EqualTo(60));
            Assert.That(Pitch.ToBuzz(60), Is.EqualTo(c5));
            Assert.That(Pitch.FromBuzz((5 << 4) | 10), Is.EqualTo(69), "A-5 in Buzz is A4 = 440 Hz");
            Assert.That(Pitch.FromBuzz(Pitch.BuzzNoteOff), Is.EqualTo(-1));
            for (int m = 0; m < 120; m++) Assert.That(Pitch.FromBuzz(Pitch.ToBuzz(m)), Is.EqualTo(m));
        }

        [Test]
        public void PositionsInBeatsOrBarBeat()
        {
            Assert.That(SongTime.ParseBeats(JsonValue.Create(12.5), 4, "start"), Is.EqualTo(12.5));
            Assert.That(SongTime.ParseBeats(JsonValue.Create("17.1"), 4, "start"), Is.EqualTo(64));
            Assert.That(SongTime.ParseBeats(JsonValue.Create("17"), 4, "start"), Is.EqualTo(64));
            Assert.That(SongTime.ParseBeats(JsonValue.Create("2.3.5"), 4, "start"), Is.EqualTo(6.5), "bar 2, half a beat after beat 3");
            Assert.That(SongTime.ParseBeats(JsonValue.Create("2.3"), 3, "start"), Is.EqualTo(5), "3 beats per bar");
            var bad = Assert.Throws<ToolError>(() => SongTime.ParseBeats(JsonValue.Create("1.5"), 4, "start"));
            Assert.That(bad!.Message, Does.Contain("from 1 to 4"));
            Assert.Throws<ToolError>(() => SongTime.ParseBeats(JsonValue.Create(-1), 4, "start"));
            Assert.That(SongTime.Ticks(2.5, 4), Is.EqualTo(10));
            Assert.That(SongTime.BarBeat(64, 4), Is.EqualTo("17.1"));
        }

        static ParamInfo Neo(string name, MappingKind kind, int min, int max, double a = 0, double b = 1, string unit = "", bool flag = false, string[] names = null, double exponent = 1)
        {
            var m = new ParamMapping { Kind = kind, A = a, B = b, Unit = unit, Flag = flag, Names = names, Exponent = exponent };
            return new ParamInfo { Name = name, Min = min, Max = max, Default = min, Mapping = m, Describe = r => m.Value(r, min, max).ToString("0.###", CultureInfo.InvariantCulture) };
        }

        static int R(ParamInfo p, object value, int current = 0, V.Mode mode = V.Mode.Value) =>
            V.Resolve(p, value is string s ? JsonValue.Create(s) : JsonValue.Create(Convert.ToDouble(value)), current, mode);

        [Test]
        public void NotesTakePitchNamesAndWavesTheMachinesNames()
        {
            // Neo Sampler (machines.md §5): keys and roots are MIDI notes, a zone's wave a slot (its own texts).
            var mapping = new ParamMapping { Kind = MappingKind.Int, Unit = "note" };
            var root = new ParamInfo { Name = "Z1 root", Min = 0, Max = 128, Default = 128, Mapping = mapping, Describe = r => r == 128 ? "Wave" : Pitch.Name(r) };
            Assert.That(R(root, "C2"), Is.EqualTo(36));
            Assert.That(R(root, "F#3"), Is.EqualTo(54));
            Assert.That(R(root, "Bb2"), Is.EqualTo(46));
            Assert.That(R(root, 60), Is.EqualTo(60));
            Assert.That(R(root, "60"), Is.EqualTo(60));
            Assert.That(R(root, "64 note"), Is.EqualTo(64));
            Assert.That(R(root, "Wave"), Is.EqualTo(128));
            Assert.That(R(root, "+2", 60, V.Mode.By), Is.EqualTo(62));
            Assert.Throws<ToolError>(() => R(root, "H2"));
            var waveMapping = new ParamMapping { Kind = MappingKind.Int, Unit = "wave" };
            var wave = new ParamInfo { Name = "Z1 wave", Min = 0, Max = 254, Default = 1, Mapping = waveMapping, Describe = r => r == 0 ? "Off" : r == 3 ? "Kick" : r == 201 ? "Built-in Kick" : $"Slot {r}" };
            Assert.That(R(wave, "Kick"), Is.EqualTo(3));
            Assert.That(R(wave, "built-in kick"), Is.EqualTo(201));
            Assert.That(R(wave, "Off"), Is.EqualTo(0));
            Assert.That(R(wave, 7), Is.EqualTo(7));
            Assert.That(R(wave, "7 wave"), Is.EqualTo(7));
            // Other whole numbers are untouched.
            var steps = Neo("Steps", MappingKind.Int, 0, 16);
            Assert.That(R(steps, "8"), Is.EqualTo(8));
            Assert.Throws<ToolError>(() => R(steps, "C2"));
        }

        [Test]
        public void NeoParametersTakeExactUnits()
        {
            var cutoff = Neo("F1 cutoff", MappingKind.Freq, 0, 1000, 20, 20000);
            int r800 = R(cutoff, "800 Hz");
            Assert.That(cutoff.Mapping!.Value(r800, 0, 1000), Is.EqualTo(800).Within(800 * 0.004));
            Assert.That(R(cutoff, "0.8 kHz"), Is.EqualTo(r800));
            Assert.That(R(cutoff, 800), Is.EqualTo(r800), "a bare number is in the parameter's unit");
            Assert.That(R(cutoff, 2, r800, V.Mode.Times), Is.EqualTo(R(cutoff, "1600 Hz")));
            var wrongUnit = Assert.Throws<ToolError>(() => R(cutoff, "-6 dB"));
            Assert.That(wrongUnit!.Message, Does.Contain("F1 cutoff is in Hz, not dB"));

            var volume = Neo("Volume", MappingKind.Db, 0, 1000, -40, 6, flag: true);
            Assert.That(volume.Mapping!.Value(R(volume, "-6 dB"), 0, 1000), Is.EqualTo(-6).Within(0.05));
            Assert.That(R(volume, "-inf dB"), Is.EqualTo(0));
            int now = R(volume, "-12 dB");
            Assert.That(volume.Mapping.Value(R(volume, "+3 dB", now, V.Mode.By), 0, 1000), Is.EqualTo(-9).Within(0.05));
            Assert.That(volume.Mapping.Value(R(volume, -3, now, V.Mode.By), 0, 1000), Is.EqualTo(-15).Within(0.05));

            var mix = Neo("Mix", MappingKind.Percent, 0, 1000);
            Assert.That(R(mix, "35%"), Is.EqualTo(350));
            Assert.That(R(mix, 35), Is.EqualTo(350), "35 on a % parameter is 35 %");
            Assert.That(R(mix, "10%", 350, V.Mode.By), Is.EqualTo(450));
            var pan = Neo("Pan", MappingKind.Bipolar, 0, 1000);
            Assert.That(R(pan, "-20%"), Is.EqualTo(400));

            var release = Neo("Release", MappingKind.Time, 0, 1000, 0.001, 10);
            Assert.That(release.Mapping!.Value(R(release, "250 ms"), 0, 1000), Is.EqualTo(0.25).Within(0.25 * 0.01));
            var ratio = Neo("Ratio", MappingKind.Ratio, 0, 1000, 1, 20, flag: true);
            Assert.That(R(ratio, "inf:1"), Is.EqualTo(1000));
            Assert.That(ratio.Mapping!.Value(R(ratio, "4:1"), 0, 1000), Is.EqualTo(4).Within(0.05));
            var semi = Neo("Semi", MappingKind.Int, 0, 24, -12, unit: "st");
            Assert.That(R(semi, "+7 st"), Is.EqualTo(19));
            Assert.That(R(semi, -5), Is.EqualTo(7));
            Assert.That(R(semi, "raw:3"), Is.EqualTo(3));
            var spread = Neo("Spread", MappingKind.Linear, 0, 180, 0, 180, unit: "°");
            Assert.That(R(spread, "90°"), Is.EqualTo(90));
            Assert.That(R(spread, "120 deg"), Is.EqualTo(120));
            Assert.That(R(spread, 45), Is.EqualTo(45));
            Assert.That(R(semi, "max"), Is.EqualTo(24));
            Assert.Throws<ToolError>(() => R(semi, "raw:30"));
        }

        [Test]
        public void ChoicesByNameWithHelpfulErrors()
        {
            var type = Neo("F1 type", MappingKind.Enum, 0, 5, names: new[] { "Ladder 24", "Ladder 12", "SVF LP", "SVF BP", "SVF HP", "SVF Notch" });
            Assert.That(R(type, "SVF HP"), Is.EqualTo(4));
            Assert.That(R(type, "svf_bp"), Is.EqualTo(3));
            Assert.That(R(type, "SVF N"), Is.EqualTo(5), "a unique prefix");
            Assert.That(R(type, 1), Is.EqualTo(1));
            Assert.That(R(type, 1, 2, V.Mode.By), Is.EqualTo(3), "steps");
            var e = Assert.Throws<ToolError>(() => R(type, "SVF Hi"));
            Assert.That(e!.Message, Does.Contain("Choices: Ladder 24, Ladder 12, SVF LP, SVF BP, SVF HP, SVF Notch"));
            var sync = Neo("Sync", MappingKind.Switch, 0, 1, names: new[] { "Off", "On" });
            Assert.That(R(sync, "on"), Is.EqualTo(1));
            Assert.That(R(sync, "Off"), Is.EqualTo(0));
        }

        [Test]
        public void LegacyParametersAreReadThroughTheirDescriptions()
        {
            // A typical old machine: 0..127 raw, described in Hz on a curve, and a choice list.
            string Cutoff(int r) => (30 * Math.Pow(2, r / 12.0)).ToString(r < 60 ? "0" : "0.00", CultureInfo.InvariantCulture) + (r < 60 ? " Hz" : "") is var s && r >= 60 ? (30 * Math.Pow(2, r / 12.0) / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " kHz" : s;
            var cutoff = new ParamInfo { Name = "Cutoff", Min = 0, Max = 127, Default = 64, Describe = Cutoff };
            int raw = R(cutoff, "1000 Hz");
            Assert.That(30 * Math.Pow(2, raw / 12.0), Is.EqualTo(1000).Within(1000 * 0.03));
            Assert.That(R(cutoff, "4 kHz"), Is.EqualTo((int)Math.Round(12 * Math.Log2(4000 / 30.0))));
            Assert.That(R(cutoff, 2, raw, V.Mode.Times), Is.EqualTo(raw + 12));

            string[] waves = { "Sine", "Saw", "Square", "Noise" };
            var wave = new ParamInfo { Name = "Wave", Min = 0, Max = 3, Describe = r => waves[r] };
            Assert.That(R(wave, "square"), Is.EqualTo(2));
            var e = Assert.Throws<ToolError>(() => R(wave, "Triangle"));
            Assert.That(e!.Message, Does.Contain("isn't a value it takes"));

            var plain = new ParamInfo { Name = "Volume", Min = 0, Max = 254, Describe = _ => null };
            Assert.That(R(plain, 128), Is.EqualTo(128), "no descriptions: numbers are raw");
            var level = new ParamInfo { Name = "Gain", Min = 0, Max = 100, Describe = r => (r - 50) + " dB" };
            Assert.That(R(level, "-6 dB"), Is.EqualTo(44));
            Assert.That(R(level, "+3 dB", 44, V.Mode.By), Is.EqualTo(47));
        }
    }
}
