// Fork (docs/specs/history-snapshots.md HS2): what a snapshot lists about the song, kept in its manifest so a
// diff needs no loading: machines, wires, parameters, patterns (by content hash), sequences (by hash), waves.
// Taken on the UI thread from live objects (small values only; no audio data is walked).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;

namespace ReBuzz.Core.SongHistory
{
    internal sealed class SongSummary
    {
        public sealed class ParamValue
        {
            public int G { get; set; }
            public int I { get; set; }
            public int T { get; set; }
            public string N { get; set; }
            public int V { get; set; }
        }

        public sealed class PatternInfo
        {
            public string Name { get; set; }
            public int Length { get; set; }
            public string Hash { get; set; }
        }

        public sealed class MachineInfo
        {
            public string Name { get; set; }
            public string Gear { get; set; }
            public bool Muted { get; set; }
            public bool Bypassed { get; set; }
            public List<ParamValue> Params { get; set; } = new();
            public List<PatternInfo> Patterns { get; set; } = new();
        }

        public int Bpm { get; set; }
        public int Tpb { get; set; }
        public List<MachineInfo> Machines { get; set; } = new();
        /// <summary>"Source>Destination amp pan".</summary>
        public List<string> Wires { get; set; } = new();
        /// <summary>"Machine#index: events hash".</summary>
        public List<string> Sequences { get; set; } = new();
        public List<string> Waves { get; set; } = new();

        static string Hash(Action<IncrementalHash> feed)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            feed(h);
            return Convert.ToHexString(h.GetHashAndReset()).Substring(0, 16).ToLowerInvariant();
        }

        static void Add(IncrementalHash h, int v) => h.AppendData(BitConverter.GetBytes(v));

        public static SongSummary Take(ReBuzzCore core)
        {
            var s = new SongSummary { Bpm = core.BPM, Tpb = core.TPB };
            var song = core.SongCore;
            foreach (var m in song.MachinesList.Where(m => !m.Hidden))
            {
                var ms = new MachineInfo { Name = m.Name, Gear = m.DLL?.Name ?? "", Muted = m.IsMuted, Bypassed = m.IsBypassed };
                for (int g = 1; g <= 2 && g < m.ParameterGroups.Count; g++)
                {
                    var group = m.ParameterGroups[g];
                    int tracks = g == 1 ? 1 : group.TrackCount;
                    for (int t = 0; t < tracks; t++)
                        foreach (var p in group.Parameters)
                            if ((p.Flags & ParameterFlags.State) != 0 && p.Type != ParameterType.Internal)
                                ms.Params.Add(new ParamValue { G = g, I = p.IndexInGroup, T = t, N = p.Name, V = p.GetValue(t) });
                }
                foreach (var pat in m.PatternsList)
                {
                    string hash = Hash(h =>
                    {
                        foreach (var col in pat.Columns)
                        {
                            h.AppendData(System.Text.Encoding.UTF8.GetBytes(col.Parameter?.Name ?? "midi"));
                            Add(h, col.Track);
                            foreach (var e in col.GetEvents(0, Math.Max(1, pat.Length) * PatternEvent.TimeBase)) { Add(h, e.Time); Add(h, e.Value); Add(h, e.Duration); }
                        }
                        if (pat.Data != null) h.AppendData(pat.Data);
                    });
                    ms.Patterns.Add(new PatternInfo { Name = pat.Name, Length = pat.Length, Hash = hash });
                }
                s.Machines.Add(ms);
                foreach (var o in m.Outputs.Where(o => !((MachineCore)o.Destination).Hidden))
                    s.Wires.Add($"{m.Name}>{o.Destination.Name} {o.Amp} {o.Pan}");
            }
            int n = 0;
            foreach (var seq in song.SequencesList)
            {
                string hash = Hash(h =>
                {
                    foreach (var kv in seq.Events)
                    {
                        Add(h, kv.Key); Add(h, (int)kv.Value.Type); Add(h, kv.Value.Span); Add(h, kv.Value.Offset);
                        h.AppendData(System.Text.Encoding.UTF8.GetBytes(kv.Value.Pattern?.Name ?? ""));
                    }
                });
                s.Sequences.Add($"{seq.Machine?.Name}#{n++}: {seq.Events.Count} events {hash}");
            }
            foreach (var w in song.WavetableCore.WavesList.Where(w => w != null))
                s.Waves.Add($"{w.Index}: {w.Name} ({string.Join("+", w.Layers.Select(l => l.SampleCount.ToString(CultureInfo.InvariantCulture)))} samples)");
            return s;
        }

        public sealed record Change(string Area, string Subject, string Text);

        /// <summary>What differs going from <paramref name="a"/> to <paramref name="b"/> (live: the machines to describe values with, or null).</summary>
        public static List<Change> Diff(SongSummary a, SongSummary b, Func<string, IMachine> live = null)
        {
            var list = new List<Change>();
            if (a.Bpm != b.Bpm || a.Tpb != b.Tpb)
                list.Add(new Change("song", "tempo", $"{a.Bpm} BPM, {a.Tpb} TPB → {b.Bpm} BPM, {b.Tpb} TPB"));
            var am = a.Machines.ToDictionary(m => m.Name);
            var bm = b.Machines.ToDictionary(m => m.Name);
            foreach (var name in am.Keys.Union(bm.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                am.TryGetValue(name, out var x);
                bm.TryGetValue(name, out var y);
                if (x == null) { list.Add(new Change("machines", name, $"added ({y.Gear})")); continue; }
                if (y == null) { list.Add(new Change("machines", name, $"removed ({x.Gear})")); continue; }
                if (x.Gear != y.Gear) list.Add(new Change("machines", name, $"{x.Gear} → {y.Gear}"));
                if (x.Muted != y.Muted) list.Add(new Change("machines", name, y.Muted ? "muted" : "unmuted"));
                if (x.Bypassed != y.Bypassed) list.Add(new Change("machines", name, y.Bypassed ? "bypassed" : "not bypassed"));
                var live1 = live?.Invoke(name);
                string Show(ParamValue p, int v)
                {
                    try
                    {
                        if (live1 != null && p.G < live1.ParameterGroups.Count && p.I < live1.ParameterGroups[p.G].Parameters.Count)
                            return SongModel.Describe(live1.ParameterGroups[p.G].Parameters[p.I], v);
                    }
                    catch (Exception) { }
                    return v.ToString(CultureInfo.InvariantCulture);
                }
                var yp = y.Params.ToDictionary(p => (p.G, p.I, p.T), p => p);
                foreach (var p in x.Params)
                    if (yp.TryGetValue((p.G, p.I, p.T), out var q) && q.V != p.V)
                        list.Add(new Change("parameters", name, $"{p.N}{(p.G == 2 ? $" [track {p.T}]" : "")}: {Show(p, p.V)} → {Show(q, q.V)}"));
                var xpat = x.Patterns.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());
                var ypat = y.Patterns.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());
                foreach (var pn in xpat.Keys.Union(ypat.Keys).OrderBy(k => k, StringComparer.Ordinal))
                {
                    xpat.TryGetValue(pn, out var p1);
                    ypat.TryGetValue(pn, out var p2);
                    if (p1 == null) list.Add(new Change("patterns", name, $"pattern {pn} added"));
                    else if (p2 == null) list.Add(new Change("patterns", name, $"pattern {pn} removed"));
                    else if (p1.Hash != p2.Hash || p1.Length != p2.Length) list.Add(new Change("patterns", name, $"pattern {pn} edited"));
                }
            }
            foreach (var w in a.Wires.Except(b.Wires).Concat(b.Wires.Except(a.Wires)).Select(Wire).Distinct().OrderBy(k => k, StringComparer.Ordinal))
            {
                bool inA = a.Wires.Any(x => Wire(x) == w), inB = b.Wires.Any(x => Wire(x) == w);
                string was = a.Wires.FirstOrDefault(x => Wire(x) == w), now = b.Wires.FirstOrDefault(x => Wire(x) == w);
                list.Add(new Change("wires", w, inA && inB ? $"gain/pan {Levels(was)} → {Levels(now)}" : inB ? "connected" : "disconnected"));
            }
            var aq = a.Sequences.ToHashSet();
            var bq = b.Sequences.ToHashSet();
            foreach (var q in aq.Except(bq).Concat(bq.Except(aq)).Select(q => q.Substring(0, q.IndexOf(':'))).Distinct())
                list.Add(new Change("sequence", q, "arrangement changed"));
            foreach (var w in a.Waves.Except(b.Waves).Concat(b.Waves.Except(a.Waves)).Distinct())
                list.Add(new Change("waves", w, b.Waves.Contains(w) ? "in the second only" : "in the first only"));
            return list;
        }

        static string Wire(string w) => w.Substring(0, w.LastIndexOf(' ', w.LastIndexOf(' ') - 1));
        static string Levels(string w) => w == null ? "" : w.Substring(Wire(w).Length + 1);
    }
}
