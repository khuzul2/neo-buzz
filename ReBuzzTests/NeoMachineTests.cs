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
    public class NeoMachineTests
    {
        // ------------------------------------------------------------------ machines

        /// <summary>A Neo machine as the solution build made it.</summary>
        internal sealed class NeoInfo(string name, string shortName, bool generator, bool multiIo = false) : ITestMachineInfo
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
                    var bin = File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First();
                    return Path.Combine(bin, "Gear", Generator ? "Generators" : "Effects", DllName);
                }
            }
        }

        internal static readonly NeoInfo Drive = new("Neo Drive", "Drive", false);
        internal static readonly NeoInfo Comp = new("Neo Comp", "Comp", false, multiIo: true);
        internal static readonly NeoInfo Verb = new("Neo Verb", "Verb", false);

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

            /// <summary>Sine → the Neo effect → master.</summary>
            public Rig(NeoInfo effect)
            {
                if (!File.Exists(effect.BuiltPath))
                    Assert.Inconclusive($"{effect.Name} not built ({effect.BuiltPath}); build the solution first");
                Driver.Gear.AddDynamicGenerator(SineInfo);
                Driver.Gear.AddBuiltMachine(effect, AbsoluteFilePath.Value(effect.BuiltPath), effect.Generator);
                Driver.Start();
                var sine = new DynamicMachineController("Neo Test Sine", "Sine");
                var fx = new DynamicMachineController(effect.Name, effect.Name);
                Driver.MachineGraph.InsertMachineInstanceFor(sine);
                Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(fx);
                Driver.MachineGraph.Connect(sine, fx);
                Sine = Driver.MachineFor(sine);
                Effect = Driver.MachineFor(fx);
                reader = Driver.CreateAudioReader(256);
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
        public void NeoPresetsLoad()
        {
            foreach (var info in new[] { Drive, Comp, Verb })
            {
                var prs = Path.Combine(Path.GetDirectoryName(info.BuiltPath)!, info.Name + ".prs.xml");
                if (!File.Exists(info.BuiltPath)) Assert.Inconclusive($"{info.Name} not built");
                Assert.That(File.Exists(prs), Is.True, $"{prs} ships beside the DLL");
            }
        }
    }
}
