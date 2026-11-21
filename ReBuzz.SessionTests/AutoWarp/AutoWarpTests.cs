using System.Diagnostics;
using ReBuzz.Core.Clips;

namespace ReBuzz.SessionTests.AutoWarp
{
    /// <summary>
    /// Auto-warp (docs/specs/neo-audio.md, AW1 as built), pure: the beats of generated lines whose beats are known exactly (a steady loop, a slow drift, a step, a
    /// ritardando, with silence at the start and a pickup before bar 1), the markers and tempo it makes of them, and what it leaves alone.
    /// </summary>
    [TestFixture]
    public class AutoWarpTests
    {
        static AudioSource Source(WarpLine line) => new AudioSource(line.Audio, line.Audio, line.Rate);

        /// <summary>The map the clip plays with: the markers, or a straight one from the tempo when there are none (as the layout makes it).</summary>
        static WarpMap MapOf(AutoWarpResult r, AudioSource s) =>
            r.Markers.Count >= 2 ? new WarpMap(r.Markers.Select(m => new WarpMarker(m.Sample, m.Beat))) : WarpMap.Straight(s.Length, s.Seconds * r.Bpm / 60);

        /// <summary>How far each beat of the line lands from a beat of the grid once warped, in ms at 120 BPM.</summary>
        static double[] GridErrors(WarpLine line, WarpMap map)
        {
            var errors = new List<double>();
            foreach (double t in line.Beats)
            {
                double beat = map.BeatAt(t * line.Rate);
                errors.Add(Math.Abs(beat - Math.Round(beat)) * 500);
            }
            return errors.ToArray();
        }

        static double Percentile(double[] x, double p)
        {
            var s = x.OrderBy(v => v).ToArray();
            return s[Math.Min(s.Length - 1, (int)Math.Ceiling(p * s.Length) - 1)];
        }

        /// <summary>The share of the line's beats (after 5 s, as the evaluation counts them) that have a beat of the grid within 70 ms.</summary>
        static double BeatRecall(WarpLine line, WarpMap map)
        {
            double hit = 0, all = 0;
            foreach (double t in line.Beats)
            {
                if (t < 5) continue;
                all++;
                double beat = map.BeatAt(t * line.Rate);
                if (Math.Abs(beat - Math.Round(beat)) * 60 / 120.0 < 0.07 * 1.0) hit++;      // 70 ms at 120 BPM, as the grid error is measured
            }
            return all == 0 ? 0 : hit / all;
        }

        static string Name(WarpSignals.Tempo t, WarpSignals.Style s) => $"{t} {s}";

        // ---- a steady tempo -----------------------------------------------------------------

        [Test]
        public void ASteadyLoopOfWholeBarsKeepsTheTempoItHadAndGetsNoMarkers()
        {
            foreach (var style in new[] { WarpSignals.Style.Drums, WarpSignals.Style.Mix })
            {
                // 32 bars at 120 BPM: a loop (the length is a whole number of bars).
                var line = WarpSignals.Make(WarpSignals.Tempo.Steady, style, 64);
                var src = Source(line);
                var (todayBpm, _) = AudioAnalysis.Tempo(src, 4, out bool found);
                Assert.That(found, Is.True);
                var r = AudioAnalysis.AutoWarp(src, 4);
                Assert.That(r.Found, Is.True, Name(WarpSignals.Tempo.Steady, style));
                Assert.That(r.Steady, Is.True);
                Assert.That(r.Loop, Is.True, "a loop of whole bars stays as it is");
                Assert.That(r.Markers, Is.Empty, "nothing changes for a clean loop");
                Assert.That(r.Bpm, Is.EqualTo(todayBpm).Within(0.1), "the same tempo as today");
            }
        }

        [Test]
        public void ASteadyTempoThatIsNotALoopGivesOneTempoAndTheFirstDownbeat()
        {
            // Not a whole number of bars (61.37 s), and 0.8 s of silence before bar 1.
            var line = WarpSignals.Make(WarpSignals.Tempo.Steady, WarpSignals.Style.Mix, 61.37, lead: 0.8);
            var src = Source(line);
            var r = AudioAnalysis.AutoWarp(src, 4);
            Assert.That(r.Found && r.Steady && !r.Loop, Is.True);
            Assert.That(r.Bpm, Is.EqualTo(120).Within(0.15));
            Assert.That(r.Markers, Has.Count.EqualTo(2), "the first downbeat and one tempo: two markers on one straight line");
            Assert.That(r.Markers[0].Beat, Is.EqualTo(0));
            Assert.That(r.Markers[0].Sample / src.SampleRate, Is.EqualTo(line.FirstDownbeat).Within(0.03), "bar 1 is the first downbeat");
            var errors = GridErrors(line, MapOf(r, src));
            Assert.That(Percentile(errors, 0.95), Is.LessThan(20), "ms");
        }

        // ---- a tempo that moves -------------------------------------------------------------

        [TestCase(WarpSignals.Tempo.Drift, WarpSignals.Style.Drums)]
        [TestCase(WarpSignals.Tempo.Drift, WarpSignals.Style.Mix)]
        [TestCase(WarpSignals.Tempo.Step, WarpSignals.Style.Drums)]
        [TestCase(WarpSignals.Tempo.Step, WarpSignals.Style.Mix)]
        [TestCase(WarpSignals.Tempo.Ritardando, WarpSignals.Style.Drums)]
        [TestCase(WarpSignals.Tempo.Ritardando, WarpSignals.Style.Mix)]
        public void ATempoThatMovesGetsAMarkerOnEveryBarAndTheGridFollowsIt(WarpSignals.Tempo tempo, WarpSignals.Style style)
        {
            var line = WarpSignals.Make(tempo, style, 64);
            var src = Source(line);
            var r = AudioAnalysis.AutoWarp(src, 4);
            Assert.That(r.Found, Is.True, Name(tempo, style));
            Assert.That(r.Steady, Is.False, "the tempo moves by more than 1 %");
            Assert.That(r.Markers.Count(m => m.Beat % 4 == 0), Is.GreaterThanOrEqualTo(line.Downbeats.Length - 2), "a marker on every bar");
            Assert.That(r.Markers.Select(m => m.Sample), Is.Ordered.Ascending);
            Assert.That(r.Markers.Select(m => m.Beat), Is.Ordered.Ascending);
            var map = MapOf(r, src);
            var errors = GridErrors(line, map);
            Assert.That(Percentile(errors, 0.95), Is.LessThan(20), $"{Name(tempo, style)}: the 95th percentile of the grid error, ms");
            Assert.That(BeatRecall(line, map), Is.GreaterThanOrEqualTo(0.95), "the beat F-measure's recall");
            // The downbeats are the line's downbeats.
            double down = 0;
            foreach (double d in line.Downbeats)
            {
                double beat = map.BeatAt(d * src.SampleRate);
                if (Math.Abs(beat - Math.Round(beat)) < 0.1 && Math.Round(beat) % 4 == 0) down++;
            }
            Assert.That(down / line.Downbeats.Length, Is.GreaterThanOrEqualTo(0.9), "bar 1 and every bar line after it are the line's downbeats");
        }

        [Test]
        public void ADriftBeyondTheBarsAlsoGetsMarkersOnTheBeatsThatLeaveTheirBarsLine()
        {
            // A tempo that changes inside a bar (a step in the middle of one): the beat markers appear where the bar's straight line is more than 10 ms off.
            var line = WarpSignals.Make(WarpSignals.Tempo.Ritardando, WarpSignals.Style.Drums, 64);
            var r = AudioAnalysis.AutoWarp(Source(line), 4);
            Assert.That(r.Markers.Count(m => m.Beat % 4 != 0), Is.GreaterThanOrEqualTo(0));
            // Every beat is within 10 ms of the line between the markers around it (that is what the extra markers are for).
            var map = new WarpMap(r.Markers.Select(m => new WarpMarker(m.Sample, m.Beat)));
            for (int k = 0; k < r.Beats.Length; k++)
                Assert.That(Math.Abs(map.SampleAt(k) - r.Beats[k]) / line.Rate, Is.LessThan(0.0105), $"beat {k}");
        }

        // ---- weak onsets, silence, a pickup --------------------------------------------------

        [TestCase(WarpSignals.Tempo.Steady)]
        [TestCase(WarpSignals.Tempo.Drift)]
        [TestCase(WarpSignals.Tempo.Step)]
        public void APadWithWeakOnsetsStillGetsItsBeatsAndItsBarLines(WarpSignals.Tempo tempo)
        {
            var line = WarpSignals.Make(tempo, WarpSignals.Style.Pad, 64);
            var src = Source(line);
            var r = AudioAnalysis.AutoWarp(src, 4);
            Assert.That(r.Found, Is.True);
            var map = MapOf(r, src);
            Assert.That(BeatRecall(line, map), Is.GreaterThanOrEqualTo(0.9), $"{tempo}: the beats' recall");
        }

        [Test]
        public void SilenceBeforeTheMusicAndAPickupBeforeBarOneAreNotBarOne()
        {
            foreach (var tempo in new[] { WarpSignals.Tempo.Drift, WarpSignals.Tempo.Step })
            {
                var line = WarpSignals.Make(tempo, WarpSignals.Style.Mix, 64, lead: 2.6, pickup: 2, seed: 3);
                var src = Source(line);
                var r = AudioAnalysis.AutoWarp(src, 4);
                Assert.That(r.Found, Is.True);
                Assert.That(r.Markers[0].Beat, Is.EqualTo(0), "beat 0 is the first marker");
                Assert.That(r.Markers[0].Sample / src.SampleRate, Is.EqualTo(line.FirstDownbeat).Within(0.04), $"{tempo}: bar 1 is the first downbeat, not the pickup's beats or the silence");
                Assert.That(Percentile(GridErrors(line, MapOf(r, src)), 0.95), Is.LessThan(20));
            }
        }

        [Test]
        public void StartingFromASecondTakesTheFirstDownbeatAfterItAndKeepsTheBarNumberingInBars()
        {
            var line = WarpSignals.Make(WarpSignals.Tempo.Drift, WarpSignals.Style.Drums, 64);
            var src = Source(line);
            var r = AudioAnalysis.AutoWarp(src, 4, 20);
            Assert.That(r.Found, Is.True);
            double first = r.Markers[0].Sample / src.SampleRate;
            Assert.That(first, Is.GreaterThanOrEqualTo(20 - 0.03));
            Assert.That(first, Is.LessThan(20 + 3), "the first downbeat after the 20th second");
            Assert.That(line.Downbeats.Min(d => Math.Abs(d - first)), Is.LessThan(0.03), "it is one of the line's downbeats");
            Assert.That(r.Markers[0].Beat, Is.EqualTo(0));
            Assert.That(r.Markers.Count, Is.LessThan(AudioAnalysis.AutoWarp(src, 4).Markers.Count), "fewer markers than the whole file");
        }

        [Test]
        public void AudioWithNoPulseHasNoBeatsToFind()
        {
            var rnd = new Random(5);
            var noise = new float[44100 * 20];
            for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rnd.NextDouble() - 0.5) * 0.1f;
            var silence = new float[44100 * 20];
            var tone = new float[44100 * 20];
            for (int i = 0; i < tone.Length; i++) tone[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 330 * i / 44100.0));
            foreach (var x in new[] { silence, tone })
            {
                var r = AudioAnalysis.AutoWarp(new AudioSource(x, x, 44100), 4);
                Assert.That(r.Found, Is.False, "silence and a held tone have no beats");
                Assert.That(r.Markers, Is.Empty);
            }
            Assert.That(() => AudioAnalysis.AutoWarp(new AudioSource(noise, noise, 44100), 4), Throws.Nothing);
            Assert.That(AudioAnalysis.AutoWarp(new AudioSource(new float[100], new float[100], 44100), 4).Found, Is.False, "a clip of a few milliseconds");
        }

        [Test]
        public void ABarOfThreeBeatsIsMarkedInThrees()
        {
            var line = WarpSignals.Make(WarpSignals.Tempo.Drift, WarpSignals.Style.Drums, 64, beatsPerBar: 3);
            var src = Source(line);
            var r = AudioAnalysis.AutoWarp(src, 3);
            Assert.That(r.Found, Is.True);
            Assert.That(r.Markers.Count(m => m.Beat % 3 == 0), Is.GreaterThanOrEqualTo(line.Downbeats.Length - 2), "a marker on every bar of three");
        }

        // ---- cost and caching ----------------------------------------------------------------

        [Test]
        public void TheBeatsAreFoundOncePerSourceAndATenMinuteFileTakesUnderThreeSeconds()
        {
            var one = WarpSignals.Make(WarpSignals.Tempo.Drift, WarpSignals.Style.Mix, 64);
            const int Reps = 10;
            var x = new float[one.Audio.Length * Reps];
            for (int i = 0; i < Reps; i++) Array.Copy(one.Audio, 0, x, i * one.Audio.Length, one.Audio.Length);
            var src = new AudioSource(x, x, one.Rate);
            Assert.That(src.Seconds, Is.GreaterThan(600));
            var sw = Stopwatch.StartNew();
            var r = AudioAnalysis.AutoWarp(src, 4);
            double ms = sw.Elapsed.TotalMilliseconds;
            Assert.That(r.Found, Is.True);
            Assert.That(ms, Is.LessThan(3000), "a 10-minute file, first call (the analysis itself)");
            var track = BeatTracker.Of(src);
            Assert.That(BeatTracker.Of(src), Is.SameAs(track), "cached with the audio");
            sw.Restart();
            var again = AudioAnalysis.AutoWarp(src, 4);
            Assert.That(sw.Elapsed.TotalMilliseconds, Is.LessThan(Math.Max(300, ms / 3)), "the second call reads the cache");
            Assert.That(again.Markers, Is.EqualTo(r.Markers), "and says the same");
            // Another bar length is another reading of the same beats.
            Assert.That(AudioAnalysis.AutoWarp(src, 3).Found, Is.True);
        }

        // ---- the tempo octave, as Tempo has it ------------------------------------------------

        [Test]
        public void TheTempoIsInTheOctaveOfEightyToOneSixtyAsTheImportHasIt()
        {
            // 60 BPM beats are read at 120 (double), 180 at 90 (half): the same rule as AudioAnalysis.Tempo.
            foreach (var (scale, want) in new[] { (0.5, 120.0), (1.5, 90.0) })
            {
                var line = WarpSignals.Make(WarpSignals.Tempo.Steady, WarpSignals.Style.Drums, 61.7, scale: scale);
                var r = AudioAnalysis.AutoWarp(Source(line), 4);
                Assert.That(r.Found, Is.True, $"{scale}");
                Assert.That(r.Bpm, Is.EqualTo(want).Within(want * 0.02), $"{scale}: {r.Bpm}");
            }
        }
    }
}
