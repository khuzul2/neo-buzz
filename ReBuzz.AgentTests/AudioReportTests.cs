using System.Text.Json.Nodes;
using NeoBuzz.Analysis;
using ReBuzz.Agent.Audio;
using static ReBuzz.AgentTests.Signals;

namespace ReBuzz.AgentTests
{
    /// <summary>The analysis facade (docs/specs/mcp-server.md §5.7): analyze's metrics, compare and analyze_mix as JSON with reads.</summary>
    [TestFixture]
    public class AudioReportTests
    {
        static StereoAudio Stereo(float[] l, float[]? r = null) => new() { Left = l, Right = r ?? (float[])l.Clone(), Rate = Rate };

        static List<string> Reads(JsonNode m) => m["reads"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();

        [Test]
        public void MetricsListsEveryMeasure()
        {
            Assert.That(AudioReport.Metrics, Is.EqualTo(new[] { "loudness", "peaks", "dynamics", "spectrum", "stereo", "pitch", "key", "tempo", "microdynamics", "tonal", "platform" }));
            Assert.That(AudioReport.Basic, Is.EqualTo(new[] { "loudness", "peaks" }));
        }

        [Test]
        public void AMelodyReadsItsNotesKeyAndTempo()
        {
            // An A minor arpeggio, a note per beat at 120 BPM (4 s), a little louder on the downbeats.
            int[] notes = { 57, 60, 64, 69, 64, 60, 57, 52 };
            var x = new float[(int)(4.2 * Rate)];
            // From 0.1 s: an onset needs sound to rise from something (as in the Session View, none at sample 0).
            for (int i = 0; i < notes.Length; i++) AddNote(x, notes[i], 0.1 + 0.5 * i, 0.45, amplitude: i % 4 == 0 ? 0.35 : 0.25);
            var m = AudioReport.Measure(Stereo(x), new[] { "pitch", "key", "tempo", "spectrum", "stereo", "dynamics" });
            var reads = Reads(m);
            TestContext.Out.WriteLine(string.Join("\n", reads));

            var pitch = m["pitch"]!;
            var heard = pitch["notes"]!.AsArray().Select(n => n!["note"]!.GetValue<string>()).ToList();
            Assert.That(heard, Is.EqualTo(new[] { "A3", "C4", "E4", "A4", "E4", "C4", "A3", "E3" }));
            Assert.That(Math.Abs(pitch["notes"]![0]!["cents"]!.GetValue<double>()), Is.LessThan(1));
            Assert.That(pitch["voiced_percent"]!.GetValue<double>(), Is.GreaterThan(80));
            Assert.That(m["key"]!["name"]!.GetValue<string>(), Is.EqualTo("A minor"));
            Assert.That(m["key"]!["chroma"]!["A"]!.GetValue<double>(), Is.GreaterThan(0.5));
            Assert.That(m["key"]!["chroma"]!["F#"]!.GetValue<double>(), Is.LessThan(0.2), "a note not played");
            Assert.That(m["tempo"]!["bpm"]!.GetValue<double>(), Is.EqualTo(120).Within(0.5));
            Assert.That(m["tempo"]!["onsets"]!.GetValue<int>(), Is.EqualTo(8));

            var spectrum = m["spectrum"]!;
            Assert.That(spectrum["bands_db"]!.AsObject().Select(p => p.Key), Is.EqualTo(Bands.Named.Select(b => b.Name)));
            Assert.That(spectrum["third_octave"]!["hz"]!.AsArray().Count, Is.EqualTo(31));
            Assert.That(spectrum["third_octave"]!["db"]!.AsArray().Count, Is.EqualTo(31));
            Assert.That(m["stereo"]!["mono"]!.GetValue<bool>(), Is.True);
            Assert.That(m["dynamics"]!["transients"]!.GetValue<int>(), Is.EqualTo(8));
            Assert.That(m["loudness"], Is.Null, "only the metrics asked for");
            Assert.That(reads, Has.Some.Contains("Key A minor"));
            Assert.That(reads, Has.Some.Contains("Tempo 120"));
            Assert.That(reads, Has.Some.Contains("Mono"));
            Assert.That(AudioReport.Line(m), Does.Contain("A minor"));
        }

        [Test]
        public void SpectrumAndStereoReadsNameTheProblem()
        {
            // Pink noise with the right channel's sub inverted: balanced tilt, out-of-phase lows.
            var pink = PeriodicNoise(-1, 0.1, 2.5);
            var sub = Sine(45, 0.2, pink.Length / (double)Rate);
            var m = AudioReport.Measure(Stereo(Mix(pink, sub), Mix(pink, Scale(sub, -1))), new[] { "spectrum", "stereo" });
            var reads = Reads(m);
            TestContext.Out.WriteLine(string.Join("\n", reads));
            Assert.That(m["spectrum"]!["tilt_db_per_octave"]!.GetValue<double>(), Is.EqualTo(0).Within(1), "pink noise plus one low sine");
            Assert.That(reads, Has.Some.Contains("Tilt"));
            Assert.That(reads, Has.Some.Contains("bright"));
            Assert.That(reads, Has.Some.Contains("sub +"), "the sine stands out of the slope in the sub band");
            Assert.That(m["stereo"]!["bands"]!["sub"]!["correlation"]!.GetValue<double>(), Is.LessThan(-0.5));
            Assert.That(reads, Has.Some.Contains("low end is wide or out of phase"));
        }

        [Test]
        public void CompareSaysWhatChanged()
        {
            var pink = PeriodicNoise(-1, 0.05, 2.5);
            var a = Stereo(pink);
            // Louder, brighter (a first difference adds +6 dB/oct) and wider (unrelated noise in the sides).
            var bright = new float[pink.Length];
            for (int i = 1; i < pink.Length; i++) bright[i] = 2 * pink[i] + 2 * (pink[i] - pink[i - 1]);
            var side = Noise(0.03, pink.Length / (double)Rate, seed: 4);
            var b = Stereo(Mix(bright, side), Mix(bright, Scale(side, -1)));
            var c = AudioReport.Compare(a, b, "before", "after");
            var reads = Reads(c);
            TestContext.Out.WriteLine(string.Join("\n", reads));
            Assert.That(c["names"]!["b"]!.GetValue<string>(), Is.EqualTo("after"));
            double louder = c["loudness"]!["integrated_lufs"]!["difference"]!.GetValue<double>();
            Assert.That(louder, Is.GreaterThan(5));
            Assert.That(c["spectrum"]!["bands_level_matched_db"]!["air"]!.GetValue<double>(), Is.GreaterThan(2));
            Assert.That(c["spectrum"]!["bands_level_matched_db"]!["low"]!.GetValue<double>(), Is.LessThan(-2));
            Assert.That(c["spectrum"]!["tilt_db_per_octave"]!["difference"]!.GetValue<double>(), Is.GreaterThan(0.5));
            Assert.That(c["stereo"]!["width"]!["a"]!.GetValue<double>(), Is.EqualTo(0));
            Assert.That(reads[0], Does.StartWith("after is"));
            Assert.That(reads[0], Does.Contain("LU louder than before"));
            Assert.That(reads, Has.Some.Contains("more air"));
            Assert.That(reads, Has.Some.Contains("less "));
            Assert.That(reads, Has.Some.Contains("brighter"));
            Assert.That(reads, Has.Some.Contains("wider"));

            var same = AudioReport.Compare(a, Stereo(pink));
            Assert.That(Reads(same).Last(), Does.StartWith("No measurable difference"));
            Assert.That(same["spectrum"]!["bands_db"]!["mid"]!["difference"]!.GetValue<double>(), Is.EqualTo(0));
        }

        /// <summary>A small mix with two planted problems: a kick and a bass on the same low notes, and a pad with one channel's polarity flipped.</summary>
        internal static (List<(string Name, StereoAudio Audio)> Tracks, StereoAudio Master) PlantedMix(double seconds = 4)
        {
            var kick = Stereo(Kick(seconds, 0.5, 0.25));
            var bass = Stereo(Sine(55, 0.125, seconds));
            var padL = new float[(int)(seconds * Rate)];
            foreach (int p in new[] { 57, 60, 64 }) AddNote(padL, p, 0, seconds, amplitude: 0.04, maxHarmonics: 10);
            var pad = new StereoAudio { Left = padL, Right = Scale(padL, -1), Rate = Rate };
            var hatNoise = Noise(0.2, seconds, seed: 11);
            var hat = new float[hatNoise.Length];
            for (int i = 2; i < hat.Length; i++) hat[i] = 0.12f * (hatNoise[i] - 2 * hatNoise[i - 1] + hatNoise[i - 2]) * (((i / (Rate / 4)) % 2 == 0) ? 1 : 0.2f);
            var hats = Stereo(hat, Scale(hat, 0.8));
            var tracks = new List<(string, StereoAudio)> { ("Kick", kick), ("Bass", bass), ("Pad", pad), ("Hats", hats) };
            var master = new StereoAudio
            {
                Left = Mix(kick.Left, bass.Left, pad.Left, hats.Left),
                Right = Mix(kick.Right!, bass.Right!, pad.Right!, hats.Right!),
                Rate = Rate,
            };
            return (tracks, master);
        }

        [Test]
        public void MixReportFindsPlantedMaskingAndPhase()
        {
            var (tracks, master) = PlantedMix(3);
            var r = AudioReport.MixReport(tracks, master);
            TestContext.Out.WriteLine(string.Join("\n", r["problems"]!.AsArray().Select(p => $"{p!["kind"]} {p["severity"]}: {p["text"]}")));
            TestContext.Out.WriteLine(r["masking"]!.ToJsonString());
            Assert.That(r["tracks"]!.AsArray().Count, Is.EqualTo(4));
            var kick = r["tracks"]![0]!;
            Assert.That(kick["name"]!.GetValue<string>(), Is.EqualTo("Kick"));
            Assert.That(kick["main_band"]!.GetValue<string>(), Is.EqualTo("low").Or.EqualTo("sub"));
            Assert.That(kick["crest_db"]!.GetValue<double>(), Is.GreaterThan(6));
            Assert.That(r["tracks"]![2]!["correlation"]!.GetValue<double>(), Is.LessThan(-0.99), "the pad's channels oppose");

            var first = r["masking"]![0]!;
            Assert.That(first["tracks"]!.AsArray().Select(t => t!.GetValue<string>()), Is.EquivalentTo(new[] { "Kick", "Bass" }));
            Assert.That(first["bands"]![0]!["from_hz"]!.GetValue<double>(), Is.EqualTo(20));
            Assert.That(first["level"]!.GetValue<string>(), Is.EqualTo("strong"));

            var problems = r["problems"]!.AsArray().Select(p => (Kind: p!["kind"]!.GetValue<string>(), Text: p["text"]!.GetValue<string>())).ToList();
            Assert.That(problems.Any(p => p.Kind == "masking" && p.Text.Contains("Kick") && p.Text.Contains("Bass")), Is.True, "the planted kick/bass masking");
            Assert.That(problems.Any(p => p.Kind == "phase" && p.Text.Contains("Pad")), Is.True, "the planted phase problem");
            Assert.That(problems.Count, Is.LessThanOrEqualTo(6));
            Assert.That(r["headroom_db"]!.GetValue<double>(), Is.GreaterThan(0));
            Assert.That(r["master"]!["loudness"]!["integrated_lufs"], Is.Not.Null);
            Assert.That(Reads(r)[1], Does.StartWith("Biggest problem"));

            // Without the plants: no masking or phase problem.
            var clean = AudioReport.MixReport(new[] { tracks[1], tracks[3] }, Stereo(Mix(tracks[1].Audio.Left, tracks[3].Audio.Left), Mix(tracks[1].Audio.Right!, tracks[3].Audio.Right!)));
            var kinds = clean["problems"]!.AsArray().Select(p => p!["kind"]!.GetValue<string>()).ToList();
            Assert.That(kinds.Contains("masking"), Is.False, string.Join(", ", kinds));
            Assert.That(kinds.Contains("phase"), Is.False, string.Join(", ", kinds));
        }
    }
}
