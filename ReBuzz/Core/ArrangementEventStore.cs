using BuzzGUI.Interfaces;
using ReBuzz.FileOps;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ReBuzz.Core
{
    /// <summary>
    /// Fork (docs/specs/session-view.md 0.5.1): keeps what a Buzz sequence event can't hold in
    /// the song file: the part of its pattern it plays (<see cref="SequenceEvent.Offset"/>,
    /// <see cref="SequenceEvent.End"/>) and its volume (<see cref="SequenceEvent.Gain"/>), as
    /// the Session View played and recorded them. Stored in an "ArrangementEvents" subsection,
    /// in .bmx and .bmxml alike; upstream ReBuzz skips it and plays those events whole at full
    /// volume, as it did before.
    ///
    /// Events are identified by sequence index and time, which the song file keeps.
    ///
    /// Version 2 (docs/specs/daw-modes.md S2) adds each event's arrangement clip
    /// (<see cref="SequenceEvent.Clip"/>) and each clip's loop (<see cref="ArrangementClips"/>).
    /// Readers of version 1 skip both.
    /// </summary>
    internal sealed class ArrangementEventStore
    {
        public const string SubSectionName = "ArrangementEvents";
        const int FormatVersion = 2;

        readonly ReBuzzCore buzz;

        internal ArrangementEventStore(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            buzz.SaveSong += OnSaveSong;
            buzz.OpenSong += OnOpenSong;
        }

        /// <summary>
        /// Fork (native-format.md NF2): an export for upstream writes take lanes (and events past tick 65,535) out of the core
        /// sequence data and keeps them here: per lane (by index), its events by time with the pattern's index. Read first on
        /// open, so a song exported and reopened has them back before the other stores give them their trims and clips.
        /// </summary>
        public const string ExportedLanesName = "ExportedLanes";

        internal static XElement ExportedLanesToXml(IReadOnlyList<SequenceCore> lanes)
        {
            var root = new XElement("ExportedLanes", new XAttribute("version", 1));
            for (int i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                var events = new List<XElement>();
                foreach (var kv in lane.EventsList)
                {
                    if (!BuzzExport.Omits(lane, kv.Key)) continue;
                    var e = kv.Value;
                    var x = new XElement("E", new XAttribute("time", kv.Key), new XAttribute("kind", e.Type.ToString().ToLowerInvariant()));
                    if (e.Type == SequenceEventType.PlayPattern)
                    {
                        int pattern = e.Pattern is PatternCore pc ? lane.MachineCore.PatternsList.IndexOf(pc) : -1;
                        if (pattern < 0) continue;
                        x.SetAttributeValue("pattern", pattern);
                    }
                    events.Add(x);
                }
                if (events.Count > 0)
                    root.Add(new XElement("Lane", new XAttribute("index", i), events));
            }
            return root.HasElements ? root : null;
        }

        internal static void ApplyExportedLanes(XElement root, IReadOnlyList<SequenceCore> lanes)
        {
            if (root == null || root.Name.LocalName != ExportedLanesName) return;
            foreach (var l in root.Elements("Lane"))
            {
                if (!int.TryParse((string)l.Attribute("index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= lanes.Count) continue;
                var lane = lanes[index];
                foreach (var x in l.Elements("E"))
                {
                    if (!int.TryParse((string)x.Attribute("time"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int time)) continue;
                    if (!Enum.TryParse<SequenceEventType>((string)x.Attribute("kind"), true, out var kind)) continue;
                    if (kind == SequenceEventType.PlayPattern)
                    {
                        int p = Int(x, "pattern", -1);
                        if (p < 0 || p >= lane.MachineCore.PatternsList.Count) continue;
                        var pattern = lane.MachineCore.PatternsList[p];
                        lane.SetEvent(time, new SequenceEvent(SequenceEventType.PlayPattern, pattern, pattern.Length));
                    }
                    else
                        lane.SetEvent(time, new SequenceEvent(kind));
                }
            }
        }

        void OnSaveSong(ISaveSong save)
        {
            if (BuzzExport.Current != null)
            {
                var lanes = ExportedLanesToXml(buzz.SongCore.SequencesList);
                var exported = lanes == null ? null : save.CreateSubSection(ExportedLanesName);
                if (exported != null)
                {
                    var data = new UTF8Encoding(false).GetBytes(lanes.ToString(SaveOptions.DisableFormatting));
                    exported.Write(data, 0, data.Length);
                    exported.Flush();
                }
            }
            var xml = ToXml(buzz.SongCore.Sequences, buzz.Clips);
            if (xml == null) return;      // nothing upstream can't hold: no subsection
            var stream = save.CreateSubSection(SubSectionName);
            if (stream == null) return;
            var bytes = new UTF8Encoding(false).GetBytes(xml.ToString(SaveOptions.DisableFormatting));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        void OnOpenSong(IOpenSong open)
        {
            try
            {
                var lanes = open.GetSubSection(ExportedLanesName);
                if (lanes != null)
                    ApplyExportedLanes(XDocument.Load(lanes).Root, buzz.SongCore.SequencesList);
                var stream = open.GetSubSection(SubSectionName);
                if (stream == null) return;
                var root = XDocument.Load(stream).Root;
                Apply(root, buzz.SongCore.SequencesList, buzz.Clips);
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("Arrangement events: " + e.Message, DCLogLevel.Warning);
            }
            finally
            {
                buzz.Clips.ContinueAfter(buzz.SongCore.SequencesList);
            }
        }

        /// <summary>
        /// The events with a start, end, volume or clip of their own, and the loops of those clips
        /// (daw-modes.md S2); null if there are none.
        /// </summary>
        internal static XElement ToXml(IEnumerable<ISequence> sequences, ArrangementClips clips = null)
        {
            var root = new XElement("ArrangementEvents", new XAttribute("version", FormatVersion));
            var ids = new SortedSet<int>();
            int index = 0;
            foreach (var seq in sequences)
            {
                var events = seq.Events
                    .Where(e => e.Value.Type == SequenceEventType.PlayPattern && e.Value.Pattern != null && (!e.Value.IsPlain || e.Value.Clip != 0))
                    .Select(e =>
                    {
                        var x = new XElement("Event", new XAttribute("time", e.Key));
                        if (e.Value.Offset != 0) x.SetAttributeValue("offset", e.Value.Offset);
                        if (e.Value.End != 0) x.SetAttributeValue("end", e.Value.End);
                        if (e.Value.Gain != 1.0) x.SetAttributeValue("gain", e.Value.Gain.ToString("R", CultureInfo.InvariantCulture));
                        if (e.Value.Clip != 0)
                        {
                            x.SetAttributeValue("clip", e.Value.Clip);
                            ids.Add(e.Value.Clip);
                        }
                        return x;
                    })
                    .ToList();
                if (events.Count > 0)
                    root.Add(new XElement("Sequence", new XAttribute("index", index), events));
                index++;
            }
            // The loops of the clips in the song, by id.
            if (clips != null)
                foreach (int id in ids)
                    if (clips.TryGetLoop(id, out var loop))
                        root.Add(new XElement("Clip", new XAttribute("id", id), new XAttribute("loopStart", loop.Start),
                            new XAttribute("loopEnd", loop.End), new XAttribute("loop", loop.On ? 1 : 0)));
            return root.HasElements ? root : null;
        }

        /// <summary>
        /// Gives the song's events their start, end, volume and clip, and the clips their loops.
        /// Unknown content and events that aren't there (any more) are ignored. Returns how many
        /// events were changed.
        /// </summary>
        internal static int Apply(XElement root, IReadOnlyList<SequenceCore> sequences, ArrangementClips clips = null)
        {
            if (root == null || root.Name.LocalName != "ArrangementEvents") return 0;
            if (clips != null)
                foreach (var c in root.Elements("Clip"))
                {
                    int id = Int(c, "id", 0);
                    if (id > 0) clips.SetLoop(id, new ClipLoop(Int(c, "loopStart", 0), Int(c, "loopEnd", 0), Int(c, "loop", 0) != 0));
                }
            int changed = 0;
            foreach (var s in root.Elements("Sequence"))
            {
                if (!int.TryParse((string)s.Attribute("index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
                    index < 0 || index >= sequences.Count)
                    continue;
                var seq = sequences[index];
                bool any = false;
                foreach (var x in s.Elements("Event"))
                {
                    if (!int.TryParse((string)x.Attribute("time"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int time)) continue;
                    if (!seq.EventsList.TryGetValue(time, out var e) || e.Type != SequenceEventType.PlayPattern || e.Pattern == null) continue;
                    e.Offset = Math.Max(0, Int(x, "offset", 0));
                    e.End = Math.Max(0, Int(x, "end", 0));
                    e.Gain = double.TryParse((string)x.Attribute("gain"), NumberStyles.Float, CultureInfo.InvariantCulture, out double g) && !double.IsNaN(g)
                        ? Math.Clamp(g, 0, 1) : 1.0;
                    e.Clip = Math.Max(0, Int(x, "clip", 0));
                    any = true;
                    changed++;
                }
                if (any)
                    seq.RefreshSpans();
            }
            return changed;
        }

        static int Int(XElement e, string name, int fallback) =>
            int.TryParse((string)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    }
}
