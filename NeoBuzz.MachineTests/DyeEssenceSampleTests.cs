using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using SampleLayer = NeoBuzz.Sampler.SampleLayer;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Dye Essence P2 (docs/specs/dye-essence.md §4.6), measured: the Sample and Granular engines on
    /// their own as engines (pitch, aliasing, loops, direction, grains, block sizes, lane
    /// implementations, pinned bits, extremes, no allocation, CPU).
    /// </summary>
    [TestFixture]
    public class DyeEssenceSampleTests
    {
        const int Rate = Measure.Rate;

        static readonly LaneKind[] Kinds = Avx2Lanes.Supported
            ? new[] { LaneKind.Scalar, LaneKind.Pair, LaneKind.Avx2 }
            : new[] { LaneKind.Scalar, LaneKind.Pair };

        const int Choir = 0, Strings = 1, Bell = 2, Pluck = 3, Texture = 4;

        /// <summary>One slot playing a built-in sample (or grains of it), nothing else: no filters, a quick attack.</summary>
        static NoteBank Slot(SlotType type, int sample, LaneKind kind = LaneKind.Auto, int oversample = 2, Action<OscSettings> tweak = null)
        {
            var b = new NoteBank(kind);
            b.S.Oversample = oversample;
            b.SetSampleRate(Rate);
            var o = b.S.Osc[0];
            o.Type = type;
            o.Source = SampleSources.GetNow(sample);
            o.Random = 0;
            b.S.Osc[1].Type = b.S.Osc[2].Type = SlotType.Off;
            b.S.Filter[0].Type = b.S.Filter[1].Type = FilterType.Off;
            b.S.Amp.A = 0.001f;
            b.S.Amp.S = 1;
            b.S.Volume = 1;
            b.S.VelAmp = 0;
            tweak?.Invoke(o);
            return b;
        }

        static float[] Render(NoteBank b, int frames, int block = 256, Action<int> at = null, params int[] cuts)
        {
            var y = new float[2 * frames];
            var l = new float[NoteBank.MaxBlock];
            var r = new float[NoteBank.MaxBlock];
            for (int pos = 0, n; pos < frames; pos += n)
            {
                at?.Invoke(pos);
                n = Math.Min(block, frames - pos);
                foreach (int c in cuts) if (c > pos && c < pos + n) n = c - pos;
                b.Process(l, r, n);
                for (int i = 0; i < n; i++)
                {
                    y[2 * (pos + i)] = l[i];
                    y[2 * (pos + i) + 1] = r[i];
                }
            }
            return y;
        }

        static float[] Left(float[] stereo, int from, int n)
        {
            var y = new float[n];
            for (int i = 0; i < n; i++) y[i] = stereo[2 * (from + i)];
            return y;
        }

        static ulong Hash(float[] x)
        {
            ulong h = 1469598103934665603UL;
            foreach (var v in x) h = (h ^ BitConverter.SingleToUInt32Bits(v)) * 1099511628211UL;
            return h;
        }

        static double Rms(float[] y, int from, int n)
        {
            double s = 0;
            for (int i = from; i < from + n; i++) s += (double)y[i] * y[i];
            return Math.Sqrt(s / n);
        }

        static double[] Blackman(float[] x, out int n)
        {
            n = x.Length;
            var w = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n;
                w[i] = (float)(x[i] * (0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t)));
            }
            return Fft.Magnitudes(w, 0, n);
        }

        static double CentroidHz(float[] y, double near, double halfWidthHz)
        {
            var mag = Blackman(y, out int n);
            double binHz = (double)Rate / n, sum = 0, weight = 0;
            for (int k = (int)((near - halfWidthHz) / binHz); k <= (int)((near + halfWidthHz) / binHz); k++)
            {
                double m = mag[k] * mag[k];
                sum += m * k * binHz;
                weight += m;
            }
            return sum / weight;
        }

        static double PeakHz(float[] y, double near, int width = 8)
        {
            var mag = Blackman(y, out int n);
            double binHz = (double)Rate / n;
            int k0 = (int)Math.Round(near / binHz), best = k0;
            for (int k = k0 - width; k <= k0 + width; k++) if (mag[k] > mag[best]) best = k;
            double a = Math.Log(mag[best - 1] + 1e-30), c = Math.Log(mag[best] + 1e-30), d = Math.Log(mag[best + 1] + 1e-30);
            return (best + 0.5 * (a - d) / (a - 2 * c + d)) * binHz;
        }

        /// <summary>
        /// Everything that is not within <paramref name="maskHz"/> of a harmonic of <paramref name="f0"/> (30 Hz – 20 kHz)
        /// against the strongest harmonic, dB. (The Strings sample's harmonics breathe a few times over its length, so
        /// played up they carry sidebands of that rate: the mask is wide enough for them.)
        /// </summary>
        static double AliasAgainstStrongest(float[] x, double f0, double maskHz)
        {
            var mag = Blackman(x, out int n);
            double binHz = (double)Rate / n, strongest = 1e-30, worst = 1e-30;
            for (int k = (int)(30 / binHz); k < Math.Min(mag.Length, (int)(20000 / binHz)); k++)
            {
                double f = k * binHz, h = f / f0;
                if (Math.Abs(h - Math.Round(h)) * f0 < maskHz) strongest = Math.Max(strongest, mag[k]);
                else worst = Math.Max(worst, mag[k]);
            }
            return 20 * Math.Log10(worst / strongest);
        }

        // ------------------------------------------------------------------ the samples

        // The five recipes' first level, hashed: pinned so Windows, Linux and macOS build the same samples.
        static readonly ulong[] PinnedSampleHashes = { 0x2D838E770EC431F1UL, 0x3E7EB3C9507DC460UL, 0xCE56F7AAB1472B99UL, 0xC2B4F7855C132B6FUL, 0x90E8F78EA656EE1BUL };

        [Test]
        public void BuiltInSamplesAreTheSameOnEveryPlatform()
        {
            var got = new ulong[SampleSources.Count];
            for (int i = 0; i < got.Length; i++)
            {
                var layer = SampleSources.GetNow(i);
                var x = new float[layer.Length];
                Array.Copy(layer.Left[0], SampleLayer.Pad, x, 0, x.Length);
                got[i] = Hash(x);
                Assert.That(layer.Peak, Is.InRange(0.89f, 0.91f), SampleSources.Names[i]);
            }
            Measure.Report("Dye Essence samples: " + string.Join(", ", got.Select((h, i) => $"{SampleSources.Names[i]} 0x{h:X16}")));
            for (int i = 0; i < got.Length; i++)
                Assert.That(got[i], Is.EqualTo(PinnedSampleHashes[i]), $"{SampleSources.Names[i]} (re-pin from the Linux run after changing a recipe)");
        }

        [Test]
        public void UserSamplesSurviveTheStateAndTheWavReader()
        {
            var rng = new Rng(5);
            var mono = new float[5000];
            var right = new float[5000];
            for (int i = 0; i < mono.Length; i++)
            {
                mono[i] = 0.8f * rng.NextBipolar();
                right[i] = 0.6f * rng.NextBipolar();
            }
            // The Neo Wave writer: 32-bit float mono at 44.1 kHz.
            var (l, r, rate) = SampleSources.ReadWav(NeoBuzz.Wave.WavReader.Write(mono, 44100));
            Assert.That(rate, Is.EqualTo(44100));
            Assert.That(r, Is.Null);
            Assert.That(l, Is.EqualTo(mono));
            // 24-bit in the state: within a step, and what comes back encodes to the same text.
            string data = SampleSources.Encode(mono, right);
            var (dl, dr) = SampleSources.Decode(data, true);
            for (int i = 0; i < mono.Length; i++)
            {
                Assert.That(dl[i], Is.EqualTo(mono[i]).Within(1.2e-7));
                Assert.That(dr[i], Is.EqualTo(right[i]).Within(1.2e-7));
            }
            Assert.That(SampleSources.Encode(dl, dr), Is.EqualTo(data));
            Assert.Throws<InvalidDataException>(() => SampleSources.ReadWav(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }));
        }

        // ------------------------------------------------------------------ the Sample engine

        [TestCase(36)]
        [TestCase(60)]
        [TestCase(72)]
        [TestCase(84)]
        public void TheSampleEnginePlaysInTune(int note)
        {
            // The Strings sample's strongest line is its fundamental; at note 60 it sounds as recorded.
            var b = Slot(SlotType.Sample, Strings, tweak: o => o.Loop = SampleLoop.Forward);
            b.NoteOn(0, note, 1f);
            var y = Left(Render(b, 8192 + 32768), 8192, 32768);
            double want = DspMath.NoteToHz(note), got = PeakHz(y, want);
            double cents = 1200 * Math.Log2(got / want);
            Measure.Report($"Dye Essence Sample engine, Strings at note {note}: {got:F2} Hz for {want:F2} Hz ({cents:F2} cents)");
            Assert.That(Math.Abs(cents), Is.LessThan(2.5));
        }

        [TestCase(72, 2)]
        [TestCase(84, 2)]
        [TestCase(96, 2)]
        [TestCase(84, 4)]
        public void TheSampleEngineIsBandLimitedWhenPlayedUp(int note, int oversample)
        {
            var b = Slot(SlotType.Sample, Strings, oversample: oversample, tweak: o => o.Loop = SampleLoop.Forward);
            b.NoteOn(0, note, 1f);
            var y = Left(Render(b, 8192 + 32768), 8192, 32768);
            double f0 = DspMath.NoteToHz(note), floor = AliasAgainstStrongest(y, f0, Math.Max(9, 5.5 * f0 / 261.63));
            Measure.Report($"Dye Essence Sample engine, Strings at note {note}, {oversample}×: alias floor {floor:F1} dB");
            Assert.That(floor, Is.LessThan(-85));
        }

        [Test]
        public void ForwardLoopsHaveNoSeamAndPingPongRuns()
        {
            // The Strings sample is 2 s; five seconds of a looped note wrap it twice. A seam would be a
            // click: the largest step between samples against the signal's own.
            foreach (var loop in new[] { SampleLoop.Forward, SampleLoop.PingPong })
            {
                var b = Slot(SlotType.Sample, Strings, tweak: o => o.Loop = loop);
                b.NoteOn(0, 60, 1f);
                var y = Left(Render(b, 5 * Rate), 2000, 5 * Rate - 2000);
                double rms = Rms(y, 0, y.Length), step = 0;
                for (int i = 1; i < y.Length; i++) step = Math.Max(step, Math.Abs(y[i] - y[i - 1]));
                // Without a seam the biggest step is the wave's own steepest slope.
                double own = 0;
                var ref0 = Slot(SlotType.Sample, Strings, tweak: o => o.Loop = SampleLoop.Forward);
                ref0.NoteOn(0, 60, 1f);
                var z = Left(Render(ref0, Rate), 2000, Rate - 2000);
                for (int i = 1; i < z.Length; i++) own = Math.Max(own, Math.Abs(z[i] - z[i - 1]));
                Measure.Report($"Dye Essence Sample engine, {loop} loop over 5 s: rms {rms:F3}, biggest step {step:F4} (the wave's own {own:F4})");
                Assert.That(rms, Is.GreaterThan(0.01), "still sounding at 5 s");
                Assert.That(step, Is.LessThan(own * 1.6), "no click at the loop's seam");
            }
        }

        [Test]
        public void TheLoopFadeRemovesTheSeamOfLoopsThatAreNotWholePeriods()
        {
            // A loop from 10% to 70% of the Choir is not a whole number of periods: a hard wrap jumps; a 30 ms fade doesn't.
            double Step(float fade)
            {
                var b = Slot(SlotType.Sample, Choir, tweak: o => { o.Loop = SampleLoop.Forward; o.LoopStart = 0.1f; o.LoopEnd = 0.7f; o.LoopFade = fade; });
                b.NoteOn(0, 60, 1f);
                var y = Left(Render(b, 4 * Rate), 4000, 4 * Rate - 4000);
                double step = 0;
                for (int i = 1; i < y.Length; i++) step = Math.Max(step, Math.Abs(y[i] - y[i - 1]));
                return step;
            }
            double hard = Step(0), faded = Step(0.03f);
            Measure.Report($"Dye Essence Sample engine, a loop of 60% of the Choir: biggest step {hard:F4} with a hard wrap, {faded:F4} with a 30 ms fade");
            Assert.That(faded, Is.LessThan(hard * 0.6));
        }

        [Test]
        public void OneShotsEndAndReversedOnesRunBackwards()
        {
            // The Pluck dies away: forward, loud first; reversed, it swells to the end, then stops.
            var f = Slot(SlotType.Sample, Pluck);
            var r = Slot(SlotType.Sample, Pluck, tweak: o => o.Reverse = true);
            f.NoteOn(0, 60, 1f);
            r.NoteOn(0, 60, 1f);
            int frames = 4 * Rate;
            var yf = Left(Render(f, frames), 0, frames);
            var yr = Left(Render(r, frames), 0, frames);
            double fStart = Rms(yf, 0, Rate / 4), fEnd = Rms(yf, 2 * Rate - Rate / 4, Rate / 4);
            double rStart = Rms(yr, 0, Rate / 4), rEnd = Rms(yr, 2 * Rate - Rate / 4, Rate / 4);
            Measure.Report($"Dye Essence Sample engine, Pluck forward {fStart:F3} → {fEnd:F3} rms, reversed {rStart:F3} → {rEnd:F3}");
            Assert.That(fStart, Is.GreaterThan(2.5 * fEnd));
            Assert.That(rEnd, Is.GreaterThan(2.5 * rStart));
            Assert.That(Rms(yf, 3 * Rate, Rate / 2), Is.LessThan(1e-3), "a one-shot is silent after its end");
        }

        [Test]
        public void StartPositionAndLoopPointsFollowTheirParameters()
        {
            // Starting half way in: the Pluck is quieter than from its start; a short loop makes a steady tone.
            var a = Slot(SlotType.Sample, Pluck);
            var b = Slot(SlotType.Sample, Pluck, tweak: o => o.Position = 0.5f);
            a.NoteOn(0, 60, 1f);
            b.NoteOn(0, 60, 1f);
            var ya = Left(Render(a, Rate / 2), 0, Rate / 2);
            var yb = Left(Render(b, Rate / 2), 0, Rate / 2);
            Assert.That(Rms(ya, 0, 4000), Is.GreaterThan(3 * Rms(yb, 0, 4000)));
            var c = Slot(SlotType.Sample, Strings, tweak: o => { o.Loop = SampleLoop.Forward; o.LoopStart = 0.2f; o.LoopEnd = 0.2f + 0.0183f; });
            c.NoteOn(0, 60, 1f);
            var yc = Left(Render(c, 3 * Rate), 0, 3 * Rate);
            Assert.That(Rms(yc, 2 * Rate, Rate / 2), Is.GreaterThan(0.05), "a short loop holds the note");
        }

        [Test]
        public void UnisonVoicesOfASampleSitAtTheirDetunes()
        {
            var b = Slot(SlotType.Sample, Choir, tweak: o => { o.Loop = SampleLoop.Forward; o.Unison = 2; o.Detune = 50; o.Spread = 1; o.Level = 0.5f; });
            b.NoteOn(0, 69, 1f);
            var s = Render(b, 8192 + 65536);
            var y = Left(s, 8192, 65536);
            // (The Strings sample's harmonics breathe, so each voice's line carries sidebands: the centre of gravity.)
            double lo = CentroidHz(y, 440 * Math.Pow(2, -50 / 1200.0), 11), hi = CentroidHz(y, 440 * Math.Pow(2, 50 / 1200.0), 11);
            Assert.That(1200 * Math.Log2(lo / 440), Is.EqualTo(-50).Within(4));
            Assert.That(1200 * Math.Log2(hi / 440), Is.EqualTo(50).Within(4));
            // Spread 100%: the two voices in the two channels.
            double l = Rms(Left(s, 8192, 65536), 0, 65536);
            var rch = new float[65536];
            for (int i = 0; i < rch.Length; i++) rch[i] = s[2 * (8192 + i) + 1];
            Assert.That(Math.Abs(Rms(rch, 0, rch.Length) - l) / l, Is.LessThan(0.2), "both channels sound");
        }

        // ------------------------------------------------------------------ the Granular engine

        static NoteBank Cloud(int sample = Strings, Action<OscSettings> tweak = null) => Slot(SlotType.Granular, sample, tweak: o =>
        {
            o.Position = 0.3f;
            o.GrainSize = 0.06f;
            o.GrainRate = 60;
            o.Spray = 0;
            o.Scan = 0;
            tweak?.Invoke(o);
        });

        [Test]
        public void GrainsKeepThePitchOfTheNote()
        {
            // Grains start at random moments, so each line of the sample is smeared by about the grain
            // rate; the line's centre of gravity sits at the note.
            foreach (int note in new[] { 48, 60, 72 })
            {
                var b = Cloud();
                b.NoteOn(0, note, 1f);
                var y = Left(Render(b, 8192 + 65536), 8192, 65536);
                var mag = Blackman(y, out int n);
                double want = DspMath.NoteToHz(note), binHz = (double)Rate / n, sum = 0, weight = 0;
                for (int k = (int)((want - 30) / binHz); k <= (int)((want + 30) / binHz); k++)
                {
                    sum += k * binHz * mag[k] * mag[k];
                    weight += mag[k] * mag[k];
                }
                double got = sum / weight;
                Measure.Report($"Dye Essence Granular engine, Strings at note {note}: line centred at {got:F2} Hz for {want:F2} Hz");
                Assert.That(Math.Abs(1200 * Math.Log2(got / want)), Is.LessThan(30));
            }
        }

        [Test]
        public void TheCloudsLoudnessHoldsAsGrainsOverlapMore()
        {
            // The cloud's loudness holds as grains overlap more: 4× the rate and size, within 6 dB.
            var thin = Cloud(tweak: o => { o.GrainRate = 20; o.GrainSize = 0.05f; });
            var thick = Cloud(tweak: o => { o.GrainRate = 80; o.GrainSize = 0.2f; });
            thin.NoteOn(0, 60, 1f);
            thick.NoteOn(0, 60, 1f);
            double a = Rms(Left(Render(thin, Rate), 8000, Rate - 8000), 0, Rate - 8000), c = Rms(Left(Render(thick, Rate), 8000, Rate - 8000), 0, Rate - 8000);
            Measure.Report($"Dye Essence Granular engine, loudness thin {a:F3} rms, thick {c:F3} rms ({Measure.Db(c / a):F1} dB)");
            Assert.That(Math.Abs(Measure.Db(c / a)), Is.LessThan(6));
        }

        [Test]
        public void ScanMovesThroughTheSampleAndFreezeHoldsIt()
        {
            // The Pluck decays: frozen near its start the cloud keeps its loudness; scanning at 1× it dies away.
            var frozen = Cloud(Pluck, o => { o.Position = 0.02f; o.Scan = 0; o.GrainRate = 40; });
            var scanning = Cloud(Pluck, o => { o.Position = 0.02f; o.Scan = 1; o.GrainRate = 40; });
            frozen.NoteOn(0, 60, 1f);
            scanning.NoteOn(0, 60, 1f);
            int frames = 3 * Rate;
            var yf = Left(Render(frozen, frames), 0, frames);
            var ys = Left(Render(scanning, frames), 0, frames);
            double f1 = Rms(yf, Rate / 4, Rate / 4), f2 = Rms(yf, 2 * Rate, Rate / 4);
            double s1 = Rms(ys, Rate / 4, Rate / 4), s2 = Rms(ys, 2 * Rate, Rate / 4);
            Measure.Report($"Dye Essence Granular engine, Pluck frozen {f1:F3} → {f2:F3} rms, scanning {s1:F3} → {s2:F3}");
            Assert.That(f2, Is.GreaterThan(0.5 * f1));
            Assert.That(s2, Is.LessThan(0.3 * s1));
        }

        [Test]
        public void EveryGrainWindowAndTheWidthWork()
        {
            foreach (var shape in Enum.GetValues<GrainShape>())
            {
                var b = Cloud(tweak: o => { o.GrainShape = shape; o.GrainWidth = 1; o.GrainReverse = 0.5f; o.GrainPitch = 200; o.Spray = 0.5f; });
                b.NoteOn(0, 60, 1f);
                var s = Render(b, Rate);
                var l = Left(s, 4000, Rate - 4000);
                var r = new float[l.Length];
                for (int i = 0; i < r.Length; i++) r[i] = s[2 * (4000 + i) + 1];
                Assert.That(Rms(l, 0, l.Length), Is.GreaterThan(0.005), shape.ToString());
                Assert.That(s.All(float.IsFinite), Is.True, shape.ToString());
                // Scattered across the field: the channels differ.
                double diff = 0;
                for (int i = 0; i < l.Length; i++) diff += Math.Abs(l[i] - r[i]);
                Assert.That(diff / l.Length, Is.GreaterThan(0.001), shape + " width");
            }
        }

        // ------------------------------------------------------------------ bits, blocks

        static void Patch(BankSettings s)
        {
            // A wavetable slot beside a looped, reversed, unison sample and a cloud, through a filter: all three engines in one note.
            var t = new TableArena(Wavetable.FromHarmonics("saw", 1, (f, h) => (2 / (Math.PI * h), 0)));
            s.Osc[0].Type = SlotType.Wavetable;
            s.Osc[0].Table = t;
            s.Osc[0].Unison = 3;
            s.Osc[0].Level = 0.4f;
            s.Osc[1].Type = SlotType.Sample;
            s.Osc[1].Source = SampleSources.GetNow(Choir);
            s.Osc[1].Loop = SampleLoop.PingPong;
            s.Osc[1].Position = 0.1f;
            s.Osc[1].Unison = 3;
            s.Osc[1].Detune = 20;
            s.Osc[1].Spread = 0.8f;
            s.Osc[1].ToF2 = 0.5f;
            s.Osc[2].Type = SlotType.Granular;
            s.Osc[2].Source = SampleSources.GetNow(Texture);
            s.Osc[2].Position = 0.4f;
            s.Osc[2].Scan = 0.25f;
            s.Osc[2].Spray = 0.3f;
            s.Osc[2].GrainPitch = 150;
            s.Osc[2].GrainReverse = 0.3f;
            s.Osc[2].Unison = 4;
            s.Osc[2].Detune = 15;
            s.Filter[0].Type = FilterType.Ladder24;
            s.Filter[0].Cutoff = 3000;
            s.Filter[1].Type = FilterType.Lp12;
            s.Filter[1].Cutoff = 8000;
            s.Routing = 0.4f;
            s.Amp.A = 0.002f;
            s.Amp.S = 0.8f;
            s.Volume = 0.6f;
            s.Routes[0].Source = RouteSource.Lfo1; s.Routes[0].Dest = RouteDest.BPosition; s.Routes[0].Amount = 0.2f;
            s.Routes[1].Source = RouteSource.ModEnv1; s.Routes[1].Dest = RouteDest.CPosition; s.Routes[1].Amount = 0.4f;
            s.Lfo[0].Rate = 3f;
        }

        static NoteBank Mixed(LaneKind kind)
        {
            var b = new NoteBank(kind);
            b.SetSampleRate(Rate);
            Patch(b.S);
            return b;
        }

        static void Notes(NoteBank b)
        {
            for (int n = 0; n < 6; n++) b.NoteOn(n, 48 + 5 * n, 0.5f + 0.1f * n);
        }

        const ulong PinnedMixedHash = 0xDECA57A337545E20UL;

        [Test]
        public void EveryLaneImplementationGivesTheSameBitsAndTheyAreThePinnedOnes()
        {
            ulong first = 0;
            foreach (var kind in Kinds)
            {
                var b = Mixed(kind);
                Notes(b);
                var y = Render(b, 24000, 256, p => { if (p == 12000) { b.NoteOff(1); b.NoteOn(7, 80, 1f); } });
                ulong h = Hash(y);
                Measure.Report($"Dye Essence samples + grains + wavetable, {kind}: 0x{h:X16}");
                if (first == 0) first = h;
                Assert.That(h, Is.EqualTo(first), kind.ToString());
            }
            Assert.That(first, Is.EqualTo(PinnedMixedHash), "the pinned render (re-pin from the Linux run when the arithmetic changes on purpose)");
        }

        [Test]
        public void BlockSizesGiveTheSameSamples()
        {
            ulong want = 0;
            foreach (int block in new[] { 256, 117, 16 })
            {
                var b = Mixed(LaneKind.Auto);
                Notes(b);
                var y = Render(b, 24000, block, p => { }, 5000, 12000);
                ulong h = Hash(y);
                if (want == 0) want = h;
                Assert.That(h, Is.EqualTo(want), $"blocks of {block}");
            }
        }

        // ------------------------------------------------------------------ extremes, allocation, CPU

        [Test]
        public void EveryNewParameterAtItsExtremesStaysFinite()
        {
            var table = Params.Table;
            var l = new float[256];
            var r = new float[256];
            double loudest = 0;
            string where = "";
            for (int p = P.ASmpSrc; p < table.Length; p++)
                foreach (int engine in new[] { (int)SlotType.Sample, (int)SlotType.Granular })
                    foreach (int raw in new[] { table[p].Min, table[p].Max })
                    {
                        var v = new ParamValues(table);
                        v.Set(P.AType, engine);
                        v.Set(P.AUnison, 16);
                        v.Set(P.ASmpSrc, 4);
                        v.Set(p, raw);
                        for (int i = 0; i < SampleSources.Count; i++) SampleSources.GetNow(i);
                        for (int i = 0; i < TableBank.Count; i++) TableBank.GetNow(i);
                        var e = new DyeEngine(v);
                        e.SetSampleRate(Rate);
                        foreach (int k in new[] { 24, 60, 67, 108 }) e.NoteOn(k, k, 1f);
                        for (int i = 0; i < 60; i++)
                        {
                            if (i == 40) foreach (int k in new[] { 24, 60, 67, 108 }) e.NoteOff(k);
                            e.Process(l, r, 256);
                            for (int j = 0; j < 256; j++)
                            {
                                Assert.That(float.IsFinite(l[j]) && float.IsFinite(r[j]), Is.True, $"{table[p].Name} = {raw}, engine {engine}");
                                double a = Math.Max(Math.Abs(l[j]), Math.Abs(r[j]));
                                if (a > loudest) { loudest = a; where = $"{table[p].Name} = {table[p].Describe(raw)}, engine {engine}"; }
                            }
                        }
                    }
            Measure.Report($"Dye Essence P2: every sample and grain parameter at its minimum and maximum, 16 unison, a held chord: finite, loudest {Measure.Db(loudest):F1} dBFS ({where})");
            Assert.That(loudest, Is.LessThan(4));
        }

        [Test]
        public void TheAudioThreadAllocatesNothingWithSamplesAndGrains()
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
                v.Set(P.AType, (int)SlotType.Sample);
                v.Set(P.ASmpLoop, 1);
                v.Set(P.AUnison, 6);
                v.Set(P.BType, (int)SlotType.Granular);
                v.Set(P.BSmpSrc, 4);
                v.Set(P.BUnison, 4);
                v.Set(P.CType, (int)SlotType.Granular);
                v.Set(P.CSmpSrc, 1);
                v.Set(P.R1Src, (int)RouteSource.Lfo1); v.Set(P.R1Dst, (int)RouteDest.BPosition); v.Set(P.R1Amt, 800);
                v.Set(P.R2Src, (int)RouteSource.ModEnv1); v.Set(P.R2Dst, (int)RouteDest.APosition); v.Set(P.R2Amt, 700);
                for (int k = 0; k < 8; k++) e.NoteOn(k, 40 + 5 * k, 0.8f);
                for (int i = 0; i < 20; i++) e.Process(l, r, 256);
                void Block(int i)
                {
                    if (i % 50 == 0) e.NoteOn(i / 50 % 24, 40 + i % 40, 0.8f);
                    if (i % 50 == 25) e.NoteOff((i / 50 + 7) % 24);
                    v.Set(P.AType, i / 130 % 5);
                    v.Set(P.ASmpSrc, i / 90 % 5);
                    v.Set(P.ASmpLoop, i / 70 % 3);
                    v.Set(P.ASmpRev, i / 110 % 2);
                    v.Set(P.BGrRate, i % 97 * 10);
                    v.Set(P.BGrSize, i % 89 * 11);
                    v.Set(P.BGrShape, i / 60 % 5);
                    v.Set(P.CGrScan, i % 101 * 10);
                    v.Set(P.CUnison, 1 + i / 100 % 16);
                    v.Set(P.BSmpSrc, i / 150 % 5);
                    e.Process(l, r, 1 + i % 256);
                }
                var (bytes, why) = AllocationCheck.Run(2000, Block);
                Measure.Report($"Dye Essence ({kind}): {bytes} bytes allocated over 2000 blocks with samples, grains, loops, sources and engines moving");
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
        public void DenseCloudsStayUnderTheGrainCapWithoutClicks()
        {
            // 16 notes of 100 grains a second at 300 ms would be 30 grains a note: the cap holds each slot to GrainCap live
            // grains (plus the few fading out) and the bank to its budget; taking a grain over fades, so no click.
            var b = Cloud(tweak: o => { o.GrainRate = 100; o.GrainSize = 0.3f; });
            b.S.Filter[0].Type = FilterType.Off;
            for (int n = 0; n < 16; n++) b.NoteOn(n, 36 + 3 * n, 0.8f);
            var l = new float[256];
            var r = new float[256];
            int most = 0;
            for (int i = 0; i < 400; i++)
            {
                b.Process(l, r, 256);
                most = Math.Max(most, b.ActiveGrains);
            }
            Measure.Report($"Dye Essence dense cloud, 16 notes: most grains sounding {most} (cap {NoteBank.GrainCap} a slot, budget {NoteBank.GrainBudget})");
            Assert.That(most, Is.LessThanOrEqualTo(NoteBank.GrainBudget + 16 * 4), "live grains within the budget, plus those fading out");
            Assert.That(most, Is.GreaterThan(16 * 3), "and the cloud is still dense");

            // One note: the steps between samples are no bigger than the cloud's own texture (a click would stand far out).
            var one = Cloud(tweak: o => { o.GrainRate = 200; o.GrainSize = 0.5f; });
            one.NoteOn(0, 60, 0.8f);
            var y = Render(one, 48000, 256, null);
            double sum = 0, peak = 0;
            for (int i = 4000; i < 48000 - 1; i++)
            {
                double d = Math.Abs(y[2 * (i + 1)] - y[2 * i]);
                sum += d * d;
                peak = Math.Max(peak, d);
            }
            double rms = Math.Sqrt(sum / 44000);
            Measure.Report($"Dye Essence dense cloud, one note: largest step {peak:F4}, rms step {rms:F4} ({peak / rms:F1}x)");
            Assert.That(peak, Is.LessThan(12 * rms), "no click where grains are taken over");
        }

        [Test]
        public void TheEnginesRunInRealTimeAsStandaloneEngines()
        {
            // Each engine alone (the other slots off), a ladder after it, 2×: 16 notes of the Sample engine
            // (1 and 8 unison voices, looped) and of the Granular engine (a light and a dense cloud).
            (string Name, Func<NoteBank> Make)[] cases =
            {
                ("Sample, 1 voice", () => Slot(SlotType.Sample, Strings, tweak: o => o.Loop = SampleLoop.Forward)),
                ("Sample, 8 unison voices", () => Slot(SlotType.Sample, Strings, tweak: o => { o.Loop = SampleLoop.Forward; o.Unison = 8; o.Detune = 25; })),
                ("Sample, 3 slots × 4 voices", () =>
                {
                    var b = Slot(SlotType.Sample, Choir, tweak: o => { o.Loop = SampleLoop.Forward; o.Unison = 4; o.Detune = 15; });
                    for (int i = 1; i < 3; i++)
                    {
                        var o = b.S.Osc[i];
                        o.Type = SlotType.Sample; o.Source = SampleSources.GetNow(Strings); o.Loop = SampleLoop.Forward; o.Unison = 4; o.Detune = 15;
                    }
                    return b;
                }),
                ("Granular, 25 grains/s of 50 ms", () => Cloud(tweak: o => { o.GrainRate = 25; o.GrainSize = 0.05f; })),
                ("Granular, 60 grains/s of 150 ms (9 at once)", () => Cloud(tweak: o => { o.GrainRate = 60; o.GrainSize = 0.15f; })),
                ("Granular, 100 grains/s of 300 ms (30 at once)", () => Cloud(tweak: o => { o.GrainRate = 100; o.GrainSize = 0.3f; })),
            };
            double[] least = { 3, 1, 0.8, 2.5, 1, 1.2 };
            int c = 0;
            foreach (var (name, make) in cases)
            {
                var b = make();
                b.S.Filter[0].Type = FilterType.Ladder24;
                b.S.Filter[0].Cutoff = 6000;
                var (wall, cpu) = Speed(b, 4, 16);
                Measure.Report($"Dye Essence {name}, 16 notes + ladder, 2×: {wall:F2}× real time (CPU {cpu:F2}×)");
                Assert.That(wall, Is.GreaterThan(least[c++]), name);
            }
        }
    }
}
