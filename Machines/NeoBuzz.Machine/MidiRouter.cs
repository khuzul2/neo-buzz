#nullable disable
// Where a MIDI-making Neo machine's events go (docs/specs/voice-to-midi.md §7, shared by Neo Voice to MIDI and Neo Audio to MIDI): note wires (docs/specs/note-wires.md:
// to the machines that take notes, in the same block, at the events' offsets), an external MIDI device, and the host's machine MIDI bus (the MIDI From source
// "Machine: <name>" of armed tracks). A song saved before note wires kept one target machine by name: it is handed to the host once, when the song opens
// (INoteLegacyTarget), and becomes a wire. The device is found by name, again every second while it is missing.

using System;
using System.Collections.Generic;
using System.Linq;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;

namespace NeoBuzz.Machine
{
    internal sealed class MidiRouter
    {
        readonly IBuzzMachineHost host;
        volatile int deviceIndex = -1;
        volatile string deviceName = "";
        volatile string legacyTarget = "";
        long lastResolve;
        IBuzz buzzCache;
        INoteEmitter emitter;

        /// <summary>Record at attack: the bus gets each note with how far back its sound began, and a recording track places the note there. The wires and the device always get the note when it is sent.</summary>
        public volatile bool RecordAtAttack = true;

        public MidiRouter(IBuzzMachineHost host) { this.host = host; }

        public string DeviceName => deviceName;

        public IBuzz Buzz
        {
            get
            {
                if (buzzCache != null) return buzzCache;
                try { buzzCache = host?.Machine?.Graph?.Buzz; } catch (Exception) { buzzCache = null; }
                return buzzCache;
            }
        }

        INoteWireHost Wires => Buzz as INoteWireHost;

        /// <summary>The machine the notes go to (the first of its note wires), or "".</summary>
        public string TargetName
        {
            get
            {
                try { return Wires?.NoteTargetsOf(host.Machine).FirstOrDefault() ?? ""; } catch (Exception) { return ""; }
            }
        }

        public bool TargetFound => TargetName.Length > 0;

        /// <summary>What a song saved before note wires named as the target: kept until the host takes it (once).</summary>
        public void SetLegacyTarget(string name) { if (!string.IsNullOrEmpty(name)) legacyTarget = name; }

        public string TakeLegacyTarget()
        {
            string t = legacyTarget;
            legacyTarget = "";
            return t.Length == 0 ? null : t;
        }

        /// <summary>Sends the notes to <paramref name="name"/> only ("" for nowhere): the machine's wires are replaced, one undo step.</summary>
        public void SetTarget(string name)
        {
            try { Wires?.SetNoteTarget(host.Machine, name ?? ""); } catch (Exception) { }
        }

        public void SetDevice(string name)
        {
            deviceName = name ?? "";
            deviceIndex = -1;
            ResolveDevice();
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

        /// <summary>Looks again for a missing device (at most once a second); true when the second has passed (the caller may read the song then too).</summary>
        public bool ResolveWhenDue()
        {
            long now = Environment.TickCount64;
            if (now - lastResolve < 1000) return false;
            lastResolve = now;
            if (!string.IsNullOrEmpty(deviceName) && deviceIndex < 0) ResolveDevice();
            return true;
        }

        /// <summary>The machines the notes can go to (every machine that takes notes but this one) and the external MIDI outputs, for the pickers.</summary>
        public IEnumerable<string> TargetNames()
        {
            try { return Wires?.NoteTargetCandidates(host.Machine) ?? Array.Empty<string>(); } catch (Exception) { return Array.Empty<string>(); }
        }

        public IEnumerable<string> DeviceNames()
        {
            var list = new List<string>();
            try { foreach (var o in Buzz?.GetMidiOuts() ?? Array.Empty<Tuple<int, string>>()) list.Add(o.Item2); }
            catch (Exception) { }
            return list;
        }

        /// <summary>Sends the first <paramref name="count"/> events down the note wires (same block, same offsets), to the device and to the host's bus.</summary>
        public void Send(MidiEvent[] events, int count)
        {
            if (count <= 0) return;
            int dev = deviceIndex;
            var bus = Buzz as IMachineMidiBus;
            var self = host?.Machine;
            var em = emitter ??= self as INoteEmitter;
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
                    if (em != null)
                    {
                        NoteMessage m = default;
                        m.Offset = e.Offset; m.Channel = e.Channel;
                        switch (e.Kind)
                        {
                            case MidiKind.NoteOn: m.Kind = NoteMessageKind.NoteOn; m.Data1 = e.Data1; m.Data2 = Math.Max(1, e.Data2); break;
                            case MidiKind.NoteOff: m.Kind = NoteMessageKind.NoteOff; m.Data1 = e.Data1; break;
                            case MidiKind.Controller: m.Kind = NoteMessageKind.Controller; m.Data1 = e.Data1; m.Data2 = e.Data2; break;
                            default: m.Kind = NoteMessageKind.PitchBend; m.Data2 = e.Data2; break;
                        }
                        em.EmitNote(m);
                    }
                    if (dev >= 0) Buzz.SendMIDIOutput(dev, data);
                    if (bus != null)
                    {
                        int lag = e.RecordedLag(RecordAtAttack);
                        if (lag > 0) bus.SendMachineMIDI(self, data, lag);
                        else bus.SendMachineMIDI(self, data);
                    }
                }
                catch (Exception) { }
            }
        }

        /// <summary>The two Commands submenus ("Send notes to machine": each machine that takes notes is a wire, checked while there is one; "Send MIDI to device"). The device callback sets the device ("" for none).</summary>
        public IEnumerable<IMenuItem> Menus(Action<string> setTarget, Action<string> setDevice)
        {
            var wired = new HashSet<string>(Wires?.NoteTargetsOf(host.Machine) ?? Array.Empty<string>(), StringComparer.Ordinal);
            var targets = new List<IMenuItem>
            {
                new MenuItemVM { Text = "(none)", IsCheckable = true, IsChecked = wired.Count == 0, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => setTarget("") } },
            };
            foreach (var name in TargetNames())
            {
                string tn = name;
                bool on = wired.Contains(tn);
                targets.Add(new MenuItemVM
                {
                    Text = tn, IsCheckable = true, IsChecked = on,
                    Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => { if (on) Wires?.DisconnectNotes(host.Machine, tn); else Wires?.ConnectNotes(host.Machine, tn); } },
                });
            }
            yield return new MenuItemVM { Text = "Send notes to machine", Children = targets };
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
