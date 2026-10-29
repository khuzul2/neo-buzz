using BuzzGUI.Interfaces;
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
    /// </summary>
    internal sealed class ArrangementEventStore
    {
        public const string SubSectionName = "ArrangementEvents";
        const int FormatVersion = 1;

        readonly ReBuzzCore buzz;

        internal ArrangementEventStore(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            buzz.SaveSong += OnSaveSong;
            buzz.OpenSong += OnOpenSong;
        }

        void OnSaveSong(ISaveSong save)
        {
            var xml = ToXml(buzz.SongCore.Sequences);
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
                var stream = open.GetSubSection(SubSectionName);
                if (stream == null) return;
                var root = XDocument.Load(stream).Root;
                Apply(root, buzz.SongCore.SequencesList);
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("Arrangement events: " + e.Message, DCLogLevel.Warning);
            }
        }

        /// <summary>The events with a start, end or volume of their own, or null if there are none.</summary>
        internal static XElement ToXml(IEnumerable<ISequence> sequences)
        {
            var root = new XElement("ArrangementEvents", new XAttribute("version", FormatVersion));
            int index = 0;
            foreach (var seq in sequences)
            {
                var events = seq.Events
                    .Where(e => e.Value.Type == SequenceEventType.PlayPattern && e.Value.Pattern != null && !e.Value.IsPlain)
                    .Select(e =>
                    {
                        var x = new XElement("Event", new XAttribute("time", e.Key));
                        if (e.Value.Offset != 0) x.SetAttributeValue("offset", e.Value.Offset);
                        if (e.Value.End != 0) x.SetAttributeValue("end", e.Value.End);
                        if (e.Value.Gain != 1.0) x.SetAttributeValue("gain", e.Value.Gain.ToString("R", CultureInfo.InvariantCulture));
                        return x;
                    })
                    .ToList();
                if (events.Count > 0)
                    root.Add(new XElement("Sequence", new XAttribute("index", index), events));
                index++;
            }
            return root.HasElements ? root : null;
        }

        /// <summary>
        /// Gives the song's events their start, end and volume. Unknown content and events that
        /// aren't there (any more) are ignored. Returns how many events were changed.
        /// </summary>
        internal static int Apply(XElement root, IReadOnlyList<SequenceCore> sequences)
        {
            if (root == null || root.Name.LocalName != "ArrangementEvents") return 0;
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
                    e.Gain = double.TryParse((string)x.Attribute("gain"), NumberStyles.Float, CultureInfo.InvariantCulture, out double g)
                        ? Math.Clamp(g, 0, 1) : 1.0;
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
