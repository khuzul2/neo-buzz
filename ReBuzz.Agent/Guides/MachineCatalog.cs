// Agent server (docs/specs/agent-knowledge.md, K0): the machine catalogue. Every machine's manifest carries a category
// (the studio browser's ten), tags (a controlled vocabulary) and one line of what to use it for; this builds
//   - rebuzz://catalog, every machine by category with its use_for and how far its manifest is trusted, and
//   - machine_find, which takes an intent ("warm bass synth", "tame harsh vocals"), a category, a tag or a role
//     and returns the machines that fit, ranked, each with the reason.
// The vocabulary lives in Machines/Manifests/machine-catalog.json (embedded here), the one file tools/machine_catalog.py
// writes into every manifest.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent.Guides
{
    public static class MachineCatalog
    {
        public const string Uri = "rebuzz://catalog";

        static readonly Lazy<JsonObject> file = new(() =>
        {
            using var stream = typeof(MachineCatalog).Assembly.GetManifestResourceStream("ReBuzz.Agent.machine-catalog.json")
                               ?? throw new InvalidOperationException("machine-catalog.json is not embedded in ReBuzz.Agent.");
            using var reader = new StreamReader(stream);
            return (JsonObject)JsonNode.Parse(reader.ReadToEnd())!;
        });

        /// <summary>The ten categories, in the studio browser's order.</summary>
        public static IReadOnlyList<string> Categories { get; } = file.Value["categories"]!.AsArray().Select(c => (string)c!).ToList();

        /// <summary>The tag vocabulary: tag and what it means.</summary>
        public static IReadOnlyDictionary<string, string> Tags { get; } =
            file.Value["tags"]!.AsObject().ToDictionary(kv => kv.Key, kv => (string)kv.Value!);

        /// <summary>What the catalogue file says about a machine (category, tags, use_for), or null; the fallback for a manifest that lacks the fields.</summary>
        public static (string Category, IReadOnlyList<string> Tags, string UseFor)? Entry(string gear) =>
            file.Value["machines"]!.AsObject().TryGetPropertyValue(gear, out var n) && n is JsonObject o
                ? ((string)o["category"]!, o["tags"]!.AsArray().Select(t => (string)t!).ToList(), (string)o["use_for"]!)
                : null;

        public static IEnumerable<string> GearInCatalogFile => file.Value["machines"]!.AsObject().Select(kv => kv.Key);

        /// <summary>One machine as the catalogue and machine_find see it.</summary>
        public sealed record Item(string Gear, string Category, string Kind, string Status, IReadOnlyList<string> Tags, string UseFor, bool HasPage, MachineManifest Manifest)
        {
            public string Health => Manifest.Health?.Status ?? "ok";
        }

        /// <summary>Items for manifests: the manifest's own fields, the catalogue file's where it has none.</summary>
        public static List<Item> Items(IEnumerable<MachineManifest> manifests)
        {
            var curatedPages = new HashSet<string>(MachineManual.CuratedGear, StringComparer.OrdinalIgnoreCase);
            var items = new List<Item>();
            foreach (var m in manifests)
            {
                var fallback = Entry(m.Gear);
                string category = m.Category.Length > 0 ? m.Category : fallback?.Category ?? "";
                var tags = m.Tags.Count > 0 ? m.Tags : fallback?.Tags ?? System.Array.Empty<string>();
                string use = m.UseFor.Length > 0 ? m.UseFor : fallback?.UseFor ?? m.Summary;
                items.Add(new Item(m.Gear, category, m.Kind, m.Quality, tags, use, curatedPages.Contains(m.Gear), m));
            }
            return items.OrderBy(i => i.Gear, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ---- rebuzz://catalog -------------------------------------------------------------------------

        public static ResourceDef Resource(Func<IEnumerable<MachineManifest>> manifests) => new()
        {
            Uri = Uri,
            Name = "catalog",
            Title = "Machine catalog",
            Description = "Every installed machine by category, with what to use it for and how far its manifest is trusted (curated, generated, draft). Pick a machine here, or ask machine_find.",
            Read = () => Build(Items(manifests())),
        };

        public static string Build(IReadOnlyList<Item> items)
        {
            var sb = new StringBuilder();
            int curated = items.Count(i => i.Status == "curated"), generated = items.Count(i => i.Status == "generated"), draft = items.Count(i => i.Status == "draft");
            sb.Append("# Machine catalog\n\n");
            sb.Append($"{items.Count} machines in {items.Select(i => i.Category).Where(c => c.Length > 0).Distinct().Count()} categories; manifests: {curated} curated, {generated} generated, {draft} draft. ");
            sb.Append("Status says how far to trust units and roles (curated and generated are exact; a draft is inferred: check by ear). `page` means a hand-written page in the manual. ");
            sb.Append("machine_find(intent=\"…\") ranks machines for what you want; guide(machine=…) opens a manual.\n");
            foreach (var category in Categories.Concat(new[] { "" }))
            {
                var group = items.Where(i => i.Category == category).ToList();
                if (group.Count == 0) continue;
                sb.Append("\n## ").Append(category.Length > 0 ? category : "Uncategorised").Append(" (").Append(group.Count).Append(")\n");
                foreach (var i in group)
                {
                    sb.Append("- **").Append(i.Gear).Append("** (").Append(i.Status).Append(i.HasPage ? ", page" : "").Append(") ").Append(i.UseFor);
                    if (i.Health != "ok") sb.Append(" [").Append(i.Health.Replace('_', ' ')).Append(']');
                    sb.Append('\n');
                }
            }
            sb.Append("\n## Tags\n").Append(string.Join(", ", Tags.Keys)).Append(". machine_find takes any of them as `tag`.\n");
            return sb.ToString();
        }

        // ---- machine_find -----------------------------------------------------------------------------

        public static void Register(ToolRegistry tools, Func<IEnumerable<MachineManifest>> manifests)
        {
            tools.Add(new ToolDef
            {
                Name = "machine_find",
                Title = "Find machines",
                Description =
                    "Finds installed machines for what you want, ranked, each with the reason. Give intent (plain words: \"warm bass synth\", \"tame harsh vocals\", " +
                    "\"glue the mix\", \"tempo-synced delay\"), or filter by category, tag (the vocabulary is in rebuzz://catalog) or role (a parameter role such as " +
                    "\"filter.cutoff\" or a prefix \"reverb\"); filters combine with intent. Curated machines with a page rank first among equals. " +
                    "Read-only. Then machine_add(gear=…) and guide(machine=…). Example: machine_find(intent=\"tempo-synced delay\").",
                Input = Object(null,
                    Opt("intent", String("What you want, in plain words (\"warm bass synth\")")),
                    Opt("category", String("Only this category", Categories.ToArray())),
                    Opt("tag", String("Only machines with this tag", Tags.Keys.ToArray())),
                    Opt("role", String("Only machines with a parameter of this role (\"filter.cutoff\", or \"reverb\" for any reverb role)")),
                    Opt("kind", String("Only generators (they make sound from notes) or effects (they process audio)", "generator", "effect")),
                    Opt("limit", Integer("How many to return (default 8)", 1, 30))),
                Output = Object(null,
                    Req("matches", Array(Object(null,
                        Req("machine", String("Its gear name")),
                        Req("category", String("Its category")),
                        Req("kind", String("generator or effect")),
                        Req("status", String("Its manifest: curated, generated or draft")),
                        Req("tags", Array(String("A tag"), "Its tags")),
                        Req("use_for", String("What to use it for")),
                        Req("reason", String("Why it matches")),
                        Req("score", Number("Rank score, higher is better")),
                        Req("page", Boolean("Whether it has a hand-written page")),
                        Req("manual", String("Its manual's resource URI"))), "Best first")),
                    Req("considered", Integer("How many machines were searched"))),
                Annotations = ToolAnnotations.Read,
                Handler = c =>
                {
                    var items = Items(manifests());
                    string intent = c.String("intent"), category = c.String("category"), tag = c.String("tag"), role = c.String("role"), kind = c.String("kind");
                    if (string.IsNullOrWhiteSpace(intent) && category == null && tag == null && string.IsNullOrWhiteSpace(role))
                        throw new ToolError("Give intent (plain words), category, tag or role. rebuzz://catalog lists the categories and tags.");
                    int limit = c.Int("limit") ?? 8;
                    var found = Find(items, intent, category, tag, role, kind, limit);
                    var matches = new JsonArray(found.Select(f => (JsonNode)new JsonObject
                    {
                        ["machine"] = f.Item.Gear,
                        ["category"] = f.Item.Category,
                        ["kind"] = f.Item.Kind,
                        ["status"] = f.Item.Status,
                        ["tags"] = new JsonArray(f.Item.Tags.Select(t => (JsonNode)t).ToArray()),
                        ["use_for"] = f.Item.UseFor,
                        ["reason"] = f.Reason,
                        ["score"] = Math.Round(f.Score, 1),
                        ["page"] = f.Item.HasPage,
                        ["manual"] = MachineManual.UriFor(f.Item.Gear),
                    }).ToArray());
                    string text = found.Count == 0
                        ? "No machine matches. Try other words, or browse rebuzz://catalog (categories and tags)."
                        : string.Join("\n", found.Select((f, i) => $"{i + 1}. {f.Item.Gear} ({f.Item.Category}, {f.Item.Status}): {f.Item.UseFor} Why: {f.Reason}."));
                    return Task.FromResult(ToolResult.Ok(text, new JsonObject { ["matches"] = matches, ["considered"] = items.Count }));
                },
            });
        }

        public sealed record Found(Item Item, double Score, string Reason);

        static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
        {
            "a", "an", "the", "for", "to", "my", "of", "on", "in", "with", "and", "or", "some", "make", "add", "get", "want", "need", "that", "it", "this",
            "sound", "sounds", "good", "better", "nice", "use", "using", "i", "me", "can", "how", "what", "do", "is", "are", "be", "but", "not", "too", "very", "more", "less", "from", "into", "up", "down",
        };

        /// <summary>Plain words that mean a tag or a category the vocabulary doesn't spell the same way (stemmed lower case on the left).</summary>
        static readonly Dictionary<string, string[]> Lexicon = new(StringComparer.Ordinal)
        {
            ["synth"] = new[] { "@Synths" }, ["synthesizer"] = new[] { "@Synths" }, ["instrument"] = new[] { "@Synths" },
            ["drum"] = new[] { "drums", "@Drums" }, ["kit"] = new[] { "drums" }, ["beat"] = new[] { "drums" }, ["snare"] = new[] { "drums", "percussion" },
            ["hat"] = new[] { "drums", "percussion" }, ["clap"] = new[] { "drums", "percussion" }, ["tom"] = new[] { "drums", "percussion" }, ["perc"] = new[] { "percussion" },
            ["sample"] = new[] { "sampler", "@Samplers" }, ["loop"] = new[] { "looper", "sampler" }, ["wave"] = new[] { "sampler", "wavetable" },
            ["compress"] = new[] { "compressor", "@Dynamics" }, ["compressor"] = new[] { "compressor" }, ["squash"] = new[] { "compressor" }, ["dynamic"] = new[] { "compressor", "@Dynamics" },
            ["level"] = new[] { "compressor", "gain" }, ["even"] = new[] { "compressor" },
            ["bus"] = new[] { "mix-bus" }, ["mix"] = new[] { "mix-bus" }, ["mixdown"] = new[] { "mix-bus" }, ["cohesive"] = new[] { "glue" }, ["cohesion"] = new[] { "glue" }, ["together"] = new[] { "glue" },
            ["master"] = new[] { "master" }, ["mastering"] = new[] { "master" }, ["loud"] = new[] { "limiter", "master" }, ["loudness"] = new[] { "limiter", "master" },
            ["harsh"] = new[] { "de-esser", "eq" }, ["sibilance"] = new[] { "de-esser" }, ["sibilant"] = new[] { "de-esser" }, ["ess"] = new[] { "de-esser" }, ["essy"] = new[] { "de-esser" },
            ["sizzle"] = new[] { "de-esser" }, ["spit"] = new[] { "de-esser" }, ["deess"] = new[] { "de-esser" }, ["tame"] = new[] { "de-esser", "compressor" },
            ["muddy"] = new[] { "eq" }, ["mud"] = new[] { "eq" }, ["boomy"] = new[] { "eq" }, ["tonal"] = new[] { "eq" }, ["tone"] = new[] { "eq" }, ["brighten"] = new[] { "eq", "exciter" },
            ["balance"] = new[] { "eq" }, ["equalizer"] = new[] { "eq" }, ["equaliser"] = new[] { "eq" },
            ["sweep"] = new[] { "filter", "lfo" }, ["cutoff"] = new[] { "filter" }, ["lowpass"] = new[] { "filter" }, ["highpass"] = new[] { "filter" },
            ["wobble"] = new[] { "lfo", "bass" }, ["repeat"] = new[] { "delay", "echo" }, ["slapback"] = new[] { "delay", "echo" }, ["dub"] = new[] { "delay", "echo" }, ["pingpong"] = new[] { "delay", "echo" },
            ["space"] = new[] { "reverb" }, ["room"] = new[] { "reverb" }, ["hall"] = new[] { "reverb" }, ["plate"] = new[] { "reverb" }, ["ambience"] = new[] { "reverb" }, ["tail"] = new[] { "reverb" },
            ["wide"] = new[] { "stereo-width" }, ["widen"] = new[] { "stereo-width" }, ["width"] = new[] { "stereo-width" }, ["stereo"] = new[] { "stereo-width" }, ["narrow"] = new[] { "stereo-width" },
            ["thick"] = new[] { "chorus", "lush" }, ["fatten"] = new[] { "chorus", "lush" }, ["ensemble"] = new[] { "chorus" }, ["jet"] = new[] { "flanger" }, ["swirl"] = new[] { "phaser", "flanger" },
            ["dirty"] = new[] { "gritty", "distortion" }, ["grit"] = new[] { "gritty" }, ["crunch"] = new[] { "gritty", "distortion" }, ["overdrive"] = new[] { "saturation", "distortion" },
            ["fuzz"] = new[] { "distortion" }, ["grunge"] = new[] { "gritty", "distortion" }, ["distort"] = new[] { "distortion" }, ["drive"] = new[] { "saturation" },
            ["saturate"] = new[] { "saturation" }, ["warmth"] = new[] { "warm", "saturation" }, ["analog"] = new[] { "warm", "vintage" }, ["classic"] = new[] { "vintage" }, ["retro"] = new[] { "vintage" }, ["old"] = new[] { "vintage" },
            ["lofi"] = new[] { "bitcrush", "gritty" }, ["crush"] = new[] { "bitcrush" }, ["crusher"] = new[] { "bitcrush" }, ["bit"] = new[] { "bitcrush" },
            ["punch"] = new[] { "punchy", "transient" }, ["snap"] = new[] { "transient", "punchy" }, ["attack"] = new[] { "transient" },
            ["bassdrum"] = new[] { "kick" }, ["duck"] = new[] { "sidechain" }, ["pump"] = new[] { "sidechain", "compressor" }, ["pumping"] = new[] { "sidechain" },
            ["limit"] = new[] { "limiter" }, ["clip"] = new[] { "distortion", "limiter" }, ["ceiling"] = new[] { "limiter" }, ["peak"] = new[] { "limiter" },
            ["bleed"] = new[] { "gate" }, ["cleanup"] = new[] { "gate" }, ["silence"] = new[] { "gate" }, ["noisy"] = new[] { "gate" },
            ["ambient"] = new[] { "pad" }, ["evolve"] = new[] { "pad" }, ["drone"] = new[] { "pad", "granular" }, ["solo"] = new[] { "lead" }, ["melody"] = new[] { "lead" }, ["stab"] = new[] { "pluck", "pad" },
            ["piano"] = new[] { "keys" }, ["epiano"] = new[] { "keys", "fm" }, ["bell"] = new[] { "fm", "bright" }, ["glassy"] = new[] { "bright", "fm" }, ["chip"] = new[] { "chiptune" }, ["8bit"] = new[] { "chiptune" },
            ["voice"] = new[] { "vocal" }, ["singer"] = new[] { "vocal" }, ["singing"] = new[] { "vocal" }, ["vox"] = new[] { "vocal" }, ["speech"] = new[] { "vocal" }, ["podcast"] = new[] { "vocal" },
            ["grain"] = new[] { "granular" }, ["texture"] = new[] { "granular", "pad" },
            ["tempo"] = new[] { "tempo-sync" }, ["bpm"] = new[] { "tempo-sync" }, ["sync"] = new[] { "tempo-sync" }, ["lock"] = new[] { "tempo-sync" },
            ["spectrum"] = new[] { "metering" }, ["analyze"] = new[] { "metering" }, ["analyse"] = new[] { "metering" }, ["meter"] = new[] { "metering" }, ["scope"] = new[] { "metering" }, ["visual"] = new[] { "metering" },
            ["arp"] = new[] { "arpeggio" }, ["arpeggiate"] = new[] { "arpeggio" }, ["chord"] = new[] { "chords" }, ["swing"] = new[] { "groove" }, ["shuffle"] = new[] { "groove" },
            ["record"] = new[] { "recording" }, ["capture"] = new[] { "recording" }, ["resample"] = new[] { "recording" }, ["plugin"] = new[] { "plugin-host" }, ["vst"] = new[] { "plugin-host" },
            ["pitch"] = new[] { "pitch-shift" }, ["transpose"] = new[] { "pitch-shift" }, ["automate"] = new[] { "automation" }, ["modulate"] = new[] { "automation", "lfo" },
            ["route"] = new[] { "routing" }, ["input"] = new[] { "audio-io" }, ["output"] = new[] { "audio-io" }, ["big"] = new[] { "lush" }, ["huge"] = new[] { "lush" }, ["smooth"] = new[] { "warm", "clean" },
            ["transparent"] = new[] { "clean" }, ["acid"] = new[] { "acid" }, ["subtractive"] = new[] { "subtractive" },
        };

        /// <summary>Lower case, no plural or tense ending: "vocals" → "vocal", "synced" → "sync".</summary>
        internal static string Stem(string w)
        {
            if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal)) return w.Substring(0, w.Length - 3) + "y";
            if (w.Length > 5 && w.EndsWith("ing", StringComparison.Ordinal)) return w.Substring(0, w.Length - 3);
            if (w.Length > 4 && w.EndsWith("ed", StringComparison.Ordinal)) return w.Substring(0, w.Length - 2);
            if (w.Length > 4 && w.EndsWith("es", StringComparison.Ordinal) && !w.EndsWith("ses", StringComparison.Ordinal)) return w.Substring(0, w.Length - 2);
            if (w.Length > 3 && w.EndsWith("s", StringComparison.Ordinal) && !w.EndsWith("ss", StringComparison.Ordinal)) return w.Substring(0, w.Length - 1);
            return w;
        }

        static IEnumerable<string> Words(string text) =>
            (text ?? "").ToLowerInvariant().Split(new[] { ' ', '\t', '\n', ',', '.', ';', ':', '(', ')', '"', '\'', '/', '!', '?', '-', '_', '+', '&' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 0);

        /// <summary>
        /// Ranks <paramref name="items"/> for an intent and filters. Each distinct thing the intent asks for counts once:
        /// a tag 3 (times how many of its words ask for it), a word of the machine's name 1.5, a word of its use_for 0.5; a role match 3. Curated
        /// manifests (+0.6), a hand-written page (+0.4) and generated manifests (+0.3) break ties; a machine that diverges
        /// or needs a window loses 2. Filters (category, tag, kind) cut the list; role also counts as a filter.
        /// </summary>
        public static List<Found> Find(IReadOnlyList<Item> items, string intent, string category, string tag, string role, string kind, int limit)
        {
            var wantTags = new Dictionary<string, double>(StringComparer.Ordinal);   // tag: how strongly the intent asks for it
            var wantCategories = new HashSet<string>(StringComparer.Ordinal);
            var plain = new List<string>();
            void Want(string tagName, double weight) => wantTags[tagName] = wantTags.GetValueOrDefault(tagName) + weight;
            var literal = Tags.Keys.Where(t => Words(t).Count() == 1).ToDictionary(Stem, t => t);
            foreach (var raw in Words(intent))
            {
                string w = Stem(raw);
                if (Stop.Contains(raw) || Stop.Contains(w)) continue;
                if (literal.TryGetValue(w, out var literalTag)) Want(literalTag, 1);
                if (Lexicon.TryGetValue(w, out var hit) || Lexicon.TryGetValue(raw, out hit))
                {
                    // The first thing a word names is what it chiefly means (1); the others are weaker guesses (0.5).
                    for (int k = 0; k < hit.Length; k++)
                        if (hit[k][0] == '@') wantCategories.Add(hit[k].Substring(1));
                        else Want(hit[k], k == 0 ? 1 : 0.5);
                }
                foreach (var c in Categories)
                    if (w != "filter" && w != "delay" && w != "reverb" && Words(c).Select(Stem).Contains(w))
                        wantCategories.Add(c);
                plain.Add(w);
            }
            string wantedCategory = category == null ? null : Categories.FirstOrDefault(c => c == category);
            var found = new List<Found>();
            foreach (var item in items)
            {
                if (wantedCategory != null && item.Category != wantedCategory) continue;
                if (tag != null && !item.Tags.Contains(tag)) continue;
                if (kind != null && !string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
                double score = 0;
                var why = new List<string>();
                var tagHits = wantTags.Where(t => item.Tags.Contains(t.Key)).Select(t => t.Key).ToList();
                if (tagHits.Count > 0) { score += 3 * tagHits.Sum(t => wantTags[t]); why.Add("tags " + string.Join(", ", tagHits)); }
                if (wantCategories.Contains(item.Category)) { score += 2; why.Add("category " + item.Category); }
                var nameWords = new HashSet<string>(Words(item.Gear).Select(Stem));
                var useWords = new HashSet<string>(Words(item.UseFor).Select(Stem));
                var nameHits = plain.Where(w => w.Length >= 3 && nameWords.Contains(w)).Distinct().ToList();
                if (nameHits.Count > 0) { score += 1.5 * nameHits.Count; why.Add("name " + string.Join(", ", nameHits)); }
                var useHits = plain.Where(w => w.Length >= 3 && useWords.Contains(w) && !nameHits.Contains(w) && !tagHits.Contains(w)).Distinct().ToList();
                if (useHits.Count > 0) { score += 0.5 * useHits.Count; why.Add("use_for mentions " + string.Join(", ", useHits)); }
                if (!string.IsNullOrWhiteSpace(role))
                {
                    var roles = RolesOf(item.Manifest).Where(r => r == role || r.StartsWith(role + ".", StringComparison.Ordinal)).ToList();
                    if (roles.Count == 0) continue;
                    score += 3;
                    why.Add("has " + string.Join(", ", roles.Take(3)));
                }
                bool asked = plain.Count > 0;
                if (asked && score <= 0) continue;
                if (asked && tagHits.Count == 0 && wantTags.Count > 0 && score < 1.5 && !wantCategories.Contains(item.Category)) continue;
                if (tag != null) why.Add("tag " + tag);
                if (wantedCategory != null) why.Add("category " + wantedCategory);
                score += item.Status == "curated" ? 0.6 : item.Status == "generated" ? 0.3 : 0;
                if (item.HasPage) score += 0.4;
                if (item.Manifest.Health is { Diverging: true } or { NeedsWindow: true }) { score -= 2; why.Add("health: " + item.Health.Replace('_', ' ')); }
                why.Add(item.Status + (item.HasPage ? " with a page" : " manifest"));
                found.Add(new Found(item, score, string.Join("; ", why)));
            }
            return found.OrderByDescending(f => f.Score).ThenBy(f => f.Item.Gear, StringComparer.OrdinalIgnoreCase).Take(Math.Max(1, limit)).ToList();
        }

        static IEnumerable<string> RolesOf(MachineManifest m) =>
            m.Parameters.Concat(m.TrackParameters).SelectMany(p => new[] { p.Role, p.SuggestedRole }).Where(r => !string.IsNullOrEmpty(r)).Distinct();
    }
}
