using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Dye Essence (docs/specs/dye-essence.md §5, §6 P1a), measured: the renderer's CPU at 16 notes ×
    /// 3 oscillators × 8 and 16 unison voices on the runner (x64 on Linux CI, arm64 on the Mac
    /// runner); the three lane implementations giving the same bits, and renders pinned by their
    /// checksums, the same on every platform; aliasing (plain and per warp, 2× and 4×), bandwidth,
    /// pitch, filters against their design, the comb's pitch, zipper, envelope times, steals, glide;
    /// block sizes; extremes; no allocation.
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

        static readonly Lazy<TableArena> SineTable = new(() => new TableArena(
            Wavetable.FromHarmonics("sine", 1, (f, h) => (h == 1 ? 1.0 : 0, 0))));

        static readonly LaneKind[] Kinds = Avx2Lanes.Supported
            ? new[] { LaneKind.Scalar, LaneKind.Pair, LaneKind.Avx2 }
            : new[] { LaneKind.Scalar, LaneKind.Pair };

        /// <summary>
        /// The gate's patch (P0's): three plain oscillators on the morphing table (between frames),
        /// detuned ±15 cents and spread, a ladder into a state-variable lowpass.
        /// </summary>
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
                x.Detune = 15;
                x.Spread = 0.8f;
                x.Position = 0.37f + 0.2f * o;
                x.Transpose = o == 1 ? 12 : o == 2 ? -12 : 0;
            }
            var s = b.S;
            s.Filter[0].Type = FilterType.Ladder24;
            s.Filter[0].Cutoff = 2000; s.Filter[0].Reso = 0.3f; s.Filter[0].Env = 2; s.Filter[0].Key = 0.5f;
            s.Filter[1].Type = FilterType.Lp12;
            s.Filter[1].Cutoff = 8000; s.Filter[1].Reso = 0.2f; s.Filter[1].Env = 1; s.Filter[1].Key = 0.5f;
            s.Amp.A = 0.005f; s.Amp.D = 0.3f; s.Amp.S = 1; s.Amp.R = 0.4f;
            s.Flt.A = 0.01f; s.Flt.D = 0.5f; s.Flt.S = 0.3f; s.Flt.R = 0.5f;
            b.Analog.Basic = TableBank.GetNow(0);
            b.Analog.Pulse = TableBank.GetNow(1);
            tweak?.Invoke(s);
            return b;
        }

        /// <summary>Everything P1a adds on top of the gate: warps with modulators, the sub and the noise, filter 2's own sources, the comb.</summary>
        static void Everything(BankSettings s)
        {
            s.Osc[0].Warp1 = Warp.Fm; s.Osc[0].Amount1 = 0.4f; s.Osc[0].Mod = ModSource.OscB;
            s.Osc[0].Warp2 = Warp.BendPlus; s.Osc[0].Amount2 = 0.5f;
            s.Osc[1].Unison = 3; s.Osc[1].ToF2 = 0.5f;
            s.Osc[2].Warp1 = Warp.Pm; s.Osc[2].Amount1 = 0.3f; s.Osc[2].Mod = ModSource.Sub;
            s.Osc[2].Warp2 = Warp.WindowSync; s.Osc[2].Amount2 = 0.4f;
            s.Sub.Level = 0.5f; s.Sub.ToF2 = 1; s.SubShape = SubShape.Triangle;
            s.Noise.Level = 0.1f; s.NoiseColor = 0.4f; s.Noise.Pan = -0.5f;
            s.Filter[1].Type = FilterType.CombPlus; s.Filter[1].Reso = 0.7f; s.Filter[1].Key = 1; s.Filter[1].Cutoff = 300;
            s.Routing = 0.4f;
        }

        static void Chord(NoteBank b, int notes = 16)
        {
            for (int n = 0; n < notes; n++) b.NoteOn(n, 36 + 3 * n, 0.4f + 0.6f * n / 16);
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

        static string RuntimeInfo() =>
            $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Split(' ')[0]}";

        // ------------------------------------------------------------------ the same bits

        /// <summary>Settings that take each code path: unison counts, packing, rates, routings, every filter model, every warp, the sub and noise.</summary>
        static readonly (string Name, int Unison, int Oversample, Action<BankSettings> Tweak)[] Paths =
        {
            ("unison 1, 1×", 1, 1, null),
            ("unison 3, 2×, parallel", 3, 2, s => s.Routing = 1),
            ("unison 8, 2×, half-parallel, ladder 12, BP 24", 8, 2, s => { s.Routing = 0.5f; s.Filter[0].Type = FilterType.Ladder12; s.Filter[1].Type = FilterType.Bp24; }),
            ("unison 16, drive, resonance, HP 12, notch", 16, 2, s => { s.Filter[0].Drive = 4; s.Filter[0].Reso = 0.95f; s.Filter[0].Type = FilterType.Hp12; s.Filter[1].Type = FilterType.Notch; s.Filter[1].Reso = 0.9f; }),
            ("unison 5, on a frame, peak, filter 1 off", 5, 2, s => { foreach (var o in s.Osc) o.Position = 0; s.Filter[0].Type = FilterType.Off; s.Filter[1].Type = FilterType.Peak; }),
            ("unison 6, 4×, comb −", 6, 4, s => { s.Routing = 0.3f; s.Filter[1].Type = FilterType.CombMinus; s.Filter[1].Reso = 0.8f; }),
            ("packed 1 + 3 + 5, LP 24, HP 24", 1, 2, s => { s.Osc[1].Unison = 3; s.Osc[2].Unison = 5; s.Filter[0].Type = FilterType.Lp24; s.Filter[1].Type = FilterType.Hp24; }),
            ("everything", 4, 2, Everything),
            ("everything, 4×", 2, 4, Everything),
            ("phase warps", 3, 2, s => { s.Osc[0].Warp1 = Warp.BendMinus; s.Osc[0].Amount1 = 0.7f; s.Osc[0].Warp2 = Warp.Squeeze; s.Osc[0].Amount2 = 0.5f;
                                         s.Osc[1].Warp1 = Warp.Mirror; s.Osc[1].Amount1 = 0.6f; s.Osc[1].Warp2 = Warp.Asym; s.Osc[1].Amount2 = 0.9f;
                                         s.Osc[2].Warp1 = Warp.Sync; s.Osc[2].Amount1 = 0.45f; }),
            ("the Analog engine: a pulse, a drifting saw stack filling its groups, a square", 8, 2, s => { foreach (var o in s.Osc) { o.Type = SlotType.Analog; o.Drift = 0.7f; }
                                         s.Osc[0].Shape = AnalogShape.Pulse; s.Osc[0].PulseWidth = 0.2f; s.Osc[0].Unison = 3; s.Osc[2].Shape = AnalogShape.Square; s.Osc[2].Unison = 1; }),
            ("AM, ring, FM in a ring of modulators, noise", 2, 2, s => { s.Osc[0].Warp1 = Warp.Am; s.Osc[0].Amount1 = 0.8f; s.Osc[0].Mod = ModSource.Noise;
                                         s.Osc[1].Warp1 = Warp.Ring; s.Osc[1].Amount1 = 0.5f; s.Osc[1].Mod = ModSource.OscC;
                                         s.Osc[2].Warp1 = Warp.Fm; s.Osc[2].Amount1 = 0.9f; s.Osc[2].Mod = ModSource.OscB; s.Noise.Level = 0.2f; s.Noise.ToF2 = 0.5f; }),
        };

        [Test]
        public void EveryLaneImplementationGivesTheSameBits()
        {
            // The kernels are one source run three ways: the scalar reference (no SIMD), two Vector128
            // halves (arm64's path) and one Vector256 (x64 with AVX2). Every sample of each path's render
            // the same, with notes starting, ending and stolen mid-render.
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
                        if (pos == 5120) { b.NoteOn(14, 90, 1f); b.NoteOn(2, 70, 0.5f); }
                    });
                    foreach (var v in y) Assert.That(float.IsFinite(v), Is.True, $"{name}, {kind}");
                    ulong h = Hash(y);
                    if (kind == Kinds[0]) first = h;
                    else Assert.That(h, Is.EqualTo(first), $"{name}: {kind} against the scalar reference");
                }
            }
            Measure.Report($"Dye Essence: lanes {string.Join(", ", Kinds)} render the same bits ({Paths.Length} settings)");
        }

        [TestCase(false, PinnedGateHash)]
        [TestCase(true, PinnedEverythingHash)]
        public void PinnedRendersAreTheSameOnEveryPlatform(bool everything, ulong pinned)
        {
            // Renders pinned by their checksums: the same numbers on Linux x64 (CI) and macOS arm64 (the
            // Mac runner), so the lanes, the tables (built by FFT with the portable maths), the warps and
            // the filters give the same bits everywhere (docs/specs/machines.md §4).
            var b = Gate(LaneKind.Auto, 8, 2, everything ? Everything : null);
            var y = Render(b, Rate, 256, pos =>
            {
                if (pos == 0) Chord(b);
                if (pos == Rate / 2) for (int n = 0; n < 16; n += 2) b.NoteOff(n);
            }, Rate / 2);
            ulong h = Hash(y);
            Measure.Report($"Dye Essence: the {(everything ? "everything" : "gate")} render's checksum {h:X16} ({b.Lanes} lanes, {RuntimeInfo()})");
            Assert.That(h, Is.EqualTo(pinned), "the render's bits");
        }

        const ulong PinnedGateHash = 0x852B6940C3C1FF68UL;
        const ulong PinnedEverythingHash = 0x56547DF10D4EE17AUL;

        // ------------------------------------------------------------------ CPU

        static (double Wall, double Cpu) Speed(NoteBank b, double seconds, int notes = 16)
        {
            var l = new float[256];
            var r = new float[256];
            Chord(b, notes);
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
        public void SixteenNotesOfThreeOscillatorsAtEightUnisonRunTwiceRealTime()
        {
            // G0's target, kept (docs/specs/dye-essence.md §5): 16 notes × 3 oscillators × 8 unison voices,
            // each note through a ladder and a state-variable filter in series, stereo, at Standard 2×: at
            // least twice real time on one core of the CI runner. Reported too: 16 unison, High 4×, one
            // voice per oscillator (packed into one group), the warped patch, and on x64 the Vector128 path
            // (what a CPU without AVX2 runs). "CPU" is the process's CPU time, which a busy runner disturbs
            // less than the wall clock.
            var kind = Avx2Lanes.Supported ? LaneKind.Avx2 : LaneKind.Pair;
            var (wall8, cpu8) = Speed(Gate(kind, 8), 10);
            Measure.Report($"Dye Essence, 16 notes × 3 osc × 8 unison + ladder and SVF, 2× ({kind}, {RuntimeInfo()}): {wall8:F2}× real time (CPU {cpu8:F2}×)");
            var (wall16, cpu16) = Speed(Gate(kind, 16), 6);
            Measure.Report($"Dye Essence, 16 notes × 3 osc × 16 unison + ladder and SVF, 2× ({kind}): {wall16:F2}× real time (CPU {cpu16:F2}×)");
            var (wall4x, cpu4x) = Speed(Gate(kind, 8, 4), 6);
            Measure.Report($"Dye Essence, 16 notes × 3 osc × 8 unison + ladder and SVF, 4× ({kind}): {wall4x:F2}× real time (CPU {cpu4x:F2}×)");
            var (wallU1, cpuU1) = Speed(Gate(kind, 1), 6);
            Measure.Report($"Dye Essence, 16 notes × 3 osc × 1 voice (one packed group) + ladder and SVF, 2× ({kind}): {wallU1:F2}× real time (CPU {cpuU1:F2}×)");
            var (wallW, cpuW) = Speed(Gate(kind, 8, 2, Everything), 6);
            Measure.Report($"Dye Essence, 16 notes, the warped patch (FM + bend, PM from the sub + window sync, sub, noise, ladder and comb), 2× ({kind}): {wallW:F2}× real time (CPU {cpuW:F2}×)");
            var (wallNo, cpuNo) = Speed(Gate(kind, 8, 2, s => { foreach (var o in s.Osc) o.Type = SlotType.Off; }), 6);
            Measure.Report($"Dye Essence, the same without oscillators (filters, envelopes, control, decimation), 2× ({kind}): {wallNo:F2}× real time (CPU {cpuNo:F2}×)");
            var (wall32, cpu32) = Speed(Gate(kind, 4), 6, 32);
            Measure.Report($"Dye Essence, 32 notes × 3 osc × 4 unison + ladder and SVF, 2× ({kind}): {wall32:F2}× real time (CPU {cpu32:F2}×)");
            if (kind == LaneKind.Avx2)
            {
                var (wallP, cpuP) = Speed(Gate(LaneKind.Pair, 8), 6);
                Measure.Report($"Dye Essence, 16 notes × 3 osc × 8 unison + ladder and SVF, 2× (Pair, x64 without AVX2): {wallP:F2}× real time (CPU {cpuP:F2}×)");
            }
            Assert.That(wall8, Is.GreaterThan(2), "16 × 3 × 8 unison with two filters at twice real time");
        }

        [Test]
        public void PlainOscillatorsSharePackedGroups()
        {
            // Lanes of plain oscillators share groups of eight (1 + 3 + 4 voices: one group, not three);
            // a warped oscillator has groups of its own.
            var b = Gate(LaneKind.Auto, 1, 2, s => { s.Osc[1].Unison = 3; s.Osc[2].Unison = 4; });
            b.NoteOn(0, 60, 1f);
            Render(b, 64);
            Assert.That(b.GroupsOf(0), Is.EqualTo(1), "1 + 3 + 4 plain voices");
            b.S.Osc[2].Unison = 5;
            Render(b, 64);
            Assert.That(b.GroupsOf(0), Is.EqualTo(2), "1 + 3 + 5 plain voices");
            b.S.Osc[0].Warp1 = Warp.Sync;
            b.S.Osc[0].Amount1 = 0.5f;
            Render(b, 64);
            Assert.That(b.GroupsOf(0), Is.EqualTo(2), "3 + 5 plain, 1 warped");
            b.S.Osc[1].Unison = 16;
            Render(b, 64);
            Assert.That(b.GroupsOf(0), Is.EqualTo(4), "16 + 5 plain, 1 warped");
            Measure.Report("Dye Essence: plain voices pack (1 + 3 + 4 in one group; 16 + 5 in three); a warped oscillator's voices in groups of their own");
        }

        // ------------------------------------------------------------------ quality

        /// <summary>One clean voice: the saw table, unison 1, filters off, full sustain, unit volume.</summary>
        static NoteBank Clean(int oversample, TableArena table = null)
        {
            var b = new NoteBank();
            b.S.Oversample = oversample;
            b.SetSampleRate(Rate);
            b.S.Osc[0].Table = table ?? SawTable.Value;
            b.S.Osc[0].Random = 0;
            b.S.Osc[1].Type = b.S.Osc[2].Type = SlotType.Off;
            b.S.Filter[0].Type = b.S.Filter[1].Type = FilterType.Off;
            b.S.Amp.A = 0.001f;
            b.S.Amp.S = 1;
            b.S.Volume = 1;
            b.S.VelAmp = 0;
            return b;
        }

        [TestCase(94, 2)]
        [TestCase(81, 2)]
        [TestCase(57, 2)]
        [TestCase(33, 2)]
        [TestCase(94, 4)]
        [TestCase(57, 4)]
        public void SawAliasFloor(int note, int oversample)
        {
            // Neo Wave's criterion (machines.md §5): the saw table at a note, everything that is not a
            // harmonic, 30 Hz – 20 kHz, under −80 dB (Neo Wave: −92 … −101 dB).
            var b = Clean(oversample);
            b.NoteOn(0, note, 1f);
            var y = Left(Render(b, 8192 + Measure.N), 8192, Measure.N);
            double floor = PolyTests.AliasFloorWindowed(y, 0, Measure.N, DspMath.NoteToHz(note));
            Measure.Report($"Dye Essence, saw at note {note} ({DspMath.NoteToHz(note):F0} Hz), {oversample}×: alias floor {floor:F1} dB");
            Assert.That(floor, Is.LessThan(-80));
        }

        [TestCase(45, 2)]
        [TestCase(69, 2)]
        [TestCase(93, 2)]
        [TestCase(105, 2)]
        [TestCase(93, 4)]
        public void KeepsTheAudibleBand(int note, int oversample)
        {
            // Neo Wave's criterion: the harmonic nearest 18 kHz within 1.5 dB of a saw's.
            var b = Clean(oversample);
            b.NoteOn(0, note, 1f);
            var y = Left(Render(b, 8192 + 32768), 8192, 32768);
            var mag = Hann(y, 32768);
            double f0 = DspMath.NoteToHz(note);
            int h = (int)(18000 / f0);
            double rel = Measure.Db(PeakNear(mag, 32768, h * f0) / PeakNear(mag, 32768, f0));
            double ideal = -20 * Math.Log10(h);
            Measure.Report($"Dye Essence, saw at {f0:F0} Hz, {oversample}×: harmonic {h} ({h * f0 / 1000:F1} kHz) at {rel:F1} dB (ideal {ideal:F1})");
            Assert.That(rel, Is.EqualTo(ideal).Within(1.5));
        }

        static double[] Hann(float[] y, int n, int from = 0)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = (float)(y[from + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n)));
            return Fft.Magnitudes(w, 0, n);
        }

        static double PeakNear(double[] mag, int n, double hz, int width = 3)
        {
            int k = (int)Math.Round(hz * n / Rate);
            double m = 0;
            for (int j = Math.Max(1, k - width); j <= Math.Min(mag.Length - 1, k + width); j++) m = Math.Max(m, mag[j]);
            return m;
        }

        /// <summary>Everything that is not a harmonic of <paramref name="f0"/> (30 Hz – 20 kHz) against the strongest harmonic, dB (Blackman–Harris).</summary>
        static double AliasAgainstStrongest(float[] x, double f0)
        {
            int n = x.Length;
            var w = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n;
                w[i] = (float)(x[i] * (0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t)));
            }
            var mag = Fft.Magnitudes(w, 0, n);
            double binHz = (double)Rate / n, strongest = 1e-30, worst = 1e-30;
            for (int k = (int)(30 / binHz); k < Math.Min(mag.Length, (int)(20000 / binHz)); k++)
            {
                double f = k * binHz, h = f / f0;
                if (Math.Abs(h - Math.Round(h)) * f0 < 6 * binHz) strongest = Math.Max(strongest, mag[k]);
                else worst = Math.Max(worst, mag[k]);
            }
            return 20 * Math.Log10(worst / strongest);
        }

        static readonly Warp[] AllWarps = Enum.GetValues<Warp>().Where(w => w != Warp.None).ToArray();

        /// <summary>A warped sine (or saw) at a note, osc B a silent sine an octave up as the modulator.</summary>
        static double WarpAlias(Warp warp, float amount, int note, int oversample, TableArena carrier)
        {
            var b = Clean(oversample, carrier);
            var s = b.S;
            s.Osc[0].Warp1 = warp;
            s.Osc[0].Amount1 = amount;
            s.Osc[0].Mod = ModSource.OscB;
            s.Osc[1].Type = SlotType.Wavetable;
            s.Osc[1].Table = SineTable.Value;
            s.Osc[1].Transpose = 12;
            s.Osc[1].Level = 0;
            s.Osc[1].Random = 0;
            b.NoteOn(0, note, 1f);
            var y = Left(Render(b, 8192 + Measure.N), 8192, Measure.N);
            return AliasAgainstStrongest(y, DspMath.NoteToHz(note));
        }

        [Test]
        public void WarpAliasFloorsAtStandardAndHigh()
        {
            // Every warp at 70 % on a sine and on a saw at A5 (880 Hz), the modulator a sine an octave up
            // (every product a harmonic of the note, so anything else is an alias): the alias floor at
            // Standard 2× and High 4×, against the strongest harmonic. Targets: the smooth warps (asym,
            // mirror's fold, window sync, FM, PM, AM, ring) under −80 dB at Standard; the edged ones
            // (bend, squeeze, hard sync: their read turns a corner or jumps at the wrap) at least 5 dB
            // lower at High than at Standard; High never worse than Standard by more than 1 dB.
            var lines = new List<string>();
            var fails = new List<string>();
            foreach (var (carrier, tname) in new[] { (SineTable.Value, "sine"), (SawTable.Value, "saw") })
                foreach (var w in AllWarps)
                {
                    double at2 = WarpAlias(w, 0.7f, 81, 2, carrier), at4 = WarpAlias(w, 0.7f, 81, 4, carrier);
                    lines.Add($"{w} on a {tname}: 2× {at2:F1} dB, 4× {at4:F1} dB");
                    if (at4 >= at2 + 1) fails.Add($"{w} on a {tname}: 4× worse than 2×");
                    if (w is Warp.BendPlus or Warp.BendMinus or Warp.Squeeze or Warp.Sync)
                    {
                        if (at4 > at2 - 5) fails.Add($"{w} on a {tname}: High only {at2 - at4:F1} dB lower");
                    }
                    else if (at2 >= -80) fails.Add($"{w} on a {tname} at 2×: {at2:F1} dB");
                }
            Measure.Report("Dye Essence, warp alias floors at 70 %, A5 (880 Hz), modulator an octave up: " + string.Join("; ", lines));
            Assert.That(fails, Is.Empty);
        }

        /// <summary>One voice of the Analog engine, filters off.</summary>
        static NoteBank Analog(AnalogShape shape, float width = 0.5f, float drift = 0f)
        {
            var b = Clean(2);
            var o = b.S.Osc[0];
            o.Type = SlotType.Analog;
            o.Shape = shape;
            o.PulseWidth = width;
            o.Drift = drift;
            b.Analog.Basic = TableBank.GetNow(0);
            b.Analog.Pulse = TableBank.GetNow(1);
            return b;
        }

        [Test]
        public void TheAnalogEngineIsCleanInTuneAndPulseWide()
        {
            // The Analog engine reads the built-in tables' exact frames: the saw's and square's alias
            // floors under −80 dB at 880 Hz and 1.9 kHz; a pulse of 25 % is high a quarter of each cycle
            // (within 2 %); a voice drifts by at most 12 cents at 100 % and not at all at 0.
            var lines = new List<string>();
            foreach (var shape in new[] { AnalogShape.Saw, AnalogShape.Square, AnalogShape.Triangle })
                foreach (int note in new[] { 81, 94 })
                {
                    var b = Analog(shape);
                    b.NoteOn(0, note, 1f);
                    var y = Left(Render(b, 8192 + Measure.N), 8192, Measure.N);
                    double floor = PolyTests.AliasFloorWindowed(y, 0, Measure.N, DspMath.NoteToHz(note));
                    lines.Add($"{shape} at {DspMath.NoteToHz(note):F0} Hz {floor:F1} dB");
                    Assert.That(floor, Is.LessThan(-80), $"{shape} at note {note}");
                }
            foreach (float width in new[] { 0.25f, 0.1f })
            {
                var p = Analog(AnalogShape.Pulse, width);
                p.NoteOn(0, 45, 1f);   // 110 Hz: some 436 samples a cycle
                var y = Left(Render(p, 8192 + 48000), 8192, 48000);
                double high = y.Count(v => v > 0) / (double)y.Length;
                lines.Add($"pulse {width * 100:F0} % high {high * 100:F1} % of the cycle");
                Assert.That(high, Is.EqualTo(width).Within(0.02), $"pulse {width}");
            }
            foreach (float drift in new[] { 0f, 1f })
            {
                var d = Analog(AnalogShape.Sine, drift: drift);
                d.NoteOn(0, 69, 1f);
                var y = Left(Render(d, 4096 + Measure.N), 4096, Measure.N);
                double cents = Measure.Cents(PeakHz(y, 440), 440);
                lines.Add($"drift {drift * 100:F0} %: {cents:+0.00;-0.00} ct");
                if (drift == 0) Assert.That(Math.Abs(cents), Is.LessThan(0.05));
                else Assert.That(Math.Abs(cents), Is.InRange(0.05, AnalogEngine.MaxDrift + 0.05));
            }
            Measure.Report("Dye Essence, the Analog engine: " + string.Join("; ", lines));
        }

        static double PeakHz(float[] y, double near, int width = 8)
        {
            int n = y.Length;
            var w = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n;
                w[i] = (float)(y[i] * (0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t)));
            }
            var mag = Fft.Magnitudes(w, 0, n);
            double binHz = (double)Rate / n;
            int k0 = (int)Math.Round(near / binHz), best = k0;
            for (int k = k0 - width; k <= k0 + width; k++) if (mag[k] > mag[best]) best = k;
            double a = Math.Log(mag[best - 1] + 1e-30), c = Math.Log(mag[best] + 1e-30), d = Math.Log(mag[best + 1] + 1e-30);
            return (best + 0.5 * (a - d) / (a - 2 * c + d)) * binHz;
        }

        [Test]
        public void UnisonVoicesAndTheSubSitWhereTheySetThem()
        {
            // Two voices detuned ±50 cents, each a sine: the peaks at 440 × 2^(±1/24), within 0.05 cent;
            // a single voice on the note; the sub (sine, one octave down) at 220 Hz.
            foreach (int u in new[] { 1, 2 })
            {
                var b = Clean(2, SineTable.Value);
                b.S.Osc[0].Unison = u;
                b.S.Osc[0].Detune = 50;
                b.NoteOn(0, 69, 1f);
                var y = Left(Render(b, 4096 + Measure.N), 4096, Measure.N);
                var expect = u == 1 ? new[] { 440.0 } : new[] { 440 * Math.Pow(2, -1 / 24.0), 440 * Math.Pow(2, 1 / 24.0) };
                foreach (var f in expect)
                {
                    double cents = Measure.Cents(PeakHz(y, f), f);
                    Measure.Report($"Dye Essence, unison {u}, detune ±50 ct: a voice at {f:F3} Hz, {cents:+0.0000;-0.0000} cent");
                    Assert.That(Math.Abs(cents), Is.LessThan(0.05));
                }
            }
            var sb = Clean(2);
            sb.S.Osc[0].Type = SlotType.Off;
            sb.S.Sub.Level = 1;
            sb.S.SubOctave = -1;
            sb.NoteOn(0, 69, 1f);
            var sy = Left(Render(sb, 4096 + Measure.N), 4096, Measure.N);
            double sc = Measure.Cents(PeakHz(sy, 220), 220);
            Measure.Report($"Dye Essence, the sub one octave below A4: {sc:+0.0000;-0.0000} cent from 220 Hz");
            Assert.That(Math.Abs(sc), Is.LessThan(0.05));
        }

        /// <summary>A sine through filter 1 alone at a frequency, against the same with filter 1 off, dB.</summary>
        static double FilterGain(FilterType type, float cutoff, float reso, double hz)
        {
            double Level(FilterType t)
            {
                var b = Clean(2, SineTable.Value);
                b.S.Osc[0].Level = 0.05f;   // small: the ladder's tanh stays linear
                b.S.Filter[0].Type = t;
                b.S.Filter[0].Cutoff = cutoff;
                b.S.Filter[0].Reso = reso;
                b.NoteOn(0, (float)(69 + 12 * Math.Log2(hz / 440)), 1f);
                var y = Left(Render(b, 16384 + 16384), 16384, 16384);
                return Measure.Rms(y, 0, y.Length);
            }
            return Measure.Db(Level(type) / Level(FilterType.Off));
        }

        [Test]
        public void FiltersMatchTheirDesign()
        {
            // At the cutoff (prewarped, so exact at the voice rate): the ladder 24 at −12 dB (four TPT
            // poles at −3 dB each), the ladder 12 at −6 dB, the SVF low- and highpass at Q = 0.5 −6 dB
            // and two of them −12 dB, the band-pass at its peak 0 dB, within 0.3 dB; an octave above, the
            // 24 dB/oct slopes below −20 dB; the notch below −30 dB at its centre.
            var cases = new (FilterType Type, double Hz, double Expect, double Tolerance)[]
            {
                (FilterType.Ladder24, 1000, -12.04, 0.3), (FilterType.Ladder12, 1000, -6.02, 0.3),
                (FilterType.Lp12, 1000, -6.02, 0.3), (FilterType.Lp24, 1000, -12.04, 0.3),
                (FilterType.Hp12, 1000, -6.02, 0.3), (FilterType.Bp12, 1000, 0, 0.3), (FilterType.Hp24, 1000, -12.04, 0.3),
            };
            var lines = new List<string>();
            foreach (var (type, hz, expect, tol) in cases)
            {
                double g = FilterGain(type, 1000, 0, hz);
                lines.Add($"{type} {g:F2} dB (design {expect:F2})");
                Assert.That(g, Is.EqualTo(expect).Within(tol), type.ToString());
            }
            foreach (var type in new[] { FilterType.Ladder24, FilterType.Lp24 })
            {
                double g = FilterGain(type, 1000, 0, 2000);
                lines.Add($"{type} an octave up {g:F1} dB");
                Assert.That(g, Is.LessThan(-20), type + " an octave up");
            }
            double notch = FilterGain(FilterType.Notch, 1000, 0, 1000);
            lines.Add($"Notch at its centre {notch:F1} dB");
            Assert.That(notch, Is.LessThan(-30));
            Measure.Report("Dye Essence filters at a 1 kHz cutoff, resonance 0, a sine at the cutoff: " + string.Join("; ", lines));
        }

        /// <summary>A sine at <paramref name="hz"/> through a comb at 220 Hz, 99 % feedback: its level once steady.</summary>
        static double CombLevel(FilterType type, double hz)
        {
            var b = Clean(2, SineTable.Value);
            b.S.Osc[0].Level = 0.05f;
            b.S.Filter[0].Type = type;
            b.S.Filter[0].Cutoff = 220;
            b.S.Filter[0].Reso = 1;
            b.NoteOn(0, (float)(69 + 12 * Math.Log2(hz / 440)), 1f);
            var y = Left(Render(b, 3 * Rate), 5 * Rate / 2, Rate / 2);
            return Measure.Rms(y, 0, y.Length);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void TheCombRingsAtItsPitch(bool positive)
        {
            // The comb at 220 Hz, 99 % feedback (its peaks some 5 cents wide), a sine played through it:
            // positive feedback peaks at 220 Hz and its multiples, negative at the odd multiples of 110 Hz.
            // For each of the first three, the level at the peak above the level 2 cents either side (so
            // the peak is within 2 cents of where it belongs), and the peak's gain.
            var type = positive ? FilterType.CombPlus : FilterType.CombMinus;
            var lines = new List<string>();
            for (int k = 1; k <= 3; k++)
            {
                double f = positive ? 220.0 * k : 110.0 * (2 * k - 1);
                double at = CombLevel(type, f), below = CombLevel(type, f * Math.Pow(2, -2 / 1200.0)), above = CombLevel(type, f * Math.Pow(2, 2 / 1200.0));
                lines.Add($"{f:F0} Hz: {Measure.Db(at / 0.05 * Math.Sqrt(2)):+0.0;-0.0} dB, ±2 ct {Measure.Db(below / at):F2} / {Measure.Db(above / at):F2} dB");
                Assert.That(at, Is.GreaterThan(below).And.GreaterThan(above), $"{type}: the peak near {f:F0} Hz");
            }
            Measure.Report($"Dye Essence {type} at 220 Hz, 99 % feedback: " + string.Join("; ", lines));
        }

        /// <summary>Energy above 2 kHz relative to a 220 Hz sine, dB (Hann-windowed spectrum of the last 65536 samples).</summary>
        static double ZipperDb(float[] y)
        {
            int n = 1 << 16;
            var mag = Hann(y, n, y.Length - n);
            int k220 = (int)Math.Round(220.0 * n / Rate), k2k = (int)Math.Round(2000.0 * n / Rate);
            double sine = 0, high = 0;
            for (int k = k220 - 40; k <= k220 + 40; k++) sine += mag[k] * mag[k];
            for (int k = k2k; k < mag.Length; k++) high += mag[k] * mag[k];
            return 10 * Math.Log10(high / sine);
        }

        [Test]
        public void MovingCutoffAndLevelMakeNoZipperNoise()
        {
            // A 220 Hz sine through the SVF lowpass, its cutoff stepped every block (256 samples) between
            // 300 Hz and 3 kHz, then the oscillator's level stepped between 20 % and 100 %: every control
            // value ramps across a tick (16 samples), so nothing reaches above 2 kHz (under −80 dB).
            foreach (var what in new[] { "cutoff", "level" })
            {
                var b = Clean(2, SineTable.Value);
                b.S.Osc[0].Level = 0.5f;
                b.S.Filter[0].Type = FilterType.Lp12;
                b.S.Filter[0].Cutoff = 3000;
                b.NoteOn(0, (float)(69 + 12 * Math.Log2(220 / 440.0)), 1f);
                var y = Left(Render(b, 8192 + (1 << 16), 256, pos =>
                {
                    double t = Math.Sin(2 * Math.PI * pos / 256 / 37.0);
                    if (what == "cutoff") b.S.Filter[0].Cutoff = (float)(300 * Math.Pow(10, 0.5 + 0.5 * t));
                    else b.S.Osc[0].Level = (float)(0.6 + 0.4 * t);
                }), 0, 8192 + (1 << 16));
                double z = ZipperDb(y);
                Measure.Report($"Dye Essence, {what} moved every block on a 220 Hz sine: energy above 2 kHz {z:F1} dB re the sine");
                Assert.That(z, Is.LessThan(-80), what);
            }
        }

        [Test]
        public void EnvelopeTimesAreTheirSettings()
        {
            // The amp envelope in closed form per tick: a 20 ms attack peaks at 20 ms (within a tick,
            // 0.33 ms, plus a cycle of the measuring sine); a 0.5 s release is 60 dB down 0.5 s after the
            // note off (within 0.5 dB).
            var b = Clean(2, SineTable.Value);
            b.S.Amp.A = 0.02f;
            b.S.Amp.D = 1f;
            b.S.Amp.S = 0.5f;
            b.S.Amp.R = 0.5f;
            int off = Rate / 2;
            b.NoteOn(0, 93, 1f);   // 1760 Hz: a peak every 0.57 ms
            var y = Left(Render(b, Rate * 3 / 2, 256, pos => { if (pos == off) b.NoteOff(0); }, off), 0, Rate * 3 / 2);
            int best = 0;
            for (int i = 0; i < Rate / 10; i++) if (Math.Abs(y[i]) > Math.Abs(y[best])) best = i;
            double peakMs = 1000.0 * best / Rate;
            double atOff = Measure.Peak(y, off - 480, 480), after = Measure.Peak(y, off + Rate / 2, 120);
            double down = Measure.Db(after / atOff);
            Measure.Report($"Dye Essence amp envelope: a 20 ms attack peaks at {peakMs:F2} ms; a 0.5 s release is {down:F1} dB down at 0.5 s");
            Assert.That(peakMs, Is.EqualTo(20).Within(1.0));
            Assert.That(down, Is.EqualTo(-60).Within(0.5));
        }

        [Test]
        public void StealsFadeInsteadOfClicking()
        {
            // A sustained 110 Hz sine; the slot is given a new note: the old one fades out over 3 ms
            // before the new one starts, so no sample-to-sample step is larger than the sines' own
            // steepest (a cut would step by up to the whole amplitude).
            var b = Clean(2, SineTable.Value);
            b.S.Osc[0].Level = 0.5f;
            b.S.Amp.A = 0.01f;   // an attack gentler than the sine's own slope: what is left is the steal
            b.NoteOn(0, 45, 1f);
            int at = Rate / 4 + 37;
            var y = Left(Render(b, Rate / 2, 256, pos => { if (pos == at) b.NoteOn(0, 57, 1f); }, at), 0, Rate / 2);
            double Steepest(int from, int to)
            {
                double m = 0;
                for (int i = from + 1; i < to; i++) m = Math.Max(m, Math.Abs(y[i] - y[i - 1]));
                return m;
            }
            double steady = Steepest(Rate / 8, at), steal = Steepest(at, at + Rate / 50), after = Steepest(at + Rate / 20, Rate / 2);
            Measure.Report($"Dye Essence steal: the steepest step {steal:F4} round the steal against {steady:F4} (old note) and {after:F4} (new note)");
            Assert.That(steal, Is.LessThan(1.05 * Math.Max(steady, after)));
        }

        [Test]
        public void GlideReachesTheNoteInItsTime()
        {
            // Glide from A3 to A4 in 100 ms: after 100 ms the pitch is within 5 % of the interval (the
            // exponential's 95 %), after 300 ms within a cent.
            var b = Clean(2, SineTable.Value);
            b.S.Glide = 0.1f;
            b.NoteOn(0, 69, 1f, 57);
            var y = Left(Render(b, Rate / 2), 0, Rate / 2);
            double Pitch(int from)
            {
                // Zero crossings over 20 ms.
                int first = -1, last = -1, count = 0;
                for (int i = from + 1; i < from + Rate / 50; i++)
                    if (y[i - 1] < 0 && y[i] >= 0)
                    {
                        if (first < 0) first = i;
                        last = i;
                        count++;
                    }
                double period = (last - first) / (double)(count - 1);
                return 69 + 12 * Math.Log2(Rate / period / 440);
            }
            double at100 = Pitch(Rate / 10 - Rate / 100), at300 = Pitch(Rate * 3 / 10);
            Measure.Report($"Dye Essence glide A3 → A4 in 0.1 s: {at100 - 57:F2} st of 12 at 0.1 s, {(at300 - 69) * 100:+0.0;-0.0} ct from A4 at 0.3 s");
            Assert.That(at100 - 57, Is.GreaterThan(11.0));
            Assert.That(Math.Abs(at300 - 69), Is.LessThan(0.01));
        }

        // ------------------------------------------------------------------ robustness

        [Test]
        public void BlockSizesGiveTheSameSamples()
        {
            // The control grid runs across blocks and a note started mid-tick gets its values for the rest
            // of it, so blocks of 256, 117 and 16 give the same samples, notes starting, ending and stolen
            // included, the warped patch with the comb included.
            foreach (bool everything in new[] { false, true })
            {
                ulong first = 0;
                foreach (int block in new[] { 256, 117, 16 })
                {
                    var b = Gate(LaneKind.Auto, 8, 2, everything ? Everything : null);
                    var y = Render(b, 12000, block, pos =>
                    {
                        if (pos == 0) Chord(b, 6);
                        if (pos == 4000) { b.NoteOff(1); b.NoteOff(4); }
                        if (pos == 7003) { b.NoteOn(9, 80, 0.9f); b.NoteOn(2, 50, 0.7f); }   // mid-tick, and a steal
                    }, 4000, 7003);
                    ulong h = Hash(y);
                    if (block == 256) first = h;
                    else Assert.That(h, Is.EqualTo(first), $"{(everything ? "everything" : "gate")}: blocks of {block}");
                }
            }
        }

        [Test]
        public void EveryNoteEndsInSilence()
        {
            // Resonant filters, released: the bank goes quiet (Process says so) and leaves no subnormals.
            var b = Gate(LaneKind.Auto, 8, 2, s => { s.Filter[0].Reso = 1; s.Filter[1].Reso = 1; s.Amp.R = 0.05f; });
            var l = new float[256];
            var r = new float[256];
            Chord(b);
            for (int i = 0; i < 100; i++) b.Process(l, r, 256);
            for (int n = 0; n < 16; n++) b.NoteOff(n);
            bool sounding = true;
            for (int i = 0; i < 400 && sounding; i++) sounding = b.Process(l, r, 256);
            Assert.That(sounding, Is.False, "silent after the release");
            Assert.That(b.ActiveNotes, Is.EqualTo(0));
            foreach (var v in l.Concat(r)) Assert.That(float.IsSubnormal(v), Is.False, "no subnormals");

            // 100 ms later it rests: silence for almost nothing, and a new note plays at once.
            for (int i = 0; i < 40; i++) b.Process(l, r, 256);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++) b.Process(l, r, 256);
            double restUs = sw.Elapsed.TotalMilliseconds * 1000 / 2000;
            Assert.That(l.Concat(r).All(v => v == 0), Is.True, "silence while resting");
            b.NoteOn(0, 60, 1f);
            bool sounds = false;
            for (int i = 0; i < 4 && !sounds; i++) sounds = b.Process(l, r, 256) && l.Any(v => v != 0);
            Measure.Report($"Dye Essence resting: {restUs:F1} µs a block of 256 ({restUs / (256e6 / Rate) * 100:F2} % of real time); a note after the rest sounds at once");
            Assert.That(sounds, Is.True, "a note after the rest");
        }

        static DyeEngine Engine(ParamValues v)
        {
            for (int i = 0; i < TableBank.Count; i++) TableBank.GetNow(i);
            var e = new DyeEngine(v);
            e.SetSampleRate(Rate);
            return e;
        }

        [Test]
        public void EveryParameterAtItsExtremesStaysFinite()
        {
            // Each parameter alone at its minimum and its maximum, a chord of four notes held then
            // released: every sample finite and under +12 dBFS.
            var table = Params.Table;
            var l = new float[256];
            var r = new float[256];
            double loudest = 0;
            string where = "";
            for (int p = 0; p < table.Length; p++)
                foreach (int raw in new[] { table[p].Min, table[p].Max })
                {
                    var v = new ParamValues(table);
                    v.Set(p, raw);
                    var e = Engine(v);
                    foreach (int k in new[] { 36, 60, 67, 96 }) e.NoteOn(k, k, 1f);
                    for (int i = 0; i < 40; i++)
                    {
                        if (i == 30) foreach (int k in new[] { 36, 60, 67, 96 }) e.NoteOff(k);
                        e.Process(l, r, 256);
                        for (int j = 0; j < 256; j++)
                        {
                            Assert.That(float.IsFinite(l[j]) && float.IsFinite(r[j]), Is.True, $"{table[p].Name} = {raw}");
                            double a = Math.Max(Math.Abs(l[j]), Math.Abs(r[j]));
                            if (a > loudest) { loudest = a; where = $"{table[p].Name} = {table[p].Describe(raw)}"; }
                        }
                    }
                }
            Measure.Report($"Dye Essence: every parameter at its minimum and maximum, a held chord: finite, loudest {Measure.Db(loudest):F1} dBFS ({where})");
            Assert.That(loudest, Is.LessThan(4));
        }

        [Test]
        public void TheAudioThreadAllocatesNothing()
        {
            // 2000 blocks through the engine with notes starting, ending and stolen, parameters changing
            // (unison, positions, warps, filter models, quality), every lane implementation the CPU has: 0 bytes.
            foreach (var kind in Kinds.Where(k => k != LaneKind.Scalar))
            {
                var v = new ParamValues(Params.Table);
                for (int i = 0; i < TableBank.Count; i++) TableBank.GetNow(i);
                var e = new DyeEngine(v, kind);
                e.SetSampleRate(Rate);
                var l = new float[256];
                var r = new float[256];
                v.Set(P.AUnison, 8);
                v.Set(P.BType, 1);
                v.Set(P.BWarp1, (int)Warp.Fm);
                v.Set(P.BWarp1Amt, 500);
                v.Set(P.CType, 1);
                v.Set(P.SubLevel, 500);
                for (int k = 0; k < 8; k++) e.NoteOn(k, 40 + 5 * k, 0.8f);
                for (int i = 0; i < 20; i++) e.Process(l, r, 256);
                void Block(int i)
                {
                    if (i % 50 == 0) e.NoteOn(i / 50 % 24, 40 + i % 40, 0.8f);
                    if (i % 50 == 25) e.NoteOff((i / 50 + 7) % 24);
                    v.Set(P.BUnison, 1 + i / 100 % 16);
                    v.Set(P.CPos, i % 97 * 10);
                    v.Set(P.CWarp1, i / 150 % 12);
                    v.Set(P.F1Type, i / 200 % 13);
                    v.Set(P.Quality, i / 500 % 2);
                    e.Process(l, r, 1 + i % 256);
                }
                var (bytes, why) = AllocationCheck.Run(2000, Block);
                Measure.Report($"Dye Essence ({kind}): {bytes} bytes allocated over 2000 blocks with notes, unison, positions, warps, filter models and quality moving");
                Assert.That(bytes, Is.EqualTo(0), why);
            }
        }
    }
}
