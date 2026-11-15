// The editor library's C ABI (called by the DSP library, neo_dsp, on the host's main thread). An editor is an opaque handle.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NeoExport
{
    internal static unsafe class Exports
    {
        static EditorInstance last;   // for the test hotspot export

        static EditorInstance Get(IntPtr h) => h == IntPtr.Zero ? null : Unsafe.As<EditorInstance>(GCHandle.FromIntPtr(h).Target);

        [UnmanagedCallersOnly(EntryPoint = "ned_init", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void Init(byte* dirUtf8)
        {
            try { EditorInstance.SetDirectory(Marshal.PtrToStringUTF8((IntPtr)dirUtf8)); } catch (Exception) { }
        }

        [UnmanagedCallersOnly(EntryPoint = "ned_create", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static IntPtr Create(Shared* sh, byte* idUtf8, double scale)
        {
            try
            {
                var e = EditorInstance.Create(sh, Marshal.PtrToStringUTF8((IntPtr)idUtf8), scale);
                if (e == null) return IntPtr.Zero;
                last = e;
                return GCHandle.ToIntPtr(GCHandle.Alloc(e));
            }
            catch (Exception) { return IntPtr.Zero; }
        }

        [UnmanagedCallersOnly(EntryPoint = "ned_destroy", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void Destroy(IntPtr h)
        {
            try { var e = Get(h); if (e == last) last = null; e?.Destroy(); } catch (Exception) { }
            if (h != IntPtr.Zero) GCHandle.FromIntPtr(h).Free();
        }

        [UnmanagedCallersOnly(EntryPoint = "ned_tick", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void Tick(IntPtr h) { try { Get(h)?.Tick(); } catch (Exception) { } }

        [UnmanagedCallersOnly(EntryPoint = "ned_show", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void Show(IntPtr h) { try { Get(h)?.Show(); } catch (Exception) { } }

        [UnmanagedCallersOnly(EntryPoint = "ned_hide", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void Hide(IntPtr h) { try { Get(h)?.Hide(); } catch (Exception) { } }

        [UnmanagedCallersOnly(EntryPoint = "ned_get_size", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int GetSize(IntPtr h, uint* w, uint* hh)
        {
            var e = Get(h); if (e == null) return 0;
            e.GetSize(out *w, out *hh); return 1;
        }

        [UnmanagedCallersOnly(EntryPoint = "ned_set_size", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int SetSize(IntPtr h, uint w, uint hh) { try { return Get(h)?.Resize((int)w, (int)hh) == true ? 1 : 0; } catch (Exception) { return 0; } }

        [UnmanagedCallersOnly(EntryPoint = "ned_set_scale", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int SetScale(IntPtr h, double s) { try { return Get(h)?.SetScale(s) == true ? 1 : 0; } catch (Exception) { return 0; } }

        [UnmanagedCallersOnly(EntryPoint = "ned_set_parent", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int SetParent(IntPtr h, int api, void* handle) { try { return Get(h)?.SetParent(api, handle) == true ? 1 : 0; } catch (Exception) { return 0; } }

        [UnmanagedCallersOnly(EntryPoint = "ned_gc_config", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int GcConfig(byte* name, int value) => 0;   // the runtime's GC settings are fixed at build time (runtimeconfig)

        /// <summary>Test only: a left mouse event (0 down, 1 move, 2 up) in the newest editor's native view, sent through the window system (Windows: a posted message; macOS: an event to the view).</summary>
        [UnmanagedCallersOnly(EntryPoint = "ned_test_inject", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int TestInject(int kind, int x, int y)
        {
            var e = last; if (e == null) return 0;
            try { e.TestInject(kind, x, y); return 1; } catch (Exception) { return 0; }
        }

        /// <summary>Test only: where a knob is in the newest editor's view (the view's units).</summary>
        [UnmanagedCallersOnly(EntryPoint = "ned_test_hotspot", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int TestHotspot(int* x, int* y)
        {
            var e = last; if (e == null) return 0;
            try { if (e.Hotspot(out int a, out int b)) { *x = a; *y = b; return 1; } } catch (Exception) { }
            return 0;
        }
    }
}
