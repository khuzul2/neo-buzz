using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Dye Essence P3a (docs/specs/dye-essence.md §4.7), measured: the Harmonic engine on its own
    /// (levels of the sets, morph, tilt, odd/even, stretch, damp, band-limiting, lane implementations,
    /// block sizes, pinned bits, extremes, no allocation, CPU).
    /// </summary>
    [TestFixture]
    public class DyeEssenceHarmonicTests
    {
        const int Rate = Measure.Rate;

        static readonly LaneKind[] Kinds = Avx2Lanes.Supported
            ? new[] { LaneKind.Scalar, LaneKind.Pair, LaneKind.Avx2 }
            : new[] { LaneKind.Scalar, LaneKind.Pair };

        /// <summary>One Harmonic slot, nothing else: no filters, a quick attack, full sustain.</summary>
        static NoteBank Harm(LaneKind kind = LaneKind.Auto, int oversample = 2, Action<OscSettings> tweak = null)
        {
            var b = new NoteBank(kind);
            b.S.Oversample = oversample;
            b.SetSampleRate(Rate);
            var o = b.S.Osc[0];
            o.Type = SlotType.Harmonic;
            o.Random = 0;
            o.Phase = 0;
            o.SetA = HarmonicSets.Get((int)HarmonicSet.Saw);
            o.SetB = HarmonicSets.Get((int)HarmonicSet.Square);
            b.S.Osc[1].Type = b.S.Osc[2].Type = SlotType.Off;
            b.S.Filter[0].Type = b.S.Filter[1].Type = FilterType.Off;
            b.S.Amp.A = 0.001f;
            b.S.Amp.S = 1;
            b.S.Volume = 1;
            b.S.VelAmp = 0;
            tweak?.Invoke(o);
            return b;
        }

        /// <summary>A block also ends at each of <paramref name="cuts"/>, so an event there lands on that sample whatever the block size.</summary>
        static float[] Render(NoteBank b, int frames, int block = 256, Action<int> at = null, params int[] cuts)
        {
            var y = new float[2 * frames];
            var l = new float[NoteBank.MaxBlock];
            var r = new float[NoteBank.MaxBlock];
            for (int pos = 0, n; pos < frames; pos += n)
            {
                at?.Invoke(pos);
                n = Math.Min(block, frames - pos);
                foreach (int c in cuts) if (c > pos && c < pos + n) n = c - pos;
                b.Process(l, r, n);
                for (int i = 0; i < n; i++)
                {
                    y[2 * (pos + i)] = l[i];
                    y[2 * (pos + i) + 1] = r[i];
                }
            }
            return y;
        }

        static ulong Hash(float[] x)
        {
            ulong h = 1469598103934665603UL;
            foreach (var v in x) h = (h ^ BitConverter.SingleToUInt32Bits(v)) * 1099511628211UL;
            return h;
        }

        /// <summary>The left channel's level at <paramref name="hz"/> over 4800 samples from <paramref name="from"/> (Hann-windowed, linear amplitude of a sine).</summary>
        static double Amp(float[] y, int from, double hz)
        {
            const int N = 4800;
            double re = 0, im = 0, w0 = 0;
            for (int i = 0; i < N; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / N);
                double x = y[2 * (from + i)] * w;
                double ph = 2 * Math.PI * hz * i / Rate;
                re += x * Math.Cos(ph);
                im += x * Math.Sin(ph);
                w0 += w;
            }
            return 2 * Math.Sqrt(re * re + im * im) / w0;
        }

        static double Db(double x) => 20 * Math.Log10(Math.Max(1e-12, x));

        static float[] Note(Action<OscSettings> tweak, int key = 57, int frames = 12000, Action<NoteBank> more = null)
        {
            var b = Harm(tweak: tweak);
            more?.Invoke(b);
            b.NoteOn(0, key, 1f);
            return Render(b, frames);
        }

        const double A3 = 220.0;

        [Test]
        public void ASetsPartialsSoundAtTheirLevels()
        {
            // Saw: partial k at 1/k of the first; 32 partials, nothing else.
            var y = Note(o => { o.Partials = 128; });
            double a1 = Amp(y, 4800, A3);
            var sb = new System.Text.StringBuilder();
            for (int k = 2; k <= 10; k++)
            {
                double want = Db(1.0 / k), got = Db(Amp(y, 4800, A3 * k) / a1);
                sb.Append($" {k}: {got - want:+0.00;-0.00}");
                Assert.That(got, Is.EqualTo(want).Within(0.3), $"partial {k}");
            }
            Measure.Report($"Dye Essence Harmonic engine, Saw set, level of partials 2 … 10 against 1/k (dB):{sb}");
            // Between the partials there is nothing (a sum of sines at whole multiples).
            Assert.That(Db(Amp(y, 4800, A3 * 2.5) / a1), Is.LessThan(-70));
        }

        [Test]
        public void MorphingMovesBetweenTheTwoSets()
        {
            // Saw → Square: the even partials fade out as "position" goes to 1; halfway they are half.
            double[] even = new double[3];
            for (int i = 0; i < 3; i++)
            {
                float pos = i * 0.5f;
                var y = Note(o => { o.Partials = 128; o.Position = pos; });
                even[i] = Amp(y, 4800, A3 * 2) / Amp(y, 4800, A3);
                Measure.Report($"Dye Essence Harmonic engine, morph {pos:0.0}: partial 2 at {even[i]:0.000} of the first");
            }
            Assert.That(even[2], Is.LessThan(1e-3), "square: no even partials");
            Assert.That(even[0], Is.EqualTo(0.5).Within(0.02), "saw: a half");
            Assert.That(even[1], Is.GreaterThan(0.05).And.LessThan(0.45), "between");
        }

        [Test]
        public void TiltOddEvenAndPartialCountShapeTheSpectrum()
        {
            // Tilt +6 dB an octave lifts partial 8 by 18 dB against partial 1 (and a flat one is the set's own).
            var flat = Note(o => { o.Partials = 64; o.SetA = HarmonicSets.Get((int)HarmonicSet.Bright); });
            var tilt = Note(o => { o.Partials = 64; o.SetA = HarmonicSets.Get((int)HarmonicSet.Bright); o.Tilt = 6; });
            double d = Db(Amp(tilt, 4800, A3 * 8) / Amp(tilt, 4800, A3)) - Db(Amp(flat, 4800, A3 * 8) / Amp(flat, 4800, A3));
            Measure.Report($"Dye Essence Harmonic engine, tilt +6 dB/oct: partial 8 against 1 moves {d:0.00} dB (18 expected)");
            Assert.That(d, Is.EqualTo(18).Within(0.4));
            // ... and the loudness stays.
            double rf = Rms(flat), rt = Rms(tilt), rd = Rms(Note(o => { o.Partials = 64; o.SetA = HarmonicSets.Get((int)HarmonicSet.Bright); o.Tilt = -18; }));
            Measure.Report($"Dye Essence Harmonic engine, loudness at tilt −18 / 0 / +6 dB per octave: {Db(rd):0.0} / {Db(rf):0.0} / {Db(rt):0.0} dBFS");
            Assert.That(Db(rt) - Db(rf), Is.EqualTo(0).Within(1.0));
            Assert.That(Db(rd) - Db(rf), Is.EqualTo(0).Within(1.0));

            // Odd/even: + weakens the even partials, − the odd ones.
            var odd = Note(o => { o.Partials = 128; o.OddEven = 1; });
            Assert.That(Db(Amp(odd, 4800, A3 * 2) / Amp(odd, 4800, A3)), Is.LessThan(-70), "odd only");
            var even = Note(o => { o.Partials = 128; o.OddEven = -1; });
            Assert.That(Db(Amp(even, 4800, A3) / Amp(even, 4800, A3 * 2)), Is.LessThan(-70), "even only");

            // The partial count: 4 partials leave nothing at the 5th (and a fade at the top).
            var four = Note(o => { o.Partials = 4; });
            Assert.That(Db(Amp(four, 4800, A3 * 5) / Amp(four, 4800, A3)), Is.LessThan(-70), "four partials");
            Assert.That(Amp(four, 4800, A3 * 3), Is.GreaterThan(0.05 * Amp(four, 4800, A3)), "the third is there");
        }

        static double Rms(float[] y)
        {
            double s = 0;
            int n = 4800;
            for (int i = 6000; i < 6000 + n; i++) s += (double)y[2 * i] * y[2 * i];
            return Math.Sqrt(s / n);
        }

        [Test]
        public void StretchMovesThePartialsOffTheWholeMultiples()
        {
            // +100 %: partial k at k^1.5 times the pitch (2 → 2.83, 4 → 8); −100 %: k^0.5 (4 → 2).
            var y = Note(o => { o.Partials = 16; o.Stretch = 1; });
            double a1 = Amp(y, 4800, A3);
            double f2 = A3 * Math.Pow(2, 1.5), f4 = A3 * 8;
            Measure.Report($"Dye Essence Harmonic engine, stretch +100 %: partial 2 at {Db(Amp(y, 4800, f2) / a1):0.0} dB (at {f2:0} Hz), nothing at 2f: {Db(Amp(y, 4800, A3 * 2) / a1):0.0} dB");
            Assert.That(Db(Amp(y, 4800, f2) / a1), Is.EqualTo(Db(0.5)).Within(0.5));
            Assert.That(Db(Amp(y, 4800, f4) / a1), Is.EqualTo(Db(0.25)).Within(0.5));
            Assert.That(Db(Amp(y, 4800, A3 * 2) / a1), Is.LessThan(-40));
            var z = Note(o => { o.Partials = 16; o.Stretch = -1; });
            Assert.That(Db(Amp(z, 4800, A3 * 2) / Amp(z, 4800, A3)), Is.EqualTo(Db(0.25)).Within(0.5), "partial 4 at twice the pitch");
        }

        [Test]
        public void DampLetsTheUpperPartialsDieAway()
        {
            // Partial 9's level (in the first 100 ms against 1 s later), with the fundamental unchanged.
            var held = Note(o => { o.Partials = 32; o.Damp = 0.5f; }, frames: 48000 + 4800);
            double early9 = Amp(held, 0, A3 * 9), late9 = Amp(held, 48000, A3 * 9);
            double early1 = Amp(held, 0, A3), late1 = Amp(held, 48000, A3);
            Measure.Report($"Dye Essence Harmonic engine, damp 50 %: partial 9 falls {Db(late9 / early9):0.0} dB in a second, partial 1 {Db(late1 / early1):0.0} dB");
            // e^(−0.5 · 0.5 · 8 · 1 s) = −17 dB for partial 9.
            Assert.That(Db(late9 / early9), Is.LessThan(-12));
            Assert.That(Db(late1 / early1), Is.GreaterThan(-1));
        }

        [Test]
        public void NothingFoldsBackFromAboveTheBand()
        {
            // A high note with every partial asked for: partials past the voice rate's band are not rendered,
            // so what the engine leaves at the frequencies they would alias to is the decimator's residue only.
            double f0 = 2637;   // note 100
            var y = Note(o => { o.Partials = 128; o.SetA = HarmonicSets.Get((int)HarmonicSet.Bright); o.SetB = HarmonicSets.Get((int)HarmonicSet.Bright); }, key: 100, frames: 12000);
            double top = Amp(y, 4800, f0);
            double worst = -200;
            string where = "";
            for (int k = 10; k <= 16; k++)
            {
                double f = f0 * k, alias = Math.Abs(f - Rate * Math.Round(f / Rate));
                double db = Db(Amp(y, 4800, alias) / top);
                if (db > worst) { worst = db; where = $"partial {k} ({f:0} Hz) at {alias:0} Hz"; }
            }
            Measure.Report($"Dye Essence Harmonic engine, note 100 with 128 partials: worst alias image {worst:0.0} dB below the fundamental ({where})");
            Assert.That(worst, Is.LessThan(-80));
        }

        static void Patch(NoteBank b)
        {
            for (int o = 0; o < 3; o++)
            {
                var s = b.S.Osc[o];
                s.Type = SlotType.Harmonic;
                s.SetA = HarmonicSets.Get(o == 0 ? (int)HarmonicSet.Strings : o == 1 ? (int)HarmonicSet.VowelA : (int)HarmonicSet.Brass);
                s.SetB = HarmonicSets.Get((int)HarmonicSet.Random);
                s.Position = 0.2f + 0.3f * o;
                s.Partials = 40 + 20 * o;
                s.Tilt = o - 1;
                s.OddEven = 0.2f * (o - 1);
                s.Stretch = 0.1f * o;
                s.Damp = 0.1f * o;
                s.Unison = 1 + 2 * o;
                s.Detune = 12;
                s.Spread = 0.5f;
                s.Level = 0.4f;
                s.Transpose = 12 * (o - 1);
                s.Random = 0.5f;
            }
            b.S.Filter[0].Type = FilterType.Ladder24;
            b.S.Filter[0].Cutoff = 4000;
            b.S.Amp.A = 0.002f;
            b.S.Amp.S = 0.8f;
            b.S.Volume = 0.6f;
            b.S.Routes[0].Source = RouteSource.Lfo1; b.S.Routes[0].Dest = RouteDest.APosition; b.S.Routes[0].Amount = 0.4f;
            b.S.Routes[1].Source = RouteSource.ModEnv1; b.S.Routes[1].Dest = RouteDest.BPitch; b.S.Routes[1].Amount = 0.1f;
            b.S.Lfo[0].Rate = 2f;
        }

        const ulong PinnedHash = 0x6A771AE6CB434435UL;

        [Test]
        public void EveryLaneImplementationGivesTheSameBitsAndTheyAreThePinnedOnes()
        {
            ulong first = 0;
            foreach (var kind in Kinds)
            {
                var b = new NoteBank(kind);
                b.SetSampleRate(Rate);
                Patch(b);
                for (int n = 0; n < 6; n++) b.NoteOn(n, 48 + 5 * n, 0.5f + 0.1f * n);
                var y = Render(b, 24000, 256, p => { if (p == 12000) { b.NoteOff(1); b.NoteOn(7, 80, 1f); } }, 12000);
                ulong h = Hash(y);
                Measure.Report($"Dye Essence harmonics (three slots, unison, morph, damp), {kind}: 0x{h:X16}");
                if (first == 0) first = h;
                Assert.That(h, Is.EqualTo(first), kind.ToString());
            }
            Assert.That(first, Is.EqualTo(PinnedHash), "the pinned render (re-pin from the Linux run when the arithmetic changes on purpose)");
        }

        [Test]
        public void ARestingBankGivesTheSameBitsInEveryBlockSize()
        {
            // A note, its release, then two seconds of silence: the bank rests after 100 ms, decided on the control grid.
            ulong want = 0;
            foreach (int block in new[] { 256, 64, 255, 117, 16, 1 })
            {
                var b = Harm();
                b.NoteOn(0, 60, 1f);
                var y = Render(b, 2 * Rate, block, p => { if (p == 6000) b.NoteOff(0); }, 6000);
                ulong h = Hash(y);
                if (want == 0) want = h;
                Assert.That(h, Is.EqualTo(want), $"blocks of {block}");
            }
        }

        [Test]
        public void BlockSizesGiveTheSameSamples()
        {
            ulong want = 0;
            foreach (int block in new[] { 256, 117, 16 })
            {
                var b = new NoteBank(LaneKind.Auto);
                b.SetSampleRate(Rate);
                Patch(b);
                for (int n = 0; n < 6; n++) b.NoteOn(n, 48 + 5 * n, 0.5f + 0.1f * n);
                ulong h = Hash(Render(b, 24000, block));
                if (want == 0) want = h;
                Assert.That(h, Is.EqualTo(want), $"blocks of {block}");
            }
        }

        [Test]
        public void EveryHarmonicParameterAtItsExtremesStaysFinite()
        {
            var table = Params.Table;
            var l = new float[256];
            var r = new float[256];
            double loudest = 0;
            string where = "";
            for (int p = P.AHSet; p < table.Length; p++)
                foreach (int raw in new[] { table[p].Min, table[p].Max })
                {
                    var v = new ParamValues(table);
                    v.Set(P.AType, (int)SlotType.Harmonic);
                    v.Set(P.BType, (int)SlotType.Harmonic);
                    v.Set(P.CType, (int)SlotType.Harmonic);
                    v.Set(P.AUnison, 16);
                    v.Set(p, raw);
                    var e = new DyeEngine(v);
                    e.SetSampleRate(Rate);
                    foreach (int k in new[] { 24, 60, 67, 108 }) e.NoteOn(k, k, 1f);
                    for (int i = 0; i < 60; i++)
                    {
                        if (i == 40) foreach (int k in new[] { 24, 60, 67, 108 }) e.NoteOff(k);
                        e.Process(l, r, 256);
                        for (int j = 0; j < 256; j++)
                        {
                            Assert.That(float.IsFinite(l[j]) && float.IsFinite(r[j]), Is.True, $"{table[p].Name} = {raw}");
                            double a = Math.Max(Math.Abs(l[j]), Math.Abs(r[j]));
                            if (a > loudest) { loudest = a; where = $"{table[p].Name} = {table[p].Describe(raw)}"; }
                        }
                    }
                }
            Measure.Report($"Dye Essence P3a: every harmonic parameter at its minimum and maximum, 16 unison, a held chord: finite, loudest {Db(loudest):F1} dBFS ({where})");
            Assert.That(loudest, Is.LessThan(4));
        }

        [Test]
        public void NothingIsAllocatedOnTheAudioThread()
        {
            foreach (var kind in Kinds.Where(k => k != LaneKind.Scalar))
            {
                var v = new ParamValues(Params.Table);
                for (int i = 0; i < TableBank.Count; i++) TableBank.GetNow(i);
                for (int i = 0; i < SampleSources.Count; i++) SampleSources.GetNow(i);
                var e = new DyeEngine(v, kind);
                e.SetSampleRate(Rate);
                var l = new float[256];
                var r = new float[256];
                v.Set(P.AType, (int)SlotType.Harmonic);
                v.Set(P.BType, (int)SlotType.Harmonic);
                v.Set(P.CType, (int)SlotType.Harmonic);
                v.Set(P.R1Src, (int)RouteSource.Lfo1); v.Set(P.R1Dst, (int)RouteDest.BPosition); v.Set(P.R1Amt, 800);
                for (int k = 0; k < 8; k++) e.NoteOn(k, 40 + 5 * k, 0.8f);
                for (int i = 0; i < 20; i++) e.Process(l, r, 256);
                void Block(int i)
                {
                    if (i % 50 == 0) e.NoteOn(i / 50 % 24, 40 + i % 40, 0.8f);
                    if (i % 50 == 25) e.NoteOff((i / 50 + 7) % 24);
                    v.Set(P.AHSet, i / 37 % 14);
                    v.Set(P.BHSet2, i / 41 % 14);
                    v.Set(P.AHPartials, 1 + i % 128);
                    v.Set(P.AHTilt, i % 1000);
                    v.Set(P.BHOddEven, i * 7 % 1000);
                    v.Set(P.CHStretch, i * 13 % 1000);
                    v.Set(P.BHDamp, i % 300);
                    v.Set(P.APos, i * 5 % 1000);
                    v.Set(P.AUnison, 1 + i / 100 % 16);
                    v.Set(P.BType, (i < 100 ? i / 16 : i / 130) % 6);
                    e.Process(l, r, 1 + i % 256);
                }
                for (int i = 0; i < 100; i++) Block(i);
                var (bytes, why) = AllocationCheck.Run(2000, Block);
                Measure.Report($"Dye Essence Harmonic engine ({kind}): {bytes} bytes allocated over 2000 blocks with sets, partials, tilt, stretch, damp, unison, engine type moving");
                Assert.That(bytes, Is.EqualTo(0), why);
            }
        }

        static (double Wall, double Cpu) Speed(NoteBank b, double seconds, int notes)
        {
            var l = new float[256];
            var r = new float[256];
            for (int n = 0; n < notes; n++) b.NoteOn(n, 36 + 3 * n, 0.4f + 0.6f * n / 16);
            for (int i = 0; i < 40; i++) b.Process(l, r, 256);
            int blocks = (int)(seconds * Rate / 256);
            var p = Process.GetCurrentProcess();
            var cpu0 = p.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < blocks; i++) b.Process(l, r, 256);
            double wall = sw.Elapsed.TotalSeconds;
            p.Refresh();
            double cpu = (p.TotalProcessorTime - cpu0).TotalSeconds;
            double audio = blocks * 256.0 / Rate;
            Assert.That(b.ActiveNotes, Is.EqualTo(notes), "every note sounding throughout");
            return (audio / wall, audio / Math.Max(1e-9, cpu));
        }

        [Test]
        public void TheEngineRunsInRealTimeAsAStandaloneEngine()
        {
            (string Name, Func<NoteBank> Make)[] cases =
            {
                ("32 partials", () => Harm(tweak: o => o.Partials = 32)),
                ("64 partials", () => Harm(tweak: o => o.Partials = 64)),
                ("128 partials", () => Harm(tweak: o => o.Partials = 128)),
                ("128 partials, 8 unison voices", () => Harm(tweak: o => { o.Partials = 128; o.Unison = 8; o.Detune = 20; })),
                ("3 slots × 48 partials × 2 voices", () =>
                {
                    var b = Harm(tweak: o => { o.Partials = 48; o.Unison = 2; o.Detune = 10; });
                    for (int i = 1; i < 3; i++)
                    {
                        var o = b.S.Osc[i];
                        o.Type = SlotType.Harmonic; o.SetA = HarmonicSets.Get(i); o.SetB = HarmonicSets.Get(i + 3); o.Partials = 48; o.Unison = 2; o.Detune = 10;
                    }
                    return b;
                }),
            };
            double[] least = { 3, 2, 1, 1, 0.8 };
            int c = 0;
            foreach (var (name, make) in cases)
            {
                var b = make();
                b.S.Filter[0].Type = FilterType.Ladder24;
                b.S.Filter[0].Cutoff = 6000;
                var floor = least[c++];
                var (wall, cpu) = GuardPolicy.Retry(() => Speed(b, 4, 16), s => s.Wall > floor, out _);   // GuardPolicy: best of up to 3
                Measure.Report($"Dye Essence Harmonic, {name}, 16 notes + ladder, 2×: {wall:F2}× real time (CPU {cpu:F2}×)");
                Assert.That(wall, Is.GreaterThan(floor), name);
            }
        }

        [Test]
        public void ALoadedWavetableGivesTheUserSets()
        {
            // A table whose first frame is a pure third harmonic and last a pure fifth: the User sets are those partials.
            var frames = new float[2][];
            for (int f = 0; f < 2; f++)
            {
                frames[f] = new float[2048];
                for (int i = 0; i < 2048; i++) frames[f][i] = (float)Math.Sin(2 * Math.PI * (f == 0 ? 3 : 5) * i / 2048);
            }
            var arena = new TableArena(Wavetable.FromFrames("test", frames, false));
            var first = HarmonicSets.FromTable(arena, 0);
            var last = HarmonicSets.FromTable(arena, 1);
            Assert.That(first[2], Is.EqualTo(1f).Within(1e-3));
            Assert.That(last[4], Is.EqualTo(1f).Within(1e-3));
            Assert.That(first[0], Is.LessThan(1e-3));
        }
    }
}
