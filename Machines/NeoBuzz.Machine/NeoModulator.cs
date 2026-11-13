#nullable disable
// Neo machines' Buzz glue for the canvas modulators (docs/specs/neo-patch.md §3, phase NM): what Neo LFO, Neo Envelope, Neo Step
// and Neo Macro share. A control machine: ReBuzz calls Work() once per block with no audio. Each machine turns its own state into a
// 0 … 1 output (Advance), and the bank (NeoBuzz.Dsp/ModTargets.cs) writes up to 8 targets through the host's IParameter.SetValue, at
// most once per 64 samples, slewed by the machine's Write glide, with SendControlChanges so a target takes the value in the same block.
//
// Targets are held by identity (the IMachine), saved by name and parameter (name, index, group), and found again by name when a song
// loads or a deleted machine comes back. All the finding runs off the audio thread (first the machine's first Work after a change,
// then a timer); the audio thread only reads the slots' ports. A write is a modulation, not an edit: SetValue records no undo step.

using System;
using System.Collections.Generic;
using System.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;

namespace NeoBuzz.Machine
{
    /// <summary>One target as the song keeps it.</summary>
    public class ModTargetState
    {
        public int Slot { get; set; }
        public string Machine { get; set; } = "";
        public string Param { get; set; } = "";
        public int ParamIndex { get; set; } = -1;
        public bool IsTrack { get; set; }
        public int Track { get; set; }
        public int Depth { get; set; } = 100;
        public bool Bipolar { get; set; }
        public int RangeMin { get; set; } = int.MinValue;
        public int RangeMax { get; set; } = int.MinValue;
        public int Curve { get; set; }
        /// <summary>The value the person set and the one written last: a reload resumes the modulation from them.</summary>
        public int Base { get; set; } = int.MinValue;
        public int Applied { get; set; } = int.MinValue;
    }

    /// <summary>What a modulator keeps with the song besides its parameters.</summary>
    public class ModulatorState
    {
        public string ListenMachine { get; set; } = "";
        public int ListenTrack { get; set; } = -1;
        public List<ModTargetState> Targets { get; set; } = new List<ModTargetState>();
    }

    /// <summary>A host parameter as a bank port.</summary>
    internal sealed class ParamPort : IModPort
    {
        readonly IParameter parameter;
        readonly IMachine machine;
        readonly int track;

        public ParamPort(IMachine machine, IParameter parameter, int track, int safeMin, int safeMax)
        {
            this.machine = machine;
            this.parameter = parameter;
            this.track = track;
            Min = parameter.MinValue;
            Max = parameter.MaxValue;
            SafeMin = Math.Max(Min, safeMin);
            SafeMax = Math.Min(Max, safeMax);
        }

        public int Min { get; }
        public int Max { get; }
        public int SafeMin { get; }
        public int SafeMax { get; }
        public IParameter Parameter => parameter;
        public int Get() => parameter.GetValue(track);

        public void Put(int raw)
        {
            parameter.SetValue(track | (1 << 16), raw);   // bit 16: not recorded into patterns while the user records (ParameterCore.SetValue)
            machine.SendControlChanges();
        }
    }

    public abstract class NeoModulatorBase : NeoMachineBase, IModulator, IDisposable
    {
        /// <summary>The smallest block a write is made for (docs/specs/neo-patch.md decision 8).</summary>
        internal const int SubBlock = 64;
        const int ScopeSize = 256;

        internal readonly ModBank Bank = new ModBank();
        readonly string kind;
        readonly Timer maintenance;
        readonly object notesGate = new object();

        int lastPos = -1, lastSpt;
        int pending;
        volatile bool needResolve = true;
        volatile float output;
        readonly float[] scope = new float[ScopeSize];
        int scopeAt;
        volatile bool disposed;
        string listenMachine = "";
        int listenTrack = -1;
        bool resolveBusy;

        // Notes heard on the listened machine (counted where the host raises them, taken by Work).
        int noteOns, noteOffs;
        readonly List<(IParameter Parameter, int Track, Action<IParameter, int> Handler)> watched = new List<(IParameter, int, Action<IParameter, int>)>();
        IMachine watchedMachine;
        int watchedTrack = -2;

        // The listened machine's output levels (the follower).
        volatile IBlockLevel[] levels = Array.Empty<IBlockLevel>();
        bool following;

        internal NeoModulatorBase(IBuzzMachineHost host, ParamDef[] table, string kind) : base(host, table)
        {
            this.kind = kind;
            ModulationBoard.Register(this);
            maintenance = new Timer(_ => Maintain(), null, 700, 700);
        }

        // ---------------------------------------------------------------- what the machine says

        /// <summary>The machine's own 0 … 1 output over the last <paramref name="samples"/> samples (<paramref name="dt"/> seconds).</summary>
        protected abstract float Advance(int samples, double dt);

        /// <summary>The Write glide, in seconds (the parameter the machine says).</summary>
        protected abstract double TargetGlide { get; }

        /// <summary>The machine heard a note on the listened machine (or the follower started): its own reaction.</summary>
        protected virtual void NoteOn() { }
        protected virtual void NoteOff() { }

        /// <summary>Whether the machine wants the listened machine's output level (the Envelope's follower).</summary>
        protected virtual bool WantsLevel => false;

        // ---------------------------------------------------------------- time

        /// <summary>The song position in beats while the transport plays (and whether it does); else the beats advanced since playing began.</summary>
        protected double Beats { get; private set; }
        protected bool Playing { get; private set; }
        double freeBeats;

        public IMachine Machine => host?.Machine;
        public string Kind => kind;
        public float Output => output;

        public int ReadScope(float[] dest)
        {
            int n = Math.Min(dest.Length, ScopeSize);
            int start = (scopeAt - n + ScopeSize * 2) % ScopeSize;
            for (int i = 0; i < n; i++) dest[i] = scope[(start + i) % ScopeSize];
            return n;
        }

        /// <summary>
        /// ReBuzz calls the machine's Work once per block. Subclasses expose it as <c>public void Work() =&gt; Run();</c>
        /// (the host takes a Work with no arguments for a control machine).
        /// </summary>
        protected void Run()
        {
            try
            {
                RunCore();
            }
            catch (Exception ex)
            {
                // A control machine's fault must not stop the engine (as the Pedal control machines do): kept for the editor.
                Fault = ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>The first fault Work caught (null: none).</summary>
        public string Fault { get; private set; }

        void RunCore()
        {
            var mi = host?.MasterInfo;
            if (mi == null || disposed) return;
            int pos = mi.PosInTick, spt = mi.SamplesPerTick;
            int adv = lastPos < 0 ? 0 : pos - lastPos;
            if (adv < 0) adv += lastSpt > 0 ? lastSpt : spt;
            lastPos = pos;
            lastSpt = spt;
            if (needResolve)
            {
                needResolve = false;
                Maintain();
            }
            pending += adv;
            if (pending < SubBlock) return;
            int samples = pending;
            pending = 0;
            double sr = SampleRate;
            double dt = samples / sr;

            // The clock: the song's position while it plays, else free-running beats at the tempo.
            double beatLength = 60.0 / BeatsPerMinute;
            freeBeats += dt / beatLength;
            bool playing = false;
            try
            {
                var buzz = host.Machine?.Graph?.Buzz;
                playing = buzz != null && buzz.Playing;
                if (playing)
                {
                    int tpb = Math.Max(1, mi.TicksPerBeat);
                    Beats = (buzz.Song.PlayPosition + (spt > 0 ? pos / (double)spt : 0)) / tpb;
                }
            }
            catch (Exception)
            {
                playing = false;
            }
            Playing = playing;
            if (!playing) Beats = freeBeats;

            // Notes first, so a note that arrived with the block shapes it.
            int offs = Interlocked.Exchange(ref noteOffs, 0);
            int ons = Interlocked.Exchange(ref noteOns, 0);
            if (offs > 0 && ons == 0) NoteOff();
            if (ons > 0) NoteOn();

            float u = Advance(samples, dt);
            if (!(u >= 0f)) u = 0f;
            else if (u > 1f) u = 1f;
            output = u;
            scope[scopeAt] = u;
            scopeAt = (scopeAt + 1) % ScopeSize;
            Bank.GlideSeconds = TargetGlide;
            Bank.Update(u, dt);
        }

        /// <summary>The level of the listened machine's output over the last block (0 … 1), for the follower.</summary>
        protected float ListenedLevel()
        {
            var l = levels;
            float peak = 0;
            for (int i = 0; i < l.Length; i++)
            {
                float p = l[i].BlockPeak;
                if (p > peak) peak = p;
            }
            return peak;
        }

        // ---------------------------------------------------------------- the song's state

        /// <summary>Kept with the song: the listened machine and the targets (a target's base, so a reload resumes it).</summary>
        public ModulatorState MachineState
        {
            get
            {
                var s = new ModulatorState { ListenMachine = listenMachine, ListenTrack = listenTrack };
                lock (Bank.Gate)
                    for (int i = 0; i < ModBank.Max; i++)
                    {
                        var t = Bank.Slots[i];
                        if (!t.Used) continue;
                        s.Targets.Add(new ModTargetState
                        {
                            Slot = i, Machine = t.Machine, Param = t.Param, ParamIndex = t.ParamIndex, IsTrack = t.IsTrack, Track = t.Track,
                            Depth = t.Depth, Bipolar = t.Bipolar, RangeMin = t.RangeMin, RangeMax = t.RangeMax, Curve = t.Curve,
                            // Only a target that is being driven has a base worth keeping.
                            Base = t.Has ? t.Base : t.SavedBase, Applied = t.Has ? t.Applied : t.SavedApplied,
                        });
                    }
                return s;
            }
            set
            {
                value ??= new ModulatorState();
                listenMachine = value.ListenMachine ?? "";
                listenTrack = value.ListenTrack;
                lock (Bank.Gate)
                {
                    for (int i = 0; i < ModBank.Max; i++) Bank.Slots[i] = new ModSlot();
                    foreach (var t in value.Targets ?? new List<ModTargetState>())
                    {
                        if (t.Slot < 0 || t.Slot >= ModBank.Max) continue;
                        Bank.Slots[t.Slot] = new ModSlot
                        {
                            Used = true, Machine = t.Machine ?? "", Param = t.Param ?? "", ParamIndex = t.ParamIndex, IsTrack = t.IsTrack, Track = t.Track,
                            Depth = Math.Clamp(t.Depth, -100, 100), Bipolar = t.Bipolar, RangeMin = t.RangeMin, RangeMax = t.RangeMax,
                            Curve = Math.Clamp(t.Curve, -100, 100), SavedBase = t.Base, SavedApplied = t.Applied,
                            State = (int)ModulationState.MachineMissing,
                        };
                    }
                }
                needResolve = true;
            }
        }

        // ---------------------------------------------------------------- IModulator

        public string ListenMachine
        {
            get => listenMachine;
            set
            {
                listenMachine = value ?? "";
                needResolve = true;
                Maintain();
                ModulationBoard.Raise();
            }
        }

        public int ListenTrack
        {
            get => listenTrack;
            set
            {
                listenTrack = value;
                Maintain();
            }
        }

        public IReadOnlyList<ModulationInfo> Targets
        {
            get
            {
                var list = new List<ModulationInfo>();
                lock (Bank.Gate)
                    for (int i = 0; i < ModBank.Max; i++)
                    {
                        var t = Bank.Slots[i];
                        if (!t.Used) continue;
                        var info = new ModulationInfo
                        {
                            Slot = i, Source = host?.Machine?.Name ?? "", Kind = kind, Machine = t.Machine, Param = t.Param, ParamIndex = t.ParamIndex,
                            Track = t.Track, IsTrack = t.IsTrack, Depth = t.Depth, Bipolar = t.Bipolar, RangeMin = t.RangeMin, RangeMax = t.RangeMax,
                            Curve = t.Curve, State = (ModulationState)t.State,
                        };
                        var p = t.Port;
                        if (p != null)
                        {
                            info.Min = p.Min; info.Max = p.Max; info.SafeMin = p.SafeMin; info.SafeMax = p.SafeMax;
                            info.Base = t.Has ? t.Base : p.Get();
                            info.Current = p.Get();
                            ModBank.Reach(t, p, info.Base, out int lo, out int hi);
                            info.Low = lo; info.High = hi;
                        }
                        list.Add(info);
                    }
                return list;
            }
        }

        public string SetTarget(int slot, IMachine machine, IParameter parameter, int track, ModulationInfo info, out int placed)
        {
            placed = -1;
            if (machine == null || parameter == null) return "no such machine or parameter";
            if (ReferenceEquals(machine, host?.Machine)) return "a modulator can't modulate itself";
            if (!(parameter.Group is { } g) || (g.Type != ParameterGroupType.Global && g.Type != ParameterGroupType.Track))
                return $"{parameter.Name} is not a parameter a modulator can drive (inputs and notes are not)";
            if (parameter.Type == ParameterType.Note) return $"{parameter.Name} is a note column, not a value";
            bool isTrack = g.Type == ParameterGroupType.Track;
            if (!isTrack) track = 0;
            if (isTrack && (track < 0 || track >= Math.Max(1, g.TrackCount))) return $"{machine.Name} has no track {track}";
            var port = ModResolver.Port(machine, parameter, track, out string refusal);
            lock (Bank.Gate)
            {
                if (slot < 0)
                {
                    // The same parameter again changes that slot; else the first free one.
                    for (int i = 0; i < ModBank.Max && slot < 0; i++)
                    {
                        var t = Bank.Slots[i];
                        if (t.Used && ReferenceEquals(t.Target, machine) && t.Param == parameter.Name && t.Track == track && t.IsTrack == isTrack) slot = i;
                    }
                    for (int i = 0; i < ModBank.Max && slot < 0; i++) if (!Bank.Slots[i].Used) slot = i;
                    if (slot < 0) return $"{host?.Machine?.Name} already has {ModBank.Max} targets; remove one first";
                }
                if (slot >= ModBank.Max) return "slots are 0 to 7";
                var s = Bank.Slots[slot];
                bool same = s.Used && ReferenceEquals(s.Target, machine) && s.Param == parameter.Name && s.Track == track && s.IsTrack == isTrack;
                if (s.Used && !same) { }   // another parameter: the old one is released below
                if (s.Used && !same) { ReleaseUnlocked(s); }
                if (!same) { s.Has = false; }
                s.Used = true;
                s.Target = machine;
                s.Machine = machine.Name;
                s.Param = parameter.Name;
                s.ParamIndex = parameter.IndexInGroup;
                s.IsTrack = isTrack;
                s.Track = track;
                s.Depth = Math.Clamp(info?.Depth ?? 100, -100, 100);
                s.Bipolar = info?.Bipolar ?? false;
                s.Curve = Math.Clamp(info?.Curve ?? 0, -100, 100);
                s.RangeMin = info?.RangeMin ?? ModSlot.None;
                s.RangeMax = info?.RangeMax ?? ModSlot.None;
                s.SavedBase = s.SavedApplied = ModSlot.None;
                if (port == null)
                {
                    s.Port = null;
                    s.State = (int)ModulationState.Refused;
                }
                else
                {
                    if (!same) s.Port = port;
                    s.State = (int)ModulationState.Active;
                }
                placed = slot;
            }
            if (port == null) return refusal;
            ModulationBoard.Raise();
            return null;
        }

        static void ReleaseUnlocked(ModSlot s)
        {
            var p = s.Port;
            if (p != null && s.Has)
            {
                try { if (p.Get() == s.Applied && s.Applied != s.Base) p.Put(s.Base); } catch (Exception) { }
            }
            s.Has = false;
        }

        public string EditTarget(int slot, ModulationInfo values)
        {
            if (slot < 0 || slot >= ModBank.Max || values == null) return "slots are 0 to 7";
            lock (Bank.Gate)
            {
                var s = Bank.Slots[slot];
                if (!s.Used) return "that slot has no target";
                s.Depth = Math.Clamp(values.Depth, -100, 100);
                s.Bipolar = values.Bipolar;
                s.Curve = Math.Clamp(values.Curve, -100, 100);
                bool both = values.RangeMin != ModSlot.None && values.RangeMax != ModSlot.None;
                s.RangeMin = both ? values.RangeMin : ModSlot.None;
                s.RangeMax = both ? values.RangeMax : ModSlot.None;
            }
            ModulationBoard.Raise();
            return null;
        }

        public void RemoveTarget(int slot)
        {
            if (slot < 0 || slot >= ModBank.Max) return;
            lock (Bank.Gate)
            {
                var s = Bank.Slots[slot];
                ReleaseUnlocked(s);
                Bank.Slots[slot] = new ModSlot();
            }
            ModulationBoard.Raise();
        }

        // ---------------------------------------------------------------- finding things again

        /// <summary>
        /// Off the audio thread (the first Work after a change, and a timer): follows renames, drops deleted targets, finds a target by
        /// name where its machine was made again, and follows the listened machine.
        /// </summary>
        internal void Maintain()
        {
            if (disposed || resolveBusy) return;
            resolveBusy = true;
            try
            {
                var machines = host?.Machine?.Graph?.Machines;
                if (machines == null) return;
                bool changed = false;
                lock (Bank.Gate)
                {
                    for (int i = 0; i < ModBank.Max; i++)
                    {
                        var s = Bank.Slots[i];
                        if (!s.Used) continue;
                        changed |= ResolveSlot(s, machines);
                    }
                }
                ListenFollow(machines);
                if (changed) ModulationBoard.Raise();
            }
            catch (Exception)
            {
                // The graph changed under us: the next round finds it.
            }
            finally
            {
                resolveBusy = false;
            }
        }

        bool ResolveSlot(ModSlot s, IList<IMachine> machines)
        {
            var before = (s.State, s.Machine, s.Port);
            IMachine m = s.Target as IMachine;
            if (m != null)
            {
                bool found = false;
                foreach (var x in machines) if (ReferenceEquals(x, m)) { found = true; break; }
                if (!found) { m = null; s.Target = null; s.Port = null; s.Has = false; }
            }
            if (m == null)
            {
                foreach (var x in machines)
                    if (x.Name == s.Machine && !ReferenceEquals(x, host?.Machine)) { m = x; break; }
                if (m == null)
                {
                    s.State = (int)ModulationState.MachineMissing;
                    return before.State != s.State;
                }
                s.Target = m;
                s.Port = null;
                s.Has = false;
            }
            s.Machine = m.Name;
            if (s.Port == null)
            {
                IParameter found = ModResolver.Find(m, s.IsTrack, s.Param, s.ParamIndex);
                if (found == null)
                {
                    s.State = (int)ModulationState.ParamMissing;
                    return before.State != s.State || before.Machine != s.Machine;
                }
                s.Param = found.Name;
                s.ParamIndex = found.IndexInGroup;
                var port = ModResolver.Port(m, found, s.Track, out _);
                s.Port = port;
                s.State = (int)(port == null ? ModulationState.Refused : ModulationState.Active);
            }
            return before.State != s.State || before.Machine != s.Machine || !ReferenceEquals(before.Port, s.Port);
        }

        void ListenFollow(IList<IMachine> machines)
        {
            IMachine m = null;
            if (listenMachine.Length > 0)
                foreach (var x in machines) if (x.Name == listenMachine && !ReferenceEquals(x, host?.Machine)) { m = x; break; }

            // Notes: the note parameters' events (raised by the host as the pattern plays).
            lock (notesGate)
            {
                if (!ReferenceEquals(m, watchedMachine) || listenTrack != watchedTrack)
                {
                    foreach (var (p, t, h) in watched) { try { p.UnsubscribeEvents(t, h, null); } catch (Exception) { } }
                    watched.Clear();
                    watchedMachine = m;
                    watchedTrack = listenTrack;
                    if (m != null) WatchNotes(m);
                }
            }

            // Levels: what the machine sends on (the Master: what it receives).
            IBlockLevel[] list = Array.Empty<IBlockLevel>();
            bool want = m != null && WantsLevel;
            if (want)
            {
                var conns = m.DLL?.Info?.Type == MachineType.Master ? m.Inputs : m.Outputs;
                var tmp = new List<IBlockLevel>();
                foreach (var c in conns) if (c is IBlockLevel b) tmp.Add(b);
                list = tmp.ToArray();
            }
            levels = list;
            if (want != following)
            {
                following = want;
                if (want) ModulationBoard.FollowerStarted(); else ModulationBoard.FollowerStopped();
            }
        }

        void WatchNotes(IMachine m)
        {
            foreach (var group in m.ParameterGroups)
            {
                bool global = group.Type == ParameterGroupType.Global, tracks = group.Type == ParameterGroupType.Track;
                if (!global && !tracks) continue;
                foreach (var p in group.Parameters)
                {
                    if (p.Type != ParameterType.Note) continue;
                    int count = tracks ? Math.Max(1, group.TrackCount) : 1;
                    for (int t = 0; t < count; t++)
                    {
                        if (tracks && listenTrack >= 0 && t != listenTrack) continue;
                        int track = t;
                        Action<IParameter, int> handler = (prm, value) =>
                        {
                            if (value == Note.Off) Interlocked.Increment(ref noteOffs);
                            else if (value != prm.NoValue) Interlocked.Increment(ref noteOns);
                        };
                        try
                        {
                            p.SubscribeEvents(track, handler, null);
                            watched.Add((p, track, handler));
                        }
                        catch (Exception) { }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { maintenance.Dispose(); } catch (Exception) { }
            // The targets go back to what the person set.
            try { Bank.ReleaseAll(); } catch (Exception) { }
            lock (notesGate)
            {
                foreach (var (p, t, h) in watched)
                {
                    try { p.UnsubscribeEvents(t, h, null); } catch (Exception) { }
                }
                watched.Clear();
            }
            if (following)
            {
                following = false;
                ModulationBoard.FollowerStopped();
            }
            ModulationBoard.Unregister(this);
        }
    }
}
