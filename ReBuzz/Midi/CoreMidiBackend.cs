#nullable disable
using ReBuzz.Core.Session;
using ReBuzz.Platform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReBuzz.Midi
{
    /// <summary>
    /// Fork (docs/specs/mac-engine.md M6): macOS MIDI through CoreMIDI (ReBuzz/Platform/CoreMidi.cs).
    ///  - Devices are indexed in a snapshot of CoreMIDI's sources (inputs) and destinations
    ///    (outputs), taken at start and again on each change CoreMIDI reports. So counts and names
    ///    agree between changes, as WinMM's numbering does on Windows. Names are CoreMIDI's display
    ///    names; a second device of the same name is "Name (2)".
    ///  - Hot-plug: each change reports the names that came and went.
    ///  - The MIDI 2 endpoint's place: a virtual destination "ReBuzz", whose messages play as a
    ///    keyboard's do (what Windows' virtual device is for), and a virtual source "ReBuzz",
    ///    offered as an output, so clock out and MIDI out machines reach other apps. They aren't
    ///    offered as an input or as an output to themselves.
    /// </summary>
    internal sealed class CoreMidiBackend : IMidiBackend
    {
        public const string VirtualName = "ReBuzz";

        readonly Action<int> toCore;
        readonly Action<string> log;
        readonly object scanLock = new object();
        volatile Devices devices;
        Action<MidiDeviceChange> changed;
        CoreMidi.VirtualDestination virtualIn;
        CoreMidi.VirtualSource virtualOut;

        /// <param name="toCore">Where inputs' short messages go (the core's MIDI input).</param>
        /// <param name="log">Problems, for the log.</param>
        public CoreMidiBackend(Action<int> toCore, Action<string> log)
        {
            this.toCore = toCore;
            this.log = log ?? (_ => { });
            devices = Scan();
        }

        /// <summary>An output: a destination, or our virtual source.</summary>
        sealed record Port(string Name, uint Ref, bool Virtual);

        sealed record Devices(Port[] Inputs, Port[] Outputs);

        Devices Scan()
        {
            var inputs = Unique(CoreMidi.Sources().Where(e => !e.Own).Select(e => new Port(e.Name, e.Ref, false)));
            var outputs = CoreMidi.Destinations().Where(e => !e.Own).Select(e => new Port(e.Name, e.Ref, false)).ToList();
            var source = virtualOut;
            if (source != null && source.Ref != 0) outputs.Add(new Port(VirtualName, source.Ref, true));
            return new Devices(inputs, Unique(outputs));
        }

        static Port[] Unique(IEnumerable<Port> ports)
        {
            var seen = new Dictionary<string, int>();
            var list = new List<Port>();
            foreach (var p in ports)
            {
                seen[p.Name] = seen.TryGetValue(p.Name, out int n) ? n + 1 : 1;
                list.Add(seen[p.Name] == 1 ? p : p with { Name = $"{p.Name} ({seen[p.Name]})" });
            }
            return list.ToArray();
        }

        public int InputCount => devices.Inputs.Length;
        public string InputName(int index) => devices.Inputs[index].Name;
        public int OutputCount => devices.Outputs.Length;
        public string OutputName(int index) => devices.Outputs[index].Name;

        public IMidiInput OpenInput(int index)
        {
            var port = devices.Inputs[index];
            return new Input(port.Name, new CoreMidi.Input(port.Ref, toCore));
        }

        public IMidiOutput OpenOutput(int index)
        {
            var port = devices.Outputs[index];
            return new Output(port.Name, Sender(port), owned: !port.Virtual);
        }

        CoreMidi.Sender Sender(Port port)
        {
            if (!port.Virtual) return new CoreMidi.Output(port.Ref);
            return virtualOut ?? throw new InvalidOperationException("The ReBuzz virtual source is gone.");
        }

        public IControllerPort OpenControllerPort(int inputIndex, int outputIndex)
        {
            var d = devices;
            var input = d.Inputs[inputIndex];
            var output = d.Outputs[outputIndex];
            return new ControllerPort(input.Ref, Sender(output), owned: !output.Virtual);
        }

        // ------------------------------------------------------------------ hot-plug

        public void StartWatching(Action<MidiDeviceChange> changed)
        {
            this.changed = changed;
            CoreMidi.SetupChanged -= Rescan;
            CoreMidi.SetupChanged += Rescan;
        }

        public void StopWatching()
        {
            CoreMidi.SetupChanged -= Rescan;
            changed = null;
        }

        /// <summary>A new snapshot, and the names that came and went (CoreMIDI's notification thread, or the caller's).</summary>
        void Rescan()
        {
            try
            {
                lock (scanLock)
                {
                    var before = devices;
                    var after = Scan();
                    devices = after;
                    var report = changed;
                    if (report == null) return;
                    var was = Names(before);
                    var now = Names(after);
                    foreach (var name in now.Where(n => !was.Contains(n)))
                        Report(report, new MidiDeviceChange(name, true, after.Inputs.Length, after.Outputs.Length));
                    foreach (var name in was.Where(n => !now.Contains(n)))
                        Report(report, new MidiDeviceChange(name, false, after.Inputs.Length, after.Outputs.Length));
                }
            }
            catch (Exception e)
            {
                log("MIDI devices: " + e.Message);
            }
        }

        void Report(Action<MidiDeviceChange> report, MidiDeviceChange change)
        {
            try
            {
                report(change);
            }
            catch (Exception e)
            {
                log($"MIDI device {change.Name}: {e.Message}");
            }
        }

        static HashSet<string> Names(Devices d) => new HashSet<string>(d.Inputs.Select(p => p.Name).Concat(d.Outputs.Select(p => p.Name)));

        // ------------------------------------------------------------------ the virtual endpoints

        public bool CreateMidi2Endpoint()
        {
            if (virtualIn == null)
            {
                try
                {
                    virtualIn = new CoreMidi.VirtualDestination(VirtualName, toCore);
                    virtualOut = new CoreMidi.VirtualSource(VirtualName);
                }
                catch (Exception e)
                {
                    log("The ReBuzz virtual MIDI ports didn't open: " + e.Message);
                    ReleaseMidi2();
                    return false;
                }
            }
            Rescan();                // the virtual source is an output now
            return true;
        }

        public void ReleaseMidi2()
        {
            var source = virtualOut;
            virtualOut = null;
            source?.Dispose();
            virtualIn?.Dispose();
            virtualIn = null;
            Rescan();
        }

        // ------------------------------------------------------------------ the devices

        sealed class Input : IMidiInput
        {
            CoreMidi.Input port;

            public Input(string name, CoreMidi.Input port)
            {
                ProductName = name;
                this.port = port;
            }

            public string ProductName { get; }

            public void Close()
            {
                Interlocked.Exchange(ref port, null)?.Dispose();
            }
        }

        sealed class Output : IMidiOutput
        {
            CoreMidi.Sender sender;
            readonly bool owned;

            /// <param name="owned">The sender is this output's (not the shared virtual source).</param>
            public Output(string name, CoreMidi.Sender sender, bool owned)
            {
                ProductName = name;
                this.sender = sender;
                this.owned = owned;
            }

            public string ProductName { get; }

            public void Send(int message) => sender?.Send(message);
            public void Send(byte[] message) => sender?.Send(message);

            /// <summary>Notes off first, as WinMM's reset does on Windows.</summary>
            public void Dispose()
            {
                var s = Interlocked.Exchange(ref sender, null);
                if (s == null) return;
                try { s.Reset(); } catch (Exception) { }
                if (owned) s.Dispose();
            }
        }

        /// <summary>A control surface's own input and output.</summary>
        sealed class ControllerPort : IControllerPort
        {
            readonly CoreMidi.Input input;
            readonly CoreMidi.Sender output;
            readonly bool owned;

            public event Action<int> Received;

            public ControllerPort(uint source, CoreMidi.Sender output, bool owned)
            {
                this.output = output;
                this.owned = owned;
                try
                {
                    input = new CoreMidi.Input(source, m => Received?.Invoke(m));
                }
                catch
                {
                    if (owned) output.Dispose();
                    throw;
                }
            }

            public void Send(int message) => output.Send(message);
            public void Send(byte[] message) => output.Send(message);

            public void Dispose()
            {
                try { input.Dispose(); } catch (Exception) { }
                if (owned) try { output.Dispose(); } catch (Exception) { }
            }
        }
    }
}
