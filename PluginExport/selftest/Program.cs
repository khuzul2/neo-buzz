using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NeoBuzz.Dsp;

namespace NeoExport
{
    static unsafe class Program
    {
        static int failures;
        static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) failures++; }

        // ---- a host's event list, output list and streams, in native memory
        const int MaxEv = 40;
        static byte* evMem;              // MaxEv slots of 64 bytes
        static int evCount;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })] static uint EvSize(ClapInputEvents* l) => (uint)evCount;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })] static ClapEventHeader* EvGet(ClapInputEvents* l, uint i) => (ClapEventHeader*)(evMem + 64 * i);
        static readonly List<(int Type, uint Id, double Value)> outEv = new List<(int, uint, double)>();
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        static byte OutPush(ClapOutputEvents* l, ClapEventHeader* h)
        {
            uint id = h->Type == 5 ? ((ClapEventParamValue*)h)->ParamId : ((ClapEventParamGesture*)h)->ParamId;
            outEv.Add((h->Type, id, h->Type == 5 ? ((ClapEventParamValue*)h)->Value : 0));
            return 1;
        }
        static byte[] saved = new byte[65536]; static int savedLen, readPos;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })] static long StWrite(ClapOStream* s, void* b, ulong n) { for (ulong i = 0; i < n; i++) saved[savedLen++] = ((byte*)b)[i]; return (long)n; }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })] static long StRead(ClapIStream* s, void* b, ulong n) { long k = 0; while ((ulong)k < n && readPos < savedLen) { ((byte*)b)[k++] = saved[readPos++]; } return k; }

        // test events: kind 0 param (a = id, v = raw), 1 note on (a = key, v = velocity), 2 note off (a = key)
        struct Ev { public int Time, Kind, A; public double V; }

        static int Main()
        {
            if (Environment.GetEnvironmentVariable("NEO_TIME_TABLES") == "1")
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!new NeoBuzz.DyeEssence.DyeEngine(new ParamValues(NeoBuzz.DyeEssence.Params.Table)).TablesBuilding == false) { }
                Console.WriteLine($"tables: {sw.ElapsedMilliseconds} ms"); return 0;
            }
            Check(sizeof(ClapPlugin) == 96 && sizeof(ClapDescriptor) == 88 && sizeof(ClapFactory) == 24, "plugin, descriptor, factory sizes");
            Check(sizeof(ClapEventHeader) == 16 && sizeof(ClapEventParamValue) == 56 && sizeof(ClapEventTransport) == 104 && sizeof(ClapEventNote) == 40 && sizeof(ClapEventParamGesture) == 20 && sizeof(ClapEventMidi) == 24, "event sizes");
            Check(sizeof(ClapProcess) == 64 && sizeof(ClapAudioBuffer) == 32 && sizeof(ClapInputEvents) == 24 && sizeof(ClapOutputEvents) == 16, "process, buffer, event list sizes");
            Check(sizeof(ClapAudioPortInfo) == 288 && sizeof(ClapParamInfo) == 1320 && sizeof(ClapPluginParams) == 48 && sizeof(ClapPluginGui) == 120 && sizeof(ClapNotePortInfo) == 268, "port, param info, params, gui, note port sizes");
            Check(sizeof(ClapHost) == 88 && sizeof(ClapResizeHints) == 12 && sizeof(ClapHostTimerSupport) == 16, "host, resize hints, timer sizes");
            Check((int)Marshal.OffsetOf<ClapEventParamValue>("Value") == 48 && (int)Marshal.OffsetOf<ClapEventNote>("Velocity") == 32 && (int)Marshal.OffsetOf<ClapEventTransport>("Tempo") == 40 && (int)Marshal.OffsetOf<ClapProcess>("InEvents") == 48, "field offsets");

            evMem = (byte*)NativeMemory.AllocZeroed(64 * MaxEv);
            var dir = Marshal.StringToCoTaskMemUTF8("/nonexistent");
            delegate* unmanaged[Cdecl]<byte*, byte> init = &ClapExports.EntryInit;
            Check(init((byte*)dir) == 1, "entry init");
            delegate* unmanaged[Cdecl]<byte*, void*> getFactory = &ClapExports.EntryGetFactory;
            var fac = (ClapFactory*)getFactory((byte*)Marshal.StringToCoTaskMemUTF8("clap.plugin-factory"));
            Check(fac != null && fac->Count(fac) == (uint)Registry.All.Length && Registry.All.Length >= 6, $"factory lists {Registry.All.Length} plug-ins");
            for (int i = 0; i < Registry.All.Length; i++) TestPlugin(fac, i);
            Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        static void* Ext(ClapPlugin* p, string id) => p->GetExtension(p, (byte*)Marshal.StringToCoTaskMemUTF8(id));

        static void TestPlugin(ClapFactory* fac, int which)
        {
            var info = Registry.All[which];
            Console.WriteLine($"--- {info.Name}");
            var d = fac->Descriptor(fac, (uint)which);
            Check(Marshal.PtrToStringUTF8((IntPtr)d->Name) == info.Name && (info.Name.StartsWith("Neo ") || info.Name == "Dye Essence"), "descriptor name is a Neo name or Dye Essence");
            var p = fac->Create(fac, null, d->Id);
            Check(p != null && p->Init(p) == 1 && p->Activate(p, 48000, 1, 256) == 1, "create, init, activate");

            // reference: the engine on its own through the same adapter
            var refEng = info.Make(); var table = refEng.Table; var refValues = new ParamValues(table); refEng.Create(refValues); refEng.SetSampleRate(48000);
            { var sw = System.Diagnostics.Stopwatch.StartNew(); var pinst = System.Runtime.CompilerServices.Unsafe.As<Instance>(GCHandle.FromIntPtr((IntPtr)p->PluginData).Target); while ((pinst.Eng.Preparing || refEng.Preparing) && sw.ElapsedMilliseconds < 30000) System.Threading.Thread.Sleep(10); Check(!pinst.Eng.Preparing && !refEng.Preparing, "tables built (" + sw.ElapsedMilliseconds + " ms after activation)"); }

            var pe = (ClapPluginParams*)Ext(p, "clap.params");
            Check(pe != null && pe->Count(p) == (uint)table.Length, $"{table.Length} parameters match the machine's table");
            var pinfo = (ClapParamInfo*)NativeMemory.AllocZeroed((nuint)sizeof(ClapParamInfo));
            bool infosOk = true, roundTrip = true; int units = 0;
            byte* txt = (byte*)NativeMemory.AllocZeroed(256);
            for (uint i = 0; i < pe->Count(p); i++)
            {
                bool one = pe->GetInfo(p, i, pinfo) == 1 && pinfo->Id == i && pinfo->Max >= pinfo->Min && pinfo->Default >= pinfo->Min && pinfo->Default <= pinfo->Max
                           && Marshal.PtrToStringUTF8((IntPtr)pinfo->Name) == table[i].Name;
                if (!one) Console.WriteLine($"     parameter {i}: id {pinfo->Id}, {pinfo->Min}..{pinfo->Max} def {pinfo->Default}, name '{Marshal.PtrToStringUTF8((IntPtr)pinfo->Name)}' vs '{table[i].Name}'");
                infosOk &= one;
                pe->ValueToText(p, i, pinfo->Default, txt, 256);
                string s = Marshal.PtrToStringUTF8((IntPtr)txt);
                if (s.Contains("Hz") || s.Contains("dB") || s.Contains("oct") || s.Contains("°") || s.Contains("%") || s.Contains("ms") || s.Contains(" s")) units++;
                // text_to_value inverts value_to_text (within a raw step) for a few values of each parameter
                foreach (double v in new[] { pinfo->Min, Math.Round((pinfo->Min + pinfo->Max) / 2), pinfo->Max })
                {
                    pe->ValueToText(p, i, v, txt, 256);
                    double back;
                    if (pe->TextToValue(p, i, txt, &back) == 0) { if (table[i].Kind != MapKind.Enum) { roundTrip = false; Console.WriteLine($"     no text_to_value for {table[i].Name} '{Marshal.PtrToStringUTF8((IntPtr)txt)}'"); } continue; }
                    // the text may name the same value for neighbouring raws (rounding): compare texts
                    byte* t2 = (byte*)NativeMemory.AllocZeroed(256);
                    pe->ValueToText(p, i, back, t2, 256);
                    if (Marshal.PtrToStringUTF8((IntPtr)t2) != Marshal.PtrToStringUTF8((IntPtr)txt)) { roundTrip = false; Console.WriteLine($"     {table[i].Name}: '{Marshal.PtrToStringUTF8((IntPtr)txt)}' -> {back} -> '{Marshal.PtrToStringUTF8((IntPtr)t2)}'"); }
                    NativeMemory.Free(t2);
                }
            }
            Check(infosOk, "parameter infos are consistent (ids, ranges, names)");
            Check(roundTrip, "value_to_text / text_to_value round trip");
            Check(units >= 1, $"{units} parameters print a unit at their default");

            var ap = (ClapPluginAudioPorts*)Ext(p, "clap.audio-ports");
            var pi = (ClapAudioPortInfo*)NativeMemory.AllocZeroed((nuint)sizeof(ClapAudioPortInfo));
            uint wantIn = info.Instrument ? 0u : info.Sidechain ? 2u : 1u;
            Check(ap->Count(p, 1) == wantIn && ap->Count(p, 0) == 1 && ap->Get(p, 0, 0, pi) == 1 && pi->ChannelCount == 2, $"audio ports: {wantIn} in, 1 out");
            var np = (ClapPluginNotePorts*)Ext(p, "clap.note-ports");
            Check((np != null) == info.Instrument && (np == null || np->Count(p, 1) == 1), info.Instrument ? "one note input port" : "no note ports");
            var lat = (ClapPluginLatency*)Ext(p, "clap.latency"); var tail = (ClapPluginTail*)Ext(p, "clap.tail");
            Check(lat->Get(p) == (uint)refEng.Latency && tail->Get(p) >= lat->Get(p), $"latency {lat->Get(p)}, tail {tail->Get(p)} samples");

            // ---- a render with sample-accurate events against the engine used directly
            const int N = 480;
            float* inL = (float*)NativeMemory.AllocZeroed(N * 4), inR = (float*)NativeMemory.AllocZeroed(N * 4), kL = (float*)NativeMemory.AllocZeroed(N * 4), kR = (float*)NativeMemory.AllocZeroed(N * 4);
            float* oL = (float*)NativeMemory.AllocZeroed(N * 4), oR = (float*)NativeMemory.AllocZeroed(N * 4);
            float** inP = (float**)NativeMemory.AllocZeroed(32), outP = (float**)NativeMemory.AllocZeroed(16);
            inP[0] = inL; inP[1] = inR; outP[0] = oL; outP[1] = oR;
            float** skP = (float**)NativeMemory.AllocZeroed(16); skP[0] = kL; skP[1] = kR;
            var ibs = (ClapAudioBuffer*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(ClapAudioBuffer)));
            ibs[0] = new ClapAudioBuffer { Data32 = inP, ChannelCount = 2 }; ibs[1] = new ClapAudioBuffer { Data32 = skP, ChannelCount = 2 };
            var ob = new ClapAudioBuffer { Data32 = outP, ChannelCount = 2 };
            var list = new ClapInputEvents { Size = &EvSize, Get = &EvGet };
            var outList = new ClapOutputEvents { TryPush = &OutPush };
            var pr = new ClapProcess { FramesCount = N, AudioInputs = ibs, AudioOutputs = &ob, AudioInputsCount = wantIn, AudioOutputsCount = 1, InEvents = &list, OutEvents = &outList };

            var rnd = new Random(7);
            var rL = new float[refEng.MaxBlock]; var rR = new float[refEng.MaxBlock]; var rKL = new float[refEng.MaxBlock]; var rKR = new float[refEng.MaxBlock];
            var refOutL = new float[N]; var refOutR = new float[N];
            bool identical = true, audible = false; long allocBefore = 0, allocAfter = 0;
            int pidx = Math.Min(2, table.Length - 1);   // a parameter to move: index 2
            const int Blocks = 400;
            for (int blk = 0; blk < Blocks; blk++)
            {
                var evs = new List<Ev>();
                if (info.Instrument)
                {
                    if (blk % 20 == 0) { evs.Add(new Ev { Time = 100, Kind = 1, A = 48 + (blk / 20) % 12, V = 0.8 }); evs.Add(new Ev { Time = 101, Kind = 1, A = 55 + (blk / 20) % 5, V = 0.5 }); }
                    if (blk % 20 == 10) { evs.Add(new Ev { Time = 300, Kind = 2, A = 48 + (blk / 20) % 12 }); evs.Add(new Ev { Time = 301, Kind = 2, A = 55 + (blk / 20) % 5 }); }
                }
                if (blk % 3 == 0)
                {
                    var t = table[pidx];
                    evs.Add(new Ev { Time = 100 + blk % 200, Kind = 0, A = pidx, V = t.Min + (t.Max - t.Min) * ((blk * 37) % 100) / 100 });
                }
                evs.Sort((a, b) => a.Time.CompareTo(b.Time));
                evCount = evs.Count;
                for (int i = 0; i < evs.Count; i++) WriteEvent(evMem + 64 * i, evs[i]);
                for (int i = 0; i < N; i++)
                {
                    inL[i] = info.Instrument ? 0 : (float)(rnd.NextDouble() * 2 - 1) * 0.5f; inR[i] = info.Instrument ? 0 : (float)(rnd.NextDouble() * 2 - 1) * 0.5f;
                    kL[i] = (float)(rnd.NextDouble() * 2 - 1) * 0.3f; kR[i] = (float)(rnd.NextDouble() * 2 - 1) * 0.3f;
                }
                                Reference(info, refEng, refValues, evs, inL, inR, kL, kR, N, rL, rR, rKL, rKR, refOutL, refOutR, wantIn == 2);
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                int status = p->Process(p, &pr);
                if (blk >= 100) allocAfter += GC.GetAllocatedBytesForCurrentThread() - a0;
                for (int i = 0; i < N; i++)
                {
                    identical &= BitConverter.SingleToInt32Bits(oL[i]) == BitConverter.SingleToInt32Bits(refOutL[i]) && BitConverter.SingleToInt32Bits(oR[i]) == BitConverter.SingleToInt32Bits(refOutR[i]);
                    if (Math.Abs(oL[i]) > 1e-4f) audible = true;
                }
                if (status != 1) identical = false;
            }
            Check(identical, $"{Blocks} blocks with parameter{(info.Instrument ? " and note" : "")} events: bit-identical to the engine used directly");
            Check(audible, "the render is audible");
            Check(allocAfter - allocBefore == 0, $"process allocates nothing over 300 blocks ({allocAfter - allocBefore} bytes)");

            // ---- real notes: velocity, a chord, sixteen notes, and a note-off that ends only its own note (instruments only)
            if (info.Instrument)
            {
                bool drums = info.Name.Contains("Drums");
                int[] chord = drums ? new[] { 36, 38, 42, 46 } : new[] { 60, 64, 67, 72 };
                var sixteen = new int[16]; for (int i = 0; i < 16; i++) sixteen[i] = drums ? 36 + i : 48 + 2 * i;
                const int Win = 30;   // 0.3 s
                double Peak(double[] pk) { double m = 0; for (int i = 0; i < Win && i < pk.Length; i++) m = Math.Max(m, pk[i]); return m; }
                // Neo FM6's Init patch has velocity sensitivity 0 (its presets set it): give operator 1 the full 7 first
                bool fm6 = info.Name == "Neo FM6"; int sensId = fm6 ? Array.FindIndex(table, t => t.Name == "Op1 vel sens") : -1;
                var pre = new List<(int, Ev)>(); if (fm6) pre.Add((0, new Ev { Time = 0, Kind = 0, A = sensId, V = 7 }));
                // Neo Learn Physical: the mallet pulse has spectral nulls, so its level is not monotone in velocity; 127 must still beat 30
                bool notMono = info.Name == "Neo Learn Physical"; double minSpan = notMono ? 1.0 : 6.0;
                var lv = new double[4]; int[] vels = { 30, 60, 90, 127 };
                for (int k = 0; k < 4; k++)
                {
                    var e = new List<(int, Ev)>(pre); foreach (int key in chord) e.Add((0, new Ev { Time = 0, Kind = 1, A = key, V = vels[k] / 127.0 }));
                    lv[k] = Peak(Render(fac, d, &pr, oL, oR, e, Win).peak);
                }
                double dbSpan = 20 * Math.Log10(lv[3] / Math.Max(lv[0], 1e-9));
                Console.WriteLine($"     chord peaks at velocity 30/60/90/127: {lv[0]:F4} {lv[1]:F4} {lv[2]:F4} {lv[3]:F4} ({dbSpan:F1} dB from 30 to 127)");
                Check(lv[0] > 1e-5 && dbSpan >= minSpan, notMono ? "a four-note chord is louder at velocity 127 than at 30 (not monotone in between)" : "a four-note chord is 6 dB or more louder at velocity 127 than at 30");
                var many = new List<(int, Ev)>(pre); foreach (int key in sixteen) many.Add((0, new Ev { Time = 0, Kind = 1, A = key, V = 0.7 }));
                var mr = Render(fac, d, &pr, oL, oR, many, Win);
                Check(Peak(mr.peak) > 1e-5 && double.IsFinite(Peak(mr.peak)) && Peak(mr.peak) < 16, "sixteen notes at once sound and stay finite");
                // note A and B held, A released: late level matches B alone (A's voice ended, B's did not)
                int ka = chord[0], kb = chord[1];
                var both = new List<(int, Ev)>(pre) { (0, new Ev { Kind = 1, A = ka, V = 0.8 }), (0, new Ev { Kind = 1, A = kb, V = 0.8 }), (10, new Ev { Kind = 2, A = ka }) };
                var only = new List<(int, Ev)>(pre) { (0, new Ev { Kind = 1, A = kb, V = 0.8 }) };
                var held = new List<(int, Ev)>(pre) { (0, new Ev { Kind = 1, A = ka, V = 0.8 }), (0, new Ev { Kind = 1, A = kb, V = 0.8 }) };
                var rb = Render(fac, d, &pr, oL, oR, both, 300).rms; var ro = Render(fac, d, &pr, oL, oR, only, 300).rms; var rh = Render(fac, d, &pr, oL, oR, held, 300).rms;
                double Late(double[] r) { double s2 = 0; for (int i = 240; i < 300; i++) s2 += r[i] * r[i]; return Math.Sqrt(s2 / 60); }
                double dbBO = 20 * Math.Log10(Math.Max(Late(rb), 1e-9) / Math.Max(Late(ro), 1e-9)), dbBH = 20 * Math.Log10(Math.Max(Late(rb), 1e-9) / Math.Max(Late(rh), 1e-9));
                Console.WriteLine($"     late level: A off + B vs B alone {dbBO:F1} dB, vs both held {dbBH:F1} dB (late rms {Late(rb):E2} / {Late(ro):E2} / {Late(rh):E2})");
                bool oneShot = drums || Late(rh) < 1e-3;   // a one-shot, or a plucked sound that has decayed below -60 dB by then, has no held level to compare
                Check(oneShot || (Math.Abs(dbBO) <= 3.0 && dbBH < dbBO), "a note-off ends only its own note");
            }

            // ---- state: round trip, an older (shorter) state, a bad header
            var stext = (ClapPluginState*)Ext(p, "clap.state");
            var os = new ClapOStream { Write = &StWrite }; var ist = new ClapIStream { Read = &StRead };
            savedLen = 0; readPos = 0;
            Check(stext->Save(p, &os) == 1 && savedLen == (table.Length + 4) * 4, $"state saved ({savedLen} bytes = 4-int header + {table.Length} values)");
            var p2 = fac->Create(fac, null, d->Id); p2->Activate(p2, 48000, 1, 256);
            Check(stext->Load(p2, &ist) == 1, "state loaded into a second instance");
            var pe2 = (ClapPluginParams*)Ext(p2, "clap.params");
            double a, b; bool same = true;
            for (uint i = 0; i < pe->Count(p); i++) { pe->GetValue(p, i, &a); pe2->GetValue(p2, i, &b); same &= a == b; }
            Check(same, "all parameter values came back");
            // an older state: the first half of the values; the rest take their defaults
            int half = Math.Max(1, table.Length / 2);
            var hdr = new byte[16 + half * 4]; Array.Copy(saved, hdr, hdr.Length); BitConverter.GetBytes(half).CopyTo(hdr, 12);
            saved = hdr; savedLen = hdr.Length; readPos = 0;
            var p3 = fac->Create(fac, null, d->Id);
            Check(stext->Load(p3, &ist) == 1, "a state with fewer parameters loads");
            var pe3 = (ClapPluginParams*)Ext(p3, "clap.params");
            pe3->GetValue(p3, (uint)(table.Length - 1), &b);
            Check(b == table[table.Length - 1].Default, "the parameters it lacks take their defaults");
            saved = new byte[65536]; savedLen = 8; readPos = 0;
            Check(stext->Load(p3, &ist) == 0, "a state with a bad header is refused");

            // ---- the editor's side: an edit through the ring reaches the engine, and goes to the host as gesture, value, gesture
            var inst = System.Runtime.CompilerServices.Unsafe.As<Instance>(GCHandle.FromIntPtr((IntPtr)p->PluginData).Target);
            int target = Math.Min(1, table.Length - 1);
            long edits0 = inst.Sh->EditsApplied;
            Shared.Post(inst.Sh, Shared.EditBegin, target, 0); Shared.Post(inst.Sh, Shared.EditValue, target, table[target].Max); Shared.Post(inst.Sh, Shared.EditEnd, target, 0);
            outEv.Clear(); evCount = 0;
            p->Process(p, &pr);
            pe->GetValue(p, (uint)target, &a);
            Check(a == table[target].Max && inst.Sh->Param[target] == table[target].Max && inst.Sh->EditsApplied - edits0 == 3, "an editor edit through the ring reaches the audio side");
            Check(outEv.Count == 3 && outEv[0].Type == 7 && outEv[1].Type == 5 && outEv[1].Value == table[target].Max && outEv[2].Type == 8 && outEv[1].Id == (uint)target, "the host gets gesture begin, value, gesture end");
            // host automation reaches the shared struct for the editor
            evCount = 1; WriteEvent(evMem, new Ev { Time = 0, Kind = 0, A = target, V = table[target].Min });
            int seq = inst.Sh->ParamSeq; p->Process(p, &pr);
            Check(inst.Sh->Param[target] == table[target].Min && inst.Sh->ParamSeq != seq, "host automation reaches the editor through the shared struct");

            p->Destroy(p); p2->Destroy(p2); p3->Destroy(p3);
        }

        /// <summary>Plays the events (block, event) on a fresh instance and returns each block's rms and peak over both channels.</summary>
        static (double[] rms, double[] peak) Render(ClapFactory* fac, ClapDescriptor* d, ClapProcess* pr, float* oL, float* oR, List<(int blk, Ev ev)> evs, int blocks)
        {
            var q = fac->Create(fac, null, d->Id); q->Init(q); q->Activate(q, 48000, 1, 256);
            var inst = System.Runtime.CompilerServices.Unsafe.As<Instance>(GCHandle.FromIntPtr((IntPtr)q->PluginData).Target);
            var sw = System.Diagnostics.Stopwatch.StartNew(); while (inst.Eng.Preparing && sw.ElapsedMilliseconds < 30000) System.Threading.Thread.Sleep(10);
            var rms = new double[blocks]; var peak = new double[blocks];
            for (int b = 0; b < blocks; b++)
            {
                int n = 0; foreach (var (blk, ev) in evs) if (blk == b) { WriteEvent(evMem + 64 * n, ev); n++; }
                evCount = n; q->Process(q, pr);
                double s2 = 0, pk = 0; int N = (int)pr->FramesCount;
                for (int i = 0; i < N; i++) { s2 += (double)oL[i] * oL[i] + (double)oR[i] * oR[i]; pk = Math.Max(pk, Math.Max(Math.Abs(oL[i]), Math.Abs(oR[i]))); }
                rms[b] = Math.Sqrt(s2 / (2 * N)); peak[b] = pk;
            }
            evCount = 0; q->Destroy(q);
            return (rms, peak);
        }

        static void WriteEvent(byte* m, Ev e)
        {
            new Span<byte>(m, 64).Clear();
            if (e.Kind == 0)
            {
                var pv = (ClapEventParamValue*)m;
                pv->Header = new ClapEventHeader { Size = (uint)sizeof(ClapEventParamValue), Time = (uint)e.Time, SpaceId = 0, Type = 5 };
                pv->ParamId = (uint)e.A; pv->Value = e.V; pv->NoteId = -1; pv->PortIndex = -1; pv->Channel = -1; pv->Key = -1;
            }
            else
            {
                var nv = (ClapEventNote*)m;
                nv->Header = new ClapEventHeader { Size = (uint)sizeof(ClapEventNote), Time = (uint)e.Time, SpaceId = 0, Type = (ushort)(e.Kind == 1 ? 0 : 1) };
                nv->NoteId = -1; nv->PortIndex = 0; nv->Channel = 0; nv->Key = (short)e.A; nv->Velocity = e.V;
            }
        }

        /// <summary>What the plug-in must produce: the engine driven the way the runtime drives it (chunks split at events and at MaxBlock).</summary>
        static void Reference(PluginInfo info, ExportEngine e, ParamValues v, List<Ev> evs, float* inL, float* inR, float* kL, float* kR, int n, float[] L, float[] R, float[] KL, float[] KR, float[] outL, float[] outR, bool key)
        {
            int pos = 0, ei = 0;
            while (pos < n)
            {
                while (ei < evs.Count && evs[ei].Time <= pos) { ApplyRef(e, v, evs[ei]); ei++; }
                int next = n; if (ei < evs.Count && evs[ei].Time < next) next = evs[ei].Time;
                int chunk = Math.Min(next - pos, e.MaxBlock);
                float peak = 0;
                for (int i = 0; i < chunk; i++) { L[i] = info.Instrument ? 0 : inL[pos + i]; R[i] = info.Instrument ? 0 : inR[pos + i]; peak = Math.Max(peak, Math.Max(Math.Abs(L[i]), Math.Abs(R[i]))); }
                if (info.GateIdle && peak <= 9.2e-7f && e.Idle) { Array.Clear(L, 0, chunk); Array.Clear(R, 0, chunk); }
                else
                {
                    if (key) for (int i = 0; i < chunk; i++) { KL[i] = kL[pos + i]; KR[i] = kR[pos + i]; }
                    if (!e.Process(L, R, key ? KL : null, key ? KR : null, chunk)) { Array.Clear(L, 0, chunk); Array.Clear(R, 0, chunk); }
                }
                for (int i = 0; i < chunk; i++) { outL[pos + i] = float.IsFinite(L[i]) ? L[i] : 0; outR[pos + i] = float.IsFinite(R[i]) ? R[i] : 0; }
                pos += chunk;
            }
            while (ei < evs.Count) { ApplyRef(e, v, evs[ei]); ei++; }
        }

        static void ApplyRef(ExportEngine e, ParamValues v, Ev ev)
        {
            if (ev.Kind == 0) v.Set(ev.A, (int)Math.Round(ev.V));
            else if (ev.Kind == 1) e.NoteOn(1000 + ev.A, ev.A, Math.Clamp((int)Math.Round(ev.V * 127), 1, 127) / 127f);
            else e.NoteOff(1000 + ev.A);
        }
    }
}
