// Fork (docs/specs/mcp-server.md §12.5, U5): successor mappings and the conversion machine_replace
// computes. Every mapping parses and names parameters the two machines have (by their manifests);
// values move through units (percent to dB, tables to Hz, separate dry and wet levels to a
// crossfade, a threshold and clamp to drive and output); pattern columns convert event by event,
// ramps kept; tracker notes become notes, slides glides; the tolerance check reads loudness and
// third octaves.
using System.Text.Json.Nodes;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Model;

namespace ReBuzz.AgentTests
{
    [TestFixture]
    public class SuccessorTests
    {
        static Dictionary<string, MachineManifest>? all;

        static Dictionary<string, MachineManifest> All()
        {
            if (all != null) return all;
            var d = CuratedManifestTests.Profiled();
            foreach (var kv in CuratedManifestTests.Generated()) d[kv.Key] = kv.Value;
            return all = d;
        }

        /// <summary>The old machine's parameters by its manifest, plus the columns the mapping describes or its notes read (the machine has them; its manifest leaves them out).</summary>
        static List<SuccessorParam> OldParams(SuccessorMap map)
        {
            var list = SuccessorParam.FromManifest(All()[map.From]);
            IEnumerable<string> extra = map.Describe.Keys;
            if (map.Notes is { } n) extra = extra.Concat(new[] { n.Note, n.Slide, n.SlideLength, n.Length, n.Volume }.Where(x => x != null)!);
            foreach (var name in extra.Distinct())
                if (!list.Any(p => p.Name == name))
                    list.Add(new SuccessorParam { Name = name, Track = true, Min = 0, Max = 255, Default = 0 });
            return list;
        }

        static SuccessorConverter Converter(string from, string to)
        {
            var map = SuccessorLibrary.Find(from, to);
            Assert.That(map, Is.Not.Null, $"no mapping {from} → {to}");
            var c = new SuccessorConverter(map!, OldParams(map!), SuccessorParam.FromManifest(All()[to]));
            Assert.That(c.Errors, Is.Empty);
            return c;
        }

        static Func<SuccessorParam, int> Raws(SuccessorConverter c, Dictionary<string, int>? values = null) =>
            p => values != null && values.TryGetValue(p.Name, out int v) ? v : p.Default;

        static int Final(SuccessorState s, string param) => s.Final.Single(x => x.Param.Name == param).Raw;

        static double Value(SuccessorConverter c, SuccessorState s, string param) => c.New(param)!.Value(Final(s, param));

        [Test]
        public void EveryMappingParsesAndNamesParametersTheMachinesHave()
        {
            Assert.That(SuccessorLibrary.Errors, Is.Empty);
            var pairs = SuccessorLibrary.All.Select(m => m.Label).ToList();
            foreach (var expected in new[]
                     {
                         "Jeskola Reverb → Neo Verb", "Pedal EQ → Neo EQ", "Jeskola Distortion → Neo Drive", "Pedal Gain → Neo Utility",
                         "Jeskola Bass → Neo Poly", "Jeskola Bass 2 → Neo Poly", "Jeskola Bass 3 → Neo Poly",
                     })
                Assert.That(pairs, Does.Contain(expected));
            foreach (var map in SuccessorLibrary.All)
            {
                Assert.That(All().ContainsKey(map.From), $"{map.Label}: no manifest for {map.From}");
                Assert.That(All().ContainsKey(map.To), $"{map.Label}: no manifest for {map.To}");
                var c = new SuccessorConverter(map, OldParams(map), SuccessorParam.FromManifest(All()[map.To]));
                Assert.That(c.Errors, Is.Empty, map.Label);
                // Unit rules join the same quantity.
                foreach (var r in map.Rules.Where(r => r.Kind == SuccessorRuleKind.Unit))
                {
                    var a = c.Old(r.From)!;
                    var b = c.New(r.To)!;
                    Assert.That(a.Dimension, Is.EqualTo(b.Dimension), $"{map.Label}: {r.From} ({a.Dimension}) → {r.To} ({b.Dimension})");
                }
                // Every old parameter is mapped, left out with a reason, or read by the notes.
                var used = c.Used.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var r in map.Rules.Where(r => r.Kind == SuccessorRuleKind.Bypass)) used.Add(r.From);
                foreach (var p in All()[map.From].Parameters.Concat(All()[map.From].TrackParameters))
                    Assert.That(used.Contains(p.Name) || map.Unmapped.Any(u => string.Equals(u.Param, p.Name, StringComparison.OrdinalIgnoreCase)),
                        $"{map.Label}: {p.Name} is neither mapped nor listed as unmapped with a reason");
                Assert.That(map.Unmapped.All(u => u.Why.Length > 0), map.Label);
                Assert.That(map.Evidence, Is.Not.Empty, map.Label);
                // The settings at the old machine's defaults convert, every value reachable.
                var state = c.ConvertState(Raws(c));
                Assert.That(state.Values, Is.Not.Empty, map.Label);
                Assert.That(state.Notes, Is.Empty, map.Label);
                Assert.That(state.Values.Where(v => v.Note != null).Select(v => v.Note), Is.Empty, map.Label);
            }
        }

        [Test]
        public void PedalGainPercentBecomesDecibelsAndMuteMinusInfinity()
        {
            var c = Converter("Pedal Gain", "Neo Utility");
            var half = c.ConvertState(Raws(c, new() { ["Gain"] = 50 }));
            Assert.That(Value(c, half, "Gain"), Is.EqualTo(-6.0).Within(0.06));
            var twice = c.ConvertState(Raws(c, new() { ["Gain"] = 200 }));
            Assert.That(Value(c, twice, "Gain"), Is.EqualTo(6.02).Within(0.06));
            var muted = c.ConvertState(Raws(c, new() { ["Gain"] = 100, ["Mute"] = 1 }));
            Assert.That(Value(c, muted, "Gain"), Is.EqualTo(double.NegativeInfinity));
            var zero = c.ConvertState(Raws(c, new() { ["Gain"] = 0 }));
            Assert.That(Value(c, zero, "Gain"), Is.EqualTo(double.NegativeInfinity));
            Assert.That(half.InputGainDb, Is.Zero);
        }

        [Test]
        public void PedalEqBandsLandOnNeoEqBandsInHertzDecibelsAndQ()
        {
            var c = Converter("Pedal EQ", "Neo EQ");
            // LS Freq step 6 (80 Hz), LM Freq step 8 (400 Hz?), LM Q step 10, LM Gain +6 dB, HS Gain −3 dB, Output −1.5 dB.
            var old = new Dictionary<string, int> { ["LS Freq"] = 6, ["LS Gain"] = 60, ["LM Freq"] = 8, ["LM Gain"] = 60, ["LM Q"] = 10, ["HS Gain"] = 42, ["Output"] = 45 };
            var s = c.ConvertState(Raws(c, old));
            Assert.That(Value(c, s, "B2 freq"), Is.EqualTo(c.Old("LS Freq")!.Value(6)).Within(0.2 / 100 * c.Old("LS Freq")!.Value(6)));
            Assert.That(Value(c, s, "B3 freq"), Is.EqualTo(c.Old("LM Freq")!.Value(8)).Within(0.002 * c.Old("LM Freq")!.Value(8)));
            Assert.That(Value(c, s, "B3 Q"), Is.EqualTo(c.Old("LM Q")!.Value(10)).Within(0.01 * c.Old("LM Q")!.Value(10)));
            Assert.That(Value(c, s, "B2 gain"), Is.EqualTo(6).Within(0.05));
            Assert.That(Value(c, s, "B3 gain"), Is.EqualTo(6).Within(0.05));
            Assert.That(Value(c, s, "B7 gain"), Is.EqualTo(-3).Within(0.05));
            Assert.That(Value(c, s, "Output"), Is.EqualTo(-1.5).Within(0.05));
            Assert.That(c.New("B2 type")!.Format(Final(s, "B2 type")), Is.EqualTo("Low shelf"));
            Assert.That(c.New("B7 type")!.Format(Final(s, "B7 type")), Is.EqualTo("High shelf"));
            Assert.That(Value(c, s, "B2 Q"), Is.EqualTo(0.7071).Within(0.005));
            Assert.That(c.New("B5 on")!.Format(Final(s, "B5 on")), Is.EqualTo("Off"));
            Assert.That(s.Bypass, Is.False);
            var bypassed = c.ConvertState(Raws(c, new() { ["Bypass"] = 1 }));
            Assert.That(bypassed.Bypass, Is.True);
        }

        [Test]
        public void JeskolaReverbLevelsBecomeACrossfadeWithTheOutputsMakingUpTheLevel()
        {
            var c = Converter("Jeskola Reverb", "Neo Verb");
            var map = c.Map;
            double lateGain = Math.Pow(10, map.Rules[0].Wet![0].GainDb / 20);
            // Defaults: Dry 0 dB, Rev Out 768 (−14.5 dB), ER Out off.
            var s = c.ConvertState(Raws(c));
            double d = 1, r = 768 / 4096.0 * lateGain;
            double m = r / (d + r);
            Assert.That(Value(c, s, "Mix"), Is.EqualTo(m).Within(0.002));
            Assert.That(Value(c, s, "Late"), Is.EqualTo(0).Within(0.1));
            Assert.That(Value(c, s, "Early"), Is.EqualTo(double.NegativeInfinity));
            Assert.That(s.OutputGainDb, Is.EqualTo(map.OutputGainDb + 20 * Math.Log10(d + r)).Within(0.01));
            Assert.That(s.InputGainDb, Is.EqualTo(6.02).Within(0.01));
            // The dry path: (1 − m) × the make-up is the old dry level.
            Assert.That((1 - Value(c, s, "Mix")) * Math.Pow(10, (s.OutputGainDb - map.OutputGainDb) / 20), Is.EqualTo(1).Within(0.003));
            // Seconds and hertz carry over.
            var t = c.ConvertState(Raws(c, new() { ["Rev Time"] = 25, ["Pre-Delay"] = 30, ["High Cutoff"] = 6000 }));
            Assert.That(Value(c, t, "Decay"), Is.EqualTo(2.5).Within(0.05));
            Assert.That(Value(c, t, "Pre-delay"), Is.EqualTo(0.030).Within(0.0005));
            Assert.That(Value(c, t, "High damping"), Is.EqualTo(6000).Within(60));
            // Wet only (a send): all wet, no make-up.
            var send = c.ConvertState(Raws(c, new() { ["Dry Out"] = 0, ["Rev Out"] = 2048 }));
            Assert.That(Value(c, send, "Mix"), Is.EqualTo(1).Within(0.001));
            Assert.That(Value(c, send, "Late"), Is.EqualTo(20 * Math.Log10(0.5 * lateGain)).Within(0.1));
            // A High Cutoff below Neo Verb's damping range is clamped, with a note.
            var low = c.ConvertState(Raws(c, new() { ["High Cutoff"] = 300 }));
            Assert.That(low.Values.Single(v => v.Param.Name == "High damping").Note, Does.Contain("out of its reach"));
        }

        [Test]
        public void JeskolaDistortionThresholdAndClampBecomeDriveAndOutput()
        {
            var c = Converter("Jeskola Distortion", "Neo Drive");
            var s = c.ConvertState(Raws(c));
            // Defaults: threshold 25 % → 1.5 / 0.25 → +15.6 dB; clamp 200 % → +6.0 dB; Amount 127 → Mix 100 %.
            Assert.That(Value(c, s, "Drive"), Is.EqualTo(20 * Math.Log10(1.5 / 0.25)).Within(0.05));
            Assert.That(Value(c, s, "Output"), Is.EqualTo(6.02).Within(0.06));
            Assert.That(Value(c, s, "Mix"), Is.EqualTo(1).Within(0.001));
            Assert.That(c.New("Mode")!.Format(Final(s, "Mode")), Is.EqualTo("Transistor"));
            Assert.That(c.New("Auto gain")!.Format(Final(s, "Auto gain")), Is.EqualTo("Off"));
            // A threshold over full scale (the Aggressor demo's 2305 = 112.5 %): the drive bottoms out at 0 dB and the inputs take the rest.
            var hot = c.ConvertState(Raws(c, new() { ["+threshold"] = 2305, ["+clamp"] = 2305 }));
            double want = 20 * Math.Log10(1.5 / (2305 / 65534.0 * 31.999));
            Assert.That(Value(c, hot, "Drive"), Is.EqualTo(Math.Max(0, want)).Within(0.05));
            Assert.That(hot.InputGainDb, Is.EqualTo(6.02 + Math.Min(0, want)).Within(0.05));
        }

        [Test]
        public void PatternColumnsConvertEventByEventAndKeepTheirRamps()
        {
            var c = Converter("Pedal EQ", "Neo EQ");
            var state = c.ConvertState(Raws(c));
            var lmGain = c.Old("LM Gain")!;
            var lsSolo = c.Old("LS Solo")!;
            var events = new Dictionary<(SuccessorParam, int), IReadOnlyList<ColumnEvent>>
            {
                [(lmGain, 0)] = new[] { new ColumnEvent(0, 48), new ColumnEvent(960, 72, 480), new ColumnEvent(1920, 24) },
                [(lsSolo, 0)] = new[] { new ColumnEvent(0, 1) },
            };
            var notes = new List<string>();
            var result = c.ConvertEvents(state, (p, t) => p.Default, events, notes);
            var b3 = result.Single(kv => kv.Key.Param.Name == "B3 gain").Value;
            Assert.That(b3.Select(e => e.Time), Is.EqualTo(new[] { 0, 960, 1920 }));
            Assert.That(b3.Select(e => Math.Round(c.New("B3 gain")!.Value(e.Raw), 1)), Is.EqualTo(new[] { 0.0, 12.0, -12.0 }));
            Assert.That(b3[1].Duration, Is.EqualTo(480), "a ramp keeps its length");
            Assert.That(result.Keys.Select(k => k.Param.Name), Is.EquivalentTo(new[] { "B3 gain" }));
            Assert.That(notes, Has.Some.Contains("LS Solo").And.Some.Contains("no counterpart"));
        }

        [Test]
        public void ADryLevelInAPatternUsesTheMakeUpFixedForTheSettings()
        {
            var c = Converter("Jeskola Reverb", "Neo Verb");
            var state = c.ConvertState(Raws(c));
            double k = state.MixGain.Single().Value;
            var dry = c.Old("Dry Out")!;
            var events = new Dictionary<(SuccessorParam, int), IReadOnlyList<ColumnEvent>> { [(dry, 0)] = new[] { new ColumnEvent(0, 2048) } };
            var result = c.ConvertEvents(state, (p, t) => p.Default, events, new List<string>());
            double mix = c.New("Mix")!.Value(result.Single(kv => kv.Key.Param.Name == "Mix").Value.Single().Raw);
            // (1 − m) / k = 0.5: half the dry, the outputs' make-up unchanged.
            Assert.That((1 - mix) / k, Is.EqualTo(0.5).Within(0.005));
            Assert.That(result.Keys.Select(k2 => k2.Param.Name), Is.EquivalentTo(new[] { "Mix", "Late", "Early" }));
        }

        [Test]
        public void TrackerNotesBecomeNotesAndSlidesGlides()
        {
            var c = Converter("Jeskola Bass", "Neo Poly");
            var note = c.Old("Note")!;
            var slide = c.Old("Slide note")!;
            var slideLength = c.Old("Slide lenght")!;
            var volume = c.Old("Volume")!;
            const int T = NoteList.TimeBase;
            // Elak_SVF.bmx's pattern 00: notes at rows 0, 4, 8, 12; row 8 slides to 67 over 2 ticks; volume 64 (50 %) from row 4.
            var columns = new Dictionary<(SuccessorParam, int), IReadOnlyList<ColumnEvent>>
            {
                [(note, 0)] = new[] { new ColumnEvent(0, 72), new ColumnEvent(4 * T, 74), new ColumnEvent(8 * T, 69), new ColumnEvent(12 * T, 65) },
                [(slide, 0)] = new[] { new ColumnEvent(8 * T, 67) },
                [(slideLength, 0)] = new[] { new ColumnEvent(8 * T, 2) },
                [(volume, 0)] = new[] { new ColumnEvent(4 * T, 64) },
            };
            var glides = new List<(int, int)>();
            var notes = new List<string>();
            var result = c.ConvertNotes(columns, 16 * T, 0, 0.125, (p, t) => p.Default, glides, notes);
            Assert.That(notes, Is.Empty);
            // Buzz notes (octave << 4 | 1…12) as ReBuzz reads them; the slide's note right after its row's.
            int P(int buzz) => TrackNotes.FromBuzz(buzz);
            Assert.That(result.Select(n => (n.Time / T, n.Pitch)), Is.EqualTo(new[] { (0, P(72)), (4, P(74)), (8, P(69)), (8, P(67)), (12, P(65)) }));
            Assert.That(result[0].Duration, Is.EqualTo(4 * T));
            Assert.That(result[0].Velocity, Is.EqualTo(64), "Volume 0x80 (100 %) → 64");
            Assert.That(result[1].Velocity, Is.EqualTo(32), "Volume 0x40 (50 %) → 32, and it holds");
            Assert.That(result[2].Velocity, Is.EqualTo(32));
            Assert.That(result[3].Time, Is.EqualTo(8 * T + 1), "the slide's note overlaps, so a legato voice glides");
            Assert.That(glides.Count, Is.EqualTo(1));
            Assert.That(c.New("Glide")!.Value(glides[0].Item2), Is.EqualTo(0.25).Within(0.01), "2 ticks at 0.125 s");
        }

        [Test]
        public void JeskolaBass3VolumeIsTheRowsOnlyAndNoteLengthEndsTheNote()
        {
            var c = Converter("Jeskola Bass 3", "Neo Poly");
            var note = c.Old("Note")!;
            var volume = c.Old("Volume")!;
            var length = c.Old("Note lenght")!;
            const int T = NoteList.TimeBase;
            var columns = new Dictionary<(SuccessorParam, int), IReadOnlyList<ColumnEvent>>
            {
                [(note, 0)] = new[] { new ColumnEvent(0, 0x45), new ColumnEvent(4 * T, 0x47) },
                [(volume, 0)] = new[] { new ColumnEvent(0, 0xFE) },
                [(length, 0)] = new[] { new ColumnEvent(4 * T, 1) },
            };
            var result = c.ConvertNotes(columns, 8 * T, 0, 0.1, (p, t) => p.Default, new List<(int, int)>(), new List<string>());
            Assert.That(result.Select(n => n.Velocity), Is.EqualTo(new[] { 127, 64 }));
            Assert.That(result[1].Duration, Is.EqualTo(T), "Note lenght 1: one tick");
        }

        [Test]
        public void ToleranceReadsLoudnessAndThirdOctaves()
        {
            int rate = 44100;
            var noise = Signals.Noise(0.1, 3, 7);
            var a = new StereoAudio { Left = noise, Right = noise, Rate = rate };
            var tol = new SuccessorTolerance { LoudnessDb = 1, BandDb = 2 };
            var same = AudioReport.Tolerance(a, a, tol);
            Assert.That(same["within"]!.GetValue<bool>(), Is.True);
            Assert.That(same["loudness_db"]!["difference"]!.GetValue<double>(), Is.EqualTo(0).Within(0.01));
            var louder = Signals.Scale(noise, 1.5);
            var l = AudioReport.Tolerance(a, new StereoAudio { Left = louder, Right = louder, Rate = rate }, tol);
            Assert.That(l["loudness_within"]!.GetValue<bool>(), Is.False);
            Assert.That(l["bands_within"]!.GetValue<bool>(), Is.True, "level-matched, the bands are the same");
            // A one-pole low-pass at about 2 kHz: the highs drop well over 2 dB.
            var dark = new float[noise.Length];
            double y = 0, k = 1 - Math.Exp(-2 * Math.PI * 2000 / rate);
            for (int i = 0; i < noise.Length; i++) dark[i] = (float)(y += k * (noise[i] - y));
            var d = AudioReport.Tolerance(a, new StereoAudio { Left = dark, Right = dark, Rate = rate }, tol);
            Assert.That(d["bands_within"]!.GetValue<bool>(), Is.False);
            // Level-matched by loudness, the shape is off: highs down against lows by far more than 2 dB.
            Assert.That(Math.Abs(d["worst_band"]!["difference_db"]!.GetValue<double>()), Is.GreaterThan(2));
            Assert.That(d["bands_db"]!["10000"]!.GetValue<double>() - d["bands_db"]!["100"]!.GetValue<double>(), Is.LessThan(-10));
            var silent = new StereoAudio { Left = new float[rate], Right = new float[rate], Rate = rate };
            var s = AudioReport.Tolerance(silent, a, tol);
            Assert.That(s["within"], Is.Null);
            Assert.That(s["silent"]!.GetValue<string>(), Is.EqualTo("before"));
        }
    }
}
