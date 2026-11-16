#nullable disable
// neoplug's C ABI (PluginHost/neoplug/neoplug.h) for the managed side. Everything but PlugNative.Process is for the
// main thread (PlugMain); Process is the audio thread's call: pinned buffers, no allocation.

using System;
using System.Runtime.InteropServices;

namespace NeoBuzz.Plug
{
    static class PlugNative
    {
        public const int Abi = 3;
        const string Lib = "neoplug";

        static PlugNative()
        {
            NativeLibrary.SetDllImportResolver(typeof(PlugNative).Assembly, (name, assembly, path) =>
            {
                if (name != Lib) return IntPtr.Zero;
                foreach (var candidate in PlugPaths.NativeLibraryCandidates(assembly.Location))
                    if (NativeLibrary.TryLoad(candidate, out var h)) return h;
                return IntPtr.Zero;
            });
        }

        /// <summary>False when the library isn't there or is another ABI: the machines then load as "plug-in host unavailable".</summary>
        public static bool Available(out string why)
        {
            try
            {
                int abi = np_abi();
                if (abi != Abi)
                {
                    why = $"neoplug is ABI {abi}, this machine needs {Abi}";
                    return false;
                }
                why = null;
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                why = "the plug-in library (neoplug) isn't installed beside the machine";
                return false;
            }
        }

        public static string Str(byte[] b)
        {
            if (b == null) return "";
            int n = Array.IndexOf(b, (byte)0);
            return System.Text.Encoding.UTF8.GetString(b, 0, n < 0 ? b.Length : n);
        }

        public const int FormatVst3 = 1, FormatClap = 2;
        public const int ParamAutomatable = 1, ParamStepped = 2, ParamReadOnly = 4, ParamHidden = 8, ParamBypass = 16;
        public const int EvNoteOn = 1, EvNoteOff = 2, EvParam = 3, EvCc = 4, EvPitchBend = 5, EvChannelPressure = 6, EvPolyPressure = 7, EvNoteExpr = 8, EvAllNotesOff = 9;
        public const int PollLatency = 1, PollParams = 2, PollRestart = 4, PollDirty = 8, PollFlush = 16;

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

        [StructLayout(LayoutKind.Sequential)]
        unsafe struct ProcessIo
        {
            public float** inp; public int in_ch;
            public float** aux; public int aux_ch;
            public float** outp; public int out_ch;
            public int frames;
            public Event* events; public int nevents;
            public Event* out_events; public int out_cap; public int out_count;
            public double tempo; public int playing;
            public double beat_pos;
            public int tsig_num, tsig_den;
        }

        [DllImport(Lib)] public static extern int np_abi();
        [DllImport(Lib)] public static extern IntPtr np_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, byte[] err, int errlen);
        [DllImport(Lib)] public static extern void np_close(IntPtr p);
        [DllImport(Lib)] public static extern int np_info(IntPtr p, out ClassInfo info);
        [DllImport(Lib)] public static extern int np_activate(IntPtr p, double rate, int maxFrames, int offline, byte[] err, int errlen);
        [DllImport(Lib)] public static extern void np_deactivate(IntPtr p);
        [DllImport(Lib)] public static extern int np_channels(IntPtr p, int input);
        [DllImport(Lib)] public static extern int np_aux_channels(IntPtr p);
        [DllImport(Lib)] public static extern int np_param_count(IntPtr p);
        [DllImport(Lib)] public static extern int np_param_info_at(IntPtr p, int index, out ParamInfo info);
        [DllImport(Lib)] public static extern double np_param_get(IntPtr p, uint id);
        [DllImport(Lib)] public static extern double np_param_plain(IntPtr p, uint id, double value);
        [DllImport(Lib)] public static extern int np_param_text(IntPtr p, uint id, double value, byte[] buf, int len);
        [DllImport(Lib)] public static extern int np_param_from_text(IntPtr p, uint id, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, out double value);
        [DllImport(Lib)] public static extern int np_param_set(IntPtr p, uint id, double value);
        [DllImport(Lib)] public static extern int np_state_save(IntPtr p, byte[] buf, int cap);
        [DllImport(Lib)] public static extern int np_state_load(IntPtr p, byte[] buf, int size, byte[] err, int errlen);
        [DllImport(Lib)] public static extern int np_latency(IntPtr p);
        [DllImport(Lib)] public static extern int np_tail(IntPtr p);
        [DllImport(Lib)] public static extern void np_idle(IntPtr p);
        [DllImport(Lib)] public static extern int np_poll(IntPtr p);
        [DllImport(Lib)] public static extern void np_flush(IntPtr p);
        [DllImport(Lib)] public static extern void np_reset(IntPtr p);
        // GUI (ABI 3)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ResizeCb(IntPtr user, int width, int height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ClosedCb(IntPtr user);
        [DllImport(Lib)] public static extern int np_gui_support(IntPtr p, [MarshalAs(UnmanagedType.LPUTF8Str)] string api);
        [DllImport(Lib)] public static extern int np_gui_attach(IntPtr p, IntPtr parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string api, out int w, out int h, byte[] err, int errlen);
        [DllImport(Lib)] public static extern void np_gui_detach(IntPtr p);
        [DllImport(Lib)] public static extern void np_gui_set_callbacks(IntPtr p, ResizeCb resize, ClosedCb closed, IntPtr user);
        [DllImport(Lib)] public static extern int np_gui_size(IntPtr p, out int w, out int h);
        [DllImport(Lib)] public static extern int np_gui_can_resize(IntPtr p);
        [DllImport(Lib)] public static extern int np_gui_set_size(IntPtr p, ref int w, ref int h);
        [DllImport(Lib)] public static extern int np_gui_set_scale(IntPtr p, double scale);
        [DllImport(Lib)] public static extern IntPtr np_platform_container(IntPtr parent, int w, int h);
        [DllImport(Lib)] public static extern void np_platform_resize(IntPtr view, int w, int h);
        [DllImport(Lib)] public static extern IntPtr np_platform_window([MarshalAs(UnmanagedType.LPUTF8Str)] string title, int w, int h);
        [DllImport(Lib)] public static extern void np_platform_window_size(IntPtr content, int w, int h);
        [DllImport(Lib)] public static extern void np_platform_on_close(IntPtr content, ClosedCb cb, IntPtr user);
        [DllImport(Lib)] public static extern void np_platform_release(IntPtr handle);
        [DllImport(Lib)] public static extern int np_platform_snapshot(IntPtr view, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int rgb, byte[] stats, int len);
        // Outputs (ABI 3)
        [DllImport(Lib)] public static extern int np_out_count(IntPtr p);
        [DllImport(Lib)] public static extern int np_out_info(IntPtr p, int index, byte[] name, int len, out int channels);
        [DllImport(Lib)] public static extern void np_out_select(IntPtr p, int index);
        [DllImport(Lib)] static extern unsafe int np_process_ex(IntPtr p, ProcessIo* io);

        /// <summary>One block. The planar channel arrays are the caller's, already filled (inL/inR: the main input or null, auxL/auxR: the sidechain or null).</summary>
        public static unsafe int Process(IntPtr p, float[] inL, float[] inR, float[] auxL, float[] auxR, float[] outL, float[] outR, int frames,
                                         Event[] events, int nevents, Event[] outEvents, int outCap, out int outCount, double tempo, int playing, double beatPos)
        {
            fixed (float* il = inL, ir = inR, al = auxL, ar = auxR, ol = outL, or = outR)
            fixed (Event* ev = events, oe = outEvents)
            {
                float** ins = stackalloc float*[2];
                float** auxs = stackalloc float*[2];
                float** outs = stackalloc float*[2];
                ins[0] = il; ins[1] = ir;
                auxs[0] = al; auxs[1] = ar;
                outs[0] = ol; outs[1] = or;
                var io = new ProcessIo
                {
                    inp = inL != null ? ins : null, in_ch = inL != null ? 2 : 0,
                    aux = auxL != null ? auxs : null, aux_ch = auxL != null ? 2 : 0,
                    outp = outs, out_ch = 2, frames = frames,
                    events = ev, nevents = nevents,
                    out_events = oe, out_cap = outCap,
                    tempo = tempo, playing = playing, beat_pos = beatPos, tsig_num = 4, tsig_den = 4,
                };
                int rc = np_process_ex(p, &io);
                outCount = io.out_count;
                return rc;
            }
        }
    }
}
