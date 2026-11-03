using System.Globalization;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Guides;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using V = ReBuzz.Agent.Model.ValueResolver;

namespace ReBuzz.AgentTests
{
    /// <summary>The pure parts of phase A1: manifests, levels, notes, WAV files, measurements, guides.</summary>
    [TestFixture]
    public class A1ModelTests
    {
        /// <summary>The repository root (walks up from the test binaries; NEO_BUZZ_ROOT overrides).</summary>
        static string RepoRoot()
        {
            string? env = Environment.GetEnvironmentVariable("NEO_BUZZ_ROOT");
            if (!string.IsNullOrEmpty(env)) return env;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "Machines")) && Directory.Exists(Path.Combine(dir.FullName, "ReBuzz.Agent")))
                    return dir.FullName;
            Assert.Inconclusive("repository root not found");
            return "";
        }

        static ParamInfo Info(ManifestParameter p) => new()
        {
            Name = p.Name, Index = p.Index, Group = p.Group, Description = p.Description,
            Min = p.Min, Max = p.Max, Default = p.Default, Mapping = p.Mapping,
            Describe = r => p.Mapping.Value(r, p.Min, p.Max).ToString(CultureInfo.InvariantCulture),
        };

        [Test]
        public void EveryNeoManifestParsesWithExactMappings()
        {
            // Not the profiled drafts of other machines (Machines/Manifests, DraftManifestTests).
            string drafts = Path.Combine(RepoRoot(), "Machines", "Manifests") + Path.DirectorySeparatorChar;
            var files = Directory.GetFiles(Path.Combine(RepoRoot(), "Machines"), "*" + MachineManifest.Suffix, SearchOption.AllDirectories)
                .Where(f => !f.StartsWith(drafts, StringComparison.Ordinal)).ToArray();
            // One per Neo machine: every machine folder with a Params.def (the core six, the mix and master batch, …).
            int machines = Directory.GetDirectories(Path.Combine(RepoRoot(), "Machines"), "Neo*").Count(d => File.Exists(Path.Combine(d, "Params.def")));
            Assert.That(machines, Is.AtLeast(10));
            Assert.That(files.Length, Is.EqualTo(machines), "one manifest per Neo machine");
            foreach (var file in files)
            {
                var m = MachineManifest.Load(file)!;
                Assert.That(Path.GetFileName(file), Is.EqualTo(m.Gear + MachineManifest.Suffix));
                Assert.That(m.Kind, Is.AnyOf("generator", "effect"));
                Assert.That(m.Summary, Does.Contain("Use it"), m.Gear + " has a summary that says when to use it");
                Assert.That(m.Parameters.Select(p => p.Index), Is.EqualTo(Enumerable.Range(0, m.Parameters.Count)));
                foreach (var p in m.Parameters)
                {
                    Assert.That(p.Default, Is.InRange(p.Min, p.Max), $"{m.Gear} {p.Name}");
                    if (p.Mapping.Kind is MappingKind.Enum)
                        Assert.That(p.Mapping.Names!.Count, Is.EqualTo(p.Max - p.Min + 1), $"{m.Gear} {p.Name}");
                    // Every raw value survives value → raw (the resolver lands on it exactly).
                    for (int raw = p.Min; raw <= p.Max; raw += Math.Max(1, (p.Max - p.Min) / 50))
                    {
                        double v = p.Mapping.Value(raw, p.Min, p.Max);
                        Assert.That(p.Mapping.Raw(v, p.Min, p.Max), Is.EqualTo(raw), $"{m.Gear} {p.Name} raw {raw} → {v}");
                    }
                }
            }
        }

        [Test]
        public void ManifestValuesResolveInUnits()
        {
            var poly = MachineManifest.Load(Path.Combine(RepoRoot(), "Machines", "NeoPoly", "Neo Poly" + MachineManifest.Suffix))!;
            Assert.That(poly.Gear, Is.EqualTo("Neo Poly"));
            Assert.That(poly.Kind, Is.EqualTo("generator"));
            Assert.That(poly.Tracks, Is.EqualTo(16));

            var volume = Info(poly.Find("Volume")!);
            int minus6 = V.Resolve(volume, JsonValue.Create("-6 dB"), volume.Default);
            Assert.That(volume.Mapping.Value(minus6, volume.Min, volume.Max), Is.EqualTo(-6).Within(0.03));
            Assert.That(V.Resolve(volume, JsonValue.Create("-inf"), volume.Default), Is.EqualTo(volume.Min));

            var glide = Info(poly.Find("glide")!);   // names match case-insensitively
            int g = V.Resolve(glide, JsonValue.Create("150 ms"), glide.Default);
            Assert.That(glide.Mapping.Value(g, glide.Min, glide.Max), Is.EqualTo(0.15).Within(0.005));

            var pw = Info(poly.Find("O1 PW")!);   // linear 2 … 98 %: percents, not fractions
            int p30 = V.Resolve(pw, JsonValue.Create("30%"), pw.Default);
            Assert.That(pw.Mapping.Value(p30, pw.Min, pw.Max), Is.EqualTo(30).Within(0.1));

            var voice = Info(poly.Find("Voice mode")!);
            Assert.That(V.Resolve(voice, JsonValue.Create("legato"), 0), Is.EqualTo(2));
            var bad = Assert.Throws<ToolError>(() => V.Resolve(voice, JsonValue.Create("Lgato"), 0));
            Assert.That(bad!.Message, Does.Contain("Did you mean \"Legato\""));
        }

        [Test]
        public void ReviewedEdgesOfValuesInUnits()
        {
            // Milliseconds in a manifest are kept in seconds, as the agent's input is.
            var verb = MachineManifest.Load(Path.Combine(RepoRoot(), "Machines", "NeoVerb", "Neo Verb" + MachineManifest.Suffix))!;
            var pre = Info(verb.Find("Pre-delay")!);
            int p20 = V.Resolve(pre, JsonValue.Create("20 ms"), pre.Default);
            Assert.That(pre.Mapping.Value(p20, pre.Min, pre.Max), Is.EqualTo(0.020).Within(0.0005));
            int p30 = V.Resolve(pre, JsonValue.Create("+10 ms"), p20, V.Mode.By);
            Assert.That(pre.Mapping.Value(p30, pre.Min, pre.Max), Is.EqualTo(0.030).Within(0.0005));

            // A finite dB never lands on the -inf end; a finite ratio never on ∞:1.
            var poly = MachineManifest.Load(Path.Combine(RepoRoot(), "Machines", "NeoPoly", "Neo Poly" + MachineManifest.Suffix))!;
            var volume = Info(poly.Find("Volume")!);
            int quiet = V.Resolve(volume, JsonValue.Create("-40 dB"), volume.Default);
            Assert.That(quiet, Is.EqualTo(volume.Min + 1));
            var times = Assert.Throws<ToolError>(() => V.Resolve(volume, JsonValue.Create(2), volume.Default, V.Mode.Times));
            Assert.That(times!.Message, Does.Contain("by \"+6 dB\""));
            var fromInf = Assert.Throws<ToolError>(() => V.Resolve(volume, JsonValue.Create("+6 dB"), volume.Min, V.Mode.By));
            Assert.That(fromInf!.Message, Does.Contain("set a value"));

            // Machines without a manifest: their own texts.
            string[] delays = { "Off", "1/16", "1/8", "1/4" };
            var sync = new ParamInfo { Name = "Sync", Min = 0, Max = 3, Default = 0, Describe = r => delays[r] };
            Assert.That(V.Resolve(sync, JsonValue.Create("1/8"), 0), Is.EqualTo(2), "a text starting with a digit is a choice");
            var time = new ParamInfo { Name = "Decay", Min = 0, Max = 100, Default = 0, Describe = r => r == 0 ? "0 ms" : $"{r * 10} ms" };
            Assert.That(V.Resolve(time, JsonValue.Create("5 ms"), 50), Is.EqualTo(1), "log search among positive values (10 ms is nearest, not 0)");
            var mix = new ParamInfo { Name = "Mix", Min = 0, Max = 100, Default = 0, Describe = r => r == 0 ? "Off" : $"{r}%" };
            Assert.That(V.Resolve(mix, JsonValue.Create("35%"), 0), Is.EqualTo(35), "the unit comes from the parameter, not its current text");
        }

        [Test]
        public void LevelsInDecibelsAndPan()
        {
            Assert.That(Levels.AmpToDb(Levels.UnityAmp), Is.EqualTo(0));
            Assert.That(Levels.DbToAmp(-6), Is.EqualTo(8211).Within(2));
            Assert.That(Levels.AmpToDb(Levels.DbToAmp(-6)), Is.EqualTo(-6).Within(0.01));
            Assert.That(Levels.DbToAmp(double.NegativeInfinity), Is.EqualTo(0));
            Assert.That(Levels.MaxDb, Is.EqualTo(12.04).Within(0.01));
            Assert.That(Levels.ParseDb(JsonValue.Create("-3 dB"), "gain_db"), Is.EqualTo(-3));
            Assert.That(Levels.ParseDb(JsonValue.Create("-inf"), "gain_db"), Is.EqualTo(double.NegativeInfinity));
            var loud = Assert.Throws<ToolError>(() => Levels.ParseDb(JsonValue.Create(20), "gain_db"));
            Assert.That(loud!.Message, Does.Contain("maximum of +12.0 dB"));
            Assert.That(Levels.UnitToPan(-1), Is.EqualTo(0));
            Assert.That(Levels.UnitToPan(1), Is.EqualTo(Levels.PanMax));
            Assert.That(Levels.PanToUnit(Levels.PanCenter), Is.EqualTo(0));
            Assert.That(Levels.PanText(-0.4), Is.EqualTo("left 40%"));
            Assert.That(Levels.Json(double.NegativeInfinity).ToJsonString(), Is.EqualTo("\"-inf\""));
        }

        [Test]
        public void NotesParseCheckAndReadBack()
        {
            var warnings = new List<string>();
            var notes = (JsonArray)JsonNode.Parse("""
                [ {"pitch": "C4", "start": 0, "length": 1, "velocity": 90},
                  {"pitch": 64, "start": 0, "length": 1},
                  {"pitch": "G4", "start": "1.3", "length": 0.5},
                  {"pitch": "A3", "start": 15.5, "length": 2} ]
                """)!;
            // 4 bars of 4 beats at 4 ticks per beat: 64 ticks.
            var list = NoteList.Parse(notes, 4, 4, 64, warnings);
            Assert.That(list.Select(n => n.Pitch), Is.EqualTo(new[] { 60, 64, 67, 57 }));
            Assert.That(list[2].Time, Is.EqualTo(2 * 4 * 240), "\"1.3\" is beat 2 of the pattern");
            Assert.That(list[1].Velocity, Is.EqualTo(100), "velocity defaults to 100");
            Assert.That(list[3].Duration, Is.EqualTo(240 * 2), "shortened at the pattern's end (beat 16)");
            Assert.That(warnings.Single(), Does.Contain("ran past the pattern's end"));

            var back = list.Select(n => NoteList.ToJson(n, 4)).ToList();
            Assert.That(back[2]["start"]!.GetValue<double>(), Is.EqualTo(2));
            Assert.That(back[2]["length"]!.GetValue<double>(), Is.EqualTo(0.5));
            Assert.That(back[0]["name"]!.GetValue<string>(), Is.EqualTo("C4"));

            var events = list.Select(n => (n.Time, n.Value, n.Duration));
            Assert.That(NoteList.FromEvents(events), Is.EqualTo(list), "MIDI column events round-trip");
            Assert.That(NoteList.Polyphony(list), Is.EqualTo(2));
            Assert.That(NoteList.Summary(list, 4), Does.StartWith("4 notes, A3–G4: C4 E4 at 0; G4 at 2; A3 at 15.5"));

            var late = (JsonArray)JsonNode.Parse("""[{"pitch": 60, "start": 16, "length": 1}]""")!;
            var e = Assert.Throws<ToolError>(() => NoteList.Parse(late, 4, 4, 64, warnings));
            Assert.That(e!.Message, Does.Contain("the pattern is 16 beats long"));
            var missing = (JsonArray)JsonNode.Parse("""[{"pitch": 60, "length": 1}]""")!;
            Assert.That(Assert.Throws<ToolError>(() => NoteList.Parse(missing, 4, 4, 64, warnings))!.Message, Does.StartWith("notes[0].start: missing"));
            var buzzName = (JsonArray)JsonNode.Parse("""[{"pitch": "C-5", "start": 0, "length": 1}]""")!;
            Assert.That(Assert.Throws<ToolError>(() => NoteList.Parse(buzzName, 4, 4, 64, warnings))!.Message, Does.Contain("C4"));

            var overlapping = NoteList.Parse((JsonArray)JsonNode.Parse("""[ {"pitch": 60, "start": 0, "length": 2}, {"pitch": 60, "start": 1, "length": 1} ]""")!, 4, 4, 64, warnings);
            Assert.That(NoteList.Clashes(overlapping, 4).Single(), Does.Contain("C4 at beat 1"));
        }

        [Test]
        public void WavFilesRoundTrip()
        {
            string path = Path.Combine(Path.GetTempPath(), $"agent-wav-{Guid.NewGuid():N}.wav");
            try
            {
                int frames = 4800;
                var x = new float[frames * 2];
                for (int i = 0; i < frames; i++)
                {
                    x[2 * i] = (float)Math.Sin(2 * Math.PI * 440 * i / 48000);
                    x[2 * i + 1] = -0.5f * x[2 * i];
                }
                Wav.WriteStereoFloat(path, x, frames, 48000);
                var a = Wav.Read(path);
                Assert.That(a.Rate, Is.EqualTo(48000));
                Assert.That(a.Frames, Is.EqualTo(frames));
                Assert.That(a.Seconds, Is.EqualTo(0.1).Within(1e-9));
                Assert.That(a.Left[100], Is.EqualTo(x[200]));
                Assert.That(a.Right![100], Is.EqualTo(x[201]));
                var e = Assert.Throws<ToolError>(() => Wav.Read(path + ".missing"));
                Assert.That(e!.Message, Does.StartWith("No file at"));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void MeasurementsComeWithReads()
        {
            int rate = 48000, n = rate * 12;
            var left = new float[n];
            // A 1 kHz sine at -20 dBFS reads -23 LUFS in stereo (EBU Tech 3341 case 1 is -23 dBFS → -23 LUFS... per channel).
            for (int i = 0; i < n; i++) left[i] = (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * i / rate));
            var audio = new StereoAudio { Left = left, Right = (float[])left.Clone(), Rate = rate };
            var m = AudioReport.Measure(audio, AudioReport.Metrics);
            Assert.That(m["silent"]!.GetValue<bool>(), Is.False);
            Assert.That(m["loudness"]!["integrated_lufs"]!.GetValue<double>(), Is.EqualTo(-20).Within(0.2));
            Assert.That(m["peaks"]!["true_peak_dbtp"]!.GetValue<double>(), Is.EqualTo(-20).Within(0.1));
            Assert.That(m["peaks"]!["clipped_samples"]!.GetValue<int>(), Is.EqualTo(0));
            var reads = m["reads"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
            Assert.That(reads, Has.Some.Contains("moderate"));
            Assert.That(reads, Has.Some.Contains("plenty of headroom"));
            Assert.That(AudioReport.Line(m), Does.Contain("-20 LUFS integrated"));

            var part = AudioReport.Slice(audio, 2, 4);
            Assert.That(part.Frames, Is.EqualTo(2 * rate));

            var silence = new StereoAudio { Left = new float[rate], Right = new float[rate], Rate = rate };
            var s = AudioReport.Measure(silence, AudioReport.Metrics);
            Assert.That(s["silent"]!.GetValue<bool>(), Is.True);
            Assert.That(s["loudness"]!["integrated_lufs"]!.GetValue<string>(), Is.EqualTo("-inf"));
            Assert.That(s["reads"]![0]!.GetValue<string>(), Does.Contain("arrangement"));

            for (int i = 0; i < n; i++) left[i] = (float)(1.2 * Math.Sin(2 * Math.PI * 1000 * i / rate));
            var hot = AudioReport.Measure(new StereoAudio { Left = left, Right = left, Rate = rate }, new[] { "peaks" });
            Assert.That(hot["loudness"], Is.Null, "only the metrics asked for");
            Assert.That(hot["reads"]![0]!.GetValue<string>(), Does.StartWith("Clipping"));
        }

        [Test]
        public void GuidesAreServedAsToolAndResources()
        {
            Assert.That(GuideLibrary.TopicNames, Is.EquivalentTo(new[] { "workflow", "concepts", "composition", "sound-design", "mixing", "mastering", "troubleshooting" }));
            foreach (var topic in GuideLibrary.TopicNames)
                Assert.That(GuideLibrary.Get(topic), Does.StartWith("# "), topic + " is embedded");
            Assert.That(GuideLibrary.Get("nonsense"), Is.Null);
            Assert.That(GuideLibrary.Resources().Select(r => r.Uri), Has.Member("rebuzz://guides/workflow"));

            var tools = new ToolRegistry();
            GuideLibrary.Register(tools);
            var server = new McpServer(new ServerInfo { Name = "t", Title = "t", Version = "1" }, tools, resources: GuideLibrary.Resources());
            var read = TestTools.CallTool(server, "guide", new JsonObject { ["topic"] = "workflow" });
            Assert.That(read.Error, Is.False, read.Text);
            Assert.That(read.Data["markdown"]!.GetValue<string>(), Does.Contain("render"));
            var bad = TestTools.CallTool(server, "guide", new JsonObject { ["topic"] = "nonsense" });
            Assert.That(bad.Error, Is.True, "unknown topics fail schema validation");
            Assert.That(bad.Text, Does.Contain("workflow"));
            var list = TestTools.CallTool(server, "guide", new JsonObject());
            Assert.That(list.Data["topics"]!.AsArray().Count, Is.EqualTo(7));
        }
    }
}
