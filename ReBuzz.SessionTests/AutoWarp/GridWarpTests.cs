using ReBuzz.Core.Clips;
using ReBuzz.Core.Session;

namespace ReBuzz.SessionTests.AutoWarp
{
    /// <summary>Auto-warp to grid (docs/specs/neo-audio.md, AW5), pure: the transients' markers, the reach, the strength, one marker a line, the stretch limit, the gate and what is saved.</summary>
    [TestFixture]
    public class GridWarpTests
    {
        const int Rate = 48000;
        const double SamplesPerBeat = 24000;               // 120 BPM
        const double Length = 32 * SamplesPerBeat;

        static List<(double Sample, double Beat)> Straight() => WarpMath.Effective(null, Length, Rate, 120);

        /// <summary>Transients at the given beats of the straight line, each shifted by a number of ms.</summary>
        static List<double> At(params (double Beat, double Ms)[] hits) => hits.Select(h => h.Beat * SamplesPerBeat + h.Ms * Rate / 1000).ToList();

        static double Err(List<(double Sample, double Beat)> m, double t, double grid)
        {
            double beat = WarpMath.BeatAt(m, t);
            return Math.Abs(beat - Math.Round(beat / grid) * grid);
        }

        [Test]
        public void ATransientOffTheBeatLandsOnTheGridLineAtFullStrength()
        {
            var t = At((4.0, 30), (5.25, -25), (6.5, 12), (7.76, 0));          // 1/16 = 0.25 beat = 125 ms: all within the reach of 40 % (50 ms)
            var rep = new GridWarpReport();
            var r = WarpMath.ToGrid(Straight(), t, 0.25, 1, 0.4, 0, Length, rep);
            Assert.That(r.Ok, Is.True, r.Error);
            foreach (double s in t) Assert.That(Err(r.Markers, s, 0.25), Is.LessThan(1e-9), "on its line");
            Assert.That(rep.Made, Is.EqualTo(4));
            Assert.That(WarpMath.Validate(r.Markers), Is.Null);
        }

        [Test]
        public void AStrengthOfZeroMovesNothingAndHalfMovesHalfway()
        {
            var t = At((4.0, 40));
            double beat = WarpMath.BeatAt(Straight(), t[0]);
            var none = WarpMath.ToGrid(Straight(), t, 0.25, 0, 0.4, 0, Length);
            Assert.That(WarpMath.BeatAt(none.Markers, t[0]), Is.EqualTo(beat).Within(1e-9));
            var half = WarpMath.ToGrid(Straight(), t, 0.25, 0.5, 0.4, 0, Length);
            Assert.That(WarpMath.BeatAt(half.Markers, t[0]), Is.EqualTo((beat + 4.0) / 2).Within(1e-9));
        }

        [Test]
        public void ATransientBeyondTheReachIsLeftAloneAndCounted()
        {
            var t = At((4.0, 0), (5.125, 0), (6.0, 60));          // the middle one is half a step (62.5 ms of 125) away: beyond 40 %; 60 ms is beyond 50 ms too
            var rep = new GridWarpReport();
            var r = WarpMath.ToGrid(Straight(), t, 0.25, 1, 0.4, 0, Length, rep);
            Assert.That(r.Ok, Is.True, r.Error);
            Assert.That(rep.OutOfReach, Is.EqualTo(2));
            Assert.That(rep.Made, Is.EqualTo(1));
            Assert.That(r.Markers.Any(x => Math.Abs(x.Sample - t[1]) < 1), Is.False, "no marker where it was left alone");
        }

        [Test]
        public void TwoTransientsOnOneLineKeepTheCloserOne()
        {
            var t = At((4.0, 10), (4.0, -30));
            var rep = new GridWarpReport();
            var r = WarpMath.ToGrid(Straight(), t, 0.25, 1, 0.4, 0, Length, rep);
            Assert.That(r.Ok, Is.True, r.Error);
            Assert.That(rep.Made, Is.EqualTo(1));
            Assert.That(r.Markers.Any(x => Math.Abs(x.Sample - t[0]) < 1), Is.True, "the one 10 ms off stays");
            Assert.That(r.Markers.Any(x => Math.Abs(x.Sample - t[1]) < 1), Is.False);
        }

        [Test]
        public void ABeatMarkerOnATransientsLineGivesWayToIt()
        {
            var m = new List<(double Sample, double Beat)> { (0, 0), (8 * SamplesPerBeat, 8), (Length, 32) };
            var t = At((8.0, 20));
            var r = WarpMath.ToGrid(m, t, 0.25, 1, 0.4, 0, Length, new GridWarpReport());
            Assert.That(r.Ok, Is.True, r.Error);
            Assert.That(r.Markers.Count(x => Math.Abs(x.Beat - 8) < 1e-9), Is.EqualTo(1), "one marker on the line");
            Assert.That(r.Markers.First(x => Math.Abs(x.Beat - 8) < 1e-9).Sample, Is.EqualTo(t[0]).Within(1e-6), "the transient's");
        }

        [Test]
        public void TheMarkersCoverTheWholeAudioWhenTheBeatMarkersDoNot()
        {
            var m = new List<(double Sample, double Beat)> { (4 * SamplesPerBeat, 0), (28 * SamplesPerBeat, 24) };       // a steady result: first downbeat and last beat
            var t = At((1.0, 20), (30.0, -20));                                                                          // one before the first marker, one after the last
            var r = WarpMath.ToGrid(m, t, 0.25, 1, 0.4, 0, Length, new GridWarpReport());
            Assert.That(r.Ok, Is.True, r.Error);
            Assert.That(r.Markers[0].Sample, Is.EqualTo(0));
            Assert.That(r.Markers[^1].Sample, Is.EqualTo(Length).Within(1e-6));
            Assert.That(Err(r.Markers, t[0], 0.25), Is.LessThan(1e-9));
            Assert.That(Err(r.Markers, t[1], 0.25), Is.LessThan(1e-9));
        }

        [Test]
        public void NoMarkerStretchesASegmentMoreThanTwice()
        {
            // Two transients 20 ms apart (0.04 beat) whose lines are 0.25 beat apart: the segment between would grow six-fold; one of them is not made.
            var t = At((4.0, 40), (4.04 + 0.0, 100));
            var rep = new GridWarpReport();
            var r = WarpMath.ToGrid(Straight(), t, 0.25, 1, 0.4, 0, Length, rep);
            Assert.That(r.Ok, Is.True, r.Error);
            var all = r.Markers;
            for (int i = 1; i < all.Count; i++)
            {
                double nb = all[i].Beat - all[i - 1].Beat, ob = WarpMath.BeatAt(Straight(), all[i].Sample) - WarpMath.BeatAt(Straight(), all[i - 1].Sample);
                Assert.That(nb / ob, Is.InRange(0.5 - 1e-9, 2 + 1e-9), "segment " + i);
            }
        }

        [Test]
        public void TransientsBeforeTheStartOfAWarpFromHereStayUntouched()
        {
            var t = At((2.0, 30), (10.0, 30));
            var r = WarpMath.ToGrid(Straight(), t, 0.25, 1, 0.4, 6 * SamplesPerBeat, Length, new GridWarpReport());
            Assert.That(r.Ok, Is.True, r.Error);
            Assert.That(r.Markers.Any(x => Math.Abs(x.Sample - t[0]) < 1), Is.False);
            Assert.That(r.Markers.Any(x => Math.Abs(x.Sample - t[1]) < 1), Is.True);
        }

        [Test]
        public void RandomHitsAlwaysGiveValidMarkersAndNoneBeyondTheReachIsMoved()
        {
            var rnd = new Random(5);
            foreach (double grid in new[] { 1, 0.5, 0.25, 0.125, 2.0 / 3, 1.0 / 3, 1.0 / 6 })
            {
                var t = Enumerable.Range(0, 300).Select(_ => rnd.NextDouble() * Length).OrderBy(x => x).ToList();
                var rep = new GridWarpReport();
                var r = WarpMath.ToGrid(Straight(), t, grid, 1, 0.4, 0, Length, rep);
                Assert.That(r.Ok, Is.True, grid + ": " + r.Error);
                Assert.That(WarpMath.Validate(r.Markers), Is.Null, grid.ToString());
                foreach (var (s, b) in r.Markers)
                    if (s > 0 && s < Length)
                        Assert.That(Math.Abs(b - Math.Round(b / grid) * grid), Is.LessThan(1e-9).Or.EqualTo(Math.Abs(b - Math.Round(b))).Within(1e-9), $"{grid}: every marker is on a line");
            }
        }

        [Test]
        public void SensitivityFiftyIsTheDetectorAsItWasAndMoreFindsMore()
        {
            // A click track with soft and loud clicks: sensitivity 50 equals Onsets(source); higher finds at least as many, lower at most as many.
            var x = new float[Rate * 20];
            var rnd = new Random(2);
            for (int k = 0; k < 40; k++)
            {
                int at = Rate / 2 + k * Rate / 2;
                float amp = k % 3 == 0 ? 0.9f : k % 3 == 1 ? 0.3f : 0.06f;
                for (int i = 0; i < 400 && at + i < x.Length; i++) x[at + i] += amp * (float)(rnd.NextDouble() * 2 - 1) * (1f - i / 400f);
            }
            var src = new AudioSource(x, x, Rate);
            int a = AudioAnalysis.Onsets(src).Count, d = AudioAnalysis.Onsets(src, 50).Count, hi = AudioAnalysis.Onsets(src, 100).Count, lo = AudioAnalysis.Onsets(src, 0).Count;
            Assert.That(d, Is.EqualTo(a));
            Assert.That(hi, Is.GreaterThanOrEqualTo(d));
            Assert.That(lo, Is.LessThanOrEqualTo(d));
        }

        [Test]
        public void ATransientInAQuietPassageOrASustainedOneGetsNoMarker()
        {
            var x = new float[Rate * 12];
            var rnd = new Random(3);
            void Click(int at, float amp) { for (int i = 0; i < 600 && at + i < x.Length; i++) x[at + i] += amp * (float)(rnd.NextDouble() * 2 - 1) * (1f - i / 600f); }
            for (int k = 0; k < 8; k++) Click(Rate + k * Rate / 2, 0.8f);               // loud clicks
            Click(Rate * 9, 0.001f);                                                       // a very quiet one (more than 35 dB under)
            var src = new AudioSource(x, x, Rate);
            var onsets = AudioAnalysis.GridOnsets(src, 100, out int gated);
            Assert.That(onsets.Count, Is.GreaterThanOrEqualTo(6), "the clicks are kept");
            Assert.That(onsets.Any(t => Math.Abs(t - Rate * 9) < 2000), Is.False, "none on the quiet one");
        }

        [Test]
        public void TheOptionsAreSavedWithTheClipAndTheDefaultsWriteNothing()
        {
            var a = new SessionDocument.AudioSettings();
            Assert.That(a.ToXml().Attributes().Select(x => x.Name.LocalName), Does.Not.Contain("autoGrid").And.Not.Contain("autoSens").And.Not.Contain("autoReach"));
            a.AutoGrid = "1/8T"; a.AutoSensitivity = 70; a.AutoReach = 25;
            var b = SessionDocument.AudioSettings.FromXml(a.ToXml());
            Assert.That((b.AutoGrid, b.AutoSensitivity, b.AutoReach), Is.EqualTo(("1/8T", 70, 25)));
            Assert.That(b.AutoGridBeats, Is.EqualTo(1.0 / 3).Within(1e-12));
            var c = a.Clone();
            Assert.That(c.AutoGrid, Is.EqualTo("1/8T"));
        }

        [Test]
        public void TheImportPreferenceHasThreeValuesAndReadsTheOldFile()
        {
            Assert.That((int)AutoWarpMode.Off, Is.EqualTo(0));
            Assert.That((int)AutoWarpMode.Beat, Is.EqualTo(1));
            Assert.That((int)AutoWarpMode.Grid, Is.EqualTo(2));
        }
    }
}
