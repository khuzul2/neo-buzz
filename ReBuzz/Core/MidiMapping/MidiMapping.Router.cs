// Fork (docs/specs/midi-mapping.md, MM1): the router. It runs on the MIDI threads (one per device) from a snapshot the UI thread rebuilds
// whenever the maps, the selection, the pins, the machines or the wires change (as SessionMidi.OnMidiThread does). Order:
//
//   1. capture: a control being mapped, or knobs being taught, takes the message;
//   2. the selected track's page: the device's knobs (slot i is knob i), then the page's direct entries;
//   3. global entries (the song's own, or the computer's);
//   4. keys of a device pinned to a track (a track whose MIDI From names it and is not armed: the armed ones are SessionCore's);
//   5. nothing: the message goes on (an unmapped knob of a group says "K7 is free on Drums").
//
// A message allocates nothing: dictionary lookups on a packed key, arithmetic, ParameterCore.SetValue, and a flag the UI thread's event queue
// drains (the automation and history side of a move, and a command's press, run there).

using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;
using ReBuzz.Core.Tracks;
using ReBuzz.Midi;
using ReBuzz.Midi.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReBuzz.Core
{
    internal sealed partial class MidiMapping
    {
        /// <summary>SetValue's flag: not recorded by the setter (the pump records it, on the UI thread, as a mouse move is).</summary>
        const int DoNotRecord = 1 << 16;

        sealed class Snapshot
        {
            public static readonly Snapshot Empty = new Snapshot();

            public readonly Dictionary<string, int> Devices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<long, Route[]> Routes = new Dictionary<long, Route[]>();
            public Dictionary<string, PinnedKeys[]> Pinned;
            public string TrackName;
            public int RouteCount;
        }

        sealed class PinnedKeys
        {
            public MachineCore Machine;
            public TrackInput Input;
        }

        /// <summary>What a control does in a snapshot: a parameter (with its takeover state) or a command, and the way back to the UI thread.</summary>
        sealed class Route : UiEventQueue.ITarget
        {
            public MidiMapping Owner;
            public ParameterCore Param;
            public int Track, PMin, PMax;
            public double RMin, RMax = 1;
            public bool Invert, Discrete, Continuous;
            public Takeover Takeover;
            public KnobEncoding Encoding;
            public MapCommand? Command;
            public int CommandIndex;
            public bool Free;
            public int Slot = -1;
            public TakeoverState State;
            public bool Down;
            int pending, before, last;

            /// <summary>MIDI thread: the control moved (<paramref name="value"/> of <paramref name="max"/>).</summary>
            public void Handle(int value, int max)
            {
                if (Command != null)
                {
                    bool pressed = Continuous ? value * 2 > max : value > 0;
                    if (pressed && !Down) { Down = true; Notify(0, 0, 1); }
                    else if (!pressed) Down = false;
                    return;
                }
                var p = Param;
                if (p == null || Free) return;
                int cur = p.GetValue(Track);
                int next;
                if (!Continuous)
                {
                    // A pad or a key on a parameter: a press toggles it between the two ends of the range (a release is nothing).
                    bool pressed = value > 0;
                    if (!pressed) { Down = false; return; }
                    if (Down) return;
                    Down = true;
                    int lo = MapMath.ToValue(0, PMin, PMax, RMin, RMax, Invert), hi = MapMath.ToValue(1, PMin, PMax, RMin, RMax, Invert);
                    next = cur == hi ? lo : hi;
                }
                else if (Encoding != KnobEncoding.Absolute)
                {
                    int whole = MapMath.Relative(ref State, Encoding, value, PMin, PMax, RMin, RMax, Invert);
                    if (whole == 0) return;
                    next = Math.Clamp(cur + whole, PMin, PMax);
                }
                else
                {
                    double position = value / (double)max;
                    double current = MapMath.ToPosition(cur, PMin, PMax, RMin, RMax, Invert);
                    if (!MapMath.Absolute(ref State, Takeover, position, current, Discrete, out double target)) return;
                    next = MapMath.ToValue(target, PMin, PMax, RMin, RMax, Invert);
                }
                if (next == cur) return;
                p.SetValue(Track | DoNotRecord, next);
                Notify(cur, next, 0);
            }

            void Notify(int was, int now, int kind)
            {
                if (kind == 0) Volatile.Write(ref last, now);
                if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) return;
                before = was;
                Owner.events.Enqueue(this, 0, kind);
            }

            /// <summary>UI thread (the event queue): what a move leaves for it (automation, history) or a command's press.</summary>
            public void Deliver(int arg, int kind)
            {
                Volatile.Write(ref pending, 0);
                if (kind == 1) { Owner.Execute(this); return; }
                var p = Param;
                if (p == null) return;
                int now = Volatile.Read(ref last);
                Owner.buzz.RecordControlChange(p, Track, now);
                Owner.buzz.MidiControllerAssignments?.RecordMove(p, Track, before, now);
            }
        }

        UiEventQueue events;
        volatile Snapshot snap = Snapshot.Empty;
        int reservedRoutes;

        static long Key(int device, MapKind kind, int channel, int number) =>
            ((long)(device + 1) << 16) | ((long)(int)kind << 12) | ((long)(channel & 15) << 8) | (long)(number & 127);

        // ------------------------------------------------------------------ rebuilding

        /// <summary>UI thread: the snapshot of the maps for the selected track, as the MIDI threads read it.</summary>
        public void Rebuild()
        {
            var core = buzz.SongCore;
            events ??= UiEventQueue.For(buzz.dispatcher);
            if (core == null) { snap = Snapshot.Empty; return; }
            var s = new Snapshot();
            var model = SongTracks.Of(core);
            var byName = new Dictionary<string, MachineCore>(StringComparer.Ordinal);
            foreach (var m in core.MachinesList) byName[m.Name] = m;
            var entry = EntryOf(SelectedMachine, model);
            s.TrackName = entry?.Name;
            var lists = new Dictionary<long, List<Route>>();

            int Device(string name)
            {
                if (string.IsNullOrEmpty(name)) return -1;
                if (!s.Devices.TryGetValue(name, out int id)) s.Devices[name] = id = s.Devices.Count;
                return id;
            }
            void Add(MapSource src, Route r)
            {
                r.Owner = this;
                long key = Key(Device(src.Device), src.Kind, src.Channel, src.Number);
                if (!lists.TryGetValue(key, out var list)) lists[key] = list = new List<Route>(2);
                list.Add(r);
                s.RouteCount++;
            }
            Route Make(MapDrive d, MapSource src, KnobEncoding enc)
            {
                var r = new Route { Owner = this, Encoding = enc, Continuous = src.IsContinuous, RMin = d.Min, RMax = d.Max, Invert = d.Invert, Takeover = d.Takeover };
                if (d.Target == null) { r.Free = true; return r; }
                if (d.Target.IsCommand) { r.Command = d.Target.Cmd; r.CommandIndex = d.Target.Index; return r; }
                var p = ResolveParam(d.Target, byName);
                if (p == null) { r.Free = true; return r; }
                r.Param = p;
                r.Track = d.Target.Track;
                r.PMin = p.MinValue;
                r.PMax = p.MaxValue;
                r.Discrete = MapMath.IsDiscrete(p.MinValue, p.MaxValue);
                return r;
            }

            // 1. The knobs of each device's group drive the slots of the selected track's current page; an empty slot falls back to the generator's pin of that number (M4).
            var tracks = entry != null ? song.Find(entry.Id) : null;
            var page = tracks?.CurrentPage;
            var generator = GeneratorOf(entry);
            var pins = generator != null && buzz.PinStore != null ? buzz.PinStore.Get(generator.Name) : Array.Empty<string>();
            foreach (var group in computer.Groups)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    var src = group.Knobs[i] with { Device = group.Device };
                    var enc = i < group.Encodings.Count ? group.Encodings[i] : KnobEncoding.Absolute;
                    Route r;
                    if (entry != null && page != null && page.Slots.TryGetValue(i, out var drive)) r = Make(drive, src, enc);
                    else if (entry != null && generator != null && i < pins.Count && byName.TryGetValue(generator.Name, out var gm)
                        && ResolveParam(MapTarget.Parameter(gm.Name, pins[i]), byName) is { } pinned)
                        r = Make(new MapDrive { Target = MapTarget.Parameter(gm.Name, pinned.Name) }, src, enc);
                    else r = new Route { Owner = this, Free = true };
                    r.Slot = i;
                    r.Continuous = src.IsContinuous;
                    r.Encoding = enc;
                    Add(src, r);
                }
            }
            // 2. The page's direct entries.
            if (entry != null && page != null)
                foreach (var e in page.Direct)
                    Add(e.Source, Make(e, e.Source, e.Encoding));
            // 3. Global entries.
            foreach (var e in song.Global ?? computer.Global)
                Add(e.Source, Make(e, e.Source, e.Encoding));

            foreach (var (key, list) in lists) s.Routes[key] = list.ToArray();

            // 4. Devices pinned to a track (its MIDI From names the device): the keys of a track that is not armed.
            var layout = buzz.SessionLayout;
            if (layout?.Document != null)
            {
                foreach (var m in core.MachinesList)
                {
                    if (!SessionLayout.CanArm(m) || SessionLayout.HasAudioInput(m)) continue;
                    var input = layout.TrackInputOf(m);
                    if (input.Midi != MidiFromKind.Device || string.IsNullOrEmpty(input.Device)) continue;
                    s.Pinned ??= new Dictionary<string, PinnedKeys[]>(StringComparer.OrdinalIgnoreCase);
                    var add = new PinnedKeys { Machine = m, Input = input };
                    s.Pinned[input.Device] = s.Pinned.TryGetValue(input.Device, out var have) ? have.Append(add).ToArray() : new[] { add };
                }
            }

            // The queue has room for a pending event of every route.
            if (s.RouteCount > reservedRoutes)
            {
                events.Reserve(s.RouteCount - reservedRoutes);
                reservedRoutes = s.RouteCount;
            }
            snap = s;
        }

        /// <summary>The device is pinned to a track (its MIDI From names it) or is not one for keys: All inputs leave it out (any thread).</summary>
        internal bool ExcludedFromAllInputs(MidiDevice device)
        {
            if (!device.IsDevice) return false;
            var s = snap;
            return (s.Pinned != null && s.Pinned.ContainsKey(device.Id)) || computerNoKeys.Contains(device.Id);
        }

        volatile HashSet<string> computerNoKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The parameter a target names, or null: no such machine, parameter or track.</summary>
        ParameterCore ResolveParam(MapTarget t, Dictionary<string, MachineCore> byName)
        {
            if (t == null || t.IsCommand || !byName.TryGetValue(t.Machine, out var m)) return null;
            var groups = m.ParameterGroupsList;
            if (groups == null || groups.Count < 3) return null;
            var group = t.IsTrackParam ? groups[2] : groups[1];
            if (t.IsTrackParam && t.Track >= group.TrackCount) return null;
            string name = t.ParamName;
            foreach (ParameterCore p in group.ParametersList)
                if (string.Equals(p.Name, name, StringComparison.Ordinal))
                    return p;
            return null;
        }

        // ------------------------------------------------------------------ the message

        volatile string freeTrack;
        int freeSlot = -1, freeCount;

        /// <summary>The last control of a knob group that had nothing on it ("K7 is free on Drums"), with a count that grows with each; null text before the first.</summary>
        public (int Count, string Text) FreeNotice
        {
            get
            {
                int count = Volatile.Read(ref freeCount);
                int slot = Volatile.Read(ref freeSlot);
                if (count == 0 || slot < 0) return (count, null);
                string track = freeTrack;
                return (count, "K" + (slot + 1) + " is free" + (track == null ? ": no track is selected" : " on " + track));
            }
        }

        /// <summary>
        /// MIDI thread: maps the message. True when it is the mapping's alone (a mapped or taught control, a pinned device's keys played, a
        /// control being captured), so it must not reach machines too.
        /// </summary>
        internal bool OnMidiThread(MidiDevice device, int data)
        {
            int status = data & 0xFF;
            if (status >= 0xF0) return false;
            var s = snap;
            if (!MapSource.TryDecode(data, out var kind, out int channel, out int number, out int value, out int max))
                return PinnedKeysPlay(s, device, data, status);

            var cap = capture;
            if (cap != null && TryCapture(cap, device, kind, channel, number, value, max)) return true;

            string id = device.Id;
            Route[] list = null;
            if (s.Routes.Count > 0)
            {
                if (id.Length > 0 && s.Devices.TryGetValue(id, out int dev)) s.Routes.TryGetValue(Key(dev, kind, channel, number), out list);
                if (list == null) s.Routes.TryGetValue(Key(-1, kind, channel, number), out list);
            }
            if (list != null)
            {
                foreach (var r in list)
                {
                    if (r.Free) continue;
                    RefineWatch(device, kind, channel, number, value);
                    r.Handle(value, max);
                    return true;
                }
                // Every route of this control is a knob with nothing on it.
                var first = list[0];
                if (first.Slot >= 0 && kind == MapKind.ControlChange)
                {
                    freeTrack = s.TrackName;
                    Volatile.Write(ref freeSlot, first.Slot);
                    Interlocked.Increment(ref freeCount);
                }
                return true;
            }
            return PinnedKeysPlay(s, device, data, status);
        }

        /// <summary>The keys of a device pinned to tracks that are not armed play those tracks' machines, whatever is selected.</summary>
        bool PinnedKeysPlay(Snapshot s, MidiDevice device, int data, int status)
        {
            if (s.Pinned == null || !device.IsDevice || !s.Pinned.TryGetValue(device.Id, out var pinned)) return false;
            int channel = status & 0x0F;
            var session = buzz.SessionCore;
            bool sent = false;
            for (int i = 0; i < pinned.Length; i++)
            {
                var p = pinned[i];
                var m = p.Machine;
                if (!MidiRouting.Accepts(p.Input, device, channel, false)) continue;
                if (session != null && session.IsArmedMachine(m)) { sent = true; continue; }        // an armed track is SessionCore's (it records too)
                var dll = m.DLL;
                if (dll == null || dll.IsMissing || dll.IsCrashed || !m.Ready) continue;
                buzz.MachineManager.SendMidiInput(m, data, dll.Name == "Polac VSTi 1.1" || dll.Name == "Polac VST 1.1");
                sent = true;
            }
            // Armed pinned tracks hear it from SessionCore, which leaves the device out of All inputs: nothing else plays it.
            return sent && !AnyArmed(pinned, session);
        }

        static bool AnyArmed(PinnedKeys[] pinned, SessionCore session)
        {
            if (session == null) return false;
            foreach (var p in pinned) if (session.IsArmedMachine(p.Machine)) return true;
            return false;
        }

        // ------------------------------------------------------------------ commands (UI thread)

        void Execute(Route r)
        {
            if (r.Command is not { } command) return;
            switch (command)
            {
                case MapCommand.Play:
                    if (!buzz.PlayRequested) buzz.Playing = true;
                    break;
                case MapCommand.Stop:
                    buzz.ExecuteCommand(BuzzCommand.Stop);
                    break;
                case MapCommand.Record:
                    buzz.Recording = !buzz.Recording;
                    break;
                case MapCommand.Loop:
                    buzz.Looping = !buzz.Looping;
                    break;
                case MapCommand.PreviousTrack: StepTrack(-1); break;
                case MapCommand.NextTrack: StepTrack(1); break;
                case MapCommand.SelectTrack: SelectTrackNumber(r.CommandIndex); break;
                case MapCommand.Arm:
                    if (SelectedGenerator is { } g && buzz.SessionLayout is { } layout)
                    {
                        bool on = !layout.IsMachineArmed(g);
                        if (!on && ReferenceEquals(g, autoArmed)) autoArmed = null;
                        layout.SetMachineArmed(g, on);
                    }
                    break;
                case MapCommand.Mute:
                    if (SelectedGenerator is { } mm) mm.IsMuted = !mm.IsMuted;
                    break;
                case MapCommand.Solo:
                    if (SelectedGenerator is { } sm) sm.IsSoloed = !sm.IsSoloed;
                    break;
            }
        }
    }
}
