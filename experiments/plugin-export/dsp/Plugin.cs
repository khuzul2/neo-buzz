// Neo Filter as a CLAP plug-in: descriptor, factory, audio ports, parameters (raw Neo values, with text and units from
// ParamDef), state, latency, the gui extension (loads the editor library on demand) and process. Nothing in Process allocates.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NeoBuzz.Dsp;
using NeoBuzz.Filter;

namespace NeoExport
{
    internal sealed unsafe class Instance
    {
        public readonly ParamValues Values = new ParamValues(Params.Table);
        public FilterEngine Engine;
        public readonly float[] L = new float[FilterEngine.MaxBlock], R = new float[FilterEngine.MaxBlock];
        public Shared* Sh;
        public double Rate = 48000;
        public GCHandle Handle;
        public bool EditorOpen;
    }

    public static unsafe class ClapExports
    {
        static string dir = "";
        static int initCount;
        static ClapFactory* factory;
        static ClapDescriptor* desc;
        static ClapPluginAudioPorts* audioPorts;
        static ClapPluginParams* paramsExt;
        static ClapPluginState* stateExt;
        static ClapPluginLatency* latencyExt;
        static ClapPluginGui* guiExt;
        static NeoSpikeExt* spikeExt;
        static byte* idStr, extAudio, extParams, extState, extLatency, extGui, extSpike, apiX11, apiCocoa, apiWin32, portMain, portStereo, factoryId;
        static byte** features;

        static byte* U8(string s) => (byte*)Marshal.StringToCoTaskMemUTF8(s);
        static bool Eq(byte* a, byte* b) { for (int i = 0; ; i++) { if (a[i] != b[i]) return false; if (a[i] == 0) return true; } }
        static Instance Inst(ClapPlugin* p) => Unsafe.As<Instance>(GCHandle.FromIntPtr((IntPtr)p->PluginData).Target);

        // ---------------------------------------------------------------- entry (called by native/clap_entry.c)
        [UnmanagedCallersOnly(EntryPoint = "neo_clap_init", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static byte EntryInit(byte* pluginDir)
        {
            if (initCount++ > 0) return 1;
            dir = Marshal.PtrToStringUTF8((IntPtr)pluginDir) ?? "";
            idStr = U8("org.neobuzz.neo-filter"); extAudio = U8("clap.audio-ports"); extParams = U8("clap.params"); extState = U8("clap.state");
            extLatency = U8("clap.latency"); extGui = U8("clap.gui"); extSpike = U8("neo.spike.stats/1");
            apiX11 = U8("x11"); apiCocoa = U8("cocoa"); apiWin32 = U8("win32"); portMain = U8("Audio In/Out"); portStereo = U8("stereo"); factoryId = U8("clap.plugin-factory");
            features = (byte**)NativeMemory.AllocZeroed((nuint)(4 * sizeof(byte*)));
            features[0] = U8("audio-effect"); features[1] = U8("filter"); features[2] = U8("stereo"); features[3] = null;

            desc = (ClapDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(ClapDescriptor));
            desc->Version = new ClapVersion { Major = 1, Minor = 2, Revision = 10 };
            desc->Id = idStr; desc->Name = U8("Neo Filter"); desc->Vendor = U8("neo-buzz"); desc->Url = U8("https://github.com/khuzul2/neo-buzz");
            desc->ManualUrl = U8(""); desc->SupportUrl = U8(""); desc->PluginVersion = U8("0.0.1"); desc->Description = U8("Resonant filter with envelope follower and LFO (neo-buzz export spike)");
            desc->Features = features;

            factory = (ClapFactory*)NativeMemory.AllocZeroed((nuint)sizeof(ClapFactory));
            factory->Count = &FactoryCount; factory->Descriptor = &FactoryDescriptor; factory->Create = &FactoryCreate;

            audioPorts = (ClapPluginAudioPorts*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPluginAudioPorts));
            audioPorts->Count = &PortsCount; audioPorts->Get = &PortsGet;
            paramsExt = (ClapPluginParams*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPluginParams));
            paramsExt->Count = &ParamCount; paramsExt->GetInfo = &ParamGetInfo; paramsExt->GetValue = &ParamGetValue;
            paramsExt->ValueToText = &ParamValueToText; paramsExt->TextToValue = &ParamTextToValue; paramsExt->Flush = &ParamFlush;
            stateExt = (ClapPluginState*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPluginState));
            stateExt->Save = &StateSave; stateExt->Load = &StateLoad;
            latencyExt = (ClapPluginLatency*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPluginLatency));
            latencyExt->Get = &LatencyGet;
            guiExt = (ClapPluginGui*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPluginGui));
            guiExt->IsApiSupported = &GuiIsApiSupported; guiExt->GetPreferredApi = &GuiGetPreferredApi; guiExt->Create = &GuiCreate; guiExt->Destroy = &GuiDestroy;
            guiExt->SetScale = &GuiSetScale; guiExt->GetSize = &GuiGetSize; guiExt->CanResize = &GuiCanResize; guiExt->GetResizeHints = &GuiGetResizeHints;
            guiExt->AdjustSize = &GuiAdjustSize; guiExt->SetSize = &GuiSetSize; guiExt->SetParent = &GuiSetParent; guiExt->SetTransient = &GuiSetParent;
            guiExt->SuggestTitle = &GuiSuggestTitle; guiExt->Show = &GuiShow; guiExt->Hide = &GuiShow;
            spikeExt = (NeoSpikeExt*)NativeMemory.AllocZeroed((nuint)sizeof(NeoSpikeExt));
            spikeExt->GetStats = &SpikeStats; spikeExt->SetInprocLoad = &SpikeInproc; spikeExt->GcConfig = &SpikeGcConfig; spikeExt->EditorOpen = &SpikeEditorOpen; spikeExt->EditorClose = &SpikeEditorClose;
            LoadGen.EnsureSampler();   // watches this runtime's GCs (there should be none)
            return 1;
        }

        [UnmanagedCallersOnly(EntryPoint = "neo_clap_deinit", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void EntryDeinit() { /* a NativeAOT runtime cannot be unloaded; the statics stay for the process */ }

        [UnmanagedCallersOnly(EntryPoint = "neo_clap_get_factory", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void* EntryGetFactory(byte* id) => factory != null && id != null && Eq(id, factoryId) ? factory : null;

        // ---------------------------------------------------------------- factory
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static uint FactoryCount(ClapFactory* f) => 1;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static ClapDescriptor* FactoryDescriptor(ClapFactory* f, uint i) => i == 0 ? desc : null;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static ClapPlugin* FactoryCreate(ClapFactory* f, void* host, byte* id)
        {
            if (id == null || !Eq(id, idStr)) return null;
            var inst = new Instance();
            inst.Engine = new FilterEngine(inst.Values);
            inst.Handle = GCHandle.Alloc(inst);
            inst.Sh = (Shared*)NativeMemory.AllocZeroed((nuint)sizeof(Shared));
            inst.Sh->Magic_ = Shared.Magic; inst.Sh->Size = sizeof(Shared);
            for (int i = 0; i < Params.Table.Length; i++) inst.Sh->Param[i] = Params.Table[i].Default;
            var p = (ClapPlugin*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPlugin));
            p->Desc = desc; p->PluginData = (void*)GCHandle.ToIntPtr(inst.Handle);
            p->Init = &PInit; p->Destroy = &PDestroy; p->Activate = &PActivate; p->Deactivate = &PDeactivate;
            p->StartProcessing = &PStart; p->StopProcessing = &PStop; p->Reset = &PReset; p->Process = &PProcess;
            p->GetExtension = &PGetExtension; p->OnMainThread = &POnMain;
            return p;
        }

        // ---------------------------------------------------------------- plugin
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte PInit(ClapPlugin* p) => 1;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void PDestroy(ClapPlugin* p)
        {
            var inst = Inst(p);
            CloseEditor(inst);
            var sh = inst.Sh; inst.Sh = null;
            inst.Handle.Free();
            NativeMemory.Free(sh);
            NativeMemory.Free(p);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte PActivate(ClapPlugin* p, double sr, uint min, uint max)
        {
            var inst = Inst(p);
            inst.Rate = sr;
            inst.Engine.SetSampleRate((float)sr);
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void PDeactivate(ClapPlugin* p) { }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte PStart(ClapPlugin* p) => 1;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void PStop(ClapPlugin* p) { }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void PReset(ClapPlugin* p) => Inst(p).Engine.Reset();
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void POnMain(ClapPlugin* p) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void* PGetExtension(ClapPlugin* p, byte* id)
        {
            if (id == null) return null;
            if (Eq(id, extAudio)) return audioPorts;
            if (Eq(id, extParams)) return paramsExt;
            if (Eq(id, extState)) return stateExt;
            if (Eq(id, extLatency)) return latencyExt;
            if (Eq(id, extGui)) return guiExt;
            if (Eq(id, extSpike)) return spikeExt;
            return null;
        }

        // ---------------------------------------------------------------- audio ports, latency
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static uint PortsCount(ClapPlugin* p, byte isInput) => 1;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte PortsGet(ClapPlugin* p, uint index, byte isInput, ClapAudioPortInfo* info)
        {
            if (index != 0) return 0;
            info->Id = isInput != 0 ? 0u : 1u;
            Copy(info->Name, 256, isInput != 0 ? "Input" : "Output");
            info->Flags = 1;   // CLAP_AUDIO_PORT_IS_MAIN
            info->ChannelCount = 2;
            info->PortType = portStereo;
            info->InPlacePair = isInput != 0 ? 1u : 0u;   // in place pair: ids of the input and output port
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static uint LatencyGet(ClapPlugin* p) => (uint)Inst(p).Engine.Latency;

        static void Copy(byte* dst, int cap, string s)
        {
            int n = 0;
            foreach (char c in s) { if (n >= cap - 1) break; dst[n++] = c < 128 ? (byte)c : (byte)'?'; }
            dst[n] = 0;
        }

        static int CopyUtf8(byte* dst, uint cap, string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length && n < cap - 1; i++)
            {
                int c = s[i];
                if (c < 0x80) dst[n++] = (byte)c;
                else if (c < 0x800) { if (n + 2 >= cap) break; dst[n++] = (byte)(0xC0 | c >> 6); dst[n++] = (byte)(0x80 | c & 63); }
                else { if (n + 3 >= cap) break; dst[n++] = (byte)(0xE0 | c >> 12); dst[n++] = (byte)(0x80 | c >> 6 & 63); dst[n++] = (byte)(0x80 | c & 63); }
            }
            dst[n] = 0;
            return n;
        }

        // ---------------------------------------------------------------- parameters: id = Neo index, value = Neo raw value
        // (the raw scale is the knob's own: a frequency's raw value is its position on the log scale, so a host's automation
        // curve is the one the Neo editor draws). value_to_text gives the unit ("1.2 kHz", "-6.0 dB", "90°").
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static uint ParamCount(ClapPlugin* p) => (uint)Params.Table.Length;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte ParamGetInfo(ClapPlugin* p, uint index, ClapParamInfo* info)
        {
            if (index >= Params.Table.Length) return 0;
            var d = Params.Table[index];
            info->Id = (uint)d.Index;
            info->Flags = 1u << 5 | (d.Kind == MapKind.Enum ? 1u | 1u << 16 : 0u);   // AUTOMATABLE; STEPPED | ENUM
            info->Cookie = null;
            Copy(info->Name, 256, d.Name);
            Copy(info->Module, 1024, d.Group ?? "");
            info->Min = d.Min; info->Max = d.Max; info->Default = d.Default;
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte ParamGetValue(ClapPlugin* p, uint id, double* value)
        {
            if (id >= Params.Table.Length) return 0;
            *value = Inst(p).Values[(int)id];
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte ParamValueToText(ClapPlugin* p, uint id, double value, byte* buf, uint cap)
        {
            if (id >= Params.Table.Length || cap == 0) return 0;
            var d = Params.Table[id];
            string s = d.Describe((int)Math.Round(value));
            if (d.Unit != null && d.Unit.Length > 0 && !s.Contains(d.Unit)) s += " " + d.Unit;
            CopyUtf8(buf, cap, s);
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte ParamTextToValue(ClapPlugin* p, uint id, byte* text, double* value)
        {
            if (id >= Params.Table.Length) return 0;
            string t = Marshal.PtrToStringUTF8((IntPtr)text) ?? "";
            var d = Params.Table[id];
            // the raw value whose text matches (a search of the knob's positions: fine on the main thread)
            int best = -1; double bestDiff = double.MaxValue;
            if (double.TryParse(t.Split(' ')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double want) && d.Kind != MapKind.Enum)
            {
                for (int r = d.Min; r <= d.Max; r++)
                {
                    double diff = Math.Abs(d.Value(r) - want);
                    if (diff < bestDiff) { bestDiff = diff; best = r; }
                }
            }
            else
            {
                for (int r = d.Min; r <= d.Max; r++) if (string.Equals(d.Describe(r), t, StringComparison.OrdinalIgnoreCase)) { best = r; break; }
            }
            if (best < 0) return 0;
            *value = best;
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void ParamFlush(ClapPlugin* p, ClapInputEvents* inEvents, ClapOutputEvents* outEvents)
        {
            var inst = Inst(p);
            uint n = inEvents->Size(inEvents);
            for (uint i = 0; i < n; i++) Apply(inst, inEvents->Get(inEvents, i));
            DrainEdits(inst);
        }

        static void SetParam(Instance inst, int index, int raw)
        {
            if ((uint)index >= (uint)Params.Table.Length) return;
            inst.Values.Set(index, raw);
            var sh = inst.Sh;
            Volatile.Write(ref sh->Param[index], inst.Values[index]);
            Volatile.Write(ref sh->ParamSeq, sh->ParamSeq + 1);
        }

        static void Apply(Instance inst, ClapEventHeader* h)
        {
            if (h->SpaceId != 0 || h->Type != 5) return;   // CLAP_EVENT_PARAM_VALUE
            var e = (ClapEventParamValue*)h;
            SetParam(inst, (int)e->ParamId, (int)Math.Round(e->Value));
        }

        static void DrainEdits(Instance inst)
        {
            var sh = inst.Sh;
            while (Shared.Take(sh, out int idx, out int raw))
            {
                SetParam(inst, idx, raw);
                Volatile.Write(ref sh->EditsApplied, sh->EditsApplied + 1);
            }
        }

        // ---------------------------------------------------------------- state: "NFC1", count, raw values (int32 little endian)
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte StateSave(ClapPlugin* p, ClapOStream* s)
        {
            var inst = Inst(p);
            int n = Params.Table.Length;
            int* buf = stackalloc int[n + 2];
            buf[0] = 0x3143464E; buf[1] = n;
            for (int i = 0; i < n; i++) buf[2 + i] = inst.Values[i];
            byte* b = (byte*)buf; long left = (n + 2) * 4;
            while (left > 0)
            {
                long w = s->Write(s, b, (ulong)left);
                if (w <= 0) return 0;
                b += w; left -= w;
            }
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte StateLoad(ClapPlugin* p, ClapIStream* s)
        {
            var inst = Inst(p);
            int* hdr = stackalloc int[2];
            if (ReadAll(s, (byte*)hdr, 8) == 0 || hdr[0] != 0x3143464E) return 0;
            int n = hdr[1];
            if (n < 0 || n > 4096) return 0;
            int* vals = stackalloc int[Math.Max(1, Math.Min(n, 256))];
            if (n > 256) return 0;
            if (ReadAll(s, (byte*)vals, n * 4) == 0) return 0;
            for (int i = 0; i < n && i < Params.Table.Length; i++) SetParam(inst, i, vals[i]);
            return 1;
        }

        static byte ReadAll(ClapIStream* s, byte* b, long n)
        {
            while (n > 0)
            {
                long r = s->Read(s, b, (ulong)n);
                if (r <= 0) return 0;
                b += r; n -= r;
            }
            return 1;
        }

        // ---------------------------------------------------------------- process: no allocation, no lock
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int PProcess(ClapPlugin* p, ClapProcess* pr)
        {
            var inst = Inst(p);
            uint n = pr->FramesCount;
            if (pr->AudioOutputsCount < 1) return 0;
            var outb = pr->AudioOutputs;
            float* oL = outb->Data32[0], oR = outb->ChannelCount > 1 ? outb->Data32[1] : oL;
            float* iL = null, iR = null;
            if (pr->AudioInputsCount > 0 && pr->AudioInputs->Data32 != null)
            {
                iL = pr->AudioInputs->Data32[0]; iR = pr->AudioInputs->ChannelCount > 1 ? pr->AudioInputs->Data32[1] : iL;
            }
            var eng = inst.Engine;
            var t = pr->Transport;
            if (t != null)
            {
                eng.Tempo = (t->Flags & 1) != 0 ? t->Tempo : 120;
                var tr = new FilterTransport();
                if ((t->Flags & (1 << 1)) != 0 && (t->Flags & (1 << 4)) != 0 && eng.Tempo > 0)
                {
                    const int tpb = 480;
                    double beats = t->SongPosBeats / 2147483648.0;
                    double spt = inst.Rate * 60.0 / (eng.Tempo * tpb);
                    double ticks = beats * tpb;
                    double whole = Math.Floor(ticks);
                    tr.Playing = true; tr.TicksPerBeat = tpb; tr.Tick = (int)whole;
                    tr.SamplesPerTick = Math.Max(1, (int)Math.Round(spt)); tr.PosInTick = (int)((ticks - whole) * tr.SamplesPerTick);
                }
                eng.Transport = tr;
            }
            DrainEdits(inst);
            uint ne = pr->InEvents != null ? pr->InEvents->Size(pr->InEvents) : 0, ei = 0, pos = 0;
            float inPeak = 0, outPeak = 0;
            while (pos < n)
            {
                while (ei < ne)
                {
                    var h = pr->InEvents->Get(pr->InEvents, ei);
                    if (h->Time > pos) break;
                    Apply(inst, h); ei++;
                }
                uint next = n;
                if (ei < ne) { uint tt = pr->InEvents->Get(pr->InEvents, ei)->Time; if (tt < next) next = tt; }
                int chunk = (int)Math.Min(next - pos, (uint)FilterEngine.MaxBlock);
                var L = inst.L; var R = inst.R;
                if (iL != null)
                    for (int i = 0; i < chunk; i++) { float a = iL[pos + i], b = iR[pos + i]; L[i] = a; R[i] = b; float m = Math.Max(Math.Abs(a), Math.Abs(b)); if (m > inPeak) inPeak = m; }
                else { Array.Clear(L, 0, chunk); Array.Clear(R, 0, chunk); }
                eng.Process(L, R, chunk);
                for (int i = 0; i < chunk; i++) { float a = L[i], b = R[i]; oL[pos + i] = a; oR[pos + i] = b; float m = Math.Max(Math.Abs(a), Math.Abs(b)); if (m > outPeak) outPeak = m; }
                pos += (uint)chunk;
            }
            var sh = inst.Sh;
            Volatile.Write(ref sh->InPeak, inPeak); Volatile.Write(ref sh->OutPeak, outPeak);
            Volatile.Write(ref sh->MeterSeq, sh->MeterSeq + 1);
            return 1;   // CLAP_PROCESS_CONTINUE
        }

        // ---------------------------------------------------------------- gui: loads the editor library (own runtime, own GC) on demand
        static IntPtr editorLib;
        static delegate* unmanaged[Cdecl]<Shared*, int, int, int> edOpen;
        static delegate* unmanaged[Cdecl]<void> edClose;
        static delegate* unmanaged[Cdecl]<byte*, int, int> edGcConfig;

        static string EditorFile()
        {
            string name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "neoeditor.dll" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "neoeditor.dylib" : "neoeditor.so";
            return System.IO.Path.Combine(dir, name);
        }

        static bool LoadEditorLibrary()
        {
            if (editorLib != IntPtr.Zero) return true;
            if (!NativeLibrary.TryLoad(EditorFile(), out editorLib)) return false;
            edOpen = (delegate* unmanaged[Cdecl]<Shared*, int, int, int>)NativeLibrary.GetExport(editorLib, "ned_open");
            edClose = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(editorLib, "ned_close");
            edGcConfig = (delegate* unmanaged[Cdecl]<byte*, int, int>)NativeLibrary.GetExport(editorLib, "ned_gc_config");
            return true;
        }

        static int OpenEditor(Instance inst, int load, int edits)
        {
            if (inst.EditorOpen) return 1;
            if (!LoadEditorLibrary()) return 0;
            inst.Sh->Closing = 0;
            if (edOpen(inst.Sh, load, edits) == 0) return 0;
            inst.EditorOpen = true;
            return 1;
        }

        static void CloseEditor(Instance inst)
        {
            if (!inst.EditorOpen) return;
            Volatile.Write(ref inst.Sh->Closing, 1);
            edClose();
            inst.EditorOpen = false;
        }

        static int EnvInt(string name) => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : 0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte GuiIsApiSupported(ClapPlugin* p, byte* api, byte floating)
        {
            if (floating != 0 || api == null) return 0;
            return (byte)((RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Eq(api, apiWin32)) || (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && Eq(api, apiCocoa)) || (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && Eq(api, apiX11)) ? 1 : 0);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte GuiGetPreferredApi(ClapPlugin* p, byte** api, byte* floating)
        {
            *floating = 0;
            *api = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? apiWin32 : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? apiCocoa : apiX11;
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte GuiCreate(ClapPlugin* p, byte* api, byte floating) => (byte)OpenEditor(Inst(p), EnvInt("NEO_EXPORT_EDITOR_LOAD"), EnvInt("NEO_EXPORT_EDITOR_EDITS"));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void GuiDestroy(ClapPlugin* p) => CloseEditor(Inst(p));
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte GuiSetScale(ClapPlugin* p, double s) => 1;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte GuiGetSize(ClapPlugin* p, uint* w, uint* h) { *w = 640; *h = 400; return 1; }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte GuiCanResize(ClapPlugin* p) => 0;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte GuiGetResizeHints(ClapPlugin* p, void* h) => 0;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte GuiAdjustSize(ClapPlugin* p, uint* w, uint* h) { *w = 640; *h = 400; return 1; }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte GuiSetSize(ClapPlugin* p, uint w, uint h) => 1;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte GuiSetParent(ClapPlugin* p, void* w) => 1;   // spike: no window is created
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void GuiSuggestTitle(ClapPlugin* p, byte* t) { }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte GuiShow(ClapPlugin* p) => 1;

        // ---------------------------------------------------------------- the spike's measurement extension
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void SpikeStats(ClapPlugin* p, NeoStats* o)
        {
            var sh = Inst(p).Sh;
            o->DspGc0 = GC.CollectionCount(0); o->DspGc1 = GC.CollectionCount(1); o->DspGc2 = GC.CollectionCount(2);
            o->DspPauseNs = GC.GetTotalPauseDuration().Ticks * 100; o->DspMaxPauseNs = LoadGen.MaxPauseNs; o->DspAllocBytes = GC.GetTotalAllocatedBytes(false);
            o->InprocReady = LoadGen.Ready;
            o->EdGc0 = Volatile.Read(ref sh->EdGc0); o->EdGc1 = Volatile.Read(ref sh->EdGc1); o->EdGc2 = Volatile.Read(ref sh->EdGc2);
            o->EdAlive = Volatile.Read(ref sh->EdAlive); o->EdReady = Volatile.Read(ref sh->EdReady);
            o->EdPauseNs = Volatile.Read(ref sh->EdPauseNs); o->EdMaxPauseNs = Volatile.Read(ref sh->EdMaxPauseNs); o->EdAllocBytes = Volatile.Read(ref sh->EdAllocBytes);
            o->EditsApplied = Volatile.Read(ref sh->EditsApplied); o->EditsPosted = Volatile.Read(ref sh->EdEditsPosted); o->EdFrames = Volatile.Read(ref sh->EdFrames);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void SpikeInproc(ClapPlugin* p, int mode) => LoadGen.Start(mode);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int SpikeGcConfig(byte* buf, int cap) => LoadGen.Describe(buf, cap);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int SpikeEditorOpen(ClapPlugin* p, int load, int edits) => OpenEditor(Inst(p), load, edits);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void SpikeEditorClose(ClapPlugin* p) => CloseEditor(Inst(p));
    }
}
