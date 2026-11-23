#nullable disable
// Fork (docs/specs/midi-mapping.md, MM1): the MIDI mapping model. Pure data with no ReBuzz types, so ReBuzz.SessionTests compiles this file and
// the maths next to it (MapMath.cs) and runs them on their own.
//
//   source   = (device, message kind, channel, number): a control of a controller. A device is named by its port name (plus " #2" for a
//              second one of the same name); an empty device name is "any device" (what an old Buzz binding means).
//   target   = a parameter (machine, parameter, track) or a command (play, stop, ..., select track n).
//   entry    = source + target + range, invert, takeover and the encoder the control sends.
//   pages    = per track (the track model's id, "track:Bass"): pages of slots (knob 1..N of a device's knob group) and the direct entries
//              that apply while that track is selected. Saved in the song.
//   global   = entries that apply whatever is selected: per computer, with an optional copy in the song.
//   groups   = per device: which controls are its knobs, in order (learned once). Per computer.
//
// A published map is read on the MIDI thread through the router's snapshot: the model itself is edited as clones.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace ReBuzz.Midi.Mapping
{
    public enum MapKind : byte { ControlChange, Note, PitchBend, ProgramChange }

    /// <summary>What a control sends: its position (absolute) or steps (relative: 64 plus or minus n, or 1 / 127 for a step up or down).</summary>
    public enum KnobEncoding : byte { Absolute, Offset64, TwosComplement }

    /// <summary>What happens when the control and the parameter disagree: wait until the control passes the value, jump to it, or scale so both meet at the ends.</summary>
    public enum Takeover : byte { Pickup, Jump, Scale }

    public enum MapCommand : byte { Play, Stop, Record, Loop, PreviousTrack, NextTrack, SelectTrack, Arm, Mute, Solo }

    /// <summary>A control of a controller.</summary>
    public readonly record struct MapSource(string Device, MapKind Kind, int Channel, int Number)
    {
        /// <summary>Any device: a control that is matched whichever controller sends it.</summary>
        public bool AnyDevice => string.IsNullOrEmpty(Device);

        public bool IsContinuous => Kind == MapKind.ControlChange || Kind == MapKind.PitchBend;

        /// <summary>The control without the device: "CC 21", "Note 36", "Bend", "PC 5" (channel 1 is not said).</summary>
        public string ShortControl
        {
            get
            {
                string text = Kind switch
                {
                    MapKind.ControlChange => "CC " + Number.ToString(CultureInfo.InvariantCulture),
                    MapKind.Note => "Note " + Number.ToString(CultureInfo.InvariantCulture),
                    MapKind.PitchBend => "Bend",
                    _ => "PC " + Number.ToString(CultureInfo.InvariantCulture),
                };
                return Channel == 0 ? text : text + " ch" + (Channel + 1).ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>The shortest form, for a badge on a small control: "CC21", "N36", "Bend", "PC5" (no channel, no device: the badge's tooltip and the popover say them).</summary>
        public string Compact => Kind switch
        {
            MapKind.ControlChange => "CC" + Number.ToString(CultureInfo.InvariantCulture),
            MapKind.Note => "N" + Number.ToString(CultureInfo.InvariantCulture),
            MapKind.PitchBend => "Bend",
            _ => "PC" + Number.ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>"CC 21 · Name": the control and, when it names one, the device.</summary>
        public string Short(string label = null) => (label ?? ShortControl) + (AnyDevice ? "" : " · " + Device);

        public override string ToString() => Short();

        /// <summary>
        /// Decodes a short MIDI message (status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16). <paramref name="value"/> is the controller's value or the note's
        /// velocity (a note off, and a note on with velocity 0, is 0); <paramref name="max"/> is its largest (127; 16383 for a pitch bend).
        /// Aftertouch, system messages and the rest are not controls.
        /// </summary>
        public static bool TryDecode(int data, out MapKind kind, out int channel, out int number, out int value, out int max)
        {
            int status = data & 0xFF;
            int d1 = (data >> 8) & 0x7F, d2 = (data >> 16) & 0x7F;
            channel = status & 0x0F;
            max = 127;
            switch (status & 0xF0)
            {
                case 0x90: kind = MapKind.Note; number = d1; value = d2; return true;
                case 0x80: kind = MapKind.Note; number = d1; value = 0; return true;
                case 0xB0: kind = MapKind.ControlChange; number = d1; value = d2; return true;
                case 0xE0: kind = MapKind.PitchBend; number = 0; value = d1 | (d2 << 7); max = 16383; return true;
                case 0xC0: kind = MapKind.ProgramChange; number = d1; value = 127; return true;
                default: kind = default; number = 0; value = 0; return false;
            }
        }

        /// <summary>The same control on its own, whichever device: what a lookup by control (not device) means.</summary>
        public MapSource OnAnyDevice() => this with { Device = "" };

        public static string KindText(MapKind kind) => kind switch { MapKind.ControlChange => "cc", MapKind.Note => "note", MapKind.PitchBend => "bend", _ => "pc" };

        public static bool TryKind(string text, out MapKind kind)
        {
            switch ((text ?? "").ToLowerInvariant())
            {
                case "cc": kind = MapKind.ControlChange; return true;
                case "note": kind = MapKind.Note; return true;
                case "bend": case "pitchbend": kind = MapKind.PitchBend; return true;
                case "pc": case "program": kind = MapKind.ProgramChange; return true;
                default: kind = default; return false;
            }
        }
    }

    /// <summary>What an entry drives: a parameter (a machine's, by names), or a command.</summary>
    public sealed class MapTarget : IEquatable<MapTarget>
    {
        /// <summary>The parameter key of a track parameter starts with this (a global's is its name), as the pins and locks do.</summary>
        public const string TrackPrefix = "T:";

        MapTarget() { }

        public static MapTarget Parameter(string machine, string param, int track = 0) => new MapTarget { Machine = machine ?? "", Param = param ?? "", Track = Math.Max(0, track) };

        public static MapTarget Command(MapCommand command, int index = 0) => new MapTarget { Cmd = command, Index = Math.Max(0, index) };

        public bool IsCommand => Cmd.HasValue;
        public MapCommand? Cmd { get; private init; }
        /// <summary>The track number of Select track (1 is the first).</summary>
        public int Index { get; private init; }
        public string Machine { get; private init; } = "";
        public string Param { get; private init; } = "";
        public int Track { get; private init; }

        public bool IsTrackParam => Param.StartsWith(TrackPrefix, StringComparison.Ordinal);

        /// <summary>The parameter's name without the key's prefix.</summary>
        public string ParamName => IsTrackParam ? Param.Substring(TrackPrefix.Length) : Param;

        public MapTarget OnMachine(string machine) => IsCommand ? this : Parameter(machine, Param, Track);

        public bool Equals(MapTarget other) =>
            other != null && Cmd == other.Cmd && Index == other.Index && Track == other.Track &&
            string.Equals(Machine, other.Machine, StringComparison.Ordinal) && string.Equals(Param, other.Param, StringComparison.Ordinal);
        public override bool Equals(object obj) => Equals(obj as MapTarget);
        public override int GetHashCode() => HashCode.Combine(Cmd, Index, Machine, Param, Track);

        public static string CommandText(MapCommand command, int index = 0) => command switch
        {
            MapCommand.Play => "Play",
            MapCommand.Stop => "Stop",
            MapCommand.Record => "Record",
            MapCommand.Loop => "Loop",
            MapCommand.PreviousTrack => "Previous track",
            MapCommand.NextTrack => "Next track",
            MapCommand.SelectTrack => "Select track " + Math.Max(1, index).ToString(CultureInfo.InvariantCulture),
            MapCommand.Arm => "Arm the selected track",
            MapCommand.Mute => "Mute the selected track",
            _ => "Solo the selected track",
        };

        /// <summary>"Neo Delay · Feedback", or the command's name.</summary>
        public string Text => IsCommand ? CommandText(Cmd.Value, Index) : Machine + " · " + ParamName;

        public override string ToString() => Text;

        internal void Write(XElement e)
        {
            if (IsCommand)
            {
                e.SetAttributeValue("command", Cmd.Value.ToString());
                if (Cmd.Value == MapCommand.SelectTrack) e.SetAttributeValue("index", Index);
                return;
            }
            e.SetAttributeValue("machine", Machine);
            e.SetAttributeValue("param", Param);
            if (Track != 0) e.SetAttributeValue("track", Track);
        }

        internal static MapTarget Read(XElement e)
        {
            string command = (string)e.Attribute("command");
            if (command != null)
                return Enum.TryParse<MapCommand>(command, out var c) ? Command(c, Math.Max(1, MapXml.Int(e, "index", 1))) : null;
            string machine = (string)e.Attribute("machine"), param = (string)e.Attribute("param");
            if (string.IsNullOrEmpty(machine) || string.IsNullOrEmpty(param)) return null;
            return Parameter(machine, param, Math.Clamp(MapXml.Int(e, "track", 0), 0, 255));
        }
    }

    /// <summary>
    /// How a control drives a target: the part of an entry that does not say which control. A page's slot is one of these (the knob is the
    /// device's, the slot's number says which).
    /// </summary>
    public class MapDrive
    {
        public MapTarget Target { get; set; }
        /// <summary>The part of the parameter's range the control covers, 0 to 1 (low end first).</summary>
        public double Min { get; set; }
        public double Max { get; set; } = 1;
        public bool Invert { get; set; }
        public Takeover Takeover { get; set; } = Takeover.Pickup;

        public bool IsFullRange => Min <= 0 && Max >= 1;

        public virtual MapDrive CloneDrive() => (MapDrive)MemberwiseClone();

        /// <summary>The range as the popover says it: "full", or "0.2 to 0.8" of the parameter's span (as percentages).</summary>
        public string RangeText => IsFullRange ? "full" : Math.Round(Min * 100).ToString(CultureInfo.InvariantCulture) + "–" + Math.Round(Max * 100).ToString(CultureInfo.InvariantCulture) + " %";

        internal void WriteDrive(XElement e)
        {
            Target?.Write(e);
            if (Min > 0) e.SetAttributeValue("min", Min.ToString("0.####", CultureInfo.InvariantCulture));
            if (Max < 1) e.SetAttributeValue("max", Max.ToString("0.####", CultureInfo.InvariantCulture));
            if (Invert) e.SetAttributeValue("invert", "true");
            if (Takeover != Takeover.Pickup) e.SetAttributeValue("takeover", Takeover.ToString().ToLowerInvariant());
        }

        internal void ReadDrive(XElement e)
        {
            Min = Math.Clamp(MapXml.Dbl(e, "min", 0), 0, 1);
            Max = Math.Clamp(MapXml.Dbl(e, "max", 1), 0, 1);
            if (Max < Min) (Min, Max) = (Max, Min);
            Invert = (string)e.Attribute("invert") == "true";
            Takeover = Enum.TryParse<Takeover>((string)e.Attribute("takeover"), true, out var t) ? t : Takeover.Pickup;
        }
    }

    /// <summary>One mapping: a control, what it drives, and how.</summary>
    public sealed class MapEntry : MapDrive
    {
        public MapSource Source { get; set; }
        public KnobEncoding Encoding { get; set; }

        public MapEntry Clone() => (MapEntry)MemberwiseClone();

        public override MapDrive CloneDrive() => Clone();

        /// <summary>The same drive, from another control.</summary>
        public MapEntry From(MapSource source) { var e = Clone(); e.Source = source; return e; }

        public XElement ToXml()
        {
            var e = new XElement("Map",
                new XAttribute("device", Source.Device ?? ""), new XAttribute("kind", MapSource.KindText(Source.Kind)),
                new XAttribute("channel", Source.Channel + 1), new XAttribute("number", Source.Number));
            if (Encoding != KnobEncoding.Absolute) e.SetAttributeValue("encoder", Encoding == KnobEncoding.Offset64 ? "offset64" : "twos");
            WriteDrive(e);
            return e;
        }

        public static MapEntry FromXml(XElement e)
        {
            if (!MapSource.TryKind((string)e.Attribute("kind"), out var kind)) return null;
            int channel = MapXml.Int(e, "channel", 1) - 1, number = MapXml.Int(e, "number", -1);
            if (channel < 0 || channel > 15 || number < 0 || number > 127) return null;
            var target = MapTarget.Read(e);
            if (target == null) return null;
            var entry = new MapEntry
            {
                Source = new MapSource((string)e.Attribute("device") ?? "", kind, channel, number),
                Target = target,
                Encoding = (string)e.Attribute("encoder") switch { "offset64" => KnobEncoding.Offset64, "twos" => KnobEncoding.TwosComplement, _ => KnobEncoding.Absolute },
            };
            entry.ReadDrive(e);
            return entry;
        }
    }

    /// <summary>One page of a track's controls: slots (the knobs of a knob group, by number) and entries of any other control.</summary>
    public sealed class TrackPage
    {
        public string Name { get; set; } = "Page 1";

        /// <summary>Slot i (from 0) is what knob i + 1 of a knob group drives; a gap is a free knob (it falls back to the generator's pin).</summary>
        public SortedDictionary<int, MapDrive> Slots { get; } = new SortedDictionary<int, MapDrive>();

        /// <summary>Entries of controls that are not knobs of a group (a fader, a pad), active while this page is.</summary>
        public List<MapEntry> Direct { get; } = new List<MapEntry>();

        public TrackPage Clone()
        {
            var p = new TrackPage { Name = Name };
            foreach (var (i, d) in Slots) p.Slots[i] = d.CloneDrive();
            foreach (var e in Direct) p.Direct.Add(e.Clone());
            return p;
        }

        public bool IsEmpty => Slots.Count == 0 && Direct.Count == 0;
    }

    /// <summary>A track's pages (the track is named by its id) and the page in use.</summary>
    public sealed class TrackPages
    {
        public const int MaxPages = 8;

        public string TrackId { get; set; } = "";
        public List<TrackPage> Pages { get; } = new List<TrackPage> { new TrackPage() };
        public int Current { get; set; }

        public TrackPage CurrentPage => Pages[Math.Clamp(Current, 0, Pages.Count - 1)];

        public bool IsEmpty => Pages.Count == 1 && Pages[0].IsEmpty && Pages[0].Name == "Page 1";

        public TrackPages Clone()
        {
            var t = new TrackPages { TrackId = TrackId, Current = Current };
            t.Pages.Clear();
            foreach (var p in Pages) t.Pages.Add(p.Clone());
            return t;
        }
    }

    /// <summary>A device's knobs, in the order they were turned when it was taught: knob 1 first.</summary>
    public sealed class KnobGroup
    {
        public string Device { get; set; } = "";
        public List<MapSource> Knobs { get; } = new List<MapSource>();
        public List<KnobEncoding> Encodings { get; } = new List<KnobEncoding>();

        public int Count => Knobs.Count;

        public KnobGroup Clone()
        {
            var g = new KnobGroup { Device = Device };
            g.Knobs.AddRange(Knobs);
            g.Encodings.AddRange(Encodings);
            return g;
        }

        public int IndexOf(MapSource source)
        {
            for (int i = 0; i < Knobs.Count; i++)
                if (Knobs[i].Kind == source.Kind && Knobs[i].Channel == source.Channel && Knobs[i].Number == source.Number)
                    return i;
            return -1;
        }
    }

    /// <summary>What is saved in a song: the track pages, and (when the song has its own) the global entries.</summary>
    public sealed class SongMap
    {
        public const int FormatVersion = 1;

        public List<TrackPages> Tracks { get; } = new List<TrackPages>();

        /// <summary>The song's own global entries, or null: the computer's apply.</summary>
        public List<MapEntry> Global { get; set; }

        public bool IsEmpty => Global == null && Tracks.All(t => t.IsEmpty);

        public TrackPages Find(string trackId) => Tracks.FirstOrDefault(t => string.Equals(t.TrackId, trackId, StringComparison.Ordinal));

        public SongMap Clone()
        {
            var s = new SongMap { Global = Global?.Select(e => e.Clone()).ToList() };
            foreach (var t in Tracks) s.Tracks.Add(t.Clone());
            return s;
        }

        /// <summary>The page set of a track, made when it has none (edit a clone only).</summary>
        public TrackPages Ensure(string trackId)
        {
            var t = Find(trackId);
            if (t != null) return t;
            t = new TrackPages { TrackId = trackId };
            Tracks.Add(t);
            return t;
        }

        public XElement ToXml(Func<string, bool> keep = null)
        {
            var root = new XElement("MidiMap", new XAttribute("version", FormatVersion));
            if (Global != null) root.Add(new XElement("Global", Global.Select(e => e.ToXml())));
            foreach (var t in Tracks.Where(t => !t.IsEmpty && (keep == null || keep(t.TrackId))).OrderBy(t => t.TrackId, StringComparer.Ordinal))
            {
                var track = new XElement("Track", new XAttribute("id", t.TrackId), new XAttribute("page", t.Current));
                foreach (var p in t.Pages)
                {
                    var page = new XElement("Page", new XAttribute("name", p.Name));
                    foreach (var (i, d) in p.Slots)
                    {
                        var slot = new XElement("Slot", new XAttribute("index", i));
                        d.WriteDrive(slot);
                        page.Add(slot);
                    }
                    foreach (var e in p.Direct) page.Add(e.ToXml());
                    track.Add(page);
                }
                root.Add(track);
            }
            return root;
        }

        /// <summary>Reads a saved map; skips what it does not understand. Null for a newer format than this build knows.</summary>
        public static SongMap FromXml(XElement root, out bool newer)
        {
            newer = false;
            var map = new SongMap();
            if (root == null) return map;
            if (MapXml.Int(root, "version", 1) > FormatVersion) { newer = true; return map; }
            if (root.Element("Global") is { } g)
                map.Global = g.Elements("Map").Select(MapEntry.FromXml).Where(e => e != null).ToList();
            foreach (var t in root.Elements("Track"))
            {
                string id = (string)t.Attribute("id");
                if (string.IsNullOrEmpty(id)) continue;
                var pages = new TrackPages { TrackId = id };
                pages.Pages.Clear();
                foreach (var p in t.Elements("Page").Take(TrackPages.MaxPages))
                {
                    var page = new TrackPage { Name = (string)p.Attribute("name") ?? "Page " + (pages.Pages.Count + 1) };
                    foreach (var s in p.Elements("Slot"))
                    {
                        int index = MapXml.Int(s, "index", -1);
                        var target = MapTarget.Read(s);
                        if (index < 0 || index > 63 || target == null) continue;
                        var drive = new MapDrive { Target = target };
                        drive.ReadDrive(s);
                        page.Slots[index] = drive;
                    }
                    foreach (var e in p.Elements("Map").Select(MapEntry.FromXml).Where(e => e != null)) page.Direct.Add(e);
                    pages.Pages.Add(page);
                }
                if (pages.Pages.Count == 0) pages.Pages.Add(new TrackPage());
                pages.Current = Math.Clamp(MapXml.Int(t, "page", 0), 0, pages.Pages.Count - 1);
                map.Tracks.Add(pages);
            }
            return map;
        }
    }

    /// <summary>What belongs to the computer: the global entries, the knob groups and the devices' roles.</summary>
    public sealed class ComputerMap
    {
        public List<MapEntry> Global { get; } = new List<MapEntry>();
        public List<KnobGroup> Groups { get; } = new List<KnobGroup>();
        /// <summary>Devices whose notes do not follow the selected track (control surfaces, pad-only devices).</summary>
        public HashSet<string> NoKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ComputerMap Clone()
        {
            var c = new ComputerMap();
            c.Global.AddRange(Global.Select(e => e.Clone()));
            c.Groups.AddRange(Groups.Select(g => g.Clone()));
            foreach (var d in NoKeys) c.NoKeys.Add(d);
            return c;
        }

        public KnobGroup GroupOf(string device) => Groups.FirstOrDefault(g => string.Equals(g.Device, device, StringComparison.OrdinalIgnoreCase));

        public XElement ToXml()
        {
            var root = new XElement("MidiComputer", new XAttribute("version", SongMap.FormatVersion));
            root.Add(new XElement("Global", Global.Select(e => e.ToXml())));
            foreach (var g in Groups.Where(g => g.Count > 0))
            {
                var group = new XElement("Group", new XAttribute("device", g.Device));
                for (int i = 0; i < g.Knobs.Count; i++)
                {
                    var k = g.Knobs[i];
                    var knob = new XElement("Knob", new XAttribute("kind", MapSource.KindText(k.Kind)), new XAttribute("channel", k.Channel + 1), new XAttribute("number", k.Number));
                    if (i < g.Encodings.Count && g.Encodings[i] != KnobEncoding.Absolute) knob.SetAttributeValue("encoder", g.Encodings[i] == KnobEncoding.Offset64 ? "offset64" : "twos");
                    group.Add(knob);
                }
                root.Add(group);
            }
            foreach (var d in NoKeys.OrderBy(d => d, StringComparer.OrdinalIgnoreCase)) root.Add(new XElement("NoKeys", new XAttribute("device", d)));
            return root;
        }

        public static ComputerMap Parse(string xml)
        {
            var map = new ComputerMap();
            if (string.IsNullOrWhiteSpace(xml)) return map;
            try
            {
                var root = XElement.Parse(xml);
                if (MapXml.Int(root, "version", 1) > SongMap.FormatVersion) return map;
                if (root.Element("Global") is { } g) map.Global.AddRange(g.Elements("Map").Select(MapEntry.FromXml).Where(e => e != null));
                foreach (var ge in root.Elements("Group"))
                {
                    string device = (string)ge.Attribute("device");
                    if (string.IsNullOrEmpty(device)) continue;
                    var group = new KnobGroup { Device = device };
                    foreach (var k in ge.Elements("Knob").Take(64))
                    {
                        if (!MapSource.TryKind((string)k.Attribute("kind"), out var kind)) continue;
                        int channel = MapXml.Int(k, "channel", 1) - 1, number = MapXml.Int(k, "number", -1);
                        if (channel < 0 || channel > 15 || number < 0 || number > 127) continue;
                        group.Knobs.Add(new MapSource(device, kind, channel, number));
                        group.Encodings.Add((string)k.Attribute("encoder") switch { "offset64" => KnobEncoding.Offset64, "twos" => KnobEncoding.TwosComplement, _ => KnobEncoding.Absolute });
                    }
                    if (group.Count > 0) map.Groups.Add(group);
                }
                foreach (var n in root.Elements("NoKeys")) if ((string)n.Attribute("device") is { Length: > 0 } d) map.NoKeys.Add(d);
            }
            catch (System.Xml.XmlException)
            {
            }
            return map;
        }
    }

    internal static class MapXml
    {
        public static int Int(XElement e, string name, int fallback) =>
            int.TryParse((string)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        public static double Dbl(XElement e, string name, double fallback) =>
            double.TryParse((string)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) ? v : fallback;
    }
}
