using ReBuzz.Core.Clips;
using ReBuzz.SessionTests.Convert;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// Slice to new MIDI track (docs/specs/audio-to-midi.md, SL1), pure: where a clip is cut on synthetic loops with known hits, the limit of 16 slices
    /// (the strongest transients kept, a grid that would make more refused), and the slices' places in the clip's beats.
    /// </summary>
    [TestFixture]
    public class SliceMathTests
    {
        const int Rate = ConvertSignals.Rate;

        /// <summary>A click: a 2 kHz tone that starts at zero and dies in a few milliseconds, at known times and levels.</summary>
        static float[] Clicks(IEnumerable<(double At, double Level)> hits, double seconds)
        {
            var x = new float[(int)(Rate * seconds)];
            foreach (var (at, level) in hits)
            {
                int a = (int)Math.Round(at * Rate);
                for (int i = 0; i < Rate / 50 && a + i < x.Length; i++)
                {
                    double t = i / (double)Rate;
                    x[a + i] += (float)(level * Math.Sin(2 * Math.PI * 2000 * t) * Math.Exp(-t / 0.003));
                }
            }
            return x;
        }

        /// <summary>The plan of a mono loop that is <paramref name="beats"/> beats long, from beat 0 to its end (or the given region).</summary>
        static SlicePlan Plan(float[] x, SliceBy by, double beats, double? start = null, double? end = null, WarpMap? map = null, double beatsPerBar = 4)
        {
            var source = new AudioSource(x, x, Rate);
            map ??= WarpMap.Straight(x.Length, beats);
            var onsets = AudioAnalysis.Onsets(source);
            Func<long, int, float[]> mono = (at, n) =>
            {
                var l = new float[n];
                source.CopyExact(0, at, l);
                return l;
            };
            return SliceMath.Plan(by, map, x.Length, Rate, start ?? 0, end ?? beats, beatsPerBar, onsets, (at, n) => mono(at, n).Select(Math.Abs).ToArray(), mono);
        }

        static double Ms(long samples) => samples * 1000.0 / Rate;

        [Test]
        public void ASliceStartsWithinAMillisecondOfEachKnownHit()
        {
            // eight clicks, two seconds: a bar at 120 BPM, off the grid of the detector's frames
            var hits = Enumerable.Range(0, 8).Select(i => (At: 0.0 + i * 0.25 + (i % 3) * 0.0007, Level: 0.5 + 0.05 * i)).ToArray();
            var plan = Plan(Clicks(hits, 2.0), SliceBy.Transients, 4);
            Assert.That(plan.Ok, plan.Why);
            Assert.That(plan.Slices.Count, Is.EqualTo(8));
            for (int i = 0; i < 8; i++)
                Assert.That(Ms(plan.Slices[i].Start), Is.EqualTo(hits[i].At * 1000).Within(1.0), $"slice {i + 1} starts on its hit");
            Assert.That(plan.Slices.Sum(s => s.Length), Is.EqualTo(plan.EndSample - plan.StartSample), "the slices tile the region");
            for (int i = 1; i < 8; i++) Assert.That(plan.Slices[i].Start, Is.EqualTo(plan.Slices[i - 1].Start + plan.Slices[i - 1].Length), "no gap, no overlap");
        }

        [Test]
        public void ADrumLoopIsCutOnItsKicksSnaresAndHats()
        {
            // 120 BPM, a bar of eighths: kick on 1 and 3, snare on 2 and 4, hats between
            double beat = 0.5;
            var hits = new List<(string, double)>();
            for (int i = 0; i < 8; i++)
            {
                double at = i * beat / 2 * 1.0;
                hits.Add((i % 4 == 0 ? "kick" : i % 4 == 2 ? "snare" : "hat", at));
            }
            var x = ConvertSignals.Drums(hits, 2.0);
            var plan = Plan(x, SliceBy.Transients, 4);
            Assert.That(plan.Ok, plan.Why);
            Assert.That(plan.Slices.Count, Is.EqualTo(8), "every hit is a slice: " + string.Join(" ", plan.Slices.Select(q => (Ms(q.Start)).ToString("F1") + "/" + q.Strength.ToString("F3"))));
            for (int i = 0; i < 8; i++)
                Assert.That(Ms(plan.Slices[i].Start), Is.EqualTo(hits[i].Item2 * 1000).Within(1.0), $"slice {i + 1} ({hits[i].Item1}) starts on its hit");
        }

        [Test]
        public void MoreThanSixteenTransientsKeepTheStrongestAndMergeTheRestIntoTheSliceBefore()
        {
            // 24 clicks, the levels all different; the first is the strongest of those it must not lose
            var hits = Enumerable.Range(0, 24).Select(i => (At: i * 0.125, Level: 0.2 + 0.03 * ((i * 7) % 24))).ToArray();
            var plan = Plan(Clicks(hits, 3.0), SliceBy.Transients, 6);
            Assert.That(plan.Ok, plan.Why);
            Assert.That(plan.Slices.Count, Is.EqualTo(SliceMath.MaxSlices));
            Assert.That(plan.Found, Is.EqualTo(24));
            Assert.That(plan.Capped, Is.True);
            // the first slice is the region's start (hit 0); of the others the 15 strongest by level stay
            var strongest = hits.Skip(1).OrderByDescending(h => h.Level).Take(15).Select(h => h.At).OrderBy(a => a).ToList();
            var starts = plan.Slices.Skip(1).Select(s => Ms(s.Start) / 1000).ToList();
            Assert.That(starts.Count, Is.EqualTo(15));
            for (int i = 0; i < 15; i++) Assert.That(starts[i], Is.EqualTo(strongest[i]).Within(0.001), $"the {i + 1}th kept hit");
            Assert.That(plan.Slices.Sum(s => s.Length), Is.EqualTo(plan.EndSample - plan.StartSample), "the merged slices are inside the one before: nothing is lost");
        }

        [Test]
        public void SixteenTransientsOrFewerAreNotCapped()
        {
            var hits = Enumerable.Range(0, 16).Select(i => (At: i * 0.125, Level: 0.5)).ToArray();
            var plan = Plan(Clicks(hits, 2.0), SliceBy.Transients, 4);
            Assert.That(plan.Slices.Count, Is.EqualTo(16));
            Assert.That(plan.Capped, Is.False);
            Assert.That(plan.Found, Is.EqualTo(16));
        }

        [Test]
        public void SilenceBeforeTheFirstHitIsNotASliceButAMarkerOnTheFirstBeatIs()
        {
            var late = Plan(Clicks(new[] { (0.3, 0.6), (0.8, 0.6), (1.3, 0.6) }, 2.0), SliceBy.Transients, 4);
            Assert.That(late.Slices.Count, Is.EqualTo(3), "the quiet before the first hit isn't one");
            Assert.That(Ms(late.Slices[0].Start), Is.EqualTo(300).Within(1.0));
            Assert.That(late.Slices[0].Beat, Is.EqualTo(0.6).Within(0.01), "its note is where the hit is, in beats");

            var onBeat = Plan(Clicks(new[] { (0.0, 0.6), (0.5, 0.6), (1.0, 0.6) }, 2.0), SliceBy.Transients, 4);
            Assert.That(onBeat.Slices.Count, Is.EqualTo(3));
            Assert.That(onBeat.Slices[0].Start, Is.EqualTo(0));
        }

        [Test]
        public void ASoundBeforeTheFirstHitIsTheFirstSlice()
        {
            var x = Clicks(new[] { (0.0, 0.25), (0.6, 0.6), (1.2, 0.6) }, 2.0);
            for (int i = 0; i < Rate / 4; i++) x[i] += 0.1f * (float)Math.Sin(2 * Math.PI * 300 * i / Rate);       // a tail that sounds from the start
            var plan = Plan(x, SliceBy.Transients, 4);
            Assert.That(plan.Slices.Count, Is.EqualTo(3));
            Assert.That(plan.Slices[0].Start, Is.EqualTo(0));
            Assert.That(Ms(plan.Slices[1].Start), Is.EqualTo(600).Within(1.0));
        }

        [Test]
        public void TheGridCutsOnBeatsAndRefusesMoreThanSixteen()
        {
            var x = new float[4 * Rate];                    // 8 beats at 120 BPM
            var bars = Plan(x, SliceBy.Bar, 8);
            Assert.That(bars.Slices.Count, Is.EqualTo(2));
            Assert.That(bars.Slices.Select(s => s.Beat), Is.EqualTo(new[] { 0.0, 4.0 }));
            Assert.That(bars.Slices[0].Length, Is.EqualTo(2 * Rate), "a bar is two seconds here");

            var quarters = Plan(x, SliceBy.Quarter, 8);
            Assert.That(quarters.Slices.Count, Is.EqualTo(8));
            Assert.That(quarters.Slices.Select(s => s.Beat), Is.EqualTo(Enumerable.Range(0, 8).Select(i => (double)i)));
            Assert.That(quarters.Slices.Select(s => s.Beats), Is.All.EqualTo(1.0).Within(1e-9));

            var eighths = Plan(x, SliceBy.Eighth, 8);
            Assert.That(eighths.Slices.Count, Is.EqualTo(16), "16 slices are allowed");
            Assert.That(eighths.Found, Is.EqualTo(16));

            var sixteenths = Plan(x, SliceBy.Sixteenth, 8);
            Assert.That(sixteenths.Ok, Is.False);
            Assert.That(sixteenths.Found, Is.EqualTo(32), "the count is there for the tooltip");
            Assert.That(sixteenths.Why, Does.Contain("32").And.Contain("16"));
            Assert.That(sixteenths.Slices, Is.Empty);

            Assert.That(SliceMath.GridCount(0, 8, 0.25), Is.EqualTo(32));
            Assert.That(SliceMath.GridCount(0, 7.9, 0.5), Is.EqualTo(16), "the last may be shorter");
        }

        [Test]
        public void ABarIsAsLongAsTheSongsBar()
        {
            var x = new float[6 * Rate];                    // 12 beats at 120 BPM
            var threeFour = Plan(x, SliceBy.Bar, 12, beatsPerBar: 3);
            Assert.That(threeFour.Slices.Select(s => s.Beat), Is.EqualTo(new[] { 0.0, 3.0, 6.0, 9.0 }));
        }

        [Test]
        public void WarpMarkersInsideTheRegionAreTheCuts()
        {
            var x = new float[4 * Rate];
            // the audio's seconds 0, 1, 2.5, 4 are heard on beats 0, 2, 3, 8 (tempo changes)
            var map = new WarpMap(new[] { new WarpMarker(0, 0), new WarpMarker(Rate, 2), new WarpMarker(2.5 * Rate, 3), new WarpMarker(4 * Rate, 8) });
            var plan = Plan(x, SliceBy.WarpMarkers, 8, map: map);
            Assert.That(plan.Slices.Count, Is.EqualTo(3), "the two ends aren't cuts");
            Assert.That(plan.Slices.Select(s => s.Beat), Is.EqualTo(new[] { 0.0, 2.0, 3.0 }));
            Assert.That(plan.Slices.Select(s => s.Beats), Is.EqualTo(new[] { 2.0, 1.0, 5.0 }));
            Assert.That(plan.Slices.Select(s => s.Start), Is.EqualTo(new long[] { 0, Rate, (long)(2.5 * Rate) }));

            var straight = Plan(x, SliceBy.WarpMarkers, 8);
            Assert.That(straight.Ok, Is.False, "a straight clip has nothing inside it");
            Assert.That(straight.Why, Does.Contain("warp markers"));

            var inner = Plan(x, SliceBy.WarpMarkers, 8, start: 2.0, end: 8.0, map: map);
            Assert.That(inner.Slices.Select(s => s.Beat), Is.EqualTo(new[] { 2.0, 3.0 }), "a region starts its own first slice");
        }

        [Test]
        public void ASliceIsPlacedByTheClipsBeatsWhereTheTempoIsNotTheLoopsOwn()
        {
            // the loop's own tempo is 120 BPM (a click every half second), the clip plays it at 100 BPM: 0.6 s a beat
            var hits = Enumerable.Range(0, 8).Select(i => (At: i * 0.5, Level: 0.6)).ToArray();
            var map = WarpMap.Straight(4 * Rate, 4 * 100 / 120.0 * 2);          // 4 s of audio over 6.667 beats: 100 BPM
            var plan = Plan(Clicks(hits, 4.0), SliceBy.Transients, 0, 0, 4 * 100 / 120.0 * 2, map);
            Assert.That(plan.Slices.Count, Is.EqualTo(8));
            for (int i = 0; i < 8; i++) Assert.That(plan.Slices[i].Beat, Is.EqualTo(i * 0.5 * 100 / 60).Within(0.002), "the beat, as the clip is warped");
        }

        [Test]
        public void ARegionOfTheClipIsAllThatIsCut()
        {
            var hits = Enumerable.Range(0, 16).Select(i => (At: i * 0.25, Level: 0.6)).ToArray();
            var plan = Plan(Clicks(hits, 4.0), SliceBy.Transients, 8, start: 2.0, end: 6.0);       // seconds 1 to 3
            Assert.That(plan.Slices.Count, Is.EqualTo(8));
            Assert.That(plan.StartSample, Is.EqualTo(Rate));
            Assert.That(plan.EndSample, Is.EqualTo(3 * Rate));
            Assert.That(Ms(plan.Slices[0].Start), Is.EqualTo(1000).Within(1.0));
            Assert.That(plan.Slices[0].Beat, Is.EqualTo(2.0).Within(0.01));
        }

        [Test]
        public void NothingToCutIsSaidInWords()
        {
            var silent = Plan(new float[2 * Rate], SliceBy.Transients, 4);
            Assert.That(silent.Ok, Is.False);
            Assert.That(silent.Why, Does.Contain("transients"));
            var one = Plan(Clicks(new[] { (0.5, 0.6) }, 2.0), SliceBy.Transients, 4);
            Assert.That(one.Ok, Is.False, "one hit is one slice: nothing to cut");
            var empty = Plan(new float[2 * Rate], SliceBy.Quarter, 4, start: 2, end: 2);
            Assert.That(empty.Why, Does.Contain("empty"));
        }

        [Test]
        public void TheNamesParse()
        {
            foreach (var by in SliceMath.All)
            {
                Assert.That(SliceMath.TryParse(SliceMath.Key(by), out var back), Is.True, SliceMath.Key(by));
                Assert.That(back, Is.EqualTo(by));
            }
            Assert.That(SliceMath.TryParse("Warp markers", out var w) && w == SliceBy.WarpMarkers, Is.True);
            Assert.That(SliceMath.TryParse("1 bar", out var b) && b == SliceBy.Bar, Is.True);
            Assert.That(SliceMath.TryParse("1/32", out _), Is.False);
        }
    }
}
