#nullable disable
// Neo Plug Spike (H0, docs/specs/plugin-host.md §6.1): a throwaway managed machine that hosts one
// VST3 or CLAP plugin in-process through the neoplug C ABI. Two builds from this file: the
// generator ("Neo Plug Spike", 8 tracks of Note and Velocity, plus MIDI) and, with FX defined, the
// effect ("Neo Plug Spike FX"). Not the H1 design: no loader instruments, no runtime parameter
// names, no main-thread pump. Which plugin a new machine loads comes from the environment:
//
//   NEOPLUG_SPIKE_PLUGINS     "path|id;path|id;..." for generators, taken in turn by new machines
//   NEOPLUG_SPIKE_FX_PLUGINS  the same for effects
//   NEOPLUG_SPIKE_TWEAK=1     on a new machine, move the first automatable parameter (as the probe does)
//   NEOPLUG_LIB               the neoplug library (default: beside this assembly)
//
// A song keeps the plugin's identity and state in MachineState; reopening restores it. A plugin
// that isn't there loads as "missing" and saves its state back unchanged.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace NeoBuzz.PlugSpike
{
    /// <summary>What a song saves for the machine (XML, through ReBuzz's MachineState).</summary>
    public class PlugState
    {
        public int Version { get; set; } = 1;
        public string Path { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Vendor { get; set; }
        public string Format { get; set; }
        /// <summary>The plugin's own state in neoplug's envelope (base64 in the song).</summary>
        public byte[] Blob { get; set; }
        /// <summary>The parameter Slot 1 drives, and the one the tweak moved (logged on restore).</summary>
        public uint SlotId { get; set; }
        public uint TweakId { get; set; } = uint.MaxValue;
    }

#if FX
    [MachineDecl(Name = "Neo Plug Spike FX", ShortName = "PlugFX", Author = "neo-buzz (H0 spike)", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public class NeoPlugSpikeMachine : IBuzzMachine, IDisposable
#else
    [MachineDecl(Name = "Neo Plug Spike", ShortName = "Plug", Author = "neo-buzz (H0 spike)", MaxTracks = 8, InputCount = 0, OutputCount = 1)]
    public class NeoPlugSpikeMachine : IBuzzMachine, IDisposable
#endif
    {
        const int MaxBlock = 1024;
        static int instances;
        readonly IBuzzMachineHost host;
        readonly object gate = new object();       // held by the main thread while it swaps the plugin
        IntPtr plugin;
        PlugState state = new PlugState();
        bool instrument, missing;
        int rate;
        readonly float[] inL = new float[MaxBlock], inR = new float[MaxBlock], outL = new float[MaxBlock], outR = new float[MaxBlock];
        readonly Native.Event[] events = new Native.Event[512];
        readonly ConcurrentQueue<Native.Event> midi = new ConcurrentQueue<Native.Event>();
        readonly int[] trackKey = new int[8];
        readonly int[] pendingNote = new int[8];
        readonly int[] pendingVelocity = new int[8];
        volatile bool stopAll;
        int slotValue = -1;
        double slotMin, slotMax = 1;

        public NeoPlugSpikeMachine(IBuzzMachineHost host)
        {
            this.host = host;
            Array.Fill(trackKey, -1);
            Array.Fill(pendingNote, -1);
            Array.Fill(pendingVelocity, -1);
            int n = Interlocked.Increment(ref instances) - 1;
#if FX
            string list = Environment.GetEnvironmentVariable("NEOPLUG_SPIKE_FX_PLUGINS");
#else
            string list = Environment.GetEnvironmentVariable("NEOPLUG_SPIKE_PLUGINS");
#endif
            if (string.IsNullOrWhiteSpace(list))
            {
                Log("no plugin configured (NEOPLUG_SPIKE_PLUGINS / NEOPLUG_SPIKE_FX_PLUGINS)");
                return;
            }
            var specs = list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var spec = specs[n % specs.Length].Split('|');
            state.Path = spec[0];
            state.Id = spec.Length > 1 ? spec[1] : "";
            lock (gate)
            {
                if (!Open(state.Path, state.Id)) return;
                if (Environment.GetEnvironmentVariable("NEOPLUG_SPIKE_TWEAK") == "1") Tweak();
            }
        }

        static void Log(string message) => Console.Error.WriteLine("[neoplug-spike] " + message);

        int Rate
        {
            get
            {
                int sr = 0;
                try { sr = host?.MasterInfo?.SamplesPerSec ?? 0; } catch { }
                return sr > 0 ? sr : 44100;
            }
        }

        bool Open(string path, string id)
        {
            Close();
            var err = new byte[512];
            plugin = Native.np_open(path, string.IsNullOrEmpty(id) ? null : id, err, err.Length);
            if (plugin == IntPtr.Zero)
            {
                missing = true;
                Log($"plugin missing or failed to load: {path} [{id}]: {Native.Str(err)}");
                return false;
            }
            missing = false;
            Native.np_info(plugin, out var info);
            instrument = info.is_instrument != 0;
            state.Name = Native.Str(info.name);
            state.Vendor = Native.Str(info.vendor);
            state.Id = Native.Str(info.id);
            state.Format = info.format == 1 ? "vst3" : "clap";
            rate = Rate;
            if (Native.np_activate(plugin, rate, 256, 0, err, err.Length) != 0) Log($"activate failed: {Native.Str(err)}");
            if (state.SlotId == 0) state.SlotId = FirstUsableParam(out _);
            int count = Native.np_param_count(plugin);
            for (int i = 0; i < count; i++)
                if (Native.np_param_info_at(plugin, i, out var pi) == 0 && pi.id == state.SlotId)
                {
                    slotMin = pi.min;
                    slotMax = pi.max;
                }
            Log($"loaded {state.Format} \"{state.Name}\" by {state.Vendor} at {rate} Hz ({Native.np_param_count(plugin)} parameters, latency {Native.np_latency(plugin)})");
            return true;
        }

        /// <summary>ReBuzz disposes a managed machine when it is deleted or its song closes.</summary>
        public void Dispose()
        {
            lock (gate) Close();
        }

        void Close()
        {
            if (plugin == IntPtr.Zero) return;
            Native.np_deactivate(plugin);
            Native.np_close(plugin);
            plugin = IntPtr.Zero;
        }

        uint FirstUsableParam(out Native.ParamInfo found)
        {
            found = default;
            int n = Native.np_param_count(plugin);
            for (int i = 0; i < n; i++)
            {
                if (Native.np_param_info_at(plugin, i, out var pi) != 0) continue;
                const int bad = 4 | 8 | 16;   // read-only, hidden, bypass
                if ((pi.flags & 1) != 0 && (pi.flags & bad) == 0)
                {
                    found = pi;
                    return pi.id;
                }
            }
            return 0;
        }

        /// <summary>Moves the first automatable parameter, as neoplug-probe does, so the state round trip means something.</summary>
        void Tweak()
        {
            uint id = FirstUsableParam(out var pi);
            if (pi.name == null) return;
            double span = pi.max - pi.min, target = pi.min + span * 0.3;
            if (pi.steps > 0)
            {
                double step = span / pi.steps;
                target = pi.min + Math.Round(0.3 * pi.steps) * step;
                if (Math.Abs(target - pi.def) < step / 2) target = pi.def + step <= pi.max ? pi.def + step : pi.def - step;
            }
            else if (Math.Abs(target - pi.def) < span * 0.05) target = pi.min + span * 0.7;
            Native.np_param_set(plugin, id, target);
            state.TweakId = id;
            Log($"tweaked \"{Native.Str(pi.name)}\" to {ParamText(id, target)} ({target:R})");
        }

        string ParamText(uint id, double v)
        {
            var buf = new byte[128];
            return Native.np_param_text(plugin, id, v, buf, buf.Length) >= 0 ? Native.Str(buf) : v.ToString("G6");
        }

        // --- Parameters -------------------------------------------------------------------------

        /// <summary>
        /// One automatable slot, 0..65534 over the plugin parameter's range. On song load ReBuzz
        /// sends every global parameter its saved value, stateless ones too (their default or
        /// minimum), which would override the plugin's restored state. So the spike takes slot
        /// values only while the song plays (patterns). H1: slots must mirror the plugin's current
        /// value, so that re-sending them is harmless.
        /// </summary>
        [ParameterDecl(Name = "Slot 1", IsStateless = true, MinValue = 0, MaxValue = 65534, Description = "The plugin's first automatable parameter, 0..65534 over its range")]
        public int Slot1
        {
            get => 65535;
            set
            {
                bool playing = false;
                try { playing = host?.Machine?.Graph?.Buzz?.Playing ?? false; } catch { }
                if (playing && value >= 0 && value <= 65534) Volatile.Write(ref slotValue, value);
                if (Debug) Log($"Slot 1 set to {value} (playing: {playing})");
            }
        }

        static readonly bool Debug = Environment.GetEnvironmentVariable("NEOPLUG_SPIKE_DEBUG") == "1";

#if !FX
        [ParameterDecl(Name = "Note", IsStateless = true, Description = "Note (off releases the track's note)")]
        public void SetNote(Note value, int track)
        {
            if (track >= 0 && track < 8) Volatile.Write(ref pendingNote[track], value.Value);
        }

        [ParameterDecl(Name = "Velocity", IsStateless = true, MinValue = 1, MaxValue = 127, DefValue = 100, Description = "Velocity of the note on the same row (100 when empty)")]
        public void SetVelocity(int value, int track)
        {
            if (track >= 0 && track < 8) Volatile.Write(ref pendingVelocity[track], value);
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            if (!Monitor.TryEnter(gate)) return false;
            try
            {
                return Render(null, output, n);
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }
#else
        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (!Monitor.TryEnter(gate))
            {
                if (input != output) Array.Copy(input, output, n);
                return true;
            }
            try
            {
                return Render((mode & WorkModes.WM_READ) != 0 ? input : null, output, n);
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }
#endif

        public void MidiNote(int channel, int value, int velocity)
        {
            midi.Enqueue(new Native.Event { type = velocity > 0 ? 1 : 2, channel = channel & 15, key = value & 127, note_id = -1, value = velocity / 127.0 });
        }

        public void Stop() => stopAll = true;

        bool Render(Sample[] input, Sample[] output, int n)
        {
            if (plugin == IntPtr.Zero || missing) return false;
            n = Math.Min(n, MaxBlock);
            int ne = 0;
            if (stopAll)
            {
                stopAll = false;
                for (int t = 0; t < 8; t++)
                    if (trackKey[t] >= 0)
                    {
                        events[ne++] = new Native.Event { type = 2, key = trackKey[t], note_id = -1 };
                        trackKey[t] = -1;
                    }
            }
            for (int t = 0; t < 8; t++)
            {
                int note = Interlocked.Exchange(ref pendingNote[t], -1);
                int vel = Interlocked.Exchange(ref pendingVelocity[t], -1);
                if (note < 0) continue;
                if (trackKey[t] >= 0) events[ne++] = new Native.Event { type = 2, key = trackKey[t], note_id = -1 };
                trackKey[t] = -1;
                if (note == Note.Off) continue;
                int semi = (note & 15) - 1, key = (note >> 4) * 12 + semi;
                if (semi < 0 || semi > 11 || key < 0 || key > 127) continue;
                events[ne++] = new Native.Event { type = 1, key = key, note_id = -1, value = (vel > 0 ? vel : 100) / 127.0 };
                trackKey[t] = key;
            }
            while (ne < events.Length && midi.TryDequeue(out var m)) events[ne++] = m;
            int slot = Interlocked.Exchange(ref slotValue, -1);
            if (slot >= 0 && state.SlotId != 0)
                events[ne++] = new Native.Event { type = 3, param_id = state.SlotId, value = SlotToValue(slot) };
            if (input != null)
                for (int i = 0; i < n; i++)
                {
                    inL[i] = input[i].L / 32768f;
                    inR[i] = input[i].R / 32768f;
                }
            double bpm = 120;
            try { bpm = host?.MasterInfo?.BeatsPerMin ?? 120; } catch { }
            int rc = Native.Process(plugin, input != null ? inL : null, input != null ? inR : null, outL, outR, n, events, ne, bpm, 1);
            if (rc < 0) return false;
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                float l = outL[i] * 32768f, r = outR[i] * 32768f;
                output[i].L = l;
                output[i].R = r;
                if (l != 0 || r != 0) any = true;
            }
            return any;
        }

        double SlotToValue(int v) => slotMin + (slotMax - slotMin) * v / 65534.0;

        public int GetLatency() => plugin == IntPtr.Zero ? 0 : Math.Max(0, Native.np_latency(plugin));

        public string DescribeValue(IParameter parameter, int value)
        {
            if (parameter?.Name != "Slot 1" || plugin == IntPtr.Zero || state.SlotId == 0) return null;
            return ParamText(state.SlotId, SlotToValue(value));
        }

        // --- State ------------------------------------------------------------------------------

        public PlugState MachineState
        {
            get
            {
                lock (gate)
                {
                    if (plugin == IntPtr.Zero) return state;   // missing: what we loaded, unchanged
                    Native.np_idle(plugin);
                    int size = Native.np_state_save(plugin, null, 0);
                    if (size > 0)
                    {
                        var blob = new byte[size];
                        Native.np_state_save(plugin, blob, size);
                        state.Blob = blob;
                    }
                    return state;
                }
            }
            set
            {
                if (value == null || string.IsNullOrEmpty(value.Path)) return;
                lock (gate)
                {
                    bool same = plugin != IntPtr.Zero && value.Path == state.Path && value.Id == state.Id;
                    var keep = value;
                    if (!same)
                    {
                        state = keep;
                        if (!Open(keep.Path, keep.Id))
                        {
                            state = keep;   // keep everything to save it back unchanged
                            return;
                        }
                    }
                    state = keep;
                    if (keep.Blob != null && keep.Blob.Length > 0)
                    {
                        var err = new byte[512];
                        Native.np_deactivate(plugin);
                        int rc = Native.np_state_load(plugin, keep.Blob, keep.Blob.Length, err, err.Length);
                        Native.np_activate(plugin, rate > 0 ? rate : Rate, 256, 0, err, err.Length);
                        Native.np_idle(plugin);
                        double now = keep.TweakId != uint.MaxValue ? Native.np_param_get(plugin, keep.TweakId) : 0;
                        string restored = keep.TweakId != uint.MaxValue ? $"; \"{ParamName(keep.TweakId)}\" is {ParamText(keep.TweakId, now)} ({now:R})" : "";
                        Log(rc == 0 ? $"restored \"{state.Name}\" from {keep.Blob.Length} bytes{restored}" : $"state load failed: {Native.Str(err)}");
                    }
                }
            }
        }

        string ParamName(uint id)
        {
            int count = Native.np_param_count(plugin);
            for (int i = 0; i < count; i++)
                if (Native.np_param_info_at(plugin, i, out var pi) == 0 && pi.id == id) return Native.Str(pi.name);
            return id.ToString();
        }
    }

    static class Native
    {
        const string Lib = "neoplug";

        static Native()
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, assembly, path) =>
            {
                if (name != Lib) return IntPtr.Zero;
                string env = Environment.GetEnvironmentVariable("NEOPLUG_LIB");
                if (!string.IsNullOrEmpty(env) && NativeLibrary.TryLoad(env, out var h)) return h;
                string dir = System.IO.Path.GetDirectoryName(assembly.Location) ?? "";
                string file = OperatingSystem.IsWindows() ? "neoplug.dll" : OperatingSystem.IsMacOS() ? "libneoplug.dylib" : "libneoplug.so";
                return NativeLibrary.TryLoad(System.IO.Path.Combine(dir, file), out h) ? h : IntPtr.Zero;
            });
        }

        public static string Str(byte[] b)
        {
            int n = Array.IndexOf(b, (byte)0);
            return System.Text.Encoding.UTF8.GetString(b, 0, n < 0 ? b.Length : n);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ClassInfo
        {
            public int format, is_instrument;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] id;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] name;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] vendor;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] version;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 192)] public byte[] category;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ParamInfo
        {
            public uint id;
            public int flags, steps;
            public double min, max, def;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] name;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] units;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] module;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Event
        {
            public uint frame;
            public int type, channel, key, note_id;
            public uint param_id;
            public double value;
        }

        [DllImport(Lib)] public static extern IntPtr np_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, byte[] err, int errlen);
        [DllImport(Lib)] public static extern void np_close(IntPtr p);
        [DllImport(Lib)] public static extern int np_info(IntPtr p, out ClassInfo info);
        [DllImport(Lib)] public static extern int np_activate(IntPtr p, double rate, int maxFrames, int offline, byte[] err, int errlen);
        [DllImport(Lib)] public static extern void np_deactivate(IntPtr p);
        [DllImport(Lib)] public static extern int np_param_count(IntPtr p);
        [DllImport(Lib)] public static extern int np_param_info_at(IntPtr p, int index, out ParamInfo info);
        [DllImport(Lib)] public static extern double np_param_get(IntPtr p, uint id);
        [DllImport(Lib)] public static extern int np_param_text(IntPtr p, uint id, double value, byte[] buf, int len);
        [DllImport(Lib)] public static extern int np_param_set(IntPtr p, uint id, double value);
        [DllImport(Lib)] public static extern int np_state_save(IntPtr p, byte[] buf, int cap);
        [DllImport(Lib)] public static extern int np_state_load(IntPtr p, byte[] buf, int size, byte[] err, int errlen);
        [DllImport(Lib)] public static extern int np_latency(IntPtr p);
        [DllImport(Lib)] public static extern void np_idle(IntPtr p);
        [DllImport(Lib)] static extern unsafe int np_process(IntPtr p, float** input, int inCh, float** output, int outCh, int frames, Event* events, int nevents, double tempo, int playing);

        /// <summary>The audio-thread call: pinned buffers, no allocation.</summary>
        public static unsafe int Process(IntPtr p, float[] inL, float[] inR, float[] outL, float[] outR, int frames, Event[] events, int nevents, double tempo, int playing)
        {
            fixed (float* il = inL, ir = inR, ol = outL, or = outR)
            fixed (Event* ev = events)
            {
                float** ins = stackalloc float*[2];
                float** outs = stackalloc float*[2];
                ins[0] = il;
                ins[1] = ir;
                outs[0] = ol;
                outs[1] = or;
                return np_process(p, inL != null ? ins : null, inL != null ? 2 : 0, outs, 2, frames, ev, nevents, tempo, playing);
            }
        }
    }
}
