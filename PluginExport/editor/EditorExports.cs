// The editor library. For this spike it does not draw: it runs the work a Neo editor does while it is open (a 60 Hz loop
// that reads the shared parameter values and meters, formats every parameter's text, keeps a little recent state alive) plus,
// by NEO_EXPORT_EDITOR_LOAD, the same managed load generator as experiments/plugin-gc (1 MB/s, 20 MB/s, or a 256 MB heap).
// Everything it knows about the plug-in comes through the Shared struct.
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using NeoBuzz.Filter;

namespace NeoExport
{
    public static unsafe class EditorExports
    {
        static Thread ui;
        static volatile bool run;
        static Shared* shared;

        /// <summary>Opens the editor on a shared block. load: 0 none, 1 = 1 MB/s, 2 = 20 MB/s, 3 = 20 MB/s + 256 MB heap. edits: 1 posts a parameter edit once a second.</summary>
        [UnmanagedCallersOnly(EntryPoint = "ned_open")]
        public static int Open(Shared* s, int load, int edits)
        {
            if (s == null || s->Magic_ != Shared.Magic) return 0;
            Close();
            shared = s;
            LoadGen.Publish = s;
            Volatile.Write(ref s->EdAlive, 1);
            LoadGen.Start(load);
            run = true;
            ui = new Thread(() => UiLoop(s, edits)) { IsBackground = true, Name = "editor-ui" };
            ui.Start();
            return 1;
        }

        [UnmanagedCallersOnly(EntryPoint = "ned_close")]
        public static void CloseExport() => Close();

        static void Close()
        {
            run = false;
            var t = ui; ui = null;
            t?.Join();
            LoadGen.Stop();
            LoadGen.Publish = null;
            if (shared != null) Volatile.Write(ref shared->EdAlive, 0);
            shared = null;
        }

        [UnmanagedCallersOnly(EntryPoint = "ned_gc_config")]
        public static int GcConfig(byte* buf, int cap) => LoadGen.Describe(buf, cap);

        static void UiLoop(Shared* s, int edits)
        {
            var table = Params.Table;
            var texts = new string[table.Length];        // what the editor shows: rebuilt when a value changes
            var seen = new int[table.Length];
            for (int i = 0; i < seen.Length; i++) seen[i] = int.MinValue;
            string meter = "";
            long frame = 0, nextEdit = Environment.TickCount64 + 1000;
            int sweep = 0;
            while (run && Volatile.Read(ref s->Closing) == 0)
            {
                // read what the audio side published
                for (int i = 0; i < table.Length; i++)
                {
                    int v = Volatile.Read(ref s->Param[i]);
                    if (v != seen[i]) { seen[i] = v; texts[i] = table[i].Name + ": " + table[i].Describe(v); }
                }
                float inP = Volatile.Read(ref s->InPeak), outP = Volatile.Read(ref s->OutPeak);
                meter = string.Format(CultureInfo.InvariantCulture, "in {0:0.0} dB   out {1:0.0} dB", 20 * Math.Log10(Math.Max(inP, 1e-6f)), 20 * Math.Log10(Math.Max(outP, 1e-6f)));
                Volatile.Write(ref s->EdFrames, ++frame);
                if (edits != 0 && Environment.TickCount64 >= nextEdit)
                {
                    nextEdit += 1000;
                    // a knob drag: Output between -0.1 and +0.1 dB around its default (small, audible only to the null test)
                    sweep = (sweep + 1) & 3;
                    if (Shared.Post(s, P.Output, 240 + (sweep - 1) * 1)) Interlocked.Increment(ref s->EdEditsPosted);
                }
                Thread.Sleep(16);
            }
            GC.KeepAlive(texts); GC.KeepAlive(meter);
        }
    }
}
