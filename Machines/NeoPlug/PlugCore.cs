#nullable disable
// One Neo Plug machine's plug-in: opening it, the 64-slot parameter bank that mirrors it, state, MIDI, and the audio
// block. The generator and the effect machine are thin shells over this (docs/specs/plugin-host.md §3.3 to §3.6).
//
// Threads. PlugMain (see PlugMain.cs) opens, closes, saves and loads state and runs the pump; it holds Gate for the
// calls that must not overlap the audio. The audio thread takes Gate with TryEnter and plays silence when it can't.
// Nothing on the audio path allocates.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using NeoBuzz.Plug;

namespace NeoBuzz.Plug
{
    /// <summary>What one slot is bound to, for the manifest and the tests.</summary>
    public struct SlotBinding
    {
        public int Slot;
        public uint Id;
        public string Name, Units, Module;
        public int Steps, Max, Default;
        public double Min, MaxValue, DefValue;
    }

    sealed class PlugCore : IDisposable
    {
        public const int Slots = 64, Chunk = 256, MaxTracks = 16;
        public const int RawMax = 65534;
        static readonly bool DebugMidi = Environment.GetEnvironmentVariable("NEOPLUG_DEBUG") == "1";
        public static readonly HashSet<PlugCore> Live = new();

        public readonly object Gate = new();
        readonly IBuzzMachineHost host;
        readonly bool effect;

        IntPtr plugin;
        PlugNative.ClassInfo info;
        public PlugState State = new();
        public volatile bool Missing;
        public string Problem = "";
        public volatile bool Busy;
        public bool Instrument { get; private set; }
        public bool IsOpen => plugin != IntPtr.Zero;
        public string Name => State.Name ?? "";

        // Slots
        readonly uint[] slotId = new uint[Slots];
        readonly bool[] bound = new bool[Slots];
        readonly double[] smin = new double[Slots], smax = new double[Slots], sdef = new double[Slots];
        readonly int[] ssteps = new int[Slots];
        readonly string[] sname = new string[Slots], sunits = new string[Slots], smodule = new string[Slots];
        readonly int[] mirror = new int[Slots];
        readonly int[] defRaw = new int[Slots];   // the value a slot has when the plug-in is opened: its default for the engine (the engine sets new machines to their defaults)
        readonly int[] toController = new int[Slots];   // 1: the pump tells the plug-in's controller about the engine's write
        readonly int[] pending = new int[Slots];
        readonly long[] wroteAt = new long[Slots];
        IParameter[] slotParams;
        int applyQueued;
        readonly int[] applyValue = new int[Slots];
        public int Revision;

        // Audio
        int rate = 44100;
        int latency, tailFrames;
        int auxIn;
        int quiet;
        double beatPos;
        bool wasPlaying;
        readonly float[] inL = new float[Chunk], inR = new float[Chunk], auxL = new float[Chunk], auxR = new float[Chunk], outL = new float[Chunk], outR = new float[Chunk];
        readonly PlugNative.Event[] events = new PlugNative.Event[1024];
        readonly PlugNative.Event[] chunkEvents = new PlugNative.Event[1024];
        readonly PlugNative.Event[] outEvents = new PlugNative.Event[512];
        readonly PlugNative.Event[] midiRing = new PlugNative.Event[1024];
        int midiCount;
        readonly object midiLock = new();
        volatile bool stopAll;
        volatile bool resetRequested;
        public volatile int MidiChannel;

        // Tracks (generator)
        readonly int[] trackKey = new int[MaxTracks];
        readonly int[] pendingNote = new int[MaxTracks], pendingVel = new int[MaxTracks], pendingDelay = new int[MaxTracks];
        readonly int[] carryNote = new int[MaxTracks], carryVel = new int[MaxTracks], carryFrames = new int[MaxTracks];
        readonly int[] pendingParam = new int[MaxTracks], pendingValue = new int[MaxTracks];

        // MIDI out
        volatile IMachine midiTarget;
        volatile string midiTargetName = "";

        public PlugCore(IBuzzMachineHost host, bool effect)
        {
            this.host = host;
            this.effect = effect;
            Array.Fill(pending, -1);
            Array.Fill(trackKey, -1);
            Array.Fill(pendingNote, -1);
            Array.Fill(pendingVel, -1);
            Array.Fill(pendingDelay, -1);
            Array.Fill(carryNote, -1);
            Array.Fill(pendingParam, -1);
            Array.Fill(pendingValue, -1);
            lock (Live) Live.Add(this);
            PlugMain.AddPump(Pump);
        }

        public void Dispose()
        {
            PlugMain.RemovePump(Pump);
            lock (Live) Live.Remove(this);
            try { PlugMain.Invoke(CloseNow, 10000); } catch (Exception) { }
        }

        static void Log(string m) => Console.Error.WriteLine("[neoplug] " + m);

        int HostRate()
        {
            int sr = 0;
            try { sr = host?.MasterInfo?.SamplesPerSec ?? 0; } catch (Exception) { }
            return sr > 0 ? sr : 44100;
        }

        // --- Opening and closing (PlugMain) ----------------------------------------------------------

        /// <summary>Opens the plug-in an entry names. Returns false (the machine shows as missing) when it can't.</summary>
        public bool Open(PlugEntry entry, string display, bool fromSong)
        {
            if (!PlugNative.Available(out string why))
            {
                Missing = true;
                Problem = why;
                return false;
            }
            return PlugMain.Invoke(() => OpenOnMain(entry, display, fromSong));
        }

        bool OpenOnMain(PlugEntry entry, string display, bool fromSong)
        {
            Busy = true;
            try
            {
                lock (Gate)
                {
                    CloseLocked();
                    State.Format = entry.Format;
                    State.Path = entry.Path;
                    State.Id = entry.Id;
                    State.Name = entry.Name;
                    State.Vendor = entry.Vendor;
                    State.Display = display ?? entry.Display;
                    var err = new byte[1024];
                    plugin = PlugNative.np_open(entry.Path, string.IsNullOrEmpty(entry.Id) ? null : entry.Id, err, err.Length);
                    if (plugin == IntPtr.Zero)
                    {
                        Missing = true;
                        Problem = "could not load: " + PlugNative.Str(err);
                        Log($"{entry.Name}: {Problem}");
                        return false;
                    }
                    Missing = false;
                    Problem = "";
                    PlugNative.np_info(plugin, out info);
                    Instrument = info.is_instrument != 0;
                    rate = HostRate();
                    if (PlugNative.np_activate(plugin, rate, Chunk, 0, err, err.Length) != 0)
                        Log($"{entry.Name}: activate failed: {PlugNative.Str(err)}");
                    Refresh();
                    Bind();
                    Interlocked.Increment(ref Revision);
                    Log($"loaded {entry.Format} \"{entry.Name}\" at {rate} Hz ({PlugNative.np_param_count(plugin)} parameters, {CountBound()} bound, latency {latency})");
                    return true;
                }
            }
            finally { Busy = false; }
        }

        void Refresh()
        {
            latency = Math.Max(0, PlugNative.np_latency(plugin));
            int tail = PlugNative.np_tail(plugin);
            tailFrames = tail < 0 ? rate * 6 : tail;
            auxIn = PlugNative.np_aux_channels(plugin);
        }

        int CountBound()
        {
            int n = 0;
            for (int i = 0; i < Slots; i++) if (bound[i]) n++;
            return n;
        }

        void CloseLocked()
        {
            if (plugin == IntPtr.Zero) return;
            try { PlugNative.np_deactivate(plugin); } catch (Exception) { }
            try { PlugNative.np_close(plugin); } catch (Exception) { }
            plugin = IntPtr.Zero;
        }

        /// <summary>Closes the plug-in at process exit or on dispose (PlugMain).</summary>
        public void CloseNow()
        {
            lock (Gate) CloseLocked();
        }

        // --- Slots -------------------------------------------------------------------------------------

        void Bind()
        {
            for (int i = 0; i < Slots; i++)
            {
                bound[i] = false;
                slotId[i] = 0;
                sname[i] = sunits[i] = smodule[i] = "";
                ssteps[i] = 0;
                Volatile.Write(ref pending[i], -1);
            }
            int count = PlugNative.np_param_count(plugin);
            var infos = new List<PlugNative.ParamInfo>();
            for (int k = 0; k < count; k++)
                if (PlugNative.np_param_info_at(plugin, k, out var pi) == 0) infos.Add(pi);
            var byId = new Dictionary<uint, PlugNative.ParamInfo>();
            foreach (var pi in infos) byId[pi.id] = pi;

            var saved = new List<string>((State.SlotIds ?? "").Split(','));
            bool haveMap = saved.Count == Slots && saved.Exists(s => s.Length > 0);
            var used = new HashSet<uint>();
            if (haveMap)
            {
                for (int i = 0; i < Slots; i++)
                    if (uint.TryParse(saved[i], out uint id) && byId.TryGetValue(id, out var pi) && used.Add(id)) Assign(i, pi);
            }
            else
            {
                int next = 0;
                foreach (var pi in infos)
                {
                    const int skip = PlugNative.ParamReadOnly | PlugNative.ParamHidden | PlugNative.ParamBypass;
                    if ((pi.flags & PlugNative.ParamAutomatable) == 0 || (pi.flags & skip) != 0) continue;
                    if (next >= Slots) break;
                    used.Add(pi.id);
                    Assign(next++, pi);
                }
            }
            // an id of 0 is a valid parameter id, so unbound slots are the empty entries
            var ids = new string[Slots];
            for (int i = 0; i < Slots; i++) ids[i] = bound[i] ? slotId[i].ToString() : "";
            State.SlotIds = string.Join(",", ids);
            for (int i = 0; i < Slots; i++)
            {
                mirror[i] = bound[i] ? ToRaw(i, PlugNative.np_param_get(plugin, slotId[i])) : 0;
                defRaw[i] = mirror[i];
                toController[i] = 0;
            }
        }

        void Assign(int i, PlugNative.ParamInfo pi)
        {
            string unit = PlugNative.Str(pi.module);
            bool numeric = unit.Length > 0 && unit.All(char.IsDigit);   // a VST3 unit id, not a name
            bound[i] = true;
            slotId[i] = pi.id;
            smin[i] = pi.min;
            smax[i] = pi.max;
            sdef[i] = pi.def;
            ssteps[i] = pi.flags != 0 && (pi.flags & PlugNative.ParamStepped) != 0 && pi.steps > 0 ? pi.steps : 0;
            sname[i] = PlugNative.Str(pi.name);
            sunits[i] = PlugNative.Str(pi.units);
            smodule[i] = numeric ? "" : unit;
        }

        public int SlotMax(int i) => bound[i] && ssteps[i] > 0 ? ssteps[i] : RawMax;

        int ToRaw(int i, double v)
        {
            double span = smax[i] - smin[i];
            if (!(span > 0)) return 0;
            double t = Math.Clamp((v - smin[i]) / span, 0, 1);
            return ssteps[i] > 0 ? (int)Math.Round(t * ssteps[i]) : (int)Math.Round(t * RawMax);
        }

        double ToValue(int i, int raw)
        {
            double span = smax[i] - smin[i];
            if (ssteps[i] > 0) return smin[i] + span * Math.Clamp(raw, 0, ssteps[i]) / ssteps[i];
            return smin[i] + span * Math.Clamp(raw, 0, RawMax) / (double)RawMax;
        }

        public bool IsBound(int i) => i >= 0 && i < Slots && bound[i];
        public int SlotValue(int i) => i >= 0 && i < Slots ? Volatile.Read(ref mirror[i]) : 0;

        public List<SlotBinding> Bindings()
        {
            var list = new List<SlotBinding>();
            for (int i = 0; i < Slots; i++)
                if (bound[i])
                    list.Add(new SlotBinding
                    {
                        Slot = i, Id = slotId[i], Name = sname[i], Units = sunits[i], Module = smodule[i], Steps = ssteps[i], Max = SlotMax(i),
                        Min = smin[i], MaxValue = smax[i], DefValue = sdef[i], Default = defRaw[i],
                    });
            return list;
        }

        /// <summary>The machine's slot property setter (the engine's thread, inside Tick). Idempotent: ReBuzz re-sending the value we mirror does nothing.</summary>
        public void SlotSet(int i, int raw)
        {
            if (i < 0 || i >= Slots) return;
            if (raw == Volatile.Read(ref mirror[i])) return;
            Volatile.Write(ref mirror[i], raw);
            Volatile.Write(ref wroteAt[i], Environment.TickCount64);
            if (bound[i])
            {
                Volatile.Write(ref pending[i], raw);
                Volatile.Write(ref toController[i], 1);
            }
        }

        /// <summary>The text of a slot value as the plug-in shows it ("-6.0 dB", "Saw").</summary>
        public string SlotText(int i, int raw)
        {
            if (i < 0 || i >= Slots || !bound[i] || plugin == IntPtr.Zero) return null;
            try
            {
                var buf = new byte[160];
                return PlugNative.np_param_text(plugin, slotId[i], ToValue(i, raw), buf, buf.Length) >= 0 ? PlugNative.Str(buf) : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The text for a value, asked on PlugMain (the engine's describe calls come from other threads).</summary>
        public string SlotTextSafe(int i, int raw)
        {
            if (Missing || plugin == IntPtr.Zero || PlugMain.OnThread) return SlotText(i, raw);
            string cached = textCache(i, raw);
            return cached;
        }

        readonly Dictionary<long, string> texts = new();
        string textCache(int i, int raw)
        {
            long key = ((long)i << 32) | (uint)raw;
            lock (texts)
            {
                if (texts.TryGetValue(key, out var t)) return t;
            }
            string s;
            try { s = PlugMain.Invoke(() => SlotText(i, raw), 2000); } catch (Exception) { return null; }
            lock (texts)
            {
                if (texts.Count > 4096) texts.Clear();
                texts[key] = s;
            }
            return s;
        }

        /// <summary>Gives the engine the slots' names and ranges, and its values the plug-in's (the caller's thread, not PlugMain).</summary>
        public void PushToEngine()
        {
            var runtime = host as IBuzzMachineHostRuntime;
            if (runtime == null) return;
            lock (texts) texts.Clear();
            for (int i = 0; i < Slots; i++)
            {
                if (bound[i])
                {
                    string desc = smodule[i].Length > 0 ? $"{sname[i]} ({smodule[i]})" : sname[i];
                    if (sunits[i].Length > 0) desc += " [" + sunits[i] + "]";
                    runtime.SetGlobalParameterInfo(i, sname[i], desc, 0, SlotMax(i), defRaw[i], true);
                }
                else runtime.SetGlobalParameterInfo(i, "Slot " + (i + 1), "Unused", 0, RawMax, 0, false);
            }
            try
            {
                var list = host.Machine.ParameterGroups[1].Parameters;
                var p = new IParameter[Slots];
                for (int i = 0; i < Slots && i < list.Count; i++) p[i] = list[i];
                slotParams = p;
                for (int i = 0; i < Slots; i++) p[i]?.SetValue(1 << 16, Volatile.Read(ref mirror[i]));
            }
            catch (Exception e) { Log("could not set the slot values: " + e.Message); }
        }

        // --- The pump (PlugMain, every ~20 ms) --------------------------------------------------------

        void Pump()
        {
            if (plugin == IntPtr.Zero || Missing || Busy) return;
            try
            {
                PlugNative.np_idle(plugin);
                int flags = PlugNative.np_poll(plugin);
                if ((flags & PlugNative.PollLatency) != 0) Refresh();
                if ((flags & PlugNative.PollRestart) != 0) Reactivate(offlineMode);
                long now = Environment.TickCount64;
                bool any = false;
                for (int i = 0; i < Slots; i++)
                {
                    if (!bound[i]) continue;
                    // The engine wrote this slot: the processor gets it as an event; a VST3 controller needs to be told too.
                    if (Volatile.Read(ref toController[i]) == 1 && Volatile.Read(ref pending[i]) < 0)
                    {
                        Volatile.Write(ref toController[i], 0);
                        PlugNative.np_param_set(plugin, slotId[i], ToValue(i, Volatile.Read(ref mirror[i])));
                    }
                    if (now - Volatile.Read(ref wroteAt[i]) < 150 || Volatile.Read(ref pending[i]) >= 0) continue;
                    int raw = ToRaw(i, PlugNative.np_param_get(plugin, slotId[i]));
                    if (raw == Volatile.Read(ref mirror[i])) continue;
                    Volatile.Write(ref mirror[i], raw);
                    Volatile.Write(ref applyValue[i], raw + 1);
                    any = true;
                }
                if (any && Interlocked.Exchange(ref applyQueued, 1) == 0) ThreadPool.QueueUserWorkItem(_ => ApplyToEngine());
                UpdateMidiTarget();
            }
            catch (Exception) { }
        }

        // The plug-in changed a parameter itself (its own window, an automation echo): tell the engine, off PlugMain, since
        // IParameter.SetValue takes the machine's work lock.
        void ApplyToEngine()
        {
            try
            {
                var p = slotParams;
                if (p == null) return;
                for (int i = 0; i < Slots; i++)
                {
                    int v = Interlocked.Exchange(ref applyValue[i], 0);
                    if (v > 0) p[i]?.SetValue(1 << 16, v - 1);
                }
            }
            catch (Exception) { }
            finally { Volatile.Write(ref applyQueued, 0); }
        }

        // --- State ---------------------------------------------------------------------------------------

        /// <summary>The state for a song: the plug-in's own blob, deflated, with the identity and the slot map.</summary>
        public PlugState SaveState()
        {
            if (Missing || plugin == IntPtr.Zero) return State;
            try
            {
                return PlugMain.Invoke(() =>
                {
                    PlugNative.np_idle(plugin);
                    int size = PlugNative.np_state_save(plugin, null, 0);
                    if (size > 0)
                    {
                        var blob = new byte[size];
                        int got = PlugNative.np_state_save(plugin, blob, size);
                        if (got > 0 && got <= size) State.Blob = PlugState.Pack(got == size ? blob : blob.AsSpan(0, got).ToArray());
                    }
                    var copy = State.Copy();
                    copy.MidiChannel = MidiChannel;
                    copy.MidiOut = midiTargetName;
                    return copy;
                }, 20000);
            }
            catch (Exception e)
            {
                Log("could not save the plug-in's state: " + e.Message);
                return State;
            }
        }

        /// <summary>Loads a song's state (the plug-in is open already, or missing and the state is kept unchanged).</summary>
        public void LoadState(PlugState s)
        {
            MidiChannel = Math.Clamp(s.MidiChannel, 0, 15);
            SetMidiTarget(s.MidiOut);
            if (Missing || plugin == IntPtr.Zero)
            {
                var keep = s.Copy();
                State = keep;
                return;
            }
            State.MidiChannel = s.MidiChannel;
            State.MidiOut = s.MidiOut;
            var blob = PlugState.Unpack(s.Blob);
            if (blob == null || blob.Length == 0) return;
            try
            {
                PlugMain.Invoke(() =>
                {
                    Busy = true;
                    try
                    {
                        lock (Gate)
                        {
                            var err = new byte[512];
                            PlugNative.np_deactivate(plugin);
                            int rc = PlugNative.np_state_load(plugin, blob, blob.Length, err, err.Length);
                            PlugNative.np_activate(plugin, rate, Chunk, offlineMode ? 1 : 0, err, err.Length);
                            PlugNative.np_idle(plugin);
                            Refresh();
                            for (int i = 0; i < Slots; i++)
                                if (bound[i]) Volatile.Write(ref mirror[i], ToRaw(i, PlugNative.np_param_get(plugin, slotId[i])));
                            Array.Fill(pending, -1);
                            if (rc != 0) Log($"state load failed: {PlugNative.Str(err)}");
                        }
                    }
                    finally { Busy = false; }
                }, 60000);
                var p = slotParams;
                if (p != null) for (int i = 0; i < Slots; i++) p[i]?.SetValue(1 << 16, Volatile.Read(ref mirror[i]));
            }
            catch (Exception e) { Log("could not load the plug-in's state: " + e.Message); }
        }

        // --- Offline rendering ---------------------------------------------------------------------------

        bool offlineMode;

        void Reactivate(bool offline)
        {
            if (plugin == IntPtr.Zero) return;
            lock (Gate)
            {
                var err = new byte[512];
                offlineMode = offline;
                PlugNative.np_deactivate(plugin);
                rate = HostRate();
                PlugNative.np_activate(plugin, rate, Chunk, offline ? 1 : 0, err, err.Length);
                Refresh();
            }
        }

        /// <summary>A render starts: the plug-in is told it's offline, and forgets what rang before.</summary>
        public void BeginRender()
        {
            UpdateMidiTarget();   // a render starts in a moment: the pump may not have found the MIDI target yet
            if (Missing || plugin == IntPtr.Zero) return;
            try
            {
                PlugMain.Invoke(() =>
                {
                    Reactivate(true);
                    lock (Gate) PlugNative.np_reset(plugin);
                }, 20000);
            }
            catch (Exception) { }
            ClearPlaying();
        }

        public void EndRender()
        {
            if (Missing || plugin == IntPtr.Zero) return;
            try { PlugMain.Invoke(() => Reactivate(false), 20000); } catch (Exception) { }
            ClearPlaying();
        }

        void ClearPlaying()
        {
            lock (midiLock) midiCount = 0;
            Array.Fill(trackKey, -1);
            Array.Fill(carryNote, -1);
            Array.Fill(pending, -1);
            quiet = 0;
            beatPos = 0;
            wasPlaying = false;
        }

        public int Latency => latency;

        // --- MIDI out --------------------------------------------------------------------------------------

        public string MidiOutName => midiTargetName;

        public void SetMidiTarget(string name)
        {
            midiTargetName = name ?? "";
            midiTarget = null;
            State.MidiOut = midiTargetName;
        }

        void UpdateMidiTarget()
        {
            string name = midiTargetName;
            if (string.IsNullOrEmpty(name)) { midiTarget = null; return; }
            var t = midiTarget;
            try
            {
                if (t != null && t.Graph != null && t.Name == name) return;
                foreach (var m in host.Machine.Graph.Machines)
                    if (m.Name == name && !ReferenceEquals(m, host.Machine)) { midiTarget = m; return; }
                midiTarget = null;
            }
            catch (Exception) { midiTarget = null; }
        }

        public IEnumerable<string> MidiTargets()
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

        // --- Input events (any thread) ------------------------------------------------------------------

        void QueueMidi(in PlugNative.Event e)
        {
            lock (midiLock)
                if (midiCount < midiRing.Length) midiRing[midiCount++] = e;
        }

        public void MidiNote(int channel, int value, int velocity)
        {
            QueueMidi(new PlugNative.Event { type = velocity > 0 ? PlugNative.EvNoteOn : PlugNative.EvNoteOff, channel = channel & 15, key = value & 127, note_id = -1, value = velocity / 127.0 });
        }

        public void MidiControlChange(int ctrl, int channel, int value)
        {
            channel &= 15;
            if (ctrl == 0xE0) QueueMidi(new PlugNative.Event { type = PlugNative.EvPitchBend, channel = channel, value = Math.Clamp((value - 8192) / 8192.0, -1, 1) });
            else if (ctrl == 0xD0) QueueMidi(new PlugNative.Event { type = PlugNative.EvChannelPressure, channel = channel, value = Math.Clamp(value / 127.0, 0, 1) });
            else if (ctrl >= 0 && ctrl < 128) QueueMidi(new PlugNative.Event { type = PlugNative.EvCc, channel = channel, key = ctrl, value = Math.Clamp(value / 127.0, 0, 1) });
        }

        public void StopAll() => stopAll = true;

        // --- Track parameters (generator) ---------------------------------------------------------------

        public void SetNote(int track, int note) { if ((uint)track < MaxTracks) Volatile.Write(ref pendingNote[track], note); }
        public void SetVelocity(int track, int v) { if ((uint)track < MaxTracks) Volatile.Write(ref pendingVel[track], v); }
        public void SetDelay(int track, int d) { if ((uint)track < MaxTracks) Volatile.Write(ref pendingDelay[track], d); }
        public void SetParam(int track, int v) { if ((uint)track < MaxTracks) Volatile.Write(ref pendingParam[track], v); }
        public void SetValue(int track, int v) { if ((uint)track < MaxTracks) Volatile.Write(ref pendingValue[track], v); }

        // --- The audio block ---------------------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static bool HasSignal(Sample[] x, int n)
        {
            for (int i = 0; i < n; i++)
                if (x[i].L != 0 || x[i].R != 0) return true;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void SortByFrame(PlugNative.Event[] e, int n)
        {
            for (int i = 1; i < n; i++)
            {
                var x = e[i];
                int j = i - 1;
                while (j >= 0 && e[j].frame > x.frame)
                {
                    e[j + 1] = e[j];
                    j--;
                }
                e[j + 1] = x;
            }
        }

        /// <summary>
        /// One block for the machine: <paramref name="input"/> is the main input (null for a generator), <paramref name="side"/> the
        /// sidechain (null when nothing is connected). Returns whether the output has sound (or may, within the tail).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool Render(Sample[] input, Sample[] side, Sample[] output, int n)
        {
            if (!Monitor.TryEnter(Gate))
            {
                Array.Clear(output, 0, n);
                return false;
            }
            try
            {
                if (plugin == IntPtr.Zero || Missing)
                {
                    if (effect && input != null && !ReferenceEquals(input, output)) Array.Copy(input, output, n);
                    else if (!effect) Array.Clear(output, 0, n);
                    return effect && input != null;
                }
                var master = host.MasterInfo;
                int ne = 0;
                bool playing = false;
                try { playing = host.Machine.Graph.Buzz.Playing; } catch (Exception) { }
                double bpm = master.BeatsPerMin > 0 ? master.BeatsPerMin : 120;
                int samplesPerTick = master.SamplesPerTick > 0 ? master.SamplesPerTick : 5000;
                int hostRate = master.SamplesPerSec;
                if (hostRate > 0 && hostRate != rate) PlugMain.Post(() => { if (HostRate() != rate) Reactivate(offlineMode); });

                if (stopAll)
                {
                    stopAll = false;
                    events[ne++] = new PlugNative.Event { type = PlugNative.EvAllNotesOff };
                    Array.Fill(trackKey, -1);
                    Array.Fill(carryNote, -1);
                }
                if (wasPlaying && !playing) events[ne++] = new PlugNative.Event { type = PlugNative.EvAllNotesOff };
                if (!playing) beatPos = 0;
                wasPlaying = playing;

                int ch = MidiChannel;
                for (int t = 0; t < MaxTracks; t++)
                {
                    int note = Interlocked.Exchange(ref pendingNote[t], -1);
                    int vel = Interlocked.Exchange(ref pendingVel[t], -1);
                    int delay = Interlocked.Exchange(ref pendingDelay[t], -1);
                    int pslot = Interlocked.Exchange(ref pendingParam[t], -1);
                    int pval = Interlocked.Exchange(ref pendingValue[t], -1);
                    if (pslot > 0 && pval >= 0 && playing && pslot <= Slots && bound[pslot - 1] && ne < events.Length - 1)
                        events[ne++] = new PlugNative.Event { type = PlugNative.EvParam, param_id = slotId[pslot - 1], value = ToValue(pslot - 1, pval) };
                    int frame = 0;
                    if (note >= 0)
                    {
                        if (delay > 0) frame = (int)((long)delay * samplesPerTick / 128);
                        carryNote[t] = note;
                        carryVel[t] = vel;
                        carryFrames[t] = frame;
                    }
                    if (carryNote[t] < 0) continue;
                    if (carryFrames[t] >= n)
                    {
                        carryFrames[t] -= n;
                        continue;
                    }
                    int f = carryFrames[t];
                    int nt = carryNote[t];
                    int nv = carryVel[t];
                    carryNote[t] = -1;
                    if (ne >= events.Length - 2) continue;
                    if (trackKey[t] >= 0)
                    {
                        events[ne++] = new PlugNative.Event { frame = (uint)f, type = PlugNative.EvNoteOff, channel = ch, key = trackKey[t], note_id = -1 };
                        trackKey[t] = -1;
                    }
                    if (nt == Note.Off) continue;
                    int semi = (nt & 15) - 1, key = (nt >> 4) * 12 + semi;
                    if (semi < 0 || semi > 11 || key < 0 || key > 127) continue;
                    events[ne++] = new PlugNative.Event { frame = (uint)f, type = PlugNative.EvNoteOn, channel = ch, key = key, note_id = -1, value = (nv > 0 ? nv : 100) / 127.0 };
                    trackKey[t] = key;
                }
                lock (midiLock)
                {
                    for (int i = 0; i < midiCount && ne < events.Length - 1; i++) events[ne++] = midiRing[i];
                    midiCount = 0;
                }
                for (int i = 0; i < Slots && ne < events.Length - 1; i++)
                {
                    int v = Interlocked.Exchange(ref pending[i], -1);
                    if (v >= 0 && bound[i]) events[ne++] = new PlugNative.Event { type = PlugNative.EvParam, param_id = slotId[i], value = ToValue(i, v) };
                }
                SortByFrame(events, ne);

                bool inputActive = (input != null && HasSignal(input, n)) || (side != null && HasSignal(side, n));
                int limit = tailFrames + latency + 4096;
                if (ne > 0 || inputActive) quiet = 0;
                else if (quiet > limit)
                {
                    Array.Clear(output, 0, n);
                    if (playing) beatPos += n * bpm / (60.0 * rate);
                    return false;
                }

                bool any = false;
                int ei = 0;
                var target = midiTarget;
                for (int off = 0; off < n; off += Chunk)
                {
                    int c = Math.Min(Chunk, n - off);
                    int ck = 0;
                    while (ei < ne && events[ei].frame < (uint)(off + c) && ck < chunkEvents.Length)
                    {
                        var e = events[ei++];
                        e.frame = e.frame >= (uint)off ? e.frame - (uint)off : 0;
                        chunkEvents[ck++] = e;
                    }
                    bool haveIn = input != null;
                    bool haveSide = side != null && auxIn > 0;
                    if (haveIn)
                        for (int i = 0; i < c; i++)
                        {
                            inL[i] = input[off + i].L * (1f / 32768f);
                            inR[i] = input[off + i].R * (1f / 32768f);
                        }
                    if (haveSide)
                        for (int i = 0; i < c; i++)
                        {
                            auxL[i] = side[off + i].L * (1f / 32768f);
                            auxR[i] = side[off + i].R * (1f / 32768f);
                        }
                    int rc = PlugNative.Process(plugin, haveIn ? inL : null, haveIn ? inR : null, haveSide ? auxL : null, haveSide ? auxR : null, outL, outR, c,
                                                chunkEvents, ck, outEvents, outEvents.Length, out int outCount, bpm, playing ? 1 : 0, playing ? beatPos : -1);
                    if (rc < 0)
                    {
                        for (int i = 0; i < c; i++) output[off + i] = default;
                    }
                    else
                    {
                        for (int i = 0; i < c; i++)
                        {
                            float l = outL[i] * 32768f, r = outR[i] * 32768f;
                            output[off + i].L = l;
                            output[off + i].R = r;
                            if (l != 0 || r != 0) any = true;
                        }
                        if (outCount > 0 && DebugMidi) Console.Error.WriteLine($"[neoplug] {outCount} events out, target {(target == null ? "none" : target.Name)} ({midiTargetName})");
                        if (target != null && outCount > 0)
                            for (int k = 0; k < outCount; k++)
                            {
                                var o = outEvents[k];
                                if (o.type == PlugNative.EvNoteOn) target.SendMIDINote(o.channel, o.key, Math.Clamp((int)Math.Round(o.value * 127), 1, 127));
                                else if (o.type == PlugNative.EvNoteOff) target.SendMIDINote(o.channel, o.key, 0);
                            }
                    }
                    if (playing) beatPos += c * bpm / (60.0 * rate);
                }
                if (any) quiet = 0; else quiet += n;
                return any || quiet <= limit;
            }
            finally
            {
                Monitor.Exit(Gate);
            }
        }
    }
}
