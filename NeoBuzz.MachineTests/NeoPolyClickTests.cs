using NeoBuzz.Dsp;
using NeoBuzz.Poly;
using static NeoBuzz.MachineTests.AcidLines;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Poly renders without clicks and pops (AC2): the acid presets on five lines through both filters, and ten other presets on a chord and a mono line.</summary>
    [TestFixture]
    public class NeoPolyClickTests
    {
        const int Rate = Measure.Rate;

        static readonly Dictionary<string, Dictionary<int, int>> Presets = PresetTests.AllPresets()
            .Where(p => p.Machine == "Neo Poly")
            .ToDictionary(p => p.Preset, p => p.Values);

        public static readonly string[] AcidNames = Presets.Keys.Where(k => k.StartsWith("Acid")).ToArray();

        /// <summary>Ten presets that are not acid: pads, basses (mono and glide), leads, keys, a pluck.</summary>
        public static readonly string[] OtherNames =
        {
            "Pad — Warm Strings", "Pad — Evolving PWM", "Bass — Classic Sub", "Bass — Acid", "Bass — Reese",
            "Lead — Sync Scream", "Lead — Smooth Saw", "Keys — Poly Brass", "Bass — Rolling Saw Ladder", "Lead — Square Hook Mono",
        };

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        /// <summary>A square LFO at 1/16 notes switches this preset's amp: a hard chop by design, so its edges are the sound and not a defect.</summary>
        const string HardChop = "Bass — Wobble Sixteenth Chop";

        static string Pos(List<int> clicks, double step) => string.Join(" ", clicks.Select(c => (c / step).ToString("F2")));

        // ── the detector itself ─────────────────────────────────────────────

        [TestCase(31.0)] [TestCase(55.0)] [TestCase(110.0)] [TestCase(220.0)] [TestCase(440.0)] [TestCase(1000.0)] [TestCase(3000.0)]
        public void TheDetectorDoesNotFlagACleanSawsOwnEdges(double hz)
        {
            var osc = new BlepOscillator();
            osc.SetShape(2f, 0.5f);
            var x = new float[Rate];
            for (int i = 0; i < x.Length; i++) x[i] = 0.3f * osc.Next(hz / Rate, out _, out _);
            Assert.That(ClickDetector.Find(x), Is.Empty);
            osc.SetShape(3f, 0.3f);
            for (int i = 0; i < x.Length; i++) x[i] = 0.3f * osc.Next(hz / Rate, out _, out _);
            Assert.That(ClickDetector.Find(x), Is.Empty, "a pulse");
        }

        [Test]
        public void TheDetectorFindsAStepInALevelAPhaseJumpAndADropout()
        {
            var osc = new BlepOscillator();
            osc.SetShape(0f, 0.5f);                                            // a sine: no edges of its own, so a step in its level shows
            var x = new float[Rate];
            for (int i = 0; i < x.Length; i++) x[i] = 0.3f * osc.Next(110.0 / Rate, out _, out _);
            int at = 24000;
            while (Math.Abs(x[at]) < 0.25f) at++;                              // where the saw is loud, so a gain step is a step
            var stepped = (float[])x.Clone();
            for (int i = at; i < x.Length; i++) stepped[i] *= 1.5f;           // +3.5 dB at once
            Assert.That(ClickDetector.Find(stepped).Where(c => c > 100).ToList(), Has.Count.EqualTo(1), "gain step");
            var cut = (float[])x.Clone();
            for (int i = at; i < at + 40; i++) cut[i] = 0;                    // a hole under 1 ms: two edges, one click
            Assert.That(ClickDetector.Find(cut).Count(c => c > 100), Is.EqualTo(1), "dropout");
            var phase = new float[Rate];
            var o2 = new BlepOscillator();
            o2.SetShape(0f, 0.5f);
            for (int i = 0; i < phase.Length; i++) phase[i] = 0.3f * (float)Math.Sin(2 * Math.PI * 220.0 * (i + (i > 24000 ? 77 : 0)) / Rate);
            Assert.That(ClickDetector.Find(phase).Where(c => c > 100).ToList(), Has.Count.EqualTo(1), "a jump in the sine's phase");
        }

        // ── the lines ───────────────────────────────────────────────────────

        public static IEnumerable<TestCaseData> AcidCases()
        {
            foreach (var name in AcidNames)
                foreach (int filter in new[] { 6, 0 })
                    foreach (var line in new[] { "pack", "allslid", "allaccent", "legatonoslide", "firstnote" })
                        yield return new TestCaseData(name, filter, line).SetName($"{name} {(filter == 6 ? "Diode18" : "Ladder24")} {line}");
        }

        static (float[] L, float[] R, List<int> Clicks, List<int> Plain, double Seconds) RenderAcid(string name, int filter, string line, int block = 16, double floor = ClickDetector.Floor)
        {
            List<Ev> events;
            double seconds;
            Action<ParamValues> tweak = null;
            switch (line)
            {
                case "pack": events = Sequence(Pack, 2); seconds = LineSeconds(2); break;
                case "allslid": events = Sequence(AllSlid(Pack), 2); seconds = LineSeconds(2); break;
                case "allaccent": events = Sequence(AllAccented(Pack), 2); seconds = LineSeconds(2); break;
                case "legatonoslide": events = Sequence(Pack, 2); seconds = LineSeconds(2); tweak = v => v.Set(P.Slide, 0); break;
                case "busy": events = Sequence(Busy, 2); seconds = LineSeconds(2); break;
                default: events = AfterSilence(); seconds = 6.5; break;
            }
            var (l, r, _) = Render(Presets[name], filter, events, seconds, tweak, block);
            var (clicks, plain) = Clicks(Presets[name], filter, events, seconds, l, r, tweak, block, floor);
            return (l, r, clicks, plain, seconds);
        }

        [TestCaseSource(nameof(AcidCases))]
        public void AcidLinesRenderWithoutClicks(string name, int filter, string line)
        {
            var (l, r, clicks, plain, _) = RenderAcid(name, filter, line);
            Measure.Report($"clicks {name} {(filter == 6 ? "Diode18" : "Ladder24")} {line}: {clicks.Count} at steps {Pos(clicks, Step)} sizes {string.Join(" ", clicks.Select(c => Math.Max(ClickDetector.Size(l, c), ClickDetector.Size(r, c)).ToString("F3")))} plain {plain.Count}");
            Assert.That(clicks, Is.Empty, $"steps {Pos(clicks, Step)}");
        }

        public static IEnumerable<TestCaseData> OtherCases()
        {
            foreach (var name in OtherNames)
                foreach (var line in new[] { "chords", "mono" })
                    yield return new TestCaseData(name, line).SetName($"{name} {line}");
        }

        [TestCaseSource(nameof(OtherCases))]
        public void OtherPresetsRenderWithoutClicks(string name, string line)
        {
            var events = line == "chords" ? Chords() : MonoLine();
            double seconds = line == "chords" ? 3.0 : LineSeconds(2);
            var (l, r, _) = Render(Presets[name], -1, events, seconds);
            var (clicks, plain) = Clicks(Presets[name], -1, events, seconds, l, r);
            Measure.Report($"clicks {name} {line}: {clicks.Count} at {Pos(clicks, Step)} plain {plain.Count}");
            Assert.That(clicks, Is.Empty, $"at steps {Pos(clicks, Step)}");
        }

        [Test]
        public void TheOtherPresetsAreAMixOfModes()
        {
            var modes = OtherNames.Select(n => Presets[n].TryGetValue(P.VoiceMode, out int m) ? m : 0).Distinct().ToList();
            Measure.Report("voice modes in the ten other presets: " + string.Join(",", modes));
            Assert.That(OtherNames.All(Presets.ContainsKey));
        }

        // ── diagnosis (local): NEO_CLICK_DIAG="Acid — Squelch|6|pack|AccentLevel=0;Accent=0" prints the clicks; NEO_CLICK_DUMP=<dir> also writes the render ──

        [Test]
        public void DiagnoseOneRender()
        {
            string spec = Environment.GetEnvironmentVariable("NEO_CLICK_DIAG");
            if (string.IsNullOrEmpty(spec)) Assert.Ignore("local only");
            foreach (var one in spec.Split('@'))
            {
                var parts = one.Split('|');
                string name = parts[0];
                int filter = int.Parse(parts[1]);
                string line = parts[2];
                int block = parts.Length > 4 ? int.Parse(parts[4]) : 16;
                var tweaks = parts.Length > 3 && parts[3].Length > 0 ? parts[3].Split(';') : Array.Empty<string>();
                List<Ev> events;
                Dictionary<int, int> presetOverride = null;
                double seconds = LineSeconds(2);
                Action<ParamValues> tweak = v =>
                {
                    foreach (var t in tweaks)
                    {
                        var kv = t.Split('=');
                        int idx = (int)typeof(P).GetField(kv[0]).GetRawConstantValue();
                        v.Set(idx, Params.Table[idx].Raw(double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture)));
                    }
                };
                switch (line)
                {
                    case "pack": events = Sequence(Pack, 2); break;
                    case "allslid": events = Sequence(AllSlid(Pack), 2); break;
                    case "allaccent": events = Sequence(AllAccented(Pack), 2); break;
                    case "busy": events = Sequence(Busy, 2); break;
                    case "legatonoslide": events = Sequence(Pack, 2); var t0 = tweak; tweak = v => { t0(v); v.Set(P.Slide, 0); }; break;
                    case "glide":
                        events = Sequence(Pack.Select(n => n with { Accent = false }).ToArray(), 2, Rate / 10, 100 / 127f);
                        var t1 = tweak;
                        tweak = v => { t1(v); v.Set(P.VoiceMode, 1); v.Set(P.GlideMode, 0); v.Set(P.Glide, 250); v.Set(P.F1Type, 0); v.Set(P.F1Reso, 700); v.Set(P.F1Cutoff, 350); v.Set(P.F1Key, 1000); v.Set(P.F1Env, 650); Set(v, P.AmpA, 0.005); Set(v, P.AmpR, 0.12); };
                        presetOverride = new Dictionary<int, int>();
                        break;
                    case "chords": events = Chords(); seconds = 3; break;
                    case "mono": events = MonoLine(); break;
                    default: events = AfterSilence(); seconds = 6.5; break;
                }
                var preset = presetOverride ?? Presets[name];
                var (l, r, _) = Render(preset, filter, events, seconds, tweak, block);
                var (clicks, plain) = Clicks(preset, filter, events, seconds, l, r, tweak, block);
                TestContext.Out.WriteLine($"DIAG {one}: {clicks.Count} clicks at {Pos(clicks, Step)} (plain {plain.Count} at {Pos(plain, Step)})");
                string dump = Environment.GetEnvironmentVariable("NEO_CLICK_DUMP");
                if (!string.IsNullOrEmpty(dump))
                {
                    Directory.CreateDirectory(dump);
                    var mid = Mid(l, r);
                    using var w = new BinaryWriter(File.Create(Path.Combine(dump, $"{name.Replace(' ', '_')}-{filter}-{line}-{parts.ElementAtOrDefault(3)?.Replace(';', '_').Replace('=', '-')}.f32")));
                    foreach (var f in mid) w.Write(f);
                }
            }
        }

        /// <summary>The AC1 listening pack's renders (local, NEO_AC1_PACK=<its named/ folder>): what the user heard as clicks must be what this detector finds.</summary>
        [Test]
        public void TheDetectorFindsTheClicksOfTheAc1Pack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_AC1_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("local only: the AC1 pack is not committed");
            int total = 0;
            foreach (var file in Directory.GetFiles(dir, "*.wav").OrderBy(f => f))
            {
                var bytes = File.ReadAllBytes(file);
                var x = new float[(bytes.Length - 44) / 4];
                Buffer.BlockCopy(bytes, 44, x, 0, x.Length * 4);
                var clicks = ClickDetector.Find(x);
                total += clicks.Count;
                Measure.Report($"AC1 {Path.GetFileName(file)}: {clicks.Count} clicks at steps {Pos(clicks, Step)}");
            }
            Assert.That(total, Is.GreaterThan(60), "the AC1 pack had 5 to 20 clicks a render by the first measure");
        }

        // ── how far the zero goes: the busy line, other block sizes, a stricter floor, and every other factory preset ──

        [TestCaseSource(nameof(BusyCases))]
        public void TheBusyLineAndOtherBlockSizesRenderWithoutClicks(string name, int filter, int block)
        {
            var (_, _, clicks, plain, _) = RenderAcid(name, filter, "busy", block);
            Measure.Report($"clicks {name} {(filter == 6 ? "Diode18" : "Ladder24")} busy block {block}: {clicks.Count} (plain {plain.Count})");
            Assert.That(clicks, Is.Empty, $"steps {Pos(clicks, Step)}");
        }

        public static IEnumerable<TestCaseData> BusyCases()
        {
            foreach (var name in AcidNames)
                foreach (int filter in new[] { 6, 0 })
                    foreach (int block in new[] { 16, 64, 256 })
                        yield return new TestCaseData(name, filter, block).SetName($"{name} {(filter == 6 ? "Diode18" : "Ladder24")} busy block {block}");
        }

        [Test]
        public void AStricterFloorIsReportedNotGated()
        {
            int total = 0, cases = 0;
            foreach (var name in AcidNames)
                foreach (int filter in new[] { 6, 0 })
                    foreach (var line in new[] { "pack", "allslid", "allaccent", "legatonoslide", "firstnote", "busy" })
                    {
                        var (_, _, clicks, _, _) = RenderAcid(name, filter, line, 16, 0.01);
                        total += clicks.Count;
                        cases++;
                        if (clicks.Count > 0) Measure.Report($"floor 0.01: {name} {(filter == 6 ? "Diode18" : "Ladder24")} {line}: {clicks.Count} at steps {Pos(clicks, Step)}");
                    }
            Measure.Report($"floor 0.01 (half the gate's): {total} clicks in {cases} renders");
        }

        /// <summary>Every Neo Poly factory preset that is not acid, on a chord line and the mono line (the gate takes ten of them; this looks at all of them).</summary>
        [Test]
        public void EveryOtherFactoryPresetOnTheTwoLines()
        {
            int total = 0, chop = 0;
            foreach (var name in Presets.Keys.Where(k => !k.StartsWith("Acid")))
                foreach (var line in new[] { "chords", "mono" })
                {
                    var events = line == "chords" ? Chords() : MonoLine();
                    double seconds = line == "chords" ? 3.0 : LineSeconds(2);
                    var (l, r, _) = Render(Presets[name], -1, events, seconds);
                    var (clicks, plain) = Clicks(Presets[name], -1, events, seconds, l, r);
                    total += clicks.Count;
                    if (name == HardChop) chop += clicks.Count;
                    if (clicks.Count > 0 || plain.Count > 0) Measure.Report($"other preset {name} {line}: {clicks.Count} clicks at {Pos(clicks, Step)} (plain {plain.Count})");
                }
            Measure.Report($"every other factory preset, two lines: {total} clicks (all in {HardChop}, see below)");
            Assert.That(total - chop, Is.EqualTo(0), "no click in any other preset");
            Assert.That(chop, Is.GreaterThan(0), "the hard chop is still a hard chop: it is the effect (a square LFO switches the amp)");
        }

        /// <summary>The glide and legato paths of a non-acid patch: the mono line with each glide mode, in Mono and Legato, through the ladder and the SVF.</summary>
        [TestCase(1, 0, 0)] [TestCase(1, 1, 0)] [TestCase(1, 2, 0)] [TestCase(2, 0, 0)] [TestCase(2, 1, 0)] [TestCase(2, 2, 0)]
        [TestCase(2, 2, 2)] [TestCase(1, 1, 3)]
        public void GlideAndLegatoOnANonAcidPatchRenderWithoutClicks(int mode, int glideMode, int filter)
        {
            var events = Sequence(Pack.Select(n => n with { Accent = false }).ToArray(), 2, Rate / 10, 100 / 127f);
            double seconds = LineSeconds(2);
            Action<ParamValues> tweak = v =>
            {
                v.Set(P.VoiceMode, mode); v.Set(P.GlideMode, glideMode); v.Set(P.Glide, 250);
                v.Set(P.F1Type, filter); v.Set(P.F1Reso, 700); v.Set(P.F1Cutoff, 350); v.Set(P.F1Key, 1000); v.Set(P.F1Env, 650);
                Set(v, P.AmpA, 0.005); Set(v, P.AmpR, 0.12);
            };
            var (l, r, _) = Render(new Dictionary<int, int>(), -1, events, seconds, tweak);
            var (clicks, plain) = Clicks(new Dictionary<int, int>(), -1, events, seconds, l, r, tweak);
            Measure.Report($"glide case mode {mode} glide {glideMode} filter {filter}: {clicks.Count} clicks at {Pos(clicks, Step)} (plain {plain.Count})");
            Assert.That(clicks, Is.Empty, $"steps {Pos(clicks, Step)}");
        }

        // ── the acid character is still there ───────────────────────────────

        static double Centroid(float[] x, int from, int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = x[from + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            var mag = Measure.Spectrum(w, 0, n);
            double num = 0, den = 0;
            for (int k = 1; k < mag.Length; k++) { double f = (double)k * Rate / n; num += f * mag[k]; den += mag[k]; }
            return num / den;
        }

        /// <summary>The brightest 5 ms of the note's first 50 ms: the largest spectral centroid of the 256-sample frames, hopped by 64 (the accent's bite is a few milliseconds long: its decay is short).</summary>
        static double BrightestCentroid(float[] x)
        {
            double best = 0;
            for (int at = 0; at + 256 <= 2400; at += 64) best = Math.Max(best, Centroid(x, at, 256));
            return best;
        }

        [TestCaseSource(nameof(AcidPresetNames))]
        public void AnAccentedNoteIsLouderAtItsPeakAndBrighterThanThePlainNote(string name)
        {
            foreach (int filter in new[] { 6, 0 })
            {
                // One note, A1, held 0.3 s: plain (velocity 90) and accented (127), the preset's own Accent.
                float[] Note(float vel)
                {
                    var (l, r, _) = Render(Presets[name], filter, new List<Ev> { new(0, 0, 33, vel), new(Rate * 3 / 10, 0, -1, 0) }, 0.6);
                    return Mid(l, r);
                }
                var plain = Note(90 / 127f);
                var accent = Note(1f);
                double peakPlain = Measure.Peak(plain, 0, plain.Length), peakAccent = Measure.Peak(accent, 0, accent.Length);
                double db = Measure.Db(peakAccent / peakPlain);
                double cPlain = BrightestCentroid(plain), cAccent = BrightestCentroid(accent);
                Measure.Report($"accent {name} {(filter == 6 ? "Diode18" : "Ladder24")}: peak {db:+0.0;-0.0} dB, brightest 5 ms (spectral centroid) {cPlain:F0} Hz plain, {cAccent:F0} Hz accented");
                if (Environment.GetEnvironmentVariable("NEO_CLICK_DIAG") == "frames")
                    Measure.Report($"frames {name} {filter}: plain " + string.Join(" ", Enumerable.Range(0, 20).Select(i => Centroid(plain, i * 128, 256).ToString("F0"))) + " | accent " + string.Join(" ", Enumerable.Range(0, 20).Select(i => Centroid(accent, i * 128, 256).ToString("F0"))));
                Assert.That(db, Is.GreaterThanOrEqualTo(3.0), $"{name} {filter}: the accented peak");
                Assert.That(cAccent, Is.GreaterThan(cPlain), $"{name} {filter}: the accented note is brighter");
            }
        }

        public static IEnumerable<string> AcidPresetNames() => AcidNames;

        /// <summary>The time a pitch glide takes (s), from where the tracker sees the pitch cross 10 % and 90 % of an octave step: the 10-90 time over 0.8.</summary>
        static double GlideTime(float[] x, double hzFrom, double hzTo)
        {
            var tr = new PitchTracker(Rate, PitchRange.Voice, PitchQuality.LowLatency);
            var buf = new PitchEstimate[tr.MaxFrames(256)];
            double t10 = double.NaN, t90 = double.NaN;
            double lo = Math.Log2(hzFrom), hi = Math.Log2(hzTo);
            for (int pos = 0; pos < x.Length; pos += 256)
            {
                int got = tr.Process(x, pos, Math.Min(256, x.Length - pos), buf, 0);
                for (int i = 0; i < got; i++)
                {
                    var e = buf[i];
                    if (!e.Voiced) continue;
                    double period = e.RawF0 > 0 ? Rate / e.RawF0 : tr.TauMax / 2.0;
                    double t = (e.EndSample - (tr.Window + period) / 2) / Rate;
                    double frac = (Math.Log2(e.F0) - lo) / (hi - lo);
                    if (t < 0.45) continue;
                    if (double.IsNaN(t10) && frac >= 0.1) t10 = t;
                    if (double.IsNaN(t90) && frac >= 0.9) { t90 = t; break; }
                }
                if (!double.IsNaN(t90)) break;
            }
            return (t90 - t10) / 0.8;
        }

        [TestCaseSource(nameof(AcidPresetNames))]
        public void ASlideIsStillASlideMeasuredByThePitchTracker(string name)
        {
            // A3 into A4 (the voice plays an octave down in these presets: 110 Hz into 220 Hz), the second note on while the first is held.
            var events = new List<Ev> { new(Rate / 20, 0, 57, 90 / 127f), new(Rate * 45 / 100, 1, 69, 90 / 127f), new(Rate / 2, 0, -1, 0), new(Rate * 9 / 10, 1, -1, 0) };
            double want = Params.Table[P.SlideTime].Value(Presets[name][P.SlideTime]);
            // The preset's own voice with its filter wide open (the tracker follows the pitch, not the filter's resonance), and the preset as it sounds.
            var (l, r, _) = Render(Presets[name], 6, events, 1.0, v => { WideOpen(v); v.Set(P.O2Level, 0); v.Set(P.O3Level, 0); });
            double hzFrom = DspMath.NoteToHz(57 + 12 * (Presets[name].TryGetValue(P.O1Oct, out int o) ? Params.Table[P.O1Oct].Value(o) : 0));
            double hzTo = DspMath.NoteToHz(69 + 12 * (Presets[name].TryGetValue(P.O1Oct, out int o2) ? Params.Table[P.O1Oct].Value(o2) : 0));
            double measured = GlideTime(Mid(l, r), hzFrom, hzTo);
            Measure.Report($"slide {name}: Slide time {want * 1000:F0} ms, measured by the pitch tracker {measured * 1000:F1} ms ({(measured / want - 1) * 100:+0.0;-0.0} %)");
            Assert.That(measured, Is.EqualTo(want).Within(want * 0.2), name);
        }

        // ── hygiene ─────────────────────────────────────────────────────────

        [Test]
        public void TheAcidRendersHaveNoDenormalsOrNaNsAndTheTailGoesToExactSilence()
        {
            int denormals = 0, bad = 0;
            foreach (var name in AcidNames)
                foreach (int filter in new[] { 6, 0 })
                    foreach (var line in new[] { "pack", "allslid", "allaccent", "legatonoslide", "firstnote", "busy" })
                    {
                        var (l, r, _, _, _) = RenderAcid(name, filter, line);
                        // Two more seconds of silence after the line: the tail must reach exact zero, and no sample on the way may be subnormal.
                        foreach (var ch in new[] { l, r })
                        {
                            foreach (var v in ch)
                            {
                                if (!float.IsFinite(v)) bad++;
                                else if (v != 0 && Math.Abs(v) < 1.17549435e-38f) denormals++;
                            }
                            for (int i = ch.Length - 2400; i < ch.Length; i++) if (ch[i] != 0 && line != "firstnote") { bad++; break; }
                        }
                    }
            Assert.That(denormals, Is.EqualTo(0), "subnormal samples");
            Assert.That(bad, Is.EqualTo(0), "non-finite samples, or a tail that does not end in silence");
        }

        // ── the listening pack (local only: NEO_ACID_PACK2=<dir> dotnet test --filter WritesTheCleanListeningPack) ──

        /// <summary>24-bit PCM, mono, 48 kHz.</summary>
        static void WriteWav24(string path, float[] x)
        {
            using var w = new BinaryWriter(File.Create(path));
            int bytes = x.Length * 3;
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 3); w.Write((short)3); w.Write((short)24);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            foreach (var f in x)
            {
                int v = (int)Math.Round(Math.Clamp(f, -1f, 1f) * 8388607.0);
                w.Write((byte)(v & 0xFF)); w.Write((byte)((v >> 8) & 0xFF)); w.Write((byte)((v >> 16) & 0xFF));
            }
        }

        static (float[] Mono, int Clicks, int Plain) RenderMono(string name, int filter, Note[] line)
        {
            var events = Sequence(line, 2);
            var (l, r, _) = Render(Presets[name], filter, events, LineSeconds(2));
            var (clicks, plain) = Clicks(Presets[name], filter, events, LineSeconds(2), l, r);
            return (Mid(l, r), clicks.Count, plain.Count);
        }

        [Test]
        public void WritesTheCleanListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_ACID_PACK2");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("local only: set NEO_ACID_PACK2 to a folder to write the listening pack");
            Directory.CreateDirectory(Path.Combine(dir, "blind"));
            Directory.CreateDirectory(Path.Combine(dir, "named"));
            Assert.That(AcidNames.Length, Is.EqualTo(6));
            var acid = AcidNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();

            // The items in a seeded order; Diode 18 in slot A in exactly 3 of the 6 (a seeded draw of which items, not a coin per item).
            var rng = new Random(20261124);
            var order = Enumerable.Range(0, acid.Length).OrderBy(_ => rng.Next()).ToArray();
            var diodeFirst = Enumerable.Range(0, acid.Length).OrderBy(_ => rng.Next()).Take(acid.Length / 2).ToHashSet();
            var key = new System.Text.StringBuilder("item\tpreset\tslot A\tslot B\tgain applied to the ladder render (dB)\tclicks (diode, ladder)\n");
            int clicksTotal = 0;
            for (int item = 0; item < acid.Length; item++)
            {
                string name = acid[order[item]];
                var (diode, cd, pd) = RenderMono(name, 6, Pack);
                var (ladder, cl, pl) = RenderMono(name, 0, Pack);
                // The same loudness (RMS over the line) on both: the ladder render is brought to the diode's.
                double gain = Measure.Rms(diode, 0, diode.Length) / Math.Max(1e-9, Measure.Rms(ladder, 0, ladder.Length));
                var ladderMatched = ladder.Select(x => (float)(x * gain)).ToArray();
                double peak = Math.Max(Measure.Peak(diode, 0, diode.Length), Measure.Peak(ladderMatched, 0, ladderMatched.Length));
                float trim = peak > 0.89 ? (float)(0.89 / peak) : 1f;     // both together, so the pair stays matched
                bool diodeInA = diodeFirst.Contains(item);
                string n = (item + 1).ToString("00");
                var a = (diodeInA ? diode : ladderMatched).Select(x => x * trim).ToArray();
                var b = (diodeInA ? ladderMatched : diode).Select(x => x * trim).ToArray();
                WriteWav24(Path.Combine(dir, "blind", $"{n}_A.wav"), a);
                WriteWav24(Path.Combine(dir, "blind", $"{n}_B.wav"), b);
                string safe = name.Replace(" — ", " ").Replace(' ', '-');
                WriteWav24(Path.Combine(dir, "named", $"{safe}-diode18.wav"), diode);
                WriteWav24(Path.Combine(dir, "named", $"{safe}-ladder24.wav"), ladder);
                // The busier line, the preset as it ships (its own filter), alone.
                var (busy, cb, pb) = RenderMono(name, -1, Busy);
                WriteWav24(Path.Combine(dir, "named", $"{safe}-busy-line.wav"), busy);
                // Every file of the pack goes through the detector before it goes out.
                clicksTotal += cd + cl + cb;
                key.Append($"{n}\t{name}\t{(diodeInA ? "Diode 18" : "Ladder 24")}\t{(diodeInA ? "Ladder 24" : "Diode 18")}\t{Measure.Db(gain):F2}\t{cd},{cl}\n");
                Measure.Report($"pack item {n} {name}: diode in {(diodeInA ? "A" : "B")}, ladder gain {Measure.Db(gain):F2} dB, peak {Measure.Db(peak):F1} dBFS, clicks diode {cd} ladder {cl} busy {cb} (without the edge exclusion {pd} {pl} {pb})");
            }
            File.WriteAllText(Path.Combine(dir, "KEY-do-not-open-before-listening.txt"), key.ToString());
            Assert.That(diodeFirst.Count, Is.EqualTo(3));
            Assert.That(clicksTotal, Is.EqualTo(0), "the detector finds nothing in the pack");
        }
    }
}
