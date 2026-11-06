using AtmaFileSystem;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Midi;
using ReBuzzTests.Automation;
using ReBuzzTests.Automation.TestMachines;
using ReBuzzTests.Automation.TestMachinesControllers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork (docs/specs/mcp-server.md §12.13): ReBuzz Midi Output, as the solution build made it, playing its tracks
    /// into a MIDI output that records what it is sent: the messages, byte for byte (status | data 1 &lt;&lt; 8 | data 2
    /// &lt;&lt; 16, as WinMM takes them). Its Work read the controller number from the track's Delay and the channel from
    /// its MidiCC, encoded every message without a channel, and took a note no Volume had set for velocity 0.
    /// </summary>
    public class MidiOutputTests
    {
        sealed class MidiOutputInfo : ITestMachineInfo
        {
            public const string Name = "ReBuzz Midi Output";

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
                    Flags = MachineInfoFlags.LOAD_DATA_RUNTIME,
                    Author = "WDE",
                    InternalVersion = 0,
                    MaxTracks = 32,
                    MinTracks = 1,
                    Name = Name,
                    ShortName = "Midi Out",
                    Type = MachineType.Generator,
                    Version = 66,
                },
                Presets = null,
                SHA1Hash = "AE1" + Math.Abs(Name.GetHashCode()).ToString("X8").PadRight(37, '0'),
                GUIFactoryDecl = new MachineGUIFactoryDecl { PreferWindowedGUI = true },
                ModuleHandle = 0,
            };

            public string DllName => Name + ".NET.dll";
            public string RealDllName => DllName;

            /// <summary>Where the solution build put it (beside ReBuzz.exe, under Gear\Generators).</summary>
            public static string BuiltPath =>
                Path.Combine(File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First(), "Gear", "Generators", Name + ".NET.dll");
        }

        /// <summary>One MIDI output device that keeps every short message it is sent.</summary>
        sealed class RecordingMidi : IMidiBackend
        {
            public readonly Output Device = new();
            public int InputCount => 0;
            public string InputName(int index) => throw new ArgumentOutOfRangeException(nameof(index));
            public int OutputCount => 1;
            public string OutputName(int index) => "Recorder";
            public IMidiInput OpenInput(int index) => throw new NotSupportedException();
            public IMidiOutput OpenOutput(int index) => Device;
            public IControllerPort OpenControllerPort(int inputIndex, int outputIndex) => throw new NotSupportedException();
            public void StartWatching(Action<MidiDeviceChange> changed) { }
            public void StopWatching() { }
            public bool CreateMidi2Endpoint() => false;
            public void ReleaseMidi2() { }

            public sealed class Output : IMidiOutput
            {
                public readonly List<int> Sent = new();
                public string ProductName => "Recorder";
                public void Send(int message) { lock (Sent) Sent.Add(message); }
                public void Send(byte[] message) { }
                public void Dispose() { }

                /// <summary>What it was sent since the last call.</summary>
                public int[] Take()
                {
                    lock (Sent)
                    {
                        var all = Sent.ToArray();
                        Sent.Clear();
                        return all;
                    }
                }
            }
        }

        sealed class Rig : IDisposable
        {
            public readonly RecordingMidi Midi = new();
            public Driver Driver { get; }
            public MachineCore Machine { get; }
            readonly Driver.AudioReader reader;

            public Rig(int tracks)
            {
                if (!File.Exists(MidiOutputInfo.BuiltPath))
                    Assert.Inconclusive($"{MidiOutputInfo.Name} not built ({MidiOutputInfo.BuiltPath}); build the solution first");
                Driver = new Driver { MidiBackend = _ => Midi };
                Driver.Gear.AddBuiltMachine(new MidiOutputInfo(), AbsoluteFilePath.Value(MidiOutputInfo.BuiltPath), generator: true);
                Driver.Start();
                Driver.Core.MidiInOutEngine.CreateMidiOut(0);
                var controller = new DynamicMachineController(MidiOutputInfo.Name, "Midi Out");
                Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(controller);
                Machine = Driver.MachineFor(controller);
                Machine.TrackCount = tracks;
                reader = Driver.CreateAudioReader(256);
                Play();                                         // the machine's first ticks, nothing set yet
                Midi.Device.Take();
            }

            public IParameter Track(string name) => Machine.ParameterGroups[2].Parameters.Single(p => p.Name == name);

            /// <summary>Sets track parameters (a tick's row: name, track, value).</summary>
            public void Set(params (string Name, int Track, int Value)[] row)
            {
                foreach (var (name, track, value) in row) Track(name).SetValue(track, value);
            }

            /// <summary>Plays past the next tick (a tick is about 22 buffers of 256 at 126 BPM), and returns what it sent.</summary>
            public int[] Play()
            {
                for (int i = 0; i < 40; i++) reader.Read(256);
                return Midi.Device.Take();
            }

            public void Dispose()
            {
                Driver.Core.Playing = false;
                Driver.Dispose();
            }
        }

        const int C5 = (5 << 4) + 1;                            // Buzz's C-5: MIDI note 60
        const int E5 = (5 << 4) + 5;                            // MIDI 64

        static int Message(int status, int data1, int data2) => status | data1 << 8 | data2 << 16;

        static string Hex(IEnumerable<int> messages) => "[" + string.Join(", ", messages.Select(m => $"{m & 0xFF:X2} {m >> 8 & 0xFF:X2} {m >> 16 & 0xFF:X2}")) + "]";

        [Test]
        public void TheTrackParametersAreWhereSongsHaveThem()
        {
            // Saved data and patterns address track parameters by their place: the fix moved none.
            using var rig = new Rig(1);
            var group = rig.Machine.ParameterGroups[2].Parameters;
            Assert.That(group.Select(p => p.Name), Is.EqualTo(new[] { "Device", "Note", "Volume", "Delay", "Cut", "MidiCC", "MidiCCValue", "Channel" }));
            Assert.That(group.Select(p => (p.MinValue, p.MaxValue, p.DefValue)), Is.EqualTo(new[]
            {
                (0, 255, 0), (1, 156, 0), (0, 127, 120), (0, 127, 0), (0, 127, 0), (0, 254, 0), (0, 0xFFFE, 0), (0, 15, 0),
            }));
        }

        [Test]
        public void AControllerGoesOutAsItsMidiCCOnItsChannel()
        {
            using var rig = new Rig(2);
            rig.Set(("MidiCC", 0, 7), ("Channel", 0, 2), ("MidiCCValue", 0, 100),
                    ("MidiCC", 1, 74), ("Channel", 1, 15), ("MidiCCValue", 1, 1));
            var sent = rig.Play();
            Assert.That(sent, Is.EquivalentTo(new[] { Message(0xB2, 7, 100), Message(0xBF, 74, 1) }), Hex(sent));

            // A new value on the same controller and channel; the controller and channel alone send nothing.
            rig.Set(("MidiCCValue", 0, 0));
            sent = rig.Play();
            Assert.That(sent, Is.EqualTo(new[] { Message(0xB2, 7, 0) }), Hex(sent));
            rig.Set(("MidiCC", 0, 10), ("Channel", 0, 3));
            sent = rig.Play();
            Assert.That(sent, Is.Empty, Hex(sent));

            // A track's Delay is a note's, not the controller number (Work read it as that).
            rig.Set(("Delay", 0, 64), ("MidiCCValue", 0, 64));
            sent = rig.Play();
            Assert.That(sent, Is.EqualTo(new[] { Message(0xB3, 10, 64) }), Hex(sent));
        }

        [Test]
        public void ValuesBeyondTheMessageGoOutAsItsHighest()
        {
            using var rig = new Rig(1);
            rig.Set(("MidiCC", 0, 1), ("MidiCCValue", 0, 300));
            var sent = rig.Play();
            Assert.That(sent, Is.EqualTo(new[] { Message(0xB0, 1, 127) }), "no data byte with its top bit set: " + Hex(sent));
        }

        [Test]
        public void MidiCC224IsThePitchWheelAndOthersAboveACControllerSendNothing()
        {
            using var rig = new Rig(1);
            rig.Set(("MidiCC", 0, 0xE0), ("Channel", 0, 3), ("MidiCCValue", 0, 0x2000));
            var sent = rig.Play();
            Assert.That(sent, Is.EqualTo(new[] { Message(0xE3, 0x00, 0x40) }), "the wheel at its centre, LSB first: " + Hex(sent));
            rig.Set(("MidiCCValue", 0, 0x2ABC));
            sent = rig.Play();
            Assert.That(sent, Is.EqualTo(new[] { Message(0xE3, 0x3C, 0x55) }), Hex(sent));
            rig.Set(("MidiCCValue", 0, 0xFFFE));
            sent = rig.Play();
            Assert.That(sent, Is.EqualTo(new[] { Message(0xE3, 0x7F, 0x7F) }), "the highest 14-bit value: " + Hex(sent));

            rig.Set(("MidiCC", 0, 200), ("MidiCCValue", 0, 5));
            sent = rig.Play();
            Assert.That(sent, Is.Empty, "200 names no message: " + Hex(sent));
        }

        [Test]
        public void NotesGoOutOnTheirTrackChannelAndAreReleasedOnIt()
        {
            using var rig = new Rig(2);
            rig.Set(("Channel", 0, 9), ("Volume", 0, 100), ("Note", 0, C5),
                    ("Note", 1, E5));                         // track 1: channel 1, no Volume yet
            var sent = rig.Play();
            Assert.That(sent, Is.EquivalentTo(new[] { Message(0x99, 60, 100), Message(0x90, 64, 120) }),
                "a note no Volume set plays at Volume's default, 120 (it went out at 0, a note-off): " + Hex(sent));

            // The channel moves while the note sounds: its note-off still goes where the note went.
            rig.Set(("Channel", 0, 5));
            Assert.That(rig.Play(), Is.Empty);
            rig.Set(("Note", 0, BuzzGUI.Common.BuzzNote.Off), ("Note", 1, BuzzGUI.Common.BuzzNote.Off));
            sent = rig.Play();
            Assert.That(sent, Is.EquivalentTo(new[] { Message(0x89, 60, 100), Message(0x80, 64, 120) }), Hex(sent));

            // The next note takes the new channel.
            rig.Set(("Note", 0, E5));
            sent = rig.Play();
            Assert.That(sent, Is.EqualTo(new[] { Message(0x95, 64, 100) }), "the track's last Volume: " + Hex(sent));
        }
    }
}
