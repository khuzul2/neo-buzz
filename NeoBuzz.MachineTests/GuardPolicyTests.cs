// The guard policy (ReBuzzTests/Automation/GuardPolicy.cs) must forgive a runner's one-off noise and never a real regression:
// a block that allocates every time still fails an allocation check, a workload twice as slow still fails a throughput guard,
// while a one-off allocation and a one-off slow measurement pass on the retry.

using System.Runtime.CompilerServices;

namespace NeoBuzz.MachineTests
{
    public class GuardPolicyTests
    {
        static object sink;
        static double fsink;

        [Test]
        public void ABlockThatAllocatesEveryTimeStillFailsTheAllocationCheck()
        {
            var (bytes, why) = AllocationCheck.Run(200, b => sink = new byte[64 + b % 3]);
            Assert.That(bytes, Is.GreaterThan(0), "a real allocation per block reproduces in every attempt");
            Assert.That(why, Does.Contain("attempt 3"), "the failure lists every attempt");
        }

        [Test]
        public void AOneOffAllocationPassesOnTheRetry()
        {
            int calls = 0;
            // The first measured stretch allocates once (the runtime's own one-off), the next ones do not.
            var (bytes, _) = GuardPolicy.Allocation(() => calls++ == 0 ? (968, "one-off") : (0, ""));
            Assert.That(bytes, Is.EqualTo(0));
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(GuardPolicy.Allocation(() => (968, "always")).Bytes, Is.GreaterThan(0));
            int measured = 0;
            long a = GuardPolicy.Allocated(() => { if (measured++ == GuardPolicy.WarmPasses) sink = new byte[512]; });
            Assert.That(a, Is.EqualTo(0), "an allocation in the first measured attempt only");
            Assert.That(GuardPolicy.Allocated(() => sink = new byte[512]), Is.GreaterThan(0));
        }

        [Test]
        public void AFreeBlockPassesTheAllocationCheck()
        {
            var data = new float[256];
            var (bytes, why) = AllocationCheck.Run(300, b => { for (int i = 0; i < data.Length; i++) data[i] = i * 0.5f + b; });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void AOneOffSlowMeasurementPassesOnTheRetry()
        {
            var speeds = new Queue<double>(new[] { 40.0, 120.0 });
            var r = GuardPolicy.Retry(() => speeds.Dequeue(), s => s > 100, out var all);
            Assert.That(r, Is.EqualTo(120.0));
            Assert.That(all, Has.Count.EqualTo(2));
            // Slow every time: all attempts, a failing result.
            var slow = GuardPolicy.Retry(() => 40.0, s => s > 100, out var every);
            Assert.That(slow, Is.EqualTo(40.0));
            Assert.That(every, Has.Count.EqualTo(GuardPolicy.Attempts));
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Work(int samples, int times)
        {
            double a = fsink;
            for (int k = 0; k < times; k++)
                for (int i = 0; i < samples; i++) a = a * 0.999 + Math.Sin(i * 0.01 + a);
            fsink = a;
        }

        static Func<Func<int, Action<int>>> Workload(int times) => () => Throughput.InBlocks(64, (pos, count) => Work(count, times));

        [Test]
        public void AWorkloadTwiceAsSlowStillFailsTheThroughputGuard()
        {
            const int Rate = 48000;
            // The speed of the workload as it is, and a floor 25 % under it: the workload passes the guard, and the same
            // workload doing twice the work (a machine twice as slow) reads about half, under the floor in every attempt.
            var plain = Throughput.Measure("guard policy: the workload", 0.5, Rate, 64, Workload(40));
            double floor = plain.Speed * 0.75;
            Assert.DoesNotThrow(() => Throughput.Guard("guard policy: the workload", floor, 0.5, Rate, 64, Workload(40), line => { }),
                "the workload at its own speed passes its floor");
            var ex = Assert.Throws<AssertionException>(() => Throughput.Guard("guard policy: twice the work", floor, 0.5, Rate, 64, Workload(80), line => { }),
                "twice the work fails the same floor");
            Assert.That(ex!.Message, Does.Contain($"all {GuardPolicy.Attempts} attempts"), "in every attempt");
        }
    }
}
