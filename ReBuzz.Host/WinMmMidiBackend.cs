#nullable disable
// Fork (docs/specs/ux-overhaul.md P6, the studio's Windows preview): MIDI for a host without WPF on
// Windows, through WinMM with NAudio, as the WPF app's WindowsMidiBackend (ReBuzz/Midi) opens its
// devices: the same MidiIn and MidiOut calls, in and out ports, short messages and SysEx buffers, and a
// control surface's own port pair for the Session View's MIDI mapping. What the WPF backend adds stays
// there: the MIDI 2 endpoint (Windows MIDI Services) and the WinRT device watcher need Windows 10
// targeting. Hot-plug here is a poll of the device lists every two seconds; no MIDI 2 endpoint.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.Midi;
using IMidiInput = ReBuzz.Midi.IMidiInput;
using IMidiOutput = ReBuzz.Midi.IMidiOutput;
using ReBuzz.Core.Session;
using ReBuzz.Midi;

namespace ReBuzz.Host
{
    internal sealed class WinMmMidiBackend : IMidiBackend
    {
        readonly Action<int> toCore;
        readonly Action<string> log;
        Timer poll;
        Action<MidiDeviceChange> changed;
        HashSet<string> known;

        /// <param name="toCore">Where inputs' short messages go (the core's MIDI input).</param>
        public WinMmMidiBackend(Action<int> toCore, Action<string> log)
        {
            this.toCore = toCore;
            this.log = log ?? (_ => { });
        }

        /// <summary>Whether WinMM answers (it does on every Windows; false elsewhere).</summary>
        public static bool Available => OperatingSystem.IsWindows();

        public int InputCount => Safe(() => MidiIn.NumberOfDevices);
        public string InputName(int index) => MidiIn.DeviceInfo(index).ProductName;
        public int OutputCount => Safe(() => MidiOut.NumberOfDevices);
        public string OutputName(int index) => MidiOut.DeviceInfo(index).ProductName;

        static int Safe(Func<int> count)
        {
            try { return count(); }
            catch (Exception) { return 0; }
        }

        public IMidiInput OpenInput(int index)
        {
            var input = new Input(index, toCore, log);
            return input;
        }

        public IMidiOutput OpenOutput(int index) => new Output(index);

        public IControllerPort OpenControllerPort(int inputIndex, int outputIndex) => new Port(inputIndex, outputIndex);

        public bool CreateMidi2Endpoint() => false;

        public void ReleaseMidi2() { }

        // ------------------------------------------------------------------ hot-plug

        IEnumerable<string> Names()
        {
            int ins = InputCount, outs = OutputCount;
            var names = new List<string>();
            for (int i = 0; i < ins; i++) names.Add(Try(() => InputName(i)));
            for (int i = 0; i < outs; i++) names.Add(Try(() => OutputName(i)));
            return names.Where(n => n != null);
        }

        static string Try(Func<string> f)
        {
            try { return f(); }
            catch (Exception) { return null; }
        }

        public void StartWatching(Action<MidiDeviceChange> changed)
        {
            this.changed = changed;
            known = new HashSet<string>(Names());
            poll?.Dispose();
            poll = new Timer(_ => Rescan(), null, 2000, 2000);
        }

        public void StopWatching()
        {
            poll?.Dispose();
            poll = null;
            changed = null;
        }

        void Rescan()
        {
            try
            {
                var report = changed;
                if (report == null) return;
                var now = new HashSet<string>(Names());
                int ins = InputCount, outs = OutputCount;
                foreach (var name in now.Where(n => !known.Contains(n)).ToList())
                    Report(report, new MidiDeviceChange(name, true, ins, outs));
                foreach (var name in known.Where(n => !now.Contains(n)).ToList())
                    Report(report, new MidiDeviceChange(name, false, ins, outs));
                known = now;
            }
            catch (Exception e)
            {
                log("MIDI devices: " + e.Message);
            }
        }

        void Report(Action<MidiDeviceChange> report, MidiDeviceChange change)
        {
            log($"MIDI {change.Name}: {(change.Connected ? "connected" : "gone")}");
            try { report(change); }
            catch (Exception e) { log($"MIDI device {change.Name}: {e.Message}"); }
        }

        // ------------------------------------------------------------------ devices

        sealed class Input : IMidiInput
        {
            readonly Action<string> log;
            MidiIn midiIn;
            readonly Action<int> toCore;

            public string ProductName { get; }

            public Input(int index, Action<int> toCore, Action<string> log)
            {
                this.toCore = toCore;
                this.log = log;
                ProductName = MidiIn.DeviceInfo(index).ProductName;
                try
                {
                    midiIn = new MidiIn(index);
                    midiIn.MessageReceived += OnMessage;
                    midiIn.ErrorReceived += OnError;
                    midiIn.Start();
                }
                catch (Exception e)
                {
                    log($"MIDI in {ProductName}: {e.Message}");
                    Close();
                }
            }

            void OnMessage(object sender, MidiInMessageEventArgs e) => toCore(e.RawMessage);

            void OnError(object sender, MidiInMessageEventArgs e) => log($"MIDI in {ProductName}: bad message 0x{e.RawMessage:X8}");

            public void Close()
            {
                var input = midiIn;
                midiIn = null;
                if (input == null) return;
                try
                {
                    input.Stop();
                    input.MessageReceived -= OnMessage;
                    input.ErrorReceived -= OnError;
                    input.Dispose();
                }
                catch (Exception) { }
            }
        }

        sealed class Output : IMidiOutput
        {
            MidiOut midiOut;

            public string ProductName { get; }

            public Output(int index)
            {
                ProductName = MidiOut.DeviceInfo(index).ProductName;
                midiOut = new MidiOut(index);
            }

            public void Send(int message) => midiOut?.Send(message);

            public void Send(byte[] message) => midiOut?.SendBuffer(message);

            public void Dispose()
            {
                var output = midiOut;
                midiOut = null;
                if (output == null) return;
                try { output.Reset(); } catch (Exception) { }
                output.Dispose();
            }
        }

        /// <summary>A controller's ports, opened for the controller alone.</summary>
        sealed class Port : IControllerPort
        {
            readonly MidiIn input;
            readonly MidiOut output;
            readonly object sendLock = new object();

            public event Action<int> Received;

            public Port(int inputIndex, int outputIndex)
            {
                output = new MidiOut(outputIndex);
                try
                {
                    input = new MidiIn(inputIndex);
                    input.MessageReceived += (s, e) => Received?.Invoke(e.RawMessage);
                    input.Start();
                }
                catch
                {
                    output.Dispose();
                    input?.Dispose();
                    throw;
                }
            }

            public void Send(int message)
            {
                lock (sendLock) output.Send(message);
            }

            public void Send(byte[] message)
            {
                lock (sendLock) output.SendBuffer(message);
            }

            public void Dispose()
            {
                try { input.Stop(); } catch (Exception) { }
                try { input.Dispose(); } catch (Exception) { }
                try { output.Dispose(); } catch (Exception) { }
            }
        }
    }
}
