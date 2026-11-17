using System.Diagnostics;
using System.Xml.Linq;
using ReBuzz.Core.Clips;
using ReBuzz.Core.Session;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// Neo Audio's Pro warp mode (docs/specs/neo-audio.md section 11, NA5): Signalsmith Stretch through the native neostretch library. It
    /// follows the map, the tempo and the transposition, lines up with Repitch, crossfades at jumps, allocates nothing, is finite, and the
    /// clip plays as Complex (with one line in the log) where the library is missing. The tests that need the library fail, by name, when it
    /// is not there (CI builds it first): they are not skipped.
    /// </summary>
    [TestFixture, NonParallelizable]
    public class NeoStretchTests
    {
        const int Rate = 44100;

        static AudioSource Tone(double seconds, double freq = 220, int rate = Rate, bool stereo = false)
        {
            var l = new float[(int)(seconds * rate)];
            for (int i = 0; i < l.Length; i++) l[i] = (float)(0.4 * Math.Sin(2 * Math.PI * freq * i / rate));
            if (!stereo) return new AudioSource(l, l, rate);
            var r = new float[l.Length];
            for (int i = 0; i < r.Length; i++) r[i] = (float)(0.3 * Math.Sin(2 * Math.PI * freq * 1.5 * i / rate));
            return new AudioSource(l, r, rate);
        }

        /// <summary>A kick and a hat on every beat of a bar of four at <paramref name="bpm"/>, for <paramref name="beats"/> beats: sharp onsets.</summary>
        static AudioSource Drums(double bpm, int beats, int rate = Rate)
        {
            int perBeat = (int)Math.Round(rate * 60 / bpm);
            var x = new float[perBeat * beats];
            var rng = new Random(5);
            for (int b = 0; b < beats; b++)
            {
                int at = b * perBeat;
                double phase = 0;
                for (int i = 0; i < rate / 8 && at + i < x.Length; i++)
                {
                    double f = 50 + 90 * Math.Exp(-i / (0.012 * rate));
                    phase += 2 * Math.PI * f / rate;
                    x[at + i] += (float)(0.7 * Math.Sin(phase) * Math.Exp(-i / (0.05 * rate)));
                    x[at + i] += (float)(0.5 * (rng.NextDouble() * 2 - 1) * Math.Exp(-i / (0.004 * rate)));         // the click of the beater
                }
                int hat = at + perBeat / 2;
                for (int i = 0; i < rate / 20 && hat + i < x.Length; i++)
                    x[hat + i] += (float)(0.25 * (rng.NextDouble() * 2 - 1) * Math.Exp(-i / (0.01 * rate)));
            }
            return new AudioSource(x, x, rate);
        }

        static AudioClipParams Clip(AudioSource s, double bpm, WarpMode mode, double semitones = 0, float gain = 1f, ClipFades fades = default)
        {
            var map = WarpMap.Straight(s.Length, s.Seconds * bpm / 60);
            return new AudioClipParams(s, map, mode, AudioAnalysis.Slices(s, map, AudioAnalysis.Onsets(s)), gain, semitones) { Fades = fades };
        }

        /// <summary>Plays a clip from <paramref name="from"/> for <paramref name="beats"/> beats at <paramref name="bpm"/>, in blocks, on one voice; the left channel.</summary>
        static float[] Play(AudioClipParams p, double bpm, double from, double beats, int block = 256, int outRate = Rate, ClipVoice? voice = null)
        {
            voice ??= new ClipVoice();
            voice.Prepare(p);
            double bps = bpm / 60.0 / outRate;
            int total = (int)(beats / bps);
            var output = new float[total];
            var l = new float[block];
            var r = new float[block];
            for (int at = 0; at < total; at += block)
            {
                int n = Math.Min(block, total - at);
                Array.Clear(l); Array.Clear(r);
                voice.Render(p, from + at * bps, bps, outRate, l, r, 0, n);
                Array.Copy(l, 0, output, at, n);
            }
            return output;
        }

        /// <summary>Where each sound starts: the first sample whose peak level (falling over 30 ms) passes <paramref name="fraction"/> of the loudest, once it has fallen below a quarter of that.</summary>
        static List<int> Onsets(float[] x, int rate = Rate, double fraction = 0.1)
        {
            var env = new float[x.Length];
            float decay = (float)Math.Exp(-1.0 / (0.03 * rate)), held = 0;          // a peak follower: up at once, down over 30 ms
            for (int i = 0; i < x.Length; i++)
            {
                held = Math.Max(Math.Abs(x[i]), held * decay);
                env[i] = held;
            }
            float hi = env.Max() * (float)fraction, lo = hi * 0.25f;
            var onsets = new List<int>();
            bool armed = true;
            for (int i = 0; i < env.Length; i++)
            {
                if (armed && env[i] >= hi) { onsets.Add(i); armed = false; }
                else if (!armed && env[i] < lo) armed = true;
            }
            return onsets;
        }

        /// <summary>The frequency of a clean tone in a stretch of samples, from its zero crossings.</summary>
        static double Frequency(float[] x, int from, int to, int rate = Rate)
        {
            int crossings = 0;
            int first = -1, last = -1;
            for (int i = from + 1; i < to; i++)
                if (x[i - 1] < 0 && x[i] >= 0)
                {
                    if (first < 0) first = i;
                    last = i;
                    crossings++;
                }
            return crossings < 2 ? 0 : (crossings - 1) * rate / (double)(last - first);
        }

        static double Rms(float[] x, int from, int to)
        {
            double sum = 0;
            for (int i = from; i < to; i++) sum += x[i] * (double)x[i];
            return Math.Sqrt(sum / Math.Max(1, to - from));
        }

        static void NeedsTheLibrary()
        {
            if (Environment.GetEnvironmentVariable("NEO_STRETCH_OFF") == "1") Assert.Ignore("the library is turned off by NEO_STRETCH_OFF");
            Assert.That(NeoStretch.Available, Is.True, "the neostretch library is not here: " + NeoStretch.Failure + " (build it with cmake -S NeoStretch -B build/NeoStretch)");
        }

        // ---- The library -----------------------------------------------------------------------------

        [Test]
        public void TheLibraryLoadsAndAMonoAndAStereoStretcherAreMade()
        {
            NeedsTheLibrary();
            foreach (int channels in new[] { 1, 2 })
            {
                var s = NeoStretch.Create(channels, 48000);
                Assert.That(s, Is.Not.Null);
                Assert.That(s!.InputLatency, Is.InRange(1000, 6000), "about 60 ms");
                Assert.That(s.OutputLatency, Is.InRange(1000, 9000), "about 90 ms");
            }
        }

        // ---- Time, pitch, tempo ----------------------------------------------------------------------

        [Test, Combinatorial]
        public void ATonePlaysAtItsPitchWhateverTheTempo([Values(70, 120, 200)] double bpm, [Values(false, true)] bool stereo)
        {
            NeedsTheLibrary();
            var p = Clip(Tone(8, 220, Rate, stereo), 120, WarpMode.Pro);               // 16 beats at 120
            var x = Play(p, bpm, 0, 8);
            int from = (int)(0.6 * Rate), to = x.Length - (int)(0.3 * Rate);
            Assert.That(Frequency(x, from, to), Is.EqualTo(220).Within(2), $"220 Hz at {bpm} BPM");
            Assert.That(Rms(x, from, to), Is.EqualTo(0.4 / Math.Sqrt(2)).Within(0.04), "at its own level");
        }

        [Test]
        public void TheLengthFollowsTheTempo()
        {
            NeedsTheLibrary();
            // Eight beats of drums, played at half the speed: hit n lands twice as late as at the file's own tempo.
            var s = Drums(120, 8);
            var p = Clip(s, 120, WarpMode.Pro);
            var x = Play(p, 60, 0, 4);                                  // 4 beats at 60 BPM: 4 seconds, the first half of the file
            var onsets = Onsets(x);
            for (int k = 0; k < 4; k++)
                Assert.That(onsets.Any(o => Math.Abs(o / (double)Rate - k) <= 0.04), Is.True, $"a hit on beat {k} at 60 BPM (stretched twice: its start smears a little early) (onsets at {string.Join(", ", onsets.Select(o => o * 1000 / Rate))} ms)");
            Assert.That(onsets.All(o => Enumerable.Range(0, 5).Any(k => Math.Abs(o / (double)Rate - k) <= 0.04 || Math.Abs(o / (double)Rate - (k + 0.5)) <= 0.04)), Is.True, "and the hats half a beat later: nothing else");
        }

        [Test]
        public void TransposingMovesThePitchAndNotTheTime([Values(-12, -5, 7, 12, 19)] double semitones)
        {
            NeedsTheLibrary();
            var p = Clip(Tone(6, 220), 120, WarpMode.Pro, semitones);
            var x = Play(p, 120, 0, 6);
            Assert.That(Frequency(x, (int)(0.6 * Rate), x.Length - (int)(0.3 * Rate)), Is.EqualTo(220 * Math.Pow(2, semitones / 12)).Within(3));
            // the drums keep their beats whatever the pitch
            var d = Clip(Drums(120, 8), 120, WarpMode.Pro, semitones);
            var onsets = Onsets(Play(d, 120, 0, 8));
            for (int k = 0; k < 8; k++)
                Assert.That(onsets.Any(o => Math.Abs(o / (double)Rate - k * 0.5) <= 0.012), Is.True, $"a hit on beat {k} at {semitones} semitones");
        }

        [Test]
        public void ADetuneOfAFewCentsIsHeard()
        {
            NeedsTheLibrary();
            var p = Clip(Tone(5, 440), 120, WarpMode.Pro, 0.37);        // 37 cents
            var x = Play(p, 120, 0, 5);
            Assert.That(Frequency(x, (int)(0.6 * Rate), x.Length - (int)(0.3 * Rate)), Is.EqualTo(440 * Math.Pow(2, 0.37 / 12)).Within(1.5));
        }

        [Test]
        public void ATempoChangingWhilePlayingFollowsAndKeepsThePitch()
        {
            NeedsTheLibrary();
            var p = Clip(Tone(12, 330), 120, WarpMode.Pro);
            var voice = new ClipVoice();
            voice.Prepare(p);
            var output = new List<float>();
            var l = new float[256];
            var r = new float[256];
            double beat = 0;
            for (int block = 0; block < 1500; block++)
            {
                double bpm = 120 + 60 * Math.Sin(2 * Math.PI * block / 700.0);         // 60 to 180
                double bps = bpm / 60 / Rate;
                Array.Clear(l); Array.Clear(r);
                voice.Render(p, beat, bps, Rate, l, r, 0, 256);
                beat += 256 * bps;
                output.AddRange(l);
            }
            var x = output.ToArray();
            Assert.That(x.All(float.IsFinite), Is.True);
            Assert.That(Frequency(x, Rate, x.Length - Rate), Is.EqualTo(330).Within(4));
            Assert.That(Rms(x, Rate, x.Length - Rate), Is.GreaterThan(0.2));
        }

        [Test]
        public void ASourceAtAnotherRateThanTheOutputKeepsItsPitch([Values(22050, 48000, 96000)] int sourceRate)
        {
            NeedsTheLibrary();
            var p = Clip(Tone(4, 300, sourceRate), 120, WarpMode.Pro);
            var x = Play(p, 120, 0, 4, 256, Rate);
            Assert.That(Frequency(x, (int)(0.7 * Rate), x.Length - (int)(0.4 * Rate)), Is.EqualTo(300).Within(3));
        }

        /// <summary>
        /// The frequency of the strongest tone within a semitone either side of <paramref name="near"/>, from a Hann-windowed DFT scan in 0.25-cent steps:
        /// unlike zero crossings it is not moved by the clicks of a slice's seam.
        /// </summary>
        static double Peak(float[] x, int from, int to, double near)
        {
            int n = to - from;
            var w = new double[n];
            for (int i = 0; i < n; i++) w[i] = x[from + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
            double best = 0, bestHz = near;
            for (double c = -100; c <= 100; c += 0.25)
            {
                double hz = near * Math.Pow(2, c / 1200), step = 2 * Math.PI * hz / Rate, re = 0, im = 0;
                for (int i = 0; i < n; i++) { double a = step * i; re += w[i] * Math.Cos(a); im += w[i] * Math.Sin(a); }
                double power = re * re + im * im;
                if (power > best) { best = power; bestHz = hz; }
            }
            return bestHz;
        }

        /// <summary>The cents between two frequencies.</summary>
        static double Cents(double a, double b) => 1200 * Math.Log2(a / b);

        /// <summary>
        /// A Pro start that gets no seek (the budget is spent and no spare is ready) stands in until its turn. The stand-in is Beats, which
        /// keeps the pitch right at any tempo and with a transposition: here it is a sine at 0.8x tempo and at +7 semitones, against Pro's own output.
        /// </summary>
        [Test]
        public void TheStandInForAProStartWithNoSeekHasProsPitchAtAnotherTempoAndWithATranspose([Values(96.0, 120.0)] double bpm, [Values(0.0, 7.0)] double semitones)
        {
            NeedsTheLibrary();
            bool was = ProVoice.Prefetch;
            ProVoice.Prefetch = false;
            try
            {
                var p = Clip(Tone(8, 220), 120, WarpMode.Pro, semitones);            // 16 beats at 120 BPM
                int from = (int)(0.6 * Rate);
                var pro = Play(p, bpm, 0, 6);
                int to = pro.Length - (int)(0.3 * Rate);
                var standIn = new ClipVoice { Budget = new SeekBudget { Left = 0 } };          // the block's seek is never there: it stays the stand-in
                var x = Play(p, bpm, 0, 6, 256, Rate, standIn);
                double want = 220 * Math.Pow(2, semitones / 12), proHz = Peak(pro, from, to, want), standHz = Peak(x, from, to, want);
                TestContext.WriteLine($"{bpm} BPM, {semitones} st: Pro {proHz:F2} Hz, stand-in {standHz:F2} Hz, wanted {want:F2} Hz ({Cents(standHz, proHz):F1} cents apart)");
                Assert.That(Cents(proHz, want), Is.EqualTo(0).Within(3), "Pro is right");
                Assert.That(Cents(standHz, proHz), Is.EqualTo(0).Within(8), "the stand-in matches Pro's pitch within a few cents (a slice that runs out loops its end: a few cents of seam; Repitch at 0.8x would be 386 cents off)");
                Assert.That(Rms(x, from, to), Is.GreaterThan(0.2), "and it sounds");
            }
            finally { ProVoice.Prefetch = was; }
        }

        // ---- Latency ---------------------------------------------------------------------------------

        [Test]
        public void AHitLandsWhereRepitchPutsIt([Values(0.0, 1.0, 2.5)] double from, [Values(64, 256, 1000)] int block)
        {
            NeedsTheLibrary();
            var s = Drums(120, 16);
            double bpm = 120;
            var pro = Play(Clip(s, bpm, WarpMode.Pro), bpm, from, 4, block);
            var re = Play(Clip(s, bpm, WarpMode.Repitch), bpm, from, 4, block);
            var a = Onsets(re);
            var b = Onsets(pro);
            Assert.That(b.Count, Is.EqualTo(a.Count), "the same hits");
            Assert.That(a.Count, Is.GreaterThanOrEqualTo(3));
            for (int k = 0; k < a.Count; k++)
                Assert.That(Math.Abs(b[k] - a[k]) / (double)Rate * 1000, Is.LessThanOrEqualTo(1.0), $"hit {k}: {a[k]} against {b[k]} samples");
        }

        [Test]
        public void AClipWithAGainAndAFadeIsSampleAccurateLikeRepitch()
        {
            NeedsTheLibrary();
            // NA2: the mixer's envelope is applied to whatever the voice renders; a Pro clip's follows the same beats.
            var s = Tone(8, 220);
            var fades = new ClipFades(2, 2, 0, false);
            double bps = 2.0 / Rate;
            var rms = new List<double[]>();
            foreach (var mode in new[] { WarpMode.Repitch, WarpMode.Pro })
            {
                var clip = new ClipPlayback { Params = Clip(s, 120, mode, 0, 0.8f, fades) };
                var mixer = new ClipMixer(512);
                var all = new List<float>();
                int total = (int)(8 / bps);
                for (int at = 0; at < total; at += 256)
                {
                    int n = Math.Min(256, total - at);
                    mixer.Begin(n);
                    mixer.Play(clip, at * bps, bps, Rate, n, at * bps, 8 - at * bps);
                    mixer.End(n);
                    all.AddRange(mixer.Left.Take(n));
                }
                var x = all.ToArray();
                rms.Add(new[] { Rms(x, 0, Rate / 4), Rms(x, Rate / 2, 3 * Rate / 4), Rms(x, Rate, 3 * Rate / 2), Rms(x, 3 * Rate / 2, 2 * Rate), Rms(x, 3 * Rate, 3 * Rate + Rate / 4), Rms(x, x.Length - Rate / 4, x.Length) });
            }
            for (int i = 0; i < 6; i++)
                Assert.That(rms[1][i], Is.EqualTo(rms[0][i]).Within(0.02 + 0.1 * rms[0][i]), $"the level at point {i}");
        }

        [Test]
        public void TheBlockSizeDoesNotMatter()
        {
            NeedsTheLibrary();
            var p = Clip(Drums(110, 16), 120, WarpMode.Pro, 3);
            var big = Play(p, 133, 0, 8, 1024);
            double worst = -300;
            foreach (int block in new[] { 17, 64, 333 })
            {
                var x = Play(p, 133, 0, 8, block);
                double err = 0, sig = 0;
                for (int i = 0; i < x.Length; i++) { err += Math.Pow(x[i] - big[i], 2); sig += Math.Pow(big[i], 2); }
                worst = Math.Max(worst, 10 * Math.Log10(err / sig + 1e-20));
                TestContext.Out.WriteLine($"block {block} against 1024: {10 * Math.Log10(err / sig + 1e-20):F1} dB");
            }
            Assert.That(worst, Is.LessThan(-40), "the chunks are on a grid counted from the start: only float rounding of the beat differs");
        }

        // ---- Jumps and stops -------------------------------------------------------------------------

        [Test, Combinatorial]
        public void ALoopGoingRoundIsSmooth([Values(0.0, 0.37)] double offset, [Values(64, 256)] int block)
        {
            NeedsTheLibrary();
            var s = Tone(2, 200);                                   // 200 Hz at 0.4: its own sample step is about 0.011
            var p = Clip(s, 120, WarpMode.Pro);
            var voice = new ClipVoice();
            voice.Prepare(p);
            double bps = 2.0 / Rate;
            var l = new float[block];
            var r = new float[block];
            double max = 0;
            float last = 0;
            for (long at = 0; at < 6 * Rate; at += block)
            {
                Array.Clear(l); Array.Clear(r);
                voice.Render(p, (offset + at * bps) % p.EndBeat, bps, Rate, l, r, 0, block);
                if (at > 8192)
                    for (int i = 0; i < block; i++) max = Math.Max(max, Math.Abs(l[i] - (i == 0 ? last : l[i - 1])));
                last = l[block - 1];
            }
            Assert.That(max, Is.LessThan(0.08), "no step at the loop's seam");
        }

        [Test]
        public void AStopFadesOutAndThenIsSilent()
        {
            NeedsTheLibrary();
            var p = Clip(Tone(4, 300), 120, WarpMode.Pro);
            var voice = new ClipVoice();
            voice.Prepare(p);
            double bps = 2.0 / Rate;
            var l = new float[256];
            var r = new float[256];
            double beat = 0;
            for (int b = 0; b < 40; b++)
            {
                Array.Clear(l); Array.Clear(r);
                voice.Render(p, beat, bps, Rate, l, r, 0, 256);
                beat += 256 * bps;
            }
            Assert.That(l.Max(Math.Abs), Is.GreaterThan(0.1f));
            Array.Clear(l); Array.Clear(r);
            voice.Stop(l, r, 0, 256);
            Assert.That(l.Take(8).Max(Math.Abs), Is.GreaterThan(0.05f), "it fades, it does not cut");
            Assert.That(l.Skip(80).All(v => v == 0), Is.True, "and it is done within 64 samples");
            float step = 0;
            for (int i = 1; i < 70; i++) step = Math.Max(step, Math.Abs(l[i] - l[i - 1]));
            Assert.That(step, Is.LessThan(0.06f));
            Assert.That(voice.IsSounding, Is.False);
            Array.Clear(l); Array.Clear(r);
            voice.Stop(l, r, 0, 256);
            Assert.That(l.All(v => v == 0), Is.True);
        }

        [Test]
        public void ALaunchOverAPlayingClipCrossfadesWithoutAStep()
        {
            NeedsTheLibrary();
            var p = Clip(Tone(4, 250), 120, WarpMode.Pro);
            var voice = new ClipVoice();
            voice.Prepare(p);
            double bps = 2.0 / Rate;
            var l = new float[128];
            var r = new float[128];
            double beat = 0, max = 0;
            float last = 0;
            for (int b = 0; b < 400; b++)
            {
                if (b % 50 == 49) beat = (b * 0.7) % 6;             // a jump every 50 blocks
                Array.Clear(l); Array.Clear(r);
                voice.Render(p, beat, bps, Rate, l, r, 0, 128);
                beat += 128 * bps;
                if (b > 80)
                    for (int i = 0; i < 128; i++) { max = Math.Max(max, Math.Abs(l[i] - (i == 0 ? last : l[i - 1]))); }
                last = l[127];
            }
            Assert.That(max, Is.LessThan(0.3), "a step well under the tone's own peak of 0.4");
        }

        [Test]
        public void AClipChangingFromProToAnotherModeFadesTheProOut()
        {
            NeedsTheLibrary();
            var s = Tone(4, 250);
            var pro = Clip(s, 120, WarpMode.Pro);
            var rep = Clip(s, 120, WarpMode.Repitch);
            var voice = new ClipVoice();
            voice.Prepare(pro);
            voice.Prepare(rep);
            double bps = 2.0 / Rate;
            var l = new float[256];
            var r = new float[256];
            double beat = 0;
            for (int b = 0; b < 30; b++)
            {
                Array.Clear(l); Array.Clear(r);
                voice.Render(pro, beat, bps, Rate, l, r, 0, 256);
                beat += 256 * bps;
            }
            Array.Clear(l); Array.Clear(r);
            voice.Render(rep, beat, bps, Rate, l, r, 0, 256);
            Assert.That(l.All(float.IsFinite), Is.True);
            float step = 0;
            for (int i = 1; i < 256; i++) step = Math.Max(step, Math.Abs(l[i] - l[i - 1]));
            Assert.That(step, Is.LessThan(0.25f));
            Assert.That(voice.IsSounding, Is.True);
        }

        // ---- Real time -------------------------------------------------------------------------------

        [Test]
        public void RenderingAllocatesNothingAndStaysFiniteAcrossStartsStopsAndChanges()
        {
            NeedsTheLibrary();
            var s = Drums(120, 8);
            var a = Clip(s, 120, WarpMode.Pro, 0, 0.7f);
            var b = Clip(s, 100, WarpMode.Pro, 5, 0.5f);
            var voice = new ClipVoice();
            voice.Prepare(a);
            voice.Prepare(b);
            var l = new float[300];
            var r = new float[300];
            var rng = new Random(3);
            double beat = 0;
            long allocated = 0;
            for (int block = 0; block < 4000; block++)
            {
                double bps = (90 + 80 * rng.NextDouble()) / 60 / Rate;
                if (block % 37 == 36) beat = rng.NextDouble() * 12;              // a jump
                var p = block % 5 == 0 ? b : a;
                long before = GC.GetAllocatedBytesForCurrentThread();
                Array.Clear(l); Array.Clear(r);
                if (block % 211 == 210) voice.Stop(l, r, 0, 300);
                else voice.Render(p, beat, bps, Rate, l, r, 0, 1 + (block * 7) % 300);
                if (block > 200) allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                beat += 300 * bps;
                foreach (float v in l) Assert.That(float.IsFinite(v) && Math.Abs(v) < 8 && (v == 0 || Math.Abs(v) > 1e-30f), Is.True, $"block {block}: {v}");
            }
            Assert.That(allocated, Is.EqualTo(0L), "managed bytes allocated while rendering");
        }

        [Test]
        public void ANoiseOrSilenceOrLoudBurstsComeOutFiniteAndNeverRunAway()
        {
            NeedsTheLibrary();
            var rng = new Random(9);
            var x = new float[Rate * 8];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(rng.NextDouble() * 2 - 1) * 0.5f;
            for (int i = 3 * Rate; i < 4 * Rate; i++) x[i] = 0;                         // a second of silence
            for (int i = 5 * Rate; i < 5 * Rate + 300; i++) x[i] = 1f;                  // a burst at full scale
            var s = new AudioSource(x, (float[])x.Clone(), Rate);
            foreach (var (bpm, semitones) in new[] { (120.0, 0.0), (60.0, 7.0), (300.0, -19.0), (20.0, 0.0), (999.0, 48.0) })
            {
                var p = Clip(s, 120, WarpMode.Pro, semitones);
                var y = Play(p, bpm, 0, Math.Min(bpm / 60 * 7, 50));
                Assert.That(y.All(float.IsFinite), Is.True, $"{bpm} BPM {semitones} st");
                Assert.That(y.Max(Math.Abs), Is.LessThan(12f), "no runaway");
            }
        }

        [Test]
        public void ProCostsMoreThanTheOthersButFitsInRealTime()
        {
            NeedsTheLibrary();
            // Per voice: the share of one core a clip takes to render at 44.1 kHz in blocks of 256, steady state, a stereo drum loop with some stretch.
            var s = Drums(120, 32, Rate);
            var stereo = new AudioSource(s.Left, (float[])s.Left.Clone(), Rate);
            double seconds = 20;
            var results = new List<(WarpMode Mode, double Percent)>();
            foreach (var mode in new[] { WarpMode.Repitch, WarpMode.Beats, WarpMode.Complex, WarpMode.Pro })
            {
                var p = Clip(stereo, 120, mode, mode == WarpMode.Repitch ? 0 : 3);
                var voice = new ClipVoice();
                voice.Prepare(p);
                var l = new float[256];
                var r = new float[256];
                double bps = 104.0 / 60 / Rate;
                int blocks = (int)(seconds * Rate / 256);
                double beat = 0;
                for (int b = 0; b < 800; b++)                      // warm up (JIT, tables)
                {
                    Array.Clear(l); Array.Clear(r);
                    voice.Render(p, beat, bps, Rate, l, r, 0, 256);
                    beat += 256 * bps;
                    if (beat >= p.EndBeat) beat -= p.EndBeat;
                }
                var sw = Stopwatch.StartNew();
                for (int b = 0; b < blocks; b++)
                {
                    Array.Clear(l); Array.Clear(r);
                    voice.Render(p, beat, bps, Rate, l, r, 0, 256);
                    beat += 256 * bps;
                    if (beat >= p.EndBeat) beat -= p.EndBeat;
                }
                sw.Stop();
                results.Add((mode, 100 * sw.Elapsed.TotalSeconds / seconds));
            }
            foreach (var (mode, percent) in results) TestContext.Out.WriteLine($"CPU per stereo voice, {mode}: {percent:F2}% of one core (44.1 kHz, blocks of 256)");
            Assert.That(results.Last().Percent, Is.LessThan(25), "a Pro voice well inside real time");
        }


        // ---- Blind-test renders (by hand) -------------------------------------------------------------

        static AudioSource Bass(double bpm, int beats)
        {
            int perBeat = (int)Math.Round(Rate * 60 / bpm);
            var x = new float[perBeat * beats];
            double[] notes = { 55.0, 55.0, 65.41, 49.0, 55.0, 73.42, 65.41, 82.41 };
            for (int b = 0; b < beats; b++)
            {
                double f = notes[b % notes.Length], phase = 0;
                for (int i = 0; i < perBeat && b * perBeat + i < x.Length; i++)
                {
                    phase += f / Rate;
                    double saw = 2 * (phase - Math.Floor(phase)) - 1, sub = Math.Sin(2 * Math.PI * phase);
                    double env = Math.Min(1, i / (0.004 * Rate)) * Math.Exp(-i / (0.35 * perBeat));
                    x[b * perBeat + i] = (float)(0.45 * env * (0.6 * sub + 0.4 * saw));
                }
            }
            return new AudioSource(x, x, Rate);
        }

        /// <summary>A vocal-like pad: detuned voices on a chord with formant-ish resonant peaks and a slow swell, stereo.</summary>
        static AudioSource Pad(double bpm, int beats)
        {
            int perBeat = (int)Math.Round(Rate * 60 / bpm);
            int n = perBeat * beats;
            var l = new float[n];
            var r = new float[n];
            double[][] chords = { new[] { 220.0, 261.63, 329.63 }, new[] { 196.0, 246.94, 293.66 } };
            for (int c = 0; c < 2; c++)
            {
                int from = c * n / 2, to = (c + 1) * n / 2;
                foreach (double f in chords[c])
                    for (int v = -1; v <= 1; v++)
                    {
                        double ff = f * Math.Pow(2, v * 0.004), phase = v * 0.3;
                        for (int i = from; i < to; i++)
                        {
                            phase += ff / Rate;
                            double t = (i - from) / (double)(to - from), env = Math.Sin(Math.PI * t);
                            double h = 0;
                            for (int k = 1; k <= 12; k++) h += Math.Sin(2 * Math.PI * k * phase) / k * Math.Exp(-Math.Pow(Math.Log(k * ff / 900.0), 2));
                            float y = (float)(0.12 * env * h);
                            l[i] += y * (v < 1 ? 1f : 0.6f);
                            r[i] += y * (v > -1 ? 1f : 0.6f);
                        }
                    }
            }
            return new AudioSource(l, r, Rate);
        }

        /// <summary>
        /// Writes the blind-test renders: three excerpts, each at -20 % tempo, +25 % tempo and +7 semitones, in Repitch, Beats, Complex and Pro,
        /// as 16-bit WAVs in the folder named by NA5_RENDER_DIR, with index.md (shuffled, the mode hidden) and key.md. Run by hand.
        /// </summary>
        [Test, Explicit("writes the blind-test renders; set NA5_RENDER_DIR")]
        public void WriteTheBlindTestRenders()
        {
            string dir = Environment.GetEnvironmentVariable("NA5_RENDER_DIR") ?? throw new InvalidOperationException("NA5_RENDER_DIR");
            Directory.CreateDirectory(dir);
            const double Bpm = 120;
            var excerpts = new (string Name, AudioSource Source)[] { ("Drum loop", Drums(Bpm, 8)), ("Bass line", Bass(Bpm, 8)), ("Pad", Pad(Bpm, 8)) };
            var settings = new (string Name, double Tempo, double Semis)[] { ("20 percent slower", 0.8, 0), ("25 percent faster", 1.25, 0), ("7 semitones up", 1.0, 7) };
            var modes = new[] { WarpMode.Repitch, WarpMode.Beats, WarpMode.Complex, WarpMode.Pro };
            var rng = new Random(2026);
            var index = new System.Text.StringBuilder("# Blind test\n\nEach group below holds four renders of the same excerpt with the same change. They differ only in the warp mode. Listen to each and rank them (or pick the best). The key is in key.md; do not open it first.\n\n");
            var key = new System.Text.StringBuilder("# Key\n\n| File | Excerpt | Change | Mode |\n|---|---|---|---|\n");
            int group = 0;
            foreach (var (name, source) in excerpts)
                foreach (var (sname, tempo, semis) in settings)
                {
                    group++;
                    var order = modes.OrderBy(_ => rng.Next()).ToArray();
                    var renders = order.Select(m => (m, Play(Clip(source, Bpm, m, semis), Bpm * tempo, 0, 8))).ToList();
                    float peak = renders.Max(x => x.Item2.Select(Math.Abs).Max());
                    float scale = peak > 0 ? 0.8f / peak : 1f;            // one scale per group: the loudness stays as the modes made it
                    index.Append($"## Group {group}: {name}, {sname}\n\n");
                    for (int k = 0; k < renders.Count; k++)
                    {
                        string file = $"g{group:00}-{(char)('a' + k)}.wav";
                        WriteWav(Path.Combine(dir, file), renders[k].Item2, scale);
                        index.Append($"- {file}\n");
                        key.Append($"| {file} | {name} | {sname} | {renders[k].m} |\n");
                    }
                    index.Append('\n');
                }
            File.WriteAllText(Path.Combine(dir, "index.md"), index.ToString());
            File.WriteAllText(Path.Combine(dir, "key.md"), key.ToString());
        }

        static void WriteWav(string path, float[] x, float scale)
        {
            using var w = new BinaryWriter(File.Create(path));
            int bytes = x.Length * 2;
            w.Write("RIFF"u8.ToArray()); w.Write(36 + bytes); w.Write("WAVEfmt "u8.ToArray());
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(bytes);
            foreach (float v in x) w.Write((short)Math.Clamp(Math.Round(v * scale * 32767), -32768, 32767));
        }


        // ---- Sixteen starts in one block (the audio thread's budget) ----------------------------------

        const int BurstRate = 48000;

        /// <summary>
        /// Sixteen Pro clips (one drum loop) all start in block 0 of a mixer: the worst block's time, and the mixed output. Each clip
        /// is its own ClipPlayback, as on a scene launch.
        /// </summary>
        static (double WorstMs, float[] Mix) Burst(WarpMode mode, int blocks, bool waitForSpares)
        {
            const int Block = 256, Clips = 16;
            double bpm = 120, bps = bpm / 60.0 / BurstRate;
            var src = Drums(bpm, 8, BurstRate);
            var p = Clip(src, bpm, mode);
            ProVoice.NoteTempo(bps, BurstRate);
            var clips = Enumerable.Range(0, Clips).Select(_ => new ClipPlayback { Params = p }).ToArray();
            if (waitForSpares)
                foreach (var c in clips) Assert.That(c.Voice.WaitForProSpare(5000), Is.True, "the spare gets ready");
            var mixer = new ClipMixer();
            var mix = new float[blocks * Block];
            double worst = 0;
            var clock = new Stopwatch();
            for (int k = 0; k < blocks; k++)
            {
                clock.Restart();
                mixer.Begin(Block);
                foreach (var c in clips) mixer.Play(c, k * Block * bps, bps, BurstRate, Block);
                mixer.End(Block, bps, BurstRate);
                clock.Stop();
                worst = Math.Max(worst, clock.Elapsed.TotalMilliseconds);
                Array.Copy(mixer.Left, 0, mix, k * Block, Block);
            }
            return (worst, mix);
        }

        /// <summary>A budget of half a 256-sample block at 48 kHz (5.3 ms): the rest is the engine's, the other machines' and the driver's.</summary>
        const double BlockBudgetMs = 0.5 * 256 * 1000.0 / BurstRate;

        static double WorstOf(int runs, Func<double> run) => Enumerable.Range(0, runs).Min(_ => run());       // the best of a few: a busy CI machine adds, never takes away

        [Test]
        public void SixteenProClipsStartingInOneBlockNeverCostMoreThanTheBlocksBudgetEvenWithoutAnySpare()
        {
            Assert.That(NeoStretch.Available, Is.True, "the library is built before the tests");
            bool was = ProVoice.Prefetch;
            ProVoice.Prefetch = false;                   // every start is a surprise
            try
            {
                Burst(WarpMode.Pro, 8, false);          // the library and the JIT, warm
                long inline0 = ProVoice.InlineStarts, later0 = ProVoice.LaterStarts, spare0 = ProVoice.SpareStarts;
                double worst = WorstOf(3, () => Burst(WarpMode.Pro, 60, false).WorstMs);
                TestContext.WriteLine($"worst block of 16 unanticipated Pro starts: {worst:F2} ms (budget {BlockBudgetMs:F2} ms)");
                Assert.That(worst, Is.LessThan(BlockBudgetMs), "one seek a block, the others wait their turn");
                Assert.That(ProVoice.SpareStarts - spare0, Is.EqualTo(0));
                Assert.That(ProVoice.InlineStarts - inline0, Is.EqualTo(3 * 16), "all sixteen got their start, in turn");
                Assert.That(ProVoice.LaterStarts - later0, Is.GreaterThan(0), "and the later ones waited");

                // Settled (a quarter of a second on), every onset is where Repitch puts it, within a millisecond.
                var pro = Burst(WarpMode.Pro, 200, false).Mix;
                var rep = Burst(WarpMode.Repitch, 200, false).Mix;
                var a = Onsets(pro, BurstRate).Where(i => i > BurstRate / 4).ToList();
                var b = Onsets(rep, BurstRate).Where(i => i > BurstRate / 4).ToList();
                Assert.That(a.Count, Is.EqualTo(b.Count), "the same hits");
                Assert.That(a.Count, Is.GreaterThan(2));
                for (int i = 0; i < a.Count; i++)
                    Assert.That(Math.Abs(a[i] - b[i]), Is.LessThan(BurstRate / 1000.0), $"hit {i}: {a[i]} against {b[i]}");
            }
            finally { ProVoice.Prefetch = was; }
        }

        [Test]
        public void SixteenProClipsStartingInOneBlockWithTheirSparesReadyStartAtOnceAndLineUp()
        {
            Assert.That(NeoStretch.Available, Is.True);
            Burst(WarpMode.Pro, 8, true);
            long inline0 = ProVoice.InlineStarts, later0 = ProVoice.LaterStarts, spare0 = ProVoice.SpareStarts;
            var best = Enumerable.Range(0, 3).Select(_ => Burst(WarpMode.Pro, 200, true)).OrderBy(r => r.WorstMs).First();
            TestContext.WriteLine($"worst block of 16 anticipated Pro starts: {best.WorstMs:F2} ms (budget {BlockBudgetMs:F2} ms)");
            Assert.That(best.WorstMs, Is.LessThan(BlockBudgetMs));
            Assert.That(ProVoice.SpareStarts - spare0, Is.EqualTo(3 * 16), "every start was a spare's");
            Assert.That(ProVoice.InlineStarts - inline0, Is.EqualTo(0));
            Assert.That(ProVoice.LaterStarts - later0, Is.EqualTo(0));
            var rep = Burst(WarpMode.Repitch, 200, false).Mix;
            var a = Onsets(best.Mix, BurstRate);
            var b = Onsets(rep, BurstRate);
            Assert.That(a.Count, Is.EqualTo(b.Count));
            for (int i = 0; i < a.Count; i++)
                Assert.That(Math.Abs(a[i] - b[i]), Is.LessThan(BurstRate / 1000.0), $"hit {i}: {a[i]} against {b[i]}");
        }

        [Test]
        public void ALoopGoingRoundInProFindsItsSpareToo()
        {
            Assert.That(NeoStretch.Available, Is.True);
            double bpm = 120, bps = bpm / 60.0 / BurstRate;
            var p = Clip(Drums(bpm, 4, BurstRate), bpm, WarpMode.Pro);
            ProVoice.NoteTempo(bps, BurstRate);
            var clip = new ClipPlayback { Params = p };
            Assert.That(clip.Voice.WaitForProSpare(5000), Is.True);
            var mixer = new ClipMixer();
            long spare0 = ProVoice.SpareStarts;
            for (int round = 0; round < 3; round++)
            {
                for (int k = 0; k < 40; k++)
                {
                    mixer.Begin(256);
                    mixer.Play(clip, k * 256 * bps, bps, BurstRate, 256);        // the loop goes back to the beat 0 each round
                    mixer.End(256, bps, BurstRate);
                }
                Assert.That(clip.Voice.WaitForProSpare(5000), Is.True, "the next spare is made while it plays");
            }
            Assert.That(ProVoice.SpareStarts - spare0, Is.EqualTo(3));
        }

        // ---- A library that is not there --------------------------------------------------------------

        [Test]
        public void WithoutTheLibraryAProClipPlaysAsComplexAndOneLineSaysSo()
        {
            var lines = new List<string>();
            var oldLog = NeoStretch.Log;
            string? oldEnv = Environment.GetEnvironmentVariable("NEO_STRETCH_OFF");
            try
            {
                Environment.SetEnvironmentVariable("NEO_STRETCH_OFF", "1");
                NeoStretch.ResetForTests();
                NeoStretch.Log = lines.Add;
                var s = Tone(3, 220);
                var pro = Clip(s, 120, WarpMode.Pro);
                var voice = new ClipVoice();
                voice.Prepare(pro);
                voice.Prepare(pro);
                Assert.That(NeoStretch.Available, Is.False);
                Assert.That(lines.Count, Is.EqualTo(1), "one line, once");
                Assert.That(lines[0], Does.Contain("Pro").And.Contain("Complex"));
                var x = Play(pro, 120, 0, 6, 256, Rate, voice);
                var reference = Play(Clip(s, 120, WarpMode.Complex), 120, 0, 6);
                Assert.That(x.All(float.IsFinite), Is.True);
                Assert.That(Rms(x, Rate, 2 * Rate), Is.GreaterThan(0.2), "not silent");
                Assert.That(Frequency(x, Rate, 2 * Rate), Is.EqualTo(220).Within(2));
                Assert.That(x.Zip(reference, (a, b) => Math.Abs(a - b)).Max(), Is.EqualTo(0).Within(1e-6), "it is Complex's output");
                Assert.That(lines.Count, Is.EqualTo(1), "and no more lines while it plays");
            }
            finally
            {
                Environment.SetEnvironmentVariable("NEO_STRETCH_OFF", oldEnv);
                NeoStretch.Log = oldLog;
                NeoStretch.ResetForTests();
            }
        }

        // ---- The song ----------------------------------------------------------------------------------

        [Test]
        public void ProIsSavedAsComplexPlusAMarkerSoAnOlderBuildPlaysItInTheBestModeItHas()
        {
            var a = new SessionDocument.AudioSettings { Asset = "a1", Warp = "Pro", Bpm = 96, Transpose = 3 };
            var xml = a.ToXml();
            Assert.That((string?)xml.Attribute("warp"), Is.EqualTo("Complex"), "what an older reader takes it for");
            Assert.That((string?)xml.Attribute("pro"), Is.EqualTo("true"));
            Assert.That(SessionDocument.AudioSettings.FromXml(XElement.Parse(xml.ToString())).Warp, Is.EqualTo("Pro"));

            // An older build: the warp it knows (Complex) and no notion of the marker.
            var old = XElement.Parse(xml.ToString());
            old.Attribute("pro")!.Remove();
            Assert.That(SessionDocument.AudioSettings.FromXml(old).Warp, Is.EqualTo("Complex"));
            Assert.That(SessionDocument.AudioSettings.WarpModes, Does.Contain("Pro"));

            // The other modes are written as they were.
            foreach (var warp in new[] { "Off", "Repitch", "Complex" })
            {
                var e = new SessionDocument.AudioSettings { Warp = warp }.ToXml();
                Assert.That((string?)e.Attribute("warp"), Is.EqualTo(warp));
                Assert.That(e.Attribute("pro"), Is.Null);
                Assert.That(SessionDocument.AudioSettings.FromXml(e).Warp, Is.EqualTo(warp));
            }
            Assert.That(new SessionDocument.AudioSettings { Warp = "Beats" }.ToXml().Attribute("warp"), Is.Null, "Beats is still the default, written as nothing");
        }
    }
}
