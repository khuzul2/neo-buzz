using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Dye Essence P3b (docs/specs/dye-essence.md §4.8), measured: the Modal engine on its own
    /// (the bodies' spacing, ring time, loss, exciters, band-limiting, lane implementations, block sizes,
    /// pinned bits, extremes, no allocation, CPU).
    /// </summary>
    [TestFixture]
    public class DyeEssenceModalTests
    {
        const int Rate = Measure.Rate;

        static readonly LaneKind[] Kinds = Avx2Lanes.Supported
            ? new[] { LaneKind.Scalar, LaneKind.Pair, LaneKind.Avx2 }
            : new[] { LaneKind.Scalar, LaneKind.Pair };

        /// <summary>One Modal slot, nothing else: no filters, a quick attack, full sustain.</summary>
        static NoteBank Modal(LaneKind kind = LaneKind.Auto, int oversample = 2, Action<OscSettings> tweak = null)
        {
            var b = new NoteBank(kind);
            b.S.Oversample = oversample;
            b.SetSampleRate(Rate);
            var o = b.S.Osc[0];
            o.Type = SlotType.Modal;
            o.Random = 0;
            o.Phase = 0;
            b.S.Osc[1].Type = b.S.Osc[2].Type = SlotType.Off;
            b.S.Filter[0].Type = b.S.Filter[1].Type = FilterType.Off;
            b.S.Amp.A = 0.001f;
            b.S.Amp.S = 1;
            b.S.Volume = 1;
            b.S.VelAmp = 0;
            tweak?.Invoke(o);
            return b;
        }

        static float[] Render(NoteBank b, int frames, int block = 256, Action<int> at = null)
        {
            var y = new float[2 * frames];
            var l = new float[NoteBank.MaxBlock];
            var r = new float[NoteBank.MaxBlock];
            for (int pos = 0, n; pos < frames; pos += n)
            {
                at?.Invoke(pos);
                n = Math.Min(block, frames - pos);
                b.Process(l, r, n);
                for (int i = 0; i < n; i++)
                {
                    y[2 * (pos + i)] = l[i];
                    y[2 * (pos + i) + 1] = r[i];
                }
            }
            return y;
        }

        static ulong Hash(float[] x)
        {
            ulong h = 1469598103934665603UL;
            foreach (var v in x) h = (h ^ BitConverter.SingleToUInt32Bits(v)) * 1099511628211UL;
            return h;
        }

        /// <summary>The left channel's level at <paramref name="hz"/> over 4800 samples from <paramref name="from"/> (Hann-windowed, linear amplitude of a sine).</summary>
        static double Amp(float[] y, int from, double hz)
        {
            const int N = 4800;
            double re = 0, im = 0, w0 = 0;
            for (int i = 0; i < N; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / N);
                double x = y[2 * (from + i)] * w;
                double ph = 2 * Math.PI * hz * i / Rate;
                re += x * Math.Cos(ph);
                im += x * Math.Sin(ph);
                w0 += w;
            }
            return 2 * Math.Sqrt(re * re + im * im) / w0;
        }

        static double Db(double x) => 20 * Math.Log10(Math.Max(1e-12, x));


        static double Rms(float[] y, int from, int n)
        {
            double s = 0;
            for (int i = 0; i < n; i++) s += (double)y[2 * (from + i)] * y[2 * (from + i)];
            return Math.Sqrt(s / n);
        }

        static float[] Note(Action<OscSettings> tweak, int key = 45, int frames = 24000, Action<NoteBank> more = null, int release = -1)
        {
            var b = Modal(tweak: tweak);
            more?.Invoke(b);
            b.NoteOn(0, key, 1f);
            return Render(b, frames, 256, p => { if (p == release) b.NoteOff(0); });
        }

        const double A2 = 110.0;

        [Test]
        public void EachBodyHasItsOwnModeSpacing()
        {
            // Struck, hard material off (exact spacing), long ring: the modes are the lines of the spectrum, and the gaps between them are empty.
            (ModalGeometry Body, double[] Ratios, double[] Gaps)[] bodies =
            {
                (ModalGeometry.String, new[] { 1.0, 2, 3, 4, 5 }, new[] { 1.5, 2.5, 3.5 }),
                (ModalGeometry.Tube, new[] { 1.0, 3, 5, 7 }, new[] { 2.0, 4, 6 }),
                (ModalGeometry.Bar, new[] { 1.0, 2.7565, 5.4039, 8.933 }, new[] { 2.0, 4, 7 }),
                (ModalGeometry.Membrane, new[] { 1.0, 1.5937, 2.653, 3.1555 }, new[] { 1.3, 1.85 }),
                (ModalGeometry.Plate, new[] { 1.0, 2.0426, 2.957, 3.78 }, new[] { 1.5, 2.5 }),
                (ModalGeometry.Bell, new[] { 0.5, 1.0, 2.0, 3.011 }, new[] { 0.75, 1.75 }),
            };
            foreach (var (body, ratios, gaps) in bodies)
            {
                var y = Note(o => { o.Geometry = (int)body; o.Material = 0; o.Ring = 6; o.HfDamp = 0; o.Force = 0.8f; o.Position = 0.2f; o.Modes = 24; });
                double top = 0;
                foreach (double rt in ratios) top = Math.Max(top, Amp(y, 2400, A2 * rt));
                var sb = new System.Text.StringBuilder();
                foreach (double rt in ratios)
                {
                    double on = Db(Amp(y, 2400, A2 * rt) / top);
                    sb.Append($" {rt:0.###}: {on:0.0}");
                    Assert.That(on, Is.GreaterThan(-35), $"{body}: a mode at {rt} × the note");
                }
                double loudestGap = -200;
                foreach (double g in gaps) loudestGap = Math.Max(loudestGap, Db(Amp(y, 2400, A2 * g) / top));
                Measure.Report($"Dye Essence Modal, {body}: levels of the modes at {sb} dB against the strongest; loudest of the gaps {loudestGap:0.0} dB");
                Assert.That(loudestGap, Is.LessThan(-30), $"{body}: nothing between the modes");
            }
        }

        [Test]
        public void ATubeHasOnlyOddModes()
        {
            var y = Note(o => { o.Geometry = (int)ModalGeometry.Tube; o.Material = 0; o.Ring = 6; o.HfDamp = 0; o.Force = 0.8f; o.Position = 0.2f; });
            double odd = Amp(y, 2400, A2 * 3), even = Amp(y, 2400, A2 * 2);
            Measure.Report($"Dye Essence Modal tube: mode 3 against twice the pitch {Db(odd / Math.Max(1e-12, even)):0} dB");
            Assert.That(Db(odd / even), Is.GreaterThan(40));
        }

        [Test]
        public void StiffnessStretchesTheSpacingOfAHardMaterial()
        {
            // Material 100 %: the string's mode 8 sits at 8 · √(1 + 0.003 · 64) = 8.7 times the note, not 8.
            var y = Note(o => { o.Geometry = (int)ModalGeometry.String; o.Material = 1; o.Ring = 8; o.HfDamp = 0; o.Force = 0.9f; o.Position = 0.1f; });
            double stretched = Amp(y, 2400, A2 * 8 * Math.Sqrt(1 + 0.003 * 64)), plain = Amp(y, 2400, A2 * 8);
            Measure.Report($"Dye Essence Modal, material 100 %: mode 8 at its stretched place {Db(stretched / plain):0} dB above the whole multiple");
            Assert.That(Db(stretched / plain), Is.GreaterThan(20));
            var soft = Note(o => { o.Geometry = (int)ModalGeometry.String; o.Material = 0; o.Ring = 8; o.HfDamp = 0; o.Force = 0.9f; o.Position = 0.1f; });
            Assert.That(Db(Amp(soft, 2400, A2 * 8) / Amp(soft, 2400, A2 * 8 * 1.087)), Is.GreaterThan(20), "material 0 keeps whole multiples");
        }

        [Test]
        public void TheRingTimeIsTheLowestModesSixtyDecibelDecay()
        {
            foreach (float ring in new[] { 0.5f, 1.5f, 4f })
            {
                var y = Note(o => { o.Material = 0; o.Ring = ring; o.HfDamp = 0; o.Force = 0.5f; o.BodySize = 0.5f; o.Position = 0.2f; }, frames: 100000);
                double a1 = Amp(y, 2400, A2), a2 = Amp(y, 2400 + 38400, A2);
                double perSecond = (Db(a1) - Db(a2)) / (38400.0 / Rate);
                double t60 = 60 / perSecond;
                Measure.Report($"Dye Essence Modal, ring time {ring} s: the fundamental falls {perSecond:0.0} dB a second, 60 dB in {t60:0.00} s");
                Assert.That(t60, Is.EqualTo(ring).Within(0.1 * ring));
            }
        }

        [Test]
        public void HighFrequencyDampingKillsTheUpperModesFirst()
        {
            double Rate8(float hf, float mat)
            {
                var y = Note(o => { o.Material = mat; o.Ring = 3; o.HfDamp = hf; o.Force = 0.9f; o.Position = 0.1f; o.BodySize = 0.5f; }, frames: 40000);
                double a = Amp(y, 2400, A2 * 8), b = Amp(y, 2400 + 9600, A2 * 8);
                double c = Amp(y, 2400, A2), d = Amp(y, 2400 + 9600, A2);
                return (Db(a) - Db(b)) - (Db(c) - Db(d));
            }
            double none = Rate8(0, 0.5f), some = Rate8(1, 0.5f), soft = Rate8(1, 0), hard = Rate8(1, 1);
            Measure.Report($"Dye Essence Modal: mode 8 falls faster than mode 1 by (dB in 0.2 s) {none:0.0} with no hf damp, {some:0.0} at 100 %, {soft:0.0} soft material, {hard:0.0} hard material");
            Assert.That(none, Is.LessThan(1));
            Assert.That(some, Is.GreaterThan(5));
            Assert.That(soft, Is.GreaterThan(some));
            Assert.That(hard, Is.LessThan(some));
        }

        [Test]
        public void AHarderStrikeIsBrighterAtTheSameLoudness()
        {
            double Bright(float force, out double rms)
            {
                var y = Note(o => { o.Geometry = (int)ModalGeometry.String; o.Material = 0.5f; o.Ring = 3; o.HfDamp = 0; o.Force = force; o.Position = 0.1f; }, frames: 9600);
                rms = Rms(y, 0, 2400);
                double hi = 0, lo = Amp(y, 100, A2);
                for (int k = 8; k <= 14; k++) hi += Math.Pow(Amp(y, 100, A2 * k), 2);
                return Db(Math.Sqrt(hi) / lo);
            }
            double soft = Bright(0.05f, out double rs), hard = Bright(0.95f, out double rh);
            Measure.Report($"Dye Essence Modal, struck: modes 8 … 14 against mode 1 {soft:0.0} dB soft, {hard:0.0} dB hard; first 50 ms rms {Db(rs):0.0} / {Db(rh):0.0} dB");
            Assert.That(hard, Is.GreaterThan(soft + 8));
            Assert.That(Math.Abs(Db(rs) - Db(rh)), Is.LessThan(4), "loudness stays");
        }

        [Test]
        public void EveryExciterAndBodySoundsAtTheSameLoudness()
        {
            double lo = 99, hi = -99;
            string loAt = "", hiAt = "";
            foreach (var exc in new[] { ModalExciter.Struck, ModalExciter.Plucked, ModalExciter.Continuous })
                foreach (var body in Enum.GetValues<ModalGeometry>())
                {
                    var y = Note(o => { o.Exciter = (int)exc; o.Geometry = (int)body; o.Ring = 2; o.Force = 0.5f; o.Position = 0.3f; }, frames: 24000);
                    double db = Db(Rms(y, 4800, 4800));
                    Assert.That(db, Is.GreaterThan(-60), $"{exc} {body} sounds");
                    if (db < lo) { lo = db; loAt = $"{exc} {body}"; }
                    if (db > hi) { hi = db; hiAt = $"{exc} {body}"; }
                }
            Measure.Report($"Dye Essence Modal, rms 100 … 200 ms of every exciter and body, key 45: quietest {lo:0.0} dB ({loAt}), loudest {hi:0.0} dB ({hiAt})");
            Assert.That(hi - lo, Is.LessThan(14));
        }

        [Test]
        public void AContinuousExciterSustainsWhileHeldAndStopsAfterTheKey()
        {
            // A bow or breath: the body keeps sounding while the key is down, at the same level, and dies after it.
            var y = Note(o => { o.Exciter = (int)ModalExciter.Continuous; o.Ring = 0.4f; o.Force = 0.5f; o.Position = 0.3f; }, frames: 96000, release: 64000, more: b => b.S.Amp.R = 0.01f);
            double a = Rms(y, 12000, 9600), b2 = Rms(y, 36000, 9600), c = Rms(y, 60000, 3000), d = Rms(y, 90000, 4800);
            Measure.Report($"Dye Essence Modal, continuous: rms {Db(a):0.0} dB at 0.25 s, {Db(b2):0.0} dB at 0.75 s, {Db(c):0.0} dB at 1.25 s (held), {Db(d):0.0} dB after the key");
            Assert.That(Math.Abs(Db(a) - Db(b2)), Is.LessThan(3));
            Assert.That(Math.Abs(Db(a) - Db(c)), Is.LessThan(3));
            Assert.That(Db(d), Is.LessThan(Db(a) - 40));
            // The struck body, with the same ring, is gone long before the continuous one stops.
            var s = Note(o => { o.Exciter = (int)ModalExciter.Struck; o.Ring = 0.4f; o.Position = 0.3f; }, frames: 96000);
            Assert.That(Db(Rms(s, 60000, 3000)), Is.LessThan(-80));
        }

        [Test]
        public void ModesAboveTheBandAreNeverRendered()
        {
            // Note 100 with 64 string modes of a hard material: the modes past 0.46 of the sample rate do not exist, so the
            // frequencies they would fold back to hold only the decimator's residue.
            double f0 = 2637;
            var y = Note(o => { o.Geometry = (int)ModalGeometry.String; o.Modes = 64; o.Material = 1; o.HfDamp = 0; o.Ring = 3; o.Force = 1; o.Position = 0.05f; }, key: 100, frames: 12000);
            double top = 0;
            for (int k = 1; k <= 8; k++) top = Math.Max(top, Amp(y, 600, f0 * k * Math.Sqrt(1 + 0.003 * k * k)));
            double worst = -200;
            string where = "";
            for (int k = 10; k <= 24; k++)
            {
                double f = f0 * k * Math.Sqrt(1 + 0.003 * k * k), alias = Math.Abs(f - Rate * Math.Round(f / Rate));
                double db = Db(Amp(y, 600, alias) / top);
                if (db > worst) { worst = db; where = $"mode {k} ({f:0} Hz) at {alias:0} Hz"; }
            }
            Measure.Report($"Dye Essence Modal engine, note 100 with 64 modes: worst alias image {worst:0.0} dB below the strongest mode ({where})");
            Assert.That(worst, Is.LessThan(-60));
        }

        static void Patch(NoteBank b)
        {
            for (int o = 0; o < 3; o++)
            {
                var s = b.S.Osc[o];
                s.Type = SlotType.Modal;
                s.Exciter = o;
                s.Geometry = 2 * o + 1;
                s.Position = 0.2f + 0.3f * o;
                s.Modes = 16 + 12 * o;
                s.Force = 0.3f + 0.3f * o;
                s.Material = 0.2f + 0.3f * o;
                s.Ring = 0.8f + o;
                s.HfDamp = 0.2f * (o + 1);
                s.BodySize = 0.3f + 0.2f * o;
                s.Unison = 1 + 2 * o;
                s.Detune = 12;
                s.Spread = 0.5f;
                s.Level = 0.4f;
                s.Transpose = 12 * (o - 1);
                s.Random = 0.5f;
            }
            b.S.Filter[0].Type = FilterType.Ladder24;
            b.S.Filter[0].Cutoff = 4000;
            b.S.Amp.A = 0.002f;
            b.S.Amp.S = 0.8f;
            b.S.Volume = 0.6f;
            b.S.Routes[0].Source = RouteSource.Lfo1; b.S.Routes[0].Dest = RouteDest.APosition; b.S.Routes[0].Amount = 0.4f;
            b.S.Routes[1].Source = RouteSource.ModEnv1; b.S.Routes[1].Dest = RouteDest.BPitch; b.S.Routes[1].Amount = 0.1f;
            b.S.Lfo[0].Rate = 2f;
        }

        const ulong PinnedHash = 0xC59A090EA5B0611BUL;

        [Test]
        public void ABendMovesEveryModeAndKeepsThemInTune()
        {
            // A struck body ringing while the pitch is bent up 40 cents (the small-move path: the rotations are turned, not rebuilt) and then 150 cents (rebuilt).
            foreach (float bend in new[] { 0.4f, 1.5f })
            {
                var b = Modal(tweak: o => { o.Geometry = (int)ModalGeometry.String; o.Material = 0; o.Ring = 6f; o.HfDamp = 0; o.Force = 0.8f; o.Position = 0.1f; });
                b.NoteOn(0, 45, 1f);
                var y = Render(b, 36000, 256, p => { if (p == 4608) b.S.Bend = bend; });
                double ratio = Math.Pow(2, bend / 12.0);
                var sb = new System.Text.StringBuilder();
                for (int k = 6; k <= 10; k += 4)
                {
                    double on = Amp(y, 24000, A2 * ratio * k), off = Amp(y, 24000, A2 * ratio * k * 1.04);
                    sb.Append($" mode {k}: {Db(on / off):0.0}");
                    Assert.That(Db(on / off), Is.GreaterThan(15), $"bend {bend}, mode {k}");
                }
                Measure.Report($"Dye Essence Modal, struck string bent by {bend * 100:0} cents: the mode against 4 % above it (dB){sb}");
            }
        }

        [Test]
        public void EveryLaneImplementationGivesTheSameBitsAndTheyAreThePinnedOnes()
        {
            ulong first = 0;
            foreach (var kind in Kinds)
            {
                var b = new NoteBank(kind);
                b.SetSampleRate(Rate);
                Patch(b);
                for (int n = 0; n < 6; n++) b.NoteOn(n, 48 + 5 * n, 0.5f + 0.1f * n);
                var y = Render(b, 24000, 256, p => { if (p == 11776) { b.NoteOff(1); b.NoteOn(7, 80, 1f); } });
                ulong h = Hash(y);
                Measure.Report($"Dye Essence modal (three slots, three exciters, unison, modulation), {kind}: 0x{h:X16}");
                if (first == 0) first = h;
                Assert.That(h, Is.EqualTo(first), kind.ToString());
            }
            Assert.That(first, Is.EqualTo(PinnedHash), "the pinned render (re-pin from the Linux run when the arithmetic changes on purpose)");
        }

        [Test]
        public void BlockSizesGiveTheSameSamples()
        {
            ulong want = 0;
            foreach (int block in new[] { 256, 117, 16 })
            {
                var b = new NoteBank(LaneKind.Auto);
                b.SetSampleRate(Rate);
                Patch(b);
                for (int n = 0; n < 6; n++) b.NoteOn(n, 48 + 5 * n, 0.5f + 0.1f * n);
                ulong h = Hash(Render(b, 24000, block));
                if (want == 0) want = h;
                Assert.That(h, Is.EqualTo(want), $"blocks of {block}");
            }
        }

        [Test]
        public void EveryModalParameterAtItsExtremesStaysFinite()
        {
            var table = Params.Table;
            var l = new float[256];
            var r = new float[256];
            double loudest = 0;
            string where = "";
            for (int p = P.AMExc; p < table.Length; p++)
                foreach (int raw in new[] { table[p].Min, table[p].Max })
                    foreach (int exc in new[] { 0, 1, 2 })
                    {
                        var v = new ParamValues(table);
                        v.Set(P.AType, (int)SlotType.Modal);
                        v.Set(P.BType, (int)SlotType.Modal);
                        v.Set(P.CType, (int)SlotType.Modal);
                        v.Set(P.AUnison, 16);
                        v.Set(P.AMExc, exc);
                        v.Set(P.BMExc, exc);
                        v.Set(p, raw);
                        var e = new DyeEngine(v);
                        e.SetSampleRate(Rate);
                        foreach (int k in new[] { 24, 60, 67, 108 }) e.NoteOn(k, k, 1f);
                        for (int i = 0; i < 60; i++)
                        {
                            if (i == 40) foreach (int k in new[] { 24, 60, 67, 108 }) e.NoteOff(k);
                            e.Process(l, r, 256);
                            for (int j = 0; j < 256; j++)
                            {
                                Assert.That(float.IsFinite(l[j]) && float.IsFinite(r[j]), Is.True, $"{table[p].Name} = {raw}");
                                double a = Math.Max(Math.Abs(l[j]), Math.Abs(r[j]));
                                if (a > loudest) { loudest = a; where = $"{table[p].Name} = {table[p].Describe(raw)}, exciter {exc}"; }
                            }
                        }
                    }
            Measure.Report($"Dye Essence P3b: every modal parameter at its minimum and maximum, 16 unison, a held chord: finite, loudest {Db(loudest):F1} dBFS ({where})");
            Assert.That(loudest, Is.LessThan(4));
        }

        [Test]
        public void ARingingBodyDecaysToExactSilence()
        {
            // The states below 1e-18 are flushed: a long-dead body renders exact zeros (no denormal tail).
            var y = Note(o => { o.Ring = 0.05f; o.HfDamp = 1; o.Material = 0; o.Modes = 64; }, frames: 96000);
            for (int i = 2 * 90000; i < y.Length; i++) Assert.That(y[i], Is.EqualTo(0f), $"sample {i / 2}");
        }

        [Test]
        public void NothingIsAllocatedOnTheAudioThread()
        {
            foreach (var kind in Kinds.Where(k => k != LaneKind.Scalar))
            {
                var v = new ParamValues(Params.Table);
                for (int i = 0; i < TableBank.Count; i++) TableBank.GetNow(i);
                for (int i = 0; i < SampleSources.Count; i++) SampleSources.GetNow(i);
                var e = new DyeEngine(v, kind);
                e.SetSampleRate(Rate);
                var l = new float[256];
                var r = new float[256];
                v.Set(P.AType, (int)SlotType.Modal);
                v.Set(P.BType, (int)SlotType.Modal);
                v.Set(P.CType, (int)SlotType.Modal);
                v.Set(P.R1Src, (int)RouteSource.Lfo1); v.Set(P.R1Dst, (int)RouteDest.BPosition); v.Set(P.R1Amt, 800);
                for (int k = 0; k < 8; k++) e.NoteOn(k, 40 + 5 * k, 0.8f);
                for (int i = 0; i < 20; i++) e.Process(l, r, 256);
                void Block(int i)
                {
                    if (i % 50 == 0) e.NoteOn(i / 50 % 24, 40 + i % 40, 0.8f);
                    if (i % 50 == 25) e.NoteOff((i / 50 + 7) % 24);
                    v.Set(P.AMExc, i / 37 % 3);
                    v.Set(P.BMGeom, i / 41 % 6);
                    v.Set(P.AMModes, 1 + i % 64);
                    v.Set(P.AMForce, i % 1000);
                    v.Set(P.BMMat, i * 7 % 1000);
                    v.Set(P.CMRing, i * 13 % 1000);
                    v.Set(P.BMHfDamp, i % 300);
                    v.Set(P.CMSize, i * 3 % 1000);
                    v.Set(P.APos, i * 5 % 1000);
                    v.Set(P.AUnison, 1 + i / 100 % 16);
                    v.Set(P.BType, (i < 100 ? i / 16 : i / 130) % 7);
                    e.Process(l, r, 1 + i % 256);
                }
                for (int i = 0; i < 100; i++) Block(i);
                var (bytes, why) = AllocationCheck.Run(2000, Block);
                Measure.Report($"Dye Essence Modal engine ({kind}): {bytes} bytes allocated over 2000 blocks with exciter, body, modes, force, material, ring, damp, size, unison and engine type moving");
                Assert.That(bytes, Is.EqualTo(0), why);
            }
        }

        static (double Wall, double Cpu) Speed(NoteBank b, double seconds, int notes)
        {
            var l = new float[256];
            var r = new float[256];
            for (int n = 0; n < notes; n++) b.NoteOn(n, 36 + 3 * n, 0.4f + 0.6f * n / 16);
            for (int i = 0; i < 40; i++) b.Process(l, r, 256);
            int blocks = (int)(seconds * Rate / 256);
            var p = Process.GetCurrentProcess();
            var cpu0 = p.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < blocks; i++) b.Process(l, r, 256);
            double wall = sw.Elapsed.TotalSeconds;
            p.Refresh();
            double cpu = (p.TotalProcessorTime - cpu0).TotalSeconds;
            double audio = blocks * 256.0 / Rate;
            Assert.That(b.ActiveNotes, Is.EqualTo(notes), "every note sounding throughout");
            return (audio / wall, audio / Math.Max(1e-9, cpu));
        }

        [Test]
        public void TheEngineRunsInRealTimeAsAStandaloneEngine()
        {
            (string Name, Func<NoteBank> Make)[] cases =
            {
                ("struck string, 24 modes", () => Modal(tweak: o => o.Modes = 24)),
                ("struck plate, 64 modes", () => Modal(tweak: o => { o.Geometry = (int)ModalGeometry.Plate; o.Modes = 64; })),
                ("bowed string, 64 modes", () => Modal(tweak: o => { o.Exciter = (int)ModalExciter.Continuous; o.Modes = 64; })),
                ("bowed, 64 modes in 8 unison voices", () => Modal(tweak: o => { o.Exciter = (int)ModalExciter.Continuous; o.Modes = 64; o.Unison = 8; o.Detune = 20; })),
                ("3 slots × 48 modes × 2 voices, bowed", () =>
                {
                    var b = Modal(tweak: o => { o.Exciter = (int)ModalExciter.Continuous; o.Modes = 48; o.Unison = 2; o.Detune = 10; });
                    for (int i = 1; i < 3; i++)
                    {
                        var o = b.S.Osc[i];
                        o.Type = SlotType.Modal; o.Exciter = (int)ModalExciter.Continuous; o.Geometry = 2 * i; o.Modes = 48; o.Unison = 2; o.Detune = 10;
                    }
                    return b;
                }),
                ("bowed, 64 modes, 8 unison, pitch modulated every tick", () =>
                {
                    var b = Modal(tweak: o => { o.Exciter = (int)ModalExciter.Continuous; o.Modes = 64; o.Unison = 8; o.Detune = 20; });
                    b.S.Routes[0].Source = RouteSource.Lfo1; b.S.Routes[0].Dest = RouteDest.APitch; b.S.Routes[0].Amount = 0.1f;
                    b.S.Lfo[0].Rate = 5f;
                    return b;
                }),
            };
            double[] least = { 4, 3, 3, 1.5, 1.5, 1.5 };
            int c = 0;
            foreach (var (name, make) in cases)
            {
                var b = make();
                b.S.Filter[0].Type = FilterType.Ladder24;
                b.S.Filter[0].Cutoff = 6000;
                var (wall, cpu) = Speed(b, 4, 16);
                Measure.Report($"Dye Essence Modal, {name}, 16 notes + ladder, 2×: {wall:F2}× real time (CPU {cpu:F2}×)");
                Assert.That(cpu, Is.GreaterThan(least[c++]), name + " (CPU time, which a busy machine does not skew)");
            }
        }
    }
}
