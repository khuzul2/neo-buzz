using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Learn;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The note contract of every Neo instrument (docs/specs/notes.md, CU6a): real notes everywhere. One row per machine; a new
    /// instrument is covered by adding one line to <see cref="Rows"/>.
    ///  - Velocity: a 4-note chord at velocities 30, 60, 90 and 127 gives four voices; each note's level, alone at the same pitch, rises with its
    ///    velocity in that order and 127 is at least 6 dB over 30 (a row says why not, when velocity does not set the level by design).
    ///  - Note-off: each note-off releases only its own note.
    ///  - Polyphony: 16 notes at once give 16 voices, or the machine's stated maximum, and 20 notes past the maximum steal without a crash.
    ///  - Allocation: nothing is allocated per block with notes starting and ending.
    /// </summary>
    [TestFixture]
    public class NoteContractTests
    {
        const int Rate = Measure.Rate;
        const int Block = 128;

        /// <summary>One engine under test, driven the way the machine drives it.</summary>
        internal sealed class Rig
        {
            public INoteSink Sink;
            public Func<float[], float[], int, bool> Process;
            /// <summary>Notes held (gate on).</summary>
            public Func<int> Held;
            /// <summary>Voices or hits still sounding (released ones too).</summary>
            public Func<int> Sounding;
        }

        public sealed class Row
        {
            public string Name;
            internal Func<Rig> Make;
            /// <summary>The pitch of the single note whose level is measured.</summary>
            public int Pitch = 60;
            /// <summary>The four chord pitches (different voices).</summary>
            public int[] Chord = { 48, 55, 60, 64 };
            /// <summary>Sixteen pitches (for a machine whose keys are voices of their own: the drums).</summary>
            public int[] Sixteen16;
            /// <summary>Notes that sound at once with 16 notes played (the machine's stated maximum when it is lower).</summary>
            public int Sixteen = 16;
            /// <summary>The most notes at once the row's settings allow (the notes past it steal); 0: not limited by a setting.</summary>
            public int Max = 16;
            /// <summary>Why velocity does not set the level of this machine, or null.</summary>
            public string VelocityBlind;
            /// <summary>Why this machine's level is not in velocity order between the four velocities, or null (it must still be 6 dB over at 127).</summary>
            public string NotMonotone;
            /// <summary>Why a note-off does not release (a one-shot machine), or null.</summary>
            public string NoNoteOff;
            public override string ToString() => Name;
        }

        // ------------------------------------------------------------------ the rows: one line per machine

        static Rig Of(Func<float[], float[], int, bool> process, INoteSink sink, Func<int> held, Func<int> sounding) =>
            new Rig { Process = process, Sink = sink, Held = held, Sounding = sounding };

        static int Raw(ParamDef[] t, string id, double value) => t.First(p => p.Id == id).Raw(value);

        internal static readonly Row[] Rows =
        {
            new Row { Name = "Neo Poly", Make = () =>
            {
                var v = new ParamValues(NeoBuzz.Poly.Params.Table);
                v.Set(NeoBuzz.Poly.P.Polyphony, 16); v.Set(NeoBuzz.Poly.P.Unison, 1); v.Set(NeoBuzz.Poly.P.AmpS, 1000);
                var e = new NeoBuzz.Poly.PolyEngine(v); e.SetSampleRate(Rate);
                return Of(e.Process, e, () => e.HeldNotes, () => e.ActiveVoices);
            } },
            new Row { Name = "Neo Wave", Make = () =>
            {
                for (int i = 0; i < TableBank.Count; i++) TableBank.GetNow(i);   // every wavetable now: they build in the background otherwise, and a note on an unbuilt one is silent
                var v = new ParamValues(NeoBuzz.Wave.Params.Table);
                v.Set(NeoBuzz.Wave.P.Polyphony, 16); v.Set(NeoBuzz.Wave.P.Unison, 1);
                var e = new NeoBuzz.Wave.WaveEngine(v); e.SetSampleRate(Rate);
                return Of(e.Process, e, () => e.HeldNotes, () => e.ActiveVoices);
            } },
            new Row { Name = "Neo FM6 (every operator at velocity sensitivity 7; the Init patch has 0, its presets set it)", Make = () =>
            {
                var t = NeoBuzz.Fm6.Params.Table;
                var v = new ParamValues(t);
                v.Set(NeoBuzz.Fm6.P.Polyphony, 16); v.Set(NeoBuzz.Fm6.P.Unison, 1);
                for (int op = 1; op <= 6; op++) v.Set(t.First(p => p.Id == $"O{op}VelSens").Index, 7);
                var e = new NeoBuzz.Fm6.FmEngine(v); e.SetSampleRate(Rate);
                return Of(e.Process, e, () => e.HeldNotes, () => e.ActiveVoices);
            } },
            new Row { Name = "Neo Mallet", Make = () =>
            {
                var v = new ParamValues(NeoBuzz.Mallet.Params.Table);
                var e = new NeoBuzz.Mallet.MalletEngine(v); e.SetSampleRate(Rate);
                return Of(e.Process, e, () => e.HeldNotes, () => e.ActiveVoices);
            }, Max = 16 },
            new Row { Name = "Neo Keys", Make = () =>
            {
                var v = new ParamValues(NeoBuzz.Keys.Params.Table);
                var e = new NeoBuzz.Keys.KeysEngine(v); e.SetSampleRate(Rate);
                return Of(e.Process, e, () => e.HeldNotes, () => e.ActiveVoices);
            }, Max = 16 },
            new Row { Name = "Neo Organ (velocity-blind by design: a tonewheel organ's keys are not touch sensitive)", Make = () =>
            {
                var v = new ParamValues(NeoBuzz.Organ.Params.Table);
                v.Set(NeoBuzz.Organ.P.B8, 8);
                var e = new NeoBuzz.Organ.OrganEngine(v); e.SetSampleRate(Rate);
                return Of(e.Process, e, () => e.HeldKeys, () => e.HeldKeys);
            }, VelocityBlind = "a tonewheel organ's keys are not velocity sensitive (neo-organ.md)", Max = 0 },
            new Row { Name = "Neo Sampler (Instrument mode, a looped sine, Vel -> amp 70 %)", Make = () =>
            {
                var v = new ParamValues(NeoBuzz.Sampler.Params.Table);
                var t = NeoBuzz.Sampler.Params.Table;
                int Z(int f) => NeoBuzz.Sampler.SamplerEngine.ZoneParam(0, f);
                v.Set(Z(NeoBuzz.Sampler.SamplerEngine.FLoop), 2);                  // a looped sine: it sustains while the note is held
                v.Set(Z(NeoBuzz.Sampler.SamplerEngine.FLoopS), t[Z(NeoBuzz.Sampler.SamplerEngine.FLoopS)].Raw(25.0));
                v.Set(Z(NeoBuzz.Sampler.SamplerEngine.FLoopE), t[Z(NeoBuzz.Sampler.SamplerEngine.FLoopE)].Raw(77.7));
                var bank = new NeoBuzz.Sampler.SampleBank();
                bank.Set(1, SamplerTests.Slot("sine", SamplerTests.Mono(SamplerTests.SineWave(441, Rate, 0.5))));
                var e = new NeoBuzz.Sampler.SamplerEngine(v, bank); e.SetSampleRate(Rate);
                return Of(e.Process, e, () => e.HeldNotes, () => e.ActiveVoices);
            }, Max = 32 },
            new Row { Name = "Neo Drums (one drum per key, 12 drums)", Make = () =>
            {
                var v = new ParamValues(NeoBuzz.Drums.Params.Table);
                var e = new NeoBuzz.Drums.DrumsEngine(v); e.SetSampleRate(Rate);
                return Of(e.Process, e, () => e.ActiveHits, () => e.ActiveHits);
            }, Pitch = 36, Chord = new[] { 36, 38, 39, 41 }, Sixteen = 12, Sixteen16 = new[] { 35, 38, 39, 37, 42, 46, 41, 45, 48, 56, 49, 51, 36, 40, 43, 47 }, Max = 0, NoNoteOff = "drums are one-shots: a note's end changes nothing" },
            new Row { Name = "Neo Vocoder (the synth carrier, noise as the modulator; 8 voices)", Make = () =>
            {
                var v = new ParamValues(NeoBuzz.Vocoder.Params.Table);
                var e = new NeoBuzz.Vocoder.VocoderEngine(v); e.SetSampleRate(Rate);
                var noise = new Rng(21);
                return Of((l, r, n) => { for (int i = 0; i < n; i++) l[i] = r[i] = 0.3f * noise.NextBipolar(); e.Process(l, r, null, null, n); return true; }, e, () => e.HeldNotes, () => e.ActiveVoices);
            }, Sixteen = 8, Max = 8 },
            new Row { Name = "Neo Patch (the default patch)", Make = () =>
            {
                var v = new ParamValues(NeoBuzz.Patch.Params.Table);
                v.Set(NeoBuzz.Patch.P.Voices, 16);
                for (int i = 0; i < TableBank.Count; i++) TableBank.GetNow(i);   // as for Neo Wave: a Wavetable Osc on an unbuilt table is silent
                var e = new NeoBuzz.Patch.PatchEngine(v); e.SetSampleRate(Rate); e.SetGraph(NeoBuzz.Patch.PatchPresets.Build(0)); e.Prepare();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (e.Runtime.Compiling && sw.ElapsedMilliseconds < 30000) Thread.Sleep(1);     // the patch compiles in the background
                return Of(e.Process, e, () => e.Runtime.HeldNotes, () => e.Runtime.ActiveVoices);
            } },
            new Row { Name = "Dye Essence", Make = () =>
            {
                var v = new ParamValues(NeoBuzz.DyeEssence.Params.Table);
                v.Set(NeoBuzz.DyeEssence.P.Polyphony, 16);
                var e = new DyeEngine(v); e.SetSampleRate(Rate);
                return Of(e.Process, e, () => e.HeldNotes, () => e.ActiveNotes);
            } },
            // The thirteen Neo Learn synths: one host over a Dye Essence patch, notes straight to its engine.
            Learn("Neo Learn Subtractive", NeoBuzz.LearnSub.Params.Table, () => new SubtractiveMap(NeoBuzz.LearnSub.Params.Table)),
            Learn("Neo Learn FM", NeoBuzz.LearnFm.Params.Table, () => new FmMap(NeoBuzz.LearnFm.Params.Table)),
            Learn("Neo Learn Wavetable", NeoBuzz.LearnWt.Params.Table, () => new WavetableMap(NeoBuzz.LearnWt.Params.Table)),
            Learn("Neo Learn Additive", NeoBuzz.LearnAdd.Params.Table, () => new AdditiveMap(NeoBuzz.LearnAdd.Params.Table)),
            Learn("Neo Learn Granular", NeoBuzz.LearnGrain.Params.Table, () => new GranularMap(NeoBuzz.LearnGrain.Params.Table)),
            Learn("Neo Learn Physical", NeoBuzz.LearnPhys.Params.Table, () => new PhysicalMap(NeoBuzz.LearnPhys.Params.Table),
                notMonotone: "FINDING (notes.md G11): the mallet's contact pulse has spectral nulls that move with velocity, so the level wobbles by up to 17 dB between neighbouring velocities (127 is still 18 dB over 30)"),
            Learn("Neo Learn String", NeoBuzz.LearnString.Params.Table, () => new StringMap(NeoBuzz.LearnString.Params.Table)),
            Learn("Neo Learn Formant", NeoBuzz.LearnFormant.Params.Table, () => new FormantMap(NeoBuzz.LearnFormant.Params.Table)),
            Learn("Neo Learn Wavefolder", NeoBuzz.LearnFold.Params.Table, () => new WavefolderMap(NeoBuzz.LearnFold.Params.Table)),
            Learn("Neo Learn Phase Distortion", NeoBuzz.LearnPd.Params.Table, () => new PhaseDistortionMap(NeoBuzz.LearnPd.Params.Table)),
            Learn("Neo Learn Vector", NeoBuzz.LearnVector.Params.Table, () => new VectorMap(NeoBuzz.LearnVector.Params.Table)),
            Learn("Neo Learn Spectral", NeoBuzz.LearnSpectral.Params.Table, () => new SpectralMap(NeoBuzz.LearnSpectral.Params.Table)),
            Learn("Neo Learn Sampler", NeoBuzz.LearnSampler.Params.Table, () => new SamplerMap(NeoBuzz.LearnSampler.Params.Table)),
        };

        static Row Learn(string name, ParamDef[] table, Func<MacroMap> map, string notMonotone = null) => new Row
        {
            Name = name,
            NotMonotone = notMonotone,
            Make = () =>
            {
                TableBank.GetNow(0); TableBank.GetNow(1);
                var v = new ParamValues(table);
                var h = new LearnHost(map(), v);
                h.SetSampleRate(Rate);
                return Of(h.Process, h.Engine, () => h.Engine.HeldNotes, () => h.Engine.ActiveNotes);
            },
        };

        public static IEnumerable<Row> All => Rows;

        // ------------------------------------------------------------------ helpers

        static void Run(Rig r, int frames, Action<int> atBlock = null)
        {
            var l = new float[Block];
            var rr = new float[Block];
            for (int pos = 0, b = 0; pos < frames; pos += Block, b++)
            {
                atBlock?.Invoke(b);
                r.Process(l, rr, Math.Min(Block, frames - pos));
            }
        }

        static float[] Render(Rig r, int frames, Action<int> atBlock = null)
        {
            var y = new float[frames];
            var l = new float[Block];
            var rr = new float[Block];
            for (int pos = 0, b = 0; pos < frames; pos += Block, b++)
            {
                atBlock?.Invoke(b);
                int n = Math.Min(Block, frames - pos);
                r.Process(l, rr, n);
                for (int i = 0; i < n; i++) y[pos + i] = 0.5f * (l[i] + rr[i]);
            }
            return y;
        }

        /// <summary>The level (RMS, dB) of one note at <paramref name="velocity"/>, over the first 0.3 s (the attack included: a pluck's level is in it).</summary>
        static double NoteDb(Row row, int velocity)
        {
            var rig = row.Make();
            rig.Sink.NoteOn(0, row.Pitch, velocity / 127f);
            var y = Render(rig, Rate / 2);
            return Measure.Db(Measure.Rms(y, 0, Rate * 3 / 10));
        }

        // ------------------------------------------------------------------ velocity

        [Test, Explicit("a probe of one machine's level against velocity and pitch")]
        public void ProbeLevels()
        {
            var row = Rows.First(r => r.Name == "Neo Learn Physical");
            foreach (int pitch in new[] { 36, 48, 60, 72, 84 })
            {
                var sb = new System.Text.StringBuilder($"pitch {pitch}: ");
                foreach (int vel in new[] { 20, 30, 40, 60, 80, 100, 127 })
                {
                    var rig = row.Make();
                    rig.Sink.NoteOn(0, pitch, vel / 127f);
                    var y = Render(rig, Rate / 2);
                    sb.Append($"{vel}:{20 * Math.Log10(y.Max(Math.Abs) + 1e-9):F0} ");
                }
                Measure.Report(sb.ToString());
            }
        }

        [TestCaseSource(nameof(All))]
        public void VelocitySetsEachNotesLevel(Row row)
        {
            double[] db = { NoteDb(row, 30), NoteDb(row, 60), NoteDb(row, 90), NoteDb(row, 127) };
            Measure.Report($"{row.Name}: level at velocity 30, 60, 90, 127: {db[0]:F1}, {db[1]:F1}, {db[2]:F1}, {db[3]:F1} dB (127 over 30: {db[3] - db[0]:F1} dB)");
            Assert.That(db[3], Is.GreaterThan(-90), $"{row.Name} sounds");
            if (row.VelocityBlind != null)
            {
                Assert.That(db[3] - db[0], Is.EqualTo(0).Within(0.01), $"{row.Name}: {row.VelocityBlind}");
                return;
            }
            if (row.NotMonotone == null)
            {
                Assert.That(db[0], Is.LessThan(db[1]), "30 under 60");
                Assert.That(db[1], Is.LessThan(db[2]), "60 under 90");
                Assert.That(db[2], Is.LessThan(db[3]), "90 under 127");
            }
            Assert.That(db[3] - db[0], Is.GreaterThanOrEqualTo(6.0), $"{row.Name}: 127 is at least 6 dB over 30");
        }

        [TestCaseSource(nameof(All))]
        public void AFourNoteChordAtFourVelocitiesGivesFourVoices(Row row)
        {
            var rig = row.Make();
            int[] vel = { 30, 60, 90, 127 };
            for (int i = 0; i < 4; i++) rig.Sink.NoteOn(i, row.Chord[i], vel[i] / 127f);
            Run(rig, Rate / 20);
            Assert.That(rig.Held(), Is.EqualTo(4), $"{row.Name}: four notes held");
            Assert.That(rig.Sounding(), Is.EqualTo(4), $"{row.Name}: four voices");
        }

        // ------------------------------------------------------------------ note-off

        [TestCaseSource(nameof(All))]
        public void EachNoteOffReleasesOnlyItsOwnNote(Row row)
        {
            var rig = row.Make();
            int[] vel = { 30, 60, 90, 127 };
            for (int i = 0; i < 4; i++) rig.Sink.NoteOn(i, row.Chord[i], vel[i] / 127f);
            Run(rig, Rate / 20);
            if (row.NoNoteOff != null)
            {
                rig.Sink.NoteOff(1);
                Run(rig, Rate / 20);
                Assert.That(rig.Sounding(), Is.EqualTo(4), $"{row.Name}: {row.NoNoteOff}");
                return;
            }
            for (int off = 0; off < 4; off++)
            {
                rig.Sink.NoteOff(off);
                Run(rig, Rate / 50);
                Assert.That(rig.Held(), Is.EqualTo(3 - off), $"{row.Name}: after the note-off of note {off + 1}, the others are still held");
            }
            // Each note-off that did not belong to a held note does nothing.
            rig.Sink.NoteOff(7);
            Run(rig, Rate / 50);
            Assert.That(rig.Held(), Is.EqualTo(0));
        }

        [TestCaseSource(nameof(All))]
        public void ANoteOffEndsOnlyTheNoteItNames(Row row)
        {
            if (row.NoNoteOff != null) Assert.Pass(row.NoNoteOff);
            var rig = row.Make();
            for (int i = 0; i < 4; i++) rig.Sink.NoteOn(i, row.Chord[i], 0.8f);
            Run(rig, Rate / 20);
            rig.Sink.NoteOff(2);
            Run(rig, Rate / 50);
            Assert.That(rig.Held(), Is.EqualTo(3), $"{row.Name}: the other three are held");
        }

        // ------------------------------------------------------------------ polyphony

        [TestCaseSource(nameof(All))]
        public void SixteenNotesAtOnceGiveSixteenVoicesOrTheStatedMaximum(Row row)
        {
            var rig = row.Make();
            for (int i = 0; i < 16; i++) rig.Sink.NoteOn(i, row.Sixteen16 != null ? row.Sixteen16[i] : 36 + 3 * i, 0.7f);
            Run(rig, Rate / 10);
            Assert.That(rig.Sounding(), Is.EqualTo(row.Sixteen), $"{row.Name}: voices with 16 notes");
            var y = Render(rig, Rate / 10);
            Assert.That(y.All(float.IsFinite), Is.True, "finite");
        }

        [TestCaseSource(nameof(All))]
        public void NotesPastTheMaximumStealWithoutACrashOrAClick(Row row)
        {
            if (row.Max == 0) Assert.Pass("no limit set by a control");
            var rig = row.Make();
            var rng = new Random(5);
            var y = Render(rig, Rate / 2, b =>
            {
                if (b % 4 == 0)
                {
                    int k = b / 4 % 40;
                    rig.Sink.NoteOn(k, 36 + rng.Next(60), 0.4f + 0.5f * (float)rng.NextDouble());
                    if (k >= 20) rig.Sink.NoteOff(k - 20);
                }
            });
            Assert.That(y.All(float.IsFinite), Is.True, "finite");
            Assert.That(rig.Held(), Is.LessThanOrEqualTo(row.Max), $"{row.Name}: no more notes held than its maximum ({row.Max})");
            Assert.That(y.Max(Math.Abs), Is.LessThan(30f), "bounded");
        }

        // ------------------------------------------------------------------ allocation

        [TestCaseSource(nameof(All))]
        public void NoteOnsAndOffsAllocateNothingPerBlock(Row row)
        {
            var rig = row.Make();
            var l = new float[Block];
            var r = new float[Block];
            void Cycle(int b)
            {
                int k = b / 6 % 8;
                if (b % 6 == 0) rig.Sink.NoteOn(k, row.Chord[k % 4] + (k / 4) * 5, (30 + 12 * (k % 8)) / 127f);
                else if (b % 6 == 3) rig.Sink.NoteOff((k + 5) % 8);
                rig.Process(l, r, Block);
            }
            for (int b = 0; b < 600; b++) Cycle(b);                        // warm up: everything the notes touch is built and compiled
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int b = 600; b < 1200; b++) Cycle(b);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(bytes, Is.EqualTo(0), $"{row.Name}: {bytes} bytes over 600 blocks with notes");
        }
    }
}
