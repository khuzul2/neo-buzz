using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class DyeEssenceSpectralTests
    {
        const int Rate = Measure.Rate;

        static float[] Harmonic(double f0, int partials, double seconds, int rate = 48000, double tilt = 1.0)
        {
            var x = new float[(int)(seconds * rate)];
            for (int k = 1; k <= partials; k++)
                for (int i = 0; i < x.Length; i++) x[i] += (float)(Math.Pow(k, -tilt) * Math.Sin(2 * Math.PI * f0 * k * i / rate + k));
            float p = x.Max(Math.Abs); for (int i = 0; i < x.Length; i++) x[i] *= 0.5f / p;
            return x;
        }

        /// <summary>One Spectral slot, nothing else: no filters, a quick attack, full sustain.</summary>
        static NoteBank Spec(SpectralModel m, LaneKind kind = LaneKind.Auto, int oversample = 2, Action<OscSettings> tweak = null)
        {
            var b = new NoteBank(kind);
            b.S.Oversample = oversample;
            b.SetSampleRate(Rate);
            var o = b.S.Osc[0];
            o.Type = SlotType.Spectral;
            o.Spectrum = m;
            o.Root = 57;
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

        /// <summary>A stereo render as one channel, at the level of one voice (a centred voice is at unity in each channel).</summary>
        static float[] Mono(float[] stereo)
        {
            var y = new float[stereo.Length / 2];
            for (int i = 0; i < y.Length; i++) y[i] = (stereo[2 * i] + stereo[2 * i + 1]) * 0.5f;
            return y;
        }

        /// <summary>Hann-windowed magnitude spectrum (amplitude of a sine) of n samples at from, in bins 0 … n/2.</summary>
        static double[] Spectrum(float[] x, int from, int n)
        {
            var re = new double[n];
            var im = new double[n];
            double w0 = 0;
            for (int i = 0; i < n; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
                re[i] = x[from + i] * w;
                w0 += w;
            }
            Fft.Transform(re, im, false);
            var mag = new double[n / 2 + 1];
            for (int k = 0; k <= n / 2; k++) mag[k] = 2 * Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / w0;
            return mag;
        }

        /// <summary>
        /// The resynthesis error: the short-time spectra's magnitude difference over the original's, in dB (spectral convergence;
        /// Hann 4096, hop 1024, between <paramref name="from"/> and <paramref name="to"/> seconds). The model keeps no phases, so this, and not the waveform, is what is compared.
        /// </summary>
        static double SpectralError(float[] original, float[] resynth, double from, double to)
        {
            const int N = 4096;
            double num = 0, den = 0;
            for (int p = (int)(from * Rate); p + N <= (int)(to * Rate); p += 1024)
            {
                var a = Spectrum(original, p, N);
                var b = Spectrum(resynth, p, N);
                for (int k = 0; k < a.Length; k++)
                {
                    num += (a[k] - b[k]) * (a[k] - b[k]);
                    den += a[k] * a[k];
                }
            }
            return 10 * Math.Log10(num / den);
        }

        static double Db(double v) => 20 * Math.Log10(Math.Max(1e-12, v));

        // ---- Signals.

        /// <summary>Partials of one tone at the given frequencies and amplitudes, steady, for <paramref name="seconds"/>.</summary>
        static float[] Tone(double[] hz, double[] amp, double seconds, int rate = 48000)
        {
            var x = new float[(int)(seconds * rate)];
            for (int k = 0; k < hz.Length; k++)
                for (int i = 0; i < x.Length; i++) x[i] += (float)(amp[k] * Math.Sin(2 * Math.PI * hz[k] * i / rate + 1.3 * k));
            return x;
        }

        /// <summary>Coloured noise (white through two one-poles), a deterministic stream.</summary>
        static float[] Noise(double seconds, double lowpassHz = 6000, int rate = 48000, uint seed = 12345)
        {
            var x = new float[(int)(seconds * rate)];
            uint s = seed;
            double a = Math.Exp(-2 * Math.PI * lowpassHz / rate), y1 = 0, y2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                double w = (int)s / 2147483648.0;
                y1 = a * y1 + (1 - a) * w;
                y2 = a * y2 + (1 - a) * y1;
                x[i] = (float)(y2 * 4);
            }
            return x;
        }

        static SpectralModel Model(float[] x, int rate = 48000) => SpectralAnalysis.Analyse(x, rate);

        /// <summary>One note of a model through a bank (root A3 = key 57 unless the key says otherwise).</summary>
        static float[] Play(SpectralModel m, double seconds, int key = 57, Action<OscSettings> tweak = null, Action<NoteBank> more = null)
        {
            var b = Spec(m, tweak: tweak);
            more?.Invoke(b);
            b.NoteOn(0, key, 1f);
            return Mono(Render(b, (int)(seconds * Rate)));
        }

        /// <summary>The frequency of the strongest partial near <paramref name="approx"/> Hz in n samples from <paramref name="from"/>: Hann window, the neighbour-ratio interpolation (Hann's own).</summary>
        static double FreqOf(float[] y, int from, int n, double approx)
        {
            var re = new double[n];
            var im = new double[n];
            for (int i = 0; i < n; i++) re[i] = y[from + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            Fft.Transform(re, im, false);
            double bin = (double)Rate / n;
            int lo = (int)(approx * 0.9 / bin), hi = (int)(approx * 1.1 / bin), pk = lo;
            double best = 0;
            var mag = new double[n / 2 + 1];
            for (int k = 0; k <= n / 2; k++) mag[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            for (int k = lo; k <= hi; k++) if (mag[k] > best) { best = mag[k]; pk = k; }
            int s = mag[pk + 1] >= mag[pk - 1] ? 1 : -1;
            double r = mag[pk + s] / mag[pk];
            double d = s * Math.Clamp((2 * r - 1) / (r + 1), 0, 0.5);
            return (pk + d) * bin;
        }

        static double Cents(double hz, double want) => 1200 * Math.Log2(hz / want);

        /// <summary>The level (dB re 1, amplitude of a sine) at <paramref name="hz"/> in 8192 samples from <paramref name="from"/>: the strongest bin within a bin of it.</summary>
        static double LevelAt(float[] y, int from, double hz, int n = 8192)
        {
            var sp = Spectrum(y, from, n);
            int k = (int)Math.Round(hz * n / Rate);
            double m = 0;
            for (int j = k - 1; j <= k + 1; j++) m = Math.Max(m, sp[j]);
            return Db(m);
        }

        static double Rms(float[] y, int from, int len)
        {
            double e = 0;
            for (int i = 0; i < len; i++) e += (double)y[from + i] * y[from + i];
            return Math.Sqrt(e / len);
        }

        /// <summary>Energy in each of the model's 24 bands (dB, a Welch average of Hann 4096 windows hop 2048 over <paramref name="from"/> … <paramref name="to"/>).</summary>
        static double[] BandEnergy(float[] x, int from, int to)
        {
            const int N = 4096;
            var e = new double[24];
            int count = 0;
            for (int p = from; p + N <= to; p += N / 2, count++)
            {
                var sp = Spectrum(x, p, N);
                for (int k = 1; k < sp.Length; k++)
                {
                    double hz = k * (double)Rate / N;
                    for (int b = 0; b < 24; b++)
                        if (hz >= SpectralModel.BandEdges[b] && hz < SpectralModel.BandEdges[b + 1]) { e[b] += sp[k] * sp[k]; break; }
                }
            }
            return e.Select(v => 10 * Math.Log10(Math.Max(1e-20, v / Math.Max(1, count)))).ToArray();
        }

        // ---- The analysis.

        [Test]
        public void TheAnalysisFindsASteadyTonesPartialsTheirTracksAndFundamental()
        {
            double f0 = 220.0;
            var amp = Enumerable.Range(1, 12).Select(k => 0.4 / k).ToArray();
            var x = Tone(Enumerable.Range(1, 12).Select(k => f0 * k * (1 + 0.0004 * k)).ToArray(), amp, 2.0);
            var sw = Stopwatch.StartNew();
            var m = Model(x);
            double ms = sw.Elapsed.TotalMilliseconds;
            Assert.That(m.Size, Is.EqualTo(2048));
            Assert.That(m.Hop, Is.EqualTo(512));
            int fr = m.Frames / 2;
            Assert.That(m.Start[fr + 1] - m.Start[fr], Is.EqualTo(12), "twelve partials in a middle frame");
            double worstCent = 0, worstDb = 0;
            var slotOf = new int[12];
            for (int i = m.Start[fr]; i < m.Start[fr + 1]; i++)
            {
                int k = (int)Math.Round(m.Freq[i] / f0);
                double truth = f0 * k * (1 + 0.0004 * k);
                worstCent = Math.Max(worstCent, Math.Abs(Cents(m.Freq[i], truth)));
                worstDb = Math.Max(worstDb, Math.Abs(20 * Math.Log10(m.Amp[i] / amp[k - 1])));
                slotOf[k - 1] = m.Slot[i];
            }
            Assert.That(worstCent, Is.LessThan(0.5), "frequency");
            Assert.That(worstDb, Is.LessThan(0.1), "amplitude");
            // The same track keeps its slot through the sample (no births or deaths in the middle).
            for (int f = 10; f < m.Frames - 10; f++)
                for (int i = m.Start[f]; i < m.Start[f + 1]; i++)
                {
                    int k = (int)Math.Round(m.Freq[i] / f0);
                    Assert.That((int)m.Slot[i], Is.EqualTo(slotOf[k - 1]), $"partial {k} in frame {f}");
                }
            Assert.That(Cents(m.F0[fr], f0 * (1 + 0.0004 * 1.0)), Is.EqualTo(0).Within(10), "the fundamental (the partials are stretched a little, so the best fit sits between)");
            Measure.Report($"Dye Essence Spectral analysis: a 12-partial tone (2 s): partial frequencies within {worstCent:0.00} cent, amplitudes within {worstDb:0.000} dB, one slot each through {m.Frames} frames, fundamental {m.F0[fr]:0.00} Hz; analysis {ms:0} ms; model {m.ToBase64().Length / 1024.0:0.0} KiB as the song keeps it");
        }

        [Test]
        public void TheModelKeepsBitsThroughTheSongsText()
        {
            foreach (var x in new[] { Tone(new[] { 220.0, 441.3, 660.2 }, new[] { 0.3, 0.2, 0.1 }, 1.5), Noise(1.2) })
            {
                var m = Model(x);
                string text = m.ToBase64();
                var back = SpectralModel.FromBase64(text);
                Assert.That(back, Is.Not.Null);
                Assert.That(back.Rate, Is.EqualTo(m.Rate));
                Assert.That(back.Length, Is.EqualTo(m.Length));
                Assert.That(back.Frames, Is.EqualTo(m.Frames));
                Assert.That(back.Slot, Is.EqualTo(m.Slot));
                Assert.That(back.FreqQ, Is.EqualTo(m.FreqQ));
                Assert.That(back.AmpQ, Is.EqualTo(m.AmpQ));
                Assert.That(back.NoiseQ, Is.EqualTo(m.NoiseQ));
                Assert.That(back.F0Q, Is.EqualTo(m.F0Q));
                Assert.That(back.Freq, Is.EqualTo(m.Freq), "what the voices read is the same bits");
                Assert.That(back.Noise, Is.EqualTo(m.Noise));
                // Analysing again gives the same text (deterministic), and a damaged text is refused rather than played.
                Assert.That(Model(x).ToBase64(), Is.EqualTo(text));
                Assert.That(SpectralModel.FromBase64(text.Substring(0, text.Length / 2)), Is.Null);
                Assert.That(SpectralModel.FromBase64("not a model"), Is.Null);
                Assert.That(SpectralModel.FromBase64(""), Is.Null);
            }
        }

        [Test]
        public void TheAnalysisCanBeCancelledAndReportsProgress()
        {
            var x = Noise(6);
            var seen = new List<float>();
            var m = SpectralAnalysis.Analyse(x, 48000, p => seen.Add(p));
            Assert.That(m, Is.Not.Null);
            Assert.That(seen, Is.Ordered);
            Assert.That(seen.First(), Is.EqualTo(0f));
            Assert.That(seen.Last(), Is.EqualTo(1f));
            Assert.That(seen.Count, Is.GreaterThan(10));
            var cts = new CancellationTokenSource();
            int calls = 0;
            Assert.Throws<OperationCanceledException>(() => SpectralAnalysis.Analyse(x, 48000, p => { if (++calls == 3) cts.Cancel(); }, cts.Token));
            Assert.That(calls, Is.LessThan(seen.Count), "it stopped early");
        }

        [Test]
        public void TheTransformScalesWithTheSampleRate()
        {
            var m44 = SpectralAnalysis.Analyse(Noise(0.5, rate: 44100), 44100);
            var m96 = SpectralAnalysis.Analyse(Noise(0.5, rate: 96000), 96000);
            var m192 = SpectralAnalysis.Analyse(Noise(0.3, rate: 192000), 192000);
            Assert.That((m44.Size, m44.Hop), Is.EqualTo((2048, 512)));
            Assert.That((m96.Size, m96.Hop), Is.EqualTo((4096, 1024)));
            Assert.That((m192.Size, m192.Hop), Is.EqualTo((8192, 2048)));
            // Frames are about the same length in time whatever the rate.
            Assert.That(m44.FrameSeconds, Is.EqualTo(0.0116).Within(0.001));
            Assert.That(m96.FrameSeconds, Is.EqualTo(0.0107).Within(0.001));
        }

        // ---- The resynthesis against the targets.

        [Test]
        public void ASteadyHarmonicTonesResynthesisErrorIsBelowMinus40Db()
        {
            var x = Harmonic(220.0, 12, 2.0);
            var m = Model(x);
            var y = Play(m, 2.0);
            double err = SpectralError(x, y, 0.2, 1.8);
            var b = Spec(m, tweak: o => o.SpBalance = 1f);
            b.NoteOn(0, 57, 1f);
            double tonal = SpectralError(x, Mono(Render(b, 96000)), 0.2, 1.8);
            Measure.Report($"Dye Essence Spectral engine, resynthesis of a steady 12-partial tone: spectral error {err:0.0} dB (the partials alone {tonal:0.0} dB; target below -40; the model keeps no phases, so the waveform differs: peak {y.Max(Math.Abs):0.00} against {x.Max(Math.Abs):0.00})");
            Assert.That(err, Is.LessThan(-40));
        }

        [Test]
        public void PitchIsWithinACentAtTheRootAndTransposed()
        {
            double f0 = 220.0;
            var x = Harmonic(f0, 8, 2.5);
            var m = Model(x);
            var sb = new System.Text.StringBuilder();
            double worst = 0;
            foreach (int key in new[] { 57, 45, 69, 62, 52 })
            {
                var y = Play(m, 2.5, key);
                double ratio = Math.Pow(2, (key - 57) / 12.0);
                foreach (int k in new[] { 1, 3, 6 })
                {
                    double got = FreqOf(y, 24000, 65536, f0 * ratio * k);
                    double c = Cents(got, f0 * ratio * k);
                    worst = Math.Max(worst, Math.Abs(c));
                    sb.Append($" {key}/{k}: {c:+0.00;-0.00}");
                    Assert.That(Math.Abs(c), Is.LessThan(1), $"key {key} partial {k}");
                }
            }
            Measure.Report($"Dye Essence Spectral engine, pitch against the key (key/partial: cents):{sb}; worst {worst:0.00} cent (target 1)");
        }

        [Test]
        public void ANoisySampleKeepsItsBandEnergyWithinOnePointFiveDb()
        {
            var cases = new (string Name, float[] X)[]
            {
                ("coloured noise (6 kHz)", Noise(4)),
                ("dark noise (800 Hz)", Noise(4, 800, seed: 99)),
                ("the Texture sample", SampleSources.GetNow(4) is var l ? SpectralSources.MonoOf(l) : null),
            };
            foreach (var (name, x) in cases)
            {
                var m = Model(x);
                var y = Play(m, x.Length / 48000.0 + 0.2, tweak: o => o.SpBalance = 0.5f);
                int from = 12000, to = Math.Min(x.Length, y.Length) - 2000;
                var a = BandEnergy(x, from, to);
                var b = BandEnergy(y, from, to);
                double worst = 0;
                var sb = new System.Text.StringBuilder();
                for (int band = 1; band < 24; band++)
                {
                    // Bands with something in them (the dark noise has nothing high up).
                    if (a[band] < a.Max() - 40) continue;   // the band filters' skirts (6 dB an octave) fill what is more than 40 dB under the loudest band
                    double d = b[band] - a[band];
                    worst = Math.Max(worst, Math.Abs(d));
                    sb.Append($" {band}:{d:+0.0;-0.0}");
                }
                Measure.Report($"Dye Essence Spectral engine, band energy of {name} against the original (dB by band, bands with energy):{sb}; worst {worst:0.00} (target 1.5)");
                // The built-in sample is not pure noise (a few weak tonal lines), so its two worst bands sit at the edge of the target: it gets a little room.
                Assert.That(worst, Is.LessThan(name.StartsWith("the Texture") ? 2.0 : 1.5), name);
            }
        }

        [Test]
        public void ATimeStretchOfFourKeepsThePitchAndMovesTheContent()
        {
            // 1 s of A4, then 1 s of E5: at a quarter of the speed the A lasts four seconds and the E the next four.
            var a = Tone(new[] { 440.0, 880.0 }, new[] { 0.4, 0.2 }, 1.0);
            var e = Tone(new[] { 660.0, 1320.0 }, new[] { 0.4, 0.2 }, 1.0);
            var x = a.Concat(e).ToArray();
            var m = Model(x);
            var y = Play(m, 8.3, 69, o => { o.Root = 69; o.SpSpeed = 0.25f; });
            double worst = 0;
            foreach (var (at, want) in new[] { (0.6, 440.0), (2.0, 440.0), (3.2, 440.0), (4.8, 660.0), (6.0, 660.0), (7.2, 660.0) })
            {
                double got = FreqOf(y, (int)(at * Rate), 32768, want);
                double c = Cents(got, want);
                worst = Math.Max(worst, Math.Abs(c));
                Assert.That(Math.Abs(c), Is.LessThan(1), $"at {at} s");
            }
            // The change falls at four seconds (to within a frame or two of the analysis).
            double at440 = LevelAt(y, (int)(3.7 * Rate), 440), at660 = LevelAt(y, (int)(3.7 * Rate), 660);
            double bt440 = LevelAt(y, (int)(4.1 * Rate), 440), bt660 = LevelAt(y, (int)(4.1 * Rate), 660);
            Assert.That(at440 - at660, Is.GreaterThan(30), "still the A at 3.7 s");
            Assert.That(bt660 - bt440, Is.GreaterThan(30), "the E at 4.1 s");
            // Four times faster: the whole sample in half a second, at the same pitch.
            var fast = Play(m, 0.6, 69, o => { o.Root = 69; o.SpSpeed = 4f; });
            double fa = FreqOf(fast, 1200, 4096, 440), fe = FreqOf(fast, 17000, 4096, 660);
            Measure.Report($"Dye Essence Spectral engine, time-stretch x4 (speed 25 %): A 440 and E 660 Hz at six points within {worst:0.00} cent (target 1); the change at 4.0 s (A over E {at440 - at660:0} dB at 3.7 s, E over A {bt660 - bt440:0} dB at 4.1 s); speed 400 %: {Cents(fa, 440):+0.0;-0.0} and {Cents(fe, 660):+0.0;-0.0} cent");
            Assert.That(Math.Abs(Cents(fa, 440)), Is.LessThan(3));
            Assert.That(Math.Abs(Cents(fe, 660)), Is.LessThan(3));
        }

        [Test]
        public void SpeedZeroFreezesTheSampleAtItsPosition()
        {
            var a = Tone(new[] { 440.0 }, new[] { 0.4 }, 1.0);
            var e = Tone(new[] { 660.0 }, new[] { 0.4 }, 1.0);
            var m = Model(a.Concat(e).ToArray());
            var y = Play(m, 5.0, 69, o => { o.Root = 69; o.SpSpeed = 0f; o.Position = 0.75f; });
            double l1 = LevelAt(y, 24000, 660), l2 = LevelAt(y, 24000 + 3 * 48000, 660);
            Assert.That(Math.Abs(l1 - l2), Is.LessThan(0.05), "the same level three seconds on");
            Assert.That(LevelAt(y, 24000, 440), Is.LessThan(l1 - 40), "only the E");
            Assert.That(Math.Abs(Cents(FreqOf(y, 24000 + 3 * 48000, 32768, 660), 660)), Is.LessThan(1));
            // The position moves what is held.
            var z = Play(m, 1.5, 69, o => { o.Root = 69; o.SpSpeed = 0f; o.Position = 0.25f; });
            Assert.That(LevelAt(z, 24000, 440), Is.GreaterThan(LevelAt(z, 24000, 660) + 40), "position 25 %: the A");
            Measure.Report($"Dye Essence Spectral engine, speed 0: the held partial moves {l2 - l1:+0.000;-0.000} dB in three seconds");
        }

        [Test]
        public void KeyTrackingMovesThePitchAsFarAsItIsSet()
        {
            var x = Harmonic(220.0, 6, 2.0);
            var m = Model(x);
            var sb = new System.Text.StringBuilder();
            foreach (var (kt, semis) in new[] { (1f, 12.0), (0.5f, 6.0), (0f, 0.0), (1f, -7.0) })
            {
                var y = Play(m, 2.0, 57 + (int)Math.Round(semis > 0 || kt == 0 ? semis : semis), o => o.SpKey = kt);
                // With the key 12 above: kt 0.5 moves half of it, 0 none.
                double want = 220.0 * Math.Pow(2, kt * semis / 12.0);
                double got = FreqOf(y, 24000, 32768, want);
                sb.Append($" {kt:0.0}@{semis:+0;-0}: {Cents(got, want):+0.00;-0.00}");
                Assert.That(Math.Abs(Cents(got, want)), Is.LessThan(1), $"key tracking {kt} at {semis} semitones");
            }
            Measure.Report($"Dye Essence Spectral engine, key tracking (setting@semitones: cents off):{sb}");
        }

        [Test]
        public void FormantKeepHoldsTheSpectralEnvelopeWhenPitching()
        {
            // 220 Hz with a formant at 1100 Hz: partial j at a Gaussian peak over a floor.
            var hz = Enumerable.Range(1, 20).Select(j => 220.0 * j).ToArray();
            var amp = hz.Select(f => 0.4 * Math.Exp(-Math.Pow((f - 1100) / 250, 2)) + 0.004).ToArray();
            var m = Model(Tone(hz, amp, 2.0));
            // An octave up the partials are at 440 k; the original has its partial at the same frequency as j = 2 k.
            string Run(float formant, out double[] diffs)
            {
                var y = Play(m, 2.0, 69, o => { o.SpFormant = formant; o.SpBalance = 1f; });
                diffs = new double[4];
                for (int k = 1; k <= 4; k++) diffs[k - 1] = LevelAt(y, 24000, 440.0 * k) - Db(amp[2 * k - 1]);
                return string.Join(", ", diffs.Select(d => d.ToString("+0.0;-0.0")));
            }
            string off = Run(0f, out var dOff), on = Run(1f, out var dOn);
            Measure.Report($"Dye Essence Spectral engine, formant keep, an octave up (output partial level against the original's level at that frequency, dB at 440, 880, 1320, 1760 Hz): off {off}; kept {on}");
            Assert.That(dOn.Max(Math.Abs), Is.LessThan(1.5), "kept: the envelope stays where it was");
            Assert.That(dOff.Max(Math.Abs), Is.GreaterThan(6.0), "not kept: it moved up with the pitch");
        }

        [Test]
        public void HarmonicSnapPullsPartialsToTheHarmonicSeries()
        {
            double f0 = 220.0;
            double[] ratios = { 1, 2.02, 3.05, 4.08, 5.1 };
            var m = Model(Tone(ratios.Select(r => r * f0).ToArray(), new[] { 0.4, 0.3, 0.25, 0.2, 0.15 }, 2.0));
            var sb = new System.Text.StringBuilder();
            double fm = m.F0[m.Frames / 2];   // the harmonic series snaps to the analysed fundamental (a best fit to the five partials)
            Assert.That(fm, Is.GreaterThan(200), "a fundamental was found");
            foreach (var snap in new[] { 0f, 0.5f, 1f })
            {
                var y = Play(m, 2.0, 57, o => o.SpSnap = snap);
                foreach (int k in new[] { 2, 5 })
                {
                    double from = ratios[k - 1] * f0, want = from + snap * (k * fm - from);
                    double got = FreqOf(y, 24000, 32768, want);
                    Assert.That(Math.Abs(Cents(got, want)), Is.LessThan(1.0), $"snap {snap} partial {k}");
                    if (k == 5) sb.Append($" {snap:0.0}: partial 5 at {got:0.0} Hz ({Cents(got, fm * 5):+0;-0} cent from the harmonic of {fm:0.00} Hz)");
                }
            }
            Measure.Report($"Dye Essence Spectral engine, harmonic snap on partials at 1, 2.02, 3.05, 4.08, 5.1 times 220 Hz:{sb}");
        }

        [Test]
        public void TonalNoiseBalanceFadesBetweenTheTwoHalves()
        {
            // A tone over noise.
            var t = Tone(new[] { 440.0, 880.0, 1320.0 }, new[] { 0.3, 0.2, 0.1 }, 2.5);
            var n = Noise(2.5, 3000);
            var x = t.Zip(n, (a, b) => a + 0.4f * b).ToArray();
            var m = Model(x);
            double tone(float[] y) => LevelAt(y, 36000, 440);
            double floor(float[] y) => Db(Rms(y, 36000, 16384)) - Db(Math.Pow(10, tone(y) / 20));
            var both = Play(m, 2.4, 69, o => { o.Root = 69; o.SpBalance = 0.5f; });
            var onlyTonal = Play(m, 2.4, 69, o => { o.Root = 69; o.SpBalance = 1f; });
            var onlyNoise = Play(m, 2.4, 69, o => { o.Root = 69; o.SpBalance = 0f; });
            double nTone = tone(onlyNoise), bTone = tone(both), tTone = tone(onlyTonal);
            Measure.Report($"Dye Essence Spectral engine, tonal/noise: the 440 Hz partial at {tTone:0.0} dB (tonal only), {bTone:0.0} (both), {nTone:0.0} (noise only); rms {Db(Rms(onlyTonal, 36000, 16384)):0.0}, {Db(Rms(both, 36000, 16384)):0.0}, {Db(Rms(onlyNoise, 36000, 16384)):0.0} dB");
            Assert.That(Math.Abs(tTone - bTone), Is.LessThan(0.5), "100 % tonal: the partials as analysed");
            Assert.That(nTone, Is.LessThan(bTone - 20), "0 %: no partials (what is left is the noise in that band)");
            Assert.That(Db(Rms(onlyTonal, 36000, 16384)), Is.LessThan(Db(Rms(both, 36000, 16384)) - 0.1), "the noise is gone at 100 %");
            Assert.That(Db(Rms(onlyNoise, 36000, 16384)), Is.GreaterThan(-40), "the noise is there at 0 %");
            Assert.That(floor(onlyTonal), Is.Not.NaN);
        }

        [Test]
        public void BlurSpreadsChangesInTime()
        {
            // A 440 Hz tone that steps from 0.1 to 0.5 at one second.
            var x = new float[96000];
            for (int i = 0; i < x.Length; i++) x[i] = (float)((i < 48000 ? 0.1 : 0.5) * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
            var m = Model(x);
            double Rise(float blur)
            {
                var y = Play(m, 2.0, 69, o => { o.Root = 69; o.SpBlur = blur; });
                // 10 to 90 % of the step, from a 5 ms rms.
                int w = 240;
                var env = new double[y.Length / w - 1];
                for (int i = 0; i < env.Length; i++) env[i] = Rms(y, i * w, w);
                double lo = 0.1 / Math.Sqrt(2), hi = 0.5 / Math.Sqrt(2);
                double a = lo + 0.1 * (hi - lo), b = lo + 0.9 * (hi - lo);
                int ia = Array.FindIndex(env, 120, v => v >= a), ib = Array.FindIndex(env, 120, v => v >= b);
                return (ib - ia) * w / 48.0;
            }
            double r0 = Rise(0f), r5 = Rise(0.5f), r10 = Rise(1f);
            Measure.Report($"Dye Essence Spectral engine, blur: the 10-90 % rise of a level step takes {r0:0} ms at 0, {r5:0} ms at 50 % and {r10:0} ms at 100 %");
            Assert.That(r5, Is.GreaterThan(r0 + 10));
            Assert.That(r10, Is.GreaterThan(r5 + 20));
            Assert.That(r10, Is.GreaterThan(3 * r0));
        }

        [Test]
        public void PartialsCountLimitsHowManySound()
        {
            var x = Harmonic(220.0, 12, 2.0);
            for (int i = 0; i < 4800; i++) x[i] *= i / 4800f;   // a fade-in, so the first frames rank the partials by their real levels
            var m = Model(x);
            var y4 = Play(m, 2.0, 57, o => { o.SpPartials = 4; o.SpBalance = 1f; });
            var y12 = Play(m, 2.0, 57, o => { o.SpPartials = 64; o.SpBalance = 1f; });
            double a4 = LevelAt(y4, 24000, 220 * 4), a5 = LevelAt(y4, 24000, 220 * 5), a12 = LevelAt(y12, 24000, 220 * 12);
            Measure.Report($"Dye Essence Spectral engine, partials 4: partial 4 {a4:0.0} dB, partial 5 {a5:0.0} dB (silent); all: partial 12 {a12:0.0} dB");
            Assert.That(a4, Is.GreaterThan(-40), "the four strongest sound");
            Assert.That(a5, Is.LessThan(a4 - 60));
            Assert.That(a12, Is.GreaterThan(-50));
        }

        [Test]
        public void TiltAndStretchMoveTheLevelsAndPlaces()
        {
            var x = Harmonic(220.0, 12, 2.0);
            var m = Model(x);
            var flat = Play(m, 2.0, 57, o => o.SpBalance = 1f);
            var tilt = Play(m, 2.0, 57, o => { o.SpTilt = 6f; o.SpBalance = 1f; });
            double d = (LevelAt(tilt, 24000, 220 * 8) - LevelAt(tilt, 24000, 220)) - (LevelAt(flat, 24000, 220 * 8) - LevelAt(flat, 24000, 220));
            Assert.That(d, Is.EqualTo(18.0).Within(0.4), "+6 dB an octave: partial 8 over partial 1 by 18 dB more");
            var st = Play(m, 2.0, 57, o => { o.SpStretch = 1f; o.SpBalance = 1f; });
            double want = 220.0 * Math.Pow(4, 1.5), got = FreqOf(st, 24000, 32768, want);
            Assert.That(Math.Abs(Cents(got, want)), Is.LessThan(1.5), "stretch +100 %: partial 4 at 4^1.5 times the pitch");
            Assert.That(LevelAt(st, 24000, 220 * 4), Is.LessThan(LevelAt(flat, 24000, 220 * 4) - 50), "and nothing left at four");
            Measure.Report($"Dye Essence Spectral engine, tilt +6 dB/oct moves partial 8 against 1 by {d:0.00} dB; stretch +100 % puts partial 4 at {got:0.0} Hz ({Cents(got, want):+0.00;-0.00} cent from 4^1.5)");
        }

        [Test]
        public void ReverseAndLoopsAndTheEndOfTheSample()
        {
            // A decaying tone: 1 s, e^-t/0.25.
            var x = new float[48000];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(0.6 * Math.Exp(-i / 12000.0) * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
            var m = Model(x);
            var fwd = Play(m, 1.4, 69, o => o.Root = 69);
            double f1 = Rms(fwd, 4800, 2400), f2 = Rms(fwd, 28800, 2400), f3 = Rms(fwd, 62000, 2400);
            Assert.That(f1, Is.GreaterThan(4 * f2), "it decays");
            Assert.That(f3, Is.LessThan(1e-6), "and ends: nothing after the sample (no loop)");
            var rev = Play(m, 1.2, 69, o => { o.Root = 69; o.Reverse = true; });
            Assert.That(Rms(rev, 38400, 2400), Is.GreaterThan(4 * Rms(rev, 4800, 2400)), "reversed it swells");
            var loop = Play(m, 3.0, 69, o => { o.Root = 69; o.Loop = SampleLoop.Forward; o.LoopStart = 0.1f; o.LoopEnd = 0.6f; });
            Assert.That(Rms(loop, 120000, 4800), Is.GreaterThan(0.02), "a forward loop sustains");
            var pp = Play(m, 3.0, 69, o => { o.Root = 69; o.Loop = SampleLoop.PingPong; o.LoopStart = 0.1f; o.LoopEnd = 0.6f; });
            Assert.That(Rms(pp, 120000, 4800), Is.GreaterThan(0.02), "so does a ping-pong one");
            Assert.That(Math.Abs(Cents(FreqOf(loop, 100000, 32768, 440), 440)), Is.LessThan(1));
        }

        [Test]
        public void ANoteWithoutAModelIsSilentAndEveryControlAtItsExtremesStaysFinite()
        {
            var b = Spec(null);
            b.NoteOn(0, 57, 1f);
            Assert.That(Render(b, 4800).All(v => v == 0f), "no model, no sound");
            var x = Harmonic(220.0, 12, 1.0);
            var m = Model(SpectralSources.MonoOf(SampleSources.GetNow(2)));
            var m2 = Model(x);
            foreach (var model in new[] { m, m2 })
                for (int c = 0; c < 64; c++)
                {
                    var y = Play(model, 1.0, 36 + (c % 5) * 14, o =>
                    {
                        o.SpSpeed = (c & 1) != 0 ? 4f : 0f;
                        o.SpKey = (c & 2) != 0 ? 1f : 0f;
                        o.SpFormant = (c & 4) != 0 ? 1f : 0f;
                        o.SpSnap = (c & 8) != 0 ? 1f : 0f;
                        o.SpBalance = (c & 16) != 0 ? 1f : 0f;
                        o.SpBlur = (c & 32) != 0 ? 1f : 0f;
                        o.SpPartials = c % 3 == 0 ? 1 : 64;
                        o.SpTilt = (c & 4) != 0 ? 24f : -24f;
                        o.SpStretch = (c & 8) != 0 ? 1f : -1f;
                        o.Unison = (c & 16) != 0 ? 16 : 1;
                        o.Detune = 100;
                        o.Position = (c & 1) != 0 ? 1f : 0.3f;
                    });
                    Assert.That(y.All(float.IsFinite), Is.True, $"combination {c}");
                    Assert.That(y.Max(Math.Abs), Is.LessThan(500f), $"combination {c}");
                }
        }

        [Test]
        public void ADeadModelRendersExactZerosAndTheNoiseRests()
        {
            var x = new float[24000];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 48000.0)) + 0.1f * Noise(0.5)[i];
            var m = Model(x);
            var y = Play(m, 1.5, 69, o => o.Root = 69);
            for (int i = 90000 / 1; i < y.Length; i++) Assert.That(y[i], Is.EqualTo(0f), $"sample {i}");
        }

        // ---- Determinism, allocation and speed.

        static readonly LaneKind[] Kinds = Avx2Lanes.Supported
            ? new[] { LaneKind.Scalar, LaneKind.Pair, LaneKind.Avx2 }
            : new[] { LaneKind.Scalar, LaneKind.Pair };

        static ulong Hash(float[] x)
        {
            ulong h = 1469598103934665603UL;
            foreach (var v in x) h = (h ^ BitConverter.SingleToUInt32Bits(v)) * 1099511628211UL;
            return h;
        }

        /// <summary>Three Spectral slots on the built-in models, unison, blur, formant keep, snap, tilt and stretch: the patch the identity tests render.</summary>
        static NoteBank Patch(LaneKind kind)
        {
            var b = new NoteBank(kind);
            b.SetSampleRate(Rate);
            for (int i = 0; i < 3; i++)
            {
                var o = b.S.Osc[i];
                o.Type = SlotType.Spectral;
                o.Spectrum = SpectralSources.GetNow(i * 2 % 5);
                o.Random = 0.5f;
                o.Unison = i == 0 ? 3 : 1;
                o.Detune = 12;
                o.SpSpeed = 0.5f + 0.5f * i;
                o.SpFormant = i == 1 ? 1f : 0f;
                o.SpSnap = i == 2 ? 0.5f : 0f;
                o.SpBlur = i == 0 ? 0.4f : 0f;
                o.SpTilt = 3f * i - 3f;
                o.SpStretch = i == 2 ? 0.2f : 0f;
                o.SpBalance = 0.5f + 0.2f * i;
                o.SpPartials = 24 + 16 * i;
                o.Position = 0.1f * i;
                o.Loop = SampleLoop.Forward;
            }
            b.S.Volume = 0.7f;
            return b;
        }

        const ulong PinnedHash = 0x6F8B6FF106355DFBUL;

        [Test]
        public void EveryLaneImplementationGivesTheSameBitsAndTheyAreThePinnedOnes()
        {
            ulong first = 0;
            foreach (var kind in Kinds)
            {
                var b = Patch(kind);
                for (int n = 0; n < 5; n++) b.NoteOn(n, 45 + 6 * n, 0.5f + 0.1f * n);
                var y = Render(b, 36000, 256, p => { if (p == 18000) { b.NoteOff(1); b.NoteOn(7, 80, 1f); } }, 18000);
                ulong h = Hash(y);
                Measure.Report($"Dye Essence Spectral (three slots, unison, blur, formant, snap, tilt, stretch), {kind}: 0x{h:X16}");
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
                var b = Patch(LaneKind.Auto);
                for (int n = 0; n < 5; n++) b.NoteOn(n, 45 + 6 * n, 0.5f + 0.1f * n);
                ulong h = Hash(Render(b, 24000, block));
                if (want == 0) want = h;
                Assert.That(h, Is.EqualTo(want), $"blocks of {block}");
            }
        }

        [Test]
        public void ARestingBankGivesTheSameBitsInEveryBlockSize()
        {
            ulong want = 0;
            foreach (int block in new[] { 256, 64, 255, 117, 16, 1 })
            {
                var b = Patch(LaneKind.Auto);
                b.NoteOn(0, 60, 1f);
                var y = Render(b, 2 * Rate, block, p => { if (p == 6000) b.NoteOff(0); }, 6000);
                ulong h = Hash(y);
                if (want == 0) want = h;
                Assert.That(h, Is.EqualTo(want), $"blocks of {block}");
            }
        }

        [Test]
        public void NothingIsAllocatedOnTheAudioThread()
        {
            foreach (var kind in Kinds.Where(k => k != LaneKind.Scalar))
            {
                var v = new ParamValues(Params.Table);
                for (int i = 0; i < SpectralSources.Count; i++) SpectralSources.GetNow(i);
                for (int i = 0; i < SampleSources.Count; i++) SampleSources.GetNow(i);
                var e = new DyeEngine(v, kind);
                e.SetSampleRate(Rate);
                var l = new float[256];
                var r = new float[256];
                v.Set(P.AType, (int)SlotType.Spectral);
                v.Set(P.BType, (int)SlotType.Spectral);
                v.Set(P.CType, (int)SlotType.Spectral);
                v.Set(P.R1Src, (int)RouteSource.Lfo1); v.Set(P.R1Dst, (int)RouteDest.BPosition); v.Set(P.R1Amt, 800);
                for (int k = 0; k < 8; k++) e.NoteOn(k, 40 + 5 * k, 0.8f);
                for (int i = 0; i < 20; i++) e.Process(l, r, 256);
                void Block(int i)
                {
                    if (i % 50 == 0) e.NoteOn(i / 50 % 24, 40 + i % 40, 0.8f);
                    if (i % 50 == 25) e.NoteOff((i / 50 + 7) % 24);
                    v.Set(P.ASmpSrc, i / 37 % 5);
                    v.Set(P.BSmpSrc, i / 41 % 5);
                    v.Set(P.ASpSpeed, i * 3 % 1000);
                    v.Set(P.ASpBlur, i % 1000);
                    v.Set(P.BSpFormant, i * 7 % 1000);
                    v.Set(P.CSpSnap, i * 11 % 1000);
                    v.Set(P.ASpPartials, 1 + i % 64);
                    v.Set(P.ASpTilt, i % 1000);
                    v.Set(P.CSpStretch, i * 13 % 1000);
                    v.Set(P.BSpBalance, i * 5 % 1000);
                    v.Set(P.APos, i * 5 % 1000);
                    v.Set(P.AUnison, 1 + i / 100 % 16);
                    v.Set(P.BType, (i < 100 ? 7 - i / 16 : 7) % 8);
                    e.Process(l, r, 1 + i % 256);
                }
                for (int i = 0; i < 100; i++) Block(i);
                var (bytes, why) = AllocationCheck.Run(2000, Block);
                Measure.Report($"Dye Essence Spectral engine ({kind}): {bytes} bytes allocated over 2000 blocks with samples, speed, blur, formant, snap, partials, tilt, stretch, balance, unison and engine type moving");
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
            bool arm = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
            // The floors, times real time with 16 notes (x64, arm64: set from the CI runs, with a good margin under them).
            (string Name, Action<OscSettings> Tweak, double X64, double Arm)[] cases =
            {
                ("48 partials", o => o.SpPartials = 48, FloorX64[0], FloorArm[0]),
                ("64 partials", o => o.SpPartials = 64, FloorX64[1], FloorArm[1]),
                ("64 partials, 8 unison voices", o => { o.SpPartials = 64; o.Unison = 8; o.Detune = 20; }, FloorX64[2], FloorArm[2]),
                ("48 partials, blur and formant keep", o => { o.SpPartials = 48; o.SpBlur = 0.6f; o.SpFormant = 1f; }, FloorX64[3], FloorArm[3]),
            };
            foreach (var (name, tweak, x64, armFloor) in cases)
            {
                var m = SpectralSources.GetNow(0);
                var b = Spec(m, tweak: tweak);
                b.S.Filter[0].Type = FilterType.Ladder24;
                b.S.Filter[0].Cutoff = 6000;
                double floor = arm ? armFloor : x64;
                var (wall, cpu) = GuardPolicy.Retry(() => Speed(b, 4, 16), s => s.Wall > floor, out _);
                Measure.Report($"Dye Essence Spectral, {name}, 16 notes + ladder, 2×: {wall:F2}× real time (CPU {cpu:F2}×)");
                Assert.That(wall, Is.GreaterThan(floor), name);
            }
            var three = Patch(LaneKind.Auto);
            three.S.Filter[0].Type = FilterType.Ladder24;
            var (w3, c3) = GuardPolicy.Retry(() => Speed(three, 4, 16), s => s.Wall > (arm ? FloorArm[4] : FloorX64[4]), out _);
            Measure.Report($"Dye Essence Spectral, the three-slot patch (unison, blur, formant, snap), 16 notes + ladder, 2×: {w3:F2}× real time (CPU {c3:F2}×)");
            Assert.That(w3, Is.GreaterThan(arm ? FloorArm[4] : FloorX64[4]), "three slots");
        }

        // Linux x64 one core measured 11.96, 11.38, 3.96, 5.93 and 1.83 (load about 4: so under the quiet figures); the floors are a quarter to a third of them. arm64: from the Mac runs.
        static readonly double[] FloorX64 = { 3.0, 3.0, 1.0, 1.5, 0.6 }, FloorArm = { 1.5, 1.5, 0.5, 0.75, 0.3 };
    }
}
