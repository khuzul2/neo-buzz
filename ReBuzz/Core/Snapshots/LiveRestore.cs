// Fork (docs/specs/history-snapshots.md HS3): restoring a snapshot while the song plays, without a click.
//
// The UI thread compares the snapshot's LIVE section with the song as it is (LiveRestorePlanner) and builds everything the change needs
// beforehand: event lists, sequence dictionaries, machines (hidden, created and fed like the real ones, their outputs silent), wires.
// Then the plan waits for its boundary on the Session launch clock (SessionCore.PlanRestoreBoundary: the latency-compensated HeardClock),
// and the audio thread, at that tick and holding AudioLock as it always does, only swaps references and sets values:
//   * parameters glide over one beat (a switch or a selector steps at the boundary),
//   * mute and bypass flags, wire amps and pans (the engine's own ramps, about four blocks),
//   * pattern and sequence events: the prebuilt lists are swapped in,
//   * a replaced or added machine fades in on its output wires while the old one fades out on its own (a crossfade of those four blocks),
//     both having been fed and sequenced the same, so the new one is in step.
// The UI thread finishes afterwards: events and names reported, the old machines deleted, the new ones shown and renamed, tempo set, one history step.
// Nothing the audio thread does here allocates; a plan that can't be built safely (a native machine whose state differs, a pattern editor's
// data, waves...) is deferred: the restore waits until the transport stops and says why.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using BuzzGUI.Common.Actions;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Actions.GraphActions;

namespace ReBuzz.Core.SongHistory
{
    /// <summary>When a live restore applies, on the Session launch grid.</summary>
    public enum RestoreQuantize { Auto, Now, Beat, Bar }

    /// <summary>What a restore did, or will do.</summary>
    internal sealed class RestoreResult
    {
        /// <summary>"applied" (the song was replaced, transport stopped), "live" (waiting for its boundary, or applied: see <see cref="Waiting"/>), "deferred" (waits for the transport to stop).</summary>
        public string Mode;
        public string Quantize;
        public string Reason;
        public bool Waiting;
        public double WaitMs;
        public List<string> Notes = new();
        public SnapshotManifest Snapshot;

        public string Text => Mode switch
        {
            "live" => Waiting ? $"The restore applies live at the next {Quantize}" + (WaitMs > 0 ? $" (in about {WaitMs / 1000:F1} s)" : "") + "." : "The restore was applied live.",
            "deferred" => "The restore waits until the transport stops: " + Reason,
            _ => "The song was restored.",
        };
    }

    internal sealed class LiveRestorePlan
    {
        internal struct ParamStep { public ParameterCore Parameter; public int Track; public int From, To; public bool Glide; }
        internal sealed class FlagStep
        {
            public MachineCore Machine;
            public bool Muted, Bypassed, MuteChanges, BypassChanges;
            /// <summary>The machine's output wires with the amps they end on: a mute fades them out first (the flag follows when the UI thread finishes), an unmute fades them in.</summary>
            public MachineConnectionCore[] Out = Array.Empty<MachineConnectionCore>();
            public int[] FinalAmps = Array.Empty<int>();
        }
        internal struct WireStep { public MachineConnectionCore Wire; public int Amp, Pan; }
        internal sealed class ColumnSwap { public PatternColumnCore Column; public List<PatternEvent> New; public bool Ramps; public List<PatternEvent> Old; public PatternCore Pattern; }
        internal sealed class SeqSwap { public SequenceCore Sequence; public Wintellect.PowerCollections.OrderedDictionary<int, SequenceEvent> New, Old; public SequenceCore.ArrangedEvents Arranged; }

        public string Name;
        public SnapshotManifest Target;
        public ParamStep[] Params = Array.Empty<ParamStep>();
        public FlagStep[] Flags = Array.Empty<FlagStep>();
        public WireStep[] Wires = Array.Empty<WireStep>();
        public ColumnSwap[] Columns = Array.Empty<ColumnSwap>();
        public SeqSwap[] Sequences = Array.Empty<SeqSwap>();

        // The UI thread's part after the boundary.
        public readonly List<(MachineCore Old, MachineCore New, string FinalName)> Replacements = new();
        public readonly List<MachineCore> Added = new();
        public readonly List<MachineCore> Removed = new();
        public readonly List<MachineConnectionCore> Disconnect = new();
        public int Bpm;
        public string Summary;

        /// <summary>The tick the audio thread applies it on.</summary>
        public long At;
        public volatile int State;          // 0 waiting, 1 applied (glide running), 2 glide done, 3 finished, 4 cancelled
        public long AppliedAt;              // Stopwatch timestamp, set by the UI thread when it sees State 1
        long glideSamples, glideLength;

        public const int Waiting = 0, Applied = 1, GlideDone = 2, Finished = 3, Cancelled = 4;
        const int DoNotRecord = 1 << 16;

        /// <summary>The hidden machines the plan made (for taking them back).</summary>
        public List<MachineCore> AllNewMachines() => Replacements.Select(r => r.New).Concat(Added).ToList();

        public bool IsEmpty => Params.Length == 0 && Flags.Length == 0 && Wires.Length == 0 && Columns.Length == 0 && Sequences.Length == 0
            && Replacements.Count == 0 && Added.Count == 0 && Removed.Count == 0 && Disconnect.Count == 0 && Bpm == 0;

        /// <summary>Audio thread, AudioLock held, at the end of a tick: the boundary has come. Allocates nothing.</summary>
        internal void OnTick(long clock)
        {
            if (State != Waiting || clock < At) return;
            Apply();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Apply()
        {
            for (int i = 0; i < Flags.Length; i++)
            {
                var f = Flags[i];
                if (f.BypassChanges) f.Machine.SetBypassedFromRestore(f.Bypassed);      // the bypass amount ramps by itself
                if (!f.MuteChanges) continue;
                if (f.Muted)
                {
                    for (int w = 0; w < f.Out.Length; w++) f.Out[w].Amp = 0;           // fades out over a few blocks; Finish sets the flag
                }
                else
                {
                    for (int w = 0; w < f.Out.Length; w++) { f.Out[w].Amp = 0; f.Out[w].SnapAmp(); }
                    f.Machine.SetMutedFromRestore(false);
                    for (int w = 0; w < f.Out.Length; w++) f.Out[w].Amp = f.FinalAmps[w];   // and fades in from silence
                }
            }
            for (int i = 0; i < Columns.Length; i++)
            {
                var c = Columns[i];
                c.Old = c.Column.SwapEvents(c.New, c.Ramps);
            }
            for (int i = 0; i < Sequences.Length; i++)
            {
                var s = Sequences[i];
                s.Old = s.Sequence.SwapEvents(s.New, s.Arranged);
            }
            for (int i = 0; i < Wires.Length; i++)
            {
                Wires[i].Wire.Amp = Wires[i].Amp;           // the connection ramps it over a few blocks
                Wires[i].Wire.Pan = Wires[i].Pan;
            }
            bool glide = false;
            for (int i = 0; i < Params.Length; i++)
            {
                if (Params[i].Glide) { glide = true; continue; }
                Params[i].Parameter.SetValue(Params[i].Track | DoNotRecord, Params[i].To);
            }
            var mi = ReBuzzCore.masterInfo;
            glideSamples = 0;
            glideLength = Math.Max(1, (long)Math.Max(1, mi.TicksPerBeat) * Math.Max(1, mi.SamplesPerTick));
            State = glide ? Applied : GlideDone;
            if (!glide) State = GlideDone;
        }

        /// <summary>
        /// Audio thread, before each block's events and tick (AudioLock held): the glide's next value where machines take values (each tick,
        /// and each subtick when those are on). Allocates nothing.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal void OnBlock(int frames, bool valuePoint)
        {
            if (State != Applied) return;
            if (valuePoint)
            {
                double p = (double)glideSamples / glideLength;
                bool done = p >= 1;
                if (done) p = 1;
                for (int i = 0; i < Params.Length; i++)
                {
                    if (!Params[i].Glide) continue;
                    int v = done ? Params[i].To : Params[i].From + (int)Math.Round((Params[i].To - Params[i].From) * p);
                    if (Params[i].Parameter.GetValue(Params[i].Track) != v)
                        Params[i].Parameter.SetValue(Params[i].Track | DoNotRecord, v);
                }
                if (done) { State = GlideDone; return; }
            }
            glideSamples += frames;
        }
    }

    internal static class LiveRestorePlanner
    {
        public sealed class Outcome
        {
            public LiveRestorePlan Plan;
            public string DeferReason;
            public List<string> Notes = new();
        }

        const int FullAmp = 0x4000;

        /// <summary>
        /// UI thread. Compares <paramref name="target"/> with the song as it is and builds the plan: everything that can be made beforehand
        /// is made here (machines included: hidden, connected, sequenced). Returns a defer reason instead when a part can't be prepared safely
        /// while playing; nothing has been changed then.
        /// </summary>
        public static Outcome Build(ReBuzzCore core, SnapshotManifest manifest, LiveSong target, SongSummary targetSummary, LiveSong nowOverride = null)
        {
            var o = new Outcome();
            var now = nowOverride ?? LiveSong.Take(core);
            var song = core.SongCore;

            // ---- what can't be done live at all
            if (target.Tpb != now.Tpb) return Defer(o, $"the ticks per beat differ ({now.Tpb} now, {target.Tpb} in the snapshot)");
            if (targetSummary != null)
            {
                var current = SongSummary.Take(core);
                if (!current.Waves.SequenceEqual(targetSummary.Waves)) return Defer(o, "the waves differ (samples can't be swapped while playing)");
            }

            // ---- machines: in place, replaced, added, removed
            var inPlace = new List<(LiveSong.Machine Now, LiveSong.Machine Then)>();
            var replace = new List<(LiveSong.Machine Now, LiveSong.Machine Then)>();
            var add = new List<LiveSong.Machine>();
            var remove = new List<LiveSong.Machine>();
            foreach (var t in target.Machines)
            {
                var n = now.Find(t.Name);
                if (n == null)
                {
                    if (t.IsMaster) return Defer(o, "the snapshot has no master machine to match");
                    if (!core.MachineDLLs.ContainsKey(t.Gear)) return Defer(o, $"the machine \"{t.Name}\" ({t.Gear}) isn't installed");
                    if (t.EditorGear != null) return Defer(o, $"the machine \"{t.Name}\" has a pattern editor, which can't be created while playing");
                    add.Add(t);
                }
                else if (n.IsMaster || n.Fingerprint == t.Fingerprint) inPlace.Add((n, t));
                else
                {
                    if (n.IsNative || t.IsNative) return Defer(o, $"\"{t.Name}\" is a native machine whose state differs: its state can't be swapped while playing");
                    if (n.IsMissing || t.IsMissing || !core.MachineDLLs.ContainsKey(t.Gear)) return Defer(o, $"\"{t.Name}\" ({t.Gear}) isn't installed");
                    if (n.EditorGear != null || t.EditorGear != null) return Defer(o, $"\"{t.Name}\" has a pattern editor whose data differs: it can't be swapped while playing");
                    replace.Add((n, t));
                }
            }
            foreach (var n in now.Machines)
                if (!n.IsMaster && target.Find(n.Name) == null) remove.Add(n);

            // ---- sequences of the machines that stay as they are
            var inPlaceNames = inPlace.Select(x => x.Now.Name).ToHashSet();
            foreach (var (n, t) in inPlace)
            {
                int a = now.Sequences.Count(q => q.Machine == n.Name), b = target.Sequences.Count(q => q.Machine == t.Name);
                if (a != b) return Defer(o, $"the tracks of \"{t.Name}\" differ ({a} now, {b} in the snapshot)");
            }

            // ---- wires between machines that stay: the channels can't change in place
            foreach (var tw in target.Wires)
            {
                var nw = now.Wires.FirstOrDefault(w => w.From == tw.From && w.To == tw.To);
                if (nw != null && (nw.SourceChannel != tw.SourceChannel || nw.DestinationChannel != tw.DestinationChannel) && inPlaceNames.Contains(tw.From) && inPlaceNames.Contains(tw.To))
                    return Defer(o, $"the wire {tw.From} to {tw.To} uses other channels");
            }

            var plan = new LiveRestorePlan { Name = manifest.Name, Target = manifest, Bpm = target.Bpm != now.Bpm ? target.Bpm : 0 };
            o.Plan = plan;
            var created = new List<MachineCore>();
            try
            {
                var steps = new Steps();
                Prepare(core, song, now, target, inPlace, replace, add, remove, plan, steps, created);
                plan.Params = steps.Params.ToArray();
                plan.Flags = steps.Flags.ToArray();
                plan.Wires = steps.Wires.ToArray();
                plan.Columns = steps.Columns.ToArray();
                plan.Sequences = steps.Sequences.ToArray();
            }
            catch (Exception e)
            {
                Discard(core, plan, created);
                o.Plan = null;
                o.DeferReason = "it couldn't be prepared (" + e.Message + ")";
                return o;
            }
            plan.Summary = Describe(plan, replace.Count, add.Count, remove.Count);
            return o;
        }

        static Outcome Defer(Outcome o, string reason)
        {
            o.DeferReason = reason;
            return o;
        }

        static string Describe(LiveRestorePlan p, int replaced, int added, int removed)
        {
            var parts = new List<string>();
            if (p.Params.Length > 0) parts.Add(p.Params.Length + " parameter" + (p.Params.Length == 1 ? "" : "s"));
            int cols = p.Columns.Count(c => c.Old == null || true);
            if (p.Columns.Length > 0) parts.Add(p.Columns.Length + " pattern column" + (p.Columns.Length == 1 ? "" : "s"));
            if (p.Sequences.Length > 0) parts.Add(p.Sequences.Length + " track" + (p.Sequences.Length == 1 ? "" : "s"));
            if (p.Wires.Length > 0) parts.Add(p.Wires.Length + " wire" + (p.Wires.Length == 1 ? "" : "s"));
            if (p.Flags.Length > 0) parts.Add(p.Flags.Length + " mute/bypass");
            if (replaced > 0) parts.Add(replaced + " machine" + (replaced == 1 ? "" : "s") + " swapped");
            if (added > 0) parts.Add(added + " machine" + (added == 1 ? "" : "s") + " added");
            if (removed > 0) parts.Add(removed + " machine" + (removed == 1 ? "" : "s") + " removed");
            if (p.Bpm > 0) parts.Add("tempo");
            return parts.Count == 0 ? "nothing differs" : string.Join(", ", parts);
        }

        sealed class Steps
        {
            public readonly List<LiveRestorePlan.ParamStep> Params = new();
            public readonly List<LiveRestorePlan.FlagStep> Flags = new();
            public readonly List<LiveRestorePlan.WireStep> Wires = new();
            public readonly List<LiveRestorePlan.ColumnSwap> Columns = new();
            public readonly List<LiveRestorePlan.SeqSwap> Sequences = new();
        }

        // ------------------------------------------------------------------ building

        static void Prepare(ReBuzzCore core, SongCore song, LiveSong now, LiveSong target,
            List<(LiveSong.Machine Now, LiveSong.Machine Then)> inPlace, List<(LiveSong.Machine Now, LiveSong.Machine Then)> replace,
            List<LiveSong.Machine> add, List<LiveSong.Machine> remove, LiveRestorePlan plan, Steps steps, List<MachineCore> created)
        {
            MachineCore Live(string name) => song.MachinesList.FirstOrDefault(m => m.Name == name && !m.Hidden);
            var instance = new Dictionary<string, MachineCore>();      // the machine that plays a name once the restore is done
            var isNew = new HashSet<MachineCore>();
            var savedFocus = core.MIDIFocusMachine;

            // machines that stay
            foreach (var (n, t) in inPlace)
            {
                var m = Live(n.Name);
                if (m == null) throw new InvalidOperationException("\"" + n.Name + "\" is gone");
                instance[t.Name] = m;
            }

            // new machines: swapped ones under a temporary name, added ones under their own
            foreach (var (n, t) in replace)
            {
                var m = CreateHidden(core, t, TempName(song, t.Name));
                created.Add(m); isNew.Add(m); instance[t.Name] = m;
                plan.Replacements.Add((Live(n.Name), m, t.Name));
            }
            foreach (var t in add)
            {
                var m = CreateHidden(core, t, t.Name);
                created.Add(m); isNew.Add(m); instance[t.Name] = m;
                plan.Added.Add(m);
            }
            foreach (var n in remove) plan.Removed.Add(Live(n.Name));
            core.MIDIFocusMachine = savedFocus;

            // the new machines' contents: values, patterns, then wires and tracks
            foreach (var t in replace.Select(x => x.Then).Concat(add))
            {
                var m = instance[t.Name];
                SetState(m, t);
                BuildPatterns(song, m, t);
            }
            var inPlaceThen = inPlace.ToDictionary(x => x.Then.Name, x => x);

            // wires of the new set
            var targetWires = target.Wires.Where(w => instance.ContainsKey(w.From) && instance.ContainsKey(w.To)).ToList();
            foreach (var w in targetWires)
            {
                var src = instance[w.From];
                var dst = instance[w.To];
                bool srcNew = isNew.Contains(src), dstNew = isNew.Contains(dst);
                var existing = srcNew || dstNew ? null : src.AllOutputs.OfType<MachineConnectionCore>().FirstOrDefault(c => c.Destination == dst);
                if (existing != null)
                {
                    if (existing.Amp != w.Amp || existing.Pan != w.Pan)
                        steps.Wires.Add(new LiveRestorePlan.WireStep { Wire = existing, Amp = w.Amp, Pan = w.Pan });
                    continue;
                }
                // A wire into a new machine is on at once (it is being fed, so it is warm); one from a new machine into a machine that
                // stays or one that is new but wired onward is silent until the boundary.
                bool silent = srcNew && !dstNew || !srcNew && !dstNew;
                var c = Connect(core, src, dst, w, silent ? 0 : w.Amp);
                if (silent) steps.Wires.Add(new LiveRestorePlan.WireStep { Wire = c, Amp = w.Amp, Pan = w.Pan });
            }

            // wires that go: from the old machines (and between machines that stay, if the snapshot has none) into machines that stay
            var oldMachines = new HashSet<MachineCore>(plan.Replacements.Select(r => r.Old).Concat(plan.Removed));
            foreach (var m in oldMachines)
                foreach (var c in m.AllOutputs.OfType<MachineConnectionCore>().ToList())
                    if (c.Destination is MachineCore dst && !oldMachines.Contains(dst) && !dst.Hidden)
                        steps.Wires.Add(new LiveRestorePlan.WireStep { Wire = c, Amp = 0, Pan = c.Pan });
            foreach (var (n, t) in inPlace)
            {
                var m = instance[t.Name];
                foreach (var c in m.AllOutputs.OfType<MachineConnectionCore>().ToList())
                {
                    if (c.Destination is not MachineCore dst || dst.Hidden || isNew.Contains(dst) || oldMachines.Contains(dst)) continue;
                    if (!target.Wires.Any(w => w.From == t.Name && w.To == dst.Name))
                    {
                        steps.Wires.Add(new LiveRestorePlan.WireStep { Wire = c, Amp = 0, Pan = c.Pan });
                        plan.Disconnect.Add(c);
                    }
                }
            }

            // tracks (sequences): new machines get theirs now (they play along, silent); machines that stay get their events swapped
            foreach (var t in replace.Select(x => x.Then).Concat(add))
            {
                var m = instance[t.Name];
                foreach (var q in target.Sequences.Where(q => q.Machine == t.Name))
                {
                    song.AddSequence(m, song.SequencesList.Count);
                    var seq = song.SequencesList[song.SequencesList.Count - 1];
                    foreach (var e in q.Events)
                    {
                        var pattern = e.Pattern == null ? null : m.PatternsList.FirstOrDefault(p => p.Name == e.Pattern);
                        seq.SetEvent(e.Time, MakeEvent(e, pattern));
                    }
                }
            }
            foreach (var (n, t) in inPlace)
            {
                var m = instance[t.Name];
                var live = song.SequencesList.Where(q => q.Machine == m).ToList();
                var then = target.Sequences.Where(q => q.Machine == t.Name).ToList();
                for (int i = 0; i < then.Count; i++)
                {
                    if (SameEvents(live[i], then[i], m)) continue;
                    var dict = new Wintellect.PowerCollections.OrderedDictionary<int, SequenceEvent>();
                    foreach (var e in then[i].Events)
                        dict[e.Time] = MakeEvent(e, e.Pattern == null ? null : m.PatternsList.FirstOrDefault(p => p.Name == e.Pattern));
                    steps.Sequences.Add(new LiveRestorePlan.SeqSwap { Sequence = live[i], New = dict, Arranged = SequenceCore.Arrange(dict) });
                }
            }

            // values, flags, events of the machines that stay
            foreach (var (n, t) in inPlace)
            {
                var m = instance[t.Name];
                foreach (var p in t.Params)
                {
                    if (n.IsMaster && IsTransportParameter(m, p)) continue;
                    var parameter = Parameter(m, p.Group, p.Index);
                    if (parameter == null) continue;
                    int from = parameter.GetValue(p.Track);
                    if (from == p.Value || p.Value == parameter.NoValue) continue;
                    steps.Params.Add(new LiveRestorePlan.ParamStep { Parameter = parameter, Track = p.Track, From = from, To = p.Value, Glide = p.Glides });
                }
                if (n.Muted != t.Muted || n.Bypassed != t.Bypassed)
                {
                    var outs = m.AllOutputs.OfType<MachineConnectionCore>().ToArray();
                    steps.Flags.Add(new LiveRestorePlan.FlagStep
                    {
                        Machine = m, Muted = t.Muted, Bypassed = t.Bypassed, MuteChanges = n.Muted != t.Muted, BypassChanges = n.Bypassed != t.Bypassed,
                        Out = outs,
                        // Where each wire ends up: a wire step's amp if the restore also changes it, else where it is now.
                        FinalAmps = outs.Select(c => steps.Wires.Where(w => ReferenceEquals(w.Wire, c)).Select(w => w.Amp).DefaultIfEmpty(c.Amp).Last()).ToArray(),
                    });
                    if (n.Muted != t.Muted)
                        steps.Wires.RemoveAll(w => outs.Contains(w.Wire));          // the mute's own fade takes these wires to their levels
                }
                foreach (var tp in t.Patterns)
                {
                    var pattern = m.PatternsList.FirstOrDefault(p => p.Name == tp.Name);
                    var np = n.Patterns.FirstOrDefault(p => p.Name == tp.Name);
                    if (pattern == null || np == null) continue;
                    foreach (var tc in tp.Columns)
                    {
                        var nc = np.Columns.FirstOrDefault(c => c.Key == tc.Key);
                        if (nc == null || SameEvents(nc.Events, tc.Events)) continue;
                        var column = pattern.ColumnsList.OfType<PatternColumnCore>()
                            .FirstOrDefault(c => (int)c.Type == tc.Type && c.Track == tc.Track && (c.Parameter == null ? -1 : (int)c.Parameter.Group.Type) == tc.Group && (c.Parameter == null ? -1 : c.Parameter.IndexInGroup) == tc.Index);
                        if (column == null) continue;
                        steps.Columns.Add(new LiveRestorePlan.ColumnSwap { Column = column, Pattern = pattern, New = new List<PatternEvent>(tc.Events), Ramps = tc.Events.Any(e => e.Duration > 0) });
                    }
                }
            }
        }

        static bool IsTransportParameter(MachineCore master, LiveSong.Param p) =>
            p.Group == 1 && (p.Index == 1 || p.Index == 2);          // the master's BPM and TPB: tempo is set by the UI thread afterwards

        static ParameterCore Parameter(MachineCore m, int group, int index)
        {
            var groups = m.ParameterGroupsList;
            if (group < 0 || group >= groups.Count) return null;
            var list = groups[group].ParametersList;
            return index >= 0 && index < list.Count ? list[index] as ParameterCore : null;
        }

        static bool SameEvents(List<PatternEvent> a, List<PatternEvent> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Time != b[i].Time || a[i].Value != b[i].Value || a[i].Duration != b[i].Duration) return false;
            return true;
        }

        static bool SameEvents(SequenceCore live, LiveSong.Sequence then, MachineCore m)
        {
            var events = live.EventsList;
            if (events.Count != then.Events.Count) return false;
            int i = 0;
            foreach (var kv in events)
            {
                var e = then.Events[i++];
                var v = kv.Value;
                if (kv.Key != e.Time || (int)v.Type != e.Type || v.Span != e.Span || v.Offset != e.Offset || v.End != e.End || v.Clip != e.Clip || v.Gain != e.Gain || (v.Pattern?.Name) != e.Pattern)
                    return false;
            }
            return true;
        }

        static SequenceEvent MakeEvent(LiveSong.SeqEvent e, PatternCore pattern) =>
            new SequenceEvent((SequenceEventType)e.Type, pattern, e.Span) { Offset = e.Offset, End = e.End, Gain = e.Gain, Clip = e.Clip };

        static string TempName(SongCore song, string name)
        {
            for (int i = 1; ; i++)
            {
                string n = name + " (restore " + i + ")";
                if (!song.MachinesList.Any(m => m.Name == n)) return n;
            }
        }

        static MachineCore CreateHidden(ReBuzzCore core, LiveSong.Machine t, string name)
        {
            var dll = core.MachineDLLs[t.Gear];
            var m = core.MachineManager.CreateMachine(t.Gear, dll.Path, t.Instrument, t.Data, Math.Max(1, t.Tracks), t.X, t.Y, true, name);
            if (m == null) throw new InvalidOperationException("\"" + t.Name + "\" couldn't be created");
            return m;
        }

        static void SetState(MachineCore m, LiveSong.Machine t)
        {
            foreach (var p in t.Params)
            {
                var parameter = Parameter(m, p.Group, p.Index);
                if (parameter != null && p.Value != parameter.NoValue) parameter.SetValue(p.Track | (1 << 16), p.Value);
            }
            m.SetMutedFromRestore(t.Muted);
            m.SetBypassedFromRestore(t.Bypassed);
        }

        static void BuildPatterns(SongCore song, MachineCore m, LiveSong.Machine t)
        {
            foreach (var tp in t.Patterns)
            {
                var pattern = new PatternCore(m, tp.Name, tp.Length, m.Graph.Buzz is ReBuzzCore rb ? rb.dispatcher : null);
                lock (ReBuzzCore.AudioLock) m.PatternsList.Add(pattern);
                foreach (var tc in tp.Columns)
                {
                    if (tc.Type == (int)PatternColumnType.MIDI)
                        pattern.InsertColumn(pattern.ColumnsList.Count, (IMachine)null);
                    else
                    {
                        var parameter = Parameter(m, tc.Group, tc.Index);
                        if (parameter == null) continue;
                        pattern.InsertColumn(pattern.ColumnsList.Count, parameter, tc.Track, new Dictionary<string, string>(tc.Meta));
                    }
                    if (pattern.ColumnsList[pattern.ColumnsList.Count - 1] is PatternColumnCore col && tc.Events.Count > 0)
                        col.SetEvents(tc.Events, true);
                }
            }
        }

        static MachineConnectionCore Connect(ReBuzzCore core, MachineCore src, MachineCore dst, LiveSong.Wire w, int amp)
        {
            new ConnectMachinesAction(core, src, dst, new[] { w.SourceChannel }, new[] { w.DestinationChannel }, amp, w.Pan, core.dispatcher, core.EngineSettings).Do();
            return src.AllOutputs.OfType<MachineConnectionCore>().Last(c => c.Destination == dst);
        }

        /// <summary>Takes back what <see cref="Build"/> made (a failure part way, a plan that never applied).</summary>
        internal static void Discard(ReBuzzCore core, LiveRestorePlan plan, List<MachineCore> created)
        {
            var song = core.SongCore;
            foreach (var m in created.Where(m => m != null).Distinct().ToList())
            {
                foreach (var seq in song.SequencesList.Where(q => q.Machine == m).ToList())
                    song.RemoveSequence(seq);
                new DeleteMachinesAction(core, new List<IMachine> { m }, core.dispatcher, core.EngineSettings).Do();
            }
        }

        // ------------------------------------------------------------------ finishing (UI thread, after the boundary)

        /// <summary>Everything the audio thread left to the UI thread: reports, tempo, the old machines out, the new ones shown and named.</summary>
        internal static void Finish(ReBuzzCore core, LiveRestorePlan plan)
        {
            var song = core.SongCore;
            foreach (var f in plan.Flags)
            {
                if (f.MuteChanges && f.Muted)
                    lock (ReBuzzCore.AudioLock)
                    {
                        f.Machine.SetMutedFromRestore(true);                       // faded out by now: the wires go back to their levels, unheard
                        for (int w = 0; w < f.Out.Length; w++) { f.Out[w].Amp = f.FinalAmps[w]; f.Out[w].SnapAmp(); }
                    }
                f.Machine.ReportRestoredFlags();
            }
            foreach (var c in plan.Columns) c.Pattern.NotifyPatternChanged();
            foreach (var s in plan.Sequences) s.Sequence.InvokeEvents();
            // Glide values that didn't get to their end (the transport stopped meanwhile) are set now.
            foreach (var p in plan.Params)
                if (p.Parameter.GetValue(p.Track) != p.To) p.Parameter.SetValue(p.Track | (1 << 16), p.To);
            if (plan.Bpm > 0) core.BPM = plan.Bpm;

            foreach (var c in plan.Disconnect)
                if (c.Source != null && c.Destination != null)
                    new DisconnectMachinesAction(core, c, core.dispatcher, core.EngineSettings).Do();

            foreach (var (old, _, _) in plan.Replacements) Delete(core, old);
            foreach (var m in plan.Removed) Delete(core, m);
            foreach (var (_, m, name) in plan.Replacements)
            {
                core.RenameMachine(m, name);
                Show(core, m);
            }
            foreach (var m in plan.Added) Show(core, m);
            core.Modified = true;
        }

        static void Delete(ReBuzzCore core, MachineCore m)
        {
            if (m == null || !core.SongCore.MachinesList.Contains(m)) return;
            new DeleteMachinesAction(core, new List<IMachine> { m }, core.dispatcher, core.EngineSettings).Do();
        }

        static void Show(ReBuzzCore core, MachineCore m)
        {
            m.Hidden = false;
            core.InvokeMachineAdded(m);
        }
    }
}
