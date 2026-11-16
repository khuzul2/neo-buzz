using ReBuzz.Core.Clips;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// The audio clip engine's hardening (docs/specs/neo-audio.md, NA1 review): non-finite audio and settings, extreme tempo,
    /// transposition, warp markers and clip lengths, other sample rates, loop and clip ends, tempo detection's failures, and a
    /// long soak that plays every warp mode for ten minutes of render time without allocating or producing NaN.
    /// </summary>
    [TestFixture]
    public class AudioHardeningTests
    {
        const int Rate = 44100;
        static readonly WarpMode[] Modes = { WarpMode.Off, WarpMode.Repitch, WarpMode.Beats, WarpMode.Complex };

        static AudioSource Tone(double seconds, int rate = Rate, bool stereo = true, double freq = 220)
        {
            var l = new float[(int)(seconds * rate)];
            for (int i = 0; i < l.Length; i++) l[i] = (float)(0.4 * Math.Sin(2 * Math.PI * freq * i / rate) * (1 + 0.5 * Math.Sin(2 * Math.PI * 3 * i / rate)));
            if (!stereo) return new AudioSource(l, l, rate);
            var r = new float[l.Length];
            for (int i = 0; i < r.Length; i++) r[i] = (float)(0.3 * Math.Sin(2 * Math.PI * freq * 1.5 * i / rate));
            return new AudioSource(l, r, rate);
        }

        static AudioSource Clicks(double bpm, int beats, int rate = Rate)
        {
            int perBeat = (int)Math.Round(rate * 60 / bpm);
            var x = new float[perBeat * beats];
            for (int b = 0; b < beats; b++)
                for (int i = 0; i < rate / 50 && b * perBeat + i < x.Length; i++)
                    x[b * perBeat + i] = (float)(0.8 * Math.Sin(2 * Math.PI * 1000 * i / rate) * Math.Exp(-i / (0.005 * rate)));
            return new AudioSource(x, x, rate);
        }

        static AudioClipParams Clip(AudioSource s, double bpm, WarpMode mode, double semitones = 0, float gain = 1f, WarpMap? map = null)
        {
            map ??= WarpMap.Straight(s.Length, s.Seconds * bpm / 60);
            return new AudioClipParams(s, map, mode, AudioAnalysis.Slices(s, map, AudioAnalysis.Onsets(s)), gain, semitones);
        }

        /// <summary>Plays beats of a clip as the engine does (blocks, the beat wrapping at the clip's end like a loop). Returns the peak; fails on NaN.</summary>
        static float PlayFinite(ClipVoice voice, AudioClipParams p, double bpm, double beats, int rate = Rate, int block = 256, bool loop = true, double from = 0)
        {
            voice.Prepare(p);
            double bps = bpm / 60.0 / rate;
            var l = new float[block];
            var r = new float[block];
            float peak = 0;
            double length = Math.Max(0.01, p.EndBeat);
            long total = (long)(beats / bps);
            for (long at = 0; at < total; at += block)
            {
                Array.Clear(l); Array.Clear(r);
                double beat = from + at * bps;
                if (loop) beat %= length;
                voice.Render(p, beat, bps, rate, l, r, 0, block);
                for (int i = 0; i < block; i++)
                {
                    Assert.That(float.IsFinite(l[i]) && float.IsFinite(r[i]), Is.True, $"finite at {at + i} ({p.Mode})");
                    peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
                }
            }
            return peak;
        }

        // ---- Non-finite input ------------------------------------------------------------------

        [Test]
        public void NonFiniteAudioIsSilencedWhereItEntersSoItNeverReachesTheOutput()
        {
            var x = new float[Rate * 2];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(0.4 * Math.Sin(i * 0.05));
            x[1000] = float.NaN; x[5000] = float.PositiveInfinity; x[9000] = float.NegativeInfinity;
            var s = new AudioSource(x, x, Rate);
            Assert.That(s.Left.All(float.IsFinite), Is.True);
            Assert.That(ReferenceEquals(s.Left, s.Right), Is.True, "mono stays mono");
            Assert.That(x[1000], Is.NaN, "the caller's array is left as it was");
            foreach (var mode in Modes)
                Assert.That(PlayFinite(new ClipVoice(), Clip(s, 120, mode), 120, 3), Is.LessThan(8f), mode.ToString());
        }

        [Test]
        public void ADamagedStereoFileKeepsItsGoodChannel()
        {
            var l = new float[1000];
            var r = new float[1000];
            Array.Fill(l, 0.25f);
            Array.Fill(r, float.NaN);
            var s = new AudioSource(l, r, Rate);
            Assert.That(s.Left[10], Is.EqualTo(0.25f));
            Assert.That(s.Right.All(v => v == 0f), Is.True);
        }

        [Test]
        public void NonFiniteSettingsPlayAsDefaults()
        {
            var s = Tone(1);
            var map = WarpMap.Straight(s.Length, 2);
            var p = new AudioClipParams(s, map, WarpMode.Complex, null, float.NaN, double.NaN);
            Assert.That((p.Gain, p.Semitones, p.PitchRatio), Is.EqualTo((1f, 0.0, 1.0)));
            var q = new AudioClipParams(s, map, WarpMode.Complex, null, float.PositiveInfinity, double.PositiveInfinity);
            Assert.That(float.IsFinite(q.Gain) && double.IsFinite(q.PitchRatio), Is.True);
            PlayFinite(new ClipVoice(), p, 120, 2);
        }

        [Test]
        public void APositionThatIsNoPlaceInTheAudioReadsSilenceNotNaN()
        {
            var s = Tone(0.1);
            foreach (double position in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1e300, -1e300, 4e15 })
            {
                s.Read(position, out float l, out float r);
                Assert.That((l, r), Is.EqualTo((0f, 0f)), position.ToString());
            }
        }

        [Test]
        public void ANonFiniteBeatOrTempoIsSilenceAndNeverThrows()
        {
            var p = Clip(Tone(1), 120, WarpMode.Repitch);
            var voice = new ClipVoice();
            voice.Prepare(p);
            var l = new float[256];
            var r = new float[256];
            foreach (double bps in new[] { double.NaN, double.PositiveInfinity, 0, -1 })
            {
                voice.Render(p, 0, bps, Rate, l, r, 0, 256);
                Assert.That(l.All(float.IsFinite), Is.True);
            }
            voice.Render(p, double.NaN, 0.0001, Rate, l, r, 0, 256);
            voice.Render(p, 0, 0.0001, 0, l, r, 0, 256);
            Assert.That(l.All(float.IsFinite), Is.True);
        }

        // ---- Extremes --------------------------------------------------------------------------

        [Test, Combinatorial]
        public void EveryModeIsFiniteAtTempoAndTranspositionExtremes(
            [Values(0, 1, 2, 3)] int mode, [Values(20, 999)] double bpm, [Values(-48, 0, 48)] double semitones, [Values(true, false)] bool stereo)
        {
            var s = Tone(1.5, Rate, stereo);
            PlayFinite(new ClipVoice(), Clip(s, 100, Modes[mode], semitones), bpm, 6);
        }

        [Test, Combinatorial]
        public void VeryShortAndEmptyClipsPlayInEveryMode([Values(0, 1, 2, 63, 64, 100, 2047, 2049)] int samples, [Values(0, 1, 2, 3)] int mode)
        {
            var x = new float[samples];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(0.5 * Math.Sin(i * 0.3));
            var s = new AudioSource(x, x, Rate);
            var p = Clip(s, 120, Modes[mode]);
            Assert.That(p.Slices.Length, Is.GreaterThanOrEqualTo(1));
            PlayFinite(new ClipVoice(), p, 120, 4);
            PlayFinite(new ClipVoice(), p, 999, 4, loop: false);
        }

        [Test]
        public void ALongClipPlaysFromItsMiddleAndItsEnd()
        {
            var s = Tone(240, 8000);                              // four minutes (at a low rate, to keep the test quick)
            foreach (var mode in Modes)
            {
                var p = Clip(s, 120, mode);
                PlayFinite(new ClipVoice(), p, 120, 1, 8000, from: p.EndBeat - 0.5);
                PlayFinite(new ClipVoice(), p, 120, 1, 8000, from: p.EndBeat / 2);
            }
        }

        [Test]
        public void ExtremeWarpMarkersAreDroppedOrPlayedWithoutNaN()
        {
            var s = Tone(2);
            var maps = new[]
            {
                new WarpMap(new[] { new WarpMarker(0, 0), new WarpMarker(1000, 1e-9 * 2), new WarpMarker(2000, 1) }),            // 1e12 samples a beat
                new WarpMap(new[] { new WarpMarker(0, 0), new WarpMarker(1e-3, 1) }),                                         // too slow a slope
                new WarpMap(new[] { new WarpMarker(0, 0), new WarpMarker(1e300, 1e300) }),                                    // huge but fine
                new WarpMap(new[] { new WarpMarker(0, 0), new WarpMarker(1, 1e12) }),                                         // a beat is a millionth of a sample
                new WarpMap(new[] { new WarpMarker(0, 0), new WarpMarker(100, 0.001), new WarpMarker(101, 3.9), new WarpMarker(88200, 4) }),
                new WarpMap(new[] { new WarpMarker(0, 0), new WarpMarker(double.MaxValue, double.MaxValue), new WarpMarker(double.MaxValue, 1) }),
            };
            foreach (var map in maps)
            {
                foreach (var m in map.Markers) Assert.That(double.IsFinite(map.SamplesPerBeat(m.Beat)), Is.True);
                foreach (var mode in Modes)
                {
                    var p = Clip(s, 120, mode, 0, 1f, map);
                    PlayFinite(new ClipVoice(), p, 120, 2, loop: false);
                }
            }
        }

        [Test]
        public void ASliceGridOverAMapOfMillionsOfBeatsIsSkippedNotLooped()
        {
            var s = Tone(2);
            var map = new WarpMap(new[] { new WarpMarker(0, 0), new WarpMarker(88200, 1e9) });
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var slices = AudioAnalysis.Slices(s, map, new List<double>(), 0.25);
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(2000));
            Assert.That(slices.Length, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void ADenseSliceGridOnALongClipIsQuick()
        {
            var s = Tone(600, 8000);
            var map = WarpMap.Straight(s.Length, 600 * 2.0);       // 1200 beats of 1/16 grid: 19,200 slices
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var slices = AudioAnalysis.Slices(s, map, Enumerable.Range(0, 5000).Select(i => i * 900.0).ToList(), 0.0625);
            Assert.That(slices.Length, Is.GreaterThan(5000));
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(4000));
        }

        // ---- Sample rates ----------------------------------------------------------------------

        [Test, Combinatorial]
        public void AudioAndOutputAtAnyRatePlayFinite([Values(8000, 22050, 48000, 96000, 192000)] int audioRate, [Values(0, 1, 2, 3)] int mode, [Values(8000, 44100, 192000)] int outputRate)
        {
            var s = Clicks(120, 4, audioRate);
            PlayFinite(new ClipVoice(), Clip(s, 120, Modes[mode]), 120, 4, outputRate);
        }

        [Test]
        public void TheOutputRateChangingMidPlayCrossfadesAndStaysFinite([Values(0, 1, 2, 3)] int mode)
        {
            var p = Clip(Clicks(120, 8), 120, Modes[mode]);
            var voice = new ClipVoice();
            voice.Prepare(p);
            var l = new float[256];
            var r = new float[256];
            double beat = 0;
            foreach (int rate in new[] { 44100, 44100, 48000, 48000, 22050, 96000, 44100 })
            {
                double bps = 2.0 / rate;
                for (int b = 0; b < 40; b++)
                {
                    Array.Clear(l); Array.Clear(r);
                    voice.Render(p, beat % p.EndBeat, bps, rate, l, r, 0, 256);
                    beat += 256 * bps;
                    Assert.That(l.All(float.IsFinite) && r.All(float.IsFinite), Is.True);
                }
            }
        }

        // ---- Loop and clip ends ----------------------------------------------------------------

        [Test, Combinatorial]
        public void LoopingAcrossTheClipsEndNeverJumpsInAnyMode([Values(1, 2, 3)] int mode, [Values(0.0, 0.37)] double offset)
        {
            // A steady tone looped: where the end meets the start the output stays smooth (a click is a step far over the tone's own).
            var s = Tone(2, Rate, false, 200);
            var p = Clip(s, 120, Modes[mode]);
            var voice = new ClipVoice();
            voice.Prepare(p);
            double bps = 2.0 / Rate;
            var l = new float[256];
            var r = new float[256];
            double max = 0;
            float last = 0;
            for (long at = 0; at < 6 * Rate; at += 256)
            {
                Array.Clear(l); Array.Clear(r);
                voice.Render(p, (offset + at * bps) % p.EndBeat, bps, Rate, l, r, 0, 256);
                if (at > 4096)
                    for (int i = 0; i < 256; i++) { max = Math.Max(max, Math.Abs(l[i] - (i == 0 ? last : l[i - 1]))); }
                last = l[255];
            }
            Assert.That(max, Is.LessThan(0.2), "no step in a 200 Hz tone of 0.4 (its own is about 0.03)");
        }

        // ---- Mixer and engine buffers ----------------------------------------------------------

        [Test]
        public void ABlockLongerThanTheMixerHoldsIsCappedNotAllocated()
        {
            var clip = new ClipPlayback { Params = Clip(Tone(2), 120, WarpMode.Repitch) };
            var mixer = new ClipMixer(512);
            mixer.Begin(100000); mixer.Play(clip, 0, 2.0 / Rate, Rate, 100000); mixer.End(100000);   // warm up (JIT)
            long before = GC.GetAllocatedBytesForCurrentThread();
            int frames = mixer.Begin(100000);
            mixer.Play(clip, 0, 2.0 / Rate, Rate, 100000);
            mixer.End(100000);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0L));
            Assert.That((frames, mixer.Left.Length), Is.EqualTo((512, 512)));
            Assert.That(mixer.Left.Take(512).All(float.IsFinite), Is.True);
            Assert.That(mixer.Begin(-5), Is.EqualTo(0));
        }

        [Test]
        public void ACaptureKeepsNoNaNFromTheInput()
        {
            var cap = new AudioCapture(Rate, 0, 100, 100);
            var l = new float[100];
            var r = new float[100];
            Array.Fill(l, 0.5f);
            l[10] = float.NaN; r[20] = float.PositiveInfinity;
            cap.Write(l, r, 100, 1f);
            var (left, right) = cap.Collect(100);
            Assert.That(left.All(float.IsFinite) && right.All(float.IsFinite), Is.True);
            Assert.That((left[9], left[10], right[20]), Is.EqualTo((0.5f, 0f, 0f)));
        }

        // ---- Tempo and transient detection: where it fails -------------------------------------

        [Test]
        public void TempoOfSilenceNoiseDCAndTinyClipsIsAnHonestDefaultInsideTheRange()
        {
            var rng = new Random(7);
            var noise = new float[Rate * 5];
            for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 2 - 1) * 0.3f;
            var dc = new float[Rate * 5];
            Array.Fill(dc, 0.25f);
            var cases = new Dictionary<string, AudioSource>
            {
                ["silence"] = new AudioSource(new float[Rate * 5], null!, Rate),
                ["noise"] = new AudioSource(noise, noise, Rate),
                ["dc"] = new AudioSource(dc, dc, Rate),
                ["empty"] = new AudioSource(new float[0], null!, Rate),
                ["one sample"] = new AudioSource(new float[1], null!, Rate),
                ["half a frame"] = new AudioSource(new float[500], null!, Rate),
            };
            foreach (var (name, s) in cases)
            {
                var (bpm, beats) = AudioAnalysis.Tempo(s);
                Assert.That(double.IsFinite(bpm) && double.IsFinite(beats) && beats >= 0, Is.True, name);
                Assert.That(bpm, Is.InRange(1, 999), name);
                Assert.That(AudioAnalysis.Onsets(s).All(o => o >= 0 && o < Math.Max(1, s.Length)), Is.True, name);
            }
            Assert.That(AudioAnalysis.Tempo(cases["noise"]).Bpm, Is.EqualTo(120.0).Within(0.001).Or.InRange(80, 160), "noise: no tempo invented outside 80-160");
            Assert.That(AudioAnalysis.Tempo(cases["silence"]).Bpm, Is.EqualTo(120.0).Within(0.01).Or.InRange(80, 160));
        }

        [Test]
        public void ARegularClickTrackKeepsItsTempoAndANonBarLengthKeepsTheTransientsTempo()
        {
            var clicks = Clicks(128, 17);                        // 17 beats: not a whole number of bars
            var (bpm, _) = AudioAnalysis.Tempo(clicks);
            Assert.That(bpm, Is.EqualTo(128).Within(2));
        }

        [Test]
        public void ATwentyMinuteFileIsAnalysedWithoutBlowingUp()
        {
            var s = Tone(20 * 60, 8000);                        // quiet and steady: no transients, the bars fit
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (bpm, beats) = AudioAnalysis.Tempo(s);
            Assert.That(double.IsFinite(bpm) && beats > 0, Is.True);
            Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(120));
        }

        // ---- Soak ------------------------------------------------------------------------------

        /// <summary>
        /// Eight clips in every warp mode (thirty-two voices), each at its own tempo, transposition and loop, tempo sweeping from
        /// 60 to 180 BPM and back, for ten minutes of render time in blocks of 256: 0 bytes allocated, no NaN, no runaway level.
        /// </summary>
        [Test, Category("Soak")]
        public void TenMinutesOfEightClipsInEveryWarpModeAllocateNothingAndStayFinite()
        {
            const int perMode = 8;
            const int block = 256;
            long blocks = 10L * 60 * Rate / block;
            var rng = new Random(11);
            var clips = new List<(ClipVoice Voice, AudioClipParams P, double Skew)>();
            foreach (var mode in Modes)
                for (int i = 0; i < perMode; i++)
                {
                    var s = i % 2 == 0 ? Clicks(90 + 5 * i, 8 + i) : Tone(1.5 + 0.4 * i, Rate, i % 4 == 1, 150 + 40 * i);
                    var p = Clip(s, 90 + 5 * i, mode, new[] { 0, 7, -5, 12, -12, 3, 0, 19 }[i], 0.7f);
                    var v = new ClipVoice();
                    v.Prepare(p);
                    clips.Add((v, p, 0.8 + 0.4 * rng.NextDouble()));
                }
            var l = new float[block];
            var r = new float[block];
            var beats = new double[clips.Count];
            double peak = 0;
            long allocated = 0;
            int bad = 0;
            // Everything is warm (compiled, its tables made) after the first thousand blocks; the rest is counted.
            for (long b = 0; b < blocks; b++)
            {
                double bpm = 120 + 60 * Math.Sin(2 * Math.PI * b / 20000.0);
                double bps = bpm / 60.0 / Rate;
                long before = GC.GetAllocatedBytesForCurrentThread();
                Array.Clear(l); Array.Clear(r);
                for (int c = 0; c < clips.Count; c++)
                {
                    var (voice, p, skew) = clips[c];
                    voice.Render(p, beats[c], bps * skew, Rate, l, r, 0, block);
                    beats[c] += block * bps * skew;
                    if (beats[c] >= p.EndBeat) beats[c] -= p.EndBeat;        // a loop going round
                }
                if (b >= 1000) allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                for (int i = 0; i < block; i++)
                {
                    float x = l[i], y = r[i];
                    if (!float.IsFinite(x) || !float.IsFinite(y)) bad++;
                    else peak = Math.Max(peak, Math.Max(Math.Abs(x), Math.Abs(y)));
                }
            }
            Assert.That(bad, Is.EqualTo(0), "NaN or infinity");
            Assert.That(allocated, Is.EqualTo(0L), "bytes allocated while rendering");
            Assert.That(peak, Is.LessThan(40), "32 voices at 0.7 of 0.8: a runaway would be far over");
        }
    }
}
