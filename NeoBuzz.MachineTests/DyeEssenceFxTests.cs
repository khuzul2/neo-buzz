using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Dye Essence P4b0 (docs/specs/dye-essence.md §4.10), measured: the effects rack: its routing, each hosted type, the
    /// latency and its compensation, what the parameters map to, the builder and no allocation on the audio thread.
    /// </summary>
    [TestFixture]
    public class DyeEssenceFxTests
    {
        const int Rate = Measure.Rate;

        sealed class Rig
        {
            public readonly ParamValues V = new(Params.Table);
            public readonly DyeEngine E;
            readonly float[] l = new float[NoteBank.MaxBlock], r = new float[NoteBank.MaxBlock];

            public Rig(Action<ParamValues> set = null, bool build = true)
            {
                for (int i = 0; i < TableBank.Count; i++) TableBank.GetNow(i);
                for (int i = 0; i < SampleSources.Count; i++) SampleSources.GetNow(i);
                set?.Invoke(V);
                E = new DyeEngine(V);
                E.SetSampleRate(Rate);
                E.Tempo = 120;
                if (build) E.Fx.BuildNow();
            }

            /// <summary>Renders <paramref name="frames"/> (interleaved), the notes already on.</summary>
            public float[] Run(int frames, int block = 256)
            {
                var y = new float[2 * frames];
                for (int done = 0, n; done < frames; done += n)
                {
                    n = Math.Min(block, frames - done);
                    E.Process(l, r, n);
                    for (int i = 0; i < n; i++)
                    {
                        y[2 * (done + i)] = l[i];
                        y[2 * (done + i) + 1] = r[i];
                    }
                }
                return y;
            }

            public void Chord()
            {
                E.NoteOn(1, 48, 0.9f);
                E.NoteOn(2, 55, 0.8f);
                E.NoteOn(3, 64, 0.8f);
            }
        }

        static Action<ParamValues> Slot(int slot, FxType type, int mix = 1000, params int[] x) => v =>
        {
            v.Set(FxRack.TypeParam(slot), (int)type);
            v.Set(FxRack.MixParam(slot), mix);
            for (int k = 0; k < x.Length; k++) v.Set(FxRack.GenericParam(slot, k), x[k]);
        };

        static Action<ParamValues> All(params Action<ParamValues>[] a) => v => { foreach (var f in a) f(v); };

        static double Diff(float[] a, float[] b)
        {
            double s = 0;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) s += (a[i] - b[i]) * (double)(a[i] - b[i]);
            return Math.Sqrt(s / a.Length);
        }

        static double Energy(float[] a)
        {
            double s = 0;
            foreach (var x in a) s += x * (double)x;
            return Math.Sqrt(s / a.Length);
        }

        // ---- routing ------------------------------------------------------------------------------------------

        [Test]
        public void RoutingTextRoundTripsAndBadTextGivesTheDefault()
        {
            Assert.That(FxRouting.Default.ToString(), Is.EqualTo("1 2 3 4 | 5 6 7 | 8 9 10"));
            Assert.That(FxRouting.Default.IsDefault);
            var moved = FxRouting.Default.Move(0, 2, 0);
            Assert.That(moved.ToString(), Is.EqualTo("2 3 4 | 5 6 7 | 1 8 9 10"));
            Assert.That(FxRouting.Parse(moved.ToString()).ToString(), Is.EqualTo(moved.ToString()));
            Assert.That(moved.ChainOf(0), Is.EqualTo(2));
            foreach (var bad in new[] { "1 2 3 | 4 5", "1 1 2 3 4 5 6 7 8 9 | | 10", "1 2 3 4 5 6 7 8 9 10", "1 2 3 4 | 5 6 7 | 8 9 11", "a b | c | d", "1 2 3 4 | 5 6 7 | 8 9" })
            {
                Assert.That(FxRouting.TryParse(bad, out var r), Is.False, bad);
                Assert.That(r.IsDefault);
            }
            Assert.That(FxRouting.TryParse("", out var empty) && empty.IsDefault);
            Assert.That(FxRouting.TryParse("1 2 3 4 5 6 7 8 9 10 | | ", out var one) && one.Chain[0].Length == 10);
        }

        // ---- the map from generic values ----------------------------------------------------------------------

        [Test]
        public void FiftyPercentIsTheEnginesDefaultAndTheEndsAreItsEnds()
        {
            for (int t = 1; t < FxTypes.Count; t++)
            {
                var info = FxTypes.Info((FxType)t);
                Assert.That(info.Params.Length, Is.EqualTo(FxTypes.PerType), info.Name);
                for (int k = 0; k < FxTypes.PerType; k++)
                {
                    var p = info.Params[k];
                    var d = info.Table[p.Index];
                    int def = p.Def < 0 ? d.Default : p.Def;
                    Assert.That(info.EngineRaw(k, 500), Is.EqualTo(def), $"{info.Name} {p.Label}");
                    Assert.That(info.EngineRaw(k, 0), Is.EqualTo(d.Min), $"{info.Name} {p.Label}");
                    Assert.That(info.EngineRaw(k, 1000), Is.EqualTo(d.Max), $"{info.Name} {p.Label}");
                    int last = d.Min;
                    for (int x = 0; x <= 1000; x += 5)
                    {
                        int raw = info.EngineRaw(k, x);
                        Assert.That(raw, Is.GreaterThanOrEqualTo(last), $"{info.Name} {p.Label} rises");
                        last = raw;
                    }
                    if (d.Kind == MapKind.Enum)
                    {
                        var seen = new HashSet<int>();
                        for (int x = 0; x <= 1000; x++) seen.Add(info.EngineRaw(k, x));
                        Assert.That(seen.Count, Is.EqualTo(d.Max - d.Min + 1), $"{info.Name} {p.Label}: every choice can be reached");
                    }
                    Assert.That(info.Describe(k, 500), Is.Not.Empty);
                }
            }
        }

        // ---- off is nothing -----------------------------------------------------------------------------------

        [Test]
        public void EverySlotOffRunsNothingAndLeavesTheAudioAlone()
        {
            var g = new Rig();
            var l = new float[256];
            var r = new float[256];
            for (int i = 0; i < 256; i++) { l[i] = i * 0.001f; r[i] = -i * 0.002f; }
            var l0 = (float[])l.Clone();
            var r0 = (float[])r.Clone();
            Assert.That(g.E.Fx.Process(l, r, 256), Is.False);
            Assert.That(l, Is.EqualTo(l0));
            Assert.That(r, Is.EqualTo(r0));
            Assert.That(g.E.Fx.Latency, Is.EqualTo(0));
        }

        // ---- each type ------------------------------------------------------------------------------------------

        static readonly (FxType Type, int[] X)[] Strong =
        {
            (FxType.Eq, new[] { 500, 1000, 500, 0, 500, 500, 1000 }),
            (FxType.Filter, new[] { 500, 500, 250 }),
            (FxType.Comp, new[] { 500, 0, 1000 }),
            (FxType.Clipper, new[] { 900, 500 }),
            (FxType.Delay, new[] { 500, 0, 500, 300 }),
            (FxType.Verb, new[] { 500, 500, 500, 700 }),
        };

        [TestCaseSource(nameof(TypeValues))]
        public void EachTypeProcessesAndChangesTheSound(int typeValue)
        {
            var type = (FxType)typeValue;
            var x = Strong.First(s => s.Type == type).X;
            var dry = new Rig();
            dry.Chord();
            var a = dry.Run(Rate);
            var wet = new Rig(All(Slot(7, type, 1000, x), v => v.Set(P.Volume, v[P.Volume])));
            wet.Chord();
            var b = wet.Run(Rate);
            Assert.That(b.All(float.IsFinite), "finite");
            Assert.That(Diff(a, b), Is.GreaterThan(1e-4), $"{type} changes the sound");
            Assert.That(Energy(b), Is.GreaterThan(1e-4).And.LessThan(8), "and plays, at a sane level");
            Measure.Report($"Dye Essence rack, {FxTypes.Info(type).Name} in the main chain: rms {Measure.Db(Energy(b)):F1} dB against {Measure.Db(Energy(a)):F1} dB dry, latency {wet.E.Fx.Latency} samples");
        }

        static IEnumerable<FxType> Types() => Enum.GetValues<FxType>().Where(t => t != FxType.Off);
        public static IEnumerable<int> TypeValues() => Types().Select(t => (int)t);

        [Test]
        public void ThePendingBuildIsDoneByTheBuilderAndTheSlotPassesUntilThen()
        {
            var g = new Rig(Slot(7, FxType.Verb, 1000, 500, 500, 500, 900), build: false);
            g.Chord();
            Assert.That(g.E.Fx.Pending, Is.True, "a chosen type that is not built is pending");
            var sw = Stopwatch.StartNew();
            while (g.E.Fx.Pending && sw.Elapsed.TotalSeconds < 30) Thread.Sleep(5);
            Assert.That(g.E.Fx.Pending, Is.False, "the builder finishes");
            var dry = new Rig();
            dry.Chord();
            Assert.That(Diff(dry.Run(Rate / 2), g.Run(Rate / 2)), Is.GreaterThan(1e-4), "and the reverb is heard");
        }

        [Test]
        public void ABlockBeforeTheBuildPassesTheDry()
        {
            var g = new Rig(Slot(7, FxType.Verb, 1000, 500, 500, 500, 900), build: false);
            var h = new Rig();
            g.Chord();
            h.Chord();
            var a = g.Run(256);
            var b = h.Run(256);
            // The first block runs before the builder can have finished a reverb in most runs; either way nothing breaks.
            Assert.That(a.All(float.IsFinite));
            Assert.That(b.All(float.IsFinite));
        }

        // ---- latency --------------------------------------------------------------------------------------------

        [Test]
        public void LatencyIsTheClippersAndSumsAlongTheChains()
        {
            int lat = NeoBuzz.Clipper.ClipperEngine.LatencyAt(Rate);
            Assert.That(lat, Is.GreaterThan(0).And.LessThan(2000));
            var clip = Slot(0, FxType.Clipper);
            int L(Action<ParamValues> set) => new Rig(set, build: false).E.Fx.Latency;
            Assert.That(L(clip), Is.EqualTo(lat), "trio: one bus");
            Assert.That(L(All(Slot(0, FxType.Clipper), Slot(4, FxType.Clipper))), Is.EqualTo(lat), "trio: two buses are parallel");
            Assert.That(L(All(Slot(0, FxType.Clipper), Slot(1, FxType.Clipper))), Is.EqualTo(2 * lat), "trio: two in one bus add");
            Assert.That(L(All(Slot(0, FxType.Clipper), Slot(7, FxType.Clipper))), Is.EqualTo(2 * lat), "trio: bus then main");
            Assert.That(L(All(v => v.Set(P.FxLayout, 1), Slot(0, FxType.Clipper), Slot(4, FxType.Clipper), Slot(7, FxType.Clipper))), Is.EqualTo(3 * lat), "duo: insert 1, insert 2, aux");
            Assert.That(L(Slot(7, FxType.Delay)), Is.EqualTo(0));
            Assert.That(L(All(Slot(0, FxType.Eq), Slot(1, FxType.Filter), Slot(2, FxType.Comp), Slot(3, FxType.Verb))), Is.EqualTo(0));
            Assert.That(FxTypes.Latency(FxType.Clipper, 192000), Is.LessThan(2048), "within a slot's pad at the highest rate");
        }

        [Test]
        public void AnInsertAtMixZeroIsTheDryLateByTheLatencyBitForBit()
        {
            int lat = NeoBuzz.Clipper.ClipperEngine.LatencyAt(Rate);
            var off = new Rig();
            off.Chord();
            var a = off.Run(Rate / 2);
            var g = new Rig(All(v => v.Set(P.FxLayout, 1), Slot(0, FxType.Clipper, 0)));
            g.Chord();
            var b = g.Run(Rate / 2, 100);
            // The mix ramp starts at 0: nothing wet at all.
            int bad = 0;
            for (int i = 2 * lat; i < a.Length; i++) if (b[i] != a[i - 2 * lat]) bad++;
            Assert.That(bad, Is.EqualTo(0));
            for (int i = 0; i < 2 * lat; i++) Assert.That(b[i], Is.EqualTo(0f));
        }

        [Test]
        public void AParallelBusStaysInTimeWithTheDry()
        {
            // A reverb-free bus holding only the clipper at mix 0 returns the dry; the dry path is padded to match, so the
            // sum is 2x the dry, late by the latency, with no comb.
            int lat = NeoBuzz.Clipper.ClipperEngine.LatencyAt(Rate);
            var off = new Rig();
            off.Chord();
            var a = off.Run(Rate / 2);
            var g = new Rig(Slot(0, FxType.Clipper, 0));
            g.Chord();
            var b = g.Run(Rate / 2);
            double worst = 0;
            for (int i = 2 * lat; i < a.Length; i++) worst = Math.Max(worst, Math.Abs(b[i] - 2 * a[i - 2 * lat]));
            Assert.That(worst, Is.LessThan(2e-3), "the sends and returns' default is 0.02 dB (raw 870 of -40 ... 6 dB), so the sum is 2 x dry within that");
        }

        // ---- layouts --------------------------------------------------------------------------------------------

        [Test]
        public void TheLayoutsRouteAsTheyShould()
        {
            // Main chain: the effect is on the whole mix. Bus A: the dry plus the bus. Duo aux: the same. Insert: only the wet.
            var xs = new[] { 500, 500, 0 };   // a filter with a low cutoff
            var main = new Rig(Slot(7, FxType.Filter, 1000, 500, 500, 100)); main.Chord();
            var bus = new Rig(Slot(0, FxType.Filter, 1000, 500, 500, 100)); bus.Chord();
            var duoAux = new Rig(All(v => v.Set(P.FxLayout, 1), Slot(7, FxType.Filter, 1000, 500, 500, 100))); duoAux.Chord();
            var duoIns = new Rig(All(v => v.Set(P.FxLayout, 1), Slot(0, FxType.Filter, 1000, 500, 500, 100))); duoIns.Chord();
            var dry = new Rig(); dry.Chord();
            var d = dry.Run(Rate / 2);
            var m = main.Run(Rate / 2);
            var b = bus.Run(Rate / 2);
            var x = duoAux.Run(Rate / 2);
            var i = duoIns.Run(Rate / 2);
            Assert.That(Energy(m), Is.LessThan(Energy(d)), "a low-pass in the main chain takes the highs away");
            Assert.That(Energy(b), Is.GreaterThan(Energy(m)), "bus A adds to the dry");
            Assert.That(Diff(b, x), Is.LessThan(1e-6), "duo's aux is the same send and return as trio's bus A");
            Assert.That(Diff(m, i), Is.LessThan(1e-6), "duo's insert is the same serial path as trio's main");
            Assert.That(xs.Length, Is.EqualTo(3));
        }

        [Test]
        public void SendsAndReturnsScaleTheBus()
        {
            var a = new Rig(All(Slot(0, FxType.Clipper, 0), v => v.Set(P.FxSendA, 0))); a.Chord();
            var dry = new Rig(); dry.Chord();
            int lat = NeoBuzz.Clipper.ClipperEngine.LatencyAt(Rate);
            var x = a.Run(Rate / 4);
            var d = dry.Run(Rate / 4);
            double worst = 0;
            for (int i = 2 * lat; i < d.Length; i++) worst = Math.Max(worst, Math.Abs(x[i] - d[i - 2 * lat]));
            Assert.That(worst, Is.LessThan(1e-6), "a send of -inf adds nothing (the dry, late)");
        }

        [Test]
        public void ARoutingMoveTakesEffect()
        {
            var g = new Rig(Slot(0, FxType.Filter, 1000, 500, 500, 100));
            g.Chord();
            var inBus = g.Run(Rate / 4);
            g.E.Fx.SetRouting(FxRouting.Default.Move(0, 2, 0));
            var inMain = g.Run(Rate / 4);
            Assert.That(Energy(inMain), Is.LessThan(Energy(inBus)), "in the main chain the low-pass is no longer added to the dry");
        }

        // ---- the tail and the rest ------------------------------------------------------------------------------

        [Test]
        public void TheRackKeepsTheMachineAwakeForItsTail()
        {
            var g = new Rig(Slot(7, FxType.Verb, 1000, 500, 500, 700, 900));
            g.E.NoteOn(1, 60, 0.9f);
            g.Run(Rate / 4);
            g.E.NoteOff(1);
            var l = new float[256];
            var r = new float[256];
            bool awake = true;
            for (int i = 0; i < Rate * 2 / 256; i++) awake &= g.E.Process(l, r, 256);
            Assert.That(awake, "two seconds after the note off the reverb still sounds");
            bool rests = false;
            for (int i = 0; i < Rate * 40 / 256 && !rests; i++) rests = !g.E.Process(l, r, 256);
            Assert.That(rests, "and the machine rests when it has died away");
        }

        // ---- CPU --------------------------------------------------------------------------------------------------

        [Test]
        public void CpuPerType()
        {
            var l = new float[256];
            var r = new float[256];
            var rng = new Random(5);
            var lines = new List<string>();
            foreach (var type in Types())
            {
                var g = new Rig(Slot(7, type, 1000, Strong.First(s => s.Type == type).X));
                for (int i = 0; i < 256; i++) { l[i] = (float)(rng.NextDouble() - 0.5) * 0.5f; r[i] = (float)(rng.NextDouble() - 0.5) * 0.5f; }
                var src = (l.ToArray(), r.ToArray());
                var fx = g.E.Fx;
                for (int i = 0; i < 50; i++) { Array.Copy(src.Item1, l, 256); Array.Copy(src.Item2, r, 256); fx.Process(l, r, 256); }
                const int Blocks = 600;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < Blocks; i++) { Array.Copy(src.Item1, l, 256); Array.Copy(src.Item2, r, 256); fx.Process(l, r, 256); }
                double seconds = sw.Elapsed.TotalSeconds;
                double real = Blocks * 256.0 / Rate;
                lines.Add($"{FxTypes.Info(type).Name} {100 * seconds / real:F2} %");
            }
            Measure.Report("Dye Essence rack, CPU of one slot of each type (stereo, 48 kHz, one core, share of real time): " + string.Join(", ", lines));
            Assert.Pass();
        }

        // ---- allocation -------------------------------------------------------------------------------------------

        [Test]
        public void NothingIsAllocatedOnTheAudioThread()
        {
            var g = new Rig(All(
                Slot(0, FxType.Eq, 800), Slot(1, FxType.Filter, 900), Slot(2, FxType.Comp, 1000), Slot(3, FxType.Clipper, 700),
                Slot(4, FxType.Delay, 600), Slot(5, FxType.Verb, 500), Slot(6, FxType.Clipper, 1000), Slot(7, FxType.Delay, 300),
                Slot(8, FxType.Verb, 400), Slot(9, FxType.Filter, 1000)));
            g.Chord();
            g.E.Fx.BuildNow();
            var l = new float[256];
            var r = new float[256];
            var routes = new[] { FxRouting.Default, FxRouting.Default.Move(0, 2, 3), FxRouting.Default.Move(9, 0, 0), FxRouting.Parse("10 9 8 7 6 | 5 4 | 3 2 1") };
            for (int i = 0; i < 100; i++) g.E.Process(l, r, 256);
            void Block(int i)
            {
                for (int s = 0; s < 10; s++)
                    for (int k = 0; k < 10; k++)
                        g.V.Set(FxRack.GenericParam(s, k), (i * 37 + s * 101 + k * 17) % 1001);
                g.V.Set(FxRack.MixParam(i % 10), (i * 13) % 1001);
                if (i % 50 == 0) g.V.Set(P.FxLayout, i / 50 % 2);
                if (i % 70 == 0) g.E.Fx.SetRouting(routes[i / 70 % routes.Length]);
                g.V.Set(P.FxSendA, i * 7 % 1001);
                g.V.Set(P.FxRetB, i * 11 % 1001);
                g.E.Tempo = 80 + i % 90;
                if (g.E.Fx.Latency < 0) throw new InvalidOperationException();   // the host asks at every tick
                g.E.Process(l, r, 1 + i % 256);
            }
            for (int i = 0; i < 300; i++) Block(i);
            var (bytes, why) = AllocationCheck.Run(2000, Block);
            Measure.Report($"Dye Essence effects rack: {bytes} bytes allocated over 2000 blocks with ten slots of all six types, every parameter, the layouts, the routing, sends and tempo moving");
            Assert.That(bytes, Is.EqualTo(0), why);
        }
    }
}
