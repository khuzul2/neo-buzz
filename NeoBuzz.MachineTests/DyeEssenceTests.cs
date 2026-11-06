using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Dye Essence's G0 gate (docs/specs/dye-essence.md §5): the unison wavetable renderer with per-note
    /// filters, measured. The CPU figures at 16 notes × 3 oscillators × 8 and 16 unison voices on the
    /// runner (x64 on Linux CI, arm64 on the Mac runner); the three lane implementations giving the
    /// same bits, and a render pinned by its checksum, the same on every platform; aliasing, bandwidth
    /// and pitch; block sizes; no allocation.
    /// </summary>
    [TestFixture]
    public class DyeEssenceTests
    {
        const int Rate = Measure.Rate;

        static readonly Lazy<TableArena> MorphTable = new(() => new TableArena(
            Wavetable.FromFunction("saw to square", 16, (f, t) =>
            {
                double a = f / 15.0;
                return (1 - a) * (2 * t - 1) + a * (t < 0.5 ? 1 : -1);
            })));

        static readonly Lazy<TableArena> SawTable = new(() => new TableArena(
            Wavetable.FromHarmonics("saw", 1, (f, h) => (2 / (Math.PI * h), 0))));

        static readonly LaneKind[] Kinds = Avx2Lanes.Supported
            ? new[] { LaneKind.Scalar, LaneKind.Pair, LaneKind.Avx2 }
            : new[] { LaneKind.Scalar, LaneKind.Pair };

        /// <summary>The gate's patch: three oscillators on the morphing table (between frames), detuned and spread, both filters.</summary>
        static NoteBank Gate(LaneKind kind, int unison, int oversample = 2, Action<BankSettings> tweak = null)
        {
            var b = new NoteBank(kind);
            b.S.Oversample = oversample;
            b.SetSampleRate(Rate);
            for (int o = 0; o < 3; o++)
            {
                var x = b.S.Osc[o];
                x.Table = MorphTable.Value;
                x.Unison = unison;
                x.Detune = 0.3f;
                x.Spread = 0.8f;
                x.Position = 0.37f + 0.2f * o;
                x.Transpose = o == 1 ? 12 : o == 2 ? -12 : 0;
            }
            b.S.AmpS = 1;
            tweak?.Invoke(b.S);
            return b;
        }

        static void Chord(NoteBank b, int notes = NoteBank.Notes)
        {
            for (int n = 0; n < notes; n++) b.NoteOn(n, 36 + 3 * n, 0.4f + 0.6f * n / NoteBank.Notes);
        }

        /// <summary>
        /// Renders <paramref name="frames"/> in blocks of <paramref name="block"/>, both channels
        /// interleaved; <paramref name="at"/>(frame) before each block. A block also ends at each of
        /// <paramref name="cuts"/>, so events there land on the same sample whatever the block size.
        /// </summary>
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

        static float[] Left(float[] stereo, int from, int n)
        {
            var y = new float[n];
            for (int i = 0; i < n; i++) y[i] = stereo[2 * (from + i)];
            return y;
        }

        // ------------------------------------------------------------------ the same bits

        /// <summary>Settings that take each code path: unison counts (padded lanes, two groups), 1× and 2×, routings, filter modes, drive.</summary>
        static readonly (string Name, int Unison, int Oversample, Action<BankSettings> Tweak)[] Paths =
        {
            ("unison 1, 1×", 1, 1, null),
            ("unison 3, 2×, parallel", 3, 2, s => s.Routing = 1),
            ("unison 8, 2×, half-parallel, LP12, band-pass", 8, 2, s => { s.Routing = 0.5f; s.Lp12 = true; s.Mode2 = SvfMode.BandPass; }),
            ("unison 16, 2×, drive, resonance, high-pass", 16, 2, s => { s.Drive1 = 4; s.Reso1 = 0.95f; s.Mode2 = SvfMode.HighPass; s.Reso2 = 0.9f; }),
            ("unison 5, on a frame, notch", 5, 2, s => { foreach (var o in s.Osc) o.Position = 0; s.Mode2 = SvfMode.Notch; }),
            ("unison 8, filters bypassed", 8, 2, s => s.FilterBypass = true),
            ("unison 6, 4×", 6, 4, s => s.Routing = 0.3f),
        };

        [Test]
        public void EveryLaneImplementationGivesTheSameBits()
        {
            // The kernels are one source run three ways: the scalar reference (no SIMD), two Vector128
            // halves (arm64's path) and one Vector256 (x64 with AVX2). Every sample of each path's render
            // the same, with notes starting and ending mid-render.
            foreach (var (name, unison, os, tweak) in Paths)
            {
                ulong first = 0;
                foreach (var kind in Kinds)
                {
                    var b = Gate(kind, unison, os, tweak);
                    Assert.That(b.Lanes, Is.EqualTo(kind));
                    var y = Render(b, 9600, 256, pos =>
                    {
                        if (pos == 0) Chord(b, 12);
                        if (pos == 2560) { b.NoteOff(3); b.NoteOff(7); }
                        if (pos == 5120) b.NoteOn(14, 90, 1f);
                    });
                    foreach (var v in y) Assert.That(float.IsFinite(v), Is.True, $"{name}, {kind}");
                    ulong h = Hash(y);
                    if (kind == Kinds[0]) first = h;
                    else Assert.That(h, Is.EqualTo(first), $"{name}: {kind} against the scalar reference");
                }
            }
            Measure.Report($"Dye Essence G0: lanes {string.Join(", ", Kinds)} render the same bits ({Paths.Length} settings)");
        }

        [Test]
        public void TheGateRenderIsTheSameOnEveryPlatform()
        {
            // One render of the gate's patch, pinned by its checksum: the same number on Linux x64 (CI)
            // and macOS arm64 (the Mac runner), so the lanes, the tables (built by FFT with the portable
            // maths) and the filters give the same bits everywhere (docs/specs/machines.md §4).
            var b = Gate(LaneKind.Auto, 8);
            var y = Render(b, Rate, 256, pos =>
            {
                if (pos == 0) Chord(b);
                if (pos == Rate / 2) for (int n = 0; n < NoteBank.Notes; n += 2) b.NoteOff(n);
            }, Rate / 2);
            ulong h = Hash(y);
            Measure.Report($"Dye Essence G0: the gate render's checksum {h:X16} ({b.Lanes} lanes, {RuntimeInfo()})");
            Assert.That(h, Is.EqualTo(PinnedGateHash), "the gate render's bits");
        }

        const ulong PinnedGateHash = 0x6B53E8A804EE8673UL;

        static string RuntimeInfo() =>
            $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Split(' ')[0]}";

        // ------------------------------------------------------------------ CPU: the gate

        static (double Wall, double Cpu) Speed(NoteBank b, double seconds)
        {
            var l = new float[256];
            var r = new float[256];
            Chord(b);
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
            Assert.That(b.ActiveNotes, Is.EqualTo(NoteBank.Notes), "every note sounding throughout");
            return (audio / wall, audio / Math.Max(1e-9, cpu));
        }

        [Test]
        public void TheGateRunsAtLeastTwiceRealTime()
        {
            // G0's target (docs/specs/dye-essence.md §5): 16 notes × 3 oscillators × 8 unison voices,
            // each note through a ladder and a state-variable filter in series, stereo, at 2×: at least
            // twice real time on one core of the CI runner. Reported too: 16 unison voices, 1×, and on x64
            // the Vector128 path (what a CPU without AVX2 runs). "CPU" is the process's CPU time, which
            // a busy runner disturbs less than the wall clock.
            var kind = Avx2Lanes.Supported ? LaneKind.Avx2 : LaneKind.Pair;
            var (wall8, cpu8) = Speed(Gate(kind, 8), 10);
            Measure.Report($"Dye Essence G0, 16 notes × 3 osc × 8 unison + ladder and SVF, 2× ({kind}, {RuntimeInfo()}): {wall8:F2}× real time (CPU {cpu8:F2}×)");
            var (wall16, cpu16) = Speed(Gate(kind, 16), 6);
            Measure.Report($"Dye Essence G0, 16 notes × 3 osc × 16 unison + ladder and SVF, 2× ({kind}): {wall16:F2}× real time (CPU {cpu16:F2}×)");
            var (wall1x, cpu1x) = Speed(Gate(kind, 8, 1), 6);
            Measure.Report($"Dye Essence G0, 16 notes × 3 osc × 8 unison + ladder and SVF, 1× ({kind}): {wall1x:F2}× real time (CPU {cpu1x:F2}×)");
            var (wall4x, cpu4x) = Speed(Gate(kind, 8, 4), 6);
            Measure.Report($"Dye Essence G0, 16 notes × 3 osc × 8 unison + ladder and SVF, 4× ({kind}): {wall4x:F2}× real time (CPU {cpu4x:F2}×)");
            var (wall16x4, cpu16x4) = Speed(Gate(kind, 16, 4), 6);
            Measure.Report($"Dye Essence G0, 16 notes × 3 osc × 16 unison + ladder and SVF, 4× ({kind}): {wall16x4:F2}× real time (CPU {cpu16x4:F2}×)");
            var (wallU1, cpuU1) = Speed(Gate(kind, 1), 6);
            Measure.Report($"Dye Essence G0, 16 notes × 3 osc × 1 voice + ladder and SVF, 2× ({kind}): {wallU1:F2}× real time (CPU {cpuU1:F2}×)");
            var (wallNo, cpuNo) = Speed(Gate(kind, 8, 2, s => { foreach (var o in s.Osc) o.Enabled = false; }), 6);
            Measure.Report($"Dye Essence G0, the same without oscillators (filters, envelopes, control, decimation), 2× ({kind}): {wallNo:F2}× real time (CPU {cpuNo:F2}×)");
            if (kind == LaneKind.Avx2)
            {
                var (wallP, cpuP) = Speed(Gate(LaneKind.Pair, 8), 6);
                Measure.Report($"Dye Essence G0, 16 notes × 3 osc × 8 unison + ladder and SVF, 2× (Pair, x64 without AVX2): {wallP:F2}× real time (CPU {cpuP:F2}×)");
            }
            Measure.Report($"Dye Essence G0: a 256-frame table takes {256L * TableArena.FrameStride * sizeof(float) / 1e6:F1} MB (11 mip levels)");
            Assert.That(wall8, Is.GreaterThan(2), "G0: 16 × 3 × 8 unison with two filters at twice real time");
        }

        // ------------------------------------------------------------------ quality

        /// <summary>One clean voice: the saw table, unison 1, filters bypassed, full sustain.</summary>
        static NoteBank Clean(int oversample)
        {
            var b = new NoteBank();
            b.S.Oversample = oversample;
            b.SetSampleRate(Rate);
            b.S.Osc[0].Table = SawTable.Value;
            b.S.Osc[1].Enabled = b.S.Osc[2].Enabled = false;
            b.S.FilterBypass = true;
            b.S.AmpA = 0.001f;
            b.S.AmpS = 1;
            b.S.Volume = 1;
            return b;
        }

        [TestCase(94, 2)]
        [TestCase(81, 2)]
        [TestCase(57, 2)]
        [TestCase(33, 2)]
        [TestCase(94, 1)]
        [TestCase(57, 1)]
        [TestCase(94, 4)]
        [TestCase(57, 4)]
        public void SawAliasFloor(int note, int oversample)
        {
            // Neo Wave's criterion (machines.md §5): the saw table at a note, everything that is not a
            // harmonic, 30 Hz – 20 kHz, under −80 dB (Neo Wave: −92 … −101 dB). At 1× the mip level keeps
            // every harmonic under Nyquist, so nothing aliases there either; what 1× costs is bandwidth
            // (KeepsTheAudibleBand).
            var b = Clean(oversample);
            b.NoteOn(0, note, 1f);
            var y = Left(Render(b, 8192 + Measure.N), 8192, Measure.N);
            double floor = PolyTests.AliasFloorWindowed(y, 0, Measure.N, DspMath.NoteToHz(note));
            Measure.Report($"Dye Essence G0, saw at note {note} ({DspMath.NoteToHz(note):F0} Hz), {oversample}×: alias floor {floor:F1} dB");
            Assert.That(floor, Is.LessThan(-80));
        }

        [TestCase(45, 2)]
        [TestCase(69, 2)]
        [TestCase(93, 2)]
        [TestCase(105, 2)]
        [TestCase(69, 1)]
        [TestCase(93, 1)]
        [TestCase(93, 4)]
        public void KeepsTheAudibleBand(int note, int oversample)
        {
            // Neo Wave's criterion: the harmonic nearest 18 kHz within 1.5 dB of a saw's at 2× and 4×. At 1×
            // it is reported (the level that keeps every harmonic under Nyquist can drop the top octave).
            var b = Clean(oversample);
            b.NoteOn(0, note, 1f);
            var y = Left(Render(b, 8192 + 32768), 8192, 32768);
            var w = new float[32768];
            for (int i = 0; i < w.Length; i++) w[i] = (float)(y[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / w.Length)));
            var mag = Fft.Magnitudes(w, 0, w.Length);
            double f0 = DspMath.NoteToHz(note);
            int h = (int)(18000 / f0);
            double binHz = (double)Rate / w.Length;
            double At(double hz)
            {
                int k = (int)Math.Round(hz / binHz);
                double m = 0;
                for (int j = k - 3; j <= k + 3; j++) m = Math.Max(m, mag[j]);
                return m;
            }
            double rel = Measure.Db(At(h * f0) / At(f0));
            double ideal = -20 * Math.Log10(h);
            Measure.Report($"Dye Essence G0, saw at {f0:F0} Hz, {oversample}×: harmonic {h} ({h * f0 / 1000:F1} kHz) at {rel:F1} dB (ideal {ideal:F1})");
            if (oversample >= 2) Assert.That(rel, Is.EqualTo(ideal).Within(1.5));
        }

        [Test]
        public void UnisonVoicesSitWhereTheDetuneSetsThem()
        {
            // Two voices, 1 semitone apart in all (±50 cents round the note), each a sine: the two peaks at
            // the note's frequency × 2^(±1/24), within 0.05 cent; and a single voice on the note.
            var sine = new TableArena(Wavetable.FromHarmonics("sine", 1, (f, h) => (h == 1 ? 1.0 : 0, 0)));
            foreach (int u in new[] { 1, 2 })
            {
                var b = Clean(2);
                b.S.Osc[0].Table = sine;
                b.S.Osc[0].Unison = u;
                b.S.Osc[0].Detune = 1;
                b.NoteOn(0, 69, 1f);
                var y = Left(Render(b, 4096 + Measure.N), 4096, Measure.N);
                var w = new float[Measure.N];
                for (int i = 0; i < w.Length; i++)
                {
                    double t = 2 * Math.PI * i / w.Length;
                    w[i] = (float)(y[i] * (0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t)));
                }
                var mag = Fft.Magnitudes(w, 0, w.Length);
                double binHz = (double)Rate / w.Length;
                double Peak(double near)
                {
                    int k0 = (int)Math.Round(near / binHz), best = k0;
                    for (int k = k0 - 8; k <= k0 + 8; k++) if (mag[k] > mag[best]) best = k;
                    double a = Math.Log(mag[best - 1] + 1e-30), c = Math.Log(mag[best] + 1e-30), d = Math.Log(mag[best + 1] + 1e-30);
                    return (best + 0.5 * (a - d) / (a - 2 * c + d)) * binHz;
                }
                var expect = u == 1 ? new[] { 440.0 } : new[] { 440 * Math.Pow(2, -1 / 24.0), 440 * Math.Pow(2, 1 / 24.0) };
                foreach (var f in expect)
                {
                    double cents = Measure.Cents(Peak(f), f);
                    Measure.Report($"Dye Essence G0, unison {u}, detune 1 st: a voice at {f:F3} Hz, {cents:+0.0000;-0.0000} cent");
                    Assert.That(Math.Abs(cents), Is.LessThan(0.05));
                }
            }
        }

        // ------------------------------------------------------------------ robustness

        [Test]
        public void BlockSizesGiveTheSameSamples()
        {
            // The control grid runs across blocks and a note started mid-tick gets its values for the rest
            // of it, so blocks of 256, 117 and 16 give the same samples, notes starting and ending included.
            ulong first = 0;
            foreach (int block in new[] { 256, 117, 16 })
            {
                var b = Gate(LaneKind.Auto, 8);
                var y = Render(b, 12000, block, pos =>
                {
                    if (pos == 0) Chord(b, 6);
                    if (pos == 4000) { b.NoteOff(1); b.NoteOff(4); }
                    if (pos == 7003) b.NoteOn(9, 80, 0.9f);   // mid-tick
                }, 4000, 7003);
                ulong h = Hash(y);
                if (block == 256) first = h;
                else Assert.That(h, Is.EqualTo(first), $"blocks of {block}");
            }
        }

        [Test]
        public void EveryNoteEndsInSilence()
        {
            // Resonant filters, released: the bank goes quiet (Process says so) and leaves no subnormals.
            var b = Gate(LaneKind.Auto, 8, 2, s => { s.Reso1 = 1; s.Reso2 = 1; s.AmpR = 0.05f; });
            var l = new float[256];
            var r = new float[256];
            Chord(b);
            for (int i = 0; i < 100; i++) b.Process(l, r, 256);
            for (int n = 0; n < NoteBank.Notes; n++) b.NoteOff(n);
            bool sounding = true;
            for (int i = 0; i < 400 && sounding; i++) sounding = b.Process(l, r, 256);
            Assert.That(sounding, Is.False, "silent after the release");
            Assert.That(b.ActiveNotes, Is.EqualTo(0));
            foreach (var v in l.Concat(r)) Assert.That(float.IsSubnormal(v), Is.False, "no subnormals");
        }

        [Test]
        public void TheAudioThreadAllocatesNothing()
        {
            // 2000 blocks with notes starting and ending, unison and positions changing, every lane
            // implementation the CPU has: 0 bytes.
            foreach (var kind in Kinds.Where(k => k != LaneKind.Scalar))
            {
                var b = Gate(kind, 8);
                var l = new float[256];
                var r = new float[256];
                Chord(b);
                for (int i = 0; i < 20; i++) b.Process(l, r, 256);
                void Block(int i)
                {
                    if (i % 50 == 0) b.NoteOn(i / 50 % NoteBank.Notes, 40 + i % 40, 0.8f);
                    if (i % 50 == 25) b.NoteOff((i / 50 + 7) % NoteBank.Notes);
                    b.S.Osc[1].Unison = 1 + i / 100 % 16;
                    b.S.Osc[2].Position = i % 97 / 96f;
                    b.Process(l, r, 1 + i % 256);
                }
                var (bytes, why) = AllocationCheck.Run(2000, Block);
                Measure.Report($"Dye Essence G0 ({kind}): {bytes} bytes allocated over 2000 blocks with notes, unison and positions moving");
                Assert.That(bytes, Is.EqualTo(0), why);
            }
        }
    }
}
