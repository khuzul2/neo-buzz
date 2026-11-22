using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Clips;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// Warp marker editing (docs/specs/neo-audio.md, WM1), pure: the arithmetic every gesture, menu item and agent action goes through keeps the markers in order with the
    /// audio's two ends where they are, and a quantized drum loop plays on the grid at another tempo.
    /// </summary>
    [TestFixture]
    public class WarpMarkersTests
    {
        const int Rate = 44100;
        const double Length = 16 * 26460.0;      // 16 beats of audio at 100 BPM

        static List<(double Sample, double Beat)> Straight() => WarpMath.Effective(null, Length, Rate, 100);

        static void AssertSound(List<(double Sample, double Beat)> m, string why)
        {
            Assert.That(m.Count, Is.GreaterThanOrEqualTo(2), why);
            Assert.That(WarpMath.Validate(m), Is.Null, why + ": valid");
            for (int i = 1; i < m.Count; i++)
            {
                Assert.That(m[i].Beat, Is.GreaterThan(m[i - 1].Beat), why + ": the beats rise");
                Assert.That(m[i].Sample, Is.GreaterThan(m[i - 1].Sample), why + ": the samples rise");
            }
            Assert.That(m[0].Sample, Is.EqualTo(0).Within(1e-6), why + ": the first end stays");
            Assert.That(m[^1].Sample, Is.EqualTo(Length).Within(1e-6), why + ": the last end stays");
        }

        [Test]
        public void NoSequenceOfEditsLeavesAMarkerOutOfOrderOrMovesAnEnd()
        {
            var rnd = new Random(20260511);
            var transients = Enumerable.Range(0, 40).Select(i => 3000.0 + i * Length / 41 + rnd.Next(-900, 900)).ToList();
            var m = Straight();
            int ok = 0, refused = 0;
            for (int step = 0; step < 4000; step++)
            {
                WarpResult r;
                int n = m.Count;
                int Pick() => rnd.Next(n);
                int[] Some() => Enumerable.Range(0, 1 + rnd.Next(3)).Select(_ => Pick()).Distinct().ToArray();
                switch (rnd.Next(12))
                {
                    case 0: r = WarpMath.Add(m, rnd.NextDouble() * Length); break;
                    case 1: r = WarpMath.Move(m, Some(), (rnd.NextDouble() - 0.5) * 6); break;
                    case 2: r = WarpMath.MoveTo(m, Pick(), rnd.NextDouble() * 16 - 2); break;
                    case 3: r = WarpMath.MoveAlong(m, Pick(), rnd.NextDouble() * Length); break;
                    case 4: r = WarpMath.Delete(m, Some()); break;
                    case 5: r = WarpMath.SegmentTempoSet(m, Pick(), 40 + rnd.NextDouble() * 200, Rate); break;
                    case 6: r = WarpMath.SegmentTempoScale(m, Pick(), rnd.Next(2) == 0 ? 0.5 : 2, Rate); break;
                    case 7: r = WarpMath.InsertAtTransients(m, transients, rnd.Next(2) == 0 ? null : rnd.NextDouble() * Length / 2, rnd.Next(2) == 0 ? null : Length / 2 + rnd.NextDouble() * Length / 2); break;
                    case 8: r = WarpMath.InsertEveryBar(m, 4, null, null); break;
                    case 9: r = WarpMath.Quantize(m, Some(), null, null, transients, new[] { 0.25, 0.5, 1 }[rnd.Next(3)], rnd.NextDouble()); break;
                    case 10: r = WarpMath.AsLoop(m, rnd.NextDouble() * Length / 2, Length / 2 + rnd.NextDouble() * Length / 2, null, 4); break;
                    default: r = m.Count > 12 ? WarpMath.Delete(m, Enumerable.Range(1, m.Count - 2).ToArray()) : WarpMath.Add(m, rnd.NextDouble() * Length); break;
                }
                if (!r.Ok) { refused++; continue; }
                ok++;
                AssertSound(r.Markers, $"step {step}");
                m = r.Markers;
            }
            Assert.That(ok, Is.GreaterThan(1000), "most edits were made");
            Assert.That(refused, Is.GreaterThan(0), "and some were refused");
        }

        [Test]
        public void AMarkerStopsOneTickBeforeItsNeighbourAndTheEndsCantBeMovedAlong()
        {
            var m = Straight();
            m = WarpMath.Add(m, Length * 0.25).Markers;
            m = WarpMath.Add(m, Length * 0.5).Markers;
            var r = WarpMath.Move(m, new[] { 1 }, 100);
            Assert.That(r.Ok);
            Assert.That(r.Markers[1].Beat, Is.EqualTo(m[2].Beat - WarpMath.MinGap).Within(1e-9), "it stops one tick before the next marker");
            Assert.That(WarpMath.MoveAlong(m, 0, 1000).Ok, Is.False, "the first end can't move along the audio");
            Assert.That(WarpMath.MoveAlong(m, m.Count - 1, 1000).Ok, Is.False, "nor the last");
            Assert.That(WarpMath.Delete(m, new[] { 0, m.Count - 1 }).Ok, Is.False, "the ends can't be deleted");
        }

        [Test]
        public void SettingASegmentsTempoStretchesOnlyThatSegment()
        {
            var m = Straight();
            m = WarpMath.Add(m, Length * 0.25).Markers;
            m = WarpMath.Add(m, Length * 0.5).Markers;
            var before = Enumerable.Range(0, m.Count - 1).Select(i => WarpMath.SegmentTempo(m, i, Rate)).ToList();
            var r = WarpMath.SegmentTempoSet(m, 1, 80, Rate);
            Assert.That(r.Ok);
            for (int i = 0; i < before.Count; i++)
                Assert.That(WarpMath.SegmentTempo(r.Markers, i, Rate), Is.EqualTo(i == 1 ? 80 : before[i]).Within(1e-6), $"segment {i}");
            Assert.That(r.Markers.Select(x => x.Sample), Is.EqualTo(m.Select(x => x.Sample)), "the audio is where it was");
        }

        // ---------------------------------------------------------------------------------------------

        /// <summary>Sixteen clicks that are roughly a beat apart at 100 BPM, each a little early or late (a drummer's loop).</summary>
        static (AudioSource Audio, List<double> Starts) Drums()
        {
            var rnd = new Random(5);
            var x = new float[(int)Length];
            var starts = new List<double>();
            for (int k = 0; k < 16; k++)
            {
                int at = k == 0 ? 0 : (int)(k * 26460 + rnd.Next(-3000, 3001));      // the first hit is the audio's start, the pinned end
                starts.Add(at);
                for (int i = 0; i < Rate / 100 && at + i < x.Length; i++)
                    x[at + i] = (float)(0.8 * Math.Sin(2 * Math.PI * 1000 * i / Rate) * Math.Exp(-i / (0.002 * Rate)));
            }
            return (new AudioSource(x, x, Rate), starts);
        }

        static List<int> Heard(float[] x)
        {
            var list = new List<int>();
            int quiet = 3000;
            for (int i = 0; i < x.Length; i++)
            {
                if (Math.Abs(x[i]) > 0.3f && quiet > 2000) { list.Add(i); quiet = 0; }
                else if (Math.Abs(x[i]) <= 0.3f) quiet++;
            }
            return list;
        }

        static float[] Play(AudioClipParams p, double bpm, double beats)
        {
            double bps = bpm / 60.0 / Rate;
            int samples = (int)(beats / bps);
            var clip = new ClipPlayback { Params = p };
            var mixer = new ClipMixer();
            var output = new float[samples];
            for (int at = 0; at < samples; at += 256)
            {
                int n = Math.Min(256, samples - at);
                mixer.Begin(n);
                double beat = at * bps;
                mixer.Play(clip, beat, bps, Rate, n, beat, beats - beat);
                mixer.End(n, bps, Rate);
                Array.Copy(mixer.Left, 0, output, at, n);
            }
            return output;
        }

        [Test]
        public void AQuantizedDrumLoopPlaysItsTransientsOnTheGridWithin1msAtAnotherTempo()
        {
            var (audio, starts) = Drums();
            var m = Straight();
            foreach (double s in starts) { var r = WarpMath.Add(m, s); if (r.Ok) m = r.Markers; }
            Assert.That(m.Count, Is.GreaterThanOrEqualTo(16), "a marker on the clicks");
            var all = Enumerable.Range(1, m.Count - 2).ToArray();
            var q = WarpMath.Quantize(m, all, null, null, starts, 1.0, 1.0);
            Assert.That(q.Ok, q.Error);
            AssertSound(q.Markers, "quantized");
            foreach (var mk in q.Markers.Skip(1).Take(q.Markers.Count - 2))
                Assert.That(mk.Beat, Is.EqualTo(Math.Round(mk.Beat)).Within(1e-9), "each inner marker is on a line of the grid");

            const double playBpm = 130;
            double beats = q.Markers[^1].Beat;
            var map = new WarpMap(q.Markers.Select(x => new WarpMarker(x.Sample, x.Beat)));
            var output = Play(new AudioClipParams(audio, map, WarpMode.Repitch, null, 1f, 0), playBpm, beats - 0.5);
            var heard = Heard(output);
            double bps = playBpm / 60.0 / Rate;
            int checkedClicks = 0;
            foreach (var mk in q.Markers.Skip(1).Take(q.Markers.Count - 2))
            {
                double expected = mk.Beat / bps;                              // where the grid line is, in the output
                double nearest = heard.Select(h => Math.Abs(h - expected)).Min();
                Assert.That(nearest, Is.LessThan(Rate * 0.001), $"the click on beat {mk.Beat} is within 1 ms of its grid line");
                checkedClicks++;
            }
            Assert.That(checkedClicks, Is.GreaterThanOrEqualTo(13));

            // Not quantized: the same loop plays off the grid by far more than a millisecond (so the test can tell).
            var rawMap = WarpMap.Straight(Length, 16);
            var raw = Heard(Play(new AudioClipParams(audio, rawMap, WarpMode.Repitch, null, 1f, 0), playBpm, 15.5));
            double worst = starts.Skip(1).Take(14).Select((s, k) => raw.Select(h => Math.Abs(h - Math.Round(s / 26460.0) / bps)).Min()).Max();
            Assert.That(worst, Is.GreaterThan(Rate * 0.001), "unwarped, the jittered clicks are off the grid");
        }
    }
}
