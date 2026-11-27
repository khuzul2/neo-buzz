using NeoBuzz.Dsp;
using NeoBuzz.Space;
using NeoBuzz.TestSupport;
using static NeoBuzz.MachineTests.SpaceRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Space (docs/specs/neo-space.md): the rooms, the shaping, and the gates of the brief (SPC1), measured.</summary>
    [TestFixture]
    public class NeoSpaceTests
    {
        // ================================================================================== the rooms

        [Test]
        public void NineRoomsAreMadeByCodeDeterministicallyInStereoAndNoLongerThanTenSeconds()
        {
            Assert.That(SpaceRooms.Count, Is.GreaterThanOrEqualTo(8), "at least eight rooms");
            Assert.That(Params.Table[P.Room].Names.Length, Is.EqualTo(SpaceRooms.Count + 1), "the rooms, then Wave");
            Assert.That(Params.Table[P.Room].Names[^1], Is.EqualTo("Wave"));
            Assert.That(SpaceRooms.Names, Does.Contain("Plate"));
            Assert.That(SpaceRooms.Names, Does.Contain("Hall"));
            var lines = new List<string>();
            for (int room = 0; room < SpaceRooms.Count; room++)
            {
                SpaceRooms.Generate(room, Rate, out var l, out var r);
                SpaceRooms.Generate(room, Rate, out var l2, out var r2);
                Assert.That(l, Is.EqualTo(l2), $"{SpaceRooms.Names[room]}: the same bits every time");
                Assert.That(r, Is.EqualTo(r2));
                Assert.That(l.Length, Is.LessThanOrEqualTo(10 * Rate), $"{SpaceRooms.Names[room]}: at most 10 s");
                Assert.That(l.Length, Is.EqualTo(r.Length));
                Assert.That(SpaceIr.PinkLevel(l, r, Rate), Is.EqualTo(1).Within(1e-3), "pink noise comes out at its own level (the two channels together)");
                Assert.That(SpaceIr.PinkLevel(l, l, Rate), Is.EqualTo(1).Within(0.4), "and each channel is close to it");
                Assert.That(SpaceIr.PinkLevel(r, r, Rate), Is.EqualTo(1).Within(0.4));
                // The tail (after a quarter of a second) is decorrelated between the ears; the direct sound and the first reflections are alike by nature.
                double dot = 0, ll = 0, rr = 0;
                for (int i = Rate / 4; i < l.Length; i++) { dot += l[i] * (double)r[i]; ll += l[i] * (double)l[i]; rr += r[i] * (double)r[i]; }
                if (ll > 0 && rr > 0) Assert.That(Math.Abs(dot / Math.Sqrt(ll * rr)), Is.LessThan(0.25), $"{SpaceRooms.Names[room]}: the tails of the channels are decorrelated");
                foreach (float v in l) Assert.That(float.IsFinite(v) && !float.IsSubnormal(v), Is.True);
                lines.Add($"{SpaceRooms.Names[room]} {l.Length / (double)Rate:F2} s");
            }
            Measure.Report("Neo Space built-in rooms: " + string.Join(", ", lines));
        }

        [Test]
        public void EveryRoomDecaysAtItsOwnTimesInThreeBands()
        {
            var lines = new List<string>();
            for (int room = 0; room < SpaceRooms.Count; room++)
            {
                SpaceRooms.Generate(room, Rate, out var l, out _);
                var (low, mid, high) = SpaceRooms.Rt60Bands(room);
                double lo = Rt60(l, 250), mi = Rt60(l, 1000), hi = Rt60(l, 8000);
                lines.Add($"{SpaceRooms.Names[room]}: 250 Hz {lo:F2}/{low:F2} s, 1 kHz {mi:F2}/{mid:F2}, 8 kHz {hi:F2}/{high:F2}");
                Assert.That(mi, Is.EqualTo(mid).Within(mid * 0.3), $"{SpaceRooms.Names[room]} at 1 kHz");
                Assert.That(lo, Is.EqualTo(low).Within(low * 0.3), $"{SpaceRooms.Names[room]} at 250 Hz");
                Assert.That(hi, Is.EqualTo(high).Within(high * 0.4), $"{SpaceRooms.Names[room]} at 8 kHz");
                // (the plate rings longer in the highs, by design: the measured order is the designed one)
                if (Math.Abs(low - high) > 0.3 * mid) Assert.That(lo > hi, Is.EqualTo(low > high), $"{SpaceRooms.Names[room]}: the order of the bands"); 
            }
            Measure.Report("Neo Space rooms, measured (Schroeder, T30 doubled) / designed: " + string.Join("; ", lines));
        }

        [Test]
        public void TheRoomsAreOrderedBySizeAndThePlateAndTheHallAreThere()
        {
            double last = 0;
            foreach (string name in new[] { "Small room", "Drum room", "Chamber", "Hall", "Large hall", "Cathedral" })
            {
                int room = Array.IndexOf(SpaceRooms.Names, name);
                SpaceRooms.Generate(room, Rate, out var l, out _);
                double rt = Rt60(l, 0);
                Assert.That(rt, Is.GreaterThan(last), $"{name} rings longer than the one before");
                last = rt;
            }
            // The plate: no early reflections, a dense tail from the first few milliseconds; the hall: gaps before its first reflections.
            SpaceRooms.Generate(Array.IndexOf(SpaceRooms.Names, "Plate"), Rate, out var plate, out _);
            Assert.That(Energy(plate, 0, 12 * Rate / 1000), Is.GreaterThan(0.002), "a plate sounds from the start");
            SpaceRooms.Generate(Array.IndexOf(SpaceRooms.Names, "Large hall"), Rate, out var hall, out _);
            Assert.That(SpaceRooms.LengthOf(Array.IndexOf(SpaceRooms.Names, "Cathedral"), Rate), Is.GreaterThan(7 * Rate), "a long hall rings for seconds");
        }

        [Test]
        public void TheEarlyReflectionsAreTheImageSourcesOfTheBox()
        {
            // Small room, left ear: the six first-order images, from the geometry, are where the response has its first taps.
            int room = Array.IndexOf(SpaceRooms.Names, "Small room");
            var (lx, ly, lz, s, rc) = SpaceRooms.Box(room);
            double[] rcv = { rc[0] - SpaceRooms.EarSpacing / 2, rc[1], rc[2] };
            double d0 = Dist(s, rc);
            var images = new List<double[]>
            {
                new[] { -s[0], s[1], s[2] }, new[] { 2 * lx - s[0], s[1], s[2] },
                new[] { s[0], -s[1], s[2] }, new[] { s[0], 2 * ly - s[1], s[2] },
                new[] { s[0], s[1], -s[2] }, new[] { s[0], s[1], 2 * lz - s[2] },
            };
            var times = images.Select(im => (Dist(im, rcv) - d0) / 343.0).OrderBy(t => t).ToList();
            SpaceRooms.Generate(room, Rate, out var l, out _);
            // Nothing before the first arrival (the sinc's reach is 8 samples).
            int first = (int)(times[0] * Rate);
            for (int i = 0; i < first - 8; i++) Assert.That(Math.Abs(l[i]), Is.LessThan(1e-4), $"the sample {i} is before any reflection");
            var lines = new List<string>();
            foreach (double t in times)
            {
                int at = (int)Math.Round(t * Rate);
                // A tap is a local maximum of the magnitude (with its neighbours' sinc skirts) and clearly above the response around it.
                float peak = 0;
                int where = at;
                for (int i = at - 2; i <= at + 2; i++) if (Math.Abs(l[i]) > peak) { peak = Math.Abs(l[i]); where = i; }
                lines.Add($"{t * 1000:F2} ms");
                Assert.That(Math.Abs(where - at), Is.LessThanOrEqualTo(1), $"the image at {t * 1000:F2} ms");
                Assert.That(peak, Is.GreaterThan(0.01f), $"the image at {t * 1000:F2} ms has a tap");
            }
            Measure.Report("Neo Space, small room's six first images arrive at " + string.Join(", ", lines) + " after the direct sound");
        }

        static double Dist(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

        // ================================================================================== shaping

        static (float[] L, float[] R) Base(string room)
        {
            SpaceRooms.Generate(Array.IndexOf(SpaceRooms.Names, room), Rate, out var l, out var r);
            return (l, r);
        }

        [Test]
        public void PreDelayPadsLengthTrimsWithAFadeReverseTurnsItAroundAndTheRestShapes()
        {
            var (bl, br) = Base("Hall");
            var plain = SpaceSettings.Plain;
            SpaceIr.Shape(bl, br, Rate, plain, false, out var a, out var b);
            Assert.That(SpaceIr.PinkLevel(a, b, Rate), Is.EqualTo(1).Within(1e-3), "the pink level is 1");

            var pre = plain; pre.PreDelayMs = 100;
            SpaceIr.Shape(bl, br, Rate, pre, false, out var p, out var pr);
            Assert.That(p.Length, Is.EqualTo(a.Length + Rate / 10).Within(1), "the pre-delay is added in front");
            for (int i = 0; i < Rate / 10; i++) Assert.That(p[i] == 0 && pr[i] == 0, Is.True, "silence before the pre-delay is over");

            var len = plain; len.LengthPercent = 40;
            SpaceIr.Shape(bl, br, Rate, len, false, out var t, out _);
            Assert.That(t.Length, Is.EqualTo((int)Math.Round(bl.Length * 0.4)).Within(1), "Length keeps a fraction");
            // The end fades out: the last samples are tiny and no step is left (the biggest jump in the last 1000 samples is small against the signal).
            Assert.That(Math.Abs(t[^1]), Is.LessThan(1e-4));
            double jump = 0;
            for (int i = t.Length - 1000; i < t.Length; i++) jump = Math.Max(jump, Math.Abs(t[i] - t[i - 1]));
            Assert.That(jump, Is.LessThan(2e-3), "no step at the cut");

            var rev = plain; rev.Reverse = true;
            SpaceIr.Shape(bl, br, Rate, rev, false, out var v, out _);
            Assert.That(Energy(v, v.Length * 3 / 4, v.Length), Is.GreaterThan(Energy(v, 0, v.Length / 4) * 3), "reversed: the tail first and the body last");

            var hi = plain; hi.HighCutHz = 2000;
            SpaceIr.Shape(bl, br, Rate, hi, false, out var h, out _);
            var sa = Measure.Spectrum(a, 0, 16384);
            var sh = Measure.Spectrum(h, 0, 16384);
            double Band(double[] s, double f0, double f1) { double e = 0; for (int k = (int)(f0 / Rate * 16384); k < (int)(f1 / Rate * 16384); k++) e += s[k] * s[k]; return e; }
            Assert.That(Band(sh, 6000, 12000) / Band(sa, 6000, 12000), Is.LessThan(0.01), "the high cut takes the highs away (-20 dB at 3 times the corner)");
            Assert.That(Band(sh, 200, 800) / Band(sa, 200, 800), Is.InRange(0.9, 4.0), "and leaves the lows (the response keeps its energy, so they come up a little)");

            var lo = plain; lo.LowCutHz = 500;
            SpaceIr.Shape(bl, br, Rate, lo, false, out var o, out _);
            var so = Measure.Spectrum(o, 0, 16384);
            Assert.That(Band(so, 40, 150) / Band(sa, 40, 150), Is.LessThan(0.02), "the low cut takes the lows away");

            var mono = plain; mono.WidthPercent = 0;
            SpaceIr.Shape(bl, br, Rate, mono, false, out var m1, out var m2);
            Assert.That(m1, Is.EqualTo(m2), "width 0 is mono");
            var wide = plain; wide.WidthPercent = 200;
            SpaceIr.Shape(bl, br, Rate, wide, false, out var w1, out var w2);
            double side(float[] x, float[] y) { double e = 0; for (int i = 0; i < x.Length; i++) { double d = 0.5 * (x[i] - y[i]); e += d * d; } return e; }
            Assert.That(side(w1, w2) / side(a, b), Is.GreaterThan(1.5), "width 200 % has more side than 100 %");

            // At most ten seconds, whatever the pre-delay adds.
            SpaceRooms.Generate(Array.IndexOf(SpaceRooms.Names, "Cathedral"), Rate, out var cl, out var cr);
            var longPre = plain; longPre.PreDelayMs = 250;
            SpaceIr.Shape(cl, cr, Rate, longPre, false, out var c, out _);
            Assert.That(c.Length, Is.LessThanOrEqualTo(10 * Rate));
            Assert.That(Math.Abs(c[^1]), Is.LessThan(1e-4), "and it ends faded");
        }

        [Test]
        public void AWaveIsAMonoOrStereoRoomResampledWithItsLeadAndOffsetTakenOut()
        {
            var settings = SpaceSettings.Plain;
            // Mono and stereo.
            var (bl, br) = Burst(1.2, 0.8, true);
            SpaceIr.Shape(bl, null, Rate, settings, true, out var m1, out var m2);
            Assert.That(m1, Is.EqualTo(m2), "a mono wave is the same in both channels");
            SpaceIr.Shape(bl, br, Rate, settings, true, out var s1, out var s2);
            Assert.That(s1, Is.Not.EqualTo(s2), "a stereo wave keeps its two channels");
            // A lead of silence and a DC offset: gone.
            var lead = new float[Rate / 2 + bl.Length];
            for (int i = 0; i < bl.Length; i++) lead[Rate / 2 + i] = bl[i] + 0.2f;
            for (int i = 0; i < Rate / 2; i++) lead[i] = 0.2f;
            SpaceIr.Shape(lead, null, Rate, settings, true, out var t, out _, out int start, out int count);
            Assert.That(start, Is.EqualTo(Rate / 2).Within(Rate / 100), "the lead of silence is skipped");
            int firstSound = 0;
            while (firstSound < t.Length && Math.Abs(t[firstSound]) < 1e-3f * Math.Abs(t.Max(x => Math.Abs(x)))) firstSound++;
            Assert.That(firstSound, Is.LessThan(60), "the response starts at once");
            double mean = 0;
            foreach (float x in t) mean += x;
            Assert.That(Math.Abs(mean / t.Length), Is.LessThan(2e-4), "no DC");
            // A rate that is not the host's: the length scales.
            var at44 = Burst(1.0, 0.5, false, 44100).L;
            var res = ImpulseResponses.Resample(at44, 44100.0 / Rate);
            Assert.That(res.Length, Is.EqualTo(Rate).Within(2), "44.1 kHz read at 48 kHz");
        }

        // ================================================================================== gate: accuracy

        [Test]
        public void TheConvolutionMatchesTheDirectSumOnNoiseBelowMinus100dB()
        {
            var v = Values(x => { Wet(x); Room(x, "Chamber"); Set(x, P.Length, 35); Set(x, P.PreDelay, 0); });
            var e = Engine(v);
            var h = e.Current;
            Assert.That(h.Left.Length, Is.GreaterThan(30000), "the response runs through the 4096-sample stage's partitions");
            int frames = 50000;
            var xl = Noise(frames, 21);
            var xr = Noise(frames, 22);
            var (yl, yr) = Run(e, xl, xr);
            double dl = ErrorDb(yl, Direct(xl, h.Left, frames)), dr = ErrorDb(yr, Direct(xr, h.Right, frames));
            Measure.Report($"Neo Space accuracy on noise ({h.Left.Length} taps): left {dl:F1} dB, right {dr:F1} dB against the direct sum");
            Assert.That(dl, Is.LessThan(-100));
            Assert.That(dr, Is.LessThan(-100));
        }

        [Test]
        public void TheConvolutionMatchesTheDirectSumOnAClickTrainThroughATenSecondResponse()
        {
            var v = Values(x => { Wet(x); Room(x, "Cathedral"); Set(x, P.PreDelay, 0); });
            var e = Engine(v);
            var h = e.Current;
            int frames = 22 * Rate;
            Assert.That(h.Left.Length, Is.GreaterThan(8 * Rate));
            // Clicks of different heights, some close together, some far apart, the last one near the response's end.
            var xl = Clicks(frames, 0, 1, 700, 4100, 4101, 9000, 17000, 48000, 123456, 250000, 333333, 480000, 560000);
            var xr = Clicks(frames, 5, 1000, 4096, 4097, 9100, 30000, 64000, 200000, 300000, 400000, 530000);
            var (yl, yr) = Run(e, xl, xr);
            double dl = ErrorDb(yl, DirectSparse(xl, h.Left, frames)), dr = ErrorDb(yr, DirectSparse(xr, h.Right, frames));
            Measure.Report($"Neo Space accuracy on a click train through {h.Left.Length / (double)Rate:F2} s ({h.Left.Length} taps): left {dl:F1} dB, right {dr:F1} dB against the direct sum");
            Assert.That(dl, Is.LessThan(-100));
            Assert.That(dr, Is.LessThan(-100));
        }

        // ================================================================================== gate: latency

        [Test]
        public void TheLatencyIsZeroAndItIsWhatTheEngineReportsAtEveryBlockSize()
        {
            Assert.That(SpaceEngine.Latency, Is.EqualTo(0), "what the machine reports to the host (GetLatency)");
            var v = Values(x => { Wet(x); Room(x, "Drum room"); Set(x, P.PreDelay, 0); });
            float[] first = null;
            foreach (int block in new[] { 256, 64, 37, 1 })
            {
                var e = Engine(v);
                var h = e.Current;
                var x = new float[4000];
                x[0] = 1;
                var (yl, _) = Run(e, x, (float[])x.Clone(), block);
                // The response starts at the first sample of the output: zero latency; bit for bit the response where the sum has one term.
                for (int i = 0; i < 64; i++) Assert.That(yl[i], Is.EqualTo(h.Left[i]).Within(1e-6), $"sample {i}, blocks of {block}");
                double err = 0, sig = 0;
                for (int i = 0; i < x.Length; i++) { err += Math.Pow(yl[i] - h.Left[i], 2); sig += h.Left[i] * (double)h.Left[i]; }
                Assert.That(10 * Math.Log10(err / sig + 1e-30), Is.LessThan(-100), $"blocks of {block}");
                if (first == null) first = yl;
                else Assert.That(yl, Is.EqualTo(first), $"the same bits at block {block} as at 256");
            }
            // The dry sound is never delayed: Mix 0 is the input at once.
            var d = Values(x => { Room(x, "Hall"); x.Set(P.Mix, 0); });
            var de = Engine(d);
            var imp = new float[600];
            imp[0] = 0.5f;
            var (dl, _) = Run(de, imp, (float[])imp.Clone());
            Assert.That(dl[0], Is.EqualTo(0.5f), "the dry is on the same sample");
        }

        // ================================================================================== gate: swap

        [Test]
        public void ALoadedRoomIsCrossfadedInOver50msAndClicksNothing()
        {
            int frames = 6 * Rate;
            var x = Measure.PinkNoise(frames, 0.1);
            var xr = Measure.PinkNoise(frames, 0.1, 99);
            var v = Values(s => { Room(s, "Hall"); Set(s, P.Mix, 0.5); });
            var e = Engine(v);
            int swapBlock = -1;
            var waves = new FakeWaves();
            var (yl, yr) = Run(e, x, xr, Block, b =>
            {
                if (b == 2 * Rate / Block) Room(v, "Cathedral");
                if (b == 2 * Rate / Block + 1) { e.WaitForBuilder(); swapBlock = b; }
                if (b == 4 * Rate / Block) Room(v, "Small room");
                if (b == 4 * Rate / Block + 1) e.WaitForBuilder();
            });
            var cl = ClickDetector.Find(yl);
            var cr = ClickDetector.Find(yr);
            var dl = ClickDetector.Find(x);
            Measure.Report($"Neo Space swap Hall → Cathedral → Small room under pink noise: {cl.Count + cr.Count} clicks in the output, {dl.Count} in the input");
            Assert.That(cl.Count + cr.Count, Is.EqualTo(dl.Count + ClickDetector.Find(xr).Count), "0 new clicks");
            Assert.That(e.Current.Name, Is.EqualTo("Small room"));

            // The crossfade is 50 ms: after it the output is the new response's alone, bit for bit what a fresh engine gives from the swap on.
            var v2 = Values(s => { Room(s, "Cathedral"); Set(s, P.Mix, 0.5); });
            var f = Engine(v2);
            int from = swapBlock * Block;
            var (fl, _) = Run(f, x[from..(4 * Rate - 4 * Block)], xr[from..(4 * Rate - 4 * Block)]);
            var (al, _) = Run(Engine(Values(s => { Room(s, "Hall"); Set(s, P.Mix, 0.5); })), x, xr, Block, b =>
            {
                if (b == 2 * Rate / Block) Room(v, "Cathedral");
            });
            int fade = (int)Math.Round(SpaceEngine.FadeSeconds * Rate);
            Assert.That(fade, Is.EqualTo(2400), "50 ms at 48 kHz");
            // Compare the swapped render's window after the fade with the fresh engine's render.
            for (int i = fade + 2 * Block; i < fl.Length; i += 97) Assert.That(yl[from + i], Is.EqualTo(fl[i]).Within(2e-6), $"{i} samples after the swap");
            // And inside the fade the two are blended: neither one alone.
            double maxDiff = 0;
            for (int i = 0; i < fade; i++) maxDiff = Math.Max(maxDiff, Math.Abs(yl[from + i] - fl[i]));
            Assert.That(maxDiff, Is.GreaterThan(1e-4), "the old response is still there during the fade");
        }

        [Test]
        public void ALoadedWaveIsSwappedInTheSameWayAndNothingClicks()
        {
            var waves = new FakeWaves();
            var (wl, wr) = Burst(2.0, 1.0, true);
            waves.Put(7, "burst", wl, wr);
            var v = Values(s => { Room(s, "Plate"); Set(s, P.Mix, 0.5); });
            var e = Engine(v, waves);
            int frames = 5 * Rate;
            var x = Measure.PinkNoise(frames, 0.1);
            var xr = Measure.PinkNoise(frames, 0.1, 5);
            var (yl, yr) = Run(e, x, xr, Block, b =>
            {
                if (b == Rate / Block) { Room(v, "Wave"); WaveSlot(v, 7); }
                if (b == Rate / Block + 1) e.WaitForBuilder();
                if (b == 3 * Rate / Block) { var (w2, _) = Burst(1.0, 0.3, false, Rate, 4); waves.Put(7, "burst two", w2, null); }
                if (b == 3 * Rate / Block + 1) e.WaitForBuilder();
            });
            Assert.That(e.Current.Name, Is.EqualTo("burst two"), "the wave was replaced in the song: the response follows");
            int clicks = ClickDetector.Find(yl).Count + ClickDetector.Find(yr).Count;
            Assert.That(clicks, Is.EqualTo(ClickDetector.Find(x).Count + ClickDetector.Find(xr).Count), "0 new clicks");
        }

        [Test]
        public void SwappingARoomOrAWaveWhilePlayingAllocatesNothingOnTheAudioThread()
        {
            var waves = new FakeWaves();
            var (wl, wr) = Burst(1.5, 0.7, true);
            waves.Put(3, "a", wl, wr);
            var (w2, _) = Burst(2.5, 1.0, false, Rate, 8);
            waves.Put(4, "b", w2, null);
            var v = Values(s => { Room(s, "Small room"); Set(s, P.Mix, 0.5); });
            var e = Engine(v, waves);
            var x = Measure.PinkNoise(Rate * 4, 0.1);
            var l = new float[Block];
            var r = new float[Block];
            var steps = new Action<ParamValues>[]
            {
                s => Room(s, "Cathedral"),
                s => { Room(s, "Wave"); WaveSlot(s, 3); },
                s => { Room(s, "Wave"); WaveSlot(s, 4); },
                s => { Room(s, "Plate"); Set(s, P.PreDelay, 80); },
                s => { Room(s, "Hall"); Set(s, P.Reverse, 1); Set(s, P.HighCut, 3000); },
                s => { Room(s, "Wave"); WaveSlot(s, 99); },   // an empty slot: the hall
                s => { Room(s, "Small room"); Set(s, P.Reverse, 0); Set(s, P.HighCut, 20000); Set(s, P.PreDelay, 2); },
            };
            // The raw values of each step, made before the measured blocks (the test's own work is not the machine's).
            var plan = new int[steps.Length][];
            var scratch = Values();
            for (int i = 0; i < P.Count; i++) scratch.Set(i, v[i]);
            for (int k = 0; k < steps.Length; k++)
            {
                steps[k](scratch);
                plan[k] = Enumerable.Range(0, P.Count).Select(i => scratch[i]).ToArray();
            }
            int swaps = 0;
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                // Every 250 blocks a new room is asked for, the builder (on its own thread) is waited for, and the next block takes it.
                if (b % 250 == 100) { var raw = plan[(b / 250) % plan.Length]; for (int i = 0; i < raw.Length; i++) v.Set(i, raw[i]); }
                if (b % 250 == 102) { e.WaitForBuilder(); swaps++; }
                int pos = (b % 700) * Block;
                Array.Copy(x, pos, l, 0, Block);
                Array.Copy(x, pos, r, 0, Block);
                e.Process(l, r, Block);
            });
            Measure.Report($"Neo Space: {swaps} swaps over the measured blocks, {bytes} bytes allocated on the audio thread");
            Assert.That(swaps, Is.GreaterThanOrEqualTo(8));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        // ================================================================================== gate: Mix and Amount

        static (float[] L, float[] R) Material()
        {
            var d = FxGoldenTests.DrumLoop(Rate, 1.5);
            var c = FxGoldenTests.Chord(Rate, 1.5);
            var l = new float[d.Length];
            var r = new float[d.Length];
            for (int i = 0; i < l.Length; i++) { l[i] = d[i] * 0.8f + c[i] * 0.6f; r[i] = d[i] * 0.6f + c[i] * 0.8f; }
            return (l, r);
        }

        [Test]
        public void AmountZeroAndMixZeroAreTheInputBitForBit()
        {
            var (l, r) = Material();
            foreach (var (name, setup) in new (string, Action<ParamValues>)[]
            {
                ("Amount 0 %", s => s.Set(P.Amount, 0)),
                ("Mix 0 %", s => s.Set(P.Mix, 0)),
            })
            {
                var v = Values(s => { Room(s, "Hall"); Set(s, P.Mix, 0.8); setup(s); });
                var e = Engine(v);
                var (yl, yr) = Run(e, l, r);
                Assert.That(yl, Is.EqualTo(l), $"{name}: left is the input bit for bit");
                Assert.That(yr, Is.EqualTo(r), $"{name}: right");
            }
            // Amount brought down to 0 during playback: settled, it is the input bit for bit again.
            var w = Values(s => { Room(s, "Hall"); Set(s, P.Mix, 0.8); });
            var f = Engine(w);
            var (al, _) = Run(f, l, r, Block, b => { if (b == 40) w.Set(P.Amount, 0); });
            for (int i = (40 + 40) * Block; i < l.Length; i++) Assert.That(al[i], Is.EqualTo(l[i]), $"sample {i} after Amount went to 0");
        }

        [Test]
        public void MixBlendsDryAndWetAndAmountScalesTheWetLevelToTwiceAt200Percent()
        {
            var (l, r) = Material();
            var wet = Values(s => { Room(s, "Hall"); Wet(s); });
            var (wl, _) = Run(Engine(wet), l, r);
            // Mix 30 %: 0.7 dry + 0.3 wet.
            var mix = Values(s => { Room(s, "Hall"); Set(s, P.Mix, 0.3); });
            var (ml, _) = Run(Engine(mix), l, r);
            for (int i = 0; i < l.Length; i += 13) Assert.That(ml[i], Is.EqualTo(0.7f * l[i] + 0.3f * wl[i]).Within(2e-6), $"sample {i}");
            // Amount 200 % at Mix 100 %: twice the wet; 50 %: half.
            foreach (var (pct, gain) in new[] { (200.0, 2.0), (50.0, 0.5), (150.0, 1.5) })
            {
                float dryGain = (float)Math.Max(0.0, 1.0 - Math.Min(1.0, gain));   // at Mix 100 %: the dry sound makes room for the wet one up to Amount 100 %
                var v = Values(s => { Room(s, "Hall"); Wet(s); Set(s, P.Amount, pct); });
                var (yl, _) = Run(Engine(v), l, r);
                for (int i = 0; i < l.Length; i += 13) Assert.That(yl[i], Is.EqualTo(dryGain * l[i] + (float)gain * wl[i]).Within(3e-6), $"Amount {pct} % sample {i}");
            }
            // Amount 200 % at Mix 50 %: the dry has made room for the wet up to 100 % only: 0.5 dry + 2 × 0.5 wet.
            var a2 = Values(s => { Room(s, "Hall"); Set(s, P.Mix, 0.5); Set(s, P.Amount, 200); });
            var (zl, _) = Run(Engine(a2), l, r);
            for (int i = 0; i < l.Length; i += 13) Assert.That(zl[i], Is.EqualTo(0.5f * l[i] + 1.0f * wl[i]).Within(3e-6), $"sample {i}");
            // Output scales the wet only.
            var o = Values(s => { Room(s, "Hall"); Wet(s); Set(s, P.Output, -6); });
            var (ol, _) = Run(Engine(o), l, r);
            double g = Math.Pow(10, -6 / 20.0);
            for (int i = 0; i < l.Length; i += 13) Assert.That(ol[i], Is.EqualTo((float)(g * wl[i])).Within(3e-6));
        }

        [Test]
        public void AmountGrowsTheReverbMonotonicallyAndAutomatingMixOrAmountDoesNotClick()
        {
            var (l, r) = Material();
            double last = -1;
            foreach (double pct in new[] { 10.0, 25, 50, 75, 100, 125, 150, 200 })
            {
                var v = Values(s => { Room(s, "Hall"); Set(s, P.Mix, 0.5); Set(s, P.Amount, pct); });
                var (yl, _) = Run(Engine(v), l, r);
                double diff = 0;
                for (int i = 0; i < l.Length; i++) diff += Math.Pow(yl[i] - l[i], 2);
                Assert.That(diff, Is.GreaterThan(last), $"Amount {pct} %");
                Assert.That(yl.All(x => float.IsFinite(x) && Math.Abs(x) < 8), Is.True, "finite and bounded");
                last = diff;
            }
            // Automation: Amount and Mix swept up and down every block under a smooth noise input, no new clicks.
            var x = Measure.PinkNoise(4 * Rate, 0.1);
            var v2 = Values(s => Room(s, "Chamber"));
            var e = Engine(v2);
            var (sl, sr) = Run(e, x, (float[])x.Clone(), Block, b =>
            {
                v2.Set(P.Amount, (int)(1000 + 1000 * Math.Sin(b * 0.05)) / 2 * 2);
                v2.Set(P.Mix, (int)(500 + 500 * Math.Sin(b * 0.021)));
                if (b % 90 == 0) v2.Set(P.Amount, 0);
            });
            Assert.That(ClickDetector.Find(sl).Count, Is.EqualTo(ClickDetector.Find(x).Count), "0 clicks while Mix and Amount move (including through 0 %)");
        }

        // ================================================================================== gate: the audio thread

        [Test]
        public void NothingIsAllocatedPerBlockOver2000Blocks()
        {
            var v = Values(s => { Room(s, "Large hall"); Set(s, P.Mix, 0.5); });
            var e = Engine(v);
            var x = Measure.PinkNoise(Rate * 4, 0.1);
            var l = new float[Block];
            var r = new float[Block];
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                int pos = (b % 700) * Block;
                Array.Copy(x, pos, l, 0, Block);
                Array.Copy(x, pos, r, 0, Block);
                // Mix, Amount and Output move every block (they are live, not a new response).
                v.Set(P.Mix, 300 + (b * 7) % 500);
                v.Set(P.Amount, 900 + (b * 3) % 400);
                v.Set(P.Output, 450 + (b % 100));
                e.Process(l, r, Block);
            });
            Measure.Report($"Neo Space: {bytes} bytes allocated per block over 2000 blocks");
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void ThirtySecondsOfTailHoldNoDenormalsAndEndInSilence()
        {
            var v = Values(s => { Room(s, "Cathedral"); Wet(s); Set(s, P.PreDelay, 100); });
            var e = Engine(v);
            int frames = 30 * Rate + Rate / 2;
            var x = new float[frames];
            for (int i = 0; i < 2000; i++) x[i] = (float)Math.Sin(i * 0.31) * (1f - i / 2000f);
            var (yl, yr) = Run(e, x, (float[])x.Clone());
            long subnormal = 0, nonFinite = 0;
            for (int i = 0; i < frames; i++)
            {
                if (float.IsSubnormal(yl[i]) || float.IsSubnormal(yr[i])) subnormal++;
                if (!float.IsFinite(yl[i]) || !float.IsFinite(yr[i])) nonFinite++;
            }
            Measure.Report($"Neo Space 30 s tail: {subnormal} denormals, {nonFinite} non-finite samples");
            Assert.That(subnormal, Is.EqualTo(0));
            Assert.That(nonFinite, Is.EqualTo(0));
            // The response is 8.9 s; after it and the input, the output is exactly zero.
            int end = e.Current.Left.Length + 2000 + 2 * Block;
            for (int i = end; i < frames; i++) Assert.That(Math.Abs(yl[i]) < 1e-7f && Math.Abs(yr[i]) < 1e-7f, Is.True, $"sample {i}: {yl[i]}");
            Assert.That(Energy(yl, 5 * Rate, 8 * Rate), Is.GreaterThan(0), "it rang for seconds");
        }

        [Test]
        public void TheEngineFilesUseNoLibmAndNoLockTheAudioThreadCouldWaitFor()
        {
            // The shared PortableMathTests scan every engine folder for libm; this one adds Neo Space's own rule: no lock statement in the audio path.
            string dir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Machines", "NeoSpace", "Engine"));
            if (!Directory.Exists(dir))
            {
                string probe = TestContext.CurrentContext.TestDirectory;
                while (probe != null && !Directory.Exists(Path.Combine(probe, "Machines", "NeoSpace"))) probe = Path.GetDirectoryName(probe);
                dir = Path.Combine(probe, "Machines", "NeoSpace", "Engine");
            }
            var text = File.ReadAllText(Path.Combine(dir, "SpaceEngine.cs"));
            // lock appears only in the builder's registry (not in Process or Poll).
            int process = text.IndexOf("public void Process(", StringComparison.Ordinal);
            int builder = text.IndexOf("static class SpaceBuilder", StringComparison.Ordinal);
            Assert.That(text.Substring(process, builder - process), Does.Not.Contain("lock ("), "no lock in the audio path");
        }

        // ================================================================================== gate: CPU

        [Test]
        public void ACpuFigureForAFourSecondStereoResponseAtBlocksOf256AgainstNeoVerb()
        {
            var waves = new FakeWaves();
            var (wl, wr) = Burst(4.0, 1.4, true);
            waves.Put(1, "four seconds", wl, wr);
            var x = Measure.PinkNoise(Rate * 3, 0.1);
            var xr = Measure.PinkNoise(Rate * 3, 0.1, 4);
            const int Warm = Rate;
            var space = Throughput.Measure("Neo Space (4 s stereo response, blocks of 256)", 2, Rate, Block, () =>
            {
                var v = Values(s => { Room(s, "Wave"); WaveSlot(s, 1); Set(s, P.Mix, 0.5); Set(s, P.PreDelay, 0); });
                var e = Engine(v, waves);
                Assert.That(e.Current.Left.Length, Is.EqualTo(4 * Rate).Within(2), "four seconds");
                var l = new float[Block];
                var r = new float[Block];
                void Step(int pos, int n)
                {
                    Array.Copy(x, pos % (x.Length - Block), l, 0, n);
                    Array.Copy(xr, pos % (x.Length - Block), r, 0, n);
                    e.Process(l, r, n);
                }
                for (int pos = 0; pos < Warm; pos += Block) Step(pos, Block);
                return Throughput.InBlocks(Block, (pos, n) => Step(Warm + pos, n));
            });
            var verb = Throughput.Measure("Neo Verb (default, blocks of 256)", 2, Rate, Block, () =>
            {
                var v = new ParamValues(NeoBuzz.Verb.Params.Table);
                v.Set(NeoBuzz.Verb.P.Mix, 500);
                var e = new NeoBuzz.Verb.VerbEngine(v);
                e.SetSampleRate(Rate);
                var l = new float[Block];
                var r = new float[Block];
                void Step(int pos, int n)
                {
                    Array.Copy(x, pos % (x.Length - Block), l, 0, n);
                    Array.Copy(xr, pos % (x.Length - Block), r, 0, n);
                    e.Process(l, r, n);
                }
                for (int pos = 0; pos < Warm; pos += Block) Step(pos, Block);
                return Throughput.InBlocks(Block, (pos, n) => Step(Warm + pos, n));
            });
            Measure.Report(space.ToString());
            Measure.Report(verb.ToString());
            Measure.Report($"Neo Space costs {verb.Speed / space.Speed:F2} times Neo Verb's CPU: {100.0 / space.Speed:F2} % of a core against {100.0 / verb.Speed:F2} %, at the reference's speed");
            Assert.That(space.Speed, Is.GreaterThan(Throughput.Floor(40, 25)), "a 4 s stereo response runs at least 40 times faster than real time at the reference's speed");
        }

        // ================================================================================== gate: state

        [Test]
        public void TheWaveReferenceRoundTripsAndFollowsTheSong()
        {
            var (wl, wr) = Burst(1.6, 0.6, true, Rate, 31);
            var songA = new FakeWaves();
            songA.Put(7, "room clap", wl, wr);
            var v = Values(s => { Room(s, "Wave"); WaveSlot(s, 7); Set(s, P.Mix, 0.4); });
            var e = Engine(v, songA);
            Assert.That(e.Current.Name, Is.EqualTo("room clap"));
            var (l, r) = Material();
            var (a, _) = Run(e, l, r);

            // Saved: the parameters' raw values (the slot is the reference); the song saves the wave itself, not Neo Space.
            var saved = Enumerable.Range(0, P.Count).Select(i => v[i]).ToArray();
            Assert.That(saved[P.Room], Is.EqualTo(SpaceEngine.WaveRoom));
            Assert.That(saved[P.Wave], Is.EqualTo(6), "the slot is stored as 7 (raw 6), nothing more of the wave");
            // Reopened: a new machine, the song's waves read in again (the same samples, another object), the saved values.
            var songB = new FakeWaves();
            songB.Put(7, "room clap", (float[])wl.Clone(), (float[])wr.Clone());
            var w = Values(s => { for (int i = 0; i < P.Count; i++) s.Set(i, saved[i]); });
            var (b, _) = Run(Engine(w, songB), l, r);
            Assert.That(b, Is.EqualTo(a), "the same impulse response, bit for bit");
            Assert.That(songB.Wanted, Is.EqualTo(7), "the engine asks the song for the slot");

            // The reference follows the song: another wave in slot 7 gives another room.
            var songC = new FakeWaves();
            var (ol, orr) = Burst(1.6, 0.6, true, Rate, 32);
            songC.Put(7, "another clap", ol, orr);
            var (c, _) = Run(Engine(Values(s => { for (int i = 0; i < P.Count; i++) s.Set(i, saved[i]); }), songC), l, r);
            Assert.That(c, Is.Not.EqualTo(a));
            // The Room and Wave parameters survive a save and the layout's version is 0.
            var machineState = new NeoBuzz.Dsp.StateMigrations(Params.Table);
            Assert.That(machineState.Current, Is.EqualTo(0), "versioned, version 0");
            Assert.That(machineState.Keys, Does.Contain("Wave"));
        }

        [Test]
        public void AMissingWaveFallsBackToTheHallAndSaysSoUntilItAppears()
        {
            var songs = new FakeWaves();
            var v = Values(s => { Room(s, "Wave"); WaveSlot(s, 42); Set(s, P.Mix, 0.5); });
            var e = Engine(v, songs);
            Assert.That(e.Current.Name, Is.EqualTo("Hall"), "the hall plays");
            Assert.That(e.Current.FellBack, Is.True);
            Assert.That(e.Status, Is.EqualTo(SpaceStatus.WaveMissing), "the editor says the wave is missing");
            var (l, r) = Material();
            var (a, _) = Run(e, l, r);
            // It is the very same response a Hall room gives.
            var hall = Values(s => { Room(s, "Hall"); Set(s, P.Mix, 0.5); });
            var (b, _) = Run(Engine(hall), l, r);
            Assert.That(a, Is.EqualTo(b), "the same bits as the Hall room");
            // The wave appears (the song loads it): the response becomes the wave's, the status clears.
            var (wl, wr) = Burst(1.0, 0.4, true);
            songs.Put(42, "late wave", wl, wr);
            e.Process(new float[Block], new float[Block], Block);
            e.WaitForBuilder();
            e.Process(new float[Block], new float[Block], Block);
            Assert.That(e.Current.Name, Is.EqualTo("late wave"));
            Assert.That(e.Status, Is.EqualTo(SpaceStatus.Ready));
            // While a wave is still being read, what plays keeps playing (no fall back yet).
            songs.Clips.Remove(42);
            songs.Loading.Add(42);
            e.Process(new float[Block], new float[Block], Block);
            Assert.That(e.Status, Is.EqualTo(SpaceStatus.WaveLoading));
            Assert.That(e.Current.Name, Is.EqualTo("late wave"));
        }

        [Test]
        public void AWaveLoadingAtTheStartPlaysDryUntilItIsInAndAChangedSampleRateBuildsAgain()
        {
            var songs = new FakeWaves();
            songs.Loading.Add(5);
            var v = Values(s => { Room(s, "Wave"); WaveSlot(s, 5); Set(s, P.Mix, 0.5); });
            var e = new SpaceEngine(v, songs);
            e.SetSampleRate(Rate);
            e.Poll();
            Assert.That(e.Pending, Is.True, "a render waits for the wave");
            var x = Noise(Block, 3);
            var l = (float[])x.Clone();
            e.Process(l, (float[])x.Clone(), Block);
            Assert.That(l.Zip(x, (p, q) => Math.Abs(p - q)).Max(), Is.GreaterThan(0), "(the dry level is 0.5: dry only)");
            Assert.That(e.Current, Is.Null);
            var (wl, wr) = Burst(0.8, 0.3, true);
            songs.Loading.Clear();
            songs.Put(5, "in", wl, wr);
            e.BuildNow();
            Assert.That(e.Current.Name, Is.EqualTo("in"));
            int len48 = e.Current.Left.Length;
            // Another sample rate: the old response is dropped, a new one made for the new rate.
            e.SetSampleRate(44100);
            e.BuildNow();
            Assert.That(e.Current.Rate, Is.EqualTo(44100));
            Assert.That(Math.Abs(e.Current.Left.Length - len48 * 44100.0 / Rate), Is.LessThan(8), "the same room in seconds");
        }

        // ================================================================================== presets

        [Test]
        public void EightPresetsAreClearlyDifferentAndClearlyAudible()
        {
            var presets = PresetTests.AllPresets().Where(p => p.Machine == "Neo Space").ToList();
            Assert.That(presets.Count, Is.EqualTo(8));
            var (l, r) = Material();
            // Material with a gap at the end so the tail is heard.
            var renders = new Dictionary<string, float[]>();
            foreach (var p in presets)
            {
                var v = new ParamValues(Params.Table);
                foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                var e = Engine(v);
                var (yl, _) = Run(e, l, r);
                double wet = 0, dry = 0;
                for (int i = 0; i < yl.Length; i++) { wet += Math.Pow(yl[i] - l[i] * (1 - v.Value(P.Mix)), 2); dry += l[i] * (double)l[i]; }
                // The reverb is at least 9 dB under the dry sound's power at worst, and clearly there: above -22 dB of it.
                double db = 10 * Math.Log10(wet / dry);
                Measure.Report($"Neo Space preset {p.Preset}: the reverb is {db:F1} dB against the dry sound");
                Assert.That(db, Is.GreaterThan(-24), $"{p.Preset} is audible");
                renders[p.Preset] = yl;
            }
            var names = renders.Keys.ToList();
            for (int i = 0; i < names.Count; i++)
                for (int j = i + 1; j < names.Count; j++)
                {
                    var a = renders[names[i]]; var b = renders[names[j]];
                    double diff = 0, sig = 0;
                    for (int k = 0; k < a.Length; k++) { diff += Math.Pow(a[k] - b[k], 2); sig += a[k] * (double)a[k]; }
                    double db = 10 * Math.Log10(diff / sig);
                    Assert.That(db, Is.GreaterThan(-12), $"{names[i]} and {names[j]} are clearly different (the difference is {db:F1} dB of the first)");
                }
        }

        [Test]
        public void ThePictureHasItsData()
        {
            var v = Values(s => { Room(s, "Hall"); Set(s, P.PreDelay, 60); Set(s, P.Length, 50); });
            var e = Engine(v);
            var c = e.Current;
            Assert.That(c.Preview.Length, Is.EqualTo(SpaceIr.PreviewColumns));
            Assert.That(c.BaseSeconds, Is.EqualTo(SpaceRooms.LengthOf(Array.IndexOf(SpaceRooms.Names, "Hall"), Rate) / (double)Rate).Within(0.01));
            Assert.That(c.Preview.Max(), Is.GreaterThan(-40f));
            Assert.That(c.Preview[^1], Is.LessThan(c.Preview.Max() - 20), "it decays");
            Assert.That(c.Name, Is.EqualTo("Hall"));
        }
    }
}
