// Fork (docs/specs/ux-overhaul.md P3e, decisions 62-70): what the device view shows of a machine, as plain data with no controls:
// its parameters in groups (the manifest's, else the name prefixes, Buzz's "- " indents, or the Global/Track/Inputs sections),
// the kind of control each gets (slider, segmented control of up to four choices, toggle), the macro strip's picks, the track
// parameters as a table, and the flat list of items (headers, rows, the table) the virtualised list draws for a search.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.Agent.Model;
using ReBuzz.Studio.Parameters;

namespace ReBuzz.Studio.Views.Device
{
    internal enum DeviceKind { Slider, Segmented, Toggle }

    /// <summary>One parameter (on one track) as the device view shows it.</summary>
    internal sealed class DeviceParam
    {
        public IParameter Parameter { get; init; }
        public int Track { get; init; }
        /// <summary>The name shown in its group (a prefix the group's header says is cut).</summary>
        public string Name { get; init; }
        /// <summary>The machine's own name for it (what the old window and the agent show).</summary>
        public string FullName { get; init; }
        public string Group { get; init; } = "";
        public bool IsTrack { get; init; }
        public bool IsInput { get; init; }
        public int Min { get; init; }
        public int Max { get; init; }
        public int Default { get; init; }
        public DeviceKind Kind { get; init; }
        /// <summary>The choices of a segmented control (up to four), their texts from the machine.</summary>
        public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
        /// <summary>The unit the manifest names ("Hz", "dB", "%"); empty when it doesn't or the machine's text says it.</summary>
        public string Unit { get; init; } = "";
        public string Description { get; init; } = "";
        /// <summary>Explain (neo-learn.md §5.3): how the control changes the sound, and what you will hear; empty without them.</summary>
        public string How { get; init; } = "";
        public string Sound { get; init; } = "";
        /// <summary>The manifest's role ("filter.cutoff"), the macro strip's pick; empty without one.</summary>
        public string Role { get; init; } = "";
        /// <summary>The manifest's plain English name ("Filter cutoff"); empty without one.</summary>
        public string PlainName { get; init; } = "";
        /// <summary>The macro strip's label: the plain name, else the name, in Title Case ("L depth" is "L Depth").</summary>
        public string MacroLabel => TitleCase(PlainName.Length > 0 ? PlainName : Name);

        /// <summary>Capitalises each lower-case word; words with a capital in them (LFO, PEG, Hz) stay.</summary>
        public static string TitleCase(string s) =>
            string.Join(" ", (s ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.IsLower(w[0]) ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w));

        public ParamInfo Info { get; init; }
        /// <summary>Where the manifest's value is zero inside the range (a bipolar mapping, a linear one through zero, a count that starts below it); null: not inside it.</summary>
        public double? ZeroAt { get; init; }
        /// <summary>The range holds zero inside it, in raw values or in the unit: the slider fills from there.</summary>
        public bool Bipolar => Min < 0 && Max > 0 || ZeroAt is > 0 and < 1;
        public bool HasExplain => How.Length > 0 || Sound.Length > 0;

        public int Value => Math.Clamp(Parameter.GetValue(Track), Min, Max);

        /// <summary>Where the value is in its range, 0 to 1.</summary>
        public double Fraction(int raw) => Max == Min ? 0 : Math.Clamp((raw - Min) / (double)(Max - Min), 0, 1);

        /// <summary>Where a slider's fill starts: the left end, or the middle of a bipolar range.</summary>
        public double ZeroFraction => ZeroAt is > 0 and < 1 ? ZeroAt.Value : Min < 0 && Max > 0 ? Fraction(0) : 0;

        /// <summary>The machine's text for a value, with the manifest's unit when the text is a bare number.</summary>
        public string Describe(int raw)
        {
            string text;
            try { text = Parameter.DescribeValue(raw); }
            catch (Exception) { text = null; }
            if (string.IsNullOrWhiteSpace(text)) text = raw.ToString(CultureInfo.InvariantCulture);
            text = text.Trim();
            if (Unit.Length > 0 && Kind == DeviceKind.Slider && !HasLetterOrPercent(text)) text += " " + Unit;
            return text;
        }

        static bool HasLetterOrPercent(string s) => s.Any(c => char.IsLetter(c) || c == '%');

        public bool Matches(string[] words)
        {
            foreach (var w in words)
            {
                if (!(Contains(Name, w) || Contains(FullName, w) || Contains(Group, w) || Contains(Description, w) || Contains(How, w)
                      || Contains(Sound, w) || Contains(Unit, w) || Choices.Any(c => Contains(c, w)))) return false;
            }
            return true;
        }

        static bool Contains(string s, string word) => !string.IsNullOrEmpty(s) && s.Contains(word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A group of parameters under a header; the track parameters are one table.</summary>
    internal sealed class DeviceSection
    {
        public string Name { get; init; }
        /// <summary>"manifest", "prefix", "indent" or "section": where the group came from.</summary>
        public string Source { get; init; }
        public List<DeviceParam> Params { get; } = new List<DeviceParam>();
        /// <summary>The track parameters: one table, a row per track and a column per parameter (Params holds track 0's).</summary>
        public bool IsTable { get; init; }
        public int Tracks { get; set; }
        public List<DeviceParam> TableCells { get; } = new List<DeviceParam>();

        public DeviceParam Cell(int track, int column) => TableCells[track * Params.Count + column];
    }

    /// <summary>An item of the list the view draws: a header, a parameter row, or the table.</summary>
    internal abstract class DeviceItem
    {
        public abstract double Height { get; }
        public const double RowHeight = 24;
        public const double HeaderHeight = 28;
    }

    internal sealed class HeaderItem : DeviceItem
    {
        public HeaderItem(DeviceSection section, int count, bool collapsed)
        {
            Section = section;
            Count = count;
            Collapsed = collapsed;
        }
        public DeviceSection Section { get; }
        public int Count { get; }
        public bool Collapsed { get; }
        public override double Height => HeaderHeight;
    }

    internal sealed class ParamItem : DeviceItem
    {
        public ParamItem(DeviceParam param) { Param = param; }
        public DeviceParam Param { get; }
        public override double Height => RowHeight;
    }

    internal sealed class TableItem : DeviceItem
    {
        public TableItem(DeviceSection section, List<int> columns)
        {
            Section = section;
            Columns = columns;
        }
        public DeviceSection Section { get; }
        /// <summary>Which of the section's parameters are the table's columns (a search narrows them).</summary>
        public List<int> Columns { get; }
        public override double Height => RowHeight * (Section.Tracks + 1);
    }

    /// <summary>The macro strip: up to eight parameters as big knobs' worth of sliders side by side.</summary>
    internal sealed class MacroItem : DeviceItem
    {
        public const double StripHeight = 56;
        public MacroItem(IReadOnlyList<DeviceParam> cells) { Cells = cells; }
        public IReadOnlyList<DeviceParam> Cells { get; }
        public override double Height => StripHeight;
    }

    /// <summary>The machine's parameters grouped and classified, built once per view.</summary>
    internal sealed class DeviceLayout
    {
        public IMachine Machine { get; init; }
        public IReadOnlyList<DeviceSection> Sections { get; init; }
        public IReadOnlyList<DeviceParam> All { get; init; }
        public IReadOnlyList<DeviceParam> Macros { get; init; }
        /// <summary>Where the global parameters' groups came from: "manifest", "prefix", "indent" or "section".</summary>
        public string GroupSource { get; init; } = "section";

        // ------------------------------------------------------------------ building

        /// <summary>Roles the macro strip prefers, in order; without them the machine's first sliders.</summary>
        static readonly string[] MacroRoles =
        {
            "filter.cutoff", "filter.resonance", "level", "mix.wet", "pitch.coarse", "env.attack", "env.release", "drive", "mix.dry", "width",
        };

        public const int MacroCount = 8;

        public static DeviceLayout Build(IMachine machine)
        {
            var manifest = machine.DLL != null ? SongModel.Manifest(machine.DLL) : null;
            var sections = new List<DeviceSection>();
            var all = new List<DeviceParam>();

            DeviceParam Make(IParameter p, int track, string name, string group, bool isTrack, bool input, ManifestParameter entry)
            {
                if (entry != null && (entry.Min != p.MinValue || entry.Max != p.MaxValue)) entry = null;   // a stale manifest: the machine decides
                var (kind, choices) = Classify(p, entry);
                var param = p;
                return new DeviceParam
                {
                    Parameter = p,
                    Track = track,
                    Name = name,
                    FullName = ParameterRow.DisplayName(p, track),
                    Group = group,
                    IsTrack = isTrack,
                    IsInput = input,
                    Min = p.MinValue,
                    Max = p.MaxValue,
                    Default = Math.Clamp(p.DefValue, p.MinValue, p.MaxValue),
                    Kind = kind,
                    Choices = choices,
                    Unit = entry != null ? DeviceExplain.Unit(entry) : "",
                    Description = entry?.Description is { Length: > 0 } d ? d : p.Description ?? "",
                    How = DeviceExplain.How(entry),
                    Sound = DeviceExplain.Sound(entry),
                    Role = entry?.Role ?? "",
                    PlainName = entry?.PlainName ?? "",
                    ZeroAt = ZeroOf(entry, p.MinValue, p.MaxValue),
                    Info = new ParamInfo
                    {
                        Name = p.Name,
                        Index = p.IndexInGroup,
                        Group = entry?.Group ?? "",
                        Description = entry?.Description ?? p.Description ?? "",
                        Min = p.MinValue,
                        Max = p.MaxValue,
                        Default = p.DefValue,
                        Mapping = entry?.Mapping,
                        TextsFirst = entry != null && manifest != null && (manifest.IsDraft || manifest.IsCurated),
                        Describe = raw => SongModel.Describe(param, raw),
                        SafeRange = entry?.SafeRange,
                    },
                };
            }

            // Inputs: an amp (and a pan on a stereo input) for each connected input.
            var inputs = machine.ParameterGroups.FirstOrDefault(g => g.Type == ParameterGroupType.Input);
            if (inputs != null && inputs.TrackCount > 0)
            {
                var s = new DeviceSection { Name = "Inputs", Source = "section" };
                for (int t = 0; t < inputs.TrackCount; t++)
                    for (int i = 0; i < Math.Min(2, inputs.Parameters.Count); i++)
                    {
                        if (i == 1 && !machine.HasStereoInput) break;
                        var p = inputs.Parameters[i];
                        var dp = Make(p, t, ParameterRow.DisplayName(p, t), "Inputs", false, true, null);
                        s.Params.Add(dp);
                        all.Add(dp);
                    }
                if (s.Params.Count > 0) sections.Add(s);
            }

            // Global: grouped from the manifest, else the "- " indents, else the name prefixes, else one section.
            var global = machine.ParameterGroups.FirstOrDefault(g => g.Type == ParameterGroupType.Global);
            string source = "section";
            if (global != null)
            {
                var ps = global.Parameters.Where(p => IsShown(p)).ToList();
                var entries = ps.Select(p => manifest?.FindAt(false, p.IndexInGroup, p.Name)).ToList();
                var names = ps.Select(p => p.Name ?? "").ToList();
                var plan = GroupFromManifest(ps, entries) ?? GroupFromIndents(names) ?? GroupFromPrefixes(names);
                if (plan.Source == "section") plan = new GroupPlan("section", new[] { ("Global", Enumerable.Range(0, ps.Count).Select(i => (i, names[i])).ToList()) });
                source = plan.Source;
                foreach (var (title, members) in plan.Groups)
                {
                    if (members.Count == 0) continue;
                    var s = new DeviceSection { Name = title, Source = plan.Source };
                    int cut = CommonWord(members.Select(m => m.Shown).ToList());
                    foreach (var (i, shown) in members)
                    {
                        var dp = Make(ps[i], 0, cut > 0 ? shown.Substring(cut).TrimStart() : shown, title, false, false, entries[i]);
                        s.Params.Add(dp);
                        all.Add(dp);
                    }
                    sections.Add(s);
                }
            }

            // Track parameters: one table, a column for each and a row for each track.
            var track = machine.ParameterGroups.FirstOrDefault(g => g.Type == ParameterGroupType.Track);
            if (track != null && track.TrackCount > 0)
            {
                var ps = track.Parameters.Where(p => IsShown(p)).ToList();
                if (ps.Count > 0)
                {
                    var s = new DeviceSection { Name = "Tracks", Source = "section", IsTable = true, Tracks = track.TrackCount };
                    foreach (var p in ps)
                        s.Params.Add(Make(p, 0, p.Name ?? "", "Tracks", true, false, manifest?.FindAt(true, p.IndexInGroup, p.Name)));
                    for (int t = 0; t < track.TrackCount; t++)
                        for (int c = 0; c < ps.Count; c++)
                        {
                            var column = s.Params[c];
                            var cell = t == 0 ? column : Make(ps[c], t, column.Name, "Tracks", true, false, manifest?.FindAt(true, ps[c].IndexInGroup, ps[c].Name));
                            s.TableCells.Add(cell);
                            all.Add(cell);
                        }
                    sections.Add(s);
                }
            }

            return new DeviceLayout { Machine = machine, Sections = sections, All = all, Macros = PickMacros(sections), GroupSource = source };
        }

        /// <summary>Where a manifest's mapping passes through zero, as a fraction of the raw range; null where it doesn't (or there is no manifest).</summary>
        static double? ZeroOf(ManifestParameter e, int min, int max)
        {
            if (e == null || max <= min) return null;
            var m = e.Mapping;
            switch (m.Kind)
            {
                case MappingKind.Bipolar: return 0.5;
                case MappingKind.Linear when m.A < 0 && m.B > 0: return -m.A / (m.B - m.A);
                case MappingKind.Int when -m.A > min && -m.A < max: return (-m.A - min) / (double)(max - min);
                default: return null;
            }
        }

        /// <summary>The length of the first word every name of a group starts with ("O1 shape", "O1 PW": "O1 "), which the header says; 0 when they share none.</summary>
        static int CommonWord(List<string> names)
        {
            if (names.Count < 3) return 0;
            int space = names[0].IndexOf(' ');
            if (space <= 0) return 0;
            string word = names[0].Substring(0, space);
            return names.All(n => n.Length > space + 1 && n.StartsWith(word, StringComparison.OrdinalIgnoreCase) && n[space] == ' ' && n.Substring(space).Trim().Length > 0) ? space + 1 : 0;
        }

        /// <summary>A state parameter a view shows (notes, triggers and internals are played, not set).</summary>
        static bool IsShown(IParameter p) => p.Flags.HasFlag(ParameterFlags.State) && p.Type != ParameterType.Note && p.Type != ParameterType.Internal;

        /// <summary>Words of a parameter's name that make it a macro without a manifest role, most wanted first.</summary>
        static readonly string[] MacroWords =
        {
            "cutoff", "reso", "threshold", "ratio", "drive", "volume", "level", "gain", "output", "makeup", "mix", "wet", "attack", "decay", "release", "size", "time", "feedback", "rate", "depth",
        };

        static IReadOnlyList<DeviceParam> PickMacros(List<DeviceSection> sections)
        {
            var globals = sections.Where(s => !s.IsTable && s.Name != "Inputs").SelectMany(s => s.Params).Where(p => p.Kind == DeviceKind.Slider && p.Max > p.Min).ToList();
            var picks = new List<DeviceParam>();
            void Add(DeviceParam p) { if (picks.Count < MacroCount && !picks.Contains(p)) picks.Add(p); }
            // The manifest's roles first; names only fill what they leave (and never an envelope stage, an operator or an LFO's numbered part).
            foreach (var role in MacroRoles)
                foreach (var p in globals.Where(p => p.Role == role)) Add(p);
            if (picks.Count < 4)
            {
                var plain = globals.Where(p => !p.FullName.Any(char.IsDigit) && !p.Group.Any(char.IsDigit)).ToList();
                foreach (var word in MacroWords)       // a name that is the word, then one that holds it
                    foreach (var p in plain.Where(p => p.Name.Equals(word, StringComparison.OrdinalIgnoreCase)))
                    {
                        Add(p);
                        break;
                    }
                foreach (var word in MacroWords)
                    foreach (var p in plain.Where(p => p.FullName.Contains(word, StringComparison.OrdinalIgnoreCase)))
                    {
                        Add(p);
                        break;                      // one of each kind: the first filter's cutoff, not every cutoff
                    }
                foreach (var p in plain) if (picks.Count < 4) Add(p);
            }
            return picks;
        }

        // ------------------------------------------------------------------ the kind of control

        /// <summary>
        /// A switch is a toggle; two to four values that the machine names (not numbers) are a segmented control; the rest
        /// are sliders. A manifest enum with names says its choices even where the machine's texts are numbers.
        /// </summary>
        static (DeviceKind, IReadOnlyList<string>) Classify(IParameter p, ManifestParameter entry)
        {
            int n = p.MaxValue - p.MinValue + 1;
            if (n < 2 || n > 4) return (DeviceKind.Slider, Array.Empty<string>());
            var texts = new List<string>();
            for (int v = p.MinValue; v <= p.MaxValue; v++)
            {
                string t;
                try { t = p.DescribeValue(v); }
                catch (Exception) { t = null; }
                texts.Add(string.IsNullOrWhiteSpace(t) ? v.ToString(CultureInfo.InvariantCulture) : t.Trim());
            }
            bool named = texts.Distinct().Count() == texts.Count && texts.All(t => t.Any(char.IsLetter));
            if (entry?.Mapping.Names is { Count: > 0 } names && names.Count == n && names.All(x => !string.IsNullOrWhiteSpace(x)))
            {
                texts = names.Select(x => x.Trim()).ToList();
                named = texts.Distinct().Count() == texts.Count;
            }
            if (p.Type == ParameterType.Switch || entry?.Mapping.Kind == MappingKind.Switch) return (DeviceKind.Toggle, texts);
            if (n == 2 && !named) return (DeviceKind.Toggle, texts);
            if (named) return (DeviceKind.Segmented, texts);
            return (DeviceKind.Slider, Array.Empty<string>());
        }

        // ------------------------------------------------------------------ grouping

        /// <summary>The groups the names alone make (for a machine with no manifest groups): the "- " indents, else the prefixes, else none.</summary>
        internal static (string Source, List<(string Title, List<string> Names)> Groups) Plan(IReadOnlyList<string> names)
        {
            var list = names.ToList();
            var plan = GroupFromIndents(list) ?? GroupFromPrefixes(list);
            return (plan.Source, plan.Groups.Select(g => (g.Title, g.Members.Select(m => m.Shown).ToList())).ToList());
        }

        sealed class GroupPlan
        {
            public GroupPlan(string source, IEnumerable<(string Title, List<(int Index, string Shown)> Members)> groups)
            {
                Source = source;
                Groups = groups.ToList();
            }
            public string Source { get; }
            public List<(string Title, List<(int Index, string Shown)> Members)> Groups { get; }
        }

        static GroupPlan Plain => new GroupPlan("section", Array.Empty<(string, List<(int, string)>)>());

        /// <summary>The manifest's groups, in the order they first appear; null when it names fewer than two.</summary>
        static GroupPlan GroupFromManifest(List<IParameter> ps, List<ManifestParameter> entries)
        {
            var order = new List<string>();
            var map = new Dictionary<string, List<(int, string)>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ps.Count; i++)
            {
                string g = entries[i]?.Group?.Trim() ?? "";
                if (g.Length == 0) g = "General";
                if (!map.TryGetValue(g, out var list))
                {
                    map[g] = list = new List<(int, string)>();
                    order.Add(g);
                }
                list.Add((i, ps[i].Name ?? ""));
            }
            if (order.Count < 2 || order.All(g => g == "General")) return null;
            return new GroupPlan("manifest", order.Select(g => (g, map[g])));
        }

        /// <summary>
        /// Buzz's indents: a name that starts with "- " belongs to the parameter above it that isn't indented, which names
        /// the group (FSM's "Osc 1 Wave", "- Decay"); null when fewer than two names are indented.
        /// </summary>
        static GroupPlan GroupFromIndents(List<string> names)
        {
            if (names.Count(IsIndented) < 2) return null;
            var groups = new List<(string, List<(int, string)>)>();
            for (int i = 0; i < names.Count; i++)
            {
                if (IsIndented(names[i]))
                {
                    if (groups.Count == 0) groups.Add(("General", new List<(int, string)>()));
                    groups[^1].Item2.Add((i, Unindent(names[i])));
                }
                else groups.Add((names[i].Trim(), new List<(int, string)> { (i, names[i].Trim()) }));
            }
            // A parent with no children is no group: those run together under "General".
            var result = new List<(string, List<(int, string)>)>();
            List<(int, string)> loose = null;
            foreach (var g in groups)
            {
                if (g.Item2.Count > 1 || g.Item1 == "General") result.Add(g);
                else
                {
                    if (loose == null) { loose = new List<(int, string)>(); result.Add(("General", loose)); }
                    loose.AddRange(g.Item2);
                }
            }
            // Same-named "General" groups merge.
            var merged = result.GroupBy(g => g.Item1, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, g.SelectMany(x => x.Item2).ToList())).ToList();
            return new GroupPlan("indent", merged);
        }

        static bool IsIndented(string name)
        {
            string t = name.TrimStart();
            return t.StartsWith("- ") || t.StartsWith("-") && t.Length > 1 && char.IsLetter(t[1]) && name.StartsWith(" ");
        }

        static string Unindent(string name)
        {
            string t = name.TrimStart();
            return t.TrimStart('-', ' ');
        }

        /// <summary>The first word of the names ("F1 cutoff", "F1 res"): a group for each word that two or more share, and at least two such.</summary>
        static GroupPlan GroupFromPrefixes(List<string> names)
        {
            string Prefix(string name)
            {
                string n = name.Trim();
                int cut = n.IndexOfAny(new[] { ' ', '_', ':', '.' });
                return cut > 0 && cut < n.Length - 1 ? n.Substring(0, cut) : "";
            }
            var prefixes = names.Select(Prefix).ToList();
            var counts = prefixes.Where(p => p.Length > 0).GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 2).Select(g => g.Key).ToList();
            if (counts.Count < 2) return Plain;
            var keep = new HashSet<string>(counts, StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            var map = new Dictionary<string, List<(int, string)>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++)
            {
                bool grouped = prefixes[i].Length > 0 && keep.Contains(prefixes[i]);
                string g = grouped ? prefixes[i] : "General";
                string shown = grouped ? names[i].Trim().Substring(prefixes[i].Length).TrimStart(' ', '_', ':', '.', '-') : names[i].Trim();
                if (shown.Length == 0) shown = names[i].Trim();
                if (!map.TryGetValue(g, out var list))
                {
                    map[g] = list = new List<(int, string)>();
                    order.Add(g);
                }
                list.Add((i, shown));
            }
            // "General" first, as the loose parameters are usually the master ones.
            order.Sort((a, b) => (a == "General" ? 0 : 1) - (b == "General" ? 0 : 1));
            return new GroupPlan("prefix", order.Select(g => (g, map[g])));
        }

        // ------------------------------------------------------------------ the list

        /// <summary>The items a search leaves: the headers of the groups with a match, the matching rows, the table with the matching columns.</summary>
        public List<DeviceItem> Items(string query, ISet<string> collapsed = null)
        {
            var words = (query ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var items = new List<DeviceItem>();
            foreach (var s in Sections)
            {
                bool closed = words.Length == 0 && collapsed != null && collapsed.Contains(s.Name);
                if (s.IsTable)
                {
                    var columns = Enumerable.Range(0, s.Params.Count).Where(c => words.Length == 0 || s.Params[c].Matches(words) || words.All(w => "tracks".Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (columns.Count == 0) continue;
                    items.Add(new HeaderItem(s, columns.Count, closed));
                    if (!closed) items.Add(new TableItem(s, columns));
                    continue;
                }
                var shown = s.Params.Where(p => words.Length == 0 || p.Matches(words) || words.All(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
                if (shown.Count == 0) continue;
                items.Add(new HeaderItem(s, shown.Count, closed));
                if (!closed) items.AddRange(shown.Select(p => new ParamItem(p)));
            }
            return items;
        }
    }
}
