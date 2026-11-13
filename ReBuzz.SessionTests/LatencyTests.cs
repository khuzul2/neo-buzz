using Buzz.MachineInterface;
using ReBuzz.Core;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// The machine delay compensation's pure parts (docs/specs/mastering-tools.md §4.1): the delay ring that changes without a
    /// gap or a click (LatencyRing), and the plan that gives each input its delay (LatencyPlan), against the recursive walk
    /// ReBuzz used before.
    /// </summary>
    [TestFixture]
    public class LatencyTests
    {
        const int N = 256;
        const int Fade = 480;   // 10 ms at 48 kHz

        static float Sine(long t, double hz = 1000) => (float)(0.5 * Math.Sin(2 * Math.PI * hz * t / 48000));

        /// <summary>A connection's use of the ring for one block: clear, add the block, read the tap out (or pass it directly).</summary>
        static void Block(LatencyRing ring, float[] input, long at, Sample[] output, int n = N)
        {
            if (!ring.Live)
            {
                for (int i = 0; i < n; i++) output[i] = new Sample(input[at + i], -input[at + i]);
                ring.Passed();
                return;
            }
            ring.Clear(n);
            var r = ring.Ring;
            int w = ring.Position;
            for (int i = 0; i < n; i++)
            {
                r[w].L += input[at + i];
                r[w].R += -input[at + i];
                w = (w + 1) & ring.Mask;
            }
            ring.Read(output, n);
        }

        static float[] Run(LatencyRing ring, float[] input, Action<int, LatencyRing>? atBlock = null)
        {
            var output = new float[input.Length];
            var block = new Sample[N];
            for (int b = 0; b * N < input.Length; b++)
            {
                atBlock?.Invoke(b, ring);
                Block(ring, input, b * N, block);
                for (int i = 0; i < N; i++)
                {
                    output[b * N + i] = block[i].L;
                    Assert.That(block[i].R, Is.EqualTo(-block[i].L), "both channels through the same tap");
                }
            }
            return output;
        }

        static float[] Signal(int length, Func<long, float> f)
        {
            var x = new float[length];
            for (int i = 0; i < length; i++) x[i] = f(i);
            return x;
        }

        [Test]
        public void BeforeAnythingPassesADelayIsExactAtOnce()
        {
            var ring = new LatencyRing();
            ring.EnsureCapacity(100);
            ring.Request(100, Fade);
            Assert.That(ring.Settled, Is.EqualTo(100));
            var x = Signal(N * 8, t => Sine(t));
            var y = Run(ring, x);
            for (int i = 0; i < y.Length; i++)
                Assert.That(y[i], Is.EqualTo(i < 100 ? 0f : x[i - 100]), $"sample {i}");
        }

        [Test]
        public void ARunningDelayCrossfadesToTheNewTapWithoutAGap()
        {
            var ring = new LatencyRing();
            ring.EnsureCapacity(5000);
            var x = Signal(N * 120, t => Sine(t));
            int askedAt = 10;
            var y = Run(ring, x, (b, r) => { if (b == askedAt) r.Request(4607, Fade); });
            // Direct until the ring holds the new delay's worth, then the curve, then the new tap exactly.
            int armed = askedAt * N;
            while (armed - askedAt * N < 4607 + N) armed += N;          // the first block whose Read has the history
            int fadeStart = armed - N;
            float step = 1f / Fade;
            double largestStep = 0, ownStep = 0;
            for (int i = 1; i < y.Length; i++)
            {
                float expected;
                if (i < fadeStart) expected = x[i];
                else if (i < fadeStart + Fade)
                {
                    float a = x[i], b = x[i - 4607];
                    float p = (i - fadeStart + 1) * step, g = p * p * (3f - 2f * p);
                    expected = a + (b - a) * g;
                }
                else expected = x[i - 4607];
                Assert.That(y[i], Is.EqualTo(expected), $"sample {i}");
                largestStep = Math.Max(largestStep, Math.Abs(y[i] - y[i - 1]));
                ownStep = Math.Max(ownStep, Math.Abs(x[i] - x[i - 1]));
            }
            TestContext.Out.WriteLine($"0 → 4607 samples while a 1 kHz sine plays: largest step {largestStep / ownStep:F3}× the sine's own");
            Assert.That(largestStep, Is.LessThan(1.2 * ownStep), "no click");
            Assert.That(ring.Settled, Is.EqualTo(4607));
        }

        [Test]
        public void ShorterAndBackToDirect()
        {
            var ring = new LatencyRing();
            ring.EnsureCapacity(2000);
            ring.Request(2000, Fade);
            var x = Signal(N * 80, t => Sine(t, 220));
            var y = Run(ring, x, (b, r) =>
            {
                if (b == 20) r.Request(300, Fade);        // shorter: the history is there, so at once
                if (b == 50) r.Request(0, Fade);          // and back to the direct path
            });
            for (int i = 20 * N; i < 20 * N + Fade; i++)
            {
                float a = x[i - 2000], b = x[i - 300];
                float p = (i - 20 * N + 1) * (1f / Fade), g = p * p * (3f - 2f * p);
                Assert.That(y[i], Is.EqualTo(a + (b - a) * g), $"sample {i}");
            }
            for (int i = 20 * N + Fade; i < 50 * N; i++) Assert.That(y[i], Is.EqualTo(x[i - 300]));
            for (int i = 50 * N + Fade; i < y.Length; i++) Assert.That(y[i], Is.EqualTo(x[i]));
            Assert.That(ring.Live, Is.False, "the direct path again");
        }

        [Test]
        public void SilenceInFlightSwitchesAtOnce()
        {
            var ring = new LatencyRing();
            ring.EnsureCapacity(4000);
            ring.Request(1000, Fade);
            // Sound for 20 blocks, then silence; later sound again.
            var x = Signal(N * 60, t => t < 20 * N || t >= 40 * N ? Sine(t) : 0f);
            var y = Run(ring, x, (b, r) => { if (b == 36) r.Request(3000, Fade); });
            Assert.That(ring.Settled, Is.EqualTo(3000), "nothing in flight: the new delay at once");
            for (int i = 36 * N; i < y.Length; i++) Assert.That(y[i], Is.EqualTo(i >= 3000 ? x[i - 3000] : 0f), $"sample {i}");
        }

        [Test]
        public void GrowingKeepsWhatIsInFlight()
        {
            var small = new LatencyRing();
            small.EnsureCapacity(700);
            small.Request(700, Fade);
            var big = new LatencyRing();
            big.EnsureCapacity(60000);
            big.Request(700, Fade);
            var x = Signal(N * 50, t => Sine(t, 333));
            var ys = Run(small, x, (b, r) => { if (b == 25) r.EnsureCapacity(40000); });
            var yb = Run(big, x);
            Assert.That(ys, Is.EqualTo(yb));
            Assert.That(small.Capacity, Is.GreaterThanOrEqualTo(40000));
        }

        [Test]
        public void ABeyondCapacityDelayIsClampedUntilTheRingGrows()
        {
            var ring = new LatencyRing();
            ring.EnsureCapacity(1000);
            int capacity = ring.Capacity;
            ring.Request(capacity + 5000, Fade);
            Assert.That(ring.Delay, Is.EqualTo(capacity));
            Assert.That(ring.Wanted, Is.EqualTo(capacity + 5000));
            ring.EnsureCapacity(ring.Wanted);
            Assert.That(ring.Wanted, Is.EqualTo(0));
            ring.Request(capacity + 5000, Fade);
            Assert.That(ring.Delay, Is.EqualTo(capacity + 5000));
        }

        [Test]
        public void TheAudioThreadsCallsAllocateNothing()
        {
            var ring = new LatencyRing();
            ring.EnsureCapacity(5000);
            var x = Signal(N * 400, t => Sine(t));
            var block = new Sample[N];
            for (int b = 0; b < 10; b++) Block(ring, x, b * N, block);
            long bytes = GuardPolicy.Allocated(() =>
            {
                for (int b = 10; b < 400; b++)
                {
                    if (b % 37 == 0) ring.Request(b % 2 == 0 ? 4000 : 120, Fade);
                    Block(ring, x, b * N, block);
                }
            }, warm: false);   // GuardPolicy: 0 unless every attempt allocates
            Assert.That(bytes, Is.EqualTo(0));
        }

        // ── the plan ──────────────────────────────────────────────────────────

        /// <summary>ReBuzz's walk before the plan (ReBuzzCore.HandleLatencyCalcRecursive): the delay of each input, by connection.</summary>
        static int Recursive(int node, int[] latency, List<(int Connection, int Source)>[] inputs, Dictionary<int, int> delays)
        {
            int max = 0;
            var arrivals = new Dictionary<int, int>();
            foreach (var (_, source) in inputs[node])
            {
                int a = Recursive(source, latency, inputs, delays);
                max = Math.Max(a, max);
                arrivals[source] = a;
            }
            foreach (var (connection, source) in inputs[node]) delays[connection] = Math.Max(0, max - arrivals[source]);
            return max + latency[node];
        }

        [Test]
        public void ADiamondGetsTheDelaysThatLineItUp()
        {
            // 0 = Master; 1 = a source; 2 (latency 10) and 3 (latency 3) after it; the source also straight to Master.
            var inputs = new List<(int, int)>[4];
            inputs[0] = new() { (0, 2), (1, 3), (2, 1) };
            inputs[1] = new();
            inputs[2] = new() { (3, 1) };
            inputs[3] = new() { (4, 1) };
            var plan = LatencyPlan.Build(0, n => inputs[n]);
            Assert.That(plan.Nodes[^1], Is.EqualTo(0), "Master last");
            var own = plan.Nodes.Select(n => n == 2 ? 10 : n == 3 ? 3 : 0).ToArray();
            var delays = new int[plan.Connections.Length];
            plan.Apply(own, delays);
            var byConnection = plan.Connections.Zip(delays).ToDictionary(p => p.First, p => p.Second);
            Assert.That(byConnection[0], Is.EqualTo(0));
            Assert.That(byConnection[1], Is.EqualTo(7));
            Assert.That(byConnection[2], Is.EqualTo(10));
            Assert.That(byConnection[3], Is.EqualTo(0));
            Assert.That(byConnection[4], Is.EqualTo(0));
            Assert.That(plan.Total, Is.EqualTo(10));
        }

        [Test]
        public void RandomGraphsGetTheRecursiveWalksDelays()
        {
            var random = new Random(1234);
            for (int round = 0; round < 300; round++)
            {
                int n = 2 + random.Next(14);
                // A DAG: node i takes inputs only from nodes above it (0 is Master, the sink).
                var inputs = new List<(int, int)>[n];
                int connection = 0;
                for (int i = 0; i < n; i++)
                {
                    inputs[i] = new();
                    for (int j = i + 1; j < n; j++)
                        if (random.NextDouble() < 0.35) inputs[i].Add((connection++, j));
                }
                var latency = Enumerable.Range(0, n).Select(_ => random.Next(4) == 0 ? random.Next(5000) : 0).ToArray();
                latency[0] = 0;
                var expected = new Dictionary<int, int>();
                int total = Recursive(0, latency, inputs, expected);
                var plan = LatencyPlan.Build(0, k => inputs[k]);
                var delays = new int[plan.Connections.Length];
                plan.Apply(plan.Nodes.Select(k => latency[k]).ToArray(), delays);
                var got = plan.Connections.Zip(delays).ToDictionary(p => p.First, p => p.Second);
                Assert.That(got, Is.EquivalentTo(expected), $"round {round}");
                Assert.That(plan.Total, Is.EqualTo(total), $"round {round}");
            }
        }

        [Test]
        public void ApplyingThePlanAllocatesNothingAndACycleDoesntHang()
        {
            var inputs = new List<(int, int)>[3];
            inputs[0] = new() { (0, 1) };
            inputs[1] = new() { (1, 2) };
            inputs[2] = new() { (2, 1) };     // a cycle between 1 and 2 (not possible in a Buzz graph)
            var plan = LatencyPlan.Build(0, k => inputs[k]);
            var own = new int[plan.Count];
            var delays = new int[plan.Connections.Length];
            plan.Apply(own, delays);
            long bytes = GuardPolicy.Allocated(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    own[0] = i % 7;
                    plan.Apply(own, delays);
                }
            }, warm: false);   // GuardPolicy: 0 unless every attempt allocates
            Assert.That(bytes, Is.EqualTo(0));
        }
    }
}
