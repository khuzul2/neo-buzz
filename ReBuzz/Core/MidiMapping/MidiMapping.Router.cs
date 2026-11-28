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
            // MM2: what the lights read (UI thread): the lights of each controller with a profile, and the bank's tracks.
            public DeviceFeedback[] Feedback = Array.Empty<DeviceFeedback>();
            public string SelectedId;
            public string[] BankIds = new string[MapTarget.BankSize];
            public MachineCore[] BankMachines = new MachineCore[MapTarget.BankSize];
            public IMachine[] BankGenerators = new IMachine[MapTarget.BankSize];
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

            // MM2: a mixer control. Volume, pan and sends are on connections, Mute and Solo on a machine, the Master's fader on its Volume
            // parameter (Param). A value is the control's own scale (a fader is 0 to Fader, a pan 0 to 0x8000, a switch 0 or 1).
            public const int Fader = 1000;
            public MixerControl? Mix;
            public MachineConnectionCore[] Outs;
            public MachineConnectionCore SendConn;
            public MachineCore MixMachine, SendTo;
            public int ParamMax;
            public int BankSlot;
            int wanted;

            /// <summary>The control's value now, in its own scale (MIDI thread).</summary>
            int Read()
            {
                switch (Mix)
                {
                    case null: return Param.GetValue(Track);
                    case MixerControl.Master:
                        {
                            double gain = ParamMax <= 0 ? 1 : 1 - Math.Clamp(Param.GetValue(0), 0, ParamMax) / (double)ParamMax;
                            return (int)Math.Round(SessionMixer.MasterFaderOf(gain) * Fader);
                        }
                    case MixerControl.Volume: return Outs.Length == 0 ? 0 : (int)Math.Round(SessionMixer.FaderOf(Outs[0].Amp) * Fader);
                    case MixerControl.Send: return SendConn == null ? 0 : (int)Math.Round(SessionMixer.FaderOf(SendConn.Amp) * Fader);
                    case MixerControl.Pan: return Outs.Length == 0 ? SessionMixer.CenterPan : Outs[0].Pan;
                    case MixerControl.Mute: return MixMachine.IsMuted ? 1 : 0;
                    default: return MixMachine.IsSoloed ? 1 : 0;
                }
            }

            void Write(int next)
            {
                switch (Mix)
                {
                    case null: Param.SetValue(Track | DoNotRecord, next); break;
                    case MixerControl.Master:
                        Param.SetValue(DoNotRecord, (int)Math.Round((1 - SessionMixer.MasterGainOf(next / (double)Fader)) * ParamMax));
                        break;
                    case MixerControl.Volume:
                        {
                            int amp = SessionMixer.AmpOf(next / (double)Fader);
                            int reference = Outs[0].Amp;
                            for (int i = 0; i < Outs.Length; i++)
                                Outs[i].Amp = Math.Clamp(i == 0 || reference <= 0 ? amp : (int)Math.Round((double)Outs[i].Amp * amp / reference), 0, SessionMixer.MaxAmp);
                            break;
                        }
                    case MixerControl.Send:
                        {
                            int amp = SessionMixer.AmpOf(next / (double)Fader);
                            if (SendConn != null) SendConn.Amp = amp;
                            else if (amp > 0) { Volatile.Write(ref wanted, amp); Notify(0, 0, 2); }       // the send's wire is made by the UI thread
                            break;
                        }
                    case MixerControl.Pan:
                        for (int i = 0; i < Outs.Length; i++) Outs[i].Pan = next;
                        break;
                    case MixerControl.Mute: Volatile.Write(ref wanted, next); Notify(0, 0, 3); break;
                    default: Volatile.Write(ref wanted, next); Notify(0, 0, 4); break;
                }
            }

            /// <summary>UI thread: where the control's value is in its range, 0 to 127 (a ring's light). 0 when it drives nothing.</summary>
            public int Position127()
            {
                if (Free || (Param == null && Mix == null)) return 0;
                if (Mix is MixerControl.Mute or MixerControl.Solo) return Read() != 0 ? 127 : 0;
                double position = MapMath.ToPosition(Read(), PMin, PMax, RMin, RMax, Invert);
                return (int)Math.Round(Math.Clamp(position, 0, 1) * 127);
            }

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
                if (Free || (Param == null && Mix == null)) return;
                int cur = Read();
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
                Write(next);
                if (Mix == null || Mix == MixerControl.Master) Notify(cur, next, 0);
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
                if (kind == 2) { Owner.ApplySend(this, Volatile.Read(ref wanted)); return; }
                if (kind >= 3) { Owner.ApplyFlag(this, kind == 3, Volatile.Read(ref wanted) != 0); return; }
                var p = Param;
                if (p == null) return;
                if (Mix == MixerControl.Master) { Owner.buzz.RecordControlChange(p, 0, p.GetValue(0)); return; }
                int now = Volatile.Read(ref last);
                Owner.buzz.RecordControlChange(p, Track, now);
                Owner.buzz.MidiControllerAssignments?.RecordMove(p, Track, before, now);
            }
        }

        /// <summary>What a mixer route needs from the song, worked out once per snapshot.</summary>
        sealed class MixerContext
        {
            public Dictionary<string, MachineCore> ByName;
            public SessionDocument Doc;
            public List<IMachine> Returns;
            public IReadOnlyList<TrackEntry> Tracks;
            public int Bank;
            public MachineCore Master;

            public static MixerContext Of(MidiMapping owner, SongCore core, TrackModel model, Dictionary<string, MachineCore> byName) => new MixerContext
            {
                ByName = byName,
                Doc = owner.buzz.SessionLayout?.Document,
                Returns = owner.ReturnsOf(core, model),
                Tracks = model.Tracks,
                Bank = Volatile.Read(ref owner.bankOffset),
                Master = core.MachinesList.FirstOrDefault(m => m.DLL?.Info?.Type == MachineType.Master),
            };

            /// <summary>The machine of a mixer target's strip (a track's generator, a group's or a return's machine), or null.</summary>
            public MachineCore StripMachine(MapTarget t)
            {
                if (t.Bank > 0)
                {
                    int at = Bank + t.Bank - 1;
                    return at < Tracks.Count && SongTracks.MachineOf(Tracks[at].Head) is MachineCore m ? m : null;
                }
                if (t.Strip.Length == 0) return null;
                return ByName.TryGetValue(t.Strip.Substring(t.Strip.IndexOf(':') + 1), out var named) ? named : null;
            }
        }

        /// <summary>Makes a route of a mixer control (a route with nothing to drive is free).</summary>
        void MixerRoute(Route r, MapTarget t, MixerContext ctx)
        {
            MixerRoute(r, t.Mix.Value, ctx.StripMachine(t), t.Send, ctx);
            r.BankSlot = t.Bank;
        }

        void MixerRoute(Route r, MixerControl control, MachineCore machine, string send, MixerContext ctx)
        {
            r.Mix = control;
            r.MixMachine = machine;
            r.BankSlot = 0;
            r.PMin = 0;
            r.PMax = Route.Fader;
            switch (control)
            {
                case MixerControl.Master:
                    if (ctx.Master?.ParameterGroupsList is { Count: > 1 } g && g[1].ParametersList.Count > 0) { r.Param = (ParameterCore)g[1].ParametersList[0]; r.ParamMax = r.Param.MaxValue; }
                    else r.Free = true;
                    break;
                case MixerControl.Volume:
                    r.Outs = machine == null ? Array.Empty<MachineConnectionCore>() : SessionMixer.MainOutputs(ctx.Doc, machine, ctx.Returns).OfType<MachineConnectionCore>().ToArray();
                    r.Free = r.Outs.Length == 0;
                    break;
                case MixerControl.Pan:
                    r.Outs = machine == null ? Array.Empty<MachineConnectionCore>() : SessionMixer.MainOutputs(ctx.Doc, machine, ctx.Returns).OfType<MachineConnectionCore>().Where(o => o.HasPan).ToArray();
                    r.Free = r.Outs.Length == 0;
                    r.PMax = SessionMixer.MaxPan;
                    break;
                case MixerControl.Send:
                    r.SendTo = !string.IsNullOrEmpty(send) && ctx.ByName.TryGetValue(send, out var to) ? to : null;
                    r.SendConn = SessionMixer.ConnectionTo(machine, r.SendTo) as MachineConnectionCore;
                    r.Free = machine == null || r.SendTo == null || (r.SendConn == null && !SessionMixer.CanSend(machine, r.SendTo));
                    break;
                default:
                    r.Free = machine == null;
                    r.PMax = 1;
                    r.Discrete = true;
                    break;
            }
            if (control is not (MixerControl.Mute or MixerControl.Solo)) r.Discrete = false;
        }

        /// <summary>The returns a track's sends go to: the track model's, then the Session View's own return tracks (as the mixer lists them).</summary>
        List<IMachine> ReturnsOf(SongCore core, TrackModel model)
        {
            var returns = new List<IMachine>();
            foreach (var e in model.Entries.Where(e => e.Kind == TrackKind.Return))
                if (SongTracks.MachineOf(e.Head) is { } head) returns.Add(head);
            if (buzz.SessionLayout?.Document is { } doc)
                foreach (string name in doc.Returns)
                    if (core.Machines.FirstOrDefault(m => m.Name == name) is { } extra && !returns.Contains(extra) && extra.DLL?.Info?.Type == MachineType.Effect)
                        returns.Add(extra);
            return returns;
        }

        /// <summary>UI thread: a send that had no wire yet is raised above nothing: the wire is made (and the route finds it).</summary>
        void ApplySend(Route r, int amp)
        {
            if (r.MixMachine != null && r.SendTo != null && amp > 0) SessionMixer.SetSend(r.MixMachine, r.SendTo, amp);
            Rebuild();
        }

        /// <summary>UI thread: a mixer On or Solo, as one undo step (the mixer's own).</summary>
        void ApplyFlag(Route r, bool mute, bool on)
        {
            var m = r.MixMachine;
            if (m == null) return;
            if (mute && m.IsMuted != on) ReBuzz.Agent.AgentActions.SetFlag(buzz, m, ReBuzz.Agent.AgentActions.Flag.Mute, on);
            else if (!mute && m.IsSoloed != on) ReBuzz.Agent.AgentActions.SetFlag(buzz, m, ReBuzz.Agent.AgentActions.Flag.Solo, on);
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
            var ctx = MixerContext.Of(this, core, model, byName);

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
                if (d.Target.IsMixer) { MixerRoute(r, d.Target, ctx); return r; }
                var p = ResolveParam(d.Target, byName);
                if (p == null) { r.Free = true; return r; }
                r.Param = p;
                r.Track = d.Target.Track;
                // Map version 2: the range the map was saved with, so a parameter that grew since (an enum with new values at its end) is still reached at the same raw values.
                int lo = p.MinValue, hi = p.MaxValue;
                if (d.RawMin is int rawLo && d.RawMax is int rawHi)
                {
                    rawLo = Math.Clamp(rawLo, p.MinValue, p.MaxValue);
                    rawHi = Math.Clamp(rawHi, p.MinValue, p.MaxValue);
                    if (rawHi >= rawLo) { lo = rawLo; hi = rawHi; }
                }
                else if (byName.TryGetValue(d.Target.Machine, out var legacyMachine) && LegacyChoicePins.OldMax(legacyMachine.DLL?.Name, p.Name) is int oldMax)
                {
                    // Map version 1 (no raw range) on a tempo-sync list that has grown since: held to the old last choice (SYN1).
                    hi = Math.Max(lo, Math.Min(hi, oldMax));
                }
                r.PMin = lo;
                r.PMax = hi;
                r.Discrete = MapMath.IsDiscrete(lo, hi);
                return r;
            }

            // 1. The knobs of each device's group drive the slots of the selected track's current page; an empty slot falls back to the generator's pin of that number (M4).
            var tracks = entry != null ? song.Find(entry.Id) : null;
            var page = tracks?.CurrentPage;
            var generator = GeneratorOf(entry);
            var pins = generator != null && buzz.PinStore != null ? buzz.PinStore.Get(generator.Name) : Array.Empty<string>();
            Route KnobRoute(int i, MapSource src, KnobEncoding enc)
            {
                Route r;
                if (entry != null && page != null && page.Slots.TryGetValue(i, out var drive)) r = Make(drive, src, enc);
                else if (entry != null && generator != null && i < pins.Count && byName.TryGetValue(generator.Name, out var gm)
                    && ResolveParam(MapTarget.Parameter(gm.Name, pins[i]), byName) is { } pinned)
                    r = Make(new MapDrive { Target = MapTarget.Parameter(gm.Name, pinned.Name) }, src, enc);
                else r = new Route { Owner = this, Free = true };
                r.Slot = i;
                r.Continuous = src.IsContinuous;
                r.Encoding = enc;
                return r;
            }
            foreach (var group in computer.Groups)
            {
                int limit = computer.SettingOf(group.Device) is { KnobGroup: > 0 } limited ? Math.Min(group.Count, limited.KnobGroup) : group.Count;
                for (int i = 0; i < limit; i++)
                {
                    var src = group.Knobs[i] with { Device = group.Device };
                    var enc = i < group.Encodings.Count ? group.Encodings[i] : KnobEncoding.Absolute;
                    Add(src, KnobRoute(i, src, enc));
                }
            }
            // 2. The page's direct entries.
            if (entry != null && page != null)
                foreach (var e in page.Direct)
                    Add(e.Source, Make(e, e.Source, e.Encoding));
            // 3. Global entries.
            foreach (var e in song.Global ?? computer.Global)
                Add(e.Source, Make(e, e.Source, e.Encoding));

            // 3b. MM2: the profiles of the controllers that have one (after the person's maps, so a control they mapped is theirs).
            s.SelectedId = entry?.Id;
            var bankTracks = ctx.Tracks;
            int bankAt = Volatile.Read(ref bankOffset);
            for (int i = 0; i < MapTarget.BankSize && bankAt + i < bankTracks.Count; i++)
            {
                s.BankIds[i] = bankTracks[bankAt + i].Id;
                s.BankMachines[i] = SongTracks.MachineOf(bankTracks[bankAt + i].Head) as MachineCore;
                s.BankGenerators[i] = GeneratorOf(bankTracks[bankAt + i]);
            }
            var bankColumns = new int[MapTarget.BankSize];
            for (int i = 0; i < bankColumns.Length; i++)
                bankColumns[i] = bankAt + i < bankTracks.Count && GeneratorOf(bankTracks[bankAt + i]) is { } bgen && buzz.SessionLayout != null ? buzz.SessionLayout.FirstTrackOf(bgen) : -1;
            bankFeed.Set(bankColumns);
            ProfileRoutes(s, lists, Device, Add, Make, KnobRoute, ctx);
            computerNoKeys = new HashSet<string>(computer.NoKeys.Concat(s.Devices.Keys.Concat(buzz.MidiInOutEngine?.OpenInputKeys() ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Where(d => RoleOf(d) == DeviceRole.Controls)), StringComparer.OrdinalIgnoreCase);

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

            // The bank's tracks as session tracks, for the clip grid.

            // The queue has room for a pending event of every route.
            if (s.RouteCount > reservedRoutes)
            {
                events.Reserve(s.RouteCount - reservedRoutes);
                reservedRoutes = s.RouteCount;
            }
            bankUsed = lists.Values.Any(l => l.Any(r => r.BankSlot > 0));
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
            var group = t.IsInputParam ? groups[0] : t.IsTrackParam ? groups[2] : groups[1];
            if ((t.IsTrackParam || t.IsInputParam) && t.Track >= group.TrackCount) return null;
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
            NoteMessage(device, data);
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
                case MapCommand.BankLeft: MoveBank(-1); break;
                case MapCommand.BankRight: MoveBank(1); break;
                case MapCommand.BankLeft8: MoveBank(-MapTarget.BankSize); break;
                case MapCommand.BankRight8: MoveBank(MapTarget.BankSize); break;
                case MapCommand.KnobPan: SetKnobMode(0); break;
                case MapCommand.KnobSend: NextKnobSend(); break;
                case MapCommand.SceneUp: MoveScenes(-1); break;
                case MapCommand.SceneDown: MoveScenes(1); break;
                case MapCommand.SelectBankTrack: SelectBankTrack(r.CommandIndex); break;
                case MapCommand.ArmBankTrack:
                    if (BankGenerator(r.CommandIndex) is { } bg && buzz.SessionLayout is { } bl)
                    {
                        bool on = !bl.IsMachineArmed(bg);
                        if (!on && ReferenceEquals(bg, autoArmed)) autoArmed = null;
                        bl.SetMachineArmed(bg, on);
                    }
                    break;
                case MapCommand.StopClipBankTrack:
                    if (bankFeed.TrackOf(r.CommandIndex) is >= 0 and int stopTrack && buzz.SessionMidi is { } sm2 && buzz.SessionLayout is { } sl)
                        sm2.ExecuteNow(SessionTarget.TrackStop, stopTrack, 0, sl.LaunchMap, 127);
                    break;
                case MapCommand.StopAllClips:
                    if (buzz.SessionMidi is { } sm3 && buzz.SessionLayout is { } sl3) sm3.ExecuteNow(SessionTarget.StopAll, 0, 0, sl3.LaunchMap, 127);
                    break;
                case MapCommand.LaunchScene:
                    if (buzz.SessionMidi is { } sm4 && buzz.SessionLayout is { } sl4) sm4.ExecuteNow(SessionTarget.Scene, 0, SceneOffset + r.CommandIndex, sl4.LaunchMap, 127);
                    break;
                case MapCommand.LaunchSlot:
                    LaunchSlot(r.CommandIndex % MapTarget.BankSize, r.CommandIndex / MapTarget.BankSize);
                    break;
            }
        }

        /// <summary>UI thread: a grid pad's press: the clip at (bank column, scene row of the window), or a take when the track is armed and the slot empty (as the Session View's own controllers do).</summary>
        void LaunchSlot(int column, int row)
        {
            var layout = buzz.SessionLayout;
            var midi = buzz.SessionMidi;
            if (layout == null || midi == null) return;
            int track = bankFeed.TrackOf(column + 1), scene = SceneOffset + row;
            if (track < 0 || scene >= layout.SceneCount) return;
            var map = layout.LaunchMap;
            var seq = map.Track(track);
            if (seq == null) return;
            if (map.Slot(track, scene) == null && (buzz.Session as SessionCore)?.IsArmed(seq) == true) layout.RecordTake(track, scene);
            else midi.ExecuteNow(SessionTarget.Slot, track, scene, map, 127);
        }

        // ------------------------------------------------------------------ banks (MM2)

        int bankOffset, sceneOffset;
        readonly BankFeed bankFeed = new BankFeed();

        /// <summary>The first track of the bank: "track n of the current bank" is track BankOffset + n (0 is the first).</summary>
        public int BankOffset => Volatile.Read(ref bankOffset);

        /// <summary>The first scene of the window a clip grid shows.</summary>
        public int SceneOffset => Volatile.Read(ref sceneOffset);

        /// <summary>How many scenes the window shows (the clip grid's rows).</summary>
        public int SceneRows { get; set; } = 5;

        /// <summary>The bank or the scene window moved (UI thread).</summary>
        public event Action BankChanged;

        /// <summary>Moves the bank by n tracks (it stays on the song's tracks, as full as it can be). False when it did not move.</summary>
        public bool MoveBank(int delta)
        {
            int count = Tracks.Count;
            int to = Math.Clamp(BankOffset + delta, 0, Math.Max(0, count - MapTarget.BankSize));
            return SetBank(to, SceneOffset);
        }

        public bool MoveScenes(int delta)
        {
            int count = buzz.SessionLayout?.SceneCount ?? 0;
            return SetBank(BankOffset, Math.Clamp(SceneOffset + delta, 0, Math.Max(0, count - SceneRows)));
        }

        /// <summary>Sets the bank and the scene window; a move makes every fader and knob of the bank take over again (pickup), and the controllers' lights are sent again.</summary>
        public bool SetBank(int tracks, int scenes)
        {
            if (tracks == BankOffset && scenes == SceneOffset) return false;
            Volatile.Write(ref bankOffset, tracks);
            Volatile.Write(ref sceneOffset, scenes);
            Rebuild();
            InvalidateFeedback();
            BankChanged?.Invoke();
            Changed?.Invoke();
            return true;
        }

        /// <summary>Whether a mixer strip (its id: "track:Bass") is one of the bank's eight tracks now.</summary>
        public bool IsInBank(string stripId)
        {
            if (stripId == null || !stripId.StartsWith("track:", StringComparison.Ordinal)) return false;
            var tracks = Tracks;
            int bank = BankOffset;
            for (int i = bank; i < bank + MapTarget.BankSize && i < tracks.Count; i++)
                if (tracks[i].Id == stripId) return true;
            return false;
        }

        /// <summary>The bank window in the Session View: the boxes (first track, first scene, tracks wide, scenes high) to outline, none when nothing uses a bank.</summary>
        public IReadOnlyList<(int Track, int Scene, int Width, int Height)> BankWindow
        {
            get
            {
                var list = new List<(int, int, int, int)>();
                if (!BankInUse) return list;
                int lo = int.MaxValue, hi = -1;
                for (int i = 1; i <= MapTarget.BankSize; i++)
                {
                    int t = bankFeed.TrackOf(i);
                    if (t < 0) continue;
                    lo = Math.Min(lo, t);
                    hi = Math.Max(hi, t);
                }
                if (hi >= 0) list.Add((lo, SceneOffset, hi - lo + 1, SceneRows));
                return list;
            }
        }

        /// <summary>The bank is shown: a controller's profile uses it, a map names a bank track, or it was moved.</summary>
        public bool BankInUse => BankOffset > 0 || SceneOffset > 0 || bankUsed;

        volatile bool bankUsed;

        /// <summary>The track of bank slot n (1 to 8), or null.</summary>
        public TrackEntry BankTrack(int slot)
        {
            var tracks = Tracks;
            int at = BankOffset + slot - 1;
            return slot >= 1 && at < tracks.Count ? tracks[at] : null;
        }

        IMachine BankGenerator(int slot) => GeneratorOf(BankTrack(slot));

        void SelectBankTrack(int slot)
        {
            if (BankTrack(slot) is { } t) Select(MachineOf(t.Head));
        }

        /// <summary>
        /// The bank's tracks as session track numbers (so a clip grid and its lights read them without asking the track model): column n (1 to 8)
        /// is the session track of the n-th track of the bank, or -1. Made by <see cref="Rebuild"/>; read on any thread.
        /// </summary>
        sealed class BankFeed
        {
            volatile int[] tracks = new int[MapTarget.BankSize];

            public int TrackOf(int slot) => slot >= 1 && slot <= MapTarget.BankSize ? tracks[slot - 1] : -1;

            public void Set(int[] next) => tracks = next;
        }
    }
}
