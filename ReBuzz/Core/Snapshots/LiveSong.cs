// Fork (docs/specs/history-snapshots.md HS3): what a live restore needs from a snapshot, kept beside its song-file sections as
// one more section (LIVE): each machine's saved data, parameter values, patterns' events, the wires and the sequences, in a
// small binary form the plan can compare with the song as it is now, and the executor can build machines and event lists from.
// Taken on the UI thread from live objects (the machine saves a save does; nothing on the audio thread). Song files never see it:
// the section is not part of the assembled file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core.SongHistory
{
    internal sealed class LiveSong
    {
        /// <summary>The section's magic ("LIVE"); <see cref="SongSnapshots.Assemble"/> leaves it out of the song file.</summary>
        public const uint Magic = 0x4556494C;
        const byte Version = 1;

        public sealed class Param
        {
            public int Group, Index, Track, Value;
            public bool Glides;          // a continuous parameter (a wide range): the restore glides it instead of stepping it
        }

        public sealed class Column
        {
            public int Type;             // PatternColumnType
            public int Group = -1;       // the parameter's group type, -1 for a MIDI column
            public int Index = -1;
            public int Track;
            public List<PatternEvent> Events = new();
            public Dictionary<string, string> Meta = new();
            public string Key => Type + ":" + Group + ":" + Index + ":" + Track;
        }

        public sealed class Pattern
        {
            public string Name;
            public int Length;
            public byte[] Data;
            public List<Column> Columns = new();
        }

        public sealed class Machine
        {
            public string Name, Gear, Instrument, EditorGear;
            public int Tracks;
            public float X, Y;
            public bool Muted, Bypassed, IsMaster, IsNative, IsMissing;
            public byte[] Data, EditorData;
            public List<Param> Params = new();
            public List<Pattern> Patterns = new();

            /// <summary>What can't change in place: the gear, its tracks, saved data, the pattern editor's data and the patterns' shape (names, lengths, data, column layout).</summary>
            public string Fingerprint { get; private set; }

            public void Seal()
            {
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                void S(string s) { var b = Encoding.UTF8.GetBytes(s ?? ""); h.AppendData(BitConverter.GetBytes(b.Length)); h.AppendData(b); }
                void B(byte[] b) { h.AppendData(BitConverter.GetBytes(b?.Length ?? -1)); if (b != null) h.AppendData(b); }
                void I(int v) => h.AppendData(BitConverter.GetBytes(v));
                S(Gear); S(Instrument); I(Tracks); B(Data); S(EditorGear); B(EditorData); I(Patterns.Count);
                foreach (var p in Patterns.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    S(p.Name); I(p.Length); B(p.Data);
                    foreach (var c in p.Columns.OrderBy(c => c.Key, StringComparer.Ordinal)) S(c.Key);
                }
                Fingerprint = Convert.ToHexString(h.GetHashAndReset());
            }
        }

        public sealed class Wire
        {
            public string From, To;
            public int Amp, Pan, SourceChannel, DestinationChannel;
            public string Key => From + ">" + To;
        }

        public sealed class SeqEvent
        {
            public int Time, Type, Span, Offset, End, Clip;
            public double Gain = 1;
            public string Pattern;
        }

        public sealed class Sequence
        {
            public string Machine;
            public List<SeqEvent> Events = new();
        }

        public int Bpm, Tpb;
        public List<Machine> Machines = new();
        public List<Wire> Wires = new();
        public List<Sequence> Sequences = new();

        public Machine Find(string name) => Machines.FirstOrDefault(m => m.Name == name);

        // ------------------------------------------------------------------ taking

        /// <summary>UI thread. The song as it is now (hidden machines, the pattern editors, are part of their machine's data).</summary>
        public static LiveSong Take(ReBuzzCore core)
        {
            var s = new LiveSong { Bpm = core.BPM, Tpb = core.TPB };
            var song = core.SongCore;
            foreach (var m in song.MachinesList.Where(m => !m.Hidden && m.Ready).ToList())
            {
                var lm = new Machine
                {
                    Name = m.Name,
                    Gear = m.DLL?.Name ?? "",
                    Instrument = m.InstrumentName,
                    Tracks = m.TrackCount,
                    X = m.Position.Item1,
                    Y = m.Position.Item2,
                    Muted = m.IsMuted,
                    Bypassed = m.IsBypassed,
                    IsMaster = m.DLL?.Info.Type == MachineType.Master,
                    IsMissing = m.DLL?.IsMissing ?? false,
                    IsNative = !(m.DLL?.IsManaged ?? true),
                };
                if (!lm.IsMaster)
                {
                    try { lm.Data = m.Data; } catch (Exception) { lm.Data = null; }
                    lm.EditorGear = m.PatternEditorDLL?.Name;
                    if (m.EditorMachine != null)
                    {
                        try { lm.EditorData = m.PatternEditorData; } catch (Exception) { lm.EditorData = null; }
                    }
                }
                for (int g = 1; g <= 2 && g < m.ParameterGroups.Count; g++)
                {
                    var group = m.ParameterGroups[g];
                    int tracks = g == 1 ? 1 : group.TrackCount;
                    for (int t = 0; t < tracks; t++)
                        foreach (var p in group.Parameters)
                            if ((p.Flags & ParameterFlags.State) != 0 && p.Type != ParameterType.Internal)
                                lm.Params.Add(new Param { Group = g, Index = p.IndexInGroup, Track = t, Value = p.GetValue(t), Glides = IsContinuous(p) });
                }
                foreach (var pat in m.PatternsList)
                {
                    var lp = new Pattern { Name = pat.Name, Length = pat.Length, Data = pat.Data };
                    foreach (var col in pat.ColumnsList.OfType<PatternColumnCore>())
                    {
                        var lc = new Column
                        {
                            Type = (int)col.Type,
                            Group = col.Parameter == null ? -1 : (int)col.Parameter.Group.Type,
                            Index = col.Parameter == null ? -1 : col.Parameter.IndexInGroup,
                            Track = col.Track,
                            Events = col.CopyEvents(),
                            Meta = new Dictionary<string, string>(col.Metadata),
                        };
                        lp.Columns.Add(lc);
                    }
                    lm.Patterns.Add(lp);
                }
                lm.Seal();
                s.Machines.Add(lm);
                foreach (var o in m.AllOutputs.OfType<MachineConnectionCore>().Where(o => !((MachineCore)o.Destination).Hidden))
                    s.Wires.Add(new Wire { From = m.Name, To = o.Destination.Name, Amp = o.Amp, Pan = o.Pan, SourceChannel = o.SourceChannel, DestinationChannel = o.DestinationChannel });
            }
            foreach (var seq in song.SequencesList)
            {
                if (seq.MachineCore != null && seq.MachineCore.Hidden) continue;       // a live restore's machine under construction
                var ls = new Sequence { Machine = seq.Machine?.Name };
                foreach (var kv in seq.EventsList)
                    ls.Events.Add(new SeqEvent
                    {
                        Time = kv.Key, Type = (int)kv.Value.Type, Span = kv.Value.Span, Offset = kv.Value.Offset, End = kv.Value.End,
                        Clip = kv.Value.Clip, Gain = kv.Value.Gain, Pattern = kv.Value.Pattern?.Name,
                    });
                s.Sequences.Add(ls);
            }
            return s;
        }

        /// <summary>A parameter a restore glides rather than steps: a wide numeric range (cutoff, volume), not a switch or a selector.</summary>
        static bool IsContinuous(IParameter p) =>
            p.Type is ParameterType.Word or ParameterType.Byte && (long)p.MaxValue - p.MinValue > 32;

        // ------------------------------------------------------------------ the binary form

        public byte[] Write()
        {
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                w.Write(Version);
                w.Write(Bpm); w.Write(Tpb);
                w.Write(Machines.Count);
                foreach (var m in Machines)
                {
                    w.Write(m.Name ?? ""); w.Write(m.Gear ?? ""); WriteN(w, m.Instrument); WriteN(w, m.EditorGear);
                    w.Write(m.Tracks); w.Write(m.X); w.Write(m.Y);
                    w.Write(m.Muted); w.Write(m.Bypassed); w.Write(m.IsMaster); w.Write(m.IsNative); w.Write(m.IsMissing);
                    WriteB(w, m.Data); WriteB(w, m.EditorData);
                    w.Write(m.Params.Count);
                    foreach (var p in m.Params) { w.Write(p.Group); w.Write(p.Index); w.Write(p.Track); w.Write(p.Value); w.Write(p.Glides); }
                    w.Write(m.Patterns.Count);
                    foreach (var p in m.Patterns)
                    {
                        w.Write(p.Name ?? ""); w.Write(p.Length); WriteB(w, p.Data);
                        w.Write(p.Columns.Count);
                        foreach (var c in p.Columns)
                        {
                            w.Write(c.Type); w.Write(c.Group); w.Write(c.Index); w.Write(c.Track);
                            w.Write(c.Meta.Count);
                            foreach (var kv in c.Meta) { w.Write(kv.Key ?? ""); w.Write(kv.Value ?? ""); }
                            w.Write(c.Events.Count);
                            foreach (var e in c.Events) { w.Write(e.Time); w.Write(e.Value); w.Write(e.Duration); }
                        }
                    }
                }
                w.Write(Wires.Count);
                foreach (var x in Wires) { w.Write(x.From); w.Write(x.To); w.Write(x.Amp); w.Write(x.Pan); w.Write(x.SourceChannel); w.Write(x.DestinationChannel); }
                w.Write(Sequences.Count);
                foreach (var q in Sequences)
                {
                    WriteN(w, q.Machine);
                    w.Write(q.Events.Count);
                    foreach (var e in q.Events)
                    {
                        w.Write(e.Time); w.Write(e.Type); w.Write(e.Span); w.Write(e.Offset); w.Write(e.End); w.Write(e.Clip); w.Write(e.Gain); WriteN(w, e.Pattern);
                    }
                }
            }
            return ms.ToArray();
        }

        static void WriteN(BinaryWriter w, string s) { w.Write(s != null); if (s != null) w.Write(s); }
        static string ReadN(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;
        static void WriteB(BinaryWriter w, byte[] b) { w.Write(b != null); if (b != null) { w.Write(b.Length); w.Write(b); } }
        static byte[] ReadB(BinaryReader r) => r.ReadBoolean() ? r.ReadBytes(r.ReadInt32()) : null;

        /// <summary>Reads a section; null when it is another version (a newer build's) or damaged.</summary>
        public static LiveSong Read(byte[] data)
        {
            try
            {
                using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
                if (r.ReadByte() != Version) return null;
                var s = new LiveSong { Bpm = r.ReadInt32(), Tpb = r.ReadInt32() };
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    var m = new Machine { Name = r.ReadString(), Gear = r.ReadString(), Instrument = ReadN(r), EditorGear = ReadN(r) };
                    m.Tracks = r.ReadInt32(); m.X = r.ReadSingle(); m.Y = r.ReadSingle();
                    m.Muted = r.ReadBoolean(); m.Bypassed = r.ReadBoolean(); m.IsMaster = r.ReadBoolean(); m.IsNative = r.ReadBoolean(); m.IsMissing = r.ReadBoolean();
                    m.Data = ReadB(r); m.EditorData = ReadB(r);
                    int np = r.ReadInt32();
                    for (int k = 0; k < np; k++) m.Params.Add(new Param { Group = r.ReadInt32(), Index = r.ReadInt32(), Track = r.ReadInt32(), Value = r.ReadInt32(), Glides = r.ReadBoolean() });
                    int npat = r.ReadInt32();
                    for (int k = 0; k < npat; k++)
                    {
                        var p = new Pattern { Name = r.ReadString(), Length = r.ReadInt32(), Data = ReadB(r) };
                        int nc = r.ReadInt32();
                        for (int c = 0; c < nc; c++)
                        {
                            var col = new Column { Type = r.ReadInt32(), Group = r.ReadInt32(), Index = r.ReadInt32(), Track = r.ReadInt32() };
                            int nm = r.ReadInt32();
                            for (int k2 = 0; k2 < nm; k2++) col.Meta[r.ReadString()] = r.ReadString();
                            int ne = r.ReadInt32();
                            for (int e = 0; e < ne; e++) col.Events.Add(new PatternEvent(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
                            p.Columns.Add(col);
                        }
                        m.Patterns.Add(p);
                    }
                    m.Seal();
                    s.Machines.Add(m);
                }
                int nw = r.ReadInt32();
                for (int i = 0; i < nw; i++)
                    s.Wires.Add(new Wire { From = r.ReadString(), To = r.ReadString(), Amp = r.ReadInt32(), Pan = r.ReadInt32(), SourceChannel = r.ReadInt32(), DestinationChannel = r.ReadInt32() });
                int nq = r.ReadInt32();
                for (int i = 0; i < nq; i++)
                {
                    var q = new Sequence { Machine = ReadN(r) };
                    int ne = r.ReadInt32();
                    for (int e = 0; e < ne; e++)
                        q.Events.Add(new SeqEvent { Time = r.ReadInt32(), Type = r.ReadInt32(), Span = r.ReadInt32(), Offset = r.ReadInt32(), End = r.ReadInt32(), Clip = r.ReadInt32(), Gain = r.ReadDouble(), Pattern = ReadN(r) });
                    s.Sequences.Add(q);
                }
                return s;
            }
            catch (Exception e) when (e is IOException or EndOfStreamException or ArgumentException or OverflowException)
            {
                return null;
            }
        }
    }
}
