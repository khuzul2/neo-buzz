// The plug-in runtime (docs/specs/plugin-export.md): the CLAP ABI for every exported Neo machine, driven by the registry the
// generator writes. One factory lists them all; each instance owns its engine and parameter values. Parameters are the machine's
// own table (id = Neo index, value = Neo raw value, text and units from ParamDef), state is versioned, the editor lives in its
// own NativeAOT library (own runtime, own GC) and shares only the Shared struct. Nothing in process allocates.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoExport
{
    internal sealed unsafe class Instance
    {
        public PluginInfo Info;
        public ExportEngine Eng;
        public ParamValues Values;
        public float[] L, R, KL, KR;
        public Shared* Sh;
        public ClapHost* Host;
        public ClapPlugin* Plugin;
        public double Rate = 48000;
        public GCHandle Handle;
        public int ReportedLatency;
        public volatile int LatencyDirty;
        public IntPtr Editor;          // the editor library's handle (0: no editor)
        public uint TimerId;
        public bool Active;
        public int Quiet;
    }

    public static unsafe class ClapExports
    {
        const int ProcessError = 0, ProcessContinue = 1;
        static string dir = "";
        static int initCount;
        static ClapFactory* factory;
        static ClapDescriptor*[] descs;
        static ClapPluginAudioPorts* audioPorts;
        static ClapPluginNotePorts* notePorts;
        static ClapPluginParams* paramsExt;
        static ClapPluginState* stateExt;
        static ClapPluginLatency* latencyExt;
        static ClapPluginTail* tailExt;
        static ClapPluginGui* guiExt;
        static ClapPluginTimerSupport* timerExt;
        static NeoSpikeExt* statsExt;
        static byte* extAudio, extNotes, extParams, extState, extLatency, extTail, extGui, extTimer, extStats, apiX11, apiCocoa, apiWin32, portStereo, factoryId;
        static byte* hostParamsId, hostLatencyId, hostTimerId, hostGuiId;

        static byte* U8(string s) => (byte*)Marshal.StringToCoTaskMemUTF8(s);
        static bool Eq(byte* a, byte* b) { for (int i = 0; ; i++) { if (a[i] != b[i]) return false; if (a[i] == 0) return true; } }
        static Instance Inst(ClapPlugin* p) => Unsafe.As<Instance>(GCHandle.FromIntPtr((IntPtr)p->PluginData).Target);

        // ---------------------------------------------------------------- entry (called by native/stub_impl.c)
        [UnmanagedCallersOnly(EntryPoint = "neo_clap_init", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static byte EntryInit(byte* pluginDir)
        {
            if (initCount++ > 0) return 1;
            dir = Marshal.PtrToStringUTF8((IntPtr)pluginDir) ?? "";
            extAudio = U8("clap.audio-ports"); extNotes = U8("clap.note-ports"); extParams = U8("clap.params"); extState = U8("clap.state");
            extLatency = U8("clap.latency"); extTail = U8("clap.tail"); extGui = U8("clap.gui"); extTimer = U8("clap.timer-support"); extStats = U8("neo.export.stats/1");
            apiX11 = U8("x11"); apiCocoa = U8("cocoa"); apiWin32 = U8("win32"); portStereo = U8("stereo"); factoryId = U8("clap.plugin-factory");
            hostParamsId = U8("clap.params"); hostLatencyId = U8("clap.latency"); hostTimerId = U8("clap.timer-support"); hostGuiId = U8("clap.gui");

            var all = Registry.All;
            descs = new ClapDescriptor*[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                var info = all[i];
                var features = (byte**)NativeMemory.AllocZeroed((nuint)((info.Features.Length + 1) * sizeof(byte*)));
                for (int f = 0; f < info.Features.Length; f++) features[f] = U8(info.Features[f]);
                var d = (ClapDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(ClapDescriptor));
                d->Version = new ClapVersion { Major = 1, Minor = 2, Revision = 10 };
                d->Id = U8(info.Id); d->Name = U8(info.Name); d->Vendor = U8("NeoBuzz Live"); d->Url = U8("https://github.com/khuzul2/neo-buzz");
                d->ManualUrl = U8(""); d->SupportUrl = U8(""); d->PluginVersion = U8("0.1.0"); d->Description = U8(info.Description);
                d->Features = features;
                descs[i] = d;
            }
            factory = (ClapFactory*)NativeMemory.AllocZeroed((nuint)sizeof(ClapFactory));
            factory->Count = &FactoryCount; factory->Descriptor = &FactoryDescriptor; factory->Create = &FactoryCreate;

            audioPorts = (ClapPluginAudioPorts*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPluginAudioPorts));
            audioPorts->Count = &PortsCount; audioPorts->Get = &PortsGet;
            notePorts = (ClapPluginNotePorts*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPluginNotePorts));
            notePorts->Count = &NotePortsCount; notePorts->Get = &NotePortsGet;
            paramsExt = (ClapPluginParams*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPluginParams));
            paramsExt->Count = &ParamCount; paramsExt->GetInfo = &ParamGetInfo; paramsExt->GetValue = &ParamGetValue;
            paramsExt->ValueToText = &ParamValueToText; paramsExt->TextToValue = &ParamTextToValue; paramsExt->Flush = &ParamFlush;
            stateExt = (ClapPluginState*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPluginState));
            stateExt->Save = &StateSave; stateExt->Load = &StateLoad;
            latencyExt = (ClapPluginLatency*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPluginLatency));
            latencyExt->Get = &LatencyGet;
            tailExt = (ClapPluginTail*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPluginTail));
            tailExt->Get = &TailGet;
            timerExt = (ClapPluginTimerSupport*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPluginTimerSupport));
            timerExt->OnTimer = &OnTimer;
            guiExt = (ClapPluginGui*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPluginGui));
            guiExt->IsApiSupported = &GuiIsApiSupported; guiExt->GetPreferredApi = &GuiGetPreferredApi; guiExt->Create = &GuiCreate; guiExt->Destroy = &GuiDestroy;
            guiExt->SetScale = &GuiSetScale; guiExt->GetSize = &GuiGetSize; guiExt->CanResize = &GuiCanResize; guiExt->GetResizeHints = &GuiGetResizeHints;
            guiExt->AdjustSize = &GuiAdjustSize; guiExt->SetSize = &GuiSetSize; guiExt->SetParent = &GuiSetParent; guiExt->SetTransient = &GuiSetTransient;
            guiExt->SuggestTitle = &GuiSuggestTitle; guiExt->Show = &GuiShow; guiExt->Hide = &GuiHide;
            statsExt = (NeoSpikeExt*)NativeMemory.AllocZeroed((nuint)sizeof(NeoSpikeExt));
            statsExt->GetStats = &StatsGet; statsExt->SetInprocLoad = &StatsInproc; statsExt->GcConfig = &StatsGcConfig; statsExt->EditorOpen = &StatsEditorOpen; statsExt->EditorClose = &StatsEditorClose;
            LoadGen.EnsureSampler();   // watches this runtime's GCs (there should be none)
            return 1;
        }

        [UnmanagedCallersOnly(EntryPoint = "neo_clap_deinit", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void EntryDeinit() { /* a NativeAOT runtime cannot be unloaded; the statics stay for the process */ }

        [UnmanagedCallersOnly(EntryPoint = "neo_clap_get_factory", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void* EntryGetFactory(byte* id) => factory != null && id != null && Eq(id, factoryId) ? factory : null;

        // ---------------------------------------------------------------- factory
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static uint FactoryCount(ClapFactory* f) => (uint)descs.Length;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static ClapDescriptor* FactoryDescriptor(ClapFactory* f, uint i) => i < descs.Length ? descs[i] : null;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static ClapPlugin* FactoryCreate(ClapFactory* f, void* host, byte* id)
        {
            if (id == null) return null;
            int which = -1;
            for (int i = 0; i < descs.Length; i++) if (Eq(id, descs[i]->Id)) { which = i; break; }
            if (which < 0) return null;
            var inst = new Instance { Info = Registry.All[which], Host = (ClapHost*)host };
            inst.Eng = inst.Info.Make();
            inst.Values = new ParamValues(inst.Eng.Table);
            inst.Eng.Create(inst.Values);
            int mb = inst.Eng.MaxBlock;
            inst.L = new float[mb]; inst.R = new float[mb]; inst.KL = new float[mb]; inst.KR = new float[mb];
            inst.Handle = GCHandle.Alloc(inst);
            var sh = (Shared*)NativeMemory.AllocZeroed((nuint)sizeof(Shared));
            sh->Magic_ = Shared.Magic; sh->Size = sizeof(Shared);
            int n = Math.Min(inst.Eng.Table.Length, Shared.MaxParams);
            sh->ParamCount = n;
            for (int i = 0; i < n; i++) sh->Param[i] = inst.Eng.Table[i].Default;
            sh->FlushFn = (void*)(delegate* unmanaged[Cdecl]<void*, void>)&RequestFlush; sh->FlushCtx = (void*)GCHandle.ToIntPtr(inst.Handle);
            inst.Sh = sh;
            var p = (ClapPlugin*)NativeMemory.AllocZeroed((nuint)sizeof(ClapPlugin));
            p->Desc = descs[which]; p->PluginData = (void*)GCHandle.ToIntPtr(inst.Handle);
            p->Init = &PInit; p->Destroy = &PDestroy; p->Activate = &PActivate; p->Deactivate = &PDeactivate;
            p->StartProcessing = &PStart; p->StopProcessing = &PStop; p->Reset = &PReset; p->Process = &PProcess;
            p->GetExtension = &PGetExtension; p->OnMainThread = &POnMain;
            inst.Plugin = p;
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
            inst.Eng.SetSampleRate((float)sr);
            inst.Sh->Rate = sr;
            inst.ReportedLatency = inst.Eng.Latency;
            inst.LatencyDirty = 0;
            inst.Active = true;
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void PDeactivate(ClapPlugin* p) => Inst(p).Active = false;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte PStart(ClapPlugin* p) => 1;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void PStop(ClapPlugin* p) { }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void PReset(ClapPlugin* p) => Inst(p).Eng.Reset();

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void POnMain(ClapPlugin* p)
        {
            var inst = Inst(p);
            if (Interlocked.Exchange(ref inst.LatencyDirty, 0) == 0) return;
            // The latency may change only while the plug-in is not active: ask the host to restart it, which asks again.
            if (inst.Active && inst.Host != null && inst.Host->RequestRestart != null) inst.Host->RequestRestart(inst.Host);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void* PGetExtension(ClapPlugin* p, byte* id)
        {
            if (id == null) return null;
            if (Eq(id, extAudio)) return audioPorts;
            if (Eq(id, extParams)) return paramsExt;
            if (Eq(id, extState)) return stateExt;
            if (Eq(id, extLatency)) return latencyExt;
            if (Eq(id, extTail)) return tailExt;
            if (Eq(id, extGui)) return guiExt;
            if (Eq(id, extTimer)) return timerExt;
            if (Eq(id, extStats)) return statsExt;
            if (Eq(id, extNotes)) return Inst(p).Info.Instrument ? notePorts : null;
            return null;
        }

        // ---------------------------------------------------------------- audio ports, notes, latency, tail
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static uint PortsCount(ClapPlugin* p, byte isInput)
        {
            var info = Inst(p).Info;
            if (isInput != 0) return info.Instrument ? 0u : info.Sidechain ? 2u : 1u;
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte PortsGet(ClapPlugin* p, uint index, byte isInput, ClapAudioPortInfo* info)
        {
            var pi = Inst(p).Info;
            bool input = isInput != 0;
            uint count = input ? (pi.Instrument ? 0u : pi.Sidechain ? 2u : 1u) : 1u;
            if (index >= count) return 0;
            info->PortType = portStereo; info->ChannelCount = 2;
            if (index == 0)
            {
                info->Id = input ? 0u : 1u;
                Copy(info->Name, 256, input ? "Input" : "Output");
                info->Flags = 1;   // CLAP_AUDIO_PORT_IS_MAIN
                info->InPlacePair = pi.Instrument ? 0xFFFFFFFFu : input ? 1u : 0u;
            }
            else   // the sidechain input
            {
                info->Id = 2; Copy(info->Name, 256, "Sidechain"); info->Flags = 0; info->InPlacePair = 0xFFFFFFFFu;
            }
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static uint NotePortsCount(ClapPlugin* p, byte isInput) => isInput != 0 ? 1u : 0u;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte NotePortsGet(ClapPlugin* p, uint index, byte isInput, ClapNotePortInfo* info)
        {
            if (index != 0 || isInput == 0) return 0;
            info->Id = 0; info->SupportedDialects = 1 | 2; info->PreferredDialect = 1;   // CLAP | MIDI
            Copy(info->Name, 256, "Notes");
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static uint LatencyGet(ClapPlugin* p) => (uint)Math.Max(0, Inst(p).ReportedLatency);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static uint TailGet(ClapPlugin* p)
        {
            var inst = Inst(p);
            return (uint)(inst.Info.TailSeconds * inst.Rate) + (uint)Math.Max(0, inst.ReportedLatency);
        }

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
        // curve is the one the Neo editor draws). value_to_text gives the unit ("1.2 kHz", "-6.0 dB", "90 deg").
        static ParamDef[] Tab(ClapPlugin* p) => Inst(p).Eng.Table;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static uint ParamCount(ClapPlugin* p) => (uint)Math.Min(Tab(p).Length, Shared.MaxParams);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte ParamGetInfo(ClapPlugin* p, uint index, ClapParamInfo* info)
        {
            var t = Tab(p);
            if (index >= t.Length || index >= Shared.MaxParams) return 0;
            var d = t[index];
            info->Id = (uint)d.Index;
            bool stepped = d.Kind == MapKind.Enum || d.Kind == MapKind.Switch || d.Kind == MapKind.Int || d.Max - d.Min <= 1;
            info->Flags = 1u << 5 | (stepped ? 1u : 0u) | (d.Kind == MapKind.Enum ? 1u << 16 : 0u);   // AUTOMATABLE; STEPPED; ENUM
            info->Cookie = null;
            CopyUtf8(info->Name, 256, d.Name);
            CopyUtf8(info->Module, 1024, d.Group ?? "");
            info->Min = d.Min; info->Max = d.Max; info->Default = d.Default;
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte ParamGetValue(ClapPlugin* p, uint id, double* value)
        {
            var inst = Inst(p);
            if (id >= inst.Eng.Table.Length || id >= Shared.MaxParams) return 0;
            *value = inst.Values[(int)id];
            return 1;
        }

        internal static string TextOf(ParamDef d, double value)
        {
            string s = d.Describe((int)Math.Round(value));
            if (d.Unit != null && d.Unit.Length > 0 && !s.Contains(d.Unit)) s += " " + d.Unit;
            return s;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte ParamValueToText(ClapPlugin* p, uint id, double value, byte* buf, uint cap)
        {
            var t = Tab(p);
            if (id >= t.Length || cap == 0) return 0;
            CopyUtf8(buf, cap, TextOf(t[id], value));
            return 1;
        }

        /// <summary>The number in a parameter's text, in the unit's base ("2.00 kHz" 2000, "-6.0 dB" -6).</summary>
        static bool Number(string t, out double v)
        {
            v = 0;
            var parts = t.Trim().Split(' ');
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return false;
            if (parts.Length > 1 && parts[1].Equals("kHz", StringComparison.OrdinalIgnoreCase)) v *= 1000;
            return true;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte ParamTextToValue(ClapPlugin* p, uint id, byte* text, double* value)
        {
            var tab = Tab(p);
            if (id >= tab.Length) return 0;
            string t = Marshal.PtrToStringUTF8((IntPtr)text) ?? "";
            var d = tab[id];
            // the raw value whose text is this text (a search of the knob's positions: fine on the main thread), else the nearest number
            int best = -1;
            for (int r = d.Min; r <= d.Max; r++) if (string.Equals(TextOf(d, r), t, StringComparison.OrdinalIgnoreCase)) { best = r; break; }
            if (best < 0 && d.Kind != MapKind.Enum && Number(t, out double want))
            {
                double bestDiff = double.MaxValue;
                for (int r = d.Min; r <= d.Max; r++)
                {
                    if (!Number(TextOf(d, r), out double have)) continue;
                    double diff = Math.Abs(have - want);
                    if (diff < bestDiff) { bestDiff = diff; best = r; }
                }
            }
            if (best < 0) return 0;
            *value = best;
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void ParamFlush(ClapPlugin* p, ClapInputEvents* inEvents, ClapOutputEvents* outEvents)
        {
            var inst = Inst(p);
            uint n = inEvents != null ? inEvents->Size(inEvents) : 0;
            for (uint i = 0; i < n; i++) Apply(inst, inEvents->Get(inEvents, i));
            DrainEdits(inst, outEvents, 0);
            CheckLatency(inst);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SetParam(Instance inst, int index, int raw)
        {
            if ((uint)index >= (uint)inst.Eng.Table.Length) return;
            inst.Values.Set(index, raw);
            var sh = inst.Sh;
            if (index < Shared.MaxParams) Volatile.Write(ref sh->Param[index], inst.Values[index]);
            Volatile.Write(ref sh->ParamSeq, sh->ParamSeq + 1);
        }

        static void CheckLatency(Instance inst)
        {
            if (inst.Eng.Latency == inst.ReportedLatency || inst.LatencyDirty != 0) return;
            inst.LatencyDirty = 1;
            if (inst.Host != null && inst.Host->RequestCallback != null) inst.Host->RequestCallback(inst.Host);
        }

        static int MidiVel(double v) => Math.Clamp((int)Math.Round(v * 127), 1, 127);

        static void Apply(Instance inst, ClapEventHeader* h)
        {
            if (h->SpaceId != 0) return;
            switch (h->Type)
            {
                case 5:   // PARAM_VALUE
                {
                    var e = (ClapEventParamValue*)h;
                    SetParam(inst, (int)e->ParamId, (int)Math.Round(e->Value));
                    break;
                }
                case 0:   // NOTE_ON
                {
                    var e = (ClapEventNote*)h;
                    if (e->Key >= 0 && e->Key < 128) inst.Eng.NoteOn(1000 + e->Key, e->Key, MidiVel(e->Velocity) / 127f);
                    break;
                }
                case 1:   // NOTE_OFF
                case 2:   // NOTE_CHOKE
                {
                    var e = (ClapEventNote*)h;
                    if (e->Key >= 0 && e->Key < 128) inst.Eng.NoteOff(1000 + e->Key);
                    break;
                }
                case 10:  // MIDI
                {
                    var e = (ClapEventMidi*)h;
                    int status = e->Data[0] & 0xF0, a = e->Data[1], b = e->Data[2];
                    if (status == 0x90 && b > 0) inst.Eng.NoteOn(1000 + a, a, Math.Clamp(b, 1, 127) / 127f);
                    else if (status == 0x80 || status == 0x90) inst.Eng.NoteOff(1000 + a);
                    else if (status == 0xB0)
                    {
                        if (a == 1) inst.Eng.ModWheel(Math.Clamp(b, 0, 127) / 127f);
                        else if (a == 64) inst.Eng.Sustain(b >= 64);
                        else if (a == 120) inst.Eng.AllNotesOff(true);
                        else if (a == 123) inst.Eng.AllNotesOff(false);
                    }
                    else if (status == 0xE0) inst.Eng.PitchBend(Math.Clamp((((b << 7) | a) - 8192) / 8192f, -1f, 1f));
                    else if (status == 0xD0) inst.Eng.Aftertouch(Math.Clamp(a, 0, 127) / 127f);
                    break;
                }
            }
        }

        /// <summary>The editor's edits: applied, and announced to the host as gestures and values (so its automation records them).</summary>
        static void DrainEdits(Instance inst, ClapOutputEvents* outEvents, uint time)
        {
            var sh = inst.Sh;
            while (Shared.Take(sh, out int kind, out int idx, out int raw))
            {
                if (kind == Shared.EditValue) SetParam(inst, idx, raw);
                Volatile.Write(ref sh->EditsApplied, sh->EditsApplied + 1);
                if (outEvents == null || outEvents->TryPush == null || (uint)idx >= (uint)inst.Eng.Table.Length) continue;
                if (kind == Shared.EditValue)
                {
                    ClapEventParamValue ev = default;
                    ev.Header.Size = (uint)sizeof(ClapEventParamValue); ev.Header.Time = time; ev.Header.SpaceId = 0; ev.Header.Type = 5; ev.Header.Flags = 1;   // IS_LIVE
                    ev.ParamId = (uint)idx; ev.NoteId = -1; ev.PortIndex = -1; ev.Channel = -1; ev.Key = -1; ev.Value = inst.Values[idx];
                    outEvents->TryPush(outEvents, &ev.Header);
                }
                else
                {
                    ClapEventParamGesture ev = default;
                    ev.Header.Size = (uint)sizeof(ClapEventParamGesture); ev.Header.Time = time; ev.Header.SpaceId = 0; ev.Header.Type = (ushort)(kind == Shared.EditBegin ? 7 : 8); ev.Header.Flags = 1;
                    ev.ParamId = (uint)idx;
                    outEvents->TryPush(outEvents, &ev.Header);
                }
            }
        }

        // ---------------------------------------------------------------- state: "NBPS", format 1, machine state version, count, raw values (int32 little endian)
        const uint StateMagic = 0x5350424E;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte StateSave(ClapPlugin* p, ClapOStream* s)
        {
            var inst = Inst(p);
            int n = inst.Eng.Table.Length;
            int* buf = (int*)NativeMemory.Alloc((nuint)((n + 4) * 4));
            buf[0] = (int)StateMagic; buf[1] = 1; buf[2] = inst.Eng.StateVersion; buf[3] = n;
            for (int i = 0; i < n; i++) buf[4 + i] = inst.Values[i];
            byte* b = (byte*)buf; long left = (n + 4) * 4;
            byte ok = 1;
            while (left > 0)
            {
                long w = s->Write(s, b, (ulong)left);
                if (w <= 0) { ok = 0; break; }
                b += w; left -= w;
            }
            NativeMemory.Free(buf);
            return ok;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte StateLoad(ClapPlugin* p, ClapIStream* s)
        {
            var inst = Inst(p);
            int* hdr = stackalloc int[4];
            if (ReadAll(s, (byte*)hdr, 16) == 0 || (uint)hdr[0] != StateMagic || hdr[1] != 1) return 0;
            int from = hdr[2], n = hdr[3];
            if (n < 0 || n > 4096) return 0;
            var vals = new int[n];
            fixed (int* v = vals) if (n > 0 && ReadAll(s, (byte*)v, n * 4) == 0) return 0;
            var tab = inst.Eng.Table;
            if (from < inst.Eng.StateVersion) inst.Eng.MigrateState(from, vals);
            // Parameters are append-only: a state with fewer values leaves the rest at their defaults, a newer one loads what is known.
            for (int i = 0; i < tab.Length; i++) SetParam(inst, i, i < n ? vals[i] : tab[i].Default);
            CheckLatency(inst);
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
            if (pr->AudioOutputsCount < 1) return ProcessError;
            var outb = pr->AudioOutputs;
            float* oL = outb->Data32[0], oR = outb->ChannelCount > 1 ? outb->Data32[1] : oL;
            float* iL = null, iR = null, kL = null, kR = null;
            if (!inst.Info.Instrument && pr->AudioInputsCount > 0 && pr->AudioInputs->Data32 != null)
            {
                iL = pr->AudioInputs->Data32[0]; iR = pr->AudioInputs->ChannelCount > 1 ? pr->AudioInputs->Data32[1] : iL;
                if (inst.Info.Sidechain && pr->AudioInputsCount > 1 && pr->AudioInputs[1].Data32 != null && pr->AudioInputs[1].ChannelCount > 0)
                {
                    kL = pr->AudioInputs[1].Data32[0]; kR = pr->AudioInputs[1].ChannelCount > 1 ? pr->AudioInputs[1].Data32[1] : kL;
                }
            }
            var eng = inst.Eng;
            var t = pr->Transport;
            if (t != null)
            {
                double tempo = (t->Flags & 1) != 0 ? t->Tempo : 120;
                bool playing = (t->Flags & (1 << 1)) != 0 && (t->Flags & (1 << 4)) != 0;
                eng.SetTransport(tempo, playing, t->SongPosBeats / 2147483648.0, inst.Rate);
                inst.Sh->Tempo = tempo;
            }
            bool gate = inst.Info.GateIdle;
            DrainEdits(inst, pr->OutEvents, 0);
            uint ne = pr->InEvents != null ? pr->InEvents->Size(pr->InEvents) : 0, ei = 0, pos = 0;
            float inPeak = 0, outPeak = 0;
            int maxBlock = eng.MaxBlock;
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
                int chunk = (int)Math.Min(next - pos, (uint)maxBlock);
                var L = inst.L; var R = inst.R;
                float peak = 0;
                if (iL != null)
                    for (int i = 0; i < chunk; i++) { float a = iL[pos + i], b = iR[pos + i]; L[i] = a; R[i] = b; float m = Math.Max(Math.Abs(a), Math.Abs(b)); if (m > peak) peak = m; }
                else { Array.Clear(L, 0, chunk); Array.Clear(R, 0, chunk); }
                if (peak > inPeak) inPeak = peak;
                bool active = true;
                if (gate && peak <= 9.2e-7f && eng.Idle)
                {
                    // Like the machine in a song: silence in, nothing left ringing, nothing to compute.
                    Array.Clear(L, 0, chunk); Array.Clear(R, 0, chunk);
                    active = false;
                }
                else
                {
                    if (kL != null)
                        for (int i = 0; i < chunk; i++) { inst.KL[i] = kL[pos + i]; inst.KR[i] = kR[pos + i]; }
                    active = eng.Process(L, R, kL != null ? inst.KL : null, kL != null ? inst.KR : null, chunk);
                    if (!active) { Array.Clear(L, 0, chunk); Array.Clear(R, 0, chunk); }
                }
                for (int i = 0; i < chunk; i++) { float a = L[i], b = R[i]; if (!float.IsFinite(a)) a = 0; if (!float.IsFinite(b)) b = 0; oL[pos + i] = a; oR[pos + i] = b; float m = Math.Max(Math.Abs(a), Math.Abs(b)); if (m > outPeak) outPeak = m; }
                pos += (uint)chunk;
            }
            // Events after the last sample (time >= frames) are applied now.
            while (ei < ne) { Apply(inst, pr->InEvents->Get(pr->InEvents, ei)); ei++; }
            var sh = inst.Sh;
            Volatile.Write(ref sh->InPeak, inPeak); Volatile.Write(ref sh->OutPeak, outPeak);
            Volatile.Write(ref sh->MeterSeq, sh->MeterSeq + 1);
            CheckLatency(inst);
            return ProcessContinue;
        }

        // ---------------------------------------------------------------- host requests
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void RequestFlush(void* ctx)
        {
            var inst = Unsafe.As<Instance>(GCHandle.FromIntPtr((IntPtr)ctx).Target);
            var host = inst.Host;
            if (host == null || host->GetExtension == null) return;
            var ext = (ClapHostParams*)host->GetExtension(host, hostParamsId);
            if (ext != null && ext->RequestFlush != null) ext->RequestFlush(host);
        }

        // ---------------------------------------------------------------- the editor: its own library (own runtime, own GC), on demand
        static IntPtr editorLib;
        static delegate* unmanaged[Cdecl]<Shared*, byte*, double, IntPtr> edCreate;
        static delegate* unmanaged[Cdecl]<IntPtr, void> edDestroy, edTick, edShow, edHide;
        static delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int> edGetSize;
        static delegate* unmanaged[Cdecl]<IntPtr, uint, uint, int> edSetSize;
        static delegate* unmanaged[Cdecl]<IntPtr, double, int> edSetScale;
        static delegate* unmanaged[Cdecl]<IntPtr, int, void*, int> edSetParent;
        static delegate* unmanaged[Cdecl]<byte*, int, int> edGcConfig;
        static delegate* unmanaged[Cdecl]<byte*, void> edInit;

        static string EditorFile()
        {
            string name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "neo_editor.dll" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "neo_editor.dylib" : "neo_editor.so";
            return System.IO.Path.Combine(dir, name);
        }

        static bool LoadEditorLibrary()
        {
            if (editorLib != IntPtr.Zero) return true;
            if (!NativeLibrary.TryLoad(EditorFile(), out editorLib)) return false;
            edCreate = (delegate* unmanaged[Cdecl]<Shared*, byte*, double, IntPtr>)NativeLibrary.GetExport(editorLib, "ned_create");
            edDestroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(editorLib, "ned_destroy");
            edTick = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(editorLib, "ned_tick");
            edShow = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(editorLib, "ned_show");
            edHide = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(editorLib, "ned_hide");
            edGetSize = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int>)NativeLibrary.GetExport(editorLib, "ned_get_size");
            edSetSize = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint, int>)NativeLibrary.GetExport(editorLib, "ned_set_size");
            edSetScale = (delegate* unmanaged[Cdecl]<IntPtr, double, int>)NativeLibrary.GetExport(editorLib, "ned_set_scale");
            edSetParent = (delegate* unmanaged[Cdecl]<IntPtr, int, void*, int>)NativeLibrary.GetExport(editorLib, "ned_set_parent");
            edGcConfig = (delegate* unmanaged[Cdecl]<byte*, int, int>)NativeLibrary.GetExport(editorLib, "ned_gc_config");
            edInit = (delegate* unmanaged[Cdecl]<byte*, void>)NativeLibrary.GetExport(editorLib, "ned_init");
            var d8 = U8(dir);   // the folder the native libraries of the editor (Skia, HarfBuzz) are next to
            edInit(d8);
            Marshal.FreeCoTaskMem((IntPtr)d8);
            return true;
        }

        static double guiScale = 1;

        static int OpenEditor(Instance inst)
        {
            if (inst.Editor != IntPtr.Zero) return 1;
            if (!LoadEditorLibrary()) return 0;
            inst.Sh->Closing = 0;
            var id = U8(inst.Info.Id);
            inst.Editor = edCreate(inst.Sh, id, guiScale);
            Marshal.FreeCoTaskMem((IntPtr)id);
            if (inst.Editor == IntPtr.Zero) return 0;
            StartTimer(inst);
            return 1;
        }

        static void StartTimer(Instance inst)
        {
            var h = inst.Host;
            if (h == null || h->GetExtension == null) return;
            var ext = (ClapHostTimerSupport*)h->GetExtension(h, hostTimerId);
            uint id = 0;
            if (ext != null && ext->RegisterTimer != null && ext->RegisterTimer(h, 16, &id) != 0) inst.TimerId = id;
        }

        static void CloseEditor(Instance inst)
        {
            if (inst.Editor == IntPtr.Zero) return;
            var h = inst.Host;
            if (inst.TimerId != 0 && h != null && h->GetExtension != null)
            {
                var ext = (ClapHostTimerSupport*)h->GetExtension(h, hostTimerId);
                if (ext != null && ext->UnregisterTimer != null) ext->UnregisterTimer(h, inst.TimerId);
                inst.TimerId = 0;
            }
            Volatile.Write(ref inst.Sh->Closing, 1);
            var e = inst.Editor; inst.Editor = IntPtr.Zero;
            edDestroy(e);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnTimer(ClapPlugin* p, uint id)
        {
            var inst = Inst(p);
            if (inst.Editor != IntPtr.Zero && id == inst.TimerId) edTick(inst.Editor);
        }

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

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte GuiCreate(ClapPlugin* p, byte* api, byte floating) => (byte)OpenEditor(Inst(p));
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void GuiDestroy(ClapPlugin* p) => CloseEditor(Inst(p));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte GuiSetScale(ClapPlugin* p, double s)
        {
            guiScale = s > 0 ? s : 1;
            var inst = Inst(p);
            return inst.Editor != IntPtr.Zero ? (byte)edSetScale(inst.Editor, guiScale) : (byte)1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte GuiGetSize(ClapPlugin* p, uint* w, uint* h)
        {
            var inst = Inst(p);
            if (inst.Editor == IntPtr.Zero) return 0;
            return (byte)edGetSize(inst.Editor, w, h);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte GuiCanResize(ClapPlugin* p) => 1;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte GuiGetResizeHints(ClapPlugin* p, ClapResizeHints* hints)
        {
            var inst = Inst(p);
            uint w = 0, h = 0;
            if (inst.Editor == IntPtr.Zero || edGetSize(inst.Editor, &w, &h) == 0) return 0;
            double s = guiScale > 0 ? guiScale : 1;
            hints->CanResizeH = 1; hints->CanResizeV = 1; hints->PreserveAspect = 1;
            hints->AspectW = (uint)Math.Max(1, Math.Round(w / s)); hints->AspectH = (uint)Math.Max(1, Math.Round(h / s));
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte GuiAdjustSize(ClapPlugin* p, uint* w, uint* h)
        {
            var inst = Inst(p);
            uint nw = 0, nh = 0;
            if (inst.Editor == IntPtr.Zero || edGetSize(inst.Editor, &nw, &nh) == 0 || nw == 0 || nh == 0) return 0;
            // keep the editor's aspect ratio: fit inside what the host offers
            double s = Math.Min(*w / (double)nw, *h / (double)nh);
            if (s <= 0) return 0;
            *w = (uint)Math.Max(1, Math.Round(nw * s)); *h = (uint)Math.Max(1, Math.Round(nh * s));
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte GuiSetSize(ClapPlugin* p, uint w, uint h) { var inst = Inst(p); return inst.Editor != IntPtr.Zero ? (byte)edSetSize(inst.Editor, w, h) : (byte)0; }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte GuiSetParent(ClapPlugin* p, ClapWindow* w)
        {
            var inst = Inst(p);
            if (inst.Editor == IntPtr.Zero || w == null) return 0;
            int api = Eq(w->Api, apiWin32) ? 1 : Eq(w->Api, apiCocoa) ? 2 : 3;
            return (byte)edSetParent(inst.Editor, api, w->Handle);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte GuiSetTransient(ClapPlugin* p, ClapWindow* w) => 0;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void GuiSuggestTitle(ClapPlugin* p, byte* t) { }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte GuiShow(ClapPlugin* p) { var inst = Inst(p); if (inst.Editor != IntPtr.Zero) edShow(inst.Editor); return 1; }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static byte GuiHide(ClapPlugin* p) { var inst = Inst(p); if (inst.Editor != IntPtr.Zero) edHide(inst.Editor); return 1; }

        // ---------------------------------------------------------------- the tests' extension ("neo.export.stats/1"): GC figures of both runtimes
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void StatsGet(ClapPlugin* p, NeoStats* o)
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

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void StatsInproc(ClapPlugin* p, int mode) => LoadGen.Start(mode);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int StatsGcConfig(byte* buf, int cap) => LoadGen.Describe(buf, cap);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int StatsEditorOpen(ClapPlugin* p, int load, int edits) => OpenEditor(Inst(p));
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void StatsEditorClose(ClapPlugin* p) => CloseEditor(Inst(p));
    }
}
