#nullable disable
// Where a MIDI-making Neo machine's events go (docs/specs/voice-to-midi.md §7, shared by Neo Voice to MIDI and Neo Audio to MIDI): a target
// machine (SendMIDINote on it, so the notes land in its next block), an external MIDI device, and the host's machine MIDI bus (the MIDI From source
// "Machine: <name>" of armed tracks). The target and the device are found by name, again every second while they are missing.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;

namespace NeoBuzz.Machine
{
    internal sealed class MidiRouter
    {
        readonly IBuzzMachineHost host;
        volatile IMachine target;
        volatile string targetName = "";
        volatile int deviceIndex = -1;
        volatile string deviceName = "";
        long lastResolve;
        IBuzz buzzCache;

        public MidiRouter(IBuzzMachineHost host) { this.host = host; }

        public string TargetName => targetName;
        public string DeviceName => deviceName;
        public bool TargetFound => target != null;

        public IBuzz Buzz
        {
            get
            {
                if (buzzCache != null) return buzzCache;
                try { buzzCache = host?.Machine?.Graph?.Buzz; } catch (Exception) { buzzCache = null; }
                return buzzCache;
            }
        }

        public void SetTarget(string name)
        {
            targetName = name ?? "";
            target = null;
            ResolveTarget(true);
        }

        public void SetDevice(string name)
        {
            deviceName = name ?? "";
            deviceIndex = -1;
            ResolveDevice();
        }

        void ResolveTarget(bool force)
        {
            string name = targetName;
            if (string.IsNullOrEmpty(name)) { target = null; return; }
            try
            {
                var t = target;
                if (t != null && t.Graph != null && t.Name == name && !force) return;
                var machines = host.Machine.Graph.Machines;
                for (int i = 0; i < machines.Count; i++)
                {
                    var m = machines[i];
                    if (m.Name == name && !ReferenceEquals(m, host.Machine)) { target = m; return; }
                }
                target = null;
            }
            catch (Exception) { target = null; }
        }

        void ResolveDevice()
        {
            string name = deviceName;
            if (string.IsNullOrEmpty(name)) { deviceIndex = -1; return; }
            try
            {
                var outs = Buzz?.GetMidiOuts();
                if (outs == null) return;
                foreach (var o in outs)
                    if (string.Equals(o.Item2, name, StringComparison.OrdinalIgnoreCase)) { deviceIndex = o.Item1; return; }
                deviceIndex = -1;
            }
            catch (Exception) { deviceIndex = -1; }
        }

        /// <summary>Looks again for a missing target or device (at most once a second); true when the second has passed (the caller may read the song then too).</summary>
        public bool ResolveWhenDue()
        {
            long now = Environment.TickCount64;
            if (now - lastResolve < 1000) return false;
            lastResolve = now;
            if (!string.IsNullOrEmpty(targetName) && target == null || target != null && target.Graph == null) ResolveTarget(target != null);
            if (!string.IsNullOrEmpty(deviceName) && deviceIndex < 0) ResolveDevice();
            return true;
        }

        /// <summary>The machines the notes can go to (every machine but this one) and the external MIDI outputs, for the pickers.</summary>
        public IEnumerable<string> TargetNames()
        {
            var list = new List<string>();
            try
            {
                foreach (var m in host.Machine.Graph.Machines)
                    if (!ReferenceEquals(m, host.Machine)) list.Add(m.Name);
            }
            catch (Exception) { }
            return list;
        }

        public IEnumerable<string> DeviceNames()
        {
            var list = new List<string>();
            try { foreach (var o in Buzz?.GetMidiOuts() ?? Array.Empty<Tuple<int, string>>()) list.Add(o.Item2); }
            catch (Exception) { }
            return list;
        }

        /// <summary>Sends the first <paramref name="count"/> events to the target, the device and the host's bus.</summary>
        public void Send(MidiEvent[] events, int count)
        {
            if (count <= 0) return;
            var t = target;
            int dev = deviceIndex;
            var bus = Buzz as IMachineMidiBus;
            var self = host?.Machine;
            for (int i = 0; i < count; i++)
            {
                ref var e = ref events[i];
                int status, d1, d2;
                switch (e.Kind)
                {
                    case MidiKind.NoteOn: status = 0x90 | e.Channel; d1 = e.Data1; d2 = e.Data2; break;
                    case MidiKind.NoteOff: status = 0x80 | e.Channel; d1 = e.Data1; d2 = 0; break;
                    case MidiKind.Controller: status = 0xB0 | e.Channel; d1 = e.Data1; d2 = e.Data2; break;
                    default: status = 0xE0 | e.Channel; d1 = e.Data2 & 127; d2 = e.Data2 >> 7; break;
                }
                int data = status | (d1 << 8) | (d2 << 16);
                try
                {
                    if (t != null)
                    {
                        switch (e.Kind)
                        {
                            case MidiKind.NoteOn: t.SendMIDINote(e.Channel, e.Data1, Math.Max(1, e.Data2)); break;
                            case MidiKind.NoteOff: t.SendMIDINote(e.Channel, e.Data1, 0); break;
                            case MidiKind.Controller: t.SendMIDIControlChange(e.Data1, e.Channel, e.Data2); break;
                            default: t.SendMIDIControlChange(0xE0, e.Channel, e.Data2); break;
                        }
                    }
                    if (dev >= 0) Buzz.SendMIDIOutput(dev, data);
                    bus?.SendMachineMIDI(self, data);
                }
                catch (Exception) { }
            }
        }

        /// <summary>The two Commands submenus ("Send MIDI to machine", "Send MIDI to device"); the callbacks set the target or the device ("" for none).</summary>
        public IEnumerable<IMenuItem> Menus(Action<string> setTarget, Action<string> setDevice)
        {
            var targets = new List<IMenuItem>
            {
                new MenuItemVM { Text = "(none)", IsCheckable = true, IsChecked = targetName.Length == 0, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => setTarget("") } },
            };
            foreach (var name in TargetNames())
            {
                string tn = name;
                targets.Add(new MenuItemVM { Text = tn, IsCheckable = true, IsChecked = targetName == tn, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => setTarget(tn) } });
            }
            yield return new MenuItemVM { Text = "Send MIDI to machine", Children = targets };
            var devices = new List<IMenuItem>
            {
                new MenuItemVM { Text = "(none)", IsCheckable = true, IsChecked = deviceName.Length == 0, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => setDevice("") } },
            };
            foreach (var name in DeviceNames())
            {
                string dn = name;
                devices.Add(new MenuItemVM { Text = dn, IsCheckable = true, IsChecked = deviceName == dn, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => setDevice(dn) } });
            }
            yield return new MenuItemVM { Text = "Send MIDI to device", Children = devices };
        }
    }
}
