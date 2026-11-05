// Fork (docs/specs/mcp-server.md §12.5, U5): successor mappings and the conversion machine_replace
// computes. Every mapping parses and names parameters the two machines have (by their manifests);
// values move through units (percent to dB, tables to Hz, separate dry and wet levels to a
// crossfade, a threshold and clamp to drive and output, a graphic EQ's summed bands to the curve
// at other centres); pattern columns convert event by event, ramps kept; tracker notes become
// notes, slides glides; the tolerance check reads loudness and third octaves.
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
            if (map.Notes is { } n) extra = extra.Concat(new[] { n.Note, n.Slide, n.SlideLength, n.Length, n.Volume }.Where(x => x != null)!).Concat(n.Triggers.Select(t => t.Column));
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
                         "Jeskola Flanger → Neo Ensemble", "Jeskola Chorus → Neo Ensemble", "FSM Chorus2 → Neo Ensemble", "Pedal Chorus → Neo Ensemble",
                         "Joachims Hyperion → Neo EQ",
                         "Jeskola Delay → Neo Delay", "Jeskola Cross Delay → Neo Delay", "Jeskola NiNjA dElaY → Neo Delay", "FSM PanzerDelay → Neo Delay",
                         "Pedal M1 → Neo Sampler",
                         "Rout 909 → Neo Drums", "FSM KickXP → Neo Drums",
                         "FSM WahMan → Neo Filter", "Pedal Filter → Neo Filter",
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
                // The settings at the old machine's defaults convert, every value reachable (but Jeskola Flanger's, whose default
                // wet out is inverted and whose sweep starts at no delay: both have no counterpart, and the notes say so).
                var state = c.ConvertState(Raws(c));
                Assert.That(state.Values, Is.Not.Empty, map.Label);
                if (map.From == "Jeskola Flanger")
                    Assert.That(state.Notes, Has.Some.Contains("polarity").And.Some.Contains("sweep goes down to 0 ms"), map.Label);
                else
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
        public void ADelaysLengthInTicksBecomesANoteValueAndOtherUnitsAFreeTime()
        {
            // Jeskola Delay at its defaults: 3 ticks at 4 ticks a beat is 1/8 dotted (it follows the tempo, as Neo Delay's Sync
            // does); feedback 50 %, dry on and wet 37.5 %: Mix 0.375 ÷ 1.375, Output +2.77 dB; its mono sum's +6 dB on the outputs.
            var c = Converter("Jeskola Delay", "Neo Delay");
            var s = c.ConvertState(Raws(c));
            string Text(SuccessorState st, string p) => c.New(p)!.Format(Final(st, p));
            Assert.That(Text(s, "Sync"), Is.EqualTo("1/8"));
            Assert.That(Text(s, "Feel"), Is.EqualTo("Dotted"));
            Assert.That(Text(s, "Mode"), Is.EqualTo("Stereo"));
            Assert.That(Value(c, s, "Time"), Is.EqualTo(3 * 5512 / 44100.0).Within(0.002), "Time takes the seconds too (120 BPM, 44.1 kHz)");
            Assert.That(Value(c, s, "Feedback"), Is.EqualTo(0.5).Within(0.001));
            Assert.That(Value(c, s, "Mix"), Is.EqualTo(0.375 / 1.375).Within(0.002));
            Assert.That(Value(c, s, "Output"), Is.EqualTo(20 * Math.Log10(1.375)).Within(0.05));
            Assert.That(Final(s, "Low cut"), Is.EqualTo(c.New("Low cut")!.Min), "the low cut off");
            Assert.That(Final(s, "High cut"), Is.EqualTo(c.New("High cut")!.Max), "the high cut open");
            Assert.That(s.InputGainDb, Is.Zero, "the line keeps the input's level");
            Assert.That(s.OutputGainDb, Is.EqualTo(6.02).Within(0.01));
            // Note values: 1 tick a sixteenth, 2 an eighth, 4 a quarter, 6 a dotted quarter, 16 a bar; at 3 ticks a beat, 1 tick an eighth triplet.
            foreach (var (ticks, tpb, sync, feel) in new[] { (1, 4, "1/16", "Straight"), (2, 4, "1/8", "Straight"), (4, 4, "1/4", "Straight"), (6, 4, "1/4", "Dotted"),
                                                             (16, 4, "1 bar", "Straight"), (1, 3, "1/8", "Triplet"), (2, 6, "1/8", "Triplet"), (12, 4, "1/2", "Dotted") })
            {
                c.Timing = new SuccessorTiming(120, tpb, 44100);
                var t = c.ConvertState(Raws(c, new() { ["Length"] = ticks }));
                Assert.That((Text(t, "Sync"), Text(t, "Feel")), Is.EqualTo((sync, feel)), $"{ticks} ticks at {tpb} a beat");
            }
            // 5 ticks is no note value: free, at the tempo's 5 × (whole) samples per tick, with a note.
            c.Timing = new SuccessorTiming(128, 4, 48000);
            var five = c.ConvertState(Raws(c, new() { ["Length"] = 5 }));
            Assert.That(Text(five, "Sync"), Is.EqualTo("Free"));
            Assert.That(Value(c, five, "Time"), Is.EqualTo(5 * 5625 / 48000.0).Within(0.002));
            Assert.That(five.Notes, Has.Some.Contains("no note value"));
            // Milliseconds and samples: free.
            var ms = c.ConvertState(Raws(c, new() { ["Length"] = 250, ["Length unit"] = 1 }));
            Assert.That(Text(ms, "Sync"), Is.EqualTo("Free"));
            Assert.That(Value(c, ms, "Time"), Is.EqualTo(0.25).Within(0.001));
            var samples = c.ConvertState(Raws(c, new() { ["Length"] = 12000, ["Length unit"] = 2 }));
            Assert.That(Value(c, samples, "Time"), Is.EqualTo(0.25).Within(0.001));
            // Feedback at −100 % never decays on Jeskola Delay; Neo Delay's stops at −99 %.
            var neg = c.ConvertState(Raws(c, new() { ["Feedback"] = 0 }));
            Assert.That(Value(c, neg, "Feedback"), Is.EqualTo(-0.99).Within(0.0005));
        }

        [Test]
        public void ACrossDelaysRightLengthBecomesTheOffsetAndPanzerDelaysLevelsInDecibelsACrossfade()
        {
            var c = Converter("Jeskola Cross Delay", "Neo Delay");
            var s = c.ConvertState(Raws(c, new() { ["L. Length"] = 4, ["R. Length"] = 6 }));
            Assert.That(c.New("Mode")!.Format(Final(s, "Mode")), Is.EqualTo("Ping-pong"), "measured: its input enters the left line only");
            Assert.That(c.New("Sync")!.Format(Final(s, "Sync")), Is.EqualTo("1/4"));
            Assert.That(Value(c, s, "Offset"), Is.EqualTo(0.5).Within(0.001), "6 ticks against 4: +50 %");
            Assert.That(s.Notes, Is.Empty);
            var far = c.ConvertState(Raws(c, new() { ["L. Length"] = 2, ["R. Length"] = 8 }));
            Assert.That(Value(c, far, "Offset"), Is.EqualTo(1).Within(0.001), "four times as long: Offset stops at +100 %");
            Assert.That(far.Values.Single(v => v.Param.Name == "Offset").Note, Does.Contain("out of its reach"));

            var p = Converter("FSM PanzerDelay", "Neo Delay");
            var d = p.ConvertState(Raws(p));
            // Dry amp and Tap Amp at 0 dB add: Mix 50 %, Output +6 dB; FB Amp −9 dB (2^(−1.5) in its source) × 0.9999.
            Assert.That(Value(p, d, "Mix"), Is.EqualTo(0.5).Within(0.001));
            Assert.That(Value(p, d, "Output"), Is.EqualTo(6.02).Within(0.06));
            Assert.That(Value(p, d, "Feedback"), Is.EqualTo(Math.Pow(10, -9 / 20.0) * 0.9999).Within(0.002));
            Assert.That(p.New("Mode")!.Format(Final(d, "Mode")), Is.EqualTo("Stereo"));
            Assert.That(d.InputGainDb, Is.Zero, "PanzerDelay is stereo");
            var pp = p.ConvertState(Raws(p, new() { ["FB PingPong"] = 1, ["Tap Amp"] = 180, ["Dry amp"] = 0 }));
            Assert.That(p.New("Mode")!.Format(Final(pp, "Mode")), Is.EqualTo("Cross"));
            Assert.That(Value(p, pp, "Mix"), Is.EqualTo(1).Within(0.001), "no dry: all wet");
            Assert.That(Value(p, pp, "Output"), Is.EqualTo(-6).Within(0.06));
        }

        [Test]
        public void NinjaDelaysFilterBecomesTheLowCutOrTheHighCut()
        {
            var c = Converter("Jeskola NiNjA dElaY", "Neo Delay");
            string Text(SuccessorState st, string p) => c.New(p)!.Format(Final(st, p));
            // Its default: a high-pass at 550 Hz in the loop.
            var s = c.ConvertState(Raws(c));
            Assert.That(Value(c, s, "Low cut"), Is.EqualTo(550).Within(550 * 0.004));
            Assert.That(Final(s, "High cut"), Is.EqualTo(c.New("High cut")!.Max), "the high cut open");
            Assert.That(Value(c, s, "Feedback"), Is.EqualTo(0.75).Within(0.002));
            var lp = c.ConvertState(Raws(c, new() { ["f. type"] = 0, ["f. cutoff"] = 2500 }));
            Assert.That(Value(c, lp, "High cut"), Is.EqualTo(2500).Within(2500 * 0.004));
            Assert.That(Final(lp, "Low cut"), Is.EqualTo(c.New("Low cut")!.Min), "the low cut off");
            // Its band-pass is about 100 Hz wide (measured): no counterpart, the filters open, with a note; its notch is left out, with a note.
            var bp = c.ConvertState(Raws(c, new() { ["f. type"] = 2, ["f. cutoff"] = 1000 }));
            Assert.That(Final(bp, "Low cut"), Is.EqualTo(c.New("Low cut")!.Min));
            Assert.That(bp.Notes, Has.Some.Contains("no counterpart"));
            var br = c.ConvertState(Raws(c, new() { ["f. type"] = 3 }));
            Assert.That(Final(br, "High cut"), Is.EqualTo(c.New("High cut")!.Max));
            Assert.That(br.Notes, Has.Some.Contains("notch"));
            // PanzerDelay's filter runs at twice its rate: its low-pass is Neo Delay's high cut at 1.30 × Cutoff, its band-pass 0.84 … 4.26 × Cutoff.
            var p = Converter("FSM PanzerDelay", "Neo Delay");
            var lp2 = p.ConvertState(Raws(p, new() { ["Filter type"] = 1 }));
            double cutoff = p.Old("Cutoff")!.Value(p.Old("Cutoff")!.Default);
            Assert.That(p.New("High cut")!.Value(Final(lp2, "High cut")), Is.EqualTo(1.30 * cutoff).Within(0.01 * cutoff));
            var bp2 = p.ConvertState(Raws(p, new() { ["Filter type"] = 3 }));
            Assert.That(p.New("Low cut")!.Value(Final(bp2, "Low cut")), Is.EqualTo(0.84 * cutoff).Within(0.01 * cutoff));
            Assert.That(p.New("High cut")!.Value(Final(bp2, "High cut")), Is.EqualTo(4.26 * cutoff).Within(0.03 * cutoff));
        }

        [Test]
        public void ASweptDelayLandsOnItsGeometricCentreAndOctaves()
        {
            // FSM Chorus2 at its defaults: 2 + 1120.5 × 80 ÷ 240 samples, plus 2 × 110.25 × 90 ÷ 240 at the top (44.1 kHz).
            var c = Converter("FSM Chorus2", "Neo Ensemble");
            var s = c.ConvertState(Raws(c));
            double lo = (2 + 1120.5 * 80 / 240.0) / 44100, hi = lo + 2 * 110.25 * 90 / 240.0 / 44100;
            Assert.That(Value(c, s, "Delay"), Is.EqualTo(Math.Sqrt(lo * hi)).Within(0.01 * Math.Sqrt(lo * hi)));
            Assert.That(Value(c, s, "Depth"), Is.EqualTo(Math.Log2(hi / lo) / 2).Within(0.006));
            Assert.That(c.New("Voices")!.Format(Final(s, "Voices")), Is.EqualTo("3"));
            // Fatness 3: the dry at 1 ÷ 3.5 and each tap at 2.5 ÷ 10.5, as FSM weighs them.
            double m = Value(c, s, "Mix"), g = Math.Pow(10, Value(c, s, "Output") / 20);
            Assert.That(g * (1 - m), Is.EqualTo(1 / 3.5).Within(0.003));
            Assert.That(g * m / Math.Sqrt(3), Is.EqualTo(2.5 / 10.5).Within(0.003));
            Assert.That(Value(c, s, "Spread"), Is.EqualTo(90).Within(1));

            // Pedal Chorus: Delay ± Depth, and its Mix a crossfade whose output gain keeps the dry level.
            var p = Converter("Pedal Chorus", "Neo Ensemble");
            var ps = p.ConvertState(Raws(p, new() { ["Delay"] = 20, ["Depth"] = 50, ["Mix"] = 60 }));
            Assert.That(Value(p, ps, "Delay"), Is.EqualTo(Math.Sqrt(0.016 * 0.024)).Within(0.0003));
            Assert.That(Value(p, ps, "Depth"), Is.EqualTo(Math.Log2(0.024 / 0.016) / 2).Within(0.006));
            double pm = Value(p, ps, "Mix"), pg = Math.Pow(10, Value(p, ps, "Output") / 20);
            Assert.That(pg * (1 - pm), Is.EqualTo(0.4).Within(0.005));
            Assert.That(pg * pm, Is.EqualTo(0.6 * 0.7071).Within(0.005));
            Assert.That(ps.Notes, Is.Empty);
            // A sweep down to no delay keeps its top at full depth, with a note.
            var deep = p.ConvertState(Raws(p, new() { ["Delay"] = 5, ["Depth"] = 100 }));
            Assert.That(Value(p, deep, "Depth"), Is.EqualTo(1).Within(0.001));
            Assert.That(Value(p, deep, "Delay"), Is.EqualTo(0.013 / 2).Within(0.0002));
            Assert.That(deep.Notes, Has.Some.Contains("sweep goes down to 0 ms"));
        }

        [Test]
        public void SeparateDryAndWetBecomeACrossfadeAndAnInvertedOneIsANote()
        {
            var c = Converter("Jeskola Chorus", "Neo Ensemble");
            // dry out 100 %, wet out 50 %: mix ⅓, output +3.5 dB.
            var s = c.ConvertState(Raws(c, new() { ["dry out"] = 128, ["wet out"] = 96 }));
            Assert.That(Value(c, s, "Mix"), Is.EqualTo(1 / 3.0).Within(0.006));
            Assert.That(Value(c, s, "Output"), Is.EqualTo(20 * Math.Log10(1.5)).Within(0.06));
            Assert.That(s.Notes, Is.Empty);
            Assert.That(s.InputGainDb, Is.EqualTo(6.02).Within(0.01), "a mono machine: the inputs' sum");
            var inverted = c.ConvertState(Raws(c, new() { ["dry out"] = 128, ["wet out"] = 0 }));
            Assert.That(Value(c, inverted, "Mix"), Is.EqualTo(0.5).Within(0.006));
            Assert.That(inverted.Notes, Has.Some.Contains("inverts the wet signal").And.Some.Contains("polarity is not"));
            var feedback = c.ConvertState(Raws(c, new() { ["feedback"] = 128 }));
            Assert.That(Value(c, feedback, "Feedback"), Is.EqualTo(0.99).Within(0.001), "Neo Ensemble's feedback stops at 99 %");
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
        public void PedalM1sKeyZonesBecomeZonesAndItsTimeConstantsNeoSamplersTimes()
        {
            var c = Converter("Pedal M1", "Neo Sampler");
            // Its defaults: ReBuzz holds a wave parameter at 1 or more, so OSC1's zones 2–4 play wave 1 from their splits (Pedal M1's
            // MIDI 60, 72, 84: Buzz C-4, C-5, C-6, Neo Sampler's keys 48, 60, 72); OSC2 is off (OSC1 only).
            var d = c.ConvertState(Raws(c));
            Assert.That(new[] { 1, 2, 3, 4 }.Select(z => (Final(d, $"Z{z} wave"), Final(d, $"Z{z} key low"), Final(d, $"Z{z} key high"))),
                Is.EqualTo(new[] { (1, 0, 47), (1, 48, 59), (1, 60, 71), (1, 72, 127) }));
            Assert.That(new[] { 5, 6, 7, 8 }.Select(z => Final(d, $"Z{z} wave")), Is.All.EqualTo(0));
            Assert.That(Value(c, d, "Z1 level"), Is.EqualTo(20 * Math.Log10(100 / 127.0)).Within(0.06), "OSC1 Level 100 of 127");
            Assert.That(Value(c, d, "Volume"), Is.EqualTo(20 * Math.Log10(0.5 * 100 / 127.0)).Within(0.06), "Volume 100, halved");
            double Tau(int raw) => 0.001 * Math.Pow(8000, raw / 127.0);   // TimeMs
            Assert.That(Value(c, d, "Amp attack"), Is.EqualTo(Tau(10) * 3.045).Within(Tau(10) * 3.045 * 0.03), "a time constant to full");
            Assert.That(Value(c, d, "Amp release"), Is.EqualTo(Tau(40) * 6.908).Within(Tau(40) * 6.908 * 0.03), "a time constant to −60 dB");
            Assert.That(Value(c, d, "Cutoff"), Is.EqualTo(18000).Within(18000 * 0.01));
            Assert.That(c.New("Filter")!.Format(Final(d, "Filter")), Is.EqualTo("LP 12"));

            // A zone covered by a higher one is off; a wave below the splits plays only up to the next zone that's set.
            var split = c.ConvertState(Raws(c, new() { ["OSC1 Wave"] = 3, ["OSC1 Wave 2"] = 5, ["OSC1 Split 2"] = 72, ["OSC1 Wave 3"] = 6, ["OSC1 Split 3"] = 72,
                                                       ["OSC1 Wave 4"] = 7, ["OSC1 Split 4"] = 96, ["OSC Mode"] = 2, ["OSC2 Wave"] = 9, ["OSC2 Octave"] = 3, ["OSC2 Transpose"] = 17 }));
            Assert.That(new[] { 1, 2, 3, 4 }.Select(z => (Final(split, $"Z{z} wave"), Final(split, $"Z{z} key low"), Final(split, $"Z{z} key high"))),
                Is.EqualTo(new[] { (3, 0, 59), (0, 0, 127), (6, 60, 83), (7, 84, 127) }));
            Assert.That(Final(split, "Z5 wave"), Is.EqualTo(9), "OSC Mode Both: OSC2 plays too");
            Assert.That(Value(c, split, "Z5 tune"), Is.EqualTo(17), "an octave and 5 semitones, on each of OSC2's zones");
            var osc2 = c.ConvertState(Raws(c, new() { ["OSC Mode"] = 1, ["OSC2 Wave"] = 9 }));
            Assert.That(Final(osc2, "Z1 wave"), Is.EqualTo(0), "OSC2 only: OSC1's zones are off");
            Assert.That(Final(osc2, "Z5 wave"), Is.EqualTo(9));

            // Key tracking centred an octave lower, and velocity round 64: the cutoff moves by both in octaves.
            var keyed = c.ConvertState(Raws(c, new() { ["Cutoff"] = 73, ["Key Track"] = 127, ["Filter Vel"] = 127 }));
            double hz = 20 * Math.Pow(900, 73 / 127.0);
            Assert.That(Value(c, keyed, "Cutoff"), Is.EqualTo(hz * Math.Pow(2, 63 / 64.0 + 1)).Within(hz * 0.04));
            Assert.That(Value(c, keyed, "Key track"), Is.EqualTo(63 / 64.0).Within(0.01));
            Assert.That(Value(c, keyed, "Filter vel"), Is.EqualTo(2 / 3.0).Within(0.01));

            // Its notes' velocity is the track Volume (it also has a global one), 127 where a track has none.
            Assert.That(c.OldColumn("Volume")!.Track, Is.True);
            Assert.That(c.Old("Volume")!.Track, Is.False);
            const int T = NoteList.TimeBase;
            var columns = new Dictionary<(SuccessorParam, int), IReadOnlyList<ColumnEvent>>
            {
                [(c.OldColumn("Note")!, 0)] = new[] { new ColumnEvent(0, 0x41), new ColumnEvent(4 * T, 0x45) },
                [(c.OldColumn("Note")!, 1)] = new[] { new ColumnEvent(0, 0x48) },
                [(c.OldColumn("Volume")!, 0)] = new[] { new ColumnEvent(4 * T, 64) },
            };
            var result = c.ConvertNotes(columns, 8 * T, 0, 0.1, (p, t) => p.Default, new List<(int, int)>(), new List<string>());
            Assert.That(result.Select(n => (n.Time / T, n.Pitch, n.Velocity)),
                Is.EqualTo(new[] { (0, TrackNotes.FromBuzz(0x41), 127), (0, TrackNotes.FromBuzz(0x48), 127), (4, TrackNotes.FromBuzz(0x45), 64) }));
        }

        [Test]
        public void Rout909sTriggersBecomeHitsOnTheGeneralMidiKeysAndItsScalesTheVoicesUnits()
        {
            var c = Converter("Rout 909", "Neo Drums");
            Assert.That(c.Map.Notes!.Note, Is.Null, "Rout 909 has no note column: its triggers are its notes");
            Assert.That(c.OldColumn("Note"), Is.Null);
            // Its defaults (every scale at 32, levels at 100 %): the voices as Neo Drums' defaults were voiced against them.
            var d = c.ConvertState(Raws(c));
            Assert.That(Value(c, d, "Volume"), Is.EqualTo(7.5).Within(0.05), "the headroom Neo Drums keeps, made up");
            foreach (var level in new[] { "Kick level", "Snare level", "Low tom level", "Rim level", "Clap level", "Closed hat level", "Crash level", "Ride level" })
                Assert.That(Value(c, d, level), Is.EqualTo(0).Within(0.06), level);
            Assert.That(Value(c, d, "Kick sweep time"), Is.EqualTo(0.025).Within(0.0005));
            Assert.That(Value(c, d, "Snare tune"), Is.EqualTo(179.3).Within(1));
            Assert.That(Value(c, d, "Low tom tune"), Is.EqualTo(59.6).Within(0.5));
            Assert.That(Value(c, d, "Open hat decay"), Is.EqualTo(0.2).Within(0.005));
            Assert.That(Value(c, d, "Crash decay"), Is.EqualTo(1.5).Within(0.02));
            Assert.That(Value(c, d, "Crash tune"), Is.EqualTo(0).Within(0.1));

            // The scales' ends, through the measured points; a level halved is −6 dB, at 0 the voice is silent.
            var ends = c.ConvertState(Raws(c, new() { ["SD Tune"] = 63, ["CH Decay"] = 63, ["RC Tune"] = 0, ["BD Level"] = 64, ["CP Level"] = 0, ["SD Snap"] = 0 }));
            Assert.That(Value(c, ends, "Snare tune"), Is.EqualTo(241.3).Within(1.5));
            Assert.That(Value(c, ends, "Closed hat decay"), Is.EqualTo(0.025).Within(0.001));
            Assert.That(Value(c, ends, "Ride tune"), Is.EqualTo(-5).Within(0.1));
            Assert.That(Value(c, ends, "Ride decay"), Is.EqualTo(2.53).Within(0.03));
            Assert.That(Value(c, ends, "Kick level"), Is.EqualTo(20 * Math.Log10(64 / 128.0)).Within(0.06));
            Assert.That(Value(c, ends, "Clap level"), Is.EqualTo(double.NegativeInfinity));
            Assert.That(Value(c, ends, "Snare snap"), Is.EqualTo(0.1).Within(0.005));

            // Trigger columns become notes on the voices' keys, at velocity 127; a 0 hits nothing.
            const int T = NoteList.TimeBase;
            var columns = new Dictionary<(SuccessorParam, int), IReadOnlyList<ColumnEvent>>
            {
                [(c.OldColumn("BD Trigger")!, 0)] = new[] { new ColumnEvent(0, 1), new ColumnEvent(2 * T, 1) },
                [(c.OldColumn("SD Trigger")!, 0)] = new[] { new ColumnEvent(T, 1), new ColumnEvent(3 * T, 0) },
                [(c.OldColumn("CH Trigger")!, 1)] = new[] { new ColumnEvent(0, 1) },
                [(c.OldColumn("OH Trigger")!, 1)] = new[] { new ColumnEvent(T / 2, 1) },
                [(c.OldColumn("RC Trigger")!, 0)] = new[] { new ColumnEvent(3 * T, 1) },
            };
            var notes = new List<string>();
            var result = c.ConvertNotes(columns, 4 * T, 0, 0.1, (p, t) => p.Default, new List<(int, int)>(), notes);
            Assert.That(result.Select(n => (n.Time, n.Pitch, n.Velocity)),
                Is.EqualTo(new[] { (0, 36, 127), (0, 42, 127), (T / 2, 46, 127), (T, 38, 127), (2 * T, 36, 127), (3 * T, 51, 127) }));
            Assert.That(notes, Is.Empty);
        }

        [Test]
        public void KickXPsNotesTuneTheKickAndItsTriggersAreKicksAtTheirVolume()
        {
            var c = Converter("FSM KickXP", "Neo Drums");
            Assert.That(c.Map.Notes!.RulesReadNote, Is.True);
            const int T = NoteList.TimeBase;
            int a1 = (2 << 4) + 9 + 1, e1 = (2 << 4) + 4 + 1;   // Buzz A-2 and E-2: A1 (55 Hz) and E1
            var columns = new Dictionary<(SuccessorParam, int), IReadOnlyList<ColumnEvent>>
            {
                [(c.OldColumn("Pitch limit")!, 0)] = new[] { new ColumnEvent(0, a1), new ColumnEvent(2 * T, a1), new ColumnEvent(3 * T, e1) },
                [(c.OldColumn("Trigger")!, 0)] = new[] { new ColumnEvent(T, 64), new ColumnEvent(2 * T, 128), new ColumnEvent(3 * T + T / 2, 0) },
            };
            // The song's most common note tunes the kick: the rules read it as the note column's value.
            Assert.That(c.MostCommonNote(new[] { columns }, out int distinct), Is.EqualTo(a1));
            Assert.That(distinct, Is.EqualTo(2));
            var noteColumn = c.OldColumn("Pitch limit")!;
            var d = c.ConvertState(p => p == noteColumn ? a1 : p.Default);
            Assert.That(Value(c, d, "Kick tune"), Is.EqualTo(55).Within(0.3));
            double start = c.Old("Start")!.Value(145);
            Assert.That(start, Is.EqualTo(33 * Math.Pow(128, 145 / 240.0)).Within(start * 0.01), "Start in Hz, as its source computes it");
            double sweep = 0.555 * 12 * (Math.Log2(start / 55) + 2.73 * 0.47) + 1.62;
            Assert.That(Value(c, d, "Kick sweep"), Is.EqualTo(sweep).Within(0.6), "the fitted interval from Start down to the note, Punch adding");
            Assert.That(Value(c, d, "Kick sweep time"), Is.EqualTo(0.0235).Within(0.001), "T DecRate 30");
            double release = 1 / (Math.Pow(20, 105 / 240.0 - 1) * 25), hold = c.Old("A DecTime")!.Value(32);
            Assert.That(Value(c, d, "Kick decay"), Is.EqualTo(2.7 * (hold + release)).Within(2.7 * (hold + release) * 0.03));
            Assert.That(Value(c, d, "Kick punch"), Is.EqualTo(0));
            Assert.That(d.Notes, Is.Empty);
            var deep = c.ConvertState(p => p == noteColumn ? (1 << 4) + 9 + 1 : p.Default);   // A0
            Assert.That(Value(c, deep, "Kick tune"), Is.EqualTo(30), "A0 (27.5 Hz) is under the kick's lowest tune");
            Assert.That(deep.Values.Where(v => v.Note != null).Select(v => v.Note), Has.Some.Contains("Kick tune"), "and says so");

            // Every note and every Trigger value is a kick on C2; a Trigger value is the volume (128 = 100 %), which holds for the
            // notes after it, as velocity in proportion (Vel → level 50 %); a row with a note and a Trigger value is one kick.
            var notes = new List<string>();
            var result = c.ConvertNotes(columns, 4 * T, 0, 0.1, (p, t) => p.Default, new List<(int, int)>(), notes);
            Assert.That(result.Select(n => (n.Time, n.Pitch, n.Velocity)),
                Is.EqualTo(new[] { (0, 36, 127), (T, 36, 64), (2 * T, 36, 127), (3 * T, 36, 127) }));
            Assert.That(c.New("Vel → level")!.Format(Final(d, "Vel → level")), Is.EqualTo("50%"));

            // In the patterns each note moves the kick's tune (and sweep) as it plays, as automation; a note-off moves nothing.
            var withOff = new Dictionary<(SuccessorParam, int), IReadOnlyList<ColumnEvent>>(columns)
            {
                [(noteColumn, 0)] = new[] { new ColumnEvent(0, a1), new ColumnEvent(T / 2, 255), new ColumnEvent(3 * T, e1) },
            };
            var moved = c.ConvertEvents(d, (p, t) => p.Default, withOff, new List<string>());
            var tune = moved[(c.New("Kick tune")!, 0)];
            Assert.That(tune.Select(e => e.Time), Is.EqualTo(new[] { 0, 3 * T }), "a note-off moves nothing");
            Assert.That(c.New("Kick tune")!.Value(tune[1].Raw), Is.EqualTo(41.2).Within(0.3), "E1's kick at E1");
            Assert.That(moved.ContainsKey((c.New("Kick sweep")!, 0)), Is.True);
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
        public void HyperionsCurveIsReadAtTheIsoCentresAndWhatTheSlidersCantReachGoesToTheOutput()
        {
            var c = Converter("Joachims Hyperion", "Neo EQ");
            var rule = c.Map.Rules.Single(r => r.Kind == SuccessorRuleKind.Graphic);
            Assert.That(rule.Bands, Has.Count.EqualTo(30));
            Assert.That(rule.Sliders, Has.Count.EqualTo(31));
            var sliders = c.NewParams.Where(p => p.Name.StartsWith("G ")).Select(p => p.Name).ToList();
            Assert.That(rule.Sliders.Select(s => s.Param), Is.EquivalentTo(sliders), "every graphic slider is set");

            // The defaults: graphic mode, flat, the output at 0 dB, not bypassed.
            var flat = c.ConvertState(Raws(c));
            Assert.That(c.New("Mode")!.Format(Final(flat, "Mode")), Is.EqualTo("Graphic"));
            foreach (var name in sliders) Assert.That(Value(c, flat, name), Is.Zero, name);
            Assert.That(Value(c, flat, "Output"), Is.Zero.Within(0.05));
            Assert.That(flat.Bypass, Is.False);
            Assert.That(flat.OutputGainDb, Is.Zero);

            // One band at +12 dB: each slider is the band's level at its centre (the cookbook's peaking filter at 44.1 kHz, as
            // wide as Bandwidth's measured table says: 2.0 octaves at the default, twice what Hyperion displays).
            Assert.That(rule.Rate, Is.EqualTo(44100));
            var b900 = rule.Bands.Single(b => b.Param.Trim() == "900 Hz");
            Assert.That(b900.Hz, Is.EqualTo(20 * Math.Pow(1000, 16 / 29.0)).Within(0.01));
            double width = c.Old("Bandwidth")!.Value(8);
            Assert.That(width, Is.EqualTo(2.0).Within(0.01));
            double Bell(SuccessorBand b, double hz, double gainDb) => SuccessorConverter.CookbookBellDb(hz, b.Hz, gainDb, width, 44100, rule.AlphaMax);
            double g = c.Old(b900.Param)!.Value(213);
            Assert.That(g, Is.EqualTo(12.05).Within(0.01));
            var one = c.ConvertState(Raws(c, new() { [b900.Param] = 213 }));
            foreach (var s in rule.Sliders)
                Assert.That(Value(c, one, s.Param), Is.EqualTo(Bell(b900, s.Hz, g)).Within(0.051), s.Param);
            Assert.That(Value(c, one, "G 1 kHz"), Is.GreaterThan(Value(c, one, "G 800 Hz")), "903.9 Hz is nearer 1 kHz");
            Assert.That(Value(c, one, "Output"), Is.Zero.Within(0.05));
            Assert.That(one.Notes, Is.Empty);

            // Input gain is the output's; below −24 dB the output connections take the rest.
            var quiet = c.ConvertState(Raws(c, new() { ["Input gain."] = 60 }));
            double inputDb = c.Old("Input gain.")!.Value(60);
            Assert.That(Value(c, quiet, "Output") + quiet.OutputGainDb, Is.EqualTo(inputDb).Within(0.05));
            var silent = c.ConvertState(Raws(c, new() { ["Input gain."] = 10 }));
            Assert.That(Value(c, silent, "Output"), Is.EqualTo(-24).Within(0.05));
            Assert.That(silent.OutputGainDb, Is.EqualTo(c.Old("Input gain.")!.Value(10) + 24).Within(0.05));

            // Every band at +18 dB at the widest: the curve is far above the sliders' reach. It moves down as a whole (the
            // output takes the move, beyond +24 dB the connections), its middle kept and its ends clamped, with a note.
            var all = rule.Bands.ToDictionary(b => b.Param, _ => 255);
            all["Bandwidth"] = 15;
            var loud = c.ConvertState(Raws(c, all));
            var values = sliders.Select(n => Value(c, loud, n)).ToList();
            Assert.That(values.Max(), Is.LessThanOrEqualTo(18));
            Assert.That(values.Min(), Is.GreaterThanOrEqualTo(-18));
            Assert.That(Value(c, loud, "Output"), Is.EqualTo(24).Within(0.05));
            Assert.That(loud.OutputGainDb, Is.GreaterThan(0));
            // Every band +3 dB at the default width: the bands add up to +21.5 dB at 3 kHz, above the sliders' +18 dB; the curve
            // fits once moved down, and keeps its shape.
            var three = rule.Bands.ToDictionary(b => b.Param, _ => 149);
            var lifted = c.ConvertState(Raws(c, three));
            double shift = Value(c, lifted, "Output");
            Assert.That(shift, Is.EqualTo(3.5).Within(0.3), "the bands add up");
            foreach (var s in rule.Sliders)
            {
                double curve = rule.Bands.Sum(b => Bell(b, s.Hz, c.Old(b.Param)!.Value(149)));
                Assert.That(Value(c, lifted, s.Param) + shift, Is.EqualTo(curve).Within(0.1), s.Param);
            }
            Assert.That(lifted.Notes, Is.Empty);

            // Highpass: a Butterworth high-pass at 1.28 times the corner displayed, into the lowest sliders after the curve is
            // placed (Output stays); raw 0 is off. Deeper than the sliders reach, they stop at −18 dB, with a note.
            Assert.That(c.Old("Highpass")!.Value(0), Is.Zero);
            var cut = c.ConvertState(Raws(c, new() { ["Highpass"] = 64 }));
            double corner = c.Old("Highpass")!.Value(64);
            Assert.That(corner, Is.EqualTo(28.8).Within(0.3));
            foreach (var s in rule.Sliders)
                Assert.That(Value(c, cut, s.Param), Is.EqualTo(SuccessorConverter.ButterworthHighpassDb(s.Hz / corner)).Within(0.051), s.Param);
            Assert.That(Value(c, cut, "G 20 Hz"), Is.EqualTo(-7.2).Within(0.3), "measured −7.24 dB at 20 Hz");
            Assert.That(Value(c, cut, "Output"), Is.Zero.Within(0.05));
            Assert.That(cut.Notes, Is.Empty);
            var deep = c.ConvertState(Raws(c, new() { ["Highpass"] = 127 }));
            Assert.That(Value(c, deep, "G 20 Hz"), Is.EqualTo(-18).Within(0.05));
            Assert.That(Value(c, deep, "G 100 Hz"), Is.EqualTo(SuccessorConverter.ButterworthHighpassDb(100 / c.Old("Highpass")!.Value(127))).Within(0.051));
            Assert.That(deep.Notes, Has.Some.Contains("Highpass"));

            // Enable off bypasses Neo EQ.
            Assert.That(c.ConvertState(Raws(c, new() { ["Enable"] = 0 })).Bypass, Is.True);
        }

        [Test]
        public void AGridIsReadBetweenItsPointsHeldAtItsEdgesAndAConditionPicksItsRule()
        {
            // Two rules on one output, each for its own type; a two-way table in the logarithm and a one-way one in the value.
            var map = SuccessorMap.Parse("""
                {
                  "format": "rebuzz-successor/1", "from": "Jeskola Filter", "to": "Neo Filter",
                  "rules": [
                    { "kind": "grid", "x": "Cutoff", "y": "Resonance", "xs": [0, 64, 128], "ys": [0, 128], "values": [[100, 1000, 10000], [200, 2000, 20000]],
                      "curve": "log", "to": "Cutoff", "when": { "param": "Type", "is": ["lowpass", "highpass"] } },
                    { "kind": "grid", "x": "Cutoff", "xs": [0, 128], "values": [[50, 150]], "to": "Cutoff", "when": [{ "param": "Type", "is": "bandpass" }, { "param": "Bandwidth", "to": 63 }] },
                    { "kind": "grid", "x": "Cutoff", "xs": [0, 128], "values": [[1000, 1000]], "to": "Cutoff", "when": [{ "param": "Type", "is": "bandpass" }, { "param": "Bandwidth", "from": 64 }] },
                    { "kind": "grid", "x": "Resonance", "xs": [0, 128], "values": [[0.5, 8]], "curve": "log", "to": "Resonance" }
                  ],
                  "evidence": ["a test"]
                }
                """);
            Assert.That(map.Rules[0].Inputs, Is.EquivalentTo(new[] { "Cutoff", "Resonance", "Type" }));
            Assert.That(map.Rules[1].Inputs, Is.EquivalentTo(new[] { "Cutoff", "Type", "Bandwidth" }));
            var c = new SuccessorConverter(map, OldParams(map), SuccessorParam.FromManifest(All()["Neo Filter"]));
            Assert.That(c.Errors, Is.Empty);
            double Hz(int type, int cutoff, int res) => Value(c, c.ConvertState(Raws(c, new() { ["Type"] = type, ["Cutoff"] = cutoff, ["Resonance"] = res })), "Cutoff");
            Assert.That(Hz(0, 64, 0), Is.EqualTo(1000).Within(1), "a point");
            Assert.That(Hz(1, 32, 0), Is.EqualTo(Math.Sqrt(100 * 1000)).Within(0.5), "halfway in the logarithm");
            Assert.That(Hz(0, 32, 64), Is.EqualTo(Math.Sqrt(100 * 1000) * Math.Sqrt(2)).Within(0.5), "both ways");
            Assert.That(Hz(0, 128, 128), Is.EqualTo(20000).Within(20), "the corner");
            Assert.That(Hz(2, 64, 0), Is.EqualTo(100).Within(0.2), "the band-pass's own rule, in the value");
            var wide = c.ConvertState(Raws(c, new() { ["Type"] = 2, ["Cutoff"] = 64, ["Bandwidth"] = 100 }));
            Assert.That(Value(c, wide, "Cutoff"), Is.EqualTo(1000).Within(1), "both conditions: a range of values");
            var q = c.ConvertState(Raws(c, new() { ["Resonance"] = 64 }));
            Assert.That(Value(c, q, "Resonance"), Is.EqualTo(2).Within(0.02), "a table over one parameter");
            Assert.That(q.Notes, Is.Empty);
            // Held at its edges: a value past the table's last point reads the last.
            var held = SuccessorMap.Parse("""
                { "format": "rebuzz-successor/1", "from": "Jeskola Filter", "to": "Neo Filter", "evidence": ["a test"],
                  "rules": [ { "kind": "grid", "x": "Cutoff", "xs": [0, 64], "values": [[100, 400]], "curve": "log", "to": "Cutoff" } ] }
                """);
            var h = new SuccessorConverter(held, OldParams(held), SuccessorParam.FromManifest(All()["Neo Filter"]));
            Assert.That(Value(h, h.ConvertState(Raws(h, new() { ["Cutoff"] = 128 })), "Cutoff"), Is.EqualTo(400).Within(0.5));
            // A table whose rows don't match its axes is refused.
            Assert.That(() => SuccessorMap.Parse("""
                { "format": "rebuzz-successor/1", "from": "Jeskola Filter", "to": "Neo Filter",
                  "rules": [ { "kind": "grid", "x": "Cutoff", "y": "Resonance", "xs": [0, 64], "ys": [0, 128], "values": [[1, 2, 3], [1, 2, 3]], "to": "Cutoff" } ] }
                """), Throws.InstanceOf<FormatException>());
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
