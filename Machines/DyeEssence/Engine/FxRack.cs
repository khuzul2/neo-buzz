#nullable disable
// Dye Essence's effects rack (docs/specs/dye-essence.md section 4.10): ten slots in three chains, after the bank.
//  - Trio: bus A and bus B take sends of the synth in parallel; the main chain runs on the sum.
//    Duo: insert 1 then insert 2 in series; the aux (chain 3) takes a send of that and is added back.
//  - A slot is Off or one of the types of FxTypes (the engines of six Neo machines). Which chain a slot is in and the
//    order within a chain are the machine state (FxRouting); its type, mix and ten generic parameters are parameters.
//  - Everything Off: the rack is not run at all, so the output is the bank's, bit for bit.
//  - Latency: a slot with a latency (the clipper's) delays its own dry signal by it; parallel paths are padded to the
//    longest, so the rack's latency is the same whatever the audio and every path stays in time (Latency).
//  - Engines are built off the audio thread (one background worker); until a slot's engine is ready the slot passes its
//    input (padded as if it ran). The audio thread allocates nothing: a type is built once per slot and kept.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    /// <summary>Which slots sit in which chain, and in what order (the machine state's FxRouting text: "1 2 3 4 | 5 6 7 | 8 9 10", slots counted from 1).</summary>
    internal sealed class FxRouting
    {
        public const int Slots = 10, Chains = 3;
        public readonly int[][] Chain;

        FxRouting(int[][] chain) { Chain = chain; }

        public static readonly FxRouting Default = new FxRouting(new[] { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6 }, new[] { 7, 8, 9 } });

        public bool IsDefault => ToString() == Default.ToString();

        /// <summary>A routing from slot lists (each slot exactly once), or null when they are not a permutation of the ten slots.</summary>
        public static FxRouting From(int[][] chain)
        {
            if (chain == null || chain.Length != Chains) return null;
            var seen = new bool[Slots];
            int total = 0;
            foreach (var c in chain)
            {
                if (c == null) return null;
                foreach (int s in c)
                {
                    if (s < 0 || s >= Slots || seen[s]) return null;
                    seen[s] = true;
                    total++;
                }
            }
            if (total != Slots) return null;
            var copy = new int[Chains][];
            for (int i = 0; i < Chains; i++) copy[i] = (int[])chain[i].Clone();
            return new FxRouting(copy);
        }

        /// <summary>Reads the text; anything that is not each of 1 ... 10 once in three groups gives the default.</summary>
        public static FxRouting Parse(string text) => TryParse(text, out var r) ? r : Default;

        /// <summary>Reads the text: false (and the default) when it is not each of 1 ... 10 once in three groups; empty text is the default.</summary>
        public static bool TryParse(string text, out FxRouting routing)
        {
            routing = Default;
            if (string.IsNullOrWhiteSpace(text)) return true;
            var groups = text.Split('|');
            if (groups.Length != Chains) return false;
            var chain = new int[Chains][];
            for (int g = 0; g < Chains; g++)
            {
                var list = new List<int>();
                foreach (var tok in groups[g].Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(tok, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int v)) return false;
                    list.Add(v - 1);
                }
                chain[g] = list.ToArray();
            }
            var r = From(chain);
            if (r == null) return false;
            routing = r;
            return true;
        }

        public override string ToString()
        {
            var parts = new string[Chains];
            for (int g = 0; g < Chains; g++)
            {
                var s = new string[Chain[g].Length];
                for (int i = 0; i < s.Length; i++) s[i] = (Chain[g][i] + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                parts[g] = string.Join(" ", s);
            }
            return string.Join(" | ", parts);
        }

        /// <summary>The slot's chain (0 ... 2).</summary>
        public int ChainOf(int slot)
        {
            for (int g = 0; g < Chains; g++) if (Array.IndexOf(Chain[g], slot) >= 0) return g;
            return Chains - 1;
        }

        /// <summary>The same routing with a slot moved to a chain (at its end) or, within its chain, to a position.</summary>
        public FxRouting Move(int slot, int toChain, int position)
        {
            var lists = new List<int>[Chains];
            for (int g = 0; g < Chains; g++) lists[g] = new List<int>(Chain[g]);
            foreach (var l in lists) l.Remove(slot);
            var dest = lists[Math.Clamp(toChain, 0, Chains - 1)];
            dest.Insert(Math.Clamp(position, 0, dest.Count), slot);
            var chain = new int[Chains][];
            for (int g = 0; g < Chains; g++) chain[g] = lists[g].ToArray();
            return From(chain) ?? this;
        }

        public bool SameAs(FxRouting other) => other != null && ToString() == other.ToString();
    }

    internal sealed class FxRack
    {
        public const int Slots = FxRouting.Slots, PerSlot = 12, MaxBlock = DyeEngine.MaxBlock;
        /// <summary>The most a slot's own pad (one clipper at 192 kHz: 1083) and the rack's pads (ten of them) delay by.</summary>
        const int SlotPad = 2047, RackPad = 16383;
        const double RampSeconds = 0.005, TailSeconds = 5, AudibleLevel = 1e-5;

        sealed class Slot
        {
            public readonly FxHost[] Hosts = new FxHost[FxTypes.Count];
            public readonly int[] Request = new int[FxTypes.Count];
            public readonly float[] WetL = new float[MaxBlock], WetR = new float[MaxBlock];
            public readonly DelayLineF PadL = new DelayLineF(SlotPad), PadR = new DelayLineF(SlotPad);
            public LinearRamp Mix;
            public int LastType;
            public bool WasOff = true;
            public readonly int[] Last = new int[FxTypes.PerType];
        }

        readonly ParamValues values;
        readonly Slot[] slot = new Slot[Slots];
        readonly int[] type = new int[Slots];
        readonly float[] aL = new float[MaxBlock], aR = new float[MaxBlock], bL = new float[MaxBlock], bR = new float[MaxBlock];
        readonly DelayLineF dryL = new DelayLineF(RackPad), dryR = new DelayLineF(RackPad);
        readonly DelayLineF padAL = new DelayLineF(RackPad), padAR = new DelayLineF(RackPad), padBL = new DelayLineF(RackPad), padBR = new DelayLineF(RackPad);
        LinearRamp sendA, sendB, retA, retB;
        FxRouting requested = FxRouting.Default;
        float rate = 48000;
        double tempo = 120;
        bool wasActive;
        long quiet;

        public FxRack(ParamValues values)
        {
            this.values = values;
            for (int i = 0; i < Slots; i++) slot[i] = new Slot();
            SetSampleRate(48000);
            FxBuilder.Register(this);
        }

        public double Tempo { set => tempo = value; }

        public FxRouting Routing => Volatile.Read(ref requested);

        /// <summary>Any thread: the routing from the next block.</summary>
        public void SetRouting(FxRouting r) => Volatile.Write(ref requested, r ?? FxRouting.Default);

        public static int TypeParam(int s) => P.X1Type + PerSlot * s;
        public static int MixParam(int s) => TypeParam(s) + 1;
        public static int GenericParam(int s, int k) => TypeParam(s) + 2 + k;

        public float Rate => Volatile.Read(ref rate);

        /// <summary>The audio thread's call when the host's rate is known or changes.</summary>
        public void SetSampleRate(float sr)
        {
            Volatile.Write(ref rate, sr);
            foreach (var s in slot)
            {
                s.Mix.Setup(RampSeconds, sr);
                s.Mix.Reset(0);
                s.LastType = 0;
                s.WasOff = true;
            }
            sendA.Setup(RampSeconds, sr); sendB.Setup(RampSeconds, sr); retA.Setup(RampSeconds, sr); retB.Setup(RampSeconds, sr);
            sendA.Reset(1); sendB.Reset(1); retA.Reset(1); retB.Reset(1);
            ResetPads();
            wasActive = false;
        }

        /// <summary>Main thread, before the audio runs: builds for this rate from now on (hosts built for another rate are stale).</summary>
        public void PrepareRate(float sr)
        {
            if (sr > 0 && sr != Rate) Volatile.Write(ref rate, sr);
        }

        void ResetPads()
        {
            dryL.Reset(); dryR.Reset(); padAL.Reset(); padAR.Reset(); padBL.Reset(); padBR.Reset();
            foreach (var s in slot) { s.PadL.Reset(); s.PadR.Reset(); }
        }

        FxHost Ready(int s, int t)
        {
            var h = Volatile.Read(ref slot[s].Hosts[t]);
            return h != null && h.Rate == Rate ? h : null;
        }

        /// <summary>Asks the builder for a slot's type (audio thread: allocation-free).</summary>
        void Want(int s, int t)
        {
            if (Interlocked.CompareExchange(ref slot[s].Request[t], 1, 0) == 0) FxBuilder.Kick();
        }

        /// <summary>The builder's job: builds what was asked for (never on the audio thread).</summary>
        internal void BuildPending()
        {
            for (int s = 0; s < Slots; s++)
                for (int t = 1; t < FxTypes.Count; t++)
                    if (Volatile.Read(ref slot[s].Request[t]) == 1)
                    {
                        try
                        {
                            var h = FxHost.Create((FxType)t, Rate);
                            Volatile.Write(ref slot[s].Hosts[t], h);
                        }
                        catch (Exception)
                        {
                            // A type that cannot be built stays a pass-through.
                        }
                        Volatile.Write(ref slot[s].Request[t], 0);
                    }
        }

        /// <summary>
        /// Any thread: whether a slot's chosen type is still being built. Asks for what is missing, so offline renders
        /// can wait for the rack before they start.
        /// </summary>
        public bool Pending => PendingFor(s => ChosenType(values, s));

        /// <summary>
        /// <see cref="Pending"/> for the types <paramref name="typeOf"/> gives (slot to type): the machine reads the host's
        /// parameter values, which a preset or a set parameter changes at once, where the engine's own values follow at the
        /// next audio tick, after an offline render has already asked whether the machine is busy.
        /// </summary>
        public bool PendingFor(Func<int, int> typeOf)
        {
            bool pending = false;
            for (int s = 0; s < Slots; s++)
            {
                int t = typeOf(s);
                if (t <= 0 || t >= FxTypes.Count) continue;
                if (Ready(s, t) == null)
                {
                    pending = true;
                    Want(s, t);
                }
            }
            return pending;
        }

        /// <summary>Builds every chosen type now, on the calling thread (tests and offline tools).</summary>
        public void BuildNow()
        {
            for (int s = 0; s < Slots; s++)
            {
                int t = values.Choice(TypeParam(s));
                if (t <= 0 || t >= FxTypes.Count || Ready(s, t) != null) continue;
                Volatile.Write(ref slot[s].Hosts[t], FxHost.Create((FxType)t, Rate));
            }
        }

        static int ChosenType(ParamValues v, int s)
        {
            int t = v.Choice(TypeParam(s));
            return t < 0 || t >= FxTypes.Count ? 0 : t;
        }

        int SlotLatency(int s) => type[s] == 0 ? 0 : FxTypes.Latency((FxType)type[s], Rate);

        int ChainLatency(int[] chain)
        {
            int sum = 0;
            foreach (int s in chain) sum += SlotLatency(s);
            return sum;
        }

        bool ChainOn(int[] chain)
        {
            foreach (int s in chain) if (type[s] != 0) return true;
            return false;
        }

        /// <summary>
        /// The rack's latency now, in samples (any thread; the host asks at every tick on the audio thread, so no allocation):
        /// the same whatever the audio; 0 with every slot Off.
        /// </summary>
        public int Latency
        {
            get
            {
                var route = Routing;
                double sr = Rate;
                int a = ChainLatencyOf(route.Chain[0], sr), b = ChainLatencyOf(route.Chain[1], sr), m = ChainLatencyOf(route.Chain[2], sr);
                if (values.Choice(P.FxLayout) == 0) return Math.Max(0, Math.Max(a, b)) + Math.Max(0, m);
                return Math.Max(0, a) + Math.Max(0, b) + Math.Max(0, m);
            }
        }

        /// <summary>A chain's latency from the parameters now: -1 when every slot of it is Off (it adds nothing).</summary>
        int ChainLatencyOf(int[] chain, double sr)
        {
            int sum = 0;
            bool on = false;
            foreach (int s in chain)
            {
                int t = ChosenType(values, s);
                if (t == 0) continue;
                on = true;
                sum += FxTypes.Latency((FxType)t, sr);
            }
            return on ? sum : -1;
        }

        /// <summary>
        /// Runs the rack on a stereo block in place (up to 256 samples). Returns whether it still sounds (its input, or
        /// a tail within five seconds), for the machine's rest decision.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool Process(float[] l, float[] r, int n)
        {
            bool any = false;
            for (int s = 0; s < Slots; s++)
            {
                type[s] = ChosenType(values, s);
                if (type[s] != 0) any = true;
            }
            if (!any)
            {
                if (wasActive)
                {
                    ResetPads();
                    wasActive = false;
                    quiet = 0;
                    foreach (var s in slot) { s.WasOff = true; s.LastType = 0; s.Mix.Reset(0); }
                }
                return false;
            }
            bool wasActive0 = wasActive;
            wasActive = true;
            var route = Routing;
            int layout = values.Choice(P.FxLayout);
            if (!wasActive0)
            {
                sendA.Reset(Gain(P.FxSendA)); sendB.Reset(Gain(P.FxSendB)); retA.Reset(Gain(P.FxRetA)); retB.Reset(Gain(P.FxRetB));
            }
            sendA.SetTarget(Gain(P.FxSendA));
            sendB.SetTarget(Gain(P.FxSendB));
            retA.SetTarget(Gain(P.FxRetA));
            retB.SetTarget(Gain(P.FxRetB));
            if (layout == 0) ProcessTrio(route, l, r, n);
            else ProcessDuo(route, l, r, n);
            float peak = 0;
            for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            if (peak > AudibleLevel) quiet = 0;
            else quiet += n;
            return quiet < TailSeconds * Rate;
        }

        float Gain(int param)
        {
            double db = values.Value(param);
            return double.IsNegativeInfinity(db) ? 0f : (float)DspMath.DbToGain(db);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void ProcessTrio(FxRouting route, float[] l, float[] r, int n)
        {
            var ca = route.Chain[0]; var cb = route.Chain[1]; var cm = route.Chain[2];
            bool aOn = ChainOn(ca), bOn = ChainOn(cb);
            int la = aOn ? ChainLatency(ca) : 0, lb = bOn ? ChainLatency(cb) : 0;
            int bus = Math.Max(la, lb);
            if (aOn)
            {
                for (int i = 0; i < n; i++) { float g = sendA.Next(); aL[i] = l[i] * g; aR[i] = r[i] * g; }
                RunChain(ca, aL, aR, n);
            }
            if (bOn)
            {
                for (int i = 0; i < n; i++) { float g = sendB.Next(); bL[i] = l[i] * g; bR[i] = r[i] * g; }
                RunChain(cb, bL, bR, n);
            }
            if (aOn || bOn)
            {
                int da = Math.Min(RackPad, bus - la), db = Math.Min(RackPad, bus - lb), dd = Math.Min(RackPad, bus);
                for (int i = 0; i < n; i++)
                {
                    float xl = l[i], xr = r[i];
                    if (dd > 0)
                    {
                        dryL.Write(xl); dryR.Write(xr);
                        xl = dryL.Read(dd); xr = dryR.Read(dd);
                    }
                    if (aOn)
                    {
                        float vl = aL[i], vr = aR[i];
                        if (da > 0)
                        {
                            padAL.Write(vl); padAR.Write(vr);
                            vl = padAL.Read(da); vr = padAR.Read(da);
                        }
                        float g = retA.Next();
                        xl += vl * g; xr += vr * g;
                    }
                    if (bOn)
                    {
                        float vl = bL[i], vr = bR[i];
                        if (db > 0)
                        {
                            padBL.Write(vl); padBR.Write(vr);
                            vl = padBL.Read(db); vr = padBR.Read(db);
                        }
                        float g = retB.Next();
                        xl += vl * g; xr += vr * g;
                    }
                    l[i] = xl; r[i] = xr;
                }
            }
            RunChain(cm, l, r, n);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void ProcessDuo(FxRouting route, float[] l, float[] r, int n)
        {
            RunChain(route.Chain[0], l, r, n);
            RunChain(route.Chain[1], l, r, n);
            var cx = route.Chain[2];
            if (!ChainOn(cx)) return;
            int lx = Math.Min(RackPad, ChainLatency(cx));
            for (int i = 0; i < n; i++) { float g = sendA.Next(); aL[i] = l[i] * g; aR[i] = r[i] * g; }
            RunChain(cx, aL, aR, n);
            for (int i = 0; i < n; i++)
            {
                float xl = l[i], xr = r[i];
                if (lx > 0)
                {
                    dryL.Write(xl); dryR.Write(xr);
                    xl = dryL.Read(lx); xr = dryR.Read(lx);
                }
                float g = retA.Next();
                l[i] = xl + aL[i] * g;
                r[i] = xr + aR[i] * g;
            }
        }

        void RunChain(int[] chain, float[] l, float[] r, int n)
        {
            foreach (int s in chain)
                if (type[s] != 0) RunSlot(s, l, r, n);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void RunSlot(int idx, float[] l, float[] r, int n)
        {
            var s = slot[idx];
            int t = type[idx];
            var info = FxTypes.Info((FxType)t);
            int lat = Math.Min(SlotPad, FxTypes.Latency((FxType)t, Rate));
            var h = Ready(idx, t);
            if (h == null) Want(idx, t);
            if (t != s.LastType)
            {
                s.LastType = t;
                s.Mix.Reset(0);
                s.WasOff = true;
            }
            float mix = (float)values.Value(MixParam(idx));
            s.Mix.SetTarget(h != null ? mix : 0);
            if (h == null || s.Mix.At(0))
            {
                // Not heard (mix 0, or the engine not built yet): the input, late by the latency.
                if (lat > 0)
                    for (int i = 0; i < n; i++)
                    {
                        s.PadL.Write(l[i]); s.PadR.Write(r[i]);
                        l[i] = s.PadL.Read(lat); r[i] = s.PadR.Read(lat);
                    }
                s.WasOff = true;
                return;
            }
            if (s.WasOff)
            {
                h.Reset();
                s.WasOff = false;
            }
            for (int k = 0; k < FxTypes.PerType; k++)
            {
                int x = values[GenericParam(idx, k)];
                int raw = info.EngineRaw(k, x);
                int engineIndex = info.Params[k].Index;
                if (h.V[engineIndex] != raw) h.V.Set(engineIndex, raw);
            }
            h.SetTempo(tempo);
            Array.Copy(l, s.WetL, n);
            Array.Copy(r, s.WetR, n);
            h.Process(s.WetL, s.WetR, n);
            bool full = s.Mix.At(1);
            for (int i = 0; i < n; i++)
            {
                float dl = l[i], dr = r[i];
                if (lat > 0)
                {
                    s.PadL.Write(dl); s.PadR.Write(dr);
                    dl = s.PadL.Read(lat); dr = s.PadR.Read(lat);
                }
                if (full)
                {
                    l[i] = s.WetL[i];
                    r[i] = s.WetR[i];
                }
                else
                {
                    float w = s.Mix.Next();
                    l[i] = dl + w * (s.WetL[i] - dl);
                    r[i] = dr + w * (s.WetR[i] - dr);
                }
            }
        }
    }

    /// <summary>The one background worker that builds the rack's engines (they allocate; the audio thread never does).</summary>
    internal static class FxBuilder
    {
        static readonly object gate = new object();
        static readonly List<WeakReference<FxRack>> racks = new List<WeakReference<FxRack>>();
        static readonly ManualResetEventSlim signal = new ManualResetEventSlim(false);
        static Thread worker;

        public static void Register(FxRack rack)
        {
            lock (gate)
            {
                racks.RemoveAll(w => !w.TryGetTarget(out _));
                racks.Add(new WeakReference<FxRack>(rack));
            }
        }

        /// <summary>Wakes the worker (any thread, allocation-free once the worker runs).</summary>
        public static void Kick()
        {
            if (worker == null) Start();
            signal.Set();
        }

        static void Start()
        {
            lock (gate)
            {
                if (worker != null) return;
                var t = new Thread(Run) { IsBackground = true, Name = "Dye Essence effects builder", Priority = ThreadPriority.BelowNormal };
                worker = t;
                t.Start();
            }
        }

        static void Run()
        {
            var live = new List<FxRack>();
            while (true)
            {
                signal.Wait();
                signal.Reset();
                live.Clear();
                lock (gate)
                    foreach (var w in racks)
                        if (w.TryGetTarget(out var rack)) live.Add(rack);
                foreach (var rack in live) rack.BuildPending();
            }
        }
    }
}
