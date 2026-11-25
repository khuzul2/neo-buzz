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
            Assert.That(MachineFace.Envelopes(new[] { decaySeconds, releaseSlope }), Is.Empty, "seconds and a bare slope (a kick): no honest picture");
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
            Assert.That(MachineFace.Plan("Test Synth", "curated", "", true, 1, new[] { cutoff, reso }, values.Raw, () => Rate).Response, Is.Null,
                "a synth's filter moves with every note: no plot");
            var face = MachineFace.Plan("Test Delay", "curated", "", false, 1, new[] { cutoff, reso }, values.Raw, () => Rate);
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
            Assert.That(MachineFace.Plan("Test Bare Filter", "curated", "", false, 1, new[] { bare }, values.Raw, () => Rate).Response, Is.Null,
                "a cutoff on a bare scale: nothing to mark");
            var drive = Linear(0, "Drive", "Distortion", "drive", 0, 100, 50, 0, 100, "%");
            Assert.That(MachineFace.Plan("Test Distortion", "curated", "", false, 1, new[] { drive }, values.Raw, () => Rate).Response, Is.Null);
        }
    }
}
