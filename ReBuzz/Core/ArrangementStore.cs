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
        readonly HashSet<string> takesHidden = new HashSet<string>(StringComparer.Ordinal);
        readonly List<(string Machine, Session.ColumnKey Key)> lanes = new List<(string, Session.ColumnKey)>();
        readonly Dictionary<string, Session.ColumnKey> envelopes = new Dictionary<string, Session.ColumnKey>(StringComparer.Ordinal);

        internal ArrangementStore(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            buzz.SaveSong += OnSaveSong;
            buzz.OpenSong += OnOpenSong;
            Recorder = new AutomationRecorder(buzz);
            Takes = new ArrangementRecorder(buzz);
            Links = new ArrangementLinks(buzz);
            BuzzGUI.Common.Actions.Restorations.Restored += OnRestored;
        }

        /// <summary>Off the static restoration bus (the core is released).</summary>
        internal void Detach() => BuzzGUI.Common.Actions.Restorations.Restored -= OnRestored;

        /// <summary>An undo brought a deleted lane back as a new sequence: the role follows it (AR6 follow-up to AR5).</summary>
        void OnRestored(object previous, object restored)
        {
            if (previous is not ISequence old || restored is not ISequence now || !roles.TryGetValue(old, out var role)) return;
            if (!buzz.SongCore.Sequences.Contains(now)) return;
            roles.Remove(old);
            if (old is SequenceCore oldSeq) oldSeq.TakeLane = false;
            SetRole(now, role);
            Changed?.Invoke();
        }

        /// <summary>MIDI recorded into the arrangement, loop recording into take lanes (daw-modes.md AR5).</summary>
        public ArrangementRecorder Takes { get; }

        /// <summary>Launching arrangement clips in the session, and clips between slots and lanes (daw-modes.md AR5).</summary>
        public ArrangementLinks Links { get; }

        /// <summary>Knob moves recorded into automation lanes (daw-modes.md AR4).</summary>
        public AutomationRecorder Recorder { get; }

        /// <summary>The parameter lanes shown under tracks (AR4): a machine and one of its state parameters (and track), in the order added.</summary>
        public IReadOnlyList<(string Machine, Session.ColumnKey Key)> ParameterLanes => lanes;

        public bool HasParameterLane(string machine, Session.ColumnKey key) => lanes.Contains((machine, key));

        public void AddParameterLane(string machine, Session.ColumnKey key)
        {
            if (machine == null || HasParameterLane(machine, key)) return;
            lanes.Add((machine, key));
            Changed?.Invoke();
        }

        public void RemoveParameterLane(string machine, Session.ColumnKey key)
        {
            if (lanes.Remove((machine, key)))
            {
                Recorder.Disarm(machine, key);
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// EV1: the parameter whose envelope a track's clips draw in the Arrangement and the Session View, as its header chose it; null when it
        /// did not (the views draw the first parameter that holds data, ClipEnvelopes.DefaultKey). Saved with the song.
        /// </summary>
        public Session.ColumnKey? EnvelopeChoice(string machine) => machine != null && envelopes.TryGetValue(machine, out var key) ? key : null;

        public void SetEnvelopeChoice(string machine, Session.ColumnKey? key)
        {
            if (machine == null) return;
            if (key is { } k ? envelopes.TryGetValue(machine, out var old) && old == k : !envelopes.ContainsKey(machine)) return;
            if (key is { } chosen) envelopes[machine] = chosen;
            else envelopes.Remove(machine);
            Changed?.Invoke();
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
            if (lane is SequenceCore seq) seq.TakeLane = role == LaneRole.Take;      // the engine skips take lanes (AR5)
            Changed?.Invoke();
        }

        public bool IsFolded(string machine) => machine != null && folded.Contains(machine);

        public void SetFolded(string machine, bool on)
        {
            if (machine == null) return;
            if (on ? folded.Add(machine) : folded.Remove(machine)) Changed?.Invoke();
        }

        /// <summary>NA4b: whether a track's take lanes are collapsed (hidden under the track; the takes stay and nothing plays differently).</summary>
        public bool AreTakesHidden(string machine) => machine != null && takesHidden.Contains(machine);

        public void SetTakesHidden(string machine, bool on)
        {
            if (machine == null) return;
            if (on ? takesHidden.Add(machine) : takesHidden.Remove(machine)) Changed?.Invoke();
        }

        /// <summary>A new song: every lane main, nothing folded.</summary>
        public void Clear()
        {
            Recorder.Clear();
            Takes.Reset();
            if (roles.Count == 0 && folded.Count == 0 && lanes.Count == 0 && takesHidden.Count == 0 && envelopes.Count == 0) return;
            ClearRoles();
            folded.Clear();
            takesHidden.Clear();
            lanes.Clear();
            envelopes.Clear();
            Changed?.Invoke();
        }

        void ClearRoles()
        {
            foreach (var lane in roles.Keys)
                if (lane is SequenceCore seq) seq.TakeLane = false;
            roles.Clear();
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
            var machines = new HashSet<string>(buzz.SongCore.MachinesList.Select(m => m.Name));
            foreach (var name in folded.Where(present.Contains).OrderBy(n => n, StringComparer.Ordinal))
                root.Add(new XElement("Folded", new XAttribute("machine", name)));
            foreach (var name in takesHidden.Where(present.Contains).OrderBy(n => n, StringComparer.Ordinal))
                root.Add(new XElement("TakesHidden", new XAttribute("machine", name)));
            var seen = new HashSet<(string, Session.ColumnKey)>();
            foreach (var (machine, key) in lanes)
                if (machines.Contains(machine) && seen.Add((machine, key)))
                    root.Add(new XElement("Param", new XAttribute("machine", machine), new XAttribute("group", key.Group), new XAttribute("index", key.Index), new XAttribute("track", key.Track)));
            foreach (var (machine, key) in envelopes.OrderBy(e => e.Key, StringComparer.Ordinal))
                if (machines.Contains(machine))
                    root.Add(new XElement("Envelope", new XAttribute("machine", machine), new XAttribute("group", key.Group), new XAttribute("index", key.Index), new XAttribute("track", key.Track)));
            return root.HasElements ? root : null;
        }

        internal void Apply(XElement root)
        {
            ClearRoles();
            folded.Clear();
            takesHidden.Clear();
            lanes.Clear();
            envelopes.Clear();
            Recorder.Clear();
            Takes.Reset();
            if (root == null || root.Name.LocalName != "Arrangement") return;
            var sequences = buzz.SongCore.Sequences;
            foreach (var x in root.Elements("Lane"))
            {
                if (!int.TryParse((string)x.Attribute("index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= sequences.Count) continue;
                if (Enum.TryParse<LaneRole>((string)x.Attribute("role"), true, out var role) && role != LaneRole.Main)
                {
                    roles[sequences[index]] = role;
                    if (sequences[index] is SequenceCore seq) seq.TakeLane = role == LaneRole.Take;
                }
            }
            foreach (var x in root.Elements("Folded"))
                if ((string)x.Attribute("machine") is { Length: > 0 } name) folded.Add(name);
            foreach (var x in root.Elements("TakesHidden"))
                if ((string)x.Attribute("machine") is { Length: > 0 } name) takesHidden.Add(name);
            foreach (var x in root.Elements("Param"))
            {
                int Num(string a) => int.TryParse((string)x.Attribute(a), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : -1;
                if ((string)x.Attribute("machine") is { Length: > 0 } name && Num("group") >= 0 && Num("index") >= 0 && Num("track") >= 0
                    && !lanes.Contains((name, new Session.ColumnKey(Num("group"), Num("index"), Num("track")))))
                    lanes.Add((name, new Session.ColumnKey(Num("group"), Num("index"), Num("track"))));
            }
            foreach (var x in root.Elements("Envelope"))
            {
                int Num(string a) => int.TryParse((string)x.Attribute(a), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : -1;
                if ((string)x.Attribute("machine") is { Length: > 0 } name && Num("group") >= 0 && Num("index") >= 0 && Num("track") >= 0)
                    envelopes[name] = new Session.ColumnKey(Num("group"), Num("index"), Num("track"));
            }
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
