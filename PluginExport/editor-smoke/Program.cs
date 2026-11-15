using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using NeoBuzz.Dsp;

namespace NeoExport
{
    static unsafe class Smoke
    {
        static int bad;
        static void Check(bool ok, string what) { if (!ok) { bad++; Console.WriteLine("FAIL " + what); } }

        static int Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "neo-editor-smoke");
            Directory.CreateDirectory(outDir);
            foreach (var id in args.Length > 1 ? args[1..] : IdsOf())
            {
                var sh = (Shared*)NativeMemory.AllocZeroed((nuint)sizeof(Shared));
                sh->Magic_ = Shared.Magic; sh->Size = sizeof(Shared);
                ParamDef[] table = TableOf(id);
                sh->ParamCount = table.Length;
                for (int i = 0; i < table.Length; i++) sh->Param[i] = table[i].Default;
                var e = EditorInstance.Create(sh, id, 1.0);
                Check(e != null, id + ": editor created");
                if (e == null) continue;
                for (int i = 0; i < 5; i++) e.Tick();
                var win = e.TestWindow;
                int errors = 0;
                foreach (var v in win.GetVisualDescendants()) if (v is TextBlock tb && tb.Text != null && tb.Text.StartsWith("Editor error")) { errors++; Console.WriteLine(id + ": " + tb.Text); }
                Check(errors == 0, id + ": no Editor error panel");
                e.GetSize(out uint w, out uint h);
                Check(w > 100 && h > 60, $"{id}: size {w}x{h}");
                using (var bmp = win.CaptureRenderedFrame()) { Check(bmp != null, id + ": frame"); bmp?.Save(Path.Combine(outDir, id.Replace("org.neobuzz.", "") + ".png")); }
                Check(sh->EdFrames >= 5, id + ": frames counted");

                // drag a knob up: gesture begin, values, gesture end in the ring
                if (e.Hotspot(out int x, out int y))
                {
                    e.MouseDown(x, y, 1); e.Tick();
                    for (int k = 1; k <= 10; k++) { e.MouseMove(x, y - 4 * k); e.Tick(); }
                    e.MouseUp(x, y - 40, 1); e.Tick();
                    int begin = 0, val = 0, end = 0, firstIndex = -1; bool order = true; bool open = false;
                    while (Shared.Take(sh, out int kind, out int index, out int raw))
                    {
                        if (kind == Shared.EditBegin) { begin++; open = true; if (firstIndex < 0) firstIndex = index; }
                        else if (kind == Shared.EditValue) { val++; if (!open) order = false; }
                        else if (kind == Shared.EditEnd) { end++; open = false; }
                    }
                    Check(begin >= 1 && begin == end && val >= 2 && order, $"{id}: drag -> {begin} begin, {val} values, {end} end, ordered={order}");
                    Console.WriteLine($"{id}: {w}x{h}, drag ok (param {firstIndex}, {val} values)");
                }
                else Check(false, id + ": a knob to drag");

                // host automation reaches the editor: change the first parameter and a tick later the machine sees it
                int p0 = 0; int nv = table[p0].Max;
                sh->Param[p0] = nv; sh->ParamSeq++;
                e.Tick();
                Check(e.TestMachineRaw(p0) == nv, id + ": host value reaches the editor");
                e.Destroy();
                NativeMemory.Free(sh);
            }
            Console.WriteLine(bad == 0 ? "ALL OK" : bad + " FAILED");
            return bad == 0 ? 0 : 1;
        }

        static string[] IdsOf() => EditorRegistry.Ids;
        static ParamDef[] TableOf(string id) { EditorRegistry.TryMake(id, out var ed, out _); return ed.Table; }
    }
}
