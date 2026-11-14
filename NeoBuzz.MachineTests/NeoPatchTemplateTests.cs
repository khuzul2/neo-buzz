using System.Globalization;
using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Patch;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Patch NP3 (docs/specs/neo-patch.md §4.7, §5): the templates (one for each Neo Learn method the nodes can build, and the empty patch with sound in
    /// three steps) and the factory presets built in code. Each sounds at -22 dB (the chord, loudest 50 ms RMS), loads through the editor's model as one
    /// history step, and the presets' file (Machines/NeoPatch/Presets.txt) is what the code builds: NEOPATCH_WRITE=1 rewrites it (and prints the templates'
    /// Out levels to paste into PatchLessons.Levels), otherwise the test compares.
    /// </summary>
    [TestFixture]
    public class NeoPatchTemplateTests
    {
        const double Target = -22.0;

        static float[] Render(PatchGraph g, int volumeRaw = -1, int seconds = 1)
        {
            for (int t = 0; t < NeoBuzz.DyeEssence.TableBank.Count; t++) NeoBuzz.DyeEssence.TableBank.GetNow(t);
            for (int s = 0; s < NeoBuzz.DyeEssence.SampleSources.Count; s++) NeoBuzz.DyeEssence.SampleSources.GetNow(s);
            var v = new ParamValues(Params.Table);
            if (volumeRaw >= 0) v.Set(P.Volume, volumeRaw);
            var e = new PatchEngine(v, Measure.Rate);
            Assert.That(e.SetGraph(g), Is.Null, "the patch compiles");
            return PresetTests.RenderSynth(e, (en, l, r, n) => ((PatchEngine)en).Process(l, r, n), Measure.Rate * seconds);
        }

        static double Loudest(float[] y)
        {
            double loudest = 0;
            for (int at = 0; at + 2400 <= y.Length * 2 / 3; at += 1200) loudest = Math.Max(loudest, Measure.Rms(y, at, 2400));
            return Measure.Db(loudest);
        }

        static double Peak(float[] y) => Measure.Db(Measure.Peak(y, 0, y.Length));

        static string RepoFile(params string[] parts)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "Machines")) && Directory.Exists(Path.Combine(dir.FullName, "ReBuzz.Agent")))
                    return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            Assert.Inconclusive("repository root not found");
            return "";
        }

        static PatchGraph Finished(int i) => i == 0 ? PatchLessons.StartFinished() : PatchLessons.Build(i);

        // ------------------------------------------------------------------ templates

        [Test]
        public void TheSlugsAreWhatThePatchToolsAndTheGuideSay()
        {
            // ReBuzz.AgentTests checks the same list against PatchTools.Templates and the build-a-synth guide.
            Assert.That(PatchLessons.Slugs, Is.EqualTo(new[] { "start", "subtractive", "fm-2op", "fm-4op", "am-ring", "wavefolder", "sync-lead", "plucked-string", "vector-crossfade", "sample-loop" }));
        }

        [Test]
        public void TheTemplatesAreNamedAndBuildWithoutCyclesAndEveryNodeIsHeard()
        {
            Assert.That(PatchLessons.Count, Is.EqualTo(10));
            Assert.That(PatchLessons.Titles.Length, Is.EqualTo(PatchLessons.Count));
            Assert.That(PatchLessons.Summaries.Length, Is.EqualTo(PatchLessons.Count));
            Assert.That(PatchLessons.Slugs.Distinct().Count(), Is.EqualTo(PatchLessons.Count));
            for (int i = 0; i < PatchLessons.Count; i++)
            {
                Assert.That(PatchLessons.Find(PatchLessons.Slugs[i]), Is.EqualTo(i));
                Assert.That(PatchLessons.Find(PatchLessons.Titles[i].ToUpperInvariant()), Is.EqualTo(i), "by title, any case");
                var g = Finished(i);
                Assert.That(g.Nodes.Count, Is.InRange(5, NodeInfo.Cap), PatchLessons.Slugs[i]);
                Assert.That(PatchCompiler.TryOrder(g, out _, out _), Is.True, PatchLessons.Slugs[i] + ": orders");
                var rt = new PatchRuntime(Measure.Rate);
                Assert.That(rt.SetGraph(g), Is.Null, PatchLessons.Slugs[i]);
                var scope = rt.Current.Scope;
                foreach (var n in g.Nodes) Assert.That(scope.ContainsKey(n.Id), Is.True, $"{PatchLessons.Slugs[i]}: {n.Info.Name} {n.Id} leads to the output");
                if (i > 0) Assert.That(g.Macros.Count, Is.GreaterThanOrEqualTo(2), PatchLessons.Slugs[i] + ": macros");
                // Every mapping is centred on the node's own value, so the macros at 50 % leave the template as built.
                foreach (var m in g.Macros)
                {
                    var n = g.Find(m.NodeId);
                    Assert.That((m.Min + m.Max) / 2, Is.EqualTo(n.Controls[m.Control]).Within(1e-3 * Math.Max(1, Math.Abs(n.Controls[m.Control]))), $"{PatchLessons.Slugs[i]}: {n.Info.Name} {n.Info.Controls[m.Control].Name}");
                }
            }
            Assert.That(PatchLessons.Find("nope"), Is.EqualTo(-1));
            Assert.That(PatchLessons.Find(null), Is.EqualTo(-1));
        }

        [Test]
        public void EveryTemplateSoundsAtMinus22DbAndSettlesBelowFullScale()
        {
            if (Environment.GetEnvironmentVariable("NEOPATCH_WRITE") == "1")
            {
                var levels = new StringBuilder();
                for (int i = 0; i < PatchLessons.Count; i++)
                {
                    var g = PatchLessons.Build(i, calibrated: false);
                    if (i == 0) g = PatchLessons.StartFinished();
                    float level = 1f;
                    for (int pass = 0; pass < 3; pass++)
                    {
                        foreach (var n in g.Nodes) if (n.Kind == NodeKind.Out) n.Set(NodeInfo.OutLevel, level);
                        double now = Loudest(Render(g));
                        level = (float)Math.Round(level * Math.Pow(10, (Target - now) / 20), 4);
                    }
                    levels.Append(level.ToString("0.####", CultureInfo.InvariantCulture)).Append("f, ");
                }
                TestContext.Out.WriteLine("Levels = { " + levels.ToString().TrimEnd(' ', ',') + " }");
            }
            for (int i = 0; i < PatchLessons.Count; i++)
            {
                var y = Render(Finished(i));
                double level = Loudest(y), peak = Peak(y);
                TestContext.Out.WriteLine($"{PatchLessons.Slugs[i]}: chord {level:+0.0;-0.0} dB RMS (loudest 50 ms), peak {peak:+0.0;-0.0} dBFS");
                Assert.That(y.All(float.IsFinite), Is.True, PatchLessons.Slugs[i]);
                Assert.That(level, Is.EqualTo(Target).Within(0.3), PatchLessons.Slugs[i] + ": level");
                Assert.That(peak, Is.LessThan(-3.0), PatchLessons.Slugs[i] + ": peak");
            }
        }

        [Test]
        public void TheEmptyPatchIsSilentUntilItsThreeWiresAreMade()
        {
            var g = PatchLessons.Build(0);
            Assert.That(g.Wires.Count, Is.EqualTo(2), "the gate to the envelope and the envelope to the VCA are made; the learner makes three");
            Assert.That(Loudest(Render(g)), Is.LessThan(-100.0), "no sound before the steps");

            // The three steps through the editor's model: each is one history step, and the third makes it play at -22 dB.
            var v = new ParamValues(Params.Table);
            var e = new PatchEngine(v, Measure.Rate);
            e.SetGraph(g);
            var hist = new Steps();
            var model = new PatchEditModel(e, hist);
            Node Of(NodeKind k) => e.Graph.Nodes.First(n => n.Kind == k);
            int made = 0;
            foreach (var (from, fp, to, tp) in PatchLessons.StartSteps)
            {
                Assert.That(model.Connect(Of(from).Id, fp, Of(to).Id, tp).Ok, Is.True);
                Assert.That(hist.List.Count, Is.EqualTo(++made), "one step for each wire");
            }
            var y = PresetTests.RenderSynth(e, (en, l, r, n) => ((PatchEngine)en).Process(l, r, n), Measure.Rate);
            Assert.That(Loudest(y), Is.EqualTo(Target).Within(0.3));
        }

        [Test]
        public void ALoadedTemplateIsOneHistoryStepAndUndoPutsTheOldPatchBack()
        {
            var v = new ParamValues(Params.Table);
            var e = new PatchEngine(v, Measure.Rate);
            e.SetGraph(PatchPresets.Build(0));
            string before = e.Text();
            var hist = new Steps();
            var model = new PatchEditModel(e, hist);
            Assert.That(model.LoadGraph(PatchLessons.Build(2), "Load template FM"), Is.True);
            Assert.That(hist.List.Count, Is.EqualTo(1));
            Assert.That(e.Graph.Name, Is.EqualTo(PatchLessons.Titles[2]));
            Assert.That(hist.List[0].Before, Is.EqualTo(before));
            e.SetPatch(hist.List[0].Before);
            Assert.That(e.Text(), Is.EqualTo(before));
        }

        [Test]
        public void ThePluckedStringIsInTuneAndRingsOut()
        {
            // A3 (note 57, 220 Hz): the delay's time follows the pitch; the burst of noise is the pluck.
            var g = PatchLessons.Build(7);
            for (int t = 0; t < NeoBuzz.DyeEssence.TableBank.Count; t++) NeoBuzz.DyeEssence.TableBank.GetNow(t);
            var v = new ParamValues(Params.Table);
            var e = new PatchEngine(v, Measure.Rate);
            e.SetGraph(g);
            e.SetSampleRate(Measure.Rate);
            e.NoteOn(0, 57, 0.8f);
            var l = new float[256]; var r = new float[256];
            var y = new float[Measure.Rate];
            for (int pos = 0; pos + 256 <= y.Length; pos += 256) { e.Process(l, r, 256); Array.Copy(l, 0, y, pos, 256); }
            double f = Measure.PeakFrequency(y, 4800, 32768);
            TestContext.Out.WriteLine($"plucked string, note 57: strongest partial {f:F1} Hz");
            double fundamentalErr = 12 * Math.Log2(Math.Max(1, f) / 220.0);
            // The strongest partial is the fundamental or a low harmonic of it: it sits on a multiple of 220 Hz within 0.3 semitone.
            double k = Math.Round(f / 220.0);
            Assert.That(k, Is.GreaterThanOrEqualTo(1));
            Assert.That(12 * Math.Log2(f / (220.0 * k)), Is.EqualTo(0).Within(0.3), "in tune");
            Assert.That(Measure.Rms(y, 4800 + 24000, 4800), Is.GreaterThan(1e-4), "still ringing half a second after the pluck");
            Assert.That(y.All(float.IsFinite), Is.True);
        }

        [Test]
        public void EveryTemplateAndNewPresetRendersTheSameBitsTwice()
        {
            for (int i = 0; i < PatchLessons.Count; i++)
                Assert.That(Render(Finished(i)), Is.EqualTo(Render(Finished(i))), PatchLessons.Slugs[i]);
            for (int i = 8; i < PatchPresets.Names.Length; i++)
                Assert.That(Render(PatchPresets.Build(i)), Is.EqualTo(Render(PatchPresets.Build(i))), PatchPresets.Names[i]);
        }

        // ------------------------------------------------------------------ presets

        sealed class Steps : IPatchHistory
        {
            public readonly List<(string Name, string Before, string After)> List = new();
            public void Record(string name, string before, string after) => List.Add((name, before, after));
        }

        [Test]
        public void ThereAreTwentyPresetsAtLeastWithPlainNames()
        {
            Assert.That(PatchPresets.Names.Length, Is.GreaterThanOrEqualTo(20));
            Assert.That(PatchPresets.Comments.Length, Is.EqualTo(PatchPresets.Names.Length));
            Assert.That(PatchPresets.Names.Distinct().Count(), Is.EqualTo(PatchPresets.Names.Length));
            var banned = new System.Text.RegularExpressions.Regex(@"\b(?:Serum|Massive|Vital|Moog|Roland|Yamaha|Korg|Sylenth|Diva|Juno|Jupiter|Prophet|Minimoog|DX7|TB-303|Oberheim|Fairlight|Mellotron|Wurlitzer|Rhodes|Hammond|Fender)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            for (int i = 0; i < PatchPresets.Names.Length; i++)
            {
                Assert.That(PatchPresets.Names[i], Does.Match(@"^[A-Z][A-Za-z]+ — [A-Z][A-Za-z0-9 ]+$"), "Group — Name");
                Assert.That(banned.IsMatch(PatchPresets.Names[i] + " " + PatchPresets.Comments[i]), Is.False, PatchPresets.Names[i]);
                var g = PatchPresets.Build(i);
                Assert.That(g.Name, Is.EqualTo(PatchPresets.Names[i]));
                Assert.That(g.Macros.Count, Is.GreaterThanOrEqualTo(2), PatchPresets.Names[i]);
                var rt = new PatchRuntime(Measure.Rate);
                Assert.That(rt.SetGraph(g), Is.Null, PatchPresets.Names[i]);
            }
        }

        /// <summary>The Volume parameter's raw value nearest to a dB value (the file keeps dB; the machine keeps raw).</summary>
        static int RawOf(double db)
        {
            int best = 0; double err = double.MaxValue;
            for (int r = 0; r <= 1000; r++) { double e = Math.Abs(VolumeDb(r) - db); if (e < err) { err = e; best = r; } }
            return best;
        }

        static double VolumeDb(int raw)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Volume, raw);
            return v.Value(P.Volume);
        }

        /// <summary>What Presets.txt says about preset i (from the 9th on): its text, comment and the Volume that puts the chord at -22 dB.</summary>
        static string Entry(int i)
        {
            var g = PatchPresets.Build(i);
            string text = PatchText.Save(g);
            // The Volume (dB, one decimal as the file keeps it) that brings the chord's loudest 50 ms closest to -22 dB RMS: Newton steps (the safety limiter
            // bends the loud ones), then the one-decimal values around where it landed.
            double db = VolumeDb(739);
            for (int k = 0; k < 8; k++)
            {
                int r = RawOf(db);
                double l = Loudest(Render(PatchPresets.Build(i), r));
                if (Math.Abs(l - Target) < 0.02) break;
                db = VolumeDb(r) + (Target - l);
            }
            double bestDb = double.NaN, bestErr = double.MaxValue;
            for (double cand = Math.Round(db, 1) - 0.2; cand <= Math.Round(db, 1) + 0.2001; cand += 0.1)
            {
                double c = Math.Round(cand, 1);
                double l = Loudest(Render(PatchPresets.Build(i), RawOf(c)));
                if (Math.Abs(l - Target) < bestErr) { bestErr = Math.Abs(l - Target); bestDb = c; }
            }
            var sb = new StringBuilder();
            sb.Append("preset \"").Append(PatchPresets.Names[i]).Append("\"\n");
            sb.Append("comment \"").Append(PatchPresets.Comments[i]).Append("\"\n");
            sb.Append("state Patch = ").Append(text).Append('\n');
            sb.Append("Volume = ").Append(bestDb.ToString("0.0", CultureInfo.InvariantCulture)).Append("\n\n");
            return sb.ToString();
        }

        [Test]
        public void ThePresetFileIsWhatTheCodeBuildsAndEveryNewOneIsAtMinus22Db()
        {
            string file = RepoFile("Machines", "NeoPatch", "Presets.txt");
            string text = File.ReadAllText(file);
            int cut = text.IndexOf("preset \"" + PatchPresets.Names[8] + "\"", StringComparison.Ordinal);
            string head = cut < 0 ? text.TrimEnd('\n') + "\n\n" : text.Substring(0, cut);
            var sb = new StringBuilder(head);
            for (int i = 8; i < PatchPresets.Names.Length; i++) sb.Append(Entry(i));
            string expected = sb.ToString().TrimEnd('\n') + "\n";
            if (Environment.GetEnvironmentVariable("NEOPATCH_WRITE") == "1") { File.WriteAllText(file, expected); text = expected; }
            Assert.That(text.TrimEnd('\n') + "\n", Is.EqualTo(expected), "Presets.txt is stale: NEOPATCH_WRITE=1 dotnet test --filter NeoPatchTemplateTests, then python3 tools/gen-machine-presets.py Machines/NeoPatch");
            for (int i = 8; i < PatchPresets.Names.Length; i++)
            {
                // At the file's Volume the chord is at -22 dB: PresetTests.EveryPresetSoundsSensible renders the file's own presets; here the same with the Volume read back.
                var line = text.Split('\n').SkipWhile(l => l != "preset \"" + PatchPresets.Names[i] + "\"").First(l => l.StartsWith("Volume = "));
                double db = double.Parse(line.Substring(9), CultureInfo.InvariantCulture);
                int raw = RawOf(db);
                var yy = Render(PatchPresets.Build(i), raw);
                double level = Loudest(yy);
                TestContext.Out.WriteLine($"{PatchPresets.Names[i]}: Volume {db:0.0} dB, chord {level:+0.0;-0.0} dB RMS, peak {Peak(yy):+0.0;-0.0} dBFS");
                Assert.That(level, Is.EqualTo(Target).Within(0.15), PatchPresets.Names[i]);
                Assert.That(Peak(yy), Is.LessThan(-3.0), PatchPresets.Names[i]);
            }
        }
    }
}
