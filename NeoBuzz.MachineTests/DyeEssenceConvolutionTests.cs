using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Dye Essence P4b2 (docs/specs/dye-essence.md §4.10), measured: the zero-latency partitioned convolution (StereoConvolver),
    /// the built-in and shaped impulse responses, the channel vocoder and the two rack types that use them.
    /// </summary>
    [TestFixture]
    public class DyeEssenceConvolutionTests
    {
        const int Rate = Measure.Rate;

        static float[] Noise(int n, uint seed, float amp = 0.5f)
        {
            var x = new float[n];
            uint s = seed;
            for (int i = 0; i < n; i++)
            {
                s = s * 1664525u + 1013904223u;
                x[i] = ((s >> 8) / 16777216f * 2 - 1) * amp;
            }
            return x;
        }

        static float[] Decaying(int n, uint seed, double seconds)
        {
            var x = Noise(n, seed, 1f);
            for (int i = 0; i < n; i++) x[i] *= (float)Math.Exp(-6.9 * i / (seconds * Rate));
            return x;
        }

        static float[] Direct(float[] x, float[] h)
        {
            var y = new float[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                double acc = 0;
                int kmax = Math.Min(i, h.Length - 1);
                for (int k = 0; k <= kmax; k++) acc += h[k] * (double)x[i - k];
                y[i] = (float)acc;
            }
            return y;
        }

        static float[] Run(StereoConvolver c, float[] l, float[] r, int block, out float[] outR)
        {
            var yl = new float[l.Length];
            outR = new float[l.Length];
            var bl = new float[block];
            var br = new float[block];
            for (int at = 0, n; at < l.Length; at += n)
            {
                n = Math.Min(block, l.Length - at);
                Array.Copy(l, at, bl, 0, n);
                Array.Copy(r, at, br, 0, n);
                c.Process(bl, br, n);
                Array.Copy(bl, 0, yl, at, n);
                Array.Copy(br, 0, outR, at, n);
            }
            return yl;
        }

        [TestCase(30)]
        [TestCase(64)]
        [TestCase(300)]
        [TestCase(1500)]
        [TestCase(5000)]
        [TestCase(30000)]
        public void TheConvolverMatchesTheDirectConvolutionAtAnyBlockSize(int taps)
        {
            var hl = Decaying(taps, 11, taps / (double)Rate / 1.5 + 0.001);
            var hr = Decaying(taps, 12, taps / (double)Rate / 1.5 + 0.001);
            int frames = taps + 12000;
            var xl = Noise(frames, 21);
            var xr = Noise(frames, 22);
            var wantL = Direct(xl, hl);
            var wantR = Direct(xr, hr);
            float[] first = null;
            foreach (int block in new[] { 256, 64, 37, 1 })
            {
                if (block == 1 && taps > 5000) continue;
                var c = new StereoConvolver(hl, hr);
                var yl = Run(c, xl, xr, block, out var yr);
                double err = 0, sig = 0;
                for (int i = 0; i < frames; i++)
                {
                    err += Math.Pow(yl[i] - wantL[i], 2) + Math.Pow(yr[i] - wantR[i], 2);
                    sig += wantL[i] * (double)wantL[i] + wantR[i] * (double)wantR[i];
                }
                double db = 10 * Math.Log10(err / sig + 1e-30);
                Measure.Report($"StereoConvolver {taps} taps, blocks of {block}: error {db:F1} dB against the direct sum");
                Assert.That(db, Is.LessThan(-100), $"{taps} taps, block {block}");
                if (first == null) first = yl;
                else Assert.That(yl, Is.EqualTo(first), $"the same bits at block {block} as at 256");
            }
        }

        [Test]
        public void TheConvolverHasNoLatencyAndAMonoResponseServesBothChannels()
        {
            var h = Decaying(9000, 3, 0.2);
            h[0] = 0.75f;
            var c = new StereoConvolver(h, null);
            var l = new float[512];
            var r = new float[512];
            l[0] = 1;
            r[3] = 1;
            c.Process(l, r, 512);
            Assert.That(l[0], Is.EqualTo(0.75f).Within(1e-6), "the first output sample is the first tap");
            Assert.That(r[3], Is.EqualTo(0.75f).Within(1e-6));
            Assert.That(l[100], Is.EqualTo(h[100]).Within(1e-5), "left takes the response");
            Assert.That(r[103], Is.EqualTo(h[100]).Within(1e-5), "right takes the same");
        }

        [Test]
        public void TheStagesTransformOnDifferentSamples()
        {
            // 64 at multiples of 64; 256 at 128 + 256k; 1024 at 512 + 1024k; 4096 at 2048 + 4096k: never on one sample but the small ones'.
            var seen = new Dictionary<long, int>();
            for (int s = 1; s < StereoConvolver.StageSize.Length; s++)
                for (long t = StereoConvolver.StagePhase[s]; t < 200000; t += StereoConvolver.StageSize[s])
                {
                    seen.TryGetValue(t, out int n);
                    seen[t] = n + 1;
                }
            Assert.That(seen.Values.Max(), Is.EqualTo(1), "the big stages' blocks are never on the same sample");
        }

        [Test]
        public void TheBuiltInResponsesDecayAtTheirRatesAreDeterministicAndStereo()
        {
            var rt = new List<string>();
            double last = 0;
            foreach (int kind in new[] { 0, 1, 2, 4, 5 })
            {
                ImpulseResponses.Generate(kind, Rate, out var l, out var r);
                ImpulseResponses.Generate(kind, Rate, out var l2, out _);
                Assert.That(l, Is.EqualTo(l2), "the same samples every time");
                Assert.That(l.Length, Is.LessThanOrEqualTo(ImpulseResponses.MaxSeconds * Rate));
                // Schroeder's decay curve: the time it takes to fall 30 dB, doubled.
                double total = 0;
                for (int i = 0; i < l.Length; i++) total += l[i] * (double)l[i];
                double acc = total, t30 = 0;
                for (int i = 0; i < l.Length; i++)
                {
                    acc -= l[i] * (double)l[i];
                    if (10 * Math.Log10(acc / total + 1e-30) < -30) { t30 = i / (double)Rate; break; }
                }
                double rt60 = 2 * t30;
                rt.Add($"{ImpulseResponses.Names[kind]} {rt60:F2} s ({l.Length / (double)Rate:F2} s long)");
                Assert.That(rt60, Is.GreaterThan(last), $"{ImpulseResponses.Names[kind]} rings longer than the one before");
                last = rt60;
                double dot = 0, el = 0, er = 0;
                for (int i = 0; i < l.Length; i++) { dot += l[i] * (double)r[i]; el += l[i] * (double)l[i]; er += r[i] * (double)r[i]; }
                Assert.That(Math.Abs(dot / Math.Sqrt(el * er)), Is.LessThan(0.25), $"{ImpulseResponses.Names[kind]}: the channels are decorrelated");
            }
            Measure.Report("Built-in impulse responses (energy decay, 30 dB doubled): " + string.Join(", ", rt));
        }

        [Test]
        public void ShapingNormalisesPadsCutsAndReverses()
        {
            ImpulseResponses.Generate(1, Rate, out var l, out var r);
            var plain = new ImpulseResponses.ShapeSettings { Size = 1, Width = 1, LowCutHz = 20, HighCutHz = 20000 };
            ImpulseResponses.Shape(l, r, Rate, plain, out var a, out var b);
            double e = 0;
            for (int i = 0; i < a.Length; i++) e += 0.5 * (a[i] * (double)a[i] + b[i] * (double)b[i]);
            Assert.That(e, Is.EqualTo(1).Within(1e-3), "unit energy");

            var pre = plain;
            pre.PreDelay = 0.1;
            ImpulseResponses.Shape(l, r, Rate, pre, out var p, out _);
            Assert.That(p.Length, Is.EqualTo(a.Length + Rate / 10).Within(2));
            for (int i = 0; i < Rate / 10 - 1; i++) Assert.That(p[i], Is.EqualTo(0f), "silence before the pre-delay");

            var big = plain;
            big.Size = 1.5;
            ImpulseResponses.Shape(l, r, Rate, big, out var s, out _);
            Assert.That(s.Length, Is.EqualTo(a.Length * 1.5).Within(a.Length * 0.01), "size stretches the time");

            var mono = plain;
            mono.Width = 0;
            ImpulseResponses.Shape(l, r, Rate, mono, out var m1, out var m2);
            Assert.That(m1, Is.EqualTo(m2), "width 0 is mono");

            var rev = plain;
            rev.Reverse = true;
            ImpulseResponses.Shape(l, r, Rate, rev, out var v, out _);
            double early = 0, late = 0;
            for (int i = 0; i < v.Length / 4; i++) early += v[i] * (double)v[i];
            for (int i = 3 * v.Length / 4; i < v.Length; i++) late += v[i] * (double)v[i];
            Assert.That(late, Is.GreaterThan(early), "reversed: the tail first, the body last");

            var cut = plain;
            cut.HighCutHz = 2000;
            ImpulseResponses.Shape(l, r, Rate, cut, out var c, out _);
            var sa = Measure.Spectrum(a, 0, 16384);
            var sc = Measure.Spectrum(c, 0, 16384);
            double HfRatio(double[] sp)
            {
                double lo = 0, hi = 0;
                for (int i = 1; i < sp.Length; i++)
                {
                    double f = Measure.BinFrequency(i, 16384);
                    if (f < 2000) lo += sp[i] * sp[i]; else if (f > 6000) hi += sp[i] * sp[i];
                }
                return hi / lo;
            }
            Assert.That(HfRatio(sc), Is.LessThan(HfRatio(sa) * 0.1), "the high cut removes the highs");
        }

        [Test]
        public void AUserResponseAtAnotherRateIsResampledAndAnEchoLandsWhereItShould()
        {
            // A response of one tap 250 ms after the start, recorded at 44.1 kHz: at 48 kHz it must be 12000 samples in.
            var h = new float[22050];
            h[11025] = 1;
            var user = new FxUserData();
            user.SetIr("echo", h, null, 44100);
            var host = (ConvolutionHost)FxHost.Create(FxType.Convolution, Rate, user);
            host.V.Set(FxNew.Conv.Ir, FxNew.Conv.UserChoice);
            host.V.Set(FxNew.Conv.Size, FxNew.Conv.Table[FxNew.Conv.Size].Raw(1.0));
            host.V.Set(FxNew.Conv.LowCut, 0);
            host.V.Set(FxNew.Conv.HighCut, 1000);
            host.V.Set(FxNew.Conv.PreDelay, 0);
            host.V.Set(FxNew.Conv.Width, FxNew.Conv.Table[FxNew.Conv.Width].Raw(1.0));
            host.V.Set(FxNew.Conv.Output, FxNew.Conv.Table[FxNew.Conv.Output].Raw(0));
            host.Poll();
            host.Work();
            Assert.That(host.IsReady);
            var l = new float[256];
            var r = new float[256];
            var out1 = new float[30000];
            for (int at = 0; at < out1.Length; at += 256)
            {
                Array.Clear(l); Array.Clear(r);
                if (at == 0) { l[0] = 1; r[0] = 1; }
                host.Process(l, r, Math.Min(256, out1.Length - at));
                Array.Copy(l, 0, out1, at, Math.Min(256, out1.Length - at));
            }
            int peak = 0;
            for (int i = 0; i < out1.Length; i++) if (Math.Abs(out1[i]) > Math.Abs(out1[peak])) peak = i;
            Assert.That(peak, Is.EqualTo(12000).Within(2), "the echo is 250 ms after the click at 48 kHz");
        }

        [Test]
        public void ThePartitionedConvolverIsTheCpuOfAFewPercentAtOneAndFourSeconds()
        {
            var lines = new List<string>();
            foreach (double seconds in new[] { 1.0, 4.0 })
            {
                var user = new FxUserData();
                user.SetIr("noise", Decaying((int)(seconds * Rate), 5, seconds / 1.5), Decaying((int)(seconds * Rate), 6, seconds / 1.5), Rate);
                var p = new ParamValues(Params.Table);
                p.Set(FxRack.TypeParam(7), (int)FxType.Convolution);
                p.Set(FxRack.MixParam(7), 1000);
                p.Set(FxRack.GenericParam(7, FxNew.Conv.Ir), 1000);
                var rack = new FxRack(p);
                rack.User.SetIr("noise", Decaying((int)(seconds * Rate), 5, seconds / 1.5), Decaying((int)(seconds * Rate), 6, seconds / 1.5), Rate);
                rack.SetSampleRate(Rate);
                rack.BuildNow();
                var l = new float[256];
                var r = new float[256];
                var rng = new Random(5);
                for (int i = 0; i < 256; i++) { l[i] = (float)(rng.NextDouble() - 0.5) * 0.5f; r[i] = (float)(rng.NextDouble() - 0.5) * 0.5f; }
                var sl = (float[])l.Clone();
                var sr = (float[])r.Clone();
                for (int i = 0; i < 200; i++) { Array.Copy(sl, l, 256); Array.Copy(sr, r, 256); rack.Process(l, r, 256); }
                const int Blocks = 1200;
                double worst = 0;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < Blocks; i++)
                {
                    Array.Copy(sl, l, 256); Array.Copy(sr, r, 256);
                    long t0 = Stopwatch.GetTimestamp();
                    rack.Process(l, r, 256);
                    worst = Math.Max(worst, (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency);
                }
                double total = sw.Elapsed.TotalSeconds;
                double real = Blocks * 256.0 / Rate;
                lines.Add($"{seconds:0} s IR {100 * total / real:F2} % (worst block {worst * 1000:F2} ms of {256000.0 / Rate:F1})");
                Assert.That(rack.Latency, Is.EqualTo(0), "no latency");
            }
            Measure.Report("Convolution slot, stereo, 48 kHz, share of a core: " + string.Join(", ", lines));
            Assert.Pass();
        }

        // ---- the vocoder ------------------------------------------------------------------------------------------

        static double BandDb(float[] x, double lo, double hi)
        {
            var sp = Measure.Spectrum(x, 0, 16384);
            double e = 0;
            for (int i = 1; i < sp.Length; i++)
            {
                double f = Measure.BinFrequency(i, 16384);
                if (f >= lo && f < hi) e += sp[i] * sp[i];
            }
            return 10 * Math.Log10(e + 1e-30);
        }

        static float[] Vocode(ChannelVocoder v, float[] carrier, float[] mod)
        {
            var l = (float[])carrier.Clone();
            var r = (float[])carrier.Clone();
            for (int at = 0; at < l.Length; at += 256)
            {
                int n = Math.Min(256, l.Length - at);
                var cl = new float[256]; var cr = new float[256]; var m = new float[256];
                Array.Copy(l, at, cl, 0, n); Array.Copy(r, at, cr, 0, n); Array.Copy(mod, at, m, 0, n);
                v.Process(cl, cr, m, n);
                Array.Copy(cl, 0, l, at, n);
            }
            return l;
        }

        [Test]
        public void TheVocoderLetsTheCarrierThroughWhereTheModulatorIs()
        {
            int len = 65536;
            var carrier = Noise(len, 77, 0.5f);
            var mod = Measure.Sine(1000, len, 0.5);
            var v = new ChannelVocoder(Rate);
            var y = Vocode(v, carrier, mod);
            double at1k = BandDb(y, 800, 1250), low = BandDb(y, 100, 400), high = BandDb(y, 4000, 9000);
            Measure.Report($"Vocoder, 1 kHz modulator over noise: 0.8-1.25 kHz {at1k:F1} dB, 100-400 Hz {low - at1k:F1} dB, 4-9 kHz {high - at1k:F1} dB relative");
            Assert.That(low - at1k, Is.LessThan(-20));
            Assert.That(high - at1k, Is.LessThan(-20));

            // Silence in, silence out.
            var quiet = Vocode(new ChannelVocoder(Rate), carrier, new float[len]);
            Assert.That(Measure.Rms(quiet, 8192, 32768), Is.LessThan(1e-6));

            // A formant shift of +12 semitones moves where the carrier is let through up an octave.
            var shifted = new ChannelVocoder(Rate) { FormantSemitones = 12 };
            var z = Vocode(shifted, carrier, mod);
            double at2k = BandDb(z, 1600, 2500), at1kShifted = BandDb(z, 800, 1250);
            Measure.Report($"Vocoder, formant +12 st: 1.6-2.5 kHz {at2k:F1} dB, 0.8-1.25 kHz {at1kShifted - at2k:F1} dB relative");
            Assert.That(at1kShifted - at2k, Is.LessThan(-10));
        }

        [Test]
        public void TheVocoderBandLayoutIsLogSpacedAndSibilanceBringsTheModulatorsHighsBack()
        {
            var centres = Enumerable.Range(0, 16).Select(i => ChannelVocoder.Centre(16, 120, 10000, i)).ToArray();
            Measure.Report("Vocoder band centres (16 bands, 120 Hz-10 kHz): " + string.Join(" ", centres.Select(c => c.ToString("F0"))) + " Hz");
            Assert.That(centres[0], Is.EqualTo(120).Within(1e-9));
            Assert.That(centres[15], Is.EqualTo(10000).Within(1e-6));
            for (int i = 2; i < 16; i++) Assert.That(centres[i] / centres[i - 1], Is.EqualTo(centres[1] / centres[0]).Within(1e-9));

            int len = 32768;
            var carrier = Measure.Sine(220, len, 0.4);
            var hiss = Noise(len, 5, 0.6f);
            // Noise above 6 kHz as the modulator: through a high-pass of the same kind the vocoder uses.
            var hp = new Biquad(); hp.Set(BiquadType.HighPass, 7000, Rate, 0.7071);
            for (int i = 0; i < len; i++) hiss[i] = hp.Process(hp.Process(hiss[i]));
            var off = Vocode(new ChannelVocoder(Rate) { Sibilance = 0 }, carrier, hiss);
            var on = Vocode(new ChannelVocoder(Rate) { Sibilance = 1 }, carrier, hiss);
            double d = BandDb(on, 6500, 12000) - BandDb(off, 6500, 12000);
            Measure.Report($"Vocoder sibilance 100 % against 0 % on a hiss: {d:F1} dB more above 6.5 kHz");
            Assert.That(d, Is.GreaterThan(20));
        }

        [Test]
        public void TheInternalVoiceIsTheSameAfterEveryResetAndIsNeverSilentForLong()
        {
            var v = new VoiceModulator(Rate);
            var a = new float[Rate];
            v.Render(a, 4096);
            v.Reset();
            var b = new float[Rate];
            for (int i = 0; i < Rate; i += 256) { var t = new float[256]; v.Render(t, 256); Array.Copy(t, 0, b, i, Math.Min(256, Rate - i)); }
            v.Reset();
            var c = new float[Rate];
            v.Render(c, Rate);
            Assert.That(b.Take(Rate - 256), Is.EqualTo(c.Take(Rate - 256)), "the same samples after a reset, whatever the blocks");
            Measure.Report("Internal voice, rms a 100 ms (dB): " + string.Join(" ", Enumerable.Range(0, 10).Select(w => Measure.Db(Measure.Rms(c, w * 4800, 4800)).ToString("F0"))));
            double peak = c.Max(Math.Abs);
            Assert.That(peak, Is.GreaterThan(0.05).And.LessThan(1.5));
            for (int w = 0; w + 4800 <= Rate; w += 4800) Assert.That(Measure.Rms(c, w, 4800), Is.GreaterThan(0), "something in every 100 ms");
        }

        // ---- the rack types ---------------------------------------------------------------------------------------

        static ParamValues Slot0(FxType type, params int[] x)
        {
            var p = new ParamValues(Params.Table);
            p.Set(FxRack.TypeParam(7), (int)type);
            p.Set(FxRack.MixParam(7), 1000);
            for (int k = 0; k < x.Length; k++) p.Set(FxRack.GenericParam(7, k), x[k]);
            return p;
        }

        [Test]
        public void ARackSlotOfConvolutionIsAReverbTailWithNoLatency()
        {
            var p = Slot0(FxType.Convolution, 250);   // IR: Room
            var rack = new FxRack(p);
            rack.SetSampleRate(Rate);
            rack.BuildNow();
            Assert.That(rack.Latency, Is.EqualTo(0));
            var l = new float[256];
            var r = new float[256];
            var tail = new List<float>();
            for (int b = 0; b < 200; b++)
            {
                Array.Clear(l); Array.Clear(r);
                if (b == 0) { l[0] = 0.5f; r[0] = 0.5f; }
                rack.Process(l, r, 256);
                tail.AddRange(l);
            }
            var y = tail.ToArray();
            Assert.That(y.All(float.IsFinite));
            Assert.That(Measure.Rms(y, 0, 4800), Is.GreaterThan(1e-4), "a tail");
            double early = Measure.Rms(y, 0, 4800), late = Measure.Rms(y, 40000, 4800);
            Assert.That(late, Is.LessThan(early * 0.1), "that dies away");
        }

        [Test]
        public void AChangedResponseIsBuiltInTheBackgroundAndTakenOverWithoutAGap()
        {
            var p = Slot0(FxType.Convolution, 250);
            var rack = new FxRack(p);
            rack.SetSampleRate(Rate);
            rack.BuildNow();
            var l = new float[256];
            var r = new float[256];
            var rng = new Random(3);
            void Block()
            {
                for (int i = 0; i < 256; i++) { l[i] = (float)(rng.NextDouble() - 0.5) * 0.3f; r[i] = l[i]; }
                rack.Process(l, r, 256);
            }
            for (int i = 0; i < 100; i++) Block();
            double before = Measure.Rms(l, 0, 256);
            // The plate, at a quarter size: asked for, built off the audio thread, then taken over.
            p.Set(FxRack.GenericParam(7, FxNew.Conv.Ir), 700);
            p.Set(FxRack.GenericParam(7, FxNew.Conv.Size), 250);
            var sw = Stopwatch.StartNew();
            double worst = 0;
            int blocks = 0;
            while (sw.Elapsed.TotalSeconds < 10)
            {
                Block();
                blocks++;
                worst = Math.Max(worst, Math.Abs(l[255] - l[254]));
                if (blocks > 400 && rack.PendingFor(s => (int)FxType.Convolution, i => p[i]) == false) break;
                Thread.Sleep(1);
            }
            Assert.That(rack.PendingFor(s => (int)FxType.Convolution, i => p[i]), Is.False, "the builder finishes");
            for (int i = 0; i < 100; i++) Block();
            Assert.That(Measure.Rms(l, 0, 256), Is.GreaterThan(before * 0.1), "and the slot still sounds");
            Assert.That(l.All(float.IsFinite));
        }

        [Test]
        public void ARackSlotOfVocoderTalksThroughTheInternalVoiceAndASample()
        {
            var user = new FxUserData();
            var p = Slot0(FxType.Vocoder, 0, 500, 500, 500, 500, 500, 500, 500, 500, 500);
            var rack = new FxRack(p);
            rack.SetSampleRate(Rate);
            rack.BuildNow();
            var l = new float[256];
            var r = new float[256];
            var saw = new float[Rate * 2];
            double phase = 0;
            for (int i = 0; i < saw.Length; i++) { phase += 110.0 / Rate; if (phase >= 1) phase -= 1; saw[i] = (float)(phase * 2 - 1) * 0.3f; }
            var y = new float[saw.Length];
            for (int at = 0; at + 256 <= saw.Length; at += 256)
            {
                Array.Copy(saw, at, l, 0, 256); Array.Copy(saw, at, r, 0, 256);
                rack.Process(l, r, 256);
                Array.Copy(l, 0, y, at, 256);
            }
            Assert.That(y.All(float.IsFinite));
            double rms = Measure.Rms(y, 0, y.Length);
            Measure.Report($"Vocoder slot, 110 Hz saw carrier, internal voice: output rms {Measure.Db(rms):F1} dB against {Measure.Db(Measure.Rms(saw, 0, saw.Length)):F1} dB in");
            Assert.That(rms, Is.GreaterThan(1e-3).And.LessThan(2));
            // The syllables are in the envelope: some 100 ms windows are much louder than others.
            var win = Enumerable.Range(0, 19).Select(w => Measure.Rms(y, w * 4800, 4800)).ToArray();
            Measure.Report("Vocoder slot, internal voice, rms a 100 ms (dB): " + string.Join(" ", win.Select(w => Measure.Db(w).ToString("F0"))));
            Assert.That(win.Max() / Math.Max(1e-9, win.Min()), Is.GreaterThan(3), "the voice's syllables move the carrier");

            // The Sample source with nothing imported plays the internal voice; with a clip it plays that.
            rack.User.SetModulator("click", Measure.Sine(500, Rate, 0.5), Rate);
            p.Set(FxRack.GenericParam(7, FxNew.Voc.Source), 1000);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 10 && rack.PendingFor(s => (int)FxType.Vocoder, i => p[i])) Thread.Sleep(2);
            Assert.That(rack.PendingFor(s => (int)FxType.Vocoder, i => p[i]), Is.False, "the clip is resampled in the background");
            var z = new float[Rate];
            for (int at = 0; at + 256 <= Rate; at += 256)
            {
                Array.Copy(saw, at, l, 0, 256); Array.Copy(saw, at, r, 0, 256);
                rack.Process(l, r, 256);
                Array.Copy(l, 0, z, at, 256);
            }
            // A steady 500 Hz modulator: a steady output (the syllables are gone).
            var win2 = Enumerable.Range(1, 8).Select(w => Measure.Rms(z, w * 4800, 4800)).ToArray();
            Assert.That(win2.Max() / Math.Max(1e-9, win2.Min()), Is.LessThan(1.5), "a steady modulator makes a steady vocoded sound");
        }

        [Test]
        public void VocoderCpu()
        {
            var lines = new List<string>();
            foreach (int bands in new[] { 8, 16, 32 })
            {
                int x = (bands - 8) * 1000 / 24;
                var p = Slot0(FxType.Vocoder, 0, x);
                var rack = new FxRack(p);
                rack.SetSampleRate(Rate);
                rack.BuildNow();
                var l = new float[256];
                var r = new float[256];
                var rng = new Random(5);
                var sl = new float[256];
                for (int i = 0; i < 256; i++) sl[i] = (float)(rng.NextDouble() - 0.5) * 0.5f;
                for (int i = 0; i < 100; i++) { Array.Copy(sl, l, 256); Array.Copy(sl, r, 256); rack.Process(l, r, 256); }
                const int Blocks = 1000;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < Blocks; i++) { Array.Copy(sl, l, 256); Array.Copy(sl, r, 256); rack.Process(l, r, 256); }
                lines.Add($"{bands} bands {100 * sw.Elapsed.TotalSeconds / (Blocks * 256.0 / Rate):F2} %");
            }
            Measure.Report("Vocoder slot, stereo carrier, 48 kHz, share of a core: " + string.Join(", ", lines));
            Assert.Pass();
        }
    }
}
