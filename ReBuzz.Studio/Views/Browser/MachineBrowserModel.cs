// Fork (docs/specs/ux-overhaul.md P3d, decisions 56-58): the one model of the machine browser. The left panel (BrowserPanel) and the
// popup on the Machine View's canvas (MachineBrowserPopup) both read it, so there is one list, one search and one set of recents
// and favourites. A machine's category, tags and use_for are the manifest catalogue's (agent-knowledge.md K0,
// ReBuzz.Agent.Guides.MachineCatalog), the same vocabulary the agent's machine_find uses; the brand is one more tag.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Guides;
using ReBuzz.Core;
using ReBuzz.Studio.Views.Machines;

namespace ReBuzz.Studio.Views.Browser
{
    /// <summary>One installed machine as the browser shows it.</summary>
    internal sealed class MachineEntry
    {
        public MachineEntry(GearItem gear, string category, IReadOnlyList<string> tags, string useFor, string brand)
        {
            Gear = gear;
            Category = category ?? "";
            Brand = brand ?? "";
            UseFor = useFor ?? "";
            Tags = brand is { Length: > 0 } ? new[] { brand }.Concat(tags).ToList() : tags;
        }

        public GearItem Gear { get; }
        public string Name => Gear.DisplayName;
        public string Category { get; }
        public string Brand { get; }
        public string UseFor { get; }

        /// <summary>The brand first (it is shown as a tag), then the catalogue's tags.</summary>
        public IReadOnlyList<string> Tags { get; }

        public string TagLine => string.Join("  ·  ", Tags.Take(5));
    }

    internal sealed class MachineBrowserModel
    {
        internal const string RegistrySection = "Studio";
        internal const string RecentsKey = "MachineRecents";
        internal const string FavouritesKey = "MachineFavourites";
        internal const int MaxRecents = 8;

        /// <summary>The starters of an empty song (one click each).</summary>
        public static readonly string[] StarterNames = { "Neo Poly", "Neo Drums", "Neo Wave" };

        static readonly ConditionalWeakTable<ReBuzzCore, MachineBrowserModel> models = new();

        readonly ReBuzzCore core;
        readonly IRegistryEx registry;
        readonly Func<string, (string Category, IReadOnlyList<string> Tags, string UseFor)?> catalogue;
        List<MachineEntry> entries;
        int instrumentCount = -1;
        List<string> recents, favourites;

        internal MachineBrowserModel(ReBuzzCore core, IRegistryEx registry, Func<string, (string, IReadOnlyList<string>, string)?> catalogue = null)
        {
            this.core = core;
            this.registry = registry;
            this.catalogue = catalogue ?? MachineCatalog.Entry;
        }

        /// <summary>The window's, the panel's and the canvas popup's one model for a song engine.</summary>
        public static MachineBrowserModel For(ReBuzzCore core, IRegistryEx registry) =>
            models.GetValue(core, c => new MachineBrowserModel(c, registry));

        /// <summary>Raised when the recents or the favourites change.</summary>
        public event Action Changed;

        public static IReadOnlyList<string> Categories => MachineCatalog.Categories;

        /// <summary>The category filter's "all" and the section of a machine the catalogue doesn't know.</summary>
        public const string Other = "Other";

        // ------------------------------------------------------------------ entries

        public IReadOnlyList<MachineEntry> Entries
        {
            get
            {
                int count = core.Instruments.Count;
                if (entries == null || count != instrumentCount)
                {
                    instrumentCount = count;
                    entries = Build();
                }
                return entries;
            }
        }

        List<MachineEntry> Build()
        {
            var list = new List<MachineEntry>();
            foreach (var (_, items) in MachineEdits.GearByKind(core))
                foreach (var g in items)
                {
                    string dll = g.Instrument.MachineDLL?.Name ?? g.DisplayName;
                    var e = catalogue(dll) ?? catalogue(g.DisplayName);
                    list.Add(new MachineEntry(g, e?.Category ?? "", e?.Tags ?? Array.Empty<string>(), e?.UseFor ?? "", BrandOf(g, dll)));
                }
            return list.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static readonly string[] Brands = { "Neo", "Jeskola", "FSM", "Oomek", "Pedal", "Rout", "Polac", "Elak", "Geonik" };

        /// <summary>The brand tag: the name's own first word where it is a known maker's, else the machine's author.</summary>
        static string BrandOf(GearItem g, string dll)
        {
            string first = (dll ?? "").Split(' ', 2)[0];
            var known = Brands.FirstOrDefault(b => b.Equals(first, StringComparison.OrdinalIgnoreCase));
            if (known != null) return known;
            string author = g.Instrument.MachineDLL?.Info?.Author?.Trim() ?? "";
            if (author.Length == 0 || author == "-" || author.Length > 24 || author.Contains('@') || author.Contains("http")) return "";
            return author;
        }

        public MachineEntry Find(string name) => Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Neo Poly, Neo Drums and Neo Wave, when installed.</summary>
        public IReadOnlyList<MachineEntry> Starters => StarterNames.Select(Find).Where(e => e != null).ToList();

        // ------------------------------------------------------------------ search

        static readonly char[] Separators = { ' ', ',', ';', '-', '/', '\t' };

        static string[] Words(string text) =>
            (text ?? "").ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>The same word, or its plural (drum, drums).</summary>
        static bool Same(string word, string term) => term == word || term == word + "s" || word == term + "s" || term == word + "es";

        /// <summary>Whether a word asks for a term: the same word, or a prefix of it (drum: drums) or it of the word (reverbs: reverb).</summary>
        static bool Means(string word, string term) =>
            term == word || (word.Length >= 3 && term.StartsWith(word, StringComparison.Ordinal)) || (term.Length >= 4 && word.StartsWith(term, StringComparison.Ordinal));

        /// <summary>
        /// The machines for a search, best first. Every word must hit something. A word counts 5 for a word of the name, 4 for a tag,
        /// 3 for the category, 1 for a word of use_for (a prefix of any, a little less); a favourite is +1.5, a recent +0.5. With no words,
        /// the category's (or all) machines by name. The same vocabulary as machine_find (agent-knowledge.md K0), on the same fields.
        /// </summary>
        public List<MachineEntry> Search(string query, string category = null, bool favouritesOnly = false)
        {
            var words = Words(query);
            var favs = Favourites;
            var recent = Recents;
            var scored = new List<(MachineEntry E, double Score)>();
            foreach (var e in Entries)
            {
                if (category != null && !SameCategory(e, category)) continue;
                if (favouritesOnly && !favs.Contains(e.Name)) continue;
                double total = 0;
                bool all = true;
                var name = Words(e.Name);
                var tags = e.Tags.SelectMany(Words).Concat(e.Tags.Select(t => t.ToLowerInvariant())).Distinct().ToArray();
                var cat = Words(e.Category);
                var use = Words(e.UseFor);
                foreach (var w in words)
                {
                    double best = 0;
                    int fields = 0;
                    if (name.Any(n => Same(w, n))) { best = 5; fields++; }
                    else if (name.Any(n => Means(w, n))) { best = 4.5; fields++; }
                    else if (e.Name.Contains(w, StringComparison.OrdinalIgnoreCase)) { best = 3.5; fields++; }
                    if (tags.Any(t => Same(w, t))) { best = Math.Max(best, 4); fields++; }
                    else if (tags.Any(t => Means(w, t))) { best = Math.Max(best, 3.5); fields++; }
                    if (cat.Any(c => Means(w, c))) { best = Math.Max(best, 3); fields++; }
                    if (use.Any(u => Same(w, u))) { best = Math.Max(best, 1); fields++; }
                    else if (use.Any(u => Means(w, u))) { best = Math.Max(best, 0.8); fields++; }
                    if (best == 0) { all = false; break; }
                    total += best + 0.3 * (fields - 1);       // a word the name, a tag and the category all say counts a little more
                }
                if (!all) continue;
                if (favs.Contains(e.Name)) total += 1.5;
                int r = recent.ToList().IndexOf(e.Name);
                if (r >= 0) total += 0.5;
                scored.Add((e, total));
            }
            return scored.OrderByDescending(s => s.Score).ThenBy(s => s.E.Name, StringComparer.OrdinalIgnoreCase).Select(s => s.E).ToList();
        }

        static bool SameCategory(MachineEntry e, string category) =>
            category == Other ? e.Category.Length == 0 : e.Category == category;

        // ------------------------------------------------------------------ recents and favourites (kept in the settings)

        public IReadOnlyList<string> Recents => recents ??= Load(RecentsKey);

        public IReadOnlyList<string> Favourites => favourites ??= Load(FavouritesKey);

        public bool IsFavourite(MachineEntry e) => Favourites.Contains(e.Name);

        List<string> Load(string key)
        {
            try { return (registry?.Read(key, "", RegistrySection) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList(); }
            catch { return new List<string>(); }
        }

        void Save(string key, List<string> list)
        {
            try { registry?.Write(key, string.Join("|", list), RegistrySection); }
            catch { /* a setting that can't be saved is not worth a failed click */ }
        }

        /// <summary>Puts a machine first in the recents (kept to <see cref="MaxRecents"/>).</summary>
        public void MarkUsed(MachineEntry e)
        {
            var list = Recents.Where(n => n != e.Name).Prepend(e.Name).Take(MaxRecents).ToList();
            recents = list;
            Save(RecentsKey, list);
            Changed?.Invoke();
        }

        public void ToggleFavourite(MachineEntry e)
        {
            var list = Favourites.ToList();
            if (!list.Remove(e.Name)) list.Add(e.Name);
            favourites = list;
            Save(FavouritesKey, list);
            Changed?.Invoke();
        }

        /// <summary>Forgets what was read, so the next read comes from the settings (tests; another window's change).</summary>
        public void Reload()
        {
            recents = favourites = null;
            Changed?.Invoke();
        }

        public IReadOnlyList<MachineEntry> RecentEntries => Recents.Select(Find).Where(e => e != null).ToList();

        public IReadOnlyList<MachineEntry> FavouriteEntries => Favourites.Select(Find).Where(e => e != null).ToList();

        // ------------------------------------------------------------------ adding

        /// <summary>
        /// Adds the machine where the song says (one undo step, as the Machine View's New does), and remembers it as recent.
        /// </summary>
        public IMachine Add(MachineEntry e, float x, float y)
        {
            var made = MachineEdits.Create(core, e.Gear.Instrument, x, y);
            if (made != null) MarkUsed(e);
            return made;
        }

        /// <summary>The song has only its Master: the empty state's starters show.</summary>
        public bool SongIsEmpty => core.SongCore.Machines.Count <= 1;

        // ------------------------------------------------------------------ the rows both surfaces list

        public sealed record Row(string Tag, string Text, MachineEntry Entry, bool Header);

        /// <summary>
        /// What the list shows. A search or a category filter: the matches, best first, under one header. Otherwise Starters (an empty
        /// song), Recent, Favourites, then each category in the catalogue's order, then Other.
        /// </summary>
        public List<Row> Rows(string query, string category = null, bool favouritesOnly = false)
        {
            var rows = new List<Row>();
            query = (query ?? "").Trim();
            void Section(string title, IEnumerable<MachineEntry> items)
            {
                var l = items.ToList();
                if (l.Count == 0) return;
                rows.Add(new Row("#" + title, title, null, true));
                foreach (var e in l) rows.Add(new Row("gear:" + e.Name, e.Name, e, false));
            }
            if (query.Length > 0)
            {
                Section(category == null ? "Results" : category, Search(query, category, favouritesOnly));
                return rows;
            }
            if (category == null && !favouritesOnly)
            {
                if (SongIsEmpty) Section("Start here", Starters);
                Section("Recent", RecentEntries);
                Section("Favourites", FavouriteEntries);
            }
            foreach (var c in Categories.Append(Other))
            {
                if (category != null && c != category) continue;
                Section(c, Search("", c, favouritesOnly));
            }
            return rows;
        }
    }
}
