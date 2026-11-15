using System;
using System.Runtime.InteropServices;
using NeoBuzz.Dsp;
using NeoBuzz.Filter;

namespace NeoExport
{
    static unsafe class Program
    {
        static int failures;
        static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) failures++; }

        // A host's event list and stream, in native memory.
        static ClapEventParamValue[] events = new ClapEventParamValue[4];
        static int evCount;
        static GCHandle evPin;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })] static uint EvSize(ClapInputEvents* l) => (uint)evCount;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })] static ClapEventHeader* EvGet(ClapInputEvents* l, uint i) => (ClapEventHeader*)(evMem + i);
        static byte[] saved = new byte[4096]; static int savedLen, readPos;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })] static long StWrite(ClapOStream* s, void* b, ulong n) { for (ulong i = 0; i < n; i++) saved[savedLen++] = ((byte*)b)[i]; return (long)n; }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })] static long StRead(ClapIStream* s, void* b, ulong n) { long k = 0; while ((ulong)k < n && readPos < savedLen) { ((byte*)b)[k++] = saved[readPos++]; } return k; }

        static ClapEventParamValue* evMem;

        static int Main()
        {
            // layouts against the C headers (x86-64 and arm64 LP64/LLP64 agree: no long/size_t members)
            Check(sizeof(ClapPlugin) == 96 && sizeof(ClapDescriptor) == 88 && sizeof(ClapFactory) == 24, "plugin, descriptor, factory sizes");
            Check(sizeof(ClapEventHeader) == 16 && sizeof(ClapEventParamValue) == 56 && sizeof(ClapEventTransport) == 104, "event sizes");
            Check(sizeof(ClapProcess) == 64 && sizeof(ClapAudioBuffer) == 32 && sizeof(ClapInputEvents) == 24 && sizeof(ClapOutputEvents) == 16, "process, buffer, event list sizes");
            Check(sizeof(ClapAudioPortInfo) == 288 && sizeof(ClapParamInfo) == 1320 && sizeof(ClapPluginParams) == 48 && sizeof(ClapPluginGui) == 120, "port, param info, params, gui sizes");
            Check((int)Marshal.OffsetOf<ClapEventParamValue>("Value") == 48 && (int)Marshal.OffsetOf<ClapEventTransport>("Tempo") == 40 && (int)Marshal.OffsetOf<ClapProcess>("InEvents") == 48, "field offsets");

            var dir = Marshal.StringToCoTaskMemUTF8("/nonexistent");
            Check(ClapExports_EntryInit((byte*)dir) == 1, "entry init");
            var fid = Marshal.StringToCoTaskMemUTF8("clap.plugin-factory");
            var fac = (ClapFactory*)ClapExports_GetFactory((byte*)fid);
            Check(fac != null && fac->Count(fac) == 1, "factory with one plug-in");
            var d = fac->Descriptor(fac, 0);
            Check(Marshal.PtrToStringUTF8((IntPtr)d->Name) == "Neo Filter", "descriptor name");
            var p = fac->Create(fac, null, d->Id);
            Check(p != null && p->Init(p) == 1, "create + init");
            Check(p->Activate(p, 48000, 1, 256) == 1, "activate");

            var ex = Marshal.StringToCoTaskMemUTF8("clap.params");
            var pe = (ClapPluginParams*)p->GetExtension(p, (byte*)ex);
            Check(pe != null && pe->Count(p) == (uint)Params.Table.Length, $"{Params.Table.Length} parameters");
            var info = (ClapParamInfo*)NativeMemory.AllocZeroed((nuint)sizeof(ClapParamInfo));
            bool infosOk = true; int units = 0;
            byte* txt = (byte*)NativeMemory.AllocZeroed(256);
            for (uint i = 0; i < pe->Count(p); i++)
            {
                infosOk &= pe->GetInfo(p, i, info) == 1 && info->Id == i && info->Max > info->Min && info->Default >= info->Min && info->Default <= info->Max;
                pe->ValueToText(p, i, info->Default, txt, 256);
                string s = Marshal.PtrToStringUTF8((IntPtr)txt);
                if (s.Contains("Hz") || s.Contains("dB") || s.Contains("oct") || s.Contains("°") || s.Contains("%") || s.Contains("ms") || s.Contains(" s")) units++;
            }
            Check(infosOk, "parameter infos are consistent");
            Check(units >= 8, $"{units} parameters print a unit");
            pe->GetInfo(p, 2, info); pe->ValueToText(p, 2, info->Default, txt, 256);
            Console.WriteLine("     Cutoff default: " + Marshal.PtrToStringUTF8((IntPtr)txt));

            var ap = (ClapPluginAudioPorts*)p->GetExtension(p, (byte*)Marshal.StringToCoTaskMemUTF8("clap.audio-ports"));
            var pi = (ClapAudioPortInfo*)NativeMemory.AllocZeroed((nuint)sizeof(ClapAudioPortInfo));
            Check(ap->Count(p, 1) == 1 && ap->Count(p, 0) == 1 && ap->Get(p, 0, 1, pi) == 1 && pi->ChannelCount == 2, "one stereo port each way");

            // reference: the engine on its own, same blocks, same events
            var refValues = new ParamValues(Params.Table);
            var refEng = new FilterEngine(refValues); refEng.SetSampleRate(48000);
            var rL = new float[256]; var rR = new float[256];

            const int N = 480;
            float* inL = (float*)NativeMemory.AllocZeroed(N * 4), inR = (float*)NativeMemory.AllocZeroed(N * 4), oL = (float*)NativeMemory.AllocZeroed(N * 4), oR = (float*)NativeMemory.AllocZeroed(N * 4);
            float** inP = (float**)NativeMemory.AllocZeroed(16), outP = (float**)NativeMemory.AllocZeroed(16);
            inP[0] = inL; inP[1] = inR; outP[0] = oL; outP[1] = oR;
            var ib = new ClapAudioBuffer { Data32 = inP, ChannelCount = 2 };
            var ob = new ClapAudioBuffer { Data32 = outP, ChannelCount = 2 };
            evMem = (ClapEventParamValue*)NativeMemory.AllocZeroed((nuint)(sizeof(ClapEventParamValue) * 4));
            var list = new ClapInputEvents { Size = &EvSize, Get = &EvGet };
            var pr = new ClapProcess { FramesCount = N, AudioInputs = &ib, AudioOutputs = &ob, AudioInputsCount = 1, AudioOutputsCount = 1, InEvents = &list };

            var rnd = new Random(7);
            bool identical = true; long allocBefore = 0, allocAfter = 0;
            for (int blk = 0; blk < 400; blk++)
            {
                for (int i = 0; i < N; i++) { inL[i] = (float)(rnd.NextDouble() * 2 - 1) * 0.5f; inR[i] = (float)(rnd.NextDouble() * 2 - 1) * 0.5f; }
                evCount = 0;
                int cut = 3000 + (blk * 37) % 6000, evAt = 100 + blk % 200;
                if (blk % 3 == 0)
                {
                    evMem[0] = new ClapEventParamValue { Header = new ClapEventHeader { Size = (uint)sizeof(ClapEventParamValue), Time = (uint)evAt, SpaceId = 0, Type = 5 }, ParamId = (uint)P.Cutoff, Value = cut, NoteId = -1, PortIndex = -1, Channel = -1, Key = -1 };
                    evCount = 1;
                }
                if (blk == 1) allocBefore = GC.GetAllocatedBytesForCurrentThread();
                // reference: events split the block at their time; chunks of at most MaxBlock (the plug-in does the same)
                int pos = 0;
                if (evCount == 0) { Run(refEng, rL, rR, inL, inR, ref pos, N, N); }
                else { Run(refEng, rL, rR, inL, inR, ref pos, evAt, N); refValues.Set(P.Cutoff, cut); Run(refEng, rL, rR, inL, inR, ref pos, N, N); }
                int status = p->Process(p, &pr);
                // the reference's output is in rL/rR per chunk; compare the whole block
                for (int i = 0; i < N; i++) identical &= BitConverter.SingleToInt32Bits(oL[i]) == BitConverter.SingleToInt32Bits(refOutL[i]) && BitConverter.SingleToInt32Bits(oR[i]) == BitConverter.SingleToInt32Bits(refOutR[i]);
                if (status != 1) identical = false;
                if (blk == 398) allocAfter = GC.GetAllocatedBytesForCurrentThread();
            }
            Check(identical, "400 blocks with parameter events: output bit-identical to the engine used directly");
            Check(allocAfter - allocBefore == 0, $"process allocates nothing over 397 blocks ({allocAfter - allocBefore} bytes)");

            // state round trip
            var stext = (ClapPluginState*)p->GetExtension(p, (byte*)Marshal.StringToCoTaskMemUTF8("clap.state"));
            var os = new ClapOStream { Write = &StWrite }; var ist = new ClapIStream { Read = &StRead };
            Check(stext->Save(p, &os) == 1 && savedLen == (Params.Table.Length + 2) * 4, $"state saved ({savedLen} bytes)");
            var p2 = fac->Create(fac, null, d->Id); p2->Activate(p2, 48000, 1, 256);
            Check(stext->Load(p2, &ist) == 1, "state loaded into a second instance");
            var pe2 = (ClapPluginParams*)p2->GetExtension(p2, (byte*)ex);
            double a, b; bool same = true;
            for (uint i = 0; i < pe->Count(p); i++) { pe->GetValue(p, i, &a); pe2->GetValue(p2, i, &b); same &= a == b; }
            Check(same && refValues[P.Cutoff] == (int)pe2GetValue(pe2, p2, P.Cutoff), "all parameter values came back");

            // the editor ring: an edit posted from the other side reaches the engine at the next block
            var inst = Unsafe_Inst(p);
            Shared.Post(inst.Sh, P.Output, 250);
            evCount = 0; p->Process(p, &pr);
            pe->GetValue(p, (uint)P.Output, &a);
            Check(a == 250 && inst.Sh->Param[P.Output] == 250 && inst.Sh->EditsApplied == 1, "an editor edit through the ring reaches the audio side");

            // The engine remembers how it got to its parameters: a filter that glided from the defaults to Cutoff 5000 / Resonance 500
            // and one that started there differ by ~1e-4 for good (the smoothers' resting values). This is why the host null test
            // judges a reloaded song, where only the native machine glides, against a looser tolerance than a settled start.
            {
                var vA = new ParamValues(Params.Table); var eA = new FilterEngine(vA); eA.SetSampleRate(44100);     // glides
                var vB = new ParamValues(Params.Table); vB.Set(P.Type, 2); vB.Set(P.Cutoff, 5000); vB.Set(P.Resonance, 500);
                var eB = new FilterEngine(vB); eB.SetSampleRate(44100);                                              // settled
                vA.Set(P.Type, 2); vA.Set(P.Cutoff, 5000); vA.Set(P.Resonance, 500);
                var aL = new float[256]; var aR = new float[256]; var bL = new float[256]; var bR = new float[256];
                var rr = new Random(3); double worstLate = 0; double worstEarly = 0;
                for (int blk = 0; blk < 44100 * 3 / 256; blk++)
                {
                    for (int i = 0; i < 256; i++) { aL[i] = bL[i] = (float)(rr.NextDouble() - 0.5); aR[i] = bR[i] = (float)(rr.NextDouble() - 0.5); }
                    eA.Process(aL, aR, 256); eB.Process(bL, bR, 256);
                    double m = 0; for (int i = 0; i < 256; i++) m = Math.Max(m, Math.Abs(aL[i] - bL[i]));
                    if (blk * 256 < 22050) worstEarly = Math.Max(worstEarly, m); else worstLate = Math.Max(worstLate, m);
                }
                Console.WriteLine($"     glided vs settled engine: largest difference in the first 0.5 s {worstEarly:g3}, after it {worstLate:g3}");
            }

            p->Destroy(p); p2->Destroy(p2);
            Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        static float[] refOutL = new float[480], refOutR = new float[480];
        static void Run(FilterEngine e, float[] L, float[] R, float* inL, float* inR, ref int pos, int end, int total)
        {
            while (pos < end)
            {
                int chunk = Math.Min(end - pos, FilterEngine.MaxBlock);
                for (int i = 0; i < chunk; i++) { L[i] = inL[pos + i]; R[i] = inR[pos + i]; }
                e.Process(L, R, chunk);
                for (int i = 0; i < chunk; i++) { refOutL[pos + i] = L[i]; refOutR[pos + i] = R[i]; }
                pos += chunk;
            }
        }

        static double pe2GetValue(ClapPluginParams* pe, ClapPlugin* p, int id) { double v; pe->GetValue(p, (uint)id, &v); return v; }
        static Instance Unsafe_Inst(ClapPlugin* p) => System.Runtime.CompilerServices.Unsafe.As<Instance>(GCHandle.FromIntPtr((IntPtr)p->PluginData).Target);

        static byte ClapExports_EntryInit(byte* dir) { delegate* unmanaged[Cdecl]<byte*, byte> f = &ClapExports.EntryInit; return f(dir); }
        static void* ClapExports_GetFactory(byte* id) { delegate* unmanaged[Cdecl]<byte*, void*> f = &ClapExports.EntryGetFactory; return f(id); }
    }
}
