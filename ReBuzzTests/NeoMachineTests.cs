using AtmaFileSystem;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzzTests.Automation;
using ReBuzzTests.Automation.TestMachines;
using ReBuzzTests.Automation.TestMachinesControllers;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: the Neo machines (docs/specs/machines.md) inside ReBuzz. Loads the DLLs the
    /// solution build made, runs audio through them, checks their parameters, latency reports and
    /// editors (screenshots go where SessionViewTests puts its own). The DSP itself is measured in
    /// detail by NeoBuzz.MachineTests. Also the core fix of §3.1: a chord on several tracks
    /// reaches a managed machine whole.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public partial class NeoMachineTests
    {
        // ------------------------------------------------------------------ machines

        /// <summary>A Neo machine as the solution build made it.</summary>
        internal sealed class NeoInfo(string name, string shortName, bool generator, bool multiIo = false, string builtPath = null) : ITestMachineInfo
        {
            public string Name { get; } = name;
            public bool Generator { get; } = generator;

            MachineDLL ITestMachineInfo.GetMachineDll(ReBuzzCore buzz, AbsoluteFilePath location) => new MachineDLL
            {
                Name = Name,
                Buzz = buzz,
                Path = location.ToString(),
                Is64Bit = true,
                IsCrashed = false,
                IsManaged = true,
                IsLoaded = false,
                IsMissing = false,
                IsOutOfProcess = false,
                ManagedDLL = null,
                MachineInfo = new MachineInfo
                {
                    Flags = MachineInfoFlags.LOAD_DATA_RUNTIME | (multiIo ? MachineInfoFlags.MULTI_IO : 0),
                    Author = "neo-buzz",
                    InternalVersion = 0,
                    MaxTracks = generator ? 16 : 0,
                    MinTracks = generator ? 1 : 0,
                    Name = Name,
                    ShortName = shortName,
                    Type = generator ? MachineType.Generator : MachineType.Effect,
                    Version = 66,
                },
                Presets = null,
                SHA1Hash = "AE0" + Math.Abs(Name.GetHashCode()).ToString("X8").PadRight(37, '0'),
                GUIFactoryDecl = new MachineGUIFactoryDecl { PreferWindowedGUI = true },
                ModuleHandle = 0,
            };

            public string DllName => Name + ".NET.dll";
            public string RealDllName => DllName;

            /// <summary>Where the solution build put the DLL (beside ReBuzz.exe, under Gear).</summary>
            public string BuiltPath
            {
                get
                {
                    if (builtPath != null) return builtPath;
                    var bin = File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First();
                    return Path.Combine(bin, "Gear", Generator ? "Generators" : "Effects", DllName);
                }
            }
        }

        internal static readonly NeoInfo Drive = new("Neo Drive", "Drive", false);
        // Neo Analyzer (docs/specs/machines.md §5, MCP A4): measures what passes, changes nothing.
        internal static readonly NeoInfo Analyzer = new("Neo Analyzer", "Analyzer", false);
        internal static readonly NeoInfo Comp = new("Neo Comp", "Comp", false, multiIo: true);
        internal static readonly NeoInfo Verb = new("Neo Verb", "Verb", false);
        internal static readonly NeoInfo Poly = new("Neo Poly", "Poly", true);
        internal static readonly NeoInfo Wave = new("Neo Wave", "Wave", true);
        internal static readonly NeoInfo Fm6 = new("Neo FM6", "FM6", true);
        // Mix and master batch (docs/specs/machines.md §5, MCP A4).
        internal static readonly NeoInfo Eq = new("Neo EQ", "EQ", false, multiIo: true);   // v2 (M3): input 2 is the sidechain
        internal static readonly NeoInfo Utility = new("Neo Utility", "Utility", false);
        internal static readonly NeoInfo Limiter = new("Neo Limiter", "Limiter", false);
        // The next batch's first successor (docs/specs/machine-safety.md §4): chorus, flanger, phaser.
        internal static readonly NeoInfo Ensemble = new("Neo Ensemble", "Ensemble", false);
        // The successor of the classic delays (docs/specs/mcp-server.md §12.5): stereo, ping-pong and cross.
        internal static readonly NeoInfo Delay = new("Neo Delay", "Delay", false);
        // The multiband compressor for mastering and buses (docs/specs/mcp-server.md §8.2): linear-phase crossovers with latency.
        internal static readonly NeoInfo Multiband = new("Neo Multiband", "Multiband", false);
        // The sampler (docs/specs/mcp-server.md §8.2): plays the song's waves.
        internal static readonly NeoInfo Sampler = new("Neo Sampler", "Sampler", true);
        // The oscilloscope and vectorscope (docs/specs/mcp-server.md §8.1): looks at what passes, changes nothing.
        internal static readonly NeoInfo Scope = new("Neo Scope", "Scope", false);
        // The drum kit (docs/specs/mcp-server.md §8.2, §12.5): General MIDI keys from C2 and Buzz drum machines' hit columns.
        internal static readonly NeoInfo Drums = new("Neo Drums", "Drums", true);
        // The filter effect (docs/specs/mcp-server.md §12.5): Neo Poly's filters with an envelope follower and an LFO.
        internal static readonly NeoInfo Filter = new("Neo Filter", "Filter", false);
        // The hybrid wavetable synth (docs/specs/dye-essence.md): a Neo-family machine with its own name.
        internal static readonly NeoInfo Dye = new("Dye Essence", "Dye", true);
        // Neo Learn (docs/specs/neo-learn.md): the teaching synth, one Dye Essence patch behind 12 controls.
        internal static readonly NeoInfo LearnSub = new("Neo Learn Subtractive", "Learn Sub", true);
        // Neo Learn L1a (docs/specs/neo-learn.md §9): FM, Wavetable and Additive, the same host behind other engines.
        internal static readonly NeoInfo LearnFm = new("Neo Learn FM", "Learn FM", true);
        internal static readonly NeoInfo LearnWt = new("Neo Learn Wavetable", "Learn WT", true);
        internal static readonly NeoInfo LearnAdd = new("Neo Learn Additive", "Learn Add", true);
        // L1b: Granular and Physical.
        internal static readonly NeoInfo LearnGrain = new("Neo Learn Granular", "Learn Grain", true);
        internal static readonly NeoInfo LearnPhys = new("Neo Learn Physical", "Learn Phys", true);
        // L2a: String, Formant (box name Learn Voice) and Wavefolder.
        internal static readonly NeoInfo LearnString = new("Neo Learn String", "Learn String", true);
        internal static readonly NeoInfo LearnFormant = new("Neo Learn Formant", "Learn Voice", true);
        internal static readonly NeoInfo LearnFold = new("Neo Learn Wavefolder", "Learn Fold", true);
        // L2b: Phase Distortion, Vector and Sampler.
        internal static readonly NeoInfo LearnPd = new("Neo Learn Phase Distortion", "Learn PD", true);
        internal static readonly NeoInfo LearnVector = new("Neo Learn Vector", "Learn Vector", true);
        internal static readonly NeoInfo LearnSampler = new("Neo Learn Sampler", "Learn Sampler", true);
        internal static readonly NeoInfo LearnSpectral = new("Neo Learn Spectral", "Learn Spectral", true);
        // Neo Patch NP1 (docs/specs/neo-patch.md): the modular synth; no editor until NP2.
        internal static readonly NeoInfo Patch = new("Neo Patch", "Patch", true);
        // The clipper (docs/specs/mastering-tools.md §5.1): oversampled curves into a ceiling no sample passes.
        internal static readonly NeoInfo Clipper = new("Neo Clipper", "Clipper", false);
        // The transient shaper and the low-end shaper (docs/specs/mastering-tools.md §5.4, §5.5).
        internal static readonly NeoInfo Transient = new("Neo Transient", "Transient", false);
        internal static readonly NeoInfo LowEnd = new("Neo Low End", "LowEnd", false);
        // The imager, the exciter and the dither (docs/specs/mastering-tools.md §5.7 - §5.9).
        internal static readonly NeoInfo Imager = new("Neo Imager", "Imager", false);
        internal static readonly NeoInfo Exciter = new("Neo Exciter", "Exciter", false);
        internal static readonly NeoInfo Dither = new("Neo Dither", "Dither", false);
        // The tape, the de-esser and the gate (docs/specs/mastering-tools.md §5.11 - §5.13).
        internal static readonly NeoInfo Tape = new("Neo Tape", "Tape", false);
        internal static readonly NeoInfo DeEsser = new("Neo De-esser", "DeEsser", false);
        internal static readonly NeoInfo Gate = new("Neo Gate", "Gate", false);
        // The resonance suppressor (docs/specs/mastering-tools.md §5.16).
        internal static readonly NeoInfo Spectral = new("Neo Spectral", "Spectral", false);
        // The strip (docs/specs/mastering-tools.md §5.10).
        internal static readonly NeoInfo Strip = new("Neo Strip", "Strip", false);

        /// <summary>A test machine compiled from source held here.</summary>
        internal sealed class SourceInfo(string name, string source, bool generator) : IDynamicTestMachineInfo
        {
            MachineDLL ITestMachineInfo.GetMachineDll(ReBuzzCore buzz, AbsoluteFilePath location) => new MachineDLL
            {
                Name = name,
                Buzz = buzz,
                Path = location.ToString(),
                Is64Bit = true,
                IsManaged = true,
                MachineInfo = new MachineInfo
                {
                    Flags = MachineInfoFlags.LOAD_DATA_RUNTIME,
                    Author = "neo-buzz",
                    MaxTracks = 8,
                    MinTracks = 1,
                    Name = name,
                    ShortName = name,
                    Type = generator ? MachineType.Generator : MachineType.Effect,
                    Version = 66,
                },
                SHA1Hash = "5E0" + Math.Abs(name.GetHashCode()).ToString("X8").PadRight(37, '0'),
                GUIFactoryDecl = new MachineGUIFactoryDecl(),
            };

            public string DllName => name + ".dll";
            public string RealDllName => DllName;
            public string SourceCode => source;
        }

        const string SineSource = """
            using Buzz.MachineInterface;
            using BuzzGUI.Interfaces;
            using System;
            namespace NeoTest
            {
                [MachineDecl(Name = "Neo Test Sine", ShortName = "Sine", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 1)]
                public class Sine : IBuzzMachine
                {
                    readonly IBuzzMachineHost host;
                    double phase;
                    public Sine(IBuzzMachineHost host) { this.host = host; }
                    [ParameterDecl(Name = "Level", MinValue = 0, MaxValue = 100, DefValue = 50)]
                    public int Level { get; set; }
                    public bool Work(Sample[] output, int n, WorkModes mode)
                    {
                        double inc = 220.0 / Math.Max(1, host.MasterInfo.SamplesPerSec);
                        float a = Level / 100f * 32768f;
                        for (int i = 0; i < n; i++)
                        {
                            float v = a * (float)Math.Sin(2 * Math.PI * phase);
                            output[i] = new Sample(v, v);
                            phase += inc;
                            if (phase >= 1) phase -= 1;
                        }
                        return Level > 0;
                    }
                }
            }
            """;

        const string ProbeSource = """
            using Buzz.MachineInterface;
            using BuzzGUI.Interfaces;
            using System;
            namespace NeoTest
            {
                [MachineDecl(Name = "Neo Chord Probe", ShortName = "Probe", Author = "neo-buzz", MaxTracks = 8, InputCount = 0, OutputCount = 1)]
                public class Probe : IBuzzMachine
                {
                    public Probe(IBuzzMachineHost host) { }
                    [ParameterDecl(Name = "Dummy", MinValue = 0, MaxValue = 1, DefValue = 0)]
                    public int Dummy { get; set; }
                    public int[] Calls = new int[8];
                    public int[] Last = new int[8];
                    [ParameterDecl(Name = "Note", IsStateless = true)]
                    public void SetNote(Note value, int track)
                    {
                        if (track >= 0 && track < 8) { Calls[track]++; Last[track] = value.Value; }
                    }
                    public bool Work(Sample[] output, int n, WorkModes mode) => false;
                }
            }
            """;

        // Neo Analyzer's test signal: a 1 kHz sine at −20 dBFS on both channels (Mode 0), or a small
        // stereo mix (Mode 1): a pulsing 55 Hz bass, a chord spread across the image, darkened noise
        // and hats, so the analyzer's editor has something to show.
        const string MixSource = """
            using Buzz.MachineInterface;
            using BuzzGUI.Interfaces;
            using System;
            namespace NeoTest
            {
                [MachineDecl(Name = "Neo Test Mix", ShortName = "Mix", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 1)]
                public class Mix : IBuzzMachine
                {
                    readonly IBuzzMachineHost host;
                    double t, nl, nr;
                    uint seed = 22222;
                    public Mix(IBuzzMachineHost host) { this.host = host; }
                    [ParameterDecl(Name = "Mode", MinValue = 0, MaxValue = 1, DefValue = 0)]
                    public int Mode { get; set; }
                    double Noise() { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return seed / 2147483648.0 - 1; }
                    static double S(double hz, double t, double phase = 0) => Math.Sin(2 * Math.PI * hz * t + phase);
                    public bool Work(Sample[] output, int n, WorkModes mode)
                    {
                        double dt = 1.0 / Math.Max(1, host.MasterInfo.SamplesPerSec);
                        for (int i = 0; i < n; i++)
                        {
                            double l, r;
                            if (Mode == 0) l = r = 0.1 * S(1000, t);
                            else
                            {
                                double bass = 0.25 * S(55, t) * (0.6 + 0.4 * S(2, t));
                                double hat = Math.Pow(Math.Max(0, S(4, t)), 40) * 0.15;
                                nl = 0.98 * nl + 0.02 * Noise();
                                nr = 0.98 * nr + 0.02 * Noise();
                                l = bass + 0.06 * (S(220, t) + S(277.2, t) + S(329.6, t)) + 1.5 * nl + hat * Noise() + 0.01 * Noise();
                                r = bass + 0.06 * (S(220, t, 0.6) + S(277.2, t, 1.1) + S(329.6, t, 1.7)) + 1.5 * nr + hat * Noise() + 0.01 * Noise();
                            }
                            output[i] = new Sample((float)(l * 32768), (float)(r * 32768));
                            t += dt;
                        }
                        return true;
                    }
                }
            }
            """;

        static readonly SourceInfo SineInfo = new("Neo Test Sine", SineSource, true);
        static readonly SourceInfo ProbeInfo = new("Neo Chord Probe", ProbeSource, true);

        // ------------------------------------------------------------------ rig

        sealed class Rig : IDisposable
        {
            public Driver Driver { get; } = new Driver();
            public ReBuzzCore Core => Driver.Core;
            readonly Driver.AudioReader reader;
            public MachineCore Sine { get; }
            public MachineCore Effect { get; }
            /// <summary>With a parallel effect: sine → it → master, beside the Neo effect.</summary>
            public MachineCore Parallel { get; }

            /// <summary>
            /// Effects: sine → the Neo effect → master (and, given <paramref name="parallel"/>, sine → that effect → master beside it).
            /// Generators: the Neo synth → master.
            /// </summary>
            public Rig(NeoInfo machine, NeoInfo parallel = null)
            {
                if (!File.Exists(machine.BuiltPath))
                    Assert.Inconclusive($"{machine.Name} not built ({machine.BuiltPath}); build the solution first");
                if (parallel != null && !File.Exists(parallel.BuiltPath))
                    Assert.Inconclusive($"{parallel.Name} not built ({parallel.BuiltPath}); build the solution first");
                if (!machine.Generator) Driver.Gear.AddDynamicGenerator(SineInfo);
                Driver.Gear.AddBuiltMachine(machine, AbsoluteFilePath.Value(machine.BuiltPath), machine.Generator);
                if (parallel != null) Driver.Gear.AddBuiltMachine(parallel, AbsoluteFilePath.Value(parallel.BuiltPath), false);
                Driver.Start();
                var fx = new DynamicMachineController(machine.Name, machine.Name);
                Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(fx);
                if (!machine.Generator)
                {
                    var sine = new DynamicMachineController("Neo Test Sine", "Sine");
                    Driver.MachineGraph.InsertMachineInstanceFor(sine);
                    Driver.MachineGraph.Connect(sine, fx);
                    Sine = Driver.MachineFor(sine);
                    if (parallel != null)
                    {
                        var side = new DynamicMachineController(parallel.Name, parallel.Name + " beside");
                        Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(side);
                        Driver.MachineGraph.Connect(sine, side);
                        Parallel = Driver.MachineFor(side);
                    }
                }
                Effect = Driver.MachineFor(fx);
                reader = Driver.CreateAudioReader(256);
            }

            /// <summary>Synths: notes on tracks 0, 1, … (Buzz note values; 255 = off).</summary>
            public void Notes(params int[] notes)
            {
                if (Effect.TrackCount < notes.Length) Effect.TrackCount = notes.Length;
                var note = Effect.ParameterGroups[2].Parameters.Single(p => p.Name == "Note");
                for (int t = 0; t < notes.Length; t++) note.SetValue(t, notes[t]);
            }

            /// <summary>Plays in short steps until there is sound (background work such as table builds may come first).</summary>
            public float[] PlayUntilSound(double seconds, double atLeast = 1e-3)
            {
                for (int i = 0; i < 100; i++)
                {
                    var x = Play(seconds);
                    if (Rms(x) > atLeast) return x;
                    Thread.Sleep(20);
                }
                return Play(seconds);
            }

            public int Rate => ReBuzzCore.masterInfo.SamplesPerSec;

            public float[] Play(double seconds)
            {
                int frames = (int)(seconds * Rate);
                var output = new float[frames];
                for (int at = 0; at < frames; at += 256)
                {
                    int n = Math.Min(256, frames - at);
                    reader.Read(n);
                    for (int i = 0; i < n; i++) output[at + i] = reader.Buffer[2 * i];
                }
                return output;
            }

            /// <summary>Both channels (the mix and master machines change the image).</summary>
            public (float[] L, float[] R) PlayStereo(double seconds)
            {
                int frames = (int)(seconds * Rate);
                var l = new float[frames];
                var r = new float[frames];
                for (int at = 0; at < frames; at += 256)
                {
                    int n = Math.Min(256, frames - at);
                    reader.Read(n);
                    for (int i = 0; i < n; i++)
                    {
                        l[at + i] = reader.Buffer[2 * i];
                        r[at + i] = reader.Buffer[2 * i + 1];
                    }
                }
                return (l, r);
            }

            public IParameter Param(string name) =>
                Effect.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters.Single(p => p.Name == name);

            public void Dispose()
            {
                Core.Playing = false;
                Driver.Dispose();
            }
        }

        static double Rms(float[] x, int from = 0)
        {
            double s = 0;
            for (int i = from; i < x.Length; i++) s += x[i] * (double)x[i];
            return Math.Sqrt(s / Math.Max(1, x.Length - from));
        }

        static double Db(double r) => 20 * Math.Log10(Math.Max(1e-12, r));

        /// <summary>Renders a machine's editor (from its GUI factory) to a PNG; returns its size.</summary>
        static Size ShootEditor(MachineCore machine, string name)
        {
            var factory = machine.DLL.GUIFactory;
            Assert.That(factory, Is.Not.Null, "the machine has a GUI factory");
            var gui = factory!.CreateGUI(null!);
            gui.Machine = machine;
            var element = (FrameworkElement)gui;
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = element.DesiredSize;
            Assert.That(size.Width, Is.GreaterThan(200));
            Assert.That(size.Height, Is.GreaterThan(150));
            SessionViewTests.Screenshot(element, name, (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
            return size;
        }

        // ------------------------------------------------------------------ tests

        [Test]
        public void ChordOnSeveralTracksReachesAManagedMachineWhole()
        {
            using var driver = new Driver();
            driver.Gear.AddDynamicGenerator(ProbeInfo);
            driver.Start();
            var probe = new DynamicMachineController("Neo Chord Probe", "Probe");
            driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(probe);
            var machine = driver.MachineFor(probe);
            machine.TrackCount = 3;
            var note = machine.ParameterGroups[2].Parameters.Single(p => p.Name == "Note");
            int[] chord = { (4 << 4) + 1, (4 << 4) + 5, (4 << 4) + 8 };   // C-4, E-4, G-4
            for (int t = 0; t < 3; t++) note.SetValue(t, chord[t]);
            var reader = driver.CreateAudioReader(256);
            for (int i = 0; i < 40; i++) reader.Read(256);   // a few ticks

            var instance = machine.ManagedMachine;
            var calls = (int[])instance.GetType().GetField("Calls")!.GetValue(instance)!;
            var last = (int[])instance.GetType().GetField("Last")!.GetValue(instance)!;
            Assert.That(calls.Take(3).ToArray(), Is.EqualTo(new[] { 1, 1, 1 }), "each track's note arrived once");
            Assert.That(last.Take(3).ToArray(), Is.EqualTo(chord));
        }

        [TestCase(".bmx")]
        [TestCase(".bmxml")]
        [Apartment(System.Threading.ApartmentState.STA)]      // the .bmxml loader creates UI objects
        public void AnOpenedSongHasItsStateParametersAndPlaysNoNotes(string extension)
        {
            // Fork (studio-mvp.md S9): the files keep stateless parameters' values (notes too). BMX's
            // loader restores state parameters only, as Buzz does; BMXML's restored every value, so a
            // song saved after playing opened with its last notes sounding.
            using var driver = new Driver();
            driver.Gear.AddDynamicGenerator(ProbeInfo);
            driver.Start();
            var probe = new DynamicMachineController("Neo Chord Probe", "Probe");
            driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(probe);
            var machine = driver.MachineFor(probe);
            machine.TrackCount = 2;
            machine.ParameterGroups[1].Parameters.Single(p => p.Name == "Dummy").SetValue(0, 1);
            var note = machine.ParameterGroups[2].Parameters.Single(p => p.Name == "Note");
            note.SetValue(0, (4 << 4) + 1);
            note.SetValue(1, (4 << 4) + 5);
            var reader = driver.CreateAudioReader(256);

            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "stateless-" + Guid.NewGuid().ToString("N") + extension);
            driver.Core.SaveSongCopy(path);
            driver.Core.Modified = false;
            driver.Core.OpenSongFile(path);
            driver.AssertNoErrorsReportedToUser();
            for (int i = 0; i < 40; i++) reader.Read(256);   // a few ticks

            var opened = driver.Core.SongCore.MachinesList.Single(m => m.DLL.Name == "Neo Chord Probe");
            Assert.That(opened, Is.Not.SameAs(machine), "the song was opened");
            Assert.That(opened.ParameterGroups[1].Parameters.Single(p => p.Name == "Dummy").GetValue(0), Is.EqualTo(1), "a state parameter is the song's");
            var instance = opened.ManagedMachine;
            var calls = (int[])instance.GetType().GetField("Calls")!.GetValue(instance)!;
            Assert.That(calls.Take(2).ToArray(), Is.EqualTo(new[] { 0, 0 }), "no note was played when the song opened");
        }

        [Test]
        public void NeoDriveSaturatesAndShowsItsEditor()
        {
            using var rig = new Rig(Drive);
            var global = rig.Effect.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global);
            Assert.That(global.Parameters.Select(p => p.Name).Take(4).ToArray(), Is.EqualTo(new[] { "Mode", "Drive", "Bias", "Character" }));
            Assert.That(rig.Param("Drive").DescribeValue(500), Is.EqualTo("18.0 dB"));
            Assert.That(rig.Param("Mode").DescribeValue(2), Is.EqualTo("Transistor"));
            Assert.That(rig.Effect.Latency, Is.GreaterThan(0), "the oversampling latency is reported");

            rig.Param("Mix").SetValue(0, 0);
            rig.Play(0.3);
            double dry = Rms(rig.Play(0.3));
            rig.Param("Mix").SetValue(0, 1000);
            rig.Param("Mode").SetValue(0, 2);
            rig.Param("Drive").SetValue(0, 1000);
            rig.Param("Auto gain").SetValue(0, 0);
            rig.Play(0.3);
            var wet = rig.Play(0.3);
            TestContext.Out.WriteLine($"dry {Db(dry):F1} dB, driven {Db(Rms(wet)):F1} dB");
            Assert.That(dry, Is.GreaterThan(1e-4), "sound passes");
            Assert.That(Rms(wet), Is.GreaterThan(dry), "36 dB of drive without auto gain is louder");

            ShootEditor(rig.Effect, "neo-drive-editor");
        }

        [Test]
        public void NeoCompCompressesReportsItsLookaheadAndShowsItsEditor()
        {
            using var rig = new Rig(Comp);
            Assert.That(rig.Effect.InputChannelCount, Is.EqualTo(2), "input 2 is the sidechain");
            rig.Play(0.2);
            double open = Rms(rig.Play(0.3));
            rig.Param("Threshold").SetValue(0, 0);      // −60 dB
            rig.Param("Ratio").SetValue(0, 1000);       // ∞
            rig.Play(0.3);
            double squashed = Rms(rig.Play(0.3));
            TestContext.Out.WriteLine($"open {Db(open):F1} dB, compressed {Db(squashed):F1} dB");
            Assert.That(open, Is.GreaterThan(1e-4));
            Assert.That(Db(squashed / open), Is.LessThan(-10));

            Assert.That(rig.Effect.Latency, Is.EqualTo(0));
            rig.Param("Lookahead").SetValue(0, 1000);   // 5 ms
            rig.Play(0.1);
            Assert.That(rig.Effect.Latency, Is.EqualTo((int)Math.Round(0.005 * rig.Rate)), "the new latency reached ReBuzz");

            ShootEditor(rig.Effect, "neo-comp-editor");
        }

        [Test]
        public void NeoVerbRingsOnAndShowsItsEditor()
        {
            using var rig = new Rig(Verb);
            rig.Param("Mix").SetValue(0, 1000);
            rig.Param("Decay").SetValue(0, 300);
            rig.Play(0.5);
            rig.Sine.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters[0].SetValue(0, 0);
            rig.Play(0.05);
            var tail = rig.Play(0.2);
            TestContext.Out.WriteLine($"tail {Db(Rms(tail)):F1} dB");
            Assert.That(Rms(tail), Is.GreaterThan(1e-4), "the tail rings after the input stops");
            var later = rig.Play(4);
            Assert.That(Rms(later, later.Length - rig.Rate / 2), Is.LessThan(Rms(tail) / 100), "and dies away");

            ShootEditor(rig.Effect, "neo-verb-editor");
        }

        [Test]
        public void NeoEnsembleFlangesBoundedPassesTheDryAtMixZeroAndShowsItsEditor()
        {
            using var rig = new Rig(Ensemble);
            Assert.That(rig.Effect.Latency, Is.EqualTo(0));
            Assert.That(rig.Param("Feedback").DescribeValue(rig.Param("Feedback").MaxValue), Is.EqualTo("99.0%"));
            Assert.That(rig.Param("Feedback").DescribeValue(rig.Param("Feedback").MinValue), Is.EqualTo("−99.0%"));
            Assert.That(rig.Param("Mode").DescribeValue(1), Is.EqualTo("Flanger"));
            Assert.That(rig.Param("Sync").DescribeValue(7), Is.EqualTo("1/4"));

            // Mix 0 %: the sine reaches the master as it does without the machine (bypassed).
            rig.Param("Mix").SetValue(0, 0);
            rig.Play(0.3);
            var dry = rig.Play(0.3);
            rig.Effect.IsBypassed = true;
            rig.Play(0.1);
            var bypassed = rig.Play(0.3);
            rig.Effect.IsBypassed = false;
            rig.Play(0.1);
            TestContext.Out.WriteLine($"Mix 0 %: {Db(Rms(dry)):F3} dB, bypassed {Db(Rms(bypassed)):F3} dB");
            Assert.That(Db(Rms(dry) / Rms(bypassed)), Is.EqualTo(0).Within(0.001), "Mix 0 % passes the input");

            // A flanger at full feedback, full depth and the fastest LFO: bounded, and it dies away once the input stops.
            rig.Param("Mode").SetValue(0, 1);
            rig.Param("Delay").SetValue(0, 400);                               // 1.5 ms
            rig.Param("Mix").SetValue(0, 1000);
            rig.Param("Feedback").SetValue(0, rig.Param("Feedback").MaxValue);
            rig.Param("Depth").SetValue(0, 1000);
            rig.Param("Rate").SetValue(0, 1000);
            var loud = rig.Play(4);
            double peak = loud.Max(v => (double)Math.Abs(v));
            TestContext.Out.WriteLine($"full feedback: {Db(Rms(loud)):F1} dB RMS, peak {Db(peak):F1} dBFS");
            Assert.That(float.IsFinite((float)peak), Is.True);
            Assert.That(Db(peak), Is.LessThan(18), "far under the guard's +60 dBFS");
            Assert.That(Rms(loud), Is.GreaterThan(1e-3));
            Assert.That(rig.Effect.IsMuted, Is.False, "the guard never tripped");
            rig.Sine.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters[0].SetValue(0, 0);
            rig.Play(2);
            var tail = rig.Play(0.5);
            Assert.That(Rms(tail), Is.LessThan(Rms(loud) / 1000), "and dies away");

            // The editor, as a phaser with feedback.
            rig.Sine.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters[0].SetValue(0, 50);
            rig.Param("Mode").SetValue(0, 2);
            rig.Param("Mix").SetValue(0, 500);
            rig.Param("Feedback").SetValue(0, 1500);
            rig.Param("Depth").SetValue(0, 600);
            rig.Param("Rate").SetValue(0, 400);
            rig.Play(0.5);
            var size = ShootEditor(rig.Effect, "neo-ensemble-editor");
            TestContext.Out.WriteLine($"editor {size.Width:0} x {size.Height:0}");
        }

        [Test]
        public void NeoDelayEchoesBoundedPassesTheDryAtMixZeroAndShowsItsEditor()
        {
            using var rig = new Rig(Delay);
            Assert.That(rig.Effect.Latency, Is.EqualTo(0));
            Assert.That(rig.Param("Feedback").DescribeValue(rig.Param("Feedback").MaxValue), Is.EqualTo("99.0%"));
            Assert.That(rig.Param("Feedback").DescribeValue(rig.Param("Feedback").MinValue), Is.EqualTo("−99.0%"));
            Assert.That(rig.Param("Mode").DescribeValue(1), Is.EqualTo("Ping-pong"));
            Assert.That(rig.Param("Sync").DescribeValue(4), Is.EqualTo("1/8"));
            Assert.That(rig.Param("Feel").DescribeValue(1), Is.EqualTo("Dotted"));
            Assert.That(rig.Param("Time").DescribeValue(6959), Is.EqualTo("375 ms"));
            Assert.That(rig.Param("Low cut").DescribeValue(0), Is.EqualTo("20.0 Hz"));

            // Mix 0 %: the sine reaches the master as it does without the machine (bypassed).
            rig.Param("Mix").SetValue(0, 0);
            rig.Play(0.3);
            var dry = rig.Play(0.3);
            rig.Effect.IsBypassed = true;
            rig.Play(0.1);
            var bypassed = rig.Play(0.3);
            rig.Effect.IsBypassed = false;
            rig.Play(0.1);
            TestContext.Out.WriteLine($"Mix 0 %: {Db(Rms(dry)):F3} dB, bypassed {Db(Rms(bypassed)):F3} dB");
            Assert.That(Db(Rms(dry) / Rms(bypassed)), Is.EqualTo(0).Within(0.001), "Mix 0 % passes the input");

            // Wet only, no feedback, 100 ms: the echo is the sine, and it stops once the sine has.
            var level = rig.Sine.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters[0];
            rig.Param("Mix").SetValue(0, 1000);
            rig.Param("Sync").SetValue(0, 0);
            rig.Param("Time").SetValue(0, 4593);                                // 50 ms
            rig.Param("Feedback").SetValue(0, 990);                             // 0 %
            rig.Param("Low cut").SetValue(0, 0);
            rig.Param("High cut").SetValue(0, 1000);
            rig.Play(0.5);
            var echo = rig.Play(0.5);
            TestContext.Out.WriteLine($"the echo of the sine: {Db(Rms(echo) / Rms(bypassed)):F3} dB against the dry sine");
            Assert.That(Db(Rms(echo) / Rms(bypassed)), Is.EqualTo(0).Within(0.05), "a clean echo is the input, later");
            level.SetValue(0, 0);
            rig.Play(0.5);
            Assert.That(Rms(rig.Play(0.3)), Is.LessThan(1e-5), "no feedback: silent once the sine has stopped");

            // Full feedback, the deepest and fastest wow, the time moving: bounded, still ringing a second later, and
            // the guard never trips.
            level.SetValue(0, 50);
            rig.Param("Feedback").SetValue(0, rig.Param("Feedback").MaxValue);
            rig.Param("Mod depth").SetValue(0, 1000);
            rig.Param("Mod rate").SetValue(0, 1000);
            rig.Param("Mode").SetValue(0, 2);
            var loud = rig.Play(2);
            rig.Param("Time").SetValue(0, 5300);
            loud = loud.Concat(rig.Play(2)).ToArray();
            double peak = loud.Max(v => (double)Math.Abs(v));
            TestContext.Out.WriteLine($"full feedback: {Db(Rms(loud)):F1} dB RMS, peak {Db(peak):F1} dBFS");
            Assert.That(float.IsFinite((float)peak), Is.True);
            Assert.That(Db(peak), Is.LessThan(18), "far under the guard's +60 dBFS");
            Assert.That(rig.Effect.IsMuted, Is.False, "the guard never tripped");
            level.SetValue(0, 0);
            rig.Play(1);
            Assert.That(Rms(rig.Play(0.5)), Is.GreaterThan(1e-4), "99 % feedback rings on after the input stops");

            // The editor, as a ping-pong delay with the filters and ducking on.
            level.SetValue(0, 50);
            rig.Param("Mode").SetValue(0, 1);
            rig.Param("Sync").SetValue(0, 4);
            rig.Param("Feedback").SetValue(0, 1490);                            // +50 %
            rig.Param("Mod depth").SetValue(0, 200);
            rig.Param("Low cut").SetValue(0, 400);
            rig.Param("High cut").SetValue(0, 750);
            rig.Param("Duck").SetValue(0, 120);
            rig.Param("Mix").SetValue(0, 400);
            rig.Play(0.5);
            var size = ShootEditor(rig.Effect, "neo-delay-editor");
            // A [notice] (tools/ci-test-summary.ps1): the WPF editor's size, which the studio's must match (NeoEditorTests).
            TestContext.Out.WriteLine($"[notice] Neo Delay in ReBuzz: Mix 0 % {Db(Rms(dry) / Rms(bypassed)):+0.000;-0.000} dB against bypassed, the clean echo {Db(Rms(echo) / Rms(bypassed)):+0.000;-0.000} dB, " +
                                      $"full feedback peak {Db(peak):+0.0;-0.0} dBFS, editor {size.Width:0} x {size.Height:0}");
        }

        [Test]
        public void NeoFilterFiltersSelfOscillatesBoundedPassesTheDryAtMixZeroAndShowsItsEditor()
        {
            using var rig = new Rig(Filter);
            Assert.That(rig.Effect.Latency, Is.EqualTo(0));
            Assert.That(rig.Param("Type").DescribeValue(0), Is.EqualTo("Ladder 24"));
            Assert.That(rig.Param("Type").DescribeValue(6), Is.EqualTo("SVF Peak"));
            Assert.That(rig.Param("Cutoff").DescribeValue(6667), Is.EqualTo("2.00 kHz"));
            Assert.That(rig.Param("Resonance").DescribeValue(66), Is.EqualTo("0.71"));
            Assert.That(rig.Param("LFO sync").DescribeValue(9), Is.EqualTo("1/4"));
            Assert.That(rig.Param("Drive").DescribeValue(240), Is.EqualTo("24.0 dB"));

            // Mix 0 %: the 220 Hz sine reaches the master as it does without the machine (bypassed).
            rig.Param("Mix").SetValue(0, 0);
            rig.Play(0.3);
            var dry = rig.Play(0.3);
            rig.Effect.IsBypassed = true;
            rig.Play(0.1);
            var bypassed = rig.Play(0.3);
            rig.Effect.IsBypassed = false;
            rig.Play(0.1);
            Assert.That(Db(Rms(dry) / Rms(bypassed)), Is.EqualTo(0).Within(0.001), "Mix 0 % passes the input");

            // The SVF low-pass with its cutoff on the sine, Q 0.71: −3.0 dB; the high-pass two octaves above it: −24.1 dB.
            rig.Param("Mix").SetValue(0, 1000);
            rig.Param("Type").SetValue(0, 2);
            rig.Param("Cutoff").SetValue(0, (int)Math.Round(10000 * Math.Log(220.0 / 20) / Math.Log(1000)));
            rig.Param("Resonance").SetValue(0, 65);                             // Q 0.705
            rig.Play(0.5);
            var lowpass = rig.Play(0.5);
            rig.Param("Type").SetValue(0, 4);
            rig.Param("Cutoff").SetValue(0, (int)Math.Round(10000 * Math.Log(880.0 / 20) / Math.Log(1000)));
            rig.Play(0.5);
            var highpass = rig.Play(0.5);
            TestContext.Out.WriteLine($"low-pass at the sine {Db(Rms(lowpass) / Rms(bypassed)):F3} dB, high-pass two octaves up {Db(Rms(highpass) / Rms(bypassed)):F3} dB");
            Assert.That(Db(Rms(lowpass) / Rms(bypassed)), Is.EqualTo(-3.0).Within(0.15), "the low-pass at its cutoff");
            Assert.That(Db(Rms(highpass) / Rms(bypassed)), Is.EqualTo(-24.1).Within(0.4), "the high-pass two octaves past it");

            // Resonance at its top: the ladder sings at its cutoff on its own once the sine stops, bounded; the guard never trips.
            var level = rig.Sine.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters[0];
            rig.Param("Type").SetValue(0, 0);
            rig.Param("Resonance").SetValue(0, rig.Param("Resonance").MaxValue);
            rig.Param("Drive").SetValue(0, 240);
            rig.Play(1);
            level.SetValue(0, 0);
            rig.Play(1);
            var sings = rig.Play(1);
            double peak = sings.Max(v => (double)Math.Abs(v));
            TestContext.Out.WriteLine($"self-oscillation: {Db(Rms(sings)):F1} dB RMS, peak {Db(peak):F1} dBFS");
            Assert.That(Rms(sings), Is.GreaterThan(0.01), "it rings on by itself");
            Assert.That(Db(peak), Is.LessThan(0), "bounded");
            Assert.That(rig.Effect.IsMuted, Is.False, "the guard never tripped");

            // The editor, sweeping: the ladder with an LFO on the song's quarter notes and the envelope.
            level.SetValue(0, 50);
            rig.Param("Resonance").SetValue(0, 500);
            rig.Param("Drive").SetValue(0, 60);
            rig.Param("LFO sync").SetValue(0, 9);
            rig.Param("LFO depth").SetValue(0, 150);
            rig.Param("Env amount").SetValue(0, 800);
            rig.Play(0.5);
            var size = ShootEditor(rig.Effect, "neo-filter-editor");
            // A [notice] (tools/ci-test-summary.ps1): the WPF editor's size, which the studio's must match (NeoEditorTests).
            TestContext.Out.WriteLine($"[notice] Neo Filter in ReBuzz: Mix 0 % {Db(Rms(dry) / Rms(bypassed)):+0.000;-0.000} dB against bypassed, low-pass at its cutoff {Db(Rms(lowpass) / Rms(bypassed)):+0.000;-0.000} dB, " +
                                      $"high-pass two octaves up {Db(Rms(highpass) / Rms(bypassed)):+0.00;-0.00} dB, self-oscillation peak {Db(peak):+0.0;-0.0} dBFS, editor {size.Width:0} x {size.Height:0}");
        }

        [Test]
        public void NeoMultibandReportsItsLatencyReBuzzCompensatesItAndShowsItsEditor()
        {
            // sine → Neo Multiband → master, and beside it sine → Neo Utility (both channels inverted) → master. Linear phase reports
            // 4607 samples (at 44.1 and 48 kHz); with every band at 1:1 the multiband passes the sine, delayed by exactly that, so
            // when ReBuzz delays the other path to match (machine delay compensation) the two cancel. Minimum phase reports 0, and
            // ReBuzz sees the change. Compressing, band 2 (where the 220 Hz sine is) turns it down.
            using var rig = new Rig(Multiband, Utility);
            rig.Play(0.05);
            Assert.That(rig.Effect.Latency, Is.EqualTo(4607), "linear phase: the FIRs' centre and one partition");
            Assert.That(rig.Param("Phase").DescribeValue(0), Is.EqualTo("Linear"));
            Assert.That(rig.Param("B2 ratio").DescribeValue(200), Is.EqualTo("1.0:1"));
            Assert.That(rig.Param("Crossover 2").DescribeValue(rig.Param("Crossover 2").DefValue), Is.EqualTo("1000 Hz"));
            for (int b = 1; b <= 4; b++) rig.Param($"B{b} ratio").SetValue(0, 200);   // 1:1
            var utility = rig.Parallel.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters;

            // The multiband alone (the inverted path muted by its gain): the sine as it went in.
            utility.Single(p => p.Name == "Gain").SetValue(0, 0);                         // −∞
            rig.Play(0.5);
            double alone = Rms(rig.Play(0.5));
            rig.Effect.IsBypassed = true;
            rig.Play(0.3);
            double bypassed = Rms(rig.Play(0.5));
            rig.Effect.IsBypassed = false;
            rig.Play(0.3);
            // Both paths, the other one inverted: compensated, nothing is left.
            utility.Single(p => p.Name == "Gain").SetValue(0, utility.Single(p => p.Name == "Gain").DefValue);
            utility.Single(p => p.Name == "Invert L").SetValue(0, 1);
            utility.Single(p => p.Name == "Invert R").SetValue(0, 1);
            rig.Play(0.5);
            double residual = Rms(rig.Play(0.5));
            TestContext.Out.WriteLine($"unity {Db(alone / bypassed):F4} dB against bypassed; against the inverted parallel path {Db(residual / alone):F1} dB");
            Assert.That(Db(alone / bypassed), Is.EqualTo(0).Within(0.001), "every band at 1:1 passes the input");
            Assert.That(Db(residual / alone), Is.LessThan(-90), "ReBuzz delayed the parallel path by the latency: they cancel");

            // Minimum phase: no latency, and ReBuzz sees it.
            rig.Param("Phase").SetValue(0, 1);
            rig.Play(0.3);
            Assert.That(rig.Effect.Latency, Is.EqualTo(0), "minimum phase: the new latency reached ReBuzz");
            rig.Param("Phase").SetValue(0, 0);
            rig.Play(0.3);
            Assert.That(rig.Effect.Latency, Is.EqualTo(4607), "and back");

            // Compressing band 2 hard: the sine comes down.
            utility.Single(p => p.Name == "Gain").SetValue(0, 0);
            rig.Param("B2 threshold").SetValue(0, 0);                                   // −60 dB
            rig.Param("B2 ratio").SetValue(0, rig.Param("B2 ratio").MaxValue);          // ∞
            rig.Play(0.5);
            double squashed = Rms(rig.Play(0.5));
            TestContext.Out.WriteLine($"band 2 at ∞:1 from −60 dB: {Db(squashed / alone):F1} dB");
            Assert.That(Db(squashed / alone), Is.LessThan(-15), "band 2 compresses the sine");
            Assert.That(rig.Effect.IsMuted, Is.False, "the guard never tripped");

            // The editor, compressing on every band.
            for (int b = 1; b <= 4; b++)
            {
                rig.Param($"B{b} ratio").SetValue(0, 600);                               // 4:1
                rig.Param($"B{b} threshold").SetValue(0, 250);                           // −35 dB
            }
            rig.Play(0.5);
            var size = ShootEditor(rig.Effect, "neo-multiband-editor");
            // A [notice] (tools/ci-test-summary.ps1): the WPF editor's size, which the studio's must match (NeoEditorTests).
            TestContext.Out.WriteLine($"[notice] Neo Multiband in ReBuzz: latency {4607} samples compensated (residual {Db(residual / alone):F1} dB against the inverted parallel path), " +
                                      $"unity {Db(alone / bypassed):+0.0000;-0.0000;0.0000} dB, editor {size.Width:0} x {size.Height:0}");
        }

        [Test]
        public void NeoMultibandsLookaheadJoinsItsLatencyInEveryPhaseAndTheParallelPathFollows()
        {
            // docs/specs/mastering-tools.md §5.14: a 3 ms lookahead is latency on top of the phase's own (4607 samples in linear phase,
            // none in minimum and Dynamic phase), reported to ReBuzz, which delays the inverted parallel path by it: every band at 1:1
            // passes the sine, delayed by exactly that, so the two cancel. The second input is the sidechain (its channel names).
            using var rig = new Rig(Multiband, Utility);
            for (int b = 1; b <= 4; b++) rig.Param($"B{b} ratio").SetValue(0, 200);   // 1:1
            rig.Param("Lookahead").SetValue(0, 300);                                    // 3 ms
            var utility = rig.Parallel.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters;
            utility.Single(p => p.Name == "Invert L").SetValue(0, 1);
            utility.Single(p => p.Name == "Invert R").SetValue(0, 1);
            var lines = new System.Collections.Generic.List<string>();
            foreach (var (phase, name, baseLatency) in new[] { (0, "linear", 4607), (1, "minimum", 0), (2, "Dynamic", 0) })
            {
                rig.Param("Phase").SetValue(0, phase);
                rig.Play(0.6);
                int added = rig.Effect.Latency - baseLatency;
                Assert.That(added, Is.InRange(132, 144), $"{name}: the lookahead (3 ms is 132 samples at 44.1 kHz, 144 at 48 kHz) on top of the phase's latency");
                rig.Play(0.3);
                double residual = Rms(rig.Play(0.5));
                lines.Add($"{name} +{added} samples, residual {Db(residual):F1} dBFS");
                // Minimum phase sums its bands to an all-pass: lined up, not nulled.
                if (phase == 1) Assert.That(residual, Is.LessThan(0.5), $"{name}: compensated, they line up");
                else Assert.That(residual, Is.LessThan(1e-7), $"{name}: compensated, they cancel");
            }
            Assert.That(rig.Param("Detector").DescribeValue(1), Is.EqualTo("RMS"));
            Assert.That(rig.Param("B1 sidechain").DescribeValue(1), Is.EqualTo("External"));
            Assert.That(rig.Param("B2 upward ratio").DescribeValue(0), Is.EqualTo("1.0:1"));
            Assert.That(rig.Param("Phase").DescribeValue(2), Is.EqualTo("Dynamic"));
            TestContext.Out.WriteLine($"[notice] Neo Multiband with a 3 ms lookahead in ReBuzz: {string.Join("; ", lines)}");
        }

        [Test]
        public void ABypassedLatentMachineKeepsItsLatencyAndALookaheadChangeMovesTheOtherPathSmoothly()
        {
            // Machine delay compensation (docs/specs/mastering-tools.md §4.1): on by default; a bypassed Neo Multiband keeps its
            // 4607 samples (its dry input delayed by them), so the inverted parallel path still cancels, through the switch too.
            using (var rig = new Rig(Multiband, Utility))
            {
                Assert.That(rig.Core.EngineSettings.MachineDelayCompensation, Is.True, "on by default");
                for (int b = 1; b <= 4; b++) rig.Param($"B{b} ratio").SetValue(0, 200);   // 1:1
                var utility = rig.Parallel.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters;
                utility.Single(p => p.Name == "Invert L").SetValue(0, 1);
                utility.Single(p => p.Name == "Invert R").SetValue(0, 1);
                rig.Play(0.5);
                double before = Rms(rig.Play(0.3));
                rig.Effect.IsBypassed = true;
                double through = Rms(rig.Play(0.5));
                rig.Effect.IsBypassed = false;
                double after = Rms(rig.Play(0.5));
                TestContext.Out.WriteLine($"[notice] Bypassing Neo Multiband beside an inverted parallel path: residual {Db(before):F1}, bypassed {Db(through):F1}, back {Db(after):F1} dBFS");
                Assert.That(before, Is.LessThan(1e-7), "compensated");
                Assert.That(through, Is.LessThan(1e-7), "the bypass keeps the latency");
                Assert.That(after, Is.LessThan(1e-7), "and back");
            }

            // Neo Limiter's lookahead changed while playing (2 → 5 ms): the parallel path's delay follows by a crossfade,
            // without a gap or a click (heard on the parallel path alone: the limiter's connection at 0, its latency still
            // counted; muting would stop its work here, where ProcessMutedMachines is off, so its lookahead wouldn't change).
            using (var rig = new Rig(Limiter, Utility))
            {
                rig.Param("Ceiling").SetValue(0, 120);                                   // 0 dBTP
                rig.Effect.Outputs.Single().Amp = 0;
                rig.Play(0.5);
                var steady = rig.Play(0.3);
                double own = 0;
                for (int i = 1; i < steady.Length; i++) own = Math.Max(own, Math.Abs(steady[i] - steady[i - 1]));
                rig.Param("Lookahead").SetValue(0, 450);                                 // 5 ms
                var moving = rig.Play(0.5);
                double step = 0, quietest = double.MaxValue;
                for (int i = 1; i < moving.Length; i++) step = Math.Max(step, Math.Abs(moving[i] - moving[i - 1]));
                for (int w = 0; w + 64 <= moving.Length; w += 64) quietest = Math.Min(quietest, Rms(moving[w..(w + 64)]));
                TestContext.Out.WriteLine($"[notice] Neo Limiter's lookahead 2 → 5 ms while playing: the parallel path's largest step {step / own:F3}× the sine's own, quietest 64 samples {quietest / Rms(steady):F3} of its level");
                Assert.That(rig.Effect.Latency, Is.EqualTo(LimiterLatency(5, rig.Rate)), "the new latency reached ReBuzz");
                Assert.That(step, Is.LessThan(1.2 * own), "no click");
                // The crossfade between the old and the new tap briefly sums two copies of the sine a few samples apart, which
                // comb-filter each other: a dip, not a gap (0.79 of the level measured here, 0.51 on Windows). A gap is silence.
                Assert.That(quietest, Is.GreaterThan(0.25 * Rms(steady)), "no gap");
            }
        }

        static readonly SourceInfo MixInfo = new("Neo Test Mix", MixSource, true);

        /// <summary>A mono 32-bit float WAV (for the wavetable).</summary>
        static void WriteWav(string path, float[] samples, int rate)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write("RIFF"u8.ToArray()); w.Write(36 + samples.Length * 4); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)32);
            w.Write("data"u8.ToArray()); w.Write(samples.Length * 4);
            foreach (var x in samples) w.Write(x);
        }

        /// <summary>Frequency by rising zero crossings over the window.</summary>
        static double Frequency(float[] x, int rate, int from = 0)
        {
            double first = -1, last = -1;
            int count = 0;
            for (int i = Math.Max(1, from); i < x.Length; i++)
                if (x[i - 1] < 0 && x[i] >= 0)
                {
                    double t = i - 1 + x[i - 1] / (x[i - 1] - x[i]);
                    if (first < 0) first = t;
                    last = t;
                    count++;
                }
            return count < 2 ? 0 : (count - 1) * (double)rate / (last - first);
        }

        [TestCase(".bmx")]
        [TestCase(".bmxml")]
        [Apartment(ApartmentState.STA)]
        public void NeoSamplerPlaysTheSongsWavesKeepsThemThroughASaveAndShowsItsEditor(string extension)
        {
            // A sine at 441 Hz, 48 kHz, into the song's wavetable (slot 1, root A4 = Buzz A-5); zone 1 plays it up to B5,
            // zone 2 the built-in kick from C6 (Buzz C-7). A-5 plays the wave at its own pitch, whatever the song's rate.
            using var rig = new Rig(Sampler);
            var wavePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "neo-sampler-sine-" + Guid.NewGuid().ToString("N") + ".wav");
            var sine = new float[48000];
            for (int i = 0; i < sine.Length; i++) sine[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 441 * i / 48000));
            WriteWav(wavePath, sine, 48000);
            var table = rig.Core.SongCore.Wavetable;
            table.LoadWave(0, wavePath, "Sine 441", false);
            table.Waves[0].Layers[0].RootNote = BuzzNote(5, 9);
            Assert.That(rig.Param("Z1 wave").DescribeValue(1), Is.EqualTo("Sine 441"), "a zone's wave reads as the slot's name");
            Assert.That(rig.Param("Z1 wave").DescribeValue(201), Is.EqualTo("Built-in Kick"));
            Assert.That(rig.Param("Z1 key high").DescribeValue(83), Is.EqualTo("B5"));
            Assert.That(rig.Param("Z1 root").DescribeValue(128), Is.EqualTo("Wave"));
            rig.Param("Z1 key high").SetValue(0, 83);
            rig.Param("Z2 wave").SetValue(0, 201);
            rig.Param("Z2 key low").SetValue(0, 84);
            rig.Param("Vel → amp").SetValue(0, 0);
            var machine = rig.Effect.ManagedMachine;
            var busy = machine.GetType().GetProperty("Busy")!;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (busy.GetValue(machine) is true && clock.ElapsedMilliseconds < 10000) Thread.Sleep(20);
            Assert.That(busy.GetValue(machine), Is.False, "the wave read in, the built-ins made");

            rig.Notes(BuzzNote(5, 9));
            var a = rig.PlayUntilSound(0.6);
            double f = Frequency(a, rig.Rate, rig.Rate / 10), level = Db(Rms(a, rig.Rate / 10));
            TestContext.Out.WriteLine($"the wave at its root: {f:F3} Hz, {level:F2} dB RMS (a 0.5 sine: {Db(0.5 / Math.Sqrt(2)):F2})");
            Assert.That(f, Is.EqualTo(441).Within(0.05), "at its own pitch");
            Assert.That(level, Is.GreaterThan(-30), "it sounds");
            rig.Notes(BuzzNote(7, 0));   // Buzz C-7 = C6 (MIDI 84)
            rig.Play(0.05);
            var kick = rig.Play(0.3);
            Assert.That(Rms(kick), Is.GreaterThan(1e-3), "zone 2's built-in kick");
            rig.Notes(255);
            rig.Play(1);

            // Saved and opened again: the zones and the wave come back with the song.
            string songPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "neo-sampler-" + Guid.NewGuid().ToString("N") + extension);
            rig.Core.SaveSongCopy(songPath);
            rig.Core.Modified = false;
            rig.Core.OpenSongFile(songPath);
            rig.Driver.AssertNoErrorsReportedToUser();
            var opened = rig.Core.SongCore.MachinesList.Single(m => m.DLL.Name == "Neo Sampler");
            Assert.That(opened, Is.Not.SameAs(rig.Effect), "the song was opened");
            var globals = opened.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters;
            Assert.That(globals.Single(p => p.Name == "Z1 key high").GetValue(0), Is.EqualTo(83));
            Assert.That(globals.Single(p => p.Name == "Z2 wave").GetValue(0), Is.EqualTo(201));
            Assert.That(globals.Single(p => p.Name == "Z2 key low").GetValue(0), Is.EqualTo(84));
            var wave = rig.Core.SongCore.Wavetable.Waves[0];
            Assert.That(wave?.Name, Is.EqualTo("Sine 441"), "the wave is the song's");
            Assert.That(wave!.Layers[0].RootNote, Is.EqualTo(BuzzNote(5, 9)));
            // The opened machine reads the song's wave in as the first one did (a render waits on Busy the same way).
            var openedMachine = opened.ManagedMachine;
            clock.Restart();
            while (busy.GetValue(openedMachine) is true && clock.ElapsedMilliseconds < 10000) Thread.Sleep(20);
            Assert.That(busy.GetValue(openedMachine), Is.False, "the opened song's wave read in");
            if (opened.TrackCount < 1) opened.TrackCount = 1;
            var note = opened.ParameterGroups[2].Parameters.Single(p => p.Name == "Note");
            note.SetValue(0, BuzzNote(5, 9));
            float[] b = null;
            for (int i = 0; i < 100; i++)
            {
                b = rig.Play(0.6);
                if (Rms(b) > 1e-3) break;
                Thread.Sleep(20);
            }
            double f2 = Frequency(b!, rig.Rate, rig.Rate / 10), level2 = Db(Rms(b!, rig.Rate / 10));
            TestContext.Out.WriteLine($"opened again ({extension}): {f2:F3} Hz, {level2:F2} dB RMS");
            Assert.That(f2, Is.EqualTo(441).Within(0.05), "the opened song plays its wave at its pitch");
            Assert.That(level2, Is.EqualTo(level).Within(0.1), "and level");
            note.SetValue(0, 255);
            rig.Play(1);

            if (extension == ".bmx")
            {
                var size = ShootEditor(opened, "neo-sampler-editor");
                // A [notice] (tools/ci-test-summary.ps1): the WPF editor's size, which the studio's must match (NeoEditorTests).
                TestContext.Out.WriteLine($"[notice] Neo Sampler in ReBuzz: the wave at its root {f:F3} Hz, {level:F2} dB; after {extension} {f2:F3} Hz, {level2:F2} dB; editor {size.Width:0} x {size.Height:0}");
            }
        }

        [TestCase(".bmx")]
        [TestCase(".bmxml")]
        [Apartment(ApartmentState.STA)]
        public void NeoDrumsPlaysFromItsKeysAndHitColumnsKeepsItsKitThroughASaveAndShowsItsEditor(string extension)
        {
            // C2 (Buzz C-3, MIDI 36) on the Note column plays the kick, at its tune once its sweep has settled; the SD column
            // plays the snare; a note-off stops nothing (a hit sounds out). Saved and opened again, the kit's settings come back.
            using var rig = new Rig(Drums);
            string dir = Path.GetDirectoryName(Drums.BuiltPath)!;
            Assert.That(File.Exists(Path.Combine(dir, "Neo Drums.prs.xml")), Is.True, "presets ship beside the DLL");
            Assert.That(File.Exists(Path.Combine(dir, "Neo Drums.machine.json")), Is.True, "and the manifest");
            Assert.That(rig.Param("Kick tune").DescribeValue(365), Is.EqualTo("54.0 Hz"));
            IParameter Track(MachineCore m, string name) => m.ParameterGroups[2].Parameters.Single(p => p.Name == name);

            rig.Notes(BuzzNote(3, 0));
            var kick = rig.PlayUntilSound(0.6);
            double f = Frequency(kick, rig.Rate, rig.Rate * 3 / 10), level = Db(Rms(kick));
            TestContext.Out.WriteLine($"C2: the kick at {f:F2} Hz (its tune 54.0 Hz), {level:F2} dB RMS over 0.6 s");
            Assert.That(f, Is.EqualTo(54.0).Within(1.0), "the kick on C2, at its tune");
            rig.Notes(255);
            var after = rig.Play(0.1);
            Assert.That(Rms(after), Is.GreaterThan(1e-5), "a note-off doesn't cut the hit");
            rig.Play(1.5);
            Assert.That(Rms(rig.Play(0.2)), Is.LessThan(1e-5), "and it falls silent");

            Track(rig.Effect, "SD").SetValue(0, 127);
            var snare = rig.Play(0.4);
            double crossings = Frequency(snare, rig.Rate);
            TestContext.Out.WriteLine($"SD 127: {Db(Rms(snare)):F2} dB RMS, zero crossings at {crossings:F0} a second");
            Assert.That(Rms(snare), Is.GreaterThan(1e-3), "the SD column plays the snare");
            Assert.That(crossings, Is.GreaterThan(150), "a snare, not the kick");
            rig.Play(1.5);

            // The kit's settings through the file: the kick tuned up (67.1 Hz) and the snare snappier.
            rig.Param("Kick tune").SetValue(0, 500);
            rig.Param("Snare snap").SetValue(0, 800);
            string songPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "neo-drums-" + Guid.NewGuid().ToString("N") + extension);
            rig.Core.SaveSongCopy(songPath);
            rig.Core.Modified = false;
            rig.Core.OpenSongFile(songPath);
            rig.Driver.AssertNoErrorsReportedToUser();
            var opened = rig.Core.SongCore.MachinesList.Single(m => m.DLL.Name == "Neo Drums");
            Assert.That(opened, Is.Not.SameAs(rig.Effect), "the song was opened");
            var globals = opened.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters;
            Assert.That(globals.Single(p => p.Name == "Kick tune").GetValue(0), Is.EqualTo(500));
            Assert.That(globals.Single(p => p.Name == "Snare snap").GetValue(0), Is.EqualTo(800));
            if (opened.TrackCount < 1) opened.TrackCount = 1;
            Track(opened, "Note").SetValue(0, BuzzNote(3, 0));
            float[] b = null;
            for (int i = 0; i < 100; i++)
            {
                b = rig.Play(0.6);
                if (Rms(b) > 1e-3) break;
                Thread.Sleep(20);
            }
            double f2 = Frequency(b!, rig.Rate, rig.Rate * 3 / 10);
            TestContext.Out.WriteLine($"opened again ({extension}): the kick at {f2:F2} Hz (its tune 67.1 Hz)");
            Assert.That(f2, Is.EqualTo(67.08).Within(1.2), "the opened song's kick at its new tune");
            Track(opened, "Note").SetValue(0, 255);
            rig.Play(1.5);

            if (extension == ".bmx")
            {
                var size = ShootEditor(opened, "neo-drums-editor");
                // A [notice] (tools/ci-test-summary.ps1): the WPF editor's size, which the studio's must match (NeoEditorTests).
                TestContext.Out.WriteLine($"[notice] Neo Drums in ReBuzz: C2's kick {f:F2} Hz, SD column {Db(Rms(snare)):F1} dB RMS; after {extension} {f2:F2} Hz; editor {size.Width:0} x {size.Height:0}");
            }
        }

        [Test]
        public void NeoAnalyzerPassesAudioThroughReadsWhatItMeasuresAndShowsItsEditor()
        {
            if (!File.Exists(Analyzer.BuiltPath)) Assert.Inconclusive($"{Analyzer.Name} not built ({Analyzer.BuiltPath}); build the solution first");
            Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(Analyzer.BuiltPath)!, "Neo Analyzer.prs.xml")), Is.True, "presets ship beside the DLL");
            Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(Analyzer.BuiltPath)!, "Neo Analyzer.machine.json")), Is.True, "and the manifest");
            using var driver = new Driver();
            driver.Gear.AddDynamicGenerator(MixInfo);
            driver.Gear.AddBuiltMachine(Analyzer, AbsoluteFilePath.Value(Analyzer.BuiltPath), false);
            driver.Start();
            var fx = new DynamicMachineController(Analyzer.Name, Analyzer.Name);
            driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(fx);
            var source = new DynamicMachineController("Neo Test Mix", "Mix");
            driver.MachineGraph.InsertMachineInstanceFor(source);
            driver.MachineGraph.Connect(source, fx);
            var analyzer = driver.MachineFor(fx);
            var mix = driver.MachineFor(source);
            var reader = driver.CreateAudioReader(256);
            IParameter Param(string name) => analyzer.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters.Single(p => p.Name == name);
            float[] Play(double seconds)
            {
                int frames = (int)(seconds * ReBuzzCore.masterInfo.SamplesPerSec);
                var left = new float[frames];
                for (int at = 0; at < frames; at += 256)
                {
                    int n = Math.Min(256, frames - at);
                    reader.Read(n);
                    for (int i = 0; i < n; i++) left[at + i] = reader.Buffer[2 * i];
                }
                return left;
            }
            var instance = analyzer.ManagedMachine;
            System.Collections.Generic.IReadOnlyDictionary<string, double> Readings() =>
                (System.Collections.Generic.IReadOnlyDictionary<string, double>)instance.GetType().GetProperty("Readings")!.GetValue(instance)!;

            Assert.That(Param("Smoothing").DescribeValue(Param("Smoothing").DefValue), Is.EqualTo("1/6 oct"));
            Assert.That(Param("Tilt").DescribeValue(2), Is.EqualTo("4.5 dB/oct"));
            Assert.That(Param("Target").DescribeValue(Param("Target").DefValue), Is.EqualTo("−14.0 dB"));
            Assert.That(Param("Peak hold").DescribeValue(Param("Peak hold").DefValue), Is.EqualTo("2.00 s"));
            Assert.That(analyzer.Latency, Is.EqualTo(0));

            // A 1 kHz sine peaking at −20 dBFS on both channels, through ReBuzz: the master gets it
            // unchanged, and the readings are what BS.1770 and the library say (EBU Tech 3341 case 1, at −20).
            var heard = Play(4);
            double rms = Rms(heard, heard.Length / 2);
            var r = Readings();
            TestContext.Out.WriteLine(string.Join(", ", r.Where(kv => !kv.Key.StartsWith("band_")).Select(kv => $"{kv.Key} {kv.Value:F3}")));
            Assert.That(Db(rms), Is.EqualTo(Db(0.1 / Math.Sqrt(2))).Within(0.01), "the master hears the sine as it was");
            Assert.That(r.Count, Is.EqualTo(48), "the manifest's 48 readings");
            Assert.That(r["integrated_lufs"], Is.EqualTo(-20).Within(0.1));
            Assert.That(r["short_term_lufs"], Is.EqualTo(-20).Within(0.1));
            Assert.That(r["true_peak_dbtp"], Is.EqualTo(-20).Within(0.1));
            Assert.That(r["band_1000hz_db"], Is.EqualTo(-23.01).Within(0.1), "a sine's band level is its RMS");
            Assert.That(r["mid_db"], Is.EqualTo(-23.01).Within(0.1));
            Assert.That(r["correlation"], Is.EqualTo(1).Within(1e-6));
            Assert.That(r["width"], Is.EqualTo(0).Within(1e-6));
            Assert.That(r["range_lu"], Is.EqualTo(0).Within(0.1));

            // Output equals input, bit for bit, at the machine's Work (loud random samples).
            instance.GetType().GetMethod("ResetReadings")!.Invoke(instance, null);
            var work = instance.GetType().GetMethod("Work", new[] { typeof(Sample[]), typeof(Sample[]), typeof(int), typeof(WorkModes) })!;
            var rng = new Random(5);
            var input = Enumerable.Range(0, 1024).Select(_ => new Sample((float)(rng.NextDouble() * 2 - 1) * 32000f, (float)(rng.NextDouble() * 2 - 1) * 32000f)).ToArray();
            var output = new Sample[1024];
            Assert.That((bool)work.Invoke(instance, new object[] { output, input, 1024, WorkModes.WM_READWRITE })!, Is.True, "sound: true");
            Assert.That(output.Zip(input).All(p => BitConverter.SingleToInt32Bits(p.First.L) == BitConverter.SingleToInt32Bits(p.Second.L)
                && BitConverter.SingleToInt32Bits(p.First.R) == BitConverter.SingleToInt32Bits(p.Second.R)), Is.True, "bit for bit");

            // The mix, after a reset through the parameter: the measures start again.
            mix.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters[0].SetValue(0, 1);
            Param("Reset").SetValue(0, 1);
            Play(5);
            r = Readings();
            TestContext.Out.WriteLine(string.Join(", ", r.Select(kv => $"{kv.Key} {kv.Value:F2}")));
            Assert.That(r["seconds"], Is.InRange(4.5, 5.1), "measured since the reset");
            Assert.That(r["integrated_lufs"], Is.InRange(-30.0, -5.0));
            Assert.That(r["correlation"], Is.InRange(0.2, 0.98), "a stereo mix");
            Assert.That(r["sub_db"], Is.GreaterThan(r["air_db"] + 10), "the 55 Hz bass is far louder than the air");

            Param("Tilt").SetValue(0, 1);
            Param("Target").SetValue(0, 180);   // −18
            ShootEditor(analyzer, "neo-analyzer-editor");
        }

        /// <summary>
        /// How long the WPF editor takes to draw everything (each GraphView invalidated, laid out, rendered into a bitmap), a
        /// frame on average over <paramref name="frames"/>, with <paramref name="between"/> run before each (the audio): the
        /// best of three such passes, as the studio's EditorFrameMs takes, since other work on a shared runner only adds time.
        /// </summary>
        static double EditorFrameMs(MachineCore machine, int frames, Action between)
        {
            var gui = machine.DLL.GUIFactory!.CreateGUI(null!);
            gui.Machine = machine;
            var element = (FrameworkElement)gui;
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = element.DesiredSize;
            element.Arrange(new Rect(0, 0, size.Width, size.Height));
            element.UpdateLayout();
            var graphs = new System.Collections.Generic.List<FrameworkElement>();
            void Walk(DependencyObject d)
            {
                if (d is FrameworkElement f && f.GetType().Name == "GraphView") graphs.Add(f);
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) Walk(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            }
            Walk(element);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(element);
            double best = double.MaxValue;
            for (int run = 0; run < 3; run++)
            {
                var timer = new System.Diagnostics.Stopwatch();
                for (int k = 0; k < frames; k++)
                {
                    between();
                    Thread.Sleep(12);                                   // the editors read a new snapshot at most every 10 ms
                    timer.Start();
                    foreach (var g in graphs) g.InvalidateVisual();
                    element.UpdateLayout();
                    bitmap.Render(element);
                    timer.Stop();
                }
                best = Math.Min(best, timer.Elapsed.TotalMilliseconds / frames);
            }
            Assert.That(graphs.Count, Is.GreaterThan(2), "the editor's graphs");
            return best;
        }

        [Test]
        public void NeoScopePassesAudioThroughReadsWhatItShowsAndShowsItsEditor()
        {
            if (!File.Exists(Scope.BuiltPath)) Assert.Inconclusive($"{Scope.Name} not built ({Scope.BuiltPath}); build the solution first");
            Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(Scope.BuiltPath)!, "Neo Scope.prs.xml")), Is.True, "presets ship beside the DLL");
            Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(Scope.BuiltPath)!, "Neo Scope.machine.json")), Is.True, "and the manifest");
            using var driver = new Driver();
            driver.Gear.AddDynamicGenerator(MixInfo);
            driver.Gear.AddBuiltMachine(Scope, AbsoluteFilePath.Value(Scope.BuiltPath), false);
            driver.Gear.AddBuiltMachine(Analyzer, AbsoluteFilePath.Value(Analyzer.BuiltPath), false);
            driver.Start();
            var fx = new DynamicMachineController(Scope.Name, Scope.Name);
            driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(fx);
            var source = new DynamicMachineController("Neo Test Mix", "Mix");
            driver.MachineGraph.InsertMachineInstanceFor(source);
            driver.MachineGraph.Connect(source, fx);
            var scope = driver.MachineFor(fx);
            var mix = driver.MachineFor(source);
            var reader = driver.CreateAudioReader(256);
            IParameter Param(string name) => scope.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters.Single(p => p.Name == name);
            float[] Play(double seconds)
            {
                int frames = (int)(seconds * ReBuzzCore.masterInfo.SamplesPerSec);
                var left = new float[frames];
                for (int at = 0; at < frames; at += 256)
                {
                    int n = Math.Min(256, frames - at);
                    reader.Read(n);
                    for (int i = 0; i < n; i++) left[at + i] = reader.Buffer[2 * i];
                }
                return left;
            }
            var instance = scope.ManagedMachine;
            System.Collections.Generic.IReadOnlyDictionary<string, double> Readings() =>
                (System.Collections.Generic.IReadOnlyDictionary<string, double>)instance.GetType().GetProperty("Readings")!.GetValue(instance)!;

            Assert.That(Param("Display").DescribeValue(Param("Display").DefValue), Is.EqualTo("L & R"));
            Assert.That(Param("Sync").DescribeValue(Param("Sync").DefValue), Is.EqualTo("1/4"));
            Assert.That(Param("Time/div").DescribeValue(Param("Time/div").DefValue), Is.EqualTo("2.00 ms"));
            Assert.That(Param("Level").DescribeValue(Param("Level").DefValue), Is.EqualTo("0.0%"));
            Assert.That(Param("Gain").DescribeValue(Param("Gain").DefValue), Is.EqualTo("0.0 dB"));
            Assert.That(scope.Latency, Is.EqualTo(0));

            // A 1 kHz sine peaking at −20 dBFS on both channels: the master hears it as it was; the readings are its peak, its
            // RMS as analyze reads it (−23.01 dB), mono; the trigger is off (synced).
            var heard = Play(4);
            double rms = Rms(heard, heard.Length / 2);
            var r = Readings();
            TestContext.Out.WriteLine(string.Join(", ", r.Select(kv => $"{kv.Key} {kv.Value:F3}")));
            Assert.That(Db(rms), Is.EqualTo(Db(0.1 / Math.Sqrt(2))).Within(0.01), "the master hears the sine as it was");
            Assert.That(r.Count, Is.EqualTo(4), "the manifest's 4 readings");
            Assert.That(r["peak_dbfs"], Is.EqualTo(-20).Within(0.05));
            Assert.That(r["rms_db"], Is.EqualTo(-23.01).Within(0.05));
            Assert.That(r["correlation"], Is.EqualTo(1).Within(1e-6));
            Assert.That(r["trigger_locked"], Is.EqualTo(0), "synced: no trigger");
            Param("Sync").SetValue(0, 0);                    // Free: Rising through 0 %
            Play(0.5);
            Assert.That(Readings()["trigger_locked"], Is.EqualTo(1), "free with the trigger: locked on the sine");

            // Output equals input, bit for bit, at the machine's Work (loud random samples).
            var work = instance.GetType().GetMethod("Work", new[] { typeof(Sample[]), typeof(Sample[]), typeof(int), typeof(WorkModes) })!;
            var rng = new Random(9);
            var input = Enumerable.Range(0, 1024).Select(_ => new Sample((float)(rng.NextDouble() * 2 - 1) * 32000f, (float)(rng.NextDouble() * 2 - 1) * 40000f)).ToArray();
            var output = new Sample[1024];
            Assert.That((bool)work.Invoke(instance, new object[] { output, input, 1024, WorkModes.WM_READWRITE })!, Is.True, "sound: true");
            Assert.That(output.Zip(input).All(p => BitConverter.SingleToInt32Bits(p.First.L) == BitConverter.SingleToInt32Bits(p.Second.L)
                && BitConverter.SingleToInt32Bits(p.First.R) == BitConverter.SingleToInt32Bits(p.Second.R)), Is.True, "bit for bit");

            // The editor on the stereo mix, and its cost against Neo Analyzer's (an analyzer after the same mix).
            mix.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters[0].SetValue(0, 1);
            Param("Sync").SetValue(0, 3);
            Play(2);
            var size = ShootEditor(scope, "neo-scope-editor");
            double scopeMs = EditorFrameMs(scope, 30, () => Play(1.0 / 30));
            var az = new DynamicMachineController(Analyzer.Name, "Analyzer");
            driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(az);
            driver.MachineGraph.Connect(source, az);
            Play(2);
            double analyzerMs = EditorFrameMs(driver.MachineFor(az), 30, () => Play(1.0 / 30));
            TestContext.Out.WriteLine($"[notice] Neo Scope in ReBuzz: a −20 dBFS sine through it within 0.01 dB, readings {r["peak_dbfs"]:F2} dBFS, {r["rms_db"]:F2} dB; " +
                                      $"WPF editor {size.Width:0} x {size.Height:0}, {scopeMs:F1} ms a frame (Neo Analyzer's {analyzerMs:F1} ms)");
            Assert.That(scopeMs, Is.LessThan(50), "a frame of the editor stays modest");
        }

        static int BuzzNote(int octave, int semitone) => (octave << 4) + semitone + 1;   // semitone 0 = C

        [Test]
        public void NeoPolyPlaysAChordStopsAndShowsItsEditor()
        {
            using var rig = new Rig(Poly);
            Assert.That(rig.Param("F1 cutoff").DescribeValue(rig.Param("F1 cutoff").DefValue), Does.EndWith("Hz"));
            rig.Notes(BuzzNote(4, 0), BuzzNote(4, 4), BuzzNote(4, 7));
            var chord = rig.PlayUntilSound(0.3);
            TestContext.Out.WriteLine($"chord {Db(Rms(chord)):F1} dB");
            Assert.That(Rms(chord), Is.GreaterThan(1e-3), "the chord sounds");
            rig.Notes(255, 255, 255);
            rig.Play(3);
            Assert.That(Rms(rig.Play(0.3)), Is.LessThan(Rms(chord) / 1000), "and stops after note-off");

            ShootEditor(rig.Effect, "neo-poly-editor");
        }

        [Test]
        public void NeoWavePlaysItsTablesAndAUserTableAndShowsItsEditor()
        {
            using var rig = new Rig(Wave);
            rig.Notes(BuzzNote(4, 0), BuzzNote(4, 7));
            var basic = rig.PlayUntilSound(0.3);
            TestContext.Out.WriteLine($"basic table {Db(Rms(basic)):F1} dB");
            Assert.That(Rms(basic), Is.GreaterThan(1e-3), "the built-in table sounds once built");

            // A user table: four frames from a WAV file, kept in the machine state.
            var machine = rig.Effect.ManagedMachine;
            var asm = machine.GetType().Assembly;
            var frames = new float[4 * 2048];
            for (int f = 0; f < 4; f++)
                for (int i = 0; i < 2048; i++)
                    for (int h = 1; h <= 1 + 4 * f; h++) frames[f * 2048 + i] += (float)(Math.Sin(2 * Math.PI * h * i / 2048) / h);
            var wav = (byte[])asm.GetType("NeoBuzz.Wave.WavReader")!.GetMethod("Write")!.Invoke(null, new object[] { frames, 48000, 2048 })!;
            var path = Path.Combine(Path.GetTempPath(), "Neo Wave test table.wav");
            File.WriteAllBytes(path, wav);
            machine.GetType().GetMethod("LoadUserTable", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(machine, new object[] { path });
            var stateProperty = machine.GetType().GetProperty("MachineState")!;
            var state = stateProperty.GetValue(machine)!;
            Assert.That((string)state.GetType().GetProperty("UserTableName")!.GetValue(state)!, Is.EqualTo("Neo Wave test table"));
            Assert.That(((string)state.GetType().GetProperty("UserTableData")!.GetValue(state)!).Length, Is.GreaterThan(4 * 2048 * 4));

            // Through XML, as songs store it, into the machine again.
            var xml = new System.Xml.Serialization.XmlSerializer(state.GetType());
            using var ms = new MemoryStream();
            xml.Serialize(ms, state);
            ms.Position = 0;
            stateProperty.SetValue(machine, xml.Deserialize(ms));

            rig.Param("A table").SetValue(0, 10);   // User
            rig.Param("A position").SetValue(0, 1000);
            rig.Play(0.1);
            var user = rig.Play(0.3);
            TestContext.Out.WriteLine($"user table {Db(Rms(user)):F1} dB");
            Assert.That(Rms(user), Is.GreaterThan(1e-3), "the user table sounds after a state round trip");

            ShootEditor(rig.Effect, "neo-wave-editor");
        }

        [Test]
        public void DyeEssencePlaysItsTablesAndAUserTableAndShowsItsEditor()
        {
            using var rig = new Rig(Dye);
            rig.Notes(BuzzNote(4, 0), BuzzNote(4, 7));
            var basic = rig.PlayUntilSound(0.3);
            TestContext.Out.WriteLine($"init {Db(Rms(basic)):F1} dB");
            Assert.That(Rms(basic), Is.GreaterThan(1e-3), "the built-in table sounds once built");

            // A user table for slot B: four frames from a WAV file, kept in the machine state.
            var machine = rig.Effect.ManagedMachine;
            var asm = machine.GetType().Assembly;
            var frames = new float[4 * 2048];
            for (int f = 0; f < 4; f++)
                for (int i = 0; i < 2048; i++)
                    for (int h = 1; h <= 1 + 4 * f; h++) frames[f * 2048 + i] += (float)(Math.Sin(2 * Math.PI * h * i / 2048) / h);
            var wav = (byte[])asm.GetType("NeoBuzz.Wave.WavReader")!.GetMethod("Write")!.Invoke(null, new object[] { frames, 48000, 2048 })!;
            var path = Path.Combine(Path.GetTempPath(), "Dye Essence test table.wav");
            File.WriteAllBytes(path, wav);
            machine.GetType().GetMethod("LoadUserTable", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(machine, new object[] { 1, path });
            var stateProperty = machine.GetType().GetProperty("MachineState")!;
            var state = stateProperty.GetValue(machine)!;
            Assert.That((string)state.GetType().GetProperty("TableBName")!.GetValue(state)!, Is.EqualTo("Dye Essence test table"));
            Assert.That(((string)state.GetType().GetProperty("TableBData")!.GetValue(state)!).Length, Is.GreaterThan(4 * 2048 * 4));

            // Through XML, as songs store it, into the machine again.
            var xml = new System.Xml.Serialization.XmlSerializer(state.GetType());
            using var ms = new MemoryStream();
            xml.Serialize(ms, state);
            ms.Position = 0;
            stateProperty.SetValue(machine, xml.Deserialize(ms));

            rig.Param("A engine").SetValue(0, 0);   // Off: only slot B sounds
            rig.Param("B engine").SetValue(0, 1);
            rig.Param("B table").SetValue(0, 10);   // User
            rig.Param("B position").SetValue(0, 1000);
            rig.Play(0.1);
            var user = rig.Play(0.3);
            TestContext.Out.WriteLine($"slot B's user table {Db(Rms(user)):F1} dB");
            Assert.That(Rms(user), Is.GreaterThan(1e-3), "the user table sounds after a state round trip");

            var size = ShootEditor(rig.Effect, "dye-essence-editor");
            TestContext.Out.WriteLine($"[notice] Dye Essence in ReBuzz: init {Db(Rms(basic)):F1} dB, slot B's user table {Db(Rms(user)):F1} dB after a state round trip, WPF editor {size.Width:0} x {size.Height:0}");
        }

        [Test]
        public void NeoFm6PlaysLoadsADx7BankAndShowsItsEditor()
        {
            using var rig = new Rig(Fm6);
            Assert.That(rig.Param("Op1 break").DescribeValue(39), Is.EqualTo("39 · C4"), "the machine's own value texts reach ReBuzz");
            rig.Notes(BuzzNote(4, 0), BuzzNote(4, 4), BuzzNote(4, 7));
            var chord = rig.PlayUntilSound(0.3);
            TestContext.Out.WriteLine($"init chord {Db(Rms(chord)):F1} dB");
            Assert.That(Rms(chord), Is.GreaterThan(1e-3), "the chord sounds");

            // A 32-voice bank written by the machine's own SysEx code, loaded as a user would.
            var machine = rig.Effect.ManagedMachine;
            var asm = machine.GetType().Assembly;
            var sysex = asm.GetType("NeoBuzz.Fm6.Sysex")!;
            var voiceType = sysex.GetNestedType("Voice", BindingFlags.NonPublic)!;
            var init = (byte[])sysex.GetMethod("InitVoice")!.Invoke(null, null)!;
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(voiceType))!;
            list.Add(Activator.CreateInstance(voiceType, "INIT VOICE", init));
            var bank = (byte[])sysex.GetMethod("WriteBank")!.Invoke(null, new object[] { list })!;
            var path = Path.Combine(Path.GetTempPath(), "Neo FM6 test bank.syx");
            File.WriteAllBytes(path, bank);
            machine.GetType().GetMethod("LoadBank", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(machine, new object[] { path });

            var stateProperty = machine.GetType().GetProperty("MachineState")!;
            var state = stateProperty.GetValue(machine)!;
            Assert.That((string)state.GetType().GetProperty("BankName")!.GetValue(state)!, Is.EqualTo("Neo FM6 test bank"));
            var xml = new System.Xml.Serialization.XmlSerializer(state.GetType());
            using var ms = new MemoryStream();
            xml.Serialize(ms, state);
            ms.Position = 0;
            stateProperty.SetValue(machine, xml.Deserialize(ms));
            var voices = (System.Collections.ICollection)machine.GetType().GetProperty("Bank", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(machine)!;
            Assert.That(voices.Count, Is.EqualTo(32), "the bank survives the song's XML");

            // Algorithm 32 with all six operators: still sounds after the change.
            rig.Param("Algorithm").SetValue(0, 31);
            for (int op = 2; op <= 6; op++) rig.Param($"Op{op} level").SetValue(0, 80);
            rig.Notes(255, 255, 255);
            rig.Play(0.5);
            rig.Notes(BuzzNote(4, 0));
            var organ = rig.PlayUntilSound(0.3);
            Assert.That(Rms(organ), Is.GreaterThan(1e-3));

            ShootEditor(rig.Effect, "neo-fm6-editor");
        }

        /// <summary>Neo Limiter's latency for a lookahead, rounded as the engine rounds it.</summary>
        static int LimiterLatency(double ms, int rate) => (int)Math.Round(ms * 0.001 * (double)(float)rate) + 11;

        [Test]
        public void NeoEqShapesTheSoundAndShowsItsEditor()
        {
            using var rig = new Rig(Eq);
            Assert.That(rig.Param("B3 freq").DescribeValue(rig.Param("B3 freq").DefValue), Is.EqualTo("250 Hz"));
            Assert.That(rig.Param("B3 gain").DescribeValue(rig.Param("B3 gain").DefValue), Is.EqualTo("0.0 dB"));
            Assert.That(rig.Param("B1 type").DescribeValue(3), Is.EqualTo("Low cut"));
            Assert.That(rig.Param("B1 slope").DescribeValue(2), Is.EqualTo("48 dB/oct"));
            Assert.That(rig.Effect.Latency, Is.EqualTo(0), "minimum phase: no latency");

            rig.Play(0.2);
            double flat = Rms(rig.Play(0.3));
            // Band 3 as a +12 dB bell on the test sine (220 Hz): B3 freq raw for 220 Hz is log(11)/log(1000) of the range.
            rig.Param("B3 freq").SetValue(0, (int)Math.Round(Math.Log(220 / 20.0) / Math.Log(1000) * 10000));
            rig.Param("B3 gain").SetValue(0, 480);   // +24 dB
            rig.Param("B3 Q").SetValue(0, 600);
            rig.Play(0.3);
            double boosted = Rms(rig.Play(0.3));
            rig.Param("B3 on").SetValue(0, 0);
            rig.Play(0.3);
            double off = Rms(rig.Play(0.3));
            TestContext.Out.WriteLine($"flat {Db(flat):F2} dB, +24 dB bell on the sine {Db(boosted):F2} dB, band off {Db(off):F2} dB");
            Assert.That(flat, Is.GreaterThan(1e-3), "sound passes");
            Assert.That(Db(boosted / flat), Is.EqualTo(24).Within(0.5), "the bell's gain at its frequency");
            Assert.That(Db(off / flat), Is.EqualTo(0).Within(0.01), "an off band passes the input");

            rig.Param("B3 on").SetValue(0, 1);
            rig.Param("B3 gain").SetValue(0, 180);   // −6 dB
            rig.Param("B7 gain").SetValue(0, 300);   // +6 dB high shelf
            rig.Param("B1 on").SetValue(0, 1);
            ShootEditor(rig.Effect, "neo-eq-editor");
        }

        [Test]
        public void NeoEqV2DynamicBandMovesItsGainAndShowsItsEditor()
        {
            // docs/specs/mastering-tools.md §5.3 (M3): two inputs (2 the sidechain); a dynamic band on the test sine (220 Hz).
            using var rig = new Rig(Eq);
            Assert.That(rig.Effect.InputChannelCount, Is.EqualTo(2), "input 2 is the sidechain");
            Assert.That(rig.Param("B3 channel").DescribeValue(4), Is.EqualTo("Side"));
            Assert.That(rig.Param("B3 shape").DescribeValue(3), Is.EqualTo("Vintage"));
            Assert.That(rig.Param("B3 dynamic").DescribeValue(1), Is.EqualTo("Down"));
            rig.Play(0.2);
            double flat = Rms(rig.Play(0.3));
            rig.Param("B3 freq").SetValue(0, (int)Math.Round(Math.Log(220 / 20.0) / Math.Log(1000) * 10000));
            rig.Param("B3 Q").SetValue(0, 600);
            rig.Param("B3 dynamic").SetValue(0, 1);       // Down
            rig.Param("B3 threshold").SetValue(0, 0);     // −60 dB
            rig.Param("B3 ratio").SetValue(0, 1000);      // ∞
            rig.Param("B3 range").SetValue(0, 120);       // 12 dB
            rig.Play(0.5);
            double ducked = Rms(rig.Play(0.3));
            TestContext.Out.WriteLine($"flat {Db(flat):F2} dB, the dynamic band down 12 dB on the sine {Db(ducked / flat):F2} dB");
            Assert.That(Db(ducked / flat), Is.EqualTo(-12).Within(0.5), "the band's gain moved by its range");
            Assert.That(rig.Effect.Latency, Is.EqualTo(0), "minimum phase: no latency");
            rig.Param("B6 channel").SetValue(0, 4);       // Side
            rig.Param("B6 gain").SetValue(0, 300);        // +6 dB
            rig.Param("B2 shape").SetValue(0, 3);         // Vintage
            rig.Param("B2 gain").SetValue(0, 300);
            ShootEditor(rig.Effect, "neo-eq-editor-v2");
        }

        [Test]
        public void NeoEqGraphicModeFollowsItsSlidersAndShowsItsEditor()
        {
            // machine-safety.md §4 (H2): Mode Graphic, 31 sliders at the ISO centres whose response passes through them.
            using var rig = new Rig(Eq);
            Assert.That(rig.Param("Mode").DescribeValue(0), Is.EqualTo("Parametric"));
            Assert.That(rig.Param("Mode").DescribeValue(1), Is.EqualTo("Graphic"));
            Assert.That(rig.Param("G 31.5 Hz").DescribeValue(rig.Param("G 31.5 Hz").DefValue), Is.EqualTo("0.0 dB"));
            Assert.That(rig.Param("G 20 kHz").DescribeValue(rig.Param("G 20 kHz").MaxValue), Is.EqualTo("+18.0 dB"));
            Assert.That(rig.Effect.Latency, Is.EqualTo(0));
            IParameter[] sliders = rig.Effect.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters.Where(p => p.Name.StartsWith("G ")).ToArray();
            Assert.That(sliders, Has.Length.EqualTo(31));

            rig.Param("B3 on").SetValue(0, 0);   // nothing parametric near the sine (220 Hz)
            rig.Play(0.2);
            double flat = Rms(rig.Play(0.3));
            rig.Param("Mode").SetValue(0, 1);
            rig.Play(0.2);
            double graphicFlat = Rms(rig.Play(0.3));
            foreach (var p in sliders) p.SetValue(0, 240);   // every slider +6 dB
            rig.Play(0.3);
            double all6 = Rms(rig.Play(0.3));
            foreach (var p in sliders) p.SetValue(0, 180);
            foreach (var name in new[] { "G 160 Hz", "G 200 Hz", "G 250 Hz", "G 315 Hz" })
                rig.Param(name).SetValue(0, 300);           // +12 dB around the sine (220 Hz)
            rig.Play(0.3);
            double between = Rms(rig.Play(0.3));
            TestContext.Out.WriteLine($"flat {Db(flat):F3} dB; graphic flat {Db(graphicFlat / flat):F4} dB; all +6 dB: {Db(all6 / flat):F4} dB; 160 to 315 Hz at +12 dB: {Db(between / flat):F3} dB at 220 Hz");
            Assert.That(Db(graphicFlat / flat), Is.EqualTo(0).Within(0.0001), "graphic mode at 0 dB passes the input");
            Assert.That(Db(all6 / flat), Is.EqualTo(6).Within(0.01), "every slider at +6 dB is +6 dB");
            Assert.That(Db(between / flat), Is.EqualTo(12).Within(1), "between sliders at +12 dB (the design: 11.73 dB at 220 Hz)");
            Assert.That(rig.Effect.IsMuted, Is.False, "the guard never tripped");

            // The editor in graphic mode, with a loudness smile.
            int[] smile = { 240, 240, 235, 230, 225, 220, 210, 200, 190, 185, 180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 185, 190, 195, 200, 210, 215, 220 };
            for (int k = 0; k < sliders.Length; k++) sliders[k].SetValue(0, smile[k]);
            rig.Play(0.5);
            var size = ShootEditor(rig.Effect, "neo-eq-editor-graphic");
            TestContext.Out.WriteLine($"editor {size.Width:0} x {size.Height:0}");
        }

        [Test]
        public void NeoUtilityStagesGainAndPansAndShowsItsEditor()
        {
            using var rig = new Rig(Utility);
            Assert.That(rig.Param("Gain").DescribeValue(rig.Param("Gain").DefValue), Is.EqualTo("0.0 dB"));
            Assert.That(rig.Param("Gain").DescribeValue(0), Is.EqualTo("−∞ dB"));
            Assert.That(rig.Param("Width").DescribeValue(150), Is.EqualTo("150%"));
            Assert.That(rig.Effect.Latency, Is.EqualTo(0));

            rig.Play(0.2);
            var (l0, r0) = rig.PlayStereo(0.3);
            rig.Param("Gain").SetValue(0, rig.Param("Gain").DefValue - 60);   // −6.0 dB
            rig.Play(0.1);
            var (l1, _) = rig.PlayStereo(0.3);
            rig.Param("Gain").SetValue(0, rig.Param("Gain").DefValue);
            rig.Param("Pan").SetValue(0, 0);   // −100 %: balance, the right side silent
            rig.Play(0.1);
            var (l2, r2) = rig.PlayStereo(0.3);
            TestContext.Out.WriteLine($"in {Db(Rms(l0)):F3} dB; gain −6: {Db(Rms(l1) / Rms(l0)):F3} dB; balance left: L {Db(Rms(l2) / Rms(l0)):F3} dB, R {Db(Rms(r2)):F1} dB");
            Assert.That(Rms(l0), Is.GreaterThan(1e-3));
            Assert.That(Rms(r0), Is.EqualTo(Rms(l0)).Within(1e-6), "a mono sine stays centred");
            Assert.That(Db(Rms(l1) / Rms(l0)), Is.EqualTo(-6).Within(0.001), "gain in exact dB");
            Assert.That(Db(Rms(l2) / Rms(l0)), Is.EqualTo(0).Within(0.001));
            Assert.That(Rms(r2), Is.LessThan(1e-7), "balance −100 %: right silent");

            rig.Param("Pan").SetValue(0, 400);
            rig.Param("Width").SetValue(0, 140);
            rig.Param("Bass mono").SetValue(0, 1);
            rig.Param("Invert R").SetValue(0, 1);
            rig.Play(0.2);
            ShootEditor(rig.Effect, "neo-utility-editor");
        }

        [Test]
        public void NeoClipperHoldsItsCeilingReportsItsLatencyAndReadingsAndShowsItsEditor()
        {
            using var rig = new Rig(Clipper);
            Assert.That(rig.Param("Ceiling").DescribeValue(rig.Param("Ceiling").DefValue), Is.EqualTo("−0.3 dB"));
            rig.Play(0.05);
            // Two oversampled stages at 8× (62 samples each) and the 5 ms ceiling guard, at every setting.
            int expected = 124 + (int)Math.Round(0.005 * rig.Rate) - 1;
            Assert.That(rig.Effect.Latency, Is.EqualTo(expected), "the latency is reported");
            rig.Param("Oversampling").SetValue(0, 0);
            rig.Play(0.05);
            Assert.That(rig.Effect.Latency, Is.EqualTo(expected), "the same at 1×");

            rig.Sine.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters[0].SetValue(0, 100);   // 0 dBFS
            rig.Param("Oversampling").SetValue(0, rig.Param("Oversampling").DefValue);
            rig.Param("Drive").SetValue(0, 240);   // +12 dB into a −0.3 dB ceiling
            rig.Play(0.5);
            var y = rig.Play(3.5);
            double peak = y.Max(s => Math.Abs((double)s));
            TestContext.Out.WriteLine($"[notice] Neo Clipper in ReBuzz: peak {Db(peak):F4} dBFS with the ceiling at −0.3 dB and +12 dB in");
            Assert.That(Db(peak), Is.LessThanOrEqualTo(-0.3 + 1e-4), "under the ceiling");
            Assert.That(Db(peak), Is.GreaterThan(-1.5), "and clipping, not muting");
            var instance = rig.Effect.ManagedMachine;
            var readings = (System.Collections.Generic.IReadOnlyDictionary<string, double>)instance.GetType().GetProperty("Readings")!.GetValue(instance)!;
            TestContext.Out.WriteLine($"[notice] Neo Clipper readings: {string.Join(", ", readings.Select(kv => $"{kv.Key} {kv.Value}"))}");
            Assert.That(readings["clip_db"], Is.GreaterThan(10), "how far over");
            Assert.That(readings.ContainsKey("gain_match_db"), Is.True, "3 s of sound: the gain match is known");
            ShootEditor(rig.Effect, "neo-clipper-editor");
        }

        [Test]
        public void NeoTransientReportsItsLookaheadAndReadingsAndShowsItsEditor()
        {
            using var rig = new Rig(Transient);
            Assert.That(rig.Param("Attack").DescribeValue(rig.Param("Attack").DefValue), Is.EqualTo("0.0 dB"));
            rig.Play(0.05);
            Assert.That(rig.Effect.Latency, Is.EqualTo((int)Math.Round(0.003 * rig.Rate)), "the default lookahead is reported");
            rig.Param("Lookahead").SetValue(0, 0);
            rig.Play(0.05);
            Assert.That(rig.Effect.Latency, Is.EqualTo(0), "and none without it");
            rig.Param("Lookahead").SetValue(0, rig.Param("Lookahead").DefValue);
            rig.Param("Attack").SetValue(0, 180);   // +6 dB
            rig.Play(0.5);
            rig.Play(3.5);
            var instance = rig.Effect.ManagedMachine;
            var readings = (System.Collections.Generic.IReadOnlyDictionary<string, double>)instance.GetType().GetProperty("Readings")!.GetValue(instance)!;
            TestContext.Out.WriteLine($"[notice] Neo Transient readings: {string.Join(", ", readings.Select(kv => $"{kv.Key} {kv.Value}"))}");
            Assert.That(readings.ContainsKey("gain_max_db"), Is.True);
            Assert.That(readings.ContainsKey("gain_match_db"), Is.True, "3 s of sound: the gain match is known");
            ShootEditor(rig.Effect, "neo-transient-editor");
        }

        [Test]
        public void NeoLowEndReportsItsLatencyInBothPhasesAndShowsItsEditor()
        {
            using var rig = new Rig(LowEnd);
            rig.Play(0.05);
            Assert.That(rig.Effect.Latency, Is.EqualTo((int)Math.Round(0.005 * rig.Rate)), "minimum phase: the lookahead");
            rig.Param("Phase").SetValue(0, 1);
            rig.Play(0.1);
            int p = 512 * (rig.Rate <= 50000 ? 1 : rig.Rate <= 100000 ? 2 : 4);
            Assert.That(rig.Effect.Latency, Is.EqualTo(16 * p / 2 - 1 + p), "linear phase: the FIR's");
            rig.Param("Phase").SetValue(0, 0);
            rig.Param("Punch").SetValue(0, 180);   // +6 dB
            rig.Play(3.6);
            var instance = rig.Effect.ManagedMachine;
            var readings = (System.Collections.Generic.IReadOnlyDictionary<string, double>)instance.GetType().GetProperty("Readings")!.GetValue(instance)!;
            TestContext.Out.WriteLine($"[notice] Neo Low End readings: {string.Join(", ", readings.Select(kv => $"{kv.Key} {kv.Value}"))}");
            Assert.That(readings.ContainsKey("latency_samples"), Is.True);
            Assert.That(readings.ContainsKey("gain_match_db"), Is.True, "3 s of sound: the gain match is known");
            ShootEditor(rig.Effect, "neo-low-end-editor");
        }

        [Test]
        public void NeoImagerAddsNoLatencyReportsItsBandsAndShowsItsEditor()
        {
            using var rig = new Rig(Imager);
            rig.Play(0.05);
            Assert.That(rig.Effect.Latency, Is.EqualTo(0));
            rig.Param("Width 4").SetValue(0, 150);
            rig.Param("Stereoize 2").SetValue(0, 300);
            rig.Play(3.6);
            var instance = rig.Effect.ManagedMachine;
            var readings = (System.Collections.Generic.IReadOnlyDictionary<string, double>)instance.GetType().GetProperty("Readings")!.GetValue(instance)!;
            TestContext.Out.WriteLine($"[notice] Neo Imager readings: {string.Join(", ", readings.Select(kv => $"{kv.Key} {kv.Value}"))}");
            Assert.That(readings.ContainsKey("width_db_4"), Is.True);
            Assert.That(readings.ContainsKey("gain_match_db"), Is.True, "3 s of sound: the gain match is known");
            ShootEditor(rig.Effect, "neo-imager-editor");
        }

        [Test]
        public void NeoExciterReportsItsConstantLatencyAndShowsItsEditor()
        {
            using var rig = new Rig(Exciter);
            rig.Play(0.05);
            int latency = rig.Effect.Latency;
            Assert.That(latency, Is.GreaterThan(0));
            rig.Param("Mode").SetValue(0, 4);
            rig.Param("Amount 3").SetValue(0, 400);
            rig.Play(3.6);
            Assert.That(rig.Effect.Latency, Is.EqualTo(latency), "the oversampling's delay, whatever the settings");
            var instance = rig.Effect.ManagedMachine;
            var readings = (System.Collections.Generic.IReadOnlyDictionary<string, double>)instance.GetType().GetProperty("Readings")!.GetValue(instance)!;
            TestContext.Out.WriteLine($"[notice] Neo Exciter readings: {string.Join(", ", readings.Select(kv => $"{kv.Key} {kv.Value}"))}");
            Assert.That(readings.ContainsKey("added_db"), Is.True);
            ShootEditor(rig.Effect, "neo-exciter-editor");
        }

        [Test]
        public void NeoTapeReportsItsLatencyAndShowsItsEditor()
        {
            using var rig = new Rig(Tape);
            rig.Play(0.05);
            int high = rig.Effect.Latency;
            Assert.That(high, Is.GreaterThan(0), "the oversampling's delay");
            rig.Param("Saturation").SetValue(0, 200);
            rig.Play(1.0);
            Assert.That(rig.Effect.Latency, Is.EqualTo(high), "constant whatever the saturation");
            rig.Param("Quality").SetValue(0, 0);
            rig.Play(0.5);
            Assert.That(rig.Effect.Latency, Is.EqualTo(0), "Live has none");
            rig.Param("Quality").SetValue(0, 1);
            rig.Play(2.5);
            var instance = rig.Effect.ManagedMachine;
            var readings = (System.Collections.Generic.IReadOnlyDictionary<string, double>)instance.GetType().GetProperty("Readings")!.GetValue(instance)!;
            TestContext.Out.WriteLine($"[notice] Neo Tape readings: {string.Join(", ", readings.Select(kv => $"{kv.Key} {kv.Value}"))}");
            Assert.That(readings.ContainsKey("added_db"), Is.True);
            ShootEditor(rig.Effect, "neo-tape-editor");
        }

        [Test]
        public void NeoDeEsserReportsItsLookaheadAsLatencyAndShowsItsEditor()
        {
            using var rig = new Rig(DeEsser);
            rig.Play(0.05);
            Assert.That(rig.Effect.Latency, Is.EqualTo((int)Math.Round(0.001 * rig.Rate)), "the default 1 ms of lookahead, the minimum-phase split");
            rig.Param("Lookahead").SetValue(0, 0);
            rig.Play(0.5);
            Assert.That(rig.Effect.Latency, Is.EqualTo(0), "no lookahead");
            rig.Param("Lookahead").SetValue(0, 400);
            rig.Play(0.5);
            Assert.That(rig.Effect.Latency, Is.EqualTo((int)Math.Round(0.002 * rig.Rate)), "2 ms of lookahead");
            rig.Param("Action").SetValue(0, 1);
            rig.Play(0.5);
            Assert.That(rig.Effect.Latency, Is.EqualTo((int)Math.Round(0.002 * rig.Rate) + 255), "the linear-phase split adds 255");
            var instance = rig.Effect.ManagedMachine;
            var readings = (System.Collections.Generic.IReadOnlyDictionary<string, double>)instance.GetType().GetProperty("Readings")!.GetValue(instance)!;
            Assert.That(readings.ContainsKey("reduction_db"), Is.True);
            ShootEditor(rig.Effect, "neo-deesser-editor");
        }

        [Test]
        public void NeoSpectralReportsItsFrameAsLatencyAndShowsItsEditor()
        {
            using var rig = new Rig(Spectral);
            rig.Play(0.5);
            int frame = rig.Rate > 66000 ? 4095 : 2047;
            Assert.That(rig.Effect.Latency, Is.EqualTo(frame), "one frame of the transform");
            var instance = rig.Effect.ManagedMachine;
            var readings = (System.Collections.Generic.IReadOnlyDictionary<string, double>)instance.GetType().GetProperty("Readings")!.GetValue(instance)!;
            Assert.That(readings.ContainsKey("reduction_db"), Is.True);
            Assert.That(readings["latency_samples"], Is.EqualTo(frame));
            ShootEditor(rig.Effect, "neo-spectral-editor");
        }

        [Test]
        public void NeoGateReportsItsLookaheadAsLatencyAndShowsItsEditor()
        {
            using var rig = new Rig(Gate);
            rig.Play(0.05);
            Assert.That(rig.Effect.Latency, Is.EqualTo(0));
            rig.Param("Lookahead").SetValue(0, 200);
            rig.Play(0.5);
            Assert.That(rig.Effect.Latency, Is.EqualTo((int)Math.Round(0.005 * rig.Rate)), "5 ms of lookahead");
            var instance = rig.Effect.ManagedMachine;
            var readings = (System.Collections.Generic.IReadOnlyDictionary<string, double>)instance.GetType().GetProperty("Readings")!.GetValue(instance)!;
            Assert.That(readings.ContainsKey("gain_db"), Is.True);
            ShootEditor(rig.Effect, "neo-gate-editor");
        }

        [Test]
        public void NeoDitherAddsNoLatencyAndShowsItsEditor()
        {
            using var rig = new Rig(Dither);
            rig.Play(0.05);
            Assert.That(rig.Effect.Latency, Is.EqualTo(0));
            Assert.That(rig.Param("Bits").DescribeValue(rig.Param("Bits").DefValue), Is.EqualTo("16"));
            rig.Play(0.5);
            ShootEditor(rig.Effect, "neo-dither-editor");
        }

        [Test]
        public void NeoStripReportsTheSameLatencyWhicheverStagesAreOnAndShowsItsEditor()
        {
            using var rig = new Rig(Strip);
            rig.Play(0.05);
            int latency = rig.Effect.Latency;
            Assert.That(latency, Is.GreaterThan(0));
            Assert.That(rig.Param("Ceiling").DescribeValue(rig.Param("Ceiling").DefValue), Is.EqualTo("−0.3 dB"));
            foreach (var stage in new[] { "Gate on", "EQ on", "Comp on", "Drive on", "Clip on", "Limit on" }) rig.Param(stage).SetValue(0, 1);
            rig.Play(0.3);
            Assert.That(rig.Effect.Latency, Is.EqualTo(latency));
            var instance = rig.Effect.ManagedMachine;
            var readings = (System.Collections.Generic.IReadOnlyDictionary<string, double>)instance.GetType().GetProperty("Readings")!.GetValue(instance)!;
            Assert.That(readings["latency_samples"], Is.EqualTo(latency));
            ShootEditor(rig.Effect, "neo-strip-editor");
        }

        [Test]
        public void NeoLimiterHoldsTheCeilingReportsItsLatencyAndShowsItsEditor()
        {
            using var rig = new Rig(Limiter);
            Assert.That(rig.Param("Ceiling").DescribeValue(rig.Param("Ceiling").DefValue), Is.EqualTo("−1.0 dB"));
            Assert.That(rig.Param("Input gain").DescribeValue(rig.Param("Input gain").MaxValue), Is.EqualTo("+24.0 dB"));
            // Lookahead 2 ms (the default) and the true-peak interpolator's 11 samples.
            rig.Play(0.05);
            Assert.That(rig.Effect.Latency, Is.EqualTo(LimiterLatency(2, rig.Rate)), "the latency is reported");

            rig.Sine.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters[0].SetValue(0, 100);   // 0 dBFS
            rig.Param("Input gain").SetValue(0, 240);   // +12 dB into a −1 dBTP ceiling
            rig.Play(0.5);
            var y = rig.Play(0.5);
            double peak = y.Max(s => Math.Abs((double)s));
            TestContext.Out.WriteLine($"peak {Db(peak):F3} dBFS with the ceiling at −1 dBTP and +12 dB in");
            Assert.That(Db(peak), Is.LessThanOrEqualTo(-1 + 0.01), "under the ceiling");
            Assert.That(Db(peak), Is.GreaterThan(-1.2), "and limiting, not muting");

            rig.Param("Lookahead").SetValue(0, rig.Param("Lookahead").MaxValue);   // 5 ms
            rig.Play(0.1);
            Assert.That(rig.Effect.Latency, Is.EqualTo(LimiterLatency(5, rig.Rate)), "the new latency reached ReBuzz");

            // Four seconds of a level that moves, so the editor's history shows limiting come and go.
            rig.Param("Lookahead").SetValue(0, rig.Param("Lookahead").DefValue);
            var level = rig.Sine.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters[0];
            foreach (int l in new[] { 20, 100, 45, 100, 30, 70, 100 })
            {
                level.SetValue(0, l);
                rig.Play(0.6);
            }
            ShootEditor(rig.Effect, "neo-limiter-editor");
        }

        [Test]
        public void NeoLimiterV2sSoftClipMovesItsLatencyAndTheCeilingHolds()
        {
            // mastering-tools.md §5.2: the soft clip (4× oversampled) adds 58 samples while on; the true-peak ceiling holds.
            using var rig = new Rig(Limiter);
            rig.Play(0.05);
            Assert.That(rig.Effect.Latency, Is.EqualTo(LimiterLatency(2, rig.Rate)));
            rig.Param("Soft clip").SetValue(0, 2);   // 9 dB
            rig.Play(0.1);
            Assert.That(rig.Effect.Latency, Is.EqualTo(LimiterLatency(2, rig.Rate) + 58), "the soft clip's latency reached ReBuzz");
            rig.Sine.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters[0].SetValue(0, 100);   // 0 dBFS
            rig.Param("Input gain").SetValue(0, 240);   // +12 dB into −1 dBTP
            rig.Param("Transients").SetValue(0, 30);
            rig.Param("Slow stage").SetValue(0, 500);
            rig.Play(0.5);
            var y = rig.Play(0.5);
            double peak = y.Max(s => Math.Abs((double)s));
            TestContext.Out.WriteLine($"[notice] Neo Limiter v2 in ReBuzz, soft clip 9 dB and +12 dB in: peak {Db(peak):F3} dBFS under a −1 dBTP ceiling");
            Assert.That(Db(peak), Is.LessThanOrEqualTo(-1 + 0.01));
            rig.Param("Soft clip").SetValue(0, 0);
            rig.Play(0.1);
            Assert.That(rig.Effect.Latency, Is.EqualTo(LimiterLatency(2, rig.Rate)), "and back");
        }

        [Test]
        public void NeoPresetsLoad()
        {
            foreach (var info in new[] { Drive, Comp, Verb, Poly, Wave, Fm6, Eq, Utility, Limiter, Analyzer, Ensemble, Delay, Multiband, Sampler, Scope, Drums, Filter, Dye, LearnSub, LearnFm, LearnWt, LearnAdd, LearnGrain, LearnPhys, LearnString, LearnFormant, LearnFold, LearnPd, LearnVector, LearnSampler, LearnSpectral, Clipper, Transient, LowEnd, Imager, Exciter, Dither, Strip, Tape, DeEsser, Gate, Spectral, Patch })
            {
                var prs = Path.Combine(Path.GetDirectoryName(info.BuiltPath)!, info.Name + ".prs.xml");
                if (!File.Exists(info.BuiltPath)) Assert.Inconclusive($"{info.Name} not built");
                Assert.That(File.Exists(prs), Is.True, $"{prs} ships beside the DLL");
            }
        }
    }
}
