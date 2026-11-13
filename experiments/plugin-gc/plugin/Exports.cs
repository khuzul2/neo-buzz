// Experiment: C-ABI exports of Neo Poly and Neo Drive. Process, parameter and note calls never allocate;
// everything is created in np_create (host's init thread). The ABI is declared in ../host/np_abi.h.
using System;
using System.Runtime.InteropServices;
using NeoBuzz.Dsp;

namespace NeoPluginGc
{
    internal sealed class Inst
    {
        public int Kind;                         // 0 = Poly, 1 = Drive
        public ParamValues Values;
        public NeoBuzz.Poly.PolyEngine Poly;
        public NeoBuzz.Drive.DriveEngine Drive;
        public readonly float[] L = new float[256], R = new float[256];
    }

    public static unsafe class Exports
    {
        static readonly Inst[] Insts = new Inst[64];
        static int count;

        [UnmanagedCallersOnly(EntryPoint = "np_create")]
        public static int Create(int kind, float sampleRate)
        {
            var inst = new Inst { Kind = kind };
            if (kind == 0)
            {
                inst.Values = new ParamValues(NeoBuzz.Poly.Params.Table);
                inst.Poly = new NeoBuzz.Poly.PolyEngine(inst.Values);
                inst.Poly.SetSampleRate(sampleRate);
                inst.Values.Set(NeoBuzz.Poly.P.Unison, 3);
            }
            else
            {
                inst.Values = new ParamValues(NeoBuzz.Drive.Params.Table);
                inst.Drive = new NeoBuzz.Drive.DriveEngine(inst.Values);
                inst.Drive.SetSampleRate(sampleRate);
            }
            lock (Insts)
            {
                if (count >= Insts.Length) return -1;
                Insts[count] = inst;
                return count++;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "np_param")]
        public static void Param(int h, int index, int raw) => Insts[h].Values.Set(index, raw);

        [UnmanagedCallersOnly(EntryPoint = "np_note_on")]
        public static void NoteOn(int h, int note, float velocity) => Insts[h].Poly?.NoteOn(note, note, velocity);

        [UnmanagedCallersOnly(EntryPoint = "np_note_off")]
        public static void NoteOff(int h, int note) => Insts[h].Poly?.NoteOff(note);

        /// <summary>Renders n frames (n &lt;= 256). Poly ignores the inputs; Drive reads them.</summary>
        [UnmanagedCallersOnly(EntryPoint = "np_process")]
        public static void Process(int h, float* inL, float* inR, float* outL, float* outR, int n)
        {
            var s = Insts[h];
            var l = s.L; var r = s.R;
            if (s.Kind == 0)
            {
                s.Poly.Process(l, r, n);
            }
            else
            {
                for (int i = 0; i < n; i++) { l[i] = inL[i]; r[i] = inR[i]; }
                s.Drive.Process(l, r, n);
            }
            for (int i = 0; i < n; i++) { outL[i] = l[i]; outR[i] = r[i]; }
        }

        // ---- GC observation (all allocation-free) ----
        [UnmanagedCallersOnly(EntryPoint = "np_gc_count")]
        public static int GcCount() => GC.CollectionCount(0);          // gen0 count includes every higher-generation GC

        [UnmanagedCallersOnly(EntryPoint = "np_gc_count_gen")]
        public static int GcCountGen(int gen) => GC.CollectionCount(gen);

        [UnmanagedCallersOnly(EntryPoint = "np_gc_pause_ns")]
        public static long GcPauseNs() => GC.GetTotalPauseDuration().Ticks * 100;

        [UnmanagedCallersOnly(EntryPoint = "np_gc_max_pause_ns")]
        public static long GcMaxPauseNs() => LoadGen.MaxPauseNs;

        [UnmanagedCallersOnly(EntryPoint = "np_gc_max_pause_reset")]
        public static void GcMaxPauseReset() => LoadGen.ResetMaxPause();

        [UnmanagedCallersOnly(EntryPoint = "np_alloc_bytes")]
        public static long AllocBytes() => GC.GetTotalAllocatedBytes(false);

        [UnmanagedCallersOnly(EntryPoint = "np_heap_bytes")]
        public static long HeapBytes() => GC.GetTotalMemory(false);

        // ---- load generator and GC settings (host control thread) ----
        /// <summary>mode 0 none, 1 = 1 MB/s, 2 = 20 MB/s, 3 = 20 MB/s plus a 256 MB live heap churning in gen2.</summary>
        [UnmanagedCallersOnly(EntryPoint = "np_load_start")]
        public static void LoadStart(int mode) => LoadGen.Start(mode);

        [UnmanagedCallersOnly(EntryPoint = "np_load_stop")]
        public static void LoadStop() => LoadGen.Stop();

        [UnmanagedCallersOnly(EntryPoint = "np_set_latency")]
        public static void SetLatency(int sustained) =>
            System.Runtime.GCSettings.LatencyMode = sustained != 0 ? System.Runtime.GCLatencyMode.SustainedLowLatency : System.Runtime.GCLatencyMode.Interactive;

        /// <summary>Writes the effective GC configuration as text, returns its length.</summary>
        [UnmanagedCallersOnly(EntryPoint = "np_gc_config")]
        public static int GcConfig(byte* buf, int cap) => LoadGen.Describe(buf, cap);
    }
}
