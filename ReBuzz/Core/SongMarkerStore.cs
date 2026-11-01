using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ReBuzz.Core
{
    /// <summary>
    /// Fork (docs/specs/mcp-server.md A3): named sections of the song ("Intro", "Verse 1"…) at
    /// tick positions, for renders, guides and the agent's own orientation. Buzz songs have no
    /// such markers, so they are kept in a "SongMarkers" subsection, in .bmx and .bmxml alike;
    /// upstream ReBuzz skips it.
    /// </summary>
    internal sealed class SongMarkerStore
    {
        public const string SubSectionName = "SongMarkers";
        const int FormatVersion = 1;

        readonly ReBuzzCore buzz;
        readonly List<(string Name, int Tick)> markers = new();

        internal SongMarkerStore(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            buzz.SaveSong += OnSaveSong;
            buzz.OpenSong += OnOpenSong;
        }

        /// <summary>The markers in time order.</summary>
        public IReadOnlyList<(string Name, int Tick)> All => markers.OrderBy(m => m.Tick).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();

        public event Action Changed;

        /// <summary>Sets a marker (a new one, or moves the one of that name).</summary>
        public void Set(string name, int tick)
        {
            markers.RemoveAll(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            markers.Add((name, Math.Max(0, tick)));
            Changed?.Invoke();
        }

        public bool Remove(string name)
        {
            bool removed = markers.RemoveAll(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Changed?.Invoke();
            return removed;
        }

        /// <summary>Replaces every marker (undo).</summary>
        public void Replace(IEnumerable<(string Name, int Tick)> all)
        {
            markers.Clear();
            markers.AddRange(all);
            Changed?.Invoke();
        }

        public void Clear()
        {
            if (markers.Count == 0) return;
            markers.Clear();
            Changed?.Invoke();
        }

        void OnSaveSong(ISaveSong save)
        {
            if (markers.Count == 0) return;
            var stream = save.CreateSubSection(SubSectionName);
            if (stream == null) return;
            var root = new XElement("SongMarkers", new XAttribute("version", FormatVersion),
                All.Select(m => new XElement("Marker", new XAttribute("name", m.Name), new XAttribute("tick", m.Tick.ToString(CultureInfo.InvariantCulture)))));
            var bytes = new UTF8Encoding(false).GetBytes(root.ToString(SaveOptions.DisableFormatting));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        void OnOpenSong(IOpenSong open)
        {
            markers.Clear();
            try
            {
                var stream = open.GetSubSection(SubSectionName);
                if (stream != null)
                {
                    var root = XDocument.Load(stream).Root;
                    foreach (var m in root.Elements("Marker"))
                    {
                        string name = (string)m.Attribute("name");
                        if (string.IsNullOrWhiteSpace(name) || !int.TryParse((string)m.Attribute("tick"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tick)) continue;
                        markers.Add((name, Math.Max(0, tick)));
                    }
                }
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("Song markers: " + e.Message, DCLogLevel.Warning);
            }
            Changed?.Invoke();
        }
    }
}
