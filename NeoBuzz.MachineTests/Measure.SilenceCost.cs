namespace NeoBuzz.MachineTests
{
    // The silence-cost guard, apart from Measure.cs: NeoBuzz.PedalTests compiles Measure.cs too, without Throughput.cs.
    internal static partial class Measure
    {
        /// <summary>
        /// What a second of silence after noise costs against a second of noise (the hygiene tests' "runs at full speed"),
        /// timed as the throughput guards are (Throughput.CostGuard, machines.md §4) and asserted under
        /// <paramref name="limit"/>: each render starts with <paramref name="noise"/> played untimed through a machine made
        /// anew, then times the same noise again (the baseline) or <paramref name="silenceSeconds"/> of silence (the test).
        /// <paramref name="make"/> makes the machine, set up, and returns its Process(left, right, count) on blocks of
        /// <paramref name="block"/> samples. A flush that stopped working (feedback decaying into subnormal numbers) makes
        /// silence cost many times what noise does on x64.
        /// </summary>
        public static Throughput.Cost SilenceCost(string what, double limit, int block, Func<Action<float[], float[], int>> make, float[] noise, int silenceSeconds = 20)
        {
            var silent = new float[silenceSeconds * Rate];
            Func<Func<int, Action<int>>> Start(float[] timed) => () =>
            {
                var process = make();
                var bl = new float[block];
                var br = new float[block];
                void Block(float[] x, int pos, int n)
                {
                    int have = Math.Clamp(x.Length - pos, 0, n);
                    Array.Copy(x, pos, bl, 0, have);
                    Array.Copy(x, pos, br, 0, have);
                    Array.Clear(bl, have, n - have);
                    Array.Clear(br, have, n - have);
                    process(bl, br, n);
                }
                for (int pos = 0; pos < noise.Length; pos += block) Block(noise, pos, Math.Min(block, noise.Length - pos));
                return Throughput.InBlocks(block, (pos, n) => Block(timed, pos, n));
            };
            return Throughput.CostGuard(what, limit, Rate, block, noise.Length / (double)Rate, Start(noise), silenceSeconds, Start(silent), Report);
        }
    }
}
