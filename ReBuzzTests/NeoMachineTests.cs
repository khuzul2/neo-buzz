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
        internal static readonly NeoInfo Poly = new("Neo Poly", "Poly", true);
        internal static readonly NeoInfo Wave = new("Neo Wave", "Wave", true);
        internal static readonly NeoInfo Fm6 = new("Neo FM6", "FM6", true);
        // Mix and master batch (docs/specs/machines.md §5, MCP A4).
        internal static readonly NeoInfo Eq = new("Neo EQ", "EQ", false);
        internal static readonly NeoInfo Utility = new("Neo Utility", "Utility", false);
        internal static readonly NeoInfo Limiter = new("Neo Limiter", "Limiter", false);

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

            /// <summary>Effects: sine → the Neo effect → master. Generators: the Neo synth → master.</summary>
            public Rig(NeoInfo machine)
            {
                if (!File.Exists(machine.BuiltPath))
                    Assert.Inconclusive($"{machine.Name} not built ({machine.BuiltPath}); build the solution first");
                if (!machine.Generator) Driver.Gear.AddDynamicGenerator(SineInfo);
                Driver.Gear.AddBuiltMachine(machine, AbsoluteFilePath.Value(machine.BuiltPath), machine.Generator);
                Driver.Start();
                var fx = new DynamicMachineController(machine.Name, machine.Name);
                Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(fx);
                if (!machine.Generator)
                {
                    var sine = new DynamicMachineController("Neo Test Sine", "Sine");
                    Driver.MachineGraph.InsertMachineInstanceFor(sine);
                    Driver.MachineGraph.Connect(sine, fx);
                    Sine = Driver.MachineFor(sine);
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

            rig.Param("Lookahead").SetValue(0, rig.Param("Lookahead").DefValue);
            rig.Play(0.6);
            ShootEditor(rig.Effect, "neo-limiter-editor");
        }

        [Test]
        public void NeoPresetsLoad()
        {
            foreach (var info in new[] { Drive, Comp, Verb, Poly, Wave, Fm6, Eq, Utility, Limiter })
            {
                var prs = Path.Combine(Path.GetDirectoryName(info.BuiltPath)!, info.Name + ".prs.xml");
                if (!File.Exists(info.BuiltPath)) Assert.Inconclusive($"{info.Name} not built");
                Assert.That(File.Exists(prs), Is.True, $"{prs} ships beside the DLL");
            }
        }
    }
}
