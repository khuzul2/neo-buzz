using System.Runtime.CompilerServices;
using System.Xml.Linq;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Factory presets (docs/specs/machines.md §3): every preset file names real parameters at
    /// their positions, in range, and every preset makes a sensible sound through its engine.
    /// </summary>
    [TestFixture]
    public class PresetTests
    {
        static string MachinesDir([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Machines"));

        /// <summary>Machine display name → parameter table and a renderer (pink noise in, output out).</summary>
        internal static readonly Dictionary<string, (ParamDef[] Table, Func<ParamValues, float[], float[]> Render)> Machines = new()
        {
            // Neo Analyzer passes its input through: its presets only change what it shows.
            ["Neo Analyzer"] = (NeoBuzz.Analyzer.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Analyzer(v).Process(l, r, n))),
            ["Neo Drive"] = (NeoBuzz.Drive.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Drive(v).Process(l, r, n))),
            ["Neo Comp"] = (NeoBuzz.Comp.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Comp(v).Process(l, r, null, null, n))),
            ["Neo Verb"] = (NeoBuzz.Verb.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Verb(v).Process(l, r, n))),
            ["Neo Poly"] = (NeoBuzz.Poly.Params.Table, (v, x) => RenderSynth(new NeoBuzz.Poly.PolyEngine(v), (e, l, r, n) => ((NeoBuzz.Poly.PolyEngine)e).Process(l, r, n), x.Length)),
            ["Neo Wave"] = (NeoBuzz.Wave.Params.Table, (v, x) => RenderSynth(WaveEngine(v), (e, l, r, n) => ((NeoBuzz.Wave.WaveEngine)e).Process(l, r, n), x.Length)),
            ["Neo FM6"] = (NeoBuzz.Fm6.Params.Table, (v, x) => RenderSynth(new NeoBuzz.Fm6.FmEngine(v), (e, l, r, n) => ((NeoBuzz.Fm6.FmEngine)e).Process(l, r, n), x.Length)),
            ["Neo EQ"] = (NeoBuzz.Eq.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Eq.EqEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            ["Neo Utility"] = (NeoBuzz.Utility.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Utility.UtilityEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            ["Neo Limiter"] = (NeoBuzz.Limiter.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Limiter.LimiterEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            // The next batch (docs/specs/machine-safety.md §4, mcp-server.md §12.5): Neo Ensemble, Neo Delay.
            ["Neo Ensemble"] = (NeoBuzz.Ensemble.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Ensemble.EnsembleEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            ["Neo Delay"] = (NeoBuzz.Delay.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Delay.DelayEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            // mcp-server.md §8.2: the multiband compressor for mastering and buses.
            ["Neo Multiband"] = (NeoBuzz.Multiband.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Multiband.MultibandEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            // mcp-server.md §8.2: the sampler; its kits play kick, snare and hat, its instruments the chord.
            ["Neo Sampler"] = (NeoBuzz.Sampler.Params.Table, (v, x) => RenderSampler(v, x.Length)),
            // mcp-server.md §8.1: the oscilloscope passes its input through; its presets only change what it shows.
            ["Neo Scope"] = (NeoBuzz.Scope.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Scope.ScopeEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n, default))),
            // mcp-server.md §8.2: the drum kit; each kit plays kick, snare and closed hat together.
            ["Neo Drums"] = (NeoBuzz.Drums.Params.Table, (v, x) => RenderDrums(v, x.Length)),
            // mcp-server.md §12.5: the filter effect (its LFO free-running here: no song plays).
            ["Neo Filter"] = (NeoBuzz.Filter.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Filter.FilterEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            // docs/specs/dye-essence.md: a Neo-family machine with its own name.
            ["Dye Essence"] = (NeoBuzz.DyeEssence.Params.Table, (v, x) => RenderSynth(DyeEngine(v), (e, l, r, n) => ((NeoBuzz.DyeEssence.DyeEngine)e).Process(l, r, n), x.Length)),
            // docs/specs/neo-learn.md: Neo Learn Subtractive plays Dye Essence's engine from its 12 controls (LearnHost).
            ["Neo Learn Subtractive"] = (NeoBuzz.LearnSub.Params.Table, (v, x) =>
            {
                NeoBuzz.DyeEssence.TableBank.GetNow(0);
                NeoBuzz.DyeEssence.TableBank.GetNow(1);
                var host = new NeoBuzz.Learn.LearnHost(new NeoBuzz.Learn.SubtractiveMap(NeoBuzz.LearnSub.Params.Table), v);
                return RenderSynth(host.Engine, (e, l, r, n) => host.Process(l, r, n), x.Length);
            }),
            // docs/specs/neo-learn.md L1a: Neo Learn FM, Wavetable and Additive, the same way.
            ["Neo Learn FM"] = (NeoBuzz.LearnFm.Params.Table, (v, x) => RenderLearn(new NeoBuzz.Learn.FmMap(NeoBuzz.LearnFm.Params.Table), v, x.Length)),
            ["Neo Learn Wavetable"] = (NeoBuzz.LearnWt.Params.Table, (v, x) => RenderLearn(new NeoBuzz.Learn.WavetableMap(NeoBuzz.LearnWt.Params.Table), v, x.Length)),
            ["Neo Learn Additive"] = (NeoBuzz.LearnAdd.Params.Table, (v, x) => RenderLearn(new NeoBuzz.Learn.AdditiveMap(NeoBuzz.LearnAdd.Params.Table), v, x.Length)),
            // docs/specs/neo-learn.md L1b: Neo Learn Granular and Physical.
            ["Neo Learn Granular"] = (NeoBuzz.LearnGrain.Params.Table, (v, x) => RenderLearn(new NeoBuzz.Learn.GranularMap(NeoBuzz.LearnGrain.Params.Table), v, x.Length)),
            ["Neo Learn Physical"] = (NeoBuzz.LearnPhys.Params.Table, (v, x) => RenderLearn(new NeoBuzz.Learn.PhysicalMap(NeoBuzz.LearnPhys.Params.Table), v, x.Length)),
            // docs/specs/neo-learn.md L2a: Neo Learn String, Formant and Wavefolder.
            ["Neo Learn String"] = (NeoBuzz.LearnString.Params.Table, (v, x) => RenderLearn(new NeoBuzz.Learn.StringMap(NeoBuzz.LearnString.Params.Table), v, x.Length)),
            ["Neo Learn Formant"] = (NeoBuzz.LearnFormant.Params.Table, (v, x) => RenderLearn(new NeoBuzz.Learn.FormantMap(NeoBuzz.LearnFormant.Params.Table), v, x.Length)),
            ["Neo Learn Wavefolder"] = (NeoBuzz.LearnFold.Params.Table, (v, x) => RenderLearn(new NeoBuzz.Learn.WavefolderMap(NeoBuzz.LearnFold.Params.Table), v, x.Length)),
            // docs/specs/neo-learn.md L2b: Neo Learn Phase Distortion, Vector and Sampler.
            ["Neo Learn Phase Distortion"] = (NeoBuzz.LearnPd.Params.Table, (v, x) => RenderLearn(new NeoBuzz.Learn.PhaseDistortionMap(NeoBuzz.LearnPd.Params.Table), v, x.Length)),
            ["Neo Learn Vector"] = (NeoBuzz.LearnVector.Params.Table, (v, x) => RenderLearn(new NeoBuzz.Learn.VectorMap(NeoBuzz.LearnVector.Params.Table), v, x.Length)),
            ["Neo Learn Sampler"] = (NeoBuzz.LearnSampler.Params.Table, (v, x) => RenderLearn(new NeoBuzz.Learn.SamplerMap(NeoBuzz.LearnSampler.Params.Table), v, x.Length)),
            ["Neo Learn Spectral"] = (NeoBuzz.LearnSpectral.Params.Table, (v, x) => RenderLearn(new NeoBuzz.Learn.SpectralMap(NeoBuzz.LearnSpectral.Params.Table), v, x.Length)),
            // mastering-tools.md §5.1: the clipper.
            ["Neo Clipper"] = (NeoBuzz.Clipper.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Clipper.ClipperEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            // mastering-tools.md §5.4, §5.5: the transient shaper and the low-end shaper.
            ["Neo Transient"] = (NeoBuzz.Transient.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Transient.TransientEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            ["Neo Low End"] = (NeoBuzz.LowEnd.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.LowEnd.LowEndEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            // mastering-tools.md §5.7 - §5.9: the imager, the exciter and the dither.
            ["Neo Imager"] = (NeoBuzz.Imager.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Imager.ImagerEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            // mastering-tools.md §5.11 - §5.13: the tape, the de-esser and the gate.
            ["Neo Tape"] = (NeoBuzz.Tape.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Tape.TapeEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            ["Neo De-esser"] = (NeoBuzz.DeEsser.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.DeEsser.DeEsserEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            ["Neo Gate"] = (NeoBuzz.Gate.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Gate.GateEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, null, null, n))),
            ["Neo Exciter"] = (NeoBuzz.Exciter.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Exciter.ExciterEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            ["Neo Dither"] = (NeoBuzz.Dither.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Dither.DitherEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
            ["Neo Strip"] = (NeoBuzz.Strip.Params.Table, (v, x) => RenderEffect(x, (l, r, n) => Fresh(v, () => { var e = new NeoBuzz.Strip.StripEngine(v); e.SetSampleRate(Measure.Rate); return e; }).Process(l, r, n))),
        };

        /// <summary>A Neo Learn machine's factory preset: its host plays the chord (the built-in tables are built first).</summary>
        static float[] RenderLearn(NeoBuzz.Learn.MacroMap map, ParamValues v, int length)
        {
            for (int i = 0; i < NeoBuzz.DyeEssence.TableBank.Count; i++) NeoBuzz.DyeEssence.TableBank.GetNow(i);
            for (int i = 0; i < NeoBuzz.DyeEssence.SampleSources.Count; i++) NeoBuzz.DyeEssence.SampleSources.GetNow(i);
            for (int i = 0; i < NeoBuzz.DyeEssence.SpectralSources.Count; i++) NeoBuzz.DyeEssence.SpectralSources.GetNow(i);
            var host = new NeoBuzz.Learn.LearnHost(map, v);
            return RenderSynth(host.Engine, (e, l, r, n) => host.Process(l, r, n), length);
        }

        /// <summary>The Neo-family machine folders: Neo* and Dye Essence (docs/specs/dye-essence.md §2).</summary>
        internal static bool IsNeoFamily(string folder) => Path.GetFileName(folder).StartsWith("Neo") || Path.GetFileName(folder) == "DyeEssence";

        // The mix and master batch: one engine per preset render, keyed by its parameter values.
        static readonly Dictionary<ParamValues, object> fresh = new(ReferenceEqualityComparer.Instance);

        static T Fresh<T>(ParamValues v, Func<T> make)
        {
            if (!fresh.TryGetValue(v, out var e))
            {
                fresh.Clear();
                fresh[v] = e = make();
            }
            return (T)e;
        }

        static readonly HashSet<string> Synths = new() { "Neo Poly", "Neo Wave", "Neo FM6", "Neo Sampler", "Neo Drums", "Dye Essence", "Neo Learn Subtractive", "Neo Learn FM", "Neo Learn Wavetable", "Neo Learn Additive", "Neo Learn Granular", "Neo Learn Physical", "Neo Learn String", "Neo Learn Formant", "Neo Learn Wavefolder", "Neo Learn Phase Distortion", "Neo Learn Vector", "Neo Learn Sampler", "Neo Learn Spectral" };

        /// <summary>
        /// Neo Sampler: the song's slots 1–16 hold the built-in drums and the sine (the presets that
        /// play song waves are templates for them); a kit plays kick, snare and closed hat (C2, D2, F#2),
        /// an instrument the chord.
        /// </summary>
        static float[] RenderSampler(ParamValues v, int length)
        {
            NeoBuzz.Sampler.BuiltIns.BuildNow();
            var bank = new NeoBuzz.Sampler.SampleBank();
            for (int k = 1; k <= 16; k++) bank.Set(k, NeoBuzz.Sampler.BuiltIns.Get(k));
            var e = new NeoBuzz.Sampler.SamplerEngine(v, bank);
            bool kit = v.Choice(NeoBuzz.Sampler.P.Mode) == 1;
            int[] notes = kit ? new[] { 36, 38, 42 } : new[] { 48, 52, 55, 59 };
            e.SetSampleRate(Measure.Rate);
            var y = new float[length];
            var l = new float[256];
            var r = new float[256];
            for (int k = 0; k < notes.Length; k++) e.NoteOn(k, notes[k], 0.8f);
            for (int pos = 0; pos < length; pos += 256)
            {
                if (pos <= length * 2 / 3 && pos + 256 > length * 2 / 3)
                    for (int k = 0; k < notes.Length; k++) e.NoteOff(k);
                int n = Math.Min(256, length - pos);
                e.Process(l, r, n);
                for (int i = 0; i < n; i++) y[pos + i] = Math.Max(Math.Abs(l[i]), Math.Abs(r[i])) * Math.Sign(l[i] + r[i] + 1e-12f);
            }
            return y;
        }

        /// <summary>Neo Drums: kick, snare and closed hat (C2, D2, F#2) at velocity 0.8, together.</summary>
        static float[] RenderDrums(ParamValues v, int length)
        {
            var e = new NeoBuzz.Drums.DrumsEngine(v);
            e.SetSampleRate(Measure.Rate);
            var y = new float[length];
            var l = new float[256];
            var r = new float[256];
            int[] notes = { 36, 38, 42 };
            for (int k = 0; k < notes.Length; k++) e.NoteOn(k, notes[k], 0.8f);
            for (int pos = 0; pos < length; pos += 256)
            {
                int n = Math.Min(256, length - pos);
                e.Process(l, r, n);
                for (int i = 0; i < n; i++) y[pos + i] = Math.Max(Math.Abs(l[i]), Math.Abs(r[i])) * Math.Sign(l[i] + r[i] + 1e-12f);
            }
            return y;
        }

        static NeoBuzz.Wave.WaveEngine WaveEngine(ParamValues v)
        {
            // Built-in tables build in the background in the machine; here, before the first note.
            for (int i = 0; i < NeoBuzz.Wave.Tables.Names.Length; i++) NeoBuzz.Wave.Tables.GetNow(i);
            return new NeoBuzz.Wave.WaveEngine(v);
        }

        static NeoBuzz.DyeEssence.DyeEngine DyeEngine(ParamValues v)
        {
            for (int i = 0; i < NeoBuzz.DyeEssence.TableBank.Count; i++) NeoBuzz.DyeEssence.TableBank.GetNow(i);
            for (int i = 0; i < NeoBuzz.DyeEssence.SampleSources.Count; i++) NeoBuzz.DyeEssence.SampleSources.GetNow(i);
            for (int i = 0; i < NeoBuzz.DyeEssence.SpectralSources.Count; i++) NeoBuzz.DyeEssence.SpectralSources.GetNow(i);
            var e = new NeoBuzz.DyeEssence.DyeEngine(v);
            // The preset's drawn LFOs, from its machine data (dye-essence.md §4.6), as the machine takes them.
            for (int l = 0; l < 4; l++)
            {
                string text = (string)dyeState?.Element($"Lfo{l + 1}Shape");
                if (string.IsNullOrEmpty(text)) continue;
                Assert.That(NeoBuzz.DyeEssence.DrawnShape.TryParse(text, out var shape, out string error), Is.True, $"LFO {l + 1}'s shape: {error}");
                e.SetLfoShape(l, shape);
            }
            // And its sequencer's steps (§4.9).
            string steps = (string)dyeState?.Element("SeqSteps");
            if (!string.IsNullOrEmpty(steps))
            {
                Assert.That(NeoBuzz.DyeEssence.SeqPattern.TryParse(steps, out var pattern, out string error), Is.True, $"the sequence: {error}");
                e.SetPattern(pattern);
            }
            // And its effects rack's chains (§4.10), the effects built now rather than in the background.
            string chains = (string)dyeState?.Element("FxRouting");
            if (!string.IsNullOrEmpty(chains)) e.Fx.SetRouting(NeoBuzz.DyeEssence.FxRouting.Parse(chains));
            e.Fx.BuildNow();
            return e;
        }

        /// <summary>The Dye Essence preset being rendered: its machine state (null: none).</summary>
        internal static XElement dyeState;

        /// <summary>
        /// A preset's machine data as ReBuzz stores it (MachineManager.GetMachineData: a version byte, the size, then the
        /// MachineState XML), read back to the XML; null for a preset without data.
        /// </summary>
        internal static XElement PresetState(string file, string preset)
        {
            var item = XDocument.Load(file).Root!.Elements("Item").First(i => (string)i.Attribute("Key")! == preset);
            string data = (string)item.Element("Preset")!.Element("Data");
            if (string.IsNullOrEmpty(data)) return null;
            var bytes = Convert.FromBase64String(data);
            Assert.That(bytes[0], Is.EqualTo(2), $"{preset}: the data's version");
            int size = BitConverter.ToInt32(bytes, 1);
            Assert.That(size, Is.EqualTo(bytes.Length - 5), $"{preset}: the data's size");
            return XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes, 5, size)).Root;
        }

        /// <summary>The notes RenderSynth plays instead of the chord (the library test's single note); null: the chord.</summary>
        internal static int[] NotesOverride;

        /// <summary>One Dye Essence preset of a preset file, rendered as EveryPresetSoundsSensible does: the chord, one second.</summary>
        internal static float[] RenderDyePreset(string file, string name, Dictionary<int, int> values, int[] notes = null)
        {
            var (table, render) = Machines["Dye Essence"];
            dyeState = PresetState(file, name);
            NotesOverride = notes;
            try
            {
                var v = new ParamValues(table);
                foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                return render(v, new float[Measure.Rate]);
            }
            finally { NotesOverride = null; dyeState = null; }
        }

        /// <summary>A chord (C3 E3 G3 B3) held for two thirds of the length, then released.</summary>
        internal static float[] RenderSynth(INoteSink engine, Func<INoteSink, float[], float[], int, bool> process, int length)
        {
            ((dynamic)engine).SetSampleRate((float)Measure.Rate);
            var y = new float[length];
            var l = new float[256];
            var r = new float[256];
            int[] chord = NotesOverride ?? new[] { 48, 52, 55, 59 };
            for (int k = 0; k < chord.Length; k++) engine.NoteOn(k, chord[k], 0.8f);
            for (int pos = 0; pos < length; pos += 256)
            {
                if (pos <= length * 2 / 3 && pos + 256 > length * 2 / 3)
                    for (int k = 0; k < chord.Length; k++) engine.NoteOff(k);
                int n = Math.Min(256, length - pos);
                process(engine, l, r, n);
                for (int i = 0; i < n; i++) y[pos + i] = Math.Max(Math.Abs(l[i]), Math.Abs(r[i])) * Math.Sign(l[i] + r[i] + 1e-12f);
            }
            return y;
        }

        // One engine per preset render (created lazily by the closures above).
        static NeoBuzz.Drive.DriveEngine driveEngine; static ParamValues driveValues;
        static NeoBuzz.Comp.CompEngine compEngine; static ParamValues compValues;
        static NeoBuzz.Verb.VerbEngine verbEngine; static ParamValues verbValues;

        static NeoBuzz.Analyzer.AnalyzerEngine analyzerEngine; static ParamValues analyzerValues;

        static NeoBuzz.Analyzer.AnalyzerEngine Analyzer(ParamValues v)
        {
            if (!ReferenceEquals(v, analyzerValues)) { analyzerValues = v; analyzerEngine = new NeoBuzz.Analyzer.AnalyzerEngine(v); analyzerEngine.SetSampleRate(Measure.Rate); }
            return analyzerEngine;
        }

        static NeoBuzz.Drive.DriveEngine Drive(ParamValues v)
        {
            if (!ReferenceEquals(v, driveValues)) { driveValues = v; driveEngine = new NeoBuzz.Drive.DriveEngine(v); driveEngine.SetSampleRate(Measure.Rate); }
            return driveEngine;
        }

        static NeoBuzz.Comp.CompEngine Comp(ParamValues v)
        {
            if (!ReferenceEquals(v, compValues)) { compValues = v; compEngine = new NeoBuzz.Comp.CompEngine(v); compEngine.SetSampleRate(Measure.Rate); }
            return compEngine;
        }

        static NeoBuzz.Verb.VerbEngine Verb(ParamValues v)
        {
            if (!ReferenceEquals(v, verbValues)) { verbValues = v; verbEngine = new NeoBuzz.Verb.VerbEngine(v); verbEngine.SetSampleRate(Measure.Rate); }
            return verbEngine;
        }

        static float[] RenderEffect(float[] x, Action<float[], float[], int> process)
        {
            var y = new float[x.Length];
            var l = new float[256];
            var r = new float[256];
            for (int pos = 0; pos < x.Length; pos += 256)
            {
                int n = Math.Min(256, x.Length - pos);
                Array.Copy(x, pos, l, 0, n);
                Array.Copy(x, pos, r, 0, n);
                process(l, r, n);
                Array.Copy(l, 0, y, pos, n);
            }
            return y;
        }

        internal static IEnumerable<(string File, string Machine, string Preset, Dictionary<int, int> Values)> AllPresets()
        {
            foreach (var file in Directory.GetFiles(MachinesDir(), "*.prs.xml", SearchOption.AllDirectories))
            {
                if (!IsNeoFamily(Path.GetDirectoryName(file)!)) continue;
                var doc = XDocument.Load(file);
                foreach (var item in doc.Root!.Elements("Item"))
                {
                    var preset = item.Element("Preset")!;
                    var values = new Dictionary<int, int>();
                    foreach (var p in preset.Element("Parameters")!.Elements("Parameter"))
                    {
                        int index = (int)p.Attribute("Index")!;
                        values[index] = (int)p.Attribute("Value")!;
                        // Name check happens in the test (needs the table).
                        values[-1 - index] = 0;
                    }
                    yield return (file, (string)preset.Attribute("Machine")!, (string)item.Attribute("Key")!, values);
                }
            }
        }

        [Test]
        public void PresetFilesMatchTheirMachines()
        {
            int count = 0;
            foreach (var file in Directory.GetFiles(MachinesDir(), "*.prs.xml", SearchOption.AllDirectories))
            {
                if (!IsNeoFamily(Path.GetDirectoryName(file)!)) continue;
                var doc = XDocument.Load(file);
                foreach (var item in doc.Root!.Elements("Item"))
                {
                    var preset = item.Element("Preset")!;
                    string machine = (string)preset.Attribute("Machine")!;
                    Assert.That(Machines.ContainsKey(machine), Is.True, $"{file}: unknown machine {machine}");
                    Assert.That(Path.GetFileName(file), Is.EqualTo(machine + ".prs.xml"), "ReBuzz finds presets by the DLL's name");
                    var table = Machines[machine].Table;
                    // A preset's machine data (its state lines), when it has some, reads back to its machine's state.
                    var state = PresetState(file, (string)item.Attribute("Key")!);
                    if (state != null) Assert.That(state.Name.LocalName, Does.EndWith("State"), $"{item.Attribute("Key")}: its data is a machine state");
                    foreach (var p in preset.Element("Parameters")!.Elements("Parameter"))
                    {
                        int index = (int)p.Attribute("Index")!;
                        int value = (int)p.Attribute("Value")!;
                        Assert.That(index, Is.InRange(0, table.Length - 1));
                        Assert.That((string)p.Attribute("Name")!, Is.EqualTo(table[index].Name), $"{item.Attribute("Key")}: parameter {index}");
                        Assert.That(value, Is.InRange(table[index].Min, table[index].Max), $"{item.Attribute("Key")}: {table[index].Name}");
                    }
                    count++;
                }
            }
            TestContext.Out.WriteLine($"{count} presets");
            Assert.That(count, Is.GreaterThan(30));
        }

        [Test]
        public void EveryPresetSoundsSensible()
        {
            var x = Measure.PinkNoise(Measure.Rate, DspMath.DbToGain(-18));
            double inRms = Measure.Rms(x, Measure.Rate / 2, Measure.Rate / 2);
            foreach (var (file, machine, name, values) in AllPresets())
            {
                var (table, render) = Machines[machine];
                dyeState = machine == "Dye Essence" ? PresetState(file, name) : null;
                var v = new ParamValues(table);
                foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                var y = render(v, x);
                if (Synths.Contains(machine))
                {
                    // The loudest 50 ms while the chord is held, and the peak.
                    double loudest = 0;
                    for (int at = 0; at + 2400 <= y.Length * 2 / 3; at += 1200) loudest = Math.Max(loudest, Measure.Rms(y, at, 2400));
                    double level = Measure.Db(loudest);
                    double top = Measure.Db(Measure.Peak(y, 0, y.Length));
                    TestContext.Out.WriteLine($"{machine} / {name}: chord {level:+0.0;−0.0} dB RMS (loudest 50 ms), peak {top:+0.0;−0.0} dBFS");
                    Assert.That(double.IsFinite(top), Is.True, name);
                    Assert.That(level, Is.InRange(-30.0, -6.0), $"{machine} / {name}: level");
                    Assert.That(top, Is.InRange(-20.0, -1.0), $"{machine} / {name}: peak");
                    continue;
                }
                double rms = Measure.Rms(y, Measure.Rate / 2, Measure.Rate / 2);
                double peak = Measure.Peak(y, 0, y.Length);
                double db = Measure.Db(rms / inRms);
                TestContext.Out.WriteLine($"{machine} / {name}: {db:+0.0;−0.0} dB, peak {Measure.Db(peak):+0.0;−0.0} dBFS");
                Assert.That(double.IsFinite(peak), Is.True, name);
                Assert.That(db, Is.InRange(-24.0, 12.0), $"{machine} / {name}");
                Assert.That(peak, Is.LessThan(1.5), $"{machine} / {name}: clips");
            }
        }
    }
}
