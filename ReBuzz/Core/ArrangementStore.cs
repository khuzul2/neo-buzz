using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ReBuzz.Core
{
    /// <summary>What a lane (a sequence) of an arrangement track is for (daw-modes.md S5).</summary>
    internal enum LaneRole { Main, Take, Automation }

    /// <summary>
    /// Fork (docs/specs/daw-modes.md S5): what the arrangement keeps with the song besides its
    /// events: the role of each lane (a sequence: main, take or automation) and which tracks are
    /// folded. Stored in an "Arrangement" subsection, in .bmx and .bmxml alike, only when
    /// something differs from the default (every lane main, nothing folded); upstream ReBuzz skips
    /// it and plays every sequence, as it did before. Lanes are identified by sequence index, as
    /// the "ArrangementEvents" subsection does; tracks (machines) by name.
    /// </summary>
    internal sealed class ArrangementStore
    {
        public const string SubSectionName = "Arrangement";
        const int FormatVersion = 1;

        readonly ReBuzzCore buzz;
        readonly Dictionary<ISequence, LaneRole> roles = new Dictionary<ISequence, LaneRole>();
        readonly HashSet<string> folded = new HashSet<string>(StringComparer.Ordinal);

        internal ArrangementStore(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            buzz.SaveSong += OnSaveSong;
            buzz.OpenSong += OnOpenSong;
        }

        /// <summary>A role or a fold changed (views redraw).</summary>
        public event Action Changed;

        /// <summary>A lane's role (main unless set otherwise).</summary>
        public LaneRole RoleOf(ISequence lane) => lane != null && roles.TryGetValue(lane, out var role) ? role : LaneRole.Main;

        /// <summary>Sets a lane's role. The Session View keeps showing every lane until takes and automation clips exist (AR4, AR5).</summary>
        public void SetRole(ISequence lane, LaneRole role)
        {
            if (lane == null || RoleOf(lane) == role) return;
            if (role == LaneRole.Main) roles.Remove(lane);
            else roles[lane] = role;
            Changed?.Invoke();
        }

        public bool IsFolded(string machine) => machine != null && folded.Contains(machine);

        public void SetFolded(string machine, bool on)
        {
            if (machine == null) return;
            if (on ? folded.Add(machine) : folded.Remove(machine)) Changed?.Invoke();
        }

        /// <summary>A new song: every lane main, nothing folded.</summary>
        public void Clear()
        {
            if (roles.Count == 0 && folded.Count == 0) return;
            roles.Clear();
            folded.Clear();
            Changed?.Invoke();
        }

        /// <summary>The "Arrangement" subsection's content, or null when everything is as by default.</summary>
        internal XElement ToXml()
        {
            var sequences = buzz.SongCore.Sequences;
            var root = new XElement("Arrangement", new XAttribute("version", FormatVersion));
            for (int i = 0; i < sequences.Count; i++)
                if (roles.TryGetValue(sequences[i], out var role) && role != LaneRole.Main)
                    root.Add(new XElement("Lane", new XAttribute("index", i), new XAttribute("role", role.ToString().ToLowerInvariant())));
            var present = new HashSet<string>(sequences.Select(s => s.Machine?.Name ?? ""));
            foreach (var name in folded.Where(present.Contains).OrderBy(n => n, StringComparer.Ordinal))
                root.Add(new XElement("Folded", new XAttribute("machine", name)));
            return root.HasElements ? root : null;
        }

        internal void Apply(XElement root)
        {
            roles.Clear();
            folded.Clear();
            if (root == null || root.Name.LocalName != "Arrangement") return;
            var sequences = buzz.SongCore.Sequences;
            foreach (var x in root.Elements("Lane"))
            {
                if (!int.TryParse((string)x.Attribute("index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= sequences.Count) continue;
                if (Enum.TryParse<LaneRole>((string)x.Attribute("role"), true, out var role) && role != LaneRole.Main)
                    roles[sequences[index]] = role;
            }
            foreach (var x in root.Elements("Folded"))
                if ((string)x.Attribute("machine") is { Length: > 0 } name) folded.Add(name);
        }

        void OnSaveSong(ISaveSong save)
        {
            var xml = ToXml();
            if (xml == null) return;
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
                Apply(stream == null ? null : XDocument.Load(stream).Root);
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("Arrangement: " + e.Message, DCLogLevel.Warning);
            }
            Changed?.Invoke();
        }
    }
}
