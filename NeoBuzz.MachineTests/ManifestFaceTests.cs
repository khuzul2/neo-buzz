using NeoBuzz.Editor;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The manifest editor's toolkit-free face (docs/specs/mcp-server.md §12.11, U4): sections, controls,
    /// envelopes and safe ranges planned from a machine's parameters, and the response plot honest: a curve
    /// only where the design is known, checked here against that design run in the time domain (the
    /// machines' own filter code, copied from their sources as the oracle), markers otherwise.
    /// </summary>
    [TestFixture]
    public class ManifestFaceTests
    {
        const double Rate = 48000;
        static readonly NeoColor Accent = NeoColor.FromRgb(0x6F, 0xA8, 0xF0);

        /// <summary>A parameter whose physical value is a straight line over its raw range (A + t·(B − A)).</summary>
        static FaceParam Linear(int slot, string label, string group, string role, int min, int max, int def, double a, double b, string dim = "",
            bool track = false, FaceSafeRange safe = null, bool fraction = false) => new FaceParam
            {
                Slot = slot,
                Name = label,
                Label = label,
                Group = group,
                Role = role,
                Min = min,
                Max = max,
                Default = def,
                Dimension = dim,
                IsTrack = track,
                SafeRange = safe,
                Fraction = fraction,
                Value = raw => a + (raw - min) / (double)(max - min) * (b - a),
                Text = raw => (a + (raw - min) / (double)(max - min) * (b - a)).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + (dim.Length > 0 ? " " + dim : ""),
            };

        static FaceParam Choice(int slot, string label, string group, string role, params string[] names) => new FaceParam
        {
            Slot = slot,
            Name = label,
            Label = label,
            Group = group,
            Role = role,
            Min = 0,
            Max = names.Length - 1,
            Choices = names,
            IsSwitch = names.Length == 2 && names[0] == "Off" && names[1] == "On",
            Value = raw => raw,
            Text = raw => names[raw],
        };

        sealed class Values
        {
            readonly Dictionary<int, int> raw = new();
            public int this[FaceParam p] { get => raw.TryGetValue(p.Slot, out int v) ? v : p.Default; set => raw[p.Slot] = value; }
            public int Raw(FaceParam p) => this[p];
        }

        // ------------------------------------------------------------------ planning

        [Test]
        public void SectionsFollowTheGroupsTrackParametersComeLastAndEachParameterGetsItsControl()
        {
            var ps = new List<FaceParam>
            {
                Linear(0, "Cutoff", "Filter", "filter.cutoff", 0, 127, 64, 30, 16000, "Hz"),
                Choice(1, "Mode", "Filter", "filter.type", "LP", "HP", "BP"),
                Linear(2, "Attack", "Envelope", "env.attack", 0, 127, 10, 0.001, 4, "s"),
                Choice(3, "Wave", "Oscillator", "osc.wave", "Sine", "Triangle", "SuperSaw", "PWM Square", "Double Square", "Hexagon"),
                Choice(4, "Chorus", "Oscillator", "fx.chorus", "Off", "On"),
                Linear(5, "Feedback", "Track", "delay.feedback", 0, 128, 64, -100, 100, "%", track: true),
                Linear(6, "Release", "Envelope", "env.release", 0, 127, 30, 0.005, 8, "s"),
            };
            var values = new Values();
            var face = MachineFace.Plan("Test Synth", "curated", "A test.", true, 4, ps, values.Raw, () => Rate);
            Assert.That(face.Sections.Select(s => s.Title), Is.EqualTo(new[] { "Filter", "Envelope", "Oscillator", "Track 1" }));
            Assert.That(face.Sections[1].Params.Select(p => p.Label), Is.EqualTo(new[] { "Attack", "Release" }), "in the machine's order");
            Assert.That(ps[0].Control, Is.EqualTo(FaceControl.Knob));
            Assert.That(ps[1].Control, Is.EqualTo(FaceControl.Choice), "three short choices: buttons");
            Assert.That(ps[3].Control, Is.EqualTo(FaceControl.Dropdown), "six (long) choices: a drop-down");
            Assert.That(ps[4].Control, Is.EqualTo(FaceControl.Switch));
            Assert.That(face.Sections[1].Envelopes, Has.Count.EqualTo(1), "attack and release: an envelope picture");
            Assert.That(face.Provenance, Does.StartWith("Curated manifest"));
            var one = MachineFace.Plan("Test Synth", "draft", "", true, 1, ps, values.Raw, () => Rate);
            Assert.That(one.Sections.Last().Title, Is.EqualTo("Track"), "one track: no number");
            Assert.That(one.Provenance, Does.StartWith("Draft manifest"));
        }

        [Test]
        public void AnEnvelopeNeedsTwoStagesInOneKindOfScale()
        {
            var decaySeconds = Linear(0, "Decay time", "Amp", "env.decay", 1, 240, 100, 0.004, 1, "s");
            var releaseSlope = Linear(1, "Release slope", "Amp", "env.release", 1, 240, 100, 1, 240);
            Assert.That(MachineFace.Envelopes(new[] { decaySeconds, releaseSlope }), Is.Empty, "seconds and a bare slope (FSM KickXP): no honest picture");
            Assert.That(MachineFace.Envelopes(new[] { decaySeconds }), Is.Empty, "one stage: none");
            var a = Linear(0, "A", "Filter", "filter.env.attack", 0, 100, 0, 0, 100);
            var d = Linear(1, "D", "Filter", "filter.env.decay", 0, 100, 0, 0, 100);
            var amp = Linear(2, "Amp A", "Filter", "env.attack", 0, 127, 0, 0.001, 4, "s");
            var ampR = Linear(3, "Amp R", "Filter", "env.release", 0, 127, 0, 0.001, 4, "s");
            var envs = MachineFace.Envelopes(new[] { a, d, amp, ampR }).ToList();
            Assert.That(envs.Select(e => e.Title), Is.EqualTo(new[] { "Envelope", "Filter envelope" }));
            Assert.That(envs[0].InSeconds, Is.True);
            Assert.That(envs[1].InSeconds, Is.False, "bare scales: drawn by position");
        }

        [Test]
        public void SafeRangesBecomeKnobPositionsAndLevelsReadEachScale()
        {
            var feedback = Linear(0, "Feedback", "Track", "delay.feedback", 0, 128, 64, -100, 100, "%",
                safe: new FaceSafeRange { Min = 1, Max = 128, Text = "-98.4% … 100.0%", Reason = "never decays at its minimum" });
            Assert.That(feedback.SafePositions!.Value.From, Is.EqualTo(1 / 128.0).Within(1e-12));
            Assert.That(feedback.SafePositions!.Value.To, Is.EqualTo(1.0));
            var model = new KnobModel(46) { Safe = feedback.SafePositions };
            model.Set(0);
            Assert.That(model.OutsideSafe, Is.True, "raw 0 is outside");
            model.Set(0.5);
            Assert.That(model.OutsideSafe, Is.False);
            var canvas = new RecordingCanvas();
            model.Draw(canvas, model.Width, Accent);
            Assert.That(canvas.Of(NeoDrawKind.Arc).Count(c => c.Stroke.Color == KnobModel.Unsafe), Is.EqualTo(1), "one end outside: one red arc");
            var whole = Linear(1, "Mix", "Out", "mix.wet", 0, 100, 50, 0, 100, "%", safe: new FaceSafeRange { Min = 0, Max = 100 });
            Assert.That(whole.SafePositions, Is.Null, "a range covering everything marks nothing");
            var plain = new KnobModel(46);
            canvas.Clear();
            plain.Draw(canvas, plain.Width, Accent);
            Assert.That(canvas.Of(NeoDrawKind.Arc).Any(c => c.Stroke.Color == KnobModel.Unsafe), Is.False, "the Neo editors' knobs as before");

            var percent = Linear(2, "Sustain", "Env", "env.sustain", 0, 100, 50, 0, 100, "%");
            var fraction = Linear(3, "Sustain", "Env", "env.sustain", 0, 1000, 500, 0, 1, "%", fraction: true);
            var db = Linear(4, "Sustain", "Env", "env.sustain", 0, 60, 54, -60, 0, "dB");
            var bare = Linear(5, "Sustain", "Env", "env.sustain", 0, 127, 127, 0, 127);
            Assert.That(percent.Level(25), Is.EqualTo(0.25).Within(1e-12));
            Assert.That(fraction.Level(250), Is.EqualTo(0.25).Within(1e-12));
            Assert.That(db.Level(54), Is.EqualTo(Math.Pow(10, -6 / 20.0)).Within(1e-9));
            Assert.That(bare.Level(127), Is.EqualTo(1.0));
        }

        [Test]
        public void PicturesDrawInsideTheirFrames()
        {
            var a = Linear(0, "Attack", "Envelope", "env.attack", 0, 127, 10, 0.001, 4, "s");
            var d = Linear(1, "Decay", "Envelope", "env.decay", 0, 127, 40, 0.005, 8, "s");
            var s = Linear(2, "Sustain", "Envelope", "env.sustain", 0, 100, 70, 0, 100, "%");
            var r = Linear(3, "Release", "Envelope", "env.release", 0, 127, 60, 0.005, 8, "s");
            var env = MachineFace.Envelopes(new[] { a, d, s, r }).Single();
            var values = new Values();
            var canvas = new RecordingCanvas();
            var frame = new NeoRect(0, 0, FacePainters.EnvelopeWidth, FacePainters.EnvelopeHeight);
            FacePainters.Envelope(canvas, frame, env, values.Raw, Accent);
            var shape = canvas.Of(NeoDrawKind.Path).Single();
            Assert.That(shape.Path!.Points.All(p => frame.Contains(p.X, p.Y)), Is.True);
            // The sustain's level: 70 % of the height between the top and bottom margins.
            double sustainY = shape.Path.Points[2].Y;
            Assert.That((frame.Bottom - 6 - sustainY) / (frame.Height - 12), Is.EqualTo(0.7).Within(1e-9));
        }

        // ------------------------------------------------------------------ response: markers

        [Test]
        public void AnEffectWithoutAKnownDesignGetsMarkersSaidToBeMarkersAndASynthNothing()
        {
            var cutoff = Linear(0, "Echo filter cutoff", "Filter", "filter.cutoff", 0, 240, 64, 440, 8000, "Hz");
            var reso = Linear(1, "Echo filter Q", "Filter", "filter.resonance", 0, 240, 0, 0.01, 1);
            var values = new Values();
            Assert.That(MachineFace.Plan("Pedal SH101", "curated", "", true, 1, new[] { cutoff, reso }, values.Raw, () => Rate).Response, Is.Null,
                "a synth's filter moves with every note: no plot");
            var face = MachineFace.Plan("FSM PanzerDelay", "curated", "", false, 1, new[] { cutoff, reso }, values.Raw, () => Rate);
            Assert.That(face.Response, Is.Not.Null);
            Assert.That(face.Response!.Computed, Is.False);
            Assert.That(face.Response.DbAt, Is.Null, "no curve");
            Assert.That(face.Response.Markers, Is.EqualTo(new[] { cutoff }), "the frequency in Hz, not the resonance");
            Assert.That(face.Response.Caption, Does.StartWith("Markers only"));
            var canvas = new RecordingCanvas();
            FacePainters.Response(canvas, new NeoRect(0, 0, FacePainters.ResponseWidth, FacePainters.ResponseHeight), face.Response, values.Raw, Accent);
            // UI gallery review (win-912): no notes for developers in the plot; the caption goes to the editor's ⓘ.
            Assert.That(canvas.Of(NeoDrawKind.Text).Select(t => t.Text), Has.None.Contains("markers only").And.None.Contains("curve"), "no note in the plot");
            Assert.That(canvas.Of(NeoDrawKind.Path).Any(p => p.Stroke.Width >= 2), Is.False, "no curve drawn");

            var bare = Linear(0, "Cutoff", "Filter", "filter.cutoff", 0, 128, 64, 0, 128);
            Assert.That(MachineFace.Plan("Jeskola Filter", "curated", "", false, 1, new[] { bare }, values.Raw, () => Rate).Response, Is.Null,
                "a cutoff on a bare scale: nothing to mark");
            var drive = Linear(0, "Drive", "Distortion", "drive", 0, 100, 50, 0, 100, "%");
            Assert.That(MachineFace.Plan("Jeskola Distortion", "curated", "", false, 1, new[] { drive }, values.Raw, () => Rate).Response, Is.Null);
        }

        // ------------------------------------------------------------------ response: Pedal EQ

        static readonly double[] LsFreqs = { 20, 25, 32, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500 };
        static readonly double[] PeakFreqs = { 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000 };
        static readonly double[] HsFreqs = { 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000 };
        static readonly double[] Qs = { 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 1.0, 1.2, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0, 6.0, 8.0, 10.0 };

        static FaceParam Table(int slot, string label, string group, string role, double[] points, int def, string dim) => new FaceParam
        {
            Slot = slot,
            Name = label,
            Label = label,
            Group = group,
            Role = role,
            Min = 0,
            Max = points.Length - 1,
            Default = def,
            Dimension = dim,
            Value = raw => points[Math.Clamp(raw, 0, points.Length - 1)],
        };

        /// <summary>Pedal EQ's parameters as its curated manifest has them (gains 0 … 96: (raw − 48) × 0.5 dB).</summary>
        static List<FaceParam> PedalEqParams() => new()
        {
            Choice(0, "Bypass", "Global", "bypass", "Off", "On"),
            Choice(1, "Low shelf solo", "Global", "solo", "Off", "On"),
            Choice(2, "Low-mid solo", "Global", "solo", "Off", "On"),
            Choice(3, "High-mid solo", "Global", "solo", "Off", "On"),
            Choice(4, "High shelf solo", "Global", "solo", "Off", "On"),
            Table(5, "Low shelf frequency", "Low shelf", "eq.low.freq", LsFreqs, 6, "Hz"),
            Linear(6, "Low shelf gain", "Low shelf", "eq.low.gain", 0, 96, 48, -24, 24, "dB"),
            Table(7, "Low-mid frequency", "Low-mid", "eq.lowmid.freq", PeakFreqs, 6, "Hz"),
            Linear(8, "Low-mid gain", "Low-mid", "eq.lowmid.gain", 0, 96, 48, -24, 24, "dB"),
            Table(9, "Low-mid Q", "Low-mid", "eq.lowmid.q", Qs, 6, ""),
            Table(10, "High-mid frequency", "High-mid", "eq.highmid.freq", PeakFreqs, 8, "Hz"),
            Linear(11, "High-mid gain", "High-mid", "eq.highmid.gain", 0, 96, 48, -24, 24, "dB"),
            Table(12, "High-mid Q", "High-mid", "eq.highmid.q", Qs, 6, ""),
            Table(13, "High shelf frequency", "High shelf", "eq.high.freq", HsFreqs, 9, "Hz"),
            Linear(14, "High shelf gain", "High shelf", "eq.high.gain", 0, 96, 48, -24, 24, "dB"),
            Linear(15, "Output", "Global", "level", 0, 96, 48, -24, 24, "dB"),
        };

        /// <summary>Pedal EQ's own biquad, Direct Form I in float as its source runs it (pedal-eq/PedalEQMachine.cs).</summary>
        sealed class Df1
        {
            readonly float b0, b1, b2, a1, a2;
            float x1, x2, y1, y2;

            public Df1(ResponseModels.Biquad c)
            {
                b0 = (float)c.B0; b1 = (float)c.B1; b2 = (float)c.B2; a1 = (float)c.A1; a2 = (float)c.A2;
            }

            public float Process(float x)
            {
                float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                return y;
            }
        }

        /// <summary>The gain of a steady sine through <paramref name="process"/>, in dB (RMS over whole periods after 0.5 s).</summary>
        static double SineGainDb(double hz, Func<float, float> process)
        {
            int settle = (int)(Rate * 0.5);
            int periods = Math.Max(1, (int)Math.Round(hz * 0.25));
            int length = (int)Math.Round(periods * Rate / hz);
            double inSum = 0, outSum = 0;
            for (int i = 0; i < settle + length; i++)
            {
                float x = (float)Math.Sin(2 * Math.PI * hz * i / Rate);
                float y = process(x);
                if (i < settle) continue;
                inSum += x * (double)x;
                outSum += y * (double)y;
            }
            return 10 * Math.Log10(outSum / inSum);
        }

        static readonly double[] Probe = { 31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

        [Test]
        public void PedalEqsCurveIsWhatItsBiquadsDo()
        {
            var ps = PedalEqParams();
            var values = new Values();
            var face = MachineFace.Plan("Pedal EQ", "curated", "", false, 1, ps, values.Raw, () => Rate);
            Assert.That(face.Response!.Computed, Is.True);
            Assert.That(face.Response.Markers.Count, Is.EqualTo(4), "the four band frequencies");
            Assert.That(face.Response.DbAt!(1000), Is.EqualTo(0).Within(1e-9), "flat at its defaults");

            // Settings: shelves and peaks at several frequencies, gains and Qs.
            var settings = new[]
            {
                (ls: 3, lsg: 72, lm: 4, lmg: 24, lmq: 9, hm: 12, hmg: 78, hmq: 13, hs: 6, hsg: 30, output: 40),
                (ls: 10, lsg: 20, lm: 8, lmg: 96, lmq: 2, hm: 15, hmg: 10, hmq: 16, hs: 11, hsg: 90, output: 60),
                (ls: 0, lsg: 96, lm: 0, lmg: 48, lmq: 0, hm: 17, hmg: 84, hmq: 0, hs: 13, hsg: 0, output: 48),
            };
            foreach (var st in settings)
            {
                values[ps[5]] = st.ls; values[ps[6]] = st.lsg; values[ps[7]] = st.lm; values[ps[8]] = st.lmg; values[ps[9]] = st.lmq;
                values[ps[10]] = st.hm; values[ps[11]] = st.hmg; values[ps[12]] = st.hmq; values[ps[13]] = st.hs; values[ps[14]] = st.hsg;
                values[ps[15]] = st.output;
                double V(FaceParam p) => p.Value(values[p]);
                double nyq = Rate * 0.499;
                var bands = new[]
                {
                    ResponseModels.Biquad.LowShelf(Math.Min(V(ps[5]), nyq), V(ps[6]), Rate),
                    ResponseModels.Biquad.Peak(Math.Min(V(ps[7]), nyq), V(ps[8]), V(ps[9]), Rate),
                    ResponseModels.Biquad.Peak(Math.Min(V(ps[10]), nyq), V(ps[11]), V(ps[12]), Rate),
                    ResponseModels.Biquad.HighShelf(Math.Min(V(ps[13]), nyq), V(ps[14]), Rate),
                };
                double outGain = Math.Pow(10, V(ps[15]) / 20);
                foreach (double hz in Probe)
                {
                    var chain = bands.Select(b => new Df1(b)).ToArray();
                    double measured = SineGainDb(hz, x =>
                    {
                        foreach (var f in chain) x = f.Process(x);
                        return (float)(x * outGain);
                    });
                    Assert.That(face.Response.DbAt(hz), Is.EqualTo(measured).Within(0.05), $"{hz} Hz, {st}");
                }
                // Soloed bands sum in parallel.
                values[ps[2]] = 1; values[ps[3]] = 1;
                foreach (double hz in Probe)
                {
                    var lm = new Df1(bands[1]);
                    var hm = new Df1(bands[2]);
                    double measured = SineGainDb(hz, x => (float)((lm.Process(x) + hm.Process(x)) * outGain));
                    Assert.That(face.Response.DbAt(hz), Is.EqualTo(measured).Within(0.05), $"solo, {hz} Hz, {st}");
                }
                values[ps[2]] = 0; values[ps[3]] = 0;
            }
            values[ps[0]] = 1;
            Assert.That(face.Response.DbAt(1000), Is.EqualTo(0), "bypass: the input");
        }

        // ------------------------------------------------------------------ response: Pedal Filter

        static List<FaceParam> PedalFilterParams() => new()
        {
            Choice(0, "Mode", "Filter", "filter.type", "Low Pass", "High Pass", "Band Pass", "Band Reject", "Comb"),
            Choice(1, "Slope", "Filter", "filter.slope", "12 dB / FIR", "24 dB / IIR"),
            Linear(2, "Cutoff", "Filter", "filter.cutoff", 20, 18000, 2000, 20, 18000, "Hz"),
            Linear(3, "Resonance", "Filter", "filter.resonance", 0, 100, 20, 0, 100, "%"),
            Linear(4, "Mix", "Output", "mix.wet", 0, 100, 100, 0, 100, "%"),
        };

        /// <summary>Pedal Filter's signal path at a fixed cutoff, as its source runs it (pedal-filter/PedalFilter.cs).</summary>
        sealed class PedalFilterPath
        {
            readonly int mode;
            readonly bool second;
            readonly double g, r, fb, wet;
            readonly int delay;
            double s1a, s2a, s1b, s2b, dcIn, dcOut;
            readonly float[] comb = new float[65536];
            int write;

            public PedalFilterPath(int mode, bool second, double cutoff, double resonance, double mix)
            {
                this.mode = mode;
                this.second = second;
                g = Math.Tan(Math.PI * cutoff / Rate);
                r = 1.0 - resonance / 100.0 * 0.995;
                fb = resonance / 100.0 * 0.99;
                wet = mix / 100.0;
                delay = (int)Math.Round(Rate / cutoff);
            }

            static void Svf(double x, double g, double r, ref double s1, ref double s2, out double hp, out double bp, out double lp, out double notch)
            {
                double denom = 1.0 + 2.0 * r * g + g * g;
                hp = (x - (2.0 * r + g) * s1 - s2) / denom;
                bp = g * hp + s1;
                lp = g * bp + s2;
                notch = lp + hp;
                s1 = 2.0 * bp - s1;
                s2 = 2.0 * lp - s2;
            }

            double Select(double lp, double hp, double bp, double notch) => mode switch { 0 => lp, 1 => hp, 2 => bp, _ => notch };

            public float Process(float input)
            {
                double x = input, filtered;
                if (mode == 4)
                {
                    // Integer delays here (the test's cutoffs divide the rate), so the source's interpolation is exact.
                    int read = (write - delay + comb.Length) % comb.Length;
                    double y = x + fb * comb[read];
                    comb[write] = second ? (float)y : (float)x;
                    write = (write + 1) % comb.Length;
                    filtered = y;
                }
                else
                {
                    Svf(x, g, r, ref s1a, ref s2a, out double hp, out double bp, out double lp, out double no);
                    filtered = Select(lp, hp, bp, no);
                    if (second)
                    {
                        Svf(filtered, g, r, ref s1b, ref s2b, out hp, out bp, out lp, out no);
                        filtered = Select(lp, hp, bp, no);
                    }
                    filtered *= Math.Min(1.0, 2.0 * r);
                }
                double dc = filtered - dcIn + 0.9995 * dcOut;
                dcIn = filtered;
                dcOut = dc;
                return (float)((1 - wet) * x + wet * dc);
            }
        }

        [Test]
        public void PedalFiltersCurveIsWhatItsFilterDoes()
        {
            var ps = PedalFilterParams();
            var values = new Values();
            var face = MachineFace.Plan("Pedal Filter", "curated", "", false, 1, ps, values.Raw, () => Rate);
            Assert.That(face.Response!.Computed, Is.True);
            Assert.That(face.Response.Caption, Does.Contain("small-signal"));
            foreach (int mode in new[] { 0, 1, 2, 3 })
                foreach (bool second in new[] { false, true })
                    foreach (var (cutoff, reso, mix) in new[] { (1000, 20, 100), (300, 85, 100), (6000, 50, 60) })
                    {
                        values[ps[0]] = mode; values[ps[1]] = second ? 1 : 0; values[ps[2]] = cutoff; values[ps[3]] = reso; values[ps[4]] = mix;
                        foreach (double hz in Probe)
                        {
                            var path = new PedalFilterPath(mode, second, cutoff, reso, mix);
                            double measured = SineGainDb(hz, path.Process);
                            double computed = face.Response.DbAt!(hz);
                            // Deep in a stop band both are tiny: compare there only to −60 dB.
                            if (measured < -60 && computed < -60) continue;
                            Assert.That(computed, Is.EqualTo(measured).Within(0.1), $"mode {mode}, {(second ? 4 : 2)} poles, {cutoff} Hz, res {reso}, mix {mix}: {hz} Hz");
                        }
                    }
            // The comb (FIR and IIR) at cutoffs whose period is a whole number of samples.
            foreach (bool iir in new[] { false, true })
                foreach (var (cutoff, reso) in new[] { (480, 50), (1200, 90) })
                {
                    values[ps[0]] = 4; values[ps[1]] = iir ? 1 : 0; values[ps[2]] = cutoff; values[ps[3]] = reso; values[ps[4]] = 100;
                    foreach (double hz in new[] { 100.0, 333.0, 700.0, 1500.0, 5100.0 })
                    {
                        var path = new PedalFilterPath(4, iir, cutoff, reso, 100);
                        Assert.That(face.Response.DbAt!(hz), Is.EqualTo(SineGainDb(hz, path.Process)).Within(0.15), $"comb {(iir ? "IIR" : "FIR")}, {cutoff} Hz, res {reso}: {hz} Hz");
                    }
                }
        }

        [Test]
        public void TheResponsePlotDrawsItsCurveAndMarkers()
        {
            var ps = PedalEqParams();
            var values = new Values();
            values[ps[8]] = 72;   // +12 dB at the low-mid band
            var face = MachineFace.Plan("Pedal EQ", "curated", "", false, 1, ps, values.Raw, () => Rate);
            var canvas = new RecordingCanvas();
            var frame = new NeoRect(0, 0, FacePainters.ResponseWidth, FacePainters.ResponseHeight);
            FacePainters.Response(canvas, frame, face.Response!, values.Raw, Accent);
            var curve = canvas.Of(NeoDrawKind.Path).Single(p => p.Stroke.Width >= 2);
            Assert.That(curve.Path!.PointCount, Is.GreaterThan(100));
            Assert.That(canvas.Of(NeoDrawKind.Text).Select(t => t.Text), Has.None.Contains("markers only"));
            Assert.That(canvas.Of(NeoDrawKind.Line).Count(l => l.Stroke.Color.R == Accent.R && l.Stroke.Color.A == 0x50), Is.EqualTo(4), "a line at each band");
        }
    }
}
