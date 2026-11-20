using ReBuzz.Core.Clips;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// Warp quality against an objective reference (docs/specs/neo-audio.md section 11, WP1). The generators of NeoStretchTests take the
    /// tempo and the pitch, so a drum loop, a bass line or a pad made at the target tempo (or pitch) is the ideal render of a change.
    /// <see cref="WriteWp1Renders"/> writes every warp mode and the ideal for the nine blind-test groups (and for real excerpts, from outside
    /// the repository) as float WAVs; tools/warp_quality.py measures them (log-spectral distance, transient smearing, clicks).
    /// </summary>
    [TestFixture, NonParallelizable]
    public class WarpQualityTests
    {
        const int Rate = 44100;
        const double Bpm = 120;

        /// <summary>The nine blind-test groups: excerpt, change, the tempo factor, the semitones.</summary>
        internal static readonly (string Name, string Excerpt, double Tempo, double Semis)[] Groups =
        {
            ("g01-drums-20-slower", "drums", 0.8, 0), ("g02-drums-25-faster", "drums", 1.25, 0), ("g03-drums-7-up", "drums", 1.0, 7),
            ("g04-bass-20-slower", "bass", 0.8, 0), ("g05-bass-25-faster", "bass", 1.25, 0), ("g06-bass-7-up", "bass", 1.0, 7),
            ("g07-pad-20-slower", "pad", 0.8, 0), ("g08-pad-25-faster", "pad", 1.25, 0), ("g09-pad-7-up", "pad", 1.0, 7),
        };

        internal static AudioSource Excerpt(string name, double bpm, double pitch) => name switch
        {
            "drums" => NeoStretchTests.Drums(bpm, 8, Rate, pitch),
            "bass" => NeoStretchTests.Bass(bpm, 8, pitch),
            _ => NeoStretchTests.Pad(bpm, 8, pitch),
        };

        /// <summary>
        /// Writes, per group, <c>group--mode.wav</c> (32-bit float, the left channel, at the engine's own level) for the modes original (the
        /// source), ideal (generated at the target tempo and pitch), repitch, beats, complex and pro; and the same for the real excerpts
        /// <c>realbass.wav</c> and <c>realdrums.wav</c> in WP1_REAL_DIR (8 s, 44.1 kHz, 32-bit float, any channels), at 20 % slower and 25 %
        /// faster, which have no ideal. Set WP1_RENDER_DIR. Run by hand; the real audio is never in the repository.
        /// </summary>
        [Test, Explicit("writes the WP1 renders; set WP1_RENDER_DIR (and WP1_REAL_DIR)")]
        public void WriteWp1Renders()
        {
            string dir = Environment.GetEnvironmentVariable("WP1_RENDER_DIR") ?? throw new InvalidOperationException("WP1_RENDER_DIR");
            Directory.CreateDirectory(dir);
            var modes = new[] { WarpMode.Repitch, WarpMode.Beats, WarpMode.Complex, WarpMode.Pro };
            foreach (var (name, excerpt, tempo, semis) in Groups)
            {
                var source = Excerpt(excerpt, Bpm, 1);
                Write(dir, name, "original", source.Left, source.SampleRate);
                var ideal = Excerpt(excerpt, Bpm * tempo, Math.Pow(2, semis / 12));
                Write(dir, name, "ideal", ideal.Left, ideal.SampleRate);
                foreach (var m in modes)
                    Write(dir, name, m.ToString().ToLowerInvariant(), NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, m, semis), Bpm * tempo, 0, 8), Rate);                Write(dir, name, "pro-old", RenderOld(source, Bpm, Bpm * tempo, semis, 8, WarpMode.Pro), Rate);
                Write(dir, name, "complex-old", RenderOld(source, Bpm, Bpm * tempo, semis, 8, WarpMode.Complex), Rate);
            }
            string? real = Environment.GetEnvironmentVariable("WP1_REAL_DIR");
            if (real == null) return;
            int n = 10;
            foreach (var (file, tag) in new[] { ("realbass", "realbass"), ("realdrums", "realdrums") })
            {
                var path = Path.Combine(real, file + ".wav");
                if (!File.Exists(path)) continue;
                var (l, r, rate) = ReadWav(path);
                var source = new AudioSource(l, r, rate);
                double beats = source.Seconds * Bpm / 60;
                foreach (var (label, tempo) in new[] { ("20-slower", 0.8), ("25-faster", 1.25) })
                {
                    string name = $"g{n++}-{tag}-{label}";
                    Write(dir, name, "original", l, rate);
                    foreach (var m in modes)
                        Write(dir, name, m.ToString().ToLowerInvariant(), NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, m, 0), Bpm * tempo, 0, beats, 256, rate), rate);
                    PeriodicVoice.Enabled = false;
                    try
                    {
                        Write(dir, name, "pro-old", NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro, 0), Bpm * tempo, 0, beats, 256, rate), rate);
                        Write(dir, name, "complex-old", NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Complex, 0), Bpm * tempo, 0, beats, 256, rate), rate);
                    }
                    finally { PeriodicVoice.Enabled = true; }
                }
            }
        }

        /// <summary>Pro as it was before the time-domain path (the stretcher alone).</summary>
        internal static float[] RenderOld(AudioSource source, double sourceBpm, double bpm, double semis, double beats, WarpMode mode = WarpMode.Pro)
        {
            PeriodicVoice.Enabled = false;
            try { return NeoStretchTests.Play(NeoStretchTests.Clip(source, sourceBpm, mode, semis), bpm, 0, beats); }
            finally { PeriodicVoice.Enabled = true; }
        }

        static void Write(string dir, string group, string mode, float[] x, int rate)
        {
            using var w = new BinaryWriter(File.Create(Path.Combine(dir, $"{group}--{mode}.wav")));
            int bytes = x.Length * 4;
            w.Write("RIFF"u8.ToArray()); w.Write(36 + bytes); w.Write("WAVEfmt "u8.ToArray());
            w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)32);
            w.Write("data"u8.ToArray()); w.Write(bytes);
            foreach (float v in x) w.Write(v);
        }

        /// <summary>A 16-bit or 32-bit float PCM WAV, any channel count (the first two are kept).</summary>
        static (float[] L, float[] R, int Rate) ReadWav(string path)
        {
            var b = File.ReadAllBytes(path);
            int channels = 1, rate = Rate, bits = 16, format = 1, at = 12;
            int dataAt = -1, dataLen = 0;
            while (at + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, at, 4);
                int len = BitConverter.ToInt32(b, at + 4);
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(b, at + 8);
                    channels = BitConverter.ToInt16(b, at + 10);
                    rate = BitConverter.ToInt32(b, at + 12);
                    bits = BitConverter.ToInt16(b, at + 22);
                }
                else if (id == "data") { dataAt = at + 8; dataLen = Math.Min(len, b.Length - dataAt); break; }
                at += 8 + len + (len & 1);
            }
            if (dataAt < 0) throw new InvalidDataException(path);
            int bytesPer = bits / 8, frames = dataLen / (bytesPer * channels);
            var l = new float[frames];
            var r = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float Sample(int c)
                {
                    int o = dataAt + (i * channels + c) * bytesPer;
                    return format == 3 ? BitConverter.ToSingle(b, o) : BitConverter.ToInt16(b, o) / 32768f;
                }
                l[i] = Sample(0);
                r[i] = channels > 1 ? Sample(1) : l[i];
            }
            return (l, channels > 1 ? r : l, rate);
        }

        // ---- The time-domain path -----------------------------------------------------------------------

        static void NeedsTheLibrary()
        {
            if (Environment.GetEnvironmentVariable("NEO_STRETCH_OFF") == "1") Assert.Ignore("the library is turned off by NEO_STRETCH_OFF");
            Assert.That(NeoStretch.Available, Is.True, "the neostretch library is not here: " + NeoStretch.Failure);
        }

        /// <summary>
        /// Clicks, as tools/warp_quality.py counts them: the peak of the signal above 1.5 kHz (a 4th-order Butterworth high-pass) in each
        /// millisecond that stands 4 dB over the loudest such peak of the reference within 12 ms either side, and over -60 dBFS; spikes within
        /// 5 ms of the first of a run are one.
        /// </summary>
        internal static int Clicks(float[] reference, float[] x)
        {
            static float[] Peaks(float[] y)
            {
                // Two biquads (RBJ high-pass at 1.5 kHz, Q 0.5412 and 1.3065 make a 4th-order Butterworth), run forward and then backward over
                // the signal (zero phase, as scipy's sosfiltfilt does in the tool): a causal pass finds a third as many clicks in the same audio.
                var h = (float[])y.Clone();
                foreach (double q in new[] { 0.5412, 1.3065 })
                {
                    double w0 = 2 * Math.PI * 1500 / Rate, alpha = Math.Sin(w0) / (2 * q), c = Math.Cos(w0);
                    double b0 = (1 + c) / 2, b1 = -(1 + c), b2 = (1 + c) / 2, a0 = 1 + alpha, a1 = -2 * c, a2 = 1 - alpha;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        Array.Reverse(h);
                        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
                        for (int i = 0; i < h.Length; i++)
                        {
                            double v = h[i], o = (b0 * v + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
                            x2 = x1; x1 = v; y2 = y1; y1 = o;
                            h[i] = (float)o;
                        }
                    }
                }
                int n = Rate / 1000, m = h.Length / n;
                var peaks = new float[m];
                for (int i = 0; i < m; i++)
                {
                    float best = 0;
                    for (int j = i * n; j < (i + 1) * n; j++) best = Math.Max(best, Math.Abs(h[j]));
                    peaks[i] = best;
                }
                return peaks;
            }
            var pr = Peaks(reference);
            var px = Peaks(x);
            int count = 0, last = -100;
            double over = Math.Pow(10, 4 / 20.0), floor = Math.Pow(10, -60 / 20.0);
            for (int i = 0; i < Math.Min(pr.Length, px.Length); i++)
            {
                float cover = 0;
                for (int j = Math.Max(0, i - 12); j <= Math.Min(pr.Length - 1, i + 12); j++) cover = Math.Max(cover, pr[j]);
                if (px[i] > over * cover && px[i] > floor)
                {
                    if (i - last > 5)
                    {
                        count++;
                        last = i;
                    }
                }
            }
            return count;
        }

        [Test]
        public void TheAnalysisFindsABassLineAndNothingElse()
        {
            var bass = PeriodicAnalysis.Of(Excerpt("bass", Bpm, 1));
            Assert.That(bass, Is.Not.Null);
            Assert.That(bass!.Use, Is.True, $"a bass line: voiced {bass.VoicedShare:F2}, low {bass.LowShare:F2}");
            Assert.That(bass.Onsets.Length, Is.EqualTo(7), "one onset per note after the first, not one per decaying harmonic");
            for (int k = 0; k < bass.Onsets.Length; k++)
                Assert.That(bass.Onsets[k], Is.EqualTo((k + 1) * 22050).Within(300), $"note {k + 2} starts on its beat");
            foreach (var name in new[] { "drums", "pad" })
            {
                var info = PeriodicAnalysis.Of(Excerpt(name, Bpm, 1));
                Assert.That(info?.Use ?? false, Is.False, $"{name} stay with the stretcher (voiced {info?.VoicedShare:F2}, low {info?.LowShare:F2})");
            }
            Assert.That(PeriodicAnalysis.Of(NeoStretchTests.Tone(0.2)), Is.Null.Or.Property("Use").False, "a fraction of a second is nothing to analyse");
        }

        [Test, Combinatorial]
        public void ABassLineStretchedInProHasNoClicksTheIdealLacks([Values(0.8, 1.25)] double tempo)
        {
            NeedsTheLibrary();
            var source = Excerpt("bass", Bpm, 1);
            var ideal = Excerpt("bass", Bpm * tempo, 1).Left;
            var now = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro), Bpm * tempo, 0, 8);
            var before = RenderOld(source, Bpm, Bpm * tempo, 0, 8);
            int clicksNow = Clicks(ideal, now), clicksBefore = Clicks(ideal, before);
            TestContext.Out.WriteLine($"tempo {tempo}: clicks {clicksBefore} before, {clicksNow} now (the ideal's own: {Clicks(ideal, ideal)})");
            Assert.That(clicksBefore, Is.GreaterThan(10), "the detector flags the stretcher's bass, as the blind test heard it");
            Assert.That(clicksNow, Is.LessThanOrEqualTo(2), "the time-domain path keeps the cycle");
        }

        [Test, Combinatorial]
        public void TheTimeDomainPathKeepsTheNotesOnTheirBeats([Values(0.8, 1.25)] double tempo)
        {
            NeedsTheLibrary();
            var source = Excerpt("bass", Bpm, 1);
            var now = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro), Bpm * tempo, 0, 8);
            for (int k = 1; k < 8; k++)
            {
                int at = (int)Math.Round(k * 22050 / tempo);
                double before = 0, after = 0;
                for (int i = at - 700; i < at - 100; i++) before = Math.Max(before, Math.Abs(now[i]));
                for (int i = at + 100; i < at + 700; i++) after = Math.Max(after, Math.Abs(now[i]));
                Assert.That(after, Is.GreaterThan(0.1), $"note {k + 1} sounds at its beat");
                Assert.That(before, Is.LessThan(0.08 * Math.Max(0.2, after)), $"and not before it (tempo {tempo})");
            }
        }

        [Test]
        public void ATransposedBassStaysWithTheStretcher()
        {
            NeedsTheLibrary();
            var source = Excerpt("bass", Bpm, 1);
            var a = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro, 7), Bpm, 0, 8);
            var b = RenderOld(source, Bpm, Bpm, 7, 8);
            Assert.That(a, Is.EqualTo(b), "a transposition is not the time-domain path's job: the output is the stretcher's, sample for sample");
        }

        [Test]
        public void TheTimeDomainPathDoesNotDependOnTheBlockSize()
        {
            NeedsTheLibrary();
            var source = Excerpt("bass", Bpm, 1);
            var big = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro), Bpm * 0.8, 0, 8, 1024);
            foreach (int block in new[] { 64, 256, 333 })
            {
                var x = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro), Bpm * 0.8, 0, 8, block);
                double err = 0, sig = 0;
                for (int i = 0; i < x.Length; i++) { err += Math.Pow(x[i] - big[i], 2); sig += Math.Pow(big[i], 2); }
                Assert.That(10 * Math.Log10(err / sig + 1e-20), Is.LessThan(-60), $"block {block} against 1024");
            }
        }

        [Test]
        public void TheTimeDomainPathAllocatesNothingAndIsCheap()
        {
            NeedsTheLibrary();
            var source = Excerpt("bass", Bpm, 1);
            var p = NeoStretchTests.Clip(source, Bpm, WarpMode.Pro);
            var voice = new ClipVoice();
            voice.Prepare(p);
            var l = new float[256];
            var r = new float[256];
            double bps = Bpm * 0.8 / 60.0 / Rate;
            double beat = 0;
            for (int b = 0; b < 50; b++) { Array.Clear(l); Array.Clear(r); voice.Render(p, beat, bps, Rate, l, r, 0, 256); beat += 256 * bps; }
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            double worst = double.MaxValue, mean = 0;
            for (int run = 0; run < 3; run++)                           // the best of three: another process taking the core is not this path's time
            {
                double w = 0;
                long total = 0;
                for (int b = 0; b < 250; b++)
                {
                    Array.Clear(l); Array.Clear(r);
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    voice.Render(p, beat, bps, Rate, l, r, 0, 256);
                    long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    w = Math.Max(w, (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    total += t1 - t0;
                    beat += 256 * bps;
                }
                if (w < worst) { worst = w; mean = total * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 250; }
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            TestContext.Out.WriteLine($"time-domain voice, 256-sample blocks at 44.1 kHz: mean {mean:F3} ms, worst {worst:F3} ms of 5.8 ms; allocated {bytes} bytes");
            Assert.That(bytes, Is.EqualTo(0), "nothing allocated while it plays");
            Assert.That(worst, Is.LessThan(5.8), "a block of it in less than the block's time, whatever else the machine is doing");
        }

        [Test]
        public void TheStartOfABassClipFromItsMiddleHasNoStepAndTheLoopSeamIsSmooth()
        {
            NeedsTheLibrary();
            var source = Excerpt("bass", Bpm, 1);
            var p = NeoStretchTests.Clip(source, Bpm, WarpMode.Pro);
            // From the middle of a note, at another tempo, then round the end of the audio.
            var x = NeoStretchTests.Play(p, Bpm * 0.8, 3.3, 8);
            double step = 0;
            for (int i = 20; i < x.Length - 20; i++) step = Math.Max(step, Math.Abs(x[i] - x[i - 1]));
            Assert.That(step, Is.LessThan(0.35), "the saw's own edge is 0.3 at most; a splice would be more");
            Assert.That(x.Take(200).Max(Math.Abs), Is.GreaterThan(0.02), "it starts sounding where it starts");
        }
    }
}
